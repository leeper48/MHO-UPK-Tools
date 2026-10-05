using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>An animation swapped in the editor, kept in the manifest (AnimSwaps): what the slot plays now and where it came
/// from, for the Animations tab (the swap itself is in the package).</summary>
sealed class AnimSwapEntry
{
    public string Package { get; set; } = "";
    /// <summary>The costume's animation that was replaced (idle, movement_run …).</summary>
    public string Slot { get; set; } = "";
    /// <summary>The donor: a game package file (UC__MarvelPlayer_Storm_SF.upk) or a mod's name.</summary>
    public string Donor { get; set; } = "";
    /// <summary>The donor's animation name.</summary>
    public string Animation { get; set; } = "";
    /// <summary>"Storm", "Rescue (Team-Up)" …: the donor as the tab shows it.</summary>
    public string Title { get; set; } = "";
}

/// <summary>
/// Gives a costume another character's animation in place of one of its own (Kurt, 2026-10-02: the Animations tab, phase 2).
/// The game finds an animation by name from the last AnimSet in the costume's mesh component list to the first (stock
/// costumes give themselves their own idle and emotes that way: CostumeAnims), so only the costume's own package changes:
/// per donor set, a copy of the set under a name of its own (its other sequences left out), the chosen sequences copied into
/// it and renamed to the slots they replace, and the set added at the end of the component's list. The hero's base package
/// and every other costume stay as they are.
///
/// Names: an object is shared by its path among loaded packages, so the copied set gets a new name
/// (&lt;set&gt;_on_&lt;costume class&gt;); a copy keeping storm_anim.storm_as would replace Storm's own set whenever both load.
/// </summary>
static class AnimSwap
{
    /// <summary>One swap: the costume's animation <paramref name="Slot"/> becomes the donor package's sequence
    /// <paramref name="DonorSequence"/> (an export of <paramref name="DonorSet"/>).</summary>
    public sealed record Swap(string Slot, string DonorFile, int DonorSet, int DonorSequence);

