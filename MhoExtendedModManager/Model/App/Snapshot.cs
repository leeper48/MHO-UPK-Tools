using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MhoMffImporter;

/// <summary>A mesh for the check renderer, in a right-handed frame: Z up, facing +X, left +Y (MHO data: negate Y first).</summary>
sealed record RMesh(Vector3[] Pos, int[] Tris, Vector2[] Uv, string? Texture, bool FlipV = true);
/// <summary>A bone for the check renderer (same frame); Parent -1 for none; Hot = drawn highlighted.</summary>
sealed record RBone(Vector3 Pos, int Parent, bool Hot);
/// <summary>One panel: what to draw from which side.</summary>
sealed record RPanel(string Title, string Sub, IReadOnlyList<RMesh> Meshes, IReadOnlyList<RBone>? Bones, string View);

/// <summary>Off-screen check renders (software, orthographic, flat light, textured with cut-out alpha). Phase check tool
/// only; the real 3D view comes with the GUI.</summary>
static class Snapshot
{
    sealed class Tex
    {
        public int W, H; public int[] Px = [];
        public static Tex? Load(string? path)
        {
            if (path == null || !File.Exists(path)) return null;
            using var bmp = new Bitmap(path);
            var t = new Tex { W = bmp.Width, H = bmp.Height, Px = new int[bmp.Width * bmp.Height] };
            var d = bmp.LockBits(new Rectangle(0, 0, t.W, t.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(d.Scan0, t.Px, 0, t.Px.Length);
            bmp.UnlockBits(d);
            return t;
        }
        public int Sample(Vector2 uv, bool flipV)
        {
            float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
            if (flipV) v = 1 - v;   // FBX UVs start bottom-left
            int x = Math.Clamp((int)(u * W), 0, W - 1), y = Math.Clamp((int)(v * H), 0, H - 1);
            return Px[y * W + x];
        }
    }

    /// <summary>The Phase 1 sheet of an MFF model: front, left side, back, front with bones.</summary>
    public static void Render(MffModel model, IEnumerable<Part> parts, string outPng)
    {
        var meshes = parts.SelectMany(p => p.Sections).Select(s => new RMesh(s.Pos, s.Tris, s.Uv, s.Tex.Diffuse)).ToList();
        var bones = model.Bones.Select(b => new RBone(b.Position, b.Parent, b.Deforms)).ToList();
        Sheet(outPng,
        [
            new("Front", $"{model.Folder} · {model.FrameFrom}", meshes, null, "front"),
            new("Left Side", "", meshes, null, "left"),
            new("Back", "", meshes, null, "back"),
            new("Front + Bones", "yellow = weighted bones, blue = others", meshes, bones, "front"),
        ]);
    }

    /// <summary>Renders the panels side by side into one image. Every panel uses the same scale (the largest extent of
    /// all of them), so sizes compare directly.</summary>
    public static void Sheet(string outPng, IReadOnlyList<RPanel> panels, int W = 420, int H = 620, float[]? zoomAt = null)
    {
        Protected.CheckWrite(outPng);
        const int Pad = 30;
        var texCache = new Dictionary<string, Tex?>(StringComparer.OrdinalIgnoreCase);
        Tex? TexOf(string? p) { if (p == null) return null; if (!texCache.TryGetValue(p, out var t)) texCache[p] = t = Tex.Load(p); return t; }
        var all = panels.SelectMany(p => p.Meshes).SelectMany(m => m.Pos).ToList();
        float span = all.Count == 0 ? 1 : all.Max(p => MathF.Max(MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) * 2, p.Z));
        float sc = MathF.Min((W - 2 * Pad) / span, (H - 2 * Pad - 20) / span);

        using var sheet = new Bitmap(W * panels.Count, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet)) g.Clear(Color.FromArgb(24, 30, 44));
        for (int vi = 0; vi < panels.Count; vi++)
        {
            var pn = panels[vi];
            var (right, toward) = pn.View switch
            {
                "left" => (-Vector3.UnitX, Vector3.UnitY),
                "back" => (-Vector3.UnitY, -Vector3.UnitX),
                "right" => (Vector3.UnitX, -Vector3.UnitY),
                _ => (Vector3.UnitY, Vector3.UnitX),
            };
            bool dim = pn.Bones != null;
            var px = new int[W * H]; var z = new float[W * H];
            Array.Fill(px, dim ? unchecked((int)0xFF20283A) : unchecked((int)0xFF1C2333)); Array.Fill(z, float.NegativeInfinity);
            // MFF_ZOOM=<height fraction>,<span fraction> (checks): a close-up centred at that height (0.93,0.2 = the head).
            var zoom = zoomAt ?? (Environment.GetEnvironmentVariable("MFF_ZOOM") is { Length: > 0 } zs ? zs.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null);
            float zsc = zoom == null ? sc : sc / zoom[1];
            Vector2 Proj(Vector3 p) => zoom == null ? new(W / 2f + Vector3.Dot(p, right) * sc, H - Pad - p.Z * sc)
                : new(W / 2f + (Vector3.Dot(p, right) - (zoom.Length > 2 ? zoom[2] * span : 0)) * zsc, H / 2f - (p.Z - zoom[0] * span) * zsc);   // MFF_ZOOM=h,span[,sideways]
            var light = Vector3.Normalize(toward + new Vector3(0, 0, 0.6f) + right * 0.3f);
            foreach (var s in pn.Meshes)
            {
                var tex = Environment.GetEnvironmentVariable("MFF_NOTEX") == "1" ? null : TexOf(s.Texture);
                for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                {
                    int i0 = s.Tris[t], i1 = s.Tris[t + 1], i2 = s.Tris[t + 2];
                    Vector3 p0 = s.Pos[i0], p1 = s.Pos[i1], p2 = s.Pos[i2];
                    var n = Vector3.Cross(p1 - p0, p2 - p0);
                    if (n.LengthSquared() < 1e-12f) continue;
                    n = Vector3.Normalize(n);
                    float shade = 0.35f + 0.65f * MathF.Abs(Vector3.Dot(n, light));
                    if (dim) shade *= 0.45f;
                    Vector2 a = Proj(p0), b = Proj(p1), c = Proj(p2);
                    float za = Vector3.Dot(p0, toward), zb = Vector3.Dot(p1, toward), zc = Vector3.Dot(p2, toward);
                    float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (MathF.Abs(area) < 1e-6f) continue;
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float qx = x + 0.5f, qy = y + 0.5f;
                            float w0 = ((b.X - qx) * (c.Y - qy) - (b.Y - qy) * (c.X - qx)) / area;
                            float w1 = ((c.X - qx) * (a.Y - qy) - (c.Y - qy) * (a.X - qx)) / area;
                            float w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float depth = w0 * za + w1 * zb + w2 * zc;
                            int k = y * W + x;
                            if (depth <= z[k]) continue;
                            int col = unchecked((int)0xFFB4B4B4);
                            if (tex != null)
                            {
                                col = tex.Sample(w0 * s.Uv[i0] + w1 * s.Uv[i1] + w2 * s.Uv[i2], s.FlipV);
                                if (((uint)col >> 24) < 128) continue;   // cut-out alpha (hair cards, lace)
                            }
                            z[k] = depth;
                            int rr = (int)(((col >> 16) & 255) * shade), gg = (int)(((col >> 8) & 255) * shade), bb = (int)((col & 255) * shade);
                            px[k] = unchecked((int)0xFF000000) | (Math.Min(rr, 255) << 16) | (Math.Min(gg, 255) << 8) | Math.Min(bb, 255);
                        }
                }
            }
            using var panel = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var d = panel.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, d.Scan0, px.Length);
            panel.UnlockBits(d);
            using (var g = Graphics.FromImage(panel))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (pn.Bones != null)
                {
                    using var pen = new Pen(Color.FromArgb(255, 200, 60), 1.6f);
                    using var cold = new Pen(Color.FromArgb(120, 170, 255), 1.2f);
                    foreach (var bn in pn.Bones)
                    {
                        var q = Proj(bn.Pos);
                        if (bn.Parent >= 0) g.DrawLine(bn.Hot ? pen : cold, ToPointF(Proj(pn.Bones[bn.Parent].Pos)), ToPointF(q));
                        using var br = new SolidBrush(bn.Hot ? Color.FromArgb(255, 220, 90) : Color.FromArgb(120, 170, 255));
                        g.FillEllipse(br, q.X - 2.5f, q.Y - 2.5f, 5, 5);
                    }
                }
                using var f = new Font("Segoe UI", 10, FontStyle.Bold);
                g.DrawString(pn.Title, f, Brushes.White, 8, 6);
                using var f2 = new Font("Segoe UI", 8);
                g.DrawString(pn.Sub, f2, Brushes.Silver, 8, 24);
                using var grid = new Pen(Color.FromArgb(70, 255, 255, 255));
                g.DrawLine(grid, 0, H - Pad, W, H - Pad);
            }
            using (var g = Graphics.FromImage(sheet)) g.DrawImage(panel, vi * W, 0);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        sheet.Save(outPng, ImageFormat.Png);
    }

    static PointF ToPointF(Vector2 v) => new(v.X, v.Y);
}
