using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexChannelLauncher.Core;

public sealed class ProfileCoordinator
{
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
    private readonly SemaphoreSlim launchGate = new(1, 1);

    public ProfileCoordinator()
        : this(new LauncherPaths())
    {
    }

    public ProfileCoordinator(LauncherPaths paths)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        MsixUserEnvironmentLease.RecoverInterruptedLaunch();
        Paths.EnsureRuntimeDirectories();
        Paths.ValidateIsolationBoundaries();
        stateStore = new StateStore(Paths);
        log = new LauncherLog(Paths);
        snapshotService = new ProfileSnapshotService(Paths);
        profileManager = new CompanyProfileManager(Paths, snapshotService);
        ConfigurationCenter = new ConfigurationCenterService(Paths, profileManager, snapshotService, packageLocator);
        MergeWorkbench = new ProfileMergeService(Paths, profileManager, snapshotService);
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
        using var operationGate = LauncherOperationGate.Acquire(Paths);
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

        using var operationGate = LauncherOperationGate.Acquire(Paths);
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

        var roots = ProcessInventory.GetChatGptRoots();
        var ownership = ProcessInventory.ClassifyChatGptRoots(Paths, roots);
        // Polling is read-only. Do not overwrite a simultaneous launch/deletion state save.
        var state = NormalizeState(registrations, ownership, out _);

        var validProfileIds = registrations.Select(profile => profile.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolvedManagedProcesses = ownership.Where(item =>
                ProcessInventory.IsManaged(item) &&
                (string.IsNullOrWhiteSpace(item.ProfileId) ||
                 !validProfileIds.Contains(item.ProfileId)))
            .ToArray();
        if (unresolvedManagedProcesses.Length > 0)
        {
            problem = CombineProblems(
                problem,
                "检测到目录不可读或无法归属的 Codex 实例；请先关闭测试/未知实例。其运行期间禁止新增启动及配置、删除和合并操作。");
        }

        var managedProcessIds = state.ProfileRootProcesses.Values
            .Select(marker => marker.ProcessId)
            .ToHashSet();
        var personalRoots = ownership
                .Where(item =>
                    !ProcessInventory.IsManaged(item) &&
                    !managedProcessIds.Contains(item.Process.ProcessId))
                .ToArray();
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
            problem,
            unresolvedManagedProcesses.Length > 0);
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
        IDisposable? operationGate = null;
        try
        {
            operationGate = await LauncherOperationGate.AcquireAsync(Paths, cancellationToken);
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
                ? status.ManagedProfiles.Any(profile => profile.Running) ||
                  status.UnresolvedManagedProcessRunning
                : status.PersonalRunning || status.ManagedProfiles.Any(profile =>
                    profile.Running &&
                    !profile.Registration.ProfileId.Equals(registration!.ProfileId, StringComparison.OrdinalIgnoreCase)) ||
                  status.UnresolvedManagedProcessRunning;

            if (status.UnresolvedManagedProcessRunning)
            {
                throw new InvalidOperationException("存在身份不明的 Codex 进程；请先关闭该实例，不能用并行模式绕过此检查。 ");
            }

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

            ReportProgress(new LaunchProgress("package-check", 0, "正在检查当前用户注册的 Codex 包"));
            var package = packageLocator.LocateFromPackageRegistration();
            LauncherPaths? launchScope = null;
            if (registration is not null)
            {
                ReportProgress(new LaunchProgress("profile-check", 0, $"正在检查 {registration.DisplayName} 配置"));
                // Validation and startup both use an immutable, profile-specific path scope.
                launchScope = Paths.CreateProfileScope(registration.ProfileDirectoryName);
                var scopedManager = new CompanyProfileManager(launchScope, new ProfileSnapshotService(launchScope));
                await Task.Run(() => scopedManager.EnsureInitialized(registration.ProfileId), cancellationToken);
            }

            var startedAt = DateTime.UtcNow;
            var newRoot = await Task.Run(
                () => launchScope is not null
                    ? MsixPackageLauncher.LaunchIsolated(package, launchScope, ReportProgress, cancellationToken)
                    : MsixPackageLauncher.LaunchPersonal(package, Paths, cancellationToken),
                cancellationToken);
            log.Info(
                $"MSIX launch verified: channel={channel}, profile={registration?.ProfileId ?? "personal"}, " +
                $"pid={newRoot.ProcessId}, package={package.PackageVersion}; temporary user environment restored");
            ReportProgress(new LaunchProgress("complete", 100, "MSIX 激活检查完成，用户环境已恢复"));

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
                    ? $"{registration.DisplayName} 已通过 MSIX 启动，并确认目标界面目录与本次数据写入。"
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
            operationGate?.Dispose();
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
        if (!GetStatus().ManagedProfiles.Any(profile =>
                profile.Registration.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase) && profile.Running))
        {
            await LaunchAsync(ChannelKind.Company, true, profileId, cancellationToken);
        }

