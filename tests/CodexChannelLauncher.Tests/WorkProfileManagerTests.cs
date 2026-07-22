using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexChannelLauncher.Core;
using Xunit;

namespace CodexChannelLauncher.Tests;

public sealed class WorkProfileManagerTests
{
    [Fact]
    public void CreateProfileGeneratesIsolatedConfigAndKeepsSecretsOutOfSnapshotsAndLogs()
    {
        using var fixture = new TestFixture();
        var apiKey = "test-secret-" + Guid.NewGuid().ToString("N");
        var personalBefore = fixture.FingerprintPersonalFiles();

        var registration = fixture.Manager.Configure(fixture.CreateRequest(apiKey));

        Assert.Equal("work", registration.ProfileDirectoryName);
        Assert.Equal("研发空间", registration.DisplayName);
        Assert.Equal(personalBefore, fixture.FingerprintPersonalFiles());
        var status = fixture.Manager.GetSetupStatus();
        Assert.Equal(WorkProfileSetupState.Configured, status.State);
        Assert.Equal("研发空间", status.Registration?.DisplayName);

        var config = File.ReadAllText(fixture.Paths.CompanyConfig);
        Assert.Contains("model_provider = \"custom-provider\"", config, StringComparison.Ordinal);
        Assert.Contains("model = \"example-model\"", config, StringComparison.Ordinal);
        Assert.Contains("model_reasoning_effort = \"high\"", config, StringComparison.Ordinal);
        Assert.Contains("network_access = \"enabled\"", config, StringComparison.Ordinal);
        Assert.Contains("disable_response_storage = true", config, StringComparison.Ordinal);
        Assert.Contains("[model_providers.custom-provider]", config, StringComparison.Ordinal);
        Assert.Contains("base_url = \"https://api.example.invalid/v1\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, config, StringComparison.Ordinal);

        using (var auth = JsonDocument.Parse(File.ReadAllText(fixture.Paths.CompanyAuth)))
        {
            Assert.Equal(apiKey, auth.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        }

        Assert.DoesNotContain(apiKey, File.ReadAllText(fixture.Paths.CompanyProfileMarker), StringComparison.Ordinal);
        Assert.DoesNotContain(
            apiKey,
            File.ReadAllText(fixture.Paths.WorkProfileRegistrationFile),
            StringComparison.Ordinal);

        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.tmp-*", SearchOption.AllDirectories));
        if (File.Exists(fixture.Paths.LogFile))
        {
            Assert.DoesNotContain(apiKey, File.ReadAllText(fixture.Paths.LogFile), StringComparison.Ordinal);
        }

        var archives = Directory.EnumerateFiles(fixture.Paths.SnapshotDirectory, "*.zip").ToArray();
        Assert.NotEmpty(archives);
        foreach (var archivePath in archives)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.EndsWith("auth.json", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in archive.Entries.Where(entry => entry.Length <= 1024 * 1024))
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                Assert.DoesNotContain(apiKey, reader.ReadToEnd(), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("https://api.example.invalid/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://127.0.0.1:8080/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("http://api.example.invalid/v1", false)]
    [InlineData("https://user:password@example.invalid/v1", false)]
    [InlineData("https://api.example.invalid/v1?token=value", false)]
    [InlineData("not-a-url", false)]
    public void BaseUrlValidationEnforcesTransportAndCredentialRules(string value, bool valid)
    {
        if (valid)
        {
            Assert.NotEmpty(CompanyProfileManager.ValidateAndNormalizeBaseUrl(value));
        }
        else
        {
            Assert.Throws<ArgumentException>(() =>
                CompanyProfileManager.ValidateAndNormalizeBaseUrl(value));
        }
    }

    [Fact]
    public void ImportCopiesOnlyTheDocumentedAllowlistAndDoesNotModifySource()
    {
        using var fixture = new TestFixture();
        var source = Path.Combine(fixture.Root, "import-source");
        CreateImportSource(source, "import-key");
        var sourceBefore = FingerprintDirectory(source);

        fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Import,
            "导入空间",
            ImportSourceHome: source));

        Assert.Equal(sourceBefore, FingerprintDirectory(source));
        Assert.True(File.Exists(fixture.Paths.CompanyConfig));
        Assert.True(File.Exists(fixture.Paths.CompanyAuth));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.CompanyCodexHome, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.CompanySkills, "user-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.CompanyMemories, "notes", "durable.md")));

        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CompanySkills, ".system")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.CompanySkills, "user-skill", ".env")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.CompanyMemories, ".git", "config")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CompanyCodexHome, "sessions")));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.CompanyCodexHome, "state.sqlite")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CompanyCodexHome, "logs")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.CompanyCodexHome, "plugins")));
    }

    [Fact]
    public void ImportingThePersonalCodexHomeLeavesEveryPersonalFileUnchanged()
    {
        using var fixture = new TestFixture();
        File.WriteAllText(fixture.Paths.PersonalConfig, TestFixture.ValidConfig);
        File.WriteAllText(
            fixture.Paths.PersonalAuth,
            JsonSerializer.Serialize(
                new Dictionary<string, string> { ["OPENAI_API_KEY"] = "personal-import-key" }));
        Directory.CreateDirectory(Path.Combine(fixture.Paths.PersonalSkills, "personal-skill"));
        File.WriteAllText(
            Path.Combine(fixture.Paths.PersonalSkills, "personal-skill", "SKILL.md"),
            "# personal skill");
        var before = fixture.FingerprintPersonalFiles();

        fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Import,
            "个人配置副本",
            ImportSourceHome: fixture.Paths.PersonalCodexHome));

        Assert.Equal(before, fixture.FingerprintPersonalFiles());
    }

    [Fact]
    public void UniqueLegacyMarkerIsRegisteredInPlaceWithoutRewritingProfileFiles()
    {
        using var fixture = new TestFixture();
        var home = fixture.CreateLegacyCandidate("legacy-one", "legacy-key");
        var configBefore = HashFile(Path.Combine(home, "config.toml"));
        var authBefore = HashFile(Path.Combine(home, "auth.json"));

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.Configured, status.State);
        Assert.Equal("legacy-one", status.Registration?.ProfileDirectoryName);
        Assert.Equal("legacy-one", fixture.Paths.WorkProfileDirectoryName);
        Assert.Equal(configBefore, HashFile(Path.Combine(home, "config.toml")));
        Assert.Equal(authBefore, HashFile(Path.Combine(home, "auth.json")));
        Assert.True(File.Exists(fixture.Paths.WorkProfileRegistrationFile));
    }

    [Fact]
    public void MultipleLegacyMarkersRequireExplicitSelection()
    {
        using var fixture = new TestFixture();
        fixture.CreateLegacyCandidate("legacy-one", "key-one");
        fixture.CreateLegacyCandidate("legacy-two", "key-two");

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.NotConfigured, status.State);
        Assert.Equal(2, status.Candidates.Count);
        Assert.False(File.Exists(fixture.Paths.WorkProfileRegistrationFile));

        fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.RegisterExisting,
            "选择后的空间",
            ExistingProfileDirectoryName: "legacy-two"));
        var selected = fixture.Manager.GetSetupStatus();
        Assert.Equal(WorkProfileSetupState.Configured, selected.State);
        Assert.Equal("legacy-two", selected.Registration?.ProfileDirectoryName);
    }

    [Fact]
    public void CorruptRegistrationIsReportedSeparatelyFromMissingConfiguration()
    {
        using var fixture = new TestFixture();
        Directory.CreateDirectory(fixture.Paths.StateDirectory);
        File.WriteAllText(fixture.Paths.WorkProfileRegistrationFile, "{broken-json");

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.Invalid, status.State);
        Assert.NotNull(status.Problem);
        Assert.Null(status.Registration);
    }

    [Fact]
    public void UpdatePreservesApiKeyWhenPasswordFieldIsBlank()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("original-key"));

        fixture.Manager.Configure(fixture.CreateRequest(
            string.Empty,
            ProfileSetupMode.Update,
            displayName: "更新后的空间",
            model: "updated-model"));

        using var auth = JsonDocument.Parse(File.ReadAllText(fixture.Paths.CompanyAuth));
        Assert.Equal("original-key", auth.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        var metadata = fixture.Manager.ReadMetadata();
        Assert.Equal("更新后的空间", metadata.DisplayName);
        Assert.Equal("updated-model", metadata.Model);
    }

    private static void CreateImportSource(string source, string apiKey)
    {
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "config.toml"), TestFixture.ValidConfig);
        File.WriteAllText(
            Path.Combine(source, "auth.json"),
            JsonSerializer.Serialize(new Dictionary<string, string> { ["OPENAI_API_KEY"] = apiKey }));
        File.WriteAllText(Path.Combine(source, "AGENTS.md"), "portable rule");

        Directory.CreateDirectory(Path.Combine(source, "skills", "user-skill"));
        File.WriteAllText(Path.Combine(source, "skills", "user-skill", "SKILL.md"), "# user skill");
        File.WriteAllText(Path.Combine(source, "skills", "user-skill", ".env"), "SECRET=value");
        Directory.CreateDirectory(Path.Combine(source, "skills", ".system"));
        File.WriteAllText(Path.Combine(source, "skills", ".system", "SKILL.md"), "system skill");

        Directory.CreateDirectory(Path.Combine(source, "memories", "notes"));
        File.WriteAllText(Path.Combine(source, "memories", "notes", "durable.md"), "durable");
        Directory.CreateDirectory(Path.Combine(source, "memories", ".git"));
        File.WriteAllText(Path.Combine(source, "memories", ".git", "config"), "private");

        Directory.CreateDirectory(Path.Combine(source, "sessions"));
        File.WriteAllText(Path.Combine(source, "sessions", "session.jsonl"), "task");
        File.WriteAllText(Path.Combine(source, "state.sqlite"), "sqlite");
        Directory.CreateDirectory(Path.Combine(source, "logs"));
        File.WriteAllText(Path.Combine(source, "logs", "runtime.log"), "log");
        Directory.CreateDirectory(Path.Combine(source, "plugins", "cache"));
        File.WriteAllText(Path.Combine(source, "plugins", "cache", "plugin.bin"), "cache");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FingerprintDirectory(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class TestFixture : IDisposable
    {
        public const string ValidConfig = """
            model_provider = "custom-provider"
            model = "example-model"
            model_reasoning_effort = "high"
            network_access = "enabled"
            disable_response_storage = true

            [model_providers.custom-provider]
            name = "Custom Provider"
            base_url = "https://api.example.invalid/v1"
            wire_api = "responses"
            requires_openai_auth = true
            """;

        public TestFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "CodexMultiLauncher.Tests",
                Guid.NewGuid().ToString("N"));
            var user = Path.Combine(Root, "user");
            var local = Path.Combine(Root, "local");
            var roaming = Path.Combine(Root, "roaming");
            Paths = new LauncherPaths(new LauncherPathOverrides(
                user,
                local,
                roaming,
                Path.Combine(user, ".codex"),
                Path.Combine(roaming, "Codex", "web", "Codex"),
                Path.Combine(local, "launcher-runtime")));
            Paths.EnsureRuntimeDirectories();
            Directory.CreateDirectory(Paths.PersonalCodexHome);
            File.WriteAllText(Paths.PersonalConfig, "personal-config-sentinel");
            File.WriteAllText(Paths.PersonalAuth, "personal-auth-sentinel");
            File.WriteAllText(Path.Combine(Paths.PersonalCodexHome, "AGENTS.md"), "personal-rule-sentinel");
            Snapshots = new ProfileSnapshotService(Paths);
            Manager = new CompanyProfileManager(Paths, Snapshots);
        }

        public string Root { get; }

        public LauncherPaths Paths { get; }

        public ProfileSnapshotService Snapshots { get; }

        public CompanyProfileManager Manager { get; }

        public ProfileSetupRequest CreateRequest(
            string apiKey,
            ProfileSetupMode mode = ProfileSetupMode.Create,
            string displayName = "研发空间",
            string model = "example-model") =>
            new(
                mode,
                displayName,
                "custom-provider",
                "Custom Provider",
                "https://api.example.invalid/v1",
                model,
                "high",
                apiKey);

        public string FingerprintPersonalFiles() => FingerprintDirectory(Paths.PersonalCodexHome);

        public string CreateLegacyCandidate(string directoryName, string apiKey)
        {
            var home = Path.Combine(Paths.ProfilesRoot, directoryName, "codex-home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), ValidConfig);
            File.WriteAllText(
                Path.Combine(home, "auth.json"),
                JsonSerializer.Serialize(new Dictionary<string, string> { ["OPENAI_API_KEY"] = apiKey }));
            File.WriteAllText(
                Path.Combine(home, "launcher-profile-v2.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 2,
                    InitializedAtUtc = DateTime.UtcNow,
                    Authority = "legacy-isolated-profile"
                }));
            return home;
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
