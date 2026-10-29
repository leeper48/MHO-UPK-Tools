using System.Buffers.Binary;
using System.Numerics;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// A donor's mount bone on another hero (Kurt, 2026-10-28: Hawkeye with Green Goblin's glider). Goblin's whole body hangs from
/// g_mountattach (root > g_mountgroundaim > g_mountattach > spine, hips) and his glider rides on it; his flight loop's barrel
/// roll is that bone turning. A hero without the bone can't play it: the glider stayed level while Hawkeye half rolled.
/// Tried and dropped: the roll baked into the glider's own animation (late, then around another point), and every edit of
/// the rider's borrowed tracks (the roll in the root: flicker; in the bones the mount carries: mangled; the loop held on its
/// first frame: he vanished), though the 3D preview showed each one right. Kurt's call: no roll. A loop the mount turns in a
/// lot was swapped for the donor's own run (0.60.260: closest so far, but he bobbed above and below the glider). 0.60.262: the
/// loop ended before the roll by its key counts and length: stretched and distorted in game. 0.60.263: the loop's roll frames
/// rewritten in place with his glide (TrimLoops), counts, offsets and length unchanged.
/// --mount-motion stays as the diagnostic.
/// </summary>
static partial class PowerBorrow
{
    /// <summary>The donor's mount bone relative to its root (row-vector matrices) for each of the donor's animations that
    /// move it, posed as the 3D preview poses them, and the reference: its place in the flight loop (else at rest).</summary>
    internal sealed record MountMotionData(string Bone, Dictionary<string, (AnimExportCli.Animation.BoneAnimation Anim, float Frames, float Seconds)> Anims, MeshAnimator Animator, int BoneIndex, Matrix4x4 Reference, string ReferenceFrom)
    {
        public Matrix4x4 At(string anim, float seconds)
        {
            var (a, frames, len) = Anims[anim];
            Animator.Pose(a, len > 0 ? Math.Clamp(seconds / len, 0, 1) * frames : 0);
            if (BoneIndex == 0) return Animator.BoneMatrix(0);   // (the root itself: against the model's origin)
            return Matrix4x4.Invert(Animator.BoneMatrix(0), out var inv) ? Animator.BoneMatrix(BoneIndex) * inv : Animator.BoneMatrix(BoneIndex);
        }
    }

