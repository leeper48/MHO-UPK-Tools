using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MhoMffImporter;

/// <summary>
/// Makes a tangent-space normal map from a colour map (MFF ships none; MHO's character materials use one).
/// Height = luminance (optionally inverted); the height is smoothed at two scales (fine: stitching, pores;
/// broad: folds, panels) and the slopes of each are added; normal = normalize(−dh/dx·s, ±dh/dy·s, 1).
/// <b>DirectX green</b> by default (MHO's normaltex: DXT1, green pointing down the image); OpenGL flips it.
/// Texels whose colour alpha is under half (cut-outs) get a flat normal. Sampling clamps at the edges.
/// Caveat: MFF colour maps have painted shading, so dark paint reads as a dent; strength and invert are the knobs.
/// </summary>
sealed class NormalMapSettings
{
    public float Strength = 1f;       // slope scale (height 0..1 per texel × this × texture size / 512)
    public float Fine = 1.0f;         // weight of the fine layer
    public float Broad = 0.6f;        // weight of the broad layer
    public float FineBlur = 0.7f;     // Gaussian sigma in texels (at 512 px; scaled with the texture size)
    public float BroadBlur = 4f;
    public bool Invert;               // dark = high instead of bright = high
    public bool OpenGl;               // green up instead of DirectX green down

    public override string ToString() =>
        $"strength {Strength}, fine {Fine} (blur {FineBlur}), broad {Broad} (blur {BroadBlur}){(Invert ? ", inverted" : "")}, {(OpenGl ? "OpenGL" : "DirectX")} green";
}

static class NormalMapGen
{
    public static (int W, int H, int[] Argb) LoadArgb(string path)
    {
        using var bmp = new Bitmap(path);
        var px = new int[bmp.Width * bmp.Height];
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(d.Scan0, px, 0, px.Length);
        bmp.UnlockBits(d);
        return (bmp.Width, bmp.Height, px);
    }

