using System.Buffers.Binary;
using System.Numerics;
using AnimExportCli.Meshes;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// An added prop's Pivot on the Hand, baked into the animations a costume took from another character (Kurt, 2026-10-23:
/// Thor's hammer on Captain America). Thor's animations place his hammer bone (g_r_palm, under his spine there) on their
/// own: held in his hand in idle (the same place against his wrist every frame) and swung by its strap in fidget02
/// (--bone-rel: up to 29 units from the wrist, frames 7 to 77). On Captain America that bone hangs from the wrist, so the
/// donor's positions put the hammer by his head. Held: each copied animation's keys of the bone are rewritten to move
/// against the costume's own grip (its rest place under the wrist) as the donor's moves against the donor's grip (the
/// donor's idle, frame 0): held frames stay in the hand, a swing swings. Let go: the donor's own keys come back (turned to
/// this skeleton's rest, as the copy had them). An animation whose donor isn't known keeps the earlier pin (the set stops
/// moving the bone), named in the log.
/// </summary>
static class PropHold
{
    /// <summary>Where a copied animation came from: the donor's package (a path) and its animation's name there.</summary>
    public sealed record Donor(string Package, string Animation);

    sealed class DonorData
    {
        public required IReadOnlyList<MeshBone> Skeleton;
        public required Dictionary<string, Quaternion> Turns;
        public Matrix4x4? Grip;   // the bone against its parent's place in the donor's idle (frame 0), by bone
        public string GripOf = "";
    }

