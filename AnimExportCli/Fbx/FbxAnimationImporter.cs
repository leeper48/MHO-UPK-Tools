using System.Numerics;
using Assimp;
using AnimExportCli.Animation;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion; // Assimp also defines Quaternion; keeps every bare use unambiguous.

namespace AnimExportCli.Fbx;

public sealed class AnimationImportException(string message) : Exception(message);

/// <summary>
/// Reads one baked animation clip out of an FBX (typically one this tool
/// exported and someone then edited in a 3D package) back into a
/// <see cref="BoneAnimation"/> — the same shape <see cref="AnimObjectReader"/>
/// produces from a UPK, so downstream code doesn't need to care which source
/// an animation came from.
/// </summary>
/// <remarks>
/// This is deliberately the mirror image of <see cref="FbxExporter.WriteAnimated"/>,
/// undoing each of its steps in reverse: Assimp's fixed 24-tick assumption,
/// then the file-space axis conversion (self-inverse, so the exact same
/// helper the exporter uses handles both directions). What this does not
/// attempt is re-deriving the engine's original *compressed* on-disk
/// representation — that's a separate concern for whatever writes this data
/// back into a package, not for reading it out of an FBX.
/// </remarks>
public static class FbxAnimationImporter
{
    /// <summary>
    /// Reads the named clip, or the first clip in the file when
    /// <paramref name="clipName"/> is null — every FBX this tool itself
    /// writes has exactly one, but an externally authored one might not.
    /// </summary>
    public static BoneAnimation Read(string path, string? clipName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var context = new AssimpContext();
        Scene scene;
        try
        {
            scene = context.ImportFile(path, PostProcessSteps.None);
        }
        catch (AssimpException ex)
        {
            throw new AnimationImportException($"Could not read '{path}': {ex.Message}");
        }

        if (scene.AnimationCount == 0)
            throw new AnimationImportException($"'{path}' has no animation to import.");

        Assimp.Animation? clip = clipName is null
            ? scene.Animations[0]
            : scene.Animations.Find(a => string.Equals(a.Name, clipName, StringComparison.OrdinalIgnoreCase));

        if (clip is null)
            throw new AnimationImportException($"'{path}' has no animation named '{clipName}'.");

        var tracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);
        float maxFrame = 0f;

        foreach (NodeAnimationChannel channel in clip.NodeAnimationChannels)
        {
            var positionKeys = new List<BonePositionKey>(channel.PositionKeys.Count);
            foreach (VectorKey key in channel.PositionKeys)
            {
                var filePosition = new Vector3(key.Value.X, key.Value.Y, key.Value.Z);
                Vector3 enginePosition = FbxExporter.ToFileSpace(filePosition);
                float frame = FrameFromTick(key.Time);
                positionKeys.Add(new BonePositionKey(frame, enginePosition));
                if (frame > maxFrame) maxFrame = frame;
            }

            var rotationKeys = new List<BoneRotationKey>(channel.RotationKeys.Count);
            foreach (QuaternionKey key in channel.RotationKeys)
            {
                var fileRotation = new Quaternion(key.Value.X, key.Value.Y, key.Value.Z, key.Value.W);
                Matrix4x4 fileMatrix = Matrix4x4.CreateFromQuaternion(fileRotation);
                Matrix4x4.Decompose(FbxExporter.ToFileSpace(fileMatrix), out _, out Quaternion engineRotation, out _);

                float frame = FrameFromTick(key.Time);
                rotationKeys.Add(new BoneRotationKey(frame, engineRotation));
                if (frame > maxFrame) maxFrame = frame;
            }

            if (positionKeys.Count == 0 && rotationKeys.Count == 0) continue;

            tracks[channel.NodeName] = new BoneTrack { PositionKeys = positionKeys, RotationKeys = rotationKeys };
        }

        if (tracks.Count == 0)
            throw new AnimationImportException($"'{path}' has animation data, but none of its channel names matched a bone.");

        return new BoneAnimation
        {
            Name = clipName ?? clip.Name ?? Path.GetFileNameWithoutExtension(path),
            DurationSeconds = maxFrame, // Frame count, not real seconds — this tool never uses it for anything but a log line, and the FBX doesn't reliably carry the source clip's true frame rate back out.
            Tracks = tracks,
        };
    }

    /// <summary>
    /// No scaling here, deliberately — this is not the mirror image of
    /// <see cref="FbxExporter"/>'s ×24 compensation, and applying it again
    /// on the way in was a real bug caught by <see cref="RoundTripVerifier"/>
    /// (frame times inflated 24×, sampling landed in the wrong part of every
    /// curve, huge apparent drift). The ×24 quirk is specifically something
    /// AssimpNet's FBX <em>writer</em> does when converting the ticks this
    /// tool hands it into the file's stored values — confirmed back when the
    /// timing bug was first found, by reading a written file straight back
    /// through Assimp and checking its ticks were already plain frame
    /// numbers (0, 1, 2, ... matching the reference file exactly), with no
    /// further conversion needed. Once that's baked into the file on write,
    /// <see cref="AssimpContext.ImportFile"/> hands the tick value straight
    /// back as the frame number it already is.
    /// </summary>
    private static float FrameFromTick(double tick) => (float)tick;
}
