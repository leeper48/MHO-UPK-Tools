using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Cape motion by motion matching (2026-10-02): the strand simulation (CapeBake) tied a rigid cape against Thor's real one,
/// since hand-keyed cape motion doesn't follow physics frame by frame. Here every frame of an animation takes the cape of
/// the donor frame whose body is posed and moving most like it: a library of every frame of a caped donor's animations
/// (body features → the cape bones' local rotations), searched per frame, then smoothed over time. Body features, in the
/// character's model space (animations play in place, facing +X): the directions of the pelvis → upper spine, upper spine →
/// neck, thigh, shin and upper-arm segments, and the same directions' change since the previous frame (the movement).
/// The cape bones' local rotations carry over as they are, since the cape (bones and bind pose) is the donor's own.
/// </summary>
sealed class CapeMatch
{
    static readonly string[][] Segments =
    [
        ["g_pelvis", "g_spine03"], ["g_spine03", "g_neck"], ["g_neck", "g_head"],
        ["g_l_hip", "g_l_knee"], ["g_l_knee", "g_l_ankle"], ["g_r_hip", "g_r_knee"], ["g_r_knee", "g_r_ankle"],
        ["g_l_shoulder", "g_l_elbow"], ["g_r_shoulder", "g_r_elbow"],
    ];
    const float MotionWeight = 6f;   // a direction's change per frame counts this much more than the direction (tuned below)

    readonly List<float[]> features = new();
    readonly List<Quaternion[]> capes = new();
    readonly List<string> sources = new();
    readonly string[] capeBones;
    public int Frames => features.Count;
    /// <summary>Diagnostics (--hair-jitter's trace): when set, Apply adds the library frame it picked for each frame.</summary>
    public List<int>? trace;
    /// <summary>A library frame's source: the donor animation and the frame in it.</summary>
    public (string Anim, int Frame) Source(int i) { int k = i; while (k > 0 && sources[k - 1] == sources[i]) k--; return (sources[i], i - k); }

    CapeMatch(string[] capeBones) => this.capeBones = capeBones;

    /// <summary>The donor library: every frame of the donor's animations (<paramref name="skip"/>: an animation left out, for testing).</summary>
    public static CapeMatch Build(IReadOnlyList<MeshBone> donorBones, CapeBake.Cape cape, IEnumerable<BoneAnimation> anims, string? skip = null) =>
        Build(donorBones, cape.Bones, anims, skip);

    /// <summary>The library for any set of donor bones (a cape, hair), in skeleton order.</summary>
    public static CapeMatch Build(IReadOnlyList<MeshBone> donorBones, IEnumerable<int> rigBones, IEnumerable<BoneAnimation> anims, string? skip = null)
    {
        var lib = new CapeMatch(rigBones.Select(b => donorBones[b].Name).ToArray());
        var poser = new MeshAnimator(donorBones, [], [], []);
        var seg = Indices(donorBones);
        foreach (var a in anims)
        {
            if (skip != null && a.Name.Equals(skip, StringComparison.OrdinalIgnoreCase)) continue;
            var (frames, _) = MeshAnimator.Span(a);
            int n = Math.Max(1, (int)MathF.Ceiling(frames));
            Vector3[]? before = null;
            for (int f = 0; f <= n; f++)
            {
                poser.Pose(a, Math.Min(f, frames));
                var dirs = Dirs(poser, seg);
                lib.features.Add(Feature(dirs, before ?? dirs));
                before = dirs;
                var q = new Quaternion[lib.capeBones.Length];
                for (int k = 0; k < q.Length; k++) q[k] = LocalRotation(poser, donorBones, lib.capeBones[k]);
                lib.capes.Add(q);
                lib.sources.Add(a.Name);
            }
        }
        return lib;
    }

