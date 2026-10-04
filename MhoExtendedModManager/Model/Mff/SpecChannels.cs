using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MhoMffImporter;

/// <summary>
/// An MHO packed spec map as four gray images, one per channel (Kurt, 2026-10-04): image editors (Photoshop) show a PNG's
/// alpha as transparency, and saving can change the colors wherever the alpha is 0 (Angela's skin and cloth: 70 % of her map),
/// which silently wipes shine, power and skin mask there. Exports write each MHO spec map as &lt;name&gt;_R / _G / _B / _A.png
/// beside the combined one (with a read-me), and channel files beside an FBX are read back (combined) when they're newer than
/// the combined map; the Materials tab's From Channels combines picked files.
/// </summary>
static class SpecChannels
{
    public static readonly string[] Suffix = ["_R", "_G", "_B", "_A"];
    public static readonly string[] Names = ["Shine (R)", "Power (G)", "Skin Mask (B)", "Reflectivity (A)"];
    /// <summary>A channel left out: shine 26 (the stock median), power 49 (Angela's skin and default), no skin mask, no reflection.</summary>
    public static readonly byte[] Default = [26, 49, 0, 0];

    /// <summary>The note for users (the From Channels window, the read-me written with exports).</summary>
    public const string Note =
        "An MHO spec map packs four gray maps into one image: R = Shine (how strong the highlight is), G = Power (how tight and " +
        "sharp it is), B = Skin Mask (where skin shading applies), A = Reflectivity (how much of the environment reflects: what " +
        "makes metal look like metal; Angela's armor is about 130, her skin and cloth 0). White is most, black is none.\r\n\r\n" +
        "The A channel is not transparency, but image editors such as Photoshop show a PNG's alpha as transparency: where " +
        "Reflectivity is 0 the map looks see-through, and saving can change or clear the R, G and B values there (on Angela that " +
        "would wipe the shine, power and skin mask of all her skin and cloth). So edit the four channels as separate gray images " +
        "(<name>_R.png, _G.png, _B.png, _A.png) and let the Mod Manager combine them, or use a format that keeps alpha as a real " +
        "channel (TGA, DDS).";

    static (int W, int H, byte[] Bgra) Load(string file, int w = 0, int h = 0)
    {
        using var src = new Bitmap(file);
        if (w <= 0) { w = src.Width; h = src.Height; }
        // the pixels as stored: drawing an image with alpha blends its colors away where the alpha is 0 (the very problem
        // these files avoid), so read them directly, and resize with a plain copy (no blending)
        Bitmap bmp;
        if (src.Width == w && src.Height == h) bmp = src.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        else
        {
            bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        using var _ = bmp;
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
        bmp.UnlockBits(d);
        return (w, h, px);
    }

    static void Save(string file, int w, int h, byte[] bgra)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, d.Scan0, bgra.Length);
        bmp.UnlockBits(d);
        bmp.Save(file, ImageFormat.Png);
    }

    /// <summary>Writes the four channels of <paramref name="rgbaFile"/> as gray PNGs &lt;stem&gt;_R … _A in <paramref name="dir"/>,
    /// and the read-me (once per folder).</summary>
    public static void Split(string rgbaFile, string dir, string stem)
    {
        Protected.CheckWrite(dir);
        var (w, h, px) = Load(rgbaFile);
        int[] offset = [2, 1, 0, 3];   // BGRA bytes: R G B A
        for (int c = 0; c < 4; c++)
        {
            var g = new byte[px.Length];
            for (int i = 0; i < w * h; i++) { byte v = px[4 * i + offset[c]]; g[4 * i] = g[4 * i + 1] = g[4 * i + 2] = v; g[4 * i + 3] = 255; }
            Save(Path.Combine(dir, stem + Suffix[c] + ".png"), w, h, g);
        }
        string readme = Path.Combine(dir, "MHO spec maps - read me.txt");
        if (!File.Exists(readme))
            File.WriteAllText(readme, Note + "\r\n\r\nHere: each <material>_mhospec.png is written as <material>_mhospec_R/_G/_B/_A.png too. Edit those; " +
                "the Model tab reads them back (combined) when they're newer than <material>_mhospec.png.\r\n");
    }

    /// <summary>Combines gray channel images (null: <paramref name="rgb"/>'s channel, else <paramref name="baseRgba"/>'s, else the
    /// default) into an RGBA PNG at <paramref name="outFile"/>, at the largest size given.</summary>
    public static string Combine(IReadOnlyList<string?> channels, string? rgb, string? baseRgba, string outFile)
    {
        var files = channels.Concat([rgb, baseRgba]).OfType<string>().ToList();
        if (files.Count == 0) throw new InvalidDataException("no image to make the spec map from");
        int w = 0, h = 0;
        foreach (var f in files) using (var b = new Bitmap(f)) { if (b.Width * b.Height > w * h) { w = b.Width; h = b.Height; } }
        var rgbPx = rgb != null ? Load(rgb, w, h).Bgra : null;
        var basePx = baseRgba != null ? Load(baseRgba, w, h).Bgra : null;
        var outp = new byte[w * h * 4];
        int[] offset = [2, 1, 0, 3];
        for (int c = 0; c < 4; c++)
        {
            byte[]? gray = channels.Count > c && channels[c] is string cf ? Load(cf, w, h).Bgra : null;
            for (int i = 0; i < w * h; i++)
                outp[4 * i + offset[c]] = gray != null ? (byte)((gray[4 * i] + gray[4 * i + 1] + gray[4 * i + 2]) / 3)
                    : rgbPx != null && c < 3 ? rgbPx[4 * i + offset[c]]
                    : basePx != null ? basePx[4 * i + offset[c]]
                    : Default[c];
        }
        Protected.CheckWrite(outFile);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        Save(outFile, w, h, outp);
        return outFile;
    }

    /// <summary>
    /// The MHO spec map of a material whose files sit in a folder: <paramref name="find"/>(suffix) finds &lt;material&gt;_mhospec +
    /// suffix. Channel files (_R … _A) newer than the combined map (or with none) are combined (into a cache under %TEMP%, keyed
    /// by the files' paths and times); else the combined map; else null.
    /// </summary>
    public static string? Find(Func<string, string?> find)
    {
        string? rgba = find("");
        var ch = Suffix.Select(s => find(s)).ToArray();
        if (ch.All(c => c == null)) return rgba;
        DateTime newest = ch.OfType<string>().Max(File.GetLastWriteTimeUtc);
        if (rgba != null && File.GetLastWriteTimeUtc(rgba) >= newest) return rgba;
        string key = string.Join("|", ch.Select(c => c == null ? "-" : c + File.GetLastWriteTimeUtc(c).Ticks)) + "|" + (rgba ?? "");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        string outFile = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_spec", hash + ".png");
        return File.Exists(outFile) ? outFile : Combine(ch, null, rgba, outFile);
    }
}