    /// <summary>
    /// The package with every …_on_… AnimSet's animations holding (<paramref name="hold"/>) or letting go of
    /// <paramref name="bones"/>. <paramref name="donorFor"/>: a copied animation's slot (its name in the set) → its donor;
    /// <paramref name="target"/>: the costume's skeleton.
    /// </summary>
    /// <param name="fit">The prop's fit on the bone (PowerBorrow.FitMatrix): the donor's motion is taken in the prop's own
    /// frame, so it pivots where the donor's does (the hammer's strap) whatever its size and turn in this hand.</param>
    /// <param name="extra">Per animation (its slot), a further turn and move along the character's axes (PropAnimFit), the
    /// turn about the bone's place, or null.</param>
    public static byte[] Apply(byte[] package, IReadOnlyList<string> bones, bool hold, Func<string, Donor?> donorFor, IReadOnlyList<MeshBone>? target, List<string> log, Matrix4x4? fit = null,
        Func<string, (Matrix4x4 Turn, Vector3 Move)?>? extra = null)
    {
        var use = bones.Where(b => !b.Equals("none", StringComparison.OrdinalIgnoreCase) && !b.Equals("root", StringComparison.OrdinalIgnoreCase)).ToList();
        if (use.Count == 0) return package;
        var p = Package.FromBytes(package);
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return p.Names.Length + j;
        }
        var donors = new Dictionary<string, DonorData?>(StringComparer.OrdinalIgnoreCase);
        DonorData? DataFor(string pkgPath)
        {
            if (donors.TryGetValue(pkgPath, out var dd)) return dd;
            dd = null;
            if (target != null && File.Exists(pkgPath) && PowerBorrow.HeroSkeleton(pkgPath) is { } sk)
                dd = new DonorData { Skeleton = sk, Turns = AnimSwap.RestTurns(sk, target) };
            return donors[pkgPath] = dd;
        }
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var baked = new List<string>(); var pinned = new List<string>(); var notes = new List<string>();
        for (int i = 0; i < p.Exports.Length; i++)
        {
            if (!p.ClassOf(p.Exports[i]).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) || !p.Exports[i].ObjectName.Contains("_on_", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] sd = p.ReadExportBytes(p.Exports[i]).ToArray();
            var stags = TagWalker.Walk(p, sd, 4);
            var tb = stags?.FirstOrDefault(x => x.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
            var sq = stags?.FirstOrDefault(x => x.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase));
            if (tb == null || sq == null) continue;
            int nb = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(tb.ValueAt));
            var trackNames = Enumerable.Range(0, nb).Select(k => p.Names[BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(tb.ValueAt + 4 + 8 * k))]).ToList();
            var seqs = new List<int>();
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(sq.ValueAt)); k < n; k++)
                if (BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(sq.ValueAt + 4 + 4 * k)) is int r && r > 0) seqs.Add(r - 1);
            // every animation of the set rewritten, or the set keeps the earlier pin (positions of the bone not taken)
            var done = new Dictionary<int, byte[]>();
            bool all = true;
            foreach (int s in seqs)
            {
                byte[] d = p.ReadExportBytes(p.Exports[s]).ToArray();
                var nt = TagWalker.Walk(p, d, 4)?.FirstOrDefault(y => y.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && y.Size == 8);
                string slot = nt == null ? "" : TagWalker.NameAt(p, d, nt.ValueAt);
                string why = "";
                if (donorFor(slot) is not { } dn) why = "its source isn't known";
                else if (DataFor(dn.Package) is not { } dd) why = "the source's skeleton doesn't read";
                else
                    foreach (string bone in use)
                        if (!Rewrite(p, d, trackNames, bone, hold, dn, dd, target!, fit ?? Matrix4x4.Identity, hold ? extra?.Invoke(slot) : null, out why)) break;
                if (why.Length > 0) { all = false; notes.Add($"{slot}: {why}"); break; }
                done[s] = d;
            }
            byte[] nsd = sd;
            if (all && done.Count > 0)
            {
                foreach (var (s, d) in done) { byte[] keep = d; rep[s] = _ => keep; }
                foreach (string bone in use) nsd = AnimSwap.WithTranslationBone(p, nsd, bone, NameIdx);
                baked.Add(p.Exports[i].ObjectName);
            }
            else if (hold)
            {
                foreach (string bone in use) if (AnimSwap.WithoutTranslationBone(p, nsd, bone) is { } x) nsd = x;
                pinned.Add(p.Exports[i].ObjectName);
            }
            else foreach (string bone in use) nsd = AnimSwap.WithTranslationBone(p, nsd, bone, NameIdx);
            if (!ReferenceEquals(nsd, sd) && !nsd.AsSpan().SequenceEqual(sd)) { byte[] keep = nsd; rep[i] = _ => keep; }
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the pivot couldn't be " + (hold ? "kept on the hand: " : "let go again: ") + string.Join("; ", problems.Take(2)));
        string bn = string.Join(", ", use);
        if (baked.Count > 0) log.Add(hold ? $"{bn}: follows the source's grip, baked into {string.Join(", ", baked)}" : $"{bn}: the source's own keys back in {string.Join(", ", baked)}");
        if (pinned.Count > 0) log.Add($"{bn}: not moved by {string.Join(", ", pinned)} ({string.Join("; ", notes)})");
        return output;
    }

    /// <summary>
    /// One copied animation's keys of <paramref name="bone"/> rewritten in place (plain float keys: positions ACF_None,
    /// rotations Float96NoW, not per-track; the key counts as stored), from the donor's animation of the same keys.
    /// </summary>
    static bool Rewrite(Package p, byte[] d, List<string> trackNames, string bone, bool hold, Donor dn, DonorData dd, IReadOnlyList<MeshBone> target, Matrix4x4 fit, (Matrix4x4 Turn, Vector3 Move)? extra, out string why)
    {
        why = "";
        int ti = trackNames.FindIndex(n => n.Equals(bone, StringComparison.OrdinalIgnoreCase));
        if (ti < 0) return true;   // (the set doesn't move the bone: nothing to do)
        var tags = TagWalker.Walk(p, d, 4);
        if (tags == null) { why = "the animation doesn't read"; return false; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? p.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), rf = Enum("RotationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (kf.Equals("akf_pertrackcompression", StringComparison.OrdinalIgnoreCase)) { why = "per-track compression"; return false; }
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = $"positions stored as {tf}"; return false; }
        if (rf.Length > 0 && !rf.Equals("acf_float96now", StringComparison.OrdinalIgnoreCase)) { why = $"rotations stored as {rf}"; return false; }
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        if (ot == null || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * trackNames.Count) { why = "the track offsets don't match the bones"; return false; }
        int at = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)); at += 4;
        if (raw < 0 || raw > 100000) { why = "the raw tracks don't read"; return false; }
        for (int k = 0; k < raw; k++)
        {
            at += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
            at += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
        }
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at)); at += 4;
        int o = ot.ValueAt + 4 + 16 * ti;
        int tOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o)), tKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 4));
        int rOff = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 8)), rKeys = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o + 12));
        if ((tKeys > 0 && (tOff < 0 || tOff + 12 * tKeys > len)) || (rKeys > 0 && (rOff < 0 || rOff + 12 * rKeys > len))) { why = "the keys run past the data"; return false; }

        // the donor's animation, decoded from its own package
        var ar = ModAnimations.Named(dn.Package, -1, null, dn.Animation);
        var da = ar == null ? null : ModAnimations.Load(ar);
        if (da == null || !da.Tracks.TryGetValue(bone, out var track)) { why = $"{Path.GetFileName(dn.Package)} has no {dn.Animation} moving {bone}"; return false; }
        if (track.PositionKeys.Count != tKeys || track.RotationKeys.Count != rKeys) { why = $"the keys don't match the source's ({tKeys}/{rKeys} vs {track.PositionKeys.Count}/{track.RotationKeys.Count})"; return false; }

        Vector3[] pos = [.. track.PositionKeys.Select(k => k.Position)];
        Quaternion[] rot;
        if (!hold)
            rot = [.. track.RotationKeys.Select(k => dd.Turns.TryGetValue(bone, out var turn) ? Quaternion.Normalize(AnimSwap.Mul(k.Rotation, turn)) : k.Rotation)];
        else
        {
            int tb = Index(target, bone);
            if (tb < 0 || target[tb].ParentIndex < 0) { why = $"this skeleton has no {bone}"; return false; }
            string parent = target[target[tb].ParentIndex].Name;
            int db = Index(dd.Skeleton, bone), dp = Index(dd.Skeleton, parent);
            if (db < 0 || dp < 0) { why = $"the source's skeleton has no {bone} or {parent}"; return false; }
            var anim = new MeshAnimator(dd.Skeleton, [], [], []);
            Matrix4x4 Rel(AnimExportCli.Animation.BoneAnimation? a, float frame)
            {
                anim.Pose(a, frame);
                Matrix4x4.Invert(anim.BoneMatrix(dp), out var inv);
                return anim.BoneMatrix(db) * inv;
            }
            // the donor's grip: the bone against the parent in its idle (frame 0), else at this animation's first frame
            if (dd.Grip == null || dd.GripOf != bone + "|" + parent)
            {
                var idle = ModAnimations.Named(dn.Package, -1, null, "idle") is { } ir ? ModAnimations.Load(ir) : null;
                dd.Grip = idle != null && idle.Tracks.ContainsKey(bone) ? Rel(idle, 0) : null;
                dd.GripOf = bone + "|" + parent;
            }
            var grip = dd.Grip ?? Rel(da, 0);
            Matrix4x4.Invert(grip, out var gripInv);
            Matrix4x4.Invert(fit, out var fitInv);
            var mine = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(target[tb].Orientation)) * Matrix4x4.CreateTranslation(target[tb].Position);
            // the donor's move off its grip, in the bone's (the prop's) own frame: rel = move × grip; here the same move in the
            // fitted prop's frame, from this skeleton's grip
            Matrix4x4 Held(float frame) => fitInv * (Rel(da, frame) * gripInv) * fit * mine;
            // this animation's own adjustment, along the character's axes (the same in every frame): this skeleton posed as
            // the copy plays (the donor's keys, turned to this skeleton's rest as the copy has them), its place there turned
            // about the bone's place and moved, then back under the parent
            Func<float, Matrix4x4> Local = Held;
            if (extra is { } ex)
            {
                var turned = new AnimExportCli.Animation.BoneAnimation
                {
                    Name = da.Name, DurationSeconds = da.DurationSeconds,
                    Tracks = da.Tracks.ToDictionary(kv => kv.Key, kv => !dd.Turns.TryGetValue(kv.Key, out var turn) ? kv.Value : new AnimExportCli.Animation.BoneTrack
                    {
                        PositionKeys = kv.Value.PositionKeys,
                        RotationKeys = [.. kv.Value.RotationKeys.Select(k => new AnimExportCli.Animation.BoneRotationKey(k.TimeFrame, Quaternion.Normalize(AnimSwap.Mul(k.Rotation, turn))))],
                    }, StringComparer.OrdinalIgnoreCase),
                };
                ModAnimations.SetTranslationBones(turned, ModAnimations.TranslationBones(da));
                var mineAnim = new MeshAnimator(target, [], [], []);
                int tp = target[tb].ParentIndex;
                Local = frame =>
                {
                    mineAnim.Pose(turned, frame);
                    var parentAt = mineAnim.BoneMatrix(tp);
                    var at = Held(frame) * parentAt;
                    var o = at.Translation;
                    var moved = at * Matrix4x4.CreateTranslation(-o) * ex.Turn * Matrix4x4.CreateTranslation(o + ex.Move);
                    Matrix4x4.Invert(parentAt, out var pInv);
                    return moved * pInv;
                };
            }
            for (int k = 0; k < pos.Length; k++) pos[k] = Local(track.PositionKeys[k].TimeFrame).Translation;
            rot = new Quaternion[track.RotationKeys.Count];
            for (int k = 0; k < rot.Length; k++)
            {
                Matrix4x4.Decompose(Local(track.RotationKeys[k].TimeFrame), out _, out var q, out _);
                rot[k] = Quaternion.Normalize(q);
            }
        }
        int data = at;
        for (int k = 0; k < pos.Length; k++)
        {
            int q = data + tOff + 12 * k;
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q), pos[k].X);
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 4), pos[k].Y);
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 8), pos[k].Z);
        }
        for (int k = 0; k < rot.Length; k++)
        {
            var nq = rot[k].W < 0 ? -rot[k] : rot[k];
            int q = data + rOff + 12 * k;
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q), -nq.X);   // stored: the conjugate
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 4), -nq.Y);
            BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(q + 8), -nq.Z);
        }
        return true;
    }

    static int Index(IReadOnlyList<MeshBone> sk, string name)
    {
        for (int i = 0; i < sk.Count; i++) if (sk[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
