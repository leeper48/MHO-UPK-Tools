using System.Numerics;
using System.Text.RegularExpressions;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Skins a model onto an added rig (Kurt, 2026-11-18: a dress rig on a mod's own model added bones nothing rode). An MFF source
/// pairs its skirt / cape chains with the rig by itself; an FBX source (the mod's own model, a model from Blender) only moves with
/// a rig when its vertices are already weighted to bones of the rig's names, and a model built from MFF before has its cloth
/// folded onto the pelvis and thighs. Here the cloth parts' vertices below the rig's top are weighted to the nearest rig chains
/// (the two nearest, by distance, so the sides blend front to back), along each chain by where they hang, fading in over the
/// chain's first stretch so the waist keeps its own weights. Bones of the legs and arms inside those parts (boots, hands) keep
/// theirs.
/// </summary>
static class RigSkin
{
    /// <summary>The kinds that hang from the hips, where the cloth parts can be guessed (a part mostly weighted to the pelvis
    /// that hangs below the rig's top). Others (cape, hair, tail, chains, wings) are skinned only on parts picked by hand.</summary>
    public static bool Guessable(BorrowedRig.Kind k) => k is BorrowedRig.Kind.Dress or BorrowedRig.Kind.Coat or BorrowedRig.Kind.Loincloth;

    static readonly Regex Limb = new(@"knee|calf|ankle|foot|ball|toe|clav|shoulder|elbow|forarm|forearm|wrist|hand|thumb|index|middle|ring|birdy|pinky|finger", RegexOptions.IgnoreCase);

    sealed record Chain(List<int> Bones, float Length, float TopHeight);

    static List<Chain> Chains(Retargeted r, BorrowedRig rig, Vector3 up)
    {
        var mine = new HashSet<int>(Enumerable.Range(0, r.Bones.Count).Where(i => rig.Bones.IsMatch(r.Bones[i].Name)));
        var chains = new List<Chain>();
        foreach (int root in mine.Where(i => !mine.Contains(r.Bones[i].Parent)))
        {
            var list = new List<int> { root };
            for (int at = root, g = 0; g < 64; g++)
            {
                int next = -1;
                for (int i = 0; i < r.Bones.Count; i++) if (r.Bones[i].Parent == at && mine.Contains(i)) { next = i; break; }
                if (next < 0) break;
                list.Add(next); at = next;
            }
            float len = 0;
            for (int i = 1; i < list.Count; i++) len += Vector3.Distance(r.Bones[list[i - 1]].Position, r.Bones[list[i]].Position);
            chains.Add(new Chain(list, Math.Max(len, 1f), Vector3.Dot(r.Bones[root].Position, up)));
        }
        return chains;
    }

    /// <summary>The bones a rig hangs from and the ones above them (the pelvis and spine for a dress whose chains hang from the
    /// thighs): a cloth vertex is weighted to these.</summary>
    static HashSet<int> HangsFrom(Retargeted r, IEnumerable<Chain> chains)
    {
        var parents = chains.Select(c => r.Bones[c.Bones[0]].Parent).Where(p => p >= 0).Distinct().ToList();
        var set = new HashSet<int>();
        if (parents.Count == 0) return set;
        // the nearest bone above all of them (the thighs' pelvis); the parent itself when they share it (a cape's spine)
        List<int> Up(int b) { var l = new List<int>(); for (int k = b, g = 0; k >= 0 && g < 256; k = r.Bones[k].Parent, g++) { l.Add(k); if (r.Bones[k].Parent == k) break; } return l; }
        var common = Up(parents[0]);
        foreach (int p in parents.Skip(1)) { var u = Up(p).ToHashSet(); common = common.Where(u.Contains).ToList(); }
        // (not the root and the *_offset bones: MHO's thighs hang from g_l_hip_offset under the root, not from the pelvis)
        foreach (int b in common) if (r.Bones[b].Parent >= 0 && r.Bones[b].Parent != b && !r.Bones[b].Name.EndsWith("_offset", StringComparison.OrdinalIgnoreCase)) set.Add(b);
        // hung from the thighs: the hips' own body bones, the pelvis and the lowest spine bone
        if (set.Count == 0)
            for (int i = 0; i < r.Bones.Count; i++)
                if (Regex.IsMatch(r.Bones[i].Name, @"^g_(pelvis|spine0?1)$", RegexOptions.IgnoreCase)) set.Add(i);
        return set;
    }

    static int Dominant((int Bone, float Weight)[] w) => w.Length == 0 ? -1 : w.MaxBy(x => x.Weight).Bone;

    /// <summary>The parts that look like cloth hanging on the rig: at least 40 vertices below its top, 60 % of them weighted
    /// mostly to the bones it hangs from.</summary>
    public static List<string> Guess(Retargeted r, BorrowedRig rig, Vector3 up)
    {
        if (!Guessable(rig.Part)) return [];
        var chains = Chains(r, rig, up);
        if (chains.Count == 0) return [];
        var from = HangsFrom(r, chains);
        float top = chains.Max(c => c.TopHeight);
        var parts = new List<string>();
        foreach (var g in r.Sections.GroupBy(s => s.Part))
        {
            int below = 0, hanging = 0;
            foreach (var s in g)
                for (int v = 0; v < s.Pos.Length; v++)
                {
                    if (Vector3.Dot(s.Pos[v], up) >= top) continue;
                    below++;
                    if (from.Contains(Dominant(s.Weights[v]))) hanging++;
                }
            if (Environment.GetEnvironmentVariable("MHO_RIGSKIN_DEBUG") == "1") Console.WriteLine($"  guess {g.Key}: {below} below {top:0.0}, {hanging} hanging from {string.Join(" ", from.Select(b => r.Bones[b].Name))}");
            if (below >= 40 && hanging >= 0.6f * below) parts.Add(g.Key);
        }
        return parts;
    }

    /// <summary>
    /// Skins the parts onto each rig nothing rides yet. <paramref name="choice"/>: "" = the guessed parts, "-" = none, else the
    /// part names (';'). Returns a note per rig skinned (empty: nothing done).
    /// </summary>
    public static List<string> Apply(Retargeted r, IReadOnlyList<BorrowedRig> rigs, Vector3 up, string? choice)
    {
        var notes = new List<string>();
        if (choice == "-") return notes;
        var picked = string.IsNullOrEmpty(choice) ? null : choice.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rig in rigs)
        {
            if (Gui.PreviewPanel.RiddenBy(r, rig) > 0) continue;   // (the model has the rig's bones already: Medusa's hair)
            var chains = Chains(r, rig, up);
            if (chains.Count == 0) continue;
            var parts = picked ?? Guess(r, rig, up).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (parts.Count == 0) continue;
            var from = HangsFrom(r, chains);
            int done = 0;
            foreach (var s in r.Sections.Where(s => parts.Contains(s.Part)))
                for (int v = 0; v < s.Pos.Length; v++)
                {
                    var p = s.Pos[v];
                    int dom = Dominant(s.Weights[v]);
                    // legs, arms and hands inside a cloth part keep their own weights (boots, gloves); the bones it hangs from and
                    // the thighs it's hung on are cloth
                    if (dom >= 0 && !from.Contains(dom) && !chains.Any(c => r.Bones[c.Bones[0]].Parent == dom) && Limb.IsMatch(r.Bones[dom].Name)) continue;
                    // the nearest point of each chain
                    var near = chains.Select(c => (Chain: c, Hit: Nearest(r, c, p))).OrderBy(x => x.Hit.Dist).Take(2).ToList();
                    var c0 = near[0];
                    float h = Vector3.Dot(p, up), fade = 0.15f * c0.Chain.Length;
                    float t = Math.Clamp((c0.Chain.TopHeight + fade - h) / (2 * fade), 0, 1);
                    float k = t * t * (3 - 2 * t);
                    if (k <= 0) continue;
                    var add = new Dictionary<int, float>();
                    float d0 = Math.Max(c0.Hit.Dist, 0.01f), a0 = 1 / (d0 * d0), a1 = 0;
                    if (near.Count > 1 && near[1].Hit.Dist < 2.5f * d0) { float d1 = Math.Max(near[1].Hit.Dist, 0.01f); a1 = 1 / (d1 * d1); }
                    void Put((int A, int B, float T) hit, float share)
                    {
                        add[hit.A] = add.GetValueOrDefault(hit.A) + share * (1 - hit.T);
                        if (hit.T > 0) add[hit.B] = add.GetValueOrDefault(hit.B) + share * hit.T;
                    }
                    Put((c0.Hit.A, c0.Hit.B, c0.Hit.T), a0 / (a0 + a1));
                    if (a1 > 0) Put((near[1].Hit.A, near[1].Hit.B, near[1].Hit.T), a1 / (a0 + a1));
                    var all = new Dictionary<int, float>();
                    foreach (var (b, w) in s.Weights[v]) all[b] = all.GetValueOrDefault(b) + w * (1 - k);
                    foreach (var (b, w) in add) all[b] = all.GetValueOrDefault(b) + w * k;
                    var top4 = all.Where(x => x.Value > 0.001f).OrderByDescending(x => x.Value).Take(4).ToList();
                    float sum = top4.Sum(x => x.Value);
                    s.Weights[v] = top4.Select(x => (x.Key, x.Value / sum)).ToArray();
                    done++;
                }
            if (done > 0) notes.Add($"{rig.Label}: {done:N0} vertices of {string.Join(", ", parts)} skinned onto its {chains.Count} chains");
        }
        return notes;
    }

    /// <summary>The nearest point of a chain: the segment's bones (a, b) and how far along it (0 = at a), and the distance. Past
    /// its last bone the last bone holds it.</summary>
    static (int A, int B, float T, float Dist) Nearest(Retargeted r, Chain c, Vector3 p)
    {
        var best = (A: c.Bones[^1], B: c.Bones[^1], T: 0f, Dist: Vector3.Distance(p, r.Bones[c.Bones[^1]].Position));
        for (int i = 0; i + 1 < c.Bones.Count; i++)
        {
            Vector3 a = r.Bones[c.Bones[i]].Position, b = r.Bones[c.Bones[i + 1]].Position, ab = b - a;
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-8f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0, 1);
            float d = Vector3.Distance(p, a + ab * t);
            if (d < best.Dist) best = (c.Bones[i], c.Bones[i + 1], t, d);
        }
        return best;
    }
}
