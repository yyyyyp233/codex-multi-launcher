using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CodexChannelLauncher.Core;

public sealed partial class CodexPackageLocator
{
    private const string PackagePrefix = "OpenAI.Codex_";
    private const string PackageSuffix = "_x64__2p2nqsd0c76g0";
    private const string PackageRepositoryRegistryPath =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private static readonly byte[] IsolatedUserDataMarker = Encoding.ASCII.GetBytes("CODEX_ELECTRON_USER_DATA_PATH");
    private readonly object cacheGate = new();
    private CodexPackageInfo? cachedPackage;

    public CodexPackageInfo Locate(bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            lock (cacheGate)
            {
                if (cachedPackage is not null && File.Exists(cachedPackage.ExecutablePath))
                {
                    return cachedPackage;
                }
            }
        }

        var candidates = new List<PackageCandidate>();

        candidates.AddRange(EnumerateRegisteredPackageExecutables()
            .Select(path => new PackageCandidate(path, IsRegisteredPackage: true)));

        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    candidates.Add(new PackageCandidate(path, IsRegisteredPackage: false));
                }
            }
            catch
            {
                // A protected child process is not a package discovery failure.
            }
            finally
            {
                process.Dispose();
            }
        }

        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

        try
        {
            candidates.AddRange(
                Directory.EnumerateDirectories(windowsApps, $"{PackagePrefix}*{PackageSuffix}")
                    .Select(path => new PackageCandidate(
                        Path.Combine(path, "app", "ChatGPT.exe"),
                        IsRegisteredPackage: false)));
        }
        catch
        {
            // Running-process discovery above still works when WindowsApps enumeration is restricted.
        }

        var selected = candidates
            .Where(candidate => File.Exists(candidate.Path))
            .Select(candidate => new
            {
                Path = Path.GetFullPath(candidate.Path),
                Version = ReadPackageVersion(candidate.Path),
                candidate.IsRegisteredPackage
            })
            .Where(item => item.Version is not null &&
                           (item.IsRegisteredPackage || LauncherPaths.IsUnder(item.Path, windowsApps)))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        if (selected is null || selected.Version is null)
        {
            throw new FileNotFoundException("未找到已安装的 OpenAI.Codex Windows App（ChatGPT.exe）。");
        }

        var result = CreatePackageInfo(selected.Path, selected.Version);

        lock (cacheGate)
        {
            cachedPackage = result;
        }

        return result;
    }

    internal CodexPackageInfo LocateFromPackageRegistration()
    {
        var selected = EnumerateRegisteredPackageExecutables()
            .Where(File.Exists)
            .Select(path => new { Path = Path.GetFullPath(path), Version = ReadPackageVersion(path) })
            .Where(item => item.Version is not null)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        if (selected is null || selected.Version is null)
        {
            throw new FileNotFoundException("当前用户的 AppX 注册信息中未找到已安装的 OpenAI.Codex Windows App。");
        }

        return CreatePackageInfo(selected.Path, selected.Version);
    }

    private static IReadOnlyList<string> EnumerateRegisteredPackageExecutables()
    {
        var executables = new List<string>();

        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(PackageRepositoryRegistryPath, writable: false);
            if (packages is null)
            {
                return executables;
            }

            foreach (var packageName in packages.GetSubKeyNames())
            {
                if (!PackageNameRegex().IsMatch(packageName))
                {
                    continue;
                }

                using var package = packages.OpenSubKey(packageName, writable: false);
                var packageRoot = package?.GetValue("PackageRootFolder") as string;
                if (!string.IsNullOrWhiteSpace(packageRoot) && Path.IsPathFullyQualified(packageRoot))
                {
                    executables.Add(Path.Combine(packageRoot, "app", "ChatGPT.exe"));
                }
            }
        }
        catch
        {
            // Process and WindowsApps discovery remain available on unusual Windows profiles.
        }

        return executables;
    }

    private static CodexPackageInfo CreatePackageInfo(string executable, Version version)
    {
        var installLocation = Directory.GetParent(Directory.GetParent(executable)!.FullName)!.FullName;
        var asar = Path.Combine(installLocation, "app", "resources", "app.asar");
        var fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "unknown";

        return new CodexPackageInfo(
            executable,
            installLocation,
            version.ToString(),
            fileVersion,
            File.Exists(asar) && ContainsMarker(asar, IsolatedUserDataMarker));
    }

    private static Version? ReadPackageVersion(string executablePath)
    {
        var directory = Directory.GetParent(Directory.GetParent(executablePath)!.FullName)?.Name;
        if (directory is null)
        {
            return null;
        }

        var match = PackageNameRegex().Match(directory);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private static bool ContainsMarker(string filePath, ReadOnlySpan<byte> marker)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        var matched = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var current = buffer[index];
                if (current == marker[matched])
                {
                    matched++;
                    if (matched == marker.Length)
                    {
                        return true;
                    }
                }
                else
                {
                    matched = current == marker[0] ? 1 : 0;
                }
            }
        }

        return false;
    }

    [GeneratedRegex(@"^OpenAI\.Codex_(?<version>[^_]+)_x64__2p2nqsd0c76g0$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageNameRegex();

    private sealed record PackageCandidate(string Path, bool IsRegisteredPackage);
}
