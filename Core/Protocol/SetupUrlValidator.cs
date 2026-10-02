using System.Globalization;
using System.Text.RegularExpressions;

namespace AiDesktopSetup.Core.Protocol;
public static class SetupUrlValidator
{
    public static ValidatedBaseUrls Validate(string setupBaseUrl, string apiBaseUrl, CredentialType type)
    {
        if (!Enum.IsDefined(typeof(CredentialType), type)) throw new ProtocolException();
        var setup = Normalize(setupBaseUrl, true); var api = Normalize(apiBaseUrl, false);
        if (type == CredentialType.ApiKey && Origin(setup) != Origin(api)) throw new ProtocolException();
        return new(setup, api);
    }
    public static string Origin(string normalizedUrl) => normalizedUrl.Substring(0, normalizedUrl.IndexOf('/', 8) is var end && end >= 0 ? end : normalizedUrl.Length);
    public static string Normalize(string text, bool setup) => NormalizeCore(text, setup, false);
    internal static string Asset(string text) => NormalizeCore(text, false, true);
    private static string NormalizeCore(string text, bool setup, bool asset)
    {
        if (text is null || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || text.Contains('\\') || text.IndexOfAny(new[] { '<', '>', '"', '|', '^', '`', '{', '}' }) >= 0) throw new ProtocolException();
        var match = Regex.Match(text, @"\A(?i:https)://([^/?#]+)([^?#]*)(.*)\z");
        if (!match.Success) throw new ProtocolException();
        var authority = match.Groups[1].Value; var path = match.Groups[2].Value; var suffix = match.Groups[3].Value;
        if (authority.Contains('@') || authority.Contains('%') || (!asset && suffix.Length != 0) || (setup && path.EndsWith("/", StringComparison.Ordinal))) throw new ProtocolException();
        // Validate literal path first: Uri otherwise resolves dot segments and silently repairs escapes.
        for (var i = 0; i < path.Length; i++)
            if (path[i] == '%')
            {
                if (i + 2 >= path.Length || !byte.TryParse(path.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) || b == 0x2f || b == 0x5c || b < 0x20 || b == 0x7f) throw new ProtocolException();
                i += 2;
            }
        for (var i = 0; i < suffix.Length; i++)
            if (suffix[i] == '%')
            {
                if (i + 2 >= suffix.Length || !byte.TryParse(suffix.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) || b < 0x20 || b == 0x7f) throw new ProtocolException();
                i += 2;
            }
        foreach (var segment in path.Split('/'))
        { var dots = Regex.Replace(segment, "%2e", ".", RegexOptions.IgnoreCase); if (dots == "." || dots == "..") throw new ProtocolException(); }
        string host; string port = "";
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var bracket = authority.IndexOf(']'); if (bracket < 0) throw new ProtocolException();
            host = authority.Substring(0, bracket + 1).ToLowerInvariant();
            if (authority.Length > bracket + 1) { if (authority[bracket + 1] != ':') throw new ProtocolException(); port = authority.Substring(bracket + 2); if (port.Length == 0) throw new ProtocolException(); }
        }
        else
        {
            var pieces = authority.Split(':'); if (pieces.Length > 2 || pieces[0].Length == 0) throw new ProtocolException();
            try { host = new IdnMapping().GetAscii(pieces[0]).ToLowerInvariant(); } catch (ArgumentException) { throw new ProtocolException(); }
            if (!Regex.IsMatch(host, @"\A[a-z0-9.-]+\z") || host.Split('.').Where(s => s.Length > 0).Any(s => s.Length > 63 || s.StartsWith("-", StringComparison.Ordinal) || s.EndsWith("-", StringComparison.Ordinal)) || host.StartsWith(".", StringComparison.Ordinal) || host.Contains("..")) throw new ProtocolException();
            if (pieces.Length == 2) { port = pieces[1]; if (port.Length == 0) throw new ProtocolException(); }
        }
        if (port.Length != 0)
        {
            if (!Regex.IsMatch(port, @"\A[0-9]+\z") || !int.TryParse(port, out var p) || p < 0 || p > 65535) throw new ProtocolException();
            port = p == 443 ? "" : ":" + p.ToString(CultureInfo.InvariantCulture);
        }
        var result = "https://" + host + port + path + suffix;
        if (!Uri.TryCreate(result, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.HostNameType == UriHostNameType.Unknown || !string.Equals(uri.Host.Trim('[', ']'), host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) throw new ProtocolException();
        if (asset && Regex.IsMatch(Uri.UnescapeDataString(suffix), @"(?i)(?:[?&#]|^)(?:access_token|api_key|authorization|credential|secret|proof|token|key)=", RegexOptions.CultureInvariant)) throw new ProtocolException();
        return result;
    }
}
