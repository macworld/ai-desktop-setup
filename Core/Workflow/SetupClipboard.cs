using AiDesktopSetup.Core.Protocol;
namespace AiDesktopSetup.Core.Workflow;

/// <summary>Best-effort cleanup of the live clipboard only; OS history and cloud copies are outside this boundary.</summary>
public sealed class SetupClipboard
{
    private readonly Func<string> read;
    private readonly Action clear;
    private string? accepted;
    public SetupClipboard(Func<string> read, Action clear) { this.read = read; this.clear = clear; }
    public void Accept(string code)
    {
        SetupCodeParser.Parse(code);
        accepted = code;
        TryClear();
    }
    public void TryClear()
    {
        if (accepted == null) return;
        try
        {
            if (string.Equals(read(), accepted, StringComparison.Ordinal)) clear();
            accepted = null;
        }
        catch (System.Runtime.InteropServices.ExternalException) { } // Another process owns the clipboard; retry at the next local boundary.
        catch (System.Threading.ThreadStateException) { }
    }
}
