using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Animations tab's Height strip (Kurt, 2026-11-02): height keys along a changed animation's timeline. Left to right is
/// the animation (0 to 100 %), up is the body raised (game units; the middle line is as the animation has it); straight
/// lines between keys. Click on empty space adds a key there, drag moves one, right-click removes one (Shift: fine steps).
/// <see cref="Committed"/> fires when the mouse lets go.
/// A power's chain (its start, loop and end) shows as segments side by side, each as wide as it plays (Kurt, 2026-11-02: one
/// place to key a travel power against the floor); each has its own keys, and the one last touched is <see cref="Active"/>.
/// The Feet line is the lowest point of the body above the game's floor with the keys applied (SetFeet): the middle line is
/// the floor, so a key that brings the Feet line to it puts the feet on the floor there.
/// </summary>
sealed class HeightStrip : Control
{
    /// <summary>A part of the strip: its caption, how long it plays (its share of the width), and why it can't take keys (null: it can).</summary>
    public sealed record Segment(string Label, float Seconds, string? Message = null);

    List<Segment> segs = [new("", 1)];
    List<List<(float At, float Z)>> segKeys = [[]];
    List<float[]?> feet = [null];
    int active;
    (int Seg, int Key) drag = (-1, -1), hover = (-1, -1);
    bool moved;
    float range = 30;

    /// <summary>The active segment's keys (sorted by At).</summary>
    public IReadOnlyList<(float At, float Z)> Keys => segKeys[active];
    /// <summary>The segment the keys, Add Key at Frame and the gizmo work on (the last one clicked, else the one the editor picked).</summary>
    public int Active => active;
    public int SegmentCount => segs.Count;
    /// <summary>Where the animation is: a segment and a share of it (0..1), drawn as a line; null: none.</summary>
    public (int Seg, float At)? Playhead { get => playhead; set { if (playhead != value) { playhead = value; Invalidate(); } } }
    (int Seg, float At)? playhead;
    /// <summary>Shown instead of the curve when keys can't be set (why), else null.</summary>
    public string? Message { get => message; set { message = value; Invalidate(); } }
    string? message;
    /// <summary>The keys changed by the user (mouse released, or a key removed), on <see cref="Active"/>.</summary>
    public event Action? Committed;
    /// <summary>What the last commit was, for Undo / History: Add Key, Move Key, Remove Key, Add Key at Frame.</summary>
    public string LastAction { get; private set; } = "Height Keys";
    bool added;
    /// <summary>While dragging: the key's place and height, for a status line.</summary>
    public event Action<float, float>? Dragging;

