using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Make Unique (Kurt, 2026-10-11; users: a modded Jessica Jones team-up and Scream on Carnage showed the stock model in hubs):
/// many stock character packages carry the same model under the same path (jessica_jones.jessicajones_tv is in her agent,
/// NPC and team-up packages; carnage.carnage in Carnage's and his team-up's; --shared-models lists 459), and the game uses
/// whichever copy loads first, so a stock copy loaded by another player's summon or a hub NPC replaces the mod's. Here a
/// mod's package gets its own paths for what it changed: each content group holding a changed shared object is renamed
/// &lt;group&gt;_&lt;tag&gt; (the tag from the mod, so two mods never share one), as costume moves do (CostumeMove.RenameAndSplit):
/// only object names are added (the name table's existing entries, used elsewhere, stay: renaming a shared name entry is
/// what hung the game for other tools), a group with streamed textures is split (those keep the stock path: the .tfc finds
/// them by it), the file, class and GUID stay.
/// </summary>
static class UniqueNames
{
    public sealed record Plan(string File, List<CostumeMove.Rename> Renames, List<CostumeMove.Split> Splits, List<string> Notes)
    {
        /// <summary>The changed shared objects (path, class) and the other stock packages that carry them.</summary>
        public List<(string Path, string Class, List<string> Others)> Shared { get; init; } = [];
        /// <summary>Changed objects left shared on purpose: their other copies are the hero's own (illusions, afterimages,
        /// summons), which show the mod's look by sharing the path. Path and those packages.</summary>
        public List<(string Path, List<string> Own)> Kept { get; init; } = [];
        public bool Changes => Renames.Count > 0 || Splits.Count > 0;
    }

    /// <summary>The character package kinds a model can be shared between.</summary>
    public static readonly string[] Kinds = ["MarvelPlayer", "MarvelTeamUp", "MarvelAgent", "MarvelNPC", "MarvelVanityPet"];
    static readonly string[] Classes = ["skeletalmesh", "materialinstanceconstant", "physicsasset", "material", "texture2d", "staticmesh", "particlesystem"];

