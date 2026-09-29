using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The preview's 3D view (Kurt: closer to the game's look). A software renderer like MHO Package Modifier's MeshViewer,
/// which it started from, but shading each pixel from the section's character material (ModMaterials): smooth normals,
/// the normal map (tangent space, tangents from the UVs and skinned with the mesh), mipmapped bilinear textures, a key
/// light from above, half-Lambert and fill light where the material switches them on, specular from its packed map,
/// the rim light with its mask and colour, and emissive glow. The game's shaders are compiled and can't run here, so
/// this rebuilds their lighting from the same inputs; the constants (light strength, rim falloff) are chosen by eye.
///
/// Two passes: the triangles are rasterised into a buffer of (triangle, barycentrics) with a depth test (and the cut-out
/// test of masked materials), then only the visible pixels are shaded, rows in parallel. Half resolution while dragging.
/// Left-drag rotates round the mesh, right- or middle-drag pans, the wheel zooms (Shift: finer), double-click frames the mesh.
/// UE is left-handed (X forward, Y right, Z up): everything is mirrored in Y for display.
/// </summary>
sealed class ModelView : UserControl
{
    /// <summary>A texture with its mip chain (BGRA bytes per level), sampled bilinearly with wrapping.</summary>
    public sealed class Map
    {
        readonly byte[][] levels; readonly int[] ws, hs;
        public int W => ws[0];
        public int H => hs[0];

