using System.Numerics;
using System.Text.RegularExpressions;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;

namespace MhoMffImporter;

/// <summary>
/// Cape motion baked from a body's own movement (2026-10-02, Kurt: a cape on a hero whose animations have none). MHO capes
/// are keyframed (every cape bone of Thor, Vision, Doctor Strange, Moon Knight, Storm has a moving track in nearly every
/// animation; none is simulated: --cape-probe), so a hero without one needs tracks made for each of its animations. Here
/// each cape strip is a strand: its first joint rides the body (the cape root's rest pose on its parent), its lower joints
/// and a tip particle hang under gravity, are pulled toward where the rigid cape would be (stiffness), damped, kept at their
/// segment lengths, pushed out of a capsule round the torso, and, for in-place movement animations (run, sprint, fly),
/// blown back by a headwind standing in for the speed the game adds. The strand's shape each frame becomes local rotation
/// keys for the strip bones (the smallest turn from the rigid cape's direction), so the result plays like any other track.
/// Preview prototype: nothing is written to a package yet.
/// </summary>
static class CapeBake
{
    /// <summary>The knobs (tuned against Thor's real cape: --cape-bake-test).</summary>
    public sealed record Params(float Stiffness = 60f, float Damping = 0.08f, float Gravity = 100f, float Wind = 300f, float Drag = 2.5f, int Substeps = 4)
    {
        public static Params FromEnvironment()
        {
            static float F(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
            var p = new Params();
            return p with { Stiffness = F("MFF_CAPE_K", p.Stiffness), Damping = F("MFF_CAPE_DAMP", p.Damping), Gravity = F("MFF_CAPE_G", p.Gravity), Wind = F("MFF_CAPE_WIND", p.Wind), Drag = F("MFF_CAPE_DRAG", p.Drag) };
        }
    }

    /// <summary>A cape in a skeleton: its root bone (kinematic) and its strips (each a chain of bone indices, root's child first).</summary>
    public sealed record Cape(int Root, List<int[]> Strips)
    {
        public IEnumerable<int> Bones => Strips.SelectMany(s => s).Prepend(Root);
    }

    static readonly Regex CapeName = new("cape", RegexOptions.IgnoreCase);

    /// <summary>The cape in a skeleton: the first bone named like a cape whose parent isn't one, and the single-child chains
    /// under it (Thor / Doctor Strange: g_cape1 with g_l_cape1-3, g_r_cape1-3; Vision: with g_l/r_cape0 first).</summary>
    public static Cape? Find(IReadOnlyList<MeshBone> bones)
    {
        int Parent(int i) => bones[i].ParentIndex == i ? -1 : bones[i].ParentIndex;
        var children = new List<int>[bones.Count];
        for (int i = 0; i < bones.Count; i++) children[i] = new();
        for (int i = 0; i < bones.Count; i++) if (Parent(i) >= 0) children[Parent(i)].Add(i);
        for (int i = 0; i < bones.Count; i++)
        {
            if (!CapeName.IsMatch(bones[i].Name) || (Parent(i) >= 0 && CapeName.IsMatch(bones[Parent(i)].Name))) continue;
            var strips = new List<int[]>();
            foreach (int c in children[i].Where(c => CapeName.IsMatch(bones[c].Name)))
            {
                var s = new List<int> { c };
                while (children[s[^1]].Count(x => CapeName.IsMatch(bones[x].Name)) == 1) s.Add(children[s[^1]].First(x => CapeName.IsMatch(bones[x].Name)));
                if (s.Count >= 2) strips.Add([.. s]);
            }
            if (strips.Count > 0) return new Cape(i, strips);
        }
        return null;
    }

