using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Another character's own always-on effects on a costume (a user, 2026-11-09: Ghost Rider's burning head, Firestar's
/// flames, Cable's eye glow on other heroes). Such effects are an attachment class the character's class default lists
/// (mAttachmentClasses): an effect-only attachment whose default has PEffectTemplates spawned at SpawnSockets
/// (marvelattachment_ghostrider: vfx_gr_bodyflames_head at socket_head; marvelattachment_cable_eye at socket_l_eye; the
/// Firestar team-up's marvelattachment_firestar, 8 flames). The class is copied into the costume's package and added to its
/// list. A costume with no list of its own plays its hero's (stock costumes mostly do), and a list of its own replaces the
/// hero's completely (Alejandra Blaze's has no head flames), so such a costume gets the hero's list, as imports, plus this one.
/// Replace also takes the costume's own effect-only attachments off the list (never a weapon).
/// </summary>
/// <summary>One borrowed set of always-on effects (manifest extension OwnFxBorrows).</summary>
sealed class OwnFxBorrowEntry
{
    /// <summary>The character package that shows them (a costume, a hero's base package, a team-up …).</summary>
    public string Package { get; set; } = "";
    /// <summary>The package the effects come from (the game's) and their attachment class.</summary>
    public string Donor { get; set; } = "";
    public string Class { get; set; } = "";
    /// <summary>The donor's name as shown (Ghost Rider).</summary>
    public string Name { get; set; } = "";
    /// <summary>The package's own effect-only attachments taken off.</summary>
    public bool Replace { get; set; }
    /// <summary>Its attachment list before any borrow (class names); null: it had none of its own (it played its hero's).</summary>
    public List<string>? Original { get; set; }
    public OwnFxBorrowEntry Copy() => new() { Package = Package, Donor = Donor, Class = Class, Name = Name, Replace = Replace, Original = Original?.ToList() };
}

static class OwnFxBorrow
{
    /// <summary>An effect-only attachment class: its name, its effect templates' count and their sockets.</summary>
    public sealed record Effect(string Class, int Templates, IReadOnlyList<string> Sockets);

