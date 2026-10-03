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
    private readonly SetupInteractionState interaction = new();
    private readonly SetupCodePaste codePaste;
    private CancellationTokenSource? operation;
    private Task? running;
    private bool closing, closed, selectingRecovery;
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
        Loaded += async (_, _) => { await Execute(ct => coordinator.CleanupAsync(ct)); RefreshRecovery(selectFirst: true); };
        Closed += (_, _) => clients.Dispose();
    }
    private void CodeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        interaction.CodeChanged(); if (!selectingRecovery) RecoveryList.SelectedItem = null;
        OfficialRetryButton.IsEnabled = ResumeButton.IsEnabled = false;
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
            CodeChanged(this, new RoutedEventArgs());
            interaction.Preview = coordinator.Preview(CodeBox.Password);
            OfficialRetryButton.IsEnabled = ResumeButton.IsEnabled = false;
            PreviewText.Text = "Unverified service label: " + interaction.Preview.UnverifiedServiceName + "\nSetup: " + interaction.Preview.Code.SetupBaseUrl + "\nAPI: " + interaction.Preview.ApiBaseUrl;
            AuthenticateButton.IsEnabled = true; InstallButton.IsEnabled = false; HelpButton.IsEnabled = false; ServiceText.Text = ""; ServiceLogo.Source = null; LaunchButton.IsEnabled = false;
        }
        catch (Exception) { interaction.Preview = null; AuthenticateButton.IsEnabled = false; StatusText.Text = "This setup code is invalid. Request a fresh code from your service."; }
    }
    private async void AuthenticateClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (interaction.Preview == null) return;
        interaction.Active = store.GetOrCreate(interaction.Preview.Code).ResumeId;
        interaction.Setup = await coordinator.AuthenticateAsync(interaction.Preview.Code, ct);
        var snapshot = interaction.Setup.Session.Snapshot;
        ServiceText.Text = snapshot.Service.Name + "\nGroup: " + snapshot.Selection.GroupLabel + " · Key: " + snapshot.Selection.KeyLabel + " (" + snapshot.Selection.KeyHint + ")";
        interaction.Help = SafeHelp(snapshot.Service.HelpUrl);
        if (snapshot.Service.LogoAvailable)
        {
            try { var bytes = await sessions.GetLogoAsync(interaction.Setup.Access, ct); ServiceLogo.Source = bytes == null ? null : BoundedLogoDecoder.Decode(bytes); }
            catch { interaction.Setup = null; throw; }
        }
        StatusText.Text = "Service authenticated. Review the addresses, then install and configure.";
    });
    private async void InstallClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (interaction.Setup == null) return;
        ShowOutcome(await coordinator.InstallAndConfigureAsync(interaction.Setup, new Progress<SetupProgress>(p =>
        { StatusText.Text = p.Message; Progress.IsIndeterminate = !p.Percent.HasValue; if (p.Percent.HasValue) Progress.Value = p.Percent.Value; }), ct));
    });
    private async void ResumeClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        if (RecoveryList.SelectedItem is not ResumeId id) return;
        interaction.Active = id; ShowOutcome(await coordinator.ResumeAsync(id, ct));
    });
    private async void OfficialRetryClick(object sender, RoutedEventArgs e) => await Execute(async ct =>
    {
        var id = RecoveryList.SelectedItem is ResumeId selected ? selected : interaction.Active;
        if (!id.HasValue) return;
        interaction.Active = id;
        ShowOutcome(await coordinator.RetryOfficialAsync(id.Value, new Progress<SetupProgress>(p => StatusText.Text = p.Message), ct));
    });
    private void ShowOutcome(SetupOutcome result)
    {
        StatusText.Text = result.LocalState == SetupLocalState.Completed ? "Configuration is complete." : "Installation is not yet registered for this account; configuration has not been written.";
        if (result.ReceiptState == ReceiptState.Pending) StatusText.Text += " Completion receipt is pending. Resume to retry; committed configuration will not be written again.";
        if (result.NeedsRestart) StatusText.Text += " Installation requested a Windows restart.";
        if (result.NeedsSignOut) StatusText.Text += " Installation requested sign-out and sign-in.";
        if (result.NeedsRestart || result.NeedsSignOut) StatusText.Text += " If already completed, open the client from the Start menu.";
        if (result.ReceiptState == ReceiptState.Acknowledged) { ClearClipboard(); CodeBox.Clear(); }
        interaction.Complete(result); LaunchButton.IsEnabled = interaction.CanLaunch;
    }
    private async Task Execute(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        operation = new(); SetBusy(true);
        try { running = action(operation.Token); await running; }
        catch (OperationCanceledException) { StatusText.Text = "Operation stopped. Recovery is retained until you cancel or exit."; }
        catch (ProtocolHttpException error) { SetupDiagnostics.Current.RecordProtocolFailure(error.Error); StatusText.Text = "Service request failed: " + error.Error.Code + (error.RetryAfter.HasValue ? ". Retry after " + Math.Ceiling(error.RetryAfter.Value.TotalSeconds) + " seconds." : ". You can retry or clear recovery."); }
        catch (PackageTrustException error) { StatusText.Text = error.Retryable ? "Package trust could not be verified. Retry when verification is available." : "Package trust verification failed. Installation is blocked."; }
        catch (Exception error) { StatusText.Text = SetupFailurePresentation.Message(error); }
        finally { ClearClipboard(); operation.Dispose(); operation = null; running = null; Progress.IsIndeterminate = false; SetBusy(false); RefreshRecovery(); }
    }
    private void SetBusy(bool busy)
    {
        PasteButton.IsEnabled = PreviewButton.IsEnabled = CodeBox.IsEnabled = RecoveryList.IsEnabled = !busy;
        ResumeButton.IsEnabled = !busy && RecoveryList.SelectedItem is ResumeId;
        AuthenticateButton.IsEnabled = !busy && interaction.CanAuthenticate; InstallButton.IsEnabled = !busy && interaction.CanInstall;
        HelpButton.IsEnabled = !busy && interaction.Help != null;
        OfficialRetryButton.IsEnabled = !busy && (interaction.Active.HasValue || RecoveryList.SelectedItem is ResumeId);
        LaunchButton.IsEnabled = !busy && interaction.CanLaunch;
    }
    private void RecoverySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (operation != null || RecoveryList.SelectedItem is not ResumeId id) return;
        try
        {
            var sameTarget = interaction.Active == id;
            var record = interaction.LoadRecovery(id, store.Load);
            if (record == null) { RecoveryUnavailable(); return; }
            if (sameTarget) { SetBusy(false); return; }
            // Clear the typed code before retargeting: PasswordChanged invalidates its old authorization.
            selectingRecovery = true;
            try { CodeBox.Clear(); } finally { selectingRecovery = false; }
            interaction.SelectRecovery(id);
            ServiceText.Text = ""; ServiceLogo.Source = null;
            SetBusy(false);
            PreviewText.Text = "Saved service label (authorization will be rechecked): " + record.Code.ServiceName + "\nSetup: " + record.Code.SetupBaseUrl + "\nAPI: " + record.Code.ApiBaseUrl;
        }
        catch { RecoveryUnavailable(); }
    }
    private void RecoveryUnavailable()
    {
        CodeBox.Clear(); CodeChanged(this, new RoutedEventArgs()); SetBusy(false);
        StatusText.Text = "Recovery is unavailable. Select another saved recovery or paste a fresh setup code.";
    }
    private void RefreshRecovery(bool selectFirst = false)
    {
        try
        {
            var available = store.ListAvailable(); var selected = interaction.RecoveryForRefresh(available, selectFirst);
            RecoveryList.ItemsSource = available; RecoveryList.SelectedItem = selected;
            SetBusy(operation != null);
        }
        catch { StatusText.Text = "Recovery storage is unavailable. No new setup will start until private storage works."; }
    }
    private async Task EndCurrent(bool all = false)
    {
        operation?.Cancel(); if (running != null) { try { await running; } catch { } }
        IReadOnlyList<ResumeId> ids = all ? store.ListAvailable() : interaction.Active.HasValue ? new[] { interaction.Active.Value } : RecoveryList.SelectedItem is ResumeId selected ? new[] { selected } : Array.Empty<ResumeId>();
        foreach (var id in ids) { try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await coordinator.CancelAsync(id, deadline.Token); } catch { if (store.Load(id) != null) throw; } }
        await coordinator.CleanupAsync(CancellationToken.None);
        ClearClipboard(); CodeBox.Clear(); interaction.CodeChanged(); LaunchButton.IsEnabled = false; RefreshRecovery();
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
    private void HelpClick(object sender, RoutedEventArgs e) { if (interaction.Help != null) Process.Start(new ProcessStartInfo(interaction.Help) { UseShellExecute = true }); }
    private void LaunchClick(object sender, RoutedEventArgs e) { try { CodexLauncher.Open(); } catch { StatusText.Text = "The client is not ready for this account. Open it from the Start menu after any requested restart or sign-in."; } }
}
