using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text;
using CodexCleaner.Engine;

namespace CodexCleaner;

// Owner-drawn transcript (ported from DevinCleaner): a centred reading column, user turns
// in soft grey bubbles on the right, Codex's replies as plain text, and every tool call as
// its own collapsible row. Text is laid out per character so it can be selected by
// dragging; layouts are cached per width and only blocks in the viewport are painted.
internal sealed class ChatView : Control
{
    // Longer messages show this many characters first and expand in place on request.
    internal const int PreviewLimit = 20_000;
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 10f);
    private static readonly Font NoteFont = new("Microsoft YaHei UI", 9f);
    private static readonly Font NoteBold = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private static readonly Font ThinkingFont = new("Microsoft YaHei UI", 9f, FontStyle.Italic);
    private static readonly Font CaptionFont = new("Microsoft YaHei UI", 8.5f);
    private static readonly Color UserFill = Color.FromArgb(242, 242, 242);
    private static readonly Color CodeFill = Color.FromArgb(245, 245, 245);
    private static readonly Color InlineCodeFill = Color.FromArgb(238, 238, 238);
    private static readonly Color Highlight = Color.FromArgb(200, 220, 252);
    private static readonly Color SystemFill = Color.FromArgb(254, 249, 238);
    private static readonly Color SystemInk = Color.FromArgb(146, 92, 14);

    private enum Kind { User, Assistant, Thinking, Tool, System }

    private sealed class Block(Kind kind, int index, int sub, ChatMessage message)
    {
        public Kind Kind { get; } = kind;
        public int Index { get; } = index;
        public int Sub { get; } = sub;
        public ChatMessage Message { get; } = message;
        public string Caption = "";
        public TextLayout Layout = null!;
        public int LayoutWidth = -1;
        public bool LayoutOpen, Previewed;
        public Rectangle CaptionRect, Bubble;
        public Point Origin;
        public ChatToolCall? Call => Kind == Kind.Tool && Message.Role == ChatRole.Assistant ? Message.ToolCalls[Sub] : null;
    }

    private readonly record struct Caret(int Block, int Offset) : IComparable<Caret>
    {
        public int CompareTo(Caret other) => Block != other.Block ? Block.CompareTo(other.Block) : Offset.CompareTo(other.Offset);
    }

    private readonly VScrollBar bar = new() { Dock = DockStyle.Right, SmallChange = 48 };
    private readonly System.Windows.Forms.Timer dragScroll = new() { Interval = 40 };
    private readonly TextMeasurer measure = new();
    private readonly List<Block> blocks = [];
    private readonly HashSet<(int, Kind, int)> expanded = [];
    private IReadOnlyList<ChatMessage> messages = [];
    private bool showThinking, showSystem, dragging, moved;
    private Caret anchor, focus;
    private Point downAt;
    private int contentHeight, hovered = -1;

    public event Action<string, string>? OpenFull;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get; set { field = value; Invalidate(); } } = "正在读取对话…";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowThinking { get => showThinking; set { showThinking = value; Rebuild(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSystem { get => showSystem; set { showSystem = value; Rebuild(); } }

    public int TurnCount => blocks.Count(b => b.Kind is Kind.User or Kind.Assistant);

    public string SelectedText
    {
        get
        {
            var (from, to) = Ordered();
            if (from == to || blocks.Count == 0) return "";
            var text = new StringBuilder();
            for (var i = from.Block; i <= to.Block; i++)
            {
                var plain = blocks[i].Layout.Plain;
                var start = i == from.Block ? from.Offset : 0;
                var end = i == to.Block ? to.Offset : plain.Length;
                if (end <= start) continue;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(plain, start, end - start);
            }
            return text.ToString();
        }
    }

    public ChatView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.White;
        TabStop = true;
        AccessibleName = "对话内容，可拖动选择文字，Ctrl+C 复制";
        Controls.Add(bar);
        bar.ValueChanged += (_, _) => Invalidate();
        dragScroll.Tick += (_, _) => DragTick();
    }

    public void SetMessages(IReadOnlyList<ChatMessage> value)
    {
        messages = value;
        expanded.Clear();
        bar.Value = 0;
        Rebuild();
    }

    public string Transcript() => string.Join("\n\n", blocks.Select(b => (b.Caption.Length > 0 ? $"【{b.Caption}】\n" : "") + FullText(b)));

    internal static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static string Speaker(ChatRole role) => role switch { ChatRole.User => "你", ChatRole.System => "系统", _ => "Codex" };

    private void Rebuild()
    {
        blocks.Clear();
        anchor = focus = default;
        hovered = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            switch (m.Role)
            {
                case ChatRole.User:
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(new Block(Kind.User, i, 0, m));
                    break;
                case ChatRole.System when showSystem:
                    blocks.Add(new Block(Kind.System, i, 0, m));
                    break;
                case ChatRole.System:
                    continue;
                case ChatRole.Tool:
                    blocks.Add(new Block(Kind.Tool, i, 0, m));
                    break;
                case ChatRole.Assistant:
                    if (showThinking && m.Thinking is not null) blocks.Add(new Block(Kind.Thinking, i, 0, m));
                    if (!string.IsNullOrWhiteSpace(m.Text)) blocks.Add(new Block(Kind.Assistant, i, 0, m));
                    for (var c = 0; c < m.ToolCalls.Count; c++) blocks.Add(new Block(Kind.Tool, i, c, m));
                    break;
            }
        }
        string? last = null;
        foreach (var b in blocks)
        {
            var who = Speaker(b.Message.Role);
            b.Caption = who == last ? "" : $"{who} · {b.Message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            last = who;
        }
        Relayout();
    }

    private bool IsOpen(Block b) => expanded.Contains((b.Index, b.Kind, b.Sub));

    // Complete, unclipped content of a block; used for copying and the full-text window.
    private static string FullText(Block b)
    {
        var m = b.Message;
        return b.Kind switch
        {
            Kind.Thinking => "思考摘要\n\n" + m.Thinking,
            Kind.Tool when b.Call is { } c => $"{c.Name}  {c.Summary}\n\n参数\n{(c.Arguments.Length > 0 ? c.Arguments : "（无）")}\n\n结果\n{(c.Result is null ? "（没有记录结果）" : c.Result.Trim())}",
            Kind.Tool => "工具结果\n\n" + m.Text.Trim(),
            _ => m.Text.Trim(),
        };
    }

    private static readonly TextStyle Body = new(BodyFont, DesktopTheme.Ink);
    private static readonly TextStyle Note = new(NoteFont, DesktopTheme.Muted);
    private static readonly TextStyle NoteStrong = new(NoteBold, DesktopTheme.Accent);
    private static readonly TextStyle Heading = new(CaptionFont, DesktopTheme.Faint);
    private static readonly TextStyle Link = new(NoteFont, Color.FromArgb(37, 99, 235));
    private static readonly TextStyle Code = new(Markdown.CodeFont, Markdown.CodeInk, true);

    private static IEnumerable<Paragraph> CodeLines(string text) =>
        Markdown.Plain(text.Length == 0 ? " " : text, Code).Select(p => p with { Code = true });

    private static List<Paragraph> Paragraphs(Block b, bool open, out bool previewed)
    {
        previewed = false;
        var m = b.Message;
        switch (b.Kind)
        {
            case Kind.Thinking:
                return [new Paragraph([new Run(open ? "▾  思考摘要" : "▸  思考摘要", Note)]), .. open ? Markdown.Plain(m.Thinking!, new TextStyle(ThinkingFont, DesktopTheme.Muted)) : []];
            case Kind.Tool when b.Call is { } c:
                var summary = c.Summary.Length > 96 ? c.Summary[..96] + "…" : c.Summary;
                List<Paragraph> tool = [new Paragraph([new Run(open ? "▾  " : "▸  ", Note), new Run(c.Name, NoteStrong), new Run(summary.Length > 0 ? "   " + summary : "", Note)])];
                if (!open) return tool;
                tool.Add(new Paragraph([new Run("参数", Heading)]));
                tool.AddRange(CodeLines(c.Arguments.Length > 0 ? c.Arguments : "（无）"));
                tool.Add(new Paragraph([new Run("结果", Heading)]));
                tool.AddRange(CodeLines(c.Result is null ? "（没有记录结果）" : c.Result.TrimEnd()));
                return tool;
            case Kind.Tool:
                return [new Paragraph([new Run(open ? "▾  " : "▸  ", Note), new Run("工具结果", NoteStrong)]), .. open ? CodeLines(m.Text.TrimEnd()) : []];
        }
        var text = m.Text.Trim();
        string? more = null;
        if (text.Length > PreviewLimit && !open)
        {
            more = $"▸  展开剩余 {text.Length - PreviewLimit:N0} 字（共 {text.Length:N0} 字）";
            text = text[..PreviewLimit];
            previewed = true;
        }
        var body = b.Kind switch
        {
            Kind.Assistant => Markdown.Parse(text, Body),
            Kind.User => Markdown.Plain(text, Body),
            _ => Markdown.Plain(text, new TextStyle(NoteFont, SystemInk)),
        };
        if (more is not null) body.Add(new Paragraph([new Run(more, Link)]));
        return body;
    }

    private void Relayout()
    {
        var width = ClientSize.Width - bar.Width;
        if (width <= 0 || !IsHandleCreated) return;
        var s = DeviceDpi / 96f;
        int pad = (int)(24 * s), gap = (int)(18 * s), tight = (int)(2 * s), captionHeight = CaptionFont.Height + (int)(6 * s);
        var column = Math.Max((int)(200 * s), Math.Min(width - 2 * pad, (int)(800 * s)));
        var left = Math.Max(pad / 2, (width - column) / 2);
        var y = pad;
        using var g = CreateGraphics();
        measure.Graphics = g;
        Kind? previous = null;
        foreach (var b in blocks)
        {
            var open = IsOpen(b);
            var (padX, padY) = b.Kind switch
            {
                Kind.User => ((int)(14 * s), (int)(10 * s)),
                Kind.Assistant => (0, 0),
                Kind.System => ((int)(12 * s), (int)(10 * s)),
                _ => ((int)(10 * s), (int)((open ? 8 : 4) * s)),
            };
            var max = b.Kind == Kind.User ? (int)(column * 0.8) : column;
            var layoutWidth = max - 2 * padX;
            if (b.LayoutWidth != layoutWidth || b.LayoutOpen != open)
            {
                b.Layout = TextLayout.Build(Paragraphs(b, open, out b.Previewed), layoutWidth, BodyFont.Height / 2, measure);
                b.LayoutWidth = layoutWidth;
                b.LayoutOpen = open;
            }
            var compact = b.Kind is Kind.Tool or Kind.Thinking && previous is Kind.Tool or Kind.Thinking && b.Caption.Length == 0;
            if (previous is not null) y += compact ? tight : gap;
            b.CaptionRect = b.Caption.Length == 0 ? Rectangle.Empty : new Rectangle(left, y, column, captionHeight);
            y += b.CaptionRect.Height;
            Size size = b.Kind switch
            {
                Kind.Tool or Kind.Thinking when open => new Size(column, b.Layout.Height + 2 * padY),
                Kind.System => new Size(column, b.Layout.Height + 2 * padY),
                _ => new Size(b.Layout.Width + 2 * padX, b.Layout.Height + 2 * padY),
            };
            var x = b.Message.Role == ChatRole.User ? left + column - size.Width : left;
            b.Bubble = new Rectangle(new Point(x, y), size);
            b.Origin = new Point(x + padX, y + padY);
            y += size.Height;
            previous = b.Kind;
        }
        measure.Graphics = null;
        contentHeight = y + pad;
        bar.LargeChange = Math.Max(1, ClientSize.Height);
        bar.Maximum = Math.Max(0, contentHeight - 1);
        bar.Enabled = contentHeight > ClientSize.Height;
        bar.Value = Math.Clamp(bar.Value, 0, MaxScroll);
        Invalidate();
    }

    private int MaxScroll => Math.Max(0, contentHeight - ClientSize.Height);

    private void ScrollBy(int delta) => bar.Value = Math.Clamp(bar.Value + delta, 0, MaxScroll);

    private (Caret From, Caret To) Ordered() => anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);

    // Nearest caret to a viewport point; points between blocks snap to the closest block edge.
    private Caret CaretAt(Point p)
    {
        var at = new Point(p.X, p.Y + bar.Value);
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (at.Y < b.Bubble.Top) return new Caret(i, 0);
            if (at.Y < b.Bubble.Bottom)
            {
                if (at.X < b.Bubble.Left && b.Message.Role != ChatRole.User) return new Caret(i, 0);
                return new Caret(i, b.Layout.OffsetAt(new Point(at.X - b.Origin.X, at.Y - b.Origin.Y)));
            }
        }
        return new Caret(blocks.Count - 1, blocks[^1].Layout.Plain.Length);
    }

    private int HitIndex(Point p)
    {
        var at = new Point(p.X, p.Y + bar.Value);
        return blocks.FindIndex(b => b.Bubble.Contains(at));
    }

    private Block? Hit(Point p) => HitIndex(p) is var i and >= 0 ? blocks[i] : null;

    // Collapsed rows toggle anywhere; expanded ones only on their header line, so the body
    // stays selectable. Long messages expand from their final "展开剩余" line.
    private bool OnToggle(Block b, Point p)
    {
        var dy = p.Y + bar.Value - b.Origin.Y;
        if (b.Kind is Kind.Thinking or Kind.Tool) return !IsOpen(b) || dy < b.Layout.Lines[0].Height;
        return b.Previewed && dy >= b.Layout.Lines[^1].Y;
    }

    private void Toggle(Block b)
    {
        if (!expanded.Add((b.Index, b.Kind, b.Sub))) expanded.Remove((b.Index, b.Kind, b.Sub));
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); measure.Clear(); foreach (var b in blocks) b.LayoutWidth = -1; Relayout(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollBy(-e.Delta * DeviceDpi / 96); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.C when e.Control: Copy(SelectedText); break;
            case Keys.A when e.Control && blocks.Count > 0: anchor = new Caret(0, 0); focus = new Caret(blocks.Count - 1, blocks[^1].Layout.Plain.Length); Invalidate(); break;
            case Keys.Up: ScrollBy(-bar.SmallChange); break;
            case Keys.Down: ScrollBy(bar.SmallChange); break;
            case Keys.PageUp: ScrollBy(-ClientSize.Height * 9 / 10); break;
            case Keys.PageDown: ScrollBy(ClientSize.Height * 9 / 10); break;
            case Keys.Home: bar.Value = 0; break;
            case Keys.End: bar.Value = MaxScroll; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (blocks.Count == 0) return;
        if (e.Button == MouseButtons.Right)
        {
            if (Hit(e.Location) is { } hit) ShowMenu(hit, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2 && Hit(e.Location) is { } b && !OnToggle(b, e.Location))
        {
            var i = blocks.IndexOf(b);
            anchor = new Caret(i, 0);
            focus = new Caret(i, b.Layout.Plain.Length);
            Invalidate();
            return;
        }
        var caret = CaretAt(e.Location);
        if ((ModifierKeys & Keys.Shift) == 0) anchor = caret;
        focus = caret;
        dragging = true;
        moved = false;
        downAt = e.Location;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging)
        {
            if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > SystemInformation.DragSize.Width) moved = true;
            focus = CaretAt(e.Location);
            dragScroll.Enabled = e.Y < 0 || e.Y > ClientSize.Height;
            Invalidate();
            return;
        }
        var index = HitIndex(e.Location);
        var b = index >= 0 ? blocks[index] : null;
        var hover = b is { Kind: Kind.Tool or Kind.Thinking } ? index : -1;
        if (hover != hovered) { hovered = hover; Invalidate(); }
        Cursor = b is null ? Cursors.Default : OnToggle(b, e.Location) ? Cursors.Hand : Cursors.IBeam;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered >= 0) { hovered = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        dragScroll.Enabled = false;
        Capture = false;
        if (moved || e.Button != MouseButtons.Left || Hit(e.Location) is not { } b) return;
        if (OnToggle(b, e.Location)) Toggle(b);
    }

    private void DragTick()
    {
        var p = PointToClient(MousePosition);
        ScrollBy(p.Y < 0 ? p.Y : p.Y - ClientSize.Height);
        focus = CaretAt(p);
        Invalidate();
    }

    private void ShowMenu(Block b, Point at)
    {
        var menu = new ContextMenuStrip { Font = NoteFont, ShowImageMargin = false };
        var selection = SelectedText;
        menu.Items.Add("复制选中文字", null, (_, _) => Copy(selection)).Enabled = selection.Length > 0;
        menu.Items.Add("复制这条（完整原文）", null, (_, _) => Copy(FullText(b)));
        menu.Items.Add("在窗口中查看全文", null, (_, _) => OpenFull?.Invoke(b.Caption.Length > 0 ? b.Caption : Speaker(b.Message.Role), FullText(b)));
        if (b.Kind is Kind.Tool or Kind.Thinking || b.Previewed || (IsOpen(b) && b.Message.Text.Trim().Length > PreviewLimit))
            menu.Items.Add(IsOpen(b) ? "收起" : "展开", null, (_, _) => Toggle(b));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("复制全部对话", null, (_, _) => Copy(Transcript()));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, at);
    }

    internal static void Copy(string text)
    {
        try { if (text.Length > 0) Clipboard.SetText(Crlf(text)); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show("剪贴板暂时被其他程序占用，请稍后重试。", "无法复制", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var view = new Rectangle(0, 0, ClientSize.Width - bar.Width, ClientSize.Height);
        if (blocks.Count == 0)
        {
            TextRenderer.DrawText(g, Placeholder, BodyFont, view, DesktopTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        var offset = bar.Value;
        var s = DeviceDpi / 96f;
        var (from, to) = Ordered();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var top = b.CaptionRect.IsEmpty ? b.Bubble.Top : b.CaptionRect.Top;
            if (top - offset > view.Bottom) break;
            if (b.Bubble.Bottom - offset < 0) continue;
            if (!b.CaptionRect.IsEmpty)
                TextRenderer.DrawText(g, b.Caption, CaptionFont, new Rectangle(b.CaptionRect.X, b.CaptionRect.Y - offset, b.CaptionRect.Width, b.CaptionRect.Height), DesktopTheme.Faint,
                    (b.Message.Role == ChatRole.User ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            var bubble = new Rectangle(b.Bubble.X, b.Bubble.Y - offset, b.Bubble.Width, b.Bubble.Height);
            PaintFrame(g, b, bubble, i == hovered, s);
            var selStart = i < from.Block || i > to.Block ? -1 : i == from.Block ? from.Offset : 0;
            var selEnd = i == to.Block ? to.Offset : int.MaxValue;
            var codeWidth = b.Kind is Kind.Tool or Kind.Thinking or Kind.System ? bubble.Width - 2 * (b.Origin.X - b.Bubble.X) : b.Layout.Width;
            PaintText(g, b, new Point(b.Origin.X, b.Origin.Y - offset), view, selStart, selEnd, codeWidth, s);
        }
    }

    private void PaintFrame(Graphics g, Block b, Rectangle bubble, bool hover, float s)
    {
        var open = IsOpen(b);
        var (fill, border, radius) = b.Kind switch
        {
            Kind.User => (UserFill, Color.Empty, 16f),
            Kind.System => (SystemFill, Color.Empty, 10f),
            Kind.Tool or Kind.Thinking when open => (Color.White, DesktopTheme.Border, 10f),
            Kind.Tool or Kind.Thinking when hover => (DesktopTheme.Hover, Color.Empty, 8f),
            _ => (Color.Empty, Color.Empty, 0f),
        };
        if (fill == Color.Empty && border == Color.Empty) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedRect(bubble, radius * s);
        if (fill != Color.Empty) { using var brush = new SolidBrush(fill); g.FillPath(brush, path); }
        if (border != Color.Empty) { using var pen = new Pen(border); g.DrawPath(pen, path); }
        g.SmoothingMode = SmoothingMode.None;
    }

    private static void PaintText(Graphics g, Block b, Point origin, Rectangle view, int selStart, int selEnd, int codeWidth, float s)
    {
        using var codeFill = new SolidBrush(CodeFill);
        using var inlineFill = new SolidBrush(InlineCodeFill);
        using var selFill = new SolidBrush(Highlight);
        var layout = b.Layout;
        var pad = (int)(6 * s);
        foreach (var line in layout.Lines)
        {
            var y = origin.Y + line.Y;
            if (y > view.Bottom) break;
            if (y + line.Height < 0) continue;
            if (line.Code) g.FillRectangle(codeFill, origin.X - pad, y, codeWidth + 2 * pad, line.Height);
            if (line.Rule) { using var pen = new Pen(DesktopTheme.Border); g.DrawLine(pen, origin.X, y + line.Height / 2, origin.X + layout.Width, y + line.Height / 2); }
            if (selStart >= 0 && selStart <= line.End && selEnd > line.Start)
            {
                var a = Math.Max(selStart, line.Start);
                var z = Math.Min(selEnd, line.End);
                var x1 = line.XOf(a);
                var x2 = selEnd > line.End ? Math.Max(line.XOf(z) + (int)(4 * s), x1 + (int)(4 * s)) : line.XOf(z);
                if (x2 > x1) g.FillRectangle(selFill, origin.X + x1, y, x2 - x1, line.Height);
            }
            foreach (var seg in line.Segs)
            {
                var font = seg.Style.Font;
                var segY = y + line.Height - font.Height;
                if (seg.Style.Code && !line.Code) g.FillRectangle(inlineFill, origin.X + seg.X - 1, segY, seg.Right - seg.X + 2, font.Height);
                TextRenderer.DrawText(g, seg.Text, font, new Point(origin.X + seg.X, segY), seg.Style.Color, TextMeasurer.Flags);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) dragScroll.Dispose();
        base.Dispose(disposing);
    }
}
