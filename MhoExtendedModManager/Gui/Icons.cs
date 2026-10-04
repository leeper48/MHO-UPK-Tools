using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Icon buttons (Kurt, 2026-10-04: the Materials tab's buttons, then other small actions across the app): each drawn in the
/// button's text color (gray when disabled, as Ui.Rounded draws them); its name heads its tooltip, bold (Ui.TipTitled).
/// </summary>
static class Icons
{
    /// <summary>Turns <paramref name="b"/> into an icon button: square, no text, its tooltip the name (bold) + the old tip.</summary>
    public static void Make(Button b, string name, Action<Graphics, RectangleF, Pen, Brush> paint, float scale)
    {
        string tip = Ui.Tips.GetToolTip(b);
        b.Text = ""; b.AccessibleName = name;
        b.AutoSize = false; b.Padding = new Padding(0); b.Size = new Size((int)(34 * scale), (int)(30 * scale));
        Ui.TipTitled(b, name, tip);
        Ui.IconPainters.AddOrUpdate(b, (g, r, c) =>
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float m = Math.Min(r.Width, r.Height) * 0.24f;
            var box = RectangleF.FromLTRB(r.X + (r.Width - r.Height) / 2f + m, r.Y + m, r.X + (r.Width + r.Height) / 2f - m, r.Bottom - m);
            using var pen = new Pen(c, Math.Max(1.5f, r.Height / 17f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var br = new SolidBrush(c);
            paint(g, box, pen, br);
        });
    }

    /// <summary>Use a File: an open folder.</summary>
    public static void Folder(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float w = b.Width, h = b.Height;
        using var path = new GraphicsPath();
        path.AddLines(new[]
        {
            new PointF(b.X, b.Y + h * 0.15f), new PointF(b.X + w * 0.38f, b.Y + h * 0.15f), new PointF(b.X + w * 0.48f, b.Y + h * 0.3f),
            new PointF(b.Right, b.Y + h * 0.3f), new PointF(b.Right, b.Bottom - h * 0.05f), new PointF(b.X, b.Bottom - h * 0.05f),
        });
        path.CloseFigure();
        g.DrawPath(p, path);
        g.DrawLine(p, b.X, b.Y + h * 0.45f, b.Right, b.Y + h * 0.45f);
    }

    /// <summary>Back to Automatic: a circular arrow (reset).</summary>
    public static void Reset(Graphics g, RectangleF b, Pen p, Brush br)
    {
        var c = RectangleF.Inflate(b, -b.Width * 0.06f, -b.Height * 0.06f);
        g.DrawArc(p, c, -60, 300);
        // the arrow head at the arc's start (top right), pointing along it
        double a = -60 * Math.PI / 180;
        var tip = new PointF(c.X + c.Width / 2 + (float)Math.Cos(a) * c.Width / 2, c.Y + c.Height / 2 + (float)Math.Sin(a) * c.Height / 2);
        float s = b.Width * 0.3f;
        g.FillPolygon(br, new[] { new PointF(tip.X + s * 0.25f, tip.Y - s * 0.75f), new PointF(tip.X + s * 0.55f, tip.Y + s * 0.35f), new PointF(tip.X - s * 0.55f, tip.Y + s * 0.05f) });
    }

    /// <summary>OpenGL Normals: an up / down arrow (the green channel turned over).</summary>
    public static void FlipVertical(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cx = b.X + b.Width / 2, s = b.Width * 0.3f;
        g.DrawLine(p, cx, b.Y + s * 0.8f, cx, b.Bottom - s * 0.8f);
        g.FillPolygon(br, new[] { new PointF(cx, b.Y), new PointF(cx - s, b.Y + s), new PointF(cx + s, b.Y + s) });
        g.FillPolygon(br, new[] { new PointF(cx, b.Bottom), new PointF(cx - s, b.Bottom - s), new PointF(cx + s, b.Bottom - s) });
    }

    /// <summary>No Glow: a sun, struck through.</summary>
    public static void NoGlow(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, r = b.Width * 0.2f;
        g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            g.DrawLine(p, cx + c * r * 1.6f, cy + s * r * 1.6f, cx + c * b.Width * 0.48f, cy + s * b.Height * 0.48f);
        }
        g.DrawLine(p, b.X, b.Bottom, b.Right, b.Y);
    }

    /// <summary>Next Recipe: skip to the next.</summary>
    public static void Next(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float h = b.Height;
        g.FillPolygon(br, new[] { new PointF(b.X + b.Width * 0.1f, b.Y + h * 0.1f), new PointF(b.X + b.Width * 0.7f, b.Y + h * 0.5f), new PointF(b.X + b.Width * 0.1f, b.Bottom - h * 0.1f) });
        g.DrawLine(p, b.Right - b.Width * 0.1f, b.Y + h * 0.1f, b.Right - b.Width * 0.1f, b.Bottom - h * 0.1f);
    }

    /// <summary>Tag Colors: a price tag.</summary>
    public static void Tag(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        using var path = new GraphicsPath();
        path.AddLines(new[]
        {
            new PointF(b.X, b.Y + h * 0.42f), new PointF(b.X + w * 0.42f, b.Y), new PointF(b.Right, b.Y), new PointF(b.Right, b.Y + h * 0.58f),
            new PointF(b.X + w * 0.58f, b.Bottom),
        });
        path.CloseFigure();
        g.DrawPath(p, path);
        float r = w * 0.09f;
        g.FillEllipse(br, b.Right - w * 0.25f - r, b.Y + h * 0.25f - r, 2 * r, 2 * r);
    }

    /// <summary>From Channels: three overlapping circles (R, G, B).</summary>
    public static void Channels(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float r = b.Width * 0.3f, cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        foreach (var (dx, dy) in new[] { (0f, -0.36f), (-0.32f, 0.2f), (0.32f, 0.2f) })
            g.DrawEllipse(p, cx + dx * b.Width * 0.6f - r, cy + dy * b.Height * 0.6f - r, 2 * r, 2 * r);
    }

    /// <summary>Undo: a curved arrow back (left).</summary>
    public static void Undo(Graphics g, RectangleF b, Pen p, Brush br) => Curl(g, b, p, br, false);
    /// <summary>Redo: a curved arrow on (right).</summary>
    public static void Redo(Graphics g, RectangleF b, Pen p, Brush br) => Curl(g, b, p, br, true);

    static void Curl(Graphics g, RectangleF b, Pen p, Brush br, bool right)
    {
        var state = g.Save();
        if (right) { g.TranslateTransform(b.X + b.Width / 2, 0); g.ScaleTransform(-1, 1); g.TranslateTransform(-(b.X + b.Width / 2), 0); }
        float w = b.Width, h = b.Height;
        var arc = new RectangleF(b.X + w * 0.2f, b.Y + h * 0.2f, w * 0.75f, h * 0.7f);
        g.DrawArc(p, arc, 200, 230);
        // the arrow head at the arc's start, on the left, pointing left / down
        float s = w * 0.28f; var tip = new PointF(b.X + w * 0.05f, b.Y + h * 0.42f);
        g.FillPolygon(br, new[] { tip, new PointF(tip.X + s * 0.9f, tip.Y - s * 0.6f), new PointF(tip.X + s * 0.85f, tip.Y + s * 0.55f) });
        g.Restore(state);
    }

    /// <summary>Loop: two arrows chasing round.</summary>
    public static void Loop(Graphics g, RectangleF b, Pen p, Brush br)
    {
        var c = RectangleF.Inflate(b, -b.Width * 0.08f, -b.Height * 0.12f);
        g.DrawArc(p, c, 200, 130);
        g.DrawArc(p, c, 20, 130);
        float s = b.Width * 0.24f;
        PointF At(double deg) { double a = deg * Math.PI / 180; return new PointF(c.X + c.Width / 2 + (float)Math.Cos(a) * c.Width / 2, c.Y + c.Height / 2 + (float)Math.Sin(a) * c.Height / 2); }
        var t1 = At(330); g.FillPolygon(br, new[] { new PointF(t1.X + s * 0.6f, t1.Y + s * 0.1f), new PointF(t1.X - s * 0.5f, t1.Y - s * 0.55f), new PointF(t1.X - s * 0.35f, t1.Y + s * 0.6f) });
        var t2 = At(150); g.FillPolygon(br, new[] { new PointF(t2.X - s * 0.6f, t2.Y - s * 0.1f), new PointF(t2.X + s * 0.5f, t2.Y + s * 0.55f), new PointF(t2.X + s * 0.35f, t2.Y - s * 0.6f) });
    }

    /// <summary>Reset View: a frame's corners round a centered dot (frame the model again).</summary>
    public static void ResetView(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float l = b.Width * 0.3f;
        g.DrawLines(p, new[] { new PointF(b.Left, b.Top + l), new PointF(b.Left, b.Top), new PointF(b.Left + l, b.Top) });
        g.DrawLines(p, new[] { new PointF(b.Right - l, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Top + l) });
        g.DrawLines(p, new[] { new PointF(b.Right, b.Bottom - l), new PointF(b.Right, b.Bottom), new PointF(b.Right - l, b.Bottom) });
        g.DrawLines(p, new[] { new PointF(b.Left + l, b.Bottom), new PointF(b.Left, b.Bottom), new PointF(b.Left, b.Bottom - l) });
        float r = b.Width * 0.13f, cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
    }

    /// <summary>Look ▾: an eye with a small ▾ (a menu).</summary>
    public static void Look(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, cx = b.X + w * 0.42f, cy = b.Y + b.Height / 2, ew = w * 0.5f, eh = b.Height * 0.32f;
        using var path = new GraphicsPath();
        path.AddBezier(cx - ew, cy, cx - ew * 0.5f, cy - eh * 1.4f, cx + ew * 0.5f, cy - eh * 1.4f, cx + ew, cy);
        path.AddBezier(cx + ew, cy, cx + ew * 0.5f, cy + eh * 1.4f, cx - ew * 0.5f, cy + eh * 1.4f, cx - ew, cy);
        g.DrawPath(p, path);
        float r = eh * 0.62f;
        g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
        float ax = b.Right + w * 0.18f, ay = cy, a = w * 0.13f;
        g.FillPolygon(br, new[] { new PointF(ax - a, ay - a * 0.5f), new PointF(ax + a, ay - a * 0.5f), new PointF(ax, ay + a * 0.7f) });
    }

    /// <summary>Layout ▾: a 2 × 2 grid (four channels) with a small ▾.</summary>
    public static void Layout(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float s = b.Width * 0.36f, gap = b.Width * 0.08f, x = b.X - b.Width * 0.08f, y = b.Y + b.Height * 0.1f;
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) g.DrawRectangle(p, x + i * (s + gap), y + j * (s + gap), s, s);
        float ax = b.Right + b.Width * 0.12f, ay = b.Y + b.Height * 0.55f, a = b.Width * 0.14f;
        g.FillPolygon(br, new[] { new PointF(ax - a, ay - a * 0.5f), new PointF(ax + a, ay - a * 0.5f), new PointF(ax, ay + a * 0.7f) });
    }
}
