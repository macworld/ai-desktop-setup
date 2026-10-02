using System.Runtime.InteropServices;
using System.Reflection;

namespace AiDesktopSetup.Core;

public enum SetupEvent
{
    Started, InspectStarted, AlreadyInstalled, InstallStarted, Download, Verify, Deploy, Confirm,
    DeploymentExit, ReceiptExit, HelperSuccess, HelperError, HelperCancelled, HelperExit,
    Registered, SignOutRequired, RestartRequired, ConfigurationStarted, ConfigurationComplete,
    ShortcutCreated, ShortcutExists, ShortcutUnavailable, UiError, HelperException, AppOpened, AppOpenFailed,
    ElevatedLaunch, StandardLaunch, ProtocolFailure,
}

/// <summary>Only fixed event names and numeric error codes; never messages, tokens, config or process output.</summary>
public sealed class SetupDiagnostics
{
    private static readonly string BuildVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
    public static SetupDiagnostics Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI Desktop Setup", "Setup", "logs"));
    public string FilePath { get; }
    private readonly object gate = new();

    public SetupDiagnostics(string directory)
    {
        FilePath = Path.Combine(directory, $"setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        Record(SetupEvent.Started);
    }

    public void RecordProtocolFailure(AiDesktopSetup.Core.Protocol.ProtocolError error)
    {
        var code = error.Code switch { "invalid_request" => 400, "invalid_credential" => 401, "access_denied" => 403, "claim_conflict" => 409, "configuration_unsupported" => 422, "rate_limited" => 429, "temporarily_unavailable" => 503, _ => 0 };
        Record(SetupEvent.ProtocolFailure,code);
    }

    public void Record(SetupEvent step, int? code = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= 524288) return;
                var name = Enum.IsDefined(typeof(SetupEvent), step) ? step.ToString() : "Unknown";
                File.AppendAllText(FilePath, $"{DateTime.UtcNow:O} {name} code={code?.ToString() ?? "-"} os={Environment.OSVersion.Version} arch={RuntimeInformation.OSArchitecture} build={BuildVersion}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
