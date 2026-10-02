using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiDesktopSetup.Core;

[SupportedOSPlatform("windows")]
public static class CodexLauncher
{
    public static void Open()
    {
        var classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        var interfaceId = typeof(IApplicationActivationManager).GUID;
        // The assistant exits immediately after activation. An out-of-process manager
        // keeps Windows' activation arguments alive independently of this process.
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref classId, 0, 4 /* CLSCTX_LOCAL_SERVER */, ref interfaceId, out var manager));
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication("OpenAI.Codex_2p2nqsd0c76g0!App", null, 2 /* AO_NOERRORUI */, out _));
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, nint outer, uint context, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}