    static int Find(Package p, string path) => Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The effect-only attachments a character package lists (its own list, else its hero's), each read from this
    /// package or the hero's base package. Empty when it has none.</summary>
    public static List<Effect> Effects(string packageFile, string? baseFile)
    {
        var res = new List<Effect>();
        var list = ModMeshes.AttachmentClasses(packageFile) ?? (baseFile != null ? ModMeshes.AttachmentClasses(baseFile) : null);
        if (list == null) return res;
        var pkgs = new List<Package> { Package.Open(packageFile) };
        if (baseFile != null && File.Exists(baseFile)) pkgs.Add(Package.Open(baseFile));
        foreach (string cls in list)
            foreach (var p in pkgs)
                if (Read(p, cls) is { } e) { res.Add(e); break; }
        return res;
    }

    /// <summary>The attachment class's default in <paramref name="p"/> when it's effect-only (effect templates, no mesh), else null.</summary>
    static Effect? Read(Package p, string cls)
    {
        int d = Find(p, "marvelgamecontent.default__" + cls.ToLowerInvariant());
        if (d < 0) return null;
        byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
        if (TagWalker.Walk(p, b, 4) is not { } tags) return null;
        if (tags.Any(t => t.Name.Contains("Mesh", StringComparison.OrdinalIgnoreCase))) return null;
        if (tags.FirstOrDefault(t => t.Name.Equals("PEffectTemplates", StringComparison.OrdinalIgnoreCase)) is not { } et) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(et.ValueAt));
        if (n <= 0) return null;
        var sockets = new List<string>();
        if (tags.FirstOrDefault(t => t.Name.Equals("SpawnSockets", StringComparison.OrdinalIgnoreCase)) is { } ss)
            for (int k = 0, m = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ss.ValueAt)); k < m && ss.ValueAt + 12 + 8 * k <= ss.End; k++)
                sockets.Add(TagWalker.NameAt(p, b, ss.ValueAt + 4 + 8 * k));
        return new Effect(cls, n, sockets);
    }

    /// <summary>
    /// <paramref name="target"/> (a costume or hero package as it is now) with its attachment list worked out again from
    /// <paramref name="original"/> (its list before any borrow; null: it had no list of its own and played its hero's, from
    /// <paramref name="baseFile"/>): that list, without its effect-only attachments when <paramref name="replace"/>, plus
    /// <paramref name="donorClass"/> of <paramref name="donorFile"/> (copied in once). No donor and no Replace: the original
    /// list again (none of its own: the tag taken out). Built from the list, so a borrow changed or reset never piles up.
    /// </summary>
    public static byte[] Rebuild(byte[] target, string targetName, string? baseFile, IReadOnlyList<string>? original, string? donorFile, string? donorClass, bool replace, List<string> log)
    {
        var dst = Package.FromBytes(target);
        int def = ModMeshes.CharacterDefault(dst);
        if (def < 0) throw new InvalidDataException($"{targetName}: no character class default");
        string? added = null;
        if (donorFile != null && donorClass != null)
        {
            var hp = Package.Open(donorFile);
            int ci = Array.FindIndex(hp.Exports, e => e.ObjectName.Equals(donorClass, StringComparison.OrdinalIgnoreCase) && hp.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase));
            if (ci < 0) throw new InvalidDataException($"{Path.GetFileName(donorFile)} has no class {donorClass}");
            if (Read(hp, donorClass) is not { } donorFx) throw new InvalidDataException($"{donorClass} isn't an effect-only attachment");
            // a copy already in (a build before; Make Unique may have given it a path of its own at Save:
            // marvelattachment_humantorch_gr23729ea, 2026-11-10), else copied in now
            bool IsCopy(MhoPackageModifier.ExportEntry e) => dst.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)
                && (e.ObjectName.Equals(donorClass, StringComparison.OrdinalIgnoreCase) || e.ObjectName.StartsWith(donorClass + "_", StringComparison.OrdinalIgnoreCase)
                    // (only Make Unique's tag, UniqueNames.Tag: letters or digits, then 6 hex digits; not another stock class
                    // such as marvelattachment_cable_eye_red)
                    && System.Text.RegularExpressions.Regex.IsMatch(e.ObjectName[(donorClass.Length + 1)..], "^[a-z0-9]{1,14}[0-9a-f]{6}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && Read(dst, e.ObjectName) != null);
            int had = Array.FindIndex(dst.Exports, IsCopy);   // (ExportEntry is a struct: FirstOrDefault gives an empty one, never null)
            if (had >= 0) donorClass = dst.Exports[had].ObjectName;
            else
            {
                var cc = CrossMove.Quiet(() => ExportCopy.Copy(hp, ci, dst, []), out string said)
                    ?? throw new InvalidDataException($"{donorClass} couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
                dst = Package.FromBytes(cc.Output);
                log.Add($"own effects: {donorClass} copied from {Path.GetFileName(donorFile)} ({donorFx.Templates} effect(s) at {string.Join(", ", donorFx.Sockets.Distinct())})");
            }
            added = donorClass;
        }
        def = ModMeshes.CharacterDefault(dst);
        byte[] d = dst.ReadExportBytes(dst.Exports[def]).ToArray();
        var tags = TagWalker.Walk(dst, d, 4) ?? throw new InvalidDataException($"{targetName}: its class default's properties don't read");
        var tag = tags.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase));

        // the wanted list, by class name
        Package? bp = baseFile != null && File.Exists(baseFile) && !Path.GetFileName(baseFile).Equals(targetName, StringComparison.OrdinalIgnoreCase) ? Package.Open(baseFile) : null;
        List<string>? names = original?.ToList();
        bool ownList = names != null;
        if (added != null || replace)
        {
            names ??= (bp != null ? ModMeshes.AttachmentClasses(baseFile!) : null)?.ToList() ?? [];
            if (replace)
            {
                var gone = names.Where(n => (Read(dst, n) ?? (bp != null ? Read(bp, n) : null)) != null).ToList();
                names.RemoveAll(gone.Contains);
                log.Add(gone.Count > 0 ? $"own effects: {string.Join(", ", gone)} taken off (Replace)" : "own effects: nothing of its own to take off (Replace)");
            }
            if (added != null && !names.Contains(added, StringComparer.OrdinalIgnoreCase)) names.Add(added);
        }

        var addNames = new List<string>();
        var addImports = new List<NewImport>();
        int NameIdx(string nm)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(nm); j = addNames.Count - 1; }
            return dst.Names.Length + j;
        }
        // the outer the hero's classes are imported under: that of the hero's class (the costume's super class)
        int? outer = null;
        int OuterOfHero()
        {
            if (outer is int o) return o;
            int bdef = bp != null ? ModMeshes.CharacterDefault(bp) : -1;
            string heroClass = bdef >= 0 ? bp!.ClassOf(bp.Exports[bdef]) : "";
            int sup = Array.FindIndex(dst.Imports, im => im.ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase) && im.ObjectName.Equals(heroClass, StringComparison.OrdinalIgnoreCase));
            if (sup < 0) throw new InvalidDataException($"{targetName}: no import of its hero's class to put the hero's attachments beside");
            outer = dst.Imports[sup].OuterIndex;
            return outer.Value;
        }
        int RefOf(string cls)
        {
            int ex = Array.FindIndex(dst.Exports, e => e.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase));
            if (ex >= 0) return ex + 1;
            int im = Array.FindIndex(dst.Imports, i => i.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && i.ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase));
            if (im >= 0) return -im - 1;
            int ai = addImports.FindIndex(i => i.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase));
            if (ai >= 0) return -(dst.Imports.Length + ai + 1);
            int ou = OuterOfHero();
            foreach (string nm in new[] { "Core", "Class", cls }) NameIdx(nm);
            addImports.Add(new NewImport("Core", "Class", ou, cls));
            return -(dst.Imports.Length + addImports.Count);
        }

        byte[] nd;
        if (names == null)
        {
            // back to the hero's list: no list of its own
            if (tag == null) { log.Add($"own effects: {targetName} plays its hero's list (as before)"); return target; }
            nd = [.. d.AsSpan(0, tag.Start), .. d.AsSpan(tag.End)];
            log.Add($"own effects: {targetName}'s own list taken out: it plays its hero's again");
        }
        else
        {
            var refs = names.Select(RefOf).ToList();
            byte[] value = [.. BitConverter.GetBytes(refs.Count), .. refs.SelectMany(BitConverter.GetBytes)];
            if (tag != null)
            {
                nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.End)];
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
            }
            else
            {
                int n1 = NameIdx("mAttachmentClasses"), n2 = NameIdx("ArrayProperty");
                byte[] head = [.. BitConverter.GetBytes(n1), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(n2), .. BitConverter.GetBytes(0), .. BitConverter.GetBytes(value.Length), .. BitConverter.GetBytes(0)];
                nd = [.. d.AsSpan(0, tags.NoneAt), .. head, .. value, .. d.AsSpan(tags.NoneAt)];
                if (!ownList) log.Add($"own effects: {targetName} had no attachment list of its own: it gets its hero's plus the new one");
            }
        }
        var replaceMap = new Dictionary<int, Func<long, byte[]>> { [def] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(dst, replaceMap, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(dst, output, replaceMap.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException($"{targetName} didn't verify: " + string.Join("; ", problems.Take(3)));
        var now = ModMeshesList(Package.FromBytes(output)).ToList();
        log.Add($"own effects: {targetName}'s list now: {(now.Count > 0 ? string.Join(", ", now) : "its hero's")}");
        return output;
    }

    /// <summary>One source character: its package, its effect-only attachment, how many effects and where.</summary>
    public sealed record Source(string File, string Class, int Templates, string Sockets)
    {
        /// <summary>Hero, Team-Up or NPC (agents and NPCs).</summary>
        public string Kind => File.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? "Hero" : File.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase) ? "Team-Up" : "NPC";

        /// <summary>The character's name from the package (Ghost Rider, Firestar, Clone Colossus).</summary>
        public string Name
        {
            get
            {
                string stem = Path.GetFileNameWithoutExtension(File);
                string n = stem[(stem.IndexOf('_', 4) + 1)..];
                if (n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) n = n[..^3];
                return System.Text.RegularExpressions.Regex.Replace(n.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])", " ");
            }
        }
    }

    /// <summary>Every character package of the game with an effect-only attachment defined in it (for the shipped list,
    /// StockData\own_effects.txt; <c>--own-fx-census</c>). Read only.</summary>
    public static List<Source> Census(string cooked)
    {
        var res = new List<Source>();
        foreach (string f in Directory.EnumerateFiles(cooked, "UC__*.upk").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string n = Path.GetFileName(f);
            if (n.Contains("bak", StringComparison.OrdinalIgnoreCase) || n.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(n.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("UC__MarvelAgent_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UC__MarvelNPC_", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var p = Package.Open(f);
                foreach (var e in p.Exports)
                {
                    if (!p.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Read(p, e.ObjectName) is { } fx) res.Add(new Source(n, e.ObjectName, fx.Templates, string.Join(", ", fx.Sockets.Distinct())));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
        }
        return res;
    }

    /// <summary>The shipped list (StockData\own_effects.txt: file, class, effects, sockets; tab-separated).</summary>
    public static List<Source> Sources()
    {
        using var s = typeof(OwnFxBorrow).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.own_effects.txt");
        if (s == null) return [];
        using var r = new StreamReader(s);
        var res = new List<Source>();
        for (string? l = r.ReadLine(); l != null; l = r.ReadLine())
            if (l.Split('\t') is { Length: 4 } c && int.TryParse(c[2], out int t)) res.Add(new Source(c[0], c[1], t, c[3]));
        return res;
    }

    static IEnumerable<string> ModMeshesList(Package p)
    {
        int def = ModMeshes.CharacterDefault(p);
        byte[] d = p.ReadExportBytes(p.Exports[def]).ToArray();
        var t = TagWalker.Walk(p, d, 4)?.FirstOrDefault(x => x.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase));
        if (t == null) yield break;
        for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt)); k < n; k++)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt + 4 + 4 * k));
            yield return r > 0 ? p.Exports[r - 1].ObjectName : r < 0 ? p.Imports[-r - 1].ObjectName : "none";
        }
    }
}
