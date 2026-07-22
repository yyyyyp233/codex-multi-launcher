using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexChannelLauncher.Core;

public sealed class ProfileCoordinator
{
    private const string CrossProcessGateName = @"Local\CodexChannelLauncher.ProfileOperation";

    private static readonly string[] ScrubbedEnvironmentVariables =
    [
        "CODEX_HOME",
        "CODEX_SQLITE_HOME",
        "CODEX_ELECTRON_USER_DATA_PATH",
        "CODEX_API_KEY",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_API_KEY",
        "OPENAI_BASE_URL",
        "OPENAI_API_BASE",
        "CHATGPT_BASE_URL"
    ];

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly CodexPackageLocator packageLocator = new();
    private readonly StateStore stateStore;
    private readonly CompanyProfileManager profileManager;
    private readonly ProfileSnapshotService snapshotService;
    private readonly LauncherLog log;
    private readonly CodexRuntimeCache runtimeCache;
    private readonly SemaphoreSlim launchGate = new(1, 1);

    public ProfileCoordinator()
        : this(new LauncherPaths())
    {
    }

    public ProfileCoordinator(LauncherPaths paths)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Paths.EnsureRuntimeDirectories();
        Paths.ValidateIsolationBoundaries();
        stateStore = new StateStore(Paths);
        log = new LauncherLog(Paths);
        snapshotService = new ProfileSnapshotService(Paths);
        profileManager = new CompanyProfileManager(Paths, snapshotService);
        ConfigurationCenter = new ConfigurationCenterService(Paths, profileManager, snapshotService, packageLocator);
        MergeWorkbench = new ProfileMergeService(Paths, profileManager, snapshotService);
        runtimeCache = new CodexRuntimeCache(Paths, log);
    }

    public LauncherPaths Paths { get; }

    public ConfigurationCenterService ConfigurationCenter { get; }

    public ProfileMergeService MergeWorkbench { get; }

    public event EventHandler<LaunchProgress>? ProgressChanged;

    public ProfileSetupStatus GetProfileSetupStatus() => profileManager.GetSetupStatus();

    public CompanyProfileMetadata ConfigureWorkProfile(ProfileSetupRequest request)
    {
        if (ConfigurationCenter.IsCompanyRunning())
        {
            throw new InvalidOperationException("请先退出工作空间 Codex App，再创建、导入或编辑连接配置。");
        }

        profileManager.Configure(request);
        return profileManager.ReadMetadata();
    }

    public RuntimeStatus GetStatus()
    {
        CodexPackageInfo? package = null;
        CompanyProfileMetadata? companyProfile = null;
        ProfileSetupStatus profileSetup;
        string? problem = null;

        try
        {
            package = packageLocator.Locate();
        }
        catch (Exception exception)
        {
            problem = exception.Message;
        }

        try
        {
            profileSetup = profileManager.GetSetupStatus();
            if (profileSetup.State == WorkProfileSetupState.Configured)
            {
                companyProfile = profileManager.ReadMetadata();
            }
            else if (profileSetup.State == WorkProfileSetupState.Invalid)
            {
                problem = profileSetup.Problem;
            }
        }
        catch (Exception exception)
        {
            profileSetup = new ProfileSetupStatus(
                WorkProfileSetupState.Invalid,
                null,
                [],
                exception.Message);
            problem = problem is null ? exception.Message : problem + " · " + exception.Message;
        }

        var state = stateStore.Load();
        var companyRunning = ProcessInventory.IsAlive(state.CompanyRootProcess);
        if (!companyRunning && state.CompanyRootProcess is not null)
        {
            state.CompanyRootProcess = null;
            stateStore.Save(state);
        }

        var roots = ProcessInventory.GetChatGptRoots();
        var companyProcessId = companyRunning ? state.CompanyRootProcess!.ProcessId : 0;
        var personalRoots = roots.Where(root => root.ProcessId != companyProcessId).ToArray();

        return new RuntimeStatus(
            personalRoots.Length > 0,
            companyRunning,
            personalRoots.Length,
            companyProcessId,
            package,
            companyProfile,
            profileSetup,
            problem);
    }

    public async Task<LaunchOutcome> LaunchAsync(
        ChannelKind channel,
        bool allowParallel,
        CancellationToken cancellationToken = default)
    {
        await launchGate.WaitAsync(cancellationToken);
        Semaphore? crossProcessGate = null;
        var crossProcessGateHeld = false;
        try
        {
            crossProcessGate = new Semaphore(1, 1, CrossProcessGateName);
            crossProcessGateHeld = await Task.Run(
                () => crossProcessGate.WaitOne(TimeSpan.FromSeconds(45)),
                CancellationToken.None);
            if (!crossProcessGateHeld)
            {
                throw new TimeoutException("另一个多开器窗口正在执行实例操作，请稍后重试。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var status = GetStatus();
            var targetRunning = channel == ChannelKind.Personal
                ? status.PersonalRunning
                : status.CompanyRunning;
            var otherRunning = channel == ChannelKind.Personal
                ? status.CompanyRunning
                : status.PersonalRunning;

            if (otherRunning && !allowParallel)
            {
                return new LaunchOutcome(
                    false,
                    false,
                    true,
                    channel,
                    "另一实例正在运行。安全模式不会关闭或复用它；请先退出另一实例，或明确开启并行模式。",
                    0);
            }

            if (targetRunning)
            {
                if (channel == ChannelKind.Personal)
                {
                    using var activation = Process.Start(CreatePersonalActivationStartInfo());
                    await Task.Delay(700, cancellationToken);
                    return new LaunchOutcome(
                        false,
                        true,
                        false,
                        channel,
                        "已通过原始 Store 入口聚焦个人 Codex。",
                        activation?.Id ?? 0);
                }

                var existingState = stateStore.Load();
                var focused = ProcessInventory.TryFocus(existingState.CompanyRootProcess);
                if (!focused &&
                    existingState.CompanyRootProcess is { } companyMarker &&
                    LauncherPaths.IsUnder(companyMarker.ExecutablePath, Paths.RuntimeCacheRoot))
                {
                    using var activation = Process.Start(CreateCompanyStartInfo(companyMarker.ExecutablePath));
                    await Task.Delay(900, cancellationToken);
                    focused = ProcessInventory.TryFocus(companyMarker) || activation is not null;
                }

                return new LaunchOutcome(
                    false,
                    focused,
                    false,
                    channel,
                    focused ? "已请求正在运行的工作空间 Codex 显示并聚焦窗口。" : "工作空间 Codex 已在后台运行，请从任务栏切换窗口。",
                    existingState.CompanyRootProcess?.ProcessId ?? 0);
            }

            ReportProgress(new LaunchProgress("package-check", 0, "正在检查主 App 最新版本"));
            var package = packageLocator.Locate(forceRefresh: true);

            if (channel == ChannelKind.Company && !package.SupportsIsolatedElectronData)
            {
                throw new NotSupportedException(
                    "当前 Codex App 不再暴露独立 Electron 用户目录入口。为避免污染个人实例，已拒绝启动工作空间实例。");
            }

            string? companyExecutable = null;
            if (channel == ChannelKind.Company)
            {
                ReportProgress(new LaunchProgress("profile-check", 0, "正在检查工作空间配置"));
                await Task.Run(profileManager.EnsureInitialized, cancellationToken);
                companyExecutable = await Task.Run(
                    () => runtimeCache.Prepare(package, ReportProgress, cancellationToken),
                    cancellationToken);
            }

            var beforeRoots = ProcessInventory.GetChatGptRoots();
            var beforeIds = beforeRoots.Select(item => item.ProcessId).ToHashSet();
            var startedAt = DateTime.UtcNow;
            ReportProgress(new LaunchProgress("process-start", 100, "正在创建独立 Codex 进程"));
            using var process = Process.Start(channel == ChannelKind.Company
                                    ? CreateCompanyStartInfo(companyExecutable!)
                                    : CreatePersonalActivationStartInfo())
                                ?? throw new InvalidOperationException("Windows 未创建 Codex App 进程。");

            log.Info($"Launch requested: channel={channel}, pid={process.Id}, parallel={allowParallel}, package={package.PackageVersion}");

            await Task.Delay(2600, cancellationToken);
            var afterRoots = ProcessInventory.GetChatGptRoots();
            var newRoot = afterRoots
                .Where(root => !beforeIds.Contains(root.ProcessId))
                .OrderBy(root => root.StartedAtUtc)
                .FirstOrDefault();

            if (newRoot is null && !process.HasExited)
            {
                newRoot = afterRoots.FirstOrDefault(root => root.ProcessId == process.Id);
            }

            if (newRoot is null)
            {
                var exitDetail = process.HasExited ? $"，退出码 {process.ExitCode}" : string.Empty;
                throw new InvalidOperationException($"Codex App 未形成新的主进程{exitDetail}。");
            }

            if (channel == ChannelKind.Company)
            {
                var state = stateStore.Load();
                state.CompanyRootProcess = new ProcessMarker(
                    newRoot.ProcessId,
                    newRoot.StartedAtUtc,
                    newRoot.ExecutablePath);
                state.LastLaunchAtUtc = startedAt;
                state.LastLaunchChannel = channel;
                state.LastPackageVersion = package.PackageVersion;
                stateStore.Save(state);
            }
            else
            {
                var state = stateStore.Load();
                state.LastLaunchAtUtc = startedAt;
                state.LastLaunchChannel = channel;
                state.LastPackageVersion = package.PackageVersion;
                stateStore.Save(state);
            }

            return new LaunchOutcome(
                true,
                false,
                false,
                channel,
                channel == ChannelKind.Company
                    ? "工作空间实例已在独立 CODEX_HOME 与独立界面数据目录中启动。"
                    : "个人实例已通过原始 Store 入口启动。",
                newRoot.ProcessId);
        }
        catch (Exception exception)
        {
            log.Error($"Launch failed: channel={channel}", exception);
            throw;
        }
        finally
        {
            if (crossProcessGateHeld)
            {
                crossProcessGate!.Release();
            }

            crossProcessGate?.Dispose();
            launchGate.Release();
        }
    }

    public SelfTestReport RunSelfTest()
    {
        var report = new SelfTestReport();
        var personalConfig = Paths.PersonalConfig;
        var personalAuth = Paths.PersonalAuth;
        var personalAgents = Path.Combine(Paths.PersonalCodexHome, "AGENTS.md");
        var personalAgentsOverride = Path.Combine(Paths.PersonalCodexHome, "AGENTS.override.md");
        var personalConfigBefore = SafeHash(personalConfig);
        var personalAuthBefore = SafeHash(personalAuth);
        var personalAgentsBefore = SafeHash(personalAgents);
        var personalAgentsOverrideBefore = SafeHash(personalAgentsOverride);

        AddCheck(report, "profile-path-isolation", () =>
        {
            Paths.ValidateIsolationBoundaries();
            return $"个人 Codex Home 与工作空间目录互不重叠；运行数据位于 {Paths.RuntimeRoot}";
        });

        AddCheck(report, "codex-package", () =>
        {
            var package = packageLocator.Locate(forceRefresh: true);
            return $"OpenAI.Codex {package.PackageVersion}";
        });

        AddCheck(report, "codex-package-registration", () =>
        {
            var package = packageLocator.LocateFromPackageRegistration();
            return $"不依赖运行中进程，可由当前用户 AppX 注册信息定位 OpenAI.Codex {package.PackageVersion}。";
        });

        AddCheck(report, "isolated-electron-hook", () =>
        {
            var package = packageLocator.Locate();
            return package.SupportsIsolatedElectronData
                ? "当前安装包包含独立 Electron 用户目录入口。"
                : throw new NotSupportedException("当前安装包缺少独立 Electron 用户目录入口。");
        });

        AddCheck(report, "work-profile-tray-branding", () =>
        {
            var package = packageLocator.Locate();
            var sourceApp = Path.GetDirectoryName(package.ExecutablePath)
                            ?? throw new InvalidOperationException("无法解析当前 Store App 目录。");
            return CompanyTrayIconBranding.VerifyCurrentPackageIcons(sourceApp);
        });

        AddCheck(report, "work-profile-auth", () =>
            CompanyProfileManager.IsAuthConfigured(Paths.CompanyAuth)
                ? "工作空间认证已配置，值未读取到报告。"
                : throw new InvalidOperationException("工作空间认证缺失或仍是占位符。"));

        AddCheck(report, "work-profile-authority", () =>
        {
            profileManager.EnsureInitialized();
            return profileManager.IsInitialized &&
                   File.Exists(Paths.CompanyConfig) &&
                   File.Exists(Paths.CompanyAuth)
                ? "工作空间独立 CODEX_HOME 已成为唯一配置权威。"
                : throw new IOException("工作空间配置未完成。");
        });

        AddCheck(report, "merge-workbench-engine", ProfileMergeService.RunEngineSelfTest);

        AddCheck(report, "global-rules-snapshot-engine", ProfileSnapshotService.RunGlobalRulesSelfTest);

        AddCheck(report, "global-rules-whitelist", () =>
        {
            var rules = ConfigurationCenter.GetGlobalRules();
            return rules.Count == ProfileContentPolicy.GlobalRuleFileNames.Count &&
                   rules.All(rule => ProfileContentPolicy.IsGlobalRulePath(rule.FileName))
                ? $"仅管理 {string.Join("、", rules.Select(rule => rule.FileName))}。"
                : throw new InvalidDataException("全局规则发现超出固定白名单。");
        });

        AddCheck(report, "personal-home-untouched", () =>
        {
            var unchanged = HashesEqual(personalConfigBefore, SafeHash(personalConfig)) &&
                            HashesEqual(personalAuthBefore, SafeHash(personalAuth)) &&
                            HashesEqual(personalAgentsBefore, SafeHash(personalAgents)) &&
                            HashesEqual(personalAgentsOverrideBefore, SafeHash(personalAgentsOverride));
            return unchanged
                ? "个人 config.toml、auth.json 与全局规则在自检前后保持不变。"
                : throw new IOException("检测到个人配置、认证或全局规则发生变化。");
        });

        report.RuntimeStatus = GetStatus();
        report.Passed = report.Checks.All(check => check.Passed);
        log.Info($"Self-test completed: passed={report.Passed}");
        return report;
    }

    public async Task<SmokeLaunchReport> RunCompanySmokeLaunchAsync()
    {
        var report = new SmokeLaunchReport();
        try
        {
            var activityFloor = DateTime.UtcNow.AddSeconds(-1);
            report.Launch = await LaunchAsync(ChannelKind.Company, true);
            await Task.Delay(TimeSpan.FromSeconds(7));
            report.RuntimeStatus = GetStatus();
            report.CompanyHomeReceivedActivity = HasActivitySince(Paths.CompanyCodexHome, activityFloor);
            report.CompanyElectronDataReceivedActivity = HasActivitySince(Paths.CompanyElectronData, activityFloor);
            report.Passed = report.RuntimeStatus.CompanyRunning &&
                            report.CompanyHomeReceivedActivity &&
                            report.CompanyElectronDataReceivedActivity;
        }
        catch (Exception exception)
        {
            report.Error = exception.Message;
            report.Passed = false;
            log.Error("Work profile smoke launch failed", exception);
        }

        return report;
    }

    public async Task OpenCompanyDeepLinkAsync(
        string deepLink,
        CancellationToken cancellationToken = default)
    {
        if (!deepLink.StartsWith("codex://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只允许打开 Codex App 内部设置链接。", nameof(deepLink));
        }

        var status = GetStatus();
        if (!status.CompanyRunning)
        {
            await LaunchAsync(ChannelKind.Company, true, cancellationToken);
            await Task.Delay(1200, cancellationToken);
        }

        var state = stateStore.Load();
        var executable = state.CompanyRootProcess?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !LauncherPaths.IsUnder(executable, Paths.RuntimeCacheRoot))
        {
            var package = packageLocator.Locate(forceRefresh: true);
            executable = await Task.Run(
                () => runtimeCache.Prepare(package, ReportProgress, cancellationToken),
                cancellationToken);
        }

        using var activation = Process.Start(CreateCompanyStartInfo(executable, deepLink))
                               ?? throw new InvalidOperationException("无法打开工作空间 Codex 设置页。");
        await Task.Delay(900, cancellationToken);
    }

    public static void WriteReport<T>(string destination, T report)
    {
        var fullPath = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, ReportJsonOptions));
        File.Move(temporary, fullPath, true);
    }

    private static ProcessStartInfo CreatePersonalActivationStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("shell:AppsFolder\\OpenAI.Codex_2p2nqsd0c76g0!App");
        return startInfo;
    }

    private ProcessStartInfo CreateCompanyStartInfo(string executablePath, string? deepLink = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--user-data-dir={Paths.CompanyElectronData}");
        if (!string.IsNullOrWhiteSpace(deepLink))
        {
            startInfo.ArgumentList.Add(deepLink);
        }

        foreach (var variable in ScrubbedEnvironmentVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment["CODEX_HOME"] = Paths.CompanyCodexHome;
        startInfo.Environment["CODEX_SQLITE_HOME"] = Paths.CompanyCodexHome;
        startInfo.Environment["CODEX_ELECTRON_USER_DATA_PATH"] = Paths.CompanyElectronData;

        return startInfo;
    }

    private void ReportProgress(LaunchProgress progress) => ProgressChanged?.Invoke(this, progress);

    private static bool HasActivitySince(string directory, DateTime floorUtc)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Any(path => File.GetLastWriteTimeUtc(path) >= floorUtc);
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? SafeHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static bool HashesEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void AddCheck(SelfTestReport report, string name, Func<string> action)
    {
        try
        {
            report.Checks.Add(new SelfTestCheck(name, true, action()));
        }
        catch (Exception exception)
        {
            report.Checks.Add(new SelfTestCheck(name, false, exception.Message));
        }
    }
}