    static int[][] Indices(IReadOnlyList<MeshBone> bones)
    {
        int I(string n) { for (int i = 0; i < bones.Count; i++) if (bones[i].Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        return [.. Segments.Select(s => new[] { I(s[0]), I(s[1]) })];
    }

    static Vector3[] Dirs(MeshAnimator poser, int[][] seg) =>
        [.. seg.Select(s => s[0] < 0 || s[1] < 0 ? Vector3.Zero : SafeNorm(poser.BonePosition(s[1]) - poser.BonePosition(s[0])))];

    static Vector3 SafeNorm(Vector3 v) => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : Vector3.Zero;

    static float[] Feature(Vector3[] dirs, Vector3[] before)
    {
        var f = new float[dirs.Length * 6];
        for (int i = 0; i < dirs.Length; i++)
        {
            f[i * 6] = dirs[i].X; f[i * 6 + 1] = dirs[i].Y; f[i * 6 + 2] = dirs[i].Z;
            var d = (dirs[i] - before[i]) * MotionWeight;
            f[i * 6 + 3] = d.X; f[i * 6 + 4] = d.Y; f[i * 6 + 5] = d.Z;
        }
        return f;
    }

    /// <summary>A bone's local rotation in the pose last made (from the posed matrices: local = global × parent⁻¹).</summary>
    static Quaternion LocalRotation(MeshAnimator poser, IReadOnlyList<MeshBone> bones, string name)
    {
        int b = poser.BoneIndex(name);
        if (b < 0) return Quaternion.Identity;
        int p = bones[b].ParentIndex;
        var g = poser.BoneMatrix(b);
        var local = p >= 0 && p != b && Matrix4x4.Invert(poser.BoneMatrix(p), out var inv) ? g * inv : g;
        local.Translation = Vector3.Zero;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
    }

    /// <summary>
    /// <paramref name="a"/> (posed on <paramref name="bones"/>, which must hold the donor's cape bones) with cape tracks matched
    /// from the library and smoothed (a centred window of <paramref name="smooth"/> frames each side). Position keys are kept
    /// only for <paramref name="translation"/> bones, as CapeBake.Bake.
    /// </summary>
    public BoneAnimation Apply(BoneAnimation a, IReadOnlyList<MeshBone> bones, IReadOnlySet<string>? translation, int smooth = 3, string? skip = null, Matrix4x4? rootCorrection = null,
        IReadOnlyDictionary<string, Matrix4x4>? corrections = null, float amplify = 1)
    {
        var body = CapeBake.Without(a, capeBones.ToHashSet(StringComparer.OrdinalIgnoreCase), translation);
        var (frames, _) = MeshAnimator.Span(a);
        int n = Math.Max(1, (int)MathF.Ceiling(frames));
        var poser = new MeshAnimator(bones, [], [], []);
        var seg = Indices(bones);
        var feats = new float[n + 1][];
        Vector3[]? before = null;
        for (int f = 0; f <= n; f++)
        {
            poser.Pose(body, Math.Min(f, frames));
            var dirs = Dirs(poser, seg);
            feats[f] = Feature(dirs, before ?? dirs);
            before = dirs;
        }
        var path = BestPath(feats, skip);
        var picked = new Quaternion[n + 1][];
        for (int f = 0; f <= n; f++) { picked[f] = capes[path[f]]; trace?.Add(path[f]); }
        // smoothed: per bone, the window's rotations averaged (signs aligned to the frame's own)
        var tracks = new Dictionary<string, BoneTrack>(body.Tracks, StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < capeBones.Length; k++)
        {
            var keys = new List<BoneRotationKey>();
            for (int f = 0; f <= n; f++)
            {
                var c = picked[f][k]; var sum = Vector4.Zero;
                for (int w = -smooth; w <= smooth; w++)
                {
                    var q = picked[Math.Clamp(f + w, 0, n)][k];
                    if (Quaternion.Dot(q, c) < 0) q = -q;
                    float wt = smooth + 1 - Math.Abs(w);
                    sum += new Vector4(q.X, q.Y, q.Z, q.W) * wt;
                }
                var avg = Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W));
                // the cape root (first) hangs on another hero's parent bone: carried into its frame
                if (k == 0 && rootCorrection is { } rc && !rc.IsIdentity)
                    avg = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateFromQuaternion(avg) * rc));
                // a group root hanging on another hero's bone (hair: three roots on g_head): carried into its frame
                if (corrections != null && corrections.TryGetValue(capeBones[k], out var cr) && !cr.IsIdentity)
                    avg = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateFromQuaternion(avg) * cr));
                // exaggerated (Mega Hair): the turn away from the bone's rest pose scaled (a long mane swings further than the
                // donor's chest-length hair: Angela's own moves 7° from rest on average)
                if (amplify != 1 && bones.FirstOrDefault(b => b.Name.Equals(capeBones[k], StringComparison.OrdinalIgnoreCase)) is { } rb)
                {
                    var rest = Quaternion.Normalize(rb.Orientation);
                    var dq = Quaternion.Normalize(Quaternion.Inverse(rest) * avg);
                    if (dq.W < 0) dq = -dq;
                    float ang = 2 * MathF.Acos(Math.Clamp(dq.W, -1, 1));
                    var axis = new Vector3(dq.X, dq.Y, dq.Z);
                    if (axis.LengthSquared() > 1e-10f && ang > 1e-5f)
                        avg = Quaternion.Normalize(rest * Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Math.Min(ang * amplify, MathF.PI * 0.9f)));
                }
                if (keys.Count > 0 && Quaternion.Dot(keys[^1].Rotation, avg) < 0) avg = -avg;
                keys.Add(new BoneRotationKey(Math.Min(f, frames), avg));
            }
            tracks[capeBones[k]] = new BoneTrack { RotationKeys = keys };
        }
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = tracks };
    }

    /// <summary>
    /// The library frames for a whole animation (0.15.2, Kurt: Mega Hair bounced side to side in movement_run 30-60): picked
    /// one frame at a time, the nearest frame jumped between donor animations and frames nearly every frame (Angela's run @15,
    /// @36, @17, a throw …), and the hair swung 50-70° across in three frames. Now the cheapest path through the library over
    /// the whole animation (Viterbi): each frame costs its feature distance, going on to the donor's next frame is free, any
    /// other step costs <see cref="JumpCost"/> × the animation's median nearest distance. MFF_MATCH_JUMP=0 = nearest per frame.
    /// </summary>
    int[] BestPath(float[][] feats, string? skip)
    {
        int n = feats.Length, L = features.Count;
        bool Skipped(int i) => skip != null && (sources[i].StartsWith(skip, StringComparison.OrdinalIgnoreCase) || skip.StartsWith(sources[i], StringComparison.OrdinalIgnoreCase));
        var cost = new float[n][];
        Parallel.For(0, n, f =>
        {
            var c = new float[L]; var q = feats[f];
            for (int i = 0; i < L; i++)
            {
                if (Skipped(i)) { c[i] = float.PositiveInfinity; continue; }
                var x = features[i]; float d = 0;
                for (int k = 0; k < x.Length; k++) { float e = x[k] - q[k]; d += e * e; }
                c[i] = MathF.Sqrt(d);
            }
            cost[f] = c;
        });
        float jump = JumpCost;
        if (jump <= 0) return cost.Select(c => Array.IndexOf(c, c.Min())).ToArray();
        var nearest = cost.Select(c => c.Min()).OrderBy(x => x).ToList();
        float J = jump * MathF.Max(1e-3f, nearest[nearest.Count / 2]);
        var acc = (float[])cost[0].Clone();
        var back = new int[n][];
        for (int f = 1; f < n; f++)
        {
            int bestPrev = 0; for (int i = 1; i < L; i++) if (acc[i] < acc[bestPrev]) bestPrev = i;
            float jumpFrom = acc[bestPrev] + J;
            var next = new float[L]; var bk = new int[L];
            for (int i = 0; i < L; i++)
            {
                // going on: the donor's next frame (same animation, one frame later); else a jump from the best so far
                float go = i > 0 && sources[i - 1] == sources[i] ? acc[i - 1] : float.PositiveInfinity;
                if (go <= jumpFrom) { next[i] = go + cost[f][i]; bk[i] = i - 1; }
                else { next[i] = jumpFrom + cost[f][i]; bk[i] = bestPrev; }
            }
            acc = next; back[f] = bk;
        }
        var path = new int[n];
        int at = 0; for (int i = 1; i < L; i++) if (acc[i] < acc[at]) at = i;
        for (int f = n - 1; f >= 0; f--) { path[f] = at; if (f > 0) at = back[f][at]; }
        return path;
    }

    /// <summary>MFF_MATCH_JUMP: a jump's cost in median nearest distances (0 = nearest per frame, as before 0.15.2).</summary>
    public static float JumpCost => float.TryParse(Environment.GetEnvironmentVariable("MFF_MATCH_JUMP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : DefaultJump;
    public const float DefaultJump = 1f;

    /// <summary>The library frame nearest <paramref name="q"/>; frames of an animation named like <paramref name="skip"/>
    /// (the same name, or one that is it plus a suffix: a rework or variant) are left out (testing on the donor itself).</summary>
    int Nearest(float[] q, string? skip = null)
    {
        int best = 0; float bd = float.MaxValue;
        object gate = new();
        Parallel.For(0, (features.Count + 2047) / 2048, chunk =>
        {
            int b = -1; float d0 = float.MaxValue;
            for (int i = chunk * 2048; i < Math.Min(features.Count, (chunk + 1) * 2048); i++)
            {
                if (skip != null && (sources[i].StartsWith(skip, StringComparison.OrdinalIgnoreCase) || skip.StartsWith(sources[i], StringComparison.OrdinalIgnoreCase))) continue;
                var f = features[i]; float d = 0;
                for (int k = 0; k < f.Length && d < d0; k++) { float e = f[k] - q[k]; d += e * e; }
                if (d < d0) { d0 = d; b = i; }
            }
            lock (gate) if (b >= 0 && d0 < bd) { bd = d0; best = b; }
        });
        return best;
    }
}
