using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexChannelLauncher.Core;

internal sealed record MsixEnvironmentValue(bool Exists, RegistryValueKind Kind, string? Value);
internal sealed record MsixEnvironmentEntry(string Name, MsixEnvironmentValue Before, MsixEnvironmentValue Applied);
internal sealed record MsixEnvironmentJournal(int Version, IReadOnlyList<MsixEnvironmentEntry> Entries);
internal enum MsixRestoreDecision { AlreadyRestored, Restore, Conflict }

/// <summary>
/// Serializes this launcher's temporary HKCU environment edits across its copies/sessions.
/// Other applications do NOT participate in this lock. This is not a Windows security boundary.
/// </summary>
internal sealed class MsixUserEnvironmentLease : IDisposable
{
    private static readonly string[] AllowedNames = ["CODEX_HOME", "CODEX_SQLITE_HOME"];
    private readonly FileStream gate;
    private bool disposed;
    private bool applied;
    private MsixEnvironmentJournal? activeJournal;

    internal static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexChannelLauncher", "msix-launch-state");

    internal static string JournalPath => Path.Combine(StateDirectory, "environment-journal.json");

    private MsixUserEnvironmentLease(FileStream gate) => this.gate = gate;

    public static MsixUserEnvironmentLease Acquire(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        LauncherPaths.EnsureNoReparsePoints(StateDirectory);
        Directory.CreateDirectory(StateDirectory);
        var lockPath = Path.Combine(StateDirectory, "environment.lock");
        LauncherPaths.EnsureNoReparsePoints(lockPath);
        var watch = Stopwatch.StartNew();
        FileStream? stream = null;
        while (stream is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(45)))
            {
                if (cancellationToken.WaitHandle.WaitOne(100))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (IOException exception)
            {
                throw new TimeoutException("另一个多开器正在切换 MSIX 启动环境，请在该次启动结束后重试。", exception);
            }
        }

        try
        {
            RecoverJournal();
            return new MsixUserEnvironmentLease(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    // No journal means no Windows environment writes. Busy means another live owner is restoring it.
    public static void RecoverInterruptedLaunch()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(JournalPath))
        {
            return;
        }

        try
        {
            using var lease = Acquire(CancellationToken.None, TimeSpan.FromMilliseconds(250));
        }
        catch (TimeoutException)
        {
            // The active owner still holds the inter-process file lock.
        }
    }

    public void Apply(string codexHome)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (applied)
        {
            throw new InvalidOperationException("一次环境租约只能应用一次。 ");
        }

