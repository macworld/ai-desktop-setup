using System.IO.Compression;
using System.Security.Cryptography;
using AiDesktopSetup.Core;
using Xunit.Abstractions;
namespace AiDesktopSetup.Tests.Windows;
public sealed class NativeSignatureFixtureFactAttribute : FactAttribute
{
    public NativeSignatureFixtureFactAttribute()
    { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AI_SETUP_SIGNATURE_MSIX"))) Skip = "Requires a real official package for architecture-independent native signature-provider testing."; }
}
/// <summary>This gate tests only the signature provider, not target package architecture or installation.</summary>
public sealed class NativeTrustProviderTests(ITestOutputHelper output)
{
    [NativeSignatureFixtureFact] public void SuccessfulOfficialBaselinePrecedesManifestAndPayloadTamperRejection()
    {
        var source = Environment.GetEnvironmentVariable("AI_SETUP_SIGNATURE_MSIX")!;
        using (var input = File.OpenRead(source))
        using (var hash = SHA256.Create()) output.WriteLine("Official fixture SHA256: " + BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
        Verify(source); // A failed baseline MUST fail this test before either negative assertion.
        output.WriteLine("Unmodified package passed the production native signature provider.");
        var root = Path.Combine(Path.GetTempPath(), "signature-provider-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var manifest in new[] { true, false })
            {
                var path = Path.Combine(root, manifest ? "manifest.msix" : "payload.msix"); File.Copy(source, path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                {
                    if (manifest)
                    {
                        var entry = zip.GetEntry("AppxManifest.xml")!; string xml;
                        using (var reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                        entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("AppxManifest.xml").Open()); writer.Write(xml.Replace("OpenAI.Codex", "Other.Product"));
                    }
                    else
                    {
                        var entry = zip.GetEntry("app/ChatGPT.exe") ?? throw new InvalidDataException("Official executable is absent.");
                        using var bytes = entry.Open(); var first = bytes.ReadByte(); Assert.True(first >= 0); bytes.Position = 0; bytes.WriteByte((byte)(first ^ 1));
                    }
                }
                var failure = Assert.Throws<PackageTrustException>(() => Verify(path));
                output.WriteLine((manifest ? "Manifest" : "Payload") + " corruption rejected: " + failure.NativeStatus);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Verify(string path)
    { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); new WindowsPackageSignatureTrust().Verify(path, file); }
}
