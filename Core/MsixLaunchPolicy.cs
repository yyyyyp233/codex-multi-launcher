using System.Text;

namespace CodexChannelLauncher.Core;

/// <summary>Pure helpers, also exercised by the MSIX regression tests.</summary>
internal static class MsixLaunchPolicy
{
    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Windows CommandLineToArgvW/CRT quoting, including trailing backslashes.
    public static string QuoteArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("启动参数不能含有 NUL 或换行。", nameof(value));
        }

        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    public static bool TryReadUserDataArgument(IReadOnlyList<string> arguments, out string? directory)
    {
        directory = null;
        var found = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            string? value = null;
            if (argument.StartsWith("--user-data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument["--user-data-dir=".Length..];
            }
            else if (argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= arguments.Count)
                {
                    return false;
                }

                value = arguments[index];
            }

            if (value is null)
            {
                continue;
            }

            if (found || string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                return false;
            }

            try
            {
                directory = Path.GetFullPath(value);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            found = true;
        }

        return true; // null means no explicit --user-data-dir, not a failed query.
    }
}

internal sealed class MsixActivitySnapshot
{
    private readonly string codexHome;
    private readonly string electronHome;
    private readonly Dictionary<string, (long Written, long Length)> beforeCodex;
    private readonly Dictionary<string, (long Written, long Length)> beforeElectron;

    public MsixActivitySnapshot(string codexHome, string electronHome)
    {
        this.codexHome = codexHome;
        this.electronHome = electronHome;
        beforeCodex = CaptureCodex(codexHome);
        beforeElectron = CaptureElectron(electronHome);
    }

    // Never accept the mere existence of an old database as readiness evidence.
    // This is a local activity check, not proof that every backend/auth path is isolated.
    public bool HasFreshActivity() =>
        Changed(beforeCodex, CaptureCodex(codexHome)) &&
        Changed(beforeElectron, CaptureElectron(electronHome));

    private static bool Changed(
        Dictionary<string, (long Written, long Length)> before,
        Dictionary<string, (long Written, long Length)> after) =>
        after.Any(item => !before.TryGetValue(item.Key, out var old) || old != item.Value);

    private static Dictionary<string, (long Written, long Length)> CaptureCodex(string home)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        Add(result, Path.Combine(home, ".codex-global-state.json"));
        foreach (var directory in new[] { home, Path.Combine(home, "sqlite") })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.sqlite*", SearchOption.TopDirectoryOnly).Take(128))
                {
                    Add(result, path);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return result;
    }

    private static Dictionary<string, (long Written, long Length)> CaptureElectron(string home)
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "Local State", "Default/Preferences", "Default/Network/Cookies", "lockfile" })
        {
            Add(result, Path.Combine(home, name.Replace('/', Path.DirectorySeparatorChar)));
        }

        return result;
    }

    private static void Add(Dictionary<string, (long Written, long Length)> result, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                result[path] = (info.LastWriteTimeUtc.Ticks, info.Length);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
