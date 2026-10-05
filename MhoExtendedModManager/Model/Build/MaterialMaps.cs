using System.Drawing;
using System.Drawing.Imaging;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The texture images a material's slots get, made from the MFF maps as PNGs in the work folder (MaterialOut encodes them).
/// The evidence behind each packing is in the comments; the measured stock values are in CLAUDE.md.
/// </summary>
sealed class MaterialMaps
{
    readonly string workDir;
    readonly List<string> notes;
    /// <summary>Spec "raw": the _sp map unchanged; "matte": nothing view-dependent (spec, rim, reflection, spec colour 0).</summary>
    readonly bool raw, matte;
    /// <summary>v1 packing: the template's stock rim level, and whether its stock material reflects (and from which _sp channel).</summary>
    readonly int rim;
    readonly bool reflects;
    readonly string reflectFrom;
    readonly float reflectScale;

    public MaterialMaps(string workDir, List<string> notes, string? spec, string? reflect, int rim, bool reflects)
    {
        this.workDir = workDir; this.notes = notes; this.rim = rim; this.reflects = reflects;
        raw = spec == "raw";
        matte = spec == "matte";
        // MFF_REFLECT=R|G|B|none (A/B, Kurt 0.7.1: B made Mark 43 chrome all over): which _sp channel drives v1 reflection.
        reflectFrom = (reflect ?? "B").ToUpperInvariant();
        reflectScale = reflectFrom == "B" ? 1.5f : 2.5f;   // R / G run about 2.5× under stock, like the spec
        Directory.CreateDirectory(workDir);
    }

    public bool Matte => matte;

    /// <summary>The note on where v1 reflection comes from (only when the stock material reflects).</summary>
    public void NoteReflection()
    {
        if (reflects) notes.Add($"reflection from _sp {(reflectFrom == "NONE" ? "nothing (off)" : reflectFrom)} × {reflectScale}");
    }

