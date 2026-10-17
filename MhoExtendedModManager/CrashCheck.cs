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
        // Streamed textures the game's texture cache can't find (2026-10-17: a costume move put Emma Frost's material_pass
        // textures at a new path; their big mips are in CharTextures.tfc, listed by the old path, and Diamond Form hung the
        // game waiting for them, then crashed). Found by path in TextureFileCacheManifest.bin.
        var listed = TfcPaths(cooked);
        if (listed.Count > 0)
            foreach (var (file, path) in packages)
            {
                MhoPackageModifier.Package mp;
                try { mp = MhoPackageModifier.Package.Open(path); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or UnauthorizedAccessException) { continue; }
                var lost = new List<string>();
                foreach (var e in mp.Exports)
                {
                    if (!mp.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                    // only a texture a costume move renamed: its path's group is <group>_on_<target>, and the cache lists it
                    // under the original group (the game's own files have streamed textures the cache doesn't list at all)
                    string tp = mp.PathOf(e);
                    var mo = System.Text.RegularExpressions.Regex.Match(tp, @"^([^.]+)_on_[a-z0-9]+\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!mo.Success || listed.Contains(tp) || !listed.Contains(mo.Groups[1].Value + tp[(mo.Length - 1)..])) continue;
                    try { if (MhoPackageModifier.TextureInfo.Read(mp, e).Mips.Any(x => x.InSeparateFile)) lost.Add(tp); }
                    catch (Exception ex) when (ex is MhoPackageModifier.PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { }
                }
                if (lost.Count > 0)
                    issues.Add(new(true, file, lost[0], $"{lost.Count} texture(s) stream their larger sizes from the game's texture cache, but under a path the cache doesn't list ({string.Join(", ", lost.Take(3))}{(lost.Count > 3 ? " …" : "")}). The game hangs, then crashes, when it needs them (Diamond Form on a moved Emma Frost). If Move to Another Costume made this mod, move the original again: this version keeps those textures' paths."));
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

    static readonly Dictionary<string, HashSet<string>> tfcPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every texture path TextureFileCacheManifest.bin lists (cached per folder); empty when it can't be read.</summary>
    static HashSet<string> TfcPaths(string cooked)
    {
        lock (tfcPaths)
        {
            if (tfcPaths.TryGetValue(cooked, out var hit)) return hit;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var e in MhoPackageModifier.TfcCache.All(cooked)) set.Add(e.Path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { set.Clear(); }
            return tfcPaths[cooked] = set;
        }
    }

    static readonly Dictionary<string, List<(string Path, int Bones)>> fileMeshes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A package's skeletal meshes (path, bones), cached by the file's size and time.</summary>
    static List<(string Path, int Bones)> MeshesIn(string file)
    {
        var fi = new FileInfo(file);
        string key = $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        lock (fileMeshes) if (fileMeshes.TryGetValue(key, out var hit)) return hit;
        var list = new List<(string, int)>();
        try
        {
            var pkg = Fx.FxPkg.Open(file); var ap = AnimPackage.Open(file);
            foreach (int i in ap.FindExportsOfClass(SkeletalMeshReader.ClassName))
            {
                if (i < 0 || i >= pkg.T.Exports.Count || !pkg.T.Exports[i].ObjectName.Equals(ap.GetExportName(i), StringComparison.OrdinalIgnoreCase)) continue;
                SkeletalMesh? sm;
                try { sm = SkeletalMeshReader.TryRead(ap, i); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { sm = null; }
                if (sm != null) list.Add((pkg.T.PathOf(i + 1), sm.Bones.Count));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or UnauthorizedAccessException) { }
        lock (fileMeshes) fileMeshes[key] = list;
        return list;
    }

    /// <summary>
    /// The turned-on mods checked together (a user, 2026-10-14: a crash after making a couple dozen mods unique; each mod's
    /// own check only compares it with the game's files). For the file each package name puts in the game (the
    /// highest-priority turned-on mod's), every model it carries is compared with the other copies of that path: other
    /// turned-on mods' files, and the game's own files no mod replaces. Findings: two copies with different bone counts where
    /// at least one is a mod's (a crash risk, Array.h:575: the game shares one animation table per path), and two different
    /// mods carrying the same path (whichever loads first shows: one of them loses its look). Copies within one mod are its
    /// author's choice (Rogue's mod ships her illusion's file too) and only count against other files. Read only; the first
    /// run reads every turned-on package (cached by file after that).
    /// </summary>
    /// <summary>One finding of <see cref="Together"/>: the model's path and its copies (file, the mod's name or null for the
    /// game's own, bones). Crash: copies with different bone counts; else two mods that both changed it.</summary>
    public sealed record Finding(bool Crash, string Model, List<(string File, string? Mod, int Bones)> Copies);

    public static List<Finding> Together(ModLibrary lib, string cooked)
    {
        var winners = lib.PackageWinners().Where(w => w.Key.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase) && UniqueNames.Covers(w.Key)).ToList();
        var shared = UniqueNames.Shared(UniqueNames.Folder(cooked));
        var copies = new Dictionary<string, List<(string File, Mod? Mod, int Bones)>>(StringComparer.OrdinalIgnoreCase);
        var replaced = winners.Select(w => w.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, mod) in winners)
        {
            string path = Path.Combine(mod.Folder, file);
            if (!File.Exists(path)) continue;
            foreach (var (mp, bones) in MeshesIn(path))
            {
                if (!copies.TryGetValue(mp, out var list)) copies[mp] = list = [];
                list.Add((file, mod, bones));
            }
        }
        var found = new List<Finding>();
        foreach (var (mp, list) in copies.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            // the game's own copies of this path in files no turned-on mod replaces
            var all = list.ToList();
            if (shared.TryGetValue(mp + "|skeletalmesh", out var stockFiles))
                foreach (string sf in stockFiles.Where(f => !replaced.Contains(f)))
                    if (StockBones(StockFiles.For(cooked, sf), mp) is int sb) all.Add((sf, null, sb));
            // different bone counts with a mod's copy involved (a mod's copies among themselves are fine when they agree)
            var crash = all.Where(a => a.Mod != null).SelectMany(a => all.Where(b => b.Bones != a.Bones && !(b.Mod == a.Mod && b.File == a.File)).Select(b => (a, b))).ToList();
            if (crash.Count > 0)
            {
                var involved = crash.SelectMany(p => new[] { p.a, p.b }).DistinctBy(c => c.File, StringComparer.OrdinalIgnoreCase).ToList();
                found.Add(new(true, mp, [.. involved.OrderByDescending(c => c.Bones).Select(c => (c.File, c.Mod?.Name, c.Bones))]));
                continue;
            }
            // two mods that both changed it (a copy the same as the game's in its own file is no conflict)
            var mods = list.Where(c => Changed(c.Mod!, c.File, mp, cooked)).Select(c => c.Mod).Distinct().ToList();
            if (mods.Count > 1)
                found.Add(new(false, mp, [.. list.Where(c => mods.Contains(c.Mod)).Select(c => (c.File, c.Mod?.Name, c.Bones))]));
        }
        return found;
    }

    /// <summary>The mod's copy of the model at <paramref name="path"/> in <paramref name="file"/> differs from the game's own
    /// copy in that file (or the game's file has none).</summary>
    static bool Changed(Mod mod, string file, string path, string cooked)
    {
        try
        {
            string mine = Path.Combine(mod.Folder, file);
            string? stock = StockFiles.For(cooked, file);
            if (stock == null || !File.Exists(stock)) return true;
            var a = MhoPackageModifier.Package.Open(mine); var b = MhoPackageModifier.Package.Open(stock);
            var ea = a.Exports.FirstOrDefault(e => a.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
            var eb = b.Exports.FirstOrDefault(e => b.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
            return ea == null || eb == null || !a.ReadExportBytes(ea).AsSpan().SequenceEqual(b.ReadExportBytes(eb));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { return true; }
    }

    /// <summary>The bone count of the skeletal mesh at <paramref name="path"/> in a package (null: none there), cached by the
    /// file's size and time.</summary>
    public static int? Bones(string? file, string path) => StockBones(file, path);

    /// <summary>The bone count of the skeletal mesh at <paramref name="path"/> in a game package (null: none there), cached.</summary>
    static int? StockBones(string? file, string path)
    {
        if (file == null || !File.Exists(file)) return null;
        var fi = new FileInfo(file);
        string key = $"{file}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{path}";
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

    internal static string Short(string file) => file.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && file.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase) ? file[4..^7] : file;

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
        var sb = new System.Text.StringBuilder();
        foreach (var (crash, title) in new[] { (true, "CRASH RISK"), (false, "WARNINGS") })
        {
            var part = l.Where(i => i.Crash == crash).ToList();
            if (part.Count == 0) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(title);
            foreach (var i in part)
            {
                sb.AppendLine($"  • {Short(i.Package)} · {i.Mesh}");
                sb.AppendLine($"    {char.ToUpperInvariant(i.Text[0])}{i.Text[1..]}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
