using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Xunit.Abstractions;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace AiDesktopSetup.Tests.Windows;
public sealed class NativeHostTests(ITestOutputHelper output)
{
    [Fact] public void HostAndOperatingSystemAreTheRequiredNativeArchitecture()
    {
        Assert.Equal(PlatformID.Win32NT, Environment.OSVersion.Platform);
#if NATIVE_ARM64
        const ushort expected = 0xaa64; const Architecture architecture = Architecture.Arm64;
#else
        const ushort expected = 0x8664; const Architecture architecture = Architecture.X64;
#endif
        Assert.Equal(8, IntPtr.Size); Assert.Equal(architecture, RuntimeInformation.ProcessArchitecture);
        Assert.True(IsWow64Process2(Process.GetCurrentProcess().Handle, out var process, out var native));
        output.WriteLine($"Process machine: 0x{process:x}; OS machine: 0x{native:x}; process architecture: {RuntimeInformation.ProcessArchitecture}");
        Assert.Equal(expected, native); Assert.Equal((ushort)0, process); Assert.Equal(expected, process == 0 ? native : process);
        var version = new OsVersion { Size = Marshal.SizeOf(typeof(OsVersion)), ServicePack = "" };
        Assert.Equal(0, RtlGetVersion(ref version));
        var target = (TargetFrameworkAttribute)Attribute.GetCustomAttribute(typeof(NativeHostTests).Assembly, typeof(TargetFrameworkAttribute))!;
        var release = Convert.ToInt32(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0));
        output.WriteLine($"Native OS: {version.Major}.{version.Minor}.{version.Build}; Framework target: {target.FrameworkName}; installed release: {release}");
        output.WriteLine("Host: " + Process.GetCurrentProcess().MainModule!.FileName);
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules) if (string.Equals(module.ModuleName, "clr.dll", StringComparison.OrdinalIgnoreCase)) output.WriteLine("CLR module: " + module.FileName);
        Assert.Equal(10u, version.Major);
#if NATIVE_ARM64
        Assert.True(version.Build >= 22621); Assert.True(release >= 533320); Assert.Contains("v4.8.1", target.FrameworkName);
#else
        Assert.InRange(version.Build, 19041u, 21999u); Assert.True(release >= 528040); Assert.Equal(".NETFramework,Version=v4.8", target.FrameworkName);
#endif
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("ntdll.dll")] private static extern int RtlGetVersion(ref OsVersion version);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OsVersion { public int Size; public uint Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; }
}
