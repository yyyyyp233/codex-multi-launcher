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
    Attach,
    Update
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfileAuthMode>))]
public enum ProfileAuthMode
{
    CustomResponses,
    OpenAiApiKey,
    ChatGptAccount
}

public sealed record ManagedProfileRegistration(
    int SchemaVersion,
    string ProfileId,
    string ProfileDirectoryName,
    string DisplayName,
    ProfileAuthMode AuthMode,
    string AccentColor,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ManagedProfileRegistry(
    int SchemaVersion,
    IReadOnlyList<ManagedProfileRegistration> Profiles);

public sealed record ProfileSetupStatus(
    WorkProfileSetupState State,
    ManagedProfileRegistration? Registration,
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
    string? ExistingCodexHome = null,
    string? ProfileId = null,
    ProfileAuthMode AuthMode = ProfileAuthMode.CustomResponses);

public sealed record ProfileDeletionResult(
    string ProfileId,
    string DisplayName,
    bool LocalContentDeleted,
    string? RetainedDataRoot,
    string? CleanupPendingPath = null);

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
    public Dictionary<string, ProcessMarker> ProfileRootProcesses { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public DateTime? LastLaunchAtUtc { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ChannelKind>))]
    public ChannelKind? LastLaunchChannel { get; set; }

    public string? LastLaunchProfileId { get; set; }

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
    bool AuthConfigured,
    string ProfileId = "",
    ProfileAuthMode AuthMode = ProfileAuthMode.CustomResponses,
    string AccentColor = "#7C3AED");

public sealed record ManagedProfileRuntimeStatus(
    ManagedProfileRegistration Registration,
    bool Running,
    int RootProcessId,
    CompanyProfileMetadata? Metadata,
    string? Problem);

public sealed record RuntimeStatus(
    bool PersonalRunning,
    int PersonalRootProcessCount,
    CodexPackageInfo? Package,
    IReadOnlyList<ManagedProfileRuntimeStatus> ManagedProfiles,
    string? Problem);

public sealed record LaunchOutcome(
    bool Started,
    bool FocusRequested,
    bool BlockedByOtherChannel,
    ChannelKind Channel,
    string Message,
    int ProcessId,
    string? ProfileId = null);

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
    Dictionary<string, string>? CompanyTrayIconSha256 = null,
    string? ProfileId = null,
    string? BadgeColor = null);

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
