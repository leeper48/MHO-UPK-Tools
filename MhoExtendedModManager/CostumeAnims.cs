using AnimExportCli.Animation;
using MhoPackageModifier;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoExtendedModManager;

/// <summary>
/// The animations a costume plays, as the game finds them (Kurt, 2026-10-02: an Animations tab, to swap animations
/// between characters). A character's mesh component (the class default's initialskeletalmesh) lists AnimSets; an
/// animation is looked up by name from the last set in that list to the first, so a later set overrides an earlier one.
/// Stock costumes use this: Storm Astonishing's own set (19 sequences: idle, fidgets, emotes) comes after Storm's base
/// sets and replaces those animations for that costume only (census 2026-10-02: 120 of 562 character packages carry
/// their own set in their list). Sets in the list are exports of the package itself, or imports: the hero's base package
/// (UC__MarvelPlayer_&lt;Hero&gt;_SF) and always-loaded ones (blink_as, interactive_as).
/// </summary>
sealed class CostumeAnims
{
    public enum Source { Costume, Hero, Shared, Other }

    /// <summary>An AnimSet in the component's list, in its place (Order 0 = first = lowest priority).</summary>
    public sealed record Set(int Order, string Path, string PackageName, string? File, int Export, Source Kind, bool FromMod,
        IReadOnlyList<string> TrackBoneNames, IReadOnlySet<string>? TranslationBones, IReadOnlyList<(string Name, int Export)> Sequences)
    {
        public bool Found => Export >= 0;
        /// <summary>"Storm Base Set", "This Costume's Set" …, for the list.</summary>
        public string Label => Kind switch
        {
            Source.Costume => "This Costume",
            Source.Hero => "Hero's Base",
            Source.Shared => "Shared",
            _ => PackageName,
        };
    }

    /// <summary>An animation the costume plays: its name, the set it comes from, and the sets it overrides there.</summary>
    public sealed record Anim(string Name, Set From, int Export, IReadOnlyList<Set> Overrides)
    {
        public AnimRef Ref => new(From.PackageName + ".upk", From.File!, Name, Export, From.TrackBoneNames) { TranslationBones = From.TranslationBones };
    }

    public required string PackageFile { get; init; }
    public required string Class { get; init; }
    public required IReadOnlyList<Set> Sets { get; init; }
    public required IReadOnlyList<Anim> Anims { get; init; }
    /// <summary>True when the costume sets no list of its own and plays its hero's (the base package's component list).</summary>
    public bool Inherited { get; init; }

