using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Animations tab's Height strip (Kurt, 2026-11-02): height keys along a changed animation's timeline. Left to right is
/// the animation (0 to 100 %), up is the body raised (game units; the middle line is as the animation has it); straight
/// lines between keys. Click on empty space adds a key there, drag moves one, right-click removes one (Shift: fine steps).
/// <see cref="Committed"/> fires when the mouse lets go.
/// </summary>
sealed class HeightStrip : Control
{
    readonly List<(float At, float Z)> keys = [];
    int drag = -1, hover = -1;
    bool moved;
    float range = 30;

    /// <summary>The keys (sorted by At).</summary>
    public IReadOnlyList<(float At, float Z)> Keys => keys;
    /// <summary>Where the animation is (0..1), drawn as a line; null: none.</summary>
    public float? Playhead { get => playhead; set { if (playhead != value) { playhead = value; Invalidate(); } } }
    float? playhead;
    /// <summary>Shown instead of the curve when keys can't be set (why), else null.</summary>
    public string? Message { get => message; set { message = value; Invalidate(); } }
    string? message;
    /// <summary>The keys changed by the user (mouse released, or a key removed).</summary>
    public event Action? Committed;
    /// <summary>While dragging: the key's place and height, for a status line.</summary>
    public event Action<float, float>? Dragging;

    public HeightStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        Cursor = Cursors.Cross;
    }

    public void SetKeys(IEnumerable<(float At, float Z)> k)
    {
        keys.Clear(); keys.AddRange(k.OrderBy(x => x.At));
        drag = hover = -1;
        FitRange();
        Invalidate();
    }

    /// <summary>A key at <paramref name="at"/> on the curve as it is (Add Key at Frame).</summary>
    public void AddAt(float at)
    {
        at = Math.Clamp(at, 0, 1);
        if (keys.Any(k => Math.Abs(k.At - at) < 0.005f)) return;
        keys.Add((at, AnimSwap.HeightAt(keys, at)));
        keys.Sort((a, b) => a.At.CompareTo(b.At));
        Invalidate();
        Committed?.Invoke();
    }

    void FitRange()
    {
        float m = keys.Count == 0 ? 0 : keys.Max(k => Math.Abs(k.Z));
        range = Math.Max(30, MathF.Ceiling((m + 10) / 10) * 10);
    }

    Rectangle Plot => new(Pad, Pad, Math.Max(1, Width - 2 * Pad), Math.Max(1, Height - 2 * Pad));
    int Pad => (int)(10 * DeviceDpi / 96f);
    PointF Pt(float at, float z) { var r = Plot; return new(r.Left + at * r.Width, r.Top + r.Height / 2f - z / range * (r.Height / 2f)); }
    float AtOf(int x) { var r = Plot; return Math.Clamp((x - r.Left) / (float)r.Width, 0, 1); }
    float ZOf(int y) { var r = Plot; return (r.Top + r.Height / 2f - y) / (r.Height / 2f) * range; }

    int KeyAt(Point p)
    {
        float hit = 8 * DeviceDpi / 96f;
        for (int i = 0; i < keys.Count; i++)
        {
            var q = Pt(keys[i].At, keys[i].Z);
            if (Math.Abs(q.X - p.X) <= hit && Math.Abs(q.Y - p.Y) <= hit) return i;
        }
        return -1;
    }

    static float Snap(float z, bool fine) => fine ? MathF.Round(z * 10) / 10 : MathF.Round(z);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (message != null) return;
        Focus();
        int k = KeyAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (k < 0) return;
            keys.RemoveAt(k); hover = -1;
            Invalidate();
            Committed?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (k < 0)
        {
            keys.Add((AtOf(e.X), Snap(ZOf(e.Y), ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift))));
            keys.Sort((a, b) => a.At.CompareTo(b.At));
            k = KeyAt(e.Location);
            if (k < 0) k = keys.Count - 1;
            moved = true;   // (a new key is a change even without moving)
        }
        else moved = false;
        drag = k;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag >= 0)
        {
            // between its neighbours (the order stays)
            float lo = drag > 0 ? keys[drag - 1].At + 0.005f : 0, hi = drag < keys.Count - 1 ? keys[drag + 1].At - 0.005f : 1;
            var k = (Math.Clamp(AtOf(e.X), lo, Math.Max(lo, hi)), Snap(ZOf(e.Y), ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift)));
            if (k != keys[drag]) { keys[drag] = k; moved = true; Dragging?.Invoke(k.Item1, k.Item2); Invalidate(); }
            return;
        }
        int h = message == null ? KeyAt(e.Location) : -1;
        if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.SizeAll : Cursors.Cross; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (drag < 0) return;
        drag = -1;
        Capture = false;
        FitRange();
        Invalidate();
        if (moved) Committed?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = Plot;
        using var line = new Pen(Ui.Line);
        using var font = new Font(Font.FontFamily, 8f);
        using var subtle = new SolidBrush(Ui.Subtle);
        if (message != null)
        {
            TextRenderer.DrawText(g, message, Font, ClientRectangle, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        // the middle line (the animation's own height), quarters across, the range at the left
        g.DrawLine(line, r.Left, r.Top + r.Height / 2f, r.Right, r.Top + r.Height / 2f);
        using (var faint = new Pen(Color.FromArgb(44, 44, 50)) { DashStyle = DashStyle.Dot })
            for (int q = 1; q < 4; q++) g.DrawLine(faint, r.Left + r.Width * q / 4f, r.Top, r.Left + r.Width * q / 4f, r.Bottom);
        g.DrawString($"+{range:0}", font, subtle, r.Left + 2, r.Top);
        g.DrawString($"−{range:0}", font, subtle, r.Left + 2, r.Bottom - font.Height);
        if (keys.Count == 0)
            TextRenderer.DrawText(g, "Click to add a height key · drag to move · right-click to remove", Font, r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        // the curve: flat before the first key and after the last
        if (keys.Count > 0)
        {
            var pts = new List<PointF> { Pt(0, keys[0].Z) };
            pts.AddRange(keys.Select(k => Pt(k.At, k.Z)));
            pts.Add(Pt(1, keys[^1].Z));
            using var curve = new Pen(Ui.Accent, 2 * DeviceDpi / 96f);
            g.DrawLines(curve, [.. pts]);
        }
        if (playhead is float ph)
        {
            using var head = new Pen(Ui.TagCharacter, 1.5f);
            float x = r.Left + ph * r.Width;
            g.DrawLine(head, x, r.Top, x, r.Bottom);
        }
        float rad = 5 * DeviceDpi / 96f;
        for (int i = 0; i < keys.Count; i++)
        {
            var p = Pt(keys[i].At, keys[i].Z);
            bool hot = i == hover || i == drag;
            using var fill = new SolidBrush(hot ? Ui.AccentHover : Ui.Accent);
            g.FillEllipse(fill, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
            using var rim = new Pen(Ui.Text, 1.2f);
            g.DrawEllipse(rim, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
            if (hot) g.DrawString($"{keys[i].At * 100:0}% · {(keys[i].Z >= 0 ? "+" : "")}{keys[i].Z:0.#}", font, Brushes.White, Math.Min(p.X + rad + 2, r.Right - 70), Math.Max(r.Top, p.Y - rad - font.Height));
        }
    }
}
