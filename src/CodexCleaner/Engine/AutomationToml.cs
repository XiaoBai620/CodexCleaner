using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexCleaner.Engine;

// Older automation definitions may exist only as TOML. Only top-level target keys count;
// quoted prompt text that merely looks like a target must not block a chat.
internal static partial class AutomationToml
{
    [GeneratedRegex(@"^\s*([A-Za-z0-9_-]+|""(?:\\.|[^""\\])*""|'[^']*')\s*=\s*(.*?)\s*$")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"^""(?:[^""\\\x00-\x1f]|\\(?:[btnfr""\\]|u[0-9a-fA-F]{4}|U[0-9a-fA-F]{8}))*""\z")]
    private static partial Regex BasicString();

    public static List<string> Targets(string text)
    {
        var targets = new List<string>();
        string? multiline = null;
        var inTable = false;
        foreach (var original in text.TrimStart('\uFEFF').Split('\n').Select(l => l.TrimEnd('\r')))
        {
            var line = new StringBuilder();
            char? quote = null;
            for (var i = 0; i < original.Length; i++)
            {
                if (multiline is not null)
                {
                    if (string.CompareOrdinal(original, i, multiline, 0, 3) == 0)
                    {
                        var backslashes = 0;
                        if (multiline == "\"\"\"") for (var j = i - 1; j >= 0 && original[j] == '\\'; j--) backslashes++;
                        if (backslashes % 2 == 0) { multiline = null; i += 2; }
                    }
                    continue;
                }
                var c = original[i];
                if (quote is not null)
                {
                    line.Append(c);
                    if (quote == '"' && c == '\\') { if (i + 1 < original.Length) line.Append(original[++i]); }
                    else if (c == quote) quote = null;
                }
                else if (c == '#') break;
                else if (string.CompareOrdinal(original, i, "\"\"\"", 0, 3) == 0 || string.CompareOrdinal(original, i, "'''", 0, 3) == 0) { multiline = original.Substring(i, 3); i += 2; }
                else { line.Append(c); if (c is '"' or '\'') quote = c; }
            }
            var content = line.ToString();
            if (content.TrimStart().StartsWith('[')) inTable = true;
            if (inTable) continue;
            var assignment = Assignment().Match(content);
            if (!assignment.Success) continue;
            var key = assignment.Groups[1].Value is ['"' or '\'', ..] quoted ? Unquote(quoted) : assignment.Groups[1].Value;
            if (key is not ("target_thread_id" or "targetThreadId")) continue;
            var target = Unquote(assignment.Groups[2].Value);
            if (!Ids.Valid(target)) throw new CleanerException("自动化目标使用了未验证的 TOML 格式，请在 Codex 中检查该自动化。");
            targets.Add(target);
        }
        if (multiline is not null) throw new CleanerException("自动化 TOML 包含未闭合的多行字符串。");
        return targets;
    }

    private static string Unquote(string token)
    {
        if (token.Length >= 2 && token[0] == '\'' && token[^1] == '\'' && !token[1..^1].Contains('\'')) return token[1..^1];
        if (!BasicString().IsMatch(token)) throw new CleanerException("自动化目标使用了未验证的 TOML 字符串格式。");
        var result = new StringBuilder();
        var body = token[1..^1];
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\') { result.Append(body[i]); continue; }
            var code = body[++i];
            switch (code)
            {
                case 'b': result.Append('\b'); break;
                case 't': result.Append('\t'); break;
                case 'n': result.Append('\n'); break;
                case 'f': result.Append('\f'); break;
                case 'r': result.Append('\r'); break;
                case '"': result.Append('"'); break;
                case '\\': result.Append('\\'); break;
                default:
                    var length = code == 'u' ? 4 : 8;
                    var scalar = int.Parse(body.AsSpan(i + 1, length), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff) throw new CleanerException("自动化 TOML 字符串含无效 Unicode 字符。");
                    result.Append(char.ConvertFromUtf32(scalar));
                    i += length;
                    break;
            }
        }
        return result.ToString();
    }
}
