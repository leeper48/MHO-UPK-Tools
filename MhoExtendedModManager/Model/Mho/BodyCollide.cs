using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;

namespace MhoMffImporter;

/// <summary>
/// Keeps borrowed hair / cape out of the body (2026-10-02, Kurt: "one of the main things I'm trying to prevent is her hair
/// intersecting with her body"). Capsules round the torso, upper arms, forearms, thighs and shins, each as thick as the model's
/// own skin on those bones (70th percentile of its distance from the bone); for each borrowed bone, how far its hair or cloth
/// spreads round it (the median distance of the skin it carries). Every frame, after the matched motion, each borrowed strand
/// is walked from its root: when the joint below a bone (or the tip, or the middle of the segment) is inside a capsule (its
/// radius plus that bone's spread), the bone is turned, about its own joint, just enough to bring it back out, and the bones
/// below follow. The result replaces the borrowed bones' tracks.
/// </summary>
sealed class BodyCollide
{
    /// <summary>A capsule along bone A → bone B, cut at <paramref name="EndAt"/> of the way (the chest stops below the neck).</summary>
    public sealed record Capsule(string Name, int A, int B, float Radius, float EndAt = 1)
    {
        public Vector3 End(Vector3 a, Vector3 b) => a + (b - a) * EndAt;
    }
    public List<Capsule> Capsules { get; } = new();
    readonly Dictionary<int, float> spread = new();
    /// <summary>Per borrowed bone, per capsule, per test point (end, ¾, ½, ¼): how far it sits from the capsule's axis at rest.
    /// A point is only pushed when deeper than that (0.14.4, Kurt: idle "not good": Scream's mane rests on her back, so with the
    /// hair's spread most strands counted as inside at rest and were shoved away every frame, the mane swept to the floor).</summary>
    readonly Dictionary<int, float[,]> restClear = new();
    static readonly float[] Fractions = [1f, 0.75f, 0.5f, 0.25f];
    readonly HashSet<int> rig;
    readonly int[] parent;
    readonly List<int>[] children;

