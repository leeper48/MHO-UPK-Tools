using System.Numerics;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A move / turn gizmo over a part of the view (Kurt, 2026-10-27: "a real time gizmo for rotating and moving an object that is
/// XYZ color coded … rotate should be colored rings you drag and it rotates locked to that axis; the move drag could be colored
/// arrow carets"). Its owner says where it is each time the view paints (<see cref="GizmoSource"/>: the centre and the three
/// move and turn axes, engine space); a drag on an arrow reports the distance moved along that axis, a drag on a ring the angle
/// turned around it (right-handed, as Matrix4x4.CreateFromAxisAngle), both counted from where the drag started; letting go
/// reports the end. X red, Y green, Z blue; the arrow or ring under the mouse (or dragged) in yellow.
/// </summary>
sealed partial class ModelView
{
    /// <summary>Where the gizmo is: its centre, the three move axes and the three turn axes (engine space, unit length), and its
    /// size in engine units (the arrows' length; the rings are a little smaller).</summary>
    public sealed record TransformGizmo(Vector3 Center, Vector3[] MoveAxes, Vector3[] TurnAxes, float Size);

    /// <summary>The gizmo to draw now (asked at every paint, so it follows an animation), or null for none.</summary>
    public Func<TransformGizmo?>? GizmoSource { get; set; }
    /// <summary>A drag on arrow <c>axis</c>: moved this far along it (engine units) since the drag began.</summary>
    public event Action<int, float>? GizmoMoved;
    /// <summary>A drag on ring <c>axis</c>: turned this many degrees around it since the drag began.</summary>
    public event Action<int, float>? GizmoTurned;
    /// <summary>The drag ended (the owner keeps what it was dragged to).</summary>
    public event Action? GizmoReleased;

    static readonly Color[] GizmoColors = [Color.FromArgb(235, 70, 80), Color.FromArgb(120, 200, 50), Color.FromArgb(60, 135, 240)];
    static readonly Color GizmoHot = Color.FromArgb(255, 210, 60);

    /// <summary>The handle under the mouse or dragged: 0-2 an arrow, 3-5 a ring; -1 none.</summary>
    int gizmoHover = -1;
    /// <summary>A drag under way: the handle, its gizmo as the drag began, the camera and pane, the press point, the angle so far.</summary>
    (int Handle, TransformGizmo G, Cam Cam, Rectangle Area, Point Start, float Angle, float LastPhi)? gizmoDrag;

    TransformGizmo? CurrentGizmo() => gizmoDrag?.G ?? GizmoSource?.Invoke();

    const int RingSegments = 48;

    /// <summary>A ring's points on the canvas (null where behind the camera).</summary>
    PointF?[] RingPoints(TransformGizmo g, int axis, Cam cam, Rectangle area)
    {
        var a = g.TurnAxes[axis];
        var u = Vector3.Normalize(Vector3.Cross(a, MathF.Abs(a.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var v = Vector3.Cross(a, u);
        float r = g.Size * 0.8f;
        var pts = new PointF?[RingSegments + 1];
        for (int k = 0; k <= RingSegments; k++)
        {
            float t = k * MathF.Tau / RingSegments;
            pts[k] = OnCanvas(g.Center + (u * MathF.Cos(t) + v * MathF.Sin(t)) * r, cam, area);
        }
        return pts;
    }

    /// <summary>The handle under <paramref name="p"/> in the main view: an arrow (its shaft or head) first, then a ring.</summary>
    int GizmoHandleAt(Point p, out Cam cam, out Rectangle area)
    {
        cam = default; area = default;
        if (CurrentGizmo() is not { } g || cams.Count == 0) return -1;
        (cam, var pane) = cams[0];
        area = PaneArea(pane);
        if (!area.Contains(p) || OnCanvas(g.Center, cam, area) is not PointF o) return -1;
        float s = Ui.Dpi(DeviceDpi), best = 8 * s;
        int hit = -1;
        for (int k = 0; k < 3; k++)
            if (OnCanvas(g.Center + g.MoveAxes[k] * g.Size, cam, area) is PointF e && SegDist(p, o, e) is float d && d < best) { best = d; hit = k; }
        if (hit >= 0) return hit;
        best = 6 * s;
        for (int k = 0; k < 3; k++)
        {
            var pts = RingPoints(g, k, cam, area);
            for (int i = 0; i < RingSegments; i++)
                if (pts[i] is PointF a && pts[i + 1] is PointF b && SegDist(p, a, b) is float d && d < best) { best = d; hit = 3 + k; }
        }
        return hit;

        static float SegDist(Point p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            float t = len < 1e-6f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return MathF.Sqrt(x * x + y * y);
        }
    }

    /// <summary>The mouse pressed: a drag on a handle starts (true), else the view turns as usual.</summary>
    bool GizmoMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || GizmoSource?.Invoke() is not { } g) return false;
        int h = GizmoHandleAt(e.Location, out var cam, out var area);
        if (h < 0 || OnCanvas(g.Center, cam, area) is not PointF o) return false;
        gizmoDrag = (h, g, cam, area, e.Location, 0, MathF.Atan2(e.Y - o.Y, e.X - o.X));
        gizmoHover = h;
        fast = true;
        canvas.Invalidate();
        return true;
    }

    bool GizmoMouseMove(MouseEventArgs e)
    {
        if (gizmoDrag is not var (h, g, cam, area, start, angle, lastPhi))
        {
            // hover: the handle under the mouse lights up
            if (dragFrom != null) return false;   // (the view being turned)
            int now = GizmoSource == null ? -1 : GizmoHandleAt(e.Location, out _, out _);
            if (now != gizmoHover) { gizmoHover = now; canvas.Invalidate(); }
            if (now < 0) return false;
            canvas.Cursor = now < 3 ? Cursors.SizeAll : Cursors.Hand;
            return true;
        }
        if (OnCanvas(g.Center, cam, area) is not PointF o) return true;
        if (h < 3)
        {
            // along the arrow as it shows on screen: the mouse's travel along it, in engine units
            if (OnCanvas(g.Center + g.MoveAxes[h] * g.Size, cam, area) is not PointF tip) return true;
            float vx = tip.X - o.X, vy = tip.Y - o.Y, len2 = vx * vx + vy * vy;
            if (len2 < 4) return true;   // (the arrow points at the camera: nothing to drag along)
            float t = ((e.X - start.X) * vx + (e.Y - start.Y) * vy) / len2 * g.Size;
            GizmoMoved?.Invoke(h, t);
        }
        else
        {
            // around the ring's centre on screen, counted up across turns; the sign so that it follows the mouse
            int k = h - 3;
            float phi = MathF.Atan2(e.Y - o.Y, e.X - o.X), d = phi - lastPhi;
            if (d > MathF.PI) d -= MathF.Tau; else if (d < -MathF.PI) d += MathF.Tau;
            angle += d;
            var axis = g.TurnAxes[k];
            var perp = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX)) * g.Size * 0.8f;
            var turned = Vector3.Transform(perp, Matrix4x4.CreateFromAxisAngle(axis, 0.05f));
            float sign = 1;
            if (OnCanvas(g.Center + perp, cam, area) is PointF q0 && OnCanvas(g.Center + turned, cam, area) is PointF q1)
            {
                float a0 = MathF.Atan2(q0.Y - o.Y, q0.X - o.X), a1 = MathF.Atan2(q1.Y - o.Y, q1.X - o.X), da = a1 - a0;
                if (da > MathF.PI) da -= MathF.Tau; else if (da < -MathF.PI) da += MathF.Tau;
                sign = da >= 0 ? 1 : -1;
            }
            gizmoDrag = (h, g, cam, area, start, angle, phi);
            GizmoTurned?.Invoke(k, angle * sign * 180 / MathF.PI);
        }
        return true;
    }

    bool GizmoMouseUp()
    {
        if (gizmoDrag == null) return false;
        gizmoDrag = null; fast = false;
        GizmoReleased?.Invoke();
        Redraw();
        return true;
    }

    void PaintGizmo(Graphics g)
    {
        if (CurrentGizmo() is not { } gz || cams.Count == 0) return;
        var (cam, pane) = cams[0];
        var area = PaneArea(pane);
        if (OnCanvas(gz.Center, cam, area) is not PointF o) return;
        float s = Ui.Dpi(DeviceDpi);
        var mode = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.SetClip(area);
        int active = gizmoDrag?.Handle ?? gizmoHover;
        // the rings first (the arrows on top)
        for (int k = 0; k < 3; k++)
        {
            var pts = RingPoints(gz, k, cam, area);
            using var pen = new Pen(active == 3 + k ? GizmoHot : Color.FromArgb(220, GizmoColors[k]), (active == 3 + k ? 3f : 2f) * s);
            for (int i = 0; i < RingSegments; i++)
                if (pts[i] is PointF a && pts[i + 1] is PointF b) g.DrawLine(pen, a, b);
        }
        for (int k = 0; k < 3; k++)
        {
            if (OnCanvas(gz.Center + gz.MoveAxes[k] * gz.Size, cam, area) is not PointF e) continue;
            var col = active == k ? GizmoHot : GizmoColors[k];
            using var pen = new Pen(col, (active == k ? 3.5f : 2.5f) * s);
            float dx = e.X - o.X, dy = e.Y - o.Y, len = MathF.Sqrt(dx * dx + dy * dy);
            if (len < 2) continue;
            float ux = dx / len, uy = dy / len, head = 12 * s, wide = 5.5f * s;
            var baseP = new PointF(e.X - ux * head, e.Y - uy * head);
            g.DrawLine(pen, o, baseP);
            using var brush = new SolidBrush(col);
            g.FillPolygon(brush, [e, new PointF(baseP.X - uy * wide, baseP.Y + ux * wide), new PointF(baseP.X + uy * wide, baseP.Y - ux * wide)]);
        }
        using (var dot = new SolidBrush(Color.FromArgb(240, 240, 240))) g.FillEllipse(dot, o.X - 3.5f * s, o.Y - 3.5f * s, 7 * s, 7 * s);
        g.ResetClip();
        g.SmoothingMode = mode;
    }

    /// <summary>Test hook: the X or Y arrow, whichever shows longer on screen.</summary>
    internal int GizmoLongestArrow()
    {
        if (GizmoSource?.Invoke() is not { } g || cams.Count == 0) return 0;
        var (cam, pane) = cams[0];
        var area = PaneArea(pane);
        if (OnCanvas(g.Center, cam, area) is not PointF o) return 0;
        float Len(int k) => OnCanvas(g.Center + g.MoveAxes[k] * g.Size, cam, area) is PointF e ? MathF.Abs(e.X - o.X) + MathF.Abs(e.Y - o.Y) : 0;
        return Len(1) > Len(0) ? 1 : 0;
    }

    /// <summary>Test hook: a drag on handle <paramref name="handle"/> (0-2 arrows, 3-5 rings) from its middle / a point on it by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) pixels in two steps, released; false when the handle isn't on screen.</summary>
    internal bool TestGizmoDrag(int handle, int dx, int dy, bool release = true)
    {
        if (GizmoSource?.Invoke() is not { } g || cams.Count == 0) return false;
        var (cam, pane) = cams[0];
        var area = PaneArea(pane);
        PointF? at = handle < 3
            ? (OnCanvas(g.Center, cam, area) is PointF o && OnCanvas(g.Center + g.MoveAxes[handle] * g.Size, cam, area) is PointF e ? new PointF((o.X + 2 * e.X) / 3, (o.Y + 2 * e.Y) / 3) : null)
            : RingPoints(g, handle - 3, cam, area).FirstOrDefault(x => x != null);
        if (at is not PointF p0) return false;
        var p = Point.Round(p0);
        if (OnCanvas(g.Center, cam, area) is not PointF c) return false;
        gizmoDrag = (handle, g, cam, area, p, 0, MathF.Atan2(p.Y - c.Y, p.X - c.X));   // (the handle itself: the mouse's hit test is Kurt's to try)
        GizmoMouseMove(new MouseEventArgs(MouseButtons.Left, 0, p.X + dx / 2, p.Y + dy / 2, 0));
        GizmoMouseMove(new MouseEventArgs(MouseButtons.Left, 0, p.X + dx, p.Y + dy, 0));
        if (release) GizmoMouseUp();
        return true;
    }

    internal void TestGizmoRelease() => GizmoMouseUp();

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>Test: the same drag as window messages (the mouse's own path through the view's handlers): press on the handle,
    /// <paramref name="steps"/> moves, and (with <paramref name="release"/>) let go.</summary>
    internal bool TestGizmoMouse(int handle, int dx, int dy, int steps, bool release)
    {
        if (GizmoSource?.Invoke() is not { } g || cams.Count == 0) return false;
        var (cam, pane) = cams[0];
        var area = PaneArea(pane);
        PointF? at = handle < 3
            ? (OnCanvas(g.Center, cam, area) is PointF o && OnCanvas(g.Center + g.MoveAxes[handle] * g.Size, cam, area) is PointF e ? new PointF((o.X + 2 * e.X) / 3, (o.Y + 2 * e.Y) / 3) : null)
            : RingPoints(g, handle - 3, cam, area).FirstOrDefault(x => x != null);
        if (at is not PointF p0) return false;
        var p = Point.Round(p0);
        static IntPtr L(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
        const int MOVE = 0x200, DOWN = 0x201, UP = 0x202, MK_L = 1;
        SendMessage(canvas.Handle, MOVE, IntPtr.Zero, L(p.X, p.Y));
        SendMessage(canvas.Handle, DOWN, (IntPtr)MK_L, L(p.X, p.Y));
        bool grabbed = gizmoDrag?.Handle == handle;
        for (int k = 1; k <= steps; k++) SendMessage(canvas.Handle, MOVE, (IntPtr)MK_L, L(p.X + dx * k / steps, p.Y + dy * k / steps));
        if (release) SendMessage(canvas.Handle, UP, IntPtr.Zero, L(p.X + dx, p.Y + dy));
        return grabbed;
    }

    internal void TestGizmoMouseUp() { var c = canvas.PointToClient(Cursor.Position); SendMessage(canvas.Handle, 0x202, IntPtr.Zero, (IntPtr)((c.Y << 16) | (c.X & 0xFFFF))); }

    /// <summary>Test: the middle of the shown vertices <paramref name="first"/> .. +<paramref name="count"/> as drawn now, and the
    /// direction from the first vertex to the middle (shows a turn).</summary>
    internal (Vector3 Mid, Vector3 Dir)? ShownPartForTest(int first, int count)
    {
        if (first < 0 || count <= 0 || first + count > pos.Length) return null;
        var mid = Vector3.Zero;
        for (int i = first; i < first + count; i++) mid += pos[i];
        mid /= count;
        return (mid, pos[first] - mid);
    }
}
