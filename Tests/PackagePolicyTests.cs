using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Tests.Protocol;
using System.Text.Json.Nodes;
namespace AiDesktopSetup.Tests;
public sealed class PackagePolicyTests
{
    internal static MachineState Machine(string arch = "x64", InstalledPackage? installed = null) => new(true, new Version(10,0,22631,0), arch, "x64", "S-1-5-21-100", installed);
    internal static ValidatedSession Session(string? mirrorVersion = null, bool license = true)
    {
        var json = ProtocolFixtures.SessionJson(); json["mirrors"] = new JsonArray();
        if (mirrorVersion != null)
        {
            var mirror = new JsonObject { ["app_id"]="codex-desktop", ["architecture"]="x64", ["version"]=mirrorVersion,
                ["package"]=new JsonObject { ["url"]="https://mirror.example/app.msix", ["bytes"]=300, ["sha256"]=new string('a',64) } };
            if (license) mirror["license"] = new JsonObject { ["url"]="https://mirror.example/license.xml", ["bytes"]=50, ["sha256"]=new string('b',64) };
            json["mirrors"]!.AsArray().Add(mirror);
        }
        return new(ProtocolFixtures.Session(json), CredentialType.SetupTicket);
    }
    [Fact] public void EmptyMirrorsUsesOfficialSource()
    {
        var plan = CodexOfficialPolicy.Resolve(Session(), Machine());
        Assert.Equal(InstallationAction.Install, plan.Action);
        Assert.Equal("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix", plan.Package!.Url);
        Assert.Equal("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-License.xml", plan.License!.Url);
        Assert.False(plan.IsMirror);
    }
    [Fact] public void NativeOsArchitectureSelectsArm64EvenForEmulatedProcess() =>
        Assert.Equal("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-arm64.msix", CodexOfficialPolicy.Resolve(Session(), Machine("arm64")).Package!.Url);
    [Fact] public void NewerInstalledVersionNeverDowngrades()
    {
        var installed = new InstalledPackage("OpenAI.Codex", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B", "OpenAI.Codex_2p2nqsd0c76g0", new Version(99,0,0,0), "x64", true);
        Assert.Equal(InstallationAction.Skip, CodexOfficialPolicy.Resolve(Session("26.928.3736.0"), Machine(installed:installed)).Action);
        Assert.Equal(InstallationAction.Register, CodexOfficialPolicy.Resolve(Session("26.928.3736.0"), Machine(installed:installed with { RegisteredForCurrentUser=false })).Action);
    }
    [Theory] [InlineData("Other.App")] [InlineData("OpenAI.Codex")]
    public void WrongInstalledIdentityCannotBypassInstallation(string name)
    {
        var installed = new InstalledPackage(name, "CN=Other", "OpenAI.Codex_other", new Version(99,0,0,0), "x64", true);
        Assert.Equal(InstallationAction.Install, CodexOfficialPolicy.Resolve(Session(), Machine(installed:installed)).Action);
    }
    [Theory] [InlineData("26.928.3735.0",true)] [InlineData("26.928.3736.0",false)] [InlineData("nonsense",true)]
    public void MirrorPairMustMatch(string version,bool license) => Assert.Throws<SetupException>(() => CodexOfficialPolicy.Resolve(Session(version,license),Machine()));
    [Fact] public void MirrorNeverReplacesLocalTrustPolicy()
    {
        var plan=CodexOfficialPolicy.Resolve(Session("26.928.3736.0"),Machine());
        Assert.True(plan.IsMirror); Assert.Equal("https://mirror.example/app.msix",plan.Package!.Url);
        Assert.Equal("OpenAI.Codex",plan.Policy.IdentityName); Assert.Equal("CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B",plan.Policy.Publisher);
        Assert.True(plan.Policy.RequiresLicense);
    }
    [Theory] [InlineData("x86",10,0,22631)] [InlineData("x64",10,0,19040)] [InlineData("arm64",10,0,22000)]
    public void UnsupportedOsOrArchitectureDoesNotDownload(string arch,int major,int minor,int build) =>
        Assert.Equal(InstallationAction.Unsupported,CodexOfficialPolicy.Resolve(Session(), Machine(arch) with { OsVersion=new Version(major,minor,build,0) }).Action);
    [Fact] public void LaunchRequiresOfficialCurrentUserRegistration()
    {
        var installed=new InstalledPackage("OpenAI.Codex","CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B","OpenAI.Codex_2p2nqsd0c76g0",new Version(26,928,3736,0),"x64",true);
        CodexLauncher.ValidateRegisteredMachine(Machine(installed:installed));
        Assert.Throws<SetupException>(()=>CodexLauncher.ValidateRegisteredMachine(Machine(installed:installed with { RegisteredForCurrentUser=false })));
        Assert.Throws<SetupException>(()=>CodexLauncher.ValidateRegisteredMachine(Machine(installed:installed with { Publisher="CN=Other" })));
        Assert.Throws<SetupException>(()=>CodexLauncher.ValidateRegisteredMachine(Machine(installed:installed with { Version=new Version(1,0,0,0) })));
    }
    [Fact] public void NonWindowsPlanCannotDownload()=>Assert.Equal(InstallationAction.Unsupported,CodexOfficialPolicy.Resolve(Session(),Machine() with { IsWindows=false }).Action);
    [Theory] [InlineData("none",true)] [InlineData("minimal",true)] [InlineData("low",true)] [InlineData("medium",true)] [InlineData("high",true)] [InlineData("xhigh",true)] [InlineData("max",false)] [InlineData("ultra",false)] [InlineData("persistent",false)] [InlineData("HIGH",false)]
    public void ProductionCapabilityIsReviewedLocalSubset(string effort,bool allowed)
    {
        var json=ProtocolFixtures.SessionJson(); json["client"]!["reasoning_effort"]=effort;
        var validator=new SessionBindingValidator(new FixedClock(),CodexOfficialPolicy.SessionCapabilities);
        if (allowed) validator.Validate(ProtocolFixtures.Code(),ProtocolFixtures.Session(json));
        else Assert.Throws<ProtocolException>(()=>validator.Validate(ProtocolFixtures.Code(),ProtocolFixtures.Session(json)));
    }
}
