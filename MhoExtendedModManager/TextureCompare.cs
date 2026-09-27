using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// --compare-textures a b: for every Texture2D path in either package, is the image the same (format, size, and the
/// pixels of the largest mip, read inline or from the game's .tfc caches)? Checks an icon-package rebuild against the
/// one MHModManager wrote before (storage details such as tag order and offsets may differ; the images must not).
/// </summary>
static class TextureCompare
{
    public static int Run(string a, string b)
    {
        var settings = Settings.Load();
        string? root = settings.ResolvedGameRoot(Settings.LibraryData(settings.LibraryPath));
        string? cache = root == null ? null : Settings.Cooked(root);
        var pa = Package.Open(a); var pb = Package.Open(b);
        var ta = Textures(pa); var tb = Textures(pb);
        int same = 0; var differ = new List<string>();
        foreach (string path in ta.Keys.Union(tb.Keys).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!ta.TryGetValue(path, out int ia)) { differ.Add($"{path}: only in {Path.GetFileName(b)}"); continue; }
            if (!tb.TryGetValue(path, out int ib)) { differ.Add($"{path}: only in {Path.GetFileName(a)}"); continue; }
            var ma = TextureExport.ReadBestMip(pa, ia, out string na, cache);
            var mb = TextureExport.ReadBestMip(pb, ib, out string nb, cache);
            if (ma == null || mb == null) { differ.Add($"{path}: unreadable ({(ma == null ? na : nb)})"); continue; }
            if (!ma.Format.Equals(mb.Format, StringComparison.OrdinalIgnoreCase) || ma.Width != mb.Width || ma.Height != mb.Height)
                differ.Add($"{path}: {ma.Width}x{ma.Height} {ma.Format} vs {mb.Width}x{mb.Height} {mb.Format}");
            else if (!ma.Pixels.AsSpan().SequenceEqual(mb.Pixels)) differ.Add($"{path}: pixels differ");
            else same++;
        }
        foreach (string d in differ.Take(40)) Console.WriteLine("  " + d);
        if (differ.Count > 40) Console.WriteLine($"  … {differ.Count - 40} more");
        Console.WriteLine($"{same} texture(s) identical, {differ.Count} different.");
        return differ.Count == 0 ? 0 : 1;
    }

    static Dictionary<string, int> Textures(Package p)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Exports.Length; i++)
            if (p.ClassOf(p.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) d.TryAdd(p.PathOf(p.Exports[i]), i);
        return d;
    }
}
