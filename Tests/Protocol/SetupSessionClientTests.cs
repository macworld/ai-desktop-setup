using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Tests.Protocol;
public sealed class SetupSessionClientTests
{
    private static readonly ResumeSecret Proof = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static SessionAccess Access(bool apiKey = false)
    {
        var codeJson = JsonNode.Parse(ProtocolFixtures.Code().ToWire().GetRawText())!;
        if (apiKey) codeJson["credential"]!["type"] = "api_key";
        var code = SetupCodeParser.Parse("AGSP1." + StrictJson.EncodeBase64Url(Encoding.UTF8.GetBytes(codeJson.ToJsonString())));
        var session = ProtocolFixtures.Validator().Validate(code, ProtocolFixtures.Session(), null);
        return apiKey ? new(session, Proof, code.Credential) : new(session, Proof);
    }
    private static HttpResponseMessage Response(byte[] body, string type = "application/json", int status = 200)
    {
        var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        result.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true }; return result;
    }
    private static SetupSessionClient Client(Func<HttpRequestMessage, HttpResponseMessage> response, Action<byte[]>? decode = null, FixedClock? clock = null, List<TimeSpan>? delays = null)
        => new(new ProtocolHttpClients(new Handler(response), new Handler(response), new Handler(response)), decode ?? (_ => { }), clock ?? new FixedClock(), (time, ct) => { ct.ThrowIfCancellationRequested(); delays?.Add(time); return Task.CompletedTask; });

    [Fact] public async Task DoesNotFollowCredentialRedirect()
    {
        var calls = 0;
        var client = Client(request => { calls++; var r = Response([], status: 302); r.Headers.Location = new("https://other.example/leak"); return r; });
        await Assert.ThrowsAsync<ProtocolException>(() => client.GetCredentialsAsync(Access(), default)); Assert.Equal(1, calls);
        using var clients = new ProtocolHttpClients();
        foreach (var handler in clients.Handlers) { Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies); Assert.False(handler.UseDefaultCredentials); Assert.Null(handler.Credentials); }
    }
    [Fact] public async Task PackageClientHasNoSecrets()
    {
        HttpRequestMessage? observed = null;
        using var clients = new ProtocolHttpClients(new Handler(_ => Response([])), new Handler(_ => Response([])), new Handler(request => { observed = request; return Response([]); }));
        using var response = await clients.GetPackageAsync("https://downloads.example/package.msix", default);
        Assert.Null(observed!.Headers.Authorization); Assert.False(observed.Headers.Contains("Cookie")); Assert.False(observed.Headers.Contains("Setup-Session-Proof"));
    }
    [Fact] public async Task BootstrapHasExactBindingAndProofWithoutSecretsInBody()
    {
        string? body = null; HttpRequestMessage? observed = null;
        var claim = new ClaimRecord(new ResumeId(Guid.NewGuid()), Guid.Parse("38d9d2dc-8571-4be8-960b-fef631ed7122"), Proof);
        var client = Client(request => { observed = request; body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Response(ProtocolFixtures.Wire(ProtocolFixtures.Session().ToWire())); });
        var result = await client.BootstrapAsync(ProtocolFixtures.Code(), claim, new("1.0.0", "x64"), default);
        Assert.Equal("ins_example", result.SessionId); Assert.Equal("https://gateway.example/api/ai-setup/v1/bootstrap", observed!.RequestUri!.AbsoluteUri);
        Assert.Equal(ProtocolFixtures.Code().Credential.Value, observed.Headers.Authorization!.Parameter); Assert.Equal(Proof.ToBearer(), Assert.Single(observed.Headers.GetValues("Setup-Session-Proof")));
        Assert.DoesNotContain(Proof.ToBearer(), body); Assert.DoesNotContain(ProtocolFixtures.Code().Credential.Value, body);
        var vector = (JsonElement)ProtocolFixtures.Cases("bootstrap").First(x => (string)x[0] == "bootstrap-body-x64")[1];
        ProtocolFixtures.Canonical(vector, JsonDocument.Parse(body!).RootElement);
    }
    [Theory] [InlineData("credentials")] [InlineData("complete")] [InlineData("cancel")]
    public async Task SessionPostIsExactlyEmptyAndUsesOnlyResumeBearer(string operation)
    {
        var observedBody = "";
        var client = Client(request => {
            Assert.Equal(Proof.ToBearer(), request.Headers.Authorization!.Parameter); Assert.False(request.Headers.Contains("Setup-Session-Proof"));
            observedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(Encoding.UTF8.GetBytes(operation == "credentials" ? "{\"version\":1,\"session_id\":\"ins_example\",\"api_base_url\":\"https://gateway.example/v1\",\"credential\":{\"type\":\"api_key\",\"delivery\":\"server\",\"value\":\"FAKE~+/==\"}}" : "{\"version\":1,\"session_id\":\"ins_example\",\"state\":\""+(operation == "complete" ? "completed" : "canceled")+"\"}"));
        });
        if (operation == "credentials") await client.GetCredentialsAsync(Access(), default);
        else if (operation == "complete") await client.CompleteAsync(Access(), default);
        else await client.CancelAsync(Access(), default);
        Assert.Equal("{}", observedBody);
    }
    [Fact] public void AccessRequiresOriginalKeyOnlyForApiKeyMode()
    {
        var access = Access(true); Assert.Throws<ProtocolException>(() => new SessionAccess(access.Session, Proof));
        Assert.Throws<ProtocolException>(() => new SessionAccess(Access().Session, Proof, new ApiCredential("FAKE")));
        Assert.DoesNotContain(access.OriginalCredential!.Value, JsonSerializer.Serialize(access));
    }
    [Theory] [MemberData(nameof(Credentials))]
    public async Task ExactCredentialVectors(string id, JsonElement input, JsonElement expected)
    {
        Assert.NotEmpty(id);
        var body = input.TryGetProperty("body", out var nested) ? nested : input;
        var bytes = ProtocolFixtures.Wire(body);
        if (input.TryGetProperty("wire_body_bytes", out var length)) bytes = Pad(bytes, length.GetInt32());
        var apiKey = input.TryGetProperty("credential_type", out var type) && type.GetString() == "api_key";
        var access = Access(apiKey);
        if (body.GetProperty("session_id").GetString() == "ins_cloud")
        {
            var json = ProtocolFixtures.SessionJson(); json["session_id"] = "ins_cloud"; json["setup_base_url"] = "https://edge.example:8443/Tenant/Setup"; json["api_base_url"] = "https://edge.example:8443/Tenant/v1/";
            var codeJson = JsonNode.Parse(ProtocolFixtures.Code().ToWire().GetRawText())!; codeJson["setup_base_url"] = json["setup_base_url"]!.DeepClone(); codeJson["api_base_url"] = json["api_base_url"]!.DeepClone(); codeJson["credential"]!["type"] = "api_key";
            var code = SetupCodeParser.Parse("AGSP1."+StrictJson.EncodeBase64Url(Encoding.UTF8.GetBytes(codeJson.ToJsonString())));
            access = new(ProtocolFixtures.Validator().Validate(code, ProtocolFixtures.Session(json), null), Proof, code.Credential);
        }
        var client = Client(_ => Response(bytes));
        if (!expected.GetProperty("accept").GetBoolean()) { var error = await Assert.ThrowsAsync<ProtocolException>(() => client.GetCredentialsAsync(access, default)); Assert.Equal(expected.GetProperty("error_code").GetString(), error.Code); }
        else
        {
            var delivery = await client.GetCredentialsAsync(access, default);
            Assert.Equal(body.GetProperty("session_id").GetString(), delivery.SessionId);
            Assert.Equal(body.GetProperty("api_base_url").GetString(), delivery.ApiBaseUrl);
            Assert.Equal(apiKey ? CredentialDeliveryMode.ProvidedByClient : CredentialDeliveryMode.Server, delivery.Delivery);
            Assert.Equal(apiKey ? access.OriginalCredential!.Value : body.GetProperty("credential").GetProperty("value").GetString(), delivery.Credential.Value);
        }
    }
    public static IEnumerable<object[]> Credentials() => ProtocolFixtures.Cases("credentials");
    [Theory] [MemberData(nameof(Errors))]
    public void ExactErrorVectors(string id, JsonElement input, JsonElement expected)
    {
        Assert.NotEmpty(id); using var response = Response(ProtocolFixtures.Wire(input.GetProperty("body")), status: input.GetProperty("http_status").GetInt32());
        if (!input.GetProperty("headers").TryGetProperty("Cache-Control", out _)) response.Headers.CacheControl = null;
        if (expected.GetProperty("accept").GetBoolean())
        { var error = SetupSessionClient.ParseError(response, ProtocolFixtures.Wire(input.GetProperty("body")), []); Assert.Equal(input.GetProperty("body").GetProperty("error").GetProperty("code").GetString(), error.Code); Assert.Equal("req_example", error.RequestId); }
        else Assert.Throws<ProtocolException>(() => SetupSessionClient.ParseError(response, ProtocolFixtures.Wire(input.GetProperty("body")), []));
    }
    public static IEnumerable<object[]> Errors() => ProtocolFixtures.Cases("error");
    [Theory] [InlineData(131073)] [InlineData(262144)]
    public async Task EnforcesResponseLimitsWithoutContentLength(int length)
    {
        var client = Client(_ => { var r = Response(Pad(ProtocolFixtures.Wire(ProtocolFixtures.Session().ToWire()), length)); r.Content.Headers.ContentLength = null; return r; });
        await Assert.ThrowsAsync<ProtocolException>(() => client.GetSessionAsync(Access(), default));
    }
    [Theory] [InlineData("image/svg+xml")] [InlineData("text/html")]
    public async Task LogoFailureUsesDefault(string type) => Assert.Null(await Client(_ => Response(Encoding.UTF8.GetBytes("<svg/>"), type)).GetLogoAsync(Access(), default));
    [Theory] [InlineData(1025, 10)] [InlineData(10, 1025)] [InlineData(0, 10)]
    public async Task LogoDimensionsRejectBeforeDecode(int width, int height)
    {
        var decoded = false; var client = Client(_ => Response(Png(width, height), "image/png"), _ => decoded = true);
        Assert.Null(await client.GetLogoAsync(Access(), default)); Assert.False(decoded);
    }
    [Fact] public async Task LogoBoundedDecodeAndDecodeFailure()
    {
        var png = Png(10, 10); var decoded = false;
        Assert.Equal(png, await Client(_ => Response(png, "image/png"), _ => decoded = true).GetLogoAsync(Access(), default)); Assert.True(decoded);
        Assert.Null(await Client(_ => Response(png, "image/png"), _ => throw new InvalidDataException()).GetLogoAsync(Access(), default));
        Assert.Null(await Client(_ => Response(new byte[524289], "image/png")).GetLogoAsync(Access(), default));
    }
    [Theory] [InlineData(401, "invalid_credential")] [InlineData(403, "access_denied")]
    public async Task LogoAuthorizationFailureIsTerminal(int status, string code)
    {
        var e = await Assert.ThrowsAsync<ProtocolHttpException>(() => Client(_ => Response(Encoding.UTF8.GetBytes("{\"error\":{\"code\":\""+code+"\",\"request_id\":\"req_example\"}}"), status: status)).GetLogoAsync(Access(), default)); Assert.Equal(code, e.Error.Code);
    }
    [Fact] public async Task TransientRetriesAreBoundedAndReuseProof()
    {
        var count = 0; var delays = new List<TimeSpan>(); var client = Client(request => { count++; Assert.Equal(Proof.ToBearer(), request.Headers.Authorization!.Parameter); return Response(Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"temporarily_unavailable\",\"request_id\":\"req_example\"}}"), status: 503); }, delays: delays);
        await Assert.ThrowsAsync<ProtocolHttpException>(() => client.GetSessionAsync(Access(), default)); Assert.Equal(3, count); Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
    }
    [Fact] public async Task LongRetryAfterReturnsToUiAndExpiredAccessSendsNothing()
    {
        var count = 0; var client = Client(_ => { count++; var r = Response(Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"rate_limited\",\"request_id\":\"req_example\"}}"), status: 429); r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return r; });
        var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => client.GetSessionAsync(Access(), default)); Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter); Assert.Equal(1, count);
        count = 0; var clock = new FixedClock { UtcNow = new(2030,1,1,2,0,0,TimeSpan.Zero) };
        await Assert.ThrowsAsync<ProtocolHttpException>(() => Client(_ => { count++; return Response([]); }, clock: clock).GetSessionAsync(Access(), default)); Assert.Equal(0, count);
    }
    [Fact] public async Task DecoderFailureOfAnyTypeUsesFallback()
        => Assert.Null(await Client(_ => Response(Png(10,10), "image/png"), _ => throw new InvalidOperationException("decode failed")).GetLogoAsync(Access(), default));
    [Fact] public async Task ExplicitLogoAuthorizationSurvivesMalformedOrUnreadableError()
    {
        var client = Client(_ => { var r = Response([],status:403); r.Content = new BrokenContent(); return r; });
        var error = await Assert.ThrowsAsync<ProtocolHttpException>(() => client.GetLogoAsync(Access(),default)); Assert.Equal("access_denied",error.Error.Code);
    }
    [Fact] public async Task RejectsCredentialApiTrailingSlashAndReceiptBindingMismatch()
    {
        await Assert.ThrowsAsync<ProtocolException>(() => Client(_=>Response(Encoding.UTF8.GetBytes("{\"version\":1,\"session_id\":\"ins_example\",\"api_base_url\":\"https://gateway.example/v1/\",\"credential\":{\"type\":\"api_key\",\"delivery\":\"server\",\"value\":\"FAKE\"}}"))).GetCredentialsAsync(Access(),default));
        await Assert.ThrowsAsync<ProtocolException>(() => Client(_=>Response(Encoding.UTF8.GetBytes("{\"version\":1,\"session_id\":\"other\",\"state\":\"completed\"}"))).CompleteAsync(Access(),default));
    }
    [Fact] public async Task CancellationStopsRetriesAndDelayNeverCrossesDeadline()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var calls=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Client(_=>{ calls++;return Response([]); }).GetSessionAsync(Access(),cancel.Token)); Assert.Equal(0,calls);
        var clock = new FixedClock { UtcNow = new(2030,1,1,1,59,59,TimeSpan.Zero) }; var delays = new List<TimeSpan>();
        await Assert.ThrowsAsync<ProtocolHttpException>(()=>Client(_=>{ calls++;return Response(Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"temporarily_unavailable\",\"request_id\":\"req_example\"}}"),status:503); },clock:clock,delays:delays).GetSessionAsync(Access(),default)); Assert.Equal(1,calls); Assert.Empty(delays);
    }
    [Fact] public async Task RetryAfterDateIsRespectedAndTransportErrorsAreRedacted()
    {
        var delays=new List<TimeSpan>();var calls=0;
        await Assert.ThrowsAsync<ProtocolHttpException>(()=>Client(_=>{ calls++;var r=Response(Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"rate_limited\",\"request_id\":\"req_example\"}}"),status:429);r.Headers.RetryAfter=new RetryConditionHeaderValue(new DateTimeOffset(2030,1,1,0,0,1,TimeSpan.Zero));return r; },delays:delays).GetSessionAsync(Access(),default)); Assert.Equal(3,calls); Assert.Equal(new[]{TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1)},delays);
        var error=await Assert.ThrowsAsync<ProtocolHttpException>(()=>Client(_=>throw new HttpRequestException("FAKE_secret_should_not_escape")).GetSessionAsync(Access(),default)); Assert.DoesNotContain("FAKE_secret",error.ToString());
    }
    [Theory] [InlineData("assistant\nversion","x64")] [InlineData("1.0","x86")] [InlineData("","arm64")]
    public async Task InvalidClientIdentitySendsNothing(string version,string architecture)
    {
        var calls=0;await Assert.ThrowsAsync<ProtocolException>(()=>Client(_=>{calls++;return Response([]);}).BootstrapAsync(ProtocolFixtures.Code(),new(new ResumeId(Guid.NewGuid()),Guid.NewGuid(),Proof),new(version,architecture),default));Assert.Equal(0,calls);
    }
    [Fact] public async Task JpegBoundsScreenPrecedesDecoder()
    {
        byte[] jpeg={255,216,255,192,0,8,8,0,10,0,10,1,255,217};var decoded=false;
        Assert.Equal(jpeg,await Client(_=>Response(jpeg,"image/jpeg"),_=>decoded=true).GetLogoAsync(Access(),default));Assert.True(decoded);
        jpeg[9]=4;jpeg[10]=1;decoded=false;Assert.Null(await Client(_=>Response(jpeg,"image/jpeg"),_=>decoded=true).GetLogoAsync(Access(),default));Assert.False(decoded);
    }
    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext? context) => throw new IOException("FAKE_secret");
        protected override bool TryComputeLength(out long length) { length=0;return false; }
    }
