using System.Buffers.Binary;
using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using MhoExtendedModManager;
using MhoPackageModifier;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Borrowed hair in the built mod (0.15.0, Kurt): the animations the costume plays, with tracks for the grafted hair bones.
/// The game looks an animation up by name from the last AnimSet in the costume's mesh component list to the first
/// (CostumeAnims; the Mod Manager's AnimSwap uses the same rule, confirmed in game), so only the built package changes:
/// each of the hero's sets (and the costume's own) is copied in under a name of its own (&lt;set&gt;_mffhair) with all its
/// sequences, the hair bones are added to the copy's TrackBoneNames, and each copied sequence gets one track per hair bone
/// appended to its compressed stream. The body tracks stay byte for byte as the game's (no re-encoding). The copies go at
/// the end of the component's list, so they win for every name they hold. Shared sets (blink, interactive) aren't copied:
/// the hair keeps its rest pose there.
///
/// Appended tracks, in the sequence's own format (UE3 rules, decoded back with AnimExportCli's reader as the check):
/// translation one key (3 floats: the bone's rest position; rotation-only sets ignore it anyway); rotation one key per frame,
/// stored as the conjugate (engine compressed data is), Float96NoW (x, y, z; w ≥ 0 derived) or Fixed48NoW (uint16 × 3) or
/// None (4 floats, not conjugated), with a frame table for VariableKeyLerp (aligned to 4; byte, or uint16 past 255 frames).
/// Per-track sequences: two ints per track; a translation track (format None, xyz, 1 key) and a rotation track (Float96NoW,
/// xyz, one key per frame, no time table = keys evenly over the frames).
/// </summary>
static class HairAnims
{
    public sealed record Result(byte[] Package, int Sets, int Sequences, int Static, List<string> Log);

    /// <summary>The package's character has no animation list of its own to put animations into (a power's model: Cyclops's
    /// Strike Team Beast plays the power's own animation sets).</summary>
    public const string NoAnimationListKey = "no animation list";

    static InvalidDataException NoAnimationList(string message)
    {
        var ex = new InvalidDataException(message);
        ex.Data[NoAnimationListKey] = true;
        return ex;
    }

    /// <summary>
    /// The built package (<paramref name="builtPath"/>) with the hair animation added. <paramref name="bones"/> = the built
    /// mesh's skeleton (the retarget's, with the grafted bones: the preview poses the same one), <paramref name="borrowed"/>
    /// the grafted bones' names, <paramref name="rig"/> the borrowed rig that makes their motion.
    /// </summary>
    /// <param name="overrides">FBX edits (0.16.0): animation name → an FBX whose clip replaces it; that sequence is encoded
    /// anew (<see cref="Encode"/>). Without a rig only the sets holding a replaced animation are copied, with just those.</param>
    /// <summary>A component's animation list with each copy right after the set it's a copy of: <paramref name="list"/> is
    /// <paramref name="sets"/> in order (null when the two don't line up).</summary>
    static List<int>? Interleave(List<int> list, IReadOnlyList<CostumeAnims.Set> sets, List<(CostumeAnims.Set From, int Ref)> copies)
    {
        if (list.Count != sets.Count) return null;
        var outList = new List<int>();
        var placed = new HashSet<int>();
        for (int k = 0; k < list.Count; k++)
        {
            outList.Add(list[k]);
            foreach (var c in copies.Where(c => ReferenceEquals(c.From, sets[k]))) { outList.Add(c.Ref); placed.Add(c.Ref); }
        }
        foreach (var c in copies.Where(c => !placed.Contains(c.Ref))) outList.Add(c.Ref);   // (a set not in the list: at the end)
        return outList;
    }

