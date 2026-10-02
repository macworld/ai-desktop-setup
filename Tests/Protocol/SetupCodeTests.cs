using System.Text;
using System.Text.Json;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Tests.Protocol;
public sealed class SetupCodeTests
{
    public static IEnumerable<object[]> Codes() => ProtocolFixtures.Cases("code");
    public static IEnumerable<object[]> Urls() => ProtocolFixtures.Cases("url");
    [Theory, MemberData(nameof(Codes))]
    public void ExactCodeVectors(string id, JsonElement input, JsonElement expected)
    {
        Assert.False(string.IsNullOrEmpty(id));
        if (!expected.GetProperty("accept").GetBoolean()) { Assert.Equal(expected.GetProperty("error_code").GetString(), Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(input.GetString()!)).Code); return; }
        ProtocolFixtures.Canonical(expected.GetProperty("canonical"), SetupCodeParser.Parse(input.GetString()!).ToWire());
    }
    [Theory, MemberData(nameof(Urls))]
    public void ExactUrlVectors(string id, JsonElement input, JsonElement expected)
    {
        Assert.False(string.IsNullOrEmpty(id));
        string Parse()
        {
            var setup = input.GetProperty("role").GetString() == "setup_base_url";
            var value = SetupUrlValidator.Normalize(input.GetProperty("value").GetString()!, setup);
            if (input.TryGetProperty("bound", out var bound) && value != SetupUrlValidator.Normalize(bound.GetString()!, false)) throw new ProtocolException();
            return value;
        }
        if (!expected.GetProperty("accept").GetBoolean()) Assert.Equal(expected.GetProperty("error_code").GetString(), Assert.Throws<ProtocolException>(() => Parse()).Code);
        else Assert.Equal(expected.GetProperty("canonical").GetProperty("url").GetString(), Parse());
    }
    [Fact] public void ApiKeyRequiresSameOrigin() => Assert.Throws<ProtocolException>(() => SetupUrlValidator.Validate("https://gateway.example/setup", "https://other.example/v1", CredentialType.ApiKey));
    [Fact] public void PreservesTenantPathAndTrailingSlash() => Assert.Equal("https://edge.example:8443/Tenant/v1/", SetupUrlValidator.Validate("https://EDGE.example:8443/Tenant/Setup", "https://EDGE.example:8443/Tenant/v1/", CredentialType.ApiKey).ApiBaseUrl);
    [Theory]
    [InlineData("a/../v1")][InlineData("%2E/v1")][InlineData("a/.%2e/v1")]
    public void RejectsDotSegmentsBeforeUriNormalization(string path) => Assert.Throws<ProtocolException>(() => SetupUrlValidator.Normalize("https://gateway.example/" + path, false));
    [Fact] public void RejectsOversizeAndDuplicateKeys()
    {
        Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(new string('a', 16385)));
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(ProtocolFixtures.CodeText().Substring(6).Replace('-', '+').Replace('_', '/') + new string('=', (4 - (ProtocolFixtures.CodeText().Length - 6) % 4) % 4)));
        Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(Encode(json.Substring(0, json.Length - 1) + ",\"ignored\":{\"a\":1,\"\\u0061\":2}}")));
        Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(Encode(json.Substring(0, json.Length - 1) + ",\"ignored\":" + new string(' ', 8192) + "0}")));
    }
    [Fact] public void UnicodeLabelsCountCodePoints() => Assert.Equal(100, SetupCodeParser.Parse(Encode(Encoding.UTF8.GetString(Convert.FromBase64String(Pad(ProtocolFixtures.CodeText().Substring(6)))).Replace("Example AI", string.Concat(Enumerable.Repeat("😀", 100))))).ServiceName.Length / 2);
    [Theory][InlineData("https://bücher.example/v1", "https://xn--bcher-kva.example/v1")][InlineData("HTTPS://GATEWAY.EXAMPLE:443", "https://gateway.example")][InlineData("https://gateway.example/A//B/%41/", "https://gateway.example/A//B/%41/")]
    public void StrictCanonicalizationPreservesPath(string input, string canonical) => Assert.Equal(canonical, SetupUrlValidator.Normalize(input, false));
    [Theory][InlineData("https://gateway.example/v1|x")][InlineData("https://gateway.example/v1<x>")]
    public void RejectsUriSilentCharacterRepair(string value) => Assert.Throws<ProtocolException>(() => SetupUrlValidator.Normalize(value, false));
    [Theory][InlineData("https://gateway.example/v1?x=%GG")]
    public void RejectsMalformedAssetUrls(string value) => Assert.Throws<ProtocolException>(() => SetupUrlValidator.Asset(value));
    [Fact] public void StrictProofEncodingRejectsNoncanonicalTrailingBits()
    {
        var canonical = new ResumeSecret(new byte[32]).ToBearer(); Assert.Equal(32, ResumeSecret.Parse(canonical).Bytes.Length);
        Assert.Throws<ProtocolException>(() => ResumeSecret.Parse(canonical.Substring(0,42) + "B"));
        Assert.Throws<ProtocolException>(() => ResumeSecret.Parse(canonical + "="));
        Assert.Throws<ProtocolException>(() => ResumeSecret.Parse("AA"));
    }
    [Fact] public void AcceptsDeepInertExtensionWithinByteCap()
    {
        var json = ProtocolFixtures.Code().ToWire().GetRawText();
        var extension = new string('[', 200) + "0" + new string(']', 200);
        var parsed = SetupCodeParser.Parse(Encode(json.Substring(0, json.Length - 1) + ",\"inert\":" + extension + "}"));
        Assert.Equal(ProtocolFixtures.Code().InstallationId, parsed.InstallationId);
    }
    [Fact] public void RejectsDeepDuplicateInsideInertExtension()
    {
        var json = ProtocolFixtures.Code().ToWire().GetRawText();
        var extension = new string('[', 200) + "{\"a\":0,\"\\u0061\":1}" + new string(']', 200);
        Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(Encode(json.Substring(0, json.Length - 1) + ",\"inert\":" + extension + "}")));
    }
    [Fact] public void RejectsEncodedCredentialInBaseUrl()
    {
        var code = ProtocolFixtures.Code(); const string fixture = "FICTIONAL+TOKEN==";
        var json = System.Text.Json.Nodes.JsonNode.Parse(code.ToWire().GetRawText())!;
        json["credential"]!["value"] = fixture;
        json["setup_base_url"] = code.SetupBaseUrl + "/" + Uri.EscapeDataString(fixture);
        Assert.Throws<ProtocolException>(() => SetupCodeParser.Parse(Encode(json.ToJsonString())));
    }
    [Theory][InlineData("https://docs.example/help?key=keyboard-shortcuts")][InlineData("https://docs.example/help?%74oken=pagination-cursor")][InlineData("https://docs.example/help#proof=worked-example")]
    public void AllowsNonSecretAssetQueryAndFragment(string value) => Assert.Equal(value,SetupUrlValidator.Asset(value));
    [Fact] public void IPv6EquivalentOriginsSupportApiKey()
    {
        var urls = SetupUrlValidator.Validate("https://[2001:0db8:0000:0000:0000:0000:0000:0001]:443/Setup", "https://[2001:db8::1]/v1/", CredentialType.ApiKey);
        Assert.Equal("https://[2001:db8::1]/Setup",urls.SetupBaseUrl);
        Assert.Equal("https://[2001:db8::1]/v1/",urls.ApiBaseUrl);
        Assert.Throws<ProtocolException>(() => SetupUrlValidator.Validate(urls.SetupBaseUrl,"https://[2001:db8::1]:8443/v1/",CredentialType.ApiKey));
    }
    internal static string Pad(string s) => s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4);
    internal static string Encode(string s) => "AGSP1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