    internal static MountMotionData? MountMotion(string donorHeroFile, string bone, string? cooked, List<string> log)
    {
        // the donor's model with that bone (the most bones: the character, not a prop)
        ModMeshes.Loaded? best = null; MeshRef? bestRef = null;
        foreach (var r in ModMeshes.List([(Path.GetFileName(donorHeroFile), donorHeroFile)], anyPackage: true))
        {
            var l = ModMeshes.Load(r, cooked, out _);
            if (l == null || !l.Bones.Any(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase))) continue;
            if (Environment.GetEnvironmentVariable("MHO_MOUNT_MESH") is { Length: > 0 } only && !r.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;   // (--mount-motion: that model)
            if (best == null || l.Bones.Count > best.Bones.Count) (best, bestRef) = (l, r);
        }
        if (best == null || bestRef == null) { log.Add($"mount: {Path.GetFileName(donorHeroFile)} has no model with {bone}"); return null; }
        var animator = new MeshAnimator(best.Bones, best.Positions, best.Normals, best.Influences);
        int bi = animator.BoneIndex(bone);
        var anims = new Dictionary<string, (AnimExportCli.Animation.BoneAnimation, float, float)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in ModAnimations.For(bestRef, best.Bones, [(Path.GetFileName(donorHeroFile), donorHeroFile)], cooked, minBones: 1))
        {
            if (anims.ContainsKey(a.Name) || ModAnimations.Load(a) is not { } ba || !ba.Tracks.TryGetValue(bone, out var t)) continue;
            if (t.RotationKeys.Count < 2 && t.PositionKeys.Count < 2) continue;   // (it stays put in this one)
            var (frames, seconds) = MeshAnimator.Span(ba);
            anims[a.Name] = (ba, frames, seconds);
        }
        // the reference: the flight loop's first frame (where the prop sits while flying, as Prop Fit was set), else rest
        string? refAnim = anims.Keys.FirstOrDefault(n => n.Equals("movement_run_flying_loop", StringComparison.OrdinalIgnoreCase))
                          ?? anims.Keys.Where(n => n.Contains("_loop", StringComparison.OrdinalIgnoreCase) && n.StartsWith("movement_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).FirstOrDefault();
        var data = new MountMotionData(bone, anims, animator, bi, Matrix4x4.Identity, refAnim ?? "rest");
        Matrix4x4 reference;
        if (refAnim != null) reference = data.At(refAnim, 0);
        else { animator.Pose(null, 0); reference = Matrix4x4.Invert(animator.BoneMatrix(0), out var inv) ? animator.BoneMatrix(bi) * inv : animator.BoneMatrix(bi); }
        return data with { Reference = reference };
    }

    /// <summary>
    /// How far (degrees) a donor's mount chain (g_mountattach and its parents up to the root) turns away from its first frame in
    /// one of its sequences (<paramref name="sequence"/> of the set <paramref name="set"/>), or 0 when it has no mount bone.
    /// </summary>
    internal static float MountTurn(AnimExportCli.Packages.Package ap, AnimExportCli.Animation.AnimObjectReader.AnimSetInfo set, int sequence, IReadOnlyList<AnimExportCli.Meshes.MeshBone> donorSkeleton)
    {
        const string mountBone = "g_mountattach";
        var chain = new List<AnimExportCli.Meshes.MeshBone>();
        int at = donorSkeleton.ToList().FindIndex(b => b.Name.Equals(mountBone, StringComparison.OrdinalIgnoreCase));
        for (int guard = 0; at > 0 && guard < 16; guard++) { chain.Add(donorSkeleton[at]); at = donorSkeleton[at].ParentIndex == at ? -1 : donorSkeleton[at].ParentIndex; }
        if (chain.Count == 0 || at != 0 || !set.TrackBoneNames.Contains(mountBone, StringComparer.OrdinalIgnoreCase)) return 0;
        if (AnimExportCli.Animation.AnimObjectReader.TryRead(ap, sequence, set.TrackBoneNames) is not { } anim) return 0;
        int frames = ap.TryReadProperties(sequence)?.GetInt("NumFrames") ?? 0;
        Quaternion Turn(float f)
        {
            var q = Quaternion.Identity;
            foreach (var b in chain)   // (the mount first, then its parents)
            {
                anim.Tracks.TryGetValue(b.Name, out var t);
                q = Quaternion.Concatenate(q, t is { RotationKeys.Count: > 0 } ? SampleRotation(t.RotationKeys, f) : Quaternion.Normalize(b.Orientation));
            }
            return Quaternion.Normalize(q);
        }
        var q0 = Turn(0);
        float most = 0;
        for (int f = 1; f < frames; f++) most = Math.Max(most, 2 * MathF.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(q0, Turn(f))))) * 180 / MathF.PI);
        return most;
    }

    /// <summary>
    /// Where to end a loop before its mount turns (Goblin's barrel roll): the first frame the mount chain is 20° from its first
    /// frame's place, stepped back to where that turn began, then of the frames in the half before it, the one whose pose is nearest the first frame's (the loop jumps
    /// back there). Null (with the reason) when the turn starts too early to leave a loop.
    /// </summary>
    internal static int? MountCut(AnimExportCli.Packages.Package ap, AnimExportCli.Animation.AnimObjectReader.AnimSetInfo set, int sequence, IReadOnlyList<AnimExportCli.Meshes.MeshBone> donorSkeleton, out string why)
    {
        why = "";
        var chain = new List<AnimExportCli.Meshes.MeshBone>();
        int at = donorSkeleton.ToList().FindIndex(b => b.Name.Equals("g_mountattach", StringComparison.OrdinalIgnoreCase));
        for (int guard = 0; at > 0 && guard < 16; guard++) { chain.Add(donorSkeleton[at]); at = donorSkeleton[at].ParentIndex == at ? -1 : donorSkeleton[at].ParentIndex; }
        if (chain.Count == 0 || AnimExportCli.Animation.AnimObjectReader.TryRead(ap, sequence, set.TrackBoneNames) is not { } anim) { why = "it doesn't read: left as it is"; return null; }
        int frames = ap.TryReadProperties(sequence)?.GetInt("NumFrames") ?? 0;
        static float Deg(Quaternion a, Quaternion b) => 2 * MathF.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(a, b)))) * 180 / MathF.PI;
        Quaternion Turn(float f)
        {
            var q = Quaternion.Identity;
            foreach (var b in chain)
            {
                anim.Tracks.TryGetValue(b.Name, out var t);
                q = Quaternion.Concatenate(q, t is { RotationKeys.Count: > 0 } ? SampleRotation(t.RotationKeys, f) : Quaternion.Normalize(b.Orientation));
            }
            return Quaternion.Normalize(q);
        }
        var q0 = Turn(0);
        if (Environment.GetEnvironmentVariable("MHO_MOUNT_DEBUG") == "1")
            Console.WriteLine("  mount turn by frame: " + string.Join(" ", Enumerable.Range(0, frames).Where(f => f % 5 == 0).Select(f => $"{f}:{Deg(q0, Turn(f)):0}")));
        // (Goblin's mount sways up to 7° while he glides: the roll is where it passes 20°, back to where it last sat still)
        int rollAt = Enumerable.Range(1, Math.Max(0, frames - 1)).FirstOrDefault(f => Deg(q0, Turn(f)) > 20, -1);
        while (rollAt > 1 && Deg(q0, Turn(rollAt - 1)) < Deg(q0, Turn(rollAt))) rollAt--;
        if (rollAt < 0) { why = "no roll found: left as it is"; return null; }
        if (rollAt < 30) { why = $"the roll starts at frame {rollAt}, too early to loop the part before it: left as it is"; return null; }
        // the pose nearest the first frame's (every animated track's turn), so the jump back to the start shows least
        var moving = anim.Tracks.Values.Where(t => t.RotationKeys.Count > 1).ToList();
        float Off(int f) => moving.Sum(t => Deg(SampleRotation(t.RotationKeys, 0), SampleRotation(t.RotationKeys, f)));
        int best = Enumerable.Range(rollAt / 2, rollAt - rollAt / 2).MinBy(Off);
        why = $"the roll starts at frame {rollAt} of {frames}: the loop ends at frame {best} ({best / 30f:0.00} s), the pose there {Off(best) / Math.Max(1, moving.Count):0.0}° from its start on average";
        return best;
    }

    /// <summary>
    /// The borrowed loops (sequences named in <paramref name="cuts"/>, in sets named *<paramref name="setSuffix"/>) without
    /// their roll: every key after the cut frame is rewritten in place with a glide, his own frames from before the cut played
    /// back (cut − j) blending into the loop's first frames played back (M − j), so frame 226 meets frame 0 again. Nothing
    /// else changes: the key counts, offsets, NumFrames and SequenceLength stay (Kurt, 2026-10-29: cutting the counts and the
    /// length stretched and distorted him in game; edits that only change values in place, the root's lift, work there).
    /// Float96NoW rotations and plain positions only (rotations are blended as stored: the stored conjugate blends the same);
    /// others are left as they are and said. Read back and checked.
    /// </summary>
    internal static byte[] TrimLoops(byte[] package, string setSuffix, IReadOnlyDictionary<string, int> cuts, List<string> log)
    {
        var dst = Package.FromBytes(package);
        var setName = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(setSuffix) + @"(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var expect = new List<(int Seq, string Name, byte[] Data)>();
        for (int i = 0; i < dst.Exports.Length; i++)
        {
            var e = dst.Exports[i];
            if (!dst.ClassOf(e).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase) || e.OuterIndex <= 0 || !setName.IsMatch(dst.Exports[e.OuterIndex - 1].ObjectName)) continue;
            byte[] d = dst.ReadExportBytes(e).ToArray();
            var tags = TagWalker.Walk(dst, d, 4);
            // (cooked sequences are animsequence_39 …: the animation's name is its SequenceName)
            string seqName = tags?.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase)) is { } sn ? dst.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(sn.ValueAt))] : e.ObjectName;
            if (tags == null || !cuts.TryGetValue(seqName, out int cut)) continue;
            string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? dst.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
            string tf = Enum("TranslationCompressionFormat"), rf = Enum("RotationCompressionFormat"), kf = Enum("KeyEncodingFormat");
            var nf = tags.FirstOrDefault(t => t.Name.Equals("NumFrames", StringComparison.OrdinalIgnoreCase));
            var cto = tags.FirstOrDefault(t => t.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
            if (nf == null || cto == null || (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase))
                || !rf.Equals("acf_float96now", StringComparison.OrdinalIgnoreCase) || (kf.Length > 0 && !kf.Equals("akf_constantkeylerp", StringComparison.OrdinalIgnoreCase)))
            { log.Add($"anims: {seqName}: keys stored as {tf}/{rf}/{kf}: its roll stays"); continue; }
            int frames = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nf.ValueAt));
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(cto.ValueAt));
            int m = frames - 1 - cut;   // (the frames after the cut)
            if (cut < 2 || m < 2 || cut - m < 0 || n % 4 != 0) { log.Add($"anims: {seqName}: can't be cut at frame {cut} of {frames}: its roll stays"); continue; }
            // native data: the raw tracks (skipped), then the compressed stream (count + bytes)
            int p = tags.NoneAt + 8;
            int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
            if (raw < 0 || raw > 100000) { log.Add($"anims: {seqName}: its raw tracks don't read: its roll stays"); continue; }
            for (int k = 0; k < raw; k++)
            {
                p += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
                p += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
            }
            int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4;
            if (len < 0 || p + len > d.Length) { log.Add($"anims: {seqName}: its key data doesn't read: its roll stays"); continue; }
            byte[] o = d.ToArray();   // (keys read from the original, written into the copy)
            static float W(int j, int m) { float x = (float)j / m; return x * x * (3 - 2 * x); }
            int tracks = 0;
            bool ok = true;
            for (int k = 0; k < n && ok; k += 4)
                for (int c = 0; c < 2 && ok; c++)
                {
                    int off = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(cto.ValueAt + 4 + 4 * (k + 2 * c)));
                    int keys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(cto.ValueAt + 4 + 4 * (k + 2 * c + 1)));
                    if (keys <= 1) continue;
                    if (keys != frames || off < 0 || off + 12 * keys > len) { ok = false; break; }
                    Vector3 Key(int f) { int z = p + off + 12 * f; return new(BinaryPrimitives.ReadSingleLittleEndian(o.AsSpan(z)), BinaryPrimitives.ReadSingleLittleEndian(o.AsSpan(z + 4)), BinaryPrimitives.ReadSingleLittleEndian(o.AsSpan(z + 8))); }
                    for (int jj = 1; jj <= m; jj++)
                    {
                        Vector3 a = Key(cut - jj), b = Key(m - jj), v;
                        float w = W(jj, m);
                        if (c == 0) v = Vector3.Lerp(a, b, w);   // (a position)
                        else
                        {
                            // (a rotation: x, y, z stored, w = the positive rest)
                            static Quaternion Q(Vector3 x) => new(x, MathF.Sqrt(MathF.Max(0, 1 - x.LengthSquared())));
                            var q = Quaternion.Slerp(Q(a), Q(b), w);
                            if (q.W < 0) q = Quaternion.Negate(q);
                            q = Quaternion.Normalize(q);
                            v = new(q.X, q.Y, q.Z);
                        }
                        int z = p + off + 12 * (cut + jj);
                        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(z), v.X);
                        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(z + 4), v.Y);
                        BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(z + 8), v.Z);
                    }
                    tracks++;
                }
            if (!ok) { log.Add($"anims: {seqName}: a track's keys aren't one per frame: its roll stays"); continue; }
            byte[] nd = d;
            rep[i] = _ => nd;
            expect.Add((i, seqName, nd));
            log.Add($"anims: {seqName}: frames {cut + 1}-{frames - 1} of {frames} (the roll) replaced in place by his glide before it, blending back into the start ({tracks} tracks); key counts and length unchanged");
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("taking the roll out: " + string.Join("; ", problems.Take(2)));
        // read back: same size as before, the sequences decode with their key counts
        string tmp = Path.Combine(Path.GetTempPath(), $"mho_trim_{Environment.ProcessId}_{Guid.NewGuid():N}.upk");
        File.WriteAllBytes(tmp, output);
        try
        {
            var ap = AnimExportCli.Packages.Package.Open(tmp);
            foreach (var (seq, name, _) in expect)
            {
                var set = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).FirstOrDefault(s => s.Sequences.Any(q => q.IsExport && q.ExportIndex == seq))
                          ?? throw new InvalidDataException($"taking the roll out: {name}'s set doesn't read back");
                _ = AnimExportCli.Animation.AnimObjectReader.TryRead(ap, seq, set.TrackBoneNames) ?? throw new InvalidDataException($"taking the roll out: {name} doesn't read back");
                if (ap.TryReadProperties(seq)?.GetInt("NumFrames") is not int nfb || dst.Exports[seq].SerialSize != Package.FromBytes(output).Exports[seq].SerialSize)
                    throw new InvalidDataException($"taking the roll out: {name} changed size");
            }
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        return output;
    }

    /// <summary>A position track at a frame, blended linearly between its keys.</summary>
    static Vector3 SamplePosition(IReadOnlyList<AnimExportCli.Animation.BonePositionKey> keys, float frame)
    {
        if (frame <= keys[0].TimeFrame) return keys[0].Position;
        for (int i = 1; i < keys.Count; i++)
            if (frame <= keys[i].TimeFrame)
            {
                float span = keys[i].TimeFrame - keys[i - 1].TimeFrame;
                return Vector3.Lerp(keys[i - 1].Position, keys[i].Position, span > 0 ? (frame - keys[i - 1].TimeFrame) / span : 0);
            }
        return keys[^1].Position;
    }
}
