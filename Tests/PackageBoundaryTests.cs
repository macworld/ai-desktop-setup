using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using System.Net;
using System.Text;
namespace AiDesktopSetup.Tests;
public sealed class PackageBoundaryTests
{
    [Fact] public async Task MirrorPairDownloadsAnonymouslyWithoutRedirects()
    {
        using var fixture=new PackageFixture(); var bytes=File.ReadAllBytes(fixture.Path);var license=Encoding.UTF8.GetBytes("<License/>");
        using var clients=new ProtocolHttpClients(new RejectHandler(),new RejectHandler(),new ArtifactHandler(bytes,license));
        var plan=CodexOfficialPolicy.Resolve(PackagePolicyTests.Session("26.928.3736.0"),PackagePolicyTests.Machine());
        plan=plan with { Package=plan.Package! with { Bytes=bytes.Length,Sha256=TestCompat.Sha256(bytes) }, License=plan.License! with { Bytes=license.Length,Sha256=TestCompat.Sha256(license) } };
        var destination=System.IO.Path.Combine(fixture.DirectoryPath,"download");Directory.CreateDirectory(destination);
        using var prepared=await new PackageTrustVerifier(new PackageTrustTests.AcceptTrust()).PrepareAsync(clients,plan,PackagePolicyTests.Machine(),destination,null,default);
        Assert.Equal(bytes,File.ReadAllBytes(prepared.Package.Path));
        Assert.Equal("codex-desktop",prepared.CreateHelperRequest().AppId);
        Assert.Equal(InstallationOperation.Provision,prepared.CreateHelperRequest().Operation);
    }
    [Fact] public async Task MirrorRedirectIsRejectedWithoutFallback()
    {
        using var fixture=new PackageFixture();using var clients=new ProtocolHttpClients(new RejectHandler(),new RejectHandler(),new RedirectHandler());
        var plan=CodexOfficialPolicy.Resolve(PackagePolicyTests.Session("26.928.3736.0"),PackagePolicyTests.Machine());
        await Assert.ThrowsAsync<HttpRequestException>(()=>new PackageTrustVerifier(new PackageTrustTests.AcceptTrust()).PrepareAsync(clients,plan,PackagePolicyTests.Machine(),fixture.DirectoryPath,null,default));
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.DirectoryPath,"package.msix")));
    }
    [Theory] [InlineData("{\"Command\":\"run\"}")] [InlineData("{\"RootCertificate\":\"trust-me\"}")] [InlineData("{\"Operation\":99}")]
    public async Task UntrustedHelperRequestCannotAddCommandsOrRoots(string request) =>
        await Assert.ThrowsAsync<SetupException>(()=>InstallChannel.ReadRequestAsync(new MemoryStream(Encoding.UTF8.GetBytes(request+"\n")),default));
    [Fact] public async Task HelperRequestIsBoundedBeforeDeserialization() =>
        await Assert.ThrowsAsync<SetupException>(()=>InstallChannel.ReadRequestAsync(new MemoryStream(Encoding.UTF8.GetBytes(new string('a',16385)+"\n")),default));
    [Fact] public void SenderRejectsEscapedFrameLargerThanHelperLimit()
    {
        var root=System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath("."))!;
        var request=new InstallHelperRequest(root+new string('界',3000)+".msix",root+new string('界',3000)+".xml",new string('a',64),new string('b',64),"codex-desktop",InstallationOperation.Provision);
        Assert.Throws<SetupException>(()=>request.ToWire());
    }
    [Fact] public async Task CredentialFreeRequestRoundTripsAndRequiresLicensePair()
    {
        using var fixture=new PackageFixture();
        var request=new InstallHelperRequest(fixture.Path,System.IO.Path.Combine(fixture.DirectoryPath,"license.xml"),new string('a',64),new string('b',64),"codex-desktop",InstallationOperation.Provision);
        var bytes=Encoding.UTF8.GetBytes(request.ToWire()+"\ncancel\n");using var stream=new MemoryStream(bytes);
        Assert.Equal(request,await InstallChannel.ReadRequestAsync(stream,default));
        Assert.Equal((int)'c',stream.ReadByte());
        Assert.Throws<SetupException>(()=>(request with { LicensePath=null,LicenseSha256=null }).ToWire());
        Assert.Throws<SetupException>(()=>(request with { AppId="other" }).ToWire());
        Assert.Throws<SetupException>(()=>(request with { PackagePath="relative.msix" }).ToWire());
    }
    [Fact] public async Task ModifiedHandoffNeverProducesDeployableArtifacts()
    {
        using var fixture=new PackageFixture();var license=System.IO.Path.Combine(fixture.DirectoryPath,"source-license.xml");File.WriteAllText(license,"<License/>");
        var request=new InstallHelperRequest(fixture.Path,license,TestCompat.Sha256(File.ReadAllBytes(fixture.Path)),TestCompat.Sha256(File.ReadAllBytes(license)),"codex-desktop",InstallationOperation.Provision);
        File.AppendAllText(fixture.Path,"replaced");var target=System.IO.Path.Combine(fixture.DirectoryPath,"protected");Directory.CreateDirectory(target);
        var executionCount=0;
        await Assert.ThrowsAsync<SetupException>(async()=>
        {
            using var protectedCopy=await new PackageTrustVerifier(new PackageTrustTests.AcceptTrust()).PrepareHelperCopyAsync(request,PackagePolicyTests.Machine(),target,default);
            executionCount++;
        });
        Assert.Equal(0,executionCount);
    }
    [Fact] public async Task HelperReconstructsPolicyInsteadOfTrustingParentIdentity()
    {
        using var fixture=new PackageFixture("Other.App","CN=Other");var license=System.IO.Path.Combine(fixture.DirectoryPath,"source-license.xml");File.WriteAllText(license,"<License/>");
        var request=new InstallHelperRequest(fixture.Path,license,TestCompat.Sha256(File.ReadAllBytes(fixture.Path)),TestCompat.Sha256(File.ReadAllBytes(license)),"codex-desktop",InstallationOperation.Provision);
        var target=System.IO.Path.Combine(fixture.DirectoryPath,"protected");Directory.CreateDirectory(target);
        await Assert.ThrowsAsync<SetupException>(()=>new PackageTrustVerifier(new PackageTrustTests.AcceptTrust()).PrepareHelperCopyAsync(request,PackagePolicyTests.Machine(),target,default));
    }
    private sealed class RejectHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>throw new InvalidOperationException("Credential-bearing transport must not be used for artifacts."); }
    private sealed class RedirectHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect){RequestMessage=request,Headers={Location=new Uri("https://elsewhere.example/app.msix")}}); }
    private sealed class ArtifactHandler(byte[] package,byte[] license) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Null(request.Headers.Authorization);Assert.DoesNotContain(request.Headers,h=>h.Key.IndexOf("proof",StringComparison.OrdinalIgnoreCase)>=0||h.Key=="Cookie");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){RequestMessage=request,Content=new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".xml")?license:package)});
        }
    }
}
