namespace MhoExtendedModManager;

/// <summary>The power customizer's Save step, outside the window (the editor's Powers tab and --power-color-test use it).</summary>
static class PowerColorBuild
{
    /// <summary>
    /// Before saving: each colored power's packages (PowerRecolor.PackagesOf) recolored from the stock originals (or the
    /// mod's own copy, when the mod ships that package itself) into a work folder and put in the draft's packages; packages
    /// built for colors that are gone now are taken out. Returns the work folder, or null when nothing changed.
    /// </summary>
    public static string? Apply(ModDraft draft, Mod? editing, ModLibrary lib, GameState game, ref Fx.GameData? powerDb, List<string> log)
    {
        var before = editing?.Manifest.PowerColors?.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var wanted = draft.PowerColors.Where(e => !e.Color.IsNone).ToList();
        if (wanted.Count == 0 && before.Count == 0) { draft.PowerColors.Clear(); return null; }
        string? hero = draft.Packages.Select(p => HeroOf.Package(p.File, game.Cooked)).FirstOrDefault(h => h != null);
        if (wanted.Count > 0 && hero == null) throw new InvalidDataException("the mod has no hero package (UC__MarvelPlayer_…) to tell whose powers these are");
        powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(game.Root, "Data", "Game", "Calligraphy.sip")));
        string work = Path.Combine(lib.DataFolder, "power-colors-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var originals = new Originals(lib.DataFolder, game);
        var assigned = new Dictionary<string, PowerColorEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in wanted)
        {
            e.Packages = [];
            foreach (string f in PowerRecolor.PackagesOf(powerDb, e.Power, hero!, game.Cooked))
            {
                if (assigned.TryGetValue(f, out var other))
                {
                    if (other.Color != e.Color) log.Add($"{f}: used by {other.Name} too; it takes {other.Name}'s color");
                    continue;
                }
                assigned[f] = e; e.Packages.Add(f);
            }
        }
        foreach (var (f, e) in assigned)
        {
            // The mod's own copy of that package (not one built here before) is the one recolored; else the stock original.
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            string? source = k >= 0 && !before.Contains(f) ? draft.Packages[k].Source : originals.Find(f);
            if (source == null) throw new InvalidDataException($"{f}: no clean original of this power package (the game's copy is changed)");
            byte[]? bytes = PowerRecolor.Build(source, e.Color, game.Cooked, l => log.Add($"{e.Name}: {l}"));
            if (bytes == null) { e.Packages.Remove(f); continue; }   // nothing in it to recolor: not part of the mod
            string file = Path.Combine(work, f);
            File.WriteAllBytes(file, bytes);
            if (k >= 0) draft.Packages[k] = (f, file); else draft.Packages.Add((f, file));
        }
        var kept = wanted.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        draft.Packages.RemoveAll(p => before.Contains(p.File) && !kept.Contains(p.File));
        draft.PowerColors.RemoveAll(e => e.Color.IsNone);
        return work;
    }
}
