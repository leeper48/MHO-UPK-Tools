using System.Numerics;
using AnimExportCli.Fbx;
using AnimExportCli.Meshes;

namespace AnimExportCli.Animation;

/// <summary>
/// Encodes an animation with <see cref="AnimSequenceEncoder"/>, then decodes
/// the result straight back with the same <see cref="AnimObjectReader"/>
/// functions that read a real UPK — not a separate check of the encoder's
/// own assumptions, the actual production decode path — and compares
/// against what the source animation says at every frame. This is what
/// proves the encoder and decoder agree, the same way
/// <see cref="RoundTripVerifier"/> proved the FBX export and import agree.
/// </summary>
public static class EncoderRoundTripVerifier
{
    public readonly record struct Result(int TrackCount, float MaxPositionError, float MaxRotationDegrees, string? WorstBone)
    {
        public bool Clean => MaxPositionError < 0.001f && MaxRotationDegrees < 0.01f;
    }

    public static Result Verify(SkeletalMesh mesh, IReadOnlyList<string> trackBoneNames, BoneAnimation animation, int numFrames)
    {
        (byte[] bytes, int[] offsets) = AnimSequenceEncoder.Encode(mesh, trackBoneNames, animation, numFrames);

        var restByName = new Dictionary<string, MeshBone>(StringComparer.OrdinalIgnoreCase);
        foreach (MeshBone bone in mesh.Bones) restByName.TryAdd(bone.Name, bone);

        float maxPositionError = 0f, maxRotationDegrees = 0f;
        string? worstBone = null;

        for (int i = 0; i < trackBoneNames.Count; i++)
        {
            string boneName = trackBoneNames[i];
            int translationOffset = offsets[(i * 4) + 0];
            int translationNumKeys = offsets[(i * 4) + 1];
            int rotationOffset = offsets[(i * 4) + 2];
            int rotationNumKeys = offsets[(i * 4) + 3];

            List<BonePositionKey> decodedPositions = AnimObjectReader.DecodePositionTrack(
                bytes, translationOffset, translationNumKeys, AnimationCompressionFormat.None, AnimationKeyFormat.ConstantKeyLerp, numFrames);
            List<BoneRotationKey> decodedRotations = AnimObjectReader.DecodeRotationTrack(
                bytes, rotationOffset, rotationNumKeys, AnimationCompressionFormat.None, AnimationKeyFormat.ConstantKeyLerp, numFrames);

            restByName.TryGetValue(boneName, out MeshBone? restBone);
            Vector3 restPosition = restBone?.Position ?? Vector3.Zero;
            Quaternion restRotation = restBone is not null ? FbxExporter.NormalisedOrIdentity(restBone.Orientation) : Quaternion.Identity;

            animation.Tracks.TryGetValue(boneName, out BoneTrack? track);
            IReadOnlyList<BonePositionKey> originalPositions = track?.PositionKeys ?? [];
            IReadOnlyList<BoneRotationKey> originalRotations = track?.RotationKeys ?? [];

            for (int frame = 0; frame < numFrames; frame++)
            {
                Vector3 expectedPosition = originalPositions.Count > 0 ? FbxExporter.SamplePosition(originalPositions, frame, restPosition) : restPosition;
                Vector3 actualPosition = decodedPositions.Count > 0 ? FbxExporter.SamplePosition(decodedPositions, frame, restPosition) : restPosition;
                float positionError = Vector3.Distance(expectedPosition, actualPosition);
                if (positionError > maxPositionError) { maxPositionError = positionError; worstBone = boneName; }

                Quaternion expectedRotation = originalRotations.Count > 0 ? FbxExporter.SampleRotation(originalRotations, frame, restRotation) : restRotation;
                Quaternion actualRotation = decodedRotations.Count > 0 ? FbxExporter.SampleRotation(decodedRotations, frame, restRotation) : restRotation;
                float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(expectedRotation, actualRotation)), -1f, 1f);
                float degrees = MathF.Acos(dot) * 2f * (180f / MathF.PI);
                if (degrees > maxRotationDegrees) { maxRotationDegrees = degrees; worstBone = boneName; }
            }
        }

        return new Result(trackBoneNames.Count, maxPositionError, maxRotationDegrees, worstBone);
    }
}
