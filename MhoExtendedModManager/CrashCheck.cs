using AnimPackage = AnimExportCli.Packages.Package;
using AnimExportCli.Meshes;

namespace MhoExtendedModManager;

/// <summary>
/// Crash checks on a mod's character models (Kurt, 2026-10-12: users saw crashes after adding bones for a loincloth, dress or
/// wings; another modder's MH Mesh Checker spots most of them). The game looks things up by bone number in tables made for
/// one copy of a skeleton, and reads past the end of a table built for fewer bones (the Array.h:575 assertion; a crash dump
/// showed "BoneToTrackTable.Num() != NumBones" on a Nightcrawler pet mesh with 99 bones instead of 97). UE3 keeps an AnimSet's
/// bone-to-track table per mesh path, so two copies of one path with different bone counts share a table that's wrong for one
/// of them. Checked, for each skeletal mesh in the mod's packages (read only):
/// - another copy of its path, in the game's other character packages or in the mod's other packages, with fewer bones
///   (crash risk: whichever loads first builds the table; Mystique with a loincloth on Black Widow crashed, on Winter
///   Soldier, whose model is in no other package, it didn't);
/// - more bones than the game's own mesh at that path in the same file (a warning on its own: it ran on Winter Soldier);
/// - a mirror table (SkelMirrorTable) whose length isn't the bone count (crash risk: mirroring an animation reads past it);
/// - sockets on bones the mesh doesn't have (a warning: effects and props there go to the origin).
/// Own code; the rules are the Mesh Checker's findings as its notes describe them.
/// </summary>
static class CrashCheck
{
    public sealed record Issue(bool Crash, string Package, string Mesh, string Text)
    {
        /// <summary>A mirror table that doesn't match the bones (MirrorTables fixes it).</summary>
        public bool Mirror { get; init; }
    }

    /// <summary>The mod has a mirror table to fix (from the cache).</summary>
    public static bool HasMirrorIssue(Mod m) { lock (cache) return cache.TryGetValue(m.Folder, out var hit) && hit.Issues.Any(i => i.Mirror); }

