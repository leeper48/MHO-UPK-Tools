using System.Text;
using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Moving a costume mod onto another costume of the same hero (Kurt, 2026-09-29: e.g. Unworthy Thor, made for Age of
/// Ultron, onto Thor Classic). Phase 1: a read-only plan.
///
/// Evidence (2026-09-29): the game finds a costume only by its Unreal class name (the costume prototype's CostumeUnrealClass
/// "MarvelPlayer_Thor_Classic" → package UC__MarvelPlayer_Thor_Classic_SF) and its icons by texture name (IconPath,
/// PortraitIconPath, StoreIconPath). A costume package holds that class, its default object (whose initialskeletalmesh
/// component names the mesh and physics asset, and whose mattachmentclasses names a costume's own weapon class, e.g.
/// marvelattachment_thorhammer_ageofultron), the mesh, materials, textures and any animations. 68 of the library's 76
/// costume packages edit the stock mesh in place with new materials beside it; the rest swap the mesh under another name.
/// So the moved package is the mod's own package with the costume's class-level objects renamed to the target costume's
/// names (the name table grows; export data stays byte for byte), and the costume's content groups renamed so they can't
/// collide with the stock source costume's objects (objects are identified by path). Icons move by the two prototypes'
/// icon names, the costume name string by their DisplayName IDs.
/// </summary>
static class CostumeMove
{
    public sealed record Rename(int Export, string OldPath, string NewName, string Why);
    /// <summary>A content group whose streamed textures must keep their path: the rest of its direct children move into a new
    /// group of their own (so the moved mesh and materials can't collide with the source costume's).</summary>
    public sealed record Split(int Group, string GroupName, string NewName, List<int> Moved, int Kept);
    public sealed record PackagePlan(string SourceFile, string SourcePath, string TargetFile, string SourceClass, string TargetClass, List<Rename> Renames, List<string> Notes)
    {
        public List<Split> Splits { get; init; } = [];
        public int Meshes { get; init; }
        public int Animations { get; init; }
        public bool Weapon => Renames.Any(r => r.Why.Contains("weapon"));
    }
    public sealed record IconMove(string Kind, string Package, string From, string To, string Dds, (int W, int H)? DdsSize, (int W, int H, string Format)? TargetSize);
    public sealed record StringMove(string File, string Language, ulong From, ulong To, string Text);
    public sealed record Plan(Costume Source, Costume Target, List<PackagePlan> Packages, List<IconMove> Icons, List<StringMove> Strings, List<string> Left, List<string> Problems)
    {
        /// <summary>Sound packs that move: every event a pack plays is an AkEvent in the moved package(s), so the pack (which patches
        /// the game's shared .pck banks by event name, not by costume) works for the new costume as it is.</summary>
        public List<string> SoundPacks { get; init; } = [];
    }

    const string Player = "marvelplayer_";

    /// <summary>The costume a package file renders ("UC__MarvelPlayer_Thor_AgeOfUltron_SF.upk" → class MarvelPlayer_Thor_AgeOfUltron).</summary>
    static string ClassOfFile(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        if (n.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        if (n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) n = n[..^3];
        return n;
    }

    /// <summary>The costumes a mod's packages render, with the game's definition of each.</summary>
    /// <summary>The costumes a mod's packages render, with the game's definition of each; the one whose own icons the mod
    /// replaces first (Storm Classic VU also carries an unchanged Astonishing package).</summary>
    public static List<(string File, Costume Costume)> SourceCostumes(Mod mod, List<Costume> all)
    {
        var icons = IconReplacements(mod).Select(x => x.Texture).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Own(Costume c) => new[] { c.Icon, c.Portrait, c.Store }.Count(p => Costume.IconTexture(p) is { } t && icons.Contains(t.Texture));
        return [.. mod.Manifest.UpkReplacements
            .Select(f => (File: f, Costume: all.Where(c => c.Class.Equals(ClassOfFile(f), StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.IsDefault).FirstOrDefault()))
            .Where(x => x.Costume != null).Select(x => (x.File, Costume: x.Costume!)).OrderByDescending(x => Own(x.Costume))];
    }

    /// <summary>
    /// A costume that renders from the hero's base package (class MarvelPlayer_&lt;Hero&gt;, e.g. Vision Classic, Black Panther
    /// Classic, Punisher Original: 22 of 536 costumes, checked 2026-09-29). That package also holds the hero's animations,
    /// so it is never moved or replaced; test prototypes (zTesting) aren't offered either.
    /// </summary>
    public static bool IsBase(Costume c) => c.Class.Count(ch => ch == '_') < 2 || c.Prototype.Replace('\\', '/').Contains("/zTesting/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"MarvelPlayer_Thor_" for MarvelPlayer_Thor_AgeOfUltron: costumes can only move within one hero's classes.</summary>
    static string HeroPrefix(Costume c) => c.Class[..(c.Class.IndexOf('_', c.Class.IndexOf('_') + 1) + 1)];

    /// <summary>The other costumes of the same hero whose package exists in the game.</summary>
    public static List<Costume> Targets(Costume source, List<Costume> all, string cooked) =>
        [.. all.Where(c => c.Hero != null && c.Hero == source.Hero && !c.Class.Equals(source.Class, StringComparison.OrdinalIgnoreCase)
                           && !IsBase(c) && c.Class.StartsWith(HeroPrefix(source), StringComparison.OrdinalIgnoreCase)
                           && File.Exists(Path.Combine(cooked, c.Package)))
               .GroupBy(c => c.Class, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(c => c.IsDefault).First()).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The one costume a mod is for (its packages render exactly one costume that can move), or null.</summary>
    public static (string File, Costume Costume)? Single(Mod mod, List<Costume> all)
    {
        var list = SourceCostumes(mod, all);
        if (list.Count == 0 || list.Select(x => x.Costume.Class).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 || IsBase(list[0].Costume)) return null;
        return list[0];
    }

    /// <summary>The source costume's own word in its class name ("ageofultron" in marvelplayer_thor_ageofultron), found as the
    /// part after the longest prefix the two class names share (on a word boundary).</summary>
    static (string Src, string Tgt) Tokens(string srcClass, string tgtClass)
    {
        string a = srcClass.ToLowerInvariant(), b = tgtClass.ToLowerInvariant();
        int k = 0;
        while (k < a.Length && k < b.Length && a[k] == b[k]) k++;
        k = a.LastIndexOf('_', Math.Max(0, k - 1)) + 1;
        return (a[k..], b[k..]);
    }

    public static Plan Make(Mod mod, string sourceFile, Costume source, Costume target, List<Costume> all, string cooked, StockCatalog? catalog)
    {
        var problems = new List<string>();
        var (srcTok, tgtTok) = Tokens(source.Class, target.Class);
        string hero = source.Class.ToLowerInvariant()[..^srcTok.Length];   // "marvelplayer_thor_"

        // Every mod package of this costume (a costume can have a pair, e.g. Rogue's UltimateForm_<costume>) moves.
        var packages = new List<PackagePlan>();
        var left = new List<string>();
        foreach (string f in mod.Manifest.UpkReplacements)
        {
            string cls = ClassOfFile(f).ToLowerInvariant();
            // The costume's packages: its own (and e.g. Rogue's UltimateForm_<costume>), and costume-specific ones of other
            // kinds (Rogue's StolenPower_…_AgeOfX effects) when the game has the target costume's version.
            bool ours = cls.EndsWith("_" + srcTok) && (cls.StartsWith(hero) || File.Exists(Path.Combine(cooked, $"UC__{ClassOfFile(f)[..^srcTok.Length]}{CaseLike(target.Class, tgtTok)}_SF.upk")));
            if (!ours) { left.Add(f + (cls.EndsWith("_" + srcTok) ? " (the game has no version of it for the target costume)" : "")); continue; }
            string tcls = cls[..^srcTok.Length] + tgtTok;
            string tfile = $"UC__{ClassOfFile(f)[..^srcTok.Length]}{CaseLike(target.Class, tgtTok)}_SF.upk";
            if (!File.Exists(Path.Combine(cooked, tfile))) { problems.Add($"{f}: the target has no {tfile} in the game"); continue; }
            packages.Add(PackageRenames(Path.Combine(mod.Folder, f), f, tfile, cls, tcls, srcTok, tgtTok));
        }
        if (packages.Count == 0) problems.Add("no package of this costume in the mod");

        // Icons: the source costume's icon names → the target's.
        var icons = new List<IconMove>();
        var pairs = new (string Kind, string? From, string? To)[]
        {
            ("Costume Icon", source.Icon, target.Icon), ("Hero Portrait", source.Portrait, target.Portrait),
            ("Store Image", source.Store, target.Store), ("Party Portrait", source.PartyPortrait, target.PartyPortrait),
        };
        var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, from, to) in pairs)
        {
            if (Costume.IconTexture(from) is not { } f || Costume.IconTexture(to) is not { } t || !moved.Add(f.Texture)) continue;
            foreach (var (pkg, tex, dds) in IconReplacements(mod).Where(x => x.Texture.Equals(f.Texture, StringComparison.OrdinalIgnoreCase)))
            {
                if (!pkg.Equals(t.Package, StringComparison.OrdinalIgnoreCase)) problems.Add($"{kind}: {tex} is in {pkg}, the target's {t.Texture} in {t.Package}");
                icons.Add(new IconMove(kind, t.Package, tex, t.Texture, dds, DdsSize(Path.Combine(mod.Folder, dds)), catalog?.Size(t.Package, t.Texture)));
            }
        }
        foreach (var (pkg, tex, _) in IconReplacements(mod))
            if (!moved.Contains(tex)) left.Add($"icon {tex} ({pkg}): not the costume's own, kept as it is");

        // The costume's name: the mod's text for the source DisplayName → the target's DisplayName.
        var strings = new List<StringMove>();
        // Every text field of the source costume the mod changes (its name, a description …) goes to the target's same field.
        var fieldMap = source.Texts.Where(kv => target.Texts.ContainsKey(kv.Key)).ToDictionary(kv => kv.Value, kv => target.Texts[kv.Key]);
        if (source.DisplayName != 0 && target.DisplayName != 0) fieldMap.TryAdd(source.DisplayName, target.DisplayName);
        if (fieldMap.Count > 0)
            foreach (string lang in Directory.Exists(mod.Folder) ? Directory.GetFiles(mod.Folder, "*.json") : [])
            {
                if (Path.GetFileName(lang).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(lang));
                    foreach (var file in doc.RootElement.EnumerateObject())
                        foreach (var e in file.Value.EnumerateObject())
                            if (ulong.TryParse(e.Name, out ulong id) && fieldMap.TryGetValue(id, out ulong to))
                                strings.Add(new StringMove(StringFileFor(Path.GetFileNameWithoutExtension(lang), to), Path.GetFileNameWithoutExtension(lang), id, to,
                                    e.Value.TryGetProperty("String", out var s) ? s.GetString() ?? "" : ""));
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { problems.Add($"{Path.GetFileName(lang)}: {ex.Message}"); }
            }
        // Sound packs (Miles Morales: 82 new events, all AkEvents in the costume package, checked 2026-09-29).
        var sounds = new List<string>();
        var events = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pk in packages)
            try { var pp = Package.Open(pk.SourcePath); foreach (var e in pp.Exports) if (pp.ClassOf(e).Equals("AkEvent", StringComparison.OrdinalIgnoreCase)) events.Add(e.ObjectName); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        foreach (string f in mod.Manifest.AudioPacks)
        {
            try
            {
                var pack = SoundPack.Load(Path.Combine(mod.Folder, f));
                int missing = pack.Patches.Count(p => !events.Contains(p.EventName));
                if (missing == 0) sounds.Add(f);
                else left.Add($"sound pack {f}: {missing} of its {pack.Patches.Count} events aren't in the moved package");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or FormatException) { left.Add($"sound pack {f}: doesn't read ({ex.Message})"); }
        }
        return new Plan(source, target, packages, icons, strings, left, problems) { SoundPacks = sounds };
    }

    /// <summary>The .string file an ID lives in: its top two bits pick one of four (checked: Thor Classic's name 0x2BF1… is in
    /// eng.all_3FFF…, Age of Ultron's 0x910B… in eng.all_BFFF…).</summary>
    static string StringFileFor(string lang, ulong id) => $"{lang}.all_{(id >> 62) switch { 0 => "3F", 1 => "7F", 2 => "BF", _ => "FF" }}FFFFFFFFFFFFFF.string";

    static string CaseLike(string cls, string tok) => cls.Length >= tok.Length ? cls[^tok.Length..] : tok;

    /// <summary>Which exports get new names: the costume's class-level objects take the target's names; its content groups
    /// (top-level packages naming the costume) get "_on_&lt;target&gt;" so nothing in them shares a path with the stock source.</summary>
    static PackagePlan PackageRenames(string path, string file, string tfile, string cls, string tcls, string srcTok, string tgtTok)
    {
        var renames = new List<Rename>();
        var notes = new List<string>();
        var splits = new List<Split>();
        Package pkg;
        try { pkg = Package.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { notes.Add("can't be read: " + ex.Message); return new(file, path, tfile, cls, tcls, renames, notes); }
        // Textures whose big mips stream from a .tfc are found there by their path (TextureFileCacheManifest.bin: path, GUID,
        // cache, mips), so a group holding one keeps its name. In game 2026-09-29: with thor_ageofultron renamed, the moved
        // Thor Classic showed briefly (inline mips) and crashed when streaming began.
        int Top(int i) { for (int g = 0; g < 64 && pkg.Exports[i].OuterIndex > 0; g++) i = pkg.Exports[i].OuterIndex - 1; return i; }
        var streams = new Dictionary<int, int>();
        var streamed = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                try { if (TextureInfo.Read(pkg, pkg.Exports[i]).Mips.Any(m => m.InSeparateFile)) { int t = Top(i); streams[t] = streams.GetValueOrDefault(t) + 1; streamed.Add(i); } }
                catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { }
        bool hasClass = false;
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            string n = e.ObjectName, lower = n.ToLowerInvariant(), c = pkg.ClassOf(e);
            string? to = null, why = null;
            if (lower == cls || lower == "default__" + cls) { to = lower.Replace(cls, tcls); why = "the costume's class"; if (lower == cls) hasClass = true; }
            else if (lower == "uc__" + cls + "_sf") { to = "uc__" + tcls + "_sf"; why = "the package's own object"; }
            else if ((lower.StartsWith("marvelattachment_") || lower.StartsWith("default__marvelattachment_")) && lower.EndsWith("_" + srcTok))
            { to = lower[..^srcTok.Length] + tgtTok; why = "the costume's weapon / prop class"; }
            else if (e.OuterIndex == 0 && c.Equals("package", StringComparison.OrdinalIgnoreCase) && lower.Contains(srcTok))
            {
                if (streams.TryGetValue(i, out int k))
                {
                    // Split (in game 2026-09-29: with the whole group kept, Age of Ultron showed the moved Unworthy Thor mesh
                    // after Thor Classic had loaded it: same path). Streamed textures are stock, so sharing their path is harmless.
                    var moved = Enumerable.Range(0, pkg.Exports.Length).Where(j => pkg.Exports[j].OuterIndex == i + 1 && !streamed.Contains(j)).ToList();
                    if (moved.Count > 0) splits.Add(new Split(i, n, n + "_on_" + tgtTok, moved, k));
                    notes.Add($"group {n}: its {k} streamed texture(s) keep their path (a .tfc finds them by it); the other {moved.Count} object(s) in it move to {n}_on_{tgtTok}");
                }
                else { to = n + "_on_" + tgtTok; why = "content group (own path)"; }
            }
            if (to != null) renames.Add(new Rename(i, pkg.PathOf(e), to, why!));
        }
        if (!hasClass) notes.Add($"no class {cls} in the package");
        int meshes = pkg.Exports.Count(e => pkg.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        int anims = pkg.Exports.Count(e => pkg.ClassOf(e).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase));
        notes.Add($"{pkg.Exports.Length} objects, {meshes} skeletal mesh(es), {anims} animation(s): all kept as they are");
        return new(file, path, tfile, cls, tcls, renames, notes) { Splits = splits, Meshes = meshes, Animations = anims };
    }

    /// <summary>
    /// Phase 2: writes each moved package into <paramref name="outDir"/> (never the game folder): the mod's package with the
    /// planned renames (new names appended to the name table, every other byte kept), read back and verified
    /// (PackageRebuilder.Verify: names = original + added, entries equal apart from the renamed names, all data identical).
    /// Returns the files written, or throws with the problems.
    /// </summary>
    public static List<string> Build(Plan plan, string outDir, Originals originals)
    {
        Directory.CreateDirectory(outDir);
        var written = new List<string>();
        foreach (var pk in plan.Packages)
        {
            // The game checks each package's GUID against the one it expects for that file (in game, 2026-09-29: "Package
            // found on disk with invalid GUID" for a moved Thor_Classic carrying Age of Ultron's GUID). So the moved package
            // takes the GUID of the target's stock package (a copy verified against the stock checksums).
            string? stock = originals.Find(pk.TargetFile);
            if (stock == null) throw new InvalidDataException($"{pk.TargetFile}: no stock copy to take its GUID from (the game's copy is changed and there's no kept original)");
            var sp = Package.Open(stock);
            byte[] guid = File.ReadAllBytes(stock).AsSpan(sp.GenerationsAt - 16, 16).ToArray();
            var pkg = Package.Open(pk.SourcePath);
            var renames = pk.Renames.ToDictionary(r => r.Export, r => r.NewName);
            var addNames = renames.Values.Concat(pk.Splits.Select(x => x.NewName)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            int NameIdx(string n) { int k = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); return k >= 0 ? k : pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); }
            // Each split adds a group object: a copy of the original group's entry (same class, flags) under the new name.
            var add = new List<NewExport>();
            var outers = new Dictionary<int, int>();
            foreach (var spl in pk.Splits)
            {
                byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[spl.Group], pkg.ExportEntryEnd[spl.Group] - pkg.ExportEntryStart[spl.Group]).ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), NameIdx(spl.NewName));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
                byte[] groupData = pkg.ReadExportBytes(pkg.Exports[spl.Group]);
                int newRef = pkg.Exports.Length + add.Count + 1;
                add.Add(new NewExport(spl.Group, 0, _ => groupData) { Entry = entry });
                foreach (int j in spl.Moved) outers[j] = newRef;
            }
            byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), add, out var data, addNames, null, renames, outers);
            guid.CopyTo(output, pkg.GenerationsAt - 16);
            var problems = PackageRebuilder.Verify(pkg, output, [], add, data, addNames, null, renames, outers);
            var back = Package.FromBytes(output);
            if (!output.AsSpan(back.GenerationsAt - 16, 16).SequenceEqual(guid)) problems.Add("the GUID isn't the target's");
            foreach (var (i, n) in renames)
                if (!back.Exports[i].ObjectName.Equals(n, StringComparison.OrdinalIgnoreCase)) problems.Add($"export {i + 1} is '{back.Exports[i].ObjectName}', expected '{n}'");
            foreach (var spl in pk.Splits)
                foreach (int j in spl.Moved)
                    if (!back.PathOf(back.Exports[j]).StartsWith(spl.NewName + ".", StringComparison.OrdinalIgnoreCase)) problems.Add($"export {j + 1} isn't under {spl.NewName}");
            if (problems.Count > 0) throw new InvalidDataException($"{pk.TargetFile}: " + string.Join("; ", problems));
            string path = Path.Combine(outDir, pk.TargetFile);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, output);
            if (!File.ReadAllBytes(tmp).AsSpan().SequenceEqual(output)) { File.Delete(tmp); throw new IOException($"{pk.TargetFile}: written file differs"); }
            File.Move(tmp, path, overwrite: true);
            written.Add(path);
        }
        return written;
    }

    /// <summary>
    /// Phase 3: the moved costume as a new mod in the library ("&lt;name&gt; (on &lt;Target&gt;)", at the top, disabled, the
    /// original untouched): the moved packages, the costume's own icons under the target's names, the costume name under
    /// the target's string ID; tags, note and description kept. Left out (tied to the source slot): the rest of the mod's
    /// packages and icons, sound packs, the preview pick and 3D views, the saved post, the Nexus link and the changelog.
    /// Returns the new mod's folder name, or null with the reason.
    /// </summary>
    public static string? CreateMod(ModLibrary lib, Mod mod, Plan plan, Originals originals, out string? error, Mod? replace = null,
        StockCatalog? catalog = null, List<string>? resized = null)
    {
        error = null;
        if (plan.Problems.Count > 0) { error = string.Join(Environment.NewLine, plan.Problems); return null; }
        string work = Path.Combine(lib.DataFolder, "costume-move-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var built = Build(plan, work, originals);
            var d = ModDraft.From(mod);
            d.Name = NewName(mod, plan.Target);
            d.Packages = built.Select(f => (Path.GetFileName(f), f)).ToList();
            var byPackage = Applier.IconPackages.Select((p, k) => (p.File, k)).ToDictionary(x => x.File, x => x.k, StringComparer.OrdinalIgnoreCase);
            d.Textures = [[], [], []];
            d.Extra = [];
            foreach (var i in plan.Icons)
            {
                string src = FitIcon(i, mod.Folder, work, catalog, resized);
                if (byPackage.TryGetValue(i.Package, out int k)) d.Textures[k].Add((i.To, src));
                else d.Extra.Add((i.Package, i.To, src));
            }
            d.Strings = [.. plan.Strings.Select(sm => mod.Strings.First(x => x.Id == sm.From && x.Language.Equals(sm.Language, StringComparison.OrdinalIgnoreCase)) with { Id = sm.To, File = sm.File, Variants = null })];
            d.SoundPacks = [.. plan.SoundPacks.Select(f => Path.Combine(mod.Folder, f))];
            d.PreviewImage = null; d.PreviewViews = null; d.CardPicture = null;
            d.PostNexus = d.PostDiscord = null; d.PostImages = [];
            d.NexusModId = null; d.Changes = ""; d.Changelog = [];
            d.Notes = (d.Notes.Length > 0 ? d.Notes.TrimEnd() + Environment.NewLine : "") + $"Moved from {mod.Name} ({plan.Source.Short.Replace(".prototype", "")} → {plan.Target.Short.Replace(".prototype", "")}).";
            // replace: rebuild an existing moved copy in place (same folder, place, on/off; e.g. after the original was updated).
            if (replace != null) { d.Tags = [.. replace.ModTags]; d.Notes = replace.Manifest.Notes ?? d.Notes; }
            return ModWriter.Save(lib, d, replace, out error);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { error = ex.Message; return null; }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>The moved mod's name: "Unworthy Thor (on Classic)".</summary>
    public static string NewName(Mod mod, Costume target) => $"{mod.Name} (on {target.Title})";

    static IEnumerable<(string Package, string Texture, string Dds)> IconReplacements(Mod mod)
    {
        foreach (var (file, _, list) in Applier.IconPackages)
            foreach (var r in list(mod.Manifest)) yield return (file, r.TextureName, r.DdsFileName);
        foreach (var r in mod.Manifest.Extra) yield return (r.Package, r.TextureName, r.DdsFileName);
    }

    /// <summary>
    /// A costume image for the target's texture (Kurt, 2026-10-01: a user's LunaStore.dds stopped a move): as it is when
    /// it's the target's size, else scaled to cover that size and centred (so nothing is stretched; the overflow is cut
    /// off) and converted like an image loaded in the editor (the target's DXT format, no mips), into the work folder.
    /// Each resize is added to <paramref name="resized"/> with both sizes. Throws with the sizes when it can't be done.
    /// </summary>
    internal static string FitIcon(IconMove i, string modFolder, string work, StockCatalog? catalog, List<string>? resized)
    {
        string src = Path.Combine(modFolder, i.Dds);
        if (i.DdsSize is { } a0 && i.TargetSize is { } t0 && a0.W == t0.W && a0.H == t0.H) return src;
        string have = i.DdsSize is { } a1 ? $"{a1.W}×{a1.H}" : "an unreadable size";
        if (i.TargetSize is not { } t || catalog == null)
            throw new InvalidDataException($"{i.Kind}: {i.Dds} is {have}, and the size of the target's {i.To} isn't known (not found in the game's icons)");
        var d = TextureDecode.ReadDds(src, out string why) ?? throw new InvalidDataException($"{i.Kind}: {i.Dds} can't be read ({why})");
        var bgra = TextureDecode.ToBgra(d.Format, d.W, d.H, d.Data, out why) ?? throw new InvalidDataException($"{i.Kind}: {i.Dds} ({d.Format}) can't be decoded ({why})");
        Directory.CreateDirectory(work);
        string png = Path.Combine(work, "fit-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        string outDds = Path.Combine(work, "fit-" + i.To + ".dds");
        bool cropped;
        using (var full = TextureDecode.ToBitmap(bgra, d.W, d.H))
        using (var fit = new System.Drawing.Bitmap(t.W, t.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            // Cover: the larger of the two scales, centred; what overflows is cut off.
            float k = Math.Max((float)t.W / d.W, (float)t.H / d.H);
            float w = d.W * k, h = d.H * k;
            cropped = Math.Abs(w - t.W) > 1 || Math.Abs(h - t.H) > 1;
            using (var g = System.Drawing.Graphics.FromImage(fit))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImage(full, (t.W - w) / 2, (t.H - h) / 2, w, h);
            }
            fit.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        try { catalog.ImageToDds(i.Package, i.To, png, outDds); }
        finally { File.Delete(png); }
        resized?.Add($"{i.Kind}: {i.Dds} {d.W}×{d.H} → {t.W}×{t.H} (the size of the target's {i.To}){(cropped ? ", centred and trimmed to keep its shape" : "")}");
        return outDds;
    }

    static (int W, int H)? DdsSize(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[20];
            if (f.Read(b, 0, 20) < 20 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S') return null;
            return (BitConverter.ToInt32(b, 16), BitConverter.ToInt32(b, 12));
        }
        catch (IOException) { return null; }
    }

    public static string Report(Plan p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Move: {p.Source.Short} ({p.Source.Class}) → {p.Target.Short} ({p.Target.Class})");
        foreach (var pk in p.Packages)
        {
            sb.AppendLine($"  package {pk.SourceFile} → {pk.TargetFile}");
            foreach (var n in pk.Notes) sb.AppendLine("    " + n);
            foreach (var r in pk.Renames) sb.AppendLine($"    rename #{r.Export + 1} {r.OldPath} → {r.NewName}   ({r.Why})");
            foreach (var sp in pk.Splits) sb.AppendLine($"    split {sp.GroupName}: {sp.Moved.Count} object(s) → {sp.NewName}, {sp.Kept} streamed texture(s) stay");
        }
        sb.AppendLine(p.Icons.Count == 0 ? "  icons: the mod has none of the costume's own" : "  icons:");
        foreach (var i in p.Icons)
        {
            string size = i.DdsSize is { } d ? $"{d.W}×{d.H}" : "?";
            string want = i.TargetSize is { } t ? $"{t.W}×{t.H} {t.Format}" : "not found in the game's icons";
            string ok = i.DdsSize is { } a && i.TargetSize is { } b ? (a.W == b.W && a.H == b.H ? "same size" : "DIFFERENT SIZE: resized when moved") : "";
            sb.AppendLine($"    {i.Kind}: {i.From} → {i.To}  ({i.Dds} {size}; target {want}) {ok}");
        }
        sb.AppendLine(p.Strings.Count == 0 ? "  costume name: the mod doesn't rename it" : "  costume name:");
        foreach (var sp in p.SoundPacks) sb.AppendLine($"  sound pack: {sp} (its events are in the moved package)");
        foreach (var s in p.Strings) sb.AppendLine($"    {s.Language}: \"{s.Text}\" → string {s.To} in {s.File} (was {s.From})");
        foreach (var l in p.Left) sb.AppendLine("  left out: " + l);
        foreach (var x in p.Problems) sb.AppendLine("  PROBLEM: " + x);
        return sb.ToString();
    }
}
