using System.Text.Json;
using CodexChannelLauncher.Core;
using Xunit;

namespace CodexChannelLauncher.Tests;

public sealed class CoreSafetyRegressionTests
{
    [Fact]
    public void RuntimeCacheValidationRejectsSameLengthTamperingAndExtraFiles()
    {
        using var fixture = new DirectoryFixture();
        var source = Path.Combine(fixture.Root, "source");
        var cache = Path.Combine(fixture.Root, "cache");
        Directory.CreateDirectory(Path.Combine(source, "resources"));
        Directory.CreateDirectory(Path.Combine(cache, "resources"));
        File.WriteAllText(Path.Combine(source, "ChatGPT.exe"), "entry");
        File.WriteAllText(Path.Combine(cache, "ChatGPT.exe"), "entry");
        File.WriteAllText(Path.Combine(source, "resources", "app.asar"), "trusted");
        File.WriteAllText(Path.Combine(cache, "resources", "app.asar"), "altered");

        Assert.False(CodexRuntimeCache.ContainsCompleteSourceTree(
            source,
            cache,
            allowManagedTrayIconOverrides: true,
            out _,
            out _));

        File.WriteAllText(Path.Combine(cache, "resources", "app.asar"), "trusted");
        File.WriteAllText(Path.Combine(cache, "resources", "unexpected.dll"), "extra");
        Assert.False(CodexRuntimeCache.ContainsCompleteSourceTree(
            source,
            cache,
            allowManagedTrayIconOverrides: true,
            out _,
            out _));

        File.Delete(Path.Combine(cache, "resources", "unexpected.dll"));
        Assert.True(CodexRuntimeCache.ContainsCompleteSourceTree(
            source,
            cache,
            allowManagedTrayIconOverrides: true,
            out var count,
            out var bytes));
        Assert.Equal(2, count);
        Assert.Equal(12, bytes);
    }

