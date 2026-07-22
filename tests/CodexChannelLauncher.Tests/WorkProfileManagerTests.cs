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
        Assert.Equal(ProfileAuthMode.CustomResponses, registration.AuthMode);
        Assert.Equal(personalBefore, fixture.FingerprintPersonalFiles());
        var status = fixture.Manager.GetSetupStatus(registration.ProfileId);
        Assert.Equal(WorkProfileSetupState.Configured, status.State);

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
        Assert.DoesNotContain(apiKey, File.ReadAllText(fixture.Paths.ProfilesRegistryFile), StringComparison.Ordinal);
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
    public void UniqueLegacyMarkerIsMigratedInPlaceWithoutRewritingProfileFiles()
    {
        using var fixture = new TestFixture();
        var home = fixture.CreateLegacyCandidate("legacy-one", "legacy-key");
        var configBefore = HashFile(Path.Combine(home, "config.toml"));
        var authBefore = HashFile(Path.Combine(home, "auth.json"));
        var markerBefore = HashFile(Path.Combine(home, "launcher-profile-v2.json"));

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.Configured, status.State);
        Assert.Equal("legacy-one", status.Registration?.ProfileDirectoryName);
        Assert.Equal(configBefore, HashFile(Path.Combine(home, "config.toml")));
        Assert.Equal(authBefore, HashFile(Path.Combine(home, "auth.json")));
        Assert.Equal(markerBefore, HashFile(Path.Combine(home, "launcher-profile-v2.json")));
        Assert.True(File.Exists(fixture.Paths.ProfilesRegistryFile));
        Assert.False(File.Exists(fixture.Paths.WorkProfileRegistrationFile));
    }

    [Fact]
    public void MultipleLegacyMarkersAreMigratedTogetherWithDistinctColors()
    {
        using var fixture = new TestFixture();
        fixture.CreateLegacyCandidate("legacy-one", "key-one");
        fixture.CreateLegacyCandidate("legacy-two", "key-two");

        var profiles = fixture.Manager.GetProfiles();

        Assert.Equal(2, profiles.Count);
        Assert.Equal(2, profiles.Select(profile => profile.ProfileId).Distinct().Count());
        Assert.Equal(2, profiles.Select(profile => profile.AccentColor).Distinct().Count());
        Assert.Contains(profiles, profile => profile.ProfileDirectoryName == "legacy-one");
        Assert.Contains(profiles, profile => profile.ProfileDirectoryName == "legacy-two");
        Assert.True(File.Exists(fixture.Paths.ProfilesRegistryFile));
    }

    [Fact]
    public void CorruptLegacyRegistrationIsReportedSeparatelyFromMissingConfiguration()
    {
        using var fixture = new TestFixture();
        Directory.CreateDirectory(fixture.Paths.StateDirectory);
        File.WriteAllText(fixture.Paths.WorkProfileRegistrationFile, "{broken-json");

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.Invalid, status.State);
        Assert.NotNull(status.Problem);
        Assert.Null(status.Registration);
        Assert.False(File.Exists(fixture.Paths.ProfilesRegistryFile));
    }

    [Fact]
    public void UpdatePreservesApiKeyWhenPasswordFieldIsBlank()
    {
        using var fixture = new TestFixture();
        var registration = fixture.Manager.Configure(fixture.CreateRequest("original-key"));

        fixture.Manager.Configure(fixture.CreateRequest(
            string.Empty,
            ProfileSetupMode.Update,
            "更新后的空间",
            "updated-model",
            registration.ProfileId));

        using var auth = JsonDocument.Parse(File.ReadAllText(fixture.Paths.CompanyAuth));
        Assert.Equal("original-key", auth.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        var metadata = fixture.Manager.ReadMetadata(registration.ProfileId);
        Assert.Equal("更新后的空间", metadata.DisplayName);
        Assert.Equal("updated-model", metadata.Model);
    }

    [Fact]
    public void CreatesArbitraryProfilesWithIndependentHomesAndStableDistinctColors()
    {
        using var fixture = new TestFixture();
        var personalBefore = fixture.FingerprintPersonalFiles();

        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        var second = fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));
        var profiles = fixture.Manager.GetProfiles();

        Assert.Equal(2, profiles.Count);
        Assert.Equal("work", first.ProfileDirectoryName);
        Assert.Equal("work-2", second.ProfileDirectoryName);
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.NotEqual(first.AccentColor, second.AccentColor);
        Assert.True(File.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work", "codex-home", "auth.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work-2", "codex-home", "auth.json")));
        Assert.Equal(personalBefore, fixture.FingerprintPersonalFiles());

        var reloaded = new CompanyProfileManager(
            fixture.Paths.CreateProfileScope("work"),
            new ProfileSnapshotService(fixture.Paths.CreateProfileScope("work")));
        var reloadedProfiles = reloaded.GetProfiles();
        Assert.Equal(first.AccentColor, reloadedProfiles.Single(profile => profile.ProfileId == first.ProfileId).AccentColor);
        Assert.Equal(second.AccentColor, reloadedProfiles.Single(profile => profile.ProfileId == second.ProfileId).AccentColor);
    }

    [Fact]
    public void CreatesMoreProfilesThanThePresetPaletteWithUniqueColors()
    {
        using var fixture = new TestFixture();

        for (var index = 1; index <= 16; index++)
        {
            fixture.Manager.Configure(fixture.CreateRequest($"key-{index}", displayName: $"Space {index}"));
        }

        var profiles = fixture.Manager.GetProfiles();
        Assert.Equal(16, profiles.Count);
        Assert.Equal(16, profiles.Select(profile => profile.AccentColor).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(profiles, profile => profile.ProfileDirectoryName == "work-16");
    }

    [Fact]
    public void MissingRegisteredDirectoryIsNotReusedByANewProfile()
    {
        using var fixture = new TestFixture();
        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        Directory.Delete(Path.Combine(fixture.Paths.ProfilesRoot, first.ProfileDirectoryName), true);

        var second = fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));

        Assert.Equal("work-2", second.ProfileDirectoryName);
        Assert.Equal(2, fixture.Manager.GetProfiles().Count);
    }

    [Fact]
    public void StatusScanRestoresTheSelectedProfileDirectory()
    {
        using var fixture = new TestFixture();
        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));
        fixture.Paths.SelectWorkProfileDirectory(first.ProfileDirectoryName);
        var coordinator = new ProfileCoordinator(fixture.Paths);

        _ = coordinator.GetStatus();

        Assert.Equal(first.ProfileDirectoryName, coordinator.Paths.WorkProfileDirectoryName);
    }

    [Fact]
    public void ChatGptAccountProfileDoesNotCreateApiKeyAuthFile()
    {
        using var fixture = new TestFixture();

        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Create,
            "Account Space",
            AuthMode: ProfileAuthMode.ChatGptAccount));

        Assert.Equal(ProfileAuthMode.ChatGptAccount, registration.AuthMode);
        Assert.False(File.Exists(fixture.Paths.CompanyAuth));
        var config = File.ReadAllText(fixture.Paths.CompanyConfig);
        Assert.DoesNotContain("model_provider", config, StringComparison.Ordinal);
        Assert.DoesNotContain("model =", config, StringComparison.Ordinal);
        var metadata = fixture.Manager.ReadMetadata(registration.ProfileId);
        Assert.True(metadata.AuthConfigured);
        Assert.Equal("ChatGPT Account", metadata.ProviderName);
    }

    [Fact]
    public void OpenAiApiKeyProfileUsesBuiltInProviderWithoutCustomProviderSection()
    {
        using var fixture = new TestFixture();

        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Create,
            "OpenAI Space",
            Model: "gpt-example",
            ReasoningEffort: "high",
            ApiKey: "official-key",
            AuthMode: ProfileAuthMode.OpenAiApiKey));

        var config = File.ReadAllText(fixture.Paths.CompanyConfig);
        Assert.Contains("model = \"gpt-example\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("model_provider", config, StringComparison.Ordinal);
        Assert.DoesNotContain("[model_providers.", config, StringComparison.Ordinal);
        var metadata = fixture.Manager.ReadMetadata(registration.ProfileId);
        Assert.Equal(ProfileAuthMode.OpenAiApiKey, metadata.AuthMode);
        Assert.Equal("OpenAI API", metadata.ProviderName);
        Assert.Equal("https://api.openai.com/v1", metadata.BaseUrl);
    }

    [Fact]
    public void ImportWithoutApiKeyIsInferredAsChatGptAccount()
    {
        using var fixture = new TestFixture();
        var source = Path.Combine(fixture.Root, "account-import");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "config.toml"),
            "network_access = \"enabled\"\ndisable_response_storage = true\n");

        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Import,
            "Imported Account",
            ImportSourceHome: source));

        Assert.Equal(ProfileAuthMode.ChatGptAccount, registration.AuthMode);
        Assert.False(File.Exists(fixture.Paths.CompanyAuth));
        Assert.True(fixture.Manager.ReadMetadata(registration.ProfileId).AuthConfigured);
    }

    [Fact]
    public void ImportWithCustomProviderButWithoutApiKeyIsRejected()
    {
        using var fixture = new TestFixture();
        var source = Path.Combine(fixture.Root, "custom-import-without-key");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "config.toml"),
            """
            model_provider = "custom-provider"
            model = "example-model"
            model_reasoning_effort = "high"

            [model_providers.custom-provider]
            name = "Custom Provider"
            base_url = "https://api.example.invalid/v1"
            wire_api = "responses"
            requires_openai_auth = true
            """);

        Assert.Throws<InvalidDataException>(() => fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Import,
            "Invalid Custom Import",
            ImportSourceHome: source)));
        Assert.Empty(fixture.Manager.GetProfiles());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work")));
    }

    [Fact]
    public void ImportWithPlaceholderApiKeyIsNotMisclassifiedAsChatGptAccount()
    {
        using var fixture = new TestFixture();
        var source = Path.Combine(fixture.Root, "placeholder-key-import");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "config.toml"),
            "network_access = \"enabled\"\ndisable_response_storage = true\n");
        File.WriteAllText(
            Path.Combine(source, "auth.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "YOUR_API_KEY_HERE"
            }));

        Assert.Throws<InvalidDataException>(() => fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Import,
            "Placeholder Import",
            ImportSourceHome: source)));
        Assert.Empty(fixture.Manager.GetProfiles());
    }

    [Fact]
    public void AccountRegistrationRejectsAResidualCustomProviderRoute()
    {
        using var fixture = new TestFixture();
        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Create,
            "Account Space",
            AuthMode: ProfileAuthMode.ChatGptAccount));
        File.AppendAllText(
            fixture.Paths.CompanyConfig,
            """

            model_provider = "unexpected-provider"
            [model_providers.unexpected-provider]
            name = "Unexpected"
            base_url = "https://api.example.invalid/v1"
            wire_api = "responses"
            requires_openai_auth = true
            """);

        Assert.Throws<InvalidDataException>(() => fixture.Manager.ReadMetadata(registration.ProfileId));
    }

    [Fact]
    public void SwitchingFromAccountToApiModeRequiresANewKey()
    {
        using var fixture = new TestFixture();
        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Create,
            "Account Space",
            AuthMode: ProfileAuthMode.ChatGptAccount));

        Assert.Throws<ArgumentException>(() => fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Update,
            "API Space",
            Model: "gpt-example",
            ProfileId: registration.ProfileId,
            AuthMode: ProfileAuthMode.OpenAiApiKey)));
        Assert.False(File.Exists(fixture.Paths.CompanyAuth));
        Assert.Equal(
            ProfileAuthMode.ChatGptAccount,
            fixture.Manager.GetProfiles().Single().AuthMode);
    }

    [Fact]
    public void LegacyRegistrationIsConsumedByOneTimeMigration()
    {
        using var fixture = new TestFixture();
        var home = fixture.CreateLegacyCandidate("legacy-selected", "legacy-key");
        var configBefore = HashFile(Path.Combine(home, "config.toml"));
        var authBefore = HashFile(Path.Combine(home, "auth.json"));
        Directory.CreateDirectory(fixture.Paths.StateDirectory);
        File.WriteAllText(
            fixture.Paths.WorkProfileRegistrationFile,
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                ProfileDirectoryName = "legacy-selected",
                DisplayName = "Migrated Space",
                RegisteredAtUtc = DateTime.UtcNow
            }));

        var profile = Assert.Single(fixture.Manager.GetProfiles());

        Assert.Equal("Migrated Space", profile.DisplayName);
        Assert.True(File.Exists(fixture.Paths.ProfilesRegistryFile));
        Assert.False(File.Exists(fixture.Paths.WorkProfileRegistrationFile));
        Assert.Equal(configBefore, HashFile(Path.Combine(home, "config.toml")));
        Assert.Equal(authBefore, HashFile(Path.Combine(home, "auth.json")));
    }

    [Fact]
    public void LegacyDiscoveryStopsAfterTheNewRegistryExists()
    {
        using var fixture = new TestFixture();
        fixture.CreateLegacyCandidate("legacy-one", "key-one");
        Assert.Single(fixture.Manager.GetProfiles());

        fixture.CreateLegacyCandidate("legacy-two", "key-two");

        var profiles = fixture.Manager.GetProfiles();
        Assert.Single(profiles);
        Assert.DoesNotContain(profiles, profile => profile.ProfileDirectoryName == "legacy-two");
    }

    [Fact]
    public void RestoreRejectsArchiveTraversalWithoutWritingOutsideTheStagingDirectory()
    {
        using var fixture = new TestFixture();
        var archivePath = Path.Combine(fixture.Paths.SnapshotDirectory, "malicious.zip");
        var escapedPath = Path.Combine(fixture.Paths.OperationStagingRoot, "escaped.txt");
        var payload = Encoding.UTF8.GetBytes("must-not-be-extracted");
        var manifest = new ProfileSnapshotManifest(
            "malicious",
            DateTime.UtcNow,
            "security-test",
            false,
            false,
            [new SnapshotFileRecord(
                "profile/../../escaped.txt",
                payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)))],
            "company",
            true);

        using (var file = File.Create(archivePath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(manifest));
            }

            var maliciousEntry = archive.CreateEntry("profile/../../escaped.txt");
            using var entryStream = maliciousEntry.Open();
            entryStream.Write(payload);
        }

        Assert.Throws<InvalidDataException>(() => fixture.Snapshots.Restore(archivePath));
        Assert.False(File.Exists(escapedPath));
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
            string model = "example-model",
            string? profileId = null) =>
            new(
                mode,
                displayName,
                "custom-provider",
                "Custom Provider",
                "https://api.example.invalid/v1",
                model,
                "high",
                apiKey,
                ProfileId: profileId,
                AuthMode: ProfileAuthMode.CustomResponses);

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
