using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AiDesktopSetup.Core;

public enum InstallationOperation { Provision }
/// <summary>Untrusted credential-free IPC. The helper independently reconstructs policy and verifies the protected copy.</summary>
public sealed record InstallHelperRequest(string PackagePath,string? LicensePath,string PackageSha256,string? LicenseSha256,string AppId,InstallationOperation Operation)
{
    internal void Validate()
    {
        static bool Digest(string? value)=>value!=null && System.Text.RegularExpressions.Regex.IsMatch(value,@"\A[0-9a-f]{64}\z");
        static bool LocalPath(string? value)=>value!=null && value.Length>=3 && value.Length<=4096 && value.IndexOf('\0')<0 && System.IO.Path.IsPathRooted(value)
            && !value.StartsWith(@"\\",StringComparison.Ordinal) && !value.StartsWith("//",StringComparison.Ordinal)
            && value.IndexOf(':',2)<0 && (!RuntimeCompat.IsWindows || (value[1]==':' && (value[2]=='\\' || value[2]=='/')));
        if (AppId!="codex-desktop" || Operation!=InstallationOperation.Provision || !LocalPath(PackagePath) || !LocalPath(LicensePath)
            || !Digest(PackageSha256) || !Digest(LicenseSha256) || PackagePath==LicensePath) throw new SetupException("Invalid installation request.");
    }
    public string ToWire()
    {
        Validate();
        var wire = System.Text.Json.JsonSerializer.Serialize(this);
        if(System.Text.Encoding.UTF8.GetByteCount(wire)>16384) throw new SetupException("Installation request exceeds its limit.");
        return wire;
    }
    internal static InstallHelperRequest Parse(byte[] bytes)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bytes);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var allowed = new[] { "PackagePath", "LicensePath", "PackageSha256", "LicenseSha256", "AppId", "Operation" };
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                    throw new SetupException("Invalid installation request.");
            }
            if (names.Count != allowed.Length) throw new SetupException("Invalid installation request.");
            var request = System.Text.Json.JsonSerializer.Deserialize<InstallHelperRequest>(bytes)
                ?? throw new SetupException("Invalid installation request.");
            request.Validate();
            return request;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            throw new SetupException("Invalid installation request.");
        }
    }
    public override string ToString()=>"[installation artifacts]";
}


/// <summary>The pipe transports one bounded artifact request, progress, and cancellation; never credentials or remote commands.</summary>
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

    internal static string CreateDownloadDirectory()
    {
        var sid=WindowsIdentity.GetCurrent().User?.Value ?? throw new SetupException("Cannot identify the Windows user.");
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AiDesktopSetupPackages."+Guid.NewGuid().ToString("N"));
        AiDesktopSetup.Core.Recovery.ResumeFileSecurity.CheckAncestors(Path.GetDirectoryName(path)!);
        // Package bytes only: alternate UAC administrators can read, but cannot replace the user's staging files.
        using var security=new SecurityDescriptor($"D:P(A;OICI;FA;;;{sid})(A;OICI;FRFX;;;BA)(A;OICI;FRFX;;;SY)");
        var attributes=security.Attributes;
        if(!CreateDirectoryW(path,ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path;
    }

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
