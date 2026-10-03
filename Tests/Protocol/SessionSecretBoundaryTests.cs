using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Tests.Protocol;

public sealed class SessionSecretBoundaryTests
{
    private const string SetupCredential = "EXAMPLE_SETUP_CREDENTIAL";
    private const string ProvidedCredential = "EXAMPLE_PROVIDED_API_KEY";
    private static readonly ResumeSecret Proof = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task KnownSetupCredentialInSessionIdIsRejectedBeforeSessionRequest(bool apiKey, bool embedded)
    {
        var code = Code(apiKey); var snapshot = Snapshot(embedded ? "ins_" + SetupCredential + "_suffix" : SetupCredential);
        Assert.Equal(embedded ? "ins_EXAMPLE_SETUP_CREDENTIAL_suffix" : "EXAMPLE_SETUP_CREDENTIAL", snapshot.SessionId);
        await RejectBeforeSessionRequest(() =>
        {
            var session = ProtocolFixtures.Validator().Validate(code, snapshot);
            return apiKey ? new SessionAccess(session, Proof, code.Credential) : new SessionAccess(session, Proof);
        }, snapshot);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ResumeBearerInSessionIdIsRejectedBeforeSessionRequest(bool embedded)
    {
        var snapshot = Snapshot(embedded ? "ins_" + Proof.ToBearer() + "_suffix" : Proof.ToBearer());
        var session = ProtocolFixtures.Validator().Validate(Code(false), snapshot);
        await RejectBeforeSessionRequest(() => new SessionAccess(session, Proof), snapshot);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ProvidedApiKeyInSessionIdIsRejectedBeforeSessionRequest(bool embedded)
    {
        var snapshot = Snapshot(embedded ? "ins_" + ProvidedCredential + "_suffix" : ProvidedCredential);
        var session = ProtocolFixtures.Validator().Validate(Code(true), snapshot);
        await RejectBeforeSessionRequest(() => new SessionAccess(session, Proof, new ApiCredential(ProvidedCredential)), snapshot);
    }

    private static async Task RejectBeforeSessionRequest(Func<SessionAccess> access, SessionSnapshot snapshot)
    {
        var calls = 0;
        using var clients = new ProtocolHttpClients(new Handler(() => calls++, snapshot), new Handler(() => calls++, snapshot), new Handler(() => calls++, snapshot));
        var client = new SetupSessionClient(clients, _ => { }, new FixedClock());
        var error = await Assert.ThrowsAsync<ProtocolException>(async () => await client.GetSessionAsync(access(), default));
        Assert.Equal("invalid_request", error.Code); Assert.Equal(0, calls);
    }

    private static SetupCode Code(bool apiKey)
    {
        var json = JsonNode.Parse(ProtocolFixtures.Code().ToWire().GetRawText())!;
        json["credential"]!["type"] = apiKey ? "api_key" : "setup_ticket";
        json["credential"]!["value"] = SetupCredential;
        return SetupCodeParser.Parse("AGSP1." + StrictJson.EncodeBase64Url(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
    private static SessionSnapshot Snapshot(string id)
    {
        var json = ProtocolFixtures.SessionJson(); json["session_id"] = id;
        return ProtocolFixtures.Session(json);
    }
    private sealed class Handler(Action called, SessionSnapshot snapshot) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ProtocolFixtures.Wire(snapshot.ToWire())) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