    public static Result Add(string builtPath, string packageFile, IReadOnlyList<MeshBone> bones, IReadOnlySet<string> borrowed, IReadOnlyList<BorrowedRig> rigs, Action<string> log,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        // every borrowed rig's bones (a cape and / or hair, 0.17.0): their tracks appended, their motion made in turn, as the preview
        var hair = bones.Select(b => b.Name).Where(n => borrowed.Contains(n) && rigs.Any(r => r.Bones.IsMatch(n))).ToList();
        BoneAnimation Moved(BoneAnimation a, IReadOnlyCollection<string>? keepIfAnimated = null)
        {
            foreach (var r in rigs)
                if (keepIfAnimated == null || !hair.Where(h => r.Bones.IsMatch(h)).All(h => a.Tracks.ContainsKey(h))) a = r.Apply(a, bones);
            return a;
        }
        overrides ??= new Dictionary<string, string>();
        var fileFor = FileFor(builtPath, packageFile);
        var ca = CostumeAnims.Read(builtPath, packageFile, fileFor) ?? throw NoAnimationList("the costume's animation list doesn't read");
        // the ankles the animations would stretch to another leg length (KeepLegs): their positions left out in the copies
        var keep = StretchedAnkles(ca, bones, out string? keepNote);
        if (hair.Count == 0 && overrides.Count == 0 && keep.Count == 0) throw new InvalidDataException("no borrowed hair bones, no replaced animations and no leg lengths to keep");
        bool all = hair.Count > 0 || keep.Count > 0;   // every animation of a set (hair, kept legs), else only the replaced ones
        string Suffix = all ? "_mffhair" : "_mffedit";
        // a replaced animation goes into the set the game plays it from (the last set holding its name)
        var winners = overrides.Keys.Select(n => ca.Anims.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var (n, w) in overrides.Keys.Zip(winners)) if (w == null) throw new InvalidDataException($"the costume plays no animation named \"{n}\"");
        var copy = ca.Sets.Where(s => s.Found && (all ? s.Kind is CostumeAnims.Source.Hero or CostumeAnims.Source.Costume : winners.Any(w => w!.From == s))).ToList();
        if (copy.Count == 0) throw new InvalidDataException("the costume plays no animation set of its hero's or its own");
        foreach (var w in winners) if (w!.From.Kind == CostumeAnims.Source.Shared && !copy.Contains(w.From)) copy.Add(w.From);
        var lines = new List<string>();
        if (keepNote != null) lines.Add(keepNote);
        string cls = ca.Class.ToLowerInvariant();
        if (all) foreach (var s in ca.Sets.Except(copy)) lines.Add($"set {s.Path} ({s.Kind}, {s.Sequences.Count} animations) not copied: the borrowed rigs keep their rest pose there");

        // 1. Sets in another package (a costume playing its hero's): copied in with ExportCopy, all sequences and notifies.
        // Sets of the built package itself (a base-package build: the hero's own sets) are cloned in step 2 instead: same
        // package, so every name and reference in their bytes already means the same thing (and ExportCopy can't read every
        // sequence: Carnage's carry morph curves, curvedata).
        var pkg = Package.Open(builtPath);
        string pkg0Name(CostumeAnims.Set s) => s.Export >= 0 && s.Export < pkg.Exports.Length ? pkg.Exports[s.Export].ObjectName : "";
        var work = new List<(CostumeAnims.Set From, int SetIndex, bool Clone)>();
        // an edit copy made by an earlier build (…_mffedit), already in the list: edited in place (Kurt, 2026-10-23: each build
        // cloned the last one again, …_mffedit_mffedit_mffedit, and the package grew by every copy)
        var inPlace = new HashSet<int>();
        // a rig copy an earlier step made (…_mffhair, …_mffhair_2: a move to another hero gives the parts it brings motion,
        // Kurt 2026-11-20), already in the list: the new rigs' tracks go into it in place, and its original isn't cloned again
        // (a new clone, listed right after the original, came before the earlier copy, which then won every animation)
        var earlierCopy = new System.Text.RegularExpressions.Regex(@"_mffhair(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool IsEarlierCopy(CostumeAnims.Set s) => all && s.File!.Equals(builtPath, StringComparison.OrdinalIgnoreCase) && earlierCopy.IsMatch(pkg0Name(s));
        bool HasEarlierCopy(CostumeAnims.Set s) => all && copy.Any(c => c != s && IsEarlierCopy(c) && c.Path.StartsWith(s.Path + "_mffhair", StringComparison.OrdinalIgnoreCase));
        foreach (var s in copy)
        {
            if (HasEarlierCopy(s)) { lines.Add($"set {s.Path}: its earlier copy is edited instead"); continue; }
            if (s.File!.Equals(builtPath, StringComparison.OrdinalIgnoreCase))
            {
                bool earlierEdit = !all ? pkg0Name(s).EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) : IsEarlierCopy(s);
                if (earlierEdit) inPlace.Add(s.Export);
                work.Add((s, s.Export, !earlierEdit));
                continue;
            }
            var src = Package.Open(s.File);
            string setName = src.Exports[s.Export].ObjectName, name = UniqueName(pkg, [], setName + Suffix);
            var r = Quiet(() => ExportCopy.Copy(src, s.Export, pkg, [], name), out string said)
                    ?? Quiet(() => ExportCopy.Copy(src, s.Export, pkg, ["notifies"], name), out said);
            if (r == null) throw new InvalidDataException($"the hero's animation set {s.Path} couldn't be copied into this costume: {Reason(said)} (build on the hero's base package instead)");
            pkg = Package.FromBytes(r.Output);
            work.Add((s, r.RootRef - 1, false));
            lines.Add($"set {s.PackageName} · {s.Path} ({s.Sequences.Count} animations) copied in as {pkg.PathOf(pkg.Exports[r.RootRef - 1])}");
        }

        // 2. The edit: hair names into each set's TrackBoneNames, hair tracks into each of its sequences (in place for the
        // copies, as new exports for the clones: a set export beside the original, its sequences inside it), the sets at the
        // end of the component's list.
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return pkg.Names.Length + j;
        }
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var newExports = new List<NewExport>();
        byte[] EntryOf(int export, int outerRef, int nameIdx, int nameNumber)
        {
            var e = pkg.Body.AsSpan(pkg.ExportEntryStart[export], pkg.ExportEntryEnd[export] - pkg.ExportEntryStart[export]).ToArray();
            if (pkg.ExportSerialFieldAt[export] - pkg.ExportEntryStart[export] != 32) throw new InvalidDataException($"export {export}: an unexpected table entry layout");
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(8), outerRef);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(12), nameIdx);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), nameNumber);
            return e;
        }
        var setRefs = new List<int>();
        // each new set and the set it's a copy of: listed right after it (Kurt, 2026-11-16: appended at the end, the copy of
        // Carnage's own set took movement_webswing back from the borrowed Emma Frost run listed after it, and he swung high)
        var copyAfter = new List<(CostumeAnims.Set From, int Ref)>();
        int touched = 0;
        var replacedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // (an earlier edit copy can hold a name twice)
        var expect = new List<Expect>();
        int sequences = 0, statics = 0, replacedCount = 0;
        var restLocal = bones.ToDictionary(b => b.Name, b => b.Position, StringComparer.OrdinalIgnoreCase);
        // bones whose position moves too (Medusa's Hair): written with a translation key per frame, and named in a rotation-only
        // set's UseTranslationBoneNames so the game applies them
        var posBones = rigs.SelectMany(r => r.Library.PositionBones).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, setIndex, clone) in work)
        {
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[setIndex]).ToArray();
            var stags = TagWalker.Walk(pkg, sd, 4) ?? throw new InvalidDataException($"the set {from.Path} doesn't read");
            var tbn = stags.FirstOrDefault(t => t.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{from.Path}: no TrackBoneNames");
            var seqTag = stags.FirstOrDefault(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{from.Path}: no Sequences");
            int nTracks = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(tbn.ValueAt));
            if (nTracks != from.TrackBoneNames.Count) throw new InvalidDataException($"{from.Path}: {nTracks} track names in the set, {from.TrackBoneNames.Count} read");
            var add = hair.Where(h => !from.TrackBoneNames.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();
            bool replacesHere = from.Sequences.Any(x => overrides.ContainsKey(x.Name));
            bool dropsHere = from.TranslationBones != null && keep.Any(from.TranslationBones.Contains);
            if (add.Count == 0 && !replacesHere && !dropsHere)
            {
                // (an animation swap from the hair's own character, Medusa's run on Carnage): its own hair tracks play; it stays
                // where it is in the list (the copies go right after their own originals, so they don't take its names back)
                lines.Add($"{from.Path}: has the hair bones already, kept as it is");
                touched++;
                continue;
            }
            var track = from.TrackBoneNames.Concat(add).ToList();
            int nSeq = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(seqTag.ValueAt));
            var oldSeqs = Enumerable.Range(0, nSeq).Select(k => BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(seqTag.ValueAt + 4 + 4 * k))).ToList();
            // a clone: the set's new reference (exports are added after the existing ones, in this order), its sequences after it
            int setRef = clone ? pkg.Exports.Length + newExports.Count + 1 : setIndex + 1;
            var newSeqRefs = new List<int>();
            var seqData = new List<(int Old, byte[] Data)>();
            foreach (int q in oldSeqs)
            {
                if (q <= 0) { newSeqRefs.Add(q); continue; }
                byte[] qd = pkg.ReadExportBytes(pkg.Exports[q - 1]).ToArray();
                var qt = TagWalker.Walk(pkg, qd, 4) ?? throw new InvalidDataException($"animation #{q} doesn't read");
                string seqName = qt.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase)) is { } sn ? TagWalker.NameAt(pkg, qd, sn.ValueAt) : pkg.Exports[q - 1].ObjectName;
                bool replaced = overrides.TryGetValue(seqName, out string? fbx);
                if (!all && !replaced) { if (!clone) newSeqRefs.Add(q); continue; }   // only the replaced ones (a copy keeps the rest as it is)
                var srcSeq = from.Sequences.FirstOrDefault(x => x.Name.Equals(seqName, StringComparison.OrdinalIgnoreCase));
                var aref = new AnimRef(from.PackageName + ".upk", from.File!, seqName, srcSeq.Name != null ? srcSeq.Export : -1, from.TrackBoneNames) { TranslationBones = from.TranslationBones };
                var anim = srcSeq.Name != null ? ModAnimations.Load(aref) : null;
                int frames = qt.FirstOrDefault(t => t.Name.Equals("NumFrames", StringComparison.OrdinalIgnoreCase)) is { } nf ? BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(nf.ValueAt)) : 0;
                var keys = new Dictionary<string, Quaternion[]>(StringComparer.OrdinalIgnoreCase);
                var places = add.ToDictionary(h => h, h => new[] { restLocal[h] }, StringComparer.OrdinalIgnoreCase);
                bool still = false;
                if (replaced)
                {
                    // the FBX's clip, as the game would play it (the game's tracks where it has none), with the hair's motion
                    // made on it unless the FBX animates the hair itself; then the whole sequence encoded anew
                    var edit = AnimEdits.ForGame(fbx!, aref, anim);
                    if (add.Count > 0) edit = Moved(edit, add);   // a rig the FBX animates itself keeps the FBX's tracks
                    byte[] eqd = Encode(pkg, qd, qt, track, edit, bones, aref, AnimEdits.SecondsPerFrame(anim), NameIdx, out var full, out int eFrames);
                    int eRef = clone ? setRef + 1 + seqData.Count : q;
                    newSeqRefs.Add(eRef);
                    seqData.Add((q, eqd));
                    expect.Add(new Expect(eRef, seqName, track, full, from.File!, -1, from.TrackBoneNames));
                    lines.Add($"\"{seqName}\" replaced by {Path.GetFileName(fbx)}: {eFrames} frames, {edit.Tracks.Count} tracks from the edit (ConstantKeyLerp, Float96NoW)");
                    sequences++; replacedCount++; replacedNames.Add(seqName);
                    continue;
                }
                if (anim != null && frames > 0)
                {
                    var moved = Moved(anim);
                    foreach (var h in add) keys[h] = Sample(moved.Tracks.TryGetValue(h, out var t) ? t : null, frames, bones.First(b => b.Name.Equals(h, StringComparison.OrdinalIgnoreCase)).Orientation);
                    foreach (var h in add)
                        if (posBones.Contains(h) && moved.Tracks.TryGetValue(h, out var pt) && pt.PositionKeys.Count > 1)
                            places[h] = SamplePositions(pt.PositionKeys, frames, restLocal[h]);
                }
                else
                {
                    // not decodable (or no frames): the hair keeps its rest pose in this one
                    foreach (var h in add) keys[h] = [bones.First(b => b.Name.Equals(h, StringComparison.OrdinalIgnoreCase)).Orientation];
                    still = true;
                }
                List<Quaternion[]> writtenKeys = [];
                byte[] nqd = add.Count == 0 ? qd : AppendTracks(pkg, qd, qt, add.Select(h => (places[h], keys[h])).ToList(), frames, seqName, out writtenKeys);   // (only the legs kept: the animation as it is)
                if (writtenKeys.Zip(add).Any(z => z.First.Length != keys[z.Second].Length)) still = true;
                if (still) statics++;
                for (int i = 0; i < add.Count; i++) keys[add[i]] = writtenKeys[i];
                int newRef = clone ? setRef + 1 + seqData.Count : q;
                newSeqRefs.Add(newRef);
                seqData.Add((q, nqd));
                expect.Add(new Expect(newRef, seqName, track, keys, from.File!, srcSeq.Name != null ? srcSeq.Export : -1, from.TrackBoneNames));
                sequences++;
            }
            byte[] names = [.. BitConverter.GetBytes(nTracks + add.Count), .. sd.AsSpan(tbn.ValueAt + 4, nTracks * 8),
                .. add.SelectMany(h => BitConverter.GetBytes((long)NameIdx(h)))];
            byte[] seqList = [.. BitConverter.GetBytes(newSeqRefs.Count), .. newSeqRefs.SelectMany(BitConverter.GetBytes)];
            // both tags rewritten, the later one first so the earlier one's place holds
            var editList = new List<(TagWalker.Tag Tag, byte[] Value)> { (tbn, names), (seqTag, seqList) };
            // a rotation-only set (the default) applies positions only to the bones it names: the moving hair bones added
            var usePos = from.TranslationBones != null ? add.Where(h => posBones.Contains(h) && !from.TranslationBones.Contains(h)).ToList() : [];
            byte[]? newUse = null;
            if (usePos.Count > 0 || dropsHere)
            {
                var useTag = stags.FirstOrDefault(t => t.Name.Equals("UseTranslationBoneNames", StringComparison.OrdinalIgnoreCase));
                int had = useTag != null ? BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(useTag.ValueAt)) : 0;
                // the entries kept (8 bytes each: name, number), the kept legs' ankles left out (they take the model's own shin)
                var kept = Enumerable.Range(0, had).Select(k => sd.AsSpan(useTag!.ValueAt + 4 + 8 * k, 8).ToArray())
                    .Where(e => { int ni = BinaryPrimitives.ReadInt32LittleEndian(e); return ni < 0 || ni >= pkg.Names.Length || !keep.Contains(pkg.Names[ni]); }).ToList();
                byte[] list = [.. BitConverter.GetBytes(kept.Count + usePos.Count), .. kept.SelectMany(e => e),
                    .. usePos.SelectMany(h => BitConverter.GetBytes((long)NameIdx(h)))];
                if (dropsHere) lines.Add($"{from.Path}: {had - kept.Count} ankle(s) take the model's own shin length (left out of the copy's UseTranslationBoneNames)");
                if (useTag != null) editList.Add((useTag, list));
                else
                {
                    var tag = new byte[24];
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("UseTranslationBoneNames"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), list.Length);
                    newUse = [.. tag, .. list];
                }
                lines.Add($"{from.Path}: {usePos.Count} hair bone(s) move by position: named in the copy's UseTranslationBoneNames");
            }
            byte[] nsd = newUse != null ? [.. sd.AsSpan(0, stags.NoneAt), .. newUse, .. sd.AsSpan(stags.NoneAt)] : sd;   // (after every tag: the places below hold)
            foreach (var (tag, value) in editList.OrderByDescending(x => x.Tag.Start))
            {
                nsd = [.. nsd.AsSpan(0, tag.ValueAt), .. value, .. nsd.AsSpan(tag.End)];
                BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(tag.Start + 16), value.Length);
            }
            if (clone)
            {
                var e = pkg.Exports[setIndex];
                string setName = UniqueName(pkg, addNames, e.ObjectName + Suffix);
                byte[] setData = nsd;
                newExports.Add(new NewExport(setIndex, 0, _ => setData) { Entry = EntryOf(setIndex, BinaryPrimitives.ReadInt32LittleEndian(pkg.Body.AsSpan(pkg.ExportEntryStart[setIndex] + 8)), NameIdx(setName), 0) });
                foreach (var (old, data) in seqData)
                {
                    int nameIdx = BinaryPrimitives.ReadInt32LittleEndian(pkg.Body.AsSpan(pkg.ExportEntryStart[old - 1] + 12));
                    int nameNum = BinaryPrimitives.ReadInt32LittleEndian(pkg.Body.AsSpan(pkg.ExportEntryStart[old - 1] + 16));
                    newExports.Add(new NewExport(old - 1, nameNum, _ => data) { Entry = EntryOf(old - 1, setRef, nameIdx, nameNum) });
                }
                lines.Add($"set {from.Path} ({seqData.Count} animations) cloned as {pkg.PathOf(e)[..^e.ObjectName.Length]}{setName}");
            }
            else
            {
                replace[setIndex] = _ => nsd;
                foreach (var (old, data) in seqData) replace[old - 1] = _ => data;
                if (inPlace.Contains(setIndex)) lines.Add($"set {from.Path}: the earlier edit copy, its animations replaced in place");
            }
            if (!inPlace.Contains(setIndex)) { setRefs.Add(setRef); copyAfter.Add((from, setRef)); }   // (an earlier edit copy is listed already)
            touched++;
        }
        if (touched == 0) throw new InvalidDataException("every set has the hair bones already");
        if (replacedNames.Count != overrides.Count) throw new InvalidDataException($"{replacedCount} of {overrides.Count} replaced animations written (a name in no copied set?)");

        // the component's list: the copies at its end (a costume without a list of its own gets its hero's first)
        string compPath = $"marvelgamecontent.default__{cls}.initialskeletalmesh";
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0) throw new InvalidDataException($"no {compPath} in the package");
        byte[] cd = pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
        var ctags = TagWalker.Walk(pkg, cd, 16) ?? throw new InvalidDataException("the mesh component's properties don't read");
        var at = ctags.FirstOrDefault(x => x.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
        byte[] addRefs = [.. setRefs.SelectMany(BitConverter.GetBytes)];
        var addImports = new List<NewImport>();
        byte[] ncd;
        if (at != null)
        {
            // the list as it was, each copy right after its original (the list is ca.Sets in order); else (it doesn't line up)
            // the copies at the end
            int n = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt));
            var old = Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(at.ValueAt + 4 + 4 * k))).ToList();
            var refsNow = Interleave(old, ca.Sets, copyAfter) ?? [.. old, .. setRefs];
            if (refsNow.Count != old.Count + setRefs.Count) throw new InvalidDataException("the animation list didn't come out whole");
            if (!refsNow.Skip(old.Count).SequenceEqual(setRefs)) lines.Add("the copies listed right after their own sets (the sets after them still win, as before)");
            byte[] value = [.. BitConverter.GetBytes(refsNow.Count), .. refsNow.SelectMany(BitConverter.GetBytes)];
            ncd = [.. cd.AsSpan(0, at.ValueAt), .. value, .. cd.AsSpan(at.End)];
            BinaryPrimitives.WriteInt32LittleEndian(ncd.AsSpan(at.Start + 16), value.Length);
        }
        else
        {
            // no list of its own: the hero's sets imported (by path, as AnimSwap does), then the copies
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
            var refs = ca.Sets.Select(s => ImportOf(s.Path)).ToList();
            var refsNow = Interleave(refs, ca.Sets, copyAfter) ?? [.. refs, .. setRefs];
            byte[] value = [.. BitConverter.GetBytes(refsNow.Count), .. refsNow.SelectMany(BitConverter.GetBytes)];
            var tag = new byte[24];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("AnimSets"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), value.Length);
            ncd = [.. cd.AsSpan(0, ctags.NoneAt), .. tag, .. value, .. cd.AsSpan(ctags.NoneAt)];
            lines.Add($"the costume's own animation list: its hero's {refs.Count} set(s), then the copies");
        }
        replace[comp] = _ => ncd;
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, newExports, out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), newExports, written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the package with the hair animation didn't verify: " + string.Join("; ", problems.Take(3)));

        // 3. Read back: every copied sequence decoded from the output; body tracks as the source's, hair as built.
        if (Environment.GetEnvironmentVariable("MFF_ANIMS_KEEP") is { Length: > 0 } keepFile) File.WriteAllBytes(keepFile, output);   // diagnostics: the package even when the check fails
        Check(output, expect, lines);
        // as the game looks them up (last set first): every animation of a copied set now comes from its copy
        string tmp = Path.Combine(Path.GetTempPath(), $"mff_hair_{Environment.ProcessId}_{packageFile}");
        try
        {
            File.WriteAllBytes(tmp, output);
            var after = CostumeAnims.Read(tmp, packageFile, n => fileFor(n) is { } f && f.Item1 == builtPath ? (tmp, true) : fileFor(n));
            var names = all ? copy.SelectMany(x => x.Sequences.Select(q => q.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase) : overrides.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // a name may play from a set after its copy (a borrowed power's, an animation swap: as before the build); it must not
            // play from an original that was copied (then the copy never plays)
            var copiedPaths = copyAfter.Select(c => c.From.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool FromCopy(CostumeAnims.Anim a) => hair.Count > 0 ? a.From.TrackBoneNames.Contains(hair[0], StringComparer.OrdinalIgnoreCase) : a.From.Path.Contains(Suffix, StringComparison.OrdinalIgnoreCase);
            var mine = after?.Anims.Where(a => names.Contains(a.Name)).ToList() ?? [];
            int fromCopy = mine.Count(FromCopy), later = mine.Count(a => !FromCopy(a) && !copiedPaths.Contains(a.From.Path));
            var lost = mine.Where(a => !FromCopy(a) && copiedPaths.Contains(a.From.Path)).Select(a => a.Name).ToList();
            lines.Add($"lookup: {fromCopy} of {names.Count} animation names play from a copy{(hair.Count > 0 ? " with the borrowed tracks" : "")}, {later} from a set listed after it (a borrowed power, a swap) ({after?.Anims.Count ?? 0} names in all)");
            if (lost.Count > 0 || fromCopy + later != names.Count) throw new InvalidDataException("the game's lookup doesn't reach every copied animation: " + lines[^1] + (lost.Count > 0 ? " (still from the original: " + string.Join(", ", lost.Take(6)) + ")" : ""));
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        foreach (var l in lines) log("anims:   " + l);
        return new Result(output, setRefs.Count, sequences, statics, lines);
    }

    /// <summary>Where a set's package is: the built one, the mod's copy of the hero's package, else the game's.</summary>
    static Func<string, (string Path, bool FromMod)?> FileFor(string builtPath, string packageFile) => name =>
    {
        string f = name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? name : name + ".upk";
        if (f.Equals(packageFile, StringComparison.OrdinalIgnoreCase)) return (builtPath, true);
        // the hero's package from the mod first (its borrowed powers' sets: Kurt, 2026-10-23, an edited borrowed flight
        // stopped the build, "the costume plays no animation named movement_run_flying")
        if (MhoAnim.BaseLookup?.Invoke(f) is string mine && File.Exists(mine)) return (mine, true);
        try { string p = BasePackage.Resolve(f, true); return File.Exists(p) ? (p, false) : null; }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException) { return null; }   // a group, not a file
    };

    static readonly string[] Ankles = ["g_l_ankle", "g_r_ankle"];

    /// <summary>
    /// The ankles the costume's animations would stretch (Kurt, 2026-11-20, ULTRAVISION on Ultron: the feet sank into the floor
    /// in game): a rotation-only set that lists an ankle in UseTranslationBoneNames gives it the animation's position, the shin
    /// length of the hero it was made for (Ultron's 27.8 on Vision's 21.0), so a model with other legs gets the hero's shins
    /// and its feet go through the floor. Confirmed in game: with the ankles left out of the list the feet stood on the floor.
    /// An ankle counts when the shin it would get differs from the model's own by more than 3 %.
    /// </summary>
    static HashSet<string> StretchedAnkles(CostumeAnims ca, IReadOnlyList<MeshBone> bones, out string? note)
    {
        note = null;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var said = new List<string>();
        foreach (string ankle in Ankles)
        {
            var mb = bones.FirstOrDefault(b => b.Name.Equals(ankle, StringComparison.OrdinalIgnoreCase));
            if (mb == null || mb.Position.Length() < 1e-3f) continue;
            // the animation the costume plays as its idle, else the first one, from a set that gives the ankle its position
            var a = ca.Anims.FirstOrDefault(x => x.Name.Equals("idle", StringComparison.OrdinalIgnoreCase) && x.From.TranslationBones?.Contains(ankle) == true)
                ?? ca.Anims.FirstOrDefault(x => x.From.TranslationBones?.Contains(ankle) == true);
            if (a == null) continue;
            var anim = ModAnimations.Load(a.Ref);
            if (anim == null || !anim.Tracks.TryGetValue(ankle, out var t) || t.PositionKeys.Count == 0) continue;
            float own = mb.Position.Length(), got = t.PositionKeys[0].Position.Length();
            if (MathF.Abs(got - own) <= 0.03f * own) continue;
            result.Add(ankle);
            said.Add($"{ankle} {got:0.0} (the model's {own:0.0})");
        }
        if (result.Count > 0) note = $"legs: the animations' shin lengths ({string.Join(", ", said)}) aren't the model's: the copies leave the ankles' positions out, so the model keeps its own legs and its feet stay on the floor";
        return result;
    }

    /// <summary>The costume's animation sets copied with the model's own shin length kept (<see cref="StretchedAnkles"/>), when its
    /// animations would stretch it; null when they wouldn't (nothing to do). A build with rigs or edits does it in <see cref="Add"/>.</summary>
    public static Result? KeepLegs(string builtPath, string packageFile, IReadOnlyList<MeshBone> bones, Action<string> log)
    {
        var ca = CostumeAnims.Read(builtPath, packageFile, FileFor(builtPath, packageFile));
        if (ca == null || StretchedAnkles(ca, bones, out _).Count == 0) return null;
        return Add(builtPath, packageFile, bones, new HashSet<string>(), [], log);
    }

    /// <summary>A name no export has yet (…_2, _3 … when taken).</summary>
    static string UniqueName(Package pkg, List<string> added, string want)
    {
        string name = want;
        for (int k = 2; pkg.Exports.Any(e => e.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)); k++) name = $"{want}_{k}";
        return name;
    }

    /// <summary>A track's rotation at each whole frame 0…frames−1 (linear between keys, nlerp); the rest rotation without one.</summary>
    static Quaternion[] Sample(BoneTrack? t, int frames, Quaternion rest)
    {
        var o = new Quaternion[frames];
        var k = t?.RotationKeys;
        for (int f = 0; f < frames; f++)
        {
            if (k == null || k.Count == 0) { o[f] = rest; continue; }
            int i = 0;
            while (i + 1 < k.Count && k[i + 1].TimeFrame <= f) i++;
            var a = k[i].Rotation;
            if (i + 1 < k.Count && k[i + 1].TimeFrame > k[i].TimeFrame && f > k[i].TimeFrame)
            {
                var b = k[i + 1].Rotation; if (Quaternion.Dot(a, b) < 0) b = -b;
                a = Quaternion.Lerp(a, b, (f - k[i].TimeFrame) / (k[i + 1].TimeFrame - k[i].TimeFrame));
            }
            o[f] = Quaternion.Normalize(a);
        }
        return o;
    }

    /// <summary>A track's position at each whole frame 0…frames−1 (linear between keys); the rest position without keys.</summary>
    static Vector3[] SamplePositions(IReadOnlyList<BonePositionKey> k, int frames, Vector3 rest)
    {
        var o = new Vector3[frames];
        for (int f = 0; f < frames; f++)
        {
            if (k.Count == 0) { o[f] = rest; continue; }
            int i = 0;
            while (i + 1 < k.Count && k[i + 1].TimeFrame <= f) i++;
            var a = k[i].Position;
            if (i + 1 < k.Count && k[i + 1].TimeFrame > k[i].TimeFrame && f > k[i].TimeFrame)
                a = Vector3.Lerp(a, k[i + 1].Position, (f - k[i].TimeFrame) / (k[i + 1].TimeFrame - k[i].TimeFrame));
            o[f] = a;
        }
        return o;
    }

    /// <summary>The sequence's export bytes with one track per hair bone appended (offsets property and compressed stream).</summary>
    static byte[] AppendTracks(Package pkg, byte[] qd, TagWalker qt, List<(Vector3[] Pos, Quaternion[] Rot)> add, int frames, string seq, out List<Quaternion[]> written)
    {
        written = new();
        string Fmt(string prop, string dflt) => qt.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase)) is { } t
            ? TagWalker.NameAt(pkg, qd, t.ValueAt).ToUpperInvariant() : dflt;
        string rotFmt = Fmt("RotationCompressionFormat", "ACF_FLOAT96NOW"), keyFmt = Fmt("KeyEncodingFormat", "AKF_CONSTANTKEYLERP");
        bool transNone = Fmt("TranslationCompressionFormat", "ACF_NONE") == "ACF_NONE";
        bool perTrack = keyFmt == "AKF_PERTRACKCOMPRESSION", variable = keyFmt == "AKF_VARIABLEKEYLERP";
        var off = qt.FirstOrDefault(t => t.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{seq}: no CompressedTrackOffsets");
        // native data after the properties: raw tracks (count, each: positions, rotations), then the compressed stream
        int p = qt.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4;
        for (int i = 0; i < raw; i++) { int pc = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4 + pc * 12; int rc = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4 + rc * 16; }
        int streamAt = p;
        int len = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(streamAt));
        var stream = new MemoryStream(); stream.Write(qd, streamAt + 4, len);
        var w = new BinaryWriter(stream);
        void Align() { while (stream.Length % 4 != 0) w.Write((byte)0); }
        var offsets = new List<int>();
        bool fixed48 = rotFmt == "ACF_FIXED48NOW", none = rotFmt == "ACF_NONE", float96 = rotFmt == "ACF_FLOAT96NOW";
        foreach (var (posAll, rot) in add)
        {
            Align();
            var keys = rot.Length == frames && frames > 1 ? rot : [rot[0]];
            if (!perTrack && !(float96 || fixed48 || none)) keys = [keys[0]];   // an interval / packed format: the first pose only
            written.Add(keys);
            // positions: a key per frame for a bone that moves (raw floats: per-track None, else the sequence's ACF_None), else
            // one key (its rest place)
            var pos = posAll.Length == frames && frames > 1 && (perTrack || transNone) && posAll.Any(v => (v - posAll[0]).LengthSquared() > 1e-10f) ? posAll : [posAll[0]];
            if (perTrack)
            {
                int tAt = (int)stream.Length;
                w.Write((0u << 28) | (7u << 24) | (uint)pos.Length);
                foreach (var v in pos) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
                int rAt = (int)stream.Length;
                w.Write((1u << 28) | (7u << 24) | (uint)keys.Length);
                foreach (var q in keys) { var s = Stored(q); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); }
                offsets.Add(tAt); offsets.Add(rAt);
                continue;
            }
            int tOff = (int)stream.Length;
            foreach (var v in pos) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            if (variable && pos.Length > 1)
            {
                Align();
                for (int f = 0; f < pos.Length; f++) { if (frames > 255) w.Write((ushort)f); else w.Write((byte)f); }
            }
            Align();
            int rOff = (int)stream.Length;
            if (keys.Length == 1 && !none) { var s = Stored(keys[0]); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); }
            else if (none) foreach (var q in keys) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
            else if (fixed48) foreach (var q in keys) { var s = Stored(q); w.Write(U16(s.X)); w.Write(U16(s.Y)); w.Write(U16(s.Z)); }
            else foreach (var q in keys) { var s = Stored(q); w.Write(s.X); w.Write(s.Y); w.Write(s.Z); }
            if (variable && keys.Length > 1)
            {
                Align();
                for (int f = 0; f < keys.Length; f++) { if (frames > 255) w.Write((ushort)f); else w.Write((byte)f); }
                Align();
            }
            offsets.AddRange([tOff, pos.Length, rOff, keys.Length]);
        }
        Align();
        byte[] ns = stream.ToArray();
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(off.ValueAt));
        byte[] offVal = [.. BitConverter.GetBytes(n0 + offsets.Count), .. qd.AsSpan(off.ValueAt + 4, n0 * 4), .. offsets.SelectMany(BitConverter.GetBytes)];
        byte[] o = [.. qd.AsSpan(0, off.ValueAt), .. offVal, .. qd.AsSpan(off.End, streamAt - off.End), .. BitConverter.GetBytes(ns.Length), .. ns, .. qd.AsSpan(streamAt + 4 + len)];
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(off.Start + 16), offVal.Length);
        return o;
    }

    /// <summary>
    /// A sequence encoded anew from <paramref name="anim"/> (an FBX edit, 0.16.0), in the stock combination 9,093 of the
    /// game's sequences use and the hair tracks proved in game: AKF_ConstantKeyLerp, rotations ACF_Float96NoW (conjugated,
    /// w ≥ 0), translations ACF_None; one key per frame (a position that never changes, or one the set ignores, one key).
    /// KeyEncodingFormat and TranslationCompressionFormat are dropped (their defaults: the game's files leave them out), and
    /// RotationCompressionFormat is written as ACF_Float96NoW: it is NOT its default (2026-10-28, a user's edited animation spun
    /// in game; every stock sequence stores it, Float96NoW too: Cyclops 201 of 201, Captain America 203 of 203, Punisher 262 of
    /// 262, so the engine's default is another format and a sequence without it is read wrong). NumFrames / SequenceLength / CompressedTrackOffsets
    /// set, the stream replaced; everything else (notifies, rate, curves) kept. <paramref name="full"/> = every track's
    /// rotation keys as written (for the read-back check).
    /// </summary>
    static byte[] Encode(Package pkg, byte[] qd, TagWalker qt, List<string> track, BoneAnimation anim, IReadOnlyList<MeshBone> bones, AnimRef aref, float secondsPerFrame,
        Func<string, int> nameIdx, out Dictionary<string, Quaternion[]> full, out int numFrames)
    {
        var (last, _) = MeshAnimator.Span(anim);
        numFrames = Math.Max(1, (int)MathF.Round(last) + 1);
        var rest = bones.ToDictionary(b => b.Name, b => b, StringComparer.OrdinalIgnoreCase);
        var stream = new MemoryStream(); var w = new BinaryWriter(stream);
        var offsets = new List<int>();
        full = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in track)
        {
            anim.Tracks.TryGetValue(name, out var t);
            rest.TryGetValue(name, out var rb);
            var restPos = rb?.Position ?? (t?.PositionKeys.Count > 0 ? t.PositionKeys[0].Position : Vector3.Zero);
            var restRot = rb != null ? Quaternion.Normalize(rb.Orientation) : (t?.RotationKeys.Count > 0 ? t.RotationKeys[0].Rotation : Quaternion.Identity);
            // translation (ACF_None, 3 floats a key)
            int tOff = (int)stream.Length;
            var pos = t?.PositionKeys is { Count: > 0 } pk && AnimEdits.KeepsPosition(aref, name)
                ? Enumerable.Range(0, numFrames).Select(f => FbxExporter.SamplePosition(pk, f, restPos)).ToArray() : [t?.PositionKeys is { Count: > 0 } p0 ? p0[0].Position : restPos];
            if (pos.All(v => (v - pos[0]).LengthSquared() < 1e-12f)) pos = [pos[0]];
            foreach (var v in pos) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            // rotation (Float96NoW, conjugated)
            int rOff = (int)stream.Length;
            var rot = t?.RotationKeys is { Count: > 0 } rk ? Enumerable.Range(0, numFrames).Select(f => Quaternion.Normalize(FbxExporter.SampleRotation(rk, f, restRot))).ToArray() : [restRot];
            // a turn that doesn't change: one key, as the game's files store it (Kurt, 2026-10-23: an edited idle_combat lay
            // Captain America on his side in game. Its root, one key in the original that the game reads the other way round
            // (its rest turn, -90 degrees; the preview's single-key rule shows it so), went in as 97 plain keys of +90; written
            // as one key, an unchanged bone's bytes are the original's)
            if (rot.Length > 1 && rot.All(q => MathF.Abs(Quaternion.Dot(q, rot[0])) > 0.999999f)) rot = [rot[0]];
            foreach (var q in rot) { var st = Stored(q); w.Write(st.X); w.Write(st.Y); w.Write(st.Z); }
            full[name] = rot;
            offsets.AddRange([tOff, pos.Length, rOff, rot.Length]);
        }
        byte[] ns = stream.ToArray();
        // native data after the properties: raw tracks (kept), then the compressed stream (replaced)
        int p = qt.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4;
        for (int i = 0; i < raw; i++) { int pc = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4 + pc * 12; int rc = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(p)); p += 4 + rc * 16; }
        int streamAt = p, len = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(streamAt));
        byte[] native = [.. qd.AsSpan(qt.NoneAt + 8, streamAt - (qt.NoneAt + 8)), .. BitConverter.GetBytes(ns.Length), .. ns, .. qd.AsSpan(streamAt + 4 + len)];
        // properties: format tags out, the counts and offsets in (from the last tag back, so earlier places hold)
        byte[] props = qd.AsSpan(0, qt.NoneAt + 8).ToArray();
        TagWalker.Tag? Tag(string n) => qt.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        var edits = new List<(TagWalker.Tag Tag, byte[]? Value)>();
        foreach (string drop in new[] { "KeyEncodingFormat", "TranslationCompressionFormat" }) if (Tag(drop) is { } d) edits.Add((d, null));
        byte[] NameBytes(string n) => [.. BitConverter.GetBytes(nameIdx(n)), .. BitConverter.GetBytes(0)];
        byte[] float96 = NameBytes("ACF_Float96NoW");
        if (Tag("RotationCompressionFormat") is { } rotTag)
            edits.Add((rotTag, rotTag.Size == 16 ? [.. qd.AsSpan(rotTag.ValueAt, 8), .. float96] : float96));   // (a byte enum stored with its enum's name first)
        // else (an edit copy built before 0.60.222 dropped it): put in before None, below
        if (Tag("NumFrames") is not { } nfTag || Tag("SequenceLength") is not { } slTag || Tag("CompressedTrackOffsets") is not { } offTag) throw new InvalidDataException($"{aref.Name}: NumFrames, SequenceLength or CompressedTrackOffsets missing");
        edits.Add((nfTag, BitConverter.GetBytes(numFrames)));
        edits.Add((slTag, BitConverter.GetBytes((numFrames - 1) * secondsPerFrame)));
        edits.Add((offTag, [.. BitConverter.GetBytes(offsets.Count), .. offsets.SelectMany(BitConverter.GetBytes)]));
        if (Tag("RotationCompressionFormat") == null)
            props = [.. props.AsSpan(0, qt.NoneAt), .. NameBytes("RotationCompressionFormat"), .. NameBytes("ByteProperty"), .. BitConverter.GetBytes(8), .. BitConverter.GetBytes(0), .. float96, .. props.AsSpan(qt.NoneAt)];
        foreach (var (tag, value) in edits.OrderByDescending(e => e.Tag.Start))
        {
            if (value == null) { props = [.. props.AsSpan(0, tag.Start), .. props.AsSpan(tag.End)]; continue; }
            props = [.. props.AsSpan(0, tag.ValueAt), .. value, .. props.AsSpan(tag.End)];
            BinaryPrimitives.WriteInt32LittleEndian(props.AsSpan(tag.Start + 16), value.Length);
        }
        return [.. props, .. native];
    }

    /// <summary>A rotation as engine-compressed data stores it: the conjugate, w ≥ 0 (w is derived on reading).</summary>
    static Quaternion Stored(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var s = new Quaternion(-q.X, -q.Y, -q.Z, q.W);
        return s.W < 0 ? -s : s;
    }

    static ushort U16(float v) => (ushort)Math.Clamp((int)MathF.Round(v * 32767f + 32767f), 0, 65535);

    /// <summary>
    /// Every copied sequence decoded from the written package with its set's new track names: each body track the same as
    /// the source's decode, each hair track's keys within 0.05° of the built ones (0.5° for Fixed48NoW).
    /// </summary>
    sealed record Expect(int Seq, string Name, List<string> Track, Dictionary<string, Quaternion[]> Hair, string SourceFile, int SourceExport, IReadOnlyList<string> SourceTrack);

    static void Check(byte[] output, List<Expect> expect, List<string> lines)
    {
        var ap = AnimPackage.Read(output);
        float worstHair = 0, worstBody = 0; int bad = 0, checkedSeq = 0;
        string worstBodyAt = "";
        string? worstName = null;
        var sources = new Dictionary<string, AnimPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in expect)
        {
            var back = AnimObjectReader.TryRead(ap, e.Seq - 1, e.Track);
            if (back == null) { bad++; worstName ??= e.Name + " (doesn't decode)"; continue; }
            checkedSeq++;
            foreach (var (h, keys) in e.Hair)
            {
                if (!back.Tracks.TryGetValue(h, out var t) || t.RotationKeys.Count != keys.Length) { bad++; worstName ??= $"{e.Name} · {h}: {t?.RotationKeys.Count ?? 0} keys, {keys.Length} written"; continue; }
                for (int k = 0; k < keys.Length; k++)
                {
                    float d = Angle(t.RotationKeys[k].Rotation, keys[k]);
                    if (d > worstHair) { worstHair = d; if (d > 0.6f) worstName ??= $"{e.Name} · {h} frame {k}: {d:0.00}°"; }
                }
            }
            // the body: the source's own decode, track for track
            if (e.SourceExport < 0) continue;
            if (!sources.TryGetValue(e.SourceFile, out var sp)) sources[e.SourceFile] = sp = AnimPackage.Open(e.SourceFile);
            var orig = AnimObjectReader.TryRead(sp, e.SourceExport, e.SourceTrack);
            if (orig == null) continue;
            foreach (var (b, ot) in orig.Tracks)
            {
                if (!back.Tracks.TryGetValue(b, out var bt) || bt.RotationKeys.Count != ot.RotationKeys.Count || bt.PositionKeys.Count != ot.PositionKeys.Count) { bad++; worstName ??= $"{e.Name} · body {b} changed"; continue; }
                for (int k = 0; k < ot.RotationKeys.Count; k++) { float d = Angle(ot.RotationKeys[k].Rotation, bt.RotationKeys[k].Rotation); if (d > worstBody) { worstBody = d; worstBodyAt = $"{e.Name} · {b} rotation key {k}, source {Path.GetFileName(e.SourceFile)} #{e.SourceExport}, output #{e.Seq}"; } }
                for (int k = 0; k < ot.PositionKeys.Count; k++) { float d = (ot.PositionKeys[k].Position - bt.PositionKeys[k].Position).Length(); if (d > worstBody) { worstBody = d; worstBodyAt = $"{e.Name} · {b} position key {k}"; } }
            }
        }
        lines.Add($"read back: {checkedSeq} of {expect.Count} animations decode; body tracks off by {worstBody:0.0000}{(worstBody > 0 ? $" ({worstBodyAt})" : "")} (the game's own bytes), borrowed / edited tracks by {worstHair:0.000}°{(bad > 0 ? $"; {bad} PROBLEM(S), first: {worstName}" : "")}");
        if (bad > 0 || worstBody > 0.001f || worstHair > 0.6f) throw new InvalidDataException($"the hair animation didn't read back right: {lines[^1]}");
    }

    /// <summary>The angle between two rotations in degrees: 2·atan2(|xyz|, |w|) of the difference, exact near 0 (2·acos of the
    /// dot gave identical keys 0.0396° once the dot rounded to 0.99999994: Daredevil Modern's emote_listen, 0.17.2).</summary>
    static float Angle(Quaternion a, Quaternion b)
    {
        var d = Quaternion.Normalize(Quaternion.Conjugate(Quaternion.Normalize(a)) * Quaternion.Normalize(b));
        return 2 * MathF.Atan2(new Vector3(d.X, d.Y, d.Z).Length(), MathF.Abs(d.W)) * 180 / MathF.PI;
    }

    static T? Quiet<T>(Func<T?> f, out string said) where T : class => ConsoleCapture.Run(f, out said);

    static string Reason(string said) => string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not ")).Select(l => l.Trim())).Trim() is { Length: > 0 } r ? r : said.Trim();
}
