using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoMffImporter;

/// <summary>An MHO animation that fits a skeleton.</summary>
sealed record MhoAnimRef(string File, string Name, int Export, IReadOnlyList<string> TrackBones, IReadOnlySet<string>? TranslationBones);

/// <summary>
/// MHO animations for checking a retarget, ported from the MHO Extended Mod Manager (ModAnimations / MeshAnimator,
/// proven against the game): a costume's animations live in the hero's base package UC__MarvelPlayer_&lt;Hero&gt;_SF;
/// AnimSets are rotation-only unless they list UseTranslationBoneNames; local = rotation × translation, posed = local ×
/// parent; a bone without a track keeps its rest pose; a single rotation key takes the sign nearer the rest pose; the root
/// keeps its rest X/Y (plays in place). Read only.
/// </summary>
static class MhoAnim
{
    /// <summary>The hero's base package next to a costume package (or the package itself).</summary>
    public static string BasePackage(string package)
    {
        const string player = "UC__MarvelPlayer_";
        string file = Path.GetFileName(package);
        if (!file.StartsWith(player, StringComparison.OrdinalIgnoreCase)) return package;
        string hero = Path.GetFileNameWithoutExtension(file)[player.Length..].Split('_')[0];
        string b = Path.Combine(Path.GetDirectoryName(package)!, $"{player}{hero}_SF.upk");
        return File.Exists(b) ? b : package;
    }

