using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;

namespace AiDesktopSetup.Tests.Protocol;

internal static class ProtocolFixtures
{
    internal static string TemporaryDirectory()
    {
        var path = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
#if !NETFRAMEWORK
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.LinkTarget != null) return Path.Combine(current.ResolveLinkTarget(true)!.FullName, path.Substring(current.FullName.Length).TrimStart(Path.DirectorySeparatorChar));
#endif
        return path;
    }
    internal static IEnumerable<object[]> Cases(string kind) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors.json")))
        .RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("kind").GetString() == kind)
        .Select(c => new object[] { c.GetProperty("id").GetString()!, c.GetProperty("input").Clone(), c.GetProperty("expected").Clone() }).ToArray();
    internal static byte[] Wire(JsonElement input) => Encoding.UTF8.GetBytes(input.ValueKind == JsonValueKind.String ? input.GetString()! : input.GetRawText());
    internal static string CodeText() => (string)((JsonElement)Cases("code").First(c => (string)c[0] == "code-ticket-example-ai")[1]).GetString()!;
    internal static SetupCode Code() => SetupCodeParser.Parse(CodeText());
    internal static JsonObject SessionJson() => JsonNode.Parse(((JsonElement)Cases("session").First(c => (string)c[0] == "session-config-example-ai")[1]).GetRawText())!.AsObject();
    internal static SessionSnapshot Session(JsonObject? json = null) => SessionSnapshot.Parse(Encoding.UTF8.GetBytes((json ?? SessionJson()).ToJsonString()));
    internal static SessionBindingValidator Validator() => new(new FixedClock(), new SessionAdapterPolicy(new[] { "low", "medium", "high" }, requiresLicense: true));
    internal static void Canonical(JsonElement expected, JsonElement actual) => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText())), "Known-field canonical projection differs");
}
internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
