namespace CodexChannelLauncher.Core;

public sealed record LauncherPathOverrides(
    string? UserProfile = null,
    string? LocalAppData = null,
    string? RoamingAppData = null,
    string? PersonalCodexHome = null,
    string? PersonalElectronData = null,
    string? RuntimeRoot = null);

public sealed class LauncherPaths
{
    public const string DefaultWorkProfileDirectoryName = "work";

    private string workProfileDirectoryName = DefaultWorkProfileDirectoryName;
    private readonly string userProfile;
    private readonly string localAppData;
    private readonly string roamingAppData;

    public LauncherPaths(LauncherPathOverrides? overrides = null)
    {
        overrides ??= new LauncherPathOverrides();
        userProfile = ResolveRoot(
            overrides.UserProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            nameof(overrides.UserProfile));
        localAppData = ResolveRoot(
            overrides.LocalAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            nameof(overrides.LocalAppData));
        roamingAppData = ResolveRoot(
            overrides.RoamingAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            nameof(overrides.RoamingAppData));

        PersonalCodexHome = Path.GetFullPath(
            overrides.PersonalCodexHome ?? Path.Combine(userProfile, ".codex"));
        PersonalElectronData = Path.GetFullPath(
            overrides.PersonalElectronData ?? Path.Combine(roamingAppData, "Codex", "web", "Codex"));

        RuntimeRoot = Path.GetFullPath(
            overrides.RuntimeRoot ?? Path.Combine(localAppData, "CodexChannelLauncher"));
        ProfilesRoot = Path.Combine(RuntimeRoot, "profiles");
        OperationStagingRoot = Path.Combine(RuntimeRoot, "staging");
        StateDirectory = Path.Combine(RuntimeRoot, "state");
        OperationLockFile = Path.Combine(StateDirectory, "profile-operation.lock");
        StateFile = Path.Combine(StateDirectory, "launcher-state.json");
        WorkProfileRegistrationFile = Path.Combine(StateDirectory, "work-profile.json");
        ProfilesRegistryFile = Path.Combine(StateDirectory, "profiles.json");
        LogDirectory = Path.Combine(RuntimeRoot, "logs");
        LogFile = Path.Combine(LogDirectory, "launcher.log");
        RuntimeCacheRoot = Path.Combine(RuntimeRoot, "runtime-cache");
    }

    public string PersonalCodexHome { get; }

    public string PersonalElectronData { get; }

    public string RuntimeRoot { get; }

    public string ProfilesRoot { get; }

    public string OperationStagingRoot { get; }

    public string StateDirectory { get; }

    public string OperationLockFile { get; }

    public string StateFile { get; }

    public string WorkProfileRegistrationFile { get; }

    public string ProfilesRegistryFile { get; }

    public string LogDirectory { get; }

    public string LogFile { get; }

    public string RuntimeCacheRoot { get; }

    public string WorkProfileDirectoryName => workProfileDirectoryName;

    public string WorkProfileRoot => Path.Combine(ProfilesRoot, workProfileDirectoryName);

    public string CompanyCodexHome => Path.Combine(WorkProfileRoot, "codex-home");

    public string CompanyElectronData => Path.Combine(WorkProfileRoot, "electron");

    public string CompanyProfileMarker => Path.Combine(CompanyCodexHome, "launcher-profile-v2.json");

    public string SnapshotDirectory => Path.Combine(RuntimeRoot, "snapshots", workProfileDirectoryName);

    public string MergeBaseDirectory => Path.Combine(RuntimeRoot, "merge-bases", workProfileDirectoryName);

    public string PersonalConfig => Path.Combine(PersonalCodexHome, "config.toml");

    public string PersonalAuth => Path.Combine(PersonalCodexHome, "auth.json");

    public string CompanyConfig => Path.Combine(CompanyCodexHome, "config.toml");

    public string CompanyAuth => Path.Combine(CompanyCodexHome, "auth.json");

    public string PersonalSkills => Path.Combine(PersonalCodexHome, "skills");

    public string CompanySkills => Path.Combine(CompanyCodexHome, "skills");

    public string PersonalMemories => Path.Combine(PersonalCodexHome, "memories");

    public string CompanyMemories => Path.Combine(CompanyCodexHome, "memories");

    public LauncherPaths CreateProfileScope(string directoryName)
    {
        var scoped = new LauncherPaths(new LauncherPathOverrides(
            userProfile,
            localAppData,
            roamingAppData,
            PersonalCodexHome,
            PersonalElectronData,
            RuntimeRoot));
        scoped.SelectWorkProfileDirectory(directoryName);
        return scoped;
    }

    public void SelectWorkProfileDirectory(string directoryName)
    {
        if (!IsSafeProfileDirectoryName(directoryName))
        {
            throw new InvalidDataException("工作空间注册信息包含无效目录名。");
        }

        workProfileDirectoryName = directoryName;
        ValidateIsolationBoundaries();
    }

    public void EnsureRuntimeDirectories()
    {
        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(SnapshotDirectory);
        Directory.CreateDirectory(MergeBaseDirectory);
        Directory.CreateDirectory(OperationStagingRoot);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RuntimeCacheRoot);
    }

    public void EnsureWorkProfileDirectories()
    {
        EnsureRuntimeDirectories();
        Directory.CreateDirectory(CompanyCodexHome);
        Directory.CreateDirectory(CompanyElectronData);
    }

    public void ValidateIsolationBoundaries()
    {
        if (PathsOverlap(PersonalCodexHome, CompanyCodexHome) ||
            PathsOverlap(PersonalElectronData, CompanyElectronData))
        {
            throw new InvalidOperationException("个人与工作空间数据目录发生重叠，已拒绝启动。");
        }

        if (IsUnder(RuntimeRoot, PersonalCodexHome) || IsUnder(PersonalCodexHome, RuntimeRoot))
        {
            throw new InvalidOperationException("启动器运行目录不能与个人 Codex Home 重叠。");
        }
    }

    public static bool IsUnder(string candidate, string parent)
    {
        var candidateFull = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parentFull = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return candidateFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidDataException($"无法解析路径根目录：{fullPath}");
        }

        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"路径包含不受支持的重解析点：{current}");
            }
        }
    }

    public static bool IsSafeProfileDirectoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value is "." or "..")
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static string ResolveRoot(string? configured, string fallback, string argumentName)
    {
        var value = configured ?? fallback;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("无法解析 Windows 用户目录。", argumentName);
        }

        return Path.GetFullPath(value);
    }

    private static bool PathsOverlap(string left, string right) =>
        PathsEqual(left, right) || IsUnder(left, right) || IsUnder(right, left);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
