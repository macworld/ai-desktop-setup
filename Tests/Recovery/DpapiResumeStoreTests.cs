using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Tests.Protocol;

namespace AiDesktopSetup.Tests.Recovery;
public sealed class DpapiResumeStoreTests
{
    [Fact] public void ProductionStoreRequiresWindows()
    {
        if (RuntimeCompat.IsWindows) return;
        Assert.Throws<PlatformNotSupportedException>(() => new DpapiResumeStore(Path.Combine(ProtocolFixtures.TemporaryDirectory(), "unused-resume-tests"), new FixedClock(), new CryptoRandomSource()));
    }
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NativeCreatedFilesHaveExactCurrentUserOwner()
    {
        var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "native-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            ResumeFileSecurity.EnsureDirectory(root);
            using (ResumeFileSecurity.OpenLock(Path.Combine(root, "owned.lock"))) { }
            ResumeFileSecurity.WriteNew(Path.Combine(root, "owned.stage"), new byte[] { 1, 2, 3 });
            ResumeFileSecurity.ValidatePrivate(Path.Combine(root, "owned.lock"), false);
            ResumeFileSecurity.ValidatePrivate(Path.Combine(root, "owned.stage"), false);
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            foreach (var path in Directory.GetFiles(root))
                Assert.Equal(sid, new FileInfo(path).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [WindowsFact] public async Task NativeConcurrentStoreCreationKeepsOneClaim()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "native-concurrent-" + Guid.NewGuid().ToString("N"));
            try
            {
                var clock = new FixedClock();
                var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new DpapiResumeStore(root, clock, new CryptoRandomSource()).GetOrCreate(ProtocolFixtures.Code()))));
                Assert.Single(claims.Select(c => c.ClaimId).Distinct()); Assert.Single(Directory.GetFiles(root, "*.resume"));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
    [WindowsFact] public void NativeCurrentUserRoundTripAndPrivateAcl()
    {
        var root = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "native-resume-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiResumeStore(root, new FixedClock(), new CryptoRandomSource());
            var claim = store.GetOrCreate(ProtocolFixtures.Code());
            Assert.Equal(claim.ResumeSecret.Bytes, store.Load(claim.ResumeId)!.Claim.ResumeSecret.Bytes);
            Assert.DoesNotContain(ProtocolFixtures.CodeText(), System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(root, "*.resume").Single())));
            ResumeFileSecurity.ValidatePrivate(root, true);
            ResumeFileSecurity.ValidatePrivate(Directory.GetFiles(root, "*.resume").Single(), false);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!RuntimeCompat.IsWindows) Skip = "Requires native Windows current-user DPAPI and ACLs."; }
}
