using System.Windows;
using System.Windows.Controls;
using AiDesktopSetup.Tests.Protocol;
namespace AiDesktopSetup.Tests.Windows;
public sealed class NativeSetupPasteTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ButtonAndNativePasswordBoxPasteClearAcceptedCode(bool native)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var text = ProtocolFixtures.CodeText(); var live = text; var previews = 0;
                var box = new PasswordBox();
                var paste = new SetupCodePaste(box, () => previews++, () => live, () => live = "");
                if (native)
                {
                    var data = new DataObject(DataFormats.UnicodeText, text);
                    var args = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
                    box.RaiseEvent(args);
                    Assert.True(args.CommandCancelled);
                }
                else paste.PasteFromClipboard();
                Assert.Equal(text, box.Password); Assert.Equal(1, previews); Assert.Equal("", live);
                live = "unrelated"; paste.TryClear(); Assert.Equal("unrelated", live);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
