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
