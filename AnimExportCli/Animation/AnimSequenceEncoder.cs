using System.Numerics;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;

namespace AnimExportCli.Animation;

/// <summary>
/// Encodes a <see cref="BoneAnimation"/> into the raw bytes of a compressed
/// AnimSequence track stream, using the simplest possible on-disk scheme:
/// <c>ACF_None</c> for both position and rotation — every key stored as
/// explicit floats, no bit-packing, no derived W, nothing ambiguous — and
/// <c>AKF_ConstantKeyLerp</c>, meaning every track gets exactly one key per
/// frame at uniform, implicit spacing, so no key-time bytes are written at
/// all. Bigger on disk than the game's own compressed tracks; in exchange,
/// this is exactly the format <see cref="AnimObjectReader"/> already decodes
/// with no format-specific special-casing, so the writer and reader are
/// proven consistent with each other by construction, not just by testing.
/// </summary>
public static class AnimSequenceEncoder
{
    /// <summary>
    /// Encodes one track per name in <paramref name="trackBoneNames"/>, in
    /// that order, sampling <paramref name="animation"/> at every integer
    /// frame from 0 to <paramref name="numFrames"/> - 1. A bone the clip
    /// doesn't drive — absent from <paramref name="animation"/>'s tracks, or
    /// missing one of position/rotation — gets a single key at that bone's
    /// rest pose from <paramref name="mesh"/> for whichever part is missing,
    /// matching how the engine already represents a track that holds still.
    /// </summary>
    public static (byte[] CompressedBytes, int[] TrackOffsets) Encode(
        SkeletalMesh mesh, IReadOnlyList<string> trackBoneNames, BoneAnimation animation, int numFrames)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(trackBoneNames);
        ArgumentNullException.ThrowIfNull(animation);
        if (numFrames <= 0) throw new ArgumentOutOfRangeException(nameof(numFrames), "Must encode at least one frame.");

        var restByName = new Dictionary<string, MeshBone>(StringComparer.OrdinalIgnoreCase);
        foreach (MeshBone bone in mesh.Bones) restByName.TryAdd(bone.Name, bone);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var trackOffsets = new int[trackBoneNames.Count * 4];

        for (int i = 0; i < trackBoneNames.Count; i++)
        {
            string boneName = trackBoneNames[i];
            animation.Tracks.TryGetValue(boneName, out BoneTrack? track);
            restByName.TryGetValue(boneName, out MeshBone? restBone);

            Vector3 restPosition = restBone?.Position ?? Vector3.Zero;
            Quaternion restRotation = restBone is not null ? FbxExporter.NormalisedOrIdentity(restBone.Orientation) : Quaternion.Identity;

            int translationOffset = checked((int)stream.Position);
            int translationKeyCount = WritePositionTrack(writer, track?.PositionKeys, numFrames, restPosition);

            int rotationOffset = checked((int)stream.Position);
            int rotationKeyCount = WriteRotationTrack(writer, track?.RotationKeys, numFrames, restRotation);

            trackOffsets[(i * 4) + 0] = translationOffset;
            trackOffsets[(i * 4) + 1] = translationKeyCount;
            trackOffsets[(i * 4) + 2] = rotationOffset;
            trackOffsets[(i * 4) + 3] = rotationKeyCount;
        }

        return (stream.ToArray(), trackOffsets);
    }

    private static int WritePositionTrack(BinaryWriter writer, IReadOnlyList<BonePositionKey>? keys, int numFrames, Vector3 restPosition)
    {
        if (keys is null || keys.Count == 0)
        {
            WriteVector3(writer, restPosition);
            return 1;
        }

        for (int frame = 0; frame < numFrames; frame++)
            WriteVector3(writer, FbxExporter.SamplePosition(keys, frame, restPosition));

        return numFrames;
    }

    private static int WriteRotationTrack(BinaryWriter writer, IReadOnlyList<BoneRotationKey>? keys, int numFrames, Quaternion restRotation)
    {
        if (keys is null || keys.Count == 0)
        {
            WriteQuaternion(writer, restRotation);
            return 1;
        }

        for (int frame = 0; frame < numFrames; frame++)
            WriteQuaternion(writer, FbxExporter.SampleRotation(keys, frame, restRotation));

        return numFrames;
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    private static void WriteQuaternion(BinaryWriter writer, Quaternion q)
    {
        writer.Write(q.X);
        writer.Write(q.Y);
        writer.Write(q.Z);
        writer.Write(q.W);
    }
}
