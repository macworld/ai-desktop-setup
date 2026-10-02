using System.Windows;
using AiDesktopSetup.Core;

namespace AiDesktopSetup;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            if (e.Args.Length == 2 && e.Args[0] == "--install" && Guid.TryParseExact(e.Args[1], "N", out _))
            {
                try { Shutdown(await WindowsInstaller.RunElevatedInstallAsync(e.Args[1])); }
                catch (Exception error) { SetupDiagnostics.Current.Record(SetupEvent.HelperException, error.HResult); Shutdown(1); }
            }
            else Shutdown(2);
            return;
        }
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "current";
        instance = new Mutex(true, "Local\\AiDesktopSetup-" + sid, out var first);
        if (!first)
        {
            MessageBox.Show("安装助手已经打开，请回到现有窗口继续。", "AI Desktop Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        MainWindow = new MainWindow();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
