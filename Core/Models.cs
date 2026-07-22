using System.Text.Json.Serialization;

namespace CodexChannelLauncher.Core;

public enum ChannelKind
{
    Personal,
    Company
}

public enum WorkProfileSetupState
{
    NotConfigured,
    Configured,
    Invalid
}

public enum ProfileSetupMode
{
    Create,
    Import,
    RegisterExisting,
    Update
}

public sealed record WorkProfileRegistration(
    int SchemaVersion,
    string ProfileDirectoryName,
    string DisplayName,
    DateTime RegisteredAtUtc);

public sealed record WorkProfileCandidate(
    string ProfileDirectoryName,
    string DisplayName,
    string CodexHome);

public sealed record ProfileSetupStatus(
    WorkProfileSetupState State,
    WorkProfileRegistration? Registration,
    IReadOnlyList<WorkProfileCandidate> Candidates,
    string? Problem);

public sealed record ProfileSetupRequest(
    ProfileSetupMode Mode,
    string DisplayName,
    string ProviderId = "",
    string ProviderName = "",
    string BaseUrl = "",
    string Model = "",
    string ReasoningEffort = "high",
    string ApiKey = "",
    string? ImportSourceHome = null,
    string? ExistingProfileDirectoryName = null);

public sealed record CodexPackageInfo(
    string ExecutablePath,
    string InstallLocation,
    string PackageVersion,
    string FileVersion,
    bool SupportsIsolatedElectronData);

public sealed record ProcessMarker(
    int ProcessId,
    DateTime StartedAtUtc,
    string ExecutablePath);

public sealed record ChatGptProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    DateTime StartedAtUtc,
    string ExecutablePath);

public sealed class LauncherState
{
    public ProcessMarker? CompanyRootProcess { get; set; }

    public DateTime? LastLaunchAtUtc { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ChannelKind>))]
    public ChannelKind? LastLaunchChannel { get; set; }

    public string? LastPackageVersion { get; set; }
}

public sealed record CompanyProfileMetadata(
    string DisplayName,
    string Provider,
    string ProviderName,
    string Model,
    string ReasoningEffort,
    string BaseUrl,
    DateTime ConfigUpdatedAt,
    bool AuthConfigured);

public sealed record RuntimeStatus(
    bool PersonalRunning,
    bool CompanyRunning,
    int PersonalRootProcessCount,
    int CompanyRootProcessId,
    CodexPackageInfo? Package,
    CompanyProfileMetadata? CompanyProfile,
    ProfileSetupStatus ProfileSetup,
    string? Problem);

public sealed record LaunchOutcome(
    bool Started,
    bool FocusRequested,
    bool BlockedByOtherChannel,
    ChannelKind Channel,
    string Message,
    int ProcessId);

public sealed record LaunchProgress(string Phase, int Percent, string Message);

public sealed record RuntimeCacheManifest(
    string PackageVersion,
    string SourceInstallLocation,
    long FileCount,
    long TotalBytes,
    string EntryExecutableName,
    string EntryExecutableSha256,
    DateTime CreatedAtUtc,
    int CompanyTrayBrandingVersion = 0,
    bool CompanyTrayBrandingApplied = false,
    Dictionary<string, string>? CompanyTrayIconSha256 = null);

public sealed record TrayBrandingResult(
    bool Applied,
    int Version,
    Dictionary<string, string> FileSha256,
    string Detail);

public sealed record SelfTestCheck(string Name, bool Passed, string Detail);

public sealed class SelfTestReport
{
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    public bool Passed { get; set; }

    public List<SelfTestCheck> Checks { get; init; } = [];

    public RuntimeStatus? RuntimeStatus { get; set; }
}

public sealed class SmokeLaunchReport
{
    public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

    public bool Passed { get; set; }

    public LaunchOutcome? Launch { get; set; }

    public RuntimeStatus? RuntimeStatus { get; set; }

    public bool CompanyHomeReceivedActivity { get; set; }

    public bool CompanyElectronDataReceivedActivity { get; set; }

    public string? Error { get; set; }
}
