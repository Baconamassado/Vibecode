using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Bakobtify.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(14, 14, 16);
    public static readonly Color Sidebar = Color.FromArgb(17, 17, 19);
    public static readonly Color Surface = Color.FromArgb(24, 24, 27);
    public static readonly Color Hover = Color.FromArgb(34, 34, 37);
    public static readonly Color Border = Color.FromArgb(39, 39, 42);
    public static readonly Color Text = Color.FromArgb(243, 241, 238);
    public static readonly Color Muted = Color.FromArgb(166, 164, 173);
    public static readonly Color Accent = Color.FromArgb(250, 163, 89);
    public static readonly Color Pink = Color.FromArgb(231, 144, 180);

    private static readonly Dictionary<(int, FontStyle, string), Font> Fonts = new();

    public static Rectangle DesignArea(Graphics graphics, Size pixels, float scale)
    {
        graphics.ScaleTransform(scale, scale);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return new Rectangle(0, 0, (int)Math.Ceiling(pixels.Width / scale),
            (int)Math.Ceiling(pixels.Height / scale));
    }

    public static Font Font(int size, FontStyle style = FontStyle.Regular, string family = "Segoe UI Variable Text")
    {
        var key = (size, style, family);
        if (!Fonts.TryGetValue(key, out var font))
            Fonts[key] = font = new Font(family, size, style, GraphicsUnit.Pixel);
        return font;
    }

    public static Font Medium(int size) => Font(size, family: "Segoe UI Semibold");
    public static Font Heading(int size) => Font(size, FontStyle.Bold, "Segoe UI Variable Display");

    public static Label Label(string text, int size = 14, Color? color = null, bool bold = false) => new ThemeLabel
    {
        Text = text,
        Font = bold ? size >= 19 ? Heading(size) : Medium(size) : Font(size),
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        UseMnemonic = false
    };

    public static void Fill(Graphics g, Rectangle rect, Color color, int radius = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var brush = new SolidBrush(color);
        if (radius <= 0) { g.FillRectangle(brush, rect); return; }
        using var path = Rounded(rect, radius);
        g.FillPath(brush, path);
    }

    public static void Outline(Graphics g, Rectangle rect, Color color, int radius)
    {
        using var path = Rounded(rect, radius);
        using var pen = new Pen(color);
        g.DrawPath(pen, path);
    }

    public static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Write(Graphics g, string text, Rectangle rect, Font font, Color color,
        TextFormatFlags align = TextFormatFlags.Left)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
            Alignment = (align & TextFormatFlags.Right) != 0 ? StringAlignment.Far :
                (align & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near,
            LineAlignment = StringAlignment.Center
        };
        using var brush = new SolidBrush(color);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(text, font, brush, rect, format);
    }
}

