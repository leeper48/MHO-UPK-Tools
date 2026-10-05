using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The Model tab's Materials tab (Kurt, 2026-10-04: "users should have the ability to insert an override map"): per source,
/// per material, a file in place of the color, normal, spec or alpha map the importer found, and a green flip for a normal
/// map made the OpenGL way (green up; MHO's are DirectX, green down). Kept as materials\&lt;source&gt;.json in the Model work
/// folder with the images copied beside it (materials\&lt;source&gt;\), so they travel with the mod; paths are relative to the
/// json. Applied after the model is made (preview, Build, Export FBX), to the sections by material name.
/// </summary>
sealed class MaterialOverrides
{
    public sealed class Entry
    {
        public string? Color { get; set; }
        public string? Normal { get; set; }
        public string? Spec { get; set; }
        public string? Alpha { get; set; }
        public bool FlipGreen { get; set; }
        /// <summary>The generated spec map's recipe (SpecMapGen; null = Soft).</summary>
        public string? SpecRecipe { get; set; }
        /// <summary>A spec map in MHO's packed layout (used as it is) and a spec color map.</summary>
        public string? SpecMho { get; set; }
        public string? SpecColor { get; set; }
        /// <summary>A glow map (a glow color on black), and No Glow.</summary>
        public string? Glow { get; set; }
        public bool GlowOff { get; set; }
        /// <summary>The layout the MHO spec map is packed in (SpecLayouts id; null = automatic).</summary>
        public string? SpecLayout { get; set; }
        /// <summary>Color group tags: "#rrggbb" → metal / skin / leather / cloth (the Tag Colors window).</summary>
        public Dictionary<string, string>? ColorTags { get; set; }
        public bool Empty => Color == null && Normal == null && Spec == null && Alpha == null && !FlipGreen && SpecRecipe == null && SpecMho == null && SpecColor == null && SpecLayout == null && Glow == null && !GlowOff && ColorTags is not { Count: > 0 };
    }

    public Dictionary<string, Entry> Materials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MaterialOverrides Load(string path)
    {
        var o = JsonSerializer.Deserialize<MaterialOverrides>(File.ReadAllText(path), Json) ?? new MaterialOverrides();
        o.Materials = new Dictionary<string, Entry>(o.Materials, StringComparer.OrdinalIgnoreCase);
        return o;
    }

    public void Save(string path)
    {
        Protected.CheckWrite(path);
        foreach (var k in Materials.Where(kv => kv.Value.Empty).Select(kv => kv.Key).ToList()) Materials.Remove(k);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (Materials.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The overrides at <paramref name="path"/> applied to the sections (none: nothing changes). Each material gets its
    /// own Textures (sections of one material share it).</summary>
    public static void Apply(Retargeted r, string? path, Action<string>? log = null)
    {
        if (path == null || !File.Exists(path)) return;
        var o = Load(path);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? Full(string? rel) => rel == null ? null : Path.Combine(dir, rel);
        var made = new Dictionary<string, Textures>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in r.Sections)
        {
            if (!o.Materials.TryGetValue(s.Material, out var e) || e.Empty) continue;
            if (!made.TryGetValue(s.Material, out var t))
            {
                t = new Textures
                {
                    Diffuse = Full(e.Color) ?? s.Tex.Diffuse, Spec = Full(e.Spec) ?? s.Tex.Spec, Alpha = Full(e.Alpha) ?? s.Tex.Alpha,
                    Normal = Full(e.Normal) ?? s.Tex.Normal, NormalFlipGreen = e.FlipGreen, Guessed = s.Tex.Guessed && e.Color == null,
                    SpecRecipe = e.SpecRecipe ?? s.Tex.SpecRecipe,
                    SpecMho = Full(e.SpecMho) ?? s.Tex.SpecMho, SpecColor = Full(e.SpecColor) ?? s.Tex.SpecColor,
                    SpecLayout = e.SpecLayout ?? (e.SpecMho == null ? s.Tex.SpecLayout : null),
                    Glow = Full(e.Glow) ?? s.Tex.Glow, GlowOff = e.GlowOff,
                    ColorTags = e.ColorTags is { Count: > 0 } ct ? ct.Select(kv => (kv.Key, kv.Value)).ToList() : s.Tex.ColorTags,
                };
                t.Extra.AddRange(s.Tex.Extra);
                made[s.Material] = t;
                log?.Invoke($"material {s.Material}: " + string.Join(", ", new[] { ("color", e.Color), ("normal", e.Normal), ("spec", e.Spec), ("MHO spec", e.SpecMho), ("spec color", e.SpecColor), ("alpha", e.Alpha), ("glow", e.Glow) }
                    .Where(x => x.Item2 != null).Select(x => $"{x.Item1} from {Path.GetFileName(x.Item2)}").Concat(e.FlipGreen ? ["OpenGL normal map (green flipped)"] : []).Concat(e.SpecRecipe != null ? [$"spec generated: {SpecMapGen.Label(e.SpecRecipe)}"] : []).Concat(e.GlowOff ? ["no glow"] : []).Concat(e.SpecLayout != null ? [$"MHO spec layout: {SpecLayouts.ById(e.SpecLayout)?.Label}"] : []).Concat(e.ColorTags is { Count: > 0 } ctg ? [$"{ctg.Count} color group(s) tagged"] : [])));
            }
            s.Tex = t;
        }
    }

    /// <summary>A normal map as the game wants it (DirectX green, PNG): the file itself, or a flipped / converted copy in
    /// <paramref name="dir"/>.</summary>
    public static string NormalForGame(string file, bool flipGreen, string dir, string name)
    {
        if (!flipGreen && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return file;
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string outFile = Path.Combine(dir, name + "_n.png");
        using var src = new Bitmap(file);
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        // (a plain copy: drawing blends the colors away wherever the alpha is 0; some normal maps keep height there)
        using (var g = Graphics.FromImage(bmp)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(src, 0, 0, src.Width, src.Height); }
        if (flipGreen)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new byte[d.Stride * d.Height];
            System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
            for (int y = 0; y < d.Height; y++) for (int x = 0; x < d.Width; x++) { int i = y * d.Stride + x * 4 + 1; px[i] = (byte)(255 - px[i]); }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length);
            bmp.UnlockBits(d);
        }
        bmp.Save(outFile, ImageFormat.Png);
        return outFile;
    }
}
