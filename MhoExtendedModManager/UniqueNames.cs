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
        public bool Changes => Renames.Count > 0 || Splits.Count > 0;
    }

    /// <summary>The character package kinds a model can be shared between.</summary>
    public static readonly string[] Kinds = ["MarvelPlayer", "MarvelTeamUp", "MarvelAgent", "MarvelNPC", "MarvelVanityPet"];
    static readonly string[] Classes = ["skeletalmesh", "materialinstanceconstant", "physicsasset", "material", "texture2d"];

    /// <summary>Groups every character uses as they are (base materials, effects, engine, shared animation): never renamed.</summary>
    public static bool Library(string path) => path.StartsWith("chbasematerial", StringComparison.OrdinalIgnoreCase) || path.StartsWith("vfx_", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("engine", StringComparison.OrdinalIgnoreCase) || path.StartsWith("biped_lib", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("marvelgamecontent", StringComparison.OrdinalIgnoreCase);

    static readonly object gate = new();
    static (string Folder, Dictionary<string, List<string>> Paths)? index;

    /// <summary>Path|class (lower case) → the stock character packages carrying it, for paths in more than one (built once per
    /// folder and session from the export tables: about 40 s cold, a second warm).</summary>
    public static Dictionary<string, List<string>> Shared(string folder)
    {
        lock (gate)
        {
            if (index is { } ix && ix.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) return ix.Paths;
            var files = Directory.EnumerateFiles(folder, "UC__*_SF.upk").Select(Path.GetFileName).OfType<string>()
                .Where(f => !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase))
                .Where(f => Kinds.Any(k => f.StartsWith($"UC__{k}_", StringComparison.OrdinalIgnoreCase))).ToList();
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
            {
                try
                {
                    var p = Package.Open(Path.Combine(folder, f));
                    foreach (var e in p.Exports)
                    {
                        string c = p.ClassOf(e).ToLowerInvariant();
                        if (Classes.Contains(c)) seen.GetOrAdd($"{p.PathOf(e)}|{c}", _ => []).Add(f);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            });
            var paths = seen.Where(kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            index = (folder, paths);
            return paths;
        }
    }

    /// <summary>A mod's tag for its renamed groups: letters and digits of its name, lower case, at most 20 ("jessicajonesclassicc").</summary>
    public static string Tag(string modName)
    {
        string t = new string(modName.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return t.Length == 0 ? "mod" : t[..Math.Min(20, t.Length)];
    }

    /// <summary>
    /// What Make Unique renames in a mod's package <paramref name="modPackage"/> (named <paramref name="file"/>): the content
    /// groups holding a shared object whose bytes differ from the stock copy <paramref name="stockPackage"/> (null: every
    /// shared object counts as changed). <paramref name="shipped"/>: the mod's packages (copies it ships itself don't count).
    /// </summary>
    public static Plan PlanFor(string modPackage, string? stockPackage, string file, Dictionary<string, List<string>> shared, IReadOnlySet<string> shipped, string tag)
    {
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
            found.Add((path, pkg.ClassOf(e), others));
            int top = Top(i);
            if (top == i || !pkg.ClassOf(pkg.Exports[top]).Equals("Package", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{path}: not inside a content group, left as it is"); continue; }
            if (!groups.TryGetValue(top, out var why)) groups[top] = why = [];
            why.Add($"{path} ({pkg.ClassOf(e)}; stock copy also in {string.Join(", ", others.Select(o => o[4..^7]))})");
        }
        if (groups.Count == 0) { notes.Add("nothing changed that another package also carries"); return new(file, renames, splits, notes) { Shared = found }; }
        // streamed textures (big mips in a .tfc, found by path) keep the stock path: their group is split
        var streamed = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (groups.ContainsKey(Top(i)) && pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                try { if (TextureInfo.Read(pkg, pkg.Exports[i]).Mips.Any(m => m.InSeparateFile)) streamed.Add(i); }
                catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { }
        foreach (var (g, why) in groups)
        {
            string name = pkg.Exports[g].ObjectName, to = $"{name}_{tag}";
            if (pkg.Exports.Any(x => x.OuterIndex == 0 && x.ObjectName.Equals(to, StringComparison.OrdinalIgnoreCase))) { notes.Add($"group {name}: {to} is already there (made unique before)"); continue; }
            var inGroup = streamed.Where(i => Top(i) == g).ToList();
            if (inGroup.Count == 0) { renames.Add(new(g, name, to, "holds changed shared objects")); notes.Add($"group {name} → {to}: {string.Join("; ", why)}"); continue; }
            if (inGroup.Any(i => pkg.Exports[i].OuterIndex != g + 1)) { notes.Add($"group {name}: a streamed texture sits in a subgroup; left as it is (can't split it)"); continue; }
            var moved = Enumerable.Range(0, pkg.Exports.Length).Where(j => pkg.Exports[j].OuterIndex == g + 1 && !streamed.Contains(j)).ToList();
            splits.Add(new(g, name, to, moved, inGroup.Count));
            notes.Add($"group {name}: its {inGroup.Count} streamed texture(s) keep their path; the other {moved.Count} object(s) move to {to}: {string.Join("; ", why)}");
        }
        return new(file, renames, splits, notes) { Shared = found };
    }

    /// <summary>Where the stock character packages are read: the clean game files when set, else the game's.</summary>
    public static string Folder(string cooked) => StockFiles.Clean is string c && Directory.Exists(c) ? c : cooked;

    static readonly Dictionary<string, (string Stamp, List<Plan> Plans)> plansCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod's character packages that Make Unique would change (each with its plan); cached per mod by its
    /// package files' sizes and times. Reads the mod's packages and the stock ones: call off the UI thread.</summary>
    public static List<Plan> Plans(Mod m, string cooked)
    {
        var files = m.Manifest.UpkReplacements.Where(f => Kinds.Any(k => f.StartsWith($"UC__{k}_", StringComparison.OrdinalIgnoreCase))).ToList();
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
            try { if (PlanFor(mf, StockFiles.For(cooked, f), f, shared, shipped, tag) is { Changes: true } p) plans.Add(p); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        }
        lock (plansCache) plansCache[m.Folder] = (stamp, plans);
        return plans;
    }

    /// <summary>The pill's reason, or null when nothing is exposed. From the cache only (null until <see cref="Plans"/> ran).</summary>
    public static string? Reason(Mod m)
    {
        List<Plan>? plans;
        lock (plansCache) plans = plansCache.TryGetValue(m.Folder, out var hit) ? hit.Plans : null;
        if (plans is not { Count: > 0 }) return null;
        var items = plans.SelectMany(p => p.Shared.Where(x => x.Class.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)).Select(x => (x.Path, x.Others)))
            .DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        if (items.Count == 0) items = plans.SelectMany(p => p.Shared.Select(x => (x.Path, x.Others))).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        static string Pkg(string f) => f[4..^7].Replace("Marvel", "").Replace("_", " ");
        return "Overlap: the game also has a stock copy of " + string.Join("; ", items.Select(x => $"{x.Path} (in {string.Join(", ", x.Others.Take(3).Select(Pkg))}{(x.Others.Count > 3 ? " …" : "")})"))
            + ". The game shows whichever copy loads first, so near other players, their summons and team-ups, or hub NPCs, this mod can show the stock look.";
    }

    /// <summary>
    /// Make Unique on a library mod: its exposed packages rebuilt with their own paths, saved through the editor's save (the
    /// old folder to the Recycle Bin, place, on/off, tags kept). The mod's packages then differ from what's in the game: Apply
    /// puts them in. Returns the packages changed, or null with <paramref name="error"/>.
    /// </summary>
    public static List<string>? Apply(ModLibrary lib, Mod m, string cooked, List<string> log, out string? error)
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
                File.WriteAllBytes(dst, Build(src, p));
                int k = d.Packages.FindIndex(x => x.Item1.Equals(p.File, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) d.Packages[k] = (p.File, dst);
                log.AddRange(p.Notes.Select(n => $"{p.File}: {n}"));
                done.Add(p.File);
            }
            if (ModWriter.Save(lib, d, m, out error) == null) return null;
            lock (plansCache) plansCache.Remove(m.Folder);
            return done;
        }
        catch (InvalidDataException ex) { error = ex.Message; return null; }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>The package with the plan applied, verified; throws with the problems.</summary>
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