    static readonly Dictionary<string, (string Stamp, List<Issue> Issues)> cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int?> stockBones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mod's issues (read from its packages and the game's; cached per mod by its files' sizes and times: call
    /// off the UI thread).</summary>
    public static List<Issue> Check(Mod m, string cooked)
    {
        var files = m.Manifest.UpkReplacements.Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(m.Folder, f))).ToList();
        string stamp = string.Join("|", files.Select(f => Path.Combine(m.Folder, f)).Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}"));
        lock (cache) if (cache.TryGetValue(m.Folder, out var hit) && hit.Stamp == stamp) return hit.Issues;
        var issues = For(files.Select(f => (f, Path.Combine(m.Folder, f))).ToList(), cooked);
        lock (cache) cache[m.Folder] = (stamp, issues);
        return issues;
    }

    /// <summary>The issues of these packages (file name, path), compared with the game's (read only).</summary>
    public static List<Issue> For(IReadOnlyList<(string File, string Path)> packages, string cooked)
    {
        var issues = new List<Issue>();
        var shared = UniqueNames.Shared(UniqueNames.Folder(cooked));
        // every skeletal mesh of the mod: package, path, bones (lower case names), sockets
        var meshes = new List<(string File, string Path, List<string> Bones, Fx.FxPkg Pkg, int Export, string FilePath)>();
        foreach (var (file, path) in packages)
        {
            Fx.FxPkg pkg; AnimPackage ap;
            try { pkg = Fx.FxPkg.Open(path); ap = AnimPackage.Open(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or UnauthorizedAccessException) { continue; }
            foreach (int i in ap.FindExportsOfClass(SkeletalMeshReader.ClassName))
            {
                if (i < 0 || i >= pkg.T.Exports.Count || !pkg.T.Exports[i].ObjectName.Equals(ap.GetExportName(i), StringComparison.OrdinalIgnoreCase)) continue;   // (the two readers' export orders agree)
                SkeletalMesh? sm;
                try { sm = SkeletalMeshReader.TryRead(ap, i); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { sm = null; }
                if (sm == null) continue;
                meshes.Add((file, pkg.T.PathOf(i + 1), sm.Bones.Select(b => b.Name.ToLowerInvariant()).ToList(), pkg, i, path));
            }
        }
        foreach (var (file, path, bones, pkg, export, filePath) in meshes)
        {
            int n = bones.Count;
            string mesh = path;
            // the game's own mesh at this path in the same file (the costume's original): more bones than it
            int? own = StockBones(StockFiles.For(cooked, file), path);
            if (own is int o && n > o)
                issues.Add(new(false, file, mesh, $"{n} bones, {n - o} more than the game's own mesh ({o}). Added bones run on most characters; they crash only where another copy of this model with fewer bones is loaded."));
            // other copies of this path: the game's other character packages, and the mod's other packages
            var fewer = new List<string>();
            if (shared.TryGetValue(path + "|skeletalmesh", out var others))
                // (a file the mod ships itself: its own copy counts, checked just below; Kurt, 2026-10-12: Rogue's mod ships
                // her illusion's package too, 220 bones in both, and was flagged against the game's 161)
                foreach (string of in others.Where(x => !x.Equals(file, StringComparison.OrdinalIgnoreCase) && !packages.Any(p => p.File.Equals(x, StringComparison.OrdinalIgnoreCase))))
                    if (StockBones(StockFiles.For(cooked, of), path) is int ob && ob < n) fewer.Add($"{Short(of)} ({ob} bones)");
            foreach (var x in meshes.Where(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && !x.File.Equals(file, StringComparison.OrdinalIgnoreCase) && x.Bones.Count < n))
                fewer.Add($"{Short(x.File)} in this mod ({x.Bones.Count} bones)");
            if (fewer.Count > 0)
                issues.Add(new(true, file, mesh, $"{n} bones, but the same model ({path}) is also in {string.Join(", ", fewer.Distinct().Take(4))}{(fewer.Count > 4 ? " …" : "")}. The game shares one animation table between copies of a model; built for the smaller one, it's too short for this one, and the game crashes (Array.h:575) when both are loaded (other players, team-ups, NPCs). Make Unique gives this model a path of its own."));
            // the mirror table and the sockets (the mesh's properties)
            var props = Fx.FxProps.Find(pkg.Bytes, pkg.T, pkg.T.Exports[export])?.Props;
            if (props?.FirstOrDefault(p => p.Name.Equals("SkelMirrorTable", StringComparison.OrdinalIgnoreCase)) is { } mt && mt.ValueAt + 4 <= pkg.Bytes.Length)
            {
                int count = BitConverter.ToInt32(pkg.Bytes, mt.ValueAt);
                if (count > 0 && count != n)
                    issues.Add(new(true, file, mesh, $"its mirror table has {count} entries for {n} bones. Mirroring an animation reads past the end of it (Array.h:575). Fix Mirror Tables gives every bone an entry.") { Mirror = true });
            }
            var sockets = Fx.FxSockets.Of(filePath, pkg.T.Exports[export].ObjectName);
            var boneSet = bones.ToHashSet();
            var lost = sockets.Where(s => !boneSet.Contains(s.Value.Bone.ToLowerInvariant())).Select(s => s.Key).ToList();
            if (lost.Count > 0)
                issues.Add(new(false, file, mesh, $"{lost.Count} socket(s) on bones it doesn't have ({string.Join(", ", lost.Take(4))}{(lost.Count > 4 ? " …" : "")}): effects and props there show at the model's feet."));
        }
        return issues;
    }

    /// <summary>The bone count of the skeletal mesh at <paramref name="path"/> in a game package (null: none there), cached.</summary>
    static int? StockBones(string? file, string path)
    {
        if (file == null || !File.Exists(file)) return null;
        string key = file + "|" + path;
        lock (stockBones) if (stockBones.TryGetValue(key, out var hit)) return hit;
        int? n = null;
        try
        {
            var pkg = Fx.FxPkg.Open(file);
            int i = pkg.Find(path);
            if (i >= 0 && pkg.T.ClassOf(pkg.T.Exports[i]).Equals(SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase)
                && SkeletalMeshReader.TryRead(AnimPackage.Open(file), i) is { } sm) n = sm.Bones.Count;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or MhoPackageModifier.PackageFormatException) { }
        lock (stockBones) stockBones[key] = n;
        return n;
    }

    static string Short(string file) => file.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && file.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase) ? file[4..^7] : file;

    /// <summary>The pill's text: the crash risks (and warnings), or null when there are none. From the cache only.</summary>
    public static string? Reason(Mod m)
    {
        List<Issue>? list;
        lock (cache) list = cache.TryGetValue(m.Folder, out var hit) ? hit.Issues : null;
        if (list is not { } l || !l.Any(i => i.Crash)) return null;
        return "Crash risk: " + string.Join("\n", l.Where(i => i.Crash).Take(3).Select(i => $"{Short(i.Package)}, {i.Mesh}: {i.Text}"));
    }

    /// <summary>All of the mod's findings as text (crash risks first), or null when there are none. From the cache only.</summary>
    public static string? Details(Mod m)
    {
        List<Issue>? list;
        lock (cache) list = cache.TryGetValue(m.Folder, out var hit) ? hit.Issues : null;
        if (list is not { Count: > 0 } l) return null;
        return string.Join("\n\n", l.OrderByDescending(i => i.Crash).Select(i => $"{(i.Crash ? "CRASH RISK" : "Warning")} · {Short(i.Package)} · {i.Mesh}: {i.Text}"));
    }
}
