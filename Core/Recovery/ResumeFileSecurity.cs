using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AiDesktopSetup.Core.Recovery;
internal static class ResumeFileSecurity
{
    internal static void EnsureDirectory(string root)
    {
        CheckAncestors(root);
        if (!Directory.Exists(root))
        {
            if (RuntimeCompat.IsWindows)
            {
                var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current user is unavailable.");
                var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(root).Create(security);
            }
#if !NETFRAMEWORK
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#else
            else throw new PlatformNotSupportedException();
#endif
        }
        ValidatePrivate(root, true);
    }
    internal static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
            if (RuntimeCompat.PathExists(current.FullName) && (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("Resume path is unsafe.");
    }
    internal static void ValidatePrivate(string path, bool directory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Resume path is unsafe.");
        if (RuntimeCompat.IsWindows)
        {
            var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("Current user is unavailable.");
            FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (!sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))) || (directory && !acl.AreAccessRulesProtected)) throw new IOException("Resume ACL is unsafe.");
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            var fullControl = false;
            foreach (var rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (!sid.Equals(rule.IdentityReference)) throw new IOException("Resume ACL is unsafe.");
                fullControl |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            if (!fullControl) throw new IOException("Resume ACL is unsafe.");
        }
#if !NETFRAMEWORK
        else
        {
            var expected = directory ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(path) != expected) throw new IOException("Resume permissions are unsafe.");
        }
#endif
    }
    // Windows opens the reparse object itself, then inspects the handle. Parent handles
    // disallow deletion/rename for the duration of a transaction, including ancestor swaps.
    internal static IDisposable PinDirectories(string root)
    {
        var handles = new List<IDisposable>();
        if (!RuntimeCompat.IsWindows) { CheckAncestors(root); return new Handles(handles); }
        try
        {
            for (var current = new DirectoryInfo(root); current != null; current = current.Parent)
            {
                var handle = Open(current.FullName, 0x80, 3, 3, 0x02200000); CheckHandle(handle); handles.Add(handle);
            }
            return new Handles(handles);
        }
        catch { foreach (var h in handles) h.Dispose(); throw; }
    }
    internal static FileStream OpenRead(string path)
    {
        if (!RuntimeCompat.IsWindows)
        { ValidatePrivate(path, false); return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        var handle = Open(path, 0x80000000, 1, 3, 0x00200000);
        try { CheckHandle(handle); ValidatePrivate(path, false); return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal static FileStream OpenLock(string path)
    {
        if (RuntimeCompat.IsWindows)
        {
            var handle = Open(path, 0xc0000000, 0, 4, 0x00200000);
            try { CheckHandle(handle); ValidatePrivate(path, false); return new FileStream(handle, FileAccess.ReadWrite); }
            catch { handle.Dispose(); throw; }
        }
#if !NETFRAMEWORK
        if (RuntimeCompat.PathExists(path)) ValidatePrivate(path, false);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        return new FileStream(path, options);
#else
        throw new PlatformNotSupportedException();
#endif
    }
    internal static void WriteNew(string path, byte[] bytes)
    {
#if NETFRAMEWORK
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
#else
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!RuntimeCompat.IsWindows) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
#endif
        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
    }
    internal static void AtomicReplace(string source, string destination)
    {
        if (RuntimeCompat.PathExists(destination)) ValidatePrivate(destination, false);
        if (RuntimeCompat.IsWindows)
        { if (!MoveFileEx(source, destination, 1 | 8)) throw new IOException("Resume commit failed."); }
#if !NETFRAMEWORK
        else File.Move(source, destination, true);
#else
        else throw new PlatformNotSupportedException();
#endif
    }
    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Resume file cannot be opened safely."); } return handle;
    }
    private static void CheckHandle(SafeFileHandle handle)
    { if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) { handle.Dispose(); throw new IOException("Resume path is unsafe."); } }
    private sealed class Handles : IDisposable { private readonly List<IDisposable> handles; internal Handles(List<IDisposable> handles) => this.handles = handles; public void Dispose() { foreach (var h in handles) h.Dispose(); } }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative { internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFileEx(string from, string to, uint flags);
}
