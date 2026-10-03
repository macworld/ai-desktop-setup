using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDesktopSetup.Core.Recovery;

namespace AiDesktopSetup.Core.Protocol;
public sealed record SessionReceipt(int Version, string SessionId, string State);
public sealed class ProtocolHttpException : Exception
{
    public ProtocolError Error { get; }
    public TimeSpan? RetryAfter { get; }
    public ProtocolHttpException(ProtocolError error, TimeSpan? retryAfter = null) : base("Setup request failed.") { Error = error; RetryAfter = retryAfter; }
}

public interface ISetupSessionClient
{
    Task<SessionSnapshot> BootstrapAsync(SetupCode code, ClaimRecord claim, ClientIdentity client, CancellationToken ct);
    Task<SessionSnapshot> GetSessionAsync(SessionAccess access, CancellationToken ct);
    Task<CredentialDelivery> GetCredentialsAsync(SessionAccess access, CancellationToken ct);
    Task<SessionReceipt> CompleteAsync(SessionAccess access, CancellationToken ct);
    Task<SessionReceipt> CancelAsync(SessionAccess access, CancellationToken ct);
    Task<byte[]?> GetLogoAsync(SessionAccess access, CancellationToken ct);
}
public sealed class SetupSessionClient : ISetupSessionClient
{
    private readonly ProtocolHttpClients clients;
    private readonly Action<byte[]> decodeLogo;
    private readonly IClock clock;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public SetupSessionClient(ProtocolHttpClients clients, Action<byte[]> decodeLogo, IClock clock) : this(clients, decodeLogo, clock, Task.Delay) { }
    internal SetupSessionClient(ProtocolHttpClients clients, Action<byte[]> decodeLogo, IClock clock, Func<TimeSpan,CancellationToken,Task> delay)
    { this.clients = clients ?? throw new ArgumentNullException(nameof(clients)); this.decodeLogo = decodeLogo ?? throw new ArgumentNullException(nameof(decodeLogo)); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.delay = delay; }

