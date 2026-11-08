using System.Drawing.Drawing2D;

namespace CodexCleaner;

// ChatGPT-style palette: white surfaces on a near-white canvas, near-black ink and pill
// buttons, hairline borders, and the ChatGPT green only for "ready" states.
internal static class DesktopTheme
{
    public static readonly Color Ink = Color.FromArgb(13, 13, 13);
    public static readonly Color Muted = Color.FromArgb(93, 93, 93);
    public static readonly Color Faint = Color.FromArgb(143, 143, 143);
    public static readonly Color Accent = Color.FromArgb(48, 48, 48);
    public static readonly Color Hover = Color.FromArgb(244, 244, 244);
    public static readonly Color Pressed = Color.FromArgb(232, 232, 232);
    public static readonly Color Canvas = Color.FromArgb(249, 249, 249);
    public static readonly Color Border = Color.FromArgb(229, 229, 229);
    public static readonly Color Green = Color.FromArgb(16, 163, 127);
    public static readonly Color GreenFill = Color.FromArgb(232, 246, 241);
    public static readonly Color WarningFill = Color.FromArgb(253, 246, 232);
    public static readonly Color Selection = Color.FromArgb(200, 220, 252);
    public static readonly Color Link = Color.FromArgb(37, 99, 235);
    public static readonly Color Code = Color.FromArgb(55, 65, 81);
    public static readonly Color Warning = Color.FromArgb(176, 110, 18);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color DangerHover = Color.FromArgb(200, 30, 30);
    public static readonly Color DangerPressed = Color.FromArgb(170, 24, 24);
    public static readonly Color InkHover = Color.FromArgb(54, 54, 54);

    // A large frame of the embedded app.ico; the shell-extracted icon is only 32 px.
    public static Bitmap Logo(int size)
    {
        using var stream = typeof(DesktopTheme).Assembly.GetManifestResourceStream("CodexCleaner.app.ico")!;
        using var icon = new Icon(stream, size, size);
        return icon.ToBitmap();
    }

    public static Color BackgroundOf(Control? control)
    {
        while (control is not null)
        {
            if (control.BackColor.A == 255) return control.BackColor;
            control = control.Parent;
        }
        return Canvas;
    }

    public static Label Label(string text, float size = 9f, bool bold = false) => new()
    {
        Text = text, ForeColor = bold ? Ink : Muted, BackColor = Color.Transparent,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = Padding.Empty, UseMnemonic = false,
    };

    public static Button Button(string text, bool primary = false, bool danger = false) => new ThemeButton(primary || danger, danger)
    {
        Text = text,
    };

    public static GraphicsPath RoundedRect(Rectangle rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ThemeButton : Button
{
    private readonly bool primary, danger;
    private bool hovered, pressed;

    public ThemeButton(bool primary, bool danger = false)
    {
        this.primary = primary;
        this.danger = danger;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        BackColor = Color.Transparent;
        ForeColor = primary ? Color.White : DesktopTheme.Ink;
        Font = new Font("Microsoft YaHei UI", 9f);
        Padding = new Padding(primary ? 18 : 12, 6, primary ? 18 : 12, 6);
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var height = (int)Math.Round((primary ? 38 : 32) * DeviceDpi / 96f);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + (int)(4 * DeviceDpi / 96f)),
            Math.Max(MinimumSize.Height, Math.Max(height, text.Height + Padding.Vertical)));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); pressed = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hovered = pressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        e.Graphics.Clear(DesktopTheme.BackgroundOf(Parent));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var fill = primary
            ? !Enabled ? DesktopTheme.Pressed
                : danger ? pressed ? DesktopTheme.DangerPressed : hovered ? DesktopTheme.DangerHover : DesktopTheme.Danger
                : pressed ? Color.Black : hovered ? DesktopTheme.InkHover : DesktopTheme.Ink
            : !Enabled ? Color.White : pressed ? DesktopTheme.Pressed : hovered ? DesktopTheme.Hover : Color.White;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = DesktopTheme.RoundedRect(rect, rect.Height / 2f);
        using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
        if (!primary)
        {
            using var border = new Pen(DesktopTheme.Border);
            e.Graphics.DrawPath(border, path);
        }
        var textColor = !Enabled ? DesktopTheme.Faint : primary ? Color.White : ForeColor;
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -(int)(4 * scale), 0), textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(primary ? DesktopTheme.Faint : DesktopTheme.Accent, Math.Max(1, scale));
            e.Graphics.DrawPath(pen, path);
        }
    }
}

internal class SurfacePanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = DesktopTheme.Border;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; init; }

    public SurfacePanel()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        ResizeRedraw = true;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (CornerRadius == 0 || Width < 2 || Height < 2) { base.OnPaintBackground(e); return; }
        e.Graphics.Clear(DesktopTheme.BackgroundOf(Parent));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DesktopTheme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius * DeviceDpi / 96f);
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillPath(fill, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        using var pen = new Pen(BorderColor);
        if (CornerRadius == 0) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        else
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = DesktopTheme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius * DeviceDpi / 96f);
            e.Graphics.DrawPath(pen, path);
        }
    }
}

internal sealed class SearchField : SurfacePanel
{
    private readonly TextBox input;
    private readonly Button clear;

