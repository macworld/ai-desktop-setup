#if !NETFRAMEWORK
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Core.Workflow;
using AiDesktopSetup.Tests.Recovery;

namespace AiDesktopSetup.Tests.Protocol;

public sealed class InteroperabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedStalledPeerCannotBlockTeardown(bool finishHandshake)
    {
        var gateway = new NeutralGateway("/setup", "Example", "model", false);
        using var peer = new TcpClient();
        var origin = new Uri(gateway.Origin);
        await peer.ConnectAsync(origin.Host, origin.Port);
        using var tls = new SslStream(peer.GetStream(), false, (_, _, _, _) => true);
        if (finishHandshake) await tls.AuthenticateAsClientAsync("localhost");
        else await peer.GetStream().WriteAsync(new byte[] { 0x16 });
        // Wait for the accepted socket, not a listener backlog connection.
        for (var i = 0; i < 100 && gateway.AcceptedConnections == 0; i++) await Task.Delay(10);
        Assert.Equal(1, gateway.AcceptedConnections);
        var disposing = gateway.DisposeAsync().AsTask();
        try { Assert.Same(disposing, await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(2)))); }
        finally { peer.Dispose(); await disposing; }
    }

    [Theory]
    [InlineData("/Orion/Setup", "Orion Example", "orion-model", true)]
    [InlineData("/nova/setup", "Nova Example", "nova-model", false)]
    public async Task TwoHttpsServicesUseSameCoordinatorAndConfiguration(string path, string brand, string model, bool assets)
    {
        await using var gateway = new NeutralGateway(path, brand, model, assets);
        using var scratch = new InteropScratch();
        using var clients = InteropClient.Clients(gateway.Origin, gateway.Certificate);
        var clock = new FixedClock();
        var store = InteropClient.Store(scratch.Root, clock);
        var config = new InteropConfiguration(scratch.Root);
        var installer = new InteropInstaller();
        var sessions = new SetupSessionClient(clients, _ => { }, clock);
        var coordinator = new SetupCoordinator(store, sessions, installer, config, clock, new("1.0.0", "x64"));
        await coordinator.CleanupAsync(default);
        var code = coordinator.Preview(gateway.Code).Code;
        Assert.Equal(0, gateway.Requests);
        var bad = JsonNode.Parse(code.ToWire().GetRawText())!;
        bad["credential"]!["value"] = StrictJson.EncodeBase64Url(RandomNumberGenerator.GetBytes(32));
        using (var badScratch = new InteropScratch())
        {
            var badCoordinator = new SetupCoordinator(InteropClient.Store(badScratch.Root, clock), sessions, installer, new InteropConfiguration(badScratch.Root), clock, new("1.0.0", "x64"));
            var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => badCoordinator.AuthenticateAsync(SetupCodeParser.Parse("AGSP1." + StrictJson.EncodeBase64Url(Encoding.UTF8.GetBytes(bad.ToJsonString()))), default));
            Assert.Equal("invalid_credential", error.Error.Code);
            Assert.Equal(0, gateway.ConfigurationReads);
        }
        var authenticated = await coordinator.AuthenticateAsync(code, default);
        Assert.True(gateway.ConfigurationReads > 0);
        if (assets)
        {
            Assert.NotNull(await sessions.GetLogoAsync(authenticated.Access, default));
            var mirror = Assert.Single(authenticated.Session.Snapshot.Mirrors);
            foreach (var artifact in new[] { mirror.Package, mirror.License! })
            {
                using var response = await clients.GetPackageAsync(artifact.Url, default);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(artifact.Bytes, bytes.Length); Assert.Equal(artifact.Sha256, TestCompat.Sha256(bytes));
            }
        }
        else { Assert.False(authenticated.Session.Snapshot.Service.LogoAvailable); Assert.Empty(authenticated.Session.Snapshot.Mirrors); }
        Assert.Equal(assets ? 2 : 0, gateway.Downloads);
        Assert.Equal(0, gateway.DownloadSecretHeaders);
        Assert.Equal(brand, authenticated.Session.Snapshot.Service.Name);
        var result = await coordinator.InstallAndConfigureAsync(authenticated, null, default);
        Assert.Equal(ReceiptState.Acknowledged, result.ReceiptState);
        Assert.True(InteropClient.ConfigurationMatches(scratch.Root, gateway.Key, code.ApiBaseUrl, model));
        Assert.Equal(1, installer.Calls);
        Assert.Equal(1, config.Writes);
        Assert.Null(store.Load(authenticated.ResumeId));
    }

    [Fact] public async Task WrongCertificateAndPersistenceFailureNeverReachHttp()
    {
        await using var gateway = new NeutralGateway("/setup", "TLS Example", "tls-model", false);
        await using var other = new NeutralGateway("/other", "Other Example", "other-model", false);
        using var wrong = InteropClient.Clients(gateway.Origin, other.Certificate);
        using var scratch = new InteropScratch();
        var clock = new FixedClock();
        var code = SetupCodeParser.Parse(gateway.Code);
        var sessions = new SetupSessionClient(wrong, _ => { }, clock);
        var claim = InteropClient.Store(scratch.Root, clock).GetOrCreate(code);
        var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => sessions.BootstrapAsync(code, claim, new("1.0.0", "x64"), default));
        Assert.Equal("temporarily_unavailable", error.Error.Code);
        Assert.Equal(0, gateway.Requests);
        using var good = InteropClient.Clients(gateway.Origin, gateway.Certificate);
        using var failing = new InteropScratch();
        var store = new DpapiResumeStore(failing.Root, clock, new CryptoRandomSource(), new FailingProtection());
        var coordinator = new SetupCoordinator(store, new SetupSessionClient(good, _ => { }, clock), new InteropInstaller(), new InteropConfiguration(failing.Root), clock, new("1.0.0", "x64"));
        await Assert.ThrowsAsync<IOException>(() => coordinator.AuthenticateAsync(code, default));
        Assert.Equal(0, gateway.Requests);
    }

    [Theory]
    [InlineData("throttle")]
    [InlineData("broken-401")]
    [InlineData("broken-403")]
    [InlineData("redirect")]
    public async Task HttpsFailuresRemainBoundedWithoutCredentialFallback(string fault)
    {
        await using var gateway = new NeutralGateway("/setup", "Failure Example", "failure-model", false);
        await using var target = new NeutralGateway("/other", "Redirect Example", "redirect-model", false);
        using var scratch = new InteropScratch();
        using var clients = InteropClient.Clients(gateway.Origin, gateway.Certificate);
        var clock = new FixedClock(); var config = new InteropConfiguration(scratch.Root); var installer = new InteropInstaller();
        var coordinator = new SetupCoordinator(InteropClient.Store(scratch.Root, clock), new SetupSessionClient(clients, _ => { }, clock), installer, config, clock, new("1.0.0", "x64"));
        var setup = await coordinator.AuthenticateAsync(SetupCodeParser.Parse(gateway.Code), default);
        gateway.Fault = _ => fault switch {
            "throttle" => NeutralGateway.Error(429, "rate_limited", "Retry-After: 60\r\n"),
            "broken-401" => (401, Encoding.UTF8.GetBytes("broken"), null),
            "broken-403" => (403, Encoding.UTF8.GetBytes("broken"), null),
            _ => (302, Array.Empty<byte>(), "Location: " + target.Origin + "/other\r\n") };
        gateway.TruncateResponse = fault.StartsWith("broken-");
        var before = gateway.Requests;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        if (fault == "redirect") await Assert.ThrowsAsync<ProtocolException>(() => coordinator.InstallAndConfigureAsync(setup, null, default));
        else {
            var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => coordinator.InstallAndConfigureAsync(setup, null, default));
            Assert.Equal(fault == "throttle" ? "rate_limited" : fault == "broken-401" ? "invalid_credential" : "access_denied", error.Error.Code);
            if (fault == "throttle") Assert.Equal(TimeSpan.FromSeconds(60), error.RetryAfter);
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(before + 1, gateway.Requests); Assert.Equal(0, target.Requests);
        Assert.Equal(0, installer.Calls); Assert.Equal(0, config.Writes);
    }

    [Fact] public async Task ChangedSnapshotOverHttpsCannotInstallOrWrite()
    {
        await using var gateway = new NeutralGateway("/setup", "Immutable Example", "fixed-model", false);
        using var scratch = new InteropScratch(); using var clients = InteropClient.Clients(gateway.Origin, gateway.Certificate);
        var clock = new FixedClock(); var installer = new InteropInstaller(); var config = new InteropConfiguration(scratch.Root);
        var coordinator = new SetupCoordinator(InteropClient.Store(scratch.Root, clock), new SetupSessionClient(clients, _ => { }, clock), installer, config, clock, new("1.0.0", "x64"));
        var setup = await coordinator.AuthenticateAsync(SetupCodeParser.Parse(gateway.Code), default);
        var modified = JsonNode.Parse(setup.Session.Snapshot.ToWire().GetRawText())!;
        modified["client"]!["model"] = "changed-model";
        gateway.Fault = _ => (200, Encoding.UTF8.GetBytes(modified.ToJsonString()), null);
        await Assert.ThrowsAsync<ProtocolException>(() => coordinator.InstallAndConfigureAsync(setup, null, default));
        Assert.Equal(0, installer.Calls); Assert.Equal(0, config.Writes);
    }

    [Fact] public async Task ProtectedLogoFailureIsCosmeticExceptAuthorizationDenial()
    {
        await using var gateway = new NeutralGateway("/setup", "Logo Example", "logo-model", true);
        using var scratch = new InteropScratch(); using var clients = InteropClient.Clients(gateway.Origin, gateway.Certificate);
        var clock = new FixedClock(); var sessions = new SetupSessionClient(clients, _ => { }, clock);
        var coordinator = new SetupCoordinator(InteropClient.Store(scratch.Root, clock), sessions, new InteropInstaller(), new InteropConfiguration(scratch.Root), clock, new("1.0.0", "x64"));
        var setup = await coordinator.AuthenticateAsync(SetupCodeParser.Parse(gateway.Code), default);
        gateway.Fault = path => path.EndsWith("/logo") ? (200, Encoding.UTF8.GetBytes("invalid-image"), null) : null;
        Assert.Null(await sessions.GetLogoAsync(setup.Access, default));
        gateway.Fault = path => path.EndsWith("/logo") ? (403, Encoding.UTF8.GetBytes("broken"), null) : null;
        var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => sessions.GetLogoAsync(setup.Access, default));
        Assert.Equal("access_denied", error.Error.Code);
    }
}