    BodyCollide(IReadOnlyList<MeshBone> bones, HashSet<int> rig)
    {
        this.rig = rig;
        parent = bones.Select((b, i) => b.ParentIndex == i ? -1 : b.ParentIndex).ToArray();
        children = Enumerable.Range(0, bones.Count).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < bones.Count; i++) if (parent[i] >= 0) children[parent[i]].Add(i);
    }

    static readonly (string Name, string A, string B, string[] Skin)[] Parts =
    [
        ("torso", "g_pelvis", "g_spine03", ["g_pelvis", "g_spine01", "g_spine02", "g_spine03"]),
        ("chest", "g_spine03", "g_neck", ["g_spine03"]),   // (stops halfway to the neck: a capsule as wide as the shoulders up to the neck caught the hair at the nape)
        ("left upper arm", "g_l_shoulder", "g_l_elbow", ["g_l_shoulder", "g_l_biceptwist"]),
        ("right upper arm", "g_r_shoulder", "g_r_elbow", ["g_r_shoulder", "g_r_biceptwist"]),
        ("left forearm", "g_l_elbow", "g_l_wrist", ["g_l_elbow", "g_l_forarm"]),
        ("right forearm", "g_r_elbow", "g_r_wrist", ["g_r_elbow", "g_r_forarm"]),
        ("left thigh", "g_l_hip", "g_l_knee", ["g_l_hip"]),
        ("right thigh", "g_r_hip", "g_r_knee", ["g_r_hip"]),
        ("left shin", "g_l_knee", "g_l_ankle", ["g_l_knee"]),
        ("right shin", "g_r_knee", "g_r_ankle", ["g_r_knee"]),
    ];

    /// <summary>Capsules and spreads measured on the model at rest (its vertices in model space, weights by bone index).</summary>
    public static BodyCollide? Measure(IReadOnlyList<MeshBone> bones, IReadOnlyList<Vector3> positions, IReadOnlyList<(int Bone, float Weight)[]> weights, ISet<string> borrowed)
    {
        int Find(string n) { for (int i = 0; i < bones.Count; i++) if (bones[i].Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        var rigIdx = Enumerable.Range(0, bones.Count).Where(i => borrowed.Contains(bones[i].Name)).ToHashSet();
        if (rigIdx.Count == 0) return null;
        var c = new BodyCollide(bones, rigIdx);
        var rest = AnimExportCli.Fbx.SkeletonPose.Rest(bones);
        Vector3 P(int b) => rest.BoneToModel[b].Translation;
        var top = new int[positions.Count];
        for (int v = 0; v < positions.Count; v++) top[v] = weights[v].Length == 0 ? -1 : weights[v].MaxBy(x => x.Weight).Bone;
        foreach (var (name, an, bn, skin) in Parts)
        {
            int a = Find(an), b = Find(bn);
            if (a < 0 || b < 0) continue;
            var skinIdx = skin.Select(Find).Where(i => i >= 0).ToHashSet();
            var d = new List<float>();
            for (int v = 0; v < positions.Count; v++)
                if (top[v] >= 0 && skinIdx.Contains(top[v]) && Param(positions[v], P(a), P(b)) is float t && t > 0.05f && t < 0.95f)
                    d.Add(SegDist(positions[v], P(a), P(b)));
            if (d.Count < 8) continue;
            d.Sort();
            c.Capsules.Add(new Capsule(name, a, b, d[(int)(d.Count * 0.7f)], name == "chest" ? 0.5f : 1));
        }
        // each borrowed bone's spread: the median distance of the skin it carries from its segment (joint → child / tip)
        float height = positions.Count > 0 ? positions.Max(p => p.Z) - positions.Min(p => p.Z) : 100;
        foreach (int b in rigIdx)
        {
            var end = c.SegmentEnd(b, P);
            var d = new List<float>();
            for (int v = 0; v < positions.Count; v++) if (top[v] == b) d.Add(SegDist(positions[v], P(b), end));
            if (d.Count == 0) continue;
            d.Sort();
            float pct = float.TryParse(Environment.GetEnvironmentVariable("MFF_SPREAD_PCT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sp) ? sp : 0.5f;
            c.spread[b] = Math.Min(d[Math.Min(d.Count - 1, (int)(d.Count * pct))], 0.15f * height);
        }
        // each test point's clearance from each capsule at rest
        foreach (int b in rigIdx)
        {
            var end = c.SegmentEnd(b, P);
            var table = new float[c.Capsules.Count, Fractions.Length];
            for (int k = 0; k < c.Capsules.Count; k++)
            {
                var cap = c.Capsules[k];
                var ca = P(cap.A); var cb = cap.End(ca, P(cap.B));
                for (int f = 0; f < Fractions.Length; f++) table[k, f] = SegDist(P(b) + (end - P(b)) * Fractions[f], ca, cb);
            }
            c.restClear[b] = table;
        }
        return c;
    }

    /// <summary>Where a bone's segment ends: its (first) rig child's joint, else its tip (the parent segment's direction and length).</summary>
    Vector3 SegmentEnd(int b, Func<int, Vector3> pos)
    {
        var kid = children[b].FirstOrDefault(k => rig.Contains(k), -1);
        if (kid >= 0) return pos(kid);
        int p = parent[b];
        if (p < 0) return pos(b);
        var d = pos(b) - pos(p);
        return pos(b) + (d.LengthSquared() > 1e-8f ? d : Vector3.UnitZ * -1);
    }

    /// <summary>
    /// <paramref name="a"/> with the borrowed bones turned out of the body each frame. Position keys are kept only for
    /// <paramref name="translation"/> bones (null: as they are), as CapeBake.Without; returns how many bone-frames were moved.
    /// </summary>
    public BoneAnimation Apply(BoneAnimation a, IReadOnlyList<MeshBone> bones, out int pushed)
    {
        pushed = 0;
        var (frames, _) = MeshAnimator.Span(a);
        int n = Math.Max(1, (int)MathF.Ceiling(frames));
        var poser = new MeshAnimator(bones, [], [], []);
        var order = rig.OrderBy(i => i).ToList();   // parents first (tree order)
        var keys = order.ToDictionary(b => b, _ => new List<BoneRotationKey>());
        var matchedKeys = order.ToDictionary(b => b, _ => new List<Quaternion>());   // each bone's matched local rotation, per frame
        var G = new Matrix4x4[bones.Count];
        var used = new Dictionary<int, float>();   // turn added to keep out, this bone and its rig parents, this frame
        for (int f = 0; f <= n; f++)
        {
            float fr = Math.Min(f, frames);
            poser.Pose(a, fr);
            for (int i = 0; i < bones.Count; i++) G[i] = poser.BoneMatrix(i);
            used.Clear();
            foreach (int b in order)
            {
                int p = parent[b];
                float budget = MaxStrand - used.GetValueOrDefault(p);
                // this bone's matched local turn under its (possibly already pushed) parent
                Matrix4x4.Invert(poser.BoneMatrix(p), out var invPosedParent);
                var local = poser.BoneMatrix(b) * invPosedParent;
                { var lm = local; lm.Translation = Vector3.Zero; matchedKeys[b].Add(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(lm))); }
                var g = local * G[p];
                float margin = spread.GetValueOrDefault(b);
                var matchedTurn = g;   // the matched pose of this bone: the correction is capped at MaxTurn from it
                for (int pass = 0; pass < 3; pass++)
                {
                    var j = g.Translation;
                    var end = EndOf(b, g, G, bones);
                    var mid = (j + end) / 2;
                    Vector3? want = null;
                    for (int ci = 0; ci < Capsules.Count; ci++)
                    {
                        var cap = Capsules[ci];
                        var ca = G[cap.A].Translation; var cb = cap.End(ca, G[cap.B].Translation);
                        float r = cap.Radius + margin;
                        var clear = restClear.GetValueOrDefault(b);
                        for (int fi = 0; fi < Fractions.Length; fi++)
                        {
                            var pt = j + (end - j) * Fractions[fi]; float w = 1f / Fractions[fi];
                            // never pushed further out than it sits at rest (hair resting on the body stays resting on it)
                            float rr = clear != null ? MathF.Min(r, clear[ci, fi]) : r;
                            var cp = Closest(pt, ca, cb); var off = pt - cp; float dl = off.Length();
                            if (dl >= rr) continue;
                            // out sideways, square to the body part: never out through its rounded ends (a strand near the top
                            // of the torso was pushed straight up past the head)
                            var axis = cb - ca; if (axis.LengthSquared() > 1e-8f) { axis = Vector3.Normalize(axis); off -= axis * Vector3.Dot(off, axis); dl = off.Length(); }
                            var outDir = dl > 1e-4f ? off / dl : Vector3.Normalize(Vector3.Cross(cb - ca, Vector3.UnitZ) + new Vector3(0, 0, 1e-3f));
                            var target = cp + outDir * rr;
                            // the end point that puts this point there (a point nearer the joint moves less for the same turn)
                            want = end + (target - pt) * w;
                        }
                    }
                    if (want is not Vector3 we) break;
                    var from = end - j; var to = we - j;
                    if (from.LengthSquared() < 1e-8f || to.LengthSquared() < 1e-8f) break;
                    var turn = Arc(Vector3.Normalize(from), Vector3.Normalize(to));
                    // capped: at most MaxTurn away from the matched pose in all (a pop or a strand thrown over the head is worse
                    // than a little hair in the body)
                    {
                        var mq = Quaternion.CreateFromRotationMatrix(Rot(matchedTurn)); var nq = Quaternion.CreateFromRotationMatrix(Rot(g * Matrix4x4.CreateFromQuaternion(turn)));
                        float total = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(mq), Quaternion.Normalize(nq))), 0, 1));
                        float cap = MathF.Min(MaxTurn, budget);
                        if (total > cap)
                        {
                            float done = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(mq), Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Rot(g))))), 0, 1));
                            float allowed = MathF.Max(0, cap - done);
                            float ang = 2 * MathF.Acos(Math.Clamp(turn.W, -1, 1));
                            if (allowed <= 1e-4f || ang <= 1e-5f) break;
                            turn = Quaternion.Slerp(Quaternion.Identity, turn, Math.Min(1, allowed / ang));
                        }
                    }
                    var t = g.Translation; g.Translation = Vector3.Zero;
                    g = g * Matrix4x4.CreateFromQuaternion(turn); g.Translation = t;
                    pushed++;
                }
                G[b] = g;
                {
                    float mine = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Rot(matchedTurn))), Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Rot(g))))), 0, 1));
                    used[b] = used.GetValueOrDefault(p) + mine;
                }
                Matrix4x4.Invert(G[p], out var invParent);
                var l = g * invParent; l.Translation = Vector3.Zero;
                var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(l));
                var list = keys[b];
                if (list.Count > 0 && Quaternion.Dot(list[^1].Rotation, q) < 0) q = -q;
                list.Add(new BoneRotationKey(fr, q));
            }
        }
        // Smoothed over time (0.15.1, Kurt: jittery in game, walking): each frame's push is decided on its own, so a strand near
        // the body flicked in and out (jitter 2.74°/frame², 10 % of frames reversing, against the donor's own 0.22 / 0.6 %).
        // The correction each push makes (collided local = matched local × correction) is averaged over ±Smooth frames and put
        // back on the matched motion, so a push eases in and out.
        int sm = SmoothFrames;
        if (sm > 0)
            foreach (var (b, list) in keys)
            {
                var mk = matchedKeys[b];
                var corr = list.Select((k, f) => { var d = Quaternion.Normalize(Quaternion.Inverse(mk[f]) * k.Rotation); return d.W < 0 ? -d : d; }).ToList();
                var smoothed = new List<BoneRotationKey>();
                for (int f = 0; f < list.Count; f++)
                {
                    var sum = Vector4.Zero;
                    for (int w = -sm; w <= sm; w++)
                    {
                        var d = corr[Math.Clamp(f + w, 0, list.Count - 1)];
                        sum += new Vector4(d.X, d.Y, d.Z, d.W) * (sm + 1 - Math.Abs(w));
                    }
                    var q = Quaternion.Normalize(mk[f] * Quaternion.Normalize(new Quaternion(sum.X, sum.Y, sum.Z, sum.W)));
                    if (smoothed.Count > 0 && Quaternion.Dot(smoothed[^1].Rotation, q) < 0) q = -q;
                    smoothed.Add(new BoneRotationKey(list[f].TimeFrame, q));
                }
                keys[b] = smoothed;
            }
        var tracks = new Dictionary<string, BoneTrack>(a.Tracks, StringComparer.OrdinalIgnoreCase);
        foreach (var (b, list) in keys) tracks[bones[b].Name] = new BoneTrack { RotationKeys = list };
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = tracks };
    }

    /// <summary>The end of bone <paramref name="b"/>'s segment in the pose being built (its rig child's joint from the rest
    /// offset, else the tip).</summary>
    Vector3 EndOf(int b, Matrix4x4 g, Matrix4x4[] G, IReadOnlyList<MeshBone> bones)
    {
        var kid = children[b].FirstOrDefault(k => rig.Contains(k), -1);
        if (kid >= 0) return Vector3.Transform(bones[kid].Position, g);
        int p = parent[b];
        var d = g.Translation - G[p].Translation;
        return g.Translation + (d.LengthSquared() > 1e-8f ? d : Vector3.TransformNormal(-Vector3.UnitZ, g));
    }

    /// <summary>How many hair / cloth vertices (carried mostly by the borrowed bones) are inside the body capsules (their own
    /// radius, no spread), summed over the frames of <paramref name="a"/>, and how many were checked.</summary>
    public int RestInside { get; private set; }
    /// <summary>When set: penetrations counted by "capsule ← the vertex's main bone" (diagnostics).</summary>
    public Dictionary<string, long>? Breakdown;

    public (long Inside, long Checked) Penetration(BoneAnimation? a, IReadOnlyList<MeshBone> bones, Vector3[] positions, IReadOnlyList<VertexInfluence> infl)
    {
        var anim = new MeshAnimator(bones, positions, new Vector3[positions.Length], infl);
        var carried = Enumerable.Range(0, positions.Length).Where(v => infl[v].Bones.Count > 0 && rig.Contains(infl[v].Bones[Enumerable.Range(0, infl[v].Bones.Count).MaxBy(k => infl[v].Weights[k])])).ToList();
        float frames = a == null ? 0 : MeshAnimator.Span(a).Frames;
        long inside = 0, all = 0;
        // inside at rest (hair lying on the back counts as touching, not as going through): left out
        var rest = new MeshAnimator(bones, positions, new Vector3[positions.Length], infl);
        rest.Pose(null, 0);
        var restInside = new HashSet<int>(carried.Where(v => Capsules.Any(cap => SegDist(rest.Positions[v], rest.BonePosition(cap.A), cap.End(rest.BonePosition(cap.A), rest.BonePosition(cap.B))) < cap.Radius)));
        RestInside = restInside.Count;
        carried = carried.Where(v => !restInside.Contains(v)).ToList();
        for (float f = 0; f <= frames; f += Math.Max(1, frames / 30))
        {
            anim.Pose(a, f);
            foreach (int v in carried)
            {
                all++;
                foreach (var cap in Capsules)
                    if (SegDist(anim.Positions[v], anim.BonePosition(cap.A), cap.End(anim.BonePosition(cap.A), anim.BonePosition(cap.B))) < cap.Radius)
                    {
                        inside++;
                        if (Breakdown != null)
                        {
                            int top = infl[v].Bones[Enumerable.Range(0, infl[v].Bones.Count).MaxBy(k => infl[v].Weights[k])];
                            string key = cap.Name + " ← " + bones[top].Name;
                            Breakdown[key] = Breakdown.GetValueOrDefault(key) + 1;
                        }
                        break;
                    }
            }
            if (a == null) break;
        }
        return (inside, all);
    }

    /// <summary>The most a bone is turned from its matched pose to keep out of the body (45°).</summary>
    /// <summary>Frames each side the pushes are averaged over (MFF_COLLIDE_SMOOTH; 0 = per frame, as before 0.15.1).</summary>
    static int SmoothFrames => int.TryParse(Environment.GetEnvironmentVariable("MFF_COLLIDE_SMOOTH"), out int v) ? Math.Max(0, v) : 6;
    static float MaxTurn => Deg("MFF_COLLIDE_BONE", 15) * MathF.PI / 180;
    /// <summary>The most turned in all along a strand (this bone's push plus its parents'): 30° (0.14.4: at 45° per bone, the
    /// pushes added up down a six-bone strand and Carnage's crouched idle swept Scream's mane to the floor).</summary>
    static float MaxStrand => Deg("MFF_COLLIDE_STRAND", 30) * MathF.PI / 180;
    static float Deg(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
    static Matrix4x4 Rot(Matrix4x4 m) { m.Translation = Vector3.Zero; return m; }

    static float Param(Vector3 p, Vector3 a, Vector3 b) { var ab = b - a; float l = ab.LengthSquared(); return l < 1e-8f ? 0 : Vector3.Dot(p - a, ab) / l; }
    static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b) => a + (b - a) * Math.Clamp(Param(p, a, b), 0, 1);
    static float SegDist(Vector3 p, Vector3 a, Vector3 b) => (p - Closest(p, a, b)).Length();

    static Quaternion Arc(Vector3 u, Vector3 v)
    {
        float d = Vector3.Dot(u, v);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f) { var ax = Vector3.Cross(u, Vector3.UnitX); if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(u, Vector3.UnitY); return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI); }
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(u, v), 1 + d));
    }
}
