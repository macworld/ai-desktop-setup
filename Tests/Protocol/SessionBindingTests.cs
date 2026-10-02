using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Tests.Protocol;
public sealed class SessionBindingTests
{
    public static IEnumerable<object[]> Sessions() => ProtocolFixtures.Cases("session").Where(c => !((JsonElement)c[1]).TryOperation());
    [Theory, MemberData(nameof(Sessions))]
    public void ExactDirectSessionVectors(string id, JsonElement input, JsonElement expected)
    {
        Assert.False(string.IsNullOrEmpty(id));
        var body = input.TryGetObjectProperty("body", out var nested) ? nested : input;
        ValidatedSession Parse() => ProtocolFixtures.Validator().Validate(CodeFor(body), SessionSnapshot.Parse(ProtocolFixtures.Wire(body)), null);
        if (!expected.GetProperty("accept").GetBoolean()) { Assert.Equal(expected.GetProperty("error_code").GetString(), Assert.Throws<ProtocolException>(() => Parse()).Code); return; }
        ProtocolFixtures.Canonical(expected.GetProperty("canonical"), Parse().Snapshot.ToWire());
    }
    private static SetupCode CodeFor(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.String) return ProtocolFixtures.Code();
        var code = JsonNode.Parse(ProtocolFixtures.Code().ToWire().GetRawText())!.AsObject();
        code["setup_base_url"] = body.GetProperty("setup_base_url").GetString(); code["api_base_url"] = body.GetProperty("api_base_url").GetString();
        return SetupCodeParser.Parse(SetupCodeTests.Encode(code.ToJsonString()));
    }
    [Theory][InlineData("revision")][InlineData("expires_at")][InlineData("api_base_url")][InlineData("setup_base_url")][InlineData("app_id")][InlineData("session_id")][InlineData("model")]
    public void RejectsChangedSnapshot(string field)
    {
        var json = ProtocolFixtures.SessionJson(); var original = ProtocolFixtures.Session();
        if (field == "model") json["client"]![field] = "different-model";
        else json[field] = field == "expires_at" ? "2030-01-01T01:59:00Z" : json[field]!.GetValue<string>() + "X";
        Assert.Throws<ProtocolException>(() => ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(json), original));
    }
    [Fact] public void AllowsSessionStateProgression()
    {
        var original = ProtocolFixtures.Session(); var json = ProtocolFixtures.SessionJson(); json["state"] = "credentials_released";
        var released = ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(json), original).Snapshot;
        json["state"] = "completed"; var completed = ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(json), released).Snapshot;
        Assert.Throws<ProtocolException>(() => ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), original, completed));
    }
    [Fact] public void RejectsExpiredAndOverlongSession()
    {
        var clock = new FixedClock { UtcNow = new DateTimeOffset(2030,1,1,2,0,0,TimeSpan.Zero) };
        var validator = new SessionBindingValidator(clock, new SessionAdapterPolicy(new[] {"high"}, true));
        Assert.Throws<ProtocolException>(() => validator.Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(), null));
        clock.UtcNow = clock.UtcNow.AddHours(-3);
        Assert.Throws<ProtocolException>(() => validator.Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session(), null));
    }
    [Fact] public void RejectsKnownCredentialInAssetLink()
    {
        var code = ProtocolFixtures.Code(); var json = ProtocolFixtures.SessionJson();
        json["service"]!["help_url"] = "https://gateway.example/help?x=" + Uri.EscapeDataString(code.Credential.Value);
        Assert.Throws<ProtocolException>(() => ProtocolFixtures.Validator().Validate(code, ProtocolFixtures.Session(json), null));
    }
    [Fact] public void StrictSessionDecoderRejectsInvalidUtf8AndOversize()
    {
        Assert.Throws<ProtocolException>(() => SessionSnapshot.Parse(new byte[] {0xff}));
        var bytes = Encoding.UTF8.GetBytes(ProtocolFixtures.SessionJson().ToJsonString()).Concat(Enumerable.Repeat((byte)' ',131072)).ToArray();
        Assert.Throws<ProtocolException>(() => SessionSnapshot.Parse(bytes));
    }
}
internal static class JsonFixtureExtensions
{
    internal static bool TryGetObjectProperty(this JsonElement e, string name, out JsonElement value) { value = default; return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value); }
    internal static bool TryOperation(this JsonElement e) => e.TryGetObjectProperty("operation", out _);
}
