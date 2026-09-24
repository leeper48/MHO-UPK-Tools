using System.Numerics;

namespace AnimExportCli.Animation;

/// <summary>
/// One sampled position key. <c>TimeFrame</c> is a frame index (fractional
/// for variable-rate keys), not seconds — matching the game's own frame-based
/// timing rather than converting to real-world time, since keys aren't stored
/// at a uniform rate to begin with.
/// </summary>
public readonly record struct BonePositionKey(float TimeFrame, Vector3 Position);

/// <summary>One sampled rotation key. See <see cref="BonePositionKey"/> for what <c>TimeFrame</c> means.</summary>
public readonly record struct BoneRotationKey(float TimeFrame, Quaternion Rotation);

/// <summary>One bone's animated track. Either list may be empty — a bone with no keys of one kind keeps its bind-pose value for that kind, for the whole clip.</summary>
public sealed class BoneTrack
{
    public IReadOnlyList<BonePositionKey> PositionKeys { get; init; } = [];
    public IReadOnlyList<BoneRotationKey> RotationKeys { get; init; } = [];
}

/// <summary>One animation clip, as tracks keyed by bone name (case-insensitive, matching <see cref="Meshes.MeshBone.Name"/>).</summary>
public sealed class BoneAnimation
{
    public required string Name { get; init; }
    public required float DurationSeconds { get; init; }
    public required IReadOnlyDictionary<string, BoneTrack> Tracks { get; init; }
}
