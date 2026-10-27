using System.Numerics;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Weight painting in the view (2026-10-25, Kurt: Blender's weight painting in the Model tab). With PaintMode on, the left
/// button paints (PaintAt, once per dab: a tenth of the brush apart), the middle button turns the view and Shift + middle
/// moves it, the right button moves it as before. The brush is a circle at the pointer; F (radius) and Shift+F (strength)
/// change it by moving the mouse as Blender's do: a click keeps the new value, Esc or a right-click puts the old one back.
/// What the brush touches is up to the owner (SurfaceAt and VerticesNear give the point on the model and the vertices around it).
/// </summary>
sealed partial class ModelView
{
    bool paintMode;
    /// <summary>The left button paints instead of turning the view.</summary>
    public bool PaintMode { get => paintMode; set { paintMode = value; painting = false; adjusting = null; canvas.Cursor = value ? Cursors.Cross : Cursors.Default; canvas.Invalidate(); } }
    /// <summary>The brush's radius on screen (pixels) and strength (0–1).</summary>
    public float BrushRadius { get; set; } = 40;
    public float BrushStrength { get; set; } = 0.5f;
    /// <summary>The brush's colour (Blur: blue, Subtract: red …).</summary>
    public Color BrushColor { get; set; } = Color.White;
    /// <summary>A dab: the point on the canvas, the keys held (Shift, Ctrl), the first of a stroke.</summary>
    public event Action<Point, Keys, bool>? PaintAt;
    /// <summary>The stroke ended (the mouse released).</summary>
    public event Action? PaintEnded;
    /// <summary>F / Shift+F changed the radius or strength (and the click kept it, or Esc put it back).</summary>
    public event Action? BrushChanged;

    bool painting;
    MouseButtons paintButton = MouseButtons.Left;

    /// <summary>A Paint drag starts with this button and keys: the binding, with the held Blur / Other Way keys allowed on top.</summary>
    static bool PaintStarts(MouseButtons button, Keys mods)
    {
        Keys blur = Shortcuts.HeldKeys("paint.blur"), other = Shortcuts.HeldKeys("paint.otherway");
        foreach (var m in new[] { mods, mods & ~blur, mods & ~other, mods & ~blur & ~other })
            if (Shortcuts.Drag("paint.paint", button, m)) return true;
        return false;
    }
    Point? mouseAt;
    Point lastDab;
    /// <summary>F / Shift+F under way: which (true: strength), where the mouse started and the value then.</summary>
    (bool Strength, int StartX, float StartValue)? adjusting;

    bool PaintMouseDown(MouseEventArgs e)
    {
        if (!paintMode) return false;
        if (adjusting != null)
        {
            if (e.Button == MouseButtons.Right) CancelAdjust(); else { adjusting = null; BrushChanged?.Invoke(); }
            canvas.Invalidate();
            return true;
        }
        if (quad) return false;
        if (Shortcuts.Click("paint.pickbone", e.Button, ModifierKeys)) return true;   // (Ctrl+Shift+click picks a bone: no paint)
        if (!PaintStarts(e.Button, ModifierKeys)) return false;
        paintButton = e.Button;
        painting = true; lastDab = e.Location;
        PaintAt?.Invoke(e.Location, ModifierKeys, true);
        return true;
    }

    bool PaintMouseMove(MouseEventArgs e)
    {
        if (!paintMode) return false;
        mouseAt = e.Location;
        if (adjusting is var (strength, x0, v0))
        {
            float dx = e.X - x0;
            if (strength) BrushStrength = Math.Clamp(v0 + dx / 200f, 0.01f, 1f);
            else BrushRadius = Math.Clamp(v0 + dx, 3, 400);
            canvas.Invalidate();
            return true;
        }
        if (painting)
        {
            float step = MathF.Max(2, BrushRadius * 0.1f);
            if (MathF.Abs(e.X - lastDab.X) + MathF.Abs(e.Y - lastDab.Y) >= step) { lastDab = e.Location; PaintAt?.Invoke(e.Location, ModifierKeys, false); }
            canvas.Invalidate();
            return true;
        }
        canvas.Invalidate();
        return false;
    }

    bool PaintMouseUp(MouseEventArgs e)
    {
        if (!paintMode || !painting || e.Button != paintButton) return false;
        painting = false;
        PaintEnded?.Invoke();
        return true;
    }

    /// <summary>F (radius) or Shift+F (strength): move the mouse to change it, click to keep, Esc to put it back.</summary>
    public void BeginBrushAdjust(bool strength)
    {
        if (!paintMode) return;
        var at = mouseAt ?? canvas.PointToClient(Cursor.Position);
        // (the value follows the mouse from where it is now)
        adjusting = (strength, at.X, strength ? BrushStrength : BrushRadius);
        canvas.Invalidate();
    }

    /// <summary>Esc during F / Shift+F: the old value back. False when no change is under way.</summary>
    public bool CancelAdjust()
    {
        if (adjusting is not var (strength, _, v0)) return false;
        if (strength) BrushStrength = v0; else BrushRadius = v0;
        adjusting = null; canvas.Invalidate();
        return true;
    }

    void PaintBrush(Graphics g)
    {
        if (!paintMode || mouseAt is not Point at) return;
        float r = BrushRadius;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(220, BrushColor), 1.5f);
        g.DrawEllipse(pen, at.X - r, at.Y - r, 2 * r, 2 * r);
        // the strength as an inner circle (as Blender shows it while changing)
        float sr = r * MathF.Sqrt(BrushStrength);
        using var inner = new Pen(Color.FromArgb(adjusting is { Strength: true } ? 220 : 90, BrushColor), 1f);
        g.DrawEllipse(inner, at.X - sr, at.Y - sr, 2 * sr, 2 * sr);
        if (adjusting is var (strength, _, _))
        {
            string label = strength ? $"Strength {BrushStrength:0.00}" : $"Radius {BrushRadius:0}";
            using var f = new Font(Font.FontFamily, 9f);
            g.DrawString(label, f, Brushes.White, at.X + r + 6, at.Y - 8);
        }
    }

    /// <summary>
    /// The point on the model under <paramref name="at"/> in the last frame (display space, as the mesh shown: Y mirrored,
    /// before ModelScale), and how many such units one canvas pixel spans there; null over the background. Not in the quad view.
    /// </summary>
    public (Vector3 Point, float UnitsPerPixel)? SurfaceAt(Point at)
    {
        var c = canvas.ClientSize;
        int w = W, h = H; var tri = gTri;
        if (quad || w <= 0 || h <= 0 || c.Width <= 0 || c.Height <= 0 || tri.Length != w * h) return null;
        int x = at.X * w / c.Width, y = at.Y * h / c.Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return null;
        int p = y * w + x, t = tri[p];
        if (t < 0) return null;
        t &= 0x3FFFFFFF;
        if (3 * t + 2 >= idx.Length) return null;
        int a = idx[3 * t], b = idx[3 * t + 1], cc = idx[3 * t + 2];
        if (a >= pos.Length || b >= pos.Length || cc >= pos.Length) return null;
        float b0 = gB0[p], b1 = gB1[p];
        var hit = pos[a] * b0 + pos[b] * b1 + pos[cc] * (1 - b0 - b1);
        var cam = lastCam;
        float z = Vector3.Dot(hit * ModelScale - cam.Eye, cam.F);
        if (z <= 0 || cam.Focal <= 0) return null;
        float perRenderPixel = z / cam.Focal;
        return (hit, perRenderPixel * w / c.Width / Math.Max(1e-6f, ModelScale));
    }

    /// <summary>A colour per shown vertex (the first ones: the character) laid over the lit model at <see cref="TintOpacity"/>
    /// (the Model tab's weights at half strength, Kurt 2026-10-25); null: none.</summary>
    public Vector3[]? Tint { get; set; }
    public float TintOpacity { get; set; } = 0.5f;
    /// <summary>The shown vertex <see cref="Tint"/>'s first colour belongs to (0: the character; a held prop being painted:
    /// its first vertex).</summary>
    public int TintStart { get; set; }

    /// <summary>Each view drawn in the last frame (one, or the quad view's four): its part of the canvas and how an engine-space
    /// point lands on it (null behind its camera), with the Size scale (ModelScale) as the model is drawn. Overlays use these
    /// (the Model tab's bones, Kurt 2026-10-25: they ignored the quad view).</summary>
    public List<(Rectangle Area, Func<Vector3, PointF?> Project)> Panes() =>
        [.. cams.Select(c => { var area = PaneArea(c.Pane); var cam = c.Cam; return (area, (Func<Vector3, PointF?>)(v => OnCanvas(v, cam, area))); })];

    /// <summary>New texture coordinates for the first vertices shown (the character; its props follow), then a redraw.</summary>
    public void SetCharacterUv(Vector2[] u) => SetUv(0, u, redraw: true);

    /// <summary>New texture coordinates for the shown vertices from <paramref name="start"/> on (a held prop's).</summary>
    public void SetUv(int start, Vector2[] u, bool redraw)
    {
        if (start < 0 || start + u.Length > uv.Length) return;
        Array.Copy(u, 0, uv, start, u.Length);
        if (redraw) Redraw();
    }

    /// <summary>The shown vertices (below <paramref name="count"/>: the character, not its props) within <paramref name="radius"/>
    /// of a display-space point, with their distance.</summary>
    public List<(int Vertex, float Distance)> VerticesNear(Vector3 point, float radius, int count) => VerticesNear(point, radius, 0, count);

    /// <summary>The same for the <paramref name="count"/> shown vertices from <paramref name="start"/> on (a held prop's), by their
    /// index from there.</summary>
    public List<(int Vertex, float Distance)> VerticesNear(Vector3 point, float radius, int start, int count)
    {
        var list = new List<(int, float)>();
        float r2 = radius * radius;
        for (int i = Math.Max(0, start); i < Math.Min(start + count, pos.Length); i++)
        {
            float d2 = Vector3.DistanceSquared(pos[i], point);
            if (d2 <= r2) list.Add((i - start, MathF.Sqrt(d2)));
        }
        return list;
    }
}
