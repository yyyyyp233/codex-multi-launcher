using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexChannelLauncher.Core;

public enum ComparisonState
{
    Same,
    Different,
    PersonalOnly,
    CompanyOnly
}

public sealed record SkillComparisonItem(
    string Name,
    bool PersonalExists,
    bool CompanyExists,
    bool CompanyEnabled,
    ComparisonState State,
    int PersonalFileCount,
    int CompanyFileCount,
    long PersonalBytes,
    long CompanyBytes,
    int SameFileCount,
    int DifferentFileCount,
    int PersonalOnlyFileCount,
    int CompanyOnlyFileCount)
{
    public string StatusText => State switch
    {
        ComparisonState.Same => "一致",
        ComparisonState.Different => "内容不同",
        ComparisonState.PersonalOnly => "仅个人存在",
        ComparisonState.CompanyOnly => "仅工作空间存在",
        _ => "未知"
    };

    public string SizeText => $"个人 {FormatBytes(PersonalBytes)} · 工作空间 {FormatBytes(CompanyBytes)}";

    public string DiffText =>
        $"相同 {SameFileCount} · 冲突 {DifferentFileCount} · 仅个人 {PersonalOnlyFileCount} · 仅工作空间 {CompanyOnlyFileCount}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };
}

public sealed record GlobalRuleComparisonItem(
    string FileName,
    int Priority,
    bool PersonalExists,
    bool CompanyExists,
    bool PersonalEffective,
    bool CompanyEffective,
    ComparisonState State,
    long PersonalBytes,
    long CompanyBytes)
{
    public string StatusText => State switch
    {
        ComparisonState.Same => PersonalExists ? "内容一致" : "双方均不存在",
        ComparisonState.Different => "内容不同",
        ComparisonState.PersonalOnly => "仅个人存在",
        ComparisonState.CompanyOnly => "仅工作空间存在",
        _ => "未知"
    };

    public string PriorityText => Priority == 1 ? "优先规则" : "后备规则";

    public string SizeText => $"个人 {FormatBytes(PersonalBytes)} · 工作空间 {FormatBytes(CompanyBytes)}";

    public string EffectiveText =>
        $"个人：{DescribeRole(PersonalExists, PersonalBytes, PersonalEffective)} · " +
        $"工作空间：{DescribeRole(CompanyExists, CompanyBytes, CompanyEffective)}";

    private static string DescribeRole(bool exists, long bytes, bool effective) =>
        !exists ? "不存在" : bytes == 0 ? "空文件" : effective ? "当前生效" : "被 override 遮蔽";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F1} KB",
        _ => $"{bytes} B"
    };
}

public sealed record MemoryComparisonInfo(
    ComparisonState State,
    bool PersonalExists,
    bool CompanyExists,
    int PersonalFileCount,
    int CompanyFileCount,
    long PersonalBytes,
    long CompanyBytes,
    int SameFileCount,
    int DifferentFileCount,
    int PersonalOnlyFileCount,
    int CompanyOnlyFileCount,
    bool FeatureEnabled,
    bool UseMemories,
    bool GenerateMemories,
    bool DisableOnExternalContext)
{
    public string StatusText => State switch
    {
        ComparisonState.Same => "个人与工作空间记忆内容一致",
        ComparisonState.Different => "个人与工作空间记忆内容不同",
        ComparisonState.PersonalOnly => "仅个人空间存在记忆",
        ComparisonState.CompanyOnly => "仅工作空间存在记忆",
        _ => "未检测"
    };

    public string DiffText =>
        $"相同 {SameFileCount} · 冲突 {DifferentFileCount} · 仅个人 {PersonalOnlyFileCount} · 仅工作空间 {CompanyOnlyFileCount}";
}

public sealed record CapabilityStatus(
    string Id,
    string DisplayName,
    bool Installed,
    bool Enabled,
    string Detail);

public sealed record McpComparisonItem(
    string Name,
    bool PersonalExists,
    bool CompanyExists,
    ComparisonState State,
    string Transport,
    string Address,
    IReadOnlyList<string> Arguments,
    bool Enabled,
    bool PersonalContainsSensitiveValues)
{
    public string StatusText => State switch
    {
        ComparisonState.Same => "一致",
        ComparisonState.Different => "配置不同",
        ComparisonState.PersonalOnly => PersonalContainsSensitiveValues ? "仅个人 · 含敏感项" : "仅个人存在",
        ComparisonState.CompanyOnly => "仅工作空间存在",
        _ => "未知"
    };
}

public sealed record PermissionSettings(
    string ApprovalPolicy,
    string SandboxMode,
    bool NetworkEnabled,
    string WindowsSandbox,
    bool UsesNamedPermissionProfiles)
{
    public bool IsFullAccess =>
        ApprovalPolicy.Equals("never", StringComparison.OrdinalIgnoreCase) &&
        SandboxMode.Equals("danger-full-access", StringComparison.OrdinalIgnoreCase);
}

