using AiDesktopSetup.Core;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Tests;

public class SetupDiagnosticsTests
{
    [Fact]
    public void RecordsUsefulCodesWithoutAcceptingFreeformUserData()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SetupDiagnostics(directory);
            log.Record(SetupEvent.DeploymentExit, 3010);
            log.Record(SetupEvent.ElevatedLaunch);
            var content = File.ReadAllText(log.FilePath);
            Assert.Contains("DeploymentExit code=3010", content);
            Assert.Contains("ElevatedLaunch code=-", content);
            // A Framework test AppDomain can have no entry assembly; production logs that as unknown.
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
            Assert.Contains("build=" + version + "\n", content);
            Assert.DoesNotContain("build=0.1.1", content);
            Assert.DoesNotContain(directory, content);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact] public void ProtocolDiagnosticsDropsRequestIdsAndUnknownRemoteCodes()
    {
        var directory = Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SetupDiagnostics(directory);
            log.RecordProtocolFailure(new("access_denied","FAKE_secret_request_id"));
            log.RecordProtocolFailure(new("FAKE_remote_secret_code","FAKE_secret_request_id"));
            var text = File.ReadAllText(log.FilePath);
            Assert.Contains("ProtocolFailure code=403",text); Assert.Contains("ProtocolFailure code=0",text); Assert.DoesNotContain("FAKE_",text);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void UnwritableLogDoesNotFailInstallation()
    {
        var path = Path.GetTempFileName();
        try { new SetupDiagnostics(path).Record(SetupEvent.HelperSuccess); }
        finally { File.Delete(path); }
    }
}
