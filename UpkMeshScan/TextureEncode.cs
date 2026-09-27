using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace UpkMeshScan;

/// <summary>
/// Image (PNG, JPG, BMP) -> DXT1 / DXT5 mip chain, for --import-texture, so no outside tool is needed. Format: DXT1 when
/// every alpha is 255 (opaque) or when asked for a 1-bit cut (alpha above --split kept, the masked materials' 1/3 clip
/// = 85), DXT5 otherwise (soft alpha). Mips: box filter down to 1x1; for a DXT1 cut, colour premultiplied and the
/// alpha re-cut at half coverage per level (no dark fringes, the cut-out holds from a distance), as make_mips.py did.
/// The block encoder fits each 4x4 block's colours along their main axis (PCA), then picks the nearest palette entry.
/// </summary>
static class TextureEncode
{
    public sealed record Result(string FourCC, int Width, int Height, List<(int W, int H, byte[] Data)> Levels);

    /// <param name="format">"dxt1", "dxt5" or null (choose).</param>
    public static Result FromImage(string path, string? format, int split, float scale, bool noMips = false, int maxSize = 0)
    {
        using var bmp = new Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        if (w % 4 != 0 || h % 4 != 0) throw new InvalidDataException($"{w}x{h}: DXT needs sizes divisible by 4");
        var rgba = new byte[w * h * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w * 4);
                for (int x = 0; x < w; x++)                                    // BGRA in memory -> RGBA
                {
                    int s = x * 4, d = (y * w + x) * 4;
                    rgba[d] = Scale(row[s + 2], scale); rgba[d + 1] = Scale(row[s + 1], scale); rgba[d + 2] = Scale(row[s], scale); rgba[d + 3] = row[s + 3];
                }
            }
        }
        finally { bmp.UnlockBits(bd); }

        bool opaque = true, soft = false;
        for (int i = 3; i < rgba.Length; i += 4) { if (rgba[i] != 255) opaque = false; if (rgba[i] != 0 && rgba[i] != 255) soft = true; }
        string fmt = format?.ToLowerInvariant() ?? (opaque || !soft ? "dxt1" : "dxt5");
        if (fmt is not ("dxt1" or "dxt5")) throw new ArgumentException($"format '{format}' (dxt1 or dxt5)");
        bool cut = fmt == "dxt1" && !opaque;
        if (cut) for (int i = 3; i < rgba.Length; i += 4) rgba[i] = rgba[i] > split ? (byte)255 : (byte)0;

        var levels = new List<(int, int, byte[])>();
        byte[] level = rgba; int lw = w, lh = h;
        // --max-size N: halve (same filter as the mips) until the image fits.
        while (maxSize > 0 && Math.Max(lw, lh) > maxSize && (lw > 1 || lh > 1)) (level, lw, lh) = Downsample(level, lw, lh, cut);
        w = lw; h = lh;
        while (true)
        {
            levels.Add((lw, lh, fmt == "dxt1" ? EncodeDxt1(level, lw, lh, !opaque) : EncodeDxt5(level, lw, lh)));
            if (noMips || (lw == 1 && lh == 1)) break;
            (level, lw, lh) = Downsample(level, lw, lh, cut);
        }
        return new Result(fmt == "dxt1" ? "DXT1" : "DXT5", w, h, levels);
    }

    static byte Scale(byte v, float s) => s == 1f ? v : (byte)Math.Clamp((int)MathF.Round(v * s), 0, 255);

    static (byte[], int, int) Downsample(byte[] src, int w, int h, bool cut)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                float r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(w - 1, x * 2 + dx), sy = Math.Min(h - 1, y * 2 + dy), s = (sy * w + sx) * 4;
                        float al = src[s + 3] / 255f, wgt = cut ? al : 1f;           // premultiplied for a cut-out
                        r += src[s] * wgt; g += src[s + 1] * wgt; b += src[s + 2] * wgt; a += src[s + 3]; n++;
                    }
                int d = (y * nw + x) * 4;
                float cov = a / n, div = cut ? MathF.Max(a / 255f, 1e-6f) : n;
                dst[d] = (byte)Math.Clamp(r / div, 0, 255); dst[d + 1] = (byte)Math.Clamp(g / div, 0, 255); dst[d + 2] = (byte)Math.Clamp(b / div, 0, 255);
                dst[d + 3] = cut ? (cov >= 128 ? (byte)255 : (byte)0) : (byte)MathF.Round(cov);
            }
        return (dst, nw, nh);
    }

    /// <summary>The 16 texels of block (bx, by), clamped at the image edge (levels under 4x4 repeat their pixels).</summary>
    static (Vector3[] C, byte[] A) Block(byte[] img, int w, int h, int bx, int by)
    {
        var c = new Vector3[16]; var a = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            int x = Math.Min(w - 1, bx * 4 + i % 4), y = Math.Min(h - 1, by * 4 + i / 4), s = (y * w + x) * 4;
            c[i] = new Vector3(img[s], img[s + 1], img[s + 2]); a[i] = img[s + 3];
        }
        return (c, a);
    }

    static ushort To565(Vector3 c) => (ushort)(((int)MathF.Round(Math.Clamp(c.X, 0, 255) * 31 / 255f) << 11) | ((int)MathF.Round(Math.Clamp(c.Y, 0, 255) * 63 / 255f) << 5) | (int)MathF.Round(Math.Clamp(c.Z, 0, 255) * 31 / 255f));
    static Vector3 From565(ushort v) => new(((v >> 11) & 31) * 255f / 31, ((v >> 5) & 63) * 255f / 63, (v & 31) * 255f / 31);

    /// <summary>Endpoints along the colours' main axis (PCA by power iteration), over the texels that count.</summary>
    static (Vector3 Lo, Vector3 Hi) Endpoints(Vector3[] c, bool[] use)
    {
        var pts = c.Where((_, i) => use[i]).ToArray();
        if (pts.Length == 0) return (Vector3.Zero, Vector3.Zero);
        var mean = pts.Aggregate(Vector3.Zero, (s, p) => s + p) / pts.Length;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var p in pts) { var d = p - mean; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        var axis = new Vector3(1, 1, 1);
        for (int k = 0; k < 8; k++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z, xy * axis.X + yy * axis.Y + yz * axis.Z, xz * axis.X + yz * axis.Y + zz * axis.Z);
            float len = axis.Length(); if (len < 1e-6f) { axis = new Vector3(1, 1, 1); break; } axis /= len;
        }
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in pts) { float t = Vector3.Dot(p - mean, axis); lo = MathF.Min(lo, t); hi = MathF.Max(hi, t); }
        return (mean + axis * lo, mean + axis * hi);
    }

    static byte[] ColorBlock(Vector3[] c, bool[] use, bool threeColor)
    {
        var (lo, hi) = Endpoints(c, use);
        ushort a = To565(hi), b = To565(lo);
        // 4-colour mode needs c0 > c1; 3-colour mode (index 3 = transparent black) needs c0 <= c1.
        if (threeColor ? a > b : a < b) (a, b) = (b, a);
        if (!threeColor && a == b) { if (b > 0) b--; else a++; }
        Vector3 p0 = From565(a), p1 = From565(b);
        var pal = threeColor ? new[] { p0, p1, (p0 + p1) / 2 } : new[] { p0, p1, (2 * p0 + p1) / 3, (p0 + 2 * p1) / 3 };
        uint idx = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 3;
            if (use[i])
            {
                float bd = float.MaxValue;
                for (int k = 0; k < pal.Length; k++) { float d = Vector3.DistanceSquared(c[i], pal[k]); if (d < bd) { bd = d; best = k; } }
            }
            idx |= (uint)best << (2 * i);
        }
        var o = new byte[8];
        BitConverter.GetBytes(a).CopyTo(o, 0); BitConverter.GetBytes(b).CopyTo(o, 2); BitConverter.GetBytes(idx).CopyTo(o, 4);
        return o;
    }

    static byte[] EncodeDxt1(byte[] img, int w, int h, bool alpha)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var o = new byte[bw * bh * 8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var (c, a) = Block(img, w, h, bx, by);
                var use = a.Select(v => !alpha || v >= 128).ToArray();
                bool anyCut = alpha && use.Any(u => !u);
                ColorBlock(c, use, anyCut).CopyTo(o, (by * bw + bx) * 8);
            }
        return o;
    }

    static byte[] EncodeDxt5(byte[] img, int w, int h)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        var o = new byte[bw * bh * 16];
        var all = Enumerable.Repeat(true, 16).ToArray();
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var (c, a) = Block(img, w, h, bx, by);
                byte a0 = a.Max(), a1 = a.Min();
                var blk = new byte[16];
                blk[0] = a0; blk[1] = a1;
                // 8-value alpha ramp (a0 > a1); equal endpoints -> every index 0.
                var ramp = new float[8]; ramp[0] = a0; ramp[1] = a1;
                for (int k = 1; k < 7; k++) ramp[k + 1] = ((7 - k) * a0 + k * a1) / 7f;
                ulong bits = 0;
                for (int i = 0; i < 16; i++)
                {
                    int best = 0; float bd = float.MaxValue;
                    if (a0 != a1) for (int k = 0; k < 8; k++) { float d = MathF.Abs(a[i] - ramp[k]); if (d < bd) { bd = d; best = k; } }
                    bits |= (ulong)best << (3 * i);
                }
                for (int k = 0; k < 6; k++) blk[2 + k] = (byte)(bits >> (8 * k));
                ColorBlock(c, all, false).CopyTo(blk, 8);
                blk.CopyTo(o, (by * bw + bx) * 16);
            }
        return o;
    }
}
