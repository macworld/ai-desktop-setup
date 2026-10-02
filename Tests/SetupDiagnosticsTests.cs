using AiDesktopSetup.Core;

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

    [Fact]
    public void UnwritableLogDoesNotFailInstallation()
    {
        var path = Path.GetTempFileName();
        try { new SetupDiagnostics(path).Record(SetupEvent.HelperSuccess); }
        finally { File.Delete(path); }
    }
}
