#if !NETFRAMEWORK
using System.Diagnostics;
using System.Collections;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Xml.Linq;
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
        Assert.Equal("x64", x64.GetProperty("PlatformTarget").GetString());
        Assert.Equal("ARM64", arm64.GetProperty("PlatformTarget").GetString());
        Assert.Equal("", x64.GetProperty("RuntimeIdentifier").GetString());
        Assert.Equal("", arm64.GetProperty("RuntimeIdentifier").GetString());
        Assert.False(bool.Parse(x64.GetProperty("SelfContained").GetString()!));
        Assert.False(bool.Parse(arm64.GetProperty("SelfContained").GetString()!));
        Assert.Equal("AI.Desktop.Setup", x64.GetProperty("AssemblyName").GetString());
        Assert.Equal("AiDesktopSetup", x64.GetProperty("RootNamespace").GetString());
    }
    [Theory]
    [InlineData("x64", "win-x64")]
    [InlineData("ARM64", "win-arm64")]
    public void ExplicitRuntimeIdentifiersArePreserved(string platform, string runtimeIdentifier)
    {
        Assert.Equal(runtimeIdentifier, Evaluate(platform, runtimeIdentifier).GetProperty("RuntimeIdentifier").GetString());
    }
    [Fact]
    public void ReleaseAssembliesDoNotExposePrivateBuildPaths()
    {
        using var stream = File.OpenRead(typeof(AiDesktopSetup.Core.RuntimeCompat).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        Assert.DoesNotContain(pe.ReadDebugDirectory(), entry => entry.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.CodeView);
    }

    [Theory]
    [InlineData("x64")]
    [InlineData("ARM64")]
    public async Task PublishedWindowIconResolvesFromCompiledWpfResources(string platform)
    {
        var root = SourceRoot.Find();
        var output = Path.Combine(Path.GetTempPath(), "ai-setup-window-resources-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "publish", Path.Combine(root, "App", "AI.Desktop.Setup.csproj"), "-c", "Release", "-p:Platform=" + platform, "-nodeReuse:false", "-p:UseSharedCompilation=false", "--nologo", "-o", output })
                AiDesktopSetup.Core.RuntimeCompat.AddArgument(start, argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await stdout + await stderr);

            // The PE application icon does not satisfy a WPF pack URI. Resolve the
            // actual window URI against the published managed resource bundle.
            var iconUri = XDocument.Load(Path.Combine(root, "App", "MainWindow.xaml")).Root!.Attribute("Icon")!.Value;
            using var file = File.OpenRead(Path.Combine(output, "AI.Desktop.Setup.exe"));
            using var pe = new PEReader(file);
            Assert.Equal(platform == "x64" ? Machine.Amd64 : Machine.Arm64, pe.PEHeaders.CoffHeader.Machine);
            var metadata = pe.GetMetadataReader();
            var resource = metadata.ManifestResources.Select(metadata.GetManifestResource)
                .Single(value => metadata.GetString(value.Name) == "AI.Desktop.Setup.g.resources");
            Assert.True(resource.Implementation.IsNil);
            var data = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress)
                .GetReader(checked((int)resource.Offset), checked((int)(pe.PEHeaders.CorHeader.ResourcesDirectory.Size - resource.Offset)));
            var length = data.ReadInt32();
            using var bundle = new MemoryStream(data.ReadBytes(length));
            using var resources = new ResourceReader(bundle);
            var entries = resources.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => entry.Value);
            var resourceName = iconUri.Replace('\\', '/').ToLowerInvariant();
            Assert.True(entries.ContainsKey(resourceName), "Window icon pack URI is missing from the published WPF resources: " + resourceName);
            using var icon = Assert.IsAssignableFrom<Stream>(entries[resourceName]);
            using var bytes = new MemoryStream(); icon.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, "App", iconUri)), bytes.ToArray());
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, recursive: true); }
    }

    private static JsonElement Evaluate(string platform, string? runtimeIdentifier = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "msbuild", Path.Combine(SourceRoot.Find(), "App", "AI.Desktop.Setup.csproj"), "-nologo", "-p:Platform=" + platform, "-getProperty:TargetFramework,PlatformTarget,RuntimeIdentifier,SelfContained,AssemblyName,RootNamespace" })
            AiDesktopSetup.Core.RuntimeCompat.AddArgument(start, argument);
        if (runtimeIdentifier is not null) AiDesktopSetup.Core.RuntimeCompat.AddArgument(start, "-p:RuntimeIdentifier=" + runtimeIdentifier);
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
