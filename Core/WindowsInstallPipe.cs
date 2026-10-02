using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AiDesktopSetup.Core;

/// <summary>The pipe transports progress and one fixed cancellation signal, never credentials or installation parameters.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInstallPipe
{
    internal static string Name(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid)) throw new SetupException("安装通道不合法，请从正常窗口重新运行工具。");
        return "AiDesktopSetup." + guid.ToString("N");
    }

    internal static NamedPipeServerStream CreateServer(string id)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SetupException("无法识别当前 Windows 用户。");
        // Alternate administrator credentials used in UAC must also be able to connect.
        using var security = new SecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{sid})");
        var attributes = security.Attributes;
        var handle = CreateNamedPipeW(@"\\.\pipe\" + Name(id), 0x00000003 | 0x40000000 | 0x00080000,
            0x00000008, 1, 16384, 16384, 0, ref attributes); // duplex, overlapped, first instance, reject remote clients
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
        catch { handle.Dispose(); throw; }
    }

    internal static bool IsExpectedClient(NamedPipeServerStream pipe, int processId) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var actual) && actual == (uint)processId;

    internal static string CreatePrivateDirectory()
    {
        var parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(parent)) throw new SetupException("无法找到受保护的安装目录。");
        var path = Path.Combine(parent, "AiDesktopSetupInstall." + Guid.NewGuid().ToString("N"));
        // The owner is Administrators, not the original split-token user's SID.
        using var security = new SecurityDescriptor("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        var attributes = security.Attributes;
        if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path;
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }
    private sealed class SecurityDescriptor : IDisposable
    {
        private IntPtr pointer;
        internal SecurityAttributes Attributes => new() { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pointer };
        internal SecurityDescriptor(string sddl)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out pointer, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { if (pointer != IntPtr.Zero) { LocalFree(pointer); pointer = IntPtr.Zero; } }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, ref SecurityAttributes security);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