    /// <summary>In-place movement animations get the headwind (the game moves the character; the animation runs on the spot).</summary>
    public static bool Moving(string animation) =>
        Regex.IsMatch(animation, @"(^|_)(run|sprint|walk|jog|fly|flying|dash|charge|movement)(_|$)", RegexOptions.IgnoreCase) && !animation.Contains("idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="a"/> with the cape's tracks made by the strand simulation (its own cape tracks, if any, replaced).
    /// Position keys are kept only for <paramref name="translation"/> bones (null = all), so the copy poses as the original
    /// does in MeshAnimator (a new animation isn't in ModAnimations' rotation-only registry).
    /// </summary>
    public static BoneAnimation Bake(BoneAnimation a, IReadOnlyList<MeshBone> bones, Cape cape, IReadOnlySet<string>? translation, Params p)
    {
        var (frames, seconds) = MeshAnimator.Span(a);
        int n = Math.Max(1, (int)MathF.Ceiling(frames));
        float fps = seconds > 0 ? frames / seconds : 30f;
        var capeSet = cape.Bones.Select(i => bones[i].Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // the body without any cape tracks: the cape rigid in its rest shape (the targets)
        var body = Without(a, capeSet, translation);
        var poser = new MeshAnimator(bones, [], [], []);
        int count = bones.Count;
        // per frame: every bone's model matrix with the cape rigid
        var rigid = new Matrix4x4[n + 1][];
        for (int f = 0; f <= n; f++)
        {
            poser.Pose(body, Math.Min(f, frames));
            rigid[f] = new Matrix4x4[count];
            for (int b = 0; b < count; b++) rigid[f][b] = poser.BoneMatrix(b);
        }
        var rest = AnimExportCli.Fbx.SkeletonPose.Rest(bones);
        var tracks = new Dictionary<string, BoneTrack>(body.Tracks, StringComparer.OrdinalIgnoreCase);
        // the torso capsule (pelvis → upper spine), its radius below the cape's rest distance from it
        int lo = Find(bones, "g_pelvis", "g_spine01"), hi = Find(bones, "g_spine03", "g_spine02", "g_neck");
        bool loop = Regex.IsMatch(a.Name, @"(^|_)(idle|loop|run|sprint|walk|fly|flying|fidget)", RegexOptions.IgnoreCase) && !a.Name.EndsWith("_start", StringComparison.OrdinalIgnoreCase) && !a.Name.EndsWith("_end", StringComparison.OrdinalIgnoreCase);
        bool moving = Moving(a.Name);
        var keys = new Dictionary<int, List<BoneRotationKey>>();
        foreach (var strip in cape.Strips)
        {
            int m = strip.Length;                       // joints; particles = joints 1..m-1 and the tip
            var len = new float[m];                     // segment j: joint j → joint j+1 (the last: to the tip)
            Vector3 J(Matrix4x4[] pose, int j) => pose[strip[j]].Translation;
            for (int j = 0; j < m - 1; j++) len[j] = (rest.BoneToModel[strip[j + 1]].Translation - rest.BoneToModel[strip[j]].Translation).Length();
            len[m - 1] = len[m - 2];
            Vector3 Tip(Matrix4x4[] pose)
            {
                var d = J(pose, m - 1) - J(pose, m - 2);
                return J(pose, m - 1) + (d.LengthSquared() > 1e-8f ? Vector3.Normalize(d) : -Vector3.UnitZ) * len[m - 1];
            }
            // targets per frame: the rigid cape's joints and tip
            Vector3[] Target(int f) { var t = new Vector3[m + 1]; for (int j = 0; j < m; j++) t[j] = J(rigid[f], j); t[m] = Tip(rigid[f]); return t; }
            float radius = float.MaxValue;
            if (lo >= 0 && hi >= 0)
            {
                var r0 = Target(0);
                for (int j = 1; j <= m; j++) radius = MathF.Min(radius, SegDist(r0[j], rigid[0][lo].Translation, rigid[0][hi].Translation));
                radius *= 0.85f;
            }
            var x = Target(0); var prev = (Vector3[])x.Clone();
            var outDir = new Vector3[n + 1][];
            float dt = 1f / fps / p.Substeps;
            int passes = loop ? 2 : 1;
            for (int pass = 0; pass < passes; pass++)
                for (int f = 0; f <= n; f++)
                {
                    var tgt = Target(f);
                    var tgtNext = Target(Math.Min(f + 1, n));
                    for (int s = 0; s < p.Substeps; s++)
                    {
                        float u = (s + 1f) / p.Substeps;
                        var anchor = Vector3.Lerp(tgt[0], tgtNext[0], u);
                        var wind = Vector3.Zero;
                        if (moving) { var fwd = Vector3.TransformNormal(Vector3.UnitX, rigid[f][lo >= 0 ? lo : 0]); fwd.Z = 0; if (fwd.LengthSquared() > 1e-6f) wind = -Vector3.Normalize(fwd) * p.Wind; }
                        for (int j = 1; j <= m; j++)
                        {
                            var goal = Vector3.Lerp(tgt[j], tgtNext[j], u);
                            var v = (x[j] - prev[j]) / dt;
                            var acc = new Vector3(0, 0, -p.Gravity) + p.Stiffness * (goal - x[j]) + p.Drag * (wind - v);
                            var nx = x[j] + (x[j] - prev[j]) * (1 - p.Damping) + acc * dt * dt;
                            prev[j] = x[j]; x[j] = nx;
                        }
                        x[0] = anchor; prev[0] = anchor;
                        for (int it = 0; it < 4; it++)
                            for (int j = 1; j <= m; j++)
                            {
                                var d = x[j] - x[j - 1]; float l = d.Length();
                                if (l > 1e-5f) x[j] = x[j - 1] + d * (len[j - 1] / l);
                                if (radius < float.MaxValue && lo >= 0 && hi >= 0)
                                {
                                    var a0 = Vector3.Lerp(rigid[f][lo].Translation, rigid[Math.Min(f + 1, n)][lo].Translation, u);
                                    var a1 = Vector3.Lerp(rigid[f][hi].Translation, rigid[Math.Min(f + 1, n)][hi].Translation, u);
                                    var c = Closest(x[j], a0, a1); var off = x[j] - c; float dl = off.Length();
                                    if (dl < radius && dl > 1e-5f) x[j] = c + off * (radius / dl);
                                }
                            }
                    }
                    if (pass == passes - 1) outDir[f] = (Vector3[])x.Clone();
                }
            // strand → local rotations: each strip bone turned (smallest turn) from the rigid cape's direction to the strand's
            for (int f = 0; f <= n; f++)
            {
                var global = new Matrix4x4[m];
                for (int j = 0; j < m; j++)
                {
                    int b = strip[j], parent = bones[b].ParentIndex;
                    var parentGlobal = j == 0 ? rigid[f][parent] : global[j - 1];
                    // this bone's frame if it kept its rest local turn under the (already turned) parent
                    var restLocal = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(bones[b].Orientation)) * Matrix4x4.CreateTranslation(bones[b].Position);
                    var g = restLocal * parentGlobal;
                    var from = j < m - 1 ? (Matrix4x4.CreateTranslation(bones[strip[j + 1]].Position) * g).Translation - g.Translation
                                         : Tip(rigid[f]) - J(rigid[f], m - 1);
                    var to = outDir[f][j + 1] - outDir[f][j];
                    if (from.LengthSquared() > 1e-8f && to.LengthSquared() > 1e-8f)
                    {
                        var turn = Arc(Vector3.Normalize(from), Vector3.Normalize(to));
                        var t = g.Translation;
                        g.Translation = Vector3.Zero;
                        g = g * Matrix4x4.CreateFromQuaternion(turn);
                        g.Translation = t;
                    }
                    global[j] = g;
                    Matrix4x4.Invert(parentGlobal, out var inv);
                    var local = g * inv;
                    local.Translation = Vector3.Zero;
                    var q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
                    if (!keys.TryGetValue(b, out var list)) keys[b] = list = new();
                    if (list.Count > 0 && Quaternion.Dot(list[^1].Rotation, q) < 0) q = -q;
                    list.Add(new BoneRotationKey(Math.Min(f, frames), q));
                }
            }
        }
        foreach (var (b, list) in keys) tracks[bones[b].Name] = new BoneTrack { RotationKeys = list };
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = tracks };
    }