    /// <summary>
    /// Reads a character package's animations. <paramref name="packagePath"/> is the file on disk, <paramref name="fileFor"/>
    /// gives another package's file by its name (the mod's copy, else the game's) and whether it's the mod's. Null when the
    /// package has no character mesh component with an AnimSets list.
    /// </summary>
    /// <summary>
    /// A class's alternate animation sets by name (2026-10-04, a user: Jean Grey's Phoenix power animations weren't listed): the
    /// class default's <c>AnimationSetAliases</c> (alias → AnimSet; Jean: phoenixas → jeangrey_phoenixform_as, darkphoenixas →
    /// phoenix_as). Powers and forms switch to a set by its alias at run time, so the mesh's AnimSets list never names it.
    /// Set paths are the export's path in this package, or "import:" + the import's path. Empty when the class has none.
    /// </summary>
    public static List<(string Alias, string SetPath)> Aliases(Package pkg, string className)
    {
        var res = new List<(string, string)>();
        int def = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals($"marvelgamecontent.default__{className}", StringComparison.OrdinalIgnoreCase));
        if (def < 0) return res;
        byte[] d = pkg.ReadExportBytes(pkg.Exports[def]).ToArray();
        var tag = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(t => t.Name.Equals("AnimationSetAliases", StringComparison.OrdinalIgnoreCase));
        if (tag == null) return res;
        int n = BitConverter.ToInt32(d, tag.ValueAt), at = tag.ValueAt + 4;
        for (int k = 0; k < n && at < tag.End; k++)
        {
            var inner = TagWalker.Walk(pkg, d, at);
            if (inner == null) break;
            string alias = inner.FirstOrDefault(t => t.Name.Equals("Alias", StringComparison.OrdinalIgnoreCase)) is { } al ? TagWalker.NameAt(pkg, d, al.ValueAt) : "";
            string set = "";
            if (inner.FirstOrDefault(t => t.Name.Equals("AnimSet", StringComparison.OrdinalIgnoreCase)) is { } st)
            {
                int r = BitConverter.ToInt32(d, st.ValueAt);
                set = r > 0 ? pkg.PathOf(pkg.Exports[r - 1]) : r < 0 ? "import:" + pkg.Imports[-r - 1].ObjectName : "";
            }
            res.Add((alias, set));
            at = inner.NoneAt + 8;
        }
        return res;
    }

    public static CostumeAnims? Read(string packagePath, string packageFile, Func<string, (string Path, bool FromMod)?> fileFor, string? className = null)
    {
        Package pkg;
        try { pkg = Package.Open(packagePath); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
        string own = Path.GetFileNameWithoutExtension(packageFile);
        string heroBase = own.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? $"UC__MarvelPlayer_{own["UC__MarvelPlayer_".Length..].Split('_')[0]}_SF" : "";
        string cls = className ?? (own.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && own.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? own[4..^3] : own);
        // A costume that doesn't set AnimSets on its component inherits its hero's (the costume class extends the hero's,
        // whose class default component in the base package is the template): 374 of 562 stock character packages.
        CostumeAnims? Inherit()
        {
            if (heroBase.Length == 0 || heroBase.Equals(own, StringComparison.OrdinalIgnoreCase) || fileFor(heroBase) is not { } hb) return null;
            var h = Read(hb.Path, heroBase + ".upk", fileFor);
            if (h == null) return null;
            var sets = h.Sets.Select(x => x.Kind == Source.Costume ? x with { Kind = Source.Hero } : x).ToList();
            var map = h.Sets.Zip(sets).ToDictionary(z => z.First, z => z.Second);
            return new CostumeAnims
            {
                PackageFile = packageFile, Class = cls, Sets = sets, Inherited = true,
                Anims = h.Anims.Select(a => a with { From = map[a.From], Overrides = a.Overrides.Select(o => map[o]).ToList() }).ToList(),
            };
        }
        // The class's component; else the package's only character component (a class named otherwise).
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals($"marvelgamecontent.default__{cls}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
        if (comp < 0)
        {
            var all = Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.PathOf(pkg.Exports[i]) is string p
                && p.StartsWith("marvelgamecontent.default__", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count != 1) return Inherit();
            comp = all[0];
            cls = pkg.PathOf(pkg.Exports[comp]).Split('.')[1]["default__".Length..];
        }
        byte[] d = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tag = TagWalker.Walk(pkg, d, 16)?.FirstOrDefault(t => t.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
        if (tag == null) return Inherit();
        int n = BitConverter.ToInt32(d, tag.ValueAt);
        if (n < 0 || tag.Size != 4 + 4 * n) return null;

        var opened = new Dictionary<string, (Package Mpm, AnimPackage Anim)?>(StringComparer.OrdinalIgnoreCase);
        (Package Mpm, AnimPackage Anim)? OpenFile(string path)
        {
            if (opened.TryGetValue(path, out var o)) return o;
            try { var m = path.Equals(packagePath, StringComparison.OrdinalIgnoreCase) ? pkg : Package.Open(path); o = (m, AnimPackage.Read(m.RawFile)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or AnimExportCli.Packages.InvalidPackageException) { o = null; }
            return opened[path] = o;
        }

        var sets = new List<Set>();
        for (int k = 0; k < n; k++)
        {
            int r = BitConverter.ToInt32(d, tag.ValueAt + 4 + 4 * k);
            string pkgName, path; string? file; bool fromMod;
            if (r > 0) { pkgName = own; path = pkg.PathOf(pkg.Exports[r - 1]); file = packagePath; fromMod = fileFor(own)?.FromMod ?? false; }
            else if (r < 0)
            {
                // An import's outer chain: the top is its package ("UC__MarvelPlayer_Storm_SF", "startup"), the rest its path there.
                var chain = new List<string>();
                int at = r, guard = 0;
                while (at < 0 && -at <= pkg.Imports.Length && guard++ < 32) { var im = pkg.Imports[-at - 1]; chain.Insert(0, im.ObjectName); at = im.OuterIndex; }
                // An import's outer can be an export of this package (a forced-export package object: wolverine_as in
                // Wolverine Modern hangs under the costume package's own copy of "wolverine_reworkas").
                if (at > 0 && at <= pkg.Exports.Length) chain.InsertRange(0, pkg.PathOf(pkg.Exports[at - 1]).Split('.'));
                // Cooked packages don't name the file an import lives in: its chain starts at a group (storm_anim, biped_lib).
                // A package-named top ("UC__MarvelPlayer_Storm_SF") is taken as the file; else the likely files are searched
                // for an AnimSet at that path: the hero's base package, then the always-loaded Startup and MarvelGame.
                path = string.Join('.', chain);
                pkgName = chain[0]; file = null; fromMod = false;
                var candidates = new List<string>();
                if (chain.Count > 1 && fileFor(chain[0]) != null) { candidates.Add(chain[0]); path = string.Join('.', chain.Skip(1)); }
                if (heroBase.Length > 0) candidates.Add(heroBase);
                candidates.AddRange(["Startup", "MarvelGame"]);
                foreach (string c in candidates)
                {
                    if (fileFor(c) is not { } f || OpenFile(f.Path) is not { } cand) continue;
                    if (!cand.Mpm.Exports.Any(e => cand.Mpm.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase) && cand.Mpm.ClassOf(e).Equals("AnimSet", StringComparison.OrdinalIgnoreCase))) continue;
                    pkgName = c; file = f.Path; fromMod = f.FromMod;
                    break;
                }
            }
            else continue;
            var kind = pkgName.Equals(own, StringComparison.OrdinalIgnoreCase) ? Source.Costume
                : pkgName.Equals(heroBase, StringComparison.OrdinalIgnoreCase) ? Source.Hero
                : pkgName.Equals("startup", StringComparison.OrdinalIgnoreCase) || pkgName.Equals("marvelgame", StringComparison.OrdinalIgnoreCase) ? Source.Shared : Source.Other;
            int export = -1; IReadOnlyList<string> bones = []; IReadOnlySet<string>? tb = null; var seqs = new List<(string, int)>();
            if (file != null && OpenFile(file) is { } op)
            {
                export = Array.FindIndex(op.Mpm.Exports, e => op.Mpm.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase) && op.Mpm.ClassOf(e).Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
                if (export >= 0 && AnimObjectReader.FindAnimSets(op.Anim).FirstOrDefault(s => s.ExportIndex == export) is { } info)
                {
                    bones = info.TrackBoneNames;
                    tb = info.RotationOnly ? new HashSet<string>(info.TranslationBones ?? [], StringComparer.OrdinalIgnoreCase) : null;
                    foreach (var s in info.Sequences.Where(s => s.IsExport))
                    {
                        try { seqs.Add((AnimObjectReader.GetSequenceDisplayName(op.Anim, s.ExportIndex), s.ExportIndex)); }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                    }
                }
            }
            sets.Add(new Set(k, path, pkgName, file, export, kind, fromMod, bones, tb, seqs));
        }

        // Each name from the last set that has it; the earlier sets with that name are what it overrides.
        var anims = new Dictionary<string, Anim>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Enumerable.Reverse(sets))
            foreach (var (name, ex) in s.Sequences)
            {
                if (anims.TryGetValue(name, out var a)) { if (!a.Overrides.Contains(s)) ((List<Set>)a.Overrides).Add(s); }
                else anims[name] = new Anim(name, s, ex, new List<Set>());
            }
        return new CostumeAnims
        {
            PackageFile = packageFile, Class = cls, Sets = sets,
            Anims = anims.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>For a mod's package: other packages by name, the mod's own copy first, else the game's stock copy.</summary>
    public static Func<string, (string Path, bool FromMod)?> FilesFor(Mod? mod, string? cooked) => name =>
    {
        string file = name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? name : name + ".upk";
        if (mod != null && mod.Manifest.UpkReplacements.FirstOrDefault(f => f.Equals(file, StringComparison.OrdinalIgnoreCase)) is { } mine
            && File.Exists(Path.Combine(mod.Folder, mine))) return (Path.Combine(mod.Folder, mine), true);
        if (cooked == null) return null;
        string stock = StockFiles.For(cooked, file);
        return File.Exists(stock) ? (stock, false) : null;
    };
}
