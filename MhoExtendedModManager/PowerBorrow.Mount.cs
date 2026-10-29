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
/// lot is swapped for the donor's own run, a glide on the same mount without the roll, copied as the game made it
/// (MountRollLoop). --mount-motion stays as the diagnostic.
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
