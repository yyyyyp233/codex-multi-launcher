using System.Text.Json;
using CodexChannelLauncher.Core;
using Microsoft.Win32;
using Xunit;

namespace CodexChannelLauncher.Tests;

public sealed class MsixLaunchRegressionTests
{
    [Theory]
    [InlineData("C:\\plain\\electron")]
    [InlineData("C:\\two words\\electron")]
    [InlineData("C:\\路径\\electron")]
    [InlineData("C:\\trailing\\")]
    [InlineData("a\"quoted\\value")]
    public void QuotingRoundTripsThroughWindows(string value)
    {
        var args = MsixProcessInspector.SplitArguments("ChatGPT.exe " + MsixLaunchPolicy.QuoteArgument(value));
        Assert.Equal(2, args.Length);
        Assert.Equal(value, args[1]);
    }

    [Theory]
    [InlineData("bad\rargument")]
    [InlineData("bad\nargument")]
    [InlineData("bad\0argument")]
    public void QuotingRejectsControlCharacters(string value) =>
        Assert.Throws<ArgumentException>(() => MsixLaunchPolicy.QuoteArgument(value));

    [Fact]
    public void UserDataArgumentSupportsBothWindowsForms()
    {
        Assert.True(MsixLaunchPolicy.TryReadUserDataArgument(["ChatGPT.exe", "--user-data-dir=C:\\profile\\electron"], out var first));
        Assert.True(MsixLaunchPolicy.TryReadUserDataArgument(["ChatGPT.exe", "--user-data-dir", "C:\\profile\\electron"], out var second));
        Assert.True(MsixLaunchPolicy.PathsEqual(first, second));
    }

    [Fact]
    public void NoUserDataArgumentIsNotTheSameAsAnUnreadableCommandLine()
    {
        Assert.True(MsixLaunchPolicy.TryReadUserDataArgument(["ChatGPT.exe"], out var directory));
        Assert.Null(directory);
    }

    [Fact]
    public void DuplicateOrRelativeUserDataArgumentsAreRejected()
    {
        Assert.False(MsixLaunchPolicy.TryReadUserDataArgument(
            ["ChatGPT.exe", "--user-data-dir=C:\\one", "--user-data-dir=C:\\two"], out _));
        Assert.False(MsixLaunchPolicy.TryReadUserDataArgument(["ChatGPT.exe", "--user-data-dir=relative"], out _));
        Assert.False(MsixLaunchPolicy.TryReadUserDataArgument(["ChatGPT.exe", "--user-data-dir"], out _));
    }

    [Fact]
    public void OldFilesDoNotProveNewStartup()
    {
        using var fixture = new Fixture();
        var home = fixture.Directory("home");
        var electron = fixture.Directory("electron");
        File.WriteAllText(Path.Combine(home, "state_5.sqlite"), "old");
        File.WriteAllText(Path.Combine(electron, "Local State"), "old");
        var activity = new MsixActivitySnapshot(home, electron);
        Assert.False(activity.HasFreshActivity());
        File.WriteAllText(Path.Combine(home, "state_5.sqlite-wal"), "new database activity");
        Assert.False(activity.HasFreshActivity());
        File.WriteAllText(Path.Combine(electron, "Local State"), "new electron activity");
        Assert.True(activity.HasFreshActivity());
    }

    [Fact]
    public void UserEnvironmentRestoresMissingAndExpandStringValues()
    {
        var absent = new MsixEnvironmentValue(false, RegistryValueKind.None, null);
        var expanded = new MsixEnvironmentValue(true, RegistryValueKind.ExpandString, "%USERPROFILE%\\custom");
        var applied = new MsixEnvironmentValue(true, RegistryValueKind.String, "C:\\profile");
        Assert.Equal(MsixRestoreDecision.Restore, MsixUserEnvironmentLease.DecideRestore(applied, absent, applied));
        Assert.Equal(MsixRestoreDecision.Restore, MsixUserEnvironmentLease.DecideRestore(applied, expanded, applied));
        Assert.Equal(MsixRestoreDecision.AlreadyRestored, MsixUserEnvironmentLease.DecideRestore(expanded, expanded, applied));
    }

    [Fact]
    public void UserEnvironmentDoesNotOverwriteAConcurrentEdit()
    {
        var before = new MsixEnvironmentValue(false, RegistryValueKind.None, null);
        var applied = new MsixEnvironmentValue(true, RegistryValueKind.String, "C:\\profile");
        var external = new MsixEnvironmentValue(true, RegistryValueKind.String, "D:\\external");
        Assert.Equal(MsixRestoreDecision.Conflict, MsixUserEnvironmentLease.DecideRestore(external, before, applied));
    }

    [Fact]
    public void UnknownProcessIsNeverSilentlyClassifiedAsPersonal()
    {
        var root = new ChatGptProcessSnapshot(123, 1, DateTime.UtcNow, "C:\\app\\ChatGPT.exe");
        Assert.True(ProcessInventory.IsManaged(new ChatGptProcessOwnership(root, false, string.Empty)));
        Assert.False(ProcessInventory.IsManaged(new ChatGptProcessOwnership(root, false, null)));
        Assert.True(ProcessInventory.IsManaged(new ChatGptProcessOwnership(root, true, null)));
    }

    [Fact]
    public void DirectoryRecoveryRequiresRegistryAndMarkerAgreement()
    {
        using var fixture = new Fixture();
        var paths = new LauncherPaths(new LauncherPathOverrides(
            fixture.Directory("user"), fixture.Directory("local"), fixture.Directory("roaming"),
            RuntimeRoot: fixture.Directory("runtime")));
        paths.EnsureWorkProfileDirectories();
        var registration = new ManagedProfileRegistration(1, "profile-a", "work", "A",
            ProfileAuthMode.ChatGptAccount, "#7C3AED", DateTime.UtcNow, DateTime.UtcNow);
        File.WriteAllText(paths.ProfilesRegistryFile, JsonSerializer.Serialize(new ManagedProfileRegistry(1, [registration])));
        var marker = new WorkProfileMarker(5, DateTime.UtcNow, "CodexChannelLauncher", "A", "test", false, false,
            ProfileAuthMode.ChatGptAccount, "#7C3AED", "profile-a");
        File.WriteAllText(paths.CompanyProfileMarker, JsonSerializer.Serialize(marker));
        Assert.Equal("profile-a", ProcessInventory.TryMatchRegisteredElectronDirectory(paths, paths.CompanyElectronData));
        File.WriteAllText(paths.CompanyProfileMarker, JsonSerializer.Serialize(marker with { ProfileId = "profile-b" }));
        Assert.Null(ProcessInventory.TryMatchRegisteredElectronDirectory(paths, paths.CompanyElectronData));
        Assert.Null(ProcessInventory.TryMatchRegisteredElectronDirectory(paths, fixture.Directory("unregistered")));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Codex.Msix.Tests", Guid.NewGuid().ToString("N"));
        public string Directory(string name)
        {
            var path = Path.Combine(root, name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose()
        {
            if (System.IO.Directory.Exists(root))
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
        }
    }
}
