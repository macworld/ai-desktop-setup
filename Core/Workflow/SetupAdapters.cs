using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using System.Runtime.Versioning;
namespace AiDesktopSetup.Core.Workflow;
[SupportedOSPlatform("windows")]
public sealed class DesktopSetupInstaller : ISetupInstaller
{
    private readonly WindowsInstaller installer = new();
    public Task<InstallState> InspectAsync(CancellationToken ct) => installer.InspectAsync(ct);
    public async Task<InstallState> InstallAsync(ValidatedSession session, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var machine = await installer.InspectMachineAsync(ct).ConfigureAwait(false);
        return await installer.InstallAsync(CodexOfficialPolicy.Resolve(session, machine), progress, ct).ConfigureAwait(false);
    }
}
public sealed class DesktopSetupConfiguration : ISetupConfiguration
{
    private readonly string home, root;
    public DesktopSetupConfiguration(string originalUserCodexHome)
    {
        home = Path.GetFullPath(originalUserCodexHome);
        root = Path.Combine(home, ".ai-desktop-setup");
    }
    private ConfigurationJournal Journal(ResumeId id) => new(root, id);
    public ConfigurationRecoveryState Inspect(ResumeId id) => Journal(id).Inspect();
    public void Recover(ResumeId id) => Journal(id).Recover();
    public async Task ConfigureAsync(ResumeId id, ValidatedSession session, ApiCredential credential, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var client = session.Snapshot.Client;
        var result = await new ConfigurationService(Journal(id)).ConfigureAsync(new(session.Snapshot.ApiBaseUrl, client.Model, client.ReasoningEffort), credential, home, progress, ct).ConfigureAwait(false);
        if (!result.Committed) throw new SetupException("Configuration has not been committed.");
    }
    public void Abandon(ResumeId id) => Journal(id).Abandon();
    public void Cleanup(Func<ResumeId,bool> hasLiveRecord) => ConfigurationJournal.CleanupOrphans(root, hasLiveRecord);
}
