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

    public IReadOnlyList<ManagedProfileRegistration> GetProfiles() => profileManager.GetProfiles();

    public ProfileSetupStatus GetProfileSetupStatus(string? profileId = null) =>
        profileManager.GetSetupStatus(profileId);

    public CompanyProfileMetadata GetProfileMetadata(string profileId) =>
        profileManager.ReadMetadata(profileId);

    public CompanyProfileMetadata GetProfileMetadataForEditing(string profileId) =>
        profileManager.ReadMetadataForEditing(profileId);

    public ProfileCoordinator CreateProfileScope(string profileId)
    {
        var registration = ResolveRegistration(profileId);
        return new ProfileCoordinator(Paths.CreateProfileScope(registration.ProfileDirectoryName));
    }

    public CompanyProfileMetadata ConfigureWorkProfile(ProfileSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.ProfileId) && IsManagedProfileRunning(request.ProfileId))
        {
            throw new InvalidOperationException("请先退出该隔离空间的 Codex App，再编辑连接配置。");
        }

        var registration = profileManager.Configure(request);
        return profileManager.ReadMetadata(registration.ProfileId);
    }

    public ProfileDeletionResult DeleteWorkProfile(string profileId, bool deleteLocalContent)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("必须指定要删除的工作空间。", nameof(profileId));
        }

        using var crossProcessGate = new Semaphore(1, 1, CrossProcessGateName);
        var crossProcessGateHeld = crossProcessGate.WaitOne(TimeSpan.FromSeconds(45));
        if (!crossProcessGateHeld)
        {
            throw new TimeoutException("另一个多开器窗口正在执行实例操作，请稍后重试。");
        }

        try
        {
            var registration = ResolveRegistration(profileId);
            if (IsManagedProfileRunning(profileId))
            {
                throw new InvalidOperationException(
                    $"请先完全退出 {registration.DisplayName}，再删除工作空间。");
            }

            var result = profileManager.Delete(profileId, deleteLocalContent);
            try
            {
                var state = stateStore.Load();
                state.ProfileRootProcesses ??=
                    new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
                state.ProfileRootProcesses.Remove(profileId);
                if (string.Equals(
                        state.LastLaunchProfileId,
                        profileId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    state.LastLaunchProfileId = null;
                }

                stateStore.Save(state);
            }
            catch (Exception exception)
            {
                log.Error($"Deleted profile state cleanup failed: profile={profileId}", exception);
            }

            log.Info(
                $"Profile deleted: profile={profileId}, localContentDeleted={deleteLocalContent}, " +
                $"cleanupPending={result.CleanupPendingPath is not null}");
            return result;
        }
        finally
        {
            crossProcessGate.Release();
        }
    }

    public RuntimeStatus GetStatus()
    {
        CodexPackageInfo? package = null;
        string? problem = null;
        try
        {
            package = packageLocator.Locate();
        }
        catch (Exception exception)
        {
            problem = exception.Message;
        }

        IReadOnlyList<ManagedProfileRegistration> registrations;
        try
        {
            registrations = profileManager.GetProfiles();
        }
        catch (Exception exception)
        {
            registrations = [];
            problem = CombineProblems(problem, exception.Message);
        }

        var state = NormalizeState(registrations, out var stateChanged);
        if (stateChanged)
        {
            stateStore.Save(state);
        }

        var roots = ProcessInventory.GetChatGptRoots();
        var managedProcessIds = state.ProfileRootProcesses.Values
            .Select(marker => marker.ProcessId)
            .ToHashSet();
        var personalRoots = roots.Where(root => !managedProcessIds.Contains(root.ProcessId)).ToArray();
        var profileStatuses = new List<ManagedProfileRuntimeStatus>();
        var selectedDirectory = Paths.WorkProfileDirectoryName;
        try
        {
            foreach (var registration in registrations)
            {
                state.ProfileRootProcesses.TryGetValue(registration.ProfileId, out var marker);
                CompanyProfileMetadata? metadata = null;
                string? profileProblem = null;
                try
                {
                    metadata = profileManager.ReadMetadata(registration.ProfileId);
                }
                catch (Exception exception)
                {
                    profileProblem = exception.Message;
                    problem = CombineProblems(problem, $"{registration.DisplayName}: {exception.Message}");
                }

                profileStatuses.Add(new ManagedProfileRuntimeStatus(
                    registration,
                    marker is not null,
                    marker?.ProcessId ?? 0,
                    metadata,
                    profileProblem));
            }
        }
        finally
        {
            Paths.SelectWorkProfileDirectory(selectedDirectory);
        }

        return new RuntimeStatus(
            personalRoots.Length > 0,
            personalRoots.Length,
            package,
            profileStatuses,
            problem);
    }

    public Task<LaunchOutcome> LaunchAsync(
        ChannelKind channel,
        bool allowParallel,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(channel, allowParallel, null, cancellationToken);

    public async Task<LaunchOutcome> LaunchAsync(
        ChannelKind channel,
        bool allowParallel,
        string? profileId,
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
            ManagedProfileRegistration? registration = null;
            if (channel == ChannelKind.Company)
            {
                registration = ResolveRegistration(profileId);
                profileManager.SelectProfile(registration.ProfileId);
            }

            var status = GetStatus();
            var targetProfile = registration is null
                ? null
                : status.ManagedProfiles.FirstOrDefault(profile => profile.Registration.ProfileId.Equals(
                    registration.ProfileId,
                    StringComparison.OrdinalIgnoreCase));
            var targetRunning = channel == ChannelKind.Personal
                ? status.PersonalRunning
                : targetProfile?.Running == true;
            var otherRunning = channel == ChannelKind.Personal
                ? status.ManagedProfiles.Any(profile => profile.Running)
                : status.PersonalRunning || status.ManagedProfiles.Any(profile =>
                    profile.Running &&
                    !profile.Registration.ProfileId.Equals(registration!.ProfileId, StringComparison.OrdinalIgnoreCase));

            if (otherRunning && !allowParallel)
            {
                return new LaunchOutcome(
                    false,
                    false,
                    true,
                    channel,
                    "另一个实例正在运行。安全模式不会关闭或复用它；请先退出其他实例，或明确开启并行模式。",
                    0,
                    registration?.ProfileId);
            }

            if (targetRunning)
            {
                return await FocusExistingAsync(channel, registration, cancellationToken);
            }

            ReportProgress(new LaunchProgress("package-check", 0, "正在检查主 App 最新版本"));
            var package = packageLocator.Locate(forceRefresh: true);
            if (channel == ChannelKind.Company && !package.SupportsIsolatedElectronData)
            {
                throw new NotSupportedException(
                    "当前 Codex App 不再暴露独立 Electron 用户目录入口。为避免污染个人实例，已拒绝启动隔离空间实例。");
            }

            string? managedExecutable = null;
            if (registration is not null)
            {
                ReportProgress(new LaunchProgress("profile-check", 0, $"正在检查 {registration.DisplayName} 配置"));
                await Task.Run(() => profileManager.EnsureInitialized(registration.ProfileId), cancellationToken);
                managedExecutable = await Task.Run(
                    () => runtimeCache.Prepare(
                        package,
                        registration.ProfileId,
                        registration.AccentColor,
                        ReportProgress,
                        cancellationToken),
                    cancellationToken);
            }

            var beforeRoots = ProcessInventory.GetChatGptRoots();
            var beforeIds = beforeRoots.Select(item => item.ProcessId).ToHashSet();
            var startedAt = DateTime.UtcNow;
            ReportProgress(new LaunchProgress("process-start", 100, "正在创建独立 Codex 进程"));
            if (registration is not null)
            {
                profileManager.SelectProfile(registration.ProfileId);
            }

            using var process = Process.Start(channel == ChannelKind.Company
                                    ? CreateCompanyStartInfo(managedExecutable!)
                                    : CreatePersonalActivationStartInfo())
                                ?? throw new InvalidOperationException("Windows 未创建 Codex App 进程。");

            log.Info(
                $"Launch requested: channel={channel}, profile={registration?.ProfileId ?? "personal"}, " +
                $"pid={process.Id}, parallel={allowParallel}, package={package.PackageVersion}");

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

            var state = stateStore.Load();
            state.ProfileRootProcesses ??= new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
            if (registration is not null)
            {
                state.ProfileRootProcesses[registration.ProfileId] = new ProcessMarker(
                    newRoot.ProcessId,
                    newRoot.StartedAtUtc,
                    newRoot.ExecutablePath);
                state.LastLaunchProfileId = registration.ProfileId;
            }
            else
            {
                state.LastLaunchProfileId = null;
            }

            state.LastLaunchAtUtc = startedAt;
            state.LastLaunchChannel = channel;
            state.LastPackageVersion = package.PackageVersion;
            stateStore.Save(state);

            return new LaunchOutcome(
                true,
                false,
                false,
                channel,
                registration is not null
                    ? $"{registration.DisplayName} 已在独立 CODEX_HOME 与独立界面数据目录中启动。"
                    : "个人实例已通过原始 Store 入口启动。",
                newRoot.ProcessId,
                registration?.ProfileId);
        }
        catch (Exception exception)
        {
            log.Error($"Launch failed: channel={channel}, profile={profileId ?? "default"}", exception);
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
        var personalFiles = new[]
        {
            Paths.PersonalConfig,
            Paths.PersonalAuth,
            Path.Combine(Paths.PersonalCodexHome, "AGENTS.md"),
            Path.Combine(Paths.PersonalCodexHome, "AGENTS.override.md")
        };
        var personalBefore = personalFiles.ToDictionary(path => path, SafeHash, StringComparer.OrdinalIgnoreCase);

        AddCheck(report, "profile-path-isolation", () =>
        {
            Paths.ValidateIsolationBoundaries();
            return $"个人 Codex Home 与隔离空间目录互不重叠；运行数据位于 {Paths.RuntimeRoot}";
        });
        AddCheck(report, "codex-package", () =>
        {
            var package = packageLocator.Locate(forceRefresh: true);
            return $"OpenAI.Codex {package.PackageVersion}";
        });
        AddCheck(report, "profile-registry", () =>
        {
            var profiles = profileManager.GetProfiles();
            foreach (var profile in profiles)
            {
                _ = profileManager.ReadMetadata(profile.ProfileId);
            }

            return $"已验证 {profiles.Count} 个隔离空间的注册、路径与认证边界。";
        });
        AddCheck(report, "merge-workbench-engine", ProfileMergeService.RunEngineSelfTest);
        AddCheck(report, "global-rules-snapshot-engine", ProfileSnapshotService.RunGlobalRulesSelfTest);
        AddCheck(report, "personal-home-untouched", () =>
        {
            var unchanged = personalFiles.All(path => HashesEqual(personalBefore[path], SafeHash(path)));
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
            var registration = ResolveRegistration(null);
            profileManager.SelectProfile(registration.ProfileId);
            var activityFloor = DateTime.UtcNow.AddSeconds(-1);
            report.Launch = await LaunchAsync(ChannelKind.Company, true, registration.ProfileId);
            await Task.Delay(TimeSpan.FromSeconds(7));
            report.RuntimeStatus = GetStatus();
            report.CompanyHomeReceivedActivity = HasActivitySince(Paths.CompanyCodexHome, activityFloor);
            report.CompanyElectronDataReceivedActivity = HasActivitySince(Paths.CompanyElectronData, activityFloor);
            report.Passed = report.RuntimeStatus.ManagedProfiles.Any(profile =>
                                profile.Registration.ProfileId.Equals(
                                    registration.ProfileId,
                                    StringComparison.OrdinalIgnoreCase) &&
                                profile.Running) &&
                            report.CompanyHomeReceivedActivity &&
                            report.CompanyElectronDataReceivedActivity;
        }
        catch (Exception exception)
        {
            report.Error = exception.Message;
            report.Passed = false;
            log.Error("Managed profile smoke launch failed", exception);
        }

        return report;
    }

    public Task OpenCompanyDeepLinkAsync(
        string deepLink,
        CancellationToken cancellationToken = default) =>
        OpenCompanyDeepLinkAsync(ResolveRegistration(null).ProfileId, deepLink, cancellationToken);

    public async Task OpenCompanyDeepLinkAsync(
        string profileId,
        string deepLink,
        CancellationToken cancellationToken = default)
    {
        if (!deepLink.StartsWith("codex://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只允许打开 Codex App 内部设置链接。", nameof(deepLink));
        }

        var registration = ResolveRegistration(profileId);
        profileManager.SelectProfile(registration.ProfileId);
        var status = GetStatus();
        var running = status.ManagedProfiles.FirstOrDefault(profile =>
            profile.Registration.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (running?.Running != true)
        {
            await LaunchAsync(ChannelKind.Company, true, profileId, cancellationToken);
            await Task.Delay(1200, cancellationToken);
        }

        var state = stateStore.Load();
        state.ProfileRootProcesses ??= new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
        state.ProfileRootProcesses.TryGetValue(profileId, out var marker);
        var executable = marker?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ||
            !LauncherPaths.IsUnder(executable, Paths.RuntimeCacheRoot))
        {
            var package = packageLocator.Locate(forceRefresh: true);
            executable = await Task.Run(
                () => runtimeCache.Prepare(
                    package,
                    registration.ProfileId,
                    registration.AccentColor,
                    ReportProgress,
                    cancellationToken),
                cancellationToken);
        }

        profileManager.SelectProfile(registration.ProfileId);
        using var activation = Process.Start(CreateCompanyStartInfo(executable, deepLink))
                               ?? throw new InvalidOperationException("无法打开隔离空间 Codex 设置页。");
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

    private ManagedProfileRegistration ResolveRegistration(string? profileId)
    {
        var profiles = profileManager.GetProfiles();
        var registration = string.IsNullOrWhiteSpace(profileId)
            ? profiles.FirstOrDefault(profile => profile.ProfileDirectoryName.Equals(
                  Paths.WorkProfileDirectoryName,
                  StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault()
            : profiles.FirstOrDefault(profile =>
                profile.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        return registration ?? throw new InvalidOperationException("尚未创建可启动的隔离空间。");
    }

    private bool IsManagedProfileRunning(string profileId)
    {
        var state = stateStore.Load();
        state.ProfileRootProcesses ??= new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
        return state.ProfileRootProcesses.TryGetValue(profileId, out var marker) &&
               ProcessInventory.IsAlive(marker);
    }

    private LauncherState NormalizeState(
        IReadOnlyList<ManagedProfileRegistration> registrations,
        out bool changed)
    {
        var state = stateStore.Load();
        changed = false;
        if (state.ProfileRootProcesses is null)
        {
            state.ProfileRootProcesses = new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
            changed = true;
        }

        if (state.ProfileRootProcesses.Count == 0 &&
            registrations.FirstOrDefault() is { } legacyOwner &&
            stateStore.TryLoadLegacyCompanyRootProcess() is { } legacyProcess)
        {
            state.ProfileRootProcesses[legacyOwner.ProfileId] = legacyProcess;
            changed = true;
        }

        var validProfileIds = registrations.Select(profile => profile.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var registeredProfileId in state.ProfileRootProcesses.Keys.ToArray())
        {
            if (!validProfileIds.Contains(registeredProfileId) ||
                !ProcessInventory.IsAlive(state.ProfileRootProcesses[registeredProfileId]))
            {
                state.ProfileRootProcesses.Remove(registeredProfileId);
                changed = true;
            }
        }

        return state;
    }

    private async Task<LaunchOutcome> FocusExistingAsync(
        ChannelKind channel,
        ManagedProfileRegistration? registration,
        CancellationToken cancellationToken)
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

        var state = stateStore.Load();
        state.ProfileRootProcesses ??= new Dictionary<string, ProcessMarker>(StringComparer.OrdinalIgnoreCase);
        state.ProfileRootProcesses.TryGetValue(registration!.ProfileId, out var marker);
        var focused = ProcessInventory.TryFocus(marker);
        if (!focused && marker is not null && LauncherPaths.IsUnder(marker.ExecutablePath, Paths.RuntimeCacheRoot))
        {
            profileManager.SelectProfile(registration.ProfileId);
            using var activation = Process.Start(CreateCompanyStartInfo(marker.ExecutablePath));
            await Task.Delay(900, cancellationToken);
            focused = ProcessInventory.TryFocus(marker) || activation is not null;
        }

        return new LaunchOutcome(
            false,
            focused,
            false,
            channel,
            focused ? $"已请求显示并聚焦 {registration.DisplayName}。" : $"{registration.DisplayName} 已在后台运行，请从任务栏切换窗口。",
            marker?.ProcessId ?? 0,
            registration.ProfileId);
    }

    private static ProcessStartInfo CreatePersonalActivationStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(@"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App");
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

    private static string CombineProblems(string? current, string next) =>
        string.IsNullOrWhiteSpace(current) ? next : current + " · " + next;

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
