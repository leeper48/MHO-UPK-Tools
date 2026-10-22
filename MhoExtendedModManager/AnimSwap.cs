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
    /// <paramref name="DonorSequence"/> (an export of <paramref name="DonorSet"/>); <paramref name="Rate"/> &gt; 0 sets the copy's
    /// own play rate (a borrowed power's animation fitted to the length of the one it replaces), else Build's rateScale;
    /// <paramref name="Lift"/> moves the whole animation up (down when negative) by its root bone.</summary>
    public sealed record Swap(string Slot, string DonorFile, int DonorSet, int DonorSequence, float Rate = 0, float Lift = 0, int Ramp = 0);

    /// <summary>Vehicle Fit: the rider (the body from g_spine01 down) moved forward / up (game units) and leaned forward
    /// (degrees) on a borrowed ride's vehicle, which stays where its own animation puts it.</summary>
    public sealed record RiderFit(float Forward, float Up, float LeanDegrees)
    {
        public bool IsNone => Math.Abs(Forward) < 0.01f && Math.Abs(Up) < 0.01f && Math.Abs(LeanDegrees) < 0.01f;
    }

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
    /// <param name="whole">The second test (2026-10-05; Kurt's first one: the form's set is used alone, nothing falls back):
    /// the alias points at a copy of the hero's whole alternate set, with the swapped animations in place of its own, each
    /// donor animation's tracks put in that set's bone order (<see cref="FitTracks"/>).</param>
    public static byte[] BuildAlias(string costumePath, string costumeClass, string heroPath, string heroClass, string alias,
        IReadOnlyList<Swap> swaps, List<string> log, bool whole = false)
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
        // whole: the hero's alternate set copied with all its animations; the donors' go in after (fitted below)
        var fits = new Dictionary<int, (string Donor, int DonorSet, int DonorSeq, string Slot)>();
        int wholeSet = 0;
        if (whole)
        {
            string wholeName = Unique($"{hero.Exports[heroSet].ObjectName}_on_{cls}");
            var wc = CrossMove.Quiet(() => ExportCopy.Copy(hero, heroSet, pkg, [], wholeName), out string ws)
                     ?? throw new InvalidDataException($"the set {target.SetPath} couldn't be copied: {Reason(ws)}");
            pkg = Package.FromBytes(wc.Output);
            wholeSet = wc.RootRef;
            string wholePath = pkg.PathOf(pkg.Exports[wholeSet - 1]);
            log.Add($"  {alias}: {target.SetPath} copied whole as {wholePath}");
            foreach (var x in swaps)
            {
                var src = Donor(x.DonorFile);
                string dsetPath = src.PathOf(src.Exports[x.DonorSet]);
                // a name of its own: under the copied set, the donor's animsequence_0 would be the path of the set's own
                // animsequence_0, and the copy would find that one instead of copying (Storm's idle came out as Jean's)
                string seqName = Unique($"{src.Exports[x.DonorSequence].ObjectName}_{x.Slot}_swap");
                var qc = CrossMove.Quiet(() => ExportCopy.Copy(src, x.DonorSequence, pkg, ["notifies"], seqName, new Dictionary<string, string> { [dsetPath] = wholePath }), out string qs)
                         ?? throw new InvalidDataException($"{src.PathOf(src.Exports[x.DonorSequence])} couldn't be copied: {Reason(qs)}");
                pkg = Package.FromBytes(qc.Output);
                fits[qc.RootRef] = (x.DonorFile, x.DonorSet, x.DonorSequence, x.Slot);
                log.Add($"  {alias} · {x.Slot} ← {src.PathOf(src.Exports[x.DonorSequence])} (#{qc.RootRef}, without its notifies)");
            }
        }
        else
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
        if (whole)
        {
            // the copy's own list, with each swapped slot's animation replaced by the donor's (fitted to the set's bones)
            var hp = AnimExportCli.Packages.Package.Open(heroPath);
            var heroInfo = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(hp).First(i => i.ExportIndex == heroSet);
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[wholeSet - 1]).ToArray();
            var st = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(y => y.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("the copied set has no Sequences list");
            var list = new List<int>();
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt)); k < n; k++) list.Add(BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt + 4 + 4 * k)));
            foreach (var (seq, f) in fits)
            {
                int at = list.FindIndex(r => r > 0 && SequenceName(r).Equals(f.Slot, StringComparison.OrdinalIgnoreCase));
                if (at < 0) throw new InvalidDataException($"{target.SetPath} has no animation '{f.Slot}' to replace (it has {string.Join(", ", list.Where(r => r > 0).Select(SequenceName).Order())})");
                // the original (for the bones the donor lacks): the hero's sequence of that name
                int orig = heroInfo.Sequences.Where(r => r.IsExport).Select(r => r.ExportIndex)
                    .First(i => AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(hp, i).Equals(f.Slot, StringComparison.OrdinalIgnoreCase));
                var original = AnimExportCli.Animation.AnimObjectReader.TryRead(hp, orig, heroInfo.TrackBoneNames);
                var dp = AnimExportCli.Packages.Package.Open(f.Donor);
                var donorNames = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(dp).First(i => i.ExportIndex == f.DonorSet).TrackBoneNames;
                byte[] d = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                d = FitTracks(pkg, d, donorNames, heroInfo.TrackBoneNames, original, log, f.Slot);
                var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(y => y.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && y.Size == 8)
                        ?? throw new InvalidDataException($"the copied animation #{seq} has no SequenceName");
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt), NameIdx(f.Slot));
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt + 4), 0);
                replace[seq - 1] = _ => d;
                list[at] = seq;
            }
            replace[wholeSet - 1] = _ => SetSequences(wholeSet, list);
            holders.Add((wholeSet, []));
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
                : setPath.StartsWith("import:") ? (own.Count > 0 ? OwnImport(setPath[7..]) : ImportOf(HeroImportPath(setPath[7..])))
                : own.Count > 0 ? Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(setPath, StringComparison.OrdinalIgnoreCase)) + 1   // the costume's own
                : ImportOf(setPath);                                                                                                        // the hero's set
            value.AddRange(Tag("Alias", "NameProperty", 8)); value.AddRange(NameVal(al));
            value.AddRange(Tag("AnimSet", "ObjectProperty", 4)); value.AddRange(BitConverter.GetBytes(r));
            value.AddRange(NameVal("None"));
            log.Add($"  alias {al} → {(r > 0 ? pkg.PathOf(pkg.Exports[r - 1]) : setPath + " (the hero's)")}");
        }
        // an alias the hero's list points at an import (Jean's darkphoenixas → phoenix_as, a set of another package): the same
        // import, by its full path
        int OwnImport(string name)   // the costume's own list naming an import: that import, as it is
        {
            int i = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? -1 - i : throw new InvalidDataException($"the costume's alias set {name} isn't among its imports");
        }
        string HeroImportPath(string name)
        {
            int i = Array.FindIndex(hero.Imports, im => im.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new InvalidDataException($"the hero's alias set {name} isn't among its imports");
            var parts = new List<string> { hero.Imports[i].ObjectName };
            for (int o = hero.Imports[i].OuterIndex; o != 0;)
            {
                if (o > 0) throw new InvalidDataException($"the hero's alias set {name} sits inside an export: not handled");
                parts.Insert(0, hero.Imports[-o - 1].ObjectName); o = hero.Imports[-o - 1].OuterIndex;
            }
            return string.Join(".", parts);
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
    /// A donor animation's tracks put in another set's bone order (2026-10-05: Jean Grey's Phoenix set with Storm's idle):
    /// each track's bytes stay where they are in the compressed stream; only CompressedTrackOffsets (4 ints per track:
    /// translation offset and keys, rotation offset and keys) is rebuilt in the target's order. A bone the donor has no track
    /// for holds one key, the original animation's first (else none: the bone's reference pose), appended to the stream in
    /// the form single keys always take (3 floats; a rotation as Float96NoW, conjugated, or 4 floats for ACF_None). The
    /// donor's bones the target lacks are left out. Per-track compression isn't handled yet.
    /// </summary>
    internal static byte[] FitTracks(Package pkg, byte[] d, IReadOnlyList<string> donorNames, IReadOnlyList<string> targetNames,
        AnimExportCli.Animation.BoneAnimation? original, List<string> log, string label)
    {
        var tags = TagWalker.Walk(pkg, d, 4) ?? throw new InvalidDataException($"{label}: the animation's properties don't read");
        string NameOf(string prop)
        {
            var t = tags.FirstOrDefault(y => y.Name.Equals(prop, StringComparison.OrdinalIgnoreCase));
            return t == null ? "" : TagWalker.NameAt(pkg, d, t.ValueAt + (t.Size == 16 ? 8 : 0));   // a byte enum: its enum name first
        }
        string key = NameOf("KeyEncodingFormat"), rot = NameOf("RotationCompressionFormat");
        if (key.Equals("AKF_PerTrackCompression", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{label}: per-track compressed animations can't be fitted to another set yet");
        bool rotNone = rot.Equals("ACF_None", StringComparison.OrdinalIgnoreCase);
        var ot = tags.FirstOrDefault(y => y.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidDataException($"{label}: no CompressedTrackOffsets");
        int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt));
        // tracks line up with the set's bones by index; an animation can have more (Storm's idle: 165 for 161 bones, left
        // over from an older bone list: the game reads only the set's) or fewer (the bones past its end have none)
        if (count % 4 != 0) throw new InvalidDataException($"{label}: CompressedTrackOffsets holds {count} ints, not 4 per track");
        if (count != donorNames.Count * 4) log.Add($"  {label}: {count / 4} tracks for the donor set's {donorNames.Count} bones (tracks past the bones are ignored, as the game does)");
        int[] offs = new int[count];
        for (int k = 0; k < count; k++) offs[k] = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt + 4 + 4 * k));
        // the native part: the raw tracks (empty when cooked), then the compressed stream, then whatever follows
        int at = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
        int p = at + 4;
        for (int k = 0; k < raw; k++) { p += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); }
        int streamAt = p + 4, streamLen = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        var stream = new List<byte>(d.AsSpan(streamAt, streamLen).ToArray());
        while (stream.Count % 4 != 0) stream.Add(0);
        void F(float v) { var b = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); stream.AddRange(b); }
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < Math.Min(donorNames.Count, count / 4); k++) index.TryAdd(donorNames[k], k);
        var fitted = new List<int>();
        int held = 0, refPose = 0;
        foreach (string bone in targetNames)
        {
            if (index.TryGetValue(bone, out int k)) { fitted.AddRange(offs.AsSpan(k * 4, 4).ToArray()); continue; }
            if (original != null && original.Tracks.TryGetValue(bone, out var tr) && tr.RotationKeys.Count > 0)
            {
                bool hasPos = tr.PositionKeys.Count > 0;
                int pAt = stream.Count;
                if (hasPos) { var pos = tr.PositionKeys[0].Position; F(pos.X); F(pos.Y); F(pos.Z); }
                var q = tr.RotationKeys[0].Rotation;
                int rAt = stream.Count;
                if (rotNone) { F(q.X); F(q.Y); F(q.Z); F(q.W); }
                else { if (q.W < 0) q = -q; F(-q.X); F(-q.Y); F(-q.Z); }   // stored conjugated, W the positive root
                fitted.AddRange([hasPos ? pAt : -1, hasPos ? 1 : 0, rAt, 1]);
                held++;
            }
            else { fitted.AddRange([-1, 0, -1, 0]); refPose++; }
        }
        int dropped = donorNames.Count(n => !targetNames.Contains(n, StringComparer.OrdinalIgnoreCase));
        int moved = targetNames.Select((n, i) => index.TryGetValue(n, out int k) && k != i).Count(x => x);
        log.Add($"  {label}: {moved} track(s) at another position than in the donor's set");
        log.Add($"  {label}: held: {string.Join(" ", targetNames.Where(n => !index.ContainsKey(n)))}");
        log.Add($"  {label}: {targetNames.Count - held - refPose} of the set's {targetNames.Count} bones from the donor, {held} held at the original's first key, {refPose} at the reference pose; {dropped} donor bone(s) the set doesn't have left out");
        var arr = new byte[4 + 4 * fitted.Count];
        BinaryPrimitives.WriteInt32LittleEndian(arr, fitted.Count);
        for (int k = 0; k < fitted.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(arr.AsSpan(4 + 4 * k), fitted[k]);
        var lenBytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(lenBytes, stream.Count);
        byte[] o = [.. d.AsSpan(0, ot.ValueAt), .. arr, .. d.AsSpan(ot.End, p - ot.End), .. lenBytes, .. stream, .. d.AsSpan(streamAt + streamLen)];
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(ot.Start + 16), arr.Length);
        return o;
    }

    /// <summary>
    /// The costume package with the swaps in, verified (the copies re-parsed by ExportCopy, the final edit by
    /// PackageRebuilder). Throws InvalidDataException with the reason when it can't be done.
    /// </summary>
    /// <param name="inheritedSets">For a costume whose component has no AnimSets list (it plays its hero's): the hero's list
    /// as paths ("biped_lib.blink_as", "ironman_anim.ironman_as" …, CostumeAnims.Sets in order), imported into the package as
    /// the start of its own list, as stock costumes with their own sets do (Storm Astonishing).</param>
    /// <param name="rateScale">The copies' play rate (Model tab's Size in Game: StepRate); 1 leaves it as the donor's.</param>
    /// <param name="retarget">Per bone: the turn that takes the donor skeleton's rest rotation to the target's
    /// (RestTurns), for bones whose rest differs; their rotation keys in the copies are turned by it.</param>
    public static byte[] Build(string costumePath, string costumeClass, IReadOnlyList<Swap> swaps, List<string> log, IReadOnlyList<string>? inheritedSets = null, float rateScale = 1,
        IReadOnlyDictionary<string, System.Numerics.Quaternion>? retarget = null, RiderFit? rider = null, IReadOnlyList<AnimExportCli.Meshes.MeshBone>? skeleton = null)
    {
        var body = BodyRoots(skeleton);
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
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[set - 1]).ToArray();
            bool lifted = false, riderMoved = false;
            var bodyMoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (seq, slot) in seqs)
            {
                byte[] d = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                var sw = swaps.FirstOrDefault(x => x.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
                float lift = sw?.Lift ?? 0;
                if (lift != 0 && sw!.Ramp != 0)
                {
                    // ramped in (a start: 0 to lift) or out (an end: lift to 0) over the animation
                    // (on the body's bone, not the root: a root that moves over time is root motion, and the game moved the whole
                    // hero down into the floor: Spider-Man blinked out; Kurt 2026-10-22)
                    float from = sw.Ramp > 0 ? 0 : lift, to = sw.Ramp > 0 ? lift : 0;
                    if (MoveBody(pkg, sd, d, body, (f, n) => new System.Numerics.Vector3(0, 0, from + (to - from) * f / Math.Max(1, n - 1)), bodyMoved, out string rwhy0) is { } rd) { d = rd; log.Add($"  {slot}: lowered {(sw.Ramp > 0 ? "0 to " + lift.ToString("0") : lift.ToString("0") + " to 0")} over the animation"); }
                    else log.Add($"  {slot}: not moved {lift:0} ({rwhy0})");
                }
                else if (lift != 0)
                {
                    if (LiftRoot(pkg, sd, d, lift, out string why)) lifted = true;
                    else log.Add($"  {slot}: not moved {lift:0} ({why})");
                }
                if (rider is { IsNone: false })
                {
                    // forward / up: the whole body (its bones under the root: hips, spine, IK targets); lean: the upper body
                    if ((Math.Abs(rider.Forward) > 0.01f || Math.Abs(rider.Up) > 0.01f) && body.Count > 0)
                    {
                        var shift = new System.Numerics.Vector3(0, rider.Forward * ForwardSign, rider.Up);
                        if (MoveBody(pkg, sd, d, body, (_, _) => shift, bodyMoved, out string bwhy) is { } bd) d = bd;
                        else log.Add($"  {slot}: the rider isn't moved ({bwhy})");
                    }
                    if (FitRider(pkg, sd, d, rider with { Forward = body.Count > 0 ? 0 : rider.Forward, Up = body.Count > 0 ? 0 : rider.Up }, out string fwhy)) riderMoved = true;
                    else log.Add($"  {slot}: the rider isn't leaned ({fwhy})");
                }
                if (retarget is { Count: > 0 })
                {
                    var turned = Retarget(pkg, sd, d, retarget, out string rwhy);
                    if (turned.Count > 0) log.Add($"  {slot}: turned to this skeleton's rest: {string.Join(", ", turned)}");
                    if (rwhy.Length > 0) log.Add($"  {slot}: not turned: {rwhy}");
                }
                var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(x => x.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && x.Size == 8)
                        ?? throw new InvalidDataException($"the copied animation #{seq} has no SequenceName");
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt), NameIdx(slot));
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(t.ValueAt + 4), 0);
                float rate = swaps.FirstOrDefault(x => x.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase))?.Rate is float own && own > 0 ? own : rateScale;
                if (Math.Abs(rate - 1) > 1e-5) d = MhoExtendedModManager.Model.StepRate.WithRate(pkg, d, rate, NameIdx);
                byte[] dd = d;
                replace[seq - 1] = _ => dd;
            }
            foreach (string bm in bodyMoved) sd = WithTranslationBone(pkg, sd, bm, NameIdx);
            if (riderMoved) { sd = WithTranslationBone(pkg, sd, RiderBone, NameIdx); if (rider is { IsNone: false }) log.Add($"  the rider moved {rider.Forward:0.#} forward, {rider.Up:0.#} up, leaned {rider.LeanDegrees:0.#} degrees"); }
            if (lifted) { sd = WithTranslationBone(pkg, sd, "root", NameIdx); log.Add($"  the set takes the root bone's position (moved {swaps.Max(x => Math.Abs(x.Lift)):0})"); }
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
    /// Moves a copied sequence (<paramref name="d"/>, edited in place) by <paramref name="lift"/> on Z through its root track's
    /// translation keys: only for plain float keys (TranslationCompressionFormat ACF_None, not per-track), which is how the
    /// game's riding animations store them (Cyclops' movement_ridebike_*: root one key at 0). The set must then list root
    /// among its UseTranslationBoneNames (WithTranslationBone), or the game keeps the root at its rest position.
    /// </summary>
    static bool LiftRoot(Package pkg, byte[] set, byte[] d, float lift, out string why)
    {
        why = "";
        var stags = TagWalker.Walk(pkg, set, 4);
        var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
        if (tb == null) { why = "the set has no bone list"; return false; }
        int nb = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt)), root = -1;
        for (int k = 0; k < nb && root < 0; k++)
            if (pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt + 4 + 8 * k))].Equals("root", StringComparison.OrdinalIgnoreCase)) root = k;
        if (root < 0) { why = "no root track"; return false; }
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tags == null) { why = "the animation's properties don't read"; return false; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = "positions stored as " + tf; return false; }
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return false; }
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        if (ot == null) { why = "no track offsets"; return false; }
        int no = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt));
        if (no != 4 * nb) { why = $"{no} track offsets for {nb} bones"; return false; }
        int at = ot.ValueAt + 4 + 16 * root;
        int tOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)), tKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
        if (tOff < 0 || tKeys < 1) { why = "the root has no position keys"; return false; }
        // native data: the raw tracks (skipped), then the compressed stream (count + bytes)
        int p = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (raw < 0 || raw > 100000) { why = "the raw tracks don't read"; return false; }
        for (int k = 0; k < raw; k++)
        {
            p += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        }
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (len < 0 || p + len > d.Length || tOff + 12 * tKeys > len) { why = "the key data doesn't read"; return false; }
        for (int k = 0; k < tKeys; k++)
        {
            int z = p + tOff + 12 * k + 8;
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(z), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(z)) + lift);
        }
        return true;
    }

    /// <summary>
    /// The turns that take one skeleton's rest rotations to another's, for bones both have whose rest rotations differ by
    /// more than 45 degrees (Kurt, 2026-10-20: Hawkeye's Sky-Cycle ride on Thor folded his right toes: Thor's g_r_ball rests
    /// turned 180 degrees about its own axis from Hawkeye's; the left one matches). Animations store rotations only, so a
    /// key meant for the donor's rest lands wrong on the target's: key x (donor rest^-1 x target rest) keeps the motion and
    /// starts from the target's rest.
    /// </summary>
    public static Dictionary<string, System.Numerics.Quaternion> RestTurns(IReadOnlyList<AnimExportCli.Meshes.MeshBone> donor, IReadOnlyList<AnimExportCli.Meshes.MeshBone> target)
    {
        var turns = new Dictionary<string, System.Numerics.Quaternion>(StringComparer.OrdinalIgnoreCase);
        var t = target.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Orientation, StringComparer.OrdinalIgnoreCase);
        foreach (var b in donor)
        {
            if (!t.TryGetValue(b.Name, out var rt)) continue;
            var rd = System.Numerics.Quaternion.Normalize(b.Orientation);
            rt = System.Numerics.Quaternion.Normalize(rt);
            float dot = MathF.Abs(System.Numerics.Quaternion.Dot(rd, rt));
            if (dot > MathF.Cos(MathF.PI / 8)) continue;   // within 45 degrees
            turns[b.Name] = System.Numerics.Quaternion.Normalize(Mul(System.Numerics.Quaternion.Conjugate(rd), rt));
        }
        return turns;
    }

    /// <summary>
    /// The bones the body hangs from: every bone directly under the root but the attach points vehicles and props hang from
    /// (g_throwable_attach..., g_mountattach...). The legs aren't under g_spine01: the hips (g_l/r_hip_offset) and the leg IK
    /// targets sit under the root too (Kurt, 2026-10-22: the lowered start dropped the upper body, the legs stretched 10 feet
    /// up to the lifted root).
    /// </summary>
    static List<(string Name, System.Numerics.Vector3 RestPos)> BodyRoots(IReadOnlyList<AnimExportCli.Meshes.MeshBone>? skeleton)
    {
        var list = new List<(string, System.Numerics.Vector3)>();
        if (skeleton is not { Count: > 1 }) return list;
        for (int i = 1; i < skeleton.Count; i++)
        {
            var b = skeleton[i];
            if (b.ParentIndex != 0) continue;
            string n = b.Name.ToLowerInvariant();
            if (n.Contains("throwable") || n.Contains("mount") || n.Contains("weapon") || n.Contains("attach") || n.Contains("prop")) continue;
            list.Add((b.Name, b.Position));
        }
        return list;
    }

    /// <summary>
    /// The body's bones (<paramref name="body"/>) moved in a copied sequence by <paramref name="delta"/>(frame, frames) in the
    /// root's frame (Y forward, Z up). A bone the set takes positions for keeps its keys plus the move; one it doesn't (its
    /// keys aren't used) starts from the target's rest place. A track with one key and a move that changes over time gets two
    /// keys (first and last frame), appended. Plain float positions only. The moved bones go into
    /// <paramref name="moved"/> (they must take positions: WithTranslationBone). Returns the new data, or null with the reason.
    /// </summary>
    static byte[]? MoveBody(Package pkg, byte[] set, byte[] d, List<(string Name, System.Numerics.Vector3 RestPos)> body, Func<float, int, System.Numerics.Vector3> delta, HashSet<string> moved, out string why)
    {
        why = "";
        if (body.Count == 0) { why = "the hero's skeleton isn't known"; return null; }
        var stags = TagWalker.Walk(pkg, set, 4);
        var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
        var ut = stags?.FirstOrDefault(x => x.Name.Equals("UseTranslationBoneNames", StringComparison.OrdinalIgnoreCase));
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tb == null || tags == null) { why = "the animation doesn't read"; return null; }
        int nb = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt));
        var trackOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < nb; k++) trackOf.TryAdd(pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt + 4 + 8 * k))], k);
        var usesPos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ut != null)
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(ut.ValueAt)); k < n; k++) usesPos.Add(pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(ut.ValueAt + 4 + 8 * k))]);
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = "positions stored as " + tf; return null; }
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return null; }
        bool variable = kf.Equals("akf_variablekeylerp", StringComparison.OrdinalIgnoreCase);
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        var nf = tags.FirstOrDefault(x => x.Name.Equals("NumFrames", StringComparison.OrdinalIgnoreCase));
        if (ot == null || nf == null || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * nb) { why = "the track offsets don't match the bones"; return null; }
        int frames = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nf.ValueAt)));
        int q = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)); q += 4;
        for (int k = 0; k < raw; k++)
        {
            q += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
            q += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
        }
        int countAt = q, len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)), streamAt = q + 4;
        var stream = new MemoryStream();
        stream.Write(d, streamAt, len);
        byte[] s0 = stream.ToArray();
        var data = new MemoryStream(); data.Write(s0);
        void Pad() { while (data.Length % 4 != 0) data.WriteByte(0); }
        bool changes = delta(0, frames) != delta(frames - 1, frames);
        var newOffsets = new Dictionary<int, (int Off, int Keys)>();
        foreach (var (name, rest) in body)
        {
            if (!trackOf.TryGetValue(name, out int track)) continue;
            int at = ot.ValueAt + 4 + 16 * track;
            int tOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)), tKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
            bool own = usesPos.Contains(name) && tOff >= 0 && tKeys >= 1 && tOff + 12 * tKeys <= len;
            System.Numerics.Vector3 Key(int k) => own
                ? new System.Numerics.Vector3(BitConverter.ToSingle(s0, tOff + 12 * k), BitConverter.ToSingle(s0, tOff + 12 * k + 4), BitConverter.ToSingle(s0, tOff + 12 * k + 8))
                : rest;
            if (own && (tKeys >= 2 || !changes))
            {
                // in place: each key moved by the move at its frame
                int timesAt = variable ? (tOff + 12 * tKeys + 3) & ~3 : 0;
                for (int k = 0; k < tKeys; k++)
                {
                    float fr = tKeys < 2 ? 0 : !variable ? (float)k * (frames - 1) / (tKeys - 1) : frames > 0xFF ? BitConverter.ToUInt16(s0, timesAt + 2 * k) : s0[timesAt + k];
                    var v = Key(k) + delta(fr, frames);
                    byte[] raw3 = [.. BitConverter.GetBytes(v.X), .. BitConverter.GetBytes(v.Y), .. BitConverter.GetBytes(v.Z)];
                    data.Position = tOff + 12 * k; data.Write(raw3); data.Position = data.Length;
                }
            }
            else
            {
                // new keys at the end: one (a steady move) or first and last frame (a move over time)
                Pad();
                int off = (int)data.Length, keys = changes ? 2 : 1;
                var b0 = Key(0);
                for (int k = 0; k < keys; k++)
                {
                    var v = b0 + delta(k == 0 ? 0 : frames - 1, frames);
                    data.Write(BitConverter.GetBytes(v.X)); data.Write(BitConverter.GetBytes(v.Y)); data.Write(BitConverter.GetBytes(v.Z));
                }
                if (keys == 2 && variable)
                {
                    Pad();
                    if (frames > 0xFF) { data.Write(BitConverter.GetBytes((ushort)0)); data.Write(BitConverter.GetBytes((ushort)(frames - 1))); }
                    else { data.WriteByte(0); data.WriteByte((byte)(frames - 1)); }
                    Pad();
                }
                newOffsets[track] = (off, keys);
            }
            moved.Add(name);
        }
        Pad();
        byte[] ns = data.ToArray();
        byte[] o = [.. d.AsSpan(0, countAt), .. BitConverter.GetBytes(ns.Length), .. ns, .. d.AsSpan(streamAt + len)];
        foreach (var (track, (off, keys)) in newOffsets)
        {
            int at = ot.ValueAt + 4 + 16 * track;
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(at), off);
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(at + 4), keys);
        }
        return o;
    }

    /// <summary>The bone the rider's whole body hangs from (pelvis, legs, spine): every player skeleton has it under
    /// g_spine01_offset, in the root's frame (checked on Thor, Spider-Man, Cyclops, Captain America: local Z up, Y front /
    /// back, X sideways). A ride's vehicle hangs from root-level bones (g_throwable_attach, root), so it stays.</summary>
    const string RiderBone = "g_spine01";

    /// <summary>
    /// Vehicle Fit on one copied sequence (<paramref name="d"/>, edited in place): the rider bone's position keys moved
    /// (forward along its frame's Y, up along Z) and its rotation keys turned about its frame's X (lean). Plain float keys
    /// only (ACF_None positions, Float96NoW rotations; not per-track).
    /// </summary>
    static bool FitRider(Package pkg, byte[] set, byte[] d, RiderFit fit, out string why)
    {
        why = "";
        var stags = TagWalker.Walk(pkg, set, 4);
        var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tb == null || tags == null) { why = "the animation doesn't read"; return false; }
        int nb = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt)), bone = -1;
        for (int k = 0; k < nb && bone < 0; k++)
            if (pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt + 4 + 8 * k))].Equals(RiderBone, StringComparison.OrdinalIgnoreCase)) bone = k;
        if (bone < 0) { why = $"no {RiderBone} track"; return false; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), rf = Enum("RotationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return false; }
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = "positions stored as " + tf; return false; }
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        if (ot == null || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * nb) { why = "the track offsets don't match the bones"; return false; }
        int at = ot.ValueAt + 4 + 16 * bone;
        int tOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)), tKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
        int rOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 8)), rKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 12));
        if (tOff < 0 || tKeys < 1) { why = $"{RiderBone} has no position keys"; return false; }
        bool float96 = rf.Length == 0 || rf.Equals("acf_float96now", StringComparison.OrdinalIgnoreCase);
        if (Math.Abs(fit.LeanDegrees) > 0.01f && rKeys > 1 && !float96) { why = "rotations stored as " + rf; return false; }
        int p = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (raw < 0 || raw > 100000) { why = "the raw tracks don't read"; return false; }
        for (int k = 0; k < raw; k++)
        {
            p += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        }
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (tOff + 12 * tKeys > len || (rKeys > 0 && rOff + 12 * rKeys > len)) { why = "the key data doesn't read"; return false; }
        for (int k = 0; k < tKeys; k++)
        {
            int y = p + tOff + 12 * k + 4, z = y + 4;
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(y), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(y)) + fit.Forward * ForwardSign);
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(z), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(z)) + fit.Up);
        }
        if (Math.Abs(fit.LeanDegrees) > 0.01f && rKeys > 0)
        {
            float half = fit.LeanDegrees * LeanSign * MathF.PI / 360f;
            var turn = new System.Numerics.Quaternion(MathF.Sin(half), 0, 0, MathF.Cos(half));   // about the frame's X
            for (int k = 0; k < rKeys; k++)
            {
                int q = p + rOff + 12 * k;
                float x = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q)), y = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q + 4)), z = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q + 8));
                float ls = x * x + y * y + z * z;
                var key = new System.Numerics.Quaternion(-x, -y, -z, ls < 1 ? MathF.Sqrt(1 - ls) : 0);   // stored: the conjugate
                var nq = System.Numerics.Quaternion.Normalize(Mul(turn, key));
                if (nq.W < 0) nq = -nq;
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q), -nq.X);
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 4), -nq.Y);
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 8), -nq.Z);
            }
        }
        return true;
    }

    /// <summary>Which way +Y and +X turns of the rider bone's frame go in the model (measured 2026-10-21 on Spider-Man with Cyclops' ride: +Y moved the pelvis forward, a +X turn leaned the head back, so lean is negated; forward
    /// = toward the model's +X, where characters face; lean forward = head toward +X).</summary>
    internal static float ForwardSign = float.TryParse(Environment.GetEnvironmentVariable("MHO_RIDER_FWD_SIGN"), out float fs) ? fs : 1;
    internal static float LeanSign = float.TryParse(Environment.GetEnvironmentVariable("MHO_RIDER_LEAN_SIGN"), out float ls0) ? ls0 : -1;

    /// <summary>The Hamilton product a x b.</summary>
    static System.Numerics.Quaternion Mul(System.Numerics.Quaternion a, System.Numerics.Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>
    /// Turns a copied sequence's rotation keys (<paramref name="d"/>, edited in place) for the bones in <paramref name="turns"/>:
    /// key x turn. Only keys stored as three floats (Float96NoW, which every single-key track is; the engine stores the
    /// conjugate, W derived and positive). Returns the bones turned; <paramref name="why"/> names those left as they were.
    /// </summary>
    static List<string> Retarget(Package pkg, byte[] set, byte[] d, IReadOnlyDictionary<string, System.Numerics.Quaternion> turns, out string why)
    {
        why = "";
        var done = new List<string>();
        var stags = TagWalker.Walk(pkg, set, 4);
        var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tb == null || tags == null) { why = "the animation doesn't read"; return done; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string rf = Enum("RotationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return done; }
        bool float96 = rf.Length == 0 || rf.Equals("acf_float96now", StringComparison.OrdinalIgnoreCase);
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        int nb = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt));
        if (ot == null || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * nb) { why = "the track offsets don't match the bones"; return done; }
        int p = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        if (raw < 0 || raw > 100000) { why = "the raw tracks don't read"; return done; }
        for (int k = 0; k < raw; k++)
        {
            p += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        }
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
        var skipped = new List<string>();
        for (int i = 0; i < nb; i++)
        {
            string bone = pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt + 4 + 8 * i))];
            if (!turns.TryGetValue(bone, out var turn)) continue;
            int at = ot.ValueAt + 4 + 16 * i;
            int rOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 8)), rKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 12));
            if (rOff < 0 || rKeys < 1) continue;
            if (rKeys > 1 && !float96) { skipped.Add($"{bone} ({rf})"); continue; }
            if (rOff + 12 * rKeys > len) { skipped.Add($"{bone} (past the data)"); continue; }
            for (int k = 0; k < rKeys; k++)
            {
                int q = p + rOff + 12 * k;
                float x = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q)), y = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q + 4)), z = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(q + 8));
                float ls = x * x + y * y + z * z;
                var key = new System.Numerics.Quaternion(-x, -y, -z, ls < 1 ? MathF.Sqrt(1 - ls) : 0);   // stored: the conjugate
                var nq = System.Numerics.Quaternion.Normalize(Mul(key, turn));
                if (nq.W < 0) nq = -nq;
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q), -nq.X);
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 4), -nq.Y);
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 8), -nq.Z);
            }
            done.Add(bone);
        }
        if (skipped.Count > 0) why = string.Join(", ", skipped);
        return done;
    }

    /// <summary>
    /// A copied sequence's root moved on Z from <paramref name="from"/> at its first frame to <paramref name="to"/> at its
    /// last (two position keys in place of its one, appended to the key data; plain float positions only). Kurt
    /// (2026-10-22): the high-flying lift comes on while a borrowed ground start plays; lowered all the way, Spider-Man sank
    /// during Captain America's start, not lowered he floated.
    /// </summary>
    static byte[]? RampRoot(Package pkg, byte[] set, byte[] d, float from, float to, out string why, string bone = "root")
    {
        why = "";
        var stags = TagWalker.Walk(pkg, set, 4);
        var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tb == null || tags == null) { why = "the animation doesn't read"; return null; }
        int nb = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt)), root = -1;
        for (int k = 0; k < nb && root < 0; k++)
            if (pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(tb.ValueAt + 4 + 8 * k))].Equals(bone, StringComparison.OrdinalIgnoreCase)) root = k;
        if (root < 0) { why = $"no {bone} track"; return null; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = "positions stored as " + tf; return null; }
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return null; }
        bool variable = kf.Equals("akf_variablekeylerp", StringComparison.OrdinalIgnoreCase);
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        var nf = tags.FirstOrDefault(x => x.Name.Equals("NumFrames", StringComparison.OrdinalIgnoreCase));
        if (ot == null || nf == null || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * nb) { why = "the track offsets don't match the bones"; return null; }
        int frames = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nf.ValueAt));
        int at = ot.ValueAt + 4 + 16 * root;
        int tOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)), tKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
        int q = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)); q += 4;
        for (int k = 0; k < raw; k++)
        {
            q += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
            q += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
        }
        int countAt = q, len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)), streamAt = q + 4;
        if (frames < 2) { why = "one frame"; return null; }
        // a track with keys over time already: each key moved by the ramp at its frame (in place)
        if (tOff >= 0 && tKeys >= 2 && tOff + 12 * tKeys <= len)
        {
            int timesAt = streamAt + tOff + 12 * tKeys;
            if (variable) timesAt = streamAt + ((tOff + 12 * tKeys + 3) & ~3);
            for (int k = 0; k < tKeys; k++)
            {
                float fr = !variable ? (float)k * (frames - 1) / (tKeys - 1)
                    : frames > 0xFF ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(timesAt + 2 * k)) : d[timesAt + k];
                float z = from + (to - from) * fr / (frames - 1);
                int za = streamAt + tOff + 12 * k + 8;
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(za), BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(za)) + z);
            }
            return d;
        }
        // the root's place as stored (its first key), else zero
        float x0 = 0, y0 = 0, z0 = 0;
        if (tOff >= 0 && tKeys >= 1 && tOff + 12 <= len)
        {
            x0 = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(streamAt + tOff));
            y0 = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(streamAt + tOff + 4));
            z0 = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(streamAt + tOff + 8));
        }
        var data = new MemoryStream();
        data.Write(d, streamAt, len);
        void Pad() { while (data.Length % 4 != 0) data.WriteByte(0); }
        Pad();
        int newOff = (int)data.Length;
        foreach (float z in new[] { z0 + from, z0 + to })
        {
            data.Write(BitConverter.GetBytes(x0)); data.Write(BitConverter.GetBytes(y0)); data.Write(BitConverter.GetBytes(z));
        }
        if (variable)
        {
            Pad();
            if (frames > 0xFF) { data.Write(BitConverter.GetBytes((ushort)0)); data.Write(BitConverter.GetBytes((ushort)(frames - 1))); }
            else { data.WriteByte(0); data.WriteByte((byte)(frames - 1)); }
            Pad();
        }
        byte[] stream = data.ToArray();
        byte[] o = [.. d.AsSpan(0, countAt), .. BitConverter.GetBytes(stream.Length), .. stream, .. d.AsSpan(streamAt + len)];
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(at), newOff);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(at + 4), 2);
        return o;
    }

    /// <summary>A set's data with <paramref name="bone"/> in its UseTranslationBoneNames (added when missing; the list must exist:
    /// without one every bone takes its animated position already).</summary>
    static byte[] WithTranslationBone(Package pkg, byte[] set, string bone, Func<string, int> nameIdx)
    {
        var t = TagWalker.Walk(pkg, set, 4)?.FirstOrDefault(x => x.Name.Equals("UseTranslationBoneNames", StringComparison.OrdinalIgnoreCase));
        if (t == null) return set;
        int n = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(t.ValueAt));
        for (int k = 0; k < n; k++)
        {
            int ni = BinaryPrimitives.ReadInt32LittleEndian(set.AsSpan(t.ValueAt + 4 + 8 * k));
            if (ni < pkg.Names.Length && pkg.Names[ni].Equals(bone, StringComparison.OrdinalIgnoreCase)) return set;
        }
        var add = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(add, nameIdx(bone));
        byte[] o = [.. set.AsSpan(0, t.End), .. add, .. set.AsSpan(t.End)];
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(t.ValueAt), n + 1);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(t.Start + 16), t.Size + 8);
        return o;
    }

    /// <summary>
    /// A costume with an animation list of its own (Daredevil Modern: blink, interactive, daredevil_as, the rework sets) doesn't
    /// play its hero's list (Kurt, 2026-10-10: Cosmic Haymaker borrowed onto Daredevil froze him: its animations were only in
    /// the hero's list). The sets in <paramref name="setPaths"/> (in the hero's base package, which loads before its
    /// costumes) are appended to that list as imports, as the costume imports the hero's own sets. Null when the costume
    /// has no list of its own (it plays the hero's) or lists them all already.
    /// </summary>
    public static byte[]? AddImportedSets(string costumePath, string costumeClass, IReadOnlyList<string> setPaths, List<string> log)
    {
        var pkg = Package.Open(costumePath);
        string compPath = $"marvelgamecontent.default__{costumeClass.ToLowerInvariant()}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) return null;
        byte[] cd = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var tags = TagWalker.Walk(pkg, cd, 16) ?? throw new InvalidDataException($"{Path.GetFileName(costumePath)}: the mesh component's properties don't read");
        var at = tags.FirstOrDefault(x => x.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
        if (at == null) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt));
        var listed = Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt + 4 + 4 * k)))
            .Select(RefPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string RefPath(int r)
        {
            var parts = new List<string>();
            for (int guard = 0; r != 0 && guard < 64; guard++)
                if (r > 0) { parts.Add(pkg.Exports[r - 1].ObjectName); r = pkg.Exports[r - 1].OuterIndex; }
                else { parts.Add(pkg.Imports[-r - 1].ObjectName); r = pkg.Imports[-r - 1].OuterIndex; }
            parts.Reverse();
            return string.Join('.', parts);
        }
        var todo = setPaths.Where(p => !listed.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (todo.Count == 0) return null;
        var addNames = new List<string>();
        int NameIdx(string nm)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(nm, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(nm); j = addNames.Count - 1; }
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
                    foreach (string nm in new[] { cp, cn, parts[k] }) NameIdx(nm);
                    addImports.Add(new NewImport(cp, cn, outer, parts[k]));
                    added = addImports.Count - 1;
                }
                outer = -(pkg.Imports.Length + added + 1);
            }
            return outer;
        }
        byte[] add = [.. todo.Select(ImportOf).SelectMany(BitConverter.GetBytes)];
        byte[] ncd = [.. cd.AsSpan(0, at.End), .. add, .. cd.AsSpan(at.End)];
        BinaryPrimitives.WriteInt32LittleEndian(ncd.AsSpan(at.ValueAt), n + todo.Count);
        BinaryPrimitives.WriteInt32LittleEndian(ncd.AsSpan(at.Start + 16), at.Size + add.Length);
        var replace = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => ncd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException($"{Path.GetFileName(costumePath)} didn't verify: " + string.Join("; ", problems.Take(3)));
        log.Add($"anims: {string.Join(", ", todo)} added to {Path.GetFileName(costumePath)}'s own animation list ({n} set(s) before)");
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
