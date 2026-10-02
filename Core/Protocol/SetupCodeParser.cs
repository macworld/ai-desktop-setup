namespace AiDesktopSetup.Core.Protocol;
public static class SetupCodeParser
{
    public static SetupCode Parse(string text)
    {
        if (text is null) throw new ProtocolException(); var trimmed = text.Trim();
        if (trimmed.Length > 16384 || !trimmed.StartsWith("AGSP1.", StringComparison.Ordinal)) throw new ProtocolException();
        var bytes = StrictJson.DecodeBase64Url(trimmed.Substring(6));
        var json = StrictJson.Object(StrictJson.Parse(bytes, 8192)); StrictJson.Version(json); StrictJson.App(json);
        var credential = StrictJson.Object(StrictJson.Required(json, "credential"));
        var type = StrictJson.String(credential, "type") switch { "setup_ticket" => CredentialType.SetupTicket, "api_key" => CredentialType.ApiKey, _ => throw new ProtocolException() };
        var value = StrictJson.String(credential, "value"); StrictJson.Bearer(value);
        var urls = SetupUrlValidator.Validate(StrictJson.String(json, "setup_base_url"), StrictJson.String(json, "api_base_url"), type);
        var id = StrictJson.Identifier(json, "installation_id"); var name = StrictJson.Text(json, "service_name", 100, label: true);
        if (Uri.UnescapeDataString(urls.SetupBaseUrl).Contains(value) || Uri.UnescapeDataString(urls.ApiBaseUrl).Contains(value) || id.Contains(value) || name.Contains(value)) throw new ProtocolException();
        return new(trimmed, id, urls, name, type, new ApiCredential(value));
    }
}
