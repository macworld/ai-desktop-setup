using AiDesktopSetup.Core;

namespace AiDesktopSetup.Tests;

public class ShortcutTests : IDisposable
{
    [Fact]
    public void KeepsLegacyCodexIconWithoutCreatingADuplicate()
    {
        File.WriteAllText(Path.Combine(desktop, "Codex.lnk"), "legacy");
        Assert.Equal(ShortcutResult.Exists, ShortcutFile.Create(desktop, _ => throw new Exception("Must preserve existing link")));
        Assert.Single(Directory.GetFiles(desktop));
    }

    private readonly string desktop = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "OneDrive", "桌面");
    public ShortcutTests() => Directory.CreateDirectory(desktop);
    public void Dispose() => Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(desktop)!)!, true);

    [Fact]
    public void CreatesAtResolvedDesktopAndPreservesExistingLink()
    {
        Assert.Equal(ShortcutResult.Created, ShortcutFile.Create(desktop, path => File.WriteAllText(path, "app-link")));
        Assert.Equal(ShortcutResult.Exists, ShortcutFile.Create(desktop, _ => throw new Exception("Must preserve existing link")));
        Assert.Equal("app-link", File.ReadAllText(Path.Combine(desktop, "ChatGPT (Codex).lnk")));
        Assert.Single(Directory.GetFiles(desktop));
    }

    [Fact]
    public void PartialShortcutIsRemovedIfSavingFails()
    {
        Assert.Throws<IOException>(() => ShortcutFile.Create(desktop, path => { File.WriteAllText(path, "partial"); throw new IOException(); }));
        Assert.Empty(Directory.GetFiles(desktop));
    }

    [Fact]
    public void RacingUserShortcutIsNeverOverwritten()
    {
        Assert.Equal(ShortcutResult.Exists, ShortcutFile.Create(desktop, path => {
            File.WriteAllText(path, "new"); File.WriteAllText(Path.Combine(desktop, "ChatGPT (Codex).lnk"), "user");
        }));
        Assert.Equal("user", File.ReadAllText(Path.Combine(desktop, "ChatGPT (Codex).lnk")));
        Assert.Single(Directory.GetFiles(desktop));
    }
}
