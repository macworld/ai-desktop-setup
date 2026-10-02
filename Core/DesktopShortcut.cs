using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace AiDesktopSetup.Core;

public enum ShortcutResult { Created, Exists, Unavailable }

[SupportedOSPlatform("windows")]
public static class DesktopShortcut
{
    public static ShortcutResult Create(string appId, string desktop)
    {
        if (!Regex.IsMatch(appId, @"\AOpenAI\.Codex_2p2nqsd0c76g0![A-Za-z0-9.]+\z"))
            throw new ArgumentException("Unexpected app identity.", nameof(appId));
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) return ShortcutResult.Unavailable;
        return ShortcutFile.Create(desktop, path => SaveAppLink(appId, path));
    }

    private static void SaveAppLink(string appId, string path)
    {
        // Target the registered app, not a versioned WindowsApps executable. Windows
        // supplies its application icon and keeps the link valid after app updates.
        Marshal.ThrowExceptionForHR(SHParseDisplayName("shell:AppsFolder\\" + appId, 0, out var itemId, 0, out _));
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
            ((IShellLink)link).SetIDList(itemId);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FreeCoTaskMem(itemId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint itemId, uint attributes, out uint resultAttributes);

    // Only the first three methods are used; keep their native vtable order.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, nint findData, uint flags);
        void GetIDList(out nint itemId);
        void SetIDList(nint itemId);
    }
}

public static class ShortcutFile
{
    public static ShortcutResult Create(string desktop, Action<string> save)
    {
        var target = Path.Combine(desktop, "ChatGPT (Codex).lnk");
        // Preserve an existing shortcut from the earlier assistant as well as a new one.
        if (RuntimeCompat.PathExists(target) || RuntimeCompat.PathExists(Path.Combine(desktop, "Codex.lnk"))) return ShortcutResult.Exists;
        var temporary = Path.Combine(desktop, ".ai_gateway-" + Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            save(temporary);
            try { File.Move(temporary, target); }
            catch (IOException) when (RuntimeCompat.PathExists(target)) { return ShortcutResult.Exists; }
            return ShortcutResult.Created;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
