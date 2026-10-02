using System.Text.Json;

namespace AiDesktopSetup.Core.Protocol;
internal static class SessionSnapshotParser
{
    internal static SessionSnapshot Parse(byte[] bytes, int maximumBytes = SessionSnapshot.MaximumNetworkBytes)
    {
        var json = StrictJson.Object(StrictJson.Parse(bytes, maximumBytes)); StrictJson.Version(json); StrictJson.App(json);
        var id = StrictJson.Identifier(json, "session_id"); var stateText = StrictJson.String(json, "state");
        var state = stateText switch { "claimed" => SessionState.Claimed, "credentials_released" => SessionState.CredentialsReleased, "completed" => SessionState.Completed, _ => throw new ProtocolException() };
        var expiryText = StrictJson.String(json, "expires_at"); var expiry = StrictJson.UtcTime(expiryText);
        var revision = StrictJson.Text(json, "revision");
        var urls = SetupUrlValidator.Validate(StrictJson.String(json, "setup_base_url"), StrictJson.String(json, "api_base_url"), CredentialType.SetupTicket);
        var serviceJson = StrictJson.Object(StrictJson.Required(json, "service"));
        var logo = StrictJson.Required(serviceJson, "logo_available"); if (logo.ValueKind != JsonValueKind.True && logo.ValueKind != JsonValueKind.False) throw new ProtocolException();
        var service = new ServiceInfo(StrictJson.Text(serviceJson, "name", 100, label: true), logo.GetBoolean(), OptionalUrl(serviceJson, "website_url"), OptionalUrl(serviceJson, "help_url"));
        var selectionJson = StrictJson.Object(StrictJson.Required(json, "selection"));
        var selection = new SelectionInfo(StrictJson.Text(selectionJson, "group_label", 100, label: true), StrictJson.Text(selectionJson, "key_label", 100, label: true), StrictJson.Text(selectionJson, "key_hint", 32, empty: true, label: true));
        var clientJson = StrictJson.Object(StrictJson.Required(json, "client"));
        var wireApi = StrictJson.String(clientJson, "wire_api"); if (wireApi != "responses") throw new ProtocolException();
        var effort = clientJson.TryGetProperty("reasoning_effort", out _) ? StrictJson.Text(clientJson, "reasoning_effort") : null;
        var client = new ClientConfiguration(StrictJson.Text(clientJson, "model", 200), wireApi, effort);
        var preflight = StrictJson.Object(StrictJson.Required(json, "preflight")); if (StrictJson.String(preflight, "status") != "passed") throw new ProtocolException();
        var mirrors = new List<PackageMirror>(); var architectures = new HashSet<string>(StringComparer.Ordinal);
        if (json.TryGetProperty("mirrors", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 2) throw new ProtocolException();
            foreach (var mirrorJson in list.EnumerateArray())
            {
                StrictJson.App(mirrorJson); var architecture = StrictJson.String(mirrorJson, "architecture");
                if ((architecture != "x64" && architecture != "arm64") || !architectures.Add(architecture)) throw new ProtocolException();
                mirrors.Add(new("codex-desktop", architecture, StrictJson.Text(mirrorJson, "version"), Artifact(StrictJson.Required(mirrorJson, "package")), mirrorJson.TryGetProperty("license", out var license) ? Artifact(license) : null));
            }
        }
        var serviceWire = new Dictionary<string, object> { ["name"] = service.Name, ["logo_available"] = service.LogoAvailable };
        if (service.WebsiteUrl != null) serviceWire["website_url"] = service.WebsiteUrl; if (service.HelpUrl != null) serviceWire["help_url"] = service.HelpUrl;
        var clientWire = new Dictionary<string, object> { ["model"] = client.Model, ["wire_api"] = client.WireApi };
        if (effort != null) clientWire["reasoning_effort"] = effort;
        var wire = StrictJson.Wire(new { version = 1, session_id = id, state = stateText, expires_at = expiryText, revision, setup_base_url = urls.SetupBaseUrl, api_base_url = urls.ApiBaseUrl, app_id = "codex-desktop", service = serviceWire, selection = new { group_label = selection.GroupLabel, key_label = selection.KeyLabel, key_hint = selection.KeyHint }, client = clientWire, preflight = new { status = "passed" }, mirrors = mirrors.Select(MirrorWire).ToArray() });
        return new(wire, id, state, expiry, revision, urls, service, selection, client, mirrors.ToArray());
    }
    private static string? OptionalUrl(JsonElement json, string name) => json.TryGetProperty(name, out _) ? SetupUrlValidator.Asset(StrictJson.String(json, name)) : null;
    private static RemoteArtifact Artifact(JsonElement json)
    {
        StrictJson.Object(json); var bytes = StrictJson.Required(json, "bytes"); var hash = StrictJson.String(json, "sha256");
        if (bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out var length) || length <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z")) throw new ProtocolException();
        return new(SetupUrlValidator.Asset(StrictJson.String(json, "url")), length, hash);
    }
    private static object ArtifactWire(RemoteArtifact artifact) => new { url = artifact.Url, bytes = artifact.Bytes, sha256 = artifact.Sha256 };
    private static object MirrorWire(PackageMirror mirror)
    {
        var result = new Dictionary<string, object> { ["app_id"] = mirror.AppId, ["architecture"] = mirror.Architecture, ["version"] = mirror.Version, ["package"] = ArtifactWire(mirror.Package) };
        if (mirror.License != null) result["license"] = ArtifactWire(mirror.License); return result;
    }
}