    [Fact]
    public void MultilineMcpArgumentsAreParsedAndReplacedAsOneAssignment()
    {
        using var fixture = new DirectoryFixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        File.WriteAllText(config, """
            [mcp_servers.demo]
            command = "node"
            args = [
              "old-one",
              "old-two", # retained comment
            ]
            enabled = true
            """);

        var document = ProfileConfigDocument.Load(config);
        Assert.Equal(["old-one", "old-two"], Assert.Single(document.GetMcpServers()).Arguments);

        document.UpsertMcpServer("demo", "STDIO", "node", ["new"], true);
        document.SaveAtomic(config);

        var saved = File.ReadAllText(config);
        Assert.DoesNotContain("old-one", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("old-two", saved, StringComparison.Ordinal);
        Assert.Equal(
            ["new"],
            Assert.Single(ProfileConfigDocument.Load(config).GetMcpServers()).Arguments);
    }

    [Theory]
    [InlineData("env", "\"OPENAI_API_KEY\" = \"secret\"")]
    [InlineData("http_headers", "\"Authorization\" = \"Bearer secret\"")]
    public void QuotedKeysInSensitiveMcpTablesBlockImport(string table, string assignment)
    {
        using var fixture = new DirectoryFixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        File.WriteAllText(config, $"""
            [mcp_servers.demo]
            command = "node"

            [mcp_servers.demo.{table}]
            {assignment}
            """);

        var info = Assert.Single(ProfileConfigDocument.Load(config).GetMcpServers());
        Assert.True(info.ContainsSensitiveValues);
    }

    [Fact]
    public void QuotedSectionNamesAreUpdatedWithoutCreatingDuplicates()
    {
        using var fixture = new DirectoryFixture();
        var config = Path.Combine(fixture.Root, "config.toml");
        File.WriteAllText(config, """
            [plugins."foo.bar"]
            enabled = false
            """);

        var document = ProfileConfigDocument.Load(config);
        document.SetPluginEnabled("foo.bar", true);
        document.SaveAtomic(config);

        var saved = File.ReadAllText(config);
        Assert.Equal(1, saved.Split("[plugins.", StringSplitOptions.None).Length - 1);
        Assert.True(ProfileConfigDocument.Load(config).IsPluginEnabled("foo.bar"));
    }

    [Fact]
    public void RestoreAutomaticallyRollsBackEarlierResourcesWhenConfigCommitFails()
    {
        using var fixture = new DirectoryFixture();
        var paths = fixture.CreatePaths();
        paths.EnsureWorkProfileDirectories();
        var snapshots = new ProfileSnapshotService(paths);
        var skillFile = Path.Combine(paths.CompanySkills, "demo", "SKILL.md");
        var memoryFile = Path.Combine(paths.CompanyMemories, "note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryFile)!);
        File.WriteAllText(paths.CompanyConfig, "model = \"snapshot\"");
        File.WriteAllText(skillFile, "snapshot-skill");
        File.WriteAllText(memoryFile, "snapshot-memory");
        var snapshot = snapshots.CreateSnapshot("rollback-source");

        File.WriteAllText(skillFile, "before-restore-skill");
        File.WriteAllText(memoryFile, "before-restore-memory");
        File.Delete(paths.CompanyConfig);
        Directory.CreateDirectory(paths.CompanyConfig);

        var exception = Assert.ThrowsAny<Exception>(() => snapshots.Restore(snapshot.ArchivePath));
        Assert.True(exception is IOException or UnauthorizedAccessException);

        Assert.Equal("before-restore-skill", File.ReadAllText(skillFile));
        Assert.Equal("before-restore-memory", File.ReadAllText(memoryFile));
    }

    [Fact]
    public void SharedOperationGateRejectsASecondConcurrentOwner()
    {
        using var fixture = new DirectoryFixture();
        var paths = fixture.CreatePaths();
        paths.EnsureRuntimeDirectories();
        using var first = LauncherOperationGate.Acquire(paths);

        Assert.Throws<TimeoutException>(() =>
            LauncherOperationGate.Acquire(paths, TimeSpan.FromMilliseconds(120)));
    }

    [Fact]
    public void RuntimeCacheManifestRecoversProfileIdentityWithoutLauncherState()
    {
        using var fixture = new DirectoryFixture();
        var paths = fixture.CreatePaths();
        var profileId = Guid.NewGuid().ToString("N");
        var cacheRoot = Path.Combine(paths.RuntimeCacheRoot, "versions", "test-cache");
        var executable = Path.Combine(cacheRoot, "app", "ChatGPT.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "test");
        File.WriteAllText(
            Path.Combine(cacheRoot, "cache-manifest.json"),
            JsonSerializer.Serialize(new { ProfileId = profileId }));

        Assert.Equal(
            profileId,
            ProcessInventory.TryReadRuntimeCacheProfileId(paths, executable));

        File.Delete(Path.Combine(cacheRoot, "cache-manifest.json"));
        Assert.Null(ProcessInventory.TryReadRuntimeCacheProfileId(paths, executable));
    }

    [Fact]
    public void PathValidationRejectsAnIntermediateDirectoryReparsePoint()
    {
        using var fixture = new DirectoryFixture();
        var outside = Path.Combine(fixture.Root, "outside");
        var owner = Path.Combine(fixture.Root, "owner");
        var link = Path.Combine(owner, "redirected");
        Directory.CreateDirectory(Path.Combine(outside, "child"));
        Directory.CreateDirectory(owner);
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Some Windows policies disable unprivileged symbolic-link creation.
            return;
        }

        Assert.Throws<InvalidDataException>(() =>
            LauncherPaths.EnsureNoReparsePoints(Path.Combine(link, "child")));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        public DirectoryFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CodexChannelLauncher.CoreSafety.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public LauncherPaths CreatePaths()
        {
            var user = Path.Combine(Root, "user");
            var local = Path.Combine(Root, "local");
            var roaming = Path.Combine(Root, "roaming");
            return new LauncherPaths(new LauncherPathOverrides(
                user,
                local,
                roaming,
                Path.Combine(user, ".codex"),
                Path.Combine(roaming, "Codex", "web", "Codex"),
                Path.Combine(local, "launcher-runtime")));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
