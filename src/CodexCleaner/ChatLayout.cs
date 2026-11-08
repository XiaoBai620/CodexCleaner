using System.Text;
using System.Text.RegularExpressions;

namespace CodexCleaner;

internal sealed record TextStyle(Font Font, Color Color, bool Code = false);

internal sealed record Run(string Text, TextStyle Style);

internal sealed record Paragraph(IReadOnlyList<Run> Runs, bool Code = false, bool Rule = false);

// A small Markdown subset: fenced code, headings, bullets, quotes, rules, `code` and **bold**.
// Tables and links stay as written.
internal static partial class Markdown
{
    public static readonly Font CodeFont = new("Consolas", 9f);
    public static readonly Color CodeInk = DesktopTheme.Code;
    private static readonly Font[] HeadingFonts = [new("Microsoft YaHei UI", 12f, FontStyle.Bold), new("Microsoft YaHei UI", 11f, FontStyle.Bold), new("Microsoft YaHei UI", 10f, FontStyle.Bold)];
    private static readonly Dictionary<Font, Font> BoldFonts = [];
    private static readonly TextStyle Code = new(CodeFont, CodeInk, true);

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    public static List<Paragraph> Plain(string text, TextStyle style) => [.. Lines(text).Select(l => new Paragraph([new Run(l, style)]))];

    public static List<Paragraph> Parse(string text, TextStyle body)
    {
        var paragraphs = new List<Paragraph>();
        var code = false;
        foreach (var raw in Lines(text))
        {
            var line = raw;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (code) { paragraphs.Add(new([new Run(line, Code)], Code: true)); continue; }
            if (Rule().IsMatch(line)) { paragraphs.Add(new([], Rule: true)); continue; }
            if (Heading().Match(line) is { Success: true } heading)
            {
                paragraphs.Add(new(Inline(heading.Groups[2].Value, body with { Font = HeadingFonts[Math.Min(heading.Groups[1].Length, 3) - 1] })));
                continue;
            }
            if (Quote().Match(line) is { Success: true } quote) { paragraphs.Add(new(Inline(quote.Groups[1].Value, body with { Color = DesktopTheme.Muted }))); continue; }
            if (Bullet().Match(line) is { Success: true } bullet) line = bullet.Groups[1].Value + "• " + bullet.Groups[2].Value;
            paragraphs.Add(new(Inline(line, body)));
        }
        return paragraphs;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');

    private static List<Run> Inline(string s, TextStyle style)
    {
        var runs = new List<Run>();
        var text = new StringBuilder();
        var bold = false;
        void Flush()
        {
            if (text.Length > 0) runs.Add(new Run(text.ToString(), bold ? style with { Font = Bold(style.Font) } : style));
            text.Clear();
        }
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '`' && s.IndexOf('`', i + 1) is var close && close > i + 1)
            {
                Flush();
                runs.Add(new Run(s[(i + 1)..close], Code));
                i = close + 1;
            }
            else if (string.CompareOrdinal(s, i, "**", 0, 2) == 0 && (bold || s.IndexOf("**", i + 2, StringComparison.Ordinal) > i + 2))
            {
                Flush();
                bold = !bold;
                i += 2;
            }
            else text.Append(s[i++]);
        }
        Flush();
        return runs;
    }

    private static Font Bold(Font font)
    {
        if (font.Bold) return font;
        if (!BoldFonts.TryGetValue(font, out var bold)) BoldFonts[font] = bold = new Font(font, FontStyle.Bold);
        return bold;
    }
}

// Glyph advances are measured once per font and character; GDI advances are whole pixels,
// so summing them matches what DrawText renders for the joined string.
internal sealed class TextMeasurer
{
    public const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    private readonly Dictionary<(Font, int), int> cache = [];

    public Graphics? Graphics { get; set; }

    public void Clear() => cache.Clear();

    public int Width(Font font, int codePoint)
    {
        if (cache.TryGetValue((font, codePoint), out var width)) return width;
        width = TextRenderer.MeasureText(Graphics!, char.ConvertFromUtf32(codePoint), font, Size.Empty, Flags).Width;
        cache[(font, codePoint)] = width;
        return width;
    }
}

internal sealed class TextSeg(TextStyle style, int start, int x)
{
    private readonly StringBuilder chars = new();

    public TextStyle Style { get; } = style;
    public int Start { get; } = start;
    public int X { get; } = x;
    public List<int> Advance { get; } = [0];
    public string Text { get; private set; } = "";
    public int End => Start + chars.Length;
    public int Right => X + Advance[^1];