    public SearchField(TextBox input, Button clear)
    {
        this.input = input;
        this.clear = clear;
        CornerRadius = 20;
        TabStop = false;
        input.Margin = clear.Margin = Padding.Empty;
        Controls.Add(input);
        Controls.Add(clear);
        Enter += (_, _) => { BorderColor = DesktopTheme.Accent; Invalidate(); };
        Leave += (_, _) => { BorderColor = DesktopTheme.Border; Invalidate(); };
        Click += (_, _) => input.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (input is null || clear is null) return;
        var scale = DeviceDpi / 96f;
        var inset = (int)(38 * scale);
        var buttonSize = (int)(28 * scale);
        input.SetBounds(inset, Math.Max(0, (Height - input.PreferredHeight) / 2), Math.Max(0, Width - inset - (int)(36 * scale)), input.PreferredHeight);
        clear.SetBounds(Width - buttonSize - (int)(5 * scale), (Height - buttonSize) / 2, buttonSize, buttonSize);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var scale = DeviceDpi / 96f;
        var x = 13 * scale;
        var y = Height / 2f - 6 * scale;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(DesktopTheme.Muted, 1.5f * scale);
        e.Graphics.DrawEllipse(pen, x, y, 10 * scale, 10 * scale);
        e.Graphics.DrawLine(pen, x + 8 * scale, y + 8 * scale, x + 13 * scale, y + 13 * scale);
    }
}

internal sealed class ClearSearchButton : Button
{
    private bool hovered;

    public ClearSearchButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.White;
        Cursor = Cursors.Hand;
        AccessibleName = "清除搜索";
        Padding = Margin = Padding.Empty;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        if (hovered && Enabled)
        {
            using var shape = DesktopTheme.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 5 * scale);
            using var fill = new SolidBrush(DesktopTheme.Hover);
            e.Graphics.FillPath(fill, shape);
        }
        using var pen = new Pen(DesktopTheme.Muted, 1.5f * scale);
        var x = Width / 2f;
        var y = Height / 2f;
        var r = 3.5f * scale;
        e.Graphics.DrawLine(pen, x - r, y - r, x + r, y + r);
        e.Graphics.DrawLine(pen, x - r, y + r, x + r, y - r);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}

// Retain the native dropdown, accessibility and keyboard behavior; paint its
// closed face so it shares the search field's spacing, border and chevron.
internal sealed class FilterComboBox : ComboBox
{
    private bool hovered;

    public FilterComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 32;
        IntegralHeight = false;
        DropDownHeight = 180;
        BackColor = Color.White;
        ForeColor = DesktopTheme.Ink;
        Cursor = Cursors.Hand;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemHeight = (int)Math.Round(32 * DeviceDpi / 96f);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDown(EventArgs e) { base.OnDropDown(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? DesktopTheme.Hover : Color.White);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var inset = (int)(10 * DeviceDpi / 96f);
        var bounds = Rectangle.FromLTRB(e.Bounds.Left + inset, e.Bounds.Top, e.Bounds.Right - inset, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, bounds, DesktopTheme.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int paint = 0x000F, print = 0x0317, printClient = 0x0318;
        if (m.Msg != paint && m.Msg != print && m.Msg != printClient) return;
        if (Width < 2 || Height < 2) return;
        using var graphics = m.Msg != paint && m.WParam != IntPtr.Zero ? Graphics.FromHdc(m.WParam) : Graphics.FromHwnd(Handle);
        graphics.Clear(DesktopTheme.BackgroundOf(Parent));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        using var path = DesktopTheme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2f);
        using var fill = new SolidBrush(hovered && Enabled ? DesktopTheme.Hover : Color.White);
        graphics.FillPath(fill, path);
        using var border = new Pen(Focused || DroppedDown ? DesktopTheme.Accent : DesktopTheme.Border);
        graphics.DrawPath(border, path);
        var bounds = Rectangle.FromLTRB((int)(12 * scale), 0, Width - (int)(30 * scale), Height);
        TextRenderer.DrawText(graphics, Text, Font, bounds, Enabled ? ForeColor : DesktopTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var x = Width - 17 * scale;
        var y = Height / 2f;
        using var arrow = new Pen(DesktopTheme.Muted, 1.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLines(arrow, [new PointF(x - 3.5f * scale, y - 1.5f * scale), new PointF(x, y + 2 * scale), new PointF(x + 3.5f * scale, y - 1.5f * scale)]);
    }
}

// Pill toggle (ported from DevinCleaner); keeps CheckBox semantics.
internal sealed class PillToggle : CheckBox
{
    private bool hovered;

    public PillToggle(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        AutoSize = true;
        Font = new Font("Microsoft YaHei UI", 9f);
        Padding = new Padding(12, 0, 12, 0);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return new Size(text.Width + Padding.Horizontal, (int)Math.Round(30 * DeviceDpi / 96f));
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(DesktopTheme.BackgroundOf(Parent));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using var path = DesktopTheme.RoundedRect(rect, rect.Height / 2f);
        using (var fill = new SolidBrush(Checked ? DesktopTheme.Ink : hovered && Enabled ? DesktopTheme.Hover : Color.White)) e.Graphics.FillPath(fill, path);
        if (!Checked) { using var pen = new Pen(DesktopTheme.Border); e.Graphics.DrawPath(pen, path); }
        var ink = !Enabled ? DesktopTheme.Faint : Checked ? Color.White : DesktopTheme.Accent;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused && ShowFocusCues) { using var pen = new Pen(DesktopTheme.Accent); e.Graphics.DrawPath(pen, path); }
    }
}
