using AiDesktopSetup.Core;

namespace AiDesktopSetup.Tests;

public class InstallHelperLaunchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyRequestsElevationWhenNeededAndPassesNoAccountData(bool isAdministrator)
    {
        var executable = Path.Combine(Path.GetTempPath(), "安装 助手", "AI-Desktop-Setup.exe");
        var id = Guid.NewGuid();
        var start = WindowsInstallerPolicy.CreateHelperStartInfo(executable, id, isAdministrator);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(Path.GetDirectoryName(executable), start.WorkingDirectory);
        Assert.Equal(!isAdministrator, start.UseShellExecute);
        Assert.Equal(isAdministrator ? "" : "runas", start.Verb);
        Assert.Equal(new[] { "--install", id.ToString("N") }, TestCompat.GetArguments(start));
#if !NETFRAMEWORK
        Assert.Empty(start.Arguments);
#endif
        Assert.Empty(start.UserName);
    }

    [Fact]
    public void CompatibilityArgumentsKeepEmptyValuesQuotesAndTrailingBackslashes()
    {
        var start = new System.Diagnostics.ProcessStartInfo("helper.exe");
        var arguments = new[] { "", "--install", "安装 助手", "C:\\a path\\", "one\"two", "\\\\server\\a path\\" };
        foreach (var argument in arguments) RuntimeCompat.AddArgument(start, argument);
        Assert.Equal(arguments, TestCompat.GetArguments(start));
    }
}
