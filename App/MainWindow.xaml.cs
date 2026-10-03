using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;
using AiDesktopSetup.Core.Workflow;
namespace AiDesktopSetup;
public partial class MainWindow : Window
{
    private readonly ProtocolHttpClients clients = new();
    private readonly DpapiResumeStore store;
    private readonly SetupSessionClient sessions;
    private readonly SetupCoordinator coordinator;
    private SetupPreview? preview;
    private AuthenticatedSetup? setup;
    private ResumeId? active;
    private string? help;
    private readonly SetupCodePaste codePaste;
    private CancellationTokenSource? operation;
    private Task? running;
    private bool closing, closed;
    public MainWindow()
    {
        InitializeComponent();
        var clock = new SystemClock();
        store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Desktop Setup", "Recovery"), clock, new CryptoRandomSource());
        sessions = new(clients, bytes => BoundedLogoDecoder.Decode(bytes), clock);
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        coordinator = new(store, sessions, new DesktopSetupInstaller(), new DesktopSetupConfiguration(home!), clock,
            new(typeof(MainWindow).Assembly.GetName().Version!.ToString(), RuntimeCompat.OsArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64"));
        codePaste = new(CodeBox, PreviewCode, Clipboard.GetText, Clipboard.Clear);
        Loaded += async (_, _) => await Execute(async ct => { await coordinator.CleanupAsync(ct); RefreshRecovery(); });
        Closed += (_, _) => clients.Dispose();
    }
    private void CodeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        preview = null; setup = null; help = null;
        AuthenticateButton.IsEnabled = InstallButton.IsEnabled = HelpButton.IsEnabled = LaunchButton.IsEnabled = false;
        PreviewText.Text = ""; ServiceText.Text = ""; ServiceLogo.Source = null;
    }
    private void PreviewClick(object sender, RoutedEventArgs e) => PreviewCode();
    private void PasteClick(object sender, RoutedEventArgs e)
    {
        try { codePaste.PasteFromClipboard(); }
        catch (Exception) { StatusText.Text = "Clipboard unavailable. Paste or type the code in the field."; }
    }
    private void PreviewCode()
    {
        try
        {
            preview = coordinator.Preview(CodeBox.Password); setup = null; help = null;
            PreviewText.Text = "Unverified service label: " + preview.UnverifiedServiceName + "\nSetup: " + preview.Code.SetupBaseUrl + "\nAPI: " + preview.ApiBaseUrl;
            AuthenticateButton.IsEnabled = true; InstallButton.IsEnabled = false; HelpButton.IsEnabled = false; ServiceText.Text = ""; ServiceLogo.Source = null; LaunchButton.IsEnabled = false;
        }
        catch (Exception) { preview = null; AuthenticateButton.IsEnabled = false; StatusText.Text = "This setup code is invalid. Request a fresh code from your service."; }
    }
    private async void AuthenticateClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (preview == null) return;
        active = store.GetOrCreate(preview.Code).ResumeId;
        setup = await coordinator.AuthenticateAsync(preview.Code, ct);
        var snapshot = setup.Session.Snapshot;
        ServiceText.Text = snapshot.Service.Name + "\nGroup: " + snapshot.Selection.GroupLabel + " · Key: " + snapshot.Selection.KeyLabel + " (" + snapshot.Selection.KeyHint + ")";
        help = SafeHelp(snapshot.Service.HelpUrl);
        if (snapshot.Service.LogoAvailable)
        {
            try { var bytes = await sessions.GetLogoAsync(setup.Access, ct); ServiceLogo.Source = bytes == null ? null : BoundedLogoDecoder.Decode(bytes); }
            catch { setup = null; throw; }
        }
        StatusText.Text = "Service authenticated. Review the addresses, then install and configure.";
    });
    private async void InstallClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (setup == null) return;
        ShowOutcome(await coordinator.InstallAndConfigureAsync(setup, new Progress<SetupProgress>(p =>
        { StatusText.Text = p.Message; Progress.IsIndeterminate = !p.Percent.HasValue; if (p.Percent.HasValue) Progress.Value = p.Percent.Value; }), ct));
    });
    private async void ResumeClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (RecoveryList.SelectedItem is not ResumeId id) return;
        active = id; ShowOutcome(await coordinator.ResumeAsync(id, ct));
    });
    private async void OfficialRetryClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        var id = RecoveryList.SelectedItem is ResumeId selected ? selected : active;
        if (!id.HasValue) return;
        active = id;
        ShowOutcome(await coordinator.RetryOfficialAsync(id.Value, new Progress<SetupProgress>(p => StatusText.Text = p.Message), ct));
    });
    private void ShowOutcome(SetupOutcome result)
    {
        StatusText.Text = result.LocalState == SetupLocalState.Completed ? "Configuration is complete." : "Installation is not yet registered for this account; configuration has not been written.";
        if (result.ReceiptState == ReceiptState.Pending) StatusText.Text += " Completion receipt is pending. Resume to retry; committed configuration will not be written again.";
        if (result.NeedsRestart) StatusText.Text += " Installation requested a Windows restart.";
        if (result.NeedsSignOut) StatusText.Text += " Installation requested sign-out and sign-in.";
        if (result.NeedsRestart || result.NeedsSignOut) StatusText.Text += " If already completed, open the client from the Start menu.";
        if (result.ReceiptState == ReceiptState.Acknowledged) { ClearClipboard(); active = null; setup = null; CodeBox.Clear(); }
        LaunchButton.IsEnabled = result.CanLaunch;
    }
    private async Task Execute(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        operation = new(); SetBusy(true);
        try { running = action(operation.Token); await running; }
        catch (OperationCanceledException) { StatusText.Text = "Operation stopped. Recovery is retained until you cancel or exit."; }
        catch (ProtocolHttpException error) { SetupDiagnostics.Current.RecordProtocolFailure(error.Error); StatusText.Text = "Service request failed: " + error.Error.Code + (error.RetryAfter.HasValue ? ". Retry after " + Math.Ceiling(error.RetryAfter.Value.TotalSeconds) + " seconds." : ". You can retry or clear recovery."); }
        catch (PackageTrustException error) { StatusText.Text = error.Retryable ? "Package trust could not be verified. Retry when verification is available." : "Package trust verification failed. Installation is blocked."; }
        catch (Exception) { StatusText.Text = "Setup could not continue. Recovery was retained. Retry, or cancel to clear recovery; existing files and private backups are preserved."; }
        finally { ClearClipboard(); operation.Dispose(); operation = null; running = null; Progress.IsIndeterminate = false; SetBusy(false); RefreshRecovery(); }
    }
    private void SetBusy(bool busy)
    {
        PasteButton.IsEnabled = PreviewButton.IsEnabled = CodeBox.IsEnabled = ResumeButton.IsEnabled = RecoveryList.IsEnabled = !busy;
        AuthenticateButton.IsEnabled = !busy && preview != null; InstallButton.IsEnabled = !busy && setup != null;
        HelpButton.IsEnabled = !busy && help != null;
        OfficialRetryButton.IsEnabled = !busy && (active.HasValue || RecoveryList.SelectedItem is ResumeId);
        if (busy) LaunchButton.IsEnabled = false;
    }
    private void RecoverySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (operation != null || RecoveryList.SelectedItem is not ResumeId id) return;
        OfficialRetryButton.IsEnabled = true;
        try
        {
            var record = store.Load(id); if (record == null) return;
            PreviewText.Text = "Saved service label (authorization will be rechecked): " + record.Code.ServiceName + "\nSetup: " + record.Code.SetupBaseUrl + "\nAPI: " + record.Code.ApiBaseUrl;
        }
        catch { StatusText.Text = "Recovery is unavailable."; }
    }
    private void RefreshRecovery()
    {
        try { RecoveryList.ItemsSource = store.ListAvailable(); if (active.HasValue) RecoveryList.SelectedItem = active.Value; else if (RecoveryList.Items.Count != 0) RecoveryList.SelectedIndex = 0; }
        catch { StatusText.Text = "Recovery storage is unavailable. No new setup will start until private storage works."; }
    }
    private async Task EndCurrent(bool all = false)
    {
        operation?.Cancel(); if (running != null) { try { await running; } catch { } }
        IReadOnlyList<ResumeId> ids = all ? store.ListAvailable() : active.HasValue ? new[] { active.Value } : RecoveryList.SelectedItem is ResumeId selected ? new[] { selected } : Array.Empty<ResumeId>();
        foreach (var id in ids) { try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await coordinator.CancelAsync(id, deadline.Token); } catch { if (store.Load(id) != null) throw; } }
        await coordinator.CleanupAsync(CancellationToken.None);
        ClearClipboard(); CodeBox.Clear(); setup = null; preview = null; active = null; LaunchButton.IsEnabled = false; RefreshRecovery();
    }
    private async void CancelClick(object sender, RoutedEventArgs e)
    { try { await EndCurrent(); StatusText.Text = "Recovery cleared. API keys, installed clients, existing configuration and backups were not removed."; SetBusy(false); } catch { StatusText.Text = "Recovery cleanup failed. Close only after private storage becomes available."; } }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return; e.Cancel = true; if (closing) return; closing = true;
        try { await EndCurrent(true); closed = true; Close(); }
        catch { StatusText.Text = "Unable to clear recovery on exit. Retry after private storage becomes available."; }
        finally { closing = false; }
    }
    private void ClearClipboard() => codePaste.TryClear();
    private static string? SafeHelp(string? value) => value != null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? value : null;
    private void HelpClick(object sender, RoutedEventArgs e) { if (help != null) Process.Start(new ProcessStartInfo(help) { UseShellExecute = true }); }
    private void LaunchClick(object sender, RoutedEventArgs e) { try { CodexLauncher.Open(); } catch { StatusText.Text = "The client is not ready for this account. Open it from the Start menu after any requested restart or sign-in."; } }
}