        await launchGate.WaitAsync(cancellationToken);
        try
        {
            using var operationGate = await LauncherOperationGate.AcquireAsync(Paths, cancellationToken);
            var root = ProcessInventory.ClassifyChatGptRoots(Paths).FirstOrDefault(item =>
                string.Equals(item.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))?.Process
                ?? throw new InvalidOperationException("未找到可验证的目标隔离进程。 ");
            var marker = new ProcessMarker(root.ProcessId, root.StartedAtUtc, root.ExecutablePath);
            var scope = Paths.CreateProfileScope(registration.ProfileDirectoryName);
            var package = packageLocator.LocateFromPackageRegistration();
            await Task.Run(() => MsixPackageLauncher.SendDeepLink(
                package, scope, marker, deepLink, cancellationToken), cancellationToken);
        }
        finally
        {
            launchGate.Release();
        }
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
        state.ProfileRootProcesses.TryGetValue(profileId, out var marker);
        var registeredProfileIds = profileManager.GetProfiles()
            .Select(profile => profile.ProfileId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ProcessInventory.IsProfileMutationBlocked(
            Paths,
            profileId,
            marker,
            registeredProfileIds);
    }

    private LauncherState NormalizeState(
        IReadOnlyList<ManagedProfileRegistration> registrations,
        IReadOnlyList<ChatGptProcessOwnership> ownership,
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
                !ProcessInventory.IsAlive(state.ProfileRootProcesses[registeredProfileId]) ||
                !ownership.Any(item =>
                    item.Process.ProcessId == state.ProfileRootProcesses[registeredProfileId].ProcessId &&
                    string.Equals(item.ProfileId, registeredProfileId, StringComparison.OrdinalIgnoreCase)))
            {
                state.ProfileRootProcesses.Remove(registeredProfileId);
                changed = true;
            }
        }

        foreach (var item in ownership.Where(item =>
                     ProcessInventory.IsManaged(item) &&
                     !string.IsNullOrWhiteSpace(item.ProfileId) &&
                     validProfileIds.Contains(item.ProfileId)))
        {
            var profileId = item.ProfileId!;
            var recovered = new ProcessMarker(
                item.Process.ProcessId,
                item.Process.StartedAtUtc,
                item.Process.ExecutablePath);
            if (!state.ProfileRootProcesses.TryGetValue(profileId, out var current) ||
                current.ProcessId != recovered.ProcessId ||
                current.StartedAtUtc != recovered.StartedAtUtc ||
                !current.ExecutablePath.Equals(
                    recovered.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                state.ProfileRootProcesses[profileId] = recovered;
                changed = true;
            }
        }

        return state;
    }

    private Task<LaunchOutcome> FocusExistingAsync(
        ChannelKind channel,
        ManagedProfileRegistration? registration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ProcessInventory.ClassifyChatGptRoots(Paths).FirstOrDefault(item =>
            channel == ChannelKind.Personal
                ? !ProcessInventory.IsManaged(item)
                : string.Equals(item.ProfileId, registration!.ProfileId, StringComparison.OrdinalIgnoreCase))?.Process;
        var marker = root is null ? null : new ProcessMarker(root.ProcessId, root.StartedAtUtc, root.ExecutablePath);
        var focused = ProcessInventory.TryFocus(marker);
        return Task.FromResult(new LaunchOutcome(
            false,
            focused,
            false,
            channel,
            focused ? "已聚焦目标 Codex 窗口。" : "未重新启动进程。请从目标 Codex 的托盘图标或任务栏恢复窗口。",
            root?.ProcessId ?? 0,
            registration?.ProfileId));
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
