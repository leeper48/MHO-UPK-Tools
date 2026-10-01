namespace MhoExtendedModManager.Gui;

/// <summary>A preset swatch (the editor's Powers tab; from the MHO Hero Creator): a rounded color chip with its number, or an
/// empty outline.</summary>
sealed class ColorSwatch : Control
{
    Color? swatch;
    bool hover;
    public int Number { get; set; }
    public Color? Swatch { get => swatch; set { swatch = value; Invalidate(); } }

    public ColorSwatch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f, rad = 5 * s;
        var r = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90); path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90); path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        path.CloseFigure();
        if (swatch is { } c) using (var b = new SolidBrush(c)) g.FillPath(b, path);
        using (var pen = new Pen(hover ? Color.FromArgb(220, 255, 255, 255) : swatch == null ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0), (hover ? 2f : 1f) * s)) g.DrawPath(pen, path);
        bool light = swatch is { } sc && sc.R * 0.299 + sc.G * 0.587 + sc.B * 0.114 > 140;
        TextRenderer.DrawText(g, Number.ToString(), Ui.Heavy(8.5f), Rectangle.Round(r), swatch == null ? Ui.Subtle : light ? Color.Black : Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}