#if !NETFRAMEWORK
    [Fact] public async Task RealHttpsRedirectNeverContactsRedirectedServer()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var certificateRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=127.0.0.1",key,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(5));
        var source = new System.Net.Sockets.TcpListener(IPAddress.Loopback,0); var redirected = new System.Net.Sockets.TcpListener(IPAddress.Loopback,0); source.Start(); redirected.Start();
        var port = ((IPEndPoint)source.LocalEndpoint).Port; var redirectedPort = ((IPEndPoint)redirected.LocalEndpoint).Port; var redirectedServerRequests = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var target = Task.Run(async()=> { try { using var connection=await redirected.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref redirectedServerRequests); } catch (OperationCanceledException) { } });
        var server = Task.Run(async()=> {
            using var connection=await source.AcceptTcpClientAsync(stop.Token); using var tls=new System.Net.Security.SslStream(connection.GetStream());
            await tls.AuthenticateAsServerAsync(certificate,false,System.Security.Authentication.SslProtocols.Tls12,false);
            using var reader=new StreamReader(tls,Encoding.ASCII,false,1024,true); string? line; var contentLength=0;
            while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync())) if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) contentLength=int.Parse(line.Substring(15).Trim());
            var body=new char[contentLength];if(contentLength>0)await reader.ReadBlockAsync(body,0,body.Length);
            var response=Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: https://127.0.0.1:"+redirectedPort+"/leak\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");await tls.WriteAsync(response);await tls.FlushAsync();
        });
        try
        {
            // Local fixture certificate trust is scoped to this test handler only. No OS trust changes.
            var handler=ProtocolHttpClients.CreateHandler();handler.ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert?.Thumbprint==certificate.Thumbprint;
            using var clients=new ProtocolHttpClients(handler,new Handler(_=>Response([])),new Handler(_=>Response([])));
            var json=ProtocolFixtures.SessionJson();json["setup_base_url"]="https://127.0.0.1:"+port+"/Tenant/Setup";
            var codeJson=JsonNode.Parse(ProtocolFixtures.Code().ToWire().GetRawText())!;codeJson["setup_base_url"]=json["setup_base_url"]!.DeepClone();
            var code=SetupCodeParser.Parse("AGSP1."+StrictJson.EncodeBase64Url(Encoding.UTF8.GetBytes(codeJson.ToJsonString())));
            var access=new SessionAccess(ProtocolFixtures.Validator().Validate(code,ProtocolFixtures.Session(json),null),Proof);
            var client=new SetupSessionClient(clients,_=>{},new FixedClock());
            await Assert.ThrowsAsync<ProtocolException>(()=>client.GetCredentialsAsync(access,stop.Token));await server;
            await Task.Delay(100);Assert.Equal(0,redirectedServerRequests);
        }
        finally { stop.Cancel();source.Stop();redirected.Stop();await target; }
    }
