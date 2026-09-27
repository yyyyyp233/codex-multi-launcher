using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexChannelLauncher.Core;

internal static class MsixProcessInspector
{
    private sealed record CachedArguments(string? UserDataDirectory);
    private static readonly ConcurrentDictionary<(int Pid, long Start), CachedArguments> ArgumentCache = new();

    public static bool TryGetUserDataDirectory(ChatGptProcessSnapshot snapshot, out string? directory)
    {
        directory = null;
        var key = (snapshot.ProcessId, snapshot.StartedAtUtc.Ticks);
        if (ArgumentCache.TryGetValue(key, out var cached))
        {
            directory = cached.UserDataDirectory;
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            if (process.StartTime.ToUniversalTime() != snapshot.StartedAtUtc || process.HasExited)
            {
                return false;
            }

            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={snapshot.ProcessId}");
            searcher.Options.Timeout = TimeSpan.FromSeconds(2);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    if (item["CommandLine"] is not string commandLine || string.IsNullOrWhiteSpace(commandLine))
                    {
                        return false;
                    }

                    var arguments = SplitArguments(commandLine);
                    if (!MsixLaunchPolicy.TryReadUserDataArgument(arguments, out directory))
                    {
                        return false;
                    }

                    process.Refresh();
                    if (process.HasExited || process.StartTime.ToUniversalTime() != snapshot.StartedAtUtc)
                    {
                        return false;
                    }

                    if (ArgumentCache.Count > 128)
                    {
                        ArgumentCache.Clear();
                    }

                    ArgumentCache[key] = new CachedArguments(directory);
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or COMException or
                                         System.ComponentModel.Win32Exception or InvalidOperationException or
                                         ArgumentException or UnauthorizedAccessException)
        {
            // Unknown identity blocks mutation/launch; do not guess "personal".
        }

        return false;
    }

    public static string? GetPackageFullName(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid)
        {
            return null;
        }

        uint length = 0;
        if (GetPackageFullNameNative(handle, ref length, null) != 122 || length is 0 or > 4096)
        {
            return null;
        }

        var buffer = new StringBuilder((int)length);
        return GetPackageFullNameNative(handle, ref length, buffer) == 0 ? buffer.ToString() : null;
    }

    internal static string[] SplitArguments(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", EntryPoint = "GetPackageFullName", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullNameNative(SafeProcessHandle process, ref uint length, StringBuilder? name);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
