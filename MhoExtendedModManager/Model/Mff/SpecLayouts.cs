using System.Drawing;
using System.Drawing.Imaging;

namespace MhoMffImporter;

/// <summary>
/// The game's packed spec map layouts (Kurt, 2026-10-04: "do any material types run counter?"): the channels follow the words
/// of the texture parameter's name, R G B A, and differ between material types (counted on 23 hero packages: v1
/// specmultrimmaskreflection 40 sections, emissivespecpow 17, v2 specmult_specpow_skinmask_reflectivity 14,
/// specmult_specpow_reflectivity_emissive 2). The Model tab builds with Angela's material, which reads the skin-mask layout,
/// so a map in another layout is converted to it: reflectivity moved to A, channels it has no counterpart for (rim mask, glow)
/// dropped, ones it lacks at their defaults (SpecChannels.Default).
/// </summary>
static class SpecLayouts
{
    /// <param name="GlowChannel">The channel holding a glow mask (0-3), -1 = none: it becomes the material's glow map (color map ×
    /// mask), since Angela's layout has no glow channel.</param>
    public sealed record Layout(string Id, string Parameter, string Label, string[] Channels, int[] FromChannel, int GlowChannel = -1);

    /// <summary>FromChannel: for Angela's R, G, B, A, the source channel (0-3 = R G B A) or -1 = the default.</summary>
    public static readonly Layout[] All =
    [
        new("v2skin", "specmult_specpow_skinmask_reflectivity", "Shine, Power, Skin Mask, Reflectivity (v2: Angela's)", ["shine", "power", "skin mask", "reflectivity"], [0, 1, 2, 3]),
        new("v2emissive", "specmult_specpow_reflectivity_emissive", "Shine, Power, Reflectivity, Glow (v2 emissive)", ["shine", "power", "reflectivity", "glow"], [0, 1, -1, 2], 3),
        new("v1", "specmultrimmaskreflection", "Shine, Rim Mask, Reflection (v1)", ["shine", "rim mask", "reflection", "—"], [0, -1, -1, 2]),
        new("v1emissive", "emissivespecpow", "Glow Mask, Power (v1 emissive)", ["glow mask", "power", "—", "—"], [-1, 1, -1, -1], 0),
        new("v1ambient", "emissivespecpowambient", "Glow Mask, Power, Ambient (v1 skin)", ["glow mask", "power", "ambient", "—"], [-1, 1, -1, -1], 0),
        new("env", "specemissivereflectheight1", "Spec, Glow, Reflection, Height (prop / environment)", ["shine", "glow", "reflection", "height"], [0, -1, -1, 2], 1),
    ];

    public static Layout? ById(string? id) => All.FirstOrDefault(l => l.Id == id);

    /// <summary>The layout a file name names (a map exported from the game carries its parameter name), else null.</summary>
    public static Layout? FromName(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        return All.OrderByDescending(l => l.Parameter.Length).FirstOrDefault(l => n.Contains(l.Parameter));
    }

    /// <summary>What a layout does to a map, in words (the Materials tab, the log).</summary>
    public static string Change(Layout l) => l.Id == "v2skin" ? "used as it is"
        : "converted: " + string.Join(", ", new[] { "shine", "power", "skin mask", "reflectivity" }.Select((name, i) =>
            l.FromChannel[i] >= 0 ? (l.FromChannel[i] == i ? null : $"{name} from {"RGBA"[l.FromChannel[i]]}") : $"{name} {SpecChannels.Default[i]}").OfType<string>())
          + (l.Channels.Any(Dropped) ? $"; its {string.Join(" and ", l.Channels.Where(Dropped))} left out" : "")
          + (l.GlowChannel >= 0 ? $"; its {l.Channels[l.GlowChannel]} ({"RGBA"[l.GlowChannel]}) becomes the glow map" : "");

    static bool Dropped(string c) => c is "rim mask" or "ambient" or "height";

    /// <summary>
    /// The glow map a layout's glow channel makes (Kurt, 2026-10-04: glow / emissive): the color map times the mask, black
    /// elsewhere, as the game's own glow maps are (Angela's weapon: a glow color on black). Cached under %TEMP%; null when the
    /// layout has no glow channel or the mask is black everywhere.
    /// </summary>
    public static string? GlowMap(string specFile, Layout layout, string colour)
    {
        if (layout.GlowChannel < 0) return null;
        string key = specFile + "|" + File.GetLastWriteTimeUtc(specFile).Ticks + "|" + colour + "|" + File.GetLastWriteTimeUtc(colour).Ticks + "|" + layout.Id;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        string outFile = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_spec", hash + "_glow.png");
        string none = outFile + ".none";
        if (File.Exists(outFile)) return outFile;
        if (File.Exists(none)) return null;
        var (w, h, spec) = Raw(specFile, 0, 0);
        var (_, _, col) = Raw(colour, w, h);
        int[] off = [2, 1, 0, 3];
        var o = new byte[spec.Length];
        long lit = 0;
        for (int i = 0; i < w * h; i++)
        {
            int m = spec[4 * i + off[layout.GlowChannel]];
            if (m > 8) lit++;
            for (int c = 0; c < 3; c++) o[4 * i + c] = (byte)(col[4 * i + c] * m / 255);
            o[4 * i + 3] = 255;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        if (lit == 0) { File.WriteAllText(none, ""); return null; }
        using var ob = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var od = ob.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(o, 0, od.Scan0, o.Length);
        ob.UnlockBits(od);
        ob.Save(outFile, ImageFormat.Png);
        return outFile;
    }

    /// <summary>An image's pixels as stored (BGRA, no blending), at its own size or scaled to w × h.</summary>
    static (int W, int H, byte[] Bgra) Raw(string file, int w, int h)
    {
        using var src = new Bitmap(file);
        if (w <= 0) { w = src.Width; h = src.Height; }
        // at its own size: a plain copy (drawing goes through premultiplied color, which loses the values under a low alpha:
        // a packed map's alpha is data); scaled (the color map, opaque): drawn
        using var b = src.Width == w && src.Height == h ? src.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb) : new Bitmap(w, h, PixelFormat.Format32bppArgb);
        if (src.Width != w || src.Height != h)
        using (var g = Graphics.FromImage(b))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, w, h);
        }
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        return (w, h, px);
    }

    /// <summary>The map in Angela's layout: the file itself for that layout, else a converted copy (cached under %TEMP%).</summary>
    public static string ToAngela(string file, Layout? layout)
    {
        if (layout == null || layout.Id == "v2skin") return file;
        string key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks + "|" + layout.Id;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        string outFile = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_spec", hash + "_" + layout.Id + ".png");
        if (File.Exists(outFile)) return outFile;
        using var src = new Bitmap(file);
        int w = src.Width, h = src.Height;
        using var b = src.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);   // the pixels as stored (no blending)
        var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
        b.UnlockBits(d);
        int[] off = [2, 1, 0, 3];   // BGRA bytes for R G B A
        var o = new byte[px.Length];
        for (int i = 0; i < w * h; i++)
            for (int c = 0; c < 4; c++)
                o[4 * i + off[c]] = layout.FromChannel[c] >= 0 ? px[4 * i + off[layout.FromChannel[c]]] : SpecChannels.Default[c];
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        using var ob = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var od = ob.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(o, 0, od.Scan0, o.Length);
        ob.UnlockBits(od);
        ob.Save(outFile, ImageFormat.Png);
        return outFile;
    }
}