    /// <summary>A copy without the named bones' tracks; position keys only for translation bones (null: all kept).</summary>
    public static BoneAnimation Without(BoneAnimation a, IReadOnlySet<string> drop, IReadOnlySet<string>? translation)
    {
        var t = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, track) in a.Tracks)
        {
            if (drop.Contains(name)) continue;
            t[name] = translation == null || translation.Contains(name) ? track : new BoneTrack { RotationKeys = track.RotationKeys, PositionKeys = RootOnly(name, track, a) };
        }
        return new BoneAnimation { Name = a.Name, DurationSeconds = a.DurationSeconds, Tracks = t };

        // the root's position is applied whatever the set says (MeshAnimator: i == 0); keep it
        static IReadOnlyList<BonePositionKey> RootOnly(string name, BoneTrack track, BoneAnimation a) =>
            name.Equals("root", StringComparison.OrdinalIgnoreCase) ? track.PositionKeys : [];
    }

    static int Find(IReadOnlyList<MeshBone> bones, params string[] names)
    {
        foreach (var n in names) for (int i = 0; i < bones.Count; i++) if (bones[i].Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float l = ab.LengthSquared();
        float t = l < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / l, 0, 1);
        return a + ab * t;
    }

    static float SegDist(Vector3 p, Vector3 a, Vector3 b) => (p - Closest(p, a, b)).Length();

    /// <summary>The smallest rotation turning unit vector <paramref name="u"/> onto <paramref name="v"/>.</summary>
    static Quaternion Arc(Vector3 u, Vector3 v)
    {
        float d = Vector3.Dot(u, v);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            var axis = Vector3.Cross(u, Vector3.UnitX); if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(u, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(u, v);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }
}
