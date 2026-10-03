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

        /// <summary>Whether the alpha channel carries anything (some texels differ): an additive effect texture whose
        /// alpha is all 0 (Forked Lightning's tex_lightning_storm) adds its colour as it is (power effects).</summary>
        public bool AlphaVaries { get; }

        /// <summary>Whether the alpha is no mask for an additive effect: zero where most of the bright colour is (Iron Man's
        /// Microlaser beam texture: the laser in RGB, alpha 0 on it; multiplied by it, the beam was black). An additive
        /// material adds its colour; such an alpha isn't what it uses.</summary>
        public bool AlphaUnused { get; }

        /// <summary>For additive drawing: the alpha to use (1 when the texture's alpha is constant or unused).</summary>
        public bool AdditiveIgnoresAlpha => !AlphaVaries || AlphaUnused;

        /// <summary>A copy with every pixel's colour through <paramref name="f"/> (the power customizer's preview).</summary>
        public Map Recolored(Func<Vector3, Vector3> f)
        {
            var src = levels[0]; var dst = new byte[src.Length];
            System.Threading.Tasks.Parallel.For(0, src.Length / 4, i =>
            {
                int o = i * 4;
                var c = f(new Vector3(src[o + 2], src[o + 1], src[o]) * (1f / 255f));
                dst[o + 2] = (byte)Math.Clamp((int)(c.X * 255 + 0.5f), 0, 255); dst[o + 1] = (byte)Math.Clamp((int)(c.Y * 255 + 0.5f), 0, 255);
                dst[o] = (byte)Math.Clamp((int)(c.Z * 255 + 0.5f), 0, 255); dst[o + 3] = src[o + 3];
            });
            return new Map(dst, ws[0], hs[0]);   // (alpha kept, so AlphaVaries reads the same)
        }

        public Map(byte[] bgra, int w, int h)
        {
            for (int i = 7; i < bgra.Length && !AlphaVaries; i += 4) if (bgra[i] != bgra[3]) AlphaVaries = true;
            if (AlphaVaries)
            {
                int bright = 0, hidden = 0;
                for (int i = 0; i + 3 < bgra.Length; i += 4 * 7)
                    if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) > 128) { bright++; if (bgra[i + 3] < 26) hidden++; }
                AlphaUnused = bright > 20 && hidden * 2 > bright;
            }
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
        /// <summary>
        /// Specular as the material sets it (Kurt, 2026-09-29; a survey of 96 stock character materials):
        /// chbasematerial (v1) spreads the power map from SpecPower to SpecPowerMax (specularpower1min / max) and may add a
        /// second highlight (specmult2, specularpower2min / max); chbasematerial_v2 with usespecpowermask takes the power as
        /// specularpower × map × specularpowermask (typically 255: the map's byte value). SpecTint: speccolorvalue;
        /// DiffuseSpec (usediffusemultspec): the highlight takes the diffuse colour × DiffuseSpecMult, desaturated by SpecDesat.
        /// </summary>
        public float SpecPowerMax, SpecPowerMask, Spec2Strength, Spec2Min, Spec2Max, DiffuseSpecMult = 1, SpecDesat;
        public Vector3 SpecTint = Vector3.One;
        public bool DiffuseSpec;
        /// <summary>
        /// Reflections (Kurt, 2026-09-29): the material's own reflectiontex, a latitude-longitude environment image (stock
        /// bw_reflect / nova_original_reflect / lukecage_90s_reflect are square, pano_mountains 2:1: sky or ceiling lights on
        /// top, the horizon across the middle), looked up by the reflected view direction (Z up). Strength: the packed map's
        /// reflect channel × reflectionmult, stronger at grazing angles by fresnelpower; multiplyreflectionbydiffuse tints it
        /// by the diffuse colour. EmissiveTex: a separate full-colour glow texture ("emissive", with use_emissivergb).
        /// </summary>
        public Map? Reflection, EmissiveTex;
        public Channel ReflectAt;
        public bool UseReflection, ReflectByDiffuse;
        public float ReflectMult = 1, FresnelPower;
        public Vector3 Rim = new(0.5f, 0.55f, 0.65f), FillColor = new(0.8f, 0.6f, 0.5f);
        /// <summary>An unlit see-through effect material (an animated actor's silhouette: Angela's, Taskmaster's slashes): not
        /// drawn as a surface; PropRig draws it as effect triangles of <see cref="GhostColor"/> (additive: glowing).</summary>
        public bool Ghost, GhostAdditive;
        public Vector3 GhostColor = Vector3.One;
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
        set { value = Math.Clamp(value, 0f, 2f); if (Math.Abs(brightness - value) < 1e-4) return; brightness = value; Redraw(); }
    }
    float brightness = 1;

    /// <summary>Shading parts shown (the preview's Spec / Reflect / Glow toggles). Each redraws.</summary>
    public bool ShowSpec { get => showSpec; set { if (showSpec == value) return; showSpec = value; Redraw(); } }
    public bool ShowReflections { get => showRefl; set { if (showRefl == value) return; showRefl = value; Redraw(); } }
    /// <summary>How strong the glow (emissive) parts are, 0–2 (Kurt, 2026-10-02: the game's values, added after the light
    /// and clipped at white here, can look stronger than in game). Redraws.</summary>
    public float GlowStrength { get => glowStrength; set { value = Math.Clamp(value, 0f, 2f); if (Math.Abs(glowStrength - value) < 1e-4) return; glowStrength = value; Redraw(); } }
    float glowStrength = 1;
    public bool ShowGlow { get => showGlow; set { if (showGlow == value) return; showGlow = value; Redraw(); } }
    /// <summary>Bloom (Kurt, 2026-10-02: as in the game): light past white (glow, the hottest highlights) blurred into a soft
    /// halo over the frame. Redraws.</summary>
    public bool ShowBloom { get => showBloom; set { if (showBloom == value) return; showBloom = value; Redraw(); } }
    bool showSpec = true, showRefl = true, showGlow = true, showBloom = true;

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
    float[] over = [];                       // per pixel, the light past white (R, G, B): what blooms
    volatile bool anyOver;

    /// <summary>
    /// A particle to draw over the model (power effects; ported from the MHO Hero Creator's ModelView, which descends from this one). Engine-space
    /// position, full size, rotation (radians), HDR colour and alpha, its texture's sub-image grid and image, how it faces
    /// the camera (0 square, 1 rectangle, 2 stretched along its velocity), kept upright (axis lock on Z), and its blending
    /// (additive: glows; else translucent).
    /// </summary>
    public readonly record struct FxQuad(Vector3 Position, Vector2 Size, float Rotation, Vector4 Color, Map? Texture, int Cols, int Rows, int Image,
        int Align, bool LockZ, bool Additive, Vector3 Velocity)
    {
        /// <summary>A sprite fixed in a plane (UE3 axis lock EPAL_X / Y / Z): its width and height directions in engine space
        /// (zero: it faces the camera).</summary>
        public Vector3 PlaneRight { get; init; }
        public Vector3 PlaneUp { get; init; }
        /// <summary>Stays in its plane (a decal on the ground), not turned to face the camera like a flat bolt.</summary>
        public bool KeepPlane { get; init; }
        /// <summary>The texture's U runs along the quad's long side (a beam's texture runs along the beam).</summary>
        public bool SwapUV { get; init; }
    }

    /// <summary>The particles drawn over the model (after shading, depth-tested against it). Set, then Redraw via UpdateEffects.</summary>
    public List<FxQuad> Effects { get; set; } = new();

    public void UpdateEffects(List<FxQuad> fx) { Effects = fx; EffectTris = new(); Redraw(); }

    /// <summary>Pass 3: every particle as a camera-facing quad, depth-tested against the model, blended into the frame.</summary>
    void DrawEffects(Vector3 r, Vector3 u, Vector3 f, Vector3 eye, float focal, float cx, float cy, float near)
    {
        foreach (var q in Effects)
        {
            if (q.PlaneRight != Vector3.Zero) { DrawPlaneQuad(q, r, u, f, eye, focal, cx, cy, near); continue; }
            var pos = new Vector3(q.Position.X, -q.Position.Y, q.Position.Z);
            var d = pos - eye;
            float z = Vector3.Dot(d, f);
            if (z < near) continue;
            float izq = 1f / z, px = cx + Vector3.Dot(d, r) * focal * izq, py = cy - Vector3.Dot(d, u) * focal * izq;
            float halfW = q.Size.X * 0.5f * focal * izq, halfH = (q.Align == 0 ? q.Size.X : q.Size.Y) * 0.5f * focal * izq;
            if (halfW < 0.3f && halfH < 0.3f) continue;
            // Velocity-aligned with no velocity: UE3 takes the direction from the velocity, so the quad collapses and nothing
            // shows (Iron Man's Microlaser Sweep charge: 900-unit flares with none stood up as tall bars, Kurt 2026-10-02).
            if (q.Align == 2 && q.Velocity.LengthSquared() <= 1e-6f) continue;
            // A beam piece right by the camera (a beam aimed past the viewer) would cover the view: left out.
            if (q.SwapUV && halfW > W / 6f) continue;
            // The quad's axes on screen: turned by its rotation, or its long side along the velocity / the world's up.
            Vector2 ax, ay;
            if (q.Align == 2 && q.Velocity.LengthSquared() > 1e-6f)
            {
                var v = new Vector3(q.Velocity.X, -q.Velocity.Y, q.Velocity.Z);
                var sv = new Vector2(Vector3.Dot(v, r), -Vector3.Dot(v, u));
                if (sv.LengthSquared() < 1e-8f) sv = new Vector2(0, -1);
                ay = Vector2.Normalize(sv); ax = new Vector2(-ay.Y, ay.X);
            }
            else if (q.LockZ)
            {
                var sv = new Vector2(Vector3.Dot(Vector3.UnitZ, r), -Vector3.Dot(Vector3.UnitZ, u));
                ay = sv.LengthSquared() > 1e-8f ? Vector2.Normalize(sv) : new Vector2(0, -1); ax = new Vector2(-ay.Y, ay.X);
            }
            else { float c = MathF.Cos(q.Rotation), sn = MathF.Sin(q.Rotation); ax = new Vector2(c, sn); ay = new Vector2(-sn, c); }
            float ext = MathF.Sqrt(halfW * halfW + halfH * halfH);
            int x0 = Math.Max(0, (int)(px - ext)), x1 = Math.Min(W - 1, (int)(px + ext)), y0 = Math.Max(0, (int)(py - ext)), y1 = Math.Min(H - 1, (int)(py + ext));
            if (x0 > x1 || y0 > y1) continue;
            int cols = Math.Max(1, q.Cols), rows = Math.Max(1, q.Rows), img = Math.Clamp(q.Image, 0, cols * rows - 1);
            float cu = (img % cols) / (float)cols, cv = (img / cols) / (float)rows;
            float lod = q.Texture == null ? 0 : Math.Max(0, MathF.Log2(Math.Max(1f, q.Texture.W / (float)cols / Math.Max(1f, 2 * halfW))));
            var tint = q.Color;
            Parallel.For(y0, y1 + 1, y =>
            {
                for (int x = x0; x <= x1; x++)
                {
                    int p = y * W + x;
                    if (depth[p] > izq) continue;                   // the model is nearer
                    var o = new Vector2(x + 0.5f - px, y + 0.5f - py);
                    float lx = Vector2.Dot(o, ax) / (2 * halfW), ly = Vector2.Dot(o, ay) / (2 * halfH);
                    if (lx < -0.5f || lx > 0.5f || ly < -0.5f || ly > 0.5f) continue;
                    if (q.Texture == null) continue;
                    Vector4 t = q.SwapUV ? q.Texture.Sample(cu + (0.5f - ly) / cols, cv + (lx + 0.5f) / rows, lod) : q.Texture.Sample(cu + (lx + 0.5f) / cols, cv + (ly + 0.5f) / rows, lod);
                    if (q.Additive && q.Texture.AdditiveIgnoresAlpha) t.W = 1;
                    if (q.SwapUV && beamDebug) lock (beamStats) { beamStats[0]++; beamStats[1] += t.X * t.W; beamStats[2] = Math.Max(beamStats[2], t.X * t.W); }
                    Blend(p, t, tint, q.Additive);
                }
            });
        }
    }

    /// <summary>Blends a particle's texel into the frame: additive (glow: texture × HDR colour × alpha, added) or translucent.</summary>
    /// <summary>Effects' strength (their opacity / glow; 1 = the game's values; the 3D View's Effects slider).</summary>
    public float EffectStrength { get; set; } = 1.25f;
    static readonly bool beamDebug = Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1";
    static readonly double[] beamStats = new double[3];
    public static string BeamStats() { lock (beamStats) { string r = $"beam pixels {beamStats[0]}, mean red×alpha {(beamStats[0] > 0 ? beamStats[1] / beamStats[0] : 0):0.000}, max {beamStats[2]:0.000}"; Array.Clear(beamStats); return r; } }

    void Blend(int p, Vector4 t, Vector4 tint, bool additive)
    {
        tint.W *= EffectStrength;
        int c0 = color[p];
        float br = ((c0 >> 16) & 255) / 255f, bgc = ((c0 >> 8) & 255) / 255f, bb = (c0 & 255) / 255f;
        float rr, gg, b2;
        if (additive)
        {
            float k = Math.Max(0, tint.W) * t.W;
            float ar = t.X * tint.X * k, ag = t.Y * tint.Y * k, ab = t.Z * tint.Z * k;
            // Overbright (HDR colours such as lightning's 4, 5, 50) turns white, as the game's bloom shows it, instead of
            // clipping one channel (magenta / pure blue fringes).
            float ex = Math.Max(0, Math.Max(ar, Math.Max(ag, ab)) - 1), over = 0.3f * ex / (1 + ex);   // soft: at most +0.3
            rr = br + ar + over; gg = bgc + ag + over; b2 = bb + ab + over;
        }
        else
        {
            float a = Math.Clamp(t.W * tint.W, 0, 1);
            rr = br + (t.X * tint.X - br) * a; gg = bgc + (t.Y * tint.Y - bgc) * a; b2 = bb + (t.Z * tint.Z - bb) * a;
        }
        color[p] = unchecked((int)0xFF000000) | (Math.Clamp((int)(rr * 255), 0, 255) << 16) | (Math.Clamp((int)(gg * 255), 0, 255) << 8) | Math.Clamp((int)(b2 * 255), 0, 255);
    }

    /// <summary>A sprite fixed in a plane: each pixel's view ray meets the plane; depth-tested against the model.</summary>
    void DrawPlaneQuad(FxQuad q, Vector3 r, Vector3 u, Vector3 f, Vector3 eye, float focal, float cx, float cy, float near)
    {
        static Vector3 M(Vector3 v) => new(v.X, -v.Y, v.Z);   // engine -> display (Y mirrored)
        var c = M(q.Position);
        var R0 = Vector3.Normalize(M(q.PlaneRight)); var U0 = Vector3.Normalize(M(q.PlaneUp));
        float cr = MathF.Cos(q.Rotation), sr = MathF.Sin(q.Rotation);
        var R = (R0 * cr + U0 * sr) * (q.Size.X * 0.5f); var U = (U0 * cr - R0 * sr) * (q.Size.Y * 0.5f);
        if (!q.KeepPlane)
        // The game's camera looks down from above, where these flat sprites (bolts lying in the socket's plane) read well;
        // from the side they'd be edge-on and vanish (Kurt: the bolts didn't show). So the sprite keeps its long axis (the
        // bolt's direction) and turns around it to face the camera: from above the same as flat, from the side visible.
        {
            bool rLong = R.LengthSquared() >= U.LengthSquared();
            var lng = rLong ? R : U; var sht = rLong ? U : R;
            var side = Vector3.Cross(Vector3.Normalize(lng), c - eye);
            if (side.LengthSquared() > 1e-8f)
            {
                side = Vector3.Normalize(side) * sht.Length();
                if (Vector3.Dot(side, sht) < 0) side = -side;
                if (rLong) U = side; else R = side;
            }
        }
        var n = Vector3.Cross(R, U);
        if (n.LengthSquared() < 1e-6f) return;
        // Screen bounds from the corners (all must be in front of the camera).
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        foreach (var k in new[] { c - R - U, c + R - U, c + R + U, c - R + U })
        {
            var d = k - eye; float z = Vector3.Dot(d, f);
            if (z < near) return;
            float sx = cx + Vector3.Dot(d, r) * focal / z, sy = cy - Vector3.Dot(d, u) * focal / z;
            x0 = Math.Min(x0, sx); x1 = Math.Max(x1, sx); y0 = Math.Min(y0, sy); y1 = Math.Max(y1, sy);
        }
        int ix0 = Math.Max(0, (int)x0), ix1 = Math.Min(W - 1, (int)x1), iy0 = Math.Max(0, (int)y0), iy1 = Math.Min(H - 1, (int)y1);
        if (ix0 > ix1 || iy0 > iy1 || q.Texture == null) return;
        int cols = Math.Max(1, q.Cols), rows = Math.Max(1, q.Rows), img = Math.Clamp(q.Image, 0, cols * rows - 1);
        float cu = (img % cols) / (float)cols, cv = (img / cols) / (float)rows;
        float px = Math.Max(1f, Math.Max(x1 - x0, y1 - y0));
        float lod = Math.Max(0, MathF.Log2(Math.Max(1f, q.Texture.W / (float)cols / px)));
        float rr2 = R.LengthSquared(), uu2 = U.LengthSquared(), nd = Vector3.Dot(c - eye, n);
        var tint = q.Color; var tex = q.Texture;
        Parallel.For(iy0, iy1 + 1, y =>
        {
            for (int x = ix0; x <= ix1; x++)
            {
                var dir = f + r * ((x + 0.5f - cx) / focal) - u * ((y + 0.5f - cy) / focal);   // dot(dir, f) = 1: t = depth
                float dn = Vector3.Dot(dir, n);
                if (MathF.Abs(dn) < 1e-9f) continue;
                float t = nd / dn;
                if (t < near) continue;
                int p = y * W + x;
                if (depth[p] > 1f / t) continue;
                var o = eye + dir * t - c;
                float lx = Vector3.Dot(o, R) / rr2 * 0.5f, ly = -Vector3.Dot(o, U) / uu2 * 0.5f;
                if (lx < -0.5f || lx > 0.5f || ly < -0.5f || ly > 0.5f) continue;
                var tx = tex.Sample(cu + (lx + 0.5f) / cols, cv + (ly + 0.5f) / rows, lod);
                if (q.Additive && tex.AdditiveIgnoresAlpha) tx.W = 1;
                Blend(p, tx, tint, q.Additive);
            }
        });
    }

    /// <summary>
    /// A textured triangle drawn with the effects (ported from the MHO Hero Creator): the triangles of a mesh effect (shockwave rings), a beam, a
    /// pet's or a thrown weapon's model. Engine-space corners, UVs, HDR colour and alpha; opaque ones (models) are lit
    /// simply and write depth, others are blended (additive or translucent) and only depth-tested.
    /// </summary>
    public readonly record struct FxTri(Vector3 A, Vector3 B, Vector3 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, Vector4 Color, Map? Texture, bool Additive, bool Opaque);

    /// <summary>The triangles drawn with the effects (opaque ones first, then the particles, then the blended ones).</summary>
    public List<FxTri> EffectTris { get; set; } = new();

    public void UpdateEffects(List<FxQuad> fx, List<FxTri> tris) { Effects = fx; EffectTris = tris; Redraw(); }

    void DrawTris(bool opaque, Vector3 r, Vector3 u, Vector3 f, Vector3 eye, float focal, float cx, float cy, float near)
    {
        static Vector3 M(Vector3 v) => new(v.X, -v.Y, v.Z);
        var light = Vector3.Normalize(-f * 0.4f + u * 0.8f - r * 0.3f);
        foreach (var t in EffectTris)
        {
            if (t.Opaque != opaque) continue;
            Vector3 a = M(t.A), b = M(t.B), c = M(t.C);
            float za = Vector3.Dot(a - eye, f), zb = Vector3.Dot(b - eye, f), zc = Vector3.Dot(c - eye, f);
            if (za < near || zb < near || zc < near) continue;
            Vector2 S(Vector3 v, float z) => new(cx + Vector3.Dot(v - eye, r) * focal / z, cy - Vector3.Dot(v - eye, u) * focal / z);
            var sa = S(a, za); var sb = S(b, zb); var sc = S(c, zc);
            float area = (sb.X - sa.X) * (sc.Y - sa.Y) - (sb.Y - sa.Y) * (sc.X - sa.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(sa.X, Math.Min(sb.X, sc.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(Math.Max(sa.X, Math.Max(sb.X, sc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(sa.Y, Math.Min(sb.Y, sc.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(Math.Max(sa.Y, Math.Max(sb.Y, sc.Y))));
            if (x0 > x1 || y0 > y1) continue;
            float ia = 1 / za, ib = 1 / zb, ic = 1 / zc;
            var n = Vector3.Cross(b - a, c - a);
            float shade = 1;
            if (opaque && n.LengthSquared() > 1e-12f) { n = Vector3.Normalize(n); if (Vector3.Dot(n, eye - a) < 0) n = -n; shade = 0.35f + 0.65f * Math.Max(0, Vector3.Dot(n, light)); }
            var tint = t.Color; var tex = t.Texture;
            float px = Math.Max(1, Math.Max(x1 - x0, y1 - y0));
            float lod = tex == null ? 0 : Math.Max(0, MathF.Log2(Math.Max(1f, tex.W * Math.Max(Vector2.Distance(t.Ua, t.Ub), Vector2.Distance(t.Ua, t.Uc)) / px)));
            void Row(int y)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float qx = x + 0.5f, qy = y + 0.5f;
                    float w0 = ((sb.X - qx) * (sc.Y - qy) - (sb.Y - qy) * (sc.X - qx)) / area;
                    float w1 = ((sc.X - qx) * (sa.Y - qy) - (sc.Y - qy) * (sa.X - qx)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float iz = w0 * ia + w1 * ib + w2 * ic;
                    int p = y * W + x;
                    if (depth[p] > iz) continue;                    // something nearer
                    var uv = (t.Ua * (w0 * ia) + t.Ub * (w1 * ib) + t.Uc * (w2 * ic)) / iz;
                    Vector4 tx = tex == null ? Vector4.One : tex.Sample(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y), lod);
                    if (opaque)
                    {
                        if (tex != null && tex.AlphaVaries && tx.W < 0.33f) continue;   // masked
                        depth[p] = iz;
                        color[p] = unchecked((int)0xFF000000) | (Math.Clamp((int)(tx.X * tint.X * shade * 255), 0, 255) << 16) | (Math.Clamp((int)(tx.Y * tint.Y * shade * 255), 0, 255) << 8) | Math.Clamp((int)(tx.Z * tint.Z * shade * 255), 0, 255);
                    }
                    else
                    {
                        if (t.Additive && tex != null && tex.AdditiveIgnoresAlpha) tx.W = 1;
                        Blend(p, tx, tint, t.Additive);
                    }
                }
            }
            if ((x1 - x0) * (y1 - y0) > 4096) Parallel.For(y0, y1 + 1, Row);
            else for (int y = y0; y <= y1; y++) Row(y);
        }
    }

    /// <summary>
    /// While an animation plays: frames are drawn at no more than <see cref="MovingPixels"/> and stretched to the view
    /// (Kurt, 2026-09-30: playback was slow; a full-size frame of the preview column took 34–57 ms, with effects 100–220).
    /// Turning it off draws the still frame at full size again.
    /// </summary>
    public bool Moving
    {
        get => moving;
        set { if (moving == value) return; moving = value; if (!value) Redraw(); }
    }
    bool moving;
    public const int MovingPixels = 360_000;
    // The playing budget adapts (dynamic resolution): heavy power effects are fill-bound (Ground Smash's 46–89 sprites
    // took 44–107 ms at 360k pixels), so it shrinks while frames take over 30 ms and grows back while they're quick.
    float movingBudget = MovingPixels;

    /// <summary>For tests: how long the last frame took to draw (ms).</summary>
    public double LastFrameMs { get; private set; }

    void Render() => Render(0, 0);

    /// <summary>Draws the view; with a size, at that size for a snapshot (the frame shown isn't touched).</summary>
    void Render(int width, int height)
    {
        bool snap = width > 0;
        if (idx.Length == 0 || (!snap && (canvas.ClientSize.Width < 8 || canvas.ClientSize.Height < 8))) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int cw = canvas.ClientSize.Width, ch = canvas.ClientSize.Height;
        float scale = snap ? 1 : fast ? 2 : moving ? Math.Max(1f, MathF.Sqrt((float)cw * ch / movingBudget)) : 1;
        W = snap ? width : Math.Max(8, (int)(cw / scale)); H = snap ? height : Math.Max(8, (int)(ch / scale));
        if (color.Length != W * H) { color = new int[W * H]; depth = new float[W * H]; gTri = new int[W * H]; gB0 = new float[W * H]; gB1 = new float[W * H]; }
        if (showBloom) { if (over.Length != W * H * 3) over = new float[W * H * 3]; else Array.Clear(over); }
        anyOver = false;
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
            if (look?.Ghost == true) continue;   // drawn as effect triangles (PropRig)
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
                color[p] = Shade(g & 0x3FFFFFFF, (g & 0x40000000) != 0, gB0[p], gB1[p], eye, key, fill, p);
            }
        });
        if (showBloom && anyOver) Bloom(bg >>> 24 == 0);
        // Pass 3 (power effects): opaque effect triangles, the particles, then the blended triangles, depth-tested against the model.
        if (EffectTris.Count > 0) DrawTris(true, r, u, f, eye, focal, cx, cy, near);
        if (Effects.Count > 0) DrawEffects(r, u, f, eye, focal, cx, cy, near);
        if (EffectTris.Count > 0) DrawTris(false, r, u, f, eye, focal, cx, cy, near);
        if (beamDebug && beamStats[0] + beamStats[1] >= 0 && Effects.Any(e => e.SwapUV)) Console.WriteLine("      drawn: " + BeamStats() + $", {Effects.Count(e => e.SwapUV)} beam pieces");

        if (snap) return;
        if (frame == null || frame.Width != W || frame.Height != H) { frame?.Dispose(); frame = new Bitmap(W, H, PixelFormat.Format32bppArgb); }
        var bd = frame.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < H; y++) Marshal.Copy(color, y * W, bd.Scan0 + y * bd.Stride, W);
        frame.UnlockBits(bd);
        LastFrameMs = clock.Elapsed.TotalMilliseconds;
        if (moving && !fast)
            movingBudget = LastFrameMs > 30 ? Math.Max(60_000, movingBudget * 0.75f) : LastFrameMs < 18 ? Math.Min(MovingPixels, movingBudget * 1.15f) : movingBudget;
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

    int Shade(int tri, bool back, float b0, float b1, Vector3 eye, Vector3 key, Vector3 fillDir, int p = -1)
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

        if (look.UseSpec && showSpec)
        {
            float m = look.Spec.At(tuv, ratio, 0.35f);
            float pch = look.SpecPow.Map != null ? look.SpecPow.At(tuv, ratio, 0.5f) : -1f;
            float power = look.SpecPowerMask > 0 && pch >= 0 ? look.SpecPower * pch * look.SpecPowerMask
                : look.SpecPowerMax > 0 && pch >= 0 ? look.SpecPower + (look.SpecPowerMax - look.SpecPower) * pch
                : pch >= 0 ? look.SpecPower * (0.5f + 1.5f * pch) : look.SpecPower;
            var h = Vector3.Normalize(key + V);
            float ndh = MathF.Max(Vector3.Dot(N, h), 0);
            float spec = MathF.Pow(ndh, Math.Clamp(power, 2, 256)) * m * look.SpecStrength * 0.5f;
            if (look.Spec2Strength > 0)
            {
                float p2 = look.Spec2Min + (look.Spec2Max - look.Spec2Min) * (pch >= 0 ? pch : 0.5f);
                spec += MathF.Pow(ndh, Math.Clamp(p2, 2, 256)) * m * look.Spec2Strength * 0.5f;
            }
            Vector3 color;
            if (look.DiffuseSpec)
            {
                float grey = rgb.X * 0.3f + rgb.Y * 0.59f + rgb.Z * 0.11f;
                color = Vector3.Lerp(rgb, new Vector3(grey), Math.Clamp(look.SpecDesat, 0, 1)) * look.DiffuseSpecMult;
            }
            else
            {
                var sc = look.SpecColor is { } scm ? scm.Sample(tuv.X, tuv.Y, Lod(ratio, scm)) : Vector4.One;
                color = new Vector3(sc.X, sc.Y, sc.Z);
            }
            outc += color * look.SpecTint * spec;
        }
        if (showRefl && look.UseReflection && look.Reflection is { } env)
        {
            float rm = look.ReflectAt.Map != null ? look.ReflectAt.At(tuv, ratio, 0f) : 0.5f;
            if (rm > 0.002f)
            {
                var rv = -V - 2 * Vector3.Dot(-V, N) * N;
                float u = 0.5f + MathF.Atan2(rv.Y, rv.X) / (2 * MathF.PI), v = MathF.Acos(Math.Clamp(rv.Z, -1, 1)) / MathF.PI;
                var e = env.Sample(u, v, 0);
                float fr = look.FresnelPower > 0 ? MathF.Pow(1 - Math.Clamp(Vector3.Dot(N, V), 0, 1), look.FresnelPower) : 1f;
                var rc = new Vector3(e.X, e.Y, e.Z) * (rm * look.ReflectMult * fr * 0.5f);
                if (look.ReflectByDiffuse) rc *= rgb;
                outc += rc;
            }
        }
        if (look.UseRim)
        {
            float edge = 1 - Math.Clamp(Vector3.Dot(N, V), 0, 1);
            float rim = edge * edge * edge * (look.RimMask ? look.RimMaskAt.At(tuv, ratio, 1f) : 1f);
            outc += look.Rim * (look.DiffuseInRim ? rgb : Vector3.One) * (rim * 0.6f);
        }
        outc *= brightness;
        if (showGlow && look.UseEmissive && look.Emissive.Map != null)
            outc += rgb * (look.Emissive.At(tuv, ratio, 0f) * look.EmissiveMult * glowStrength);
        if (showGlow && look.UseEmissive && look.EmissiveTex is { } em)
        {
            var ev = em.Sample(tuv.X, tuv.Y, Lod(ratio, em));
            outc += new Vector3(ev.X, ev.Y, ev.Z) * (look.EmissiveMult * glowStrength);
        }

        // What goes past white is kept for the bloom (clipped here, as the screen would).
        if (showBloom && p >= 0 && (outc.X > 1 || outc.Y > 1 || outc.Z > 1))
        {
            over[p * 3] = Math.Max(0, outc.X - 1); over[p * 3 + 1] = Math.Max(0, outc.Y - 1); over[p * 3 + 2] = Math.Max(0, outc.Z - 1);
            anyOver = true;
        }
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

    /// <summary>Bloom strength and spread (by eye; UE3 blooms the scene colour past a threshold, blurred, added back).</summary>
    const float BloomStrength = 0.9f;
    const int BloomRadius = 7;   // in quarter-size pixels (about 28 frame pixels)

    /// <summary>
    /// The light past white, shrunk to a quarter (each cell the average of 4 × 4 pixels), blurred (Gaussian, two passes) and
    /// added back over the frame. <paramref name="modelOnly"/>: only onto the model's pixels (a see-through background: an
    /// icon snapshot's empty pixels stay empty).
    /// </summary>
    void Bloom(bool modelOnly)
    {
        int qw = (W + 3) / 4, qh = (H + 3) / 4;
        var q = new float[qw * qh * 3];
        Parallel.For(0, qh, qy =>
        {
            for (int qx = 0; qx < qw; qx++)
            {
                float r = 0, g = 0, b = 0; int n = 0;
                for (int y = qy * 4; y < Math.Min(H, qy * 4 + 4); y++)
                    for (int x = qx * 4; x < Math.Min(W, qx * 4 + 4); x++) { int i = (y * W + x) * 3; r += over[i]; g += over[i + 1]; b += over[i + 2]; n++; }
                int o = (qy * qw + qx) * 3;
                if (n > 0) { q[o] = r / n; q[o + 1] = g / n; q[o + 2] = b / n; }
            }
        });
        var kernel = new float[BloomRadius * 2 + 1];
        float sigma = BloomRadius / 2.5f, sum = 0;
        for (int k = -BloomRadius; k <= BloomRadius; k++) sum += kernel[k + BloomRadius] = MathF.Exp(-k * k / (2 * sigma * sigma));
        for (int k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        var t = new float[q.Length];
        Parallel.For(0, qh, y =>
        {
            for (int x = 0; x < qw; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = -BloomRadius; k <= BloomRadius; k++)
                {
                    int xx = Math.Clamp(x + k, 0, qw - 1), i = (y * qw + xx) * 3; float w = kernel[k + BloomRadius];
                    r += q[i] * w; g += q[i + 1] * w; b += q[i + 2] * w;
                }
                int o = (y * qw + x) * 3; t[o] = r; t[o + 1] = g; t[o + 2] = b;
            }
        });
        Parallel.For(0, qh, y =>
        {
            for (int x = 0; x < qw; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = -BloomRadius; k <= BloomRadius; k++)
                {
                    int yy = Math.Clamp(y + k, 0, qh - 1), i = (yy * qw + x) * 3; float w = kernel[k + BloomRadius];
                    r += t[i] * w; g += t[i + 1] * w; b += t[i + 2] * w;
                }
                int o = (y * qw + x) * 3; q[o] = r; q[o + 1] = g; q[o + 2] = b;
            }
        });
        // Back up to full size (bilinear) and added.
        Parallel.For(0, H, y =>
        {
            float fy = Math.Clamp((y + 0.5f) / 4 - 0.5f, 0, qh - 1); int y0 = (int)fy, y1 = Math.Min(qh - 1, y0 + 1); float ty = fy - y0;
            for (int x = 0; x < W; x++)
            {
                int p = y * W + x;
                if (modelOnly && gTri[p] < 0) continue;
                float fx = Math.Clamp((x + 0.5f) / 4 - 0.5f, 0, qw - 1); int x0 = (int)fx, x1 = Math.Min(qw - 1, x0 + 1); float tx = fx - x0;
                int a = (y0 * qw + x0) * 3, b = (y0 * qw + x1) * 3, c = (y1 * qw + x0) * 3, d = (y1 * qw + x1) * 3;
                float w00 = (1 - tx) * (1 - ty), w10 = tx * (1 - ty), w01 = (1 - tx) * ty, w11 = tx * ty;
                float br = (q[a] * w00 + q[b] * w10 + q[c] * w01 + q[d] * w11) * BloomStrength;
                float bgc = (q[a + 1] * w00 + q[b + 1] * w10 + q[c + 1] * w01 + q[d + 1] * w11) * BloomStrength;
                float bb = (q[a + 2] * w00 + q[b + 2] * w10 + q[c + 2] * w01 + q[d + 2] * w11) * BloomStrength;
                if (br < 0.002f && bgc < 0.002f && bb < 0.002f) continue;
                int col = color[p];
                int R = Math.Min(255, ((col >> 16) & 255) + (int)(br * 255)), G = Math.Min(255, ((col >> 8) & 255) + (int)(bgc * 255)), B = Math.Min(255, (col & 255) + (int)(bb * 255));
                color[p] = (col & unchecked((int)0xFF000000)) | (R << 16) | (G << 8) | B;
            }
        });
    }

    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}
