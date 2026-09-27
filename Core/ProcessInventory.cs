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

                all.Add(new ChatGptProcessSnapshot(process.Id, parents.GetValueOrDefault(process.Id),
                    process.StartTime.ToUniversalTime(), path));
            }
            catch
            {
                // Do not turn an unreadable desktop process into "nothing is running".
                // Unknown roots conservatively block mutation and managed launches.
                all.Add(new ChatGptProcessSnapshot(process.Id, parents.GetValueOrDefault(process.Id),
                    DateTime.MinValue, string.Empty));
            }
            finally
            {
                process.Dispose();
            }
        }

        var ids = all.Select(item => item.ProcessId).ToHashSet();
        return all.Where(item => !ids.Contains(item.ParentProcessId)).OrderBy(item => item.StartedAtUtc).ToArray();
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
            return !process.HasExited && IsDesktopExecutable(path) &&
                   process.StartTime.ToUniversalTime() == marker.StartedAtUtc &&
                   MsixLaunchPolicy.PathsEqual(path, marker.ExecutablePath);
        }
        catch
        {
            return false;
        }
    }

    // IsRuntimeCache remains a physical-path flag for backwards compatibility.
    // A non-null ProfileId also denotes a managed (or unresolved) MSIX/direct instance.
    // Empty ProfileId is the explicit unresolved sentinel, never a valid registered profile ID.
    public static bool IsManaged(ChatGptProcessOwnership item) => item.IsRuntimeCache || item.ProfileId is not null;

    public static IReadOnlyList<ChatGptProcessOwnership> ClassifyChatGptRoots(
        LauncherPaths paths, IReadOnlyList<ChatGptProcessSnapshot>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        roots ??= GetChatGptRoots();
        return roots.Select(process =>
        {
            if (string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                return new ChatGptProcessOwnership(process, false, string.Empty);
            }

            var isRuntimeCache = LauncherPaths.IsUnder(process.ExecutablePath, paths.RuntimeCacheRoot);
            if (isRuntimeCache)
            {
                return new ChatGptProcessOwnership(process, true,
                    TryReadRuntimeCacheProfileId(paths, process.ExecutablePath));
            }

            if (!MsixProcessInspector.TryGetUserDataDirectory(process, out var directory))
            {
                return new ChatGptProcessOwnership(process, false, string.Empty);
            }

            if (directory is null || MsixLaunchPolicy.PathsEqual(directory, paths.PersonalElectronData))
            {
                return new ChatGptProcessOwnership(process, false, null);
            }

            var packageDefault = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "OpenAI.Codex_2p2nqsd0c76g0", "LocalCache", "Roaming", "Codex", "web", "Codex");
            if (MsixLaunchPolicy.PathsEqual(directory, packageDefault))
            {
                return new ChatGptProcessOwnership(process, false, null);
            }

            return new ChatGptProcessOwnership(process, false,
                TryMatchRegisteredElectronDirectory(paths, directory) ?? string.Empty);
        }).ToArray();
    }

    internal static string? TryMatchRegisteredElectronDirectory(LauncherPaths paths, string directory)
    {
        try
        {
            if (!LauncherPaths.IsUnder(directory, paths.ProfilesRoot))
            {
                return null;
            }

            LauncherPaths.EnsureNoReparsePoints(directory);
            LauncherPaths.EnsureNoReparsePoints(paths.ProfilesRegistryFile);
            if (!File.Exists(paths.ProfilesRegistryFile) || new FileInfo(paths.ProfilesRegistryFile).Length > 1024 * 1024)
            {
                return null;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var registry = JsonSerializer.Deserialize<ManagedProfileRegistry>(File.ReadAllText(paths.ProfilesRegistryFile), options);
            if (registry is null || registry.SchemaVersion != 1 || registry.Profiles is null)
            {
                return null;
            }

            var matches = registry.Profiles.Where(profile =>
                IsSafeProfileId(profile.ProfileId) && LauncherPaths.IsSafeProfileDirectoryName(profile.ProfileDirectoryName) &&
                MsixLaunchPolicy.PathsEqual(directory,
                    Path.Combine(paths.ProfilesRoot, profile.ProfileDirectoryName, "electron"))).ToArray();
            if (matches.Length != 1)
            {
                return null;
            }

            var candidate = matches[0];
            var markerPath = Path.Combine(paths.ProfilesRoot, candidate.ProfileDirectoryName,
                "codex-home", "launcher-profile-v2.json");
            LauncherPaths.EnsureNoReparsePoints(markerPath);
            if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 64 * 1024)
            {
                return null;
            }

            var marker = JsonSerializer.Deserialize<WorkProfileMarker>(File.ReadAllText(markerPath), options);
            return marker is not null && string.Equals(marker.ProfileId, candidate.ProfileId, StringComparison.OrdinalIgnoreCase)
                ? candidate.ProfileId : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                         ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsProfileMutationBlocked(
        LauncherPaths paths, string profileId, ProcessMarker? stateMarker = null,
        IReadOnlySet<string>? registeredProfileIds = null)
    {
        var ownership = ClassifyChatGptRoots(paths);
        if (stateMarker is not null && ownership.Any(item => IsSameProcess(item.Process, stateMarker)))
        {
            return true;
        }

        return ownership.Any(item => IsManaged(item) &&
            (string.IsNullOrWhiteSpace(item.ProfileId) ||
             item.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase) ||
             registeredProfileIds is not null && !registeredProfileIds.Contains(item.ProfileId)));
    }

    public static bool IsPersonalRunning(LauncherPaths paths) =>
        ClassifyChatGptRoots(paths).Any(item => !IsManaged(item) || string.IsNullOrWhiteSpace(item.ProfileId));

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
        return file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
               file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileName(Path.GetDirectoryName(path))?.Equals("app", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsSameProcess(ChatGptProcessSnapshot process, ProcessMarker marker) =>
        process.ProcessId == marker.ProcessId && process.StartedAtUtc == marker.StartedAtUtc &&
        MsixLaunchPolicy.PathsEqual(process.ExecutablePath, marker.ExecutablePath);

    internal static string? TryReadRuntimeCacheProfileId(LauncherPaths paths, string executablePath)
    {
        try
        {
            var executable = Path.GetFullPath(executablePath);
            var appDirectory = Path.GetDirectoryName(executable);
            var cacheDirectory = appDirectory is null ? null : Path.GetDirectoryName(appDirectory);
            var versionsRoot = Path.Combine(paths.RuntimeCacheRoot, "versions");
            if (appDirectory is null || cacheDirectory is null ||
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
            if (!document.RootElement.TryGetProperty("ProfileId", out var property) || property.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var profileId = property.GetString();
            return IsSafeProfileId(profileId) ? profileId : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeProfileId(string? profileId) =>
        !string.IsNullOrWhiteSpace(profileId) && profileId.Length <= 64 &&
        profileId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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
            var window = process.MainWindowHandle;
            if (window == IntPtr.Zero)
            {
                return false;
            }

            ShowWindow(window, 9);
            return SetForegroundWindow(window);
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
