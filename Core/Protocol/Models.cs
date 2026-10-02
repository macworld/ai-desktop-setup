using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDesktopSetup.Core.Protocol;
public enum CredentialType { SetupTicket, ApiKey }
public enum SessionState { Claimed, CredentialsReleased, Completed }
public enum LocalStage { Created, Authenticated, PackageReady, Installed, ConfigPrepared, ConfigCommitted, Completed }
public sealed class ProtocolException : Exception { public string Code { get; } public ProtocolException(string code = "invalid_request") : base("AGSP validation failed.") { Code = code; } }
public sealed class ApiCredential
{
    [JsonIgnore] public string Value { get; }
    public ApiCredential(string value) { StrictJson.Bearer(value); Value = value; }
    public override string ToString() => "[redacted]";
}
public sealed class ResumeSecret
{
    private readonly byte[] bytes;
    [JsonIgnore] public byte[] Bytes => (byte[])bytes.Clone();
    public ResumeSecret(byte[] value) { if (value is null || value.Length != 32) throw new ProtocolException(); bytes = (byte[])value.Clone(); }
    public static ResumeSecret Parse(string bearer) => new(StrictJson.DecodeBase64Url(bearer));
    public string ToBearer() => StrictJson.EncodeBase64Url(bytes);
    public override string ToString() => "[redacted]";
}
public readonly record struct ResumeId
{
    public Guid Value { get; }
    public ResumeId(Guid value) { if (value == Guid.Empty) throw new ProtocolException(); Value = value; }
    public override string ToString() => Value.ToString("D");
}
public sealed record ValidatedBaseUrls(string SetupBaseUrl, string ApiBaseUrl);
public sealed class SetupCode
{
    public int Version => 1;
    public string InstallationId { get; }
    public string SetupBaseUrl { get; }
    public string ApiBaseUrl { get; }
    public string ServiceName { get; }
    public string AppId => "codex-desktop";
    public CredentialType CredentialType { get; }
    [JsonIgnore] public ApiCredential Credential { get; }
    [JsonIgnore] public string OriginalCode { get; }
    internal SetupCode(string original, string id, ValidatedBaseUrls urls, string name, CredentialType type, ApiCredential credential)
    { OriginalCode = original; InstallationId = id; SetupBaseUrl = urls.SetupBaseUrl; ApiBaseUrl = urls.ApiBaseUrl; ServiceName = name; CredentialType = type; Credential = credential; }
    // Only explicit wire construction can expose the bearer. Never serialize this object for diagnostics.
    public JsonElement ToWire() => StrictJson.Wire(new { version = 1, installation_id = InstallationId, setup_base_url = SetupBaseUrl, api_base_url = ApiBaseUrl, service_name = ServiceName, app_id = AppId, credential = new { type = CredentialType == CredentialType.SetupTicket ? "setup_ticket" : "api_key", value = Credential.Value } });
    public override string ToString() => "[redacted]";
}
public sealed record ServiceInfo(string Name, bool LogoAvailable, string? WebsiteUrl, string? HelpUrl);
public sealed record SelectionInfo(string GroupLabel, string KeyLabel, string KeyHint);
public sealed record ClientConfiguration(string Model, string WireApi, string? ReasoningEffort);
public sealed record RemoteArtifact(string Url, long Bytes, string Sha256);
public sealed record PackageMirror(string AppId, string Architecture, string Version, RemoteArtifact Package, RemoteArtifact? License);
public sealed class SessionSnapshot
{
    public const int MaximumNetworkBytes = 131072;
    // Default JSON escaping expands a one-byte character to at most six bytes.
    // Account for normalized hosts and inserted optional-field defaults separately.
    internal const int MaximumStoredBytes = MaximumNetworkBytes * 6 + 4096;
    public int Version => 1;
    public string SessionId { get; }
    public SessionState State { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Revision { get; }
    public string SetupBaseUrl { get; }
    public string ApiBaseUrl { get; }
    public string AppId => "codex-desktop";
    public ServiceInfo Service { get; }
    public SelectionInfo Selection { get; }
    public ClientConfiguration Client { get; }
    public IReadOnlyList<PackageMirror> Mirrors { get; }
    private readonly JsonElement wire;
    internal SessionSnapshot(JsonElement wire, string id, SessionState state, DateTimeOffset expiry, string revision, ValidatedBaseUrls urls, ServiceInfo service, SelectionInfo selection, ClientConfiguration client, PackageMirror[] mirrors)
    { this.wire = wire; SessionId = id; State = state; ExpiresAt = expiry; Revision = revision; SetupBaseUrl = urls.SetupBaseUrl; ApiBaseUrl = urls.ApiBaseUrl; Service = service; Selection = selection; Client = client; Mirrors = Array.AsReadOnly(mirrors); }
    public static SessionSnapshot Parse(byte[] bytes) => SessionSnapshotParser.Parse(bytes);
    internal static SessionSnapshot ParseStored(byte[] bytes) => SessionSnapshotParser.Parse(bytes, MaximumStoredBytes);
    public JsonElement ToWire() => wire.Clone();
}
public sealed class SessionAdapterPolicy
{
    public IReadOnlyCollection<string> SupportedReasoningEfforts { get; }
    public bool RequiresLicense { get; }
    public SessionAdapterPolicy(IEnumerable<string> efforts, bool requiresLicense)
    { if (efforts is null) throw new ArgumentNullException(nameof(efforts)); SupportedReasoningEfforts = Array.AsReadOnly(efforts.ToArray()); RequiresLicense = requiresLicense; }
}
public sealed class ValidatedSession
{
    public SessionSnapshot Snapshot { get; }
    public CredentialType CredentialType { get; }
    internal ValidatedSession(SessionSnapshot snapshot, CredentialType type) { Snapshot = snapshot; CredentialType = type; }
}
public sealed record ClientIdentity(string AssistantVersion, string Architecture);
public enum CredentialDeliveryMode { Server, ProvidedByClient }
public sealed class CredentialDelivery
{
    public string SessionId { get; } public string ApiBaseUrl { get; } public CredentialDeliveryMode Delivery { get; }
    [JsonIgnore] public ApiCredential Credential { get; }
    public CredentialDelivery(string sessionId, string apiBaseUrl, CredentialDeliveryMode delivery, ApiCredential credential)
    { SessionId = sessionId; ApiBaseUrl = apiBaseUrl; Delivery = delivery; Credential = credential; }
    public override string ToString() => "[redacted]";
}
public sealed record ProtocolError(string Code, string RequestId);
public sealed class SessionAccess
{
    public string SetupBaseUrl => Session.Snapshot.SetupBaseUrl;
    public string ApiBaseUrl => Session.Snapshot.ApiBaseUrl;
    public string SessionId => Session.Snapshot.SessionId;
    public DateTimeOffset ExpiresAt => Session.Snapshot.ExpiresAt;
    public CredentialType CredentialType => Session.CredentialType;
    [JsonIgnore] public ValidatedSession Session { get; }
    [JsonIgnore] public ResumeSecret ResumeSecret { get; }
    [JsonIgnore] public ApiCredential? OriginalCredential { get; }
    public SessionAccess(ValidatedSession session, ResumeSecret secret) : this(session, secret, null, false) { }
    public SessionAccess(ValidatedSession session, ResumeSecret secret, ApiCredential originalCredential) : this(session, secret, originalCredential, true) { }
    private SessionAccess(ValidatedSession session, ResumeSecret secret, ApiCredential? originalCredential, bool provided)
    {
        if (session == null || secret == null || (session.CredentialType == CredentialType.ApiKey ? !provided || originalCredential == null : provided)) throw new ProtocolException();
        Session = session; ResumeSecret = secret; OriginalCredential = originalCredential;
    }
    public override string ToString() => "[redacted]";
}
public sealed record AuthenticatedSetup(ResumeId ResumeId, SessionAccess Access, ValidatedSession Session);
public sealed record SetupPreview(SetupCode Code, string SetupOrigin, string ApiBaseUrl, string UnverifiedServiceName);
public sealed class ClaimRecord
{
    public ResumeId ResumeId { get; } public Guid ClaimId { get; } [JsonIgnore] public ResumeSecret ResumeSecret { get; }
    public ClaimRecord(ResumeId id, Guid claimId, ResumeSecret secret)
    { var b = claimId.ToByteArray(); if ((b[7] & 0xf0) != 0x40 || (b[8] & 0xc0) != 0x80) throw new ProtocolException(); ResumeId = id; ClaimId = claimId; ResumeSecret = secret; }
    public override string ToString() => "[redacted]";
}
public sealed class ResumeRecord
{
    public ClaimRecord Claim { get; } [JsonIgnore] public SetupCode Code { get; }
    public DateTimeOffset CreatedAt { get; } public DateTimeOffset ExpiresAt { get; } public LocalStage LocalStage { get; } public SessionSnapshot? Snapshot { get; } public InstallState? Installation { get; }
    public ResumeRecord(ClaimRecord claim, SetupCode code, DateTimeOffset createdAt, DateTimeOffset expiresAt, LocalStage stage, SessionSnapshot? snapshot = null, InstallState? installation = null)
    { Claim = claim; Code = code; CreatedAt = createdAt; ExpiresAt = expiresAt; LocalStage = stage; Snapshot = snapshot; Installation = installation; }
    public ResumeRecord Authenticate(ValidatedSession session)
    {
        if (session.CredentialType != Code.CredentialType || session.Snapshot.SetupBaseUrl != Code.SetupBaseUrl || session.Snapshot.ApiBaseUrl != Code.ApiBaseUrl) throw new ProtocolException();
        return new(Claim, Code, CreatedAt, session.Snapshot.ExpiresAt, LocalStage < LocalStage.Authenticated ? LocalStage.Authenticated : LocalStage, session.Snapshot, Installation);
    }
    public ResumeRecord WithInstallation(InstallState installation) => new(Claim, Code, CreatedAt, ExpiresAt, LocalStage, Snapshot, installation);
    public ResumeRecord Advance(LocalStage stage) => stage < LocalStage || !Enum.IsDefined(typeof(LocalStage), stage) ? throw new ProtocolException() : new(Claim, Code, CreatedAt, ExpiresAt, stage, Snapshot, Installation);
    public override string ToString() => "[redacted]";
}
