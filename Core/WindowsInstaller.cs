using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AiDesktopSetup.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsInstaller
{
    /// <summary>Runs in the original user session and respects a redirected/OneDrive desktop.</summary>
    public async Task<ShortcutResult> CreateDesktopShortcutAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        const string query = "$ErrorActionPreference='Stop'; $p=Get-AppxPackage -Name 'OpenAI.Codex' | Where-Object { $_.Publisher -ceq 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B' -and $_.Status -eq 'Ok' } | Select-Object -First 1; if ($p) { $m=Get-AppxPackageManifest $p; $a=$m.Package.Applications.Application | Where-Object { $_.Id -ceq 'App' } | Select-Object -First 1; if ($a) { $p.PackageFamilyName + '!' + $a.Id } }";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await RunSystemProcessAsync(PowerShellPath(), ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", query], deadline.Token);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return ShortcutResult.Unavailable;
        return DesktopShortcut.Create(result.Output.Trim(), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    }

    /// <summary>Inspects registration for the original user; this method never elevates.</summary>
    public async Task<InstallState> InspectAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        SetupDiagnostics.Current.Record(SetupEvent.InspectStarted);
        // Fixed, read-only query. No user text, token, profile path, or downloaded script enters PowerShell.
        const string query = "$ErrorActionPreference='Stop'; Get-AppxPackage -Name 'OpenAI.Codex' | Where-Object { $_.Publisher -ceq 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B' -and $_.Status -eq 'Ok' } | Select-Object -First 1 | ForEach-Object { 'installed' }";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ProcessResult result;
        try { result = await RunSystemProcessAsync(PowerShellPath(), ["-NoLogo","-NoProfile","-NonInteractive","-Command",query], deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new SetupException("检查 ChatGPT 桌面版安装状态超时。请检查 Windows 应用安装服务后重试。"); }
        if (result.ExitCode != 0) throw new SetupException("无法检查 ChatGPT 桌面版安装状态。请检查 Windows 应用安装服务或联系管理员。");
        return new(result.Output.Split('\n').Any(line => line.Trim() == "installed"));
    }

    /// <summary>Elevates only the same executable's credential-free --install GUID entry point.</summary>
    public async Task<InstallState> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        progress?.Report(new("inspect", "正在检查 ChatGPT 桌面版 安装状态…"));
        var existing = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Installed) { SetupDiagnostics.Current.Record(SetupEvent.AlreadyInstalled); return existing; }
        SetupDiagnostics.Current.Record(SetupEvent.InstallStarted);
        cancellationToken.ThrowIfCancellationRequested();
        var executable = RuntimeCompat.ProcessPath;
        if (executable is null || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new SetupException("请运行完整的 Windows 安装工具。");
        var id = Guid.NewGuid().ToString("N");
        using var pipe = WindowsInstallPipe.CreateServer(id);
        var isAdministrator = WindowsInstallPipe.IsAdministrator();
        progress?.Report(new("authorizing", isAdministrator ? "正在准备安装…" : "请在 Windows 授权窗口中确认安装。"));
        Process helper;
        try
        {
            helper = await Task.Run(() => Process.Start(WindowsInstallerPolicy.CreateHelperStartInfo(executable, Guid.ParseExact(id, "N"), isAdministrator))
                ?? throw new SetupException("无法启动安装进程。"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { throw new SetupException("已取消管理员授权，账户配置尚未开始。"); }
        catch (Win32Exception) { throw new SetupException(isAdministrator ? "无法启动安装进程，请检查组织策略或联系管理员。" : "无法请求管理员授权，请检查组织策略或联系管理员。"); }
        using (helper)
        {
            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    do
                    {
                        await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
                        if (WindowsInstallPipe.IsExpectedClient(pipe, helper.Id)) break;
                        pipe.Disconnect(); // A random local client cannot spoof success or progress.
                    } while (!connectTimeout.IsCancellationRequested);
                }
                catch (OperationCanceledException) { throw new SetupException("安装进程未能连接。请关闭工具后重试。"); }
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\n" };
                // After elevation cancellation is a request, not a claim that deployment was rolled back.
                var cancellationSent = false;
                using var registration = cancellationToken.Register(() =>
                {
                    lock (writer)
                    {
                        if (cancellationSent) return;
                        cancellationSent = true;
                        try { writer.WriteLine("cancel"); } catch (IOException) { }
                    }
                });
                InstallMessage? terminal = null;
                try
                {
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (line.Length > 16384) throw new SetupException("安装进度通道异常，请重新运行。");
                        var message = JsonSerializer.Deserialize<InstallMessage>(line);
                        if (message?.Type == "diagnostic" && message.Event is { } diagnostic) SetupDiagnostics.Current.Record(diagnostic, message.Code);
                        else if (message?.Type == "progress" && message.Progress is not null) progress?.Report(message.Progress);
                        else if (message?.Type is "success" or "cancelled" or "error") { terminal = message; break; }
                        else throw new SetupException("安装进度通道异常，请重新运行。");
                    }
                }
                catch (IOException) { throw new SetupException("与安装进程的连接已断开。请重新打开工具检查安装状态。"); }
                await RuntimeCompat.WaitForExitAsync(helper, CancellationToken.None).ConfigureAwait(false);
                SetupDiagnostics.Current.Record(SetupEvent.HelperExit, helper.ExitCode);
                SetupDiagnostics.Current.Record(terminal?.Type switch { "success" => SetupEvent.HelperSuccess, "cancelled" => SetupEvent.HelperCancelled, _ => SetupEvent.HelperError });
                if (terminal?.Type == "cancelled") throw new OperationCanceledException("已取消下载安装。", cancellationToken);
                if (terminal?.Type != "success" || helper.ExitCode != 0)
                    throw new SetupException(terminal?.Error ?? "Windows 未能确认安装成功，账户配置尚未开始。请重试或联系管理员。");
                // Query in the original user identity, including when UAC used a different administrator.
                var registered = await InspectAsync(CancellationToken.None).ConfigureAwait(false);
                SetupDiagnostics.Current.Record(registered.Installed ? SetupEvent.Registered : SetupEvent.SignOutRequired);
                if (terminal.NeedsRestart) SetupDiagnostics.Current.Record(SetupEvent.RestartRequired);
                return new(true, !registered.Installed, terminal.NeedsRestart);
            }
            finally
            {
                // Disconnect on every exit path so a downloading helper cancels.
                // Keep the GUI (and its extracted dependencies) alive until the
                // helper exits; an in-flight DISM operation must finish normally.
                try { pipe.Dispose(); }
                finally { await RuntimeCompat.WaitForExitAsync(helper, CancellationToken.None).ConfigureAwait(false); }
            }
        }
    }

    /// <summary>Call before creating any GUI or reading any configuration/token. Return this process exit code.</summary>
    public static async Task<int> RunElevatedInstallAsync(string pipeId, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var name = WindowsInstallPipe.Name(pipeId);
        if (!WindowsInstallPipe.IsAdministrator()) return 1;
        // Explicit Anonymous SQOS prevents even a malicious local pipe server from impersonating this elevated process.
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try { await pipe.ConnectAsync(30000, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException) { return 1; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deploying = 0;
        var cancellationGate = new object();
        await using var channel = new InstallChannel(pipe, () => { lock(cancellationGate) { if (deploying == 0) cancellation.Cancel(); } });
        var progress = new PipeProgress(channel.Writer);
        string? directory = null;
        try
        {
            directory = WindowsInstallPipe.CreatePrivateDirectory();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseDefaultCredentials = false, MaxConnectionsPerServer = 3 }) { Timeout = TimeSpan.FromMinutes(20) };
            var architecture = RuntimeCompat.OsArchitecture switch
            {
                Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => throw new SetupException("此工具仅支持 x64 或 ARM64 Windows。"),
            };
            var sources = WindowsInstallerPolicy.ResolveSources(architecture);
            var packagePath = Path.Combine(directory,"ChatGPT.msix");
            var licensePath = Path.Combine(directory,"ChatGPT-License.xml");
            await WindowsInstallerPolicy.DownloadAsync(client, sources.Package, packagePath, WindowsInstallerPolicy.MaximumPackageBytes, progress, cancellation.Token).ConfigureAwait(false);
            await WindowsInstallerPolicy.DownloadAsync(client, sources.License, licensePath, WindowsInstallerPolicy.MaximumLicenseBytes, progress, cancellation.Token).ConfigureAwait(false);
            progress.Report(new("verify", "正在核对官方安装包身份…"));
            cancellation.Token.ThrowIfCancellationRequested();
            var package = WindowsInstallerPolicy.ValidatePackage(packagePath, architecture, Environment.OSVersion.Version, sources.Package.Version);
            // Once Windows servicing begins, do not kill DISM or pretend cancellation undid its changes.
            lock(cancellationGate) { cancellation.Token.ThrowIfCancellationRequested(); deploying = 1; }
            progress.Report(new("deploy", "正在安装 ChatGPT 桌面版。请保持窗口打开，Windows 会验证签名与许可证。"));
            var dism = Path.Combine(Environment.SystemDirectory,"dism.exe");
            var deployed = await RunSystemProcessAsync(dism, ["/Online","/English","/Add-ProvisionedAppxPackage","/PackagePath:"+packagePath,"/LicensePath:"+licensePath,"/Region:all","/NoRestart"], CancellationToken.None).ConfigureAwait(false);
            progress.TrySend(new("diagnostic", Event: SetupEvent.DeploymentExit, Code: deployed.ExitCode));
            if (deployed.ExitCode is not (0 or 3010)) throw new SetupException("Windows 无法安装 ChatGPT 桌面版。请检查系统更新、磁盘空间及组织的安装策略，再重试。");
            progress.Report(new("confirm", "正在确认 Windows 安装结果…"));
            var receipt = await RunSystemProcessAsync(dism, ["/Online","/English","/Get-ProvisionedAppxPackages"], CancellationToken.None).ConfigureAwait(false);
            progress.TrySend(new("diagnostic", Event: SetupEvent.ReceiptExit, Code: receipt.ExitCode));
            var needsRestart = WindowsInstallerPolicy.ValidateDeployment(deployed.ExitCode, receipt.ExitCode == 0 && WindowsInstallerPolicy.IsProvisioned(receipt.Output, package));
            progress.Send(new("success", NeedsRestart: needsRestart));
            return 0;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref deploying) == 0 && cancellation.IsCancellationRequested)
        { progress.TrySend(new("cancelled")); return 2; }
        catch (Exception error)
        {
            progress.TrySend(new("diagnostic", Event: SetupEvent.HelperException, Code: error.HResult));
            var message = error switch
            {
                SetupException => error.Message,
                InvalidDataException => "安装文件的大小、哈希、身份或系统版本校验失败。请重新下载或联系支持。",
                HttpRequestException or OperationCanceledException => "安装文件下载失败或超时。请检查网络或代理后重试。",
                _ => "安装未完成。请检查磁盘空间、Windows 更新和组织策略后重试。",
            };
            progress.TrySend(new("error", Error: message));
            return 1;
        }
        finally
        {
            // This directory contains only official installation artifacts, never credentials.
            if (directory is not null) { try { Directory.Delete(directory,true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    private static async Task<ProcessResult> RunSystemProcessAsync(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var argument in arguments) RuntimeCompat.AddArgument(start, argument);
        using var process = Process.Start(start) ?? throw new SetupException("无法启动 Windows 安装服务。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await RuntimeCompat.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(); } catch (InvalidOperationException) { } throw; }
        var output = await stdout.ConfigureAwait(false);
        await stderr.ConfigureAwait(false); // Drain diagnostics without writing them to logs or GUI.
        return new(process.ExitCode, output);
    }

    private static string PowerShellPath() => Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
    [SupportedOSPlatformGuard("windows")]
    private static bool IsWindows => RuntimeCompat.IsWindows;
    private static void EnsureWindows() { if (!IsWindows) throw new SetupException("安装功能只能在 Windows 上运行。"); }
    private sealed record ProcessResult(int ExitCode, string Output);
    private sealed record InstallMessage(string Type, SetupProgress? Progress=null, string? Error=null, bool NeedsRestart=false, SetupEvent? Event=null, int? Code=null);
    private sealed class PipeProgress(StreamWriter writer) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value) => Send(new("progress",value));
        internal void Send(InstallMessage value) { lock (writer) { writer.WriteLine(JsonSerializer.Serialize(value)); } }
        internal void TrySend(InstallMessage value) { try { Send(value); } catch (IOException) { } }
    }
}
