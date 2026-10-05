using System.Numerics;
using System.Text.RegularExpressions;
using Assimp;
using Assimp.Configs;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Other skeleton families read as an MFF model (Kurt, 2026-10-03: a Marvel Strike Force rip, Captain Carter, has a Mixamo
/// armature). Their bones are renamed to the Biped names the MFF retarget knows (<see cref="Retarget.DefaultMap"/>), so the
/// same chain matching, proportions, scale fitting and Bone Map apply. Bones a profile doesn't name (lips, eyelids, the
/// "_End" tips) keep their own names and give their weights to the nearest paired parent, as extra MFF bones do.
/// A skeleton with names no profile knows is guessed from its shape (<see cref="Guess"/>), and the Bone Map marks those pairs
/// for checking.
/// </summary>
static class SkeletonProfile
{
    /// <summary>What was found: the family ("Mixamo", or "Guessed" for <see cref="Guess"/>), original name → Biped name, and
    /// notes for the log (the guess lists what it took for each limb, and what it didn't find).</summary>
    public sealed record Result(string Family, Dictionary<string, string> Rename, List<string> Notes)
    {
        public bool Guessed => Family == "Guessed";
    }

    /// <summary>Mixamo (and rigs exported with its names: "mixamorig:Hips", "mixamorig1:Hips", "mixamorig_Hips" or bare "Hips").
    /// The names are fixed, so the profile is exact.</summary>
    static IEnumerable<(string Name, string Biped)> Mixamo()
    {
        yield return ("Hips", "Bip001 Pelvis");
        yield return ("Spine", "Bip001 Spine");
        yield return ("Spine1", "Bip001 Spine1");
        yield return ("Spine2", "Bip001 Spine2");
        yield return ("Neck", "Bip001 Neck");
        yield return ("Head", "Bip001 Head");
        foreach (var (side, s) in new[] { ("Left", "L"), ("Right", "R") })
        {
            yield return ($"{side}UpLeg", $"Bip001 {s} Thigh");
            yield return ($"{side}Leg", $"Bip001 {s} Calf");
            yield return ($"{side}Foot", $"Bip001 {s} Foot");
            yield return ($"{side}ToeBase", $"Bip001 {s} Toe0");
            yield return ($"{side}Shoulder", $"Bip001 {s} Clavicle");
            yield return ($"{side}Arm", $"Bip001 {s} UpperArm");
            yield return ($"{side}ForeArm", $"Bip001 {s} Forearm");
            yield return ($"{side}ForeArmRoll", $"Bip001 {s} ForeTwist");   // not stock Mixamo; Strike Force rigs add it
            yield return ($"{side}Hand", $"Bip001 {s} Hand");
            string[] fingers = ["Thumb", "Index", "Middle", "Ring", "Pinky"];
            for (int f = 0; f < 5; f++)
            {
                yield return ($"{side}Hand{fingers[f]}1", $"Bip001 {s} Finger{f}");
                yield return ($"{side}Hand{fingers[f]}2", $"Bip001 {s} Finger{f}1");
                yield return ($"{side}Hand{fingers[f]}3", $"Bip001 {s} Finger{f}2");
            }
        }
    }

    static readonly Regex MixamoPrefix = new(@"^mixamorig\d*[:_]", RegexOptions.IgnoreCase);
    static readonly Dictionary<string, string> MixamoTable = Mixamo().ToDictionary(x => x.Name, x => x.Biped, StringComparer.OrdinalIgnoreCase);

    /// <summary>The bone's Biped name by the Mixamo profile (prefix and case ignored), or null when it isn't one.</summary>
    public static string? MixamoName(string name) => MixamoTable.TryGetValue(MixamoPrefix.Replace(name, ""), out var biped) ? biped : null;

    static readonly Regex MhoCore = new(@"^g_(pelvis|spine01|head|[lr]_hip|[lr]_knee)$", RegexOptions.IgnoreCase);

    /// <summary>Opens a scene the way <see cref="MffModel.Load"/> does (no pivot helper nodes).</summary>
    public static Scene? Open(string file, PostProcessSteps steps = PostProcessSteps.None)
    {
        using var ctx = new AssimpContext();
        ctx.SetConfig(new FBXPreservePivotsConfig(false));
        return ctx.ImportFile(file, steps);
    }

    /// <summary>
    /// What a scene's skeleton is: null for an MFF (Biped) or MHO skeleton (they have their own routes) and for a scene
    /// without one; else the Mixamo profile, else a guess from the shape. <paramref name="whyNot"/>: why an unknown skeleton
    /// couldn't be guessed. <paramref name="forceGuess"/> (tests): guess even when the names are known.
    /// </summary>
    public static Result? Find(Scene scene, out string? whyNot, bool forceGuess = false)
    {
        whyNot = null;
        var names = Names(scene).ToList();
        if (!forceGuess)
        {
            if (names.Any(n => n.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase))) return null;
            if (names.Count(n => MhoCore.IsMatch(n)) >= 3) return null;
            var rename = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in names.Distinct())
                if (MixamoName(name) is string biped && !rename.ContainsValue(biped)) rename[name] = biped;
            var hit = rename.Values.ToHashSet();
            if (hit.Contains("Bip001 Pelvis") && hit.Contains("Bip001 L Thigh") && hit.Contains("Bip001 R Thigh") && hit.Contains("Bip001 Head"))
                return new Result("Mixamo", rename, [$"Mixamo skeleton: {rename.Count} bones paired by name with the MFF (Biped) ones; the rest follow their parents"]);
        }
        if (!scene.Meshes.Any(m => m.BoneCount > 0)) { whyNot = "the FBX has no armature (no mesh is skinned to bones)"; return null; }
        return Guess(scene, out whyNot);
    }

    /// <summary>The family of an FBX file's skeleton (see <see cref="Find"/>).</summary>
    public static string? DetectFile(string file, out string? whyNot)
    {
        whyNot = null;
        var scene = Open(file);
        return scene == null ? null : Find(scene, out whyNot)?.Family;
    }

    /// <summary>Every bone-like name in the scene: its nodes and the bones its meshes are skinned to.</summary>
    public static IEnumerable<string> Names(Scene scene)
    {
        var stack = new Stack<Node>(); stack.Push(scene.RootNode);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n.Name;
            foreach (var c in n.Children) stack.Push(c);
        }
        foreach (var m in scene.Meshes) foreach (var b in m.Bones) yield return b.Name;
    }

    /// <summary>Renames a known or guessed skeleton's bones (nodes and mesh bones alike) to their Biped names; null when the
    /// scene isn't one (then nothing changes). A Biped name that's already taken isn't given again.</summary>
    public static Result? Apply(Scene scene, List<string>? notes = null)
    {
        var found = Find(scene, out _);
        if (found == null) return null;
        var taken = new HashSet<string>(Names(scene), StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in found.Rename.ToList()) if (taken.Contains(to) && !from.Equals(to, StringComparison.OrdinalIgnoreCase)) found.Rename.Remove(from);
        var stack = new Stack<Node>(); stack.Push(scene.RootNode);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (found.Rename.TryGetValue(n.Name, out var to)) n.Name = to;
            foreach (var c in n.Children) stack.Push(c);
        }
        foreach (var m in scene.Meshes) foreach (var b in m.Bones) if (found.Rename.TryGetValue(b.Name, out var to)) b.Name = to;
        notes?.AddRange(found.Notes);
        return found;
    }

    // ------------------------------------------------------------------------------------------------- guessing from the shape

    sealed class J
    {
        public required Node Node;
        public J? Parent;
        public List<J> Children { get; } = [];
        public Vector3 Pos;
        public bool Deforms;
        /// <summary>The vertex weight this joint carries (all meshes): the head carries the face, hair bones far less.</summary>
        public float Weight;
        public int Level;
        /// <summary>Joints in the longest chain from here down (this one counts).</summary>
        public int Depth;
        public int Count;
        public string Name => Node.Name;
    }

    /// <summary>
    /// Pairs an unknown skeleton's joints with Biped ones by its shape, not its names:
    /// <list type="bullet">
    /// <item>the hips: the highest joint where three chains of 3+ joints meet; the chain with the most joints is the spine, the
    /// two most alike of the others (length, direction) the legs; up = from the feet to the hips;</item>
    /// <item>each leg: the thigh, the knee (half way down), the ankle (the last joint more than 4 % of the leg above its lowest
    /// point) and the toe after it; front = the way the toes point from the ankles (else the knees' bend); left = up × front;</item>
    /// <item>the spine: its chain upward; the arms are the two chains off it reaching farthest to the left and to the right;
    /// spine bones up to where the arms leave, the neck and head above (end bones without weights skipped);</item>
    /// <item>each arm: the hand is the first joint with three finger chains (else the arm's last weighted joint); a short first
    /// segment is the clavicle; the elbow is half way from the shoulder to the hand; the thumb is the finger rooted nearest the
    /// wrist, the others are index to pinky from front to back.</item>
    /// </list>
    /// Joints at the same place as their parent (helpers, offsets) are skipped.
    /// </summary>
    public static Result? Guess(Scene scene, out string? whyNot)
    {
        whyNot = null;
        var global = new Dictionary<Node, Matrix4x4>();
        void Walk(Node n, Matrix4x4 parent)
        {
            var g = MffModel.ToNumerics(n.Transform) * parent;
            global[n] = g;
            foreach (var c in n.Children) Walk(c, g);
        }
        Walk(scene.RootNode, Matrix4x4.Identity);
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var n in global.Keys) byName.TryAdd(n.Name, n);
        var deform = new HashSet<string>(scene.Meshes.SelectMany(m => m.Bones).Where(b => b.VertexWeightCount > 0).Select(b => b.Name), StringComparer.Ordinal);
        var meshNodes = new HashSet<Node>(global.Keys.Where(n => n.MeshCount > 0));
        var skel = new HashSet<Node>();
        foreach (var name in scene.Meshes.SelectMany(m => m.Bones).Select(b => b.Name))
            for (var n = byName.GetValueOrDefault(name); n != null && n != scene.RootNode && !meshNodes.Contains(n); n = n.Parent)
                skel.Add(n);
        var joints = new Dictionary<Node, J>();
        var weight = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var b in scene.Meshes.SelectMany(m => m.Bones)) weight[b.Name] = weight.GetValueOrDefault(b.Name) + b.VertexWeights.Sum(w => w.Weight);
        foreach (var n in skel) joints[n] = new J { Node = n, Pos = global[n].Translation, Deforms = deform.Contains(n.Name), Weight = weight.GetValueOrDefault(n.Name) };
        foreach (var j in joints.Values)
        {
            for (var p = j.Node.Parent; p != null; p = p.Parent)
                if (joints.TryGetValue(p, out var pj)) { j.Parent = pj; pj.Children.Add(j); break; }
        }
        void Measure(J j, int level)
        {
            j.Level = level;
            foreach (var c in j.Children) Measure(c, level + 1);
            j.Depth = 1 + (j.Children.Count > 0 ? j.Children.Max(c => c.Depth) : 0);
            j.Count = 1 + j.Children.Sum(c => c.Count);
        }
        foreach (var root in joints.Values.Where(j => j.Parent == null)) Measure(root, 0);

        // the hips: of the joints where three chains of 3+ joints meet, the deepest whose chains look like a body (two alike
        // chains going down, a spine reaching at least half a leg above); a model's root with skirt or cape chains beside the
        // skeleton is passed over (Doctor Strange S02), and so is a chest, whose biggest chain is an arm, not a spine
        IEnumerable<J> Under(J j) { yield return j; foreach (var c in j.Children) foreach (var d in Under(c)) yield return d; }
        Vector3 Tip(J b, Vector3 from) => Under(b).MaxBy(x => Vector3.DistanceSquared(x.Pos, from))!.Pos;
        (J Hips, J Spine, J LegA, J LegB, float Score, bool Body, float Legs)? Try(J at)
        {
            var bs = at.Children.Where(c => c.Depth >= 3).ToList();
            var sp = bs.MaxBy(b => b.Count)!;
            var rest = bs.Where(b => b != sp).ToList();
            (J, J)? pair = null; float sc = float.MinValue;
            // legs are alike, point the same way and reach farthest (a skirt's or coat's chains are alike too, but shorter)
            float rankBest = float.MinValue;
            float longest = rest.Count == 0 ? 1 : rest.Max(b => (Tip(b, at.Pos) - at.Pos).Length());
            for (int a = 0; a < rest.Count; a++)
                for (int b = a + 1; b < rest.Count; b++)
                {
                    var da = Tip(rest[a], at.Pos) - at.Pos; var db = Tip(rest[b], at.Pos) - at.Pos;
                    float la = da.Length(), lb = db.Length();
                    if (la < 1e-6f || lb < 1e-6f) continue;
                    float score = MathF.Min(la, lb) / MathF.Max(la, lb) * Vector3.Dot(da / la, db / lb);
                    float rank = score * MathF.Min(la, lb) / MathF.Max(longest, 1e-6f);
                    if (pair == null || rank > rankBest) { sc = score; rankBest = rank; pair = (rest[a], rest[b]); }
                }
            if (pair is not { } p) return null;
            var mid = (Tip(p.Item1, at.Pos) + Tip(p.Item2, at.Pos)) / 2;
            float len = (at.Pos - mid).Length();
            if (len < 1e-6f) return null;
            var u = (at.Pos - mid) / len;
            float reach = Under(sp).Max(x => Vector3.Dot(x.Pos - at.Pos, u));
            return (at, sp, p.Item1, p.Item2, sc, sc >= 0.6f && reach >= 0.5f * len, len);
        }
        // (of those, the one with the longest legs: hair hanging from the head looks like legs too, but shorter)
        var tried = joints.Values.Where(j => j.Children.Count(c => c.Depth >= 3) >= 3).Select(Try).OfType<(J Hips, J Spine, J LegA, J LegB, float Score, bool Body, float Legs)>().ToList();
        var pick = tried.Where(t => t.Body).OrderByDescending(t => t.Legs).ThenByDescending(t => t.Hips.Level).Select(t => ((J, J, J, J, float, bool, float)?)t).FirstOrDefault()
            ?? tried.Where(t => t.Score >= 0.3f).OrderBy(t => t.Hips.Level).Select(t => ((J, J, J, J, float, bool, float)?)t).FirstOrDefault();
        if (tried.Count == 0) { whyNot = "no joint where three chains (spine and two legs) meet: the skeleton couldn't be guessed"; return null; }
        if (pick is not { } chosen) { whyNot = "no two chains below the hips that look like legs: the skeleton couldn't be guessed"; return null; }
        var (hips, spineRoot, legA, legB, _, _, _) = chosen;
        var legMid = (Tip(legA, hips.Pos) + Tip(legB, hips.Pos)) / 2;
        var up = Vector3.Normalize(hips.Pos - legMid);
        float H(Vector3 p) => Vector3.Dot(p, up);
        float legLen = (hips.Pos - legMid).Length();
        float near = 0.01f * legLen;

        var notes = new List<string>();
        var roles = new Dictionary<J, string>();
        var missing = new List<string>();

        // a chain down a branch, skipping joints at their parent's place
        List<J> Chain(J start, Func<J, J?> next)
        {
            var list = new List<J>();
            for (J? j = start; j != null; j = next(j))
                if (list.Count == 0 || Vector3.Distance(list[^1].Pos, j.Pos) > near) list.Add(j);
            return list;
        }
        // the next joint down a limb: the child whose joints reach farthest from here (a twist chain is deep but stays on
        // the forearm; Biped's ForeTwist … ForeTwist9 beside the hand)
        J? Deepest(J j) => j.Children.OrderByDescending(c => Under(c).Max(x => Vector3.DistanceSquared(x.Pos, j.Pos))).FirstOrDefault();

        // legs
        var legs = new List<(List<J> Chain, int Knee, int Foot, int Toe)>();
        foreach (var root in new[] { legA, legB })
        {
            var c = Chain(root, Deepest);
            float ground = c.Min(j => H(j.Pos));
            float len = MathF.Max(H(c[0].Pos) - ground, near);
            // the ankle: where the leg stops going straight down (the segment after it runs forward to the toes), else the
            // last joint more than 4 % of the leg above its lowest point
            int foot = -1;
            for (int i = 2; i + 1 < c.Count && foot < 0; i++)
            {
                var seg = c[i + 1].Pos - c[i].Pos;
                if (seg.Length() > near && MathF.Abs(Vector3.Dot(Vector3.Normalize(seg), up)) < 0.8f && H(c[i].Pos) - ground > 0.04f * len) foot = i;
            }
            if (foot < 0) for (int i = c.Count - 1; i >= 2; i--) if (H(c[i].Pos) - ground > 0.04f * len) { foot = i; break; }
            if (Environment.GetEnvironmentVariable("MFF_GUESS_DEBUG") == "1")
                Console.WriteLine($"  leg {root.Name}: " + string.Join(" > ", c.Select(j => $"{j.Name} {(H(j.Pos) - ground) / len:0.00}")) + $"; foot {foot}");
            if (foot < 2) { legs.Add((c, -1, -1, -1)); continue; }
            int toe = foot + 1 < c.Count && Horizontal(c[foot + 1].Pos - c[foot].Pos, up).Length() > 0.04f * len ? foot + 1 : -1;
            float mid = (H(c[0].Pos) + H(c[foot].Pos)) / 2;
            int knee = Enumerable.Range(1, foot - 1).MinBy(i => MathF.Abs(H(c[i].Pos) - mid));
            legs.Add((c, knee, foot, toe));
        }
        if (legs.Any(l => l.Foot < 0)) { whyNot = "a leg without a knee and an ankle (fewer than three joints down to the floor): the skeleton couldn't be guessed"; return null; }

        // front and left
        var front = Vector3.Zero;
        foreach (var l in legs.Where(l => l.Toe >= 0)) front += Horizontal(l.Chain[l.Toe].Pos - l.Chain[l.Foot].Pos, up);
        string? frontFrom = "the toes";
        if (front.Length() < near)
        {
            front = Vector3.Zero; frontFrom = "the knees' bend";
            foreach (var l in legs) front += Horizontal(l.Chain[l.Knee].Pos - (l.Chain[0].Pos + l.Chain[l.Foot].Pos) / 2, up);
        }
        if (front.Length() < near * 0.2f)
        {
            // no telling front from back: any way across the legs; left and right may come out swapped
            front = Vector3.Cross(Horizontal(legs[0].Chain[0].Pos - legs[1].Chain[0].Pos, up), up);
            frontFrom = null;
            notes.Add("couldn't tell the front from the back (no toes, straight knees): left and right may be swapped, check them in the Bone Map");
        }
        front = Vector3.Normalize(front);
        var left = Vector3.Normalize(Vector3.Cross(up, front));
        float Side(Vector3 p) => Vector3.Dot(p - hips.Pos, left);
        bool debug = Environment.GetEnvironmentVariable("MFF_GUESS_DEBUG") == "1";
        if (debug) Console.WriteLine($"  hips {hips.Name}, up {up}, front {front} (from {frontFrom}), left {left}, legs {legA.Name} / {legB.Name}");

        // Biped-like rigs hang the legs and the spine's next bone from the first spine bone, under a pelvis joint just below
        // it with that one chain: the pelvis is that parent, the junction is the spine's first bone
        J pelvis = hips;
        if (hips.Parent is { } hp && Vector3.Distance(hp.Pos, hips.Pos) is var d && (d > near || hp.Weight > 0) && d < 0.2f * legLen)
            pelvis = hp;
        roles[pelvis] = "Bip001 Pelvis";
        if (debug && hips.Parent is { } dp) Console.WriteLine($"  hips parent {dp.Name}: {Vector3.Distance(dp.Pos, hips.Pos) / legLen:0.000} leg lengths away, weight {dp.Weight:0}, {dp.Children.Count(c => c.Depth >= 3)} chain(s); pelvis {pelvis.Name}");

        foreach (var l in legs)
        {
            string s = Side(l.Chain[0].Pos) > 0 ? "L" : "R";
            if (roles.ContainsValue($"Bip001 {s} Thigh")) s = s == "L" ? "R" : "L";
            roles[l.Chain[0]] = $"Bip001 {s} Thigh";
            roles[l.Chain[l.Knee]] = $"Bip001 {s} Calf";
            roles[l.Chain[l.Foot]] = $"Bip001 {s} Foot";
            if (l.Toe >= 0) roles[l.Chain[l.Toe]] = $"Bip001 {s} Toe0"; else missing.Add($"{(s == "L" ? "left" : "right")} toe");
        }

        // the spine upward, and the arms off it
        J? Highest(J j) => j.Children.OrderByDescending(c => Under(c).Max(x => H(x.Pos))).FirstOrDefault();
        var raw = new List<J>();
        if (pelvis != hips) raw.Add(hips);
        for (J? j = spineRoot; j != null; j = Highest(j)) raw.Add(j);
        var arms = new List<(J Root, int At, float Side)>();
        for (int i = 0; i < raw.Count; i++)
            foreach (var c in raw[i].Children)
                if ((i + 1 >= raw.Count || c != raw[i + 1]) && c.Depth >= 3 && c != legA && c != legB)
                {
                    // its side by its first joints (clavicle, upper arm, elbow), not its farthest one (a prop bone in the hand)
                    var first = Chain(c, Deepest).Take(3).ToList();
                    var at = first.Aggregate(Vector3.Zero, (acc, j) => acc + j.Pos) / first.Count;
                    arms.Add((c, i, Vector3.Dot(at - raw[i].Pos, left)));
                }
        if (debug) foreach (var a in arms) Console.WriteLine($"  arm candidate {a.Root.Name} at {raw[a.At].Name}, side {a.Side:0.00}, depth {a.Root.Depth}");
        var leftArm = arms.Where(a => a.Side > 0).OrderByDescending(a => a.Side).FirstOrDefault();
        var rightArm = arms.Where(a => a.Side < 0).OrderBy(a => a.Side).FirstOrDefault();
        int junction = leftArm.Root != null || rightArm.Root != null
            ? Math.Max(leftArm.Root != null ? leftArm.At : -1, rightArm.Root != null ? rightArm.At : -1)
            : (int)(raw.Count * 0.7f);
        var spine = new List<J>();
        foreach (var j in raw.Take(junction + 1)) if (spine.Count == 0 ? Vector3.Distance(j.Pos, pelvis.Pos) > near : Vector3.Distance(spine[^1].Pos, j.Pos) > near) spine.Add(j);
        var above = new List<J>();
        foreach (var j in raw.Skip(junction + 1))
            if ((j.Deforms || j.Children.Count > 0) && (above.Count == 0 ? Vector3.Distance(raw[junction].Pos, j.Pos) > near : Vector3.Distance(above[^1].Pos, j.Pos) > near)) above.Add(j);
        if (above.Count == 0) { whyNot = "no head above the shoulders: the skeleton couldn't be guessed"; return null; }
        // the head: the first joint above the shoulders that branches (eyes, jaw, hair, an end bone), else the last; the walk up
        // follows the highest reach, which goes on into hair
        // (the joint carrying the most weight above the shoulders: the face and skull; the walk goes on into hair)
        int hi = above.Any(j => j.Weight > 0) ? above.IndexOf(above.MaxBy(j => j.Weight)!) : above.FindIndex(j => j.Children.Count >= 2);
        if (hi < 0) hi = above.Count - 1;
        J? neck = null; J head = above[hi];
        if (hi >= 1) neck = above[hi - 1];
        else if (spine.Count >= 2 && Vector3.Distance(raw[junction].Pos, head.Pos) < 0.2f * legLen)
        { neck = spine[^1]; spine.RemoveAt(spine.Count - 1); }   // the arms hang from the neck itself (Biped's clavicles start at it): the joint the head sits right on
        if (spine.Count >= 1) roles[spine[0]] = "Bip001 Spine";
        if (spine.Count >= 3) { roles[spine[(spine.Count - 1) / 2]] = "Bip001 Spine1"; roles[spine[^1]] = "Bip001 Spine2"; }
        else if (spine.Count == 2) roles[spine[1]] = "Bip001 Spine1";   // as a Biped with two spine bones
        if (neck != null) roles[neck] = "Bip001 Neck"; else missing.Add("neck");
        roles[head] = "Bip001 Head";

        foreach (var (arm, s) in new[] { (leftArm, "L"), (rightArm, "R") })
        {
            string side = s == "L" ? "left" : "right";
            if (arm.Root == null) { missing.Add(side + " arm"); continue; }
            // the arm joint by joint (joints at their parent's place skipped; twist chains are side branches): a clavicle when
            // the first segment is short against the next (clavicle / upper arm 0.4–0.75, upper arm / forearm 1–1.2), then the
            // upper arm, the elbow and the hand
            var a = Chain(arm.Root, Deepest);
            // (against the straight line from the upper arm to the hand when there is one: clavicles can be long on big models)
            bool clavicle = a.Count >= 4 && (Vector3.Distance(a[0].Pos, a[1].Pos) < 0.85f * Vector3.Distance(a[1].Pos, a[2].Pos)
                || Vector3.Distance(a[0].Pos, a[1].Pos) < 0.6f * Vector3.Distance(a[1].Pos, a[3].Pos));
            int first = clavicle ? 1 : 0;
            if (a.Count < first + 2) { missing.Add(side + " arm"); continue; }
            var handJ = a[Math.Min(first + 2, a.Count - 1)];
            if (debug) Console.WriteLine($"  {side} arm: " + string.Join(" > ", a.Select(j => j.Name)) + " ; hand " + handJ.Name);
            if (clavicle) roles[a[0]] = $"Bip001 {s} Clavicle"; else missing.Add(side + " clavicle");
            a = a.Skip(first).TakeWhile(j => j != handJ).ToList();
            roles[a[0]] = $"Bip001 {s} UpperArm";
            if (a.Count >= 2)
            {
                float half = Vector3.Distance(a[0].Pos, handJ.Pos) / 2;
                roles[a.Skip(1).MinBy(j => MathF.Abs(Vector3.Distance(a[0].Pos, j.Pos) - half))!] = $"Bip001 {s} Forearm";
            }
            else missing.Add(side + " elbow");
            roles[handJ] = $"Bip001 {s} Hand";
            // fingers
            var fingers = handJ.Children.Where(c => c.Depth >= 2).ToList();
            if (fingers.Count < 2) { missing.Add(side + " fingers"); continue; }
            var byDistance = fingers.OrderBy(f => Vector3.Distance(f.Pos, handJ.Pos)).ToList();
            var rest = byDistance.Skip(1).Select(f => Vector3.Distance(f.Pos, handJ.Pos)).OrderBy(d => d).ToList();
            // the thumb: the finger pointing most unlike the others (the four lie side by side), else one rooted much nearer the wrist
            Vector3 Dir(J f) { var c = Chain(f, Deepest); var d = c[^1].Pos - handJ.Pos; return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : Vector3.UnitX; }
            var alike = fingers.Select(f => (F: f, Cos: fingers.Where(o => o != f).Average(o => Vector3.Dot(Dir(f), Dir(o))))).OrderBy(x => x.Cos).ToList();
            J? thumb = fingers.Count >= 3 && (fingers.Count >= 5 || alike[0].Cos < alike[1].Cos - 0.1f) ? alike[0].F
                : Vector3.Distance(byDistance[0].Pos, handJ.Pos) < 0.8f * rest[rest.Count / 2] ? byDistance[0] : null;
            var order = fingers.Where(f => f != thumb).OrderByDescending(f => Vector3.Dot(f.Pos - handJ.Pos, front)).Take(4).ToList();
            void Finger(J root, int f)
            {
                var c = Chain(root, Deepest);
                for (int k = 0; k < 3 && k < c.Count; k++) roles[c[k]] = $"Bip001 {s} Finger{f}{(k == 0 ? "" : k.ToString())}";
            }
            if (thumb != null) Finger(thumb, 0); else missing.Add(side + " thumb");
            for (int k = 0; k < order.Count; k++) Finger(order[k], k + 1);
        }

        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (j, biped) in roles) if (!rename.ContainsKey(j.Name) && !rename.ContainsValue(biped)) rename[j.Name] = biped;
        notes.Insert(0, $"skeleton guessed from its shape (no names this importer knows): {rename.Count} bones paired with MFF (Biped) ones" +
            (frontFrom != null ? $", front from {frontFrom}" : "") + ". Check the pairs in the Bone Map (marked \"guessed\").");
        notes.Add($"guessed: hips {hips.Name}; spine {string.Join(", ", spine.Select(j => j.Name))}; neck {neck?.Name ?? "-"}; head {head.Name}");
        if (missing.Count > 0) notes.Add("not found: " + string.Join(", ", missing) + " (their weights go to the nearest paired parent)");
        return new Result("Guessed", rename, notes);
    }

    static Vector3 Horizontal(Vector3 v, Vector3 up) => v - Vector3.Dot(v, up) * up;
}
