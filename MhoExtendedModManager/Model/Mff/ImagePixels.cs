using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Reading and writing an image's pixels at once (the refactor, 2026-10-04: four near-copies of this were spread over the Model
/// code). Pixels are read as stored, never drawn: drawing goes through premultiplied color and changes the values under a low
/// alpha, and a packed spec map's alpha is data, not transparency. ARGB ints and BGRA bytes are the same memory layout.
/// </summary>
static class ImagePixels
{
    /// <summary>The image's pixels as 32-bit ARGB (other formats converted by GDI+, nothing blended).</summary>
    public static (int W, int H, int[] Argb) ReadArgb(string path)
    {
        using var b = new Bitmap(path);
        return (b.Width, b.Height, ReadArgb(b));
    }

    /// <summary>A bitmap's pixels as 32-bit ARGB (GetPixel's values, read at once).</summary>
    public static int[] ReadArgb(Bitmap b)
    {
        var px = new int[b.Width * b.Height];
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * b.Width, b.Width);
        b.UnlockBits(d);
        return px;
    }

    /// <summary>The image's pixels as BGRA bytes (as stored).</summary>
    public static (int W, int H, byte[] Bgra) ReadBgra(string path)
    {
        var (w, h, argb) = ReadArgb(path);
        return (w, h, ToBytes(argb));
    }

    /// <summary>The image's pixels as BGRA bytes at <paramref name="w"/> × <paramref name="h"/>: as stored when it's that size,
    /// else resized with a plain copy (bilinear, no blending with what's under it).</summary>
    public static (int W, int H, byte[] Bgra) ReadBgra(string path, int w, int h)
    {
        using var src = new Bitmap(path);
        if (w <= 0 || (src.Width == w && src.Height == h)) return (src.Width, src.Height, ToBytes(ReadArgb(src)));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        return (w, h, ToBytes(ReadArgb(bmp)));
    }

    /// <summary>A 32-bit bitmap of the pixels (the caller owns it).</summary>
    public static Bitmap ToBitmap(int w, int h, byte[] bgra)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, d.Scan0 + y * d.Stride, w * 4);
        bmp.UnlockBits(d);
        return bmp;
    }

    public static Bitmap ToBitmap(int w, int h, int[] argb)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++) Marshal.Copy(argb, y * w, d.Scan0 + y * d.Stride, w);
        bmp.UnlockBits(d);
        return bmp;
    }

    /// <summary>The pixels as a PNG.</summary>
    public static void Save(int w, int h, byte[] bgra, string file) { using var b = ToBitmap(w, h, bgra); b.Save(file, ImageFormat.Png); }

    public static void Save(int w, int h, int[] argb, string file) { using var b = ToBitmap(w, h, argb); b.Save(file, ImageFormat.Png); }

    static byte[] ToBytes(int[] argb)
    {
        var o = new byte[argb.Length * 4];
        Buffer.BlockCopy(argb, 0, o, 0, o.Length);
        return o;
    }
}
