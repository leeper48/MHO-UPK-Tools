using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MhoPackageModifier.Gui;

/// <summary>
/// A 3D view of a StaticMesh, drawn by a small software renderer (no graphics library needed): depth buffer,
/// perspective-correct texturing (nearest texel, wrapping UVs, alpha below 64 cut out), flat head-light shading.
/// Left-drag rotates round the mesh, right- or middle-drag pans, the wheel zooms, double-click frames the mesh again.
/// Textured / Solid, Wireframe, Backfaces (the game draws one side only: back sides are shown red, so a face turned the
/// wrong way stands out) and a ground grid. While dragging it draws at half resolution, so it stays responsive.
/// UE is left-handed (X forward, Y right, Z up): positions are mirrored in Y for display so the mesh isn't shown mirrored.
/// </summary>
sealed class MeshViewer : UserControl
{
    /// <summary>A section's diffuse texture (BGRA).</summary>
    public sealed record Tex(byte[] Bgra, int W, int H);

    Vector3[] pos = [];
    Vector2[] uv = [];
    int[] idx = [];
    int[] triSection = [];
    Tex?[] sectionTex = [];
    float windingSign = 1f;                                                // +1: outward normal = cross(b-a, c-a) (set from the stored normals)
    Vector3 center; float radius = 1, minZ;
    string name = "", info = "", message = "Select a Mesh in the List to See It Here.";

    float yaw = -0.8f, pitch = 0.35f, distance = 10;
    Vector3 target;
    Point? dragFrom; MouseButtons dragButton;
    bool fast;                                                             // half resolution while dragging

