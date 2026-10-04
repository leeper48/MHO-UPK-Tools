using System.Drawing;
using System.Drawing.Imaging;

namespace MhoMffImporter;

/// <summary>
/// An MHO packed spec map from the user's word on what each color group is (Kurt, 2026-10-04). No map made from the colors
/// alone can tell gold paint from gold metal (measured: metal from color features scored AUC 0.58 on held-out stock maps,
/// chance being 0.5), so the color map is split into color groups (k-means, deterministic) and the user tags them on the
/// Materials tab. Each tag's values come from Angela's own map (the one stock map whose metal and skin channels are clean):
/// metal shine 133, spec power 102, reflectivity 130; skin shine 78, power 49, skin mask on; cloth shine 17, power 56.
/// Leather has no stock reference: halfway between cloth and skin (a guess). Untagged groups get the Soft recipe's shine.
/// Within a group, brighter texels shine up to 30 % more and darker ones 30 % less (wear and detail stay visible).
/// Written in MHO's packed layout (R shine, G spec power, B skin mask, A reflectivity): used as an MHO spec map.
/// </summary>
static class ColorTags
{
    public static readonly string[] Tags = ["", "metal", "skin", "leather", "cloth", "glow"];

    public static string Label(string? tag) => tag switch
    {
        "metal" => "Metal",
        "skin" => "Skin",
        "leather" => "Leather",
        "cloth" => "Cloth",
        "glow" => "Glow",
        _ => "Not Set",
    };

    /// <summary>R shine, G spec power, B skin mask, A reflectivity per tag (Angela's measured values; leather a guess).</summary>
    static (byte R, byte G, byte B, byte A) Values(string? tag) => tag switch
    {
        "metal" => (133, 102, 0, 130),
        "skin" => (78, 49, 255, 0),
        "leather" => (48, 52, 0, 0),
        "cloth" => (17, 56, 0, 0),
        "glow" => (17, 56, 0, 0),   // (Kurt, 2026-10-04: a Glow tag) dull, as cloth: the glow is the light, not a highlight
        _ => (26, 49, 0, 0),
    };

    public sealed record Group(Color Center, float Share);

    /// <summary>16: on Angela with ideal tags, 8 groups gave shine error 48.0 (correlation 0.47), 12 46.1, 16 41.8 (0.61),
    /// 24 38.7 (0.62); Soft 56.5, flat 55.8. More than 16 gains little and is more to tag.</summary>
    public const int DefaultGroups = 16;

    /// <summary>A copy at most 512 wide (nearest texel): the groups are always found on it (the Tag Colors window and the build
    /// see the same groups, so a saved tag finds its group).</summary>
    public static (int W, int H, byte[] Bgra) Small(int w, int h, byte[] bgra)
    {
        float sc = Math.Min(1f, 512f / Math.Max(w, h));
        if (sc >= 1f) return (w, h, bgra);
        int sw = Math.Max(1, (int)(w * sc)), sh = Math.Max(1, (int)(h * sc));
        var o = new byte[sw * sh * 4];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
                Array.Copy(bgra, ((int)(y / sc) * w + (int)(x / sc)) * 4, o, (y * sw + x) * 4, 4);
        return (sw, sh, o);
    }

    /// <summary>The color map's color groups, largest first (cut-out texels left out). Deterministic: the same map gives the
    /// same groups, so saved tags find their group again.</summary>
    public static List<Group> Groups(int w, int h, byte[] bgra, int k = 0)
    {
        if (k <= 0) k = int.TryParse(Environment.GetEnvironmentVariable("MHO_COLOR_GROUPS"), out int kk) ? kk : DefaultGroups;
        var (cent, counts) = Cluster(w, h, bgra, k);
        int total = Math.Max(1, counts.Sum());
        return Enumerable.Range(0, cent.Length).Where(i => counts[i] > 0)
            .Select(i => new Group(Color.FromArgb((int)cent[i].R, (int)cent[i].G, (int)cent[i].B), counts[i] / (float)total))
            .OrderByDescending(g => g.Share).ToList();
    }

