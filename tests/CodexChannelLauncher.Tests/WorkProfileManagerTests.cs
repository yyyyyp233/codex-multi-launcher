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
    public void AttachExistingProfileUsesSelectedDataInPlaceWithoutCreatingCopy()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("seed-key"));
        var home = fixture.CreateLegacyCandidate(
            "taishi",
            "existing-key",
            "company-app-codex-home");
        var profileRoot = Directory.GetParent(home)!.FullName;
        var session = Path.Combine(home, "sessions", "existing-thread.jsonl");
        var state = Path.Combine(home, "state_5.sqlite");
        var globalState = Path.Combine(home, ".codex-global-state.json");
        var electronState = Path.Combine(profileRoot, "electron", "Default", "Preferences");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        Directory.CreateDirectory(Path.GetDirectoryName(electronState)!);
        File.WriteAllText(session, """{"type":"existing-thread"}""");
        File.WriteAllText(state, "existing-sqlite");
        File.WriteAllText(globalState, """{"selected-project":{"type":"local"}}""");
        File.WriteAllText(electronState, """{"existing-window-state":true}""");
        var before = FingerprintDirectory(profileRoot);

        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Attach,
            "公司空间",
            ExistingCodexHome: profileRoot));

        Assert.Equal("taishi", registration.ProfileDirectoryName);
        Assert.Equal(home, fixture.Paths.CompanyCodexHome);
        Assert.Equal(before, FingerprintDirectory(profileRoot));
        Assert.Equal(2, fixture.Manager.GetProfiles().Count);
        Assert.True(File.Exists(session));
        Assert.True(File.Exists(state));
        Assert.True(File.Exists(globalState));
        Assert.True(File.Exists(electronState));
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work-2")));
    }

    [Fact]
    public void AttachRejectsPersonalCodexHomeWithoutModifyingIt()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("seed-key"));
        File.WriteAllText(fixture.Paths.PersonalConfig, TestFixture.ValidConfig);
        File.WriteAllText(
            fixture.Paths.PersonalAuth,
            JsonSerializer.Serialize(
                new Dictionary<string, string> { ["OPENAI_API_KEY"] = "personal-key" }));
        Directory.CreateDirectory(Path.Combine(fixture.Paths.PersonalSkills, "personal-skill"));
        File.WriteAllText(
            Path.Combine(fixture.Paths.PersonalSkills, "personal-skill", "SKILL.md"),
            "# personal skill");
        var before = fixture.FingerprintPersonalFiles();

        Assert.Throws<InvalidOperationException>(() => fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Attach,
            "个人空间",
            ExistingCodexHome: fixture.Paths.PersonalCodexHome)));

        Assert.Equal(before, fixture.FingerprintPersonalFiles());
        Assert.Single(fixture.Manager.GetProfiles());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work-2")));
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
    public void CompanyAppCodexHomeMarkerIsMigratedInPlaceWithConversationState()
    {
        using var fixture = new TestFixture();
        var home = fixture.CreateLegacyCandidate(
            "taishi",
            "legacy-key",
            "company-app-codex-home");
        var profileRoot = Directory.GetParent(home)!.FullName;
        var session = Path.Combine(home, "sessions", "legacy-thread.jsonl");
        var globalState = Path.Combine(home, ".codex-global-state.json");
        var electronState = Path.Combine(profileRoot, "electron", "Default", "Preferences");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        Directory.CreateDirectory(Path.GetDirectoryName(electronState)!);
        File.WriteAllText(session, """{"type":"legacy-thread"}""");
        File.WriteAllText(globalState, """{"selected-project":{"type":"local"}}""");
        File.WriteAllText(electronState, """{"legacy-window-state":true}""");
        var before = new Dictionary<string, string>
        {
            [session] = HashFile(session),
            [globalState] = HashFile(globalState),
            [electronState] = HashFile(electronState)
        };

        var status = fixture.Manager.GetSetupStatus();

        Assert.Equal(WorkProfileSetupState.Configured, status.State);
        Assert.Equal("taishi", status.Registration?.ProfileDirectoryName);
        Assert.All(before, item => Assert.Equal(item.Value, HashFile(item.Key)));
        Assert.True(File.Exists(fixture.Paths.ProfilesRegistryFile));
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
    public void DeleteProfileKeepsLocalContentByDefaultAndOnlyRemovesRegistration()
    {
        using var fixture = new TestFixture();
        var personalBefore = fixture.FingerprintPersonalFiles();
        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        var second = fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));
        var profileRoot = Path.Combine(fixture.Paths.ProfilesRoot, first.ProfileDirectoryName);
        var snapshotRoot = Path.Combine(fixture.Paths.RuntimeRoot, "snapshots", first.ProfileDirectoryName);
        var mergeRoot = Path.Combine(fixture.Paths.RuntimeRoot, "merge-bases", first.ProfileDirectoryName);
        var runtimeRoot = CreateRuntimeCache(fixture.Paths, first.ProfileId, "first-cache");
        Directory.CreateDirectory(mergeRoot);
        File.WriteAllText(Path.Combine(mergeRoot, "baseline.txt"), "keep-me");

        var result = fixture.Manager.Delete(first.ProfileId, deleteLocalContent: false);

        Assert.False(result.LocalContentDeleted);
        Assert.Equal(profileRoot, result.RetainedDataRoot);
        Assert.Null(result.CleanupPendingPath);
        Assert.True(Directory.Exists(profileRoot));
        Assert.True(Directory.Exists(snapshotRoot));
        Assert.True(Directory.Exists(mergeRoot));
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Equal(second.ProfileId, Assert.Single(fixture.Manager.GetProfiles()).ProfileId);
        Assert.Equal(personalBefore, fixture.FingerprintPersonalFiles());
    }

    [Fact]
    public void RetainedProfileCanBeReattachedInPlaceWithoutAllocatingANewDirectory()
    {
        using var fixture = new TestFixture();
        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));
        var profileRoot = Path.Combine(fixture.Paths.ProfilesRoot, first.ProfileDirectoryName);
        var codexHome = Path.Combine(profileRoot, "codex-home");
        var session = Path.Combine(codexHome, "sessions", "retained-thread.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        File.WriteAllText(session, """{"type":"retained-thread"}""");

        fixture.Manager.Delete(first.ProfileId, deleteLocalContent: false);
        var before = FingerprintDirectory(profileRoot);
        var reattached = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Attach,
            "First Reattached",
            ExistingCodexHome: codexHome));

        Assert.Equal("work", reattached.ProfileDirectoryName);
        Assert.Equal(first.AccentColor, reattached.AccentColor);
        Assert.Equal(codexHome, fixture.Paths.CompanyCodexHome);
        Assert.Equal(before, FingerprintDirectory(profileRoot));
        Assert.Equal(2, fixture.Manager.GetProfiles().Count);
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work-3")));
    }

    [Fact]
    public void DeleteProfileWithLocalContentRemovesOnlyOwnedData()
    {
        using var fixture = new TestFixture();
        var personalBefore = fixture.FingerprintPersonalFiles();
        var first = fixture.Manager.Configure(fixture.CreateRequest("first-key", displayName: "First"));
        var second = fixture.Manager.Configure(fixture.CreateRequest("second-key", displayName: "Second"));
        var firstProfileRoot = Path.Combine(fixture.Paths.ProfilesRoot, first.ProfileDirectoryName);
        var secondProfileRoot = Path.Combine(fixture.Paths.ProfilesRoot, second.ProfileDirectoryName);
        var snapshotRoot = Path.Combine(fixture.Paths.RuntimeRoot, "snapshots", first.ProfileDirectoryName);
        var mergeRoot = Path.Combine(fixture.Paths.RuntimeRoot, "merge-bases", first.ProfileDirectoryName);
        Directory.CreateDirectory(mergeRoot);
        File.WriteAllText(Path.Combine(mergeRoot, "baseline.txt"), "delete-me");
        var firstRuntimeRoot = CreateRuntimeCache(fixture.Paths, first.ProfileId, "first-cache");
        var secondRuntimeRoot = CreateRuntimeCache(fixture.Paths, second.ProfileId, "second-cache");

        var result = fixture.Manager.Delete(first.ProfileId, deleteLocalContent: true);

        Assert.True(result.LocalContentDeleted);
        Assert.Null(result.RetainedDataRoot);
        Assert.Null(result.CleanupPendingPath);
        Assert.False(Directory.Exists(firstProfileRoot));
        Assert.False(Directory.Exists(snapshotRoot));
        Assert.False(Directory.Exists(mergeRoot));
        Assert.False(Directory.Exists(firstRuntimeRoot));
        Assert.True(Directory.Exists(secondProfileRoot));
        Assert.True(Directory.Exists(secondRuntimeRoot));
        Assert.Equal(second.ProfileId, Assert.Single(fixture.Manager.GetProfiles()).ProfileId);
        Assert.Equal(personalBefore, fixture.FingerprintPersonalFiles());
    }

    [Fact]
    public void DeleteUnknownProfileDoesNotChangeExistingProfiles()
    {
        using var fixture = new TestFixture();
        var registration = fixture.Manager.Configure(fixture.CreateRequest("profile-key"));

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Manager.Delete(Guid.NewGuid().ToString("N"), deleteLocalContent: true));

        Assert.Equal(registration.ProfileId, Assert.Single(fixture.Manager.GetProfiles()).ProfileId);
        Assert.True(Directory.Exists(Path.Combine(
            fixture.Paths.ProfilesRoot,
            registration.ProfileDirectoryName)));
    }

    [Fact]
    public void DeleteWithLocalContentRollsDirectoriesBackWhenRegistryCommitFails()
    {
        using var fixture = new TestFixture();
        var registration = fixture.Manager.Configure(fixture.CreateRequest("profile-key"));
        var profileRoot = Path.Combine(fixture.Paths.ProfilesRoot, registration.ProfileDirectoryName);
        var mergeRoot = Path.Combine(
            fixture.Paths.RuntimeRoot,
            "merge-bases",
            registration.ProfileDirectoryName);
        Directory.CreateDirectory(mergeRoot);
        File.WriteAllText(Path.Combine(mergeRoot, "baseline.txt"), "must-survive");
        var runtimeRoot = CreateRuntimeCache(fixture.Paths, registration.ProfileId, "profile-cache");

        using (File.Open(
                   fixture.Paths.ProfilesRegistryFile,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var exception = Assert.ThrowsAny<Exception>(() =>
                fixture.Manager.Delete(registration.ProfileId, deleteLocalContent: true));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        Assert.True(Directory.Exists(profileRoot));
        Assert.True(Directory.Exists(mergeRoot));
        Assert.True(Directory.Exists(runtimeRoot));
        Assert.Equal(registration.ProfileId, Assert.Single(fixture.Manager.GetProfiles()).ProfileId);
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.Paths.OperationStagingRoot,
            "profile-delete-*"));
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
    public void AttachWithoutApiKeyIsInferredAsChatGptAccount()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("seed-key"));
        var source = fixture.CreateAttachCandidate(
            "account-existing",
            "network_access = \"enabled\"\ndisable_response_storage = true\n");

        var registration = fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Attach,
            "Existing Account",
            ExistingCodexHome: source));

        Assert.Equal(ProfileAuthMode.ChatGptAccount, registration.AuthMode);
        Assert.False(File.Exists(fixture.Paths.CompanyAuth));
        Assert.True(fixture.Manager.ReadMetadata(registration.ProfileId).AuthConfigured);
    }

    [Fact]
    public void AttachWithCustomProviderButWithoutApiKeyIsRejected()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("seed-key"));
        var source = fixture.CreateAttachCandidate(
            "custom-existing-without-key",
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
            ProfileSetupMode.Attach,
            "Invalid Existing Custom",
            ExistingCodexHome: source)));
        Assert.Single(fixture.Manager.GetProfiles());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ProfilesRoot, "work-2")));
    }

    [Fact]
    public void AttachWithPlaceholderApiKeyIsNotMisclassifiedAsChatGptAccount()
    {
        using var fixture = new TestFixture();
        fixture.Manager.Configure(fixture.CreateRequest("seed-key"));
        var source = fixture.CreateAttachCandidate(
            "placeholder-key-existing",
            "network_access = \"enabled\"\ndisable_response_storage = true\n",
            new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "YOUR_API_KEY_HERE"
            });

        Assert.Throws<InvalidDataException>(() => fixture.Manager.Configure(new ProfileSetupRequest(
            ProfileSetupMode.Attach,
            "Placeholder Existing",
            ExistingCodexHome: source)));
        Assert.Single(fixture.Manager.GetProfiles());
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

    private static string CreateRuntimeCache(LauncherPaths paths, string profileId, string directoryName)
    {
        var root = Path.Combine(paths.RuntimeCacheRoot, "versions", directoryName);
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "cache-manifest.json"),
            JsonSerializer.Serialize(new { ProfileId = profileId }));
        File.WriteAllText(Path.Combine(root, "payload.bin"), "runtime-copy");
        return root;
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

        public string CreateAttachCandidate(
            string directoryName,
            string config,
            IReadOnlyDictionary<string, string>? auth = null,
            string authority = "legacy-isolated-profile")
        {
            var home = Path.Combine(Paths.ProfilesRoot, directoryName, "codex-home");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "config.toml"), config);
            if (auth is not null)
            {
                File.WriteAllText(
                    Path.Combine(home, "auth.json"),
                    JsonSerializer.Serialize(auth));
            }

            File.WriteAllText(
                Path.Combine(home, "launcher-profile-v2.json"),
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 2,
                    InitializedAtUtc = DateTime.UtcNow,
                    Authority = authority
                }));
            return home;
        }

        public string CreateLegacyCandidate(
            string directoryName,
            string apiKey,
            string authority = "legacy-isolated-profile")
            => CreateAttachCandidate(
                directoryName,
                ValidConfig,
                new Dictionary<string, string> { ["OPENAI_API_KEY"] = apiKey },
                authority);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
