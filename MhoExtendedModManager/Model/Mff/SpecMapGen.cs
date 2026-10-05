using System.Drawing;
using System.Drawing.Imaging;

namespace MhoExtendedModManager.Model;

/// <summary>
/// A spec (shine) map made from the color map when a model has none (Kurt, 2026-10-04: "a spec mask generator like we did
/// with normals"). Measured on the game's own maps first (37 stock character materials, 23 packages, v1 and v2 packed spec
/// maps): the shine channel follows the color map's brightness within a map (correlation 0.45) but its level is set per
/// material, so no recipe predicts it better than a flat value (mean error: flat 32 → 32.2, a soft brightness mask 32.0,
/// stronger masks worse). Hence a soft default and recipes to cycle through on the Materials tab:
/// <list type="bullet">
/// <item>Soft: the stock median (26) varied by a quarter of the stock range by brightness rank (about 20–83).</item>
/// <item>Strong: the same at 0.6 (about 10–160).</item>
/// <item>Dark Is Shiny: by darkness instead (leather, latex, rubber).</item>
/// <item>Detail: by local contrast (seams, stitching, creases).</item>
/// <item>Flat: 26 everywhere.</item>
/// </list>
/// The map is written in MFF's _sp layout (R = shine, B = 0: no metal), which the build packs as the game's.
/// Cut-out texels (alpha under half) get none.
/// </summary>
static class SpecMapGen
{
    public static readonly string[] Recipes = ["soft", "strong", "dark", "detail", "flat"];

    public static string Label(string? recipe) => (recipe ?? "soft") switch
    {
        "strong" => "Strong (bright parts shine a lot more)",
        "dark" => "Dark Is Shiny (leather, latex)",
        "detail" => "Detail (seams and creases shine)",
        "flat" => "Flat (the same shine everywhere)",
        _ => "Soft (bright parts shine a little more)",
    };

    public static string Next(string? recipe) => Recipes[(Array.IndexOf(Recipes, recipe ?? "soft") + 1) % Recipes.Length];

    /// <summary>The stock shine values by rank (every 5 %, 0 → 100 %), from the 37 stock maps.</summary>
    static readonly float[] Table = [0, 0, 0, 0, 3, 8, 10, 13, 16, 18, 22, 27, 34, 43, 55, 62, 75, 107, 140, 179, 255];
    const float Median = 26;

    /// <summary>Shine per texel (0-255) for a BGRA color map.</summary>
    public static byte[] Make(int w, int h, byte[] bgra, string? recipe)
    {
        recipe ??= "soft";
        int n = w * h;
        var lum = new float[n];
        for (int i = 0; i < n; i++) lum[i] = (0.299f * bgra[4 * i + 2] + 0.587f * bgra[4 * i + 1] + 0.114f * bgra[4 * i]) / 255f;
        var outv = new byte[n];
        if (recipe == "flat") { for (int i = 0; i < n; i++) outv[i] = bgra[4 * i + 3] < 128 ? (byte)0 : (byte)Median; return outv; }
        float[] sig = recipe switch
        {
            "dark" => lum.Select(l => 1 - l).ToArray(),
            "detail" => Detail(w, h, lum),
            _ => lum,
        };
        float k = recipe switch { "strong" => 0.6f, "detail" => 0.4f, _ => 0.25f };
        // rank by a 1024-bin histogram (fast on 4K maps), then the stock value at that rank
        const int Bins = 1024;
        float lo = sig.Min(), hi = sig.Max(), span = Math.Max(1e-6f, hi - lo);
        var hist = new int[Bins];
        foreach (var s in sig) hist[Math.Min(Bins - 1, (int)((s - lo) / span * Bins))]++;
        var below = new float[Bins];
        for (int b = 0, acc = 0; b < Bins; b++) { below[b] = (acc + hist[b] * 0.5f) / Math.Max(1, n - 1) * 100f; acc += hist[b]; }
        for (int i = 0; i < n; i++)
        {
            if (bgra[4 * i + 3] < 128) { outv[i] = 0; continue; }
            float pct = below[Math.Min(Bins - 1, (int)((sig[i] - lo) / span * Bins))];
            float f = Math.Clamp(pct / 5f, 0, Table.Length - 1);
            int a = (int)f; float t = f - a;
            float v = a + 1 < Table.Length ? Table[a] * (1 - t) + Table[a + 1] * t : Table[a];
            outv[i] = (byte)Math.Clamp(Median + k * (v - Median) + 0.5f, 0, 255);
        }
        return outv;
    }

    /// <summary>Local contrast: |brightness − its blur| (a box blur about 1/85 of the map wide, 3 texels at 256).</summary>
    static float[] Detail(int w, int h, float[] lum)
    {
        int r = Math.Max(1, w / 85);
        var tmp = new float[lum.Length]; var blur = new float[lum.Length];
        for (int y = 0; y < h; y++)
        {
            float sum = 0; int cnt = 0;
            for (int x = -r; x < w + r; x++)
            {
                if (x + r < w) { sum += lum[y * w + x + r]; cnt++; }
                if (x - r - 1 >= 0) { sum -= lum[y * w + x - r - 1]; cnt--; }
                if (x >= 0 && x < w) tmp[y * w + x] = sum / Math.Max(1, cnt);
            }
        }
        for (int x = 0; x < w; x++)
        {
            float sum = 0; int cnt = 0;
            for (int y = -r; y < h + r; y++)
            {
                if (y + r < h) { sum += tmp[(y + r) * w + x]; cnt++; }
                if (y - r - 1 >= 0) { sum -= tmp[(y - r - 1) * w + x]; cnt--; }
                if (y >= 0 && y < h) blur[y * w + x] = sum / Math.Max(1, cnt);
            }
        }
        var d = new float[lum.Length];
        for (int i = 0; i < d.Length; i++) d[i] = MathF.Abs(lum[i] - blur[i]);
        return d;
    }

    /// <summary>The generated map as an _sp-layout PNG (R = shine) in <paramref name="dir"/>; the color map's alpha (or the
    /// separate alpha map) cuts it.</summary>
    public static string Write(string colorFile, string? alphaFile, string? recipe, string dir, string name)
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
        var shine = Make(w, h, bgra, recipe);
        var px = new int[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFF000000) | (shine[i] << 16);
        string outFile = Path.Combine(dir, name + "_sp_gen.png");
        ImagePixels.Save(w, h, px, outFile);
        return outFile;
    }
}
