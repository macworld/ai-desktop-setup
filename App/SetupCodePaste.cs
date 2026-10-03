using System.Windows;
using System.Windows.Controls;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Workflow;
namespace AiDesktopSetup;

/// <summary>Both button and native PasswordBox paste use the same accepted-code boundary.</summary>
internal sealed class SetupCodePaste
{
    private readonly PasswordBox box;
    private readonly Action preview;
    private readonly Func<string> read;
    private readonly SetupClipboard clipboard;
    internal SetupCodePaste(PasswordBox box, Action preview, Func<string> read, Action clear)
    {
        this.box = box; this.preview = preview; this.read = read;
        clipboard = new(read, clear);
        DataObject.AddPastingHandler(box, OnPaste);
    }
    internal void PasteFromClipboard() => Accept(read());
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // Handle Ctrl-V and the native context-menu command before WPF inserts
        // credentials. Invalid text may still be edited, but is never accepted.
        e.CancelCommand();
        try
        {
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                Accept((string)e.DataObject.GetData(DataFormats.UnicodeText));
            else if (e.DataObject.GetDataPresent(DataFormats.Text))
                Accept((string)e.DataObject.GetData(DataFormats.Text));
        }
        catch (System.Runtime.InteropServices.ExternalException) { } // Clipboard contention must not escape the routed event.
    }
    private void Accept(string text)
    {
        box.Password = text;
        preview();
        try { clipboard.Accept(text); }
        catch (ProtocolException) { } // Preview already reports invalid input.
    }
    internal void TryClear() => clipboard.TryClear();
}
