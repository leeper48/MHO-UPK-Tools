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

    /// <summary>A small ▾ right of the icon (the button opens a menu).</summary>
    static void Caret(Graphics g, RectangleF b, Brush br)
    {
        float ax = b.Right + b.Width * 0.18f, ay = b.Y + b.Height * 0.55f, a = b.Width * 0.13f;
        g.FillPolygon(br, new[] { new PointF(ax - a, ay - a * 0.5f), new PointF(ax + a, ay - a * 0.5f), new PointF(ax, ay + a * 0.7f) });
    }

    /// <summary>The icon drawn a little left and smaller, with a ▾ (menus).</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> WithMenu(Action<Graphics, RectangleF, Pen, Brush> icon) => (g, b, p, br) =>
    {
        var inner = new RectangleF(b.X - b.Width * 0.08f, b.Y + b.Height * 0.06f, b.Width * 0.88f, b.Height * 0.88f);
        icon(g, inner, p, br);
        Caret(g, b, br);
    };

    /// <summary>Build into Mod: a box with an arrow going in.</summary>
    public static void Build(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, cx = b.X + w / 2;
        g.DrawRectangle(p, b.X, b.Y + h * 0.45f, w, h * 0.55f);
        g.DrawLine(p, b.X, b.Y + h * 0.45f, b.X + w * 0.18f, b.Y + h * 0.3f);
        g.DrawLine(p, b.Right, b.Y + h * 0.45f, b.Right - w * 0.18f, b.Y + h * 0.3f);
        g.DrawLine(p, cx, b.Y - h * 0.08f, cx, b.Y + h * 0.5f);
        float s = w * 0.22f;
        g.FillPolygon(br, new[] { new PointF(cx, b.Y + h * 0.72f), new PointF(cx - s, b.Y + h * 0.72f - s * 1.1f), new PointF(cx + s, b.Y + h * 0.72f - s * 1.1f) });
    }

    /// <summary>Install Mod: an arrow down into a tray.</summary>
    public static void Install(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, cx = b.X + w / 2;
        g.DrawLines(p, new[] { new PointF(b.X, b.Y + h * 0.55f), new PointF(b.X, b.Bottom), new PointF(b.Right, b.Bottom), new PointF(b.Right, b.Y + h * 0.55f) });
        g.DrawLine(p, cx, b.Y - h * 0.04f, cx, b.Y + h * 0.5f);
        float s = w * 0.28f;
        g.FillPolygon(br, new[] { new PointF(cx, b.Bottom - h * 0.18f), new PointF(cx - s, b.Bottom - h * 0.18f - s * 0.95f), new PointF(cx + s, b.Bottom - h * 0.18f - s * 0.95f) });
    }

    /// <summary>New Mod: a plus.</summary>
    public static void Plus(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, r = b.Width * 0.45f;
        using var thick = (Pen)p.Clone(); thick.Width = p.Width * 1.3f;
        g.DrawLine(thick, cx - r, cy, cx + r, cy); g.DrawLine(thick, cx, cy - r, cx, cy + r);
    }

    /// <summary>Cancel: an X.</summary>
    public static void Cancel(Graphics g, RectangleF b, Pen p, Brush _)
    {
        var c = RectangleF.Inflate(b, -b.Width * 0.1f, -b.Height * 0.1f);
        g.DrawLine(p, c.Left, c.Top, c.Right, c.Bottom); g.DrawLine(p, c.Right, c.Top, c.Left, c.Bottom);
    }

    /// <summary>Save: a check mark.</summary>
    public static void Save(Graphics g, RectangleF b, Pen p, Brush _)
    {
        using var thick = (Pen)p.Clone(); thick.Width = p.Width * 1.3f;
        g.DrawLines(thick, new[] { new PointF(b.X + b.Width * 0.02f, b.Y + b.Height * 0.55f), new PointF(b.X + b.Width * 0.38f, b.Bottom - b.Height * 0.08f), new PointF(b.Right, b.Y + b.Height * 0.12f) });
    }

    /// <summary>Remove: a trash can.</summary>
    public static void Trash(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float w = b.Width, h = b.Height;
        g.DrawLine(p, b.X, b.Y + h * 0.18f, b.Right, b.Y + h * 0.18f);
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.35f, b.Y + h * 0.18f), new PointF(b.X + w * 0.38f, b.Y), new PointF(b.Right - w * 0.38f, b.Y), new PointF(b.Right - w * 0.35f, b.Y + h * 0.18f) });
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.12f, b.Y + h * 0.3f), new PointF(b.X + w * 0.2f, b.Bottom), new PointF(b.Right - w * 0.2f, b.Bottom), new PointF(b.Right - w * 0.12f, b.Y + h * 0.3f) });
        g.DrawLine(p, b.X + w * 0.4f, b.Y + h * 0.42f, b.X + w * 0.42f, b.Bottom - h * 0.14f);
        g.DrawLine(p, b.Right - w * 0.4f, b.Y + h * 0.42f, b.Right - w * 0.42f, b.Bottom - h * 0.14f);
    }

    /// <summary>Edit: a pencil.</summary>
    public static void Pencil(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        var tip = new PointF(b.X + w * 0.04f, b.Bottom - h * 0.04f);
        var a = new PointF(b.X + w * 0.04f, b.Bottom - h * 0.3f); var c = new PointF(b.X + w * 0.3f, b.Bottom - h * 0.04f);
        var a2 = new PointF(b.Right - w * 0.26f, b.Y + h * 0.02f); var c2 = new PointF(b.Right - w * 0.02f, b.Y + h * 0.26f);
        g.DrawPolygon(p, new[] { tip, a, a2, c2, c });
        g.DrawLine(p, b.Right - w * 0.4f, b.Y + h * 0.16f, b.Right - w * 0.16f, b.Y + h * 0.4f);
        g.FillPolygon(br, new[] { tip, new PointF(tip.X, tip.Y - h * 0.14f), new PointF(tip.X + w * 0.14f, tip.Y) });
    }

    /// <summary>Create Post: a speech bubble with lines of text.</summary>
    public static void Post(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float w = b.Width, h = b.Height;
        using var path = new GraphicsPath();
        path.AddLines(new[]
        {
            new PointF(b.X, b.Y + h * 0.05f), new PointF(b.Right, b.Y + h * 0.05f), new PointF(b.Right, b.Y + h * 0.7f),
            new PointF(b.X + w * 0.45f, b.Y + h * 0.7f), new PointF(b.X + w * 0.18f, b.Bottom), new PointF(b.X + w * 0.22f, b.Y + h * 0.7f),
            new PointF(b.X, b.Y + h * 0.7f),
        });
        path.CloseFigure();
        g.DrawPath(p, path);
        g.DrawLine(p, b.X + w * 0.2f, b.Y + h * 0.27f, b.Right - w * 0.2f, b.Y + h * 0.27f);
        g.DrawLine(p, b.X + w * 0.2f, b.Y + h * 0.47f, b.Right - w * 0.35f, b.Y + h * 0.47f);
    }

    /// <summary>Help: a question mark.</summary>
    public static void Help(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, cx = b.X + w / 2;
        g.DrawArc(p, cx - w * 0.3f, b.Y, w * 0.6f, h * 0.55f, 180, 250);
        g.DrawLine(p, cx + w * 0.1f, b.Y + h * 0.5f, cx, b.Y + h * 0.6f);
        g.DrawLine(p, cx, b.Y + h * 0.6f, cx, b.Y + h * 0.72f);
        float r = w * 0.08f;
        g.FillEllipse(br, cx - r, b.Bottom - 2 * r, 2 * r, 2 * r);
    }

    /// <summary>Settings: a gear.</summary>
    public static void Gear(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, ro = b.Width * 0.5f, ri = b.Width * 0.36f;
        var pts = new List<PointF>();
        for (int k = 0; k < 8; k++)
        {
            double a0 = (k - 0.22) * Math.PI / 4, a1 = (k + 0.22) * Math.PI / 4, a2 = (k + 0.5) * Math.PI / 4;
            pts.Add(new PointF(cx + (float)Math.Cos(a0) * ro, cy + (float)Math.Sin(a0) * ro));
            pts.Add(new PointF(cx + (float)Math.Cos(a1) * ro, cy + (float)Math.Sin(a1) * ro));
            pts.Add(new PointF(cx + (float)Math.Cos(a2) * ri, cy + (float)Math.Sin(a2) * ri));
        }
        g.DrawPolygon(p, pts.ToArray());
        float r = b.Width * 0.15f;
        g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
    }

    /// <summary>Check for Updates: a circular arrow round a small up arrow.</summary>
    public static void CheckUpdates(Graphics g, RectangleF b, Pen p, Brush br)
    {
        var c = RectangleF.Inflate(b, -b.Width * 0.04f, -b.Height * 0.04f);
        g.DrawArc(p, c, -50, 290);
        double a = -50 * Math.PI / 180;
        var tip = new PointF(c.X + c.Width / 2 + (float)Math.Cos(a) * c.Width / 2, c.Y + c.Height / 2 + (float)Math.Sin(a) * c.Height / 2);
        float s = b.Width * 0.26f;
        g.FillPolygon(br, new[] { new PointF(tip.X + s * 0.3f, tip.Y - s * 0.8f), new PointF(tip.X + s * 0.6f, tip.Y + s * 0.35f), new PointF(tip.X - s * 0.55f, tip.Y + s * 0.05f) });
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, u = b.Width * 0.16f;
        g.DrawLine(p, cx, cy - u * 1.1f, cx, cy + u * 1.2f);
        g.FillPolygon(br, new[] { new PointF(cx, cy - u * 1.7f), new PointF(cx - u, cy - u * 0.5f), new PointF(cx + u, cy - u * 0.5f) });
    }

    /// <summary>Find My Mods: a magnifying glass.</summary>
    public static void Search(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float r = b.Width * 0.32f, cx = b.X + r + b.Width * 0.06f, cy = b.Y + r + b.Height * 0.06f;
        g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
        using var thick = (Pen)p.Clone(); thick.Width = p.Width * 1.4f;
        g.DrawLine(thick, cx + r * 0.72f, cy + r * 0.72f, b.Right - b.Width * 0.04f, b.Bottom - b.Height * 0.04f);
    }

    /// <summary>Browse Nexus: a globe.</summary>
    public static void Globe(Graphics g, RectangleF b, Pen p, Brush _)
    {
        var c = RectangleF.Inflate(b, -b.Width * 0.03f, -b.Height * 0.03f);
        g.DrawEllipse(p, c);
        g.DrawEllipse(p, c.X + c.Width * 0.28f, c.Y, c.Width * 0.44f, c.Height);
        g.DrawLine(p, c.X, c.Y + c.Height / 2, c.Right, c.Y + c.Height / 2);
        g.DrawLine(p, c.X + c.Width * 0.1f, c.Y + c.Height * 0.25f, c.Right - c.Width * 0.1f, c.Y + c.Height * 0.25f);
        g.DrawLine(p, c.X + c.Width * 0.1f, c.Y + c.Height * 0.75f, c.Right - c.Width * 0.1f, c.Y + c.Height * 0.75f);
    }

    /// <summary>Export Maps: an arrow up out of a tray.</summary>
    public static void Export(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, cx = b.X + w / 2;
        g.DrawLines(p, new[] { new PointF(b.X, b.Y + h * 0.55f), new PointF(b.X, b.Bottom), new PointF(b.Right, b.Bottom), new PointF(b.Right, b.Y + h * 0.55f) });
        g.DrawLine(p, cx, b.Y + h * 0.25f, cx, b.Bottom - h * 0.25f);
        float s = w * 0.28f;
        g.FillPolygon(br, new[] { new PointF(cx, b.Y - h * 0.04f), new PointF(cx - s, b.Y + s * 0.95f), new PointF(cx + s, b.Y + s * 0.95f) });
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

    // --- more of the vocabulary (Kurt, 2026-10-04: almost every button an icon; tabs keep words) ------------------------------

    /// <summary>A page with a folded corner and a short label in it (a file of that type: .PNG, .DDS, .UPK …).</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> File(string label, bool arrowOut = false, bool arrowIn = false) => (g, b, p, br) =>
    {
        float w = b.Width, h = b.Height, f = w * 0.3f;
        var page = new[] { new PointF(b.X + w * 0.08f, b.Y - h * 0.06f), new PointF(b.Right - f, b.Y - h * 0.06f), new PointF(b.Right - w * 0.04f, b.Y - h * 0.06f + f),
                           new PointF(b.Right - w * 0.04f, b.Bottom + h * 0.06f), new PointF(b.X + w * 0.08f, b.Bottom + h * 0.06f) };
        g.DrawPolygon(p, page);
        g.DrawLines(p, new[] { new PointF(b.Right - f, b.Y - h * 0.06f), new PointF(b.Right - f, b.Y - h * 0.06f + f), new PointF(b.Right - w * 0.04f, b.Y - h * 0.06f + f) });
        using var font = new Font("Segoe UI", Math.Max(5f, h * (label.Length > 3 ? 0.27f : 0.33f)), FontStyle.Bold, GraphicsUnit.Pixel);
        var sz = g.MeasureString(label, font);
        g.DrawString(label, font, br, b.X + w * 0.48f - sz.Width / 2, b.Y + h * 0.62f - sz.Height / 2);
        float s = w * 0.2f, ax = b.X - w * 0.12f;
        if (arrowOut) { g.DrawLine(p, ax, b.Y + h * 0.15f, ax, b.Y + h * 0.5f); g.FillPolygon(br, new[] { new PointF(ax, b.Y - h * 0.08f), new PointF(ax - s, b.Y + s * 0.8f), new PointF(ax + s, b.Y + s * 0.8f) }); }
        if (arrowIn) { g.DrawLine(p, ax, b.Y, ax, b.Y + h * 0.42f); g.FillPolygon(br, new[] { new PointF(ax, b.Y + h * 0.75f), new PointF(ax - s, b.Y + h * 0.75f - s), new PointF(ax + s, b.Y + h * 0.75f - s) }); }
    };

    /// <summary>A small + badge at the lower right (adds something).</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> WithPlus(Action<Graphics, RectangleF, Pen, Brush> icon) => (g, b, p, br) =>
    {
        icon(g, new RectangleF(b.X - b.Width * 0.06f, b.Y - b.Height * 0.06f, b.Width * 0.86f, b.Height * 0.86f), p, br);
        float cx = b.Right + b.Width * 0.02f, cy = b.Bottom + b.Height * 0.02f, r = b.Width * 0.2f;
        g.DrawLine(p, cx - r, cy, cx + r, cy); g.DrawLine(p, cx, cy - r, cx, cy + r);
    };

    /// <summary>A check box, ticked or empty (Tick All / Tick None).</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> Box(bool ticked, bool all = false) => (g, b, p, br) =>
    {
        var r = RectangleF.Inflate(b, -b.Width * 0.06f, -b.Height * 0.06f);
        if (all) { g.DrawRectangle(p, r.X + r.Width * 0.22f, r.Y - r.Height * 0.1f, r.Width * 0.85f, r.Height * 0.85f); r = new RectangleF(r.X - r.Width * 0.08f, r.Y + r.Height * 0.2f, r.Width * 0.85f, r.Height * 0.85f); }
        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        if (ticked) g.DrawLines(p, new[] { new PointF(r.X + r.Width * 0.2f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.75f), new PointF(r.X + r.Width * 0.82f, r.Y + r.Height * 0.25f) });
    };

    /// <summary>Open a web page: a square with an arrow out of its corner.</summary>
    public static void External(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.45f, b.Y + h * 0.08f), new PointF(b.X, b.Y + h * 0.08f), new PointF(b.X, b.Bottom), new PointF(b.Right - w * 0.08f, b.Bottom), new PointF(b.Right - w * 0.08f, b.Y + h * 0.55f) });
        g.DrawLine(p, b.X + w * 0.42f, b.Y + h * 0.58f, b.Right, b.Y - h * 0.02f);
        g.DrawLines(p, new[] { new PointF(b.Right - w * 0.38f, b.Y - h * 0.02f), new PointF(b.Right, b.Y - h * 0.02f), new PointF(b.Right, b.Y + h * 0.36f) });
    }

    /// <summary>Copy: two overlapping pages.</summary>
    public static void Copy(Graphics g, RectangleF b, Pen p, Brush _)
    {
        float w = b.Width, h = b.Height;
        g.DrawRectangle(p, b.X + w * 0.3f, b.Y, w * 0.7f, h * 0.72f);
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.18f, b.Y + h * 0.28f), new PointF(b.X, b.Y + h * 0.28f), new PointF(b.X, b.Bottom), new PointF(b.X + w * 0.7f, b.Bottom), new PointF(b.X + w * 0.7f, b.Y + h * 0.84f) });
    }

    /// <summary>Back: an arrow left.</summary>
    public static void Back(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cy = b.Y + b.Height / 2, s = b.Width * 0.3f;
        g.DrawLine(p, b.X + s * 0.8f, cy, b.Right, cy);
        g.FillPolygon(br, new[] { new PointF(b.X, cy), new PointF(b.X + s, cy - s), new PointF(b.X + s, cy + s) });
    }

    /// <summary>Contents: a list (dots and lines).</summary>
    public static void List(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float r = b.Width * 0.07f;
        for (int k = 0; k < 3; k++)
        {
            float y = b.Y + b.Height * (0.15f + 0.35f * k);
            g.FillEllipse(br, b.X, y - r, 2 * r, 2 * r);
            g.DrawLine(p, b.X + b.Width * 0.3f, y, b.Right, y);
        }
    }

    /// <summary>Zoom: a magnifier with + or −; Fit: four corner arrows in; 1:1 as text.</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> Zoom(bool @in) => (g, b, p, br) =>
    {
        Search(g, b, p, br);
        float r = b.Width * 0.32f, cx = b.X + r + b.Width * 0.06f, cy = b.Y + r + b.Height * 0.06f, a = r * 0.55f;
        g.DrawLine(p, cx - a, cy, cx + a, cy);
        if (@in) g.DrawLine(p, cx, cy - a, cx, cy + a);
    };

    public static void Fit(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float l = b.Width * 0.3f;
        g.DrawRectangle(p, b.X + b.Width * 0.22f, b.Y + b.Height * 0.22f, b.Width * 0.56f, b.Height * 0.56f);
        foreach (var (x, y, dx, dy) in new[] { (b.Left, b.Top, 1, 1), (b.Right, b.Top, -1, 1), (b.Right, b.Bottom, -1, -1), (b.Left, b.Bottom, 1, -1) })
            g.DrawLines(p, new[] { new PointF(x + dx * l * 0.6f, y), new PointF(x, y), new PointF(x, y + dy * l * 0.6f) });
    }

    /// <summary>Text drawn as the icon (1:1, A+, A−).</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> Text(string text) => (g, b, p, br) =>
    {
        using var font = new Font("Segoe UI", b.Height * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        var sz = g.MeasureString(text, font);
        g.DrawString(text, font, br, b.X + (b.Width - sz.Width) / 2, b.Y + (b.Height - sz.Height) / 2);
    };

    /// <summary>Full screen: four corners out.</summary>
    public static void FullScreen(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float l = b.Width * 0.32f;
        foreach (var (x, y, dx, dy) in new[] { (b.Left, b.Top, 1, 1), (b.Right, b.Top, -1, 1), (b.Right, b.Bottom, -1, -1), (b.Left, b.Bottom, 1, -1) })
            g.DrawLines(p, new[] { new PointF(x + dx * l, y), new PointF(x, y), new PointF(x, y + dy * l) });
    }

    /// <summary>A person framed at a shot (Head, Bust, Full Body), as the previews draw them.</summary>
    public static Action<Graphics, RectangleF, Pen, Brush> Person(Framing.Shot shot) => (g, b, p, br) =>
        MhoExtendedModManager.Model.Gui.PreviewPanel.PersonIcon(g, Rectangle.Round(RectangleF.Inflate(b, b.Width * 0.3f, b.Height * 0.3f)), p.Color, shot);

    /// <summary>Eyedropper.</summary>
    public static void Dropper(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawLine(p, b.X + w * 0.05f, b.Bottom - h * 0.05f, b.X + w * 0.55f, b.Y + h * 0.45f);
        using var thick = (Pen)p.Clone(); thick.Width = p.Width * 2.2f;
        g.DrawLine(thick, b.X + w * 0.5f, b.Y + h * 0.5f, b.Right - w * 0.2f, b.Y + h * 0.2f);
        g.FillEllipse(br, b.Right - w * 0.32f, b.Y - h * 0.02f, w * 0.34f, h * 0.34f);
    }

    /// <summary>Camera (Take Snapshot).</summary>
    public static void Camera(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawRectangle(p, b.X, b.Y + h * 0.22f, w, h * 0.7f);
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.28f, b.Y + h * 0.22f), new PointF(b.X + w * 0.36f, b.Y + h * 0.05f), new PointF(b.X + w * 0.64f, b.Y + h * 0.05f), new PointF(b.X + w * 0.72f, b.Y + h * 0.22f) });
        float r = h * 0.22f, cx = b.X + w / 2, cy = b.Y + h * 0.57f;
        g.DrawEllipse(p, cx - r, cy - r, 2 * r, 2 * r);
    }

    /// <summary>History (Use Previous Setup): a clock with an arrow round it.</summary>
    public static void History(Graphics g, RectangleF b, Pen p, Brush br)
    {
        Reset(g, b, p, br);
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
        g.DrawLine(p, cx, cy, cx, cy - b.Height * 0.22f);
        g.DrawLine(p, cx, cy, cx + b.Width * 0.16f, cy + b.Height * 0.1f);
    }

    /// <summary>Two overlapping squares (Original Overlay, Compare).</summary>
    public static void Overlay(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawRectangle(p, b.X, b.Y, w * 0.64f, h * 0.64f);
        using var fill = new SolidBrush(Color.FromArgb(110, p.Color));
        g.FillRectangle(fill, b.X + w * 0.36f, b.Y + h * 0.36f, w * 0.64f, h * 0.64f);
        g.DrawRectangle(p, b.X + w * 0.36f, b.Y + h * 0.36f, w * 0.64f, h * 0.64f);
    }

    /// <summary>Compare: a square split in two halves.</summary>
    public static void Split(Graphics g, RectangleF b, Pen p, Brush br)
    {
        g.DrawRectangle(p, b.X, b.Y, b.Width, b.Height);
        g.FillRectangle(br, b.X, b.Y, b.Width / 2, b.Height);
    }

    /// <summary>A sparkle (spec), a half-mirrored circle (reflection), a sun (glow).</summary>
    public static void Sparkle(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, r = b.Width / 2, q = r * 0.22f;
        g.FillPolygon(br, new[] { new PointF(cx, cy - r), new PointF(cx + q, cy - q), new PointF(cx + r, cy), new PointF(cx + q, cy + q), new PointF(cx, cy + r), new PointF(cx - q, cy + q), new PointF(cx - r, cy), new PointF(cx - q, cy - q) });
    }

    public static void Mirror(Graphics g, RectangleF b, Pen p, Brush br)
    {
        g.DrawEllipse(p, b);
        using var path = new GraphicsPath(); path.AddPie(b.X, b.Y, b.Width, b.Height, 225, 180);
        g.FillPath(br, path);
    }

    public static void Sun(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2, r = b.Width * 0.22f;
        g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
        for (int k = 0; k < 8; k++)
        {
            double a = k * Math.PI / 4; float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            g.DrawLine(p, cx + c * r * 1.55f, cy + s * r * 1.55f, cx + c * b.Width * 0.5f, cy + s * b.Height * 0.5f);
        }
    }

    /// <summary>Power FXs: a lightning bolt.</summary>
    public static void Bolt(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.FillPolygon(br, new[] { new PointF(b.X + w * 0.6f, b.Y), new PointF(b.X + w * 0.12f, b.Y + h * 0.58f), new PointF(b.X + w * 0.46f, b.Y + h * 0.58f),
                                  new PointF(b.X + w * 0.36f, b.Bottom), new PointF(b.X + w * 0.88f, b.Y + h * 0.4f), new PointF(b.X + w * 0.54f, b.Y + h * 0.4f) });
    }

    /// <summary>Single Animation ▾ / Change Animation: a film strip.</summary>
    public static void Film(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawRectangle(p, b.X + w * 0.1f, b.Y, w * 0.8f, h);
        for (int k = 0; k < 4; k++) { float y = b.Y + h * (0.1f + 0.23f * k); g.FillRectangle(br, b.X + w * 0.16f, y, w * 0.12f, h * 0.1f); g.FillRectangle(br, b.Right - w * 0.28f, y, w * 0.12f, h * 0.1f); }
    }

    /// <summary>Bones: a bone.</summary>
    public static void Bone(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, r = w * 0.13f;
        using var thick = (Pen)p.Clone(); thick.Width = p.Width * 1.8f;
        g.DrawLine(thick, b.X + w * 0.22f, b.Bottom - h * 0.22f, b.Right - w * 0.22f, b.Y + h * 0.22f);
        foreach (var (x, y) in new[] { (b.X + w * 0.1f, b.Bottom - h * 0.3f), (b.X + w * 0.3f, b.Bottom - h * 0.1f), (b.Right - w * 0.3f, b.Y + h * 0.1f), (b.Right - w * 0.1f, b.Y + h * 0.3f) })
            g.FillEllipse(br, x - r, y - r, 2 * r, 2 * r);
    }

    /// <summary>Weights: a gradient bar (weight paint).</summary>
    public static void Weights(Graphics g, RectangleF b, Pen p, Brush br)
    {
        for (int k = 0; k < 5; k++)
        {
            using var f = new SolidBrush(Color.FromArgb(60 + 48 * k, p.Color));
            g.FillRectangle(f, b.X + b.Width * 0.2f * k, b.Y + b.Height * 0.15f, b.Width * 0.2f, b.Height * 0.7f);
        }
        g.DrawRectangle(p, b.X, b.Y + b.Height * 0.15f, b.Width, b.Height * 0.7f);
    }

    /// <summary>Smooth Weights: a wave.</summary>
    public static void Wave(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float cy = b.Y + b.Height / 2, a = b.Height * 0.3f;
        var pts = Enumerable.Range(0, 21).Select(i => { float t = i / 20f; return new PointF(b.X + b.Width * t, cy - a * MathF.Sin(t * MathF.PI * 2)); }).ToArray();
        g.DrawCurve(p, pts);
    }

    /// <summary>Voice: a speaker.</summary>
    public static void Speaker(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.FillPolygon(br, new[] { new PointF(b.X, b.Y + h * 0.35f), new PointF(b.X + w * 0.22f, b.Y + h * 0.35f), new PointF(b.X + w * 0.5f, b.Y + h * 0.08f),
                                  new PointF(b.X + w * 0.5f, b.Bottom - h * 0.08f), new PointF(b.X + w * 0.22f, b.Bottom - h * 0.35f), new PointF(b.X, b.Bottom - h * 0.35f) });
        g.DrawArc(p, b.X + w * 0.35f, b.Y + h * 0.28f, w * 0.35f, h * 0.44f, -50, 100);
        g.DrawArc(p, b.X + w * 0.3f, b.Y + h * 0.1f, w * 0.65f, h * 0.8f, -50, 100);
    }

    /// <summary>Stop: a filled square.</summary>
    public static void Stop(Graphics g, RectangleF b, Pen p, Brush br) => g.FillRectangle(br, RectangleF.Inflate(b, -b.Width * 0.12f, -b.Height * 0.12f));

    /// <summary>Link: two chain links.</summary>
    public static void Link(Graphics g, RectangleF b, Pen p, Brush br)
    {
        var st = g.Save();
        g.TranslateTransform(b.X + b.Width / 2, b.Y + b.Height / 2); g.RotateTransform(-45);
        float lw = b.Width * 0.62f, lh = b.Height * 0.36f;
        using (var a = Ui.Round(new RectangleF(-lw + lw * 0.25f, -lh / 2, lw, lh), lh / 2)) g.DrawPath(p, a);
        using (var c = Ui.Round(new RectangleF(-lw * 0.25f, -lh / 2, lw, lh), lh / 2)) g.DrawPath(p, c);
        g.Restore(st);
    }

    /// <summary>A picture (mountains and sun in a frame).</summary>
    public static void Picture(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height;
        g.DrawRectangle(p, b.X, b.Y + h * 0.08f, w, h * 0.84f);
        g.DrawLines(p, new[] { new PointF(b.X + w * 0.08f, b.Bottom - h * 0.16f), new PointF(b.X + w * 0.38f, b.Y + h * 0.48f), new PointF(b.X + w * 0.6f, b.Y + h * 0.7f), new PointF(b.X + w * 0.72f, b.Y + h * 0.58f), new PointF(b.Right - w * 0.08f, b.Bottom - h * 0.16f) });
        float r = w * 0.09f; g.FillEllipse(br, b.Right - w * 0.3f - r, b.Y + h * 0.3f - r, 2 * r, 2 * r);
    }

    /// <summary>A 3D cube (Create from 3D).</summary>
    public static void Cube(Graphics g, RectangleF b, Pen p, Brush br)
    {
        float w = b.Width, h = b.Height, cx = b.X + w / 2;
        var top = new PointF(cx, b.Y); var l = new PointF(b.X + w * 0.06f, b.Y + h * 0.25f); var r = new PointF(b.Right - w * 0.06f, b.Y + h * 0.25f);
        var mid = new PointF(cx, b.Y + h * 0.5f); var lb = new PointF(l.X, b.Bottom - h * 0.25f); var rb = new PointF(r.X, b.Bottom - h * 0.25f); var bot = new PointF(cx, b.Bottom);
        g.DrawPolygon(p, new[] { top, r, rb, bot, lb, l });
        g.DrawLines(p, new[] { l, mid, r }); g.DrawLine(p, mid, bot);
    }

    /// <summary>An eraser / clear (a square X).</summary>
    public static void Clear(Graphics g, RectangleF b, Pen p, Brush br)
    {
        var r = RectangleF.Inflate(b, -b.Width * 0.04f, -b.Height * 0.04f);
        g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        Cancel(g, RectangleF.Inflate(r, -r.Width * 0.18f, -r.Height * 0.18f), p, br);
    }

    /// <summary>Note (a page with a folded corner and lines).</summary>
    public static void Note(Graphics g, RectangleF b, Pen p, Brush br)
    {
        File("")(g, b, p, br);
        for (int k = 0; k < 3; k++) { float y = b.Y + b.Height * (0.35f + 0.2f * k); g.DrawLine(p, b.X + b.Width * 0.28f, y, b.Right - b.Width * 0.24f, y); }
    }

    // --- every button by its label: title (the tooltip's bold first line) and icon (null: the label stays, e.g. ▲ ▼) ----------

    /// <summary>Buttons whose words stay (set on them): a count (Apply Changes), the update alert, a setting's current value
    /// (Sort / Group), named presets, and the answer buttons of message and confirm windows.</summary>
    public const string KeepText = "keep-text";

    static readonly Dictionary<string, (string Title, Action<Graphics, RectangleF, Pen, Brush>? Paint)> ByLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cancel"] = ("Cancel", Cancel), ["Close"] = ("Close", Cancel), ["OK"] = ("OK", Save), ["Done"] = ("Done", Save),
        ["Choose"] = ("Choose", Folder), ["Clear"] = ("Clear", Clear), ["Clear All"] = ("Clear All", Clear), ["Reset"] = ("Reset", Reset),
        ["Tick All"] = ("Tick All", Box(true, all: true)), ["Tick None"] = ("Tick None", Box(false, all: true)),
        ["Use This Animation"] = ("Use This Animation", Save), ["Use Ticked"] = ("Use Ticked", Box(true)),
        ["Eyedropper"] = ("Eyedropper", Dropper), ["Refresh"] = ("Refresh", CheckUpdates),
        ["Open Releases Page"] = ("Open Releases Page", External), ["Release Page"] = ("Release Page", External),
        ["Open on Nexus"] = ("Open on Nexus", External), ["Open in Web Browser"] = ("Open in Web Browser", External),
        ["Save All as .JSON"] = ("Save All as .JSON", File("JSON", arrowOut: true)), ["Save Selected as .DDS"] = ("Save Selected as .DDS", File("DDS", arrowOut: true)),
        ["Save Selected as .PNG"] = ("Save Selected as .PNG", File("PNG", arrowOut: true)), ["Save as .PNG"] = ("Save as .PNG", File("PNG", arrowOut: true)),
        ["Export Original"] = ("Export Original", File("PNG", arrowOut: true)), ["Load .DDS/.PNG"] = ("Load .DDS / .PNG", File("DDS", arrowIn: true)),
        ["Add .UPK Files"] = ("Add .UPK Files", File("UPK", arrowIn: true)), ["Add .MHSFX Files"] = ("Add .MHSFX Files", File("SFX", arrowIn: true)),
        ["Open a .UPK"] = ("Open a .UPK", File("UPK", arrowIn: true)), ["Import Changes (.JSON)"] = ("Import Changes (.JSON)", File("JSON", arrowIn: true)),
        ["Keep as a Mod"] = ("Keep as a Mod", Build), ["Restore to Original"] = ("Restore to Original", Reset),
        ["A+"] = ("Larger Text", null), ["A−"] = ("Smaller Text", null), ["Back"] = ("Back", Back), ["Contents"] = ("Contents", List),
        ["▲"] = ("Previous / Up", null), ["▼"] = ("Next / Down", null), ["×"] = ("Remove", null), ["▶"] = ("Play / Pause", null), ["▾"] = ("More", null),
        ["Bust"] = ("Bust", Person(Framing.Shot.Bust)), ["Full Body"] = ("Full Body", Person(Framing.Shot.Full)),
        ["Head"] = ("Head", Person(Framing.Shot.HeadShoulders)), ["Head & Shoulders"] = ("Head & Shoulders", Person(Framing.Shot.HeadShoulders)),
        ["Glow"] = ("Glow", Sun), ["Reflect"] = ("Reflect", Mirror), ["Spec"] = ("Spec", Sparkle), ["Original Overlay"] = ("Original Overlay", Overlay),
        ["Reset View"] = ("Reset View", ResetView), ["Take Snapshot"] = ("Take Snapshot", Camera), ["Use Previous Setup"] = ("Use Previous Setup", History),
        ["Use as Replacement"] = ("Use as Replacement", Save), ["+"] = ("Zoom In", Zoom(true)), ["−"] = ("Zoom Out", Zoom(false)), ["1:1"] = ("Actual Size", Text("1:1")),
        ["Export"] = ("Export", Export), ["Fit"] = ("Fit", Fit), ["Full Screen"] = ("Full Screen", FullScreen), ["⛶"] = ("Full Screen", FullScreen),
        ["Edit the Selected Mod"] = ("Edit the Selected Mod", Pencil), ["Use the Mod's Note"] = ("Use the Mod's Note", Note),
        ["Back to the Original"] = ("Back to the Original", Reset), ["Change Animation"] = ("Change Animation", Film), ["Copy From a Character"] = ("Copy From a Character", Copy),
        ["Apply to All Powers"] = ("Apply to All Powers", Box(true, all: true)), ["Game's Colors"] = ("Game's Colors", Reset),
        ["Add Selected to the Mod ↓"] = ("Add Selected to the Mod", WithPlus(List)), ["Create from 3D"] = ("Create from 3D", Cube),
        ["Remove Replacement"] = ("Remove Replacement", Trash), ["Remove Selected Rows"] = ("Remove Selected Rows", Trash), ["Remove"] = ("Remove", Trash),
        ["Remove Shift"] = ("Remove Shift", Trash), ["Search"] = ("Search", Search), ["Shift This Voice"] = ("Shift This Voice", Wave),
        ["Show All"] = ("Show All", List), ["Stop"] = ("Stop", Stop), ["Turn All On"] = ("Turn All On", Box(true, all: true)),
        ["Use Another Voice ▾"] = ("Use Another Voice", WithMenu(Speaker)), ["Use the Hero's Voice"] = ("Use the Hero's Voice", Speaker),
        ["Delete Ticked"] = ("Delete Ticked", Trash), ["Open Folder"] = ("Open Folder", Folder), ["Create Mod"] = ("Create Mod", Save),
        ["Link Selected"] = ("Link Selected", Link), ["Add Mod Images"] = ("Add Mod Images", WithPlus(Picture)), ["Add Screenshots"] = ("Add Screenshots", WithPlus(Camera)),
        ["Copy"] = ("Copy", Copy), ["Export Images"] = ("Export Images", Export), ["Save to Mod"] = ("Save to Mod", Save), ["Start Over"] = ("Start Over", Reset),
        ["Make the Map"] = ("Make the Map", Save), ["Bones"] = ("Bones", Bone), ["Default Parts"] = ("Default Parts", Reset), ["Reset to Automatic"] = ("Reset to Automatic", Reset),
        ["Smooth Weights"] = ("Smooth Weights", Wave), ["Weights"] = ("Weights", Weights), ["Compare"] = ("Compare", Split), ["Power FXs"] = ("Power FXs", Bolt),
        ["Single Animation ▾"] = ("Single Animation", WithMenu(Film)), ["Single ▾"] = ("Single Animation", WithMenu(Film)), ["Look ▾"] = ("Look", Look), ["⟳ Loop"] = ("Loop", Loop),
    };

    /// <summary>
    /// Every button under <paramref name="root"/> by its label (Ui.Restyle runs it on each window and new view): an icon and a
    /// titled tooltip, or only the title for glyph buttons (▲ ▼ ▶ ×, A+ A−). Skipped: buttons already made (Make), tagged
    /// KeepText, and the answer buttons of message / confirm windows (DialogForm, UpdateForm, ApplyForm).
    /// </summary>
    public static void Apply(Control root)
    {
        foreach (var b in Ui.All<Button>(root).ToList())
        {
            if (!string.IsNullOrEmpty(b.AccessibleName) || b.AccessibleDescription == KeepText) continue;
            if (b.FindForm() is { } f && f.GetType().Name is "DialogForm" or "UpdateForm" or "ApplyForm") continue;
            string label = System.Text.RegularExpressions.Regex.Replace(b.Text.Trim(), @"\s+", " ");
            if (!ByLabel.TryGetValue(label, out var it)) continue;
            float sc = b.DeviceDpi / 96f;
            if (it.Paint != null) Make(b, it.Title, it.Paint, sc);
            else { b.AccessibleName = it.Title; Ui.TipTitled(b, it.Title, Ui.Tips.GetToolTip(b)); }
        }
    }
}