    static ((float R, float G, float B)[] Centers, int[] Counts) Cluster(int w, int h, byte[] bgra, int k)
    {
        // a sample of up to 128 × 128 texels
        int step = Math.Max(1, Math.Max(w, h) / 128);
        var px = new List<(float R, float G, float B)>();
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 3] < 128) continue;
                px.Add((bgra[i + 2], bgra[i + 1], bgra[i]));
            }
        if (px.Count == 0) return ([], []);
        k = Math.Min(k, px.Count);
        // k-means++ seeding, deterministic: the texel farthest from the centers so far
        var cent = new (float R, float G, float B)[k];
        cent[0] = px.OrderBy(p => p.R + p.G + p.B).ElementAt(px.Count / 2);
        var dmin = px.Select(p => D(p, cent[0])).ToArray();
        for (int c = 1; c < k; c++)
        {
            int far = Array.IndexOf(dmin, dmin.Max());
            cent[c] = px[far];
            for (int i = 0; i < px.Count; i++) dmin[i] = MathF.Min(dmin[i], D(px[i], cent[c]));
        }
        var counts = new int[k];
        for (int it = 0; it < 12; it++)
        {
            var sum = new (double R, double G, double B)[k];
            Array.Clear(counts);
            foreach (var p in px)
            {
                int c = Nearest(cent, p);
                sum[c].R += p.R; sum[c].G += p.G; sum[c].B += p.B; counts[c]++;
            }
            for (int c = 0; c < k; c++)
                if (counts[c] > 0) cent[c] = ((float)(sum[c].R / counts[c]), (float)(sum[c].G / counts[c]), (float)(sum[c].B / counts[c]));
        }
        return (cent, counts);
    }

    static float D((float R, float G, float B) a, (float R, float G, float B) b)
    {
        // a rough perceptual weighting (green counts most)
        float dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return 2 * dr * dr + 4 * dg * dg + 3 * db * db;
    }

    static int Nearest((float R, float G, float B)[] cent, (float R, float G, float B) p)
    {
        int best = 0; float bd = float.MaxValue;
        for (int c = 0; c < cent.Length; c++) { float d = D(p, cent[c]); if (d < bd) { bd = d; best = c; } }
        return best;
    }

    static (float, float, float) Rgb(Color c) => (c.R, c.G, c.B);

    /// <summary>Which group each texel belongs to (index into <paramref name="groups"/>; -1 cut out).</summary>
    public static int[] Assign(int w, int h, byte[] bgra, IReadOnlyList<Group> groups)
    {
        var cent = groups.Select(g => ((float)g.Center.R, (float)g.Center.G, (float)g.Center.B)).ToArray();
        var a = new int[w * h];
        for (int i = 0; i < a.Length; i++)
            a[i] = bgra[4 * i + 3] < 128 || cent.Length == 0 ? -1 : Nearest(cent, (bgra[4 * i + 2], bgra[4 * i + 1], bgra[4 * i]));
        return a;
    }

    /// <summary>Each group's tag: every saved tag goes to its single nearest group (within ~20 levels), so neighboring
    /// shades don't both take one tag.</summary>
    public static string?[] TagsFor(IReadOnlyList<Group> groups, IReadOnlyList<(string Color, string Tag)> tags)
    {
        var res = new string?[groups.Count];
        foreach (var (hex, tag) in tags)
        {
            if (!TryHex(hex, out var c)) continue;
            int best = -1; float bd = float.MaxValue;
            for (int g = 0; g < groups.Count; g++) { float d = D(Rgb(groups[g].Center), Rgb(c)); if (d < bd) { bd = d; best = g; } }
            if (best >= 0 && bd <= 9 * 400) res[best] = tag;
        }
        return res;
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static bool TryHex(string s, out Color c)
    {
        c = Color.Empty;
        if (s.Length != 7 || s[0] != '#' || !int.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        c = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }

    /// <summary>The packed map (BGRA bytes: B = skin mask, G = power, R = shine, A = reflectivity) for a color map and its tags.</summary>
    public static byte[] MakePacked(int w, int h, byte[] bgra, IReadOnlyList<(string Color, string Tag)> tags)
    {
        var small = Small(w, h, bgra);
        var groups = Groups(small.W, small.H, small.Bgra);
        var tagOf = TagsFor(groups, tags);
        var assign = Assign(w, h, bgra, groups);
        // each group's brightness range (5th to 95th percentile, sampled) for the within-group variation
        var lum = new float[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = 0.299f * bgra[4 * i + 2] + 0.587f * bgra[4 * i + 1] + 0.114f * bgra[4 * i];
        var lo = new float[groups.Count]; var hi = new float[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            var vals = new List<float>();
            for (int i = 0; i < lum.Length; i += 7) if (assign[i] == g) vals.Add(lum[i]);
            vals.Sort();
            lo[g] = vals.Count > 0 ? vals[vals.Count / 20] : 0; hi[g] = vals.Count > 0 ? vals[vals.Count * 19 / 20] : 255;
        }
        var outp = new byte[w * h * 4];
        for (int i = 0; i < assign.Length; i++)
        {
            int g = assign[i];
            if (g < 0) continue;
            var v = Values(tagOf[g]);
            float t = hi[g] > lo[g] ? Math.Clamp((lum[i] - lo[g]) / (hi[g] - lo[g]), 0, 1) : 0.5f;
            outp[4 * i + 2] = (byte)Math.Clamp(v.R * (0.7f + 0.6f * t), 0, 255);
            outp[4 * i + 1] = v.G; outp[4 * i] = v.B; outp[4 * i + 3] = v.A;
        }
        return outp;
    }

    /// <summary>
    /// The glow map the Glow tags make (Kurt, 2026-10-04): the color map where a group is tagged Glow, black elsewhere, as the
    /// game's own glow maps. Cached under %TEMP% (keyed by the color map and the tags); null without a Glow tag.
    /// </summary>
    public static string? GlowFile(string colorFile, IReadOnlyList<(string Color, string Tag)> tags)
    {
        if (!tags.Any(t => t.Tag == "glow")) return null;
        string key = colorFile + "|" + File.GetLastWriteTimeUtc(colorFile).Ticks + "|" + string.Join(",", tags.OrderBy(t => t.Color).Select(t => t.Color + "=" + t.Tag));
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        string outFile = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_spec", hash + "_tagglow.png");
        if (File.Exists(outFile)) return outFile;
        var (w, h, argb) = NormalMapGen.LoadArgb(colorFile);
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { bgra[4 * i] = (byte)argb[i]; bgra[4 * i + 1] = (byte)(argb[i] >> 8); bgra[4 * i + 2] = (byte)(argb[i] >> 16); bgra[4 * i + 3] = 255; }
        var small = Small(w, h, bgra);
        var groups = Groups(small.W, small.H, small.Bgra);
        var tagOf = TagsFor(groups, tags);
        var assign = Assign(w, h, bgra, groups);
        var outp = new byte[w * h * 4];
        for (int i = 0; i < assign.Length; i++)
        {
            outp[4 * i + 3] = 255;
            if (assign[i] >= 0 && tagOf[assign[i]] == "glow") { outp[4 * i] = bgra[4 * i]; outp[4 * i + 1] = bgra[4 * i + 1]; outp[4 * i + 2] = bgra[4 * i + 2]; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(outp, 0, d.Scan0, outp.Length);
        bmp.UnlockBits(d);
        bmp.Save(outFile, ImageFormat.Png);
        return outFile;
    }

    /// <summary>The packed map as a PNG (RGBA) in <paramref name="dir"/>.</summary>
    public static string Write(string colorFile, string? alphaFile, IReadOnlyList<(string Color, string Tag)> tags, string dir, string name)
    {
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        var (w, h, argb) = NormalMapGen.LoadArgb(colorFile);
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { bgra[4 * i] = (byte)argb[i]; bgra[4 * i + 1] = (byte)(argb[i] >> 8); bgra[4 * i + 2] = (byte)(argb[i] >> 16); bgra[4 * i + 3] = (byte)(argb[i] >> 24); }
        if (alphaFile != null)
        {
            var (aw, ah, apx) = NormalMapGen.LoadArgb(alphaFile);
            if (aw == w && ah == h) for (int i = 0; i < w * h; i++) bgra[4 * i + 3] = (byte)(((apx[i] & 0xFF) + ((apx[i] >> 8) & 0xFF) + ((apx[i] >> 16) & 0xFF)) / 3);
        }
        var packed = MakePacked(w, h, bgra, tags);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(packed, 0, d.Scan0, packed.Length);
        bmp.UnlockBits(d);
        string outFile = Path.Combine(dir, name + "_mhospec_tags.png");
        bmp.Save(outFile, ImageFormat.Png);
        return outFile;
    }
}