    public async Task<SessionSnapshot> BootstrapAsync(SetupCode code, ClaimRecord claim, ClientIdentity client, CancellationToken ct)
    {
        var identity = StrictJson.Wire(new { assistant_version = client.AssistantVersion });
        StrictJson.Text(identity, "assistant_version");
        if (client.Architecture != "x64" && client.Architecture != "arm64") throw new ProtocolException();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, installation_id = code.InstallationId, claim_id = claim.ClaimId.ToString("D"), credential_type = code.CredentialType == CredentialType.SetupTicket ? "setup_ticket" : "api_key", setup_base_url = code.SetupBaseUrl, api_base_url = code.ApiBaseUrl, app_id = code.AppId, assistant_version = client.AssistantVersion, architecture = client.Architecture });
        if (body.Length > 16384) throw new ProtocolException();
        var bytes = await SendAsync(clients.Authenticated, code.SetupBaseUrl + "/bootstrap", code.Credential.Value, claim.ResumeSecret.ToBearer(), body, null, 131072, false, ct).ConfigureAwait(false);
        return SessionSnapshot.Parse(bytes);
    }
    public async Task<SessionSnapshot> GetSessionAsync(SessionAccess access, CancellationToken ct)
    {
        var bytes = await SessionRequest(access, null, null, ct).ConfigureAwait(false); var snapshot = SessionSnapshot.Parse(bytes);
        if (snapshot.SessionId != access.SessionId || snapshot.SetupBaseUrl != access.SetupBaseUrl || snapshot.ApiBaseUrl != access.ApiBaseUrl || snapshot.ExpiresAt != access.ExpiresAt || snapshot.State < access.Session.Snapshot.State || !SessionBindingValidator.SameConfiguration(snapshot,access.Session.Snapshot)) throw new ProtocolException();
        return snapshot;
    }
    public async Task<CredentialDelivery> GetCredentialsAsync(SessionAccess access, CancellationToken ct)
    {
        var value = StrictJson.Object(StrictJson.Parse(await SessionRequest(access, "/credentials", "{}", ct).ConfigureAwait(false)));
        StrictJson.Version(value); var id = StrictJson.Identifier(value, "session_id"); var url = SetupUrlValidator.Normalize(StrictJson.String(value, "api_base_url"), false);
        if (id != access.SessionId || url != access.ApiBaseUrl) throw new ProtocolException();
        var credential = StrictJson.Object(StrictJson.Required(value, "credential"));
        if (StrictJson.String(credential, "type") != "api_key") throw new ProtocolException();
        var delivery = StrictJson.String(credential, "delivery");
        if (access.CredentialType == CredentialType.SetupTicket)
        {
            if (delivery != "server") throw new ProtocolException();
            return new(id, url, CredentialDeliveryMode.Server, new ApiCredential(StrictJson.String(credential, "value")));
        }
        if (delivery != "provided_by_client" || credential.TryGetProperty("value", out _)) throw new ProtocolException();
        return new(id, url, CredentialDeliveryMode.ProvidedByClient, access.OriginalCredential!);
    }
    public Task<SessionReceipt> CompleteAsync(SessionAccess access, CancellationToken ct) => Receipt(access, "complete", "completed", ct);
    public Task<SessionReceipt> CancelAsync(SessionAccess access, CancellationToken ct) => Receipt(access, "cancel", "canceled", ct);
    private async Task<SessionReceipt> Receipt(SessionAccess access, string operation, string state, CancellationToken ct)
    {
        var value = StrictJson.Object(StrictJson.Parse(await SessionRequest(access, "/" + operation, "{}", ct).ConfigureAwait(false)));
        StrictJson.Version(value);
        if (StrictJson.Identifier(value, "session_id") != access.SessionId || StrictJson.String(value, "state") != state) throw new ProtocolException();
        return new(1, access.SessionId, state);
    }
    private Task<byte[]> SessionRequest(SessionAccess access, string? suffix, string? body, CancellationToken ct)
        => SendAsync(clients.Authenticated, SessionUrl(access) + suffix, access.ResumeSecret.ToBearer(), null, body == null ? null : Encoding.UTF8.GetBytes(body), access.ExpiresAt, 131072, false, ct);
    private static string SessionUrl(SessionAccess access) => access.SetupBaseUrl + "/sessions/" + access.SessionId;

    /// <summary>Decoder must fully decode PNG/JPEG and reject decoded dimensions outside 1..1024. WPF owns the native decoder.</summary>
    public async Task<byte[]?> GetLogoAsync(SessionAccess access, CancellationToken ct)
    {
        try
        {
            var bytes = await SendAsync(clients.Logo, SessionUrl(access) + "/logo", access.ResumeSecret.ToBearer(), null, null, access.ExpiresAt, 524288, true, ct).ConfigureAwait(false);
            // A cheap bounds screen precedes all native image decoding/allocation.
            if (!LogoDimensions(bytes)) return null;
            try { decodeLogo(bytes); } catch (Exception) { return null; }
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProtocolHttpException error) when (error.Error.Code == "invalid_credential" || error.Error.Code == "access_denied") { throw; }
        catch (Exception error) when (error is ProtocolException || error is ProtocolHttpException || error is HttpRequestException || error is OperationCanceledException || error is InvalidDataException || error is ArgumentException || error is NotSupportedException || error is System.Runtime.InteropServices.COMException || error is IOException) { return null; }
    }
    private async Task<byte[]> SendAsync(HttpClient client, string url, string bearer, string? proof, byte[]? body, DateTimeOffset? deadline, int limit, bool logo, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested(); EnsureDeadline(deadline);
            var remaining = deadline.HasValue ? deadline.Value - clock.UtcNow : TimeSpan.FromSeconds(30);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
            ProtocolHttpException? failure = null;
            try
            {
                using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (proof != null) request.Headers.Add("Setup-Session-Proof", proof);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(logo ? "image/png" : "application/json"));
                if (logo) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
                if (body != null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status >= 300 && status <= 399) throw new ProtocolException();
                byte[] bytes;
                // Authorization status remains terminal even if the server's error envelope is malformed.
                if (status == 401 || status == 403)
                {
                    var error = new ProtocolError(status == 401 ? "invalid_credential" : "access_denied", "");
                    try { bytes = await ReadBounded(response.Content, 131072, timeout.Token).ConfigureAwait(false); error = ParseError(response, bytes, new[] { bearer, proof ?? "" }); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception errorRead) when (errorRead is ProtocolException || errorRead is IOException || errorRead is HttpRequestException || errorRead is OperationCanceledException) { }
                    throw new ProtocolHttpException(error);
                }
                bytes = await ReadBounded(response.Content, status == 200 ? limit : 131072, timeout.Token).ConfigureAwait(false);
                if (response.Headers.CacheControl?.NoStore != true) throw new ProtocolException();
                if (status == 200)
                {
                    var media = response.Content.Headers.ContentType?.MediaType;
                    if (logo ? media != "image/png" && media != "image/jpeg" : media != "application/json") throw new ProtocolException();
                    if (logo && ((media == "image/png") != IsPng(bytes))) throw new ProtocolException();
                    EnsureDeadline(deadline); return bytes;
                }
                failure = new ProtocolHttpException(ParseError(response, bytes, new[] { bearer, proof ?? "" }), RetryAfter(response));
                if (status != 429 && status < 500) throw failure;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { failure = new(new("temporarily_unavailable", "")); }
            catch (HttpRequestException) { failure = new(new("temporarily_unavailable", "")); }
            catch (IOException) { ct.ThrowIfCancellationRequested(); failure = new(new("temporarily_unavailable", "")); }
            var wait = failure!.RetryAfter ?? TimeSpan.FromSeconds(attempt + 1);
            // Longer throttles belong to the UI. Never sleep past the fixed session deadline.
            if (attempt == 2 || wait > TimeSpan.FromSeconds(2) || (deadline.HasValue && clock.UtcNow + wait >= deadline.Value)) throw failure;
            await delay(wait, ct).ConfigureAwait(false);
        }
        throw new ProtocolException();
    }
    private void EnsureDeadline(DateTimeOffset? deadline)
    { if (deadline.HasValue && clock.UtcNow >= deadline.Value) throw new ProtocolHttpException(new("invalid_credential", "")); }
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        var time = value?.Delta ?? (value?.Date - clock.UtcNow);
        return time.HasValue && time.Value >= TimeSpan.Zero ? time : null;
    }
    internal static ProtocolError ParseError(HttpResponseMessage response, byte[] bytes, IEnumerable<string> secrets)
    {
        if (response.Headers.CacheControl?.NoStore != true || response.Content.Headers.ContentType?.MediaType != "application/json") throw new ProtocolException();
        var root = StrictJson.Object(StrictJson.Parse(bytes));
        if (root.EnumerateObject().Count() != 1) throw new ProtocolException();
        var error = StrictJson.Object(StrictJson.Required(root, "error"));
        if (error.EnumerateObject().Count() != 2) throw new ProtocolException();
        var code = StrictJson.String(error, "code"); var id = StrictJson.String(error, "request_id");
        if (!Regex.IsMatch(id,@"\A[A-Za-z0-9_-]+\z")) throw new ProtocolException();
        var status = (int)response.StatusCode;
        var expected = status switch { 400 => "invalid_request", 401 => "invalid_credential", 403 => "access_denied", 409 => "claim_conflict", 422 => "configuration_unsupported", 429 => "rate_limited", >= 500 and <= 599 => "temporarily_unavailable", _ => "" };
        if (code != expected || secrets.Any(s => s.Length != 0 && id.Contains(s))) throw new ProtocolException();
        return new(code, id);
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit || content.Headers.ContentEncoding.Count != 0) throw new ProtocolException();
        using var stream = await RuntimeCompat.ReadAsStreamAsync(content, ct).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await RuntimeCompat.ReadAsync(stream, buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length), ct).ConfigureAwait(false);
            if (count == 0) return output.ToArray(); output.Write(buffer, 0, count); if (output.Length > limit) throw new ProtocolException();
        }
    }
    private static bool IsPng(byte[] b) => b.Length >= 24 && new byte[] {137,80,78,71,13,10,26,10}.SequenceEqual(b.Take(8));
    public static bool HasBoundedLogoDimensions(byte[] b) => b != null && b.Length <= 524288 && LogoDimensions(b);
    private static bool LogoDimensions(byte[] b)
    {
        if (IsPng(b))
        {
            if (b.Length < 33 || !new byte[] {0,0,0,13,73,72,68,82}.SequenceEqual(b.Skip(8).Take(8))) return false;
            var w = Big32(b,16); var h = Big32(b,20); return w >= 1 && w <= 1024 && h >= 1 && h <= 1024;
        }
        if (b.Length < 4 || b[0] != 255 || b[1] != 216) return false;
        var offset = 2;
        while (offset + 3 < b.Length)
        {
            if (b[offset++] != 255) return false;
            while (offset < b.Length && b[offset] == 255) offset++;
            if (offset >= b.Length) return false;
            var marker = b[offset++];
            if (marker == 216 || marker == 1 || marker >= 208 && marker <= 215) continue;
            if (marker == 217 || marker == 218 || offset + 2 > b.Length) return false;
            var length = b[offset] * 256 + b[offset + 1];
            if (length < 2 || offset + length > b.Length) return false;
            if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204)
            {
                if (length < 8) return false;
                var h = b[offset+3]*256+b[offset+4]; var w = b[offset+5]*256+b[offset+6]; return w >= 1 && w <= 1024 && h >= 1 && h <= 1024;
            }
            offset += length;
        }
        return false;
    }
    private static uint Big32(byte[] b, int i) => ((uint)b[i]<<24)|((uint)b[i+1]<<16)|((uint)b[i+2]<<8)|b[i+3];
}
