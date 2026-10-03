using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiDesktopSetup.Core.Protocol;

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

    /// <summary>Inspects only the original user's registration; never elevates.</summary>
    public async Task<MachineState> InspectMachineAsync(CancellationToken cancellationToken=default)
    {
        EnsureWindows();
        var architecture=RuntimeCompat.OsArchitecture switch { Architecture.X64=>"x64",Architecture.Arm64=>"arm64",_=>"unsupported" };
        var sid=WindowsIdentity.GetCurrent().User?.Value ?? throw new SetupException("Cannot identify the current Windows user.");
        const string query="$ErrorActionPreference='Stop'; Get-AppxPackage -Name 'OpenAI.Codex' | Where-Object { $_.Publisher -ceq 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B' -and $_.PackageFamilyName -ceq 'OpenAI.Codex_2p2nqsd0c76g0' -and $_.Status -eq 'Ok' } | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1 | ForEach-Object { $_.Name; $_.Publisher; $_.PackageFamilyName; $_.Version.ToString(); $_.Architecture.ToString().ToLowerInvariant() }";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var result=await RunSystemProcessAsync(PowerShellPath(),["-NoLogo","-NoProfile","-NonInteractive","-Command",query],deadline.Token).ConfigureAwait(false);
        if (result.ExitCode!=0) throw new SetupException("Cannot inspect the current user's package registration.");
        var fields=result.Output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
        InstalledPackage? installed=null;
        if (fields.Length==5 && CodexOfficialPolicy.TryVersion(fields[3],out var version)) installed=new(fields[0],fields[1],fields[2],version,fields[4],true);
        else if(fields.Length!=0) throw new SetupException("Package inspection returned an invalid result.");
        return new(true,Environment.OSVersion.Version,architecture,RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),sid,installed);
    }
    public async Task<InstallState> InspectAsync(CancellationToken cancellationToken=default)
    {
        SetupDiagnostics.Current.Record(SetupEvent.InspectStarted);
        var machine=await InspectMachineAsync(cancellationToken).ConfigureAwait(false);
        if(machine.OsArchitecture is not ("x64" or "arm64")) return new(false);
        var policy=CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture);
        return new(CodexOfficialPolicy.Matches(machine.InstalledPackage,policy) && machine.InstalledPackage!.Version>=policy.MinimumVersion);
    }
    /// <summary>Registration runs in this user process, including after another administrator supplies UAC credentials.</summary>
    public async Task<InstallState> RegisterAsync(CancellationToken cancellationToken=default)
    {
        EnsureWindows();
        const string command="$ErrorActionPreference='Stop'; Add-AppxPackage -RegisterByFamilyName -MainPackage 'OpenAI.Codex_2p2nqsd0c76g0'";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var result=await RunSystemProcessAsync(PowerShellPath(),["-NoLogo","-NoProfile","-NonInteractive","-Command",command],deadline.Token).ConfigureAwait(false);
        var registered=await InspectAsync(cancellationToken).ConfigureAwait(false);
        return registered.Installed ? registered : new(false,NeedsSignOut:result.ExitCode!=0 || !registered.Installed);
    }

    /// <summary>Elevates only the same executable's credential-free --install GUID entry point.</summary>
    public async Task<InstallState> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var machine=await InspectMachineAsync(cancellationToken).ConfigureAwait(false);
        return await InstallAsync(CodexOfficialPolicy.Official(CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture)),progress,cancellationToken).ConfigureAwait(false);
    }
    public async Task<InstallState> InstallAsync(InstallationPlan plan,IProgress<SetupProgress>? progress,CancellationToken cancellationToken=default)
    {
        EnsureWindows();
        var machine=await InspectMachineAsync(cancellationToken).ConfigureAwait(false);
        if(plan.Action==InstallationAction.Unsupported || plan.Policy.Architecture!=machine.OsArchitecture) throw new SetupException("This Windows machine is unsupported.");
        if(plan.Action==InstallationAction.Register) return await RegisterAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new("inspect", "正在检查 ChatGPT 桌面版 安装状态…"));
        var existing = new InstallState(CodexOfficialPolicy.Matches(machine.InstalledPackage, plan.Policy) && machine.InstalledPackage!.RegisteredForCurrentUser && machine.InstalledPackage.Version >= plan.Policy.MinimumVersion);
        if (existing.Installed && (plan.Package?.Version==null || machine.InstalledPackage!.Version>=Version.Parse(plan.Package.Version))) { SetupDiagnostics.Current.Record(SetupEvent.AlreadyInstalled); return existing; }
        if(plan.Action==InstallationAction.Skip) throw new SetupException("Package registration changed; inspect and resolve the installation plan again.");
        var downloadDirectory=WindowsInstallPipe.CreateDownloadDirectory();
        try
        {
            using var clients=new ProtocolHttpClients();
            using var prepared=await new PackageTrustVerifier().PrepareAsync(clients,plan,machine,downloadDirectory,progress,cancellationToken).ConfigureAwait(false);
            return await InstallPreparedAsync(prepared,progress,cancellationToken).ConfigureAwait(false);
        }
        finally { try { Directory.Delete(downloadDirectory,true); } catch(IOException) {} catch(UnauthorizedAccessException) {} }
    }
    /// <summary>Original user retains PreparedInstallation locks until the helper exits.</summary>
    public async Task<InstallState> InstallPreparedAsync(PreparedInstallation prepared,IProgress<SetupProgress>? progress,CancellationToken cancellationToken=default)
    {
        EnsureWindows();
        if (WindowsIdentity.GetCurrent().User?.Value!=prepared.OriginalUserSid) throw new SetupException("Installation must remain in the original user session.");
        var requestWire=prepared.CreateHelperRequest().ToWire();
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
                await writer.WriteLineAsync(requestWire).ConfigureAwait(false);
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
                    throw WindowsInstallerPolicy.DeploymentFailure(terminal?.Error);
                // Query in the original user identity, including when UAC used a different administrator.
                var registered = await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
                SetupDiagnostics.Current.Record(registered.Installed ? SetupEvent.Registered : SetupEvent.SignOutRequired);
                if (terminal.NeedsRestart) SetupDiagnostics.Current.Record(SetupEvent.RestartRequired);
                return WindowsInstallerPolicy.RegistrationOutcome(registered, terminal.NeedsRestart);
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
        InstallHelperRequest request;
        using(var requestTimeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { request=await InstallChannel.ReadRequestAsync(pipe,requestTimeout.Token).ConfigureAwait(false); }
            catch(Exception error) when(error is SetupException or IOException or OperationCanceledException) { return 1; }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deploying = 0;
        var cancellationGate = new object();
        await using var channel = new InstallChannel(pipe, () => { lock(cancellationGate) { if (deploying == 0) cancellation.Cancel(); } });
        var progress = new PipeProgress(channel.Writer);
        string? directory = null;
        try
        {
            directory = WindowsInstallPipe.CreatePrivateDirectory();
            var machine=await InspectHelperMachineAsync(cancellation.Token).ConfigureAwait(false);
            progress.Report(new("verify", "正在验证受保护的官方安装包…"));
            using var prepared=await new PackageTrustVerifier().PrepareHelperCopyAsync(request,machine,directory,cancellation.Token).ConfigureAwait(false);
            var package=new WindowsPackageIdentity(prepared.Package.Version.ToString(),prepared.Package.Architecture);
            var packagePath=prepared.Package.Path;var licensePath=prepared.LicensePath;
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
                SetupException or PackageTrustException => error.Message,
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

    private static async Task<MachineState> InspectHelperMachineAsync(CancellationToken token)
    {
        var arch=RuntimeCompat.OsArchitecture switch { Architecture.X64=>"x64",Architecture.Arm64=>"arm64",_=>throw new SetupException("Unsupported Windows architecture.") };
        const string query="$ErrorActionPreference='Stop'; Get-AppxPackage -AllUsers -Name 'OpenAI.Codex' | Where-Object { $_.Publisher -ceq 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B' -and $_.PackageFamilyName -ceq 'OpenAI.Codex_2p2nqsd0c76g0' -and $_.Status -eq 'Ok' } | ForEach-Object { $_.Version.ToString() }; Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -ceq 'OpenAI.Codex' -and $_.PackageName -match '__2p2nqsd0c76g0$' } | ForEach-Object { $_.Version.ToString() }";
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var result=await RunSystemProcessAsync(PowerShellPath(),["-NoLogo","-NoProfile","-NonInteractive","-Command",query],deadline.Token).ConfigureAwait(false);
        if(result.ExitCode!=0) throw new SetupException("Cannot inspect machine package versions before deployment.");
        Version? newest=null;
        foreach(var value in result.Output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        { if(!CodexOfficialPolicy.TryVersion(value,out var version)) throw new SetupException("Invalid machine package version.");if(newest==null || version>newest) newest=version; }
        var policy=CodexOfficialPolicy.ForArchitecture(arch);
        InstalledPackage? installed=newest==null?null:new(policy.IdentityName,policy.Publisher,policy.FamilyName,newest,arch,false);
        return new(true,Environment.OSVersion.Version,arch,RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),WindowsIdentity.GetCurrent().User?.Value??"",installed);
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