        if (!Path.IsPathFullyQualified(codexHome) || !Directory.Exists(codexHome))
        {
            throw new DirectoryNotFoundException("隔离 Codex Home 必须是已存在的绝对路径。 ");
        }

        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true)
            ?? throw new InvalidOperationException("无法打开用户环境注册表。 ");
        var entries = AllowedNames.Select(name => new MsixEnvironmentEntry(
            name, ReadValue(key, name), new MsixEnvironmentValue(true, RegistryValueKind.String, codexHome))).ToArray();
        activeJournal = new MsixEnvironmentJournal(1, entries);
        WriteJournal(activeJournal); // durable BEFORE first registry write
        applied = true;
        foreach (var entry in entries)
        {
            WriteValue(key, entry.Name, entry.Applied);
        }

        key.Flush();
        Broadcast();
    }

    public void AssertApplied()
    {
        if (!applied)
        {
            throw new InvalidOperationException("尚未设置隔离启动环境。 ");
        }

        var journal = activeJournal ?? throw new InvalidOperationException("缺少本次环境事务记录。 ");
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true)
            ?? throw new InvalidOperationException("无法打开用户环境注册表。 ");
        if (journal.Entries.Any(entry => ReadValue(key, entry.Name) != entry.Applied))
        {
            throw new InvalidOperationException("启动过程中用户环境被其他程序修改，已停止此次启动验证。 ");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            if (applied)
            {
                RecoverJournal(activeJournal);
            }
        }
        finally
        {
            gate.Dispose();
        }
    }

    internal static MsixRestoreDecision DecideRestore(
        MsixEnvironmentValue current, MsixEnvironmentValue before, MsixEnvironmentValue intended) =>
        current == before ? MsixRestoreDecision.AlreadyRestored :
        current == intended ? MsixRestoreDecision.Restore : MsixRestoreDecision.Conflict;

    private static void RecoverJournal(MsixEnvironmentJournal? inMemory = null)
    {
        if (inMemory is null && !File.Exists(JournalPath))
        {
            return;
        }

        var journal = inMemory ?? ReadJournal();
        using var key = Registry.CurrentUser.CreateSubKey("Environment", writable: true)
            ?? throw new InvalidOperationException("无法打开用户环境注册表。 ");
        var conflicts = new List<string>();
        foreach (var entry in journal.Entries)
        {
            switch (DecideRestore(ReadValue(key, entry.Name), entry.Before, entry.Applied))
            {
                case MsixRestoreDecision.Restore:
                    WriteValue(key, entry.Name, entry.Before);
                    break;
                case MsixRestoreDecision.Conflict:
                    conflicts.Add(entry.Name);
                    break;
            }
        }

        key.Flush();
        Broadcast(); // repeat after a crash even when registry values were already restored
        if (conflicts.Count != 0)
        {
            throw new InvalidOperationException(
                $"未覆盖其他程序修改的用户环境变量：{string.Join(", ", conflicts)}。" +
                "恢复记录已保留，请先核对用户环境；不要删除恢复记录或继续启动新实例。 ");
        }

        File.Delete(JournalPath);
    }

    private static MsixEnvironmentJournal ReadJournal()
    {
        LauncherPaths.EnsureNoReparsePoints(JournalPath);
        if (new FileInfo(JournalPath).Length > 512 * 1024)
        {
            throw new InvalidDataException("MSIX 环境恢复记录异常，已拒绝自动恢复。 ");
        }

        var journal = JsonSerializer.Deserialize<MsixEnvironmentJournal>(File.ReadAllText(JournalPath));
        if (journal is null || journal.Version != 1 || journal.Entries is null || journal.Entries.Count != 2 ||
            !journal.Entries.Select(entry => entry.Name).Order().SequenceEqual(AllowedNames.Order()))
        {
            throw new InvalidDataException("MSIX 环境恢复记录格式不正确。 ");
        }

        foreach (var entry in journal.Entries)
        {
            ValidateValue(entry.Before);
            ValidateValue(entry.Applied);
            if (!entry.Applied.Exists || entry.Applied.Kind != RegistryValueKind.String ||
                !Path.IsPathFullyQualified(entry.Applied.Value!))
            {
                throw new InvalidDataException("MSIX 环境恢复记录含有无效目标。 ");
            }
        }

        return journal;
    }

    private static void ValidateValue(MsixEnvironmentValue value)
    {
        if (value is null || value.Exists &&
            (value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) ||
             value.Value is null || value.Value.Length > 32767 || value.Value.Contains('\0')) ||
            !value.Exists && (value.Value is not null || value.Kind != RegistryValueKind.None))
        {
            throw new InvalidDataException("MSIX 环境恢复值格式不正确。 ");
        }
    }

    private static MsixEnvironmentValue ReadValue(RegistryKey key, string name)
    {
        if (!key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new MsixEnvironmentValue(false, RegistryValueKind.None, null);
        }

        var kind = key.GetValueKind(name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) ||
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
        {
            throw new InvalidDataException($"用户环境变量 {name} 不是受支持的字符串类型。 ");
        }

        return new MsixEnvironmentValue(true, kind, value);
    }

    private static void WriteValue(RegistryKey key, string name, MsixEnvironmentValue value)
    {
        if (value.Exists)
        {
            key.SetValue(name, value.Value!, value.Kind);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    private static void WriteJournal(MsixEnvironmentJournal journal)
    {
        LauncherPaths.EnsureNoReparsePoints(JournalPath);
        var temporary = JournalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, journal, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, JournalPath); // never overwrite an unrecovered journal
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void Broadcast()
    {
        if (SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 3000, out _) == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows 环境变化广播未完成；恢复记录已保留供重试。 ");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
