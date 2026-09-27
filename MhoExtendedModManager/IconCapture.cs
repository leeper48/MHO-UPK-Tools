using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// --capture-icons: icon-package textures that differ from stock in the game but that no mod in the library replaces
/// (made with another tool, or left by a mod that was deleted). Apply rebuilds the icon packages from stock, which
/// would drop them, so they are saved as .dds files into a new texture mod (enabled, lowest priority). On Kurt's game
/// 2026-09-27 there were 10 (Ms. Marvel, Captain Marvel, Rogue, Storm, Beast costume/hero images).
/// </summary>
static class IconCapture
{
    public static int Run(ModLibrary lib, GameState game, Originals originals, string? name)
    {
        string legacy = Path.Combine(lib.DataFolder, "legacy");
        // Original images of streamed textures come from the kept original Icons.tfc when there is one (the live cache
        // may itself be modded: 11 store images were, 2026-09-27); otherwise from the live cache.
        string originalCache = originals.FindTfc("Icons") != null ? originals.TfcFolder : game.Cooked;
        name ??= $"Captured icon changes {DateTime.Now:yyyy-MM-dd}";
        string folder = Path.Combine(lib.DataFolder, "mods", name);
        if (Directory.Exists(folder)) { Console.WriteLine($"A mod folder '{name}' already exists. Nothing captured."); return 1; }

        var manifest = new ModManifest { Name = name, Author = "captured from the game", Version = DateTime.Now.ToString("yyyy-MM-dd"), Type = ModType.Texture, HasTextures = true };
        var lists = new[] { manifest.Replacements, manifest.AchievementReplacements, manifest.StoreReplacements };
        string temp = folder + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        for (int k = 0; k < Applier.IconPackages.Length; k++)
        {
            var (file, label, list) = Applier.IconPackages[k];
            string live = Path.Combine(game.Cooked, file);
            string? original = originals.Find(file, legacy);
            if (!File.Exists(live) || original == null) { Console.WriteLine($"  {file}: {(original == null ? "no clean original to compare with" : "not in the game")}, skipped"); continue; }
            var claimed = lib.Mods.SelectMany(m => list(m.Manifest)).Select(r => r.TextureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pl = Package.Open(live); var po = Package.Open(original);
            var stock = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < po.Exports.Length; i++) if (po.ClassOf(po.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) stock.TryAdd(po.PathOf(po.Exports[i]), i);
            int found = 0;
            for (int i = 0; i < pl.Exports.Length; i++)
            {
                var e = pl.Exports[i];
                if (!pl.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) || claimed.Contains(e.ObjectName)) continue;
                if (!stock.TryGetValue(pl.PathOf(e), out int si)) continue;       // not a stock texture: a rebuild from stock can't hold it
                var ml = TextureExport.ReadBestMip(pl, i, out _, game.Cooked);
                var ms = TextureExport.ReadBestMip(po, si, out _, originalCache);
                if (ml == null || ms == null) continue;
                if (ml.Format.Equals(ms.Format, StringComparison.OrdinalIgnoreCase) && ml.Width == ms.Width && ml.Height == ms.Height && ml.Pixels.AsSpan().SequenceEqual(ms.Pixels)) continue;
                string dds = e.ObjectName + ".dds";
                if (TextureExport.WriteDds(pl, i, Path.Combine(temp, dds), out string note, game.Cooked) == null) { Console.WriteLine($"  {e.ObjectName}: can't save ({note})"); continue; }
                lists[k].Add(new TextureReplacement { TextureName = e.ObjectName, DdsFileName = dds });
                Console.WriteLine($"  {e.ObjectName} ({label}, {ml.Width}x{ml.Height} {ml.Format})");
                found++;
            }
            Console.WriteLine($"  {file}: {found} unmanaged change(s)");
        }
        manifest.TextureReplacementCount = lists.Sum(l => l.Count);
        if (manifest.TextureReplacementCount == 0) { if (Directory.Exists(temp)) Directory.Delete(temp, true); Console.WriteLine("Nothing to capture: every changed icon belongs to a mod."); return 0; }
        File.WriteAllText(Path.Combine(temp, "manifest.json"), JsonSerializer.Serialize(manifest, ModManifest.Json));
        Directory.Move(temp, folder);

        // Enabled, at the bottom of the order, so real mods win where they overlap later.
        lib.State.ModOrder = lib.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).Append(name).ToList();
        lib.State.EnabledMods = lib.Mods.Where(m => m.Enabled).Select(m => m.FolderName).Append(name).ToList();
        File.WriteAllText(Path.Combine(lib.DataFolder, "state.json"), JsonSerializer.Serialize(lib.State, ModManifest.Json));
        Console.WriteLine($"Saved {manifest.TextureReplacementCount} texture(s) as the mod '{name}' (enabled, lowest priority).");
        return 0;
    }
}
