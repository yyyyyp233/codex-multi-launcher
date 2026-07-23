using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexChannelLauncher.Core;

public static class ProcessInventory
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<ChatGptProcessSnapshot> GetChatGptRoots()
    {
        var parents = ReadParentProcessMap();
        var all = new List<ChatGptProcessSnapshot>();

        foreach (var process in GetDesktopProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName ?? string.Empty;
                if (!IsDesktopExecutable(path))
                {
                    continue;
                }

                var parent = parents.GetValueOrDefault(process.Id);
                all.Add(new ChatGptProcessSnapshot(
                    process.Id,
                    parent,
                    process.StartTime.ToUniversalTime(),
                    path));
            }
            catch
            {
                // Ignore protected/transient children; root detection remains conservative.
            }
            finally
            {
                process.Dispose();
            }
        }

        var chatGptIds = all.Select(item => item.ProcessId).ToHashSet();
        return all
            .Where(item => !chatGptIds.Contains(item.ParentProcessId))
            .OrderBy(item => item.StartedAtUtc)
            .ToArray();
    }

    public static bool IsAlive(ProcessMarker? marker)
    {
        if (marker is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            var path = process.MainModule?.FileName ?? string.Empty;
            if (process.HasExited || !IsDesktopExecutable(path))
            {
                return false;
            }

            var delta = (process.StartTime.ToUniversalTime() - marker.StartedAtUtc).Duration();
            return delta < TimeSpan.FromSeconds(2);
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<ChatGptProcessOwnership> ClassifyChatGptRoots(
        LauncherPaths paths,
        IReadOnlyList<ChatGptProcessSnapshot>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        roots ??= GetChatGptRoots();
        return roots.Select(process =>
        {
            var isRuntimeCache = LauncherPaths.IsUnder(
                process.ExecutablePath,
                paths.RuntimeCacheRoot);
            return new ChatGptProcessOwnership(
                process,
                isRuntimeCache,
                isRuntimeCache ? TryReadRuntimeCacheProfileId(paths, process.ExecutablePath) : null);
        }).ToArray();
    }

    public static bool IsProfileMutationBlocked(
        LauncherPaths paths,
        string profileId,
        ProcessMarker? stateMarker = null,
        IReadOnlySet<string>? registeredProfileIds = null)
    {
        var ownership = ClassifyChatGptRoots(paths);
        if (stateMarker is not null &&
            ownership.Any(item => item.Process.ProcessId == stateMarker.ProcessId &&
                                  IsSameProcess(item.Process, stateMarker)))
        {
            return true;
        }

        return ownership.Any(item =>
            item.IsRuntimeCache &&
            (string.IsNullOrWhiteSpace(item.ProfileId) ||
             item.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase) ||
             registeredProfileIds is not null &&
             !registeredProfileIds.Contains(item.ProfileId)));
    }

    public static bool IsPersonalRunning(LauncherPaths paths) =>
        ClassifyChatGptRoots(paths).Any(item => !item.IsRuntimeCache);

    private static IEnumerable<Process> GetDesktopProcesses()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            yield return process;
        }

        foreach (var process in Process.GetProcessesByName("Codex"))
        {
            yield return process;
        }
    }

    private static bool IsDesktopExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var file = Path.GetFileName(path);
        if (file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(Path.GetDirectoryName(path))?.Equals("app", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsSameProcess(ChatGptProcessSnapshot process, ProcessMarker marker) =>
        process.ProcessId == marker.ProcessId &&
        (process.StartedAtUtc - marker.StartedAtUtc).Duration() < TimeSpan.FromSeconds(2);

    internal static string? TryReadRuntimeCacheProfileId(
        LauncherPaths paths,
        string executablePath)
    {
        try
        {
            var executable = Path.GetFullPath(executablePath);
            var appDirectory = Path.GetDirectoryName(executable);
            var cacheDirectory = appDirectory is null ? null : Path.GetDirectoryName(appDirectory);
            var versionsRoot = Path.Combine(paths.RuntimeCacheRoot, "versions");
            if (appDirectory is null ||
                cacheDirectory is null ||
                !Path.GetFileName(appDirectory).Equals("app", StringComparison.OrdinalIgnoreCase) ||
                !LauncherPaths.IsUnder(cacheDirectory, versionsRoot))
            {
                return null;
            }

            LauncherPaths.EnsureNoReparsePoints(cacheDirectory);
            var manifestPath = Path.Combine(cacheDirectory, "cache-manifest.json");
            LauncherPaths.EnsureNoReparsePoints(manifestPath);
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("ProfileId", out var profileProperty) ||
                profileProperty.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var profileId = profileProperty.GetString();
            return IsSafeProfileId(profileId) ? profileId : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) &&
        profileId.Length <= 64 &&
        profileId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool TryFocus(ProcessMarker? marker)
    {
        if (!IsAlive(marker))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(marker!.ProcessId);
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            ShowWindow(handle, 9);
            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<int, int> ReadParentProcessMap()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
