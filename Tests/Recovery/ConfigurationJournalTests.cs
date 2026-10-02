using System.Text;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;

namespace AiDesktopSetup.Tests.Recovery;
public sealed class ConfigurationJournalTests : IDisposable
{
    private readonly string root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "configuration-journal-" + Guid.NewGuid().ToString("N"));
    private readonly ResumeId id = new(Guid.NewGuid());
    private string Home => Path.Combine(root, "home");
    private ConfigurationJournal Journal() => new(Path.Combine(root,"journal"), id);
    public ConfigurationJournalTests() { Directory.CreateDirectory(Home); ResumeFileSecurity.EnsureDirectory(Path.Combine(root,"journal")); File.WriteAllText(Path.Combine(Home,"config.toml"), "model = \"old\"\n"); File.WriteAllText(Path.Combine(Home,"auth.json"), "{\"keep\":true}"); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ConfigurationIntent Intent()
    {
        var backup = Path.Combine(Home,"ai-desktop-setup-backup."+Guid.NewGuid().ToString("N")); ResumeFileSecurity.EnsureDirectory(backup);
        var files = new List<ConfigurationFileIntent>();
        foreach (var name in new[] {"config.toml","auth.json"})
        {
            var target = Path.Combine(Home,name); var original = File.ReadAllBytes(target); var staged = Path.Combine(Journal().StagingDirectory,Guid.NewGuid().ToString("N")+".stage");
            ResumeFileSecurity.EnsureDirectory(Journal().StagingDirectory); var next = Encoding.UTF8.GetBytes(name == "auth.json" ? "{\"OPENAI_API_KEY\":\"FAKE_new_key~+/==\"}" : "model = \"new\"\n");
            ResumeFileSecurity.WriteNew(staged,next); var saved = Path.Combine(backup,name); ResumeFileSecurity.WriteNew(saved,original);
            files.Add(new(target,TestCompat.Sha256(original),TestCompat.Sha256(next),saved,staged));
        }
        return new(backup,files);
    }
    [Fact] public void CrashBetweenReplacementsRecoversIntent()
    {
        var journal = Journal(); var intent = Intent(); journal.Prepare(intent);
        File.WriteAllBytes(intent.Files[0].TargetPath,File.ReadAllBytes(intent.Files[0].StagedPath));
        Assert.Equal(ConfigurationRecoveryState.Prepared,Journal().Inspect());
        var result = Journal().Recover(); Assert.True(result!.Committed); Assert.Equal(intent.BackupDirectory,result.BackupDirectory);
        Assert.Equal("model = \"new\"\n",File.ReadAllText(Path.Combine(Home,"config.toml"))); Assert.Contains("FAKE_new_key",File.ReadAllText(Path.Combine(Home,"auth.json")));
        Assert.Equal(ConfigurationRecoveryState.Committed,Journal().Inspect());
    }
    [Fact] public void CrashBeforeStageSaveDoesNotRewrite()
    {
        var journal = Journal(); var intent = Intent(); journal.Prepare(intent);
        foreach(var file in intent.Files) File.WriteAllBytes(file.TargetPath,File.ReadAllBytes(file.StagedPath));
        foreach(var file in intent.Files) File.Delete(file.StagedPath);
        var times = intent.Files.Select(f => File.GetLastWriteTimeUtc(f.TargetPath)).ToArray();
        Assert.Equal(ConfigurationRecoveryState.Committed,Journal().Inspect()); Assert.True(Journal().Recover()!.Committed);
        Assert.Equal(times,intent.Files.Select(f=>File.GetLastWriteTimeUtc(f.TargetPath)).ToArray());
    }
    [Fact] public void ExternalEditPreventsOverwrite()
    {
        var intent = Intent(); Journal().Prepare(intent); var target = intent.Files[0].TargetPath; var external = Encoding.UTF8.GetBytes("# external\nmodel = \"outside\"\n"); File.WriteAllBytes(target,external);
        Assert.Equal(ConfigurationRecoveryState.Conflict,Journal().Inspect()); Assert.Throws<SetupException>(()=>Journal().Recover()); Assert.Equal(external,File.ReadAllBytes(target));
        Assert.True(File.Exists(intent.Files[0].BackupPath));
    }
    [Fact] public void StageTamperingConflictsWithoutChangingTargets()
    {
        var intent = Intent(); Journal().Prepare(intent); File.WriteAllText(intent.Files[1].StagedPath,"tampered");
        Assert.Equal(ConfigurationRecoveryState.Conflict,Journal().Inspect()); Assert.Throws<SetupException>(()=>Journal().Recover()); Assert.Equal("{\"keep\":true}",File.ReadAllText(Path.Combine(Home,"auth.json")));
    }
    [Fact] public void AbandonReclaimsStagesAndOrphansButPreservesUserFilesAndBackups()
    {
        var intent = Intent(); Journal().Prepare(intent); ResumeFileSecurity.WriteNew(Path.Combine(Journal().StagingDirectory,"orphan.stage"),Encoding.UTF8.GetBytes("FAKE_key"));
        Journal().Abandon(); Assert.Equal(ConfigurationRecoveryState.None,Journal().Inspect()); Assert.Empty(Directory.GetFiles(Journal().StagingDirectory,"*.stage"));
        Assert.Equal("{\"keep\":true}",File.ReadAllText(Path.Combine(Home,"auth.json"))); Assert.True(File.Exists(intent.Files[1].BackupPath));
    }
    [Fact] public void OrphanWithoutIntentIsReclaimedOnNextAccess()
    {
        ResumeFileSecurity.EnsureDirectory(Journal().StagingDirectory); var orphan = Path.Combine(Journal().StagingDirectory,"interrupted.stage"); ResumeFileSecurity.WriteNew(orphan,Encoding.UTF8.GetBytes("FAKE_key"));
        Assert.Equal(ConfigurationRecoveryState.None,Journal().Inspect()); Assert.False(File.Exists(orphan));
    }
    [Fact] public void CleanupRejectsSymlinkWithoutDeletingItsTarget()
    {
        var intent = Intent(); Journal().Prepare(intent); var target = Path.Combine(root,"outside"); File.WriteAllText(target,"keep"); var link=Path.Combine(Journal().StagingDirectory,"orphan.stage"); TestCompat.CreateFileSymbolicLink(link,target);
        Assert.Throws<IOException>(()=>Journal().Abandon()); Assert.Equal("keep",File.ReadAllText(target));
    }
    [Fact] public async Task ConfigurationServicePersistsAndVerifiesIntent()
    {
        var result = await new ConfigurationService(Journal()).ConfigureAsync(new("https://gateway.example/Tenant/v1/", "example-model",null),new ApiCredential("FAKE~+/=="),Home);
        Assert.True(result.Committed); Assert.Equal(ConfigurationRecoveryState.Committed,Journal().Inspect()); Assert.Empty(Directory.GetFiles(Journal().StagingDirectory,"*.stage"));
        var backup = result.BackupDirectory;
        var second = await new ConfigurationService(Journal()).ConfigureAsync(new("https://gateway.example/Tenant/v1/", "other-model",null),new ApiCredential("FAKE_other"),Home);
        Assert.Equal(backup,second.BackupDirectory); Assert.DoesNotContain("other-model",File.ReadAllText(Path.Combine(Home,"config.toml")));
    }

    #if !NETFRAMEWORK
    [Theory]
    [InlineData("config-before-prepare", false)]
    [InlineData("config-prepared", false)]
    [InlineData("writing-config", false)]
    [InlineData("config-replaced", false)]
    [InlineData("writing-auth", false)]
    [InlineData("auth-replaced", true)]
    [InlineData("config-written", true)]
    [InlineData("config-before-commit", true)]
    [InlineData("config-committed", true)]
    public async Task ProcessTerminationAtConfigurationBoundariesRecoversWithoutRewritingCommittedTargets(string stage, bool alreadyWritten)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach(var arg in new[] {typeof(ConfigurationJournalTests).Assembly.Location,"configuration-crash",root,id.ToString(),stage}) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        await TestCompat.WithTimeout(process.WaitForExitAsync(),TimeSpan.FromSeconds(20));
        Assert.Equal(73,process.ExitCode);
        var times = new[] {"config.toml","auth.json"}.Select(n=>File.GetLastWriteTimeUtc(Path.Combine(Home,n))).ToArray();
        if (stage == "config-before-prepare")
        { Assert.Equal(ConfigurationRecoveryState.None,Journal().Inspect()); Assert.Equal("model = \"old\"\n",File.ReadAllText(Path.Combine(Home,"config.toml"))); return; }
        Assert.Equal(alreadyWritten ? ConfigurationRecoveryState.Committed : ConfigurationRecoveryState.Prepared,Journal().Inspect());
        Assert.True(Journal().Recover()!.Committed); Assert.Contains("example-model",File.ReadAllText(Path.Combine(Home,"config.toml")));
        Assert.Contains("FAKE_worker_key",File.ReadAllText(Path.Combine(Home,"auth.json")));
        if (alreadyWritten) Assert.Equal(times,new[] {"config.toml","auth.json"}.Select(n=>File.GetLastWriteTimeUtc(Path.Combine(Home,n))).ToArray());
    }
    #endif

    [Theory] [InlineData(true)] [InlineData(false)]
    public void CleanupOrphansRetainsLiveAndReclaimsDeadRecords(bool live)
    {
        var intent=Intent();Journal().Prepare(intent);var called=false;
        ConfigurationJournal.CleanupOrphans(Path.Combine(root,"journal"),candidate=>{called=true;Assert.Equal(id,candidate);return live;});Assert.True(called);
        Assert.Equal(live?ConfigurationRecoveryState.Prepared:ConfigurationRecoveryState.None,Journal().Inspect());
        Assert.Equal(live,File.Exists(intent.Files[1].StagedPath));Assert.True(File.Exists(intent.Files[1].BackupPath));
    }
    [Fact] public void CleanupLivenessFailurePreservesRecordAndDoesNotHoldLockDuringCallback()
    {
        var intent=Intent();Journal().Prepare(intent);
        Assert.Throws<IOException>(()=>ConfigurationJournal.CleanupOrphans(Path.Combine(root,"journal"),candidate=>{Assert.Equal(ConfigurationRecoveryState.Prepared,Journal().Inspect());throw new IOException("resume unavailable");}));
        Assert.Equal(ConfigurationRecoveryState.Prepared,Journal().Inspect());Assert.True(File.Exists(intent.Files[1].StagedPath));
    }
    [Fact] public void CleanupRejectsUnownedAndReparseDirectories()
    {
        Journal().Inspect();var outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"keep"),"keep");
        var link=Path.Combine(root,"journal",Guid.NewGuid().ToString("N"));
#if !NETFRAMEWORK
        Directory.CreateSymbolicLink(link,outside);
        Assert.Throws<IOException>(()=>ConfigurationJournal.CleanupOrphans(Path.Combine(root,"journal"),_=>false));Assert.Equal("keep",File.ReadAllText(Path.Combine(outside,"keep")));Directory.Delete(link);
#endif
        Directory.CreateDirectory(Path.Combine(root,"journal","unowned"));
        ConfigurationJournal.CleanupOrphans(Path.Combine(root,"journal"),_=>false);Assert.True(Directory.Exists(Path.Combine(root,"journal","unowned")));
    }
    [Fact] public void PersistedIntentIsSecretFreeAndResumeBound()
    {
        var intent = Intent(); Journal().Prepare(intent);
        foreach(var path in Directory.GetFiles(Journal().StagingDirectory,"*.json")) Assert.DoesNotContain("FAKE_new_key",File.ReadAllText(path));
        var other = new ConfigurationJournal(Path.Combine(root,"journal"),new ResumeId(Guid.NewGuid())); Assert.Equal(ConfigurationRecoveryState.None,other.Inspect());
        Journal().Abandon(); Assert.True(File.Exists(intent.Files[1].BackupPath));
    }
}
