using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexChannelLauncher.Core;

public sealed record WorkProfileMarker(
    int SchemaVersion,
    DateTime InitializedAtUtc,
    string Authority,
    string DisplayName,
    string Origin,
    bool ImportedSkills,
    bool ImportedMemories);

public sealed partial class CompanyProfileManager(
    LauncherPaths paths,
    ProfileSnapshotService snapshots)
{
    private const int RegistrationSchemaVersion = 1;
    private const int MarkerSchemaVersion = 3;
    private const string InitializationGateName = @"Local\CodexChannelLauncher.WorkProfileInitialization";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ReasoningEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimal", "low", "medium", "high", "xhigh"
    };

    public bool IsInitialized => GetSetupStatus().State == WorkProfileSetupState.Configured;

    public ProfileSetupStatus GetSetupStatus()
    {
        paths.EnsureRuntimeDirectories();
        paths.ValidateIsolationBoundaries();

        return WithInitializationGate(() => ResolveSetupStatus(autoRegisterUnique: true));
    }

    public CompanyProfileMetadata EnsureInitialized()
    {
        var status = GetSetupStatus();
        return status.State switch
        {
            WorkProfileSetupState.Configured => ReadMetadataCore(
                paths.CompanyConfig,
                paths.CompanyAuth,
                status.Registration!.DisplayName),
            WorkProfileSetupState.Invalid => throw new InvalidDataException(
                status.Problem ?? "工作空间配置已损坏，请重新配置。"),
            _ => throw new InvalidOperationException(
                status.Candidates.Count > 1
                    ? "发现多个旧工作空间，请先在配置向导中选择一个。"
                    : "工作空间尚未配置，请先完成首次配置。")
        };
    }

    public CompanyProfileMetadata ReadMetadata() => EnsureInitialized();

    public WorkProfileRegistration Configure(ProfileSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        paths.EnsureRuntimeDirectories();
        paths.ValidateIsolationBoundaries();

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
            if (!document.RootElement.TryGetProperty("OPENAI_API_KEY", out var property))
            {
                return false;
            }

            var value = property.GetString();
            return IsUsableApiKey(value);
        }
        catch
        {
            return false;
        }
    }

    private WorkProfileRegistration ConfigureCore(ProfileSetupRequest request)
    {
        var displayName = ValidateDisplayName(request.DisplayName);
        var currentStatus = ResolveSetupStatus(autoRegisterUnique: false);

        return request.Mode switch
        {
            ProfileSetupMode.RegisterExisting => RegisterExisting(request, displayName),
            ProfileSetupMode.Update => UpdateExisting(request, displayName, currentStatus),
            ProfileSetupMode.Create => CreateProfile(request, displayName, importSource: null, currentStatus),
            ProfileSetupMode.Import => CreateProfile(
                request,
                displayName,
                ValidateImportSource(request.ImportSourceHome),
                currentStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "未知的工作空间配置模式。")
        };
    }

    private WorkProfileRegistration RegisterExisting(ProfileSetupRequest request, string displayName)
    {
        if (!LauncherPaths.IsSafeProfileDirectoryName(request.ExistingProfileDirectoryName))
        {
            throw new ArgumentException("请选择有效的旧工作空间。", nameof(request));
        }

        var candidate = DiscoverCandidates().FirstOrDefault(item =>
            item.ProfileDirectoryName.Equals(
                request.ExistingProfileDirectoryName,
                StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            throw new InvalidOperationException("所选旧工作空间已不存在或不再有效，请刷新后重试。");
        }

        var registration = NewRegistration(candidate.ProfileDirectoryName, displayName);
        paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
        WriteRegistration(registration);
        paths.EnsureRuntimeDirectories();
        return registration;
    }

    private WorkProfileRegistration UpdateExisting(
        ProfileSetupRequest request,
        string displayName,
        ProfileSetupStatus currentStatus)
    {
        var registration = currentStatus.Registration ?? TryReadRegistration()
            ?? throw new InvalidOperationException("没有可更新的工作空间注册信息。");
        paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
        paths.EnsureWorkProfileDirectories();

        var settings = ValidateProviderSettings(request, requireApiKey: false);
        if (string.IsNullOrWhiteSpace(request.ApiKey) && !IsAuthConfigured(paths.CompanyAuth))
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
        ApplyProviderSettings(document, settings);
        document.SaveAtomic(paths.CompanyConfig);

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            WriteApiKeyAtomic(paths.CompanyAuth, ValidateApiKey(request.ApiKey));
        }

        var updated = registration with
        {
            DisplayName = displayName,
            RegisteredAtUtc = DateTime.UtcNow
        };
        WriteMarker(paths.CompanyProfileMarker, displayName, "updated", false, false);
        WriteRegistration(updated);
        return updated;
    }

    private WorkProfileRegistration CreateProfile(
        ProfileSetupRequest request,
        string displayName,
        string? importSource,
        ProfileSetupStatus currentStatus)
    {
        if (currentStatus.State == WorkProfileSetupState.Configured)
        {
            throw new InvalidOperationException("工作空间已配置；请使用编辑模式修改现有配置。");
        }

        ProviderSettings? settings = null;
        CompanyProfileMetadata? importedMetadata = null;
        if (importSource is null)
        {
            settings = ValidateProviderSettings(request, requireApiKey: true);
        }
        else
        {
            importedMetadata = ReadMetadataCore(
                Path.Combine(importSource, "config.toml"),
                Path.Combine(importSource, "auth.json"),
                displayName);
        }

        var directoryName = AllocateProfileDirectoryName();
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
                ApplyProviderSettings(document, settings!);
                document.SaveAtomic(Path.Combine(stagedHome, "config.toml"));
                WriteApiKeyAtomic(
                    Path.Combine(stagedHome, "auth.json"),
                    ValidateApiKey(request.ApiKey));
            }
            else
            {
                (importedSkills, importedMemories) = CopyImportAllowlist(importSource, stagedHome);
                _ = importedMetadata;
            }

            WriteMarker(
                Path.Combine(stagedHome, "launcher-profile-v2.json"),
                displayName,
                importSource is null ? "created" : "imported",
                importedSkills,
                importedMemories);

            if (Directory.Exists(targetRoot))
            {
                throw new IOException("工作空间目标目录已存在，请重试。");
            }

            Directory.Move(stagedProfileRoot, targetRoot);
            var registration = NewRegistration(directoryName, displayName);
            paths.SelectWorkProfileDirectory(directoryName);
            WriteRegistration(registration);
            paths.EnsureWorkProfileDirectories();
            snapshots.CreateSnapshot(importSource is null ? "profile-created" : "profile-imported");
            return registration;
        }
        finally
        {
            ProfileSnapshotService.SafeDeleteDirectory(stagingRoot);
        }
    }

    private ProfileSetupStatus ResolveSetupStatus(bool autoRegisterUnique)
    {
        if (File.Exists(paths.WorkProfileRegistrationFile))
        {
            WorkProfileRegistration? registration = null;
            try
            {
                registration = ReadRegistration();
                paths.SelectWorkProfileDirectory(registration.ProfileDirectoryName);
                paths.EnsureRuntimeDirectories();
                ValidateRegisteredProfile(registration);
                return new ProfileSetupStatus(
                    WorkProfileSetupState.Configured,
                    registration,
                    [],
                    null);
            }
            catch (Exception exception)
            {
                return new ProfileSetupStatus(
                    WorkProfileSetupState.Invalid,
                    registration,
                    DiscoverCandidates(),
                    "工作空间注册或配置损坏：" + exception.Message);
            }
        }

        var candidates = DiscoverCandidates();
        if (autoRegisterUnique && candidates.Count == 1)
        {
            var candidate = candidates[0];
            var registration = NewRegistration(candidate.ProfileDirectoryName, candidate.DisplayName);
            paths.SelectWorkProfileDirectory(candidate.ProfileDirectoryName);
            WriteRegistration(registration);
            paths.EnsureRuntimeDirectories();
            return new ProfileSetupStatus(
                WorkProfileSetupState.Configured,
                registration,
                candidates,
                null);
        }

        return new ProfileSetupStatus(
            WorkProfileSetupState.NotConfigured,
            null,
            candidates,
            candidates.Count > 1
                ? "发现多个可兼容的旧工作空间，请选择要继续使用的一个。"
                : null);
    }

    private IReadOnlyList<WorkProfileCandidate> DiscoverCandidates()
    {
        if (!Directory.Exists(paths.ProfilesRoot))
        {
            return [];
        }

        var result = new List<WorkProfileCandidate>();
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
                if (!TryReadMarkerDisplayName(marker, out var markerDisplayName))
                {
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(markerDisplayName)
                    ? "工作空间"
                    : ValidateDisplayName(markerDisplayName);
                _ = ReadMetadataCore(
                    Path.Combine(home, "config.toml"),
                    Path.Combine(home, "auth.json"),
                    displayName);
                result.Add(new WorkProfileCandidate(directoryName, displayName, home));
            }
            catch
            {
                // Invalid or incomplete folders are not migration candidates.
            }
        }

        return result;
    }

    private void ValidateRegisteredProfile(WorkProfileRegistration registration)
    {
        if (!TryReadMarkerDisplayName(paths.CompanyProfileMarker, out _))
        {
            throw new InvalidDataException("工作空间标记缺失或不可解析。");
        }

        _ = ReadMetadataCore(paths.CompanyConfig, paths.CompanyAuth, registration.DisplayName);
    }

    private CompanyProfileMetadata ReadMetadataCore(
        string configPath,
        string authPath,
        string displayName)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("工作空间 config.toml 不存在。", configPath);
        }

        var document = ProfileConfigDocument.Load(configPath);
        var provider = document.GetString(null, "model_provider").Trim();
        ValidateProviderId(provider);
        var section = $"model_providers.{provider}";
        var providerName = ValidateText(
            document.GetString(section, "name", provider),
            "Provider 名称",
            80);
        var baseUrl = ValidateAndNormalizeBaseUrl(document.GetString(section, "base_url"));
        var model = ValidateText(document.GetString(null, "model"), "模型", 120);
        var effort = ValidateReasoningEffort(
            document.GetString(null, "model_reasoning_effort", "high"));
        if (!IsAuthConfigured(authPath))
        {
            throw new InvalidDataException("工作空间认证缺失、损坏或仍为占位值。");
        }

        return new CompanyProfileMetadata(
            ValidateDisplayName(displayName),
            provider,
            providerName,
            model,
            effort,
            baseUrl,
            File.GetLastWriteTimeUtc(configPath),
            true);
    }

    private static ProviderSettings ValidateProviderSettings(
        ProfileSetupRequest request,
        bool requireApiKey)
    {
        var providerId = ValidateProviderId(request.ProviderId);
        var providerName = ValidateText(request.ProviderName, "Provider 名称", 80);
        var model = ValidateText(request.Model, "模型", 120);
        var baseUrl = ValidateAndNormalizeBaseUrl(request.BaseUrl);
        var reasoningEffort = ValidateReasoningEffort(request.ReasoningEffort);
        if (requireApiKey)
        {
            _ = ValidateApiKey(request.ApiKey);
        }

        return new ProviderSettings(providerId, providerName, model, reasoningEffort, baseUrl);
    }

    private static void ApplyProviderSettings(
        ProfileConfigDocument document,
        ProviderSettings settings)
    {
        var previousProvider = document.GetString(null, "model_provider").Trim();
        if (!string.IsNullOrWhiteSpace(previousProvider) &&
            ProviderIdRegex().IsMatch(previousProvider) &&
            !previousProvider.Equals(settings.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            document.RemoveSectionTree($"model_providers.{previousProvider}");
        }

        document.SetString(null, "model_provider", settings.ProviderId);
        document.SetString(null, "model", settings.Model);
        document.SetString(null, "model_reasoning_effort", settings.ReasoningEffort);
        document.SetString(null, "network_access", "enabled");
        document.SetBool(null, "disable_response_storage", true);

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
        ProfileSnapshotService.AtomicCopy(
            Path.Combine(sourceHome, "auth.json"),
            Path.Combine(destinationHome, "auth.json"));

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

        foreach (var fileName in new[] { "config.toml", "auth.json" })
        {
            var requiredFile = Path.Combine(fullPath, fileName);
            if (!File.Exists(requiredFile) ||
                (File.GetAttributes(requiredFile) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"导入源缺少常规文件：{fileName}");
            }
        }

        if (LauncherPaths.IsUnder(fullPath, paths.OperationStagingRoot) ||
            LauncherPaths.IsUnder(fullPath, paths.RuntimeCacheRoot))
        {
            throw new InvalidOperationException("不能从启动器临时目录或运行副本导入配置。");
        }

        return fullPath;
    }

    private string AllocateProfileDirectoryName()
    {
        for (var index = 1; index < 1000; index++)
        {
            var name = index == 1
                ? LauncherPaths.DefaultWorkProfileDirectoryName
                : $"{LauncherPaths.DefaultWorkProfileDirectoryName}-{index}";
            if (!Directory.Exists(Path.Combine(paths.ProfilesRoot, name)))
            {
                return name;
            }
        }

        throw new IOException("无法分配新的工作空间目录。");
    }

    private WorkProfileRegistration ReadRegistration()
    {
        var registration = JsonSerializer.Deserialize<WorkProfileRegistration>(
                               File.ReadAllText(paths.WorkProfileRegistrationFile),
                               JsonOptions)
                           ?? throw new InvalidDataException("工作空间注册文件不可解析。");
        if (registration.SchemaVersion != RegistrationSchemaVersion ||
            !LauncherPaths.IsSafeProfileDirectoryName(registration.ProfileDirectoryName))
        {
            throw new InvalidDataException("工作空间注册文件版本或目录名无效。");
        }

        _ = ValidateDisplayName(registration.DisplayName);
        return registration;
    }

    private WorkProfileRegistration? TryReadRegistration()
    {
        try
        {
            return File.Exists(paths.WorkProfileRegistrationFile) ? ReadRegistration() : null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteRegistration(WorkProfileRegistration registration) =>
        WriteAtomic(
            paths.WorkProfileRegistrationFile,
            JsonSerializer.Serialize(registration, JsonOptions));

    private static WorkProfileRegistration NewRegistration(
        string directoryName,
        string displayName) =>
        new(RegistrationSchemaVersion, directoryName, displayName, DateTime.UtcNow);

    private static void WriteMarker(
        string destination,
        string displayName,
        string origin,
        bool importedSkills,
        bool importedMemories)
    {
        var marker = new WorkProfileMarker(
            MarkerSchemaVersion,
            DateTime.UtcNow,
            "isolated-work-profile",
            displayName,
            origin,
            importedSkills,
            importedMemories);
        WriteAtomic(destination, JsonSerializer.Serialize(marker, JsonOptions));
    }

    private static bool TryReadMarkerDisplayName(string markerPath, out string? displayName)
    {
        displayName = null;
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
            if (schema < 2)
            {
                return false;
            }

            if (TryGetProperty(document.RootElement, "DisplayName", out var displayProperty) &&
                displayProperty.ValueKind == JsonValueKind.String)
            {
                displayName = displayProperty.GetString();
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

    private static string ValidateDisplayName(string? value) =>
        ValidateText(value, "显示名", 60);

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
                throw new TimeoutException("等待工作空间配置操作超时。");
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
}
