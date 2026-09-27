using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// --capture-icons: icon-package textures that differ from stock in the game but that no mod in the library replaces
/// (made with another tool, or left by a mod that was deleted). Apply rebuilds icon packages from stock, which would drop
/// them, so they are saved as .dds files into a new texture mod (enabled, lowest priority). Covers the three MHModManager
/// icon packages and every other stock icon package (ICO__*) whose live file isn't stock (those go to the
/// ExtraIconReplacements extension). On Kurt's game 2026-09-27 there were 10 (Ms. Marvel, Captain Marvel, Rogue, Storm, Beast).
/// </summary>
static class IconCapture
{
    /// <summary>The icon packages besides MHModManager's three: every stock ICO__*.upk in the game folder.</summary>
    public static List<string> ExtraPackages(GameState game) =>
        Directory.Exists(game.Cooked)
            ? Directory.GetFiles(game.Cooked, "ICO__*.upk").Select(Path.GetFileName).OfType<string>()
                .Where(f => game.IsStockName(f) && !Applier.IconPackages.Any(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>Stock textures whose image in <paramref name="livePath"/> differs from the original's (object names).</summary>
    public static List<string> ChangedTextures(GameState game, string livePath, string originalPath, Originals originals)
    {
        var pl = Package.Open(livePath); var po = Package.Open(originalPath);
        var stock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < po.Exports.Length; i++) if (po.ClassOf(po.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) stock.TryAdd(po.PathOf(po.Exports[i]), i);
        var changed = new List<string>();
        for (int i = 0; i < pl.Exports.Length; i++)
        {
            var e = pl.Exports[i];
            if (!pl.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) || !stock.TryGetValue(pl.PathOf(e), out int si)) continue;
            if (!SameImage(game, pl, i, po, si, originals)) changed.Add(e.ObjectName);
        }
        return changed;
    }

    static bool SameImage(GameState game, Package pl, int i, Package po, int si, Originals originals)
    {
        var ml = TextureExport.ReadBestMip(pl, i, out _, game.Cooked);
        var ms = TextureExport.ReadBestMip(po, si, out _, originals.CacheFolderFor(po, si));
        if (ml == null || ms == null) return true;   // unreadable either side: not reported as a change
        return ml.Format.Equals(ms.Format, StringComparison.OrdinalIgnoreCase) && ml.Width == ms.Width && ml.Height == ms.Height && ml.Pixels.AsSpan().SequenceEqual(ms.Pixels);
    }

    public static int Run(ModLibrary lib, GameState game, Originals originals, string? name)
    {
        string legacy = Path.Combine(lib.DataFolder, "legacy");
        name ??= $"Captured icon changes {DateTime.Now:yyyy-MM-dd}";
        string folder = Path.Combine(lib.DataFolder, "mods", name);
        if (Directory.Exists(folder)) { Console.WriteLine($"A mod folder '{name}' already exists. Nothing captured."); return 1; }

        var manifest = new ModManifest { Name = name, Author = "captured from the game", Version = DateTime.Now.ToString("yyyy-MM-dd"), Type = ModType.Texture };
        var lists = new[] { manifest.Replacements, manifest.AchievementReplacements, manifest.StoreReplacements };
        var extra = new List<ExtraIconReplacement>();
        string temp = folder + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);

        // The three MHModManager packages, then any other icon package that isn't stock any more.
        var targets = Applier.IconPackages.Select((p, k) => (p.File, p.Label, K: k)).ToList();
        targets.AddRange(ExtraPackages(game).Where(f => game.IsStock(f) == false).Select(f => (File: f, Label: "icon", K: -1)));
        foreach (var (file, label, k) in targets)
        {
            string live = Path.Combine(game.Cooked, file);
            string? original = originals.Find(file, legacy);
            if (!File.Exists(live) || original == null) { Console.WriteLine($"  {file}: {(original == null ? "no clean original to compare with" : "not in the game")}, skipped"); continue; }
            var claimed = (k >= 0 ? lib.Mods.SelectMany(m => Applier.IconPackages[k].List(m.Manifest)).Select(r => r.TextureName)
                                  : lib.Mods.SelectMany(m => m.Manifest.Extra).Where(r => r.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).Select(r => r.TextureName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pl = Package.Open(live);
            int found = 0;
            foreach (string tex in ChangedTextures(game, live, original, originals).Where(t => !claimed.Contains(t)))
            {
                int i = Array.FindIndex(pl.Exports, e => e.ObjectName.Equals(tex, StringComparison.OrdinalIgnoreCase) && pl.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
                string dds = tex + ".dds";
                if (TextureExport.WriteDds(pl, i, Path.Combine(temp, dds), out string note, game.Cooked) == null) { Console.WriteLine($"  {tex}: can't save ({note})"); continue; }
                if (k >= 0) lists[k].Add(new TextureReplacement { TextureName = tex, DdsFileName = dds });
                else extra.Add(new ExtraIconReplacement { Package = file, TextureName = tex, DdsFileName = dds });
                Console.WriteLine($"  {tex} ({label}{(k < 0 ? ", " + file : "")})");
                found++;
            }
            Console.WriteLine($"  {file}: {found} unmanaged change(s)");
        }
        manifest.TextureReplacementCount = lists.Sum(l => l.Count);
        manifest.HasTextures = manifest.TextureReplacementCount > 0;
        if (extra.Count > 0) manifest.ExtraIconReplacements = extra;
        if (manifest.TextureReplacementCount + extra.Count == 0) { if (Directory.Exists(temp)) Directory.Delete(temp, true); Console.WriteLine("Nothing to capture: every changed icon belongs to a mod."); return 0; }
        File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(manifest, ModManifest.Json));
        Directory.Move(temp, folder);

        // Enabled, at the bottom of the order, so real mods win where they overlap later.
        lib.State.ModOrder = lib.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).Append(name).ToList();
        lib.State.EnabledMods = lib.Mods.Where(m => m.Enabled).Select(m => m.FolderName).Append(name).ToList();
        lib.State.ApplyLocks();   // locked mods keep their place at the top / bottom
        File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(lib.State, ModManifest.Json));
        Console.WriteLine($"Saved {manifest.TextureReplacementCount + extra.Count} texture(s) as the mod '{name}' (enabled, lowest priority).");
        return 0;
    }
}
