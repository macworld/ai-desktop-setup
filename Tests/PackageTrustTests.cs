using AiDesktopSetup.Core;
using System.IO.Compression;
namespace AiDesktopSetup.Tests;
public sealed class PackageTrustTests
{
    [Theory] [InlineData("Other.App","CN=Other","x64","26.928.3736.0")] [InlineData("OpenAI.Codex","CN=Other","x64","26.928.3736.0")]
    [InlineData("OpenAI.Codex","CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B","arm64","26.928.3736.0")]
    [InlineData("OpenAI.Codex","CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B","x64","26.928.3735.0")]
    [InlineData("OpenAI.Codex","CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B","x64","65536.0.0.0")]
    public void WrongPublisherOrIdentityRejected(string name,string publisher,string arch,string version)
    {
        using var fixture=new PackageFixture(name,publisher,arch,version);
        var verifier=new PackageTrustVerifier(new AcceptTrust()); // Policy unit test only; never native evidence.
        Assert.Throws<SetupException>(()=>verifier.Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine()));
    }
    [Fact] public void InvalidNativeTrustRejectsOtherwiseCorrectMetadata()
    {
        using var fixture=new PackageFixture();
        var verifier=new PackageTrustVerifier(new RejectTrust());
        Assert.Throws<PackageTrustException>(()=>verifier.Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine()));
    }
    [Fact] public void ModifiedAfterVerificationRejected()
    {
        using var fixture=new PackageFixture(); var verifier=new PackageTrustVerifier(new AcceptTrust());
        string digest;
        using(var verified=verifier.Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine())) digest=verified.Sha256;
        File.AppendAllText(fixture.Path,"tamper");
        Assert.Throws<SetupException>(()=>verifier.VerifyExpected(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine(),digest));
    }
    [Fact] public void NewerInstalledVersionRejectsOlderDownloadedPackage()
    {
        using var fixture=new PackageFixture(); var verifier=new PackageTrustVerifier(new AcceptTrust());
        var installed=new InstalledPackage("OpenAI.Codex","CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B","OpenAI.Codex_2p2nqsd0c76g0",new Version(99,0,0,0),"x64",true);
        Assert.Throws<SetupException>(()=>verifier.Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine(installed:installed)));
    }
    [Fact] public void UnsupportedArm64RuntimeBlockedEvenAfterValidSignature()
    {
        using var fixture=new PackageFixture(arch:"arm64");
        Assert.Throws<SetupException>(()=>new PackageTrustVerifier(new AcceptTrust()).Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("arm64"),PackagePolicyTests.Machine("arm64") with { OsVersion=new Version(10,0,22000,0) }));
    }
    [Fact] public void NonWindowsCannotClaimNativeSuccess()
    {
        if (RuntimeCompat.IsWindows) return;
        using var fixture=new PackageFixture();
        Assert.Throws<PackageTrustException>(()=>new PackageTrustVerifier().Verify(fixture.Path,CodexOfficialPolicy.ForArchitecture("x64"),PackagePolicyTests.Machine()));
    }
    internal sealed class AcceptTrust : IPackageSignatureTrust { public void Verify(string path,FileStream file) {} }
    internal sealed class RejectTrust : IPackageSignatureTrust { public void Verify(string path,FileStream file)=>throw new PackageTrustException(unchecked((int)0x80096010)); }
}
internal sealed class PackageFixture : IDisposable
{
    internal string DirectoryPath { get; }=System.IO.Path.Combine(AiDesktopSetup.Tests.Protocol.ProtocolFixtures.TemporaryDirectory(),"package-test-"+Guid.NewGuid().ToString("N"));
    internal string Path => System.IO.Path.Combine(DirectoryPath,"app.msix");
    internal PackageFixture(string name="OpenAI.Codex",string publisher="CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",string arch="x64",string version="26.928.3736.0")
    {
        Directory.CreateDirectory(DirectoryPath);
        using var archive=ZipFile.Open(Path,ZipArchiveMode.Create); using var writer=new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open());
        writer.Write($"<Package><Identity Name=\"{name}\" Publisher=\"{publisher}\" ProcessorArchitecture=\"{arch}\" Version=\"{version}\"/><Dependencies><TargetDeviceFamily Name=\"Windows.Desktop\" MinVersion=\"10.0.19041.0\"/></Dependencies></Package>");
    }
    public void Dispose()=>Directory.Delete(DirectoryPath,true);
}
