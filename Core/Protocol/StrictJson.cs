using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDesktopSetup.Core.Protocol;

/// <summary>Shared strict wire decoder. Errors deliberately carry no input or inner exception.</summary>
public static class StrictJson
{
    public static JsonElement Parse(byte[] bytes, int maximumBytes = 131072)
    {
        if (bytes is null || bytes.Length > maximumBytes) throw new ProtocolException();
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = Math.Max(1, bytes.Length) });
            Check(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is InvalidOperationException || error is ArgumentException)
        { throw new ProtocolException(); }
    }
    private static void Check(JsonElement value)
    {
        // Unknown inert extensions may be deeper than the library default. The wire
        // byte cap bounds nesting; use an explicit stack rather than recursive calls.
        var pending = new Stack<JsonElement>(); pending.Push(value);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if (current.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in current.EnumerateObject())
                { Count(property.Name); if (!names.Add(property.Name)) throw new ProtocolException(); pending.Push(property.Value); }
            }
            else if (current.ValueKind == JsonValueKind.Array) foreach (var item in current.EnumerateArray()) pending.Push(item);
            else if (current.ValueKind == JsonValueKind.String) Count(current.GetString()!);
        }
    }
    internal static JsonElement Object(JsonElement value)
    { if (value.ValueKind != JsonValueKind.Object) throw new ProtocolException(); return value; }
    internal static JsonElement Required(JsonElement value, string key)
    { Object(value); if (!value.TryGetProperty(key, out var result)) throw new ProtocolException(); return result; }
    internal static string String(JsonElement value, string key)
    { var result = Required(value, key); if (result.ValueKind != JsonValueKind.String) throw new ProtocolException(); return result.GetString()!; }
    internal static string Text(JsonElement value, string key, int max = int.MaxValue, bool empty = false, bool label = false)
    { var text = String(value, key); if ((!empty && text.Length == 0) || Count(text) > max || text.Any(c => char.IsControl(c)) || (label && (text.Contains('<') || text.Contains('>')))) throw new ProtocolException(); return text; }
    internal static int Count(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++, count++)
            if (char.IsSurrogate(text[i]))
            { if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i])) throw new ProtocolException(); }
        return count;
    }
    internal static string Identifier(JsonElement value, string key)
    { var text = String(value, key); if (!Regex.IsMatch(text, @"\A[A-Za-z0-9_-]{1,80}\z")) throw new ProtocolException(); return text; }
    internal static void Version(JsonElement value)
    { var n = Required(value, "version"); if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var i) || i != 1) throw new ProtocolException(); }
    internal static void App(JsonElement value)
    { if (String(value, "app_id") != "codex-desktop") throw new ProtocolException("configuration_unsupported"); }
    internal static void Bearer(string text)
    { if (!Regex.IsMatch(text, @"\A[A-Za-z0-9._~+/-]+=*\z")) throw new ProtocolException(); }
    internal static DateTimeOffset UtcTime(string text)
    {
        if (!Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|\+00:00)\z") ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) || result.Offset != TimeSpan.Zero) throw new ProtocolException();
        return result;
    }
    public static byte[] DecodeBase64Url(string text)
    {
        if (text.Length == 0 || !Regex.IsMatch(text, @"\A[A-Za-z0-9_-]+\z") || text.Length % 4 == 1) throw new ProtocolException();
        try
        {
            var result = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
            if (EncodeBase64Url(result) != text) throw new ProtocolException(); return result;
        }
        catch (FormatException) { throw new ProtocolException(); }
    }
    public static string EncodeBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static JsonElement Wire(object value) => JsonSerializer.SerializeToElement(value);
}
