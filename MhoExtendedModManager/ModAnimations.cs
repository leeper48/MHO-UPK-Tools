using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoExtendedModManager;

/// <summary>An animation (AnimSequence) that can play on a mesh in the preview's 3D view.</summary>
sealed record AnimRef(string Package, string File, string Name, int SequenceExport, IReadOnlyList<string> TrackBoneNames)
{
    /// <summary>The AnimSet's bones that take the animation's positions; null = all (bAnimRotationOnly false).</summary>
    public IReadOnlySet<string>? TranslationBones { get; init; }
}

/// <summary>
/// Animations for the preview's 3D view (Kurt: pick one from a drop-down). A costume's meshes are in
/// UC__MarvelPlayer_&lt;Hero&gt;_&lt;Costume&gt;_SF, but the hero's animations are in the base UC__MarvelPlayer_&lt;Hero&gt;_SF (Storm's:
/// two AnimSets, 116 + 12 sequences, 161 bones; checked 2026-09-28), so both are searched, the mod's copy of the base
/// package first, else the game's. Only AnimSets whose bones match the mesh's are offered. Decoding is AnimExportCli's
/// AnimObjectReader (the reader its FBX export uses); the pose math follows its FbxExporter (local = rotation then
/// translation, a bone without a track keeps its rest pose, the single-key rotation sign chosen by the rest pose).
/// </summary>
static class ModAnimations
{
    /// <summary>
    /// UE3 AnimSets are rotation-only by default (bAnimRotationOnly, omitted when true): a bone's position comes from the
    /// mesh's own bind pose unless the set lists it in UseTranslationBoneNames (Daredevil's: 16 of 98 bones). Applying every
    /// position put Daredevil's eyes on Magik's head in the preview (a costume moved to another hero), while the game
    /// showed her right. Kept per decoded animation for MeshAnimator.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BoneAnimation, IReadOnlySet<string>> translation = new();
    public static IReadOnlySet<string>? TranslationBones(BoneAnimation a) => translation.TryGetValue(a, out var s) ? s : null;

    static readonly Dictionary<string, AnimPackage> open = new(StringComparer.OrdinalIgnoreCase);

    static AnimPackage Open(string path)
    {
        lock (open)
        {
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            if (open.TryGetValue(key, out var p)) return p;
            if (open.Count > 3) open.Clear();
            return open[key] = AnimPackage.Open(path);
        }
    }

    /// <summary>The packages to search for a mesh: its own, then (costumes) the hero's base package: the mod's copy, else the game's.</summary>
    static IEnumerable<(string File, string Path)> Sources(MeshRef mesh, IEnumerable<(string File, string Path)> modPackages, string? cooked)
    {
        yield return (mesh.Package, mesh.File);
        const string player = "UC__MarvelPlayer_";
        if (!mesh.Package.StartsWith(player, StringComparison.OrdinalIgnoreCase)) yield break;
        string hero = Path.GetFileNameWithoutExtension(mesh.Package)[player.Length..].Split('_')[0];
        string baseFile = $"{player}{hero}_SF.upk";
        if (baseFile.Equals(mesh.Package, StringComparison.OrdinalIgnoreCase)) yield break;
        var mine = modPackages.FirstOrDefault(p => p.File.Equals(baseFile, StringComparison.OrdinalIgnoreCase));
        if (mine.Path != null && File.Exists(mine.Path)) yield return mine;
        else if (cooked != null && File.Exists(Path.Combine(cooked, baseFile))) yield return (baseFile, StockFiles.For(cooked, baseFile));
    }

