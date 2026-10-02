using System.Text;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;

namespace AiDesktopSetup.Tests.Recovery;
public sealed class ResumeStoreTests : IDisposable
{
    private readonly string root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "setup-resume-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock = new();
    private DpapiResumeStore Store(IResumeProtection? protection = null) => new(root, clock, new CryptoRandomSource(), protection ?? new TestProtection());
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Theory] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void InstallationRequirementsSurviveAuthenticationAdvanceAndRoundTrip(bool restart, bool signOut)
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code());
        var session = ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session());
        var record = store.Load(claim.ResumeId)!.WithInstallation(new(true, signOut, restart)).Authenticate(session).Advance(LocalStage.ConfigCommitted);
        store.Save(record);
        var loaded = Store().Load(claim.ResumeId)!;
        Assert.Equal(restart, loaded.Installation!.NeedsRestart); Assert.Equal(signOut, loaded.Installation.NeedsSignOut);
        Assert.True(loaded.Installation.Installed); Assert.Equal(LocalStage.ConfigCommitted, loaded.LocalStage);
        Assert.Contains(claim.ResumeId, Store().ListAvailable());
    }
    [Fact] public void LegacyRecordAbsenceDoesNotInventInstallationReadiness()
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code());
        Assert.Null(Store().Load(claim.ResumeId)!.Installation);
    }
    [Fact] public void ReusesClaimAfterCrash()
    {
        var first = Store().GetOrCreate(ProtocolFixtures.Code()); var recovered = Store().GetOrCreate(ProtocolFixtures.Code());
        Assert.Equal(first.ResumeId, recovered.ResumeId); Assert.Equal(first.ClaimId, recovered.ClaimId);
        Assert.Equal(first.ResumeSecret.Bytes, recovered.ResumeSecret.Bytes); Assert.Equal(32, recovered.ResumeSecret.Bytes.Length);
        var bytes = first.ResumeSecret.Bytes; bytes[0] ^= 255; Assert.NotEqual(bytes, first.ResumeSecret.Bytes);
        Assert.Equal(4, first.ClaimId.ToByteArray()[7] >> 4);
    }
    [Fact] public void ExpiredRecordIsDeleted()
    {
        var first = Store().GetOrCreate(ProtocolFixtures.Code()); clock.UtcNow = clock.UtcNow.AddHours(2);
        Assert.Null(Store().Load(first.ResumeId)); Assert.Empty(Directory.GetFiles(root, "*.resume"));
        Assert.NotEqual(first.ClaimId, Store().GetOrCreate(ProtocolFixtures.Code()).ClaimId);
    }
    [Fact] public void PersistenceFailureDoesNotReturnClaim()
    {
        Assert.Throws<IOException>(() => Store(new FailingProtection()).GetOrCreate(ProtocolFixtures.Code()));
        Assert.Empty(Directory.GetFiles(root, "*.resume"));
    }
    [Fact] public void SecretFormattingIsRedacted()
    {
        var claim = Store().GetOrCreate(ProtocolFixtures.Code()); var record = Store().Load(claim.ResumeId)!;
        foreach (var secret in new object[] { claim.ResumeSecret, ProtocolFixtures.Code().Credential, ProtocolFixtures.Code(), claim, record })
        { Assert.DoesNotContain(ProtocolFixtures.CodeText(), secret.ToString()); Assert.DoesNotContain(ProtocolFixtures.Code().Credential.Value, secret.ToString()); }
        Assert.Equal("[redacted]", claim.ResumeSecret.ToString()); Assert.Equal("[redacted]", ProtocolFixtures.Code().Credential.ToString());
        Assert.DoesNotContain(claim.ResumeSecret.ToBearer(), System.Text.Json.JsonSerializer.Serialize(claim.ResumeSecret));
    }
    [Fact] public void AuthenticatedExpiryIsFixedAndStateSurvivesCrash()
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code()); var record = store.Load(claim.ResumeId)!;
        store.Save(record.Authenticate(ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(), null)));
        var recovered = Store().Load(claim.ResumeId)!; Assert.Equal(LocalStage.Authenticated, recovered.LocalStage);
        Assert.Equal(ProtocolFixtures.Session().ExpiresAt, recovered.ExpiresAt); Assert.NotNull(recovered.Snapshot);
        var json = ProtocolFixtures.SessionJson(); json["expires_at"] = "2030-01-01T01:30:00Z";
        Assert.Throws<ProtocolException>(() => store.Save(recovered.Authenticate(ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(json), null))));
    }
    [Fact] public void HalfWrittenTemporaryFileIsNotReadAsRecord()
    {
        var claim = Store().GetOrCreate(ProtocolFixtures.Code()); ResumeFileSecurity.WriteNew(Path.Combine(root, "interrupted.tmp"), Encoding.UTF8.GetBytes("half-written"));
        Assert.Equal(claim.ClaimId, Store().GetOrCreate(ProtocolFixtures.Code()).ClaimId);
    }
    [Fact] public void RejectsReparsePointWithoutReadingTarget()
    {
        var claim = Store().GetOrCreate(ProtocolFixtures.Code()); var file = Directory.GetFiles(root, "*.resume").Single();
        var outside = root + ".outside"; File.WriteAllText(outside, "keep"); File.Delete(file);
        TestCompat.CreateFileSymbolicLink(file, outside);
        try { Assert.Throws<IOException>(() => Store().Load(claim.ResumeId)); Assert.Equal("keep", File.ReadAllText(outside)); }
        finally { File.Delete(outside); }
    }
    [Fact] public void FailedSavePreservesTheDurableClaim()
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code()); var record = store.Load(claim.ResumeId)!;
        Assert.Throws<IOException>(() => Store(new FailOnProtect()).Save(record.Authenticate(ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(), null))));
        var recovered = Store().Load(claim.ResumeId)!; Assert.Equal(LocalStage.Created, recovered.LocalStage); Assert.Equal(claim.ClaimId, recovered.Claim.ClaimId);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }
    [Fact] public void RejectsCorruptRecordRatherThanCreatingAnotherClaim()
    {
        Store().GetOrCreate(ProtocolFixtures.Code()); var file = Directory.GetFiles(root, "*.resume").Single(); File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Throws<ProtocolException>(() => Store().GetOrCreate(ProtocolFixtures.Code()));
        Assert.Single(Directory.GetFiles(root, "*.resume"));
    }
    [Fact] public void RejectsStateRegressionAndClaimReplacement()
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code()); var original = store.Load(claim.ResumeId)!;
        store.Save(original.Authenticate(ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(), null)).Advance(LocalStage.Installed));
        Assert.Throws<ProtocolException>(() => store.Save(original));
        var wrong = new ClaimRecord(claim.ResumeId, Guid.NewGuid(), new ResumeSecret(new byte[32]));
        var saved = store.Load(claim.ResumeId)!;
        Assert.Throws<ProtocolException>(() => store.Save(new ResumeRecord(wrong, saved.Code, saved.CreatedAt, saved.ExpiresAt, saved.LocalStage, saved.Snapshot)));
    }
    [Fact] public void ExpiryCannotBeExtendedBySavingAnUnclaimedRecord()
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code()); var record = store.Load(claim.ResumeId)!;
        Assert.Throws<ProtocolException>(() => store.Save(new ResumeRecord(record.Claim, record.Code, record.CreatedAt, record.ExpiresAt.AddMinutes(1), LocalStage.Created)));
        clock.UtcNow = clock.UtcNow.AddHours(2); Assert.Null(store.Load(claim.ResumeId));
    }
    [Theory][InlineData("revision")][InlineData("reasoning_effort")][InlineData("projection_growth")][InlineData("ascii_escape")]
    public void UnicodeNetworkBoundaryRoundTripsInStoredRepresentation(string field)
    {
        var json = ProtocolFixtures.SessionJson(); var revision = new string('界', 40000);
        if (field == "reasoning_effort") { json["client"]![field] = revision; }
        else if (field == "revision") json[field] = revision;
        else if (field == "ascii_escape") json["revision"] = new string('<', 130000);
        else
        {
            json.Remove("mirrors"); json["revision"] = "";
            var overhead = Encoding.UTF8.GetByteCount(json.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            json["revision"] = new string('r',131072-overhead);
        }
        var text = json.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var bytes = Encoding.UTF8.GetBytes(text); Assert.True(bytes.Length <= 131072);
        bytes = bytes.Concat(Enumerable.Repeat((byte)' ', 131072 - bytes.Length)).ToArray();
        var snapshot = SessionSnapshot.Parse(bytes); Assert.True(Encoding.UTF8.GetByteCount(snapshot.ToWire().GetRawText()) > 131072);
        Assert.Throws<ProtocolException>(() => SessionSnapshot.Parse(bytes.Concat(new byte[] {(byte)' '}).ToArray()));
        var efforts = field == "reasoning_effort" ? new[] {revision} : new[] {"high"};
        var validated = new SessionBindingValidator(clock, new SessionAdapterPolicy(efforts, true)).Validate(ProtocolFixtures.Code(), snapshot, null);
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code());
        store.Save(store.Load(claim.ResumeId)!.Authenticate(validated));
        var recovered = Store().Load(claim.ResumeId)!;
        ProtocolFixtures.Canonical(snapshot.ToWire(), recovered.Snapshot!.ToWire());
        clock.UtcNow = snapshot.ExpiresAt; Assert.Null(Store().Load(claim.ResumeId)); Assert.Empty(Directory.GetFiles(root,"*.resume"));
    }
    [Theory][InlineData("restart")][InlineData("expiry")][InlineData("delete")]
    public void ReclaimsFullyWrittenOrphanProtectedTemps(string operation)
    {
        var store = Store(); var claim = store.GetOrCreate(ProtocolFixtures.Code());
        var orphan = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        ResumeFileSecurity.WriteNew(orphan, File.ReadAllBytes(Directory.GetFiles(root,"*.resume").Single()));
        Assert.True(File.Exists(orphan));
        if (operation == "expiry") { clock.UtcNow = clock.UtcNow.AddHours(2); Assert.Null(Store().Load(claim.ResumeId)); }
        else if (operation == "delete") Store().Delete(claim.ResumeId);
        else Assert.Equal(claim.ClaimId, Store().GetOrCreate(ProtocolFixtures.Code()).ClaimId);
        Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }
    [Fact] public void OrphanCleanupRejectsReparseTargets()
    {
        var claim = Store().GetOrCreate(ProtocolFixtures.Code()); var outside = root + ".outside";
        File.WriteAllText(outside,"keep"); var orphan = Path.Combine(root,"unsafe.tmp"); TestCompat.CreateFileSymbolicLink(orphan,outside);
        try { Assert.Throws<IOException>(() => Store().Load(claim.ResumeId)); Assert.Equal("keep",File.ReadAllText(outside)); Assert.True(File.Exists(orphan)); }
        finally { File.Delete(outside); }
    }
    [Fact] public void RandomIdCollisionCannotOverwriteAnotherClaim()
    {
        var store = new DpapiResumeStore(root, clock, new RepeatingRandom(), new TestProtection());
        var first = store.GetOrCreate(ProtocolFixtures.Code());
        var otherCode = ProtocolFixtures.Code().ToWire().GetRawText().Replace("ins_example", "ins_other");
        Assert.Throws<IOException>(() => store.GetOrCreate(SetupCodeParser.Parse(SetupCodeTests.Encode(otherCode))));
        Assert.Equal("ins_example", store.Load(first.ResumeId)!.Code.InstallationId);
    }
    [Fact] public async Task ConcurrentStoresReuseOneClaim()
    {
        var claims = await Task.WhenAll(Enumerable.Range(0,8).Select(_ => Task.Run(() => Store().GetOrCreate(ProtocolFixtures.Code()))));
        Assert.Single(claims.Select(c => c.ClaimId).Distinct()); Assert.Single(Directory.GetFiles(root, "*.resume"));
    }
}
internal sealed class TestProtection : IResumeProtection
{
    // Deliberate substitute. These bytes provide no Windows/DPAPI evidence.
    public byte[] Protect(byte[] value) => value.Select(b => (byte)(b ^ 0xa5)).ToArray();
    public byte[] Unprotect(byte[] value) => Protect(value);
}
internal sealed class FailingProtection : IResumeProtection
{
    public byte[] Protect(byte[] value) => throw new IOException("simulated persistence failure");
    public byte[] Unprotect(byte[] value) => throw new IOException("simulated persistence failure");
}

internal sealed class FailOnProtect : IResumeProtection
{
    public byte[] Protect(byte[] value) => throw new IOException("simulated durable save failure");
    public byte[] Unprotect(byte[] value) => new TestProtection().Unprotect(value);
}

internal sealed class RepeatingRandom : IRandomSource { public byte[] GetBytes(int length) => Enumerable.Repeat((byte)1, length).ToArray(); }
