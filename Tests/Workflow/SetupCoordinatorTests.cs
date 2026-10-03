using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Core.Workflow;
using AiDesktopSetup.Tests.Protocol;
namespace AiDesktopSetup.Tests.Workflow;
public sealed class SetupCoordinatorTests
{
    [Fact] public void BeforePasteMakesNoNetworkRequests()
    {
        var f = new Fixture(); var coordinator = f.Create();
        var preview = coordinator.Preview(ProtocolFixtures.CodeText());
        Assert.Equal(0, f.Session.Requests); Assert.Equal(0, f.Store.Creates);
        Assert.Equal(ProtocolFixtures.Code().ApiBaseUrl, preview.ApiBaseUrl);
    }
    [Fact] public async Task PersistFailureMakesZeroRequests()
    {
        var f = new Fixture(); f.Store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.Create().AuthenticateAsync(ProtocolFixtures.Code(), default));
        Assert.Equal(0, f.Session.Requests);
    }
    [Fact] public async Task LostBootstrapResponseResumesSameClaim()
    {
        var f = new Fixture(); f.Session.LoseBootstrap = true;
        await Assert.ThrowsAsync<IOException>(() => f.Create().AuthenticateAsync(ProtocolFixtures.Code(), default));
        var original = f.Store.Record!.Claim; f.Clock.UtcNow += TimeSpan.FromMinutes(11);
        await f.Create().ResumeAsync(original.ResumeId, default);
        Assert.Equal(2, f.Session.Claims.Count);
        Assert.All(f.Session.Claims, c => { Assert.Equal(original.ClaimId, c.ClaimId); Assert.Equal(original.ResumeSecret.ToBearer(), c.ResumeSecret.ToBearer()); });
    }
    [Fact] public async Task LostCompleteResponseOnlyRetriesReceipt()
    {
        var f = new Fixture(); f.Session.LoseComplete = true; var c = f.Create();
        var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        var pending = await c.InstallAndConfigureAsync(setup, null, default);
        Assert.Equal(ReceiptState.Pending, pending.ReceiptState); Assert.NotNull(f.Store.Record);
        f.Installer.InstallCalls = 0; f.Configuration.WriteCalls = 0; f.Session.CompleteCalls = 0;
        var outcome = await f.Create().ResumeAsync(setup.ResumeId, default);
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls);
        Assert.Equal(1, f.Session.CompleteCalls); Assert.Equal(ReceiptState.Acknowledged, outcome.ReceiptState);
        Assert.Null(f.Store.Record); Assert.Equal(1, f.Configuration.Abandons);
    }
    [Fact] public async Task CanceledSetupNeverRevokesKey()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        await c.CancelAsync(setup.ResumeId, default);
        Assert.Equal(1, f.Session.CancelCalls); Assert.Equal(0, f.Session.CredentialsCalls);
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Null(f.Store.Record); Assert.Equal(1, f.Configuration.Abandons);
        await Assert.ThrowsAsync<SetupException>(() => c.ResumeAsync(setup.ResumeId, default));
    }
    [Fact] public async Task ReceiptPendingAndSignOutRequiredCannotLaunch()
    {
        var f = new Fixture(); f.Installer.State = new(true, true, true); f.Session.LoseComplete = true;
        var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        var pending = await c.InstallAndConfigureAsync(setup, null, default);
        Assert.Equal(ReceiptState.Pending, pending.ReceiptState); Assert.True(pending.NeedsSignOut); Assert.True(pending.NeedsRestart); Assert.False(pending.CanLaunch);
        var resumed = await f.Create().ResumeAsync(setup.ResumeId, default);
        Assert.True(resumed.NeedsSignOut); Assert.True(resumed.NeedsRestart); Assert.False(resumed.CanLaunch);
    }
    [Fact] public async Task AuthorizationFailurePreventsInstallAndWrite()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Session.Deny = true;
        await Assert.ThrowsAsync<ProtocolHttpException>(() => c.InstallAndConfigureAsync(setup, null, default));
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls);
    }
    [Fact] public async Task CommittedJournalWinsOverStaleStage()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Configuration.State = ConfigurationRecoveryState.Committed;
        await c.ResumeAsync(setup.ResumeId, default);
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls); Assert.Equal(1, f.Session.CompleteCalls);
    }
    [Fact] public async Task ConflictingJournalNeverInstallsOrWrites()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Configuration.State = ConfigurationRecoveryState.Conflict;
        await Assert.ThrowsAsync<SetupException>(() => c.ResumeAsync(setup.ResumeId, default));
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls);
    }
    [Fact] public void HelperNeverReceivesCredentials()
    {
        var request = new InstallHelperRequest(Path.Combine(Path.GetTempPath(), "package.msix"), Path.Combine(Path.GetTempPath(), "license.xml"), new string('a', 64), new string('b', 64), "codex-desktop", InstallationOperation.Provision);
        var wire = request.ToWire();
        Assert.DoesNotContain("credential", wire.ToLowerInvariant()); Assert.DoesNotContain("session", wire.ToLowerInvariant());
        Assert.DoesNotContain(ProtocolFixtures.Code().Credential.Value, wire);
        Assert.Equal(new[] { "AppId", "LicensePath", "LicenseSha256", "Operation", "PackagePath", "PackageSha256" }, typeof(InstallHelperRequest).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray());
    }
    [Fact] public async Task UnknownInstallationFlagsRequireFreshInspection()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Configuration.State = ConfigurationRecoveryState.Committed; f.Installer.State = new(false);
        var result = await c.ResumeAsync(setup.ResumeId, default);
        Assert.False(result.CanLaunch); Assert.Equal(1, f.Installer.InspectCalls); Assert.Equal(0, f.Installer.InstallCalls);
    }
    [Fact] public async Task MissingCommittedJournalFailsClosed()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Store.Record = f.Store.Record!.Advance(LocalStage.ConfigCommitted);
        await Assert.ThrowsAsync<SetupException>(() => c.ResumeAsync(setup.ResumeId, default));
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls);
    }
    [Fact] public async Task UnregisteredProvisioningDoesNotWriteConfiguration()
    {
        var f = new Fixture(); f.Installer.State = new(false, true); var c = f.Create();
        var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        var result = await c.InstallAndConfigureAsync(setup, null, default);
        Assert.Equal(SetupLocalState.Incomplete, result.LocalState); Assert.False(result.CanLaunch); Assert.True(result.NeedsSignOut);
        Assert.Equal(0, f.Configuration.WriteCalls); Assert.Equal(0, f.Session.CredentialsCalls); Assert.Equal(0, f.Session.CompleteCalls);
    }
    [Fact] public async Task RealStoreAndJournalReceiptRetryPreserveFilesAndCleanupOwnedStages()
    {
        var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "workflow-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedClock(); var store = new DpapiResumeStore(Path.Combine(root, "resume"), clock, new CryptoRandomSource(), new AiDesktopSetup.Tests.Recovery.TestProtection());
            var home = Path.Combine(root, "codex"); var configuration = new DesktopSetupConfiguration(home);
            var session = new Session { LoseComplete = true }; var installer = new Installer();
            SetupCoordinator Create() => new(store, session, installer, configuration, clock, new("1.0.0", "x64"));
            var setup = await Create().AuthenticateAsync(ProtocolFixtures.Code(), default);
            var first = await Create().InstallAndConfigureAsync(setup, null, default);
            Assert.Equal(ReceiptState.Pending, first.ReceiptState);
            var config = File.ReadAllBytes(Path.Combine(home, "config.toml")); var auth = File.ReadAllBytes(Path.Combine(home, "auth.json"));
            var time = File.GetLastWriteTimeUtc(Path.Combine(home, "auth.json")); installer.InstallCalls = 0;
            var result = await Create().ResumeAsync(setup.ResumeId, default);
            Assert.Equal(ReceiptState.Acknowledged, result.ReceiptState); Assert.Equal(0, installer.InstallCalls);
            Assert.Equal(config, File.ReadAllBytes(Path.Combine(home, "config.toml"))); Assert.Equal(auth, File.ReadAllBytes(Path.Combine(home, "auth.json")));
            Assert.Equal(time, File.GetLastWriteTimeUtc(Path.Combine(home, "auth.json"))); Assert.Null(store.Load(setup.ResumeId));
            Assert.Empty(Directory.GetFiles(Path.Combine(home, ".ai-desktop-setup"), "*.stage", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Path.Combine(home, ".ai-desktop-setup"), "intent.json", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task RealPersistenceFailureSendsNoBootstrap()
    {
        var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "workflow-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(); var store = new DpapiResumeStore(root, f.Clock, new CryptoRandomSource(), new AiDesktopSetup.Tests.Recovery.FailingProtection());
            var coordinator = new SetupCoordinator(store, f.Session, f.Installer, f.Configuration, f.Clock, new("1.0.0", "x64"));
            await Assert.ThrowsAsync<IOException>(() => coordinator.AuthenticateAsync(ProtocolFixtures.Code(), default));
            Assert.Equal(0, f.Session.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task ExpiryCleanupReclaimsOrphanSecretStageButPreservesUserFiles()
    {
        var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "workflow-expiry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var f = new Fixture(); var store = new DpapiResumeStore(Path.Combine(root, "resume"), f.Clock, new CryptoRandomSource(), new AiDesktopSetup.Tests.Recovery.TestProtection());
            var home = Path.Combine(root, "codex"); var configuration = new DesktopSetupConfiguration(home);
            var coordinator = new SetupCoordinator(store, f.Session, f.Installer, configuration, f.Clock, new("1.0.0", "x64"));
            var setup = await coordinator.AuthenticateAsync(ProtocolFixtures.Code(), default);
            var journal = new ConfigurationJournal(Path.Combine(home, ".ai-desktop-setup"), setup.ResumeId);
            var staged = journal.Execute(() => journal.Stage(System.Text.Encoding.UTF8.GetBytes("synthetic-orphan-key")), reclaim: false);
            var target = Path.Combine(home, "auth.json"); File.WriteAllText(target, "preserve-user-data");
            f.Clock.UtcNow += TimeSpan.FromHours(2);
            await coordinator.CleanupAsync(default);
            Assert.False(File.Exists(staged)); Assert.Equal("preserve-user-data", File.ReadAllText(target)); Assert.Null(store.Load(setup.ResumeId));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task MirrorFailureRequiresExplicitOfficialRetryAndReauthorizesBeforeConfiguration()
    {
        using var fixture = new PackageFixture();
        var f = new Fixture();
        f.Session.Snapshot = PackagePolicyTests.Session("26.928.3736.0").Snapshot;
        var downloads = new List<string>();
        var plans = new List<InstallationPlan>();
        var trust = new CountingTrust();
        var machine = PackagePolicyTests.Machine();
        var desktop = new DesktopSetupInstaller(_ => Task.FromResult(new InstallState(false)), _ => Task.FromResult(machine), async (plan, progress, ct) =>
        {
            plans.Add(plan);
            using var clients = new ProtocolHttpClients(new ArtifactSource(fixture.Path, downloads), new ArtifactSource(fixture.Path, downloads), new ArtifactSource(fixture.Path, downloads));
            var destination = Path.Combine(fixture.DirectoryPath, "attempt-" + plans.Count);
            Directory.CreateDirectory(destination);
            using var prepared = await new PackageTrustVerifier(trust).PrepareAsync(clients, plan, machine, destination, progress, ct);
            Assert.Equal("OpenAI.Codex", prepared.Package.IdentityName);
            return new InstallState(true);
        });
        var c = new SetupCoordinator(f.Store, f.Session, desktop, f.Configuration, f.Clock, new("1.0.0", "x64"));
        var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        var original = setup.Session.Snapshot.ToWire();
        await Assert.ThrowsAsync<InvalidDataException>(() => c.InstallAndConfigureAsync(setup, null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => c.ResumeAsync(setup.ResumeId, default));
        Assert.All(plans, p => Assert.True(p.IsMirror));
        Assert.Equal(0, f.Configuration.WriteCalls); Assert.Equal(0, f.Session.CredentialsCalls);
        Assert.DoesNotContain(downloads, u => u.Contains("oaistatic"));
        var result = await c.RetryOfficialAsync(setup.ResumeId, null, default);
        Assert.Equal(ReceiptState.Acknowledged, result.ReceiptState);
        Assert.False(plans.Last().IsMirror); Assert.Equal(1, trust.Calls);
        Assert.Equal(2, downloads.Count(u => u.StartsWith("https://persistent.oaistatic.com/", StringComparison.Ordinal)));
        Assert.Equal(original, f.Session.Snapshot.ToWire());
        Assert.Equal(1, f.Configuration.WriteCalls); Assert.Equal(1, f.Session.CredentialsCalls);
    }
    [Fact] public async Task ExplicitOfficialRetryStillRequiresCurrentAuthorization()
    {
        var f = new Fixture(); var c = f.Create(); var setup = await c.AuthenticateAsync(ProtocolFixtures.Code(), default);
        f.Session.Deny = true;
        await Assert.ThrowsAsync<ProtocolHttpException>(() => c.RetryOfficialAsync(setup.ResumeId, null, default));
        Assert.Equal(0, f.Installer.InstallCalls); Assert.Equal(0, f.Configuration.WriteCalls);
    }
    private sealed class CountingTrust : IPackageSignatureTrust
    {
        public int Calls;
        public void Verify(string path, FileStream file) { Calls++; }
    }
    private sealed class ArtifactSource(string package, List<string> downloads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri; downloads.Add(url);
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain(request.Headers, h => h.Key == "Cookie" || h.Key.Contains("Proof"));
            var bytes = url.EndsWith(".xml", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes("<License/>") : File.ReadAllBytes(package);
            // Mirror metadata intentionally mismatches these bytes. Official retry
            // downloads fresh bytes and still enters the production trust verifier.
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }
    private sealed class Fixture
    {
        public FixedClock Clock = new(); public Store Store; public Session Session = new(); public Installer Installer = new(); public Configuration Configuration = new();
        public Fixture() { Store = new(Clock); }
        public SetupCoordinator Create() => new(Store, Session, Installer, Configuration, Clock, new("1.0.0", "x64"));
    }
    private sealed class Store(FixedClock clock) : IResumeStore
    {
        public ResumeRecord? Record; public bool Fail; public int Creates;
        public ClaimRecord GetOrCreate(SetupCode code) { if (Fail) throw new IOException(); Creates++; return (Record ??= new(new(new(Guid.NewGuid()), Guid.NewGuid(), new(new byte[32])), code, clock.UtcNow, clock.UtcNow.AddHours(2), LocalStage.Created)).Claim; }
        public ResumeRecord? Load(ResumeId id) { if (Record != null && clock.UtcNow >= Record.ExpiresAt) Record = null; return Record?.Claim.ResumeId == id ? Record : null; }
        public void Save(ResumeRecord record) { if (Fail) throw new IOException(); Assert.NotNull(Record); Record = record; }
        public void Delete(ResumeId id) { Record = null; }
    }
    private sealed class Session : ISetupSessionClient
    {
        public SessionSnapshot Snapshot = ProtocolFixtures.Session();
        public int Requests, CompleteCalls, CancelCalls, CredentialsCalls; public bool LoseBootstrap, LoseComplete, Deny; public List<ClaimRecord> Claims = new();
        public Task<SessionSnapshot> BootstrapAsync(SetupCode code, ClaimRecord claim, ClientIdentity identity, CancellationToken ct) { Requests++; Claims.Add(claim); if (LoseBootstrap) { LoseBootstrap = false; throw new IOException(); } return Task.FromResult(Snapshot); }
        public Task<SessionSnapshot> GetSessionAsync(SessionAccess access, CancellationToken ct) { Requests++; if (Deny) throw new ProtocolHttpException(new("access_denied", "")); return Task.FromResult(Snapshot); }
        public Task<CredentialDelivery> GetCredentialsAsync(SessionAccess access, CancellationToken ct) { Requests++; CredentialsCalls++; return Task.FromResult(new CredentialDelivery(access.SessionId, access.ApiBaseUrl, CredentialDeliveryMode.Server, new("synthetic-test-key"))); }
        public Task<SessionReceipt> CompleteAsync(SessionAccess access, CancellationToken ct) { Requests++; CompleteCalls++; if (LoseComplete) { LoseComplete = false; throw new IOException(); } return Task.FromResult(new SessionReceipt(1, access.SessionId, "completed")); }
        public Task<SessionReceipt> CancelAsync(SessionAccess access, CancellationToken ct) { Requests++; CancelCalls++; return Task.FromResult(new SessionReceipt(1, access.SessionId, "canceled")); }
        public Task<byte[]?> GetLogoAsync(SessionAccess access, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }
    private sealed class Installer : ISetupInstaller
    {
        public int InstallCalls, InspectCalls; public InstallState State = new(true);
        public Task<InstallState> InspectAsync(CancellationToken ct) { InspectCalls++; return Task.FromResult(State); }
        public Task<InstallState> InstallAsync(ValidatedSession session, IProgress<SetupProgress>? progress, CancellationToken ct, PackageSource source = PackageSource.Session) { InstallCalls++; return Task.FromResult(State); }
    }
    private sealed class Configuration : ISetupConfiguration
    {
        public int WriteCalls, Abandons; public ConfigurationRecoveryState State;
        public ConfigurationRecoveryState Inspect(ResumeId id) => State;
        public void Recover(ResumeId id) { State = ConfigurationRecoveryState.Committed; }
        public Task ConfigureAsync(ResumeId id, ValidatedSession session, ApiCredential credential, IProgress<SetupProgress>? progress, CancellationToken ct) { WriteCalls++; State = ConfigurationRecoveryState.Committed; return Task.CompletedTask; }
        public void Abandon(ResumeId id) { Abandons++; }
        public void Cleanup(Func<ResumeId, bool> hasLiveRecord) { }
    }
}
