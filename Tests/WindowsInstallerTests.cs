using System.IO.Compression;
using System.Net;
using System.Text;
using AiDesktopSetup.Core;
using Xunit;

namespace AiDesktopSetup.Tests;

public sealed class WindowsInstallerTests
{
    private const string Publisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";

    [Theory]
    [InlineData("x64", "https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix")]
    [InlineData("arm64", "https://persistent.oaistatic.com/codex-app-prod/ChatGPT-arm64.msix")]
    public void ResolvesOnlyOfficialSources(string architecture, string package)
    {
        var sources = WindowsInstallerPolicy.ResolveSources(architecture);
        Assert.Equal(package, sources.Package.Url.AbsoluteUri);
        Assert.Equal("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-License.xml", sources.License.Url.AbsoluteUri);
        Assert.False(sources.IsMirror);
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("")]
    [InlineData("ARM64")]
    public void UnsupportedArchitecturesAreRejected(string architecture)
        => Assert.Throws<InvalidDataException>(() => WindowsInstallerPolicy.ResolveSources(architecture));

    [Theory]
    [InlineData("https://gateway.example/installer.msix")]
    [InlineData("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix?token=x")]
    public async Task UntrustedDownloadAddressIsRejectedBeforeCreatingAFile(string address)
    {
        using var fixture = new TempDirectory();
        using var client = Client(HttpStatusCode.OK, [1,2,3]);
        var output = Path.Combine(fixture.Path, "untrusted.msix");
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsInstallerPolicy.DownloadAsync(client,
            new WindowsArtifact(new Uri(address)), output, 1024, null, default));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    public void SuccessfulDeploymentKeepsTheWindowsRestartRequirement(int exitCode, bool provisioned, bool restart)
        => Assert.Equal(restart, WindowsInstallerPolicy.ValidateDeployment(exitCode, provisioned));

    [Theory]
    [InlineData(0, false)]
    [InlineData(3010, false)]
    [InlineData(5, true)]
    public void FailedOrUnconfirmedDeploymentCannotBeReportedAsInstalled(int exitCode, bool provisioned)
        => Assert.Throws<SetupException>(() => WindowsInstallerPolicy.ValidateDeployment(exitCode, provisioned));

