using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MhoPackageModifier;

/// <summary>
/// Texture data to a viewable bitmap, for the GUI's texture preview: the formats the game's textures use (DXT1, DXT3,
/// DXT5, BC5 normal maps, A8R8G8B8, G8), .dds files (first mip) and ordinary images. BC5 stores only a normal map's
/// X and Y; Z is rebuilt (sqrt(1 - x² - y²)) so it looks like a normal map.
/// </summary>
static class TextureDecode
{
    /// <summary>Pixels as BGRA (4 bytes each, row by row), or null with the reason.</summary>
    public static byte[]? ToBgra(string format, int w, int h, byte[] data, out string note)
    {
        note = "";
        string f = format.ToUpperInvariant().Replace("PF_", "");
        var o = new byte[w * h * 4];
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        int Need(int n) { if (data.Length < n) throw new InvalidDataException($"{format} {w}x{h}: {data.Length} bytes, need {n}"); return n; }
        try
        {
            switch (f)
            {
                case "DXT1":
                    Need(bw * bh * 8);
                    for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++) ColorBlock(data, (by * bw + bx) * 8, o, w, h, bx, by, dxt1: true);
                    break;
                case "DXT3":
                    Need(bw * bh * 16);
                    for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
                    {
                        int b = (by * bw + bx) * 16;
                        ColorBlock(data, b + 8, o, w, h, bx, by, dxt1: false);
                        for (int i = 0; i < 16; i++) Put(o, w, h, bx, by, i, 3, (byte)(((data[b + i / 2] >> (4 * (i & 1))) & 15) * 17));
                    }
                    break;
                case "DXT5":
                    Need(bw * bh * 16);
                    for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
                    {
                        int b = (by * bw + bx) * 16;
                        ColorBlock(data, b + 8, o, w, h, bx, by, dxt1: false);
                        var a = AlphaBlock(data, b);
                        for (int i = 0; i < 16; i++) Put(o, w, h, bx, by, i, 3, a[i]);
                    }
                    break;
                case "BC5" or "ATI2":
                    Need(bw * bh * 16);
                    for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
                    {
                        int b = (by * bw + bx) * 16;
                        var r = AlphaBlock(data, b); var g = AlphaBlock(data, b + 8);
                        for (int i = 0; i < 16; i++)
                        {
                            float x = r[i] / 127.5f - 1, y = g[i] / 127.5f - 1, z = MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y));
                            Put(o, w, h, bx, by, i, 2, r[i]); Put(o, w, h, bx, by, i, 1, g[i]);
                            Put(o, w, h, bx, by, i, 0, (byte)Math.Clamp((z + 1) * 127.5f, 0, 255)); Put(o, w, h, bx, by, i, 3, 255);
                        }
                    }
                    break;
                case "A8R8G8B8":
                    Array.Copy(data, o, Need(w * h * 4));                  // stored B, G, R, A: already BGRA
                    break;
                case "V8U8":
                    // Two signed bytes per pixel (a normal map's X and Y); Z rebuilt, shown like a normal map.
                    Need(w * h * 2);
                    for (int i = 0; i < w * h; i++)
                    {
                        float x = (sbyte)data[i * 2] / 127f, y = (sbyte)data[i * 2 + 1] / 127f, z = MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y));
                        o[i * 4 + 2] = (byte)Math.Clamp((x + 1) * 127.5f, 0, 255); o[i * 4 + 1] = (byte)Math.Clamp((y + 1) * 127.5f, 0, 255);
                        o[i * 4] = (byte)Math.Clamp((z + 1) * 127.5f, 0, 255); o[i * 4 + 3] = 255;
                    }
                    break;
                case "G8" or "L8":
                    Need(w * h);
                    for (int i = 0; i < w * h; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = data[i]; o[i * 4 + 3] = 255; }
                    break;
                default:
                    note = $"format {format} can't be shown yet";
                    return null;
            }
        }
        catch (InvalidDataException ex) { note = ex.Message; return null; }
        return o;
    }

    static void Put(byte[] o, int w, int h, int bx, int by, int i, int channel, byte v)
    {
        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
        if (x < w && y < h) o[(y * w + x) * 4 + channel] = v;
    }

    static void ColorBlock(byte[] d, int at, byte[] o, int w, int h, int bx, int by, bool dxt1)
    {
        ushort c0 = BitConverter.ToUInt16(d, at), c1 = BitConverter.ToUInt16(d, at + 2);
        uint idx = BitConverter.ToUInt32(d, at + 4);
        var pal = new (int R, int G, int B, int A)[4];
        (int, int, int) U(ushort c) => (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);
        var (r0, g0, b0) = U(c0); var (r1, g1, b1) = U(c1);
        pal[0] = (r0, g0, b0, 255); pal[1] = (r1, g1, b1, 255);
        if (c0 > c1 || !dxt1)
        {
            pal[2] = ((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
            pal[3] = ((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
        }
        else { pal[2] = ((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255); pal[3] = (0, 0, 0, 0); }
        for (int i = 0; i < 16; i++)
        {
            var c = pal[(idx >> (2 * i)) & 3];
            Put(o, w, h, bx, by, i, 0, (byte)c.B); Put(o, w, h, bx, by, i, 1, (byte)c.G); Put(o, w, h, bx, by, i, 2, (byte)c.R); Put(o, w, h, bx, by, i, 3, (byte)c.A);
        }
    }

    /// <summary>A DXT5 alpha / BC4 block: two endpoints and sixteen 3-bit indices.</summary>
    static byte[] AlphaBlock(byte[] d, int at)
    {
        int a0 = d[at], a1 = d[at + 1];
        var pal = new int[8]; pal[0] = a0; pal[1] = a1;
        if (a0 > a1) for (int k = 1; k < 7; k++) pal[k + 1] = ((7 - k) * a0 + k * a1) / 7;
        else { for (int k = 1; k < 5; k++) pal[k + 1] = ((5 - k) * a0 + k * a1) / 5; pal[6] = 0; pal[7] = 255; }
        ulong bits = 0;
        for (int k = 0; k < 6; k++) bits |= (ulong)d[at + 2 + k] << (8 * k);
        var o = new byte[16];
        for (int i = 0; i < 16; i++) o[i] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
        return o;
    }

    /// <summary>A .dds file's first mip: (format, width, height, data), or null with the reason.</summary>
    public static (string Format, int W, int H, byte[] Data)? ReadDds(string path, out string note)
    {
        note = "";
        byte[] f = File.ReadAllBytes(path);
        if (f.Length < 128 || BitConverter.ToUInt32(f, 0) != 0x20534444) { note = "not a .dds file"; return null; }
        int h = BitConverter.ToInt32(f, 12), w = BitConverter.ToInt32(f, 16);
        int pfFlags = BitConverter.ToInt32(f, 80), bits = BitConverter.ToInt32(f, 88);
        string fourCC = System.Text.Encoding.ASCII.GetString(f, 84, 4);
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        (string Format, int Size) pf = (pfFlags & 4) != 0
            ? fourCC switch { "DXT1" => ("DXT1", bw * bh * 8), "DXT3" => ("DXT3", bw * bh * 16), "DXT5" => ("DXT5", bw * bh * 16), "ATI2" or "BC5U" => ("BC5", bw * bh * 16), _ => ("", 0) }
            : bits == 32 ? ("A8R8G8B8", w * h * 4) : bits == 8 ? ("G8", w * h) : ("", 0);
        if (pf.Format == "") { note = $"DDS format {(((pfFlags & 4) != 0) ? fourCC : bits + "-bit")} can't be shown yet"; return null; }
        if (f.Length < 128 + pf.Size) { note = "the .dds is shorter than its header says"; return null; }
        return (pf.Format, w, h, f.AsSpan(128, pf.Size).ToArray());
    }

    /// <summary>BGRA pixels to a 32-bit bitmap.</summary>
    public static Bitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        return bmp;
    }

    /// <summary>A bitmap's pixels as BGRA.</summary>
    public static byte[] FromBitmap(Bitmap src)
    {
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var o = new byte[bmp.Width * bmp.Height * 4];
        for (int y = 0; y < bmp.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, o, y * bmp.Width * 4, bmp.Width * 4);
        bmp.UnlockBits(bd);
        return o;
    }
}
