using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>Which textures a live texture cache (.tfc) holds differently from its original (compared mip by mip at the manifest's locations).</summary>
static class TfcCheck
{
    /// <summary>Texture paths (e.g. MarvelUIIcons_Store.Store_Vision_Classic) whose cached mips differ.</summary>
    public static List<string> Changed(string cooked, string cache, string originalTfc)
    {
        var changed = new List<string>();
        using var fa = File.OpenRead(Path.Combine(cooked, cache + ".tfc"));
        using var fb = File.OpenRead(originalTfc);
        foreach (var e in TfcCache.All(cooked).Where(e => e.Cache.Equals(cache, StringComparison.OrdinalIgnoreCase)))
            foreach (var (_, off, size) in e.Mips.Where(m => m.Size > 0))
            {
                if (off + (long)size > fa.Length || off + (long)size > fb.Length) { changed.Add(e.Path); break; }
                var x = new byte[size]; var y = new byte[size];
                fa.Position = off; fa.ReadExactly(x); fb.Position = off; fb.ReadExactly(y);
                if (!x.AsSpan().SequenceEqual(y)) { changed.Add(e.Path); break; }
            }
        return changed;
    }
}