    public HeightStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        Cursor = Cursors.Cross;
    }

    /// <summary>The parts shown, each with its keys; <paramref name="activeSeg"/> takes the keys' edits.</summary>
    public void SetSegments(IReadOnlyList<Segment> s, IReadOnlyList<IEnumerable<(float At, float Z)>> keys, int activeSeg)
    {
        segs = s.Count > 0 ? [.. s] : [new("", 1)];
        segKeys = [.. segs.Select((_, i) => i < keys.Count ? keys[i].OrderBy(x => x.At).ToList() : new List<(float, float)>())];
        feet = [.. segs.Select(_ => (float[]?)null)];
        feetBase.Clear();
        active =Math.Clamp(activeSeg, 0, segs.Count - 1);
        drag = hover = (-1, -1);
        playhead = null;
        FitRange();
        Invalidate();
    }

    /// <summary>Another segment made the active one.</summary>
    public void SetActive(int s) { s = Math.Clamp(s, 0, segs.Count - 1); if (s != active) { active = s; Invalidate(); } }

    /// <summary>The active segment's keys replaced.</summary>
    public void SetKeys(IEnumerable<(float At, float Z)> k)
    {
        segKeys[active].Clear(); segKeys[active].AddRange(k.OrderBy(x => x.At));
        drag = hover = (-1, -1);
        FitRange();
        Invalidate();
    }

    /// <summary>A segment's Feet line: the body's lowest point above the floor, sampled evenly over it, as the animation is
    /// now (the keys it has applied); null: none.</summary>
    public void SetFeet(int seg, float[]? samples)
    {
        if (seg < 0 || seg >= feet.Count) return;
        feet[seg] = samples;
        FitRange();
        Invalidate();
    }

    /// <summary>The keys the Feet line of <paramref name="seg"/> was measured with (they're in the animation then): edits since
    /// move the line by the difference.</summary>
    public void SetFeetBase(int seg, IEnumerable<(float At, float Z)> keys)
    {
        while (feetBase.Count < segs.Count) feetBase.Add([]);
        if (seg >= 0 && seg < feetBase.Count) feetBase[seg] = [.. keys.OrderBy(x => x.At)];
    }
    readonly List<List<(float At, float Z)>> feetBase = [];

    /// <summary>A key at <paramref name="at"/> on the active segment's curve as it is (Add Key at Frame).</summary>
    public void AddAt(float at)
    {
        var keys = segKeys[active];
        at = Math.Clamp(at, 0, 1);
        if (keys.Any(k => Math.Abs(k.At - at) < 0.005f)) return;
        keys.Add((at, AnimSwap.HeightAt(keys, at)));
        keys.Sort((a, b) => a.At.CompareTo(b.At));
        Invalidate();
        LastAction = "Add Key at Frame";
        Committed?.Invoke();
    }

    /// <summary>The Feet line of a segment at <paramref name="at"/> with its current keys (tests, the editor's note).</summary>
    public float? FeetAt(int seg, float at)
    {
        if (seg < 0 || seg >= feet.Count || feet[seg] is not { Length: > 1 } f) return null;
        float x = Math.Clamp(at, 0, 1) * (f.Length - 1);
        int i = Math.Min((int)x, f.Length - 2);
        float v = f[i] + (f[i + 1] - f[i]) * (x - i);
        var b = seg < feetBase.Count ? feetBase[seg] : [];
        return v + AnimSwap.HeightAt(segKeys[seg], at) - AnimSwap.HeightAt(b, at);
    }

    void FitRange()
    {
        float m = 0;
        for (int s = 0; s < segs.Count; s++)
        {
            foreach (var k in segKeys[s]) m = Math.Max(m, Math.Abs(k.Z));
            if (feet[s] is { Length: > 1 } f)
                for (int i = 0; i < f.Length; i++) if (FeetAt(s, i / (float)(f.Length - 1)) is float v) m = Math.Max(m, Math.Abs(v));
        }
        range = Math.Clamp(MathF.Ceiling((m + 10) / 10) * 10, 30, 400);
    }

    int Pad => (int)(10 * DeviceDpi / 96f);
    int Gap => segs.Count > 1 ? (int)(8 * DeviceDpi / 96f) : 0;
    int Head => segs.Count > 1 ? (int)(14 * DeviceDpi / 96f) : 0;
    Rectangle Plot => new(Pad, Pad + Head, Math.Max(1, Width - 2 * Pad), Math.Max(1, Height - 2 * Pad - Head));

    /// <summary>Each segment's part of the plot, as wide as its share of the playing time.</summary>
    RectangleF SegRect(int s)
    {
        var r = Plot;
        float total = segs.Sum(x => Math.Max(0.05f, x.Seconds)), w = r.Width - Gap * (segs.Count - 1), x = r.Left;
        for (int i = 0; i < s; i++) x += Math.Max(0.05f, segs[i].Seconds) / total * w + Gap;
        return new RectangleF(x, r.Top, Math.Max(0.05f, segs[s].Seconds) / total * w, r.Height);
    }

    PointF Pt(int s, float at, float z) { var r = SegRect(s); return new(r.Left + at * r.Width, r.Top + r.Height / 2f - z / range * (r.Height / 2f)); }
    float ZOf(int y) { var r = Plot; return (r.Top + r.Height / 2f - y) / (r.Height / 2f) * range; }

    /// <summary>The segment under <paramref name="x"/> (the nearest across a gap) and the share of it there.</summary>
    (int Seg, float At) SegAt(int x)
    {
        for (int s = 0; s < segs.Count; s++)
        {
            var r = SegRect(s);
            if (x <= r.Right + Gap / 2f || s == segs.Count - 1) return (s, Math.Clamp((x - r.Left) / r.Width, 0, 1));
        }
        return (0, 0);
    }

    (int Seg, int Key) KeyAt(Point p)
    {
        float hit = 8 * DeviceDpi / 96f;
        for (int s = 0; s < segs.Count; s++)
            for (int i = 0; i < segKeys[s].Count; i++)
            {
                var q = Pt(s, segKeys[s][i].At, segKeys[s][i].Z);
                if (Math.Abs(q.X - p.X) <= hit && Math.Abs(q.Y - p.Y) <= hit) return (s, i);
            }
        return (-1, -1);
    }

    static float Snap(float z, bool fine) => fine ? MathF.Round(z * 10) / 10 : MathF.Round(z);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (message != null) return;
        Focus();
        var k = KeyAt(e.Location);
        if (e.Button == MouseButtons.Right)
        {
            if (k.Seg < 0) return;
            active = k.Seg;
            segKeys[k.Seg].RemoveAt(k.Key); hover = (-1, -1);
            Invalidate();
            LastAction = "Remove Key";
            Committed?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (k.Seg < 0)
        {
            var (s, at) = SegAt(e.X);
            if (segs[s].Message != null) return;
            var keys = segKeys[s];
            var nk = (at, Snap(ZOf(e.Y), ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift)));
            keys.Add(nk);
            keys.Sort((a, b) => a.At.CompareTo(b.At));
            k = (s, keys.IndexOf(nk));
            moved = added = true;   // (a new key is a change even without moving)
        }
        else moved = added = false;
        active = k.Seg;
        drag = k;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag.Seg >= 0)
        {
            var keys = segKeys[drag.Seg];
            int d = drag.Key;
            // between its neighbours, in its own segment (the order stays)
            float lo = d > 0 ? keys[d - 1].At + 0.005f : 0, hi = d < keys.Count - 1 ? keys[d + 1].At - 0.005f : 1;
            var r = SegRect(drag.Seg);
            var k = (Math.Clamp((e.X - r.Left) / r.Width, lo, Math.Max(lo, hi)), Snap(ZOf(e.Y), ModifierKeys.HasFlag(System.Windows.Forms.Keys.Shift)));
            if (k != keys[d]) { keys[d] = k; moved = true; Dragging?.Invoke(k.Item1, k.Item2); Invalidate(); }
            return;
        }
        (int Seg, int Key) h = message == null ? KeyAt(e.Location) : (-1, -1);
        if (h != hover) { hover = h; Cursor = h.Seg >= 0 ? Cursors.SizeAll : Cursors.Cross; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (drag.Seg < 0) return;
        drag = (-1, -1);
        Capture = false;
        FitRange();
        Invalidate();
        if (moved) { LastAction = added ? "Add Key" : "Move Key"; Committed?.Invoke(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover.Seg >= 0) { hover = (-1, -1); Invalidate(); } }

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
        bool anyFeet = feet.Any(f => f is { Length: > 1 });
        for (int s = 0; s < segs.Count; s++)
        {
            var sr = SegRect(s);
            var keys = segKeys[s];
            if (segs.Count > 1)
            {
                // the part's caption over it; the active one bright
                using var cap = new SolidBrush(s == active ? Ui.Text : Ui.Subtle);
                g.DrawString($"{segs[s].Label} · {segs[s].Seconds:0.00} s", font, cap, sr.Left, r.Top - Head);
                if (s == active) using (var under = new Pen(Ui.Accent, 2)) g.DrawLine(under, sr.Left, r.Top - 1, sr.Right, r.Top - 1);
            }
            // the middle line (the animation's own height, and the floor for the Feet line), quarters across
            g.DrawLine(line, sr.Left, sr.Top + sr.Height / 2f, sr.Right, sr.Top + sr.Height / 2f);
            using (var faint = new Pen(Color.FromArgb(44, 44, 50)) { DashStyle = DashStyle.Dot })
                for (int q = 1; q < 4; q++) g.DrawLine(faint, sr.Left + sr.Width * q / 4f, sr.Top, sr.Left + sr.Width * q / 4f, sr.Bottom);
            if (segs[s].Message != null)
            {
                TextRenderer.DrawText(g, segs[s].Message, font, Rectangle.Round(sr), Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                continue;
            }
            // the Feet line: the lowest point above the floor with the keys as they are now
            if (feet[s] is { Length: > 1 } f)
            {
                var pts = new PointF[f.Length];
                for (int i = 0; i < f.Length; i++) { float at = i / (float)(f.Length - 1); pts[i] = Pt(s, at, FeetAt(s, at) ?? 0); }
                using var fp = new Pen(Ui.TagTeam, 1.5f * DeviceDpi / 96f);
                g.DrawLines(fp, pts);
            }
            // the keys' curve: flat before the first key and after the last
            if (keys.Count > 0)
            {
                var pts = new List<PointF> { Pt(s, 0, keys[0].Z) };
                pts.AddRange(keys.Select(k => Pt(s, k.At, k.Z)));
                pts.Add(Pt(s, 1, keys[^1].Z));
                using var curve = new Pen(Ui.Accent, 2 * DeviceDpi / 96f);
                g.DrawLines(curve, [.. pts]);
            }
        }
        g.DrawString($"+{range:0}", font, subtle, r.Left + 2, r.Top);
        g.DrawString($"−{range:0}", font, subtle, r.Left + 2, r.Bottom - font.Height);
        if (anyFeet)
        {
            using var legend = new SolidBrush(Ui.TagTeam);
            string t = "Feet Above the Floor";
            var size = g.MeasureString(t, font);
            g.DrawString(t, font, legend, r.Right - size.Width - 2, r.Top);
        }
        if (segKeys.All(k => k.Count == 0))
            TextRenderer.DrawText(g, "Click to add a height key · drag to move · right-click to remove", Font, r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        if (playhead is (int ps, float ph) && ps >= 0 && ps < segs.Count)
        {
            using var head = new Pen(Ui.TagCharacter, 1.5f);
            var sr = SegRect(ps);
            float x = sr.Left + ph * sr.Width;
            g.DrawLine(head, x, sr.Top, x, sr.Bottom);
        }
        float rad = 5 * DeviceDpi / 96f;
        for (int s = 0; s < segs.Count; s++)
            for (int i = 0; i < segKeys[s].Count; i++)
            {
                var k = segKeys[s][i];
                var p = Pt(s, k.At, k.Z);
                bool hot = (s, i) == hover || (s, i) == drag;
                using var fill = new SolidBrush(hot ? Ui.AccentHover : s == active ? Ui.Accent : Ui.Subtle);
                g.FillEllipse(fill, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
                using var rim = new Pen(Ui.Text, 1.2f);
                g.DrawEllipse(rim, p.X - rad, p.Y - rad, 2 * rad, 2 * rad);
                if (hot)
                {
                    string feetText = FeetAt(s, k.At) is float fa ? $" · feet {(fa >= 0 ? "+" : "")}{fa:0}" : "";
                    g.DrawString($"{k.At * 100:0}% · {(k.Z >= 0 ? "+" : "")}{k.Z:0.#}{feetText}", font, Brushes.White, Math.Min(p.X + rad + 2, r.Right - 120), Math.Max(r.Top, p.Y - rad - font.Height));
                }
            }
    }
}
