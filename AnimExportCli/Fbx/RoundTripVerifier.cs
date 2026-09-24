using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;

namespace AnimExportCli.Fbx;

/// <summary>
/// Bakes an already-decoded animation to a temporary FBX, reads that FBX
/// back with <see cref="FbxAnimationImporter"/>, and reports how far the
/// result drifted from what export actually should have produced. On an
/// unedited round-trip this should come back essentially exact
/// (floating-point noise only) — that's the whole point: it isolates
/// whether the import math is a correct inverse of the export math, with no
/// package-writing risk at all, since nothing here touches a UPK.
/// </summary>
/// <remarks>
/// The comparison baseline is not the raw decoded animation — it's that
/// animation with the same single-key rotation correction
/// <see cref="FbxExporter.ResolveSingleKeyRotation"/> applies during export
/// applied here too. Comparing against the raw decode instead would flag
/// every bone that correction legitimately touches as "drifted", when the
/// round trip is doing exactly what it's supposed to.
/// </remarks>
public static class RoundTripVerifier
{
    public readonly record struct Result(
        int BoneCount,
        int MismatchedBoneCount,
        float MaxPositionError,
        float MaxRotationDegrees,
        string? WorstBone,
        IReadOnlyList<string> MissingBones,
        IReadOnlyList<string> KeyCountMismatchBones)
    {
        public bool Clean => MismatchedBoneCount == 0;
    }

    public static Result Verify(SkeletalMesh mesh, SkeletalMeshLod lod, BoneAnimation original)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"animexportcli-roundtrip-{Guid.NewGuid():N}.fbx");
        try
        {
            FbxExporter.WriteAnimated(tempPath, mesh, lod, original);
            BoneAnimation reimported = FbxAnimationImporter.Read(tempPath, original.Name);
            BoneAnimation expected = ApplyExportCorrections(mesh, original);
            return Compare(expected, reimported);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>Mirrors the single-key rotation correction <see cref="FbxExporter.BuildAnimation"/> applies, so the comparison is apples to apples.</summary>
    private static BoneAnimation ApplyExportCorrections(SkeletalMesh mesh, BoneAnimation original)
    {
        var restRotationByBone = new Dictionary<string, Quaternion>(StringComparer.OrdinalIgnoreCase);
        foreach (MeshBone bone in mesh.Bones) restRotationByBone.TryAdd(bone.Name, FbxExporter.NormalisedOrIdentity(bone.Orientation));

        var tracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);

        foreach ((string boneName, BoneTrack track) in original.Tracks)
        {
            if (track.RotationKeys.Count == 1 && restRotationByBone.TryGetValue(boneName, out Quaternion restRotation))
            {
                Quaternion resolved = FbxExporter.ResolveSingleKeyRotation(track.RotationKeys[0].Rotation, restRotation);
                tracks[boneName] = new BoneTrack
                {
                    PositionKeys = track.PositionKeys,
                    RotationKeys = [new BoneRotationKey(track.RotationKeys[0].TimeFrame, resolved)],
                };
            }
            else
            {
                tracks[boneName] = track;
            }
        }

        return new BoneAnimation { Name = original.Name, DurationSeconds = original.DurationSeconds, Tracks = tracks };
    }

    private static Result Compare(BoneAnimation expected, BoneAnimation reimported)
    {
        int mismatched = 0;
        float maxPositionError = 0f, maxRotationDegrees = 0f;
        string? worstBone = null;
        var missingBones = new List<string>();

        var keyCountMismatchBones = new List<string>();

        foreach ((string boneName, BoneTrack expectedTrack) in expected.Tracks)
        {
            if (!reimported.Tracks.TryGetValue(boneName, out BoneTrack? reimportedTrack))
            {
                mismatched++;
                missingBones.Add(boneName);
                continue;
            }

            bool boneClean = true;
            bool keyCountDiffered = false;

            if (expectedTrack.PositionKeys.Count != reimportedTrack.PositionKeys.Count) keyCountDiffered = true;
            for (int i = 0; i < expectedTrack.PositionKeys.Count; i++)
            {
                Vector3 sampled = SamplePosition(reimportedTrack.PositionKeys, expectedTrack.PositionKeys[i].TimeFrame);
                float error = Vector3.Distance(expectedTrack.PositionKeys[i].Position, sampled);
                if (error > maxPositionError) { maxPositionError = error; worstBone = boneName; }
                if (error > 0.05f) boneClean = false; // a real position difference, not just a different key layout for the same curve
            }

            if (expectedTrack.RotationKeys.Count != reimportedTrack.RotationKeys.Count) keyCountDiffered = true;
            for (int i = 0; i < expectedTrack.RotationKeys.Count; i++)
            {
                Quaternion sampled = SampleRotation(reimportedTrack.RotationKeys, expectedTrack.RotationKeys[i].TimeFrame);
                float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(expectedTrack.RotationKeys[i].Rotation, sampled)), -1f, 1f);
                float degrees = MathF.Acos(dot) * 2f * (180f / MathF.PI);
                if (degrees > maxRotationDegrees) { maxRotationDegrees = degrees; worstBone = boneName; }
                if (degrees > 1f) boneClean = false; // ditto for rotation
            }

            if (keyCountDiffered && boneClean) keyCountMismatchBones.Add(boneName);
            if (!boneClean) mismatched++;
        }

        return new Result(expected.Tracks.Count, mismatched, maxPositionError, maxRotationDegrees, worstBone, missingBones, keyCountMismatchBones);
    }

    /// <summary>Linear-interpolated position at an arbitrary frame — mirrors <see cref="FbxExporter"/>'s own sampling, so "does the curve still agree" means the same thing here as it does at export time.</summary>
    private static Vector3 SamplePosition(IReadOnlyList<BonePositionKey> keys, float frame)
    {
        if (keys.Count == 0) return Vector3.Zero;
        if (keys.Count == 1 || frame <= keys[0].TimeFrame) return keys[0].Position;
        if (frame >= keys[^1].TimeFrame) return keys[^1].Position;

        for (int i = 1; i < keys.Count; i++)
        {
            if (frame > keys[i].TimeFrame) continue;
            BonePositionKey a = keys[i - 1], c = keys[i];
            float span = c.TimeFrame - a.TimeFrame;
            float t = span > 0f ? (frame - a.TimeFrame) / span : 0f;
            return Vector3.Lerp(a.Position, c.Position, t);
        }

        return keys[^1].Position;
    }

    /// <summary>Spherically-interpolated rotation at an arbitrary frame — see the remark on <see cref="SamplePosition"/>.</summary>
    private static Quaternion SampleRotation(IReadOnlyList<BoneRotationKey> keys, float frame)
    {
        if (keys.Count == 0) return Quaternion.Identity;
        if (keys.Count == 1 || frame <= keys[0].TimeFrame) return keys[0].Rotation;
        if (frame >= keys[^1].TimeFrame) return keys[^1].Rotation;

        for (int i = 1; i < keys.Count; i++)
        {
            if (frame > keys[i].TimeFrame) continue;
            BoneRotationKey a = keys[i - 1], c = keys[i];
            float span = c.TimeFrame - a.TimeFrame;
            float t = span > 0f ? (frame - a.TimeFrame) / span : 0f;
            return Quaternion.Slerp(a.Rotation, c.Rotation, t);
        }

        return keys[^1].Rotation;
    }
}
