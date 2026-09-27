using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// What a mod can target, read from the originals (never the modded live files): the texture names of the three icon
/// packages with previews, and every original string per language for searching. Loaded on demand and cached.
/// </summary>
sealed class StockCatalog(ModLibrary lib, GameState game)
{
    readonly Originals originals = new(lib.DataFolder, game);
    string Legacy => Path.Combine(lib.DataFolder, "legacy");
    readonly Dictionary<string, (Package Pkg, Dictionary<string, int> Textures)> packages = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<(string File, ulong Id, string Text)>> strings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Texture name → export index in the original icon package (sorted names), or null when there's no verified original.</summary>
    public IReadOnlyDictionary<string, int>? Textures(string iconPackage)
    {
        lock (packages)
        {
            if (packages.TryGetValue(iconPackage, out var have)) return have.Textures;
            string? original = originals.Find(iconPackage, Legacy);
            if (original == null) return null;
            var pkg = Package.Open(original);
            var names = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pkg.Exports.Length; i++)
                if (pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) names.TryAdd(pkg.Exports[i].ObjectName, i);
            var d = new Dictionary<string, int>(names, StringComparer.OrdinalIgnoreCase);
            packages[iconPackage] = (pkg, d);
            return d;
        }
    }

    /// <summary>The stock image: BGRA, size and format (largest mip, from the package or the game's .tfc).</summary>
    public (byte[] Bgra, int W, int H, string Format)? Preview(string iconPackage, string texture)
    {
        if (Textures(iconPackage) is not { } t || !t.TryGetValue(texture, out int index)) return null;
        Package pkg; lock (packages) pkg = packages[iconPackage].Pkg;
        var mip = TextureExport.ReadBestMip(pkg, index, out _, game.Cooked);
        if (mip == null) return null;
        var bgra = TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
        return bgra == null ? null : (bgra, mip.Width, mip.Height, mip.Format);
    }

    /// <summary>The stock texture's full size (what a replacement should match).</summary>
    public (int W, int H, string Format)? Size(string iconPackage, string texture)
    {
        if (Textures(iconPackage) is not { } t || !t.TryGetValue(texture, out int index)) return null;
        Package pkg; lock (packages) pkg = packages[iconPackage].Pkg;
        try { var ti = TextureInfo.Read(pkg, pkg.Exports[index]); return (ti.SizeX, ti.SizeY, ti.Format); }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { return null; }
    }

    public List<string> Languages() =>
        Directory.Exists(game.Loco) ? Directory.GetDirectories(game.Loco, "*.all").Select(d => Path.GetFileName(d)[..^4]).Order().ToList() : [];

    /// <summary>Every original string of a language (file, ID, text).</summary>
    public List<(string File, ulong Id, string Text)> Strings(string lang)
    {
        lock (strings)
        {
            if (strings.TryGetValue(lang, out var have)) return have;
            var all = new List<(string, ulong, string)>();
            string dir = Path.Combine(game.Loco, lang + ".all");
            if (Directory.Exists(dir))
                foreach (string live in Directory.GetFiles(dir, "*.string").Order())
                {
                    string name = Path.GetFileName(live), rel = Path.Combine(lang + ".all", name);
                    string? src = originals.FindString(rel, Legacy);
                    if (src == null) continue;
                    foreach (var (id, e) in StringFile.Parse(File.ReadAllBytes(src)).Entries) all.Add((name, id, e.Text));
                }
            strings[lang] = all;
            return all;
        }
    }
}