    [Fact]
    public async Task DownloadDeadlineAlsoCoversAStalledResponseBody()
    {
        using var fixture = new TempDirectory();
        using var client = new HttpClient(new StalledHandler());
        var output = System.IO.Path.Combine(fixture.Path, "slow.msix");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsInstallerPolicy.DownloadAsync(client,
            new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix")), output, 1024, null, default, TimeSpan.FromMilliseconds(50)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task StalledBodyStillUpdatesSpeedUntilCancelledAndRemovesPartialFile()
    {
        using var fixture = new TempDirectory();
        using var client = new HttpClient(new StalledHandler());
        var reports = new List<SetupProgress>();
        var output = Path.Combine(fixture.Path, "stalled.msix");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsInstallerPolicy.DownloadAsync(client,
            new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix")),
            output, 1024, new CaptureProgress(reports), default, TimeSpan.FromMilliseconds(1800)));
        Assert.True(reports.Count >= 3, "A stalled read must not freeze the progress display.");
        Assert.Contains(reports, report => report.BytesPerSecond == 0 && report.RemainingSeconds is null);
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("Other.App", Publisher, "x64", "10.0.19041.0")]
    [InlineData("OpenAI.Codex", "CN=Other", "x64", "10.0.19041.0")]
    [InlineData("OpenAI.Codex", Publisher, "arm64", "10.0.19041.0")]
    [InlineData("OpenAI.Codex", Publisher, "x64", "10.0.99999.0")]
    public void PackageWithWrongIdentityOrUnsupportedOsIsRejected(string name, string publisher, string arch, string min)
    {
        using var fixture = new TempDirectory();
        var path = CreatePackage(fixture.Path, name, publisher, arch, min);
        Assert.Throws<InvalidDataException>(() => WindowsInstallerPolicy.ValidatePackage(path, "x64", new Version(10,0,22631,0), null));
    }

    [Fact]
    public void PackageVersionMustMatchMirrorMetadataAndDeploymentReceipt()
    {
        using var fixture = new TempDirectory();
        var path = CreatePackage(fixture.Path, "OpenAI.Codex", Publisher, "x64", "10.0.19041.0");
        var package = WindowsInstallerPolicy.ValidatePackage(path, "x64", new Version(10,0,22631,0), "1.2.3.4");
        Assert.Equal("1.2.3.4", package.Version);
        Assert.Throws<InvalidDataException>(() => WindowsInstallerPolicy.ValidatePackage(path, "x64", new Version(10,0,22631,0), "1.2.3.5"));
        Assert.True(WindowsInstallerPolicy.IsProvisioned("PackageName : OpenAI.Codex_1.2.3.4_x64__2p2nqsd0c76g0\r\n", package));
        Assert.False(WindowsInstallerPolicy.IsProvisioned("PackageName : OpenAI.Codex_1.2.3.5_x64__2p2nqsd0c76g0", package));
        Assert.False(WindowsInstallerPolicy.IsProvisioned("PackageName : OpenAI.Codex_1.2.3.4_x64__otherpublisher", package));
    }

    [Fact]
    public async Task DownloadsActualBytesAndReportsActualProgress()
    {
        using var fixture = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("official-package-fixture");
        using var client = Client(HttpStatusCode.OK, bytes);
        var reports = new List<SetupProgress>();
        var artifact = new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix"), bytes.Length, TestCompat.Sha256(bytes), "1.2.3.4");
        var output = System.IO.Path.Combine(fixture.Path, "a.msix");
        await WindowsInstallerPolicy.DownloadAsync(client, artifact, output, 1024, new CaptureProgress(reports), default);
        Assert.Equal(bytes, await TestCompat.ReadAllBytesAsync(output));
        Assert.Equal(100d, reports.Last().Percent);
        Assert.All(reports, report => Assert.Equal("download", report.Stage));
    }

    [Fact]
    public async Task DownloadProgressCarriesItsActualSourceAcrossThePipe()
    {
        using var fixture = new TempDirectory();
        using var client = Client(HttpStatusCode.OK, [1, 2, 3]);
        var reports = new List<SetupProgress>();
        await WindowsInstallerPolicy.DownloadAsync(client,
            new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix")),
            Path.Combine(fixture.Path, "a.msix"), 1024, new CaptureProgress(reports), default);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(reports[0]);
        Assert.True(json.TryGetProperty("Source", out var source), "Download reports must identify their source.");
        Assert.Equal("官方源 · persistent.oaistatic.com", source.GetString());
    }

    [Theory]
    [InlineData(200, 8, false)]
    [InlineData(200, 7, true)]
    [InlineData(302, 7, false)]
    public async Task CorruptOrRedirectedDownloadsAreRejectedAndRemoved(int status, long size, bool wrongHash)
    {
        using var fixture = new TempDirectory();
        var bytes = Encoding.UTF8.GetBytes("fixture");
        using var client = Client((HttpStatusCode)status, bytes);
        var artifact = new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix"), size,
            wrongHash ? new string('0',64) : TestCompat.Sha256(bytes), "1.2.3.4");
        var output = System.IO.Path.Combine(fixture.Path, "a.msix");
        if (status >= 300)
            await Assert.ThrowsAsync<HttpRequestException>(() => WindowsInstallerPolicy.DownloadAsync(client, artifact, output, 1024, null, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => WindowsInstallerPolicy.DownloadAsync(client, artifact, output, 1024, null, default));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CancellationDoesNotLeaveAnInstallationArtifact()
    {
        using var fixture = new TempDirectory();
        using var client = Client(HttpStatusCode.OK, [1,2,3]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = System.IO.Path.Combine(fixture.Path, "a.msix");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsInstallerPolicy.DownloadAsync(client,
            new WindowsArtifact(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix")), output, 1024, null, cts.Token));
        Assert.False(File.Exists(output));
    }

    private static HttpClient Client(HttpStatusCode status, byte[] data) => new(new StaticHandler(status, data));
    private sealed class StaticHandler(HttpStatusCode status, byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(data), RequestMessage=request });
        }
    }
    private sealed class StalledHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StreamContent(new StalledStream()), RequestMessage=request });
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }
    private sealed class CaptureProgress(List<SetupProgress> reports) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => reports.Add(value);
    }
    private static string CreatePackage(string directory, string name, string publisher, string architecture, string minimum)
    {
        var path=System.IO.Path.Combine(directory,"app.msix");
        using var archive=ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer=new StreamWriter(archive.CreateEntry("AppxManifest.xml").Open());
        writer.Write($"<Package><Identity Name=\"{name}\" Publisher=\"{publisher}\" ProcessorArchitecture=\"{architecture}\" Version=\"1.2.3.4\"/><Dependencies><TargetDeviceFamily Name=\"Windows.Desktop\" MinVersion=\"{minimum}\"/></Dependencies></Package>");
        return path;
    }
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"ai-desktop-setup-installer-test-"+Guid.NewGuid().ToString("N"));
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path,true);
    }
}
