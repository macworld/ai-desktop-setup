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
        var claim = Store().GetOrCreate(ProtocolFixtures.Code()); File.WriteAllText(Path.Combine(root, "interrupted.tmp"), "half-written");
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
