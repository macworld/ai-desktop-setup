using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
namespace AiDesktopSetup.Core.Workflow;
public enum PackageSource { Session, Official }
public enum SetupLocalState { Completed, Incomplete, Canceled }
public enum ReceiptState { None, Pending, Acknowledged }
public sealed record SetupOutcome(SetupLocalState LocalState, ReceiptState ReceiptState, bool NeedsRestart, bool NeedsSignOut, bool CanLaunch);
public interface ISetupInstaller
{
    Task<InstallState> InspectAsync(CancellationToken ct);
    Task<InstallState> InstallAsync(ValidatedSession session, IProgress<SetupProgress>? progress, CancellationToken ct, PackageSource source = PackageSource.Session);
}
public interface ISetupConfiguration
{
    ConfigurationRecoveryState Inspect(ResumeId id);
    void Recover(ResumeId id);
    Task ConfigureAsync(ResumeId id, ValidatedSession session, ApiCredential credential, IProgress<SetupProgress>? progress, CancellationToken ct);
    void Abandon(ResumeId id);
    void Cleanup(Func<ResumeId,bool> hasLiveRecord);
}
/// <summary>Serializes local lifecycle and cleanup across coordinator instances. The store remains expiry authority.</summary>
public sealed class SetupCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IResumeStore store;
    private readonly ISetupSessionClient sessions;
    private readonly ISetupInstaller installer;
    private readonly ISetupConfiguration configuration;
    private readonly SessionBindingValidator validator;
    private readonly ClientIdentity identity;
    public SetupCoordinator(IResumeStore store, ISetupSessionClient sessions, ISetupInstaller installer, ISetupConfiguration configuration, IClock clock, ClientIdentity identity)
    { this.store = store; this.sessions = sessions; this.installer = installer; this.configuration = configuration; this.identity = identity; validator = new(clock, CodexOfficialPolicy.SessionCapabilities); }
    public SetupPreview Preview(string code)
    {
        var parsed = SetupCodeParser.Parse(code);
        return new(parsed, new Uri(parsed.SetupBaseUrl).GetLeftPart(UriPartial.Authority), parsed.ApiBaseUrl, parsed.ServiceName);
    }
    public async Task CleanupAsync(CancellationToken ct)
    { await Gate.WaitAsync(ct).ConfigureAwait(false); try { Cleanup(); } finally { Gate.Release(); } }
    private void Cleanup() => configuration.Cleanup(id => store.Load(id) != null);
    private ResumeRecord Load(ResumeId id) => store.Load(id) ?? throw new SetupException("Recovery expired or was ended. Paste a new setup code.");
    private SessionAccess Access(ResumeRecord record, ValidatedSession session) => record.Code.CredentialType == CredentialType.ApiKey
        ? new(session, record.Claim.ResumeSecret, record.Code.Credential) : new(session, record.Claim.ResumeSecret);
    public async Task<AuthenticatedSetup> AuthenticateAsync(SetupCode code, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { Cleanup(); var claim = store.GetOrCreate(code); return await Authorize(Load(claim.ResumeId), ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    private async Task<AuthenticatedSetup> Authorize(ResumeRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = record.Snapshot == null
            ? await sessions.BootstrapAsync(record.Code, record.Claim, identity, ct).ConfigureAwait(false)
            : await sessions.GetSessionAsync(Access(record, validator.Validate(record.Code, record.Snapshot, record.Snapshot)), ct).ConfigureAwait(false);
        var validated = validator.Validate(record.Code, snapshot, record.Snapshot);
        record = record.Authenticate(validated); store.Save(record);
        return new(record.Claim.ResumeId, Access(record, validated), validated);
    }
    public async Task<SetupOutcome> InstallAndConfigureAsync(AuthenticatedSetup setup, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { Cleanup(); return await Run(setup.ResumeId, progress, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    public async Task<SetupOutcome> ResumeAsync(ResumeId id, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { Cleanup(); return await Run(id, null, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    /// <summary>One user-selected attempt; does not change the authenticated snapshot or future ordinary retries.</summary>
    public async Task<SetupOutcome> RetryOfficialAsync(ResumeId id, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { Cleanup(); return await Run(id, progress, ct, PackageSource.Official).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    private async Task<SetupOutcome> Run(ResumeId id, IProgress<SetupProgress>? progress, CancellationToken ct, PackageSource source = PackageSource.Session)
    {
        var setup = await Authorize(Load(id), ct).ConfigureAwait(false);
        var record = Load(id);
        var journal = configuration.Inspect(id);
        if (journal == ConfigurationRecoveryState.Conflict) throw new SetupException("Configuration changed outside setup. Resolve the private backup conflict before continuing.");
        var state = record.Installation;
        if (journal == ConfigurationRecoveryState.None && record.LocalStage >= LocalStage.ConfigCommitted)
            throw new SetupException("Committed configuration evidence is missing. Manual recovery is required.");
        if (journal == ConfigurationRecoveryState.None)
        {
            progress?.Report(new("install", "Installing or checking the official desktop client…"));
            state = await installer.InstallAsync(setup.Session, progress, ct, source).ConfigureAwait(false);
            record = Load(id).WithInstallation(state); store.Save(record);
            if (!state.Installed) return Outcome(SetupLocalState.Incomplete, ReceiptState.None, state);
            record = record.Advance(LocalStage.Installed); store.Save(record);
        }
        // Authorization is checked again after package work and before recovering/writing any credentials.
        setup = await Authorize(Load(id), ct).ConfigureAwait(false);
        if (journal == ConfigurationRecoveryState.None)
        {
            var credential = await sessions.GetCredentialsAsync(setup.Access, ct).ConfigureAwait(false);
            await configuration.ConfigureAsync(id, setup.Session, credential.Credential, progress, ct).ConfigureAwait(false);
        }
        else configuration.Recover(id);
        record = Load(id).Advance(LocalStage.ConfigCommitted);
        // Absence of saved flags is not evidence of registration or launch readiness.
        state ??= await installer.InspectAsync(ct).ConfigureAwait(false);
        record = record.WithInstallation(state); store.Save(record);
        try
        {
            await sessions.CompleteAsync(setup.Access, ct).ConfigureAwait(false);
            End(id);
            return Outcome(SetupLocalState.Completed, ReceiptState.Acknowledged, state);
        }
        catch (Exception error) when (error is IOException || error is HttpRequestException || error is OperationCanceledException
            || error is ProtocolHttpException http && (http.Error.Code == "temporarily_unavailable" || http.Error.Code == "rate_limited"))
        { return Outcome(SetupLocalState.Completed, ReceiptState.Pending, state); }
    }
    private static SetupOutcome Outcome(SetupLocalState local, ReceiptState receipt, InstallState state) => new(local, receipt, state.NeedsRestart, state.NeedsSignOut,
        local == SetupLocalState.Completed && receipt == ReceiptState.Acknowledged && state.Installed && !state.NeedsRestart && !state.NeedsSignOut);
    public async Task CancelAsync(ResumeId id, CancellationToken ct)
    {
        // Cancellation/end must still reclaim local secrets even when its network request fails.
        await Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Cleanup(); var record = store.Load(id);
            if (record == null) return;
            try
            {
                if (record.Snapshot != null)
                    await sessions.CancelAsync(Access(record, validator.Validate(record.Code, record.Snapshot, record.Snapshot)), ct).ConfigureAwait(false);
            }
            finally { End(id); }
        }
        finally { Gate.Release(); }
    }
    private void End(ResumeId id) { configuration.Abandon(id); store.Delete(id); }
}
