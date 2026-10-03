#if !NETFRAMEWORK
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Core.Workflow;
namespace AiDesktopSetup.Tests.Protocol;

// A neutral, explicitly requested child process. No private service discovery or native acceptance.
internal static class InteropLiveProcess
{
    internal static int Run()
    {
        try { Execute().GetAwaiter().GetResult(); return 0; }
        catch { Console.Error.WriteLine("Interoperability phase failed (details suppressed to protect fixture secrets)."); return 76; }
    }
    private static async Task Execute()
    {
        var file = Environment.GetEnvironmentVariable("AGSP_LIVE_FIXTURE");
        Assert.True(!string.IsNullOrWhiteSpace(file) && File.Exists(file), "Required neutral fixture is missing");
        var f = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(file!))!;
        var origin = new Uri(f.Origin);
        Assert.True(f.Version == 1 && Directory.Exists(f.Root) && origin.IsLoopback && origin.Scheme == "https" && origin.GetLeftPart(UriPartial.Authority) == f.Origin);
        Assert.Contains(f.Phase, new[] { "happy", "api-key", "cancel", "bootstrap-loss", "resume", "complete-loss", "receipt", "revoke-before-install", "revoke-after-install", "receipt-denied" });
        Assert.True(!string.IsNullOrWhiteSpace(f.Key) && Path.GetDirectoryName(Path.GetFullPath(f.Result)) == Path.GetFullPath(f.Root));
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(f.Certificate));
        using var clients = InteropClient.Clients(f.Origin, certificate);
        var clock = new FixedClock { UtcNow = f.Now };
        var store = InteropClient.Store(f.Root, clock);
        var config = new InteropConfiguration(f.Root);
        var installer = new InteropInstaller();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var sessions = new SetupSessionClient(clients, _ => throw new InvalidOperationException("No native decoder in this fixture"), clock);
        var coordinator = new SetupCoordinator(store, sessions, installer, config, clock, new("1.0.0", "x64"));
        await coordinator.CleanupAsync(timeout.Token);
        var code = coordinator.Preview(f.Code).Code;
        await Signal(f.Root, "preview", timeout.Token);
        var outcome = "";
        ResumeId id;
        if (f.Phase == "bootstrap-loss")
        {
            var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => coordinator.AuthenticateAsync(code, timeout.Token));
            Assert.Equal("temporarily_unavailable", error.Error.Code);
            id = Assert.Single(store.ListAvailable());
            Assert.Equal(LocalStage.Created, store.Load(id)!.LocalStage);
            Assert.Null(store.Load(id)!.Snapshot);
            Assert.False(Directory.Exists(Path.Combine(f.Root, "home")) && File.Exists(Path.Combine(f.Root, "home", "auth.json")));
            outcome = "bootstrap-pending";
        }
        else if (f.Phase is "resume" or "receipt" or "receipt-denied")
        {
            id = Assert.Single(store.ListAvailable());
            Assert.True(store.Load(id)!.Code.OriginalCode == f.Code);
            var paths = new[] { Path.Combine(f.Root, "home", "config.toml"), Path.Combine(f.Root, "home", "auth.json") };
            var before = f.Phase.StartsWith("receipt") ? paths.Select(File.ReadAllBytes).ToArray() : null;
            var times = f.Phase.StartsWith("receipt") ? paths.Select(File.GetLastWriteTimeUtc).ToArray() : null;
            if (f.Phase == "receipt-denied")
            {
                var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => coordinator.ResumeAsync(id, timeout.Token));
                Assert.Equal("access_denied", error.Error.Code);
                Assert.NotNull(store.Load(id)); outcome = "denied";
            }
            else
            {
                var result = await coordinator.ResumeAsync(id, timeout.Token);
                Assert.Equal(ReceiptState.Acknowledged, result.ReceiptState);
                Assert.Null(store.Load(id)); outcome = "acknowledged";
            }
            if (before != null)
            {
                Assert.Equal(0, installer.Calls); Assert.Equal(0, config.Writes);
                for (var i = 0; i < paths.Length; i++) { Assert.True(before[i].SequenceEqual(File.ReadAllBytes(paths[i]))); Assert.Equal(times![i], File.GetLastWriteTimeUtc(paths[i])); }
            }
        }
        else
        {
            var setup = await coordinator.AuthenticateAsync(code, timeout.Token); id = setup.ResumeId;
            Assert.Equal(f.Model, setup.Session.Snapshot.Client.Model);
            Assert.Equal(f.Brand, setup.Session.Snapshot.Service.Name);
            Assert.Equal(f.Revision, setup.Session.Snapshot.Revision);
            Assert.Equal("Selected fixture", setup.Session.Snapshot.Selection.KeyLabel);
            Assert.Equal("Fixture group", setup.Session.Snapshot.Selection.GroupLabel);
            Assert.True(!setup.Session.Snapshot.Selection.KeyHint.Contains(f.Key));
            Assert.Equal(code.ApiBaseUrl, setup.Session.Snapshot.ApiBaseUrl);
            Assert.False(setup.Session.Snapshot.Service.LogoAvailable); Assert.Empty(setup.Session.Snapshot.Mirrors);
            if (f.Phase == "cancel") { await coordinator.CancelAsync(id, timeout.Token); Assert.Null(store.Load(id)); outcome = "canceled"; }
            else
            {
                if (f.Phase == "revoke-before-install") await Signal(f.Root, "revoke", timeout.Token);
                if (f.Phase == "revoke-after-install") installer.OnInstall = () => Signal(f.Root, "revoke", timeout.Token);
                if (f.Phase.StartsWith("revoke"))
                {
                    var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => coordinator.InstallAndConfigureAsync(setup, null, timeout.Token));
                    Assert.Equal("access_denied", error.Error.Code); Assert.Equal(0, config.Writes);
                    Assert.Equal(f.Phase == "revoke-before-install" ? 0 : 1, installer.Calls);
                    Assert.False(File.Exists(Path.Combine(f.Root, "home", "auth.json"))); outcome = "denied";
                }
                else
                {
                    var result = await coordinator.InstallAndConfigureAsync(setup, null, timeout.Token);
                    Assert.Equal(SetupLocalState.Completed, result.LocalState);
                    Assert.True(InteropClient.ConfigurationMatches(f.Root, f.Key, code.ApiBaseUrl, f.Model));
                    if (f.Phase == "complete-loss")
                    { Assert.Equal(ReceiptState.Pending, result.ReceiptState); Assert.False(result.CanLaunch); Assert.Equal(LocalStage.ConfigCommitted, store.Load(id)!.LocalStage); outcome = "receipt-pending"; }
                    else { Assert.Equal(ReceiptState.Acknowledged, result.ReceiptState); Assert.Null(store.Load(id)); outcome = "acknowledged"; }
                }
            }
        }
        if (outcome == "acknowledged")
        {
            Assert.True(InteropClient.ConfigurationMatches(f.Root, f.Key, code.ApiBaseUrl, f.Model));
            var journal = Path.Combine(f.Root, "home", ".ai-desktop-setup");
            Assert.Empty(Directory.GetFiles(journal, "*.stage", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(journal, "intent.json", SearchOption.AllDirectories));
        }
        var resultFile = JsonSerializer.Serialize(new { outcome, installs = installer.Calls, writes = config.Writes, resumeId = id.Value, processId = Environment.ProcessId });
        File.WriteAllText(f.Result, resultFile); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f.Result, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static async Task Signal(string root, string name, CancellationToken ct)
    {
        var ready = Path.Combine(root, name + ".ready"); File.WriteAllText(ready, "ready");
        while (!File.Exists(Path.Combine(root, name + ".go"))) await Task.Delay(20, ct);
    }
    private sealed class Fixture
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Certificate { get; set; } = "";
        public string Code { get; set; } = "";
        public string Key { get; set; } = "";
        public string Model { get; set; } = "";
        public string Brand { get; set; } = "";
        public string Revision { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Result { get; set; } = "";
        public DateTimeOffset Now { get; set; }
    }
}
#endif
