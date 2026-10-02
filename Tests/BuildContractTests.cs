#if !NETFRAMEWORK
using System.Diagnostics;
using System.Text.Json;
namespace AiDesktopSetup.Tests;
public partial class RuntimeCompatibilityTests
{
    [Fact]
    public void PreservesFrameworkTargets()
    {
        var x64 = Evaluate("x64"); var arm64 = Evaluate("ARM64");
        Assert.Equal("net48", x64.GetProperty("TargetFramework").GetString());
        Assert.Equal("net481", arm64.GetProperty("TargetFramework").GetString());
        Assert.False(bool.Parse(x64.GetProperty("SelfContained").GetString()!));
        Assert.False(bool.Parse(arm64.GetProperty("SelfContained").GetString()!));
        Assert.Equal("AI.Desktop.Setup", x64.GetProperty("AssemblyName").GetString());
        Assert.Equal("AiDesktopSetup", x64.GetProperty("RootNamespace").GetString());
    }
    [Fact]
    public void ReleaseAssembliesDoNotExposePrivateBuildPaths()
    {
        using var stream = File.OpenRead(typeof(AiDesktopSetup.Core.RuntimeCompat).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        Assert.DoesNotContain(pe.ReadDebugDirectory(), entry => entry.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView);
    }

    private static JsonElement Evaluate(string platform)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "msbuild", Path.Combine(SourceRoot.Find(), "App", "AI.Desktop.Setup.csproj"), "-nologo", "-p:Platform=" + platform, "-getProperty:TargetFramework,SelfContained,AssemblyName,RootNamespace" })
            AiDesktopSetup.Core.RuntimeCompat.AddArgument(start, argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return JsonDocument.Parse(output).RootElement.GetProperty("Properties").Clone();
    }
}
internal static class SourceRoot
{
    internal static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Source checkout required for build-contract tests.");
    }
}
#endif
