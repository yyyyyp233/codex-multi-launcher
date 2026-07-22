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
    string AccentColor = "#7C3AED");

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
        string AccentColor);

    private const int LegacyRegistrationSchemaVersion = 1;
    private const int RegistrySchemaVersion = 1;
    private const int MarkerSchemaVersion = 4;
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
            ProfileSetupMode.Create => CreateProfile(request, displayName, null, registry),
            ProfileSetupMode.Import => CreateProfile(
                request,
                displayName,
                ValidateImportSource(request.ImportSourceHome),
                registry),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "未知的隔离空间配置模式。")
        };
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
        document.SaveAtomic(paths.CompanyConfig);

        if (request.AuthMode == ProfileAuthMode.ChatGptAccount)
        {
            if (authModeChanged && File.Exists(paths.CompanyAuth))
            {
                File.Delete(paths.CompanyAuth);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            WriteApiKeyAtomic(paths.CompanyAuth, ValidateApiKey(request.ApiKey));
        }

        var updated = registration with
        {
            DisplayName = displayName,
            AuthMode = request.AuthMode,
            UpdatedAtUtc = DateTime.UtcNow
        };
        WriteMarker(
            paths.CompanyProfileMarker,
            displayName,
            "updated",
            false,
            false,
            updated.AuthMode,
            updated.AccentColor);
        WriteRegistry(registry with
        {
            Profiles = registry.Profiles
                .Select(profile => profile.ProfileId.Equals(updated.ProfileId, StringComparison.OrdinalIgnoreCase)
                    ? updated
                    : profile)
                .ToArray()
        });
        return updated;
    }

    private ManagedProfileRegistration CreateProfile(
        ProfileSetupRequest request,
        string displayName,
        string? importSource,
        ManagedProfileRegistry registry)
    {
        ProviderSettings? settings = null;
        var authMode = request.AuthMode;
        if (importSource is null)
        {
            settings = ValidateProviderSettings(
                request,
                requireApiKey: authMode != ProfileAuthMode.ChatGptAccount);
        }
        else
        {
            authMode = InferAuthMode(
                Path.Combine(importSource, "config.toml"),
                Path.Combine(importSource, "auth.json"));
        }

        var directoryName = AllocateProfileDirectoryName(registry.Profiles);
        var accentColor = AllocateAccentColor(registry.Profiles);
        var targetRoot = Path.Combine(paths.ProfilesRoot, directoryName);
        var stagingRoot = Path.Combine(
            paths.OperationStagingRoot,
            "profile-setup-" + Guid.NewGuid().ToString("N"));
        var stagedProfileRoot = Path.Combine(stagingRoot, directoryName);
        var stagedHome = Path.Combine(stagedProfileRoot, "codex-home");
        var importedSkills = false;
        var importedMemories = false;
        Directory.CreateDirectory(stagedHome);

        try
        {
            if (importSource is null)
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
            }
            else
            {
                (importedSkills, importedMemories) = CopyImportAllowlist(importSource, stagedHome);
            }

            var registration = NewRegistration(directoryName, displayName, authMode, accentColor);
            _ = ReadMetadataCore(
                Path.Combine(stagedHome, "config.toml"),
                Path.Combine(stagedHome, "auth.json"),
                registration);
            WriteMarker(
                Path.Combine(stagedHome, "launcher-profile-v2.json"),
                displayName,
                importSource is null ? "created" : "imported",
                importedSkills,
                importedMemories,
                authMode,
                accentColor);

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
                snapshots.CreateSnapshot(importSource is null ? "profile-created" : "profile-imported");
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
                AllocateAccentColor(registrations)));
        }

        var registry = new ManagedProfileRegistry(RegistrySchemaVersion, registrations);
        if (registrations.Count > 0 || legacyRegistration is not null)
        {
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

    private void WriteRegistry(ManagedProfileRegistry registry) =>
        WriteAtomic(paths.ProfilesRegistryFile, JsonSerializer.Serialize(registry, JsonOptions));

    private IReadOnlyList<LegacyProfileCandidate> DiscoverCandidates()
    {
        if (!Directory.Exists(paths.ProfilesRoot))
        {
            return [];
        }

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
                if (!TryReadMarker(marker, out var markerDisplayName, out var markerColor))
                {
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(markerDisplayName)
                    ? directoryName
                    : ValidateDisplayName(markerDisplayName);
                var authMode = InferAuthMode(
                    Path.Combine(home, "config.toml"),
                    Path.Combine(home, "auth.json"));
                var color = IsValidAccentColor(markerColor) ? markerColor! : "#7C3AED";
                var temporary = NewRegistration(directoryName, displayName, authMode, color);
                _ = ReadMetadataCore(
                    Path.Combine(home, "config.toml"),
                    Path.Combine(home, "auth.json"),
                    temporary);
                result.Add(new LegacyProfileCandidate(directoryName, displayName, home, authMode, color));
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
        if (!TryReadMarker(paths.CompanyProfileMarker, out _, out _))
        {
            throw new InvalidDataException("隔离空间标记缺失或不可解析。");
        }

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

    private (bool ImportedSkills, bool ImportedMemories) CopyImportAllowlist(
        string sourceHome,
        string destinationHome)
    {
        ProfileSnapshotService.AtomicCopy(
            Path.Combine(sourceHome, "config.toml"),
            Path.Combine(destinationHome, "config.toml"));
        var sourceAuth = Path.Combine(sourceHome, "auth.json");
        if (File.Exists(sourceAuth))
        {
            ProfileSnapshotService.AtomicCopy(sourceAuth, Path.Combine(destinationHome, "auth.json"));
        }

        foreach (var fileName in ProfileContentPolicy.GlobalRuleFileNames)
        {
            var source = Path.Combine(sourceHome, fileName);
            if (File.Exists(source) &&
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) == 0)
            {
                ProfileSnapshotService.AtomicCopy(source, Path.Combine(destinationHome, fileName));
            }
        }

        var importedSkills = CopyFilteredDirectory(
            Path.Combine(sourceHome, "skills"),
            Path.Combine(destinationHome, "skills"),
            relativePath =>
            {
                var first = relativePath.Split('\\', '/')[0];
                return !first.Equals(".system", StringComparison.OrdinalIgnoreCase) &&
                       IsPortableFile(relativePath);
            });
        var importedMemories = CopyFilteredDirectory(
            Path.Combine(sourceHome, "memories"),
            Path.Combine(destinationHome, "memories"),
            relativePath => ProfileContentPolicy.IsManagedMemoryPath(relativePath) &&
                            IsPortableFile(relativePath));
        return (importedSkills, importedMemories);
    }

    private static bool CopyFilteredDirectory(
        string sourceRoot,
        string destinationRoot,
        Func<string, bool> include)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return false;
        }

        var copied = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", options))
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            if (!include(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!LauncherPaths.IsUnder(destination, destinationRoot))
            {
                throw new InvalidDataException("导入内容包含越界路径，已拒绝导入。");
            }

            ProfileSnapshotService.AtomicCopy(sourceFile, destination);
            copied = true;
        }

        return copied;
    }

    private string ValidateImportSource(string? sourceHome)
    {
        if (string.IsNullOrWhiteSpace(sourceHome))
        {
            throw new ArgumentException("请选择要导入的 Codex Home。", nameof(sourceHome));
        }

        var fullPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(sourceHome.Trim().Trim('"')));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("要导入的 Codex Home 不存在。");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("不能从链接或重解析点导入 Codex Home。");
        }

        var configPath = Path.Combine(fullPath, "config.toml");
        if (!File.Exists(configPath) ||
            (File.GetAttributes(configPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("导入源缺少常规文件：config.toml");
        }

        var authPath = Path.Combine(fullPath, "auth.json");
        if (File.Exists(authPath) &&
            (File.GetAttributes(authPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("导入源 auth.json 不能是链接或重解析点。");
        }

        if (LauncherPaths.IsUnder(fullPath, paths.OperationStagingRoot) ||
            LauncherPaths.IsUnder(fullPath, paths.RuntimeCacheRoot))
        {
            throw new InvalidOperationException("不能从启动器临时目录或运行副本导入配置。");
        }

        return fullPath;
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
        string accentColor)
    {
        var now = DateTime.UtcNow;
        return new ManagedProfileRegistration(
            RegistrySchemaVersion,
            Guid.NewGuid().ToString("N"),
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
        string accentColor)
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
            accentColor);
        WriteAtomic(destination, JsonSerializer.Serialize(marker, JsonOptions));
    }

    private static bool TryReadMarker(
        string markerPath,
        out string? displayName,
        out string? accentColor)
    {
        displayName = null;
        accentColor = null;
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
                !(authority.Equals("legacy-isolated-profile", StringComparison.OrdinalIgnoreCase) ||
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

            return true;
        }
        catch
        {
            return false;
        }
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

    private static bool IsPortableFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return !fileName.Equals("auth.json", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".snk", StringComparison.OrdinalIgnoreCase);
    }

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