#endif
    [Fact] public void SafeRequestIdHasOnlyMessageSizeLimit()
    {
        var id=new string('a',500);var bytes=Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"invalid_request\",\"request_id\":\""+id+"\"}}");using var response=Response(bytes,status:400);
        Assert.Equal(id,SetupSessionClient.ParseError(response,bytes,[]).RequestId);
    }
    [Theory] [MemberData(nameof(AcceptedOperations))]
    public async Task ExactAcceptedSessionOperationVectors(string id,JsonElement input,JsonElement expected)
    {
        Assert.NotEmpty(id);var operation=input.GetProperty("operation").GetString();var body=ProtocolFixtures.Wire(expected.GetProperty("canonical"));
        var client=Client(request=>{Assert.Equal("https://gateway.example/api/ai-setup/v1/sessions/ins_example"+(operation=="get"?"":"/"+operation),request.RequestUri!.AbsoluteUri);Assert.Equal(input.GetProperty("request").GetProperty("headers").GetProperty("Authorization").GetString(),request.Headers.Authorization!.ToString());if(operation!="get")Assert.Equal("{}",request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());return Response(body);});
        if(operation=="get")ProtocolFixtures.Canonical(expected.GetProperty("canonical"),(await client.GetSessionAsync(Access(),default)).ToWire());
        else {var receipt=operation=="complete"?await client.CompleteAsync(Access(),default):await client.CancelAsync(Access(),default);Assert.Equal(expected.GetProperty("canonical").GetProperty("state").GetString(),receipt.State);Assert.Equal("ins_example",receipt.SessionId);Assert.Equal(1,receipt.Version);}
    }
    public static IEnumerable<object[]> AcceptedOperations() => ProtocolFixtures.Cases("session").Where(c=>((JsonElement)c[1]).ValueKind==JsonValueKind.Object&&((JsonElement)c[1]).TryGetProperty("operation",out _)&&((JsonElement)c[2]).GetProperty("accept").GetBoolean());
    [Theory] [MemberData(nameof(BootstrapBodies))]
    public async Task ExactGeneratedBootstrapBodyVectors(string id,JsonElement input,JsonElement expected)
    {
        Assert.NotEmpty(id);var claim=new ClaimRecord(new ResumeId(Guid.NewGuid()),Guid.Parse(input.GetProperty("claim_id").GetString()!),Proof);
        var client=Client(request=>{ProtocolFixtures.Canonical(expected.GetProperty("canonical"),JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement);return Response(ProtocolFixtures.Wire(ProtocolFixtures.Session().ToWire()));});
        await client.BootstrapAsync(ProtocolFixtures.Code(),claim,new(input.GetProperty("assistant_version").GetString()!,input.GetProperty("architecture").GetString()!),default);
    }
    public static IEnumerable<object[]> BootstrapBodies() => ProtocolFixtures.Cases("bootstrap").Where(c=>(string)c[0]=="bootstrap-body-x64"||(string)c[0]=="bootstrap-body-arm64");
    [Fact] public async Task GetSessionRejectsChangedSnapshotBeforeExposure()
    {
        var json=ProtocolFixtures.SessionJson();json["revision"]="changed";
        await Assert.ThrowsAsync<ProtocolException>(()=>Client(_=>Response(Encoding.UTF8.GetBytes(json.ToJsonString()))).GetSessionAsync(Access(),default));
    }
    private static byte[] Pad(byte[] bytes, int length) { var b = Enumerable.Repeat((byte)32, length).ToArray(); Buffer.BlockCopy(bytes,0,b,0,bytes.Length); return b; }
    private static byte[] Png(int width, int height) { var b = new byte[33]; new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82}.CopyTo(b,0); for (var i=0;i<4;i++) {b[16+i]=(byte)(width>>(24-8*i)); b[20+i]=(byte)(height>>(24-8*i));} return b; }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); } }
}