    /// <summary>An 8×8 flat-colour stand-in (shared by every material) for a slot MFF has no map for.</summary>
    public string Flat(string name, Color c)
    {
        string p = Path.Combine(workDir, name + ".png");
        Protected.CheckWrite(p);
        using var b = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b)) g.Clear(c);
        b.Save(p, ImageFormat.Png);
        return p;
    }

    /// <summary>
    /// v1 specmultrimmaskreflection (R spec amount, G rim mask, B reflection), packed the stock way (Kurt, 0.6.4: "a weird
    /// sheen" on a flat dark grey shirt; the MFF _sp map put in as is turned reflection on everywhere). MFF's _sp red runs about
    /// 2.5× under stock (Mark 43 44 vs 110, Punisher ~20 vs 51); its blue is a metal mask (Mark 43 armour 109-148, Punisher's
    /// cloth 14-60): R = red × 2.5, G = the stock rim level, B = blue × 1.5 when the stock material reflects, else 0.
    /// </summary>
    /// <param name="redScale">MFF's _sp red runs about 2.5× under stock (below); a generated map is at stock levels already: 1.</param>
    public string PackedV1(string sp, string name, string noSpec, float redScale = 2.5f)
    {
        if (raw) return sp;
        if (matte) return noSpec;
        return Repack(sp, name, c =>
        {
            int src2 = reflectFrom switch { "R" => c.R, "G" => c.G, "NONE" => 0, _ => c.B };
            return Color.FromArgb(255, Math.Min(255, (int)(c.R * redScale)), rim, reflects ? Math.Min(255, (int)(src2 * reflectScale)) : 0);
        });
    }

    /// <summary>v2 specmult_specpow_skinmask_reflectivity (Punisher Modern VU): R spec amount, G spec power (the stock map's
    /// mean, 15), B skin mask 0, A reflectivity (DXT1: none).</summary>
    public string PackedV2(string sp, string name, string noSpec, int g = 238, int bl = 0)
    {
        if (raw) return sp;
        if (matte) return noSpec;
        return Repack(sp, name, c => Color.FromArgb(255, c.R, g, bl));
    }

    /// <summary>MFF's metal mask: (_sp blue − 60) / 50, smoothed.</summary>
    public static float Metal(Color c) { float m = Math.Clamp((c.B - 60) / 50f, 0, 1); return m * m * (3 - 2 * m); }

    /// <summary>
    /// Metal style (0.7.3, from Angela's stock armour): A (reflectivity) = 130 on metal, R = _sp red × 3.5, G 45 → 115 on
    /// metal, B 0. Measured on Angela: gold / silver armour R 157-169, G 114-119, B 4, A a flat ~130; skin R 82, G 43, B 207,
    /// A 0; cloth R 41, G 57, A 0. Encoded DXT5 (the alpha).
    /// </summary>
    public string PackedMetal(string sp, string name)
    {
        if (raw) return sp;
        return Repack(sp, name, c =>
        {
            float mt = matte ? 0 : Metal(c);
            return Color.FromArgb((int)(130 * mt), matte ? 0 : Math.Min(255, (int)(c.R * 3.5f)), (int)(45 + 70 * mt), 0);
        });
    }

    /// <summary>The metal style's spec colour: the colour map × 0.85, up to × 1.25 on metal (Angela: gold armour's spec colour
    /// is its colour brightened ~1.25×, cloth's ≈ 0.85×).</summary>
    public string SpecColour(string colour, string? sp, string name)
    {
        string outPng = Path.Combine(workDir, name + ".png");
        Protected.CheckWrite(outPng);
        using var col = new Bitmap(colour);
        int w = col.Width, h = col.Height;
        var cpx = Pixels(col);
        int[]? spx = null;
        if (sp != null) { using var raw = new Bitmap(sp); using var spb = new Bitmap(raw, w, h); spx = Pixels(spb); }
        var o = new int[w * h];
        Parallel.For(0, h, y =>
        {
            for (int i = y * w; i < (y + 1) * w; i++)
            {
                var c = Color.FromArgb(cpx[i]);
                float k = matte ? 0 : 0.85f + 0.4f * (spx != null ? Metal(Color.FromArgb(spx[i])) : 0);
                o[i] = Color.FromArgb(255, Math.Min(255, (int)(c.R * k)), Math.Min(255, (int)(c.G * k)), Math.Min(255, (int)(c.B * k))).ToArgb();
            }
        });
        SavePixels(w, h, o, outPng);
        return outPng;
    }

    /// <summary>A texel MFF paints as glowing: near-white or bright cyan.</summary>
    public static bool Glows(Color c) => (c.R + c.G + c.B) / 3f > 200 || (c.B > 180 && c.G > 170 && c.R < 160);

    /// <summary>
    /// Glow (0.7.5): for a template with a plain "emissive" slot (Angela's weapons_1602_mtl: a glow colour on black), the MFF
    /// colour map where it glows, black elsewhere (Iron Man S01's eyes, arc reactor, palms, boot spots: 0.4-0.8 % of his
    /// sheets; stock Mark 43 glows on 0.32 %).
    /// </summary>
    public string GlowMap(string colour, string name)
    {
        string outPng = Path.Combine(workDir, name + ".png");
        Protected.CheckWrite(outPng);
        using var col = new Bitmap(colour);
        var cpx = Pixels(col);
        var o = new int[cpx.Length];
        int lit = 0;
        for (int i = 0; i < cpx.Length; i++)
        {
            var c = Color.FromArgb(cpx[i]); bool g = !matte && Glows(c); if (g) lit++;
            o[i] = g ? Color.FromArgb(255, c.R, c.G, c.B).ToArgb() : Color.Black.ToArgb();
        }
        SavePixels(col.Width, col.Height, o, outPng);
        notes.Add($"{name}: glow on {(float)lit / (col.Width * col.Height):P2} of the colour map");
        return outPng;
    }

    /// <summary>An image with every pixel of <paramref name="sp"/> mapped through <paramref name="f"/>.</summary>
    string Repack(string sp, string name, Func<Color, Color> f)
    {
        string outPng = Path.Combine(workDir, name + ".png");
        Protected.CheckWrite(outPng);
        using var src = new Bitmap(sp);
        int w = src.Width, h = src.Height;
        var px = Pixels(src);
        var o = new int[w * h];
        Parallel.For(0, h, y => { for (int i = y * w; i < (y + 1) * w; i++) o[i] = f(Color.FromArgb(px[i])).ToArgb(); });
        SavePixels(w, h, o, outPng);
        return outPng;
    }

    static int[] Pixels(Bitmap b) => ImagePixels.ReadArgb(b);
    static void SavePixels(int w, int h, int[] px, string file) => ImagePixels.Save(w, h, px, file);
}