public sealed record ConfigurationCenterOverview(
    bool CompanyRunning,
    bool PersonalRunning,
    bool ProfileInitialized,
    int SkillCount,
    int McpCount,
    int SnapshotCount,
    MemoryComparisonInfo Memories,
    CapabilityStatus Chrome,
    CapabilityStatus ComputerUse,
    PermissionSettings Permissions);

public sealed class ConfigurationCenterService(
    LauncherPaths paths,
    CompanyProfileManager profileManager,
    ProfileSnapshotService snapshots,
    CodexPackageLocator packageLocator)
{
    private const string OperationGateName = @"Local\CodexChannelLauncher.ConfigurationOperation";

    public string CompanyConfigPath => paths.CompanyConfig;

    public string CompanyHome => paths.CompanyCodexHome;

    public string SnapshotDirectory => paths.SnapshotDirectory;

    public bool IsCompanyRunning()
    {
        var registration = profileManager.GetProfiles().FirstOrDefault(profile =>
            profile.ProfileDirectoryName.Equals(
                paths.WorkProfileDirectoryName,
                StringComparison.OrdinalIgnoreCase));
        if (registration is null)
        {
            return false;
        }

        var state = new StateStore(paths).Load();
        return state.ProfileRootProcesses is not null &&
               state.ProfileRootProcesses.TryGetValue(registration.ProfileId, out var marker) &&
               ProcessInventory.IsAlive(marker);
    }

    public bool IsPersonalRunning() => ProcessInventory.GetChatGptRoots()
        .Any(process => !LauncherPaths.IsUnder(process.ExecutablePath, paths.RuntimeCacheRoot));

    public ConfigurationCenterOverview GetOverview()
    {
        EnsureProfileReady();
        var skills = GetSkillsCore();
        var memories = GetMemoryComparisonCore();
        var capabilities = GetCapabilitiesCore();
        var permissions = GetPermissionsCore();
        return new ConfigurationCenterOverview(
            IsCompanyRunning(),
            IsPersonalRunning(),
            profileManager.IsInitialized,
            skills.Count,
            GetMcpComparisonsCore().Count,
            snapshots.ListSnapshots().Count,
            memories,
            capabilities.Chrome,
            capabilities.ComputerUse,
            permissions);
    }

    public IReadOnlyList<SkillComparisonItem> GetSkills()
    {
        EnsureProfileReady();
        return GetSkillsCore();
    }

    public IReadOnlyList<GlobalRuleComparisonItem> GetGlobalRules()
    {
        EnsureProfileReady();
        return GetGlobalRulesCore();
    }

    public SnapshotSummary MergeSkillFromPersonal(string name) =>
        MergeSkill(name, ChannelKind.Personal);

    public SnapshotSummary MergeSkillFromCompany(string name) =>
        MergeSkill(name, ChannelKind.Company);

    public SnapshotSummary MergeGlobalRuleFromPersonal(string fileName) =>
        MergeGlobalRule(fileName, ChannelKind.Personal);

    public SnapshotSummary MergeGlobalRuleFromCompany(string fileName) =>
        MergeGlobalRule(fileName, ChannelKind.Company);

    public SnapshotSummary ImportSkillFromPersonal(string name) => MergeSkillFromPersonal(name);

    public SnapshotSummary SetSkillEnabled(string name, bool enabled) => ExecuteConfigMutation(
        "before-skill-toggle",
        document =>
        {
            var skillPath = Path.Combine(ResolveChildDirectory(paths.CompanySkills, name), "SKILL.md");
            if (!File.Exists(skillPath))
            {
                throw new FileNotFoundException("工作空间 Skill 缺少 SKILL.md。", skillPath);
            }

            document.SetSkillEnabled(skillPath, enabled);
        });

    public MemoryComparisonInfo GetMemoryComparison()
    {
        EnsureProfileReady();
        return GetMemoryComparisonCore();
    }

    public SnapshotSummary ApplyMemorySettings(
        bool featureEnabled,
        bool useMemories,
        bool generateMemories,
        bool disableOnExternalContext) => ExecuteConfigMutation(
        "before-memory-settings",
        document =>
        {
            document.SetBool("features", "memories", featureEnabled);
            document.SetBool("memories", "use_memories", useMemories);
            document.SetBool("memories", "generate_memories", generateMemories);
            document.SetBool("memories", "disable_on_external_context", disableOnExternalContext);
        });

    public SnapshotSummary MergeMemoriesFromPersonal() =>
        MergeMemories(ChannelKind.Personal);

    public SnapshotSummary MergeMemoriesFromCompany() =>
        MergeMemories(ChannelKind.Company);

    public SnapshotSummary SyncMemoriesFromPersonal() => MergeMemoriesFromPersonal();

    public (CapabilityStatus Chrome, CapabilityStatus ComputerUse, IReadOnlyList<string> AllowedApps)
        GetCapabilities()
    {
        EnsureProfileReady();
        return GetCapabilitiesCore();
    }

    public SnapshotSummary ApplyCapabilities(
        bool chromeEnabled,
        bool computerUseEnabled,
        IEnumerable<string> allowedApps) => ExecuteConfigMutation(
        "before-capability-settings",
        document =>
        {
            if (chromeEnabled && !IsPluginInstalled(paths.CompanyCodexHome, "chrome"))
            {
                throw new InvalidOperationException("工作空间尚未安装 Chrome 插件，请先打开工作空间 App 插件页完成安装。");
            }

            if (computerUseEnabled && !IsPluginInstalled(paths.CompanyCodexHome, "computer-use"))
            {
                throw new InvalidOperationException("工作空间尚未安装 Computer Use 插件，请先打开工作空间 App 插件页完成安装。");
            }

            document.SetPluginEnabled("chrome@openai-bundled", chromeEnabled);
            document.SetPluginEnabled("computer-use@openai-bundled", computerUseEnabled);
            document.SetStringArray("computer_use.windows", "always_allowed_app_ids",
                allowedApps.Select(app => app.Trim()).Where(app => app.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        });

    public SnapshotSummary InstallBundledPlugin(string pluginName)
    {
        var pluginId = GetBundledPluginId(pluginName);
        return ExecuteMutation(
            $"before-{pluginName}-plugin-install",
            () =>
            {
                var bundled = ResolveBundledPlugin(pluginName);
                var pluginRoot = Path.Combine(
                    paths.CompanyCodexHome,
                    "plugins",
                    "cache",
                    "openai-bundled",
                    pluginName);
                var destination = ResolveChildDirectory(pluginRoot, bundled.Version);
                var sourceFingerprint = FingerprintDirectory(bundled.SourceDirectory);
                var destinationFingerprint = FingerprintDirectory(destination);

                if (!destinationFingerprint.Exists ||
                    !destinationFingerprint.Hash.Equals(sourceFingerprint.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    AtomicReplaceDirectory(bundled.SourceDirectory, destination);
                    destinationFingerprint = FingerprintDirectory(destination);
                    if (!destinationFingerprint.Exists ||
                        !destinationFingerprint.Hash.Equals(sourceFingerprint.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        ProfileSnapshotService.SafeDeleteDirectory(destination);
                        throw new IOException($"{bundled.DisplayName} 插件复制校验失败，未保留不完整文件。");
                    }
                }

                var document = ProfileConfigDocument.Load(paths.CompanyConfig);
                document.SetPluginEnabled(pluginId, true);
                document.SaveAtomic(paths.CompanyConfig);
            });
    }

    public IReadOnlyList<McpComparisonItem> GetMcpComparisons()
    {
        EnsureProfileReady();
        return GetMcpComparisonsCore();
    }

    public SnapshotSummary ImportMcpFromPersonal(string name) => ExecuteConfigMutation(
        "before-mcp-import",
        company => company.ImportMcpServer(ProfileConfigDocument.Load(paths.PersonalConfig), name));

    public SnapshotSummary SaveMcp(
        string name,
        string transport,
        string address,
        IEnumerable<string> arguments,
        bool enabled) => ExecuteConfigMutation(
        "before-mcp-save",
        document => document.UpsertMcpServer(name, transport, address, arguments, enabled));

    public SnapshotSummary RemoveMcp(string name) => ExecuteConfigMutation(
        "before-mcp-remove",
        document => document.RemoveMcpServer(name));

    public SnapshotSummary SetMcpEnabled(string name, bool enabled) => ExecuteConfigMutation(
        "before-mcp-toggle",
        document => document.SetMcpEnabled(name, enabled));

    public PermissionSettings GetPermissions()
    {
        EnsureProfileReady();
        return GetPermissionsCore();
    }

    public SnapshotSummary ApplyPermissions(PermissionSettings settings) => ExecuteConfigMutation(
        "before-permission-change",
        document =>
        {
            if (document.HasNamedPermissionProfiles())
            {
                throw new InvalidOperationException(
                    "当前配置使用新版 permission profiles，不能与 sandbox_mode 旧模式混写。启动器已拒绝自动转换。");
            }

            document.SetString(null, "approval_policy", settings.ApprovalPolicy);
            document.SetString(null, "sandbox_mode", settings.SandboxMode);
            document.SetString(null, "network_access", settings.NetworkEnabled ? "enabled" : "disabled");
            document.SetString("windows", "sandbox", settings.WindowsSandbox);
        });

    public IReadOnlyList<SnapshotSummary> GetSnapshots() => snapshots.ListSnapshots();

    public SnapshotSummary CreateSnapshotNow()
    {
        EnsureProfileReady();
        EnsureCompanyStopped();
        return WithOperationGate(() =>
        {
            EnsureCompanyStopped();
            return snapshots.CreateSnapshot("manual");
        });
    }

    public SnapshotSummary RestoreSnapshot(string archivePath)
    {
        EnsureProfileReady();
        var target = snapshots.GetSnapshotTarget(archivePath);
        if (target == "personal")
        {
            EnsurePersonalStopped();
        }
        else
        {
            EnsureCompanyStopped();
        }

        return WithOperationGate(() =>
        {
            if (target == "personal")
            {
                EnsurePersonalStopped();
            }
            else
            {
                EnsureCompanyStopped();
            }

            InvalidateMergeBases();
            return snapshots.Restore(archivePath);
        });
    }

    private SnapshotSummary MergeSkill(string name, ChannelKind sourceSide)
    {
        var sourceRoot = sourceSide == ChannelKind.Personal ? paths.PersonalSkills : paths.CompanySkills;
        var targetRoot = sourceSide == ChannelKind.Personal ? paths.CompanySkills : paths.PersonalSkills;
        var targetSide = sourceSide == ChannelKind.Personal ? ChannelKind.Company : ChannelKind.Personal;
        var source = ResolveChildDirectory(sourceRoot, name);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"{(sourceSide == ChannelKind.Personal ? "个人" : "工作空间")} Skill 不存在：{name}");
        }

        var destination = ResolveChildDirectory(targetRoot, name);
        return ExecuteBidirectionalMutation(
            $"before-skill-merge-{sourceSide.ToString().ToLowerInvariant()}",
            targetSide,
            () =>
            {
                InvalidateMergeBases();
                AtomicReplaceDirectory(source, destination, targetRoot);
            });
    }

    private SnapshotSummary MergeGlobalRule(string fileName, ChannelKind sourceSide)
    {
        var sourceRoot = sourceSide == ChannelKind.Personal ? paths.PersonalCodexHome : paths.CompanyCodexHome;
        var targetRoot = sourceSide == ChannelKind.Personal ? paths.CompanyCodexHome : paths.PersonalCodexHome;
        var targetSide = sourceSide == ChannelKind.Personal ? ChannelKind.Company : ChannelKind.Personal;
        var source = ResolveGlobalRulePath(sourceRoot, fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"{(sourceSide == ChannelKind.Personal ? "个人" : "工作空间")}全局规则不存在：{fileName}",
                source);
        }

        var destination = ResolveGlobalRulePath(targetRoot, fileName);
        return ExecuteBidirectionalMutation(
            $"before-global-rule-merge-{sourceSide.ToString().ToLowerInvariant()}",
            targetSide,
            () =>
            {
                InvalidateMergeBases();
                ProfileSnapshotService.AtomicCopy(source, destination);
            });
    }

    private SnapshotSummary MergeMemories(ChannelKind sourceSide)
    {
        var source = sourceSide == ChannelKind.Personal ? paths.PersonalMemories : paths.CompanyMemories;
        var destination = sourceSide == ChannelKind.Personal ? paths.CompanyMemories : paths.PersonalMemories;
        var targetSide = sourceSide == ChannelKind.Personal ? ChannelKind.Company : ChannelKind.Personal;
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"{(sourceSide == ChannelKind.Personal ? "个人" : "工作空间")} Memories 目录不存在。");
        }

        return ExecuteBidirectionalMutation(
            $"before-memory-merge-{sourceSide.ToString().ToLowerInvariant()}",
            targetSide,
            () =>
            {
                InvalidateMergeBases();
                MergeManagedMemoryFiles(source, destination);
            });
    }

    private IReadOnlyList<SkillComparisonItem> GetSkillsCore()
    {
        var personal = EnumerateNamedDirectories(paths.PersonalSkills);
        var company = EnumerateNamedDirectories(paths.CompanySkills);
        var companyConfig = ProfileConfigDocument.Load(paths.CompanyConfig);
        return personal.Keys.Union(company.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                personal.TryGetValue(name, out var personalPath);
                company.TryGetValue(name, out var companyPath);
                var diff = DiffDirectories(personalPath, companyPath, _ => true);
                var enabled = companyPath is not null &&
                              companyConfig.IsSkillEnabled(Path.Combine(companyPath, "SKILL.md"));
                return new SkillComparisonItem(
                    name,
                    personalPath is not null,
                    companyPath is not null,
                    enabled,
                    diff.State,
                    diff.PersonalFileCount,
                    diff.CompanyFileCount,
                    diff.PersonalBytes,
                    diff.CompanyBytes,
                    diff.SameFileCount,
                    diff.DifferentFileCount,
                    diff.PersonalOnlyFileCount,
                    diff.CompanyOnlyFileCount);
            })
            .ToArray();
    }

    private IReadOnlyList<GlobalRuleComparisonItem> GetGlobalRulesCore()
    {
        var personalEffective = ResolveEffectiveGlobalRule(paths.PersonalCodexHome);
        var companyEffective = ResolveEffectiveGlobalRule(paths.CompanyCodexHome);
        return ProfileContentPolicy.GlobalRuleFileNames
            .Select((fileName, index) =>
            {
                var personal = ProbeGlobalRule(paths.PersonalCodexHome, fileName);
                var company = ProbeGlobalRule(paths.CompanyCodexHome, fileName);
                var state = !personal.Exists
                    ? company.Exists ? ComparisonState.CompanyOnly : ComparisonState.Same
                    : !company.Exists
                        ? ComparisonState.PersonalOnly
                        : personal.Fingerprint.Equals(company.Fingerprint, StringComparison.OrdinalIgnoreCase)
                            ? ComparisonState.Same
                            : ComparisonState.Different;
                return new GlobalRuleComparisonItem(
                    fileName,
                    index + 1,
                    personal.Exists,
                    company.Exists,
                    personalEffective?.Equals(fileName, StringComparison.OrdinalIgnoreCase) == true,
                    companyEffective?.Equals(fileName, StringComparison.OrdinalIgnoreCase) == true,
                    state,
                    personal.Length,
                    company.Length);
            })
            .ToArray();
    }

    private MemoryComparisonInfo GetMemoryComparisonCore()
    {
        var diff = DiffDirectories(paths.PersonalMemories, paths.CompanyMemories, IsManagedMemoryPath);
        var document = ProfileConfigDocument.Load(paths.CompanyConfig);
        return new MemoryComparisonInfo(
            diff.State,
            Directory.Exists(paths.PersonalMemories),
            Directory.Exists(paths.CompanyMemories),
            diff.PersonalFileCount,
            diff.CompanyFileCount,
            diff.PersonalBytes,
            diff.CompanyBytes,
            diff.SameFileCount,
            diff.DifferentFileCount,
            diff.PersonalOnlyFileCount,
            diff.CompanyOnlyFileCount,
            document.GetBool("features", "memories"),
            document.GetBool("memories", "use_memories"),
            document.GetBool("memories", "generate_memories"),
            document.GetBool("memories", "disable_on_external_context"));
    }

    private (CapabilityStatus Chrome, CapabilityStatus ComputerUse, IReadOnlyList<string> AllowedApps)
        GetCapabilitiesCore()
    {
        var document = ProfileConfigDocument.Load(paths.CompanyConfig);
        var chromeInstalled = IsPluginInstalled(paths.CompanyCodexHome, "chrome");
        var computerInstalled = IsPluginInstalled(paths.CompanyCodexHome, "computer-use");
        var chromeEnabled = document.IsPluginEnabled("chrome@openai-bundled");
        var computerEnabled = document.IsPluginEnabled("computer-use@openai-bundled");
        return (
            new CapabilityStatus(
                "chrome",
                "Chrome 操作",
                chromeInstalled,
                chromeEnabled,
                chromeInstalled
                    ? $"插件已安装 · 工作空间配置{(chromeEnabled ? "已启用" : "未启用")}；扩展连接与网站授权仍由工作空间 App 管理"
                    : "插件未安装；可从当前官方 Codex App 内置包一键安装"),
            new CapabilityStatus(
                "computer-use",
                "电脑操作",
                computerInstalled,
                computerEnabled,
                computerInstalled
                    ? $"插件已安装 · 工作空间配置{(computerEnabled ? "已启用" : "未启用")}；Windows 前台桌面仍与个人实例共享"
                    : "插件未安装；可从当前官方 Codex App 内置包一键安装"),
            document.GetStringArray("computer_use.windows", "always_allowed_app_ids"));
    }

    private IReadOnlyList<McpComparisonItem> GetMcpComparisonsCore()
    {
        var personalServers = File.Exists(paths.PersonalConfig)
            ? ProfileConfigDocument.Load(paths.PersonalConfig).GetMcpServers()
                .ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, McpConfigInfo>(StringComparer.OrdinalIgnoreCase);
        var companyServers = ProfileConfigDocument.Load(paths.CompanyConfig).GetMcpServers()
            .ToDictionary(server => server.Name, StringComparer.OrdinalIgnoreCase);

        return personalServers.Keys.Union(companyServers.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                personalServers.TryGetValue(name, out var personal);
                companyServers.TryGetValue(name, out var company);
                var state = personal is null
                    ? ComparisonState.CompanyOnly
                    : company is null
                        ? ComparisonState.PersonalOnly
                        : personal.Fingerprint.Equals(company.Fingerprint, StringComparison.OrdinalIgnoreCase)
                            ? ComparisonState.Same
                            : ComparisonState.Different;
                var displayed = company ?? personal!;
                return new McpComparisonItem(
                    name,
                    personal is not null,
                    company is not null,
                    state,
                    displayed.Transport,
                    displayed.Address,
                    displayed.Arguments,
                    displayed.Enabled,
                    personal?.ContainsSensitiveValues == true);
            })
            .ToArray();
    }

    private PermissionSettings GetPermissionsCore()
    {
        var document = ProfileConfigDocument.Load(paths.CompanyConfig);
        return new PermissionSettings(
            document.GetString(null, "approval_policy", "on-request"),
            document.GetString(null, "sandbox_mode", "workspace-write"),
            document.GetString(null, "network_access", "disabled")
                .Equals("enabled", StringComparison.OrdinalIgnoreCase),
            document.GetString("windows", "sandbox", "unelevated"),
            document.HasNamedPermissionProfiles());
    }

    private SnapshotSummary ExecuteConfigMutation(string reason, Action<ProfileConfigDocument> mutation) =>
        ExecuteMutation(reason, () =>
        {
            var document = ProfileConfigDocument.Load(paths.CompanyConfig);
            mutation(document);
            document.SaveAtomic(paths.CompanyConfig);
        });

    private SnapshotSummary ExecuteMutation(string reason, Action mutation)
    {
        EnsureProfileReady();
        EnsureCompanyStopped();
        return WithOperationGate(() =>
        {
            EnsureCompanyStopped();
            var snapshot = snapshots.CreateSnapshot(reason);
            mutation();
            return snapshot;
        });
    }

    private SnapshotSummary ExecuteBidirectionalMutation(
        string reason,
        ChannelKind targetSide,
        Action mutation)
    {
        EnsureProfileReady();
        EnsureBothAppsStopped();
        return WithOperationGate(() =>
        {
            EnsureBothAppsStopped();
            var snapshot = targetSide == ChannelKind.Personal
                ? snapshots.CreatePersonalSnapshot(reason)
                : snapshots.CreateSnapshot(reason);
            mutation();
            return snapshot;
        });
    }

    private T WithOperationGate<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, OperationGateName);
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
                throw new TimeoutException("另一个多开器窗口正在修改配置或双向数据。");
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

    private void EnsureProfileReady()
    {
        if (profileManager.IsInitialized)
        {
            return;
        }

        EnsureCompanyStopped();
        profileManager.EnsureInitialized();
    }

    private void EnsureCompanyStopped()
    {
        if (IsCompanyRunning())
        {
            throw new InvalidOperationException("请先退出工作空间 Codex App，再修改、迁移或恢复配置。个人 Codex App 可以继续运行。");
        }
    }

    private void EnsurePersonalStopped()
    {
        if (IsPersonalRunning())
        {
            throw new InvalidOperationException("请先退出个人 Codex App，再修改或恢复个人 Skills、Memories 或全局规则。");
        }
    }

    private void EnsureBothAppsStopped()
    {
        var companyRunning = IsCompanyRunning();
        var personalRunning = IsPersonalRunning();
        if (!companyRunning && !personalRunning)
        {
            return;
        }

        var running = companyRunning && personalRunning
            ? "个人与工作空间 Codex App"
            : companyRunning
                ? "工作空间 Codex App"
                : "个人 Codex App";
        throw new InvalidOperationException($"双向合并前请先退出{running}，避免源目录或目标目录在复制期间变化。");
    }

    private void AtomicReplaceDirectory(string source, string destination)
        => AtomicReplaceDirectory(source, destination, paths.CompanyCodexHome);

    private void AtomicReplaceDirectory(string source, string destination, string destinationBoundary)
    {
        var fullSource = Path.GetFullPath(source);
        var fullDestination = Path.GetFullPath(destination);
        if (!Directory.Exists(fullSource) || !LauncherPaths.IsUnder(fullDestination, destinationBoundary))
        {
            throw new InvalidOperationException("目录同步边界无效。");
        }

        var staging = Path.Combine(paths.OperationStagingRoot, "copy-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(paths.OperationStagingRoot, "backup-" + Guid.NewGuid().ToString("N"));
        ProfileSnapshotService.CopyDirectory(fullSource, staging);
        var destinationExisted = Directory.Exists(fullDestination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        try
        {
            if (destinationExisted)
            {
                Directory.Move(fullDestination, backup);
            }

            Directory.Move(staging, fullDestination);
            ProfileSnapshotService.SafeDeleteDirectory(backup);
        }
        catch
        {
            ProfileSnapshotService.SafeDeleteDirectory(fullDestination);
            if (Directory.Exists(backup))
            {
                Directory.Move(backup, fullDestination);
            }

            throw;
        }
        finally
        {
            ProfileSnapshotService.SafeDeleteDirectory(staging);
        }
    }

    private void MergeManagedMemoryFiles(string sourceRoot, string destinationRoot)
    {
        var fullSource = Path.GetFullPath(sourceRoot);
        var fullDestination = Path.GetFullPath(destinationRoot);
        var knownRoots = new[] { paths.PersonalMemories, paths.CompanyMemories }
            .Select(Path.GetFullPath)
            .ToArray();
        if (!Directory.Exists(fullSource) ||
            !knownRoots.Any(root => PathsEqual(root, fullSource)) ||
            !knownRoots.Any(root => PathsEqual(root, fullDestination)) ||
            PathsEqual(fullSource, fullDestination))
        {
            throw new InvalidOperationException("Memories 合并边界无效。");
        }

        MergeManagedMemoryFilesCore(fullSource, fullDestination, paths.OperationStagingRoot);
    }

    private static void MergeManagedMemoryFilesCore(
        string fullSource,
        string fullDestination,
        string operationStagingRoot)
    {
        Directory.CreateDirectory(fullDestination);
        Directory.CreateDirectory(operationStagingRoot);
        var staging = Path.Combine(operationStagingRoot, "memory-merge-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(operationStagingRoot, "memory-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(backup);
        var applied = new List<(string Destination, string Backup, bool HadBackup)>();

        try
        {
            foreach (var sourceFile in EnumerateIncludedFiles(fullSource, IsManagedMemoryPath))
            {
                var relative = Path.GetRelativePath(fullSource, sourceFile);
                var destination = Path.GetFullPath(Path.Combine(fullDestination, relative));
                if (!LauncherPaths.IsUnder(destination, fullDestination))
                {
                    throw new InvalidOperationException("Memories 文件路径越界。");
                }

                if (File.Exists(destination) && FilesEqual(sourceFile, destination))
                {
                    continue;
                }

                var stagedFile = Path.Combine(staging, relative);
                ProfileSnapshotService.AtomicCopy(sourceFile, stagedFile);
            }

            foreach (var stagedFile in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(staging, path), StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(staging, stagedFile);
                var destination = Path.GetFullPath(Path.Combine(fullDestination, relative));
                var backupFile = Path.GetFullPath(Path.Combine(backup, relative));
                if (!LauncherPaths.IsUnder(destination, fullDestination) ||
                    !LauncherPaths.IsUnder(backupFile, backup))
                {
                    throw new InvalidOperationException("Memories 暂存路径越界。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var hadBackup = File.Exists(destination);
                if (hadBackup)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
                    File.Move(destination, backupFile);
                }

                try
                {
                    File.Move(stagedFile, destination);
                    applied.Add((destination, backupFile, hadBackup));
                }
                catch
                {
                    if (hadBackup && File.Exists(backupFile))
                    {
                        File.Move(backupFile, destination);
                    }

                    throw;
                }
            }

            ProfileSnapshotService.SafeDeleteDirectory(backup);
        }
        catch
        {
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                if (File.Exists(item.Destination))
                {
                    File.Delete(item.Destination);
                }

                if (item.HadBackup && File.Exists(item.Backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                    File.Move(item.Backup, item.Destination);
                }
            }

            throw;
        }
        finally
        {
            ProfileSnapshotService.SafeDeleteDirectory(staging);
            ProfileSnapshotService.SafeDeleteDirectory(backup);
        }
    }

    private static Dictionary<string, string> EnumerateNamedDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateDirectories(root)
            .Where(directory => !Path.GetFileName(directory).Equals(".system", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(directory => Path.GetFileName(directory)!, Path.GetFullPath,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveChildDirectory(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("资源名称不是安全的目录名。", nameof(name));
        }

        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!LauncherPaths.IsUnder(path, root))
        {
            throw new InvalidOperationException("资源路径越界。");
        }

        return path;
    }

    private static string ResolveGlobalRulePath(string root, string fileName)
    {
        if (!ProfileContentPolicy.IsGlobalRulePath(fileName))
        {
            throw new ArgumentException(
                "全局规则只允许 AGENTS.override.md 与 AGENTS.md。",
                nameof(fileName));
        }

        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, fileName));
        if (!LauncherPaths.IsUnder(path, fullRoot))
        {
            throw new InvalidOperationException("全局规则路径越界。");
        }

        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("全局规则文件是链接或重解析点，已拒绝访问。");
        }

        return path;
    }

    private static GlobalRuleFileProbe ProbeGlobalRule(string root, string fileName)
    {
        var path = ResolveGlobalRulePath(root, fileName);
        if (!File.Exists(path))
        {
            return new GlobalRuleFileProbe(false, 0, string.Empty);
        }

        var info = new FileInfo(path);
        return new GlobalRuleFileProbe(true, info.Length, ProfileSnapshotService.ComputeSha256(path));
    }

    private static string? ResolveEffectiveGlobalRule(string root)
    {
        foreach (var fileName in ProfileContentPolicy.GlobalRuleFileNames)
        {
            var path = ResolveGlobalRulePath(root, fileName);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return fileName;
            }
        }

        return null;
    }

    private static bool IsPluginInstalled(string codexHome, string pluginName)
    {
        var root = Path.Combine(codexHome, "plugins", "cache", "openai-bundled", pluginName);
        return Directory.Exists(root) && Directory.EnumerateDirectories(root)
            .Where(directory => !Path.GetFileName(directory).Equals("latest", StringComparison.OrdinalIgnoreCase))
             .Any(directory => File.Exists(Path.Combine(directory, ".codex-plugin", "plugin.json")));
    }

    private BundledPluginPackage ResolveBundledPlugin(string pluginName)
    {
        _ = GetBundledPluginId(pluginName);
        var package = packageLocator.Locate();
        var sourceDirectory = Path.GetFullPath(Path.Combine(
            package.InstallLocation,
            "app",
            "resources",
            "plugins",
            "openai-bundled",
            "plugins",
            pluginName));
        var pluginManifest = Path.Combine(sourceDirectory, ".codex-plugin", "plugin.json");
        if (!LauncherPaths.IsUnder(sourceDirectory, package.InstallLocation) || !File.Exists(pluginManifest))
        {
            throw new FileNotFoundException(
                $"当前 Codex App 安装包未提供 {pluginName} 插件，请先更新 Codex App。",
                pluginManifest);
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(pluginManifest));
        var root = manifest.RootElement;
        var manifestName = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var authorName = root.TryGetProperty("author", out var authorElement) &&
                         authorElement.TryGetProperty("name", out var authorNameElement)
            ? authorNameElement.GetString()
            : null;
        if (!string.Equals(manifestName, pluginName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(version) ||
            !string.Equals(authorName, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{pluginName} 插件清单与官方内置包不匹配，已拒绝安装。");
        }

        _ = ResolveChildDirectory(Path.Combine(paths.OperationStagingRoot, "version-check"), version);
        return new BundledPluginPackage(
            sourceDirectory,
            version,
            pluginName.Equals("computer-use", StringComparison.OrdinalIgnoreCase) ? "Computer Use" : "Chrome");
    }

    private static string GetBundledPluginId(string pluginName) => pluginName.ToLowerInvariant() switch
    {
        "chrome" => "chrome@openai-bundled",
        "computer-use" => "computer-use@openai-bundled",
        _ => throw new ArgumentOutOfRangeException(nameof(pluginName), "只允许安装 Chrome 或 Computer Use 官方内置插件。")
    };

    private static DirectoryDiffSummary DiffDirectories(
        string? personalDirectory,
        string? companyDirectory,
        Func<string, bool> include)
    {
        var personalExists = personalDirectory is not null && Directory.Exists(personalDirectory);
        var companyExists = companyDirectory is not null && Directory.Exists(companyDirectory);
        var personalFiles = EnumerateFileFingerprints(personalDirectory, include);
        var companyFiles = EnumerateFileFingerprints(companyDirectory, include);
        var same = 0;
        var different = 0;
        var personalOnly = 0;
        var companyOnly = 0;

        foreach (var relativePath in personalFiles.Keys.Union(
                     companyFiles.Keys,
                     StringComparer.OrdinalIgnoreCase))
        {
            var hasPersonal = personalFiles.TryGetValue(relativePath, out var personal);
            var hasCompany = companyFiles.TryGetValue(relativePath, out var company);
            if (hasPersonal && hasCompany)
            {
                if (personal!.Length == company!.Length &&
                    personal.Sha256.Equals(company.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    same++;
                }
                else
                {
                    different++;
                }
            }
            else if (hasPersonal)
            {
                personalOnly++;
            }
            else
            {
                companyOnly++;
            }
        }

        var state = !personalExists
            ? companyExists ? ComparisonState.CompanyOnly : ComparisonState.Same
            : !companyExists
                ? ComparisonState.PersonalOnly
                : different == 0 && personalOnly == 0 && companyOnly == 0
                    ? ComparisonState.Same
                    : ComparisonState.Different;
        return new DirectoryDiffSummary(
            state,
            personalFiles.Count,
            companyFiles.Count,
            personalFiles.Values.Sum(file => file.Length),
            companyFiles.Values.Sum(file => file.Length),
            same,
            different,
            personalOnly,
            companyOnly);
    }

    private static Dictionary<string, FileFingerprint> EnumerateFileFingerprints(
        string? directory,
        Func<string, bool> include)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
        }

        return EnumerateIncludedFiles(directory, include)
            .ToDictionary(
                path => Path.GetRelativePath(directory, path).Replace('\\', '/'),
                path =>
                {
                    var info = new FileInfo(path);
                    return new FileFingerprint(info.Length, ProfileSnapshotService.ComputeSha256(path));
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateIncludedFiles(
        string directory,
        Func<string, bool> include)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(path => include(Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsManagedMemoryPath(string relativePath)
        => ProfileContentPolicy.IsManagedMemoryPath(relativePath);

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        return leftInfo.Length == rightInfo.Length &&
               ProfileSnapshotService.ComputeSha256(left)
                   .Equals(ProfileSnapshotService.ComputeSha256(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private void InvalidateMergeBases()
    {
        ProfileSnapshotService.SafeDeleteDirectory(paths.MergeBaseDirectory);
        Directory.CreateDirectory(paths.MergeBaseDirectory);
    }

    private static ComparisonState Compare(DirectoryFingerprint personal, DirectoryFingerprint company)
    {
        if (!personal.Exists)
        {
            return company.Exists ? ComparisonState.CompanyOnly : ComparisonState.Same;
        }

        if (!company.Exists)
        {
            return ComparisonState.PersonalOnly;
        }

        return personal.Hash.Equals(company.Hash, StringComparison.OrdinalIgnoreCase)
            ? ComparisonState.Same
            : ComparisonState.Different;
    }

    private static DirectoryFingerprint FingerprintDirectory(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return new DirectoryFingerprint(false, 0, 0, string.Empty);
        }

        var files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        })
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            bytes += stream.Length;
        }

        return new DirectoryFingerprint(true, files.Length, bytes, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private sealed record DirectoryFingerprint(bool Exists, int FileCount, long Bytes, string Hash);

    private sealed record FileFingerprint(long Length, string Sha256);

    private sealed record GlobalRuleFileProbe(bool Exists, long Length, string Fingerprint);

    private sealed record DirectoryDiffSummary(
        ComparisonState State,
        int PersonalFileCount,
        int CompanyFileCount,
        long PersonalBytes,
        long CompanyBytes,
        int SameFileCount,
        int DifferentFileCount,
        int PersonalOnlyFileCount,
        int CompanyOnlyFileCount);

    private sealed record BundledPluginPackage(string SourceDirectory, string Version, string DisplayName);
}
