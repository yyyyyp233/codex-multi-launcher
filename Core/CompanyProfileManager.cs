using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexChannelLauncher.Core;

public sealed record WorkProfileMarker(
    int SchemaVersion,
    DateTime InitializedAtUtc,
    string Authority,
    string DisplayName,
    string Origin,
    bool ImportedSkills,
    bool ImportedMemories,
    ProfileAuthMode AuthMode = ProfileAuthMode.CustomResponses,
    string AccentColor = "#7C3AED",
    string? ProfileId = null);

public sealed partial class CompanyProfileManager(
    LauncherPaths paths,
    ProfileSnapshotService snapshots)
{
    private sealed record LegacyWorkProfileRegistration(
        int SchemaVersion,
        string ProfileDirectoryName,
        string DisplayName,
        DateTime RegisteredAtUtc);

    private sealed record LegacyProfileCandidate(
        string ProfileDirectoryName,
        string DisplayName,
        string CodexHome,
        ProfileAuthMode AuthMode,
        string AccentColor,
        string? ProfileId);

    private sealed record ExistingProfileBinding(
        string ProfileDirectoryName,
        string CodexHome,
        string? AccentColor,
        string? ProfileId);

    private const int LegacyRegistrationSchemaVersion = 1;
    private const int RegistrySchemaVersion = 1;
    private const int MarkerSchemaVersion = 5;
    private const string InitializationGateName = @"Local\CodexChannelLauncher.WorkProfileInitialization";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> ReasoningEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimal", "low", "medium", "high", "xhigh"
    };

    private static readonly string[] AccentPalette =
    [
        "#8B5CF6", "#06B6D4", "#F97316", "#22C55E",
        "#EC4899", "#EAB308", "#3B82F6", "#EF4444",
        "#14B8A6", "#A855F7", "#F59E0B", "#10B981"
    ];

    public bool IsInitialized => GetSetupStatus().State == WorkProfileSetupState.Configured;

    public IReadOnlyList<ManagedProfileRegistration> GetProfiles()
    {
        PreparePaths();
        return WithInitializationGate(() => ReadOrMigrateRegistry().Profiles);
    }

    public ProfileSetupStatus GetSetupStatus(string? profileId = null)
    {
        PreparePaths();
        return WithInitializationGate(() => ResolveSetupStatus(profileId));
    }

    public ManagedProfileRegistration SelectProfile(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("必须指定 Profile ID。", nameof(profileId));
        }

        var registration = GetProfiles().FirstOrDefault(profile =>
            profile.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("指定的隔离空间不存在或已被移除。");
        paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
        return registration;
    }

    public CompanyProfileMetadata EnsureInitialized(string? profileId = null)
    {
        var status = GetSetupStatus(profileId);
        return status.State switch
        {
            WorkProfileSetupState.Configured => ReadMetadataCore(
                paths.CompanyConfig,
                paths.CompanyAuth,
                status.Registration!),
            WorkProfileSetupState.Invalid => throw new InvalidDataException(
                status.Problem ?? "隔离空间配置已损坏，请重新配置。"),
            _ => throw new InvalidOperationException("隔离空间尚未配置，请先完成配置。")
        };
    }

    public CompanyProfileMetadata ReadMetadata(string? profileId = null) => EnsureInitialized(profileId);

    public CompanyProfileMetadata ReadMetadataForEditing(string profileId)
    {
        var registration = SelectProfile(profileId);
        return ReadMetadataCore(
            paths.CompanyConfig,
            paths.CompanyAuth,
            registration,
            requireUsableAuth: false);
    }

    public ManagedProfileRegistration Configure(ProfileSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreparePaths();
        return WithInitializationGate(() => ConfigureCore(request));
    }

    public ProfileDeletionResult Delete(string profileId, bool deleteLocalContent)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("必须指定要删除的 Profile ID。", nameof(profileId));
        }

        PreparePaths();
        return WithInitializationGate(() => DeleteCore(profileId, deleteLocalContent));
    }

    public static string ValidateAndNormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Base URL 必须是完整的绝对 URL。", nameof(value));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Base URL 不能内嵌用户名、密码或其他凭据。", nameof(value));
        }

        var isHttps = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                             uri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            throw new ArgumentException("Base URL 必须使用 HTTPS；仅 localhost 或回环地址允许 HTTP。", nameof(value));
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Base URL 不能包含查询参数或片段。", nameof(value));
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static bool IsAuthConfigured(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authPath));
            return document.RootElement.TryGetProperty("OPENAI_API_KEY", out var property) &&
                   IsUsableApiKey(property.GetString());
        }
        catch
        {
            return false;
        }
    }

    private void PreparePaths()
    {
        paths.EnsureRuntimeDirectories();
        paths.ValidateIsolationBoundaries();
    }

    private ManagedProfileRegistration ConfigureCore(ProfileSetupRequest request)
    {
        var displayName = ValidateDisplayName(request.DisplayName);
        var registry = ReadOrMigrateRegistry();
        return request.Mode switch
        {
            ProfileSetupMode.Update => UpdateExisting(request, displayName, registry),
            ProfileSetupMode.Create => CreateProfile(request, displayName, registry),
            ProfileSetupMode.Attach => AttachExistingProfile(request, displayName, registry),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "未知的隔离空间配置模式。")
        };
    }

    private ProfileDeletionResult DeleteCore(string profileId, bool deleteLocalContent)
    {
        var registry = ReadOrMigrateRegistry();
        var registration = registry.Profiles.FirstOrDefault(profile =>
            profile.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("指定的隔离空间不存在或已经被移除。");
        var updatedRegistry = registry with
        {
            Profiles = registry.Profiles
                .Where(profile => !profile.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
        var profileRoot = Path.GetFullPath(
            Path.Combine(paths.ProfilesRoot, registration.ProfileDirectoryName));

        if (!deleteLocalContent)
        {
            EnsureMarkerProfileId(
                Path.Combine(profileRoot, "codex-home", "launcher-profile-v2.json"),
                registration.ProfileId);
            WriteRegistry(updatedRegistry);
            return new ProfileDeletionResult(
                registration.ProfileId,
                registration.DisplayName,
                false,
                profileRoot);
        }

        var quarantineRoot = Path.Combine(
            paths.OperationStagingRoot,
            "profile-delete-" + Guid.NewGuid().ToString("N"));
        var movedDirectories = new List<(string Source, string Quarantined)>();
        LauncherPaths.EnsureNoReparsePoints(paths.OperationStagingRoot);
        Directory.CreateDirectory(quarantineRoot);
        LauncherPaths.EnsureNoReparsePoints(quarantineRoot);
        try
        {
            var ownedDirectories = EnumerateOwnedDirectories(registration).ToArray();
            for (var index = 0; index < ownedDirectories.Length; index++)
            {
                MoveToQuarantine(
                    ownedDirectories[index].Path,
                    ownedDirectories[index].OwnerRoot,
                    quarantineRoot,
                    index,
                    movedDirectories);
            }

            WriteRegistry(updatedRegistry);
        }
        catch (Exception operationException)
        {
            try
            {
                RestoreFromQuarantine(movedDirectories);
                ProfileSnapshotService.SafeDeleteDirectory(quarantineRoot);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    $"删除工作空间失败，且本地内容回滚不完整；隔离目录：{quarantineRoot}",
                    operationException,
                    rollbackException);
            }

            throw;
        }

        string? cleanupPendingPath = null;
        try
        {
            ProfileSnapshotService.SafeDeleteDirectory(quarantineRoot);
        }
        catch
        {
            cleanupPendingPath = quarantineRoot;
        }

        return new ProfileDeletionResult(
            registration.ProfileId,
            registration.DisplayName,
            true,
            null,
            cleanupPendingPath);
    }

    private IEnumerable<(string Path, string OwnerRoot)> EnumerateOwnedDirectories(
        ManagedProfileRegistration registration)
    {
        yield return (
            Path.Combine(paths.ProfilesRoot, registration.ProfileDirectoryName),
            paths.ProfilesRoot);

        var snapshotsRoot = Path.Combine(paths.RuntimeRoot, "snapshots");
        yield return (
            Path.Combine(snapshotsRoot, registration.ProfileDirectoryName),
            snapshotsRoot);

        var mergeBasesRoot = Path.Combine(paths.RuntimeRoot, "merge-bases");
        yield return (
            Path.Combine(mergeBasesRoot, registration.ProfileDirectoryName),
            mergeBasesRoot);

        var versionsRoot = Path.Combine(paths.RuntimeCacheRoot, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            yield break;
        }

        LauncherPaths.EnsureNoReparsePoints(versionsRoot);
        foreach (var candidate in Directory.EnumerateDirectories(versionsRoot))
        {
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"运行副本目录包含链接或重解析点，已拒绝删除：{candidate}");
            }

            var manifestPath = Path.Combine(candidate, "cache-manifest.json");
            LauncherPaths.EnsureNoReparsePoints(manifestPath);
            if (RuntimeCacheBelongsToProfile(manifestPath, registration.ProfileId))
            {
                yield return (candidate, versionsRoot);
            }
        }
    }

    private static bool RuntimeCacheBelongsToProfile(string manifestPath, string profileId)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("ProfileId", out var profileProperty) &&
                   profileProperty.ValueKind == JsonValueKind.String &&
                   string.Equals(
                       profileProperty.GetString(),
                       profileId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // An unrelated or incomplete runtime cache is not owned by this profile.
            return false;
        }
    }

    private static void MoveToQuarantine(
        string source,
        string ownerRoot,
        string quarantineRoot,
        int index,
        ICollection<(string Source, string Quarantined)> movedDirectories)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var fullSource = Path.GetFullPath(source);
        var fullOwnerRoot = Path.GetFullPath(ownerRoot);
        if (!LauncherPaths.IsUnder(fullSource, fullOwnerRoot))
        {
            throw new InvalidOperationException("待删除的工作空间目录越过了允许边界。");
        }

        LauncherPaths.EnsureNoReparsePoints(fullOwnerRoot);
        LauncherPaths.EnsureNoReparsePoints(fullSource);
        LauncherPaths.EnsureNoReparsePoints(quarantineRoot);

        var quarantined = Path.Combine(
            quarantineRoot,
            $"{index:D2}-{Path.GetFileName(fullSource)}");
        if (!LauncherPaths.IsUnder(quarantined, quarantineRoot))
        {
            throw new InvalidOperationException("删除隔离目录越过了允许边界。");
        }

        Directory.Move(fullSource, quarantined);
        movedDirectories.Add((fullSource, quarantined));
    }

    private static void RestoreFromQuarantine(
        IReadOnlyList<(string Source, string Quarantined)> movedDirectories)
    {
        for (var index = movedDirectories.Count - 1; index >= 0; index--)
        {
            var item = movedDirectories[index];
            if (!Directory.Exists(item.Quarantined))
            {
                continue;
            }

            if (Directory.Exists(item.Source))
            {
                throw new IOException($"无法回滚本地目录，目标已重新出现：{item.Source}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.Source)!);
            Directory.Move(item.Quarantined, item.Source);
        }
    }

    private ManagedProfileRegistration UpdateExisting(
        ProfileSetupRequest request,
        string displayName,
        ManagedProfileRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId))
        {
            throw new ArgumentException("编辑隔离空间时必须指定 Profile ID。", nameof(request));
        }

        var registration = registry.Profiles.FirstOrDefault(profile =>
            profile.ProfileId.Equals(request.ProfileId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("没有可更新的隔离空间注册信息。");
        paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
        paths.EnsureWorkProfileDirectories();

        var authModeChanged = registration.AuthMode != request.AuthMode;
        var settings = ValidateProviderSettings(request, requireApiKey: false);
        if (request.AuthMode != ProfileAuthMode.ChatGptAccount &&
            string.IsNullOrWhiteSpace(request.ApiKey) &&
            (authModeChanged || !IsAuthConfigured(paths.CompanyAuth)))
        {
            throw new ArgumentException("当前认证不可用，请输入 API Key。", nameof(request));
        }

        if (File.Exists(paths.CompanyConfig))
        {
            snapshots.CreateSnapshot("before-profile-settings");
        }

        var document = File.Exists(paths.CompanyConfig)
            ? ProfileConfigDocument.Load(paths.CompanyConfig)
            : ProfileConfigDocument.Create();
        ApplyProfileSettings(document, request.AuthMode, settings);

        var updated = registration with
        {
            DisplayName = displayName,
            AuthMode = request.AuthMode,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var updatedRegistry = registry with
        {
            Profiles = registry.Profiles
                .Select(profile => profile.ProfileId.Equals(updated.ProfileId, StringComparison.OrdinalIgnoreCase)
                    ? updated
                    : profile)
                .ToArray()
        };

        var stagingRoot = Path.Combine(
            paths.OperationStagingRoot,
            "profile-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var stagedConfig = Path.Combine(stagingRoot, "config.toml");
            var stagedAuth = Path.Combine(stagingRoot, "auth.json");
            var stagedMarker = Path.Combine(stagingRoot, "launcher-profile-v2.json");
            var stagedRegistry = Path.Combine(stagingRoot, "profiles.json");
            document.SaveAtomic(stagedConfig);
            WriteMarker(
                stagedMarker,
                displayName,
                "updated",
                false,
                false,
                updated.AuthMode,
                updated.AccentColor,
                updated.ProfileId);
            WriteAtomic(
                stagedRegistry,
                JsonSerializer.Serialize(ValidateRegistry(updatedRegistry), JsonOptions));

            var changes = new List<AtomicFileChange>
            {
                new(paths.CompanyConfig, stagedConfig)
            };
            if (request.AuthMode == ProfileAuthMode.ChatGptAccount)
            {
                if (authModeChanged && File.Exists(paths.CompanyAuth))
                {
                    changes.Add(new AtomicFileChange(paths.CompanyAuth, null));
                }
            }
            else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                WriteApiKeyAtomic(stagedAuth, ValidateApiKey(request.ApiKey));
                changes.Add(new AtomicFileChange(paths.CompanyAuth, stagedAuth));
            }

            changes.Add(new AtomicFileChange(paths.CompanyProfileMarker, stagedMarker));
            changes.Add(new AtomicFileChange(paths.ProfilesRegistryFile, stagedRegistry));
            AtomicFileTransaction.Commit(paths, changes);
        }
        finally
        {
            ProfileSnapshotService.SafeDeleteDirectory(stagingRoot);
        }

        return updated;
    }

    private ManagedProfileRegistration AttachExistingProfile(
        ProfileSetupRequest request,
        string displayName,
        ManagedProfileRegistry registry)
    {
        var binding = ValidateExistingProfile(request.ExistingCodexHome);
        if (registry.Profiles.Any(profile => profile.ProfileDirectoryName.Equals(
                binding.ProfileDirectoryName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("所选工作空间已经接入，无需重复添加。");
        }

        var authMode = InferAuthMode(
            Path.Combine(binding.CodexHome, "config.toml"),
            Path.Combine(binding.CodexHome, "auth.json"));
        var accentColor = IsValidAccentColor(binding.AccentColor) &&
                          registry.Profiles.All(profile => !profile.AccentColor.Equals(
                              binding.AccentColor,
                              StringComparison.OrdinalIgnoreCase))
            ? binding.AccentColor!
            : AllocateAccentColor(registry.Profiles);
        var registration = NewRegistration(
            binding.ProfileDirectoryName,
            displayName,
            authMode,
            accentColor,
            binding.ProfileId);
        if (registry.Profiles.Any(profile =>
                profile.ProfileId.Equals(registration.ProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("所选工作空间的 Profile ID 已被其他注册项占用。");
        }

        _ = ReadMetadataCore(
            Path.Combine(binding.CodexHome, "config.toml"),
            Path.Combine(binding.CodexHome, "auth.json"),
            registration);

        EnsureMarkerProfileId(
            Path.Combine(binding.CodexHome, "launcher-profile-v2.json"),
            registration.ProfileId);
        WriteRegistry(registry with { Profiles = [.. registry.Profiles, registration] });
        paths.SelectWorkProfileDirectory(binding.ProfileDirectoryName);
        return registration;
    }

    private ManagedProfileRegistration CreateProfile(
        ProfileSetupRequest request,
        string displayName,
        ManagedProfileRegistry registry)
    {
        var authMode = request.AuthMode;
        var settings = ValidateProviderSettings(
            request,
            requireApiKey: authMode != ProfileAuthMode.ChatGptAccount);

        var directoryName = AllocateProfileDirectoryName(registry.Profiles);
        var accentColor = AllocateAccentColor(registry.Profiles);
        var targetRoot = Path.Combine(paths.ProfilesRoot, directoryName);
        var stagingRoot = Path.Combine(
            paths.OperationStagingRoot,
            "profile-setup-" + Guid.NewGuid().ToString("N"));
        var stagedProfileRoot = Path.Combine(stagingRoot, directoryName);
        var stagedHome = Path.Combine(stagedProfileRoot, "codex-home");
        Directory.CreateDirectory(stagedHome);

        try
        {
            var document = ProfileConfigDocument.Create();
            ApplyProfileSettings(document, authMode, settings);
            document.SaveAtomic(Path.Combine(stagedHome, "config.toml"));
            if (authMode != ProfileAuthMode.ChatGptAccount)
            {
                WriteApiKeyAtomic(
                    Path.Combine(stagedHome, "auth.json"),
                    ValidateApiKey(request.ApiKey));
            }

            var registration = NewRegistration(directoryName, displayName, authMode, accentColor);
            _ = ReadMetadataCore(
                Path.Combine(stagedHome, "config.toml"),
                Path.Combine(stagedHome, "auth.json"),
                registration);
            WriteMarker(
                Path.Combine(stagedHome, "launcher-profile-v2.json"),
                displayName,
                "created",
                false,
                false,
                authMode,
                accentColor,
                registration.ProfileId);

            if (Directory.Exists(targetRoot))
            {
                throw new IOException("隔离空间目标目录已存在，请重试。");
            }

            Directory.Move(stagedProfileRoot, targetRoot);
            var registryCommitted = false;
            try
            {
                paths.SelectWorkProfileDirectory(directoryName);
                paths.EnsureWorkProfileDirectories();
                WriteRegistry(registry with { Profiles = [.. registry.Profiles, registration] });
                registryCommitted = true;
            }
            catch
            {
                if (!registryCommitted && Directory.Exists(targetRoot))
                {
                    Directory.Move(targetRoot, stagedProfileRoot);
                }

                throw;
            }

            try
            {
                snapshots.CreateSnapshot("profile-created");
            }
            catch
            {
                // The profile is already committed. A missing initial snapshot must not report creation as failed.
            }

            return registration;
        }
        finally
        {
            ProfileSnapshotService.SafeDeleteDirectory(stagingRoot);
        }
    }

    private ProfileSetupStatus ResolveSetupStatus(string? profileId)
    {
        ManagedProfileRegistry registry;
        try
        {
            registry = ReadOrMigrateRegistry();
        }
        catch (Exception exception)
        {
            return new ProfileSetupStatus(
                WorkProfileSetupState.Invalid,
                null,
                "隔离空间注册表损坏：" + exception.Message);
        }

        var registration = string.IsNullOrWhiteSpace(profileId)
            ? registry.Profiles.FirstOrDefault(profile => profile.ProfileDirectoryName.Equals(
                  paths.WorkProfileDirectoryName,
                  StringComparison.OrdinalIgnoreCase)) ?? registry.Profiles.FirstOrDefault()
            : registry.Profiles.FirstOrDefault(profile =>
                profile.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (registration is null)
        {
            return new ProfileSetupStatus(
                WorkProfileSetupState.NotConfigured,
                null,
                string.IsNullOrWhiteSpace(profileId) ? null : "指定的隔离空间不存在。");
        }

        try
        {
            paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
            ValidateRegisteredProfile(registration);
            return new ProfileSetupStatus(WorkProfileSetupState.Configured, registration, null);
        }
        catch (Exception exception)
        {
            return new ProfileSetupStatus(
                WorkProfileSetupState.Invalid,
                registration,
                "隔离空间配置损坏：" + exception.Message);
        }
    }

    private ManagedProfileRegistry ReadOrMigrateRegistry()
    {
        if (File.Exists(paths.ProfilesRegistryFile))
        {
            return ReadRegistry();
        }

        LegacyWorkProfileRegistration? legacyRegistration = null;
        if (File.Exists(paths.WorkProfileRegistrationFile))
        {
            legacyRegistration = ReadLegacyRegistration();
        }

        var candidates = DiscoverCandidates();
        if (legacyRegistration is not null && candidates.All(candidate =>
                !candidate.ProfileDirectoryName.Equals(
                    legacyRegistration.ProfileDirectoryName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("旧版注册文件指向的隔离空间不存在或无效。");
        }

        var registrations = new List<ManagedProfileRegistration>();
        foreach (var candidate in candidates.OrderBy(candidate =>
                     legacyRegistration is not null && candidate.ProfileDirectoryName.Equals(
                         legacyRegistration.ProfileDirectoryName,
                         StringComparison.OrdinalIgnoreCase)
                         ? 0
                         : 1).ThenBy(candidate => candidate.ProfileDirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = legacyRegistration is not null && candidate.ProfileDirectoryName.Equals(
                legacyRegistration.ProfileDirectoryName,
                StringComparison.OrdinalIgnoreCase)
                ? legacyRegistration.DisplayName
                : candidate.DisplayName;
            registrations.Add(NewRegistration(
                candidate.ProfileDirectoryName,
                displayName,
                candidate.AuthMode,
                AllocateAccentColor(registrations),
                candidate.ProfileId));
        }

        var registry = new ManagedProfileRegistry(RegistrySchemaVersion, registrations);
        if (registrations.Count > 0 || legacyRegistration is not null)
        {
            foreach (var registration in registrations)
            {
                EnsureMarkerProfileId(
                    Path.Combine(
                        paths.ProfilesRoot,
                        registration.ProfileDirectoryName,
                        "codex-home",
                        "launcher-profile-v2.json"),
                    registration.ProfileId);
            }

            WriteRegistry(registry);
            if (legacyRegistration is not null)
            {
                File.Delete(paths.WorkProfileRegistrationFile);
            }
        }

        return registry;
    }

    private ManagedProfileRegistry ReadRegistry()
    {
        var registry = JsonSerializer.Deserialize<ManagedProfileRegistry>(
                           File.ReadAllText(paths.ProfilesRegistryFile),
                           JsonOptions)
                       ?? throw new InvalidDataException("隔离空间注册表不可解析。");
        return ValidateRegistry(registry);
    }

    private static ManagedProfileRegistry ValidateRegistry(ManagedProfileRegistry registry)
    {
        if (registry.SchemaVersion != RegistrySchemaVersion || registry.Profiles is null)
        {
            throw new InvalidDataException("隔离空间注册表版本无效。");
        }

        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accentColors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registry.Profiles)
        {
            ValidateRegistration(registration);
            if (!profileIds.Add(registration.ProfileId) ||
                !directories.Add(registration.ProfileDirectoryName) ||
                !accentColors.Add(registration.AccentColor))
            {
                throw new InvalidDataException("隔离空间注册表包含重复的 Profile ID、目录或角标颜色。");
            }
        }

        return registry with { Profiles = registry.Profiles.ToArray() };
    }

    private LegacyWorkProfileRegistration ReadLegacyRegistration()
    {
        var registration = JsonSerializer.Deserialize<LegacyWorkProfileRegistration>(
                               File.ReadAllText(paths.WorkProfileRegistrationFile),
                               JsonOptions)
                           ?? throw new InvalidDataException("旧版隔离空间注册文件不可解析。");
        if (registration.SchemaVersion != LegacyRegistrationSchemaVersion ||
            !LauncherPaths.IsSafeProfileDirectoryName(registration.ProfileDirectoryName))
        {
            throw new InvalidDataException("旧版隔离空间注册文件版本或目录名无效。");
        }

        _ = ValidateDisplayName(registration.DisplayName);
        return registration;
    }

    private void WriteRegistry(ManagedProfileRegistry registry)
    {
        var validated = ValidateRegistry(registry);
        WriteAtomic(paths.ProfilesRegistryFile, JsonSerializer.Serialize(validated, JsonOptions));
    }

    private IReadOnlyList<LegacyProfileCandidate> DiscoverCandidates()
    {
        if (!Directory.Exists(paths.ProfilesRoot))
        {
            return [];
        }

        LauncherPaths.EnsureNoReparsePoints(paths.ProfilesRoot);
        var result = new List<LegacyProfileCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(paths.ProfilesRoot)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var directoryName = info.Name;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !LauncherPaths.IsSafeProfileDirectoryName(directoryName))
                {
                    continue;
                }

                var home = Path.Combine(directory, "codex-home");
                var marker = Path.Combine(home, "launcher-profile-v2.json");
                LauncherPaths.EnsureNoReparsePoints(home);
                LauncherPaths.EnsureNoReparsePoints(marker);
                if (!TryReadMarker(
                        marker,
                        out var markerDisplayName,
                        out var markerColor,
                        out var markerProfileId))
                {
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(markerDisplayName)
                    ? directoryName
                    : ValidateDisplayName(markerDisplayName);
                var configPath = Path.Combine(home, "config.toml");
                var authPath = Path.Combine(home, "auth.json");
                LauncherPaths.EnsureNoReparsePoints(configPath);
                if (File.Exists(authPath))
                {
                    LauncherPaths.EnsureNoReparsePoints(authPath);
                }

                var authMode = InferAuthMode(
                    configPath,
                    authPath);
                var color = IsValidAccentColor(markerColor) ? markerColor! : "#7C3AED";
                var temporary = NewRegistration(
                    directoryName,
                    displayName,
                    authMode,
                    color,
                    markerProfileId);
                _ = ReadMetadataCore(
                    configPath,
                    authPath,
                    temporary);
                result.Add(new LegacyProfileCandidate(
                    directoryName,
                    displayName,
                    home,
                    authMode,
                    color,
                    markerProfileId));
            }
            catch
            {
                // Invalid or incomplete folders are not migration candidates.
            }
        }

        return result;
    }

    private void ValidateRegisteredProfile(ManagedProfileRegistration registration)
    {
        ValidateRegistration(registration);
        if (!TryReadMarker(paths.CompanyProfileMarker, out _, out _, out var markerProfileId))
        {
            throw new InvalidDataException("隔离空间标记缺失或不可解析。");
        }

        if (markerProfileId is not null &&
            !markerProfileId.Equals(registration.ProfileId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("隔离空间标记与注册表的 Profile ID 不一致。");
        }

        EnsureMarkerProfileId(paths.CompanyProfileMarker, registration.ProfileId);
        _ = ReadMetadataCore(paths.CompanyConfig, paths.CompanyAuth, registration);
    }

    private static void ValidateRegistration(ManagedProfileRegistration registration)
    {
        if (registration.SchemaVersion != RegistrySchemaVersion ||
            !LauncherPaths.IsSafeProfileDirectoryName(registration.ProfileId) ||
            !LauncherPaths.IsSafeProfileDirectoryName(registration.ProfileDirectoryName) ||
            !Enum.IsDefined(registration.AuthMode) ||
            !IsValidAccentColor(registration.AccentColor))
        {
            throw new InvalidDataException("隔离空间注册信息的版本、标识、目录、认证方式或颜色无效。");
        }

        _ = ValidateDisplayName(registration.DisplayName);
    }

    private static CompanyProfileMetadata ReadMetadataCore(
        string configPath,
        string authPath,
        ManagedProfileRegistration registration,
        bool requireUsableAuth = true)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("隔离空间 config.toml 不存在。", configPath);
        }

        var document = ProfileConfigDocument.Load(configPath);
        var provider = "chatgpt";
        var providerName = "ChatGPT Account";
        var baseUrl = string.Empty;
        var model = "由账号与 App 决定";
        var effort = document.GetString(null, "model_reasoning_effort", "high");
        var authConfigured = registration.AuthMode == ProfileAuthMode.ChatGptAccount;
        var configuredProvider = document.GetString(null, "model_provider").Trim();

        if (registration.AuthMode != ProfileAuthMode.CustomResponses &&
            !string.IsNullOrWhiteSpace(configuredProvider) &&
            !configuredProvider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("隔离空间注册的认证方式与 config.toml 中的第三方 Provider 不一致。");
        }

        if (registration.AuthMode != ProfileAuthMode.ChatGptAccount)
        {
            model = ValidateText(document.GetString(null, "model"), "模型", 120);
            effort = ValidateReasoningEffort(effort);
            authConfigured = IsAuthConfigured(authPath);
            if (!authConfigured && requireUsableAuth)
            {
                throw new InvalidDataException("隔离空间 API Key 缺失、损坏或仍为占位值。");
            }

            if (registration.AuthMode == ProfileAuthMode.CustomResponses)
            {
                provider = ValidateProviderId(configuredProvider);
                var section = $"model_providers.{provider}";
                providerName = ValidateText(document.GetString(section, "name", provider), "Provider 名称", 80);
                baseUrl = ValidateAndNormalizeBaseUrl(document.GetString(section, "base_url"));
            }
            else
            {
                provider = "openai";
                providerName = "OpenAI API";
                baseUrl = "https://api.openai.com/v1";
            }
        }

        return new CompanyProfileMetadata(
            ValidateDisplayName(registration.DisplayName),
            provider,
            providerName,
            model,
            effort,
            baseUrl,
            File.GetLastWriteTimeUtc(configPath),
            authConfigured,
            registration.ProfileId,
            registration.AuthMode,
            registration.AccentColor);
    }

    private static ProfileAuthMode InferAuthMode(string configPath, string authPath)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("隔离空间 config.toml 不存在。", configPath);
        }

        var document = ProfileConfigDocument.Load(configPath);
        var provider = document.GetString(null, "model_provider").Trim();
        if (!string.IsNullOrWhiteSpace(provider) &&
            !provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
        {
            return ProfileAuthMode.CustomResponses;
        }

        return HasUsableApiKeyForInference(authPath)
            ? ProfileAuthMode.OpenAiApiKey
            : ProfileAuthMode.ChatGptAccount;
    }

    private static bool HasUsableApiKeyForInference(string authPath)
    {
        if (!File.Exists(authPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(authPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("隔离空间 auth.json 必须是 JSON 对象。");
            }

            if (!document.RootElement.TryGetProperty("OPENAI_API_KEY", out var property))
            {
                return false;
            }

            if (property.ValueKind != JsonValueKind.String || !IsUsableApiKey(property.GetString()))
            {
                throw new InvalidDataException("隔离空间 auth.json 包含空值或占位 API Key。");
            }

            return true;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("隔离空间 auth.json 无法解析。", exception);
        }
    }

    private static ProviderSettings ValidateProviderSettings(
        ProfileSetupRequest request,
        bool requireApiKey)
    {
        if (request.AuthMode == ProfileAuthMode.ChatGptAccount)
        {
            return new ProviderSettings(string.Empty, string.Empty, string.Empty, "high", string.Empty);
        }

        var model = ValidateText(request.Model, "模型", 120);
        var reasoningEffort = ValidateReasoningEffort(request.ReasoningEffort);
        var providerId = request.AuthMode == ProfileAuthMode.CustomResponses
            ? ValidateProviderId(request.ProviderId)
            : "openai";
        var providerName = request.AuthMode == ProfileAuthMode.CustomResponses
            ? ValidateText(request.ProviderName, "Provider 名称", 80)
            : "OpenAI";
        var baseUrl = request.AuthMode == ProfileAuthMode.CustomResponses
            ? ValidateAndNormalizeBaseUrl(request.BaseUrl)
            : "https://api.openai.com/v1";
        if (requireApiKey)
        {
            _ = ValidateApiKey(request.ApiKey);
        }

        return new ProviderSettings(providerId, providerName, model, reasoningEffort, baseUrl);
    }

    private static void ApplyProfileSettings(
        ProfileConfigDocument document,
        ProfileAuthMode authMode,
        ProviderSettings? settings)
    {
        var previousProvider = document.GetString(null, "model_provider").Trim();
        if (!string.IsNullOrWhiteSpace(previousProvider) && ProviderIdRegex().IsMatch(previousProvider))
        {
            document.RemoveSectionTree($"model_providers.{previousProvider}");
        }

        document.SetString(null, "network_access", "enabled");
        document.SetBool(null, "disable_response_storage", true);
        if (authMode == ProfileAuthMode.ChatGptAccount)
        {
            document.RemoveKey(null, "model_provider");
            document.RemoveKey(null, "model");
            document.RemoveKey(null, "model_reasoning_effort");
            return;
        }

        ArgumentNullException.ThrowIfNull(settings);
        document.SetString(null, "model", settings.Model);
        document.SetString(null, "model_reasoning_effort", settings.ReasoningEffort);
        if (authMode == ProfileAuthMode.OpenAiApiKey)
        {
            document.RemoveKey(null, "model_provider");
            return;
        }

        document.SetString(null, "model_provider", settings.ProviderId);
        var section = $"model_providers.{settings.ProviderId}";
        document.SetString(section, "name", settings.ProviderName);
        document.SetString(section, "base_url", settings.BaseUrl);
        document.SetString(section, "wire_api", "responses");
        document.SetBool(section, "requires_openai_auth", true);
    }

    private ExistingProfileBinding ValidateExistingProfile(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new ArgumentException("请选择已有工作空间目录或 Codex Home。", nameof(selectedPath));
        }

        var selected = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(selectedPath.Trim().Trim('"')));
        if (!Directory.Exists(selected))
        {
            throw new DirectoryNotFoundException("所选工作空间目录不存在。");
        }

        LauncherPaths.EnsureNoReparsePoints(selected);

        var directConfig = Path.Combine(selected, "config.toml");
        var nestedHome = Path.Combine(selected, "codex-home");
        var codexHome = File.Exists(directConfig)
            ? selected
            : File.Exists(Path.Combine(nestedHome, "config.toml"))
                ? nestedHome
                : throw new InvalidDataException("所选目录不是有效工作空间：缺少 codex-home/config.toml。");
        var homeInfo = new DirectoryInfo(codexHome);
        var profileRoot = homeInfo.Parent;
        if (!homeInfo.Name.Equals("codex-home", StringComparison.OrdinalIgnoreCase) ||
            profileRoot?.Parent is null ||
            !PathsEqual(profileRoot.Parent.FullName, paths.ProfilesRoot) ||
            !LauncherPaths.IsSafeProfileDirectoryName(profileRoot.Name))
        {
            throw new InvalidOperationException(
                "只能原地接入多开器本地 profiles 目录下保留的工作空间；个人或外部 Codex Home 不会被绑定。");
        }

        LauncherPaths.EnsureNoReparsePoints(profileRoot.FullName);
        LauncherPaths.EnsureNoReparsePoints(codexHome);

        var configPath = Path.Combine(codexHome, "config.toml");
        LauncherPaths.EnsureNoReparsePoints(configPath);

        var authPath = Path.Combine(codexHome, "auth.json");
        if (File.Exists(authPath))
        {
            LauncherPaths.EnsureNoReparsePoints(authPath);
        }

        var markerPath = Path.Combine(codexHome, "launcher-profile-v2.json");
        LauncherPaths.EnsureNoReparsePoints(markerPath);
        if (!TryReadMarker(markerPath, out _, out var markerColor, out var markerProfileId))
        {
            throw new InvalidDataException("所选目录缺少有效的多开器工作空间标记，无法安全接入。");
        }

        var electronData = Path.Combine(profileRoot.FullName, "electron");
        if (Directory.Exists(electronData))
        {
            LauncherPaths.EnsureNoReparsePoints(electronData);
        }

        return new ExistingProfileBinding(
            profileRoot.Name,
            codexHome,
            markerColor,
            markerProfileId);
    }

    private string AllocateProfileDirectoryName(IEnumerable<ManagedProfileRegistration> profiles)
    {
        var registeredDirectories = profiles
            .Select(profile => profile.ProfileDirectoryName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < 10000; index++)
        {
            var name = index == 1
                ? LauncherPaths.DefaultWorkProfileDirectoryName
                : $"{LauncherPaths.DefaultWorkProfileDirectoryName}-{index}";
            if (!registeredDirectories.Contains(name) &&
                !Directory.Exists(Path.Combine(paths.ProfilesRoot, name)))
            {
                return name;
            }
        }

        throw new IOException("无法分配新的隔离空间目录。");
    }

    private static ManagedProfileRegistration NewRegistration(
        string directoryName,
        string displayName,
        ProfileAuthMode authMode,
        string accentColor,
        string? profileId = null)
    {
        var now = DateTime.UtcNow;
        return new ManagedProfileRegistration(
            RegistrySchemaVersion,
            profileId ?? Guid.NewGuid().ToString("N"),
            directoryName,
            ValidateDisplayName(displayName),
            authMode,
            accentColor.ToUpperInvariant(),
            now,
            now);
    }

    private static string AllocateAccentColor(IEnumerable<ManagedProfileRegistration> profiles)
    {
        var used = profiles
            .Select(profile => profile.AccentColor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var color in AccentPalette)
        {
            if (!used.Contains(color))
            {
                return color;
            }
        }

        for (var index = used.Count; index < used.Count + 360; index++)
        {
            var hue = (index * 137.508) % 360;
            var color = HslToHex(hue, 0.68, 0.56);
            if (!used.Contains(color))
            {
                return color;
            }
        }

        throw new InvalidOperationException("无法为新隔离空间分配唯一颜色。");
    }

    private static string HslToHex(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var segment = hue / 60;
        var secondary = chroma * (1 - Math.Abs(segment % 2 - 1));
        var (red, green, blue) = segment switch
        {
            < 1 => (chroma, secondary, 0d),
            < 2 => (secondary, chroma, 0d),
            < 3 => (0d, chroma, secondary),
            < 4 => (0d, secondary, chroma),
            < 5 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        var match = lightness - chroma / 2;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{(int)Math.Round((red + match) * 255):X2}{(int)Math.Round((green + match) * 255):X2}{(int)Math.Round((blue + match) * 255):X2}");
    }

    private static void WriteMarker(
        string destination,
        string displayName,
        string origin,
        bool importedSkills,
        bool importedMemories,
        ProfileAuthMode authMode,
        string accentColor,
        string profileId)
    {
        var marker = new WorkProfileMarker(
            MarkerSchemaVersion,
            DateTime.UtcNow,
            "isolated-managed-profile",
            displayName,
            origin,
            importedSkills,
            importedMemories,
            authMode,
            accentColor,
            profileId);
        WriteAtomic(destination, JsonSerializer.Serialize(marker, JsonOptions));
    }

    private static bool TryReadMarker(
        string markerPath,
        out string? displayName,
        out string? accentColor,
        out string? profileId)
    {
        displayName = null;
        accentColor = null;
        profileId = null;
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            var schema = TryGetProperty(document.RootElement, "SchemaVersion", out var schemaProperty) &&
                         schemaProperty.TryGetInt32(out var parsedSchema)
                ? parsedSchema
                : 0;
            if (schema is < 2 or > MarkerSchemaVersion)
            {
                return false;
            }

            if (!TryGetProperty(document.RootElement, "Authority", out var authorityProperty) ||
                authorityProperty.ValueKind != JsonValueKind.String ||
                authorityProperty.GetString() is not { } authority ||
                !(authority.Equals("company-app-codex-home", StringComparison.OrdinalIgnoreCase) ||
                  authority.Equals("legacy-isolated-profile", StringComparison.OrdinalIgnoreCase) ||
                  authority.Equals("isolated-work-profile", StringComparison.OrdinalIgnoreCase) ||
                  authority.Equals("isolated-managed-profile", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (TryGetProperty(document.RootElement, "DisplayName", out var displayProperty) &&
                displayProperty.ValueKind == JsonValueKind.String)
            {
                displayName = displayProperty.GetString();
            }

            if (TryGetProperty(document.RootElement, "AccentColor", out var colorProperty) &&
                colorProperty.ValueKind == JsonValueKind.String)
            {
                accentColor = colorProperty.GetString();
            }

            if (TryGetProperty(document.RootElement, "ProfileId", out var profileIdProperty) &&
                profileIdProperty.ValueKind == JsonValueKind.String)
            {
                profileId = profileIdProperty.GetString();
                if (!LauncherPaths.IsSafeProfileDirectoryName(profileId))
                {
                    return false;
                }
            }

            if (schema >= MarkerSchemaVersion && profileId is null)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureMarkerProfileId(string markerPath, string profileId)
    {
        if (!LauncherPaths.IsSafeProfileDirectoryName(profileId))
        {
            throw new InvalidDataException("Profile ID 无效，无法写入空间标记。");
        }

        LauncherPaths.EnsureNoReparsePoints(markerPath);
        if (!TryReadMarker(markerPath, out _, out _, out var existingProfileId))
        {
            throw new InvalidDataException("隔离空间标记缺失或不可解析。");
        }

        if (existingProfileId is not null &&
            !existingProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("隔离空间标记已绑定不同的 Profile ID。");
        }

        var marker = JsonSerializer.Deserialize<WorkProfileMarker>(
                         File.ReadAllText(markerPath),
                         JsonOptions)
                     ?? throw new InvalidDataException("隔离空间标记不可解析。");

        if (marker.SchemaVersion == MarkerSchemaVersion &&
            marker.ProfileId?.Equals(profileId, StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        WriteAtomic(
            markerPath,
            JsonSerializer.Serialize(
                marker with
                {
                    SchemaVersion = MarkerSchemaVersion,
                    ProfileId = profileId
                },
                JsonOptions));
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string ValidateDisplayName(string? value) => ValidateText(value, "显示名", 60);

    private static string ValidateProviderId(string? value)
    {
        var result = ValidateText(value, "Provider ID", 64);
        if (!ProviderIdRegex().IsMatch(result))
        {
            throw new ArgumentException("Provider ID 只能包含英文字母、数字、短横线和下划线。", nameof(value));
        }

        return result;
    }

    private static string ValidateReasoningEffort(string? value)
    {
        var result = ValidateText(value, "推理等级", 16).ToLowerInvariant();
        if (!ReasoningEfforts.Contains(result))
        {
            throw new ArgumentException("推理等级必须是 minimal、low、medium、high 或 xhigh。", nameof(value));
        }

        return result;
    }

    private static string ValidateText(string? value, string fieldName, int maximumLength)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length == 0 || result.Length > maximumLength || result.Any(char.IsControl))
        {
            throw new ArgumentException($"{fieldName}不能为空、过长或包含控制字符。", fieldName);
        }

        return result;
    }

    private static string ValidateApiKey(string? value)
    {
        var result = value?.Trim() ?? string.Empty;
        if (!IsUsableApiKey(result) || result.Any(char.IsControl))
        {
            throw new ArgumentException("API Key 不能为空或仍为占位值。", nameof(value));
        }

        return result;
    }

    private static bool IsUsableApiKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return !normalized.Equals("YOUR_API_KEY_HERE", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("YOUR_KEY", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Equals("API_KEY", StringComparison.OrdinalIgnoreCase) &&
               !(normalized.StartsWith('<') && normalized.EndsWith('>'));
    }

    private static bool IsValidAccentColor(string? value) =>
        value is not null && AccentColorRegex().IsMatch(value);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void WriteApiKeyAtomic(string destination, string apiKey)
    {
        var content = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = apiKey },
            JsonOptions);
        WriteAtomic(destination, content);
    }

    private static void WriteAtomic(string destination, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // A completed atomic replace makes temporary cleanup best effort only.
            }
        }
    }

    private T WithInitializationGate<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, InitializationGateName);
        var lockTaken = false;
        try
        {
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.FromSeconds(45));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                throw new TimeoutException("等待隔离空间配置操作超时。");
            }

            return action();
        }
        finally
        {
            if (lockTaken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private sealed record ProviderSettings(
        string ProviderId,
        string ProviderName,
        string Model,
        string ReasoningEffort,
        string BaseUrl);

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdRegex();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccentColorRegex();
}