internal sealed class ThemeLabel : Label
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float TextOpacity { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float PaintScale { get; set; } = 1;

    public ThemeLabel() => SetStyle(ControlStyles.Opaque | ControlStyles.ResizeRedraw |
        ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        Theme.Fill(g, ClientRectangle, BackColor.A == 0 ? Parent?.BackColor ?? Theme.Background : BackColor);
        var area = Theme.DesignArea(g, ClientSize, PaintScale);
        if (TextAlign == ContentAlignment.TopLeft)
        {
            using var brush = new SolidBrush(ForeColor);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(Text, Font, brush, area);
            return;
        }

        var alignment = TextAlign switch
        {
            ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.MiddleRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        Theme.Write(g, Text, area, Font, Color.FromArgb((int)(ForeColor.A * TextOpacity), ForeColor), alignment);
    }
}

internal sealed class Surface : Panel
{
    public Surface(Color color)
    {
        BackColor = color;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}

internal sealed class SoftButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color FillColor { get; set; } = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color HoverColor { get; set; } = Theme.Hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color Stroke { get; set; } = Color.Transparent;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal int Radius { get; set; } = 10;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Glyph { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Color? GlyphColor { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Color>? PaintBackground { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<Graphics, Rectangle>? DrawContent { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AlignLeft { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float PaintScale { get; set; } = 1;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float Reveal { get; set; } = 1;
    private bool _hover;

    public SoftButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.Text;
        Font = Theme.Medium(14);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, false);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // O buffer é reutilizado entre controles. Cubra todos os pixels antes
        // de suavizar as bordas, inclusive a primeira e a última coluna.
        g.SmoothingMode = SmoothingMode.None;
        Theme.Fill(g, ClientRectangle, PaintBackground?.Invoke() ?? BackColor);
        var area = Theme.DesignArea(g, ClientSize, PaintScale);
        var rect = new Rectangle(0, 0, area.Width - 1, area.Height - 1);
        var fill = Enabled && _hover ? HoverColor : FillColor;
        if (fill.A != 0) Theme.Fill(g, rect, fill, Radius);
        if (Stroke.A != 0 && rect.Width > 0 && rect.Height > 0) Theme.Outline(g, rect, Stroke, Radius);
        if (Focused && ShowFocusCues && area.Width > 8 && area.Height > 8)
            Theme.Outline(g, Rectangle.Inflate(rect, -3, -3), Theme.Accent, Radius);
        if (DrawContent is not null)
        {
            DrawContent(g, area);
            if (Reveal < 1)
                Theme.Fill(g, area, Color.FromArgb((int)(255 * (1 - Reveal)), BackColor));
            return;
        }

        var textWidth = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font,
            Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        var color = Enabled ? ForeColor : Color.FromArgb(98, 97, 104);
        if (Glyph.Length != 0)
        {
            var x = AlignLeft ? 14 : Math.Max(5, (area.Width - (textWidth == 0 ? 18 : 27 + textWidth)) / 2);
            Icons.Draw(g, Glyph, new Rectangle(x, (area.Height - 18) / 2, 18, 18),
                Enabled ? GlyphColor ?? color : color);
            if (textWidth != 0)
                Theme.Write(g, Text, new Rectangle(x + 27, 0, Math.Max(0, area.Width - x - 30), area.Height), Font, color);
        }
        else
        {
            Theme.Write(g, Text, Rectangle.Inflate(area, -8, 0), Font, color,
                AlignLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
        }
    }
}

internal static class Icons
{
    public static void Draw(Graphics g, string name, Rectangle rect, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(rect.Left, rect.Top);
        g.ScaleTransform(rect.Width / 20f, rect.Height / 20f);
        using var pen = new Pen(color, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        void Lines(params PointF[] points) => g.DrawLines(pen, points);
        void Line(float a, float b, float c, float d) => g.DrawLine(pen, a, b, c, d);
        void Triangle(float x, float y, float size) => g.FillPolygon(brush,
            [new PointF(x, y), new PointF(x + size, y + size / 2), new PointF(x, y + size)]);
        switch (name)
        {
            case "home": Lines(new(3, 9), new(10, 3), new(17, 9), new(17, 17), new(12, 17),
                new(12, 12), new(8, 12), new(8, 17), new(3, 17), new(3, 9)); break;
            case "explore":
                g.DrawEllipse(pen, 2.8f, 2.8f, 14.4f, 14.4f);
                Lines(new(7, 13), new(8.6f, 8.6f), new(13, 7), new(11.4f, 11.4f), new(7, 13)); break;
            case "heart":
            case "liked":
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(10, 16, 8, 14, 3, 11, 3, 7);
                    path.AddBezier(3, 7, 3, 3, 8, 3, 10, 6);
                    path.AddBezier(10, 6, 12, 3, 17, 3, 17, 7);
                    path.AddBezier(17, 7, 17, 11, 12, 14, 10, 16);
                    path.CloseFigure();
                    if (name == "liked") g.FillPath(brush, path); else g.DrawPath(pen, path);
                }
                break;
            case "history":
                g.DrawEllipse(pen, 3, 3, 14, 14);
                Lines(new(10, 6), new(10, 10), new(13, 12)); break;
            case "search": g.DrawEllipse(pen, 3, 3, 10, 10); Line(12, 12, 16, 16); break;
            case "back": Lines(new(12, 5), new(7, 10), new(12, 15)); break;
            case "forward": Lines(new(8, 5), new(13, 10), new(8, 15)); break;
            case "play": Triangle(6, 4, 12); break;
            case "pause": g.FillRectangle(brush, 5, 4, 3.5f, 12); g.FillRectangle(brush, 11, 4, 3.5f, 12); break;
            case "prev": Lines(new(14, 4), new(6, 10), new(14, 16), new(14, 4)); Line(3, 4, 3, 16); break;
            case "next": Lines(new(6, 4), new(14, 10), new(6, 16), new(6, 4)); Line(17, 4, 17, 16); break;
            case "list": Line(3, 5, 17, 5); Line(3, 10, 17, 10); Line(3, 15, 12, 15); break;
            case "queue": Line(3, 5, 13, 5); Line(3, 10, 12, 10); Line(3, 15, 10, 15); Triangle(14, 9, 5); break;
            case "volume":
            case "mute":
                Lines(new(3, 8), new(6, 8), new(10, 5), new(10, 15), new(6, 12), new(3, 12), new(3, 8));
                if (name == "volume") g.DrawArc(pen, 9, 5, 8, 10, -55, 110);
                else { Line(13, 8, 17, 12); Line(17, 8, 13, 12); }
                break;
            case "repeat":
            case "repeat1":
                Lines(new(4, 9), new(4, 5), new(16, 5), new(14, 3)); Line(16, 5, 14, 7);
                Lines(new(16, 11), new(16, 15), new(4, 15), new(6, 13)); Line(4, 15, 6, 17);
                if (name == "repeat1") Line(10, 8, 10, 12);
                break;
            case "shuffle":
                Lines(new(2, 5), new(6, 5), new(13, 15), new(17, 15), new(15, 13));
                Line(17, 15, 15, 17); Line(2, 15, 6, 15); Line(6, 15, 8, 12);
                Lines(new(11, 8), new(13, 5), new(17, 5), new(15, 3)); Line(17, 5, 15, 7); break;
            case "settings":
                Line(3, 6, 17, 6); Line(3, 14, 17, 14); g.FillEllipse(brush, 6, 3.5f, 5, 5);
                g.FillEllipse(brush, 11, 11.5f, 5, 5); break;
            case "plus": Line(5, 10, 15, 10); Line(10, 5, 10, 15); break;
            case "link": Lines(new(9, 5), new(12, 2), new(16, 2), new(18, 4), new(18, 8), new(15, 11));
                Lines(new(11, 15), new(8, 18), new(4, 18), new(2, 16), new(2, 12), new(5, 9));
                Line(7, 13, 13, 7); break;
            case "min": Line(5, 10, 15, 10); break;
            case "max": g.DrawRectangle(pen, 5, 5, 10, 10); break;
            case "restore": g.DrawRectangle(pen, 4, 7, 9, 9); Lines(new(7, 5), new(7, 3), new(17, 3), new(17, 13), new(15, 13)); break;
            case "close": Line(5, 5, 15, 15); Line(15, 5, 5, 15); break;
            case "more":
                foreach (var x in new[] { 4, 9, 14 }) g.FillEllipse(brush, x, 9, 2, 2);
                break;
            case "logo":
                g.FillRectangle(brush, 3, 8, 2.5f, 5); g.FillRectangle(brush, 8.5f, 4, 2.5f, 12);
                g.FillRectangle(brush, 14, 6, 2.5f, 8); break;
        }
        g.Restore(state);
    }
}