    public static void SaveArgb(int w, int h, int[] px, string path)
    {
        Protected.CheckWrite(path);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>Returns the normal map as ARGB (alpha 255), same size as the colour map.</summary>
    public static int[] Make(int w, int h, int[] colour, NormalMapSettings s, int[]? alphaMask = null)
    {
        var height = new float[w * h];
        var cut = new bool[w * h];
        for (int i = 0; i < height.Length; i++)
        {
            int c = colour[i];
            float lum = (0.299f * ((c >> 16) & 255) + 0.587f * ((c >> 8) & 255) + 0.114f * (c & 255)) / 255f;
            height[i] = s.Invert ? 1 - lum : lum;
            int a = alphaMask != null ? (alphaMask[i] >> 16) & 255 : (int)((uint)c >> 24);
            cut[i] = a < 128;
        }
        // Blur sizes and slope are given for a 512 px texture and scaled, so a 1024 map looks like a 512 one.
        float k = Math.Max(w, h) / 512f;
        var gx = new float[w * h]; var gy = new float[w * h];
        foreach (var (weight, sigma) in new[] { (s.Fine, s.FineBlur), (s.Broad, s.BroadBlur) })
        {
            if (weight == 0) continue;
            var layer = Blur(height, w, h, sigma * k);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float H(int dx, int dy) => layer[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                    // Sobel, per texel.
                    float sx = (H(1, -1) + 2 * H(1, 0) + H(1, 1) - H(-1, -1) - 2 * H(-1, 0) - H(-1, 1)) / 8f;
                    float sy = (H(-1, 1) + 2 * H(0, 1) + H(1, 1) - H(-1, -1) - 2 * H(0, -1) - H(1, -1)) / 8f;
                    gx[y * w + x] += weight * sx; gy[y * w + x] += weight * sy;
                }
        }
        var outPx = new int[w * h];
        float scale = s.Strength * 12f * k;   // 12: at strength 1 a full black-to-white step over 12 texels leans the normal 45°
        for (int i = 0; i < outPx.Length; i++)
        {
            Vector3 n;
            if (cut[i]) n = Vector3.UnitZ;
            else
            {
                // gy is the slope down the image. DirectX: green = −dh/dy(image); OpenGL: +.
                n = Vector3.Normalize(new Vector3(-gx[i] * scale, (s.OpenGl ? 1 : -1) * gy[i] * scale, 1));
            }
            int r = (int)MathF.Round((n.X * 0.5f + 0.5f) * 255), g = (int)MathF.Round((n.Y * 0.5f + 0.5f) * 255), b = (int)MathF.Round((n.Z * 0.5f + 0.5f) * 255);
            outPx[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        return outPx;
    }

    static float[] Blur(float[] src, int w, int h, float sigma)
    {
        if (sigma < 0.3f) return src;
        int r = (int)MathF.Ceiling(sigma * 3);
        var kern = new float[2 * r + 1];
        float sum = 0;
        for (int i = -r; i <= r; i++) sum += kern[i + r] = MathF.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kern.Length; i++) kern[i] /= sum;
        var tmp = new float[src.Length]; var dst = new float[src.Length];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += kern[i + r] * src[y * w + Math.Clamp(x + i, 0, w - 1)];
                tmp[y * w + x] = a;
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float a = 0;
                for (int i = -r; i <= r; i++) a += kern[i + r] * tmp[Math.Clamp(y + i, 0, h - 1) * w + x];
                dst[y * w + x] = a;
            }
        });
        return dst;
    }

    /// <summary>Check image: colour | normal map | colour lit by the normal map from the upper left, then from the right
    /// (a flat light would hide the relief), each scaled to at most 512 px.</summary>
    public static void Preview(int w, int h, int[] colour, int[] normal, bool openGl, string outPng)
    {
        int ps = Math.Min(512, Math.Max(w, h));
        float f = (float)ps / Math.Max(w, h);
        int pw = Math.Max(1, (int)(w * f)), ph = Math.Max(1, (int)(h * f));
        var panels = new int[4][];
        for (int p = 0; p < 4; p++) panels[p] = new int[pw * ph];
        // Light directions in image terms (x right, y down), as the decoded normal (x right, y down for DirectX).
        var l1 = Vector3.Normalize(new Vector3(-0.6f, -0.6f, 0.55f));
        var l2 = Vector3.Normalize(new Vector3(0.8f, 0.1f, 0.55f));
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int sx = Math.Min(w - 1, (int)(x / f)), sy = Math.Min(h - 1, (int)(y / f));
                int c = colour[sy * w + sx] | unchecked((int)0xFF000000), n = normal[sy * w + sx];
                var nv = new Vector3(((n >> 16) & 255) / 127.5f - 1, ((n >> 8) & 255) / 127.5f - 1, (n & 255) / 127.5f - 1);
                if (openGl) nv.Y = -nv.Y;   // to image-down
                int k = y * pw + x;
                panels[0][k] = c; panels[1][k] = n;
                panels[2][k] = Shade(c, 0.25f + 0.85f * MathF.Max(0, Vector3.Dot(nv, l1)));
                panels[3][k] = Shade(c, 0.25f + 0.85f * MathF.Max(0, Vector3.Dot(nv, l2)));
            }
        var sheet = new int[pw * 4 * ph];
        for (int p = 0; p < 4; p++)
            for (int y = 0; y < ph; y++) Array.Copy(panels[p], y * pw, sheet, y * pw * 4 + p * pw, pw);
        SaveArgb(pw * 4, ph, sheet, outPng);
    }

    static int Shade(int c, float s)
    {
        int r = Math.Min(255, (int)(((c >> 16) & 255) * s)), g = Math.Min(255, (int)(((c >> 8) & 255) * s)), b = Math.Min(255, (int)((c & 255) * s));
        return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }
}
