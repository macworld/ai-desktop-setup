using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;
using Xunit;

namespace AiDesktopSetup.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private const string Token = "sk-fixture-not-a-real-token";
    private readonly string root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "ai-desktop-setup-config-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly CodexConfiguration Configuration = new("https://gateway.example/v1", "example-model", "high");

    public ConfigurationTests() => Directory.CreateDirectory(root);
    public void Dispose() { Directory.Delete(root, true); if (Directory.Exists(root + ".journal")) Directory.Delete(root + ".journal", true); }
    private ConfigurationService Service() => new(new ConfigurationJournal(root + ".journal", new ResumeId(Guid.NewGuid())));

    [Fact]
    public async Task PreservesSettingsAndAuthWithExplicitConfigurationAndDistinctBackups()
    {
        var original = "# preserve\nmodel = \"old\"\nreview_model = \"custom\"\nmodel_context_window = 123456\nmodel_auto_compact_token_limit = 99999\nnotify = [\"echo\", \"hello\"]\n[model_providers.ai_gateway]\nenv_key = \"OLD\"\nrequest_max_retries = 4\n[features]\nshell_tool = true\n[projects.\"/a/project\"]\ntrust_level = \"trusted\"\n";
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "config.toml"), original);
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "auth.json"), "{\"tokens\":{\"refresh_token\":\"fixture\"},\"extra\":42}");
        var messages = new List<SetupProgress>();
        var result = await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, new ImmediateProgress(messages.Add));
        var config = await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml"));
        Assert.Contains("model = \"example-model\"", config);
        Assert.Contains("model_reasoning_effort = \"high\"", config);
        Assert.Contains("review_model = \"custom\"", config);
        Assert.Contains("model_context_window = 123456", config);
        Assert.Contains("model_auto_compact_token_limit = 99999", config);
        Assert.Contains("request_max_retries = 4", config);
        Assert.Contains("[features]\nshell_tool = true", config);
        Assert.Contains("[projects.\"/a/project\"]", config);
        Assert.DoesNotContain("env_key", config);
        Assert.Equal(original, await TestCompat.ReadAllTextAsync(Path.Combine(result.BackupDirectory, "config.toml")));
        var auth = JsonNode.Parse(await TestCompat.ReadAllTextAsync(Path.Combine(root, "auth.json")))!;
        Assert.Equal(Token, (string?)auth["OPENAI_API_KEY"]);
        Assert.Equal("apikey", (string?)auth["auth_mode"]);
        Assert.Equal("fixture", (string?)auth["tokens"]!["refresh_token"]);
        Assert.Equal(42, (int?)auth["extra"]);
        var second = await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);
        Assert.NotEqual(result.BackupDirectory, second.BackupDirectory);
        Assert.Equal(config, await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml")));
        Assert.DoesNotContain(Token, string.Join(" ", messages));
#if !NETFRAMEWORK
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "auth.json")));
#endif
    }

    [Fact]
    public async Task FreshConfigDoesNotSetReviewOrContextLimits()
    {
        var result = await Service().ConfigureAsync(Configuration, new ApiCredential(Token), Path.Combine(root, "new-home"));
        var config = await TestCompat.ReadAllTextAsync(Path.Combine(root, "new-home", "config.toml"));
        Assert.DoesNotContain("review_model", config);
        Assert.DoesNotContain("model_context_window", config);
        Assert.DoesNotContain("model_auto_compact", config);
        Assert.DoesNotContain("[windows]", config);
        Assert.DoesNotContain("sandbox", config);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "auth.json.absent")));
    }

    [Theory]
    [InlineData("elevated")]
    [InlineData("unelevated")]
    public async Task AccountSetupPreservesExistingWindowsSandbox(string mode)
    {
        var setting = "[windows]\nsandbox = \"" + mode + "\"\n";
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "config.toml"), setting);
        await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);
        Assert.Contains(setting, await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml")));
    }

    [Theory]
    [InlineData("profile = \"work\"\n")]
    [InlineData("forced_login_method = \"chatgpt\"\n")]
    [InlineData("cli_auth_credentials_store = \"keyring\"\n")]
    [InlineData("cli_auth_credentials_store = \"auto\"\n")]
    [InlineData("cli_auth_credentials_store = \"ephemeral\"\n")]
    [InlineData("notify = [\n\"multiline\"\n]\n")]
    [InlineData("model = \"a\"\nmodel = \"b\"\n")]
    [InlineData("\"model\" = \"quoted\"\n")]
    [InlineData("[model_providers]\nai_gateway = { name = \"inline\" }\n")]
    [InlineData("[model]\nname = \"conflict\"\n")]
    [InlineData("model_context_window = 12__34\n")]
    [InlineData("desktop = \"invalid-table\"\n")]
    [InlineData("[desktop.localeOverride]\nvalue = \"en-US\"\n")]
    public async Task UnsupportedConfigIsUnchanged(string original)
    {
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "config.toml"), original);
        var error = await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root));
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(original, await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"keep\":true}]")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"tokens\":{\"key\":1,\"key\":2}}") ]
    public async Task InvalidAuthIsNotOverwritten(string original)
    {
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "auth.json"), original);
        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root));
        Assert.Equal(original, await TestCompat.ReadAllTextAsync(Path.Combine(root, "auth.json")));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sk-key\n")]
    [InlineData("sk-$command")]
    public async Task InvalidTokenDoesNotWrite(string token)
    {
        var service = Service();
        Assert.Throws<ProtocolException>(() => new ApiCredential(token));
        await Task.CompletedTask;
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task SymlinkIsRejectedWithoutChangingItsTarget()
    {
        var target = Path.Combine(root, "outside");
        await TestCompat.WriteAllTextAsync(target, "keep");
        var home = Path.Combine(root, "home"); Directory.CreateDirectory(home);
        TestCompat.CreateFileSymbolicLink(Path.Combine(home, "auth.json"), target);
        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), home));
        Assert.Equal("keep", await TestCompat.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFileSystemEntries(home));
    }

    [Fact]
    public async Task CancellationBeforeWritesKeepsDirectoryUntouched()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, cancellationToken: cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task FailureAfterFirstReplacementRestoresBothOriginalFiles()
    {
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "config.toml"), "model = \"old\"\n");
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "auth.json"), "{\"keep\":true}");
        var progress = new ImmediateProgress(value => { if (value.Stage == "writing-auth") throw new IOException("simulated write failure"); });
        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, progress));
        Assert.Equal("model = \"old\"\n", await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml")));
        Assert.Equal("{\"keep\":true}", await TestCompat.ReadAllTextAsync(Path.Combine(root, "auth.json")));
        Assert.Single(Directory.GetDirectories(root, "ai-desktop-setup-backup.*"));
    }

    [Fact]
    public async Task ConfigurationChangedWhilePreparingIsNotOverwritten()
    {
        var path = Path.Combine(root, "config.toml");
        await TestCompat.WriteAllTextAsync(path, "model = \"old\"\n");
        var progress = new ImmediateProgress(value => { if (value.Stage == "writing-config") File.WriteAllText(path, "model = \"external\"\n"); });
        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, progress));
        Assert.Equal("model = \"external\"\n", await TestCompat.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(root, "auth.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RollbackPreservesConcurrentChangesAfterFirstReplacement(bool configExisted)
    {
        var configPath = Path.Combine(root, "config.toml");
        var authPath = Path.Combine(root, "auth.json");
        const string originalConfig = "model = \"old\"\n";
        const string originalAuth = "{\"keep\":true}";
        if (configExisted) await TestCompat.WriteAllTextAsync(configPath, originalConfig);
        await TestCompat.WriteAllTextAsync(authPath, originalAuth);
        var progress = new ImmediateProgress(value =>
        {
            if (value.Stage != "writing-auth") return;
            File.WriteAllText(configPath, "model = \"external\"\n");
            File.WriteAllText(authPath, "{\"external\":true}");
        });

        var error = await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, progress));

        Assert.Equal("model = \"external\"\n", await TestCompat.ReadAllTextAsync(configPath));
        Assert.Equal("{\"external\":true}", await TestCompat.ReadAllTextAsync(authPath));
        var backup = Assert.Single(Directory.GetDirectories(root, "ai-desktop-setup-backup.*"));
        Assert.Contains("手动恢复", error.Message);
        Assert.Contains(backup, error.Message);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(originalAuth, await TestCompat.ReadAllTextAsync(Path.Combine(backup, "auth.json")));
        if (configExisted) Assert.Equal(originalConfig, await TestCompat.ReadAllTextAsync(Path.Combine(backup, "config.toml")));
        else Assert.True(File.Exists(Path.Combine(backup, "config.toml.absent")));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-TW")]
    public async Task ExplicitDesktopLanguageIsPreserved(string locale)
    {
        var original = "[desktop]\nlocaleOverride = \"" + locale + "\"\n";
        await TestCompat.WriteAllTextAsync(Path.Combine(root, "config.toml"), original);
        await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);
        Assert.Contains(original, await TestCompat.ReadAllTextAsync(Path.Combine(root, "config.toml")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"electron-persisted-atom-state\":[]}")]
    [InlineData("{\"history\":[1,2],\"localeOverride\":\"ja-JP\"}")]
    public async Task DesktopStateIsUntouchedEvenWhenMalformed(string original)
    {
        var path = Path.Combine(root, ".codex-global-state.json");
        File.WriteAllText(path, original);
        var result = await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.DoesNotContain(Directory.GetFiles(result.BackupDirectory), file => Path.GetFileName(file).StartsWith(".codex-global-state"));
    }

    [Fact]
    public async Task FreshConfigurationDoesNotCreateDesktopState()
    {
        await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);
        Assert.False(File.Exists(Path.Combine(root, ".codex-global-state.json")));
    }

    [Fact]
    public async Task DesktopStateLinkIsUntouched()
    {
        var home = Path.Combine(root, "home"); Directory.CreateDirectory(home);
        var target = Path.Combine(root, "outside-state"); File.WriteAllText(target, "not json");
        var link = Path.Combine(home, ".codex-global-state.json");
        TestCompat.CreateFileSymbolicLink(link, target);
        await Service().ConfigureAsync(Configuration, new ApiCredential(Token), home);
        Assert.Equal("not json", File.ReadAllText(target));
        Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);
    }

    [Fact]
    public async Task FailureAfterBothReplacementsRestoresBothOriginalFiles()
    {
        File.WriteAllText(Path.Combine(root, "config.toml"), "model = \"old\"\n");
        File.WriteAllText(Path.Combine(root, "auth.json"), "{\"keep\":true}");
        var reached = false;
        var progress = new ImmediateProgress(step =>
        {
            if (step.Stage != "config-written") return;
            reached = true;
            Assert.Contains("example-model", File.ReadAllText(Path.Combine(root, "config.toml")));
            Assert.Contains(Token, File.ReadAllText(Path.Combine(root, "auth.json")));
            throw new IOException("simulated failure after both replacements");
        });
        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, progress));
        Assert.True(reached);
        Assert.Equal("model = \"old\"\n", File.ReadAllText(Path.Combine(root, "config.toml")));
        Assert.Equal("{\"keep\":true}", File.ReadAllText(Path.Combine(root, "auth.json")));
    }

    [Theory]
    [InlineData("EXAMPLE_FAKE~+/==")]
    [InlineData("A+/~._-=")]
    public async Task AcceptsGenericBearerCharacters(string key)
    {
        await Service().ConfigureAsync(Configuration, new ApiCredential(key), root);
        var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "auth.json")))!;
        Assert.Equal(key, (string?)auth["OPENAI_API_KEY"]);
    }

    [Theory]
    [InlineData("", null, false)]
    [InlineData("model_reasoning_effort = \"low\" # user setting\n", null, true)]
    [InlineData("model_reasoning_effort = \"high\"\n", "none", true)]
    public async Task OptionalReasoningPreservesUserSettingOrWritesExplicitNone(string original, string? effort, bool hasReasoning)
    {
        File.WriteAllText(Path.Combine(root, "config.toml"), original);
        await Service().ConfigureAsync(new("https://gateway.example/Tenant/v1/", "m\\\"\n[evil]", effort), new ApiCredential(Token), root);
        var text = File.ReadAllText(Path.Combine(root, "config.toml"));
        Assert.Equal(hasReasoning, text.Contains("model_reasoning_effort"));
        if (effort == null && hasReasoning) Assert.Contains(original, text);
        if (effort != null) Assert.Contains("model_reasoning_effort = \"none\"", text);
        Assert.Contains("base_url = \"https://gateway.example/Tenant/v1/\"", text);
        Assert.DoesNotContain("\n[evil]", text);
    }

    private sealed class ImmediateProgress(Action<SetupProgress> report) : IProgress<SetupProgress> { public void Report(SetupProgress value) => report(value); }
}
