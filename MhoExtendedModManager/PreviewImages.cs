namespace MhoExtendedModManager;

/// <summary>A picture that can be shown big for a mod: one of its own images, or the game's original.</summary>
sealed record PreviewCandidate(string Key, string Texture, bool FromMod, string? File, string Package)
{
    public string Source => Package.Length == 0 ? "Custom Image" : FromMod ? "From the Mod" : "The Game's Image";
}

/// <summary>
/// The pictures offered for a mod's big preview (Kurt: authors can choose one, users can pick their own): the mod's own
/// store images, hero portraits, costume and inventory icons, then the game's originals of those, then the game's
/// picture for what a package-only mod changes (StockCatalog.DefaultStoreFor / DefaultIconFor). Keys are
/// "mod:&lt;texture&gt;" and "game:&lt;texture&gt;". Which one shows: the user's pick (state.json Previews), else the mod's choice
/// (manifest PreviewImage), else automatic (the mod's first store image, else the game's store image, else the first).
/// </summary>
static class PreviewImages
{
    static readonly string[] Prefixes = ["store", "herohor", "costume", "inventory_"];
    static int Rank(string tex) { for (int i = 0; i < Prefixes.Length; i++) if (tex.StartsWith(Prefixes[i], StringComparison.OrdinalIgnoreCase)) return i; return 99; }
    public static bool PictureLike(string tex) => Rank(tex) < 99;
    /// <summary>The original icon package a game texture is in (store images: the store package; the rest: the icons package).</summary>
    public static string PackageFor(string tex) => tex.StartsWith("store", StringComparison.OrdinalIgnoreCase) ? Applier.IconPackages[2].File : Applier.IconPackages[0].File;

    /// <summary>The candidates for a set of the mod's images (texture, .dds file, package), plus the game's defaults.</summary>
    public static List<PreviewCandidate> For(IEnumerable<(string Texture, string File, string Package)> modImages, IEnumerable<string?> gameDefaults, StockCatalog? cat)
    {
        var mine = modImages.Where(x => PictureLike(x.Texture) && File.Exists(x.File)).Select((x, i) => (x, i))
                            .OrderBy(t => Rank(t.x.Texture)).ThenBy(t => t.i).Select(t => t.x).ToList();
        var list = new List<PreviewCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tex, file, pkg) in mine)
            if (seen.Add("mod:" + tex)) list.Add(new PreviewCandidate("mod:" + tex, tex, true, file, pkg));
        bool HasStock(string tex) => cat?.Textures(PackageFor(tex)) is { } names && names.ContainsKey(tex);
        foreach (var (tex, _, pkg) in mine)
            if (pkg.Equals(PackageFor(tex), StringComparison.OrdinalIgnoreCase) && HasStock(tex) && seen.Add("game:" + tex))
                list.Add(new PreviewCandidate("game:" + tex, tex, false, null, PackageFor(tex)));
        foreach (string? tex in gameDefaults)
            if (tex != null && HasStock(tex) && seen.Add("game:" + tex))
                list.Add(new PreviewCandidate("game:" + tex, tex, false, null, PackageFor(tex)));
        return list.Take(24).ToList();
    }

    /// <summary>The candidates for an installed mod.</summary>
    public static List<PreviewCandidate> For(Mod m, StockCatalog? cat)
    {
        var man = m.Manifest;
        var images = man.StoreReplacements.Select(r => (r.TextureName ?? "", Path.Combine(m.Folder, r.DdsFileName ?? ""), Applier.IconPackages[2].File))
            .Concat(man.Replacements.Select(r => (r.TextureName ?? "", Path.Combine(m.Folder, r.DdsFileName ?? ""), Applier.IconPackages[0].File)))
            .Concat(man.Extra.Select(r => (r.TextureName ?? "", Path.Combine(m.Folder, r.DdsFileName ?? ""), r.Package)));
        var list = For(images, [cat?.DefaultStoreFor(m), cat?.DefaultIconFor(m)], cat);
        // Custom pictures (ModPictures): the user's own pick on this PC first, then the mod's own (Pictures\).
        var custom = new List<PreviewCandidate>();
        if (ModPictures.Resolve(m.Folder, m.LocalPreview) is string mine) custom.Add(new PreviewCandidate(m.LocalPreview!, ModPictures.Label(m.LocalPreview!), true, mine, ""));
        foreach (var (key, file) in ModPictures.Own(m.Folder)) custom.Add(new PreviewCandidate(key, ModPictures.Label(key), true, file, ""));
        return [.. custom, .. list];
    }

    /// <summary>The automatic choice: the mod's first store image, else the game's store image, else the first candidate.</summary>
    public static string? Automatic(List<PreviewCandidate> c) =>
        (c.FirstOrDefault(x => x.FromMod && x.Texture.StartsWith("store", StringComparison.OrdinalIgnoreCase))
         ?? c.FirstOrDefault(x => !x.FromMod && x.Texture.StartsWith("store", StringComparison.OrdinalIgnoreCase))
         ?? c.FirstOrDefault())?.Key;

    /// <summary>What shows: the user's pick, else the mod's choice, else automatic (a pick that isn't offered anymore is skipped).</summary>
    public static string? Chosen(string? userPick, string? modChoice, List<PreviewCandidate> c)
    {
        bool Has(string? k) => k != null && c.Any(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
        return Has(userPick) ? userPick : Has(modChoice) ? modChoice : Automatic(c);
    }
}