    readonly Panel canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill };
    readonly CheckBox textured = new() { Text = "Textured", AutoSize = true, Checked = true, Padding = new Padding(4, 4, 0, 0) };
    readonly CheckBox wire = new() { Text = "Wireframe", AutoSize = true, Padding = new Padding(4, 4, 0, 0) };
    readonly CheckBox back = new() { Text = "Backfaces", AutoSize = true, Padding = new Padding(4, 4, 0, 0) };
    readonly CheckBox grid = new() { Text = "Grid", AutoSize = true, Checked = true, Padding = new Padding(4, 4, 0, 0) };
    readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 70, Padding = new Padding(6, 4, 6, 4), Tag = "hint" };
    Bitmap? frame;
    readonly FlowLayoutPanel bar = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2), WrapContents = true };

    /// <summary>Just the 3D view: no option bar or status line, no grid (MHO Extended Mod Manager's preview pane).</summary>
    public bool Compact
    {
        get => !bar.Visible;
        set { bar.Visible = !value; status.Visible = !value; grid.Checked = !value; Redraw(); }
    }

    /// <summary>The colour behind the mesh (null: the theme's background).</summary>
    public Color? Background { get; set; }

    sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

    public MeshViewer()
    {
        var frameBtn = new Button { Text = "Frame", AutoSize = true };
        frameBtn.Click += (_, _) => { FrameMesh(); Redraw(); };
        foreach (var c in new[] { textured, wire, back, grid }) c.CheckedChanged += (_, _) => Redraw();
        bar.Controls.AddRange([textured, wire, back, grid, frameBtn,
            new Label { Text = "left-drag: rotate   right-drag: pan   wheel: zoom   double-click: frame", AutoSize = true, Padding = new Padding(10, 8, 0, 0), Tag = "hint" }]);
        canvas.Paint += (_, e) => Paint2(e.Graphics);
        canvas.Resize += (_, _) => Redraw();
        canvas.MouseDown += (_, e) => { dragFrom = e.Location; dragButton = e.Button; fast = true; canvas.Focus(); };
        canvas.MouseUp += (_, _) => { bool moved = dragFrom != null; dragFrom = null; fast = false; Redraw(); if (moved) ViewChanged?.Invoke(); };
        canvas.MouseMove += (_, e) => Drag(e);
        canvas.MouseWheel += (_, e) => { distance *= MathF.Pow(0.85f, e.Delta / 120f); distance = Math.Clamp(distance, radius * 0.05f, radius * 50); Redraw(); ViewChanged?.Invoke(); };
        canvas.DoubleClick += (_, _) => { FrameMesh(); Redraw(); ViewChanged?.Invoke(); };
        Controls.Add(canvas);
        Controls.Add(status);
        Controls.Add(bar);
    }

    public bool HasMesh => idx.Length > 0;

    /// <summary>For --gui-snapshot: the view options and angle.</summary>
    public void SetView(bool? tex = null, bool? wireframe = null, bool? backfaces = null, float? yawDeg = null, float? pitchDeg = null)
    {
        if (tex is bool t) textured.Checked = t;
        if (wireframe is bool w) wire.Checked = w;
        if (backfaces is bool b) back.Checked = b;
        if (yawDeg is float y) yaw = y * MathF.PI / 180;
        if (pitchDeg is float p) pitch = p * MathF.PI / 180;
        Redraw();
    }

    /// <summary>A mesh to show: engine-space positions, UV0, triangle indices, per-triangle section, per-section texture.</summary>
    public void ShowMesh(string name, Vector3[] positions, Vector3[] normals, Vector2[] uv0, int[] indices, int[] triangleSection, Tex?[] textures, string info)
    {
        this.name = name; this.info = info;
        pos = positions.Select(p => new Vector3(p.X, -p.Y, p.Z)).ToArray();          // left-handed engine space -> right-handed view
        var nrm = normals.Select(n => new Vector3(n.X, -n.Y, n.Z)).ToArray();
        uv = uv0; idx = indices; triSection = triangleSection; sectionTex = textures;
        // Which winding faces outward: the side the stored vertex normals agree with (most triangles).
        int agree = 0, disagree = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var n = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
            float d = Vector3.Dot(n, nrm[idx[t]] + nrm[idx[t + 1]] + nrm[idx[t + 2]]);
            if (d > 0) agree++; else if (d < 0) disagree++;
        }
        windingSign = agree >= disagree ? 1f : -1f;
        Vector3 lo = pos.Length > 0 ? pos.Aggregate(Vector3.Min) : Vector3.Zero, hi = pos.Length > 0 ? pos.Aggregate(Vector3.Max) : Vector3.Zero;
        center = (lo + hi) / 2; radius = Math.Max(1f, (hi - lo).Length() / 2); minZ = lo.Z;
        status.Text = $"{name}   " + Look.TitleCase($"{positions.Length:N0} vertices, {indices.Length / 3:N0} triangles, {textures.Length} section(s), size {hi.X - lo.X:0} x {hi.Y - lo.Y:0} x {hi.Z - lo.Z:0}   {info}");
        FrameMesh();
        Redraw();
    }

    /// <summary>The user turned, panned or zoomed the view (after a drag, a wheel step or a double-click).</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// The camera, relative to the mesh (so it fits the mesh again after reloading): yaw, pitch, distance / radius, and the
    /// target's offset from the mesh's centre / radius (x, y, z). Setting it redraws.
    /// </summary>
    public float[] ViewState
    {
        get => [yaw, pitch, distance / radius, (target.X - center.X) / radius, (target.Y - center.Y) / radius, (target.Z - center.Z) / radius];
        set
        {
            if (value is not { Length: 6 }) return;
            yaw = value[0]; pitch = Math.Clamp(value[1], -1.5f, 1.5f); distance = Math.Clamp(value[2], 0.05f, 50f) * radius;
            target = center + new Vector3(value[3], value[4], value[5]) * radius;
            Redraw();
        }
    }

    /// <summary>Back to the default framing (as a double-click does).</summary>
    public void ResetView() { FrameMesh(); Redraw(); }

    /// <summary>Moves the camera back by a factor (e.g. to leave room for an animation), from the framed distance.</summary>
    public void ZoomOut(float factor) { FrameMesh(); distance *= factor; Redraw(); }

    /// <summary>New vertex positions for the mesh shown (an animation frame): same triangles, textures and camera.</summary>
    public void UpdateGeometry(Vector3[] positions)
    {
        if (positions.Length != pos.Length) return;
        for (int i = 0; i < positions.Length; i++) pos[i] = new Vector3(positions[i].X, -positions[i].Y, positions[i].Z);
        Redraw();
    }

    public void ShowMessage(string text)
    {
        message = Look.TitleCase(text); idx = []; pos = []; status.Text = "";
        frame?.Dispose(); frame = null;
        canvas.Invalidate();
    }

    void FrameMesh() { target = center; distance = radius * 2.6f; yaw = -0.8f; pitch = 0.35f; }

    void Drag(MouseEventArgs e)
    {
        if (dragFrom is not Point from) return;
        float dx = e.X - from.X, dy = e.Y - from.Y;
        dragFrom = e.Location;
        if (dragButton == MouseButtons.Left)
        {
            yaw -= dx * 0.01f;
            pitch = Math.Clamp(pitch + dy * 0.01f, -1.5f, 1.5f);
        }
        else
        {
            var (r, u, _, _) = Basis();
            float s = distance * 2 * MathF.Tan(Fov / 2) / Math.Max(1, canvas.ClientSize.Height);
            target += -r * dx * s + u * dy * s;
        }
        Redraw();
    }

    const float Fov = 0.8f;                                               // vertical field of view (radians)

    (Vector3 Right, Vector3 Up, Vector3 Forward, Vector3 Eye) Basis()
    {
        var eye = target + distance * new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        return (r, Vector3.Cross(r, f), f, eye);
    }

    void Redraw() { Render(); canvas.Invalidate(); }

    void Paint2(Graphics g)
    {
        if (frame == null)
        {
            TextRenderer.DrawText(g, message, Font, canvas.ClientRectangle, Theme.Current.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.DrawImage(frame, canvas.ClientRectangle);
    }

    // ---------------------------------------------------------------- the renderer

    int W, H;
    int[] color = [];
    float[] depth = [];                                                    // 1/z: larger is nearer, 0 = empty

    void Render()
    {
        if (idx.Length == 0 || canvas.ClientSize.Width < 8 || canvas.ClientSize.Height < 8) return;
        int scale = fast ? 2 : 1;
        W = canvas.ClientSize.Width / scale; H = canvas.ClientSize.Height / scale;
        if (color.Length != W * H) { color = new int[W * H]; depth = new float[W * H]; }
        int bg = (Background ?? Theme.Current.Back).ToArgb();
        Array.Fill(color, bg);
        Array.Clear(depth);

        var (r, u, f, eye) = Basis();
        float focal = H / 2f / MathF.Tan(Fov / 2), cx = W / 2f, cy = H / 2f, near = distance * 0.01f;
        int n = pos.Length;
        var sx = new float[n]; var sy = new float[n]; var iz = new float[n];
        for (int i = 0; i < n; i++)
        {
            var d = pos[i] - eye;
            float z = Vector3.Dot(d, f);
            if (z < near) { iz[i] = -1; continue; }
            iz[i] = 1f / z;
            sx[i] = cx + Vector3.Dot(d, r) * focal * iz[i];
            sy[i] = cy - Vector3.Dot(d, u) * focal * iz[i];
        }
        if (grid.Checked) DrawGrid(r, u, f, eye, focal, cx, cy, near);

        bool useTex = textured.Checked, showBack = back.Checked;
        var light = Vector3.Normalize(-f + u * 0.3f);
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (iz[a] < 0 || iz[b] < 0 || iz[c] < 0) continue;              // crosses the near plane
            var nOut = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]) * windingSign;
            float len = nOut.Length();
            if (len < 1e-12f) continue;
            nOut /= len;
            bool front = Vector3.Dot(nOut, eye - pos[a]) > 0;
            if (!front && !showBack) continue;
            float lit = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(nOut, light));
            int s = triSection[t / 3];
            Tex? tex = useTex && s < sectionTex.Length ? sectionTex[s] : null;
            int baseColor = front ? (useTex && tex == null ? SectionColor(s) : unchecked((int)0xFFB4B4B4)) : unchecked((int)0xFFC03030);
            Raster(a, b, c, sx, sy, iz, front ? tex : null, lit, baseColor);
        }
        if (wire.Checked)
        {
            int wc = unchecked((int)0xFF101010) | (Theme.Current.IsDark ? 0x00E0E0E0 : 0x00303030);
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if (iz[a] < 0 || iz[b] < 0 || iz[c] < 0) continue;
                var nOut = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]) * windingSign;
                if (!showBack && Vector3.Dot(nOut, eye - pos[a]) <= 0) continue;
                Line(sx[a], sy[a], iz[a], sx[b], sy[b], iz[b], wc, 1.02f);
                Line(sx[b], sy[b], iz[b], sx[c], sy[c], iz[c], wc, 1.02f);
                Line(sx[c], sy[c], iz[c], sx[a], sy[a], iz[a], wc, 1.02f);
            }
        }

        if (frame == null || frame.Width != W || frame.Height != H) { frame?.Dispose(); frame = new Bitmap(W, H, PixelFormat.Format32bppArgb); }
        var bd = frame.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < H; y++) Marshal.Copy(color, y * W, bd.Scan0 + y * bd.Stride, W);
        frame.UnlockBits(bd);
    }

    static readonly int[] Palette = [unchecked((int)0xFFB4B4B4), unchecked((int)0xFF8FB0D8), unchecked((int)0xFFD8B08F), unchecked((int)0xFF9FD08F), unchecked((int)0xFFD08FC0), unchecked((int)0xFFD8D08F)];
    static int SectionColor(int s) => Palette[s % Palette.Length];

    void Raster(int a, int b, int c, float[] sx, float[] sy, float[] iz, Tex? tex, float lit, int baseColor)
    {
        float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))), maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))), maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;
        float inv = 1f / area;
        float z0 = iz[a], z1 = iz[b], z2 = iz[c];
        Vector2 t0 = uv.Length > a ? uv[a] * z0 : default, t1 = uv.Length > b ? uv[b] * z1 : default, t2 = uv.Length > c ? uv[c] * z2 : default;
        int br = (baseColor >> 16) & 255, bgc = (baseColor >> 8) & 255, bb = baseColor & 255;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * inv;
                float w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * inv;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * z0 + w1 * z1 + w2 * z2;
                int p = y * W + x;
                if (z <= depth[p]) continue;
                int cr = br, cg = bgc, cb = bb;
                if (tex != null)
                {
                    float uu = (w0 * t0.X + w1 * t1.X + w2 * t2.X) / z, vv = (w0 * t0.Y + w1 * t1.Y + w2 * t2.Y) / z;
                    int tx = (int)((uu - MathF.Floor(uu)) * tex.W), ty = (int)((vv - MathF.Floor(vv)) * tex.H);
                    int ti = (Math.Min(ty, tex.H - 1) * tex.W + Math.Min(tx, tex.W - 1)) * 4;
                    if (tex.Bgra[ti + 3] < 64) continue;                        // cut-out
                    cb = tex.Bgra[ti]; cg = tex.Bgra[ti + 1]; cr = tex.Bgra[ti + 2];
                }
                depth[p] = z;
                color[p] = unchecked((int)0xFF000000) | ((int)(cr * lit) << 16) | ((int)(cg * lit) << 8) | (int)(cb * lit);
            }
        }
    }

    /// <summary>A depth-tested line (grid, wireframe); bias > 1 lets it win against the surface it lies on.</summary>
    void Line(float xa, float ya, float za, float xb, float yb, float zb, int col, float bias)
    {
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(xb - xa), MathF.Abs(yb - ya)));
        if (steps > 20000) return;
        for (int i = 0; i <= steps; i++)
        {
            float k = steps == 0 ? 0 : (float)i / steps;
            int x = (int)(xa + (xb - xa) * k), y = (int)(ya + (yb - ya) * k);
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) continue;
            float z = (za + (zb - za) * k) * bias;
            int p = y * W + x;
            if (z < depth[p]) continue;
            color[p] = col;
        }
    }

    void DrawGrid(Vector3 r, Vector3 u, Vector3 f, Vector3 eye, float focal, float cx, float cy, float near)
    {
        float extent = radius * 1.6f;
        float step = MathF.Pow(10, MathF.Floor(MathF.Log10(extent / 4)));
        if (extent / step > 20) step *= 5; else if (extent / step > 10) step *= 2;
        int lines = (int)(extent / step);
        int col = Theme.Current.IsDark ? unchecked((int)0xFF3A3A3A) : unchecked((int)0xFFC8C8C8);
        (float X, float Y, float Z)? P(Vector3 w)
        {
            var d = w - eye; float z = Vector3.Dot(d, f);
            if (z < near) return null;
            float izv = 1 / z;
            return (cx + Vector3.Dot(d, r) * focal * izv, cy - Vector3.Dot(d, u) * focal * izv, izv);
        }
        void Seg(Vector3 a, Vector3 b)
        {
            // Split so a line partly behind the camera still draws its visible part.
            const int parts = 8;
            for (int k = 0; k < parts; k++)
            {
                var pa = P(Vector3.Lerp(a, b, k / (float)parts)); var pb = P(Vector3.Lerp(a, b, (k + 1) / (float)parts));
                if (pa is { } q && pb is { } s) Line(q.X, q.Y, q.Z, s.X, s.Y, s.Z, col, 1f);
            }
        }
        float gz = minZ;
        var o = new Vector3(MathF.Round(center.X / step) * step, MathF.Round(center.Y / step) * step, gz);
        for (int i = -lines; i <= lines; i++)
        {
            Seg(o + new Vector3(i * step, -lines * step, 0), o + new Vector3(i * step, lines * step, 0));
            Seg(o + new Vector3(-lines * step, i * step, 0), o + new Vector3(lines * step, i * step, 0));
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}
