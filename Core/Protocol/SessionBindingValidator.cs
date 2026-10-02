using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Recovery;
namespace AiDesktopSetup.Core.Protocol;
public sealed class SessionBindingValidator
{
    private readonly IClock clock;
    private readonly SessionAdapterPolicy policy;
    public SessionBindingValidator(IClock clock, SessionAdapterPolicy policy)
    { this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.policy = policy ?? throw new ArgumentNullException(nameof(policy)); }
    public ValidatedSession Validate(SetupCode code, SessionSnapshot snapshot, SessionSnapshot? original = null)
    {
        if (code is null || snapshot is null) throw new ProtocolException();
        var urls = SetupUrlValidator.Validate(snapshot.SetupBaseUrl, snapshot.ApiBaseUrl, code.CredentialType);
        if (urls.SetupBaseUrl != code.SetupBaseUrl || urls.ApiBaseUrl != code.ApiBaseUrl || snapshot.AppId != code.AppId) throw new ProtocolException();
        var now = clock.UtcNow;
        if (now >= snapshot.ExpiresAt || (original == null && snapshot.ExpiresAt > now.AddHours(2))) throw new ProtocolException("invalid_credential");
        if (snapshot.Client.ReasoningEffort != null && !policy.SupportedReasoningEfforts.Contains(snapshot.Client.ReasoningEffort, StringComparer.Ordinal)) throw new ProtocolException("configuration_unsupported");
        if (policy.RequiresLicense && snapshot.Mirrors.Any(m => m.License is null)) throw new ProtocolException("configuration_unsupported");
        if (snapshot.Selection.KeyHint.Contains(code.Credential.Value) || snapshot.Service.Name.Contains(code.Credential.Value) || snapshot.Selection.KeyLabel.Contains(code.Credential.Value) || snapshot.Selection.GroupLabel.Contains(code.Credential.Value)) throw new ProtocolException();
        var links = new List<string?> { snapshot.Service.WebsiteUrl, snapshot.Service.HelpUrl };
        foreach (var mirror in snapshot.Mirrors) { links.Add(mirror.Package.Url); links.Add(mirror.License?.Url); }
        foreach (var link in links)
            if (link != null && Uri.UnescapeDataString(link).Contains(code.Credential.Value)) throw new ProtocolException();
        if (original != null)
        {
            if (snapshot.State < original.State || !SameConfiguration(snapshot, original)) throw new ProtocolException();
        }
        return new(snapshot, code.CredentialType);
    }
    internal static bool SameConfiguration(SessionSnapshot first, SessionSnapshot second)
    {
        var a = JsonNode.Parse(first.ToWire().GetRawText())!.AsObject(); var b = JsonNode.Parse(second.ToWire().GetRawText())!.AsObject();
        a.Remove("state"); b.Remove("state"); return JsonNode.DeepEquals(a, b);
    }
}