        public Map(byte[] bgra, int w, int h)
        {
            var l = new List<byte[]> { bgra }; var lw = new List<int> { w }; var lh = new List<int> { h };
            while (w > 1 || h > 1)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var src = l[^1]; var dst = new byte[nw * nh * 4];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                        for (int c = 0; c < 4; c++)
                        {
                            int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1), y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
                            dst[(y * nw + x) * 4 + c] = (byte)((src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c] + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c] + 2) / 4);
                        }
                l.Add(dst); lw.Add(nw); lh.Add(nh); w = nw; h = nh;
            }
            levels = [.. l]; ws = [.. lw]; hs = [.. lh];
        }

        /// <summary>RGBA 0..1 at (u, v); lod = log2 of texels per pixel for this texture's full size.</summary>
        public Vector4 Sample(float u, float v, float lod)
        {
            int m = Math.Clamp((int)MathF.Round(lod), 0, levels.Length - 1);
            int w = ws[m], h = hs[m]; var d = levels[m];
            float fx = (u - MathF.Floor(u)) * w - 0.5f, fy = (v - MathF.Floor(v)) * h - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float ax = fx - x0, ay = fy - y0;
            int xa = ((x0 % w) + w) % w, xb = (xa + 1) % w, ya = ((y0 % h) + h) % h, yb = (ya + 1) % h;
            int i00 = (ya * w + xa) * 4, i10 = (ya * w + xb) * 4, i01 = (yb * w + xa) * 4, i11 = (yb * w + xb) * 4;
            float w00 = (1 - ax) * (1 - ay), w10 = ax * (1 - ay), w01 = (1 - ax) * ay, w11 = ax * ay;
            float B(int o) => (d[i00 + o] * w00 + d[i10 + o] * w10 + d[i01 + o] * w01 + d[i11 + o] * w11) * (1f / 255f);
            return new Vector4(B(2), B(1), B(0), B(3));
        }

        /// <summary>Alpha at the nearest texel (the cut-out test).</summary>
        public byte AlphaAt(float u, float v, float lod)
        {
            int m = Math.Clamp((int)MathF.Round(lod), 0, levels.Length - 1);
            int w = ws[m], h = hs[m];
            int x = Math.Min(w - 1, (int)((u - MathF.Floor(u)) * w)), y = Math.Min(h - 1, (int)((v - MathF.Floor(v)) * h));
            return levels[m][(y * w + x) * 4 + 3];
        }
    }

    /// <summary>One channel (0 R, 1 G, 2 B, 3 A) of a packed map.</summary>
    public readonly record struct Channel(Map? Map, int Ch)
    {
        public float At(Vector2 uv, float ratio, float fallback) =>
            Map is { } m ? Ch switch { 0 => m.Sample(uv.X, uv.Y, Lod(ratio, m)).X, 1 => m.Sample(uv.X, uv.Y, Lod(ratio, m)).Y, 2 => m.Sample(uv.X, uv.Y, Lod(ratio, m)).Z, _ => m.Sample(uv.X, uv.Y, Lod(ratio, m)).W } : fallback;
    }

    /// <summary>How a section is shaded (from its material; see ModMeshes.LookFor).</summary>
    public sealed class Look
    {
        public Map? Diffuse, Normal, SpecColor;
        /// <summary>Where each value comes from: a channel of a packed map (Map null: not in any map).</summary>
        public Channel Spec, SpecPow, RimMaskAt, Emissive;
        public bool UseNormal, UseSpec, UseRim, RimMask, DiffuseInRim, HalfLambert, Fill, UseEmissive, Cutout, TwoSided;
        public float NormalStrength = 1, SpecStrength = 1, SpecPower = 16, EmissiveMult = 1, Ambient = 1;
        public Vector3 Rim = new(0.5f, 0.55f, 0.65f), FillColor = new(0.8f, 0.6f, 0.5f);
        /// <summary>A plain look: just a colour texture (a material that couldn't be read), cut out at alpha 64 as before.</summary>
        public static Look Plain(Map? diffuse) => new() { Diffuse = diffuse, Cutout = true, UseRim = true, Rim = new(0.35f, 0.38f, 0.45f) };
    }

    Vector3[] pos = [], nrm = [];
    Vector4[] tan = [];
    Vector2[] uv = [];
    int[] idx = [];
    int[] triSection = [];
    Look?[] looks = [];
    float windingSign = 1f;
    Vector3 center; float radius = 1;
    string message = "";

    float yaw = -0.8f, pitch = 0.35f, distance = 10;
    Vector3 target;
    Point? dragFrom; MouseButtons dragButton;
    bool fast;

    readonly Panel canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill };
    Bitmap? frame;

    /// <summary>How bright the lights are (0.5–2; the glow of emissive parts isn't changed). Redraws.</summary>
    public float Brightness
    {
        get => brightness;
        set { value = Math.Clamp(value, 0.5f, 2f); if (Math.Abs(brightness - value) < 1e-4) return; brightness = value; Redraw(); }
    }
    float brightness = 1;

    /// <summary>A picture behind the mesh, stretched over the view (the icon creator's portrait backdrop), or null.</summary>
    public Image? Backdrop { get => backdrop; set { backdrop = value; Redraw(); } }
    Image? backdrop;

    /// <summary>A picture laid over the view at <see cref="OverlayOpacity"/> (the icon creator: the game's original, to line up
    /// pose and size); never part of a snapshot.</summary>
    public Image? Overlay { get => overlay; set { overlay = value; canvas.Invalidate(); } }
    Image? overlay;
    public float OverlayOpacity { get => overlayOpacity; set { overlayOpacity = Math.Clamp(value, 0, 1); canvas.Invalidate(); } }
    float overlayOpacity = 0.35f;

    /// <summary>The colour behind the mesh.</summary>
    public Color Background { get; set; } = Color.FromArgb(30, 31, 36);

    sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

    public ModelView()
    {
        canvas.Paint += (_, e) => PaintFrame(e.Graphics);
        canvas.Resize += (_, _) => Redraw();
        canvas.MouseDown += (_, e) => { dragFrom = e.Location; dragButton = e.Button; fast = true; canvas.Focus(); };
        canvas.MouseUp += (_, _) => { bool moved = dragFrom != null; dragFrom = null; fast = false; Redraw(); if (moved) ViewChanged?.Invoke(); };
        canvas.MouseMove += (_, e) => Drag(e);
        // Wheel zoom: 8 % a notch, 2 % with Shift held (Kurt: one notch went too far for framing an icon).
        canvas.MouseWheel += (_, e) =>
        {
            float step = (ModifierKeys & Keys.Shift) != 0 ? 0.98f : 0.92f;
            distance *= MathF.Pow(step, e.Delta / 120f); distance = Math.Clamp(distance, radius * 0.05f, radius * 50); Redraw(); ViewChanged?.Invoke();
        };
        canvas.DoubleClick += (_, _) => { FrameMesh(); Redraw(); ViewChanged?.Invoke(); };
        Controls.Add(canvas);
    }

    /// <summary>The user turned, panned or zoomed the view (after a drag, a wheel step or a double-click).</summary>
    public event Action? ViewChanged;

    /// <summary>Shows a loaded mesh (bind pose) with its section looks.</summary>
    /// <summary>
    /// Shows a mesh; <paramref name="framedVertices"/> limits the framing bounds to the first vertices (a character with
    /// props: the camera stays relative to the character, so ticking a prop doesn't move a saved view).
    /// </summary>
    public void ShowMesh(ModMeshes.Loaded l, int framedVertices)
    {
        framedCount = framedVertices;
        ShowMesh(l);
        framedCount = -1;
    }
    int framedCount = -1;

    public void ShowMesh(ModMeshes.Loaded l)
    {
        uv = l.Uv; idx = l.Indices; triSection = l.TriangleSection; looks = l.Looks;
        pos = new Vector3[l.Positions.Length]; nrm = new Vector3[l.Normals.Length]; tan = new Vector4[l.Tangents.Length];
        SetGeometry(l.Positions, l.Normals, l.Tangents);
        // Which winding faces outward: the side the stored vertex normals agree with (most triangles).
        int agree = 0, disagree = 0;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            var n = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]);
            float d = Vector3.Dot(n, nrm[idx[t]] + nrm[idx[t + 1]] + nrm[idx[t + 2]]);
            if (d > 0) agree++; else if (d < 0) disagree++;
        }
        windingSign = agree >= disagree ? 1f : -1f;
        var framed = framedCount > 0 && framedCount < pos.Length ? pos.Take(framedCount) : pos;
        Vector3 lo = pos.Length > 0 ? framed.Aggregate(Vector3.Min) : Vector3.Zero, hi = pos.Length > 0 ? framed.Aggregate(Vector3.Max) : Vector3.Zero;
        center = (lo + hi) / 2; radius = Math.Max(1f, (hi - lo).Length() / 2);
        message = "";
        FrameMesh();
        Redraw();
    }

    /// <summary>An animation frame: new positions, normals and tangents for the mesh shown (same triangles and camera).</summary>
    /// <summary>New positions, normals and tangents (engine space) for the mesh shown, e.g. a character with props.</summary>
    public void UpdateGeometry(Vector3[] p, Vector3[] n, Vector4[] t)
    {
        if (p.Length != pos.Length) return;
        SetGeometry(p, n, t);
        Redraw();
    }

    public void UpdateGeometry(MeshAnimator a)
    {
        if (a.Positions.Length != pos.Length) return;
        SetGeometry(a.Positions, a.Normals, a.Tangents);
        Redraw();
    }

    // Engine space → display space (mirrored in Y). A mirror flips handedness, so the bitangent sign flips too.
    void SetGeometry(Vector3[] p, Vector3[] n, Vector4[] t)
    {
        for (int i = 0; i < pos.Length; i++) pos[i] = new Vector3(p[i].X, -p[i].Y, p[i].Z);
        for (int i = 0; i < nrm.Length && i < n.Length; i++) nrm[i] = new Vector3(n[i].X, -n[i].Y, n[i].Z);
        for (int i = 0; i < tan.Length && i < t.Length; i++) tan[i] = new Vector4(t[i].X, -t[i].Y, t[i].Z, -t[i].W);
    }

    /// <summary>The camera, relative to the mesh: yaw, pitch, distance / radius, target offset / radius (x, y, z) (as MeshViewer's).</summary>
    public float[] ViewState
    {
        get => [yaw, pitch, distance / radius / LensScale, (target.X - center.X) / radius, (target.Y - center.Y) / radius, (target.Z - center.Z) / radius];
        set
        {
            if (value is not { Length: 6 }) return;
            yaw = value[0]; pitch = Math.Clamp(value[1], -1.5f, 1.5f); distance = Math.Clamp(value[2], 0.05f, 50f) * radius * LensScale;
            target = center + new Vector3(value[3], value[4], value[5]) * radius;
            Redraw();
        }
    }

    public void ResetView() { FrameMesh(); Redraw(); }

    /// <summary>Points the camera at a spot (engine space) from a distance, turned by yaw / pitch (radians).</summary>
    public void Aim(Vector3 engineTarget, float dist, float yawRad, float pitchRad)
    {
        target = new Vector3(engineTarget.X, -engineTarget.Y, engineTarget.Z);
        distance = Math.Clamp(dist, radius * 0.05f, radius * 50); yaw = yawRad; pitch = Math.Clamp(pitchRad, -1.5f, 1.5f);
        Redraw();
    }
    public void ZoomOut(float factor) { FrameMesh(); distance *= factor; Redraw(); }

    public void ShowMessage(string text)
    {
        message = Ui.TitleCase(text); idx = []; pos = [];
        frame?.Dispose(); frame = null;
        canvas.Invalidate();
    }

    void FrameMesh() { target = center; distance = radius * 2.6f * LensScale; yaw = -0.8f; pitch = 0.35f; }

    void Drag(MouseEventArgs e)
    {
        if (dragFrom is not Point from) return;
        float dx = e.X - from.X, dy = e.Y - from.Y;
        dragFrom = e.Location;
        if (dragButton == MouseButtons.Left) { yaw -= dx * 0.01f; pitch = Math.Clamp(pitch + dy * 0.01f, -1.5f, 1.5f); }
        else
        {
            var (r, u, _, _) = Basis();
            float s = distance * 2 * MathF.Tan(Fov / 2) / Math.Max(1, canvas.ClientSize.Height);
            target += -r * dx * s + u * dy * s;
        }
        Redraw();
    }

    /// <summary>The vertical field of view (radians). Default: a 50 mm lens (Kurt, 2026-09-29; was 0.8 rad, about 28.5 mm).</summary>
    float Fov = DefaultFov;
    public static readonly float DefaultFov = 2 * MathF.Atan(12f / 50f);
    /// <summary>
    /// Framing is kept as it would be with the old 0.8 rad (28.5 mm) lens: saved views, the automatic framing and the icon
    /// presets store a distance for that lens, and the camera stands back by this factor for the current one (a dolly zoom),
    /// so a different lens changes the perspective, not the size in the frame.
    /// </summary>
    public const float ReferenceFov = 0.8f;
    public static float ReferenceFocalLength => 12f / MathF.Tan(ReferenceFov / 2);
    float LensScale => MathF.Tan(ReferenceFov / 2) / MathF.Tan(Fov / 2);

    /// <summary>
    /// The lens as a 35 mm camera's focal length (a 24 mm tall frame: fov = 2·atan(12 / mm)). Setting it keeps what's
    /// framed the same size (the camera moves back or in, a dolly zoom), so only the perspective changes (Kurt: icons).
    /// </summary>
    public float FocalLength
    {
        get => 12f / MathF.Tan(Fov / 2);
        set
        {
            float fov = 2 * MathF.Atan(12f / Math.Clamp(value, 8f, 400f));
            if (Math.Abs(fov - Fov) < 1e-5f) return;
            distance *= MathF.Tan(Fov / 2) / MathF.Tan(fov / 2);
            Fov = fov;
            Redraw();
        }
    }
    public static float DefaultFocalLength => 12f / MathF.Tan(DefaultFov / 2);

    (Vector3 Right, Vector3 Up, Vector3 Forward, Vector3 Eye) Basis()
    {
        var eye = target + distance * new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        return (r, Vector3.Cross(r, f), f, eye);
    }

    void Redraw() { Render(); canvas.Invalidate(); }

    void PaintFrame(Graphics g)
    {
        if (frame == null)
        {
            using (var b = new SolidBrush(Background)) g.FillRectangle(b, canvas.ClientRectangle);
            TextRenderer.DrawText(g, message, Font, canvas.ClientRectangle, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        if (backdrop != null) { using (var b = new SolidBrush(Background)) g.FillRectangle(b, canvas.ClientRectangle); g.DrawImage(backdrop, canvas.ClientRectangle); }
        g.DrawImage(frame, canvas.ClientRectangle);
        if (overlay != null && overlayOpacity > 0.01f)
        {
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = overlayOpacity });
            var r = canvas.ClientRectangle;
            g.DrawImage(overlay, r, 0, 0, overlay.Width, overlay.Height, GraphicsUnit.Pixel, ia);
        }
    }

    // ---------------------------------------------------------------- the renderer

    int W, H;
    int[] color = [];
    float[] depth = [];                      // 1/z: larger is nearer, 0 = empty
    int[] gTri = [];                         // triangle index per pixel (-1: none; bit 30: seen from behind)
    float[] gB0 = [], gB1 = [];              // perspective-correct barycentrics of vertices a and b
    float[] sx = [], sy = [], iz = [];
    float[] triRatio = [];                   // UV area / screen area per triangle (for the mip level)

    /// <summary>For tests: how long the last frame took to draw (ms).</summary>
    public double LastFrameMs { get; private set; }

    void Render() => Render(0, 0);

    /// <summary>Draws the view; with a size, at that size for a snapshot (the frame shown isn't touched).</summary>
    void Render(int width, int height)
    {
        bool snap = width > 0;
        if (idx.Length == 0 || (!snap && (canvas.ClientSize.Width < 8 || canvas.ClientSize.Height < 8))) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int scale = fast && !snap ? 2 : 1;
        W = snap ? width : canvas.ClientSize.Width / scale; H = snap ? height : canvas.ClientSize.Height / scale;
        if (color.Length != W * H) { color = new int[W * H]; depth = new float[W * H]; gTri = new int[W * H]; gB0 = new float[W * H]; gB1 = new float[W * H]; }
        int bg = backdrop != null && !snap ? 0 : Background.ToArgb();   // with a backdrop, empty pixels stay see-through
        Array.Fill(color, bg);
        Array.Clear(depth);
        Array.Fill(gTri, -1);

        var (r, u, f, eye) = Basis();
        float focal = H / 2f / MathF.Tan(Fov / 2), cx = W / 2f, cy = H / 2f, near = distance * 0.01f;
        int n = pos.Length;
        if (sx.Length != n) { sx = new float[n]; sy = new float[n]; iz = new float[n]; }
        for (int i = 0; i < n; i++)
        {
            var d = pos[i] - eye;
            float z = Vector3.Dot(d, f);
            if (z < near) { iz[i] = -1; continue; }
            iz[i] = 1f / z;
            sx[i] = cx + Vector3.Dot(d, r) * focal * iz[i];
            sy[i] = cy - Vector3.Dot(d, u) * focal * iz[i];
        }
        if (triRatio.Length != idx.Length / 3) triRatio = new float[idx.Length / 3];

        // Pass 1: which triangle is nearest at each pixel.
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = idx[t], b = idx[t + 1], c = idx[t + 2];
            if (iz[a] < 0 || iz[b] < 0 || iz[c] < 0) continue;
            var nOut = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]) * windingSign;
            if (nOut.LengthSquared() < 1e-20f) continue;
            bool front = Vector3.Dot(nOut, eye - pos[a]) > 0;
            int s = t / 3 < triSection.Length ? triSection[t / 3] : 0;
            var look = s < looks.Length ? looks[s] : null;
            if (!front && look?.TwoSided != true) continue;
            float sArea = MathF.Abs((sx[b] - sx[a]) * (sy[c] - sy[a]) - (sx[c] - sx[a]) * (sy[b] - sy[a]));
            Vector2 ua = uv.Length > a ? uv[a] : default, ub = uv.Length > b ? uv[b] : default, uc = uv.Length > c ? uv[c] : default;
            float uArea = MathF.Abs((ub.X - ua.X) * (uc.Y - ua.Y) - (uc.X - ua.X) * (ub.Y - ua.Y));
            triRatio[t / 3] = sArea > 1e-6f ? uArea / sArea : 0;
            Raster(t, a, b, c, front, look);
        }

        // Pass 2: shade the visible pixels.
        var key = Vector3.Normalize(-f * 0.55f + u * 0.8f - r * 0.35f);
        var fill = Vector3.Normalize(-f * 0.35f - u * 0.15f + r * 0.9f);
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                int p = y * W + x, g = gTri[p];
                if (g < 0) continue;
                color[p] = Shade(g & 0x3FFFFFFF, (g & 0x40000000) != 0, gB0[p], gB1[p], eye, key, fill);
            }
        });

        if (snap) return;
        if (frame == null || frame.Width != W || frame.Height != H) { frame?.Dispose(); frame = new Bitmap(W, H, PixelFormat.Format32bppArgb); }
        var bd = frame.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < H; y++) Marshal.Copy(color, y * W, bd.Scan0 + y * bd.Stride, W);
        frame.UnlockBits(bd);
        LastFrameMs = clock.Elapsed.TotalMilliseconds;
    }

    internal static float Lod(float ratio, Map m) => ratio <= 0 ? 0 : 0.5f * MathF.Log2(Math.Max(1e-8f, ratio * m.W * m.H));

    void Raster(int t, int a, int b, int c, bool front, Look? look)
    {
        float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
        float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area) < 1e-6f) return;
        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2)))), maxX = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2)))), maxY = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;
        float inv = 1f / area;
        float z0 = iz[a], z1 = iz[b], z2 = iz[c];
        var cut = look is { Cutout: true, Diffuse: not null } ? look.Diffuse : null;
        float lod = cut != null ? Lod(triRatio[t / 3], cut) : 0;
        Vector2 ua = uv.Length > a ? uv[a] : default, ub = uv.Length > b ? uv[b] : default, uc = uv.Length > c ? uv[c] : default;
        int tag = t / 3 | (front ? 0 : 0x40000000);
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
                float b0 = w0 * z0 / z, b1 = w1 * z1 / z;
                if (cut != null)
                {
                    var tuv = ua * b0 + ub * b1 + uc * (1 - b0 - b1);
                    if (cut.AlphaAt(tuv.X, tuv.Y, lod) < 85) continue;   // the masked clip (0.33)
                }
                depth[p] = z; gTri[p] = tag; gB0[p] = b0; gB1[p] = b1;
            }
        }
    }

    int Shade(int tri, bool back, float b0, float b1, Vector3 eye, Vector3 key, Vector3 fillDir)
    {
        int t = tri * 3, a = idx[t], b = idx[t + 1], c = idx[t + 2];
        float b2 = 1 - b0 - b1;
        var P = pos[a] * b0 + pos[b] * b1 + pos[c] * b2;
        var V = Vector3.Normalize(eye - P);
        var N = Vector3.Normalize(nrm[a] * b0 + nrm[b] * b1 + nrm[c] * b2);
        if (back) N = -N;
        var tuv = uv[a] * b0 + uv[b] * b1 + uv[c] * b2;
        int s = tri < triSection.Length ? triSection[tri] : 0;
        var look = s < looks.Length ? looks[s] : null;
        float ratio = triRatio[tri];

        var diff = look?.Diffuse is { } dm ? dm.Sample(tuv.X, tuv.Y, Lod(ratio, dm)) : new Vector4(0.62f, 0.62f, 0.64f, 1);
        var rgb = new Vector3(diff.X, diff.Y, diff.Z);
        if (look == null) look = Look.Plain(null);

        if (look.UseNormal && look.Normal is { } nm && tan.Length > a)
        {
            var ts = nm.Sample(tuv.X, tuv.Y, Lod(ratio, nm));
            float nx = (ts.X * 2 - 1) * look.NormalStrength, ny = (ts.Y * 2 - 1) * look.NormalStrength, nz = ts.Z * 2 - 1;
            var T4 = tan[a] * b0 + tan[b] * b1 + tan[c] * b2;
            var T = new Vector3(T4.X, T4.Y, T4.Z);
            T -= N * Vector3.Dot(N, T);
            if (T.LengthSquared() > 1e-12f)
            {
                T = Vector3.Normalize(T);
                var B = Vector3.Cross(N, T) * (tan[a].W < 0 ? -1f : 1f);
                var bumped = T * nx + B * ny + N * MathF.Max(0.05f, nz);
                if (bumped.LengthSquared() > 1e-12f) N = Vector3.Normalize(bumped);
            }
        }

        float ndl = Vector3.Dot(N, key);
        float diffuseLight = look.HalfLambert ? (ndl * 0.5f + 0.5f) * (ndl * 0.5f + 0.5f) : MathF.Max(ndl, 0);
        var light = new Vector3(0.38f * look.Ambient + 0.95f * diffuseLight);
        if (look.Fill) light += look.FillColor * (0.25f * MathF.Max(Vector3.Dot(N, fillDir), 0));
        var outc = rgb * light;

        if (look.UseSpec)
        {
            float m = look.Spec.At(tuv, ratio, 0.35f);
            float power = look.SpecPower * (look.SpecPow.Map != null ? 0.5f + 1.5f * look.SpecPow.At(tuv, ratio, 0.5f) : 1f);
            var h = Vector3.Normalize(key + V);
            float spec = MathF.Pow(MathF.Max(Vector3.Dot(N, h), 0), Math.Clamp(power, 2, 128)) * m * look.SpecStrength * 0.5f;
            var sc = look.SpecColor is { } scm ? scm.Sample(tuv.X, tuv.Y, Lod(ratio, scm)) : Vector4.One;
            outc += new Vector3(sc.X, sc.Y, sc.Z) * spec;
        }
        if (look.UseRim)
        {
            float edge = 1 - Math.Clamp(Vector3.Dot(N, V), 0, 1);
            float rim = edge * edge * edge * (look.RimMask ? look.RimMaskAt.At(tuv, ratio, 1f) : 1f);
            outc += look.Rim * (look.DiffuseInRim ? rgb : Vector3.One) * (rim * 0.6f);
        }
        outc *= brightness;
        if (look.UseEmissive && look.Emissive.Map != null)
            outc += rgb * (look.Emissive.At(tuv, ratio, 0f) * look.EmissiveMult);

        int R = (int)(Math.Clamp(outc.X, 0, 1) * 255 + 0.5f), G = (int)(Math.Clamp(outc.Y, 0, 1) * 255 + 0.5f), Bc = (int)(Math.Clamp(outc.Z, 0, 1) * 255 + 0.5f);
        return unchecked((int)0xFF000000) | (R << 16) | (G << 8) | Bc;
    }

    /// <summary>
    /// The model as the view frames it, at <paramref name="w"/>×<paramref name="h"/> (the view's shape is expected to match),
    /// on a transparent background: drawn <paramref name="supersample"/> times larger and scaled down, so edges are smooth and
    /// the alpha is soft. Null when no mesh is shown. (The icon creator, Kurt: herohor, costume and store images.)
    /// </summary>
    public Bitmap? Snapshot(int w, int h, int supersample = 4)
    {
        if (idx.Length == 0 || w < 1 || h < 1) return null;
        int bw = w * supersample, bh = h * supersample;
        bool wasFast = fast; fast = false;
        try
        {
            Render(bw, bh);
            var big = new byte[bw * bh * 4];
            for (int p = 0; p < bw * bh; p++)
            {
                if (gTri[p] < 0) continue;   // background: fully transparent
                int c = color[p];
                big[p * 4] = (byte)c; big[p * 4 + 1] = (byte)(c >> 8); big[p * 4 + 2] = (byte)(c >> 16); big[p * 4 + 3] = 255;
            }
            // Box-filter down with premultiplied colour, so the soft edge doesn't pick up the background's colour.
            var small = new byte[w * h * 4];
            int k = supersample * supersample;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sb = 0, sg = 0, sr = 0, sa = 0;
                    for (int yy = 0; yy < supersample; yy++)
                        for (int xx = 0; xx < supersample; xx++)
                        {
                            int i = ((y * supersample + yy) * bw + x * supersample + xx) * 4, a = big[i + 3];
                            sb += big[i] * a; sg += big[i + 1] * a; sr += big[i + 2] * a; sa += a;
                        }
                    int o = (y * w + x) * 4;
                    small[o + 3] = (byte)((sa + k / 2) / k);
                    if (sa > 0) { small[o] = (byte)(sb / sa); small[o + 1] = (byte)(sg / sa); small[o + 2] = (byte)(sr / sa); }
                }
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(small, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
            bmp.UnlockBits(bd);
            return bmp;
        }
        finally { fast = wasFast; Redraw(); }   // back to the view's own size
    }

    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}
