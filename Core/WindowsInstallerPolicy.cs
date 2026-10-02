using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

[assembly: InternalsVisibleTo("AI.Desktop.Setup.Tests")]
namespace AiDesktopSetup.Core;

internal sealed record WindowsArtifact(Uri Url, long? Bytes = null, string? Sha256 = null, string? Version = null);
internal sealed record WindowsSources(WindowsArtifact Package, WindowsArtifact License, bool IsMirror);
internal sealed record WindowsPackageIdentity(string Version, string Architecture);

internal static class WindowsInstallerPolicy
{
    internal static ProcessStartInfo CreateHelperStartInfo(string executable, Guid pipeId, bool isAdministrator)
    {
        // Keep the helper credential-free. An already elevated parent can launch it
        // directly under the same identity; other callers request elevation via UAC.
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = !isAdministrator,
            Verb = isAdministrator ? "" : "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        RuntimeCompat.AddArgument(start, "--install");
        RuntimeCompat.AddArgument(start, pipeId.ToString("N"));
        return start;
    }

    internal const string Publisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    internal const long MaximumPackageBytes = 10L * 1024 * 1024 * 1024;
    internal const long MaximumLicenseBytes = 1024 * 1024;

    internal static WindowsSources ResolveSources(string architecture)
    {
        if (architecture is not ("x64" or "arm64")) throw new InvalidDataException("不支持的 Windows 架构。");
        return new WindowsSources(
            new(new Uri($"https://persistent.oaistatic.com/codex-app-prod/ChatGPT-{architecture}.msix")),
            new(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-License.xml")), false);
    }

    private static bool ValidVersion(string? value) => value is not null && Regex.IsMatch(value, @"^\d+(\.\d+){3}$") && Version.TryParse(value, out _);

    internal static WindowsPackageIdentity ValidatePackage(string path, string architecture, Version osVersion, string? expectedVersion)
    {
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        return ValidatePackage(stream,architecture,osVersion,expectedVersion);
    }

    internal static WindowsPackageIdentity ValidatePackage(Stream package, string architecture, Version osVersion, string? expectedVersion)
    {
        using var archive = new ZipArchive(package,ZipArchiveMode.Read,true);
        var entries = archive.Entries.Where(e => e.FullName == "AppxManifest.xml").ToArray();
        if (entries.Length != 1 || entries[0].Length > 1048576) throw new InvalidDataException("安装包清单缺失或不合法。");
        using var stream = entries[0].Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
        var root = XDocument.Load(reader).Root;
        var identity = root?.Elements().SingleOrDefault(e => e.Name.LocalName == "Identity");
        var version = identity?.Attribute("Version")?.Value;
        if (root?.Name.LocalName != "Package" || identity?.Attribute("Name")?.Value != "OpenAI.Codex" || identity.Attribute("Publisher")?.Value != Publisher || identity.Attribute("ProcessorArchitecture")?.Value != architecture || !ValidVersion(version))
            throw new InvalidDataException("安装包身份或架构不符合官方 ChatGPT 桌面版。");
        if (expectedVersion is not null && expectedVersion != version) throw new InvalidDataException("安装包版本与镜像清单不一致。");
        var families = root.Elements().Where(e => e.Name.LocalName == "Dependencies").Elements().Where(e => e.Name.LocalName == "TargetDeviceFamily" && e.Attribute("Name")?.Value is "Windows.Desktop" or "Windows.Universal").ToArray();
        if (osVersion < new Version(10,0,19041,0) || families.Length == 0 || !families.Any(f => Version.TryParse(f.Attribute("MinVersion")?.Value, out var min) && osVersion >= min))
            throw new InvalidDataException("此版本 ChatGPT 桌面版 需要更新的 Windows。");
        return new(version!, architecture);
    }

    internal static bool IsProvisioned(string output, WindowsPackageIdentity package)
    {
        var expected = $"OpenAI.Codex_{package.Version}_{package.Architecture}__2p2nqsd0c76g0";
        return output.Split('\n').Any(line => Regex.IsMatch(line.Trim(), @"^PackageName\s*:\s*" + Regex.Escape(expected) + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
    }

    internal static bool ValidateDeployment(int exitCode, bool provisioned)
    {
        if (exitCode is not (0 or 3010)) throw new SetupException("Windows 无法安装 ChatGPT 桌面版。请检查系统更新、磁盘空间及组织的安装策略，再重试。");
        if (!provisioned) throw new SetupException("暂时无法确认 ChatGPT 桌面版已安装。请重新打开工具检查，不要关闭系统签名验证。");
        return exitCode == 3010;
    }

    internal static async Task DownloadAsync(HttpClient client, WindowsArtifact artifact, string destination, long maximumBytes, IProgress<SetupProgress>? progress, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (!IsAllowedArtifact(artifact.Url)) throw new InvalidDataException("安装文件地址不可信。");
        await DownloadCoreAsync(ct=>client.GetAsync(artifact.Url,HttpCompletionOption.ResponseHeadersRead,ct),artifact,destination,maximumBytes,progress,cancellationToken,timeout).ConfigureAwait(false);
    }

    internal static Task DownloadAsync(AiDesktopSetup.Core.Protocol.ProtocolHttpClients clients,PackageArtifact artifact,string destination,long maximumBytes,IProgress<SetupProgress>? progress,CancellationToken cancellationToken)
    {
        var url=AiDesktopSetup.Core.Protocol.SetupUrlValidator.Asset(artifact.Url);
        if (artifact.Bytes is <= 0 || artifact.Bytes > maximumBytes || (artifact.Sha256!=null && !Regex.IsMatch(artifact.Sha256,@"\A[0-9a-f]{64}\z"))) throw new SetupException("Artifact size or digest is unsupported.");
        return DownloadCoreAsync(ct=>clients.GetPackageAsync(artifact.Url,ct),new WindowsArtifact(new Uri(url),artifact.Bytes,artifact.Sha256,artifact.Version),destination,maximumBytes,progress,cancellationToken,null);
    }

    private static async Task DownloadCoreAsync(Func<CancellationToken,Task<HttpResponseMessage>> send,WindowsArtifact artifact,string destination,long maximumBytes,IProgress<SetupProgress>? progress,CancellationToken cancellationToken,TimeSpan? timeout)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers; this deadline also covers every body read.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(20));
        cancellationToken = deadline.Token;
        var created = false;
        try
        {
            progress?.Report(new("download", "正在连接下载源…", Source: DownloadProgressTracker.SourceLabel(artifact.Url)));
            using var response = await send(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode>=300 && (int)response.StatusCode<=399) throw new HttpRequestException("Artifact redirects are not allowed.");
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri != artifact.Url) throw new InvalidDataException("下载地址发生变化，已停止。");
            var length = response.Content.Headers.ContentLength;
            if (length > maximumBytes || length is <= 0 || (artifact.Bytes is not null && length is not null && artifact.Bytes != length)) throw new InvalidDataException("安装文件大小校验失败。");
            var total = artifact.Bytes ?? length;
            using var input = await RuntimeCompat.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                created = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[131072];
                long received = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var tracker = new DownloadProgressTracker(artifact.Url, total);
                var lastReport = TimeSpan.Zero;
                void Report()
                {
                    lastReport = clock.Elapsed;
                    progress?.Report(tracker.Sample(received, lastReport));
                }
                Report();
                var tick = Task.Delay(500, cancellationToken);
                int count;
                while (true)
                {
                    var read = RuntimeCompat.ReadAsync(input, buffer, 0, buffer.Length, cancellationToken);
                    while (!read.IsCompleted)
                    {
                        await Task.WhenAny(read, tick).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (tick.IsCompleted)
                        {
                            await tick.ConfigureAwait(false);
                            Report();
                            tick = Task.Delay(500, cancellationToken);
                        }
                    }
                    count = await read.ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > maximumBytes || (total.HasValue && received > total.Value)) throw new InvalidDataException("安装文件大小超出预期。");
                    await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer,0,count);
                    if (clock.Elapsed - lastReport >= TimeSpan.FromMilliseconds(500)) Report();
                }
                if (received == 0 || (total.HasValue && received != total.Value)) throw new InvalidDataException("安装文件下载不完整。");
                if (artifact.Sha256 is not null && !string.Equals(RuntimeCompat.Hex(hash.GetHashAndReset()), artifact.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("安装文件 SHA256 校验失败。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new("download", "文件下载完成", 100, DownloadProgressTracker.SourceLabel(artifact.Url)));
            }
        }
        catch
        {
            if (created) { try { File.Delete(destination); } catch (IOException) { } }
            throw;
        }
    }

    private static bool IsAllowedArtifact(Uri url) => url.Scheme == "https" && url.IsDefaultPort && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment)
        && url.Host == "persistent.oaistatic.com" && url.AbsolutePath is "/codex-app-prod/ChatGPT-x64.msix" or "/codex-app-prod/ChatGPT-arm64.msix" or "/codex-app-prod/ChatGPT-License.xml";
}
