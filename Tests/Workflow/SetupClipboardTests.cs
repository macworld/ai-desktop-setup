using AiDesktopSetup.Core.Workflow;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Tests.Protocol;
namespace AiDesktopSetup.Tests.Workflow;
public sealed class SetupClipboardTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void AcceptedCodeIsClearedPromptlyAndLaterUserContentSurvives(bool apiKey)
    {
        var code = ProtocolFixtures.CodeText();
        if (apiKey)
        {
            var json = StrictJson.Parse(StrictJson.DecodeBase64Url(code.Substring(6)), 8192);
            var text = json.GetRawText().Replace("setup_ticket", "api_key");
            code = "AGSP1." + StrictJson.EncodeBase64Url(System.Text.Encoding.UTF8.GetBytes(text));
        }
        string live = code; var clears = 0;
        var clipboard = new SetupClipboard(() => live, () => { live = ""; clears++; });
        clipboard.Accept(code);
        Assert.Equal("", live); Assert.Equal(1, clears);
        live = "unrelated"; clipboard.TryClear(); // failure/completion retries
        Assert.Equal("unrelated", live); Assert.Equal(1, clears);
    }
    [Fact] public void ChangedClipboardIsPreserved()
    {
        var live = "unrelated";
        var clipboard = new SetupClipboard(() => live, () => live = "");
        clipboard.Accept(ProtocolFixtures.CodeText()); Assert.Equal("unrelated", live);
    }
    [Fact] public void ContentionIsSafeAndCanRetryOnFailureOrCompletion()
    {
        var code = ProtocolFixtures.CodeText(); var live = code; var busy = true;
        var clipboard = new SetupClipboard(() => busy ? throw new System.Runtime.InteropServices.ExternalException() : live, () => live = "");
        clipboard.Accept(code); Assert.Equal(code, live);
        busy = false; clipboard.TryClear(); Assert.Equal("", live);
    }
    [Fact] public void InvalidInputDoesNotClearClipboard()
    {
        var live = "invalid"; var clipboard = new SetupClipboard(() => live, () => live = "");
        Assert.Throws<ProtocolException>(() => clipboard.Accept(live)); Assert.Equal("invalid", live);
    }
}