    public void Add(char c, int width) { chars.Append(c); Advance.Add(Advance[^1] + width); }

    public void Seal() => Text = chars.ToString();
}

internal sealed class TextLine(int start, int y, bool code, bool rule)
{
    public int Start { get; } = start;
    public int End { get; set; } = start;
    public int Y { get; } = y;
    public int Height { get; set; }
    public bool Code { get; } = code;
    public bool Rule { get; } = rule;
    public List<TextSeg> Segs { get; } = [];
    public int Right => Segs.Count == 0 ? 0 : Segs[^1].Right;

    public int XOf(int offset)
    {
        foreach (var s in Segs)
            if (offset < s.End) return offset <= s.Start ? s.X : s.X + s.Advance[offset - s.Start];
        return Right;
    }

    public int OffsetAt(int x)
    {
        foreach (var s in Segs)
        {
            if (x >= s.Right) continue;
            for (var i = 0; i < s.Text.Length; i++)
                if (!char.IsLowSurrogate(s.Text[i]) && x < s.X + (s.Advance[i] + s.Advance[i + 1]) / 2) return s.Start + i;
            return s.End;
        }
        return End;
    }
}

internal sealed class TextLayout
{
    private TextLayout(string plain, List<TextLine> lines, int maxWidth)
    {
        Plain = plain;
        Lines = lines;
        Width = Math.Min(maxWidth, lines.Max(l => l.Right));
        Height = lines[^1].Y + lines[^1].Height;
    }

    public string Plain { get; }
    public List<TextLine> Lines { get; }
    public int Width { get; }
    public int Height { get; }

    public int OffsetAt(Point p) => (Lines.FirstOrDefault(l => p.Y < l.Y + l.Height) ?? Lines[^1]).OffsetAt(p.X);

    public static TextLayout Build(IReadOnlyList<Paragraph> paragraphs, int maxWidth, int emptyHeight, TextMeasurer measure)
    {
        var plain = new StringBuilder();
        var lines = new List<TextLine>();
        var y = 0;
        TextLine line = null!;
        void Open(bool code, bool rule) { line = new TextLine(plain.Length, y, code, rule); lines.Add(line); }
        void Close()
        {
            line.End = plain.Length;
            line.Height = line.Segs.Count == 0 ? emptyHeight : line.Segs.Max(s => s.Style.Font.Height);
            foreach (var s in line.Segs) s.Seal();
            y += line.Height;
        }
        void Add(TextStyle style, char c, int width)
        {
            var last = line.Segs.Count == 0 ? null : line.Segs[^1];
            if (last is null || last.Style != style) line.Segs.Add(last = new TextSeg(style, plain.Length, line.Right));
            last.Add(c, width);
            plain.Append(c);
        }

        for (var p = 0; p < paragraphs.Count; p++)
        {
            if (p > 0) plain.Append('\n');
            var paragraph = paragraphs[p];
            Open(paragraph.Code, paragraph.Rule);
            foreach (var run in paragraph.Runs)
                foreach (var (start, length, space) in Atoms(run.Text))
                {
                    var widths = new int[length];
                    for (var i = 0; i < length; i++)
                    {
                        var c = run.Text[start + i];
                        if (char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(run.Text[start + i + 1]))
                            widths[i++] = measure.Width(run.Style.Font, char.ConvertToUtf32(c, run.Text[start + i]));
                        else widths[i] = measure.Width(run.Style.Font, char.IsSurrogate(c) ? 0xFFFD : c);
                    }
                    var total = widths.Sum();
                    if (!space && line.Right > 0 && line.Right + total > maxWidth) { Close(); Open(paragraph.Code, false); }
                    for (var i = 0; i < length; i++)
                    {
                        if (total > maxWidth && line.Right > 0 && line.Right + widths[i] > maxWidth) { Close(); Open(paragraph.Code, false); }
                        Add(run.Style, run.Text[start + i], widths[i]);
                    }
                }
            Close();
        }
        return new TextLayout(plain.ToString(), lines, maxWidth);
    }

    // Break opportunities: around spaces, and between characters of CJK or other wide scripts.
    private static IEnumerable<(int Start, int Length, bool Space)> Atoms(string s)
    {
        static bool Wide(char c) => c >= '\u2E80' || char.IsSurrogate(c);
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == ' ') { yield return (i++, 1, true); continue; }
            if (Wide(s[i]))
            {
                var length = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
                yield return (i, length, false);
                i += length;
                continue;
            }
            var j = i;
            while (j < s.Length && s[j] != ' ' && !Wide(s[j])) j++;
            yield return (i, j - i, false);
            i = j;
        }
    }
}
