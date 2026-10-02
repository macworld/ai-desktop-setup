using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;

namespace AiDesktopSetup.Tests;

public sealed class ConfigurationCompatibilityTests : IDisposable
{
    private const string Token = "sk-fixture-not-a-real-token";
    private const string OriginalConfig = "model = \"old\"\n";
    private const string OriginalAuth = "{\"keep\":true}";
    private readonly string root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "ai-desktop-setup-config-compat-" + Guid.NewGuid().ToString("N"));

    public ConfigurationCompatibilityTests() => Directory.CreateDirectory(root);
    public void Dispose() { Directory.Delete(FixturePath(root), true); if (Directory.Exists(root + ".journal")) Directory.Delete(root + ".journal", true); }

    [Theory]
    [InlineData("config.toml")]
    [InlineData("auth.json")]
    public async Task DanglingFileLinkIsRejectedBeforeWrites(string fileName)
    {
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var target = Path.Combine(root, "missing-target");
        var link = Path.Combine(home, fileName);
        TestCompat.CreateFileSymbolicLink(link, target);
        var service = Service();

        var error = await Assert.ThrowsAsync<SetupException>(() => service.ConfigureAsync(Configuration, new ApiCredential(Token), home));

        Assert.Contains("符号链接或重解析点", error.Message);
        Assert.False(File.Exists(target));
        Assert.Single(Directory.GetFileSystemEntries(home));
        Assert.Equal(FileAttributes.ReparsePoint, File.GetAttributes(link) & FileAttributes.ReparsePoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsFilesAndBackupsHavePrivateAclAfterConfiguration(bool overwrite)
    {
        if (!RuntimeCompat.IsWindows) return;
        if (overwrite) WritePublicFixtureFiles();

        var result = await Service().ConfigureAsync(Configuration, new ApiCredential(Token), root);

        foreach (var name in new[] { "config.toml", "auth.json" })
            AssertPrivateFile(Path.Combine(root, name));
        AssertPrivateDirectory(result.BackupDirectory);
        foreach (var path in Directory.GetFiles(result.BackupDirectory)) AssertPrivateFile(path);
    }

    [Fact]
    public async Task WindowsRollbackRestoresContentsWithoutRestoringBroadReadAccess()
    {
        if (!RuntimeCompat.IsWindows) return;
        WritePublicFixtureFiles();
        var progress = new ImmediateProgress(step =>
        {
            if (step.Stage != "config-written") return;
            Assert.Contains("example-model", File.ReadAllText(Path.Combine(root, "config.toml")));
            Assert.Contains(Token, File.ReadAllText(Path.Combine(root, "auth.json")));
            throw new IOException("simulated failure after both replacements");
        });

        await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), root, progress));

        var configPath = Path.Combine(root, "config.toml");
        var authPath = Path.Combine(root, "auth.json");
        Assert.Equal(OriginalConfig, File.ReadAllText(configPath));
        Assert.Equal(OriginalAuth, File.ReadAllText(authPath));
        AssertPrivateFile(configPath);
        AssertPrivateFile(authPath);
        Assert.False(File.Exists(Path.Combine(root, ".codex-global-state.json")));
        var backup = Assert.Single(Directory.GetDirectories(root, "ai-desktop-setup-backup.*"));
        Assert.Equal(OriginalConfig, File.ReadAllText(Path.Combine(backup, "config.toml")));
        Assert.Equal(OriginalAuth, File.ReadAllText(Path.Combine(backup, "auth.json")));
        AssertPrivateDirectory(backup);
        foreach (var path in Directory.GetFiles(backup)) AssertPrivateFile(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLongConfigurationPathSupportsCommitAndRollback(bool failAfterAccountWrites)
    {
        if (!RuntimeCompat.IsWindows) return;
        var home = Path.Combine(root, new string('a', 65), new string('b', 65), new string('c', 65), new string('d', 65));
        // Fixture I/O uses extended paths even when the machine-wide long-path
        // policy is off; pass the ordinary configured path to the actual service.
        Directory.CreateDirectory(FixturePath(home));
        var configPath = FixturePath(Path.Combine(home, "config.toml"));
        var authPath = FixturePath(Path.Combine(home, "auth.json"));
        Assert.True(home.Length > 300);
        File.WriteAllText(configPath, OriginalConfig);
        File.WriteAllText(authPath, OriginalAuth);
        var reachedLastWrite = false;
        var progress = new ImmediateProgress(step =>
        {
            if (step.Stage != "config-written") return;
            reachedLastWrite = true;
            Assert.Contains("example-model", File.ReadAllText(configPath));
            Assert.Contains(Token, File.ReadAllText(authPath));
            if (failAfterAccountWrites) throw new IOException("simulated last-file failure in a long path");
        });

        if (failAfterAccountWrites)
        {
            await Assert.ThrowsAsync<SetupException>(() => Service().ConfigureAsync(Configuration, new ApiCredential(Token), home, progress));
            Assert.Equal(OriginalConfig, File.ReadAllText(configPath));
            Assert.Equal(OriginalAuth, File.ReadAllText(authPath));
        }
        else
        {
            await Service().ConfigureAsync(Configuration, new ApiCredential(Token), home, progress);
            Assert.Contains("model = \"example-model\"", File.ReadAllText(configPath));
            Assert.Contains(Token, File.ReadAllText(authPath));
        }
        // A failure before this stage would not exercise the replacement/rollback path.
        Assert.True(reachedLastWrite);
        AssertPrivateFile(configPath);
        AssertPrivateFile(authPath);
    }

    private static string FixturePath(string path) => RuntimeCompat.IsWindows ? @"\\?\" + path : path;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void WritePublicFixtureFiles()
    {
        foreach (var item in new[] { new { Name = "config.toml", Content = OriginalConfig }, new { Name = "auth.json", Content = OriginalAuth } })
        {
            var path = Path.Combine(root, item.Name);
            File.WriteAllText(path, item.Content);
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(CurrentUser);
            security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            Assert.Contains(new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => ((SecurityIdentifier)rule.IdentityReference).IsWellKnown(WellKnownSidType.WorldSid));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!; }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertPrivateFile(string path) => AssertPrivate(new FileInfo(path).GetAccessControl());
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertPrivateDirectory(string path) => AssertPrivate(new DirectoryInfo(path).GetAccessControl());
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertPrivate(FileSystemSecurity security)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow).ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(CurrentUser, rule.IdentityReference));
        Assert.Contains(rules, rule => (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    private static readonly CodexConfiguration Configuration = new("https://gateway.example/v1", "example-model", "high");
    private ConfigurationService Service() => new(new ConfigurationJournal(root + ".journal", new ResumeId(Guid.NewGuid())));
    private sealed class ImmediateProgress(Action<SetupProgress> report) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => report(value);
    }
}