    /// <summary>The bones the hero's animations place (UseTranslationBoneNames of its rotation-only AnimSets, all sets that fit
    /// the skeleton together); empty when none, or when a fitting set isn't rotation-only.</summary>
    public static HashSet<string> TranslationBones(string package, IEnumerable<string> boneNames)
    {
        var names = boneNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in new[] { package, BasePackage(package) }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var pkg = AnimPackage.Open(file);
                foreach (var set in AnimObjectReader.FindAnimSets(pkg))
                {
                    if (set.TrackBoneNames.Count(names.Contains) < Math.Max(3, set.TrackBoneNames.Count / 2)) continue;
                    if (!set.RotationOnly) return new(StringComparer.OrdinalIgnoreCase);
                    foreach (var b in set.TranslationBones ?? []) result.Add(b);
                }
            }
        }
        catch (Exception) { }
        return result;
    }

    public static List<MhoAnimRef> For(string package, IEnumerable<string> boneNames)
    {
        var names = boneNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<MhoAnimRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { package, BasePackage(package) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pkg = AnimPackage.Open(file);
            foreach (var set in AnimObjectReader.FindAnimSets(pkg))
            {
                if (set.TrackBoneNames.Count(names.Contains) < Math.Max(3, set.TrackBoneNames.Count / 2)) continue;
                foreach (var seq in set.Sequences.Where(s => s.IsExport))
                {
                    string n;
                    try { n = AnimObjectReader.GetSequenceDisplayName(pkg, seq.ExportIndex); } catch { continue; }
                    if (seen.Add(n)) list.Add(new MhoAnimRef(file, n, seq.ExportIndex, set.TrackBoneNames,
                        set.RotationOnly ? new HashSet<string>(set.TranslationBones ?? [], StringComparer.OrdinalIgnoreCase) : null));
                }
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static BoneAnimation? Load(MhoAnimRef a) => AnimObjectReader.TryRead(AnimPackage.Open(a.File), a.Export, a.TrackBones);

    public static float Frames(BoneAnimation a)
    {
        float last = 0;
        foreach (var t in a.Tracks.Values)
        {
            if (t.PositionKeys.Count > 0) last = Math.Max(last, t.PositionKeys[^1].TimeFrame);
            if (t.RotationKeys.Count > 0) last = Math.Max(last, t.RotationKeys[^1].TimeFrame);
        }
        return last;
    }

    /// <summary>Skeleton (names, parents, local rotation / position) from model-space bone matrices, in the reader's
    /// convention (local = rotation × translation; global = local × parent).</summary>
    public static List<MeshBone> FromGlobals(IReadOnlyList<(string Name, int Parent, Matrix4x4 Global)> bones)
    {
        var list = new List<MeshBone>();
        foreach (var (name, parent, g) in bones)
        {
            var local = g;
            if (parent >= 0 && Matrix4x4.Invert(bones[parent].Global, out var inv)) local = g * inv;
            Matrix4x4.Decompose(local, out _, out var rot, out var pos);
            list.Add(new MeshBone { Name = name, ParentIndex = parent, Orientation = Quaternion.Normalize(rot), Position = pos });
        }
        return list;
    }

    /// <summary>Poses a skinned mesh (vertices in bind pose, model space) at a frame; null animation = rest.</summary>
    public static Vector3[] Pose(IReadOnlyList<MeshBone> bones, IReadOnlyList<Vector3> positions, IReadOnlyList<(int Bone, float Weight)[]> weights,
                                 BoneAnimation? a, float frame, IReadOnlySet<string>? translationBones)
    {
        var rest = SkeletonPose.Rest(bones);
        var posed = new Matrix4x4[bones.Count]; var skin = new Matrix4x4[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            Vector3 p = b.Position; Quaternion r = Unit(b.Orientation);
            if (a != null && a.Tracks.TryGetValue(b.Name, out var t))
            {
                bool root = i == 0 || b.ParentIndex < 0 || b.ParentIndex == i;
                if (translationBones == null || root || translationBones.Contains(b.Name)) p = Sample(t.PositionKeys, frame, p);
                r = t.RotationKeys.Count == 1 ? SingleKey(t.RotationKeys[0].Rotation, r) : Sample(t.RotationKeys, frame, r);
                if (root) p = new Vector3(b.Position.X, b.Position.Y, p.Z);
            }
            var local = Matrix4x4.CreateFromQuaternion(Unit(r)) * Matrix4x4.CreateTranslation(p);
            posed[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? local * posed[b.ParentIndex] : local;
            skin[i] = rest.ModelToBone[i] * posed[i];
        }
        var outPos = new Vector3[positions.Count];
        for (int v = 0; v < positions.Count; v++)
        {
            var inf = weights[v];
            if (inf.Length == 0) { outPos[v] = positions[v]; continue; }
            Vector3 sp = Vector3.Zero; float tot = 0;
            foreach (var (bi, w) in inf) { if (w <= 0 || bi < 0 || bi >= skin.Length) continue; sp += Vector3.Transform(positions[v], skin[bi]) * w; tot += w; }
            outPos[v] = tot > 0 ? sp / tot : positions[v];
        }
        return outPos;
    }

    public static (int, float)[][] Weights(SkeletalMeshLod lod) =>
        lod.Influences.Select(i => i.Bones == null ? [] : i.Bones.Zip(i.Weights, (b, w) => (b, w)).ToArray()).ToArray();

    static Quaternion Unit(Quaternion q) { float l = q.Length(); return l > 0.0001f ? Quaternion.Normalize(q) : Quaternion.Identity; }

    static Vector3 Sample(IReadOnlyList<BonePositionKey> keys, float f, Vector3 fallback)
    {
        if (keys.Count == 0) return fallback;
        if (keys.Count == 1 || f <= keys[0].TimeFrame) return keys[0].Position;
        if (f >= keys[^1].TimeFrame) return keys[^1].Position;
        for (int i = 1; i < keys.Count; i++)
            if (f <= keys[i].TimeFrame) { var a = keys[i - 1]; var c = keys[i]; float s = c.TimeFrame - a.TimeFrame; return Vector3.Lerp(a.Position, c.Position, s > 0 ? (f - a.TimeFrame) / s : 0); }
        return keys[^1].Position;
    }

    static Quaternion Sample(IReadOnlyList<BoneRotationKey> keys, float f, Quaternion fallback)
    {
        if (keys.Count == 0) return fallback;
        if (keys.Count == 1 || f <= keys[0].TimeFrame) return keys[0].Rotation;
        if (f >= keys[^1].TimeFrame) return keys[^1].Rotation;
        for (int i = 1; i < keys.Count; i++)
            if (f <= keys[i].TimeFrame) { var a = keys[i - 1]; var c = keys[i]; float s = c.TimeFrame - a.TimeFrame; return Quaternion.Slerp(a.Rotation, c.Rotation, s > 0 ? (f - a.TimeFrame) / s : 0); }
        return keys[^1].Rotation;
    }

    static Quaternion SingleKey(Quaternion decoded, Quaternion rest)
    {
        var conj = new Quaternion(-decoded.X, -decoded.Y, -decoded.Z, decoded.W);
        return MathF.Abs(Quaternion.Dot(conj, rest)) > MathF.Abs(Quaternion.Dot(decoded, rest)) ? conj : decoded;
    }
}
