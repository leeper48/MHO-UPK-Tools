using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Puts an MFF model on an MHO hero's skeleton, keeping MFF proportions (Kurt, 2026-09-30):
/// 1. Biped bones map to MHO bones by name (<see cref="DefaultMap"/>); other MFF bones give their weights to the nearest
///    mapped ancestor.
/// 2. Each mapped MFF segment is swung (shortest arc, no roll) to point the way the matching MHO segment points; children
///    follow; the skinned mesh follows its bones. Segment lengths stay MFF's.
/// 3. The new reference skeleton keeps MHO's names, hierarchy and orientations, at the posed MFF joint positions; unmapped
///    MHO bones (offsets, twists, IK, eyes …) are placed from the MHO skeleton around the mapped ones.
/// 4. One uniform scale fits the MFF height to the MHO mesh's, and Y is negated (MFF normalized frame is right-handed,
///    MHO's left-handed), which also gives MHO's clockwise winding with the same corner order.
/// </summary>
static partial class Retarget
{
    /// <summary>MFF Biped bone → MHO bone (applied only when both exist).</summary>
    public static IEnumerable<(string Mff, string Mho)> DefaultMap()
    {
        yield return ("Bip001 Pelvis", "g_pelvis");
        yield return ("Bip001 Spine", "g_spine01");
        yield return ("Bip001 Spine1", "g_spine02");
        yield return ("Bip001 Spine2", "g_spine03");
        yield return ("Bip001 Neck", "g_neck");
        yield return ("Bip001 Head", "g_head");
        foreach (var (s, m) in new[] { ("L", "l"), ("R", "r") })
        {
            yield return ($"Bip001 {s} Thigh", $"g_{m}_hip");
            yield return ($"Bip001 {s} Calf", $"g_{m}_knee");
            yield return ($"Bip001 {s} Foot", $"g_{m}_ankle");
            yield return ($"Bip001 {s} Toe0", $"g_{m}_ball");
            yield return ($"Bip001 {s} Clavicle", $"g_{m}_clavical");
            yield return ($"Bip001 {s} UpperArm", $"g_{m}_shoulder");
            yield return ($"Bip001 {s} Forearm", $"g_{m}_elbow");
            yield return ($"Bip001 {s} Hand", $"g_{m}_wrist");
            yield return ($"Bip001 {s} ForeTwist", $"g_{m}_forarm");
            for (int t = 1; t <= 9; t++) yield return ($"Bip001 {s} ForeTwist{t}", $"g_{m}_forarm");
            yield return ($"Bip001 {s}UpArmTwist", $"g_{m}_biceptwist");
            yield return ($"Bip001 {s}ThighTwist", $"g_{m}_thightwist");
            yield return ($"Bip001 {s} ThighTwist", $"g_{m}_thightwist");
            yield return ($"Bip001 {s} UpArmTwist", $"g_{m}_biceptwist");
            string[] fingers = ["thumb", "index", "birdy", "ring", "pinky"];
            for (int f = 0; f < 5; f++)
            {
                yield return ($"Bip001 {s} Finger{f}", $"g_{m}_{fingers[f]}1");
                yield return ($"Bip001 {s} Finger{f}1", $"g_{m}_{fingers[f]}2");
                yield return ($"Bip001 {s} Finger{f}2", $"g_{m}_{fingers[f]}3");
            }
        }
    }

    /// <summary>Aim pairs: the segment from the first MFF bone to the second is swung onto the MHO segment between their
    /// mapped bones. Bones without one keep their parent's swing.</summary>
    static IEnumerable<(string Bone, string Toward)> Aims()
    {
        yield return ("Bip001 Spine", "Bip001 Spine1");
        yield return ("Bip001 Spine1", "Bip001 Spine2");
        yield return ("Bip001 Spine2", "Bip001 Neck");
        yield return ("Bip001 Neck", "Bip001 Head");
        foreach (var s in new[] { "L", "R" })
        {
            yield return ($"Bip001 {s} Thigh", $"Bip001 {s} Calf");
            yield return ($"Bip001 {s} Calf", $"Bip001 {s} Foot");
            yield return ($"Bip001 {s} Foot", $"Bip001 {s} Toe0");
            yield return ($"Bip001 {s} Clavicle", $"Bip001 {s} UpperArm");
            yield return ($"Bip001 {s} UpperArm", $"Bip001 {s} Forearm");
            yield return ($"Bip001 {s} Forearm", $"Bip001 {s} Hand");
            yield return ($"Bip001 {s} Hand", $"Bip001 {s} Finger2");
            for (int f = 0; f < 5; f++)
            {
                yield return ($"Bip001 {s} Finger{f}", $"Bip001 {s} Finger{f}1");
                yield return ($"Bip001 {s} Finger{f}1", $"Bip001 {s} Finger{f}2");
            }
        }
    }

    static Vector3 Mirror(Vector3 v) => new(v.X, -v.Y, v.Z);

    /// <summary>Retargets the parts onto the MHO skeleton (see <see cref="Job"/> for the steps). <paramref name="file"/>: an
    /// edited bone map (it decides every pair); <paramref name="options"/>: the switches (default: from the MFF_* environment).</summary>
    public static Retargeted Run(MffModel m, IEnumerable<Part> parts, MhoSkeleton sk, BoneMapFile? file = null, RetargetOptions? options = null)
    {
        var r = new Job(m, parts, sk, file, options ?? RetargetOptions.FromEnvironment()).Run();
        // the map file's weight smoothing (0.12.0), after everything else
        if (file is { Smooth.Count: > 0 }) r.Notes.AddRange(WeightSmooth.Apply(r, file.Smooth));
        return r;
    }
}
