using AiDesktopSetup.Core;
using System.IO.Compression;
namespace AiDesktopSetup.Tests;
public sealed class NativePackageTrustFactAttribute : FactAttribute
{
    public NativePackageTrustFactAttribute()
    {
        if(!RuntimeCompat.IsWindows) Skip="Requires a native Windows host; ZIP parsing or emulation cannot establish package trust.";
        else if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AI_SETUP_OFFICIAL_MSIX"))) Skip="Set AI_SETUP_OFFICIAL_MSIX to a local, real official package (never a synthetic fixture).";
    }
}
public sealed class NativePackageTrustTests
{
    private static MachineState Machine()
    {
        var arch=RuntimeCompat.OsArchitecture.ToString().ToLowerInvariant();
        Assert.Equal(arch,System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        return new(true,Environment.OSVersion.Version,arch,arch,"native-test-user");
    }
    [NativePackageTrustFact] public void ActualOfficialPackageRequiresNativeTrustAndPinsBytes()
    {
        var path=Environment.GetEnvironmentVariable("AI_SETUP_OFFICIAL_MSIX")!;var machine=Machine();
        using var verified=new PackageTrustVerifier().Verify(path,CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture),machine);
        Assert.Equal("OpenAI.Codex",verified.IdentityName);Assert.Equal(64,verified.Sha256.Length);
        Assert.Throws<IOException>(()=> { using var writable=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite); });
        var other=machine.OsArchitecture=="x64"?"arm64":"x64";
        Assert.Throws<SetupException>(()=>new PackageTrustVerifier().Verify(path,CodexOfficialPolicy.ForArchitecture(other),machine));
    }
    [NativePackageTrustFact] public void ActualOfficialPackageTamperedManifestAndPayloadFailNativeTrust()
    {
        var source=Environment.GetEnvironmentVariable("AI_SETUP_OFFICIAL_MSIX")!;var machine=Machine();
        var root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"native-package-trust-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var copy=System.IO.Path.Combine(root,"tampered.msix");File.Copy(source,copy);
            using(var zip=ZipFile.Open(copy,ZipArchiveMode.Update))
            { var manifest=zip.GetEntry("AppxManifest.xml")!;string xml;using(var reader=new StreamReader(manifest.Open())) xml=reader.ReadToEnd();manifest.Delete();using var writer=new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open());writer.Write(xml.Replace("OpenAI.Codex","Other.Product")); }
            Assert.Throws<PackageTrustException>(()=>new PackageTrustVerifier().Verify(copy,CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture),machine));
            File.Delete(copy);File.Copy(source,copy);
            using(var zip=ZipFile.Open(copy,ZipArchiveMode.Update))
            { var payload=zip.GetEntry("app/ChatGPT.exe") ?? throw new InvalidDataException("Expected official executable is absent.");using var stream=payload.Open();var first=stream.ReadByte();stream.Position=0;stream.WriteByte((byte)(first^1)); }
            Assert.Throws<PackageTrustException>(()=>new PackageTrustVerifier().Verify(copy,CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture),machine));
        }
        finally { Directory.Delete(root,true); }
    }
}
