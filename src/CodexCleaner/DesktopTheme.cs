using System.Drawing.Drawing2D;

namespace CodexCleaner;

internal static class DesktopTheme
{
    public static readonly Color Ink = Color.FromArgb(35, 49, 70);
    public static readonly Color Muted = Color.FromArgb(113, 125, 145);
    public static readonly Color Accent = Color.FromArgb(48, 100, 226);
    public static readonly Color Navy = Color.FromArgb(25, 41, 65);
    public static readonly Color Canvas = Color.FromArgb(243, 246, 251);
    public static readonly Color Border = Color.FromArgb(222, 229, 239);
    public static readonly Font SmallFont = new("Microsoft YaHei UI", 8f);

    public static Label Label(string text, float size = 9f, bool bold = false) => new()
    {
        Text = text, ForeColor = bold ? Ink : Muted, BackColor = Color.Transparent,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = Padding.Empty, UseMnemonic = false,
    };

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink, Font = new Font("Microsoft YaHei UI", 8.5f, primary ? FontStyle.Bold : FontStyle.Regular),
            Padding = new Padding(10, 5, 10, 5), Margin = Padding.Empty,
            UseVisualStyleBackColor = false, Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(38, 87, 204) : Color.FromArgb(242, 246, 252);
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(32, 76, 183) : Color.FromArgb(232, 239, 250);
        return button;
    }

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

internal sealed class SurfacePanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; init; } = DesktopTheme.Border;

    public SurfacePanel()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
