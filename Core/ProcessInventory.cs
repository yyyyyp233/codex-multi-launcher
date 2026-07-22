using System.Diagnostics;
using System.Runtime.InteropServices;

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