    /// <summary>The animations that fit a mesh (its bones), by name.</summary>
    public static List<AnimRef> For(MeshRef mesh, IReadOnlyList<MeshBone> bones, IEnumerable<(string File, string Path)> modPackages, string? cooked, int minBones = 3)
    {
        var boneNames = bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<AnimRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = modPackages.ToList();
        // The game's order first (Kurt, 2026-10-02): a character's component lists its AnimSets and the last set with a name
        // wins (Storm's rework set replaces three combo animations of her first set; a costume's own set its idle, emotes …).
        // Then the sets of the packages below that the list doesn't name (a prop's own skeleton).
        var listed = new HashSet<(string, int)>();
        Func<string, (string Path, bool FromMod)?> fileFor = name =>
        {
            string f = name.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? name : name + ".upk";
            var mine = mods.FirstOrDefault(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (mine.Path != null && File.Exists(mine.Path)) return (mine.Path, true);
            if (cooked == null) return null;
            string stock = StockFiles.For(cooked, f);
            return File.Exists(stock) ? (stock, false) : null;
        };
        if (mesh.Package.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && CostumeAnims.Read(mesh.File, mesh.Package, fileFor) is { } ca)
            foreach (var set in ca.Sets.Reverse().Where(s => s.Found))
            {
                listed.Add((set.File!, set.Export));
                // The game matches tracks to bones by name whatever their number: a set it lists counts when half of the
                // smaller skeleton matches (Storm's 161-bone set swapped onto Rescue's 83 bones matches 79).
                int match = set.TrackBoneNames.Count(boneNames.Contains);
                if (match < Math.Max(minBones, Math.Min(set.TrackBoneNames.Count, boneNames.Count) / 2)) continue;
                foreach (var (name, ex) in set.Sequences)
                    if (seen.Add(name)) result.Add(new AnimRef(set.PackageName + ".upk", set.File!, name, ex, set.TrackBoneNames) { TranslationBones = set.TranslationBones });
            }
        foreach (var (file, path) in Sources(mesh, mods, cooked))
        {
            AnimPackage pkg;
            try { pkg = Open(path); } catch (Exception ex) when (ex is IOException or InvalidDataException or AnimExportCli.Packages.InvalidPackageException) { continue; }
            foreach (var set in AnimObjectReader.FindAnimSets(pkg))
            {
                if (listed.Contains((path, set.ExportIndex))) continue;
                int match = set.TrackBoneNames.Count(boneNames.Contains);
                if (match < Math.Max(minBones, set.TrackBoneNames.Count / 2)) continue;   // another skeleton (a prop's own skeleton can be small: minBones)
                foreach (var seq in set.Sequences.Where(s => s.IsExport))
                {
                    string name;
                    try { name = AnimObjectReader.GetSequenceDisplayName(pkg, seq.ExportIndex); } catch { continue; }
                    if (seen.Add(name)) result.Add(new AnimRef(file, path, name, seq.ExportIndex, set.TrackBoneNames)
                        { TranslationBones = set.RotationOnly ? new HashSet<string>(set.TranslationBones ?? [], StringComparer.OrdinalIgnoreCase) : null });
                }
            }
        }
        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Decodes an animation; null if it can't be read.</summary>
    public static BoneAnimation? Load(AnimRef a)
    {
        try
        {
            var anim = AnimObjectReader.TryRead(Open(a.File), a.SequenceExport, a.TrackBoneNames);
            if (anim != null && a.TranslationBones != null) translation.AddOrUpdate(anim, a.TranslationBones);
            return anim;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or AnimExportCli.Packages.InvalidPackageException or ArgumentException or IndexOutOfRangeException) { return null; }
    }
}

/// <summary>
/// Plays an animation on a skinned mesh: the pose at a time (per bone: its track's position / rotation, else the rest
/// pose; parents first), then every vertex moved by its bones' weights (skinning matrix = rest model→bone × posed
/// bone→model). The root bone keeps its rest X/Y so a run or walk plays in place.
/// </summary>
sealed class MeshAnimator
{
    readonly IReadOnlyList<MeshBone> bones;
    readonly Vector3[] restPos, restNrm;
    readonly Vector4[] restTan;
    readonly IReadOnlyList<VertexInfluence> influences;
    readonly Matrix4x4[] restModelToBone;
    readonly Matrix4x4[] posed, skin;
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    /// <summary>Tangents (xyz) turned with the mesh; w, the bitangent's sign, is kept.</summary>
    public Vector4[] Tangents { get; }

    public MeshAnimator(IReadOnlyList<MeshBone> bones, Vector3[] positions, Vector3[] normals, IReadOnlyList<VertexInfluence> influences, Vector4[]? tangents = null)
    {
        this.bones = bones; restPos = positions; restNrm = normals; this.influences = influences;
        restTan = tangents ?? [];
        Tangents = new Vector4[restTan.Length];
        var rest = AnimExportCli.Fbx.SkeletonPose.Rest(bones);
        restModelToBone = [.. rest.ModelToBone];
        posed = new Matrix4x4[bones.Count]; skin = new Matrix4x4[bones.Count];
        Positions = new Vector3[positions.Length]; Normals = new Vector3[normals.Length];
    }

    static Quaternion Unit(Quaternion q) { float l = q.Length(); return l > 0.0001f ? Quaternion.Normalize(q) : Quaternion.Identity; }

    /// <summary>The bones' names (skeleton order), for finding one such as the head.</summary>
    public IEnumerable<string> BoneNames => bones.Select(b => b.Name);

    /// <summary>A bone's transform (bone → model space) in the pose last made by Pose: what a held prop follows.</summary>
    public Matrix4x4 BoneMatrix(int bone) => bone >= 0 && bone < posed.Length ? posed[bone] : Matrix4x4.Identity;

    /// <summary>A bone's index by name (case-insensitive), or -1.</summary>
    public int BoneIndex(string name) { int i = 0; foreach (var b in bones) { if (b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i; i++; } return -1; }

    /// <summary>A bone's position in the pose last made by Pose (engine space).</summary>
    public Vector3 BonePosition(int bone) => bone >= 0 && bone < posed.Length ? posed[bone].Translation : Vector3.Zero;

    /// <summary>The frames an animation spans (its last key), and its length in seconds.</summary>
    public static (float Frames, float Seconds) Span(BoneAnimation a)
    {
        float last = 0;
        foreach (var t in a.Tracks.Values)
        {
            if (t.PositionKeys.Count > 0) last = Math.Max(last, t.PositionKeys[^1].TimeFrame);
            if (t.RotationKeys.Count > 0) last = Math.Max(last, t.RotationKeys[^1].TimeFrame);
        }
        return (last, a.DurationSeconds > 0 ? a.DurationSeconds : last / 30f);
    }

    /// <summary>Poses the mesh at a frame of <paramref name="a"/> (null: the rest pose) into Positions / Normals.</summary>
    public void Pose(BoneAnimation? a, float frame)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            Vector3 p = b.Position; Quaternion r = Unit(b.Orientation);
            if (a != null && a.Tracks.TryGetValue(b.Name, out var t))
            {
                // Rotation-only sets: the animation's position only for its listed bones (and the root).
                var tb = ModAnimations.TranslationBones(a);
                if (tb == null || i == 0 || b.ParentIndex < 0 || tb.Contains(b.Name)) p = Sample(t.PositionKeys, frame, p);
                var keys = t.RotationKeys;
                if (keys.Count == 1) r = SingleKey(keys[0].Rotation, r);
                else r = Sample(keys, frame, r);
                if (i == 0 || b.ParentIndex < 0) p = new Vector3(b.Position.X, b.Position.Y, p.Z);   // play in place
            }
            var local = Matrix4x4.CreateFromQuaternion(Unit(r)) * Matrix4x4.CreateTranslation(p);
            posed[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? local * posed[b.ParentIndex] : local;
            skin[i] = restModelToBone[i] * posed[i];
        }
        for (int v = 0; v < restPos.Length; v++)
        {
            var inf = v < influences.Count ? influences[v] : default;
            if (inf.Bones == null || inf.Bones.Count == 0) { Positions[v] = restPos[v]; if (v < Normals.Length) Normals[v] = restNrm[v]; if (v < Tangents.Length) Tangents[v] = restTan[v]; continue; }
            Vector3 sp = Vector3.Zero, sn = Vector3.Zero, st = Vector3.Zero; float total = 0;
            var rt = v < restTan.Length ? new Vector3(restTan[v].X, restTan[v].Y, restTan[v].Z) : Vector3.Zero;
            for (int k = 0; k < inf.Bones.Count; k++)
            {
                int bi = inf.Bones[k]; float w = inf.Weights[k];
                if (w <= 0 || bi < 0 || bi >= skin.Length) continue;
                sp += Vector3.Transform(restPos[v], skin[bi]) * w;
                if (v < restNrm.Length) sn += Vector3.TransformNormal(restNrm[v], skin[bi]) * w;
                if (v < restTan.Length) st += Vector3.TransformNormal(rt, skin[bi]) * w;
                total += w;
            }
            Positions[v] = total > 0 ? sp / total : restPos[v];
            if (v < Normals.Length) Normals[v] = total > 0 && sn != Vector3.Zero ? Vector3.Normalize(sn) : restNrm[v];
            if (v < Tangents.Length) Tangents[v] = total > 0 && st != Vector3.Zero ? new Vector4(Vector3.Normalize(st), restTan[v].W) : restTan[v];
        }
    }

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

    /// <summary>A single rotation key: as decoded or its conjugate, whichever is nearer the rest pose (FbxExporter.ResolveSingleKeyRotation).</summary>
    static Quaternion SingleKey(Quaternion decoded, Quaternion rest)
    {
        var conj = new Quaternion(-decoded.X, -decoded.Y, -decoded.Z, decoded.W);
        return MathF.Abs(Quaternion.Dot(conj, rest)) > MathF.Abs(Quaternion.Dot(decoded, rest)) ? conj : decoded;
    }
}