    /// <summary>The packages Make Unique looks at: characters (Kinds) and powers (UC__Power…: Kurt, 2026-10-11, a Captain Carter
    /// mod changed vfx_taskmaster.textures.tex_taskmaster_shield in Taskmaster's Shield Bounce packages, and the boss's stock
    /// Shield Toss, which carries the same path, threw her shield).</summary>
    public static bool Covers(string file) => file.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase)
        && (file.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) || Kinds.Any(k => file.StartsWith($"UC__{k}_", StringComparison.OrdinalIgnoreCase)));

    static readonly string[] FixedLibraries = ["chbasematerial", "engine", "biped_lib", "marvelgamecontent", "vfx_shared"];
    /// <summary>Top-level groups carried by more packages than this are libraries (vfx_shared_textures, vfx_ubervfx: in
    /// thousands), never renamed. A hero's own effects group is in all its power packages (vfx_taskmaster: over 40), so only groups past 1,000 count.</summary>
    const int LibraryPackages = 1000;
    static HashSet<string> libraryGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups everything uses as they are (base materials, shared effects, engine, shared animation): never renamed.</summary>
    public static bool Library(string path)
    {
        string top = path.Split('.')[0];
        if (FixedLibraries.Any(l => top.StartsWith(l, StringComparison.OrdinalIgnoreCase))) return true;
        lock (gate) return libraryGroups.Contains(top);
    }

    sealed class Saved
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, List<string>> Paths { get; set; } = [];
        public List<string> Libraries { get; set; } = [];
    }

    static readonly object gate = new();
    static (string Folder, Dictionary<string, List<string>> Paths)? index;

    /// <summary>Path|class (lower case) → the stock character and power packages carrying it, for paths in more than one
    /// (library groups left out). Built from the export tables once per folder version (a minute or two cold) and kept in
    /// data\shared_paths.json.</summary>
    public static Dictionary<string, List<string>> Shared(string folder)
    {
        lock (gate)
        {
            if (index is { } ix && ix.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) return ix.Paths;
            var infos = new DirectoryInfo(folder).EnumerateFiles("UC__*_SF.upk")
                .Where(f => !f.Name.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase) && Covers(f.Name)).ToList();
            string stamp = $"{Path.GetFullPath(folder).ToLowerInvariant()}|{infos.Count}|{infos.Sum(f => f.Length)}|{(infos.Count == 0 ? 0 : infos.Max(f => f.LastWriteTimeUtc.Ticks))}|v4";
            string cache = Path.Combine(Settings.Home, "shared_paths.json");
            try
            {
                if (File.Exists(cache) && System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(cache)) is { } sv && sv.Stamp == stamp)
                {
                    libraryGroups = new(sv.Libraries, StringComparer.OrdinalIgnoreCase);
                    var loaded = new Dictionary<string, List<string>>(sv.Paths, StringComparer.OrdinalIgnoreCase);
                    index = (folder, loaded);
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
            var groupFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(infos, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, fi =>
            {
                try
                {
                    var p = Package.Open(fi.FullName);
                    foreach (var e in p.Exports)
                    {
                        string c = p.ClassOf(e).ToLowerInvariant();
                        if (c == "class" && e.ObjectName.StartsWith("marvelattachment_", StringComparison.OrdinalIgnoreCase))
                        { seen.GetOrAdd($"{p.PathOf(e)}|class", _ => []).Add(fi.Name); continue; }
                        if (!Classes.Contains(c)) continue;
                        string path = p.PathOf(e);
                        seen.GetOrAdd($"{path}|{c}", _ => []).Add(fi.Name);
                        groupFiles.GetOrAdd(path.Split('.')[0], _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(fi.Name, 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            });
            libraryGroups = groupFiles.Where(kv => kv.Value.Count > LibraryPackages).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = seen.Where(kv => (kv.Key.EndsWith("|class", StringComparison.OrdinalIgnoreCase) || !libraryGroups.Contains(kv.Key.Split('.')[0])) && kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            index = (folder, paths);
            try { File.WriteAllText(cache, System.Text.Json.JsonSerializer.Serialize(new Saved { Stamp = stamp, Paths = paths, Libraries = [.. libraryGroups] })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return paths;
        }
    }

    /// <summary>
    /// The packages whose copies of a hero package's models are the hero's own (a user, 2026-10-14: after Make Unique, Jean
    /// Grey's and Emma Frost's illusions and the dodge afterimages showed the stock model): what the hero's powers use or
    /// summon (PowerRecolor.HeroFiles: Jean's Telepathic Illusion agents, Strange's Projections, Fury's agents). Those load
    /// while the hero is there, so sharing the path is what shows them in the mod's look. Empty for anything but a hero's
    /// package, or without the game data. Reads the game data the first time (call off the UI thread when you can).
    /// </summary>
    public static IReadOnlySet<string> OwnFiles(string file, string cooked)
    {
        if (!file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelPlayerAudio_", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>();
        try
        {
            if (HeroOf.Package(file, cooked) is not string hero || Gui.PowerPreview.GameDb(cooked).Result is not { } db) return new HashSet<string>();
            // and every playable hero's summons and power packages (Kurt, 2026-10-19): a copy of this hero's model there is the
            // same character summoned (Cyclops' Call Jean carries All-New Jean's model; renamed for it, Jean's own illusions,
            // which share that path, lost the mod's model)
            var set = new HashSet<string>(PowerRecolor.HeroFiles(db, hero, cooked), StringComparer.OrdinalIgnoreCase);
            string sip = Path.GetFullPath(Path.Combine(cooked, "..", "..", "..", "Data", "Game", "Calligraphy.sip"));
            if (File.Exists(sip)) set.UnionWith(PowerRecolor.PlayerFiles(db, cooked, sip).Where(SummonPackage));
            return set;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or AggregateException) { return new HashSet<string>(); }
    }

    /// <summary>A summoned character's or a power's package (UC__MarvelAgent_…, UC__Power…): a copy of a hero's model there is
    /// that character summoned, kept shared with the mod's model like the hero's own copies.</summary>
    static bool SummonPackage(string file) =>
        file.StartsWith("UC__MarvelAgent_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase);

    /// <summary>A mod's tag for its renamed groups: letters and digits of its name, lower case, at most 14, then 6 hex digits
    /// of a hash of the whole name ("jessicajonescl3fa9c2"). Two mods whose names start alike (Juggernaut Classic Costume
    /// Visual Update and its "(on Unstoppable)" copy) got the same 20-letter tag before 0.37.273, so both renamed a shared
    /// model to the same new path (a user, 2026-10-14). Mods made unique before keep their old names (only shared paths are
    /// renamed, and theirs aren't shared any more).</summary>
    public static string Tag(string modName)
    {
        string t = new string(modName.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        uint h = 2166136261;
        foreach (char c in modName.Trim().ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return (t.Length == 0 ? "mod" : t[..Math.Min(14, t.Length)]) + (h & 0xFFFFFF).ToString("x6");
    }

    /// <summary>
    /// What Make Unique renames in a mod's package <paramref name="modPackage"/> (named <paramref name="file"/>): the content
    /// groups holding a shared object whose bytes differ from the stock copy <paramref name="stockPackage"/> (null: every
    /// shared object counts as changed). <paramref name="shipped"/>: the mod's packages (copies it ships itself don't count).
    /// </summary>
    /// <param name="own">Packages whose copies are the hero's own (<see cref="OwnFiles"/>): not counted, so those objects stay
    /// shared, except a skeletal mesh with more bones than the game's (sharing it with fewer bones is a crash risk).</param>
    /// <param name="cooked">The game's CookedPCConsole, for the import check (ImportIndex); null: no check.</param>
    public static Plan PlanFor(string modPackage, string? stockPackage, string file, Dictionary<string, List<string>> shared, IReadOnlySet<string> shipped, string tag, IReadOnlySet<string>? own = null, string? cooked = null)
    {
        var kept = new List<(string, List<string>)>();
        var notes = new List<string>();
        var renames = new List<CostumeMove.Rename>();
        var splits = new List<CostumeMove.Split>();
        var pkg = Package.Open(modPackage);
        var stock = stockPackage != null && File.Exists(stockPackage) ? Package.Open(stockPackage) : null;
        var stockByPath = new Dictionary<string, ExportEntry>(StringComparer.OrdinalIgnoreCase);
        if (stock != null) foreach (var e in stock.Exports) stockByPath.TryAdd($"{stock.PathOf(e)}|{stock.ClassOf(e).ToLowerInvariant()}", e);
        int Top(int i) { for (int g = 0; g < 64 && pkg.Exports[i].OuterIndex > 0; g++) i = pkg.Exports[i].OuterIndex - 1; return i; }
        var groups = new SortedDictionary<int, List<string>>();
        var found = new List<(string, string, List<string>)>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            string path = pkg.PathOf(e), key = $"{path}|{pkg.ClassOf(e).ToLowerInvariant()}";
            if (Library(path) || !shared.TryGetValue(key, out var carriers)) continue;
            var others = carriers.Where(c => !c.Equals(file, StringComparison.OrdinalIgnoreCase) && !shipped.Contains(c)).ToList();
            if (others.Count == 0) continue;   // the mod ships every copy
            bool changed = stock == null || !stockByPath.TryGetValue(key, out var se) || !pkg.ReadExportBytes(e).AsSpan().SequenceEqual(stock.ReadExportBytes(se));
            if (!changed) continue;
            // the hero's own copies (illusions, afterimages, summons) stay shared, unless the mod's mesh has more bones than
            // the game's (then a copy with fewer bones loaded beside it is a crash risk: CrashCheck)
            var mineToo = own == null || own.Count == 0 ? [] : others.Where(own.Contains).ToList();
            if (mineToo.Count > 0)
            {
                bool grew = false;
                if (pkg.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
                {
                    int? n = CrashCheck.Bones(modPackage, path), o = CrashCheck.Bones(stockPackage, path);
                    grew = n == null || o == null || n > o;
                }
                if (!grew)
                {
                    others = others.Except(mineToo, StringComparer.OrdinalIgnoreCase).ToList();
                    kept.Add((path, mineToo));
                    if (others.Count == 0) continue;
                }
                else notes.Add($"{path}: more bones than the game's model, so it can't stay shared with the hero's own copies ({string.Join(", ", mineToo.Select(o => o[4..^7]))})");
            }
            found.Add((path, pkg.ClassOf(e), others));
            int top = Top(i);
            if (top == i || !pkg.ClassOf(pkg.Exports[top]).Equals("Package", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{path}: not inside a content group, left as it is"); continue; }
            if (!groups.TryGetValue(top, out var why)) groups[top] = why = [];
            why.Add($"{path} ({pkg.ClassOf(e)}; stock copy also in {string.Join(", ", others.Select(o => o[4..^7]))})");
        }
        // Attachment classes (Kurt, 2026-10-11: the Taskmaster boss held and threw Carter's shield after the mesh was renamed):
        // a class is found by its path too, and Taskmaster's player package defines marvelattachment_taskmastershield, as the
        // boss's does; loaded first, the player's class (its default naming the mod's shield) is the one the boss uses. A shared
        // attachment class whose mesh differs from the stock class's (changed, or already renamed) gets the tag, with its default.
        var classRenames = new List<(int Class, int Default, string Name, List<string> Others)>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("marvelattachment_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!shared.TryGetValue($"{pkg.PathOf(e)}|class", out var carriers)) continue;
            // only other characters' packages (a boss, an agent, an NPC, a team-up, a pet): the hero's power packages define
            // the same class on purpose (Taskmaster's Basic Shot and Coup de Grace: his guns), and resolve to the hero's
            var others = carriers.Where(c => !c.Equals(file, StringComparison.OrdinalIgnoreCase) && !shipped.Contains(c)
                && !c.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)).ToList();
            if (others.Count == 0) continue;
            string dname = "default__" + e.ObjectName;
            int d = Array.FindIndex(pkg.Exports, x => x.ObjectName.Equals(dname, StringComparison.OrdinalIgnoreCase) && x.OuterIndex == e.OuterIndex);
            if (d < 0) continue;
            var mine = MeshOf(pkg, d);
            int sd = stock == null ? -1 : Array.FindIndex(stock.Exports, x => x.ObjectName.Equals(dname, StringComparison.OrdinalIgnoreCase));
            var theirs = sd >= 0 ? MeshOf(stock!, sd) : null;
            bool differs = mine == null || theirs == null || !mine.Value.Path.Equals(theirs.Value.Path, StringComparison.OrdinalIgnoreCase) || !mine.Value.Bytes.AsSpan().SequenceEqual(theirs.Value.Bytes);
            if (differs) classRenames.Add((i, d, e.ObjectName, others));
        }
        foreach (var (c, d, name, others) in classRenames)
        {
            if (pkg.Exports.Any(x => x.ObjectName.Equals($"{name}_{tag}", StringComparison.OrdinalIgnoreCase))) continue;
            renames.Add(new(c, pkg.PathOf(pkg.Exports[c]), $"{name}_{tag}", "attachment class shared with " + string.Join(", ", others.Select(o => o[4..^7]))));
            renames.Add(new(d, pkg.PathOf(pkg.Exports[d]), $"default__{name}_{tag}", "its default"));
            notes.Add($"class {name} → {name}_{tag}: its mesh isn't the stock one, and {string.Join(", ", others.Select(o => o[4..^7]))} define the same class");
            found.Add((pkg.PathOf(pkg.Exports[c]), "Class", others));
        }
        foreach (var (kp, ko) in kept.DistinctBy(k => k.Item1, StringComparer.OrdinalIgnoreCase))
            notes.Add($"{kp}: kept shared with the hero's own copies and summons: {string.Join(", ", ko.Select(o => o[4..^7]))} (they show the mod's look)");
        if (groups.Count == 0) { if (classRenames.Count == 0) notes.Add("nothing changed that another package also carries"); return new(file, renames, splits, notes) { Shared = found, Kept = kept }; }
        // streamed textures (big mips in a .tfc, found by path) keep the stock path: their group is split
        var streamed = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (groups.ContainsKey(Top(i)) && pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                try { if (TextureInfo.Read(pkg, pkg.Exports[i]).Mips.Any(m => m.InSeparateFile)) streamed.Add(i); }
                catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { }
        // An object another game file imports by path, that no other package exports, must keep its path: renamed, the
        // import finds nothing (2026-10-14: Nova's Richard Rider effects import their materials from the base effect package;
        // moved, they drew black). A group holding one is left as it is.
        var importIx = cooked != null ? ImportIndex.Imported(Folder(cooked)) : null;
        string? Breaks(int g)
        {
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                if (Top(i) != g || i == g) continue;
                string key = $"{pkg.PathOf(pkg.Exports[i])}|{pkg.ClassOf(pkg.Exports[i]).ToLowerInvariant()}";
                if (importIx == null || shared.ContainsKey(key) || !importIx.TryGetValue(key, out var by)) continue;
                var outside = by.Where(f => !f.Equals(file, StringComparison.OrdinalIgnoreCase) && !shipped.Contains(f)).ToList();
                if (outside.Count > 0) return $"{pkg.PathOf(pkg.Exports[i])} is imported by {string.Join(", ", outside.Take(3).Select(o => o[4..^7]))}{(outside.Count > 3 ? " …" : "")} and no other file has it";
            }
            return null;
        }
        foreach (var (g, why) in groups)
        {
            string name = pkg.Exports[g].ObjectName, to = $"{name}_{tag}";
            if (Breaks(g) is string br) { notes.Add($"group {name}: left as it is, renaming it would break an import ({br})"); continue; }
            if (pkg.Exports.Any(x => x.OuterIndex == 0 && x.ObjectName.Equals(to, StringComparison.OrdinalIgnoreCase))) { notes.Add($"group {name}: {to} is already there (made unique before)"); continue; }
            var inGroup = streamed.Where(i => Top(i) == g).ToList();
            if (inGroup.Count == 0) { renames.Add(new(g, name, to, "holds changed shared objects")); notes.Add($"group {name} → {to}: {string.Join("; ", why)}"); continue; }
            if (inGroup.Any(i => pkg.Exports[i].OuterIndex != g + 1)) { notes.Add($"group {name}: a streamed texture sits in a subgroup; left as it is (can't split it)"); continue; }
            var moved = Enumerable.Range(0, pkg.Exports.Length).Where(j => pkg.Exports[j].OuterIndex == g + 1 && !streamed.Contains(j)).ToList();
            splits.Add(new(g, name, to, moved, inGroup.Count));
            notes.Add($"group {name}: its {inGroup.Count} streamed texture(s) keep their path; the other {moved.Count} object(s) move to {to}: {string.Join("; ", why)}");
        }
        return new(file, renames, splits, notes) { Shared = found, Kept = kept };
    }

    /// <summary>The first ModelMesh of an attachment class default (path, bytes), or null.</summary>
    static (string Path, byte[] Bytes)? MeshOf(Package pkg, int defaultExport)
    {
        try
        {
            byte[] b = pkg.ReadExportBytes(pkg.Exports[defaultExport]).ToArray();
            var t = TagWalker.Walk(pkg, b, 4)?.FirstOrDefault(x => x.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase));
            if (t == null || t.Size < 8) return null;
            int r = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt + 4));
            if (r <= 0 || r > pkg.Exports.Length) return null;
            return (pkg.PathOf(pkg.Exports[r - 1]), pkg.ReadExportBytes(pkg.Exports[r - 1]).ToArray());
        }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Where the stock character packages are read: the clean game files when set, else the game's.</summary>
    public static string Folder(string cooked) => StockFiles.Clean is string c && Directory.Exists(c) ? c : cooked;

    static readonly Dictionary<string, (string Stamp, List<Plan> Plans)> plansCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod's character packages that Make Unique would change (each with its plan); cached per mod by its
    /// package files' sizes and times. Reads the mod's packages and the stock ones: call off the UI thread.</summary>
    public static List<Plan> Plans(Mod m, string cooked)
    {
        var files = m.Manifest.UpkReplacements.Where(Covers).ToList();
        if (files.Count == 0) return [];
        string stamp = string.Join("|", files.Select(f => Path.Combine(m.Folder, f)).Where(File.Exists).Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        lock (plansCache) if (plansCache.TryGetValue(m.Folder, out var hit) && hit.Stamp == stamp) return hit.Plans;
        var shared = Shared(Folder(cooked));
        var shipped = m.Manifest.UpkReplacements.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string tag = Tag(m.Name);
        var plans = new List<Plan>();
        foreach (string f in files)
        {
            string mf = Path.Combine(m.Folder, f);
            if (!File.Exists(mf)) continue;
            try { if (PlanFor(mf, StockFiles.For(cooked, f), f, shared, shipped, tag, OwnFiles(f, cooked), cooked) is { Changes: true } p) plans.Add(p); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        }
        lock (plansCache) plansCache[m.Folder] = (stamp, plans);
        return plans;
    }

    /// <summary>
    /// What Make Unique changes for the characters around this mod, in plain words (a user, 2026-10-14: "would I have broken
    /// anything by making them unique?"). Lose: the files whose copies of the mod's changed models keep the game's look after
    /// it (other characters, NPCs, team-ups). Keep: the hero's own copies left shared (they go on showing the mod's look).
    /// From the cache only (empty until <see cref="Plans"/> ran).
    /// </summary>
    public static (List<string> Lose, List<string> Keep) Impact(Mod m)
    {
        List<Plan>? plans;
        lock (plansCache) plans = plansCache.TryGetValue(m.Folder, out var hit) ? hit.Plans : null;
        if (plans == null) return ([], []);
        static string Name(string f) => f.Length > 11 && f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) ? f[4..^7].Replace("Marvel", "").Replace("_", " ").Trim() : f;
        var lose = plans.SelectMany(p => p.Shared.SelectMany(s => s.Others)).Distinct(StringComparer.OrdinalIgnoreCase).Select(Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var keep = plans.SelectMany(p => p.Kept.SelectMany(k => k.Own)).Distinct(StringComparer.OrdinalIgnoreCase).Select(Name).Where(n => !lose.Contains(n, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return (lose, keep);
    }

    /// <summary>The mod's changed shared objects for a window: path and the other files' names (models first, then the rest),
    /// at most <paramref name="max"/>. From the cache only.</summary>
    public static List<(string Path, List<string> Others)> Items(Mod m, int max = 6)
    {
        List<Plan>? plans;
        lock (plansCache) plans = plansCache.TryGetValue(m.Folder, out var hit) ? hit.Plans : null;
        if (plans == null) return [];
        static string Name(string f) => f.Length > 11 && f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) ? f[4..^7].Replace("Marvel", "").Replace("_", " ").Trim() : f;
        return [.. plans.SelectMany(p => p.Shared).OrderBy(s => s.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).Take(max).Select(s => (s.Path, s.Others.Select(Name).ToList()))];
    }

    /// <summary>The pill's reason, or null when nothing is exposed. From the cache only (null until <see cref="Plans"/> ran).</summary>
    /// <param name="evenKept">Also for a mod whose author keeps its paths shared (Keep Shared Paths: no OVERLAP warning,
    /// no Make Unique offered; the CRASH RISK window still offers it, as a crash outweighs the choice).</param>
    public static string? Reason(Mod m, bool evenKept = false)
    {
        if (!evenKept && m.Manifest.KeepShared == true) return null;
        List<Plan>? plans;
        lock (plansCache) plans = plansCache.TryGetValue(m.Folder, out var hit) ? hit.Plans : null;
        if (plans is not { Count: > 0 }) return null;
        var items = plans.SelectMany(p => p.Shared.Where(x => x.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)).Select(x => (x.Path, x.Others)))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        if (items.Count == 0) items = plans.SelectMany(p => p.Shared.Select(x => (x.Path, x.Others))).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        static string Pkg(string f) => f[4..^7].Replace("Marvel", "").Replace("_", " ");
        return "Overlap: The game also has a stock copy of " + string.Join("; ", items.Select(x => $"{x.Path} (in {string.Join(", ", x.Others.Take(3).Select(Pkg))}{(x.Others.Count > 3 ? " …" : "")})"))
            + ". The game shows whichever copy loads first, so near other players, their summons and team-ups, or hub NPCs, this mod can show the stock look.";
    }

    /// <summary>The mod's folder for Make Unique's undo copies: each package as it was before its first rename (and after
    /// later edits, as it was before the latest one), with the SHA-256 of the renamed package beside it (Kurt, 2026-10-14: a
    /// way back to the shared paths when unique causes a problem).</summary>
    public const string BeforeFolder = "Before Unique";

    /// <summary>The mod's undo copies: package file, the copy's path, the renamed package's SHA-256.</summary>
    public static List<(string File, string Source, string After)> BeforeCopies(Mod m)
    {
        string dir = Path.Combine(m.Folder, BeforeFolder);
        if (!Directory.Exists(dir)) return [];
        var list = new List<(string, string, string)>();
        foreach (string f in Directory.EnumerateFiles(dir, "*.upk"))
        {
            string file = Path.GetFileName(f), sum = f + ".sha256";
            if (!m.Manifest.UpkReplacements.Contains(file, StringComparer.OrdinalIgnoreCase) || !File.Exists(sum)) continue;
            list.Add((file, f, File.ReadAllText(sum).Trim()));
        }
        return list;
    }

    public static string Sha(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)); }
    public static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>The undo copies that still apply: the mod's package is still exactly what Make Unique wrote. The others
    /// (changed since by a Model build, a color, an update) are listed with <paramref name="changed"/>.</summary>
    public static List<(string File, string Source)> Undoable(Mod m, out List<string> changed)
    {
        changed = [];
        var ok = new List<(string, string)>();
        foreach (var (file, src, after) in BeforeCopies(m))
        {
            string live = Path.Combine(m.Folder, file);
            if (File.Exists(live) && Sha(live).Equals(after, StringComparison.OrdinalIgnoreCase)) ok.Add((file, src));
            else changed.Add(file);
        }
        return ok;
    }

    /// <summary>
    /// Undo Make Unique: the packages back to their copies from before the rename, the mod marked Keep Shared Paths (so the
    /// next Save doesn't rename them again), saved like an edit. Returns the packages put back, or null with the error.
    /// </summary>
    public static List<string>? Undo(ModLibrary lib, Mod m, out string? error)
    {
        error = null;
        var files = Undoable(m, out _);
        if (files.Count == 0) { error = "Nothing to undo: no package is still as Make Unique left it."; return null; }
        var d = ModDraft.From(m);
        foreach (var (file, src) in files)
        {
            int k = d.Packages.FindIndex(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) d.Packages[k] = (file, src);
            d.BeforeUnique.Remove(file);
        }
        d.KeepShared = true;
        if (ModWriter.Save(lib, d, m, out error) == null) return null;
        lock (plansCache) plansCache.Remove(m.Folder);
        return [.. files.Select(f => f.File)];
    }

    /// <summary>
    /// Make Unique on a library mod: its exposed packages rebuilt with their own paths, saved through the editor's save (the
    /// old folder to the Recycle Bin, place, on/off, tags kept). The mod's packages then differ from what's in the game: Apply
    /// puts them in. Returns the packages changed, or null with <paramref name="error"/>.
    /// </summary>
    public static List<string>? Apply(ModLibrary lib, Mod m, string cooked, List<string> log, out string? error, string? copyName = null)
    {
        error = null;
        var plans = Plans(m, cooked);
        if (plans.Count == 0) { error = "Nothing to change: no changed model of this mod is in another stock package."; return null; }
        string work = Path.Combine(lib.DataFolder, "make-unique-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var d = ModDraft.From(m);
            var done = new List<string>();
            foreach (var p in plans)
            {
                string src = Path.Combine(m.Folder, p.File), dst = Path.Combine(work, p.File);
                byte[] built = Build(src, p);
                File.WriteAllBytes(dst, built);
                if (copyName == null) d.BeforeUnique[p.File] = (src, Sha(built));   // (the undo copy: the package before this rename)
                int k = d.Packages.FindIndex(x => x.Item1.Equals(p.File, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) d.Packages[k] = (p.File, dst);
                log.AddRange(p.Notes.Select(n => $"{p.File}: {n}"));
                done.Add(p.File);
            }
            if (copyName != null) d.Name = copyName;   // a new mod; the original kept as it is
            d.KeepShared = false;   // (made unique on purpose: Save may rename again later)
            if (ModWriter.Save(lib, d, copyName != null ? null : m, out error) == null) return null;
            lock (plansCache) plansCache.Remove(m.Folder);
            return done;
        }
        catch (InvalidDataException ex) { error = ex.Message; return null; }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>The package with the plan applied, verified; throws with the problems.</summary>
    /// <summary>
    /// Groups an earlier Make Unique renamed that today's rules keep shared, put back under their game names (Kurt,
    /// 2026-10-19: Ultimate Jean Grey was made unique before 0.50.18, when Cyclops' Call Jean still counted against sharing;
    /// her illusions kept the stock model, and the mod had no copy from before to go back to). A renamed group is a top-level
    /// group "&lt;game group&gt;_&lt;tag&gt;" (the stock package has the game group, not this name). It goes back when every object in
    /// it that other game files carry is carried only by the hero's own copies and summons (<paramref name="own"/>) or the
    /// mod's own packages, and no skeletal mesh in it has more bones than the game's. Renamed: named back; split (its streamed
    /// textures stayed in the game group): its objects move back into that group, the emptied group stays. Attachment
    /// classes stay as they are. Returns the package bytes (verified) and what went back, or null when nothing does.
    /// </summary>
    public static byte[]? Reshare(string modPackage, string? stockPackage, string file, Dictionary<string, List<string>> shared, IReadOnlySet<string> shipped,
        IReadOnlySet<string> own, string modName, List<string> notes)
    {
        // only this mod's own Make Unique tags (today's, and the 20-letter one before 0.37.273): a group like storm_astonishing
        // is a real game group, not a rename
        string letters = new string(modName.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Tag(modName), letters.Length == 0 ? "mod" : letters[..Math.Min(20, letters.Length)] };
        if (stockPackage == null || !File.Exists(stockPackage) || own.Count == 0) return null;
        var pkg = Package.Open(modPackage);
        var stock = Package.Open(stockPackage);
        var stockGroups = stock.Exports.Where(e => e.OuterIndex == 0 && stock.ClassOf(e).Equals("Package", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Top(int i) { for (int g = 0; g < 64 && pkg.Exports[i].OuterIndex > 0; g++) i = pkg.Exports[i].OuterIndex - 1; return i; }
        var renames = new Dictionary<int, string>();
        var outers = new Dictionary<int, int>();
        for (int g = 0; g < pkg.Exports.Length; g++)
        {
            var ge = pkg.Exports[g];
            if (ge.OuterIndex != 0 || !pkg.ClassOf(ge).Equals("Package", StringComparison.OrdinalIgnoreCase) || stockGroups.Contains(ge.ObjectName)) continue;
            int cut = ge.ObjectName.LastIndexOf('_');
            if (cut <= 0) continue;
            string game = ge.ObjectName[..cut], suffix = ge.ObjectName[(cut + 1)..];
            if (!stockGroups.Contains(game) || !tags.Contains(suffix)) continue;
            // every object under it, at its game path: shared only with the hero's own copies and summons?
            bool back = true; int objects = 0;
            for (int i = 0; i < pkg.Exports.Length && back; i++)
            {
                if (i == g || Top(i) != g) continue;
                objects++;
                string path = game + pkg.PathOf(pkg.Exports[i])[ge.ObjectName.Length..], cls = pkg.ClassOf(pkg.Exports[i]);
                if (!shared.TryGetValue($"{path}|{cls.ToLowerInvariant()}", out var carriers)) continue;
                if (carriers.Any(c => !c.Equals(file, StringComparison.OrdinalIgnoreCase) && !shipped.Contains(c) && !own.Contains(c))) back = false;
                else if (cls.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                    && (CrashCheck.Bones(modPackage, pkg.PathOf(pkg.Exports[i])) is not int n || CrashCheck.Bones(stockPackage, path) is not int o || n > o)) back = false;
            }
            if (!back || objects == 0) continue;
            int gameGroup = Array.FindIndex(pkg.Exports, e => e.OuterIndex == 0 && e.ObjectName.Equals(game, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(e).Equals("Package", StringComparison.OrdinalIgnoreCase));
            if (gameGroup < 0) { renames[g] = game; notes.Add($"group {ge.ObjectName} → {game} again: today's rules keep it shared (the hero's own copies and summons)"); }
            else
            {
                for (int i = 0; i < pkg.Exports.Length; i++) if (pkg.Exports[i].OuterIndex == g + 1) outers[i] = gameGroup + 1;
                notes.Add($"group {ge.ObjectName}: its objects go back into {game}: today's rules keep it shared (the hero's own copies and summons)");
            }
        }
        if (renames.Count == 0 && outers.Count == 0) return null;
        var addNames = renames.Values.Where(n => !pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), [], out var data, addNames, null, renames, outers);
        var problems = PackageRebuilder.Verify(pkg, output, [], [], data, addNames, null, renames, outers);
        var check = Package.FromBytes(output);
        if (!output.AsSpan(check.GenerationsAt - 16, 16).SequenceEqual(pkg.RawFile.AsSpan(pkg.GenerationsAt - 16, 16))) problems.Add("the package's GUID changed");
        if (problems.Count > 0) throw new InvalidDataException($"{file}: " + string.Join("; ", problems.Take(5)));
        return output;
    }

    public static byte[] Build(string modPackage, Plan plan)
    {
        var pkg = Package.Open(modPackage);
        var problems = new List<string>();
        byte[] output = CostumeMove.RenameAndSplit(pkg, plan.Renames.ToDictionary(r => r.Export, r => r.NewName), plan.Splits, problems);
        var back = Package.FromBytes(output);
        if (!output.AsSpan(back.GenerationsAt - 16, 16).SequenceEqual(pkg.RawFile.AsSpan(pkg.GenerationsAt - 16, 16))) problems.Add("the package's GUID changed");
        if (problems.Count > 0) throw new InvalidDataException($"{plan.File}: " + string.Join("; ", problems.Take(5)));
        return output;
    }
}
