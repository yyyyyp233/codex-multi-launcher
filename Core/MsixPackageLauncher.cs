using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexChannelLauncher.Core;

internal static class MsixPackageLauncher
{
    private const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);

    public static ChatGptProcessSnapshot LaunchIsolated(
        CodexPackageInfo package, LauncherPaths scope, Action<LaunchProgress>? progress, CancellationToken cancellationToken)
    {
        scope.ValidateIsolationBoundaries();
        LauncherPaths.EnsureNoReparsePoints(scope.CompanyCodexHome);
        LauncherPaths.EnsureNoReparsePoints(scope.CompanyElectronData);
        Directory.CreateDirectory(scope.CompanyCodexHome);
        Directory.CreateDirectory(scope.CompanyElectronData);

        using var lease = MsixUserEnvironmentLease.Acquire(cancellationToken);
        AssertNoGlobalOverrides();
        var before = ProcessInventory.GetChatGptRoots();
        var beforeIds = before.Select(item => item.ProcessId).ToHashSet();
        foreach (var root in before)
        {
            if (!MsixProcessInspector.TryGetUserDataDirectory(root, out var directory))
            {
                throw new InvalidOperationException("有无法识别目录的 Codex 进程。请先关闭该实例，再启动隔离空间。 ");
            }

            if (MsixLaunchPolicy.PathsEqual(directory, scope.CompanyElectronData))
            {
                throw new InvalidOperationException("该隔离目录已有 Codex 进程占用，已拒绝重复启动。 ");
            }
        }

        var activity = new MsixActivitySnapshot(scope.CompanyCodexHome, scope.CompanyElectronData);
        progress?.Invoke(new LaunchProgress("msix-environment", 10,
            "正在临时切换用户启动环境；请勿同时从任务栏或终端另开 Codex"));
        lease.Apply(scope.CompanyCodexHome);
        Pause(1000, cancellationToken); // propagation grace, NOT the startup-completion test
        lease.AssertApplied();
        cancellationToken.ThrowIfCancellationRequested();
        var pid = Activate("--user-data-dir=" + MsixLaunchPolicy.QuoteArgument(scope.CompanyElectronData));
        if (beforeIds.Contains(pid))
        {
            throw new InvalidOperationException(
                "Windows 将激活转交给了已有实例，未创建独立进程。已拒绝把该 PID 登记到新空间；没有关闭已有实例。 ");
        }

        progress?.Invoke(new LaunchProgress("msix-verify", 50, "正在核对包身份、目标目录与本次初始化写入"));
        var expectedPackage = Path.GetFileName(package.InstallLocation.TrimEnd('\\', '/'));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < StartupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ProcessInventory.GetChatGptRoots().FirstOrDefault(item => item.ProcessId == pid);
            if (root is not null && MsixProcessInspector.TryGetUserDataDirectory(root, out var directory))
            {
                if (!MsixLaunchPolicy.PathsEqual(directory, scope.CompanyElectronData))
                {
                    throw new InvalidOperationException("已激活的进程没有采用目标 Electron 目录，未登记为成功启动。 ");
                }

                if (string.Equals(MsixProcessInspector.GetPackageFullName(pid), expectedPackage, StringComparison.OrdinalIgnoreCase) &&
                    activity.HasFreshActivity())
                {
                    lease.AssertApplied();
                    progress?.Invoke(new LaunchProgress("msix-restore", 90, "独立目录已有本次写入，正在恢复用户环境"));
                    return root; // using lease restores and broadcasts BEFORE this returns to the caller
                }
            }

            Pause(300, cancellationToken);
        }

        throw new TimeoutException(
            $"MSIX 已返回 PID {pid}，但未在限定时间内同时确认包身份、目标目录和新写入。" +
            "不会把旧数据库的存在当作成功。用户环境将恢复；若窗口仍在，请先关闭该窗口后重试。 ");
    }

    public static ChatGptProcessSnapshot LaunchPersonal(
        CodexPackageInfo package, LauncherPaths paths, CancellationToken cancellationToken)
    {
        using var lease = MsixUserEnvironmentLease.Acquire(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var pid = Activate(null); // original Store entry; no global settings are changed for the primary profile
        var expectedPackage = Path.GetFileName(package.InstallLocation.TrimEnd('\\', '/'));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = ProcessInventory.GetChatGptRoots();
            var ownership = ProcessInventory.ClassifyChatGptRoots(paths, roots);
            var owner = ownership.FirstOrDefault(item => item.Process.ProcessId == pid);
            if (owner is not null && !ProcessInventory.IsManaged(owner) &&
                string.Equals(MsixProcessInspector.GetPackageFullName(pid), expectedPackage, StringComparison.OrdinalIgnoreCase))
            {
                return owner.Process;
            }

            if (owner is not null && ProcessInventory.IsManaged(owner))
            {
                throw new InvalidOperationException("Windows 将个人入口交给了隔离/未知实例，已停止；没有重新绑定任何空间。 ");
            }

            Pause(300, cancellationToken);
        }

        throw new TimeoutException("个人 MSIX 入口没有形成可验证的 Codex 主进程。 ");
    }

    public static void SendDeepLink(
        CodexPackageInfo package, LauncherPaths scope, ProcessMarker existing, string deepLink, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(deepLink, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("只允许 Codex 内部链接。", nameof(deepLink));
        }

        using var lease = MsixUserEnvironmentLease.Acquire(cancellationToken);
        AssertNoGlobalOverrides();
        var root = ProcessInventory.GetChatGptRoots().FirstOrDefault(item =>
            item.ProcessId == existing.ProcessId && item.StartedAtUtc == existing.StartedAtUtc &&
            MsixLaunchPolicy.PathsEqual(item.ExecutablePath, existing.ExecutablePath));
        if (root is null || !MsixProcessInspector.TryGetUserDataDirectory(root, out var directory) ||
            !MsixLaunchPolicy.PathsEqual(directory, scope.CompanyElectronData) ||
            !string.Equals(MsixProcessInspector.GetPackageFullName(root.ProcessId),
                Path.GetFileName(package.InstallLocation.TrimEnd('\\', '/')), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("无法确认现有隔离进程，未发送设置链接。 ");
        }

        lease.Apply(scope.CompanyCodexHome);
        Pause(1000, cancellationToken);
        lease.AssertApplied();
        var pid = Activate("--user-data-dir=" + MsixLaunchPolicy.QuoteArgument(scope.CompanyElectronData) +
                           " " + MsixLaunchPolicy.QuoteArgument(deepLink));
        if (pid != existing.ProcessId || !ProcessInventory.IsAlive(existing))
        {
            throw new InvalidOperationException(
                "Windows 没有把设置链接交给已验证的目标 PID，未报告成功；请在目标 Codex 窗口内手动打开设置。 ");
        }
    }

    private static void AssertNoGlobalOverrides()
    {
        // The MSIX broker does not inherit ProcessStartInfo.Environment. Never briefly delete global secrets.
        // Instead fail closed when the old per-child environment scrub cannot be preserved.
        string[] names =
        [
            "CODEX_ELECTRON_USER_DATA_PATH", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_API_KEY",
            "OPENAI_BASE_URL", "OPENAI_API_BASE", "CHATGPT_BASE_URL", "CODEX_CLI_PATH", "CODEX_APP_SERVER_WS_URL"
        ];
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)))
            {
                throw new InvalidOperationException(
                    $"检测到用户级/系统级 {name}。MSIX 激活不能沿用旧版的逐子进程清理方式；" +
                    "请先检查该变量与隔离空间的关系。未输出其值，也未修改它。 ");
            }
        }
    }

    private static void Pause(int milliseconds, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(milliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static int Activate(string? arguments)
    {
        var instance = new ApplicationActivationManagerClass();
        try
        {
            var manager = (IApplicationActivationManager)instance;
            var hr = manager.ActivateApplication(AppUserModelId, arguments, 0, out var pid);
            if (hr < 0)
            {
                throw new COMException($"MSIX ActivateApplication 失败，HRESULT=0x{hr:X8}。", hr);
            }

            if (pid == 0 || pid > int.MaxValue)
            {
                throw new InvalidOperationException("MSIX 返回了无效进程 ID。 ");
            }

            return (int)pid;
        }
        finally
        {
            _ = Marshal.ReleaseComObject(instance);
        }
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManagerClass { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr items, out uint processId);
    }
}
