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
