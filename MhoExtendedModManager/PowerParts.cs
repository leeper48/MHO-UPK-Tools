using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A part of a power or character switched off in the editor (Powers tab → Parts), kept in the manifest (PartsOff)
/// so it can be switched on again. The package itself carries the change, so the mod still installs in MHModManager.</summary>
sealed class PartOffEntry
{
    public string Package { get; set; } = "";
    /// <summary>The part's path: marvelgamecontent.default__&lt;class&gt;.&lt;part&gt;.</summary>
    public string Part { get; set; } = "";
    /// <summary>What was switched off: "effect" (particlesystemtemplate), "sound" (activateevent and deactivateevent),
    /// "attachment" (whileactivatedattachmentclass) or a setting's property name (cantransitiontohover).</summary>
    public string Switch { get; set; } = "";
    /// <summary>The value before (the objects' paths, one per property, '|' between; "true" for a setting), to put back.</summary>
    public string Original { get; set; } = "";
    /// <summary>The package came into the mod only for this (the game's copy): it leaves again when nothing in it is off.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Added { get; set; }
}

/// <summary>
/// Parts of a power or character that can be switched off (a user, 2026-10-17: Invisible Woman's hover when she runs a while;
/// tested in game). What the game builds from a class is its Class export's own map of component templates, not the class
/// default's components list: taking a part off that list did nothing in game. So a part is switched off inside: an effect's
/// particle system, a sound's events, an attachment's class set to none, or a setting set to false (Invisible Woman's
/// animcomplexcomponent.cantransitiontohover = false: running never turns into her hover, with its disc, effects and sound).
/// Only object and true/false values change (the package keeps its layout; PackageRebuilder rebuilds and verifies it).
/// </summary>
static class PowerParts
{
    /// <summary>One switch: the part, its kind (Effect, Sound, Attachment, Setting), a label, what it shows or plays, and the
    /// value it has in the package read (Original: "" when it's off there).</summary>
    public sealed record Switch(string Package, string Part, string PartName, string PartClass, string Kind, string Key, string Label, string What, string Original);

