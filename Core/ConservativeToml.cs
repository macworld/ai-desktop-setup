using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiDesktopSetup.Core;

internal static class ConservativeToml
{
    private const string Part = "(?:[A-Za-z0-9_-]+|\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*')";
    private static SetupException Unsupported() => new("现有 config.toml 包含无法安全合并的格式或重复设置。配置未修改，请保留原文件并联系支持。");

    public static string Merge(string text, CodexConfiguration configuration)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        var root = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["model"] = JsonSerializer.Serialize(configuration.Model), ["model_provider"] = "\"ai_gateway\"" };
        if (configuration.ReasoningEffort != null) root["model_reasoning_effort"] = JsonSerializer.Serialize(configuration.ReasoningEffort);
        var provider = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["name"] = "\"AI Gateway\"", ["base_url"] = JsonSerializer.Serialize(configuration.ApiBaseUrl), ["wire_api"] = "\"responses\"", ["requires_openai_auth"] = "true" };
        var output = new List<string>();
        var section = "";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var providerWritten = false;
        void Insert()
        {
            if (section == "") foreach (var pair in root) output.Add($"{pair.Key} = {pair.Value}");
            if (section == "model_providers.ai_gateway")
            { foreach (var pair in provider) output.Add($"{pair.Key} = {pair.Value}"); providerWritten = true; }
        }
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index == lines.Length - 1 && line.Length == 0) continue;
            var value = RemoveComment(line).Trim();
            if (value.Length == 0) { output.Add(line); continue; }
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                var header = Regex.Match(value, "^\\[\\s*(" + Part + "(?:\\s*\\.\\s*" + Part + ")*)\\s*\\]$");
                if (!header.Success) throw Unsupported();
                var raw = header.Groups[1].Value;
                var parts = Regex.Matches(raw, Part).Cast<Match>().Select(m => ReadString(m.Value)).ToArray();
                var next = string.Join(".", parts);
                if (parts[0] == "desktop" && (parts.Length == 1 && raw != "desktop" || parts.Length > 1 && parts[1] == "localeOverride")) throw Unsupported();
                if (root.ContainsKey(parts[0]) || (parts[0] == "model_providers" && parts.Length > 2 && parts[1] == "ai_gateway")) throw Unsupported();
                if (next == "model_providers.ai_gateway" && raw != next) throw Unsupported();
                if (!tables.Add(next)) throw Unsupported();
                Insert(); section = next; output.Add(line); continue;
            }
            var assignment = Regex.Match(value, "^([A-Za-z0-9_-]+)\\s*=\\s*(.+)$");
            if (!assignment.Success || !IsValue(assignment.Groups[2].Value)) throw Unsupported();
            var key = assignment.Groups[1].Value;
            var setting = assignment.Groups[2].Value.Trim();
            if (!keys.Add(section + "\n" + key)) throw Unsupported();
            if (section == "")
            {
                if (key == "profile") throw new SetupException("请先取消 config.toml 中的默认 profile，再重新配置。原文件未修改。");
                if (key == "forced_login_method" && ReadString(setting) != "api") throw new SetupException("forced_login_method 不是 api。请先检查登录方式，再重新配置。原文件未修改。");
                if (key == "cli_auth_credentials_store" && ReadString(setting) != "file") throw new SetupException("cli_auth_credentials_store 不是 file。请按原凭据存储方式手动配置或联系管理员；不会改动系统密钥库。");
                if (key is "model_providers" or "profiles" or "desktop") throw Unsupported();
                if (root.ContainsKey(key)) continue;
            }
            if (section == "model_providers" && key == "ai_gateway") throw Unsupported();
            if (section == "model_providers.ai_gateway" && (provider.ContainsKey(key) || key is "env_key" or "experimental_bearer_token")) continue;
            output.Add(line);
        }
        Insert();
        if (!providerWritten)
        { output.Add(""); output.Add("[model_providers.ai_gateway]"); foreach (var pair in provider) output.Add($"{pair.Key} = {pair.Value}"); }
        return string.Join("\n", output) + "\n";
    }

    private static string ReadString(string value)
    {
        if (value.StartsWith("\"", StringComparison.Ordinal)) return JsonSerializer.Deserialize<string>(value) ?? throw Unsupported();
        return value.StartsWith("'", StringComparison.Ordinal) ? value.Substring(1, value.Length - 2) : value;
    }

    private static string RemoveComment(string text)
    {
        var quote = '\0'; var escape = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            { if (escape) escape = false; else if (c == '\\' && quote == '"') escape = true; else if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#') return text.Substring(0, i);
        }
        return text;
    }

    private static bool IsValue(string value, int nesting = 0)
    {
        value = value.Trim();
        if (nesting > 16 || value.Length == 0) return false;
        if (Regex.IsMatch(value, "^(true|false|[+-]?(?:0|[1-9](?:_?[0-9])*)(?:\\.[0-9](?:_?[0-9])*)?(?:[eE][+-]?[0-9](?:_?[0-9])*)?)$")) return true;
        if (Regex.IsMatch(value, "^'[^'\\r\\n]*'$")) return true;
        if (Regex.IsMatch(value, "^\"(?:[^\"\\\\\\r\\n]|\\\\[\"\\\\bfnrt]|\\\\u[0-9a-fA-F]{4})*\"$"))
        { try { JsonSerializer.Deserialize<string>(value); return true; } catch (JsonException) { return false; } }
        if (!value.StartsWith("[", StringComparison.Ordinal) || !value.EndsWith("]", StringComparison.Ordinal)) return false;
        var body = value.Substring(1, value.Length - 2); var start = 0; var depth = 0; var quote = '\0'; var escape = false;
        var chunks = new List<string>();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote != '\0')
            { if (escape) escape = false; else if (c == '\\' && quote == '"') escape = true; else if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0) { chunks.Add(body.Substring(start, i - start)); start = i + 1; }
            if (depth < 0) return false;
        }
        if (quote != '\0' || depth != 0) return false;
        chunks.Add(body.Substring(start));
        if (string.IsNullOrWhiteSpace(chunks[chunks.Count - 1])) chunks.RemoveAt(chunks.Count - 1);
        return chunks.All(chunk => IsValue(chunk, nesting + 1));
    }
}
