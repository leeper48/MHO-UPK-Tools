namespace MhoExtendedModManager;

/// <summary>The power customizer's Save step, outside the window (the editor's Powers tab and --power-color-test use it).</summary>
static class PowerColorBuild
{
    /// <summary>
    /// Before saving: each colored power's packages (PowerRecolor.PackagesOf) recolored from the stock originals (or the
    /// mod's own copy, when the mod ships that package itself) into a work folder and put in the draft's packages; packages
    /// built for colors that are gone now are taken out. Returns the work folder, or null when nothing changed.
    /// </summary>
    /// <param name="modelFolder">The draft's Model work folder (the editor's), where an own-effects recolor keeps the package
    /// it started from (Model\color_base); null: one is made here.</param>
    public static string? Apply(ModDraft draft, Mod? editing, ModLibrary lib, GameState game, ref Fx.GameData? powerDb, List<string> log, Func<string>? modelFolder = null)
    {
        var before = editing?.Manifest.PowerColors?.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var beforeOwn = editing?.Manifest.PowerColors?.Where(e => IsOwn(e.Power)).SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var wanted = draft.PowerColors.Where(e => !e.Color.IsNone).ToList();
        if (wanted.Count == 0 && before.Count == 0) { draft.PowerColors.Clear(); return null; }
        string? hero = draft.Packages.Select(p => HeroOf.Package(p.File, game.Cooked)).FirstOrDefault(h => h != null);
        if (wanted.Any(e => !IsOwn(e.Power)) && hero == null) throw new InvalidDataException("the mod has no hero package (UC__MarvelPlayer_…) to tell whose powers these are");
        if (wanted.Any(e => !IsOwn(e.Power)) || before.Count > beforeOwn.Count)
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(game.Root, "Data", "Game", "Calligraphy.sip")));
        string ModelFolder()
        {
            if (modelFolder != null) return modelFolder();
            if (draft.ModelFolder != null) return draft.ModelFolder;
            string w = Path.Combine(lib.DataFolder, "model-work-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(w);
            if (editing != null && Path.Combine(editing.Folder, ModelWork.Folder) is var own && Directory.Exists(own)) ModelWork.CopyInto(own, w);
            return draft.ModelFolder = w;
        }
        string work = Path.Combine(lib.DataFolder, "power-colors-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var originals = new Originals(lib.DataFolder, game);
        var assigned = new Dictionary<string, PowerColorEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in wanted)
        {
            e.Packages = [];
            foreach (string f in IsOwn(e.Power) ? [e.Power[OwnPrefix.Length..]] : PowerRecolor.PackagesOf(powerDb!, e.Power, hero!, game.Cooked))
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
            if (IsOwn(e.Power))
            {
                // A character's own effects (an NPC or enemy package: its glows, trails): the package also holds the Model tab's
                // model, so it's never rebuilt from the game's. The package before the recolor is kept (Model\color_base); a
                // package that's new since the last save (a Model build, an update) becomes the new start.
                if (k < 0) { log.Add($"{f}: not in the mod any more; its colors are dropped"); e.Packages.Remove(f); continue; }
                source = OwnStart(draft, editing, f, k, beforeOwn.Contains(f), ModelFolder());
            }
            if (source == null) throw new InvalidDataException($"{f}: no clean original of this power package (the game's copy is changed)");
            byte[]? bytes = PowerRecolor.Build(source, e.Color, game.Cooked, l => log.Add($"{e.Name}: {l}"));
            if (bytes == null) { e.Packages.Remove(f); continue; }   // nothing in it to recolor: not part of the mod
            string file = Path.Combine(work, f);
            File.WriteAllBytes(file, bytes);
            if (k >= 0) draft.Packages[k] = (f, file); else draft.Packages.Add((f, file));
        }
        var kept = wanted.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // own effects back to the game's colors: the package goes back to the one kept before the recolor (not out of the mod)
        foreach (string f in beforeOwn.Where(f => !kept.Contains(f)))
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k < 0) continue;
            string start = OwnStart(draft, editing, f, k, true, ModelFolder());
            draft.Packages[k] = (f, start);
            log.Add($"{f}: its own effects back to the game's colors");
        }
        draft.Packages.RemoveAll(p => before.Contains(p.File) && !beforeOwn.Contains(p.File) && !kept.Contains(p.File));
        draft.PowerColors.RemoveAll(e => e.Color.IsNone);
        return work;
    }

    /// <summary>A Powers tab entry for a character's own effects (Kurt, 2026-10-05: the Sinister clones' red glow): "own:" and
    /// the package file.</summary>
    public const string OwnPrefix = "own:";
    public static bool IsOwn(string power) => power.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The package whose character this package's class extends, when that's another NPC or enemy package of the game (Kurt,
    /// 2026-10-05: the Sinister Medal pets, MarvelAgent_CloneWolverineMedal, extend MarvelAgent_CloneWolverine: their model and
    /// the red glow are that package's; their own 37 KB holds only the class, its sounds and a spawn puff). The class
    /// export's super class (an int at byte 8 of its data, after NetIndex and the next-field link: an import) names it; the
    /// file is found in the game folder (its real name). Null when the class extends nothing of that kind.
    /// </summary>
    public static string? ParentPackage(string packagePath, string cooked)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(packagePath);
            if (stem.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) stem = stem[4..];
            if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
            var pkg = MhoPackageModifier.Package.Open(packagePath);
            int cls = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals(stem, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase));
            if (cls < 0) return null;
            byte[] d = pkg.ReadExportBytes(pkg.Exports[cls]).ToArray();
            if (d.Length < 12) return null;
            int super = BitConverter.ToInt32(d, 8);
            if (super >= 0 || -super > pkg.Imports.Length) return null;
            var imp = pkg.Imports[-super - 1];
            if (!imp.ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase)) return null;
            string parent = imp.ObjectName;
            if (!(parent.StartsWith("marvelagent_", StringComparison.OrdinalIgnoreCase) || parent.StartsWith("marvelnpc_", StringComparison.OrdinalIgnoreCase))) return null;
            string want = $"UC__{parent}_SF.upk";
            return Directory.EnumerateFiles(cooked, want).Select(Path.GetFileName).FirstOrDefault(f => f!.Equals(want, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>An NPC, enemy or team-up package: its own effects can be recolored (a team-up's hologram too).</summary>
    public static bool HasOwnEffects(string file) =>
        file.StartsWith("UC__MarvelAgent_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelNPC_", StringComparison.OrdinalIgnoreCase)
        || file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The package an own-effects recolor starts from. The mod's saved copy that an earlier recolor made: the copy kept before
    /// it (Model\color_base\&lt;file&gt;). Anything else (a Model build since, a package new to the mod, the first recolor): that
    /// package, which is kept as the new start.
    /// </summary>
    static string OwnStart(ModDraft draft, Mod? editing, string f, int k, bool recoloredBefore, string modelFolder)
    {
        string current = draft.Packages[k].Source;
        string kept = Path.Combine(modelFolder, "color_base", f);
        bool savedCopy = editing != null && Path.GetFullPath(current).StartsWith(Path.GetFullPath(editing.Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (recoloredBefore && savedCopy && File.Exists(kept)) return kept;
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        if (!Path.GetFullPath(current).Equals(Path.GetFullPath(kept), StringComparison.OrdinalIgnoreCase)) File.Copy(current, kept, true);
        return kept;
    }
}