    /// <summary>Settings worth switching (true/false values on a part), with a label and what switching them off does.</summary>
    public static readonly Dictionary<string, (string Label, string Off)> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cantransitiontohover"] = ("Can Transition to Hover", "Running never turns into the hover (Invisible Woman: no hover, disc, effects or hover sound while running)."),
    };

    static readonly string[] SoundProps = ["activateevent", "deactivateevent"];

    /// <summary>The switches of a package's class default (the one named after the package's class, else every class default
    /// in marvelgamecontent with parts). Read only.</summary>
    public static List<Switch> List(string packageFile, string path)
    {
        var list = new List<Switch>();
        Package pkg;
        try { pkg = Package.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { return list; }
        string cls = "default__" + PowerColorBuild.ClassOf(packageFile);
        var defaults = Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.Exports[i].ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase)).ToList();
        if (defaults.Count == 0)
            defaults = [.. Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.Exports[i].ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase)
                && pkg.PathOf(pkg.Exports[i]).StartsWith("marvelgamecontent.", StringComparison.OrdinalIgnoreCase) && !pkg.Exports[i].ObjectName.StartsWith("default__marvelattachment", StringComparison.OrdinalIgnoreCase))];
        foreach (int def in defaults)
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                if (e.OuterIndex != def + 1) continue;
                byte[] d;
                try { d = pkg.ReadExportBytes(e).ToArray(); } catch (PackageFormatException) { continue; }
                string part = pkg.PathOf(e), cname = pkg.ClassOf(e);
                var walk = Walk(pkg, d, t => t.Name.Equals("particlesystemtemplate", StringComparison.OrdinalIgnoreCase) || SoundProps.Contains(t.Name, StringComparer.OrdinalIgnoreCase)
                    || t.Name.Equals("whileactivatedattachmentclass", StringComparison.OrdinalIgnoreCase) || Settings.ContainsKey(t.Name));
                if (walk == null) continue;
                string Ref(TagWalker.Tag t) { int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt)); return r == 0 ? "" : RefPath(pkg, r); }
                if (walk.FirstOrDefault(t => t.Name.Equals("particlesystemtemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } pt)
                    list.Add(new(packageFile, part, e.ObjectName, cname, "Effect", "effect", Words(e.ObjectName), Leaf(Ref(pt)), Ref(pt)));
                var snd = walk.Where(t => SoundProps.Contains(t.Name, StringComparer.OrdinalIgnoreCase) && t.Size == 4).OrderBy(t => Array.IndexOf(SoundProps, t.Name.ToLowerInvariant())).ToList();
                if (snd.Count > 0)
                {
                    string orig = string.Join("|", SoundProps.Select(sp => snd.FirstOrDefault(t => t.Name.Equals(sp, StringComparison.OrdinalIgnoreCase)) is { } t ? Ref(t) : ""));
                    string play = snd.Select(Ref).FirstOrDefault(x => x.Length > 0) ?? "";
                    list.Add(new(packageFile, part, e.ObjectName, cname, "Sound", "sound", Words(e.ObjectName), Leaf(play), orig.Trim('|').Length == 0 ? "" : orig));
                }
                if (walk.FirstOrDefault(t => t.Name.Equals("whileactivatedattachmentclass", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } at)
                    list.Add(new(packageFile, part, e.ObjectName, cname, "Attachment", "attachment", Words(e.ObjectName), Leaf(Ref(at)), Ref(at)));
                foreach (var t in walk.Where(t => Settings.ContainsKey(t.Name) && t.Type.Equals("boolproperty", StringComparison.OrdinalIgnoreCase) && t.Start + 24 < d.Length))
                    list.Add(new(packageFile, part, e.ObjectName, cname, "Setting", t.Name.ToLowerInvariant(), Settings[t.Name].Label, Settings[t.Name].Off, d[t.Start + 24] != 0 ? "true" : ""));
            }
        return list;
    }

    /// <summary>
    /// A character package's props (Kurt, 2026-10-17: Black Cat's belt stayed on a model built onto her costume): each of its
    /// attachment class defaults (marvelgamecontent.default__marvelattachment…) with the mesh it shows: a plain one's modelmesh
    /// list, an animated one's mesh component's skeletalmesh. Off = that mesh set to none in this package only (the class
    /// still spawns, with nothing to draw). Read only.
    /// </summary>
    public static List<Switch> CostumeProps(string packageFile, string path)
    {
        var list = new List<Switch>();
        Package pkg;
        try { pkg = Package.Open(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { return list; }
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!e.ObjectName.StartsWith("default__marvelattachment", StringComparison.OrdinalIgnoreCase) || !pkg.PathOf(e).StartsWith("marvelgamecontent.", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d;
            try { d = pkg.ReadExportBytes(e).ToArray(); } catch (PackageFormatException) { continue; }
            string cls = e.ObjectName["default__".Length..];
            string label = PropLabel(cls);
            var walk = Walk(pkg, d, t => t.Name.Equals("modelmesh", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase));
            if (walk == null) continue;
            if (walk.FirstOrDefault(t => t.Name.Equals("modelmesh", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase)) is { } mm
                && mm.Size >= 4 && BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mm.ValueAt)) is int n && n >= 1 && mm.Size == 4 + 4 * n)
            {
                var refs = Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mm.ValueAt + 4 + 4 * k))).Select(r => r == 0 ? "" : RefPath(pkg, r)).ToList();
                string orig = refs.All(r => r.Length == 0) ? "" : string.Join(",", refs);
                list.Add(new(packageFile, pkg.PathOf(e), cls, pkg.ClassOf(e), "Prop", "modelmesh", label, Leaf(refs.FirstOrDefault(r => r.Length > 0) ?? ""), orig));
                continue;
            }
            if (walk.FirstOrDefault(t => t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } mt
                && BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mt.ValueAt)) is int c && c > 0 && c <= pkg.Exports.Length)
            {
                var ce = pkg.Exports[c - 1];
                byte[] cd;
                try { cd = pkg.ReadExportBytes(ce).ToArray(); } catch (PackageFormatException) { continue; }
                if (Walk(pkg, cd, t => t.Name.Equals("skeletalmesh", StringComparison.OrdinalIgnoreCase))?.FirstOrDefault(t => t.Name.Equals("skeletalmesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } sm) continue;
                int r = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(sm.ValueAt));
                string orig = r == 0 ? "" : RefPath(pkg, r);
                list.Add(new(packageFile, pkg.PathOf(ce), cls, pkg.ClassOf(e), "Prop", "skeletalmesh", label, Leaf(orig), orig));
            }
        }
        return list;
    }

    /// <summary>The switch of a prop the costume gets from its hero (its class off the costume's prop list).</summary>
    public const string ListEntry = "listentry";

    /// <summary>
    /// The props a costume gets from its hero (Kurt, 2026-10-28: Doctor Doom Classic's pistol and throne weren't listed): the
    /// classes on its prop list (its own, else the hero's) that aren't defined in its own package, as switches of kind
    /// <see cref="ListEntry"/> (Original: the class's path). <paramref name="listPath"/>: the package whose list counts (the
    /// mod's copy); <paramref name="basePath"/>: the hero's base package (its list, when the costume has none).
    /// </summary>
    public static List<Switch> HeroProps(string packageFile, string listPath, string? basePath)
    {
        var list = new List<Switch>();
        try
        {
            var pkg = Package.Open(listPath);
            var own = OwnListPaths(pkg) ?? (basePath != null ? OwnListPaths(Package.Open(basePath)) : null) ?? [];
            foreach (string path in own)
            {
                string cls = path[(path.LastIndexOf('.') + 1)..];
                if (pkg.Exports.Any(x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase))) continue;
                string label = PropLabel(cls);
                list.Add(new(packageFile, "marvelgamecontent.default__" + cls, cls, "Class", "Hero Prop", ListEntry, label, cls, path));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>The class paths a package's player class default lists (mAttachmentClasses), or null when it has no list.</summary>
    static List<string>? OwnListPaths(Package pkg)
    {
        int pd = Array.FindIndex(pkg.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
        if (pd < 0) return null;
        byte[] d = pkg.ReadExportBytes(pkg.Exports[pd]).ToArray();
        if (TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)) is not { } at) return null;
        var t = new Fx.FxTables(pkg);
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at.ValueAt));
        return [.. Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at.ValueAt + 4 + 4 * k))).Where(r => r != 0).Select(r => t.PathOf(r))];
    }

    /// <summary>"marvelattachmentanimated_blackcatanad_whipleft" → "Whipleft" (the class's last word: what the prop is).</summary>
    static string PropLabel(string cls)
    {
        string last = cls[(cls.LastIndexOf('_') + 1)..];
        return last.Length == 0 ? cls : char.ToUpperInvariant(last[0]) + last[1..];
    }

    /// <summary>The attachment class a prop switch's part belongs to (default__&lt;class&gt;[.component]).</summary>
    public static string PropClass(string part)
    {
        int at = part.IndexOf("default__", StringComparison.OrdinalIgnoreCase);
        string s = at < 0 ? part : part[(at + "default__".Length)..];
        int dot = s.IndexOf('.');
        return dot < 0 ? s : s[..dot];
    }

    /// <summary>
    /// The package with these switches off (Off) or back to their original values (the entry's Original). Every change is read
    /// back; throws InvalidDataException with the reason. Unchanged bytes are kept.
    /// </summary>
    public static byte[] Apply(string path, IReadOnlyList<(PartOffEntry Entry, bool Off)> edits, List<string> log)
    {
        // a prop the costume gets from its hero: off = taken off the costume's prop list (Kurt, 2026-10-28)
        var listed = edits.Where(x => x.Entry.Switch == ListEntry).ToList();
        if (listed.Count > 0)
        {
            byte[] bytes = File.ReadAllBytes(path);
            foreach (var (en, off) in listed) bytes = PowerBorrow.SetListed(bytes, en.Package, en.Original, !off, log);
            string tmp = Path.Combine(Path.GetTempPath(), $"mho_parts_{Guid.NewGuid():N}_{Path.GetFileName(path)}");
            File.WriteAllBytes(tmp, bytes);
            try { return Apply(tmp, [.. edits.Where(x => x.Entry.Switch != ListEntry)], log); }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        }
        var pkg = Package.Open(path);
        var replace = new Dictionary<int, Func<long, byte[]>>();
        foreach (var g in edits.GroupBy(x => x.Entry.Part, StringComparer.OrdinalIgnoreCase))
        {
            int i = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) { log.Add($"{g.Key}: not in {Path.GetFileName(path)}, left out"); continue; }
            byte[] d = pkg.ReadExportBytes(pkg.Exports[i]).ToArray();
            foreach (var (en, off) in g)
            {
                var props = Props(en.Switch);
                var walk = Walk(pkg, d, t => props.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{g.Key}: its {en.Switch} can't be read");
                var origs = en.Original.Split('|');
                for (int k = 0; k < props.Length; k++)
                {
                    if (walk.FirstOrDefault(t => t.Name.Equals(props[k], StringComparison.OrdinalIgnoreCase)) is not { } t) continue;
                    if (t.Type.Equals("boolproperty", StringComparison.OrdinalIgnoreCase)) d[t.Start + 24] = (byte)(off ? 0 : 1);
                    else if (t.Type.Equals("arrayproperty", StringComparison.OrdinalIgnoreCase) && t.Size >= 4
                        && BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt)) is int n && t.Size == 4 + 4 * n)
                    {
                        // a list of objects (a prop's modelmesh): every element to none, or back (the paths, ',' between)
                        var os = (k < origs.Length ? origs[k] : "").Split(',');
                        for (int j = 0; j < n; j++)
                        {
                            string o = j < os.Length ? os[j] : "";
                            int r = off || o.Length == 0 ? 0 : RefOf(pkg, o) ?? throw new InvalidDataException($"{g.Key}: {o} isn't in the package any more");
                            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt + 4 + 4 * j), r);
                        }
                    }
                    else if (t.Size == 4)
                    {
                        string o = k < origs.Length ? origs[k] : "";
                        int r = off || o.Length == 0 ? 0 : RefOf(pkg, o) ?? throw new InvalidDataException($"{g.Key}: {o} isn't in the package any more");
                        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt), r);
                    }
                }
                log.Add($"{Path.GetFileName(path)}: {g.Key[(g.Key.LastIndexOf('.') + 1)..]} {en.Switch} {(off ? "off" : "back on")}");
            }
            byte[] nd = d;
            replace[i] = _ => nd;
        }
        if (replace.Count == 0) return File.ReadAllBytes(path);
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written);
        var back = Package.FromBytes(output);
        foreach (int i in replace.Keys)
            if (!back.ReadExportBytes(back.Exports[i]).AsSpan().SequenceEqual(written[i])) problems.Add($"{back.PathOf(back.Exports[i])} doesn't read back");
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        return output;
    }

    /// <summary>
    /// Save (after the power colors, which rebuild their packages from the game's): every package with a part off gets those
    /// switches off; one whose parts were switched on again since the last save gets their original values back. A package
    /// that isn't in the mod comes in as the game's copy (Added); one that only came in for parts leaves when none is off
    /// and nothing else of the mod uses it. Returns the work folder, or null when nothing changed.
    /// </summary>
    public static string? Build(ModDraft draft, Mod? editing, ModLibrary lib, GameState game, List<string> log)
    {
        var before = editing?.Manifest.PartsOff ?? [];
        if (draft.PartsOff.Count == 0 && before.Count == 0) return null;
        var cmp = StringComparer.OrdinalIgnoreCase;
        static string Key(PartOffEntry e) => e.Package + "|" + e.Part + "|" + e.Switch;
        var wantedKeys = draft.PartsOff.Select(Key).ToHashSet(cmp);
        var colored = draft.PowerColors.SelectMany(e => e.Packages).ToHashSet(cmp);
        string work = Path.Combine(lib.DataFolder, "power-parts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var originals = new Originals(lib.DataFolder, game);
        foreach (string f in draft.PartsOff.Select(e => e.Package).Concat(before.Select(e => e.Package)).Distinct(cmp))
        {
            var off = draft.PartsOff.Where(e => e.Package.Equals(f, StringComparison.OrdinalIgnoreCase)).ToList();
            var back = before.Where(e => e.Package.Equals(f, StringComparison.OrdinalIgnoreCase) && !wantedKeys.Contains(Key(e))).ToList();
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            bool addedBefore = before.Any(e => e.Package.Equals(f, StringComparison.OrdinalIgnoreCase) && e.Added);
            if (off.Count == 0 && addedBefore && !colored.Contains(f))
            {
                // it came in for its parts only: out again (the game's own copy plays)
                if (k >= 0) draft.Packages.RemoveAt(k);
                log.Add($"{f}: nothing in it is off any more, taken out of the mod");
                continue;
            }
            string? source = k >= 0 ? draft.Packages[k].Source : originals.Find(f) ?? (StockFiles.For(game.Cooked, f) is var st && File.Exists(st) ? st : null);
            if (source == null) throw new InvalidDataException($"{f}: no clean original of this package (the game's copy is changed)");
            bool added = k < 0 || addedBefore;
            foreach (var e in off) e.Added = added;
            byte[] bytes = Apply(source, [.. off.Select(e => (e, true)), .. back.Select(e => (e, false))], log);
            string file = Path.Combine(work, f);
            File.WriteAllBytes(file, bytes);
            if (k >= 0) draft.Packages[k] = (f, file); else draft.Packages.Add((f, file));
        }
        return work;
    }

    /// <summary>The properties a switch changes.</summary>
    public static string[] Props(string sw) => sw switch
    {
        "effect" => ["particlesystemtemplate"],
        "sound" => SoundProps,
        "attachment" => ["whileactivatedattachmentclass"],
        _ => [sw],
    };

    /// <summary>A part's properties: they start at byte 16 (most parts) or 8 (PowerFXSound); the start whose walk finds one
    /// of the wanted properties.</summary>
    static TagWalker? Walk(Package pkg, byte[] d, Func<TagWalker.Tag, bool> wanted) =>
        new[] { 16, 8, 4 }.Select(s => TagWalker.Walk(pkg, d, s)).FirstOrDefault(w => w != null && w.Any(wanted));   // (4: a class default's)

    /// <summary>An object reference's full path (an import's through its outers).</summary>
    static string RefPath(Package pkg, int r)
    {
        if (r > 0 && r <= pkg.Exports.Length) return pkg.PathOf(pkg.Exports[r - 1]);
        var parts = new List<string>();
        for (int g = 0; r < 0 && -r <= pkg.Imports.Length && g < 32; g++)
        {
            var im = pkg.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex;
            if (r > 0 && r <= pkg.Exports.Length) { parts.Insert(0, pkg.PathOf(pkg.Exports[r - 1])); break; }
        }
        return string.Join('.', parts);
    }

    /// <summary>The reference to an object by its path (an export, else an import), or null.</summary>
    static int? RefOf(Package pkg, string path)
    {
        int e = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (e >= 0) return e + 1;
        for (int k = 0; k < pkg.Imports.Length; k++) if (RefPath(pkg, -(k + 1)).Equals(path, StringComparison.OrdinalIgnoreCase)) return -(k + 1);
        // the object under another group now (Save's Make Unique renames a mod's changed groups): the one export of that name
        string leaf = path[(path.LastIndexOf('.') + 1)..];
        var same = Enumerable.Range(0, pkg.Exports.Length).Where(i => pkg.Exports[i].ObjectName.Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
        return same.Count == 1 ? same[0] + 1 : null;
    }

    static string Leaf(string path) => path.Length == 0 ? "(Off in the Game)" : path[(path.LastIndexOf('.') + 1)..];

    /// <summary>"hovervfxhands" → "Hover Vfx Hands" (good enough to tell parts apart; the game's names have no spaces).</summary>
    static string Words(string name)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(name, @"(vfx|sfx|fx)", " $1 ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (string w in new[] { "start", "end", "loop", "hover", "fly", "trail", "land", "takeoff", "hands", "palm", "glow", "streak", "trails", "death", "skull", "dodge", "throw", "vox", "level", "up", "anim" })
            s = System.Text.RegularExpressions.Regex.Replace(s, $"(?<=[a-z])({w})", " $1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant() switch { "vfx" => "Effect", "sfx" => "Sound", "fx" => "Effect", "vox" => "Voice", _ => char.ToUpperInvariant(w[0]) + w[1..] });
        return string.Join(' ', words);
    }
}