    /// <summary>
    /// An in-game test for the hero's alternate sets (2026-10-04, a user: Jean Grey's Phoenix animations; CostumeAnims.Aliases):
    /// powers and forms switch to such a set by its alias. An animation's tracks follow its own set's bone order, so a donor's
    /// animation can't simply be listed in a copy of the alternate set (Jean's sets have 110, 138 and 165 bones). This build
    /// tests whether the game layers the alias set over the costume's list (animations it lacks then come from the normal
    /// sets): the alias points at a set of the donor's animations only (named after their slots, in the donor's bone order,
    /// as Build does), and the costume class default gets an AnimationSetAliases list of its own (the hero's, with this alias
    /// on that set; the others keep the hero's sets, imported). In game: the swapped power in that form should play the
    /// donor's animation; the form's other powers show whether the rest falls back (normal versions) or breaks. Only the
    /// costume's package changes.
    /// </summary>
    public static byte[] BuildAlias(string costumePath, string costumeClass, string heroPath, string heroClass, string alias,
        IReadOnlyList<Swap> swaps, List<string> log)
    {
        var pkg = Package.Open(costumePath);
        string cls = costumeClass.ToLowerInvariant();
        var hero = Package.Open(heroPath);
        var heroAliases = CostumeAnims.Aliases(hero, heroClass);
        if (heroAliases.Count == 0) throw new InvalidDataException($"{heroClass} has no alternate animation sets");
        var target = heroAliases.FirstOrDefault(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (target.Alias == null) throw new InvalidDataException($"{heroClass} has no alternate set '{alias}' (it has {string.Join(", ", heroAliases.Select(a => a.Alias))})");
        int heroSet = Array.FindIndex(hero.Exports, e => hero.PathOf(e).Equals(target.SetPath, StringComparison.OrdinalIgnoreCase));
        if (heroSet < 0) throw new InvalidDataException($"{target.SetPath} isn't in {Path.GetFileName(heroPath)}");
        string Unique(string b) { string n = b; for (int k = 2; pkg.Exports.Any(e => e.ObjectName.Equals(n, StringComparison.OrdinalIgnoreCase)); k++) n = $"{b}_{k}"; return n; }

        // 2. the donors' animations, in a set of their own per donor set (named after their slots)
        var donors = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        Package Donor(string f) => donors.TryGetValue(f, out var dp) ? dp : donors[f] = Package.Open(f);
        var holders = new List<(int Set, List<(int Seq, string Slot)> Seqs)>();
        foreach (var group in swaps.GroupBy(x => (x.DonorFile.ToLowerInvariant(), x.DonorSet)))
        {
            var first = group.First();
            var src = Donor(first.DonorFile);
            string dsetPath = src.PathOf(src.Exports[first.DonorSet]);
            string holderName = Unique($"{src.Exports[first.DonorSet].ObjectName}_{alias}_on_{cls}");
            var hc = CrossMove.Quiet(() => ExportCopy.Copy(src, first.DonorSet, pkg, ["sequences"], holderName), out string hs)
                     ?? throw new InvalidDataException($"the set {dsetPath} couldn't be copied: {Reason(hs)}");
            pkg = Package.FromBytes(hc.Output);
            string holderPath = pkg.PathOf(pkg.Exports[hc.RootRef - 1]);
            var seqs = new List<(int, string)>();
            foreach (var x in group)
            {
                var qc = CrossMove.Quiet(() => ExportCopy.Copy(src, x.DonorSequence, pkg, [], null, new Dictionary<string, string> { [dsetPath] = holderPath }), out string qs);
                if (qc == null)
                {
                    qc = CrossMove.Quiet(() => ExportCopy.Copy(src, x.DonorSequence, pkg, ["notifies"], null, new Dictionary<string, string> { [dsetPath] = holderPath }), out string q2)
                         ?? throw new InvalidDataException($"{src.PathOf(src.Exports[x.DonorSequence])} couldn't be copied: {Reason(qs)}");
                    log.Add($"  {x.Slot}: copied without its notifies ({Reason(qs)})");
                }
                pkg = Package.FromBytes(qc.Output);
                seqs.Add((qc.RootRef, x.Slot));
                log.Add($"  {alias} · {x.Slot} ← {src.PathOf(src.Exports[x.DonorSequence])} (#{qc.RootRef})");
            }
            holders.Add((hc.RootRef, seqs));
        }

        // 3. the edit
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, y => y.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(y => y.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return pkg.Names.Length + j;
        }
        var addImports = new List<NewImport>();
        int ImportOf(string path)
        {
            string[] parts = path.Split('.');
            int outer = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                bool last = k == parts.Length - 1;
                string cn = last ? "AnimSet" : "Package", cp = last ? "Engine" : "Core";
                int found = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals(cn, StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer);
                if (found >= 0) { outer = -1 - found; continue; }
                int added = addImports.FindIndex(im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName == cn && im.OuterIndex == outer);
                if (added < 0)
                {
                    foreach (string n in new[] { cp, cn, parts[k] }) NameIdx(n);
                    addImports.Add(new NewImport(cp, cn, outer, parts[k]));
                    added = addImports.Count - 1;
                }
                outer = -(pkg.Imports.Length + added + 1);
            }
            return outer;
        }
        var replace = new Dictionary<int, Func<long, byte[]>>();
        byte[] SetSequences(int set, IReadOnlyList<int> refs)
        {
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[set - 1]).ToArray();
            var st = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(y => y.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException($"the set #{set} has no Sequences list");
            var list = new byte[4 + 4 * refs.Count];
            BinaryPrimitives.WriteInt32LittleEndian(list, refs.Count);
            for (int k = 0; k < refs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(4 + 4 * k), refs[k]);
            byte[] nsd = [.. sd.AsSpan(0, st.ValueAt), .. list, .. sd.AsSpan(st.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(st.Start + 16), list.Length);
            return nsd;
        }
        string SequenceName(int seq)
        {
            byte[] d = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
            var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(y => y.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && y.Size == 8);
            return t == null ? "" : TagWalker.NameAt(pkg, d, t.ValueAt);
        }
        // the donors' animations named after their slots; each holder set lists only them
        foreach (var (set, seqs) in holders)
        {
            foreach (var (seq, slot) in seqs)
            {
                byte[] d = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(y => y.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && y.Size == 8)
                        ?? throw new InvalidDataException($"the copied animation #{seq} has no SequenceName");
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt), NameIdx(slot));
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt + 4), 0);
                replace[seq - 1] = _ => d;
            }
            replace[set - 1] = _ => SetSequences(set, seqs.Select(q => q.Seq).ToList());
        }
        if (holders.Count != 1) throw new InvalidDataException("the test takes the swapped animations from one donor set");
        int aliasSet = holders[0].Set;
        // the costume class default's own alias list: the hero's, this alias on the copy
        string defPath = $"marvelgamecontent.default__{cls}";
        int def = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(defPath, StringComparison.OrdinalIgnoreCase));
        if (def < 0) throw new InvalidDataException($"no {defPath} in the package");
        byte[] dd = pkg.ReadExportBytes(pkg.Exports[def]).ToArray();
        var dtags = TagWalker.Walk(pkg, dd, 4) ?? throw new InvalidDataException("the class default's properties don't read");
        var own = CostumeAnims.Aliases(pkg, cls);
        var entries = (own.Count > 0 ? own : heroAliases).Select(a => (a.Alias, a.SetPath)).ToList();
        if (!entries.Any(e => e.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))) entries.Add((alias, ""));
        byte[] Tag(string name, string type, int size)
        {
            var t = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(0), NameIdx(name));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), NameIdx(type));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), size);
            return t;
        }
        byte[] NameVal(string name) { var b = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(b, NameIdx(name)); return b; }
        var value = new List<byte>();
        value.AddRange(BitConverter.GetBytes(entries.Count));
        foreach (var (al, setPath) in entries)
        {
            int r = al.Equals(alias, StringComparison.OrdinalIgnoreCase) ? aliasSet
                : setPath.StartsWith("import:") ? throw new InvalidDataException($"the alias {al} points at an import ({setPath}): not handled")
                : own.Count > 0 ? Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(setPath, StringComparison.OrdinalIgnoreCase)) + 1   // the costume's own
                : ImportOf(setPath);                                                                                                        // the hero's set
            value.AddRange(Tag("Alias", "NameProperty", 8)); value.AddRange(NameVal(al));
            value.AddRange(Tag("AnimSet", "ObjectProperty", 4)); value.AddRange(BitConverter.GetBytes(r));
            value.AddRange(NameVal("None"));
            log.Add($"  alias {al} → {(r > 0 ? pkg.PathOf(pkg.Exports[r - 1]) : setPath + " (the hero's)")}");
        }
        var old = dtags.FirstOrDefault(y => y.Name.Equals("AnimationSetAliases", StringComparison.OrdinalIgnoreCase));
        byte[] prop = [.. Tag("AnimationSetAliases", "ArrayProperty", value.Count), .. value];
        byte[] ndd = old != null ? [.. dd.AsSpan(0, old.Start), .. prop, .. dd.AsSpan(old.End)] : [.. dd.AsSpan(0, dtags.NoneAt), .. prop, .. dd.AsSpan(dtags.NoneAt)];
        replace[def] = _ => ndd;
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the edited package didn't verify: " + string.Join("; ", problems.Take(3)));
        log.Add($"{swaps.Count} animation(s) swapped in the alternate set '{alias}'; the costume's own alias list has {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
        return output;
    }

    /// <summary>
    /// The costume package with the swaps in, verified (the copies re-parsed by ExportCopy, the final edit by
    /// PackageRebuilder). Throws InvalidDataException with the reason when it can't be done.
    /// </summary>
    /// <param name="inheritedSets">For a costume whose component has no AnimSets list (it plays its hero's): the hero's list
    /// as paths ("biped_lib.blink_as", "ironman_anim.ironman_as" …, CostumeAnims.Sets in order), imported into the package as
    /// the start of its own list, as stock costumes with their own sets do (Storm Astonishing).</param>
    public static byte[] Build(string costumePath, string costumeClass, IReadOnlyList<Swap> swaps, List<string> log, IReadOnlyList<string>? inheritedSets = null)
    {
        var pkg = Package.Open(costumePath);
        string cls = costumeClass.ToLowerInvariant();
        // A slot swapped before: out of its swapped-in set first, so the new one is the only copy.
        if (Unswap(pkg, cls, swaps.Select(s => s.Slot), log) is { } freed) pkg = Package.FromBytes(freed);
        var donors = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        Package Donor(string f) => donors.TryGetValue(f, out var p) ? p : donors[f] = Package.Open(f);
        // Per donor set: the copied set's reference and its copied sequences (reference, slot).
        var newSets = new List<(int Set, List<(int Seq, string Slot)> Seqs)>();
        foreach (var group in swaps.GroupBy(s => (s.DonorFile.ToLowerInvariant(), s.DonorSet)))
        {
            var first = group.First();
            var src = Donor(first.DonorFile);
            string setPath = src.PathOf(src.Exports[first.DonorSet]);
            string setName = src.Exports[first.DonorSet].ObjectName;
            // A name of its own, unique in the package (a second swap from the same set gets _2 …).
            string baseName = $"{setName}_on_{cls}", name = baseName;
            for (int k = 2; pkg.Exports.Any(e => e.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)); k++) name = $"{baseName}_{k}";
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(src, first.DonorSet, pkg, ["sequences"], name), out string said)
                     ?? throw new InvalidDataException($"the animation set {setPath} couldn't be copied: {Reason(said)}");
            pkg = Package.FromBytes(sc.Output);
            string newSetPath = pkg.PathOf(pkg.Exports[sc.RootRef - 1]);
            log.Add($"set {Path.GetFileName(first.DonorFile)} · {setPath} copied as {newSetPath} (#{sc.RootRef})");
            var seqs = new List<(int, string)>();
            foreach (var s in group)
            {
                // Its outer (the donor set) is the copied set: the sequence lands inside it; its notifies come along.
                var qc = CrossMove.Quiet(() => ExportCopy.Copy(src, s.DonorSequence, pkg, [], null, new Dictionary<string, string> { [setPath] = newSetPath }), out string qsaid);
                if (qc == null)
                {
                    // Notifies (sounds, effects timed to the animation) can reach far (a power's particle systems): without them.
                    qc = CrossMove.Quiet(() => ExportCopy.Copy(src, s.DonorSequence, pkg, ["notifies"], null, new Dictionary<string, string> { [setPath] = newSetPath }), out string q2)
                         ?? throw new InvalidDataException($"{src.PathOf(src.Exports[s.DonorSequence])} couldn't be copied: {Reason(qsaid)}");
                    log.Add($"  {s.Slot}: copied without its notifies ({Reason(qsaid)})");
                }
                pkg = Package.FromBytes(qc.Output);
                seqs.Add((qc.RootRef, s.Slot));
                log.Add($"  {s.Slot} ← {src.PathOf(src.Exports[s.DonorSequence])} (#{qc.RootRef})");
            }
            newSets.Add((sc.RootRef, seqs));
        }

        // The edit: each copied sequence named after its slot, each copied set listing only its copied sequences, the
        // component's AnimSets list with the new sets at its end.
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));   // (names are case-insensitive: the table refuses "Engine" next to "engine")
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return pkg.Names.Length + j;
        }
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var addImports = new List<NewImport>();
        // An import by path (each outer a Package, the last an AnimSet), reusing the package's own imports where they exist.
        int ImportOf(string path)
        {
            string[] parts = path.Split('.');
            int outer = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                bool last = k == parts.Length - 1;
                string cn = last ? "AnimSet" : "Package", cp = last ? "Engine" : "Core";
                int found = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals(cn, StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer);
                if (found >= 0) { outer = -1 - found; continue; }
                int added = addImports.FindIndex(im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.ClassName == cn && im.OuterIndex == outer);
                if (added < 0)
                {
                    foreach (string n in new[] { cp, cn, parts[k] }) NameIdx(n);
                    addImports.Add(new NewImport(cp, cn, outer, parts[k]));
                    added = addImports.Count - 1;
                }
                outer = -(pkg.Imports.Length + added + 1);
            }
            return outer;
        }
        foreach (var (set, seqs) in newSets)
        {
            foreach (var (seq, slot) in seqs)
            {
                byte[] d = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(x => x.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && x.Size == 8)
                        ?? throw new InvalidDataException($"the copied animation #{seq} has no SequenceName");
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt), NameIdx(slot));
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt + 4), 0);
                replace[seq - 1] = _ => d;
            }
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[set - 1]).ToArray();
            var st = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(x => x.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException($"the copied set #{set} has no Sequences list");
            var list = new byte[4 + 4 * seqs.Count];
            BinaryPrimitives.WriteInt32LittleEndian(list, seqs.Count);
            for (int k = 0; k < seqs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(4 + 4 * k), seqs[k].Seq);
            byte[] nsd = [.. sd.AsSpan(0, st.ValueAt), .. list, .. sd.AsSpan(st.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(st.Start + 16), list.Length);   // the tag's size
            replace[set - 1] = _ => nsd;
        }
        string compPath = $"marvelgamecontent.default__{cls}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) throw new InvalidDataException($"no {compPath} in the package");
        byte[] cd = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tags = TagWalker.Walk(pkg, cd, 16) ?? throw new InvalidDataException("the mesh component's properties don't read");
        var at = tags.FirstOrDefault(x => x.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
        byte[] add = [.. newSets.SelectMany(x => BitConverter.GetBytes(x.Set))];
        byte[] ncd;
        if (at != null)
        {
            int n = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt));
            ncd = [.. cd.AsSpan(0, at.End), .. add, .. cd.AsSpan(at.End)];
            BinaryPrimitives.WriteInt32LittleEndian(ncd.AsSpan(at.ValueAt), n + newSets.Count);
            BinaryPrimitives.WriteInt32LittleEndian(ncd.AsSpan(at.Start + 16), at.Size + add.Length);
        }
        else
        {
            // No list of its own (it plays its hero's, 374 of 562 stock character packages): the hero's list imported, then
            // the new sets, as an AnimSets tag before the component's None.
            if (inheritedSets == null || inheritedSets.Count == 0) throw new InvalidDataException("this costume has no animation list of its own and its hero's wasn't given");
            var refs = inheritedSets.Select(ImportOf).ToList();
            byte[] value = [.. BitConverter.GetBytes(refs.Count + newSets.Count), .. refs.SelectMany(BitConverter.GetBytes), .. add];
            var tag = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("AnimSets"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), value.Length);
            int noneAt = tags.NoneAt;
            ncd = [.. cd.AsSpan(0, noneAt), .. tag, .. value, .. cd.AsSpan(noneAt)];
            log.Add($"the costume's own animation list: its hero's {refs.Count} set(s) ({string.Join(", ", inheritedSets)}), then the new one(s)");
        }
        replace[comp] = _ => ncd;
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the edited package didn't verify: " + string.Join("; ", problems.Take(3)));
        log.Add($"{swaps.Count} animation(s) swapped; {newSets.Count} set(s) added to the costume's list");
        return output;
    }

    /// <summary>
    /// Back to the original: <paramref name="slots"/> taken out of the sets swaps added (exports of the package in its
    /// component's list, named …_on_…), so the name is found in the hero's (or costume's own) set again. The copied
    /// sequences stay in the file, unlisted (the game looks only through a set's Sequences). Null when no slot was swapped.
    /// </summary>
    public static byte[]? Unswap(Package pkg, string costumeClass, IEnumerable<string> slots, List<string> log)
    {
        var want = slots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string compPath = $"marvelgamecontent.default__{costumeClass.ToLowerInvariant()}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) return null;
        byte[] cd = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var at = TagWalker.Walk(pkg, cd, 16)?.FirstOrDefault(x => x.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
        if (at == null) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt));
        var replace = new Dictionary<int, Func<long, byte[]>>();
        for (int k = 0; k < n; k++)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt + 4 + 4 * k));
            if (r <= 0 || !pkg.Exports[r - 1].ObjectName.Contains("_on_", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[r - 1]).ToArray();
            var st = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(x => x.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase));
            if (st == null) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt));
            var keep = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int q = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt + 4 + 4 * i));
                string? name = null;
                if (q > 0)
                {
                    byte[] qd = pkg.ReadExportBytes(pkg.Exports[q - 1]).ToArray();
                    if (TagWalker.Walk(pkg, qd, 4)?.FirstOrDefault(x => x.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && x.Size == 8) is { } nt)
                        name = TagWalker.NameAt(pkg, qd, nt.ValueAt);
                }
                if (name != null && want.Contains(name)) { log.Add($"{name}: out of {pkg.PathOf(pkg.Exports[r - 1])}"); continue; }
                keep.Add(q);
            }
            if (keep.Count == count) continue;
            byte[] list = [.. BitConverter.GetBytes(keep.Count), .. keep.SelectMany(BitConverter.GetBytes)];
            byte[] nsd = [.. sd.AsSpan(0, st.ValueAt), .. list, .. sd.AsSpan(st.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(st.Start + 16), list.Length);
            replace[r - 1] = _ => nsd;
        }
        if (replace.Count == 0) return null;
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the edited package didn't verify: " + string.Join("; ", problems.Take(3)));
        return output;
    }

    static string Reason(string said) => string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not ")).Select(l => l.Trim())).Trim() is { Length: > 0 } r ? r : "see the log";
}