internal sealed class InteropScratch : IDisposable
{
    internal string Root { get; } = Path.Combine(ProtocolFixtures.TemporaryDirectory(), "agsp-interop-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
internal static class InteropClient
{
    internal static ProtocolHttpClients Clients(string origin, X509Certificate2 certificate)
    {
        HttpClientHandler Handler()
        {
            var h = ProtocolHttpClients.CreateHandler();
            h.ServerCertificateCustomValidationCallback = (request, cert, _, _) => request.RequestUri?.GetLeftPart(UriPartial.Authority) == origin && cert != null && cert.RawData.SequenceEqual(certificate.RawData);
            return h;
        }
        return new(Handler(), Handler(), Handler());
    }
    internal static DpapiResumeStore Store(string root, IClock clock) => new(Path.Combine(root, "resume"), clock, new CryptoRandomSource(), new TestProtection());
    internal static bool ConfigurationMatches(string root, string key, string api, string model)
    {
        var home = Path.Combine(root, "home");
        var auth = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")))!;
        var text = File.ReadAllText(Path.Combine(home, "config.toml"));
        return auth["OPENAI_API_KEY"]?.GetValue<string>() == key && text.Contains(api) && text.Contains(model);
    }
}
internal sealed class InteropInstaller : ISetupInstaller
{
    internal int Calls;
    internal Func<Task>? OnInstall;
    public Task<InstallState> InspectAsync(CancellationToken ct) => Task.FromResult(new InstallState(true));
    public async Task<InstallState> InstallAsync(ValidatedSession session, IProgress<SetupProgress>? progress, CancellationToken ct, PackageSource source = PackageSource.Session)
    { Calls++; if (OnInstall != null) await OnInstall(); return new(true); }
}
internal sealed class InteropConfiguration(string root) : ISetupConfiguration
{
    private readonly DesktopSetupConfiguration real = new(Path.Combine(root, "home"));
    internal int Writes;
    public ConfigurationRecoveryState Inspect(ResumeId id) => real.Inspect(id);
    public void Recover(ResumeId id) => real.Recover(id);
    public void Abandon(ResumeId id) => real.Abandon(id);
    public void Cleanup(Func<ResumeId, bool> alive) => real.Cleanup(alive);
    public Task ConfigureAsync(ResumeId id, ValidatedSession session, ApiCredential credential, IProgress<SetupProgress>? progress, CancellationToken ct)
    { Writes++; return real.ConfigureAsync(id, session, credential, progress, ct); }
}
internal sealed class NeutralGateway : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    internal X509Certificate2 Certificate { get; }
    internal string Origin { get; }
    internal string Code { get; }
    internal string Key { get; } = "fixture_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal int Requests, ConfigurationReads, Downloads, DownloadSecretHeaders;
    internal int AcceptedConnections;
    internal bool TruncateResponse;
    private readonly string path, brand, model;
    private readonly bool assets;
    private string? proof;
    private string state = "claimed";
    internal Func<string, (int Status, byte[] Body, string? Extra)?>? Fault;
    private static readonly byte[] Logo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lZkAAAAASUVORK5CYII=");
    internal NeutralGateway(string path, string brand, string model, bool assets)
    {
        this.path = path; this.brand = brand; this.model = model; this.assets = assets;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        listener.Start(); Origin = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Code = "AGSP1." + StrictJson.EncodeBase64Url(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, installation_id = "fixture-install", setup_base_url = Origin + path, api_base_url = Origin + "/" + model + "/v1", app_id = "codex-desktop", service_name = brand, expires_at = "2030-01-01T00:10:00Z", credential = new { type = "setup_ticket", value = StrictJson.EncodeBase64Url(RandomNumberGenerator.GetBytes(32)) } }));
        server = Serve();
    }
    private async Task Serve()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                Interlocked.Increment(ref AcceptedConnections);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var closeOnStop = deadline.Token.Register(connection.Dispose);
                try
                {
                    using var tls = new SslStream(connection.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate, EnabledSslProtocols = SslProtocols.Tls12 }, deadline.Token);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, true);
                    var line = await reader.ReadLineAsync(deadline.Token); if (line == null) continue;
                    Interlocked.Increment(ref Requests);
                    var target = line.Split(' ')[1];
                    var incoming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) { var split = line.IndexOf(':'); incoming[line.Substring(0, split)] = line.Substring(split + 1).Trim(); }
                    var length = incoming.TryGetValue("Content-Length", out var value) ? int.Parse(value) : 0;
                    if (length > 16384) throw new IOException("Fixture request too large");
                    var requestBody = new char[length];
                    if (length > 0) await reader.ReadBlockAsync(requestBody.AsMemory(), deadline.Token);
                    var reply = Reply(target, incoming, new string(requestBody));
                    var body = reply.Body;
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + reply.Status + " Fixture\r\nCache-Control: no-store\r\nContent-Type: " + (target.EndsWith("/logo") && reply.Status == 200 ? "image/png" : "application/json") + "\r\nConnection: close\r\n" + reply.Extra + "Content-Length: " + (body.Length + (TruncateResponse ? 5 : 0)) + "\r\n\r\n");
                    await tls.WriteAsync(headers, deadline.Token); await tls.WriteAsync(body, deadline.Token); await tls.FlushAsync(deadline.Token);
                }
                catch (IOException) { }
                catch (AuthenticationException) { }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (deadline.IsCancellationRequested) { }
                catch (SocketException) when (deadline.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
    }
    private (int Status, byte[] Body, string? Extra) Reply(string target, Dictionary<string, string> headers, string body)
    {
        if (target.StartsWith("/assets/"))
        {
            Downloads++;
            if (new[] { "Authorization", "Setup-Session-Proof", "Cookie" }.Any(headers.ContainsKey)) DownloadSecretHeaders++;
            return (200, Encoding.UTF8.GetBytes("synthetic-download"), null);
        }
        if (Fault?.Invoke(target) is { } fault) return fault;
        headers.TryGetValue("Authorization", out var bearer);
        if (target == path + "/bootstrap")
        {
            if (bearer != "Bearer " + SetupCodeParser.Parse(Code).Credential.Value) return Error(401, "invalid_credential");
            if (!headers.TryGetValue("Setup-Session-Proof", out var supplied)) return Error(401, "invalid_credential");
            var request = JsonNode.Parse(body)!;
            if (request["setup_base_url"]?.GetValue<string>() != Origin + path) return Error(400, "invalid_request");
            proof = supplied;
        }
        else if (proof == null || bearer != "Bearer " + proof) return Error(401, "invalid_credential");
        ConfigurationReads++;
        if (target.EndsWith("/logo")) return (200, Logo, null);
        if (target.EndsWith("/credentials"))
        { state = "credentials_released"; return (200, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, session_id = "fixture-install", api_base_url = Origin + "/" + model + "/v1", credential = new { type = "api_key", delivery = "server", value = Key } }), null); }
        if (target.EndsWith("/complete") || target.EndsWith("/cancel"))
        { state = target.EndsWith("/complete") ? "completed" : "canceled"; return (200, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, session_id = "fixture-install", state }), null); }
        var snapshot = ProtocolFixtures.SessionJson();
        snapshot["session_id"] = "fixture-install"; snapshot["setup_base_url"] = Origin + path;
        snapshot["api_base_url"] = Origin + "/" + model + "/v1"; snapshot["state"] = state;
        snapshot["service"]!["name"] = brand; snapshot["service"]!["logo_available"] = assets;
        snapshot["client"]!["model"] = model;
        if (assets) snapshot["mirrors"] = JsonSerializer.SerializeToNode(new[] { new { app_id = "codex-desktop", architecture = "x64", version = "1.0.0", package = new { url = Origin + "/assets/package", bytes = 18, sha256 = TestCompat.Sha256(Encoding.UTF8.GetBytes("synthetic-download")) }, license = new { url = Origin + "/assets/license", bytes = 18, sha256 = TestCompat.Sha256(Encoding.UTF8.GetBytes("synthetic-download")) } } });
        return (200, Encoding.UTF8.GetBytes(snapshot.ToJsonString()), null);
    }
    internal static (int Status, byte[] Body, string? Extra) Error(int status, string code, string? extra = null) => (status, JsonSerializer.SerializeToUtf8Bytes(new { error = new { code, request_id = "fixture" } }), extra);
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await server; Certificate.Dispose(); stop.Dispose(); }
}
#endif
