using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Smooths one MHO bone's weights over the mesh (0.12.0, Kurt: a button on the Bone Map tab), as Blender's Weight Paint
/// Smooth on one vertex group, Kurt's routine there: factor 0.5, repeated. Per step, each vertex's weight for the bone moves
/// halfway to the average of its neighbours'; the vertex's other bones then share what's left in their old proportions
/// (a vertex that was all on this bone takes the rest from its neighbours' other bones). Neighbours come from the triangles,
/// with vertices at the same place welded across sections and parts (UV seams, material borders), so a seam smooths as one.
/// Every vertex keeps at most 4 bones, summing to 1.
/// </summary>
static class WeightSmooth
{
    /// <summary>Steps per click (each at <see cref="Factor"/>): Blender's Smooth 0.5 × 5.</summary>
    public const int StepsPerPass = 5;
    public const float Factor = 0.5f;

    /// <summary>Applies the map file's smoothing (in its order) to the retarget's weights; returns a line per bone.</summary>
    public static List<string> Apply(Retargeted r, IEnumerable<BoneMapFile.SmoothEntry> entries)
    {
        var notes = new List<string>();
        var list = entries.Where(e => e.Passes > 0).ToList();
        if (list.Count == 0) return notes;
        var topo = Topology.Of(r);
        foreach (var e in list)
        {
            int bone = r.Bones.FindIndex(b => b.Name.Equals(e.Bone, StringComparison.OrdinalIgnoreCase));
            if (bone < 0) { notes.Add($"smooth: {e.Bone} isn't a bone of this skeleton (skipped)"); continue; }
            int changed = Bone(r, topo, bone, e.Passes * StepsPerPass, Factor);
            notes.Add($"smooth: {e.Bone} × {e.Passes} ({changed} vertices changed)");
        }
        return notes;
    }

    /// <summary>The welded mesh: a group per place, its member vertices and its neighbouring groups.</summary>
    sealed class Topology
    {
        public List<(int Section, int V)>[] Members = [];
        public int[][] Neighbours = [];

        public static Topology Of(Retargeted r)
        {
            float height = 1;
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var s in r.Sections) foreach (var p in s.Pos) { lo = MathF.Min(lo, p.Z); hi = MathF.Max(hi, p.Z); }
                if (hi > lo) height = hi - lo;
            }
            float cell = height * 1e-5f;
            var key = new Dictionary<(long, long, long), int>();
            var groupOf = new int[r.Sections.Count][];
            var members = new List<List<(int, int)>>();
            for (int si = 0; si < r.Sections.Count; si++)
            {
                var s = r.Sections[si];
                groupOf[si] = new int[s.Pos.Length];
                for (int v = 0; v < s.Pos.Length; v++)
                {
                    var p = s.Pos[v];
                    var k = ((long)MathF.Round(p.X / cell), (long)MathF.Round(p.Y / cell), (long)MathF.Round(p.Z / cell));
                    if (!key.TryGetValue(k, out int g)) { g = members.Count; key[k] = g; members.Add(new()); }
                    groupOf[si][v] = g;
                    members[g].Add((si, v));
                }
            }
            var nb = new HashSet<int>[members.Count];
            for (int g = 0; g < nb.Length; g++) nb[g] = new();
            for (int si = 0; si < r.Sections.Count; si++)
            {
                var t = r.Sections[si].Tris;
                for (int i = 0; i + 2 < t.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = groupOf[si][t[i + e]], b = groupOf[si][t[i + (e + 1) % 3]];
                        if (a != b) { nb[a].Add(b); nb[b].Add(a); }
                    }
            }
            return new Topology { Members = members.Select(m => m.ToList()).ToArray(), Neighbours = nb.Select(n => n.ToArray()).ToArray() };
        }
    }

    /// <summary>Smooths bone <paramref name="bone"/>'s weights: <paramref name="steps"/> steps at <paramref name="factor"/>.
    /// Returns how many vertices changed.</summary>
    static int Bone(Retargeted r, Topology topo, int bone, int steps, float factor)
    {
        int n = topo.Members.Length;
        // each group's weights (its first member's; welded copies normally agree)
        var w = new Dictionary<int, float>[n];
        for (int g = 0; g < n; g++)
        {
            var (si, v) = topo.Members[g][0];
            w[g] = r.Sections[si].Weights[v].ToDictionary(x => x.Bone, x => x.Weight);
        }
        // only groups on or next to the bone can change
        var active = new HashSet<int>();
        for (int g = 0; g < n; g++)
            if (w[g].GetValueOrDefault(bone) > 0) { active.Add(g); foreach (int o in topo.Neighbours[g]) active.Add(o); }
        var touched = new HashSet<int>();
        for (int step = 0; step < steps; step++)
        {
            var next = new Dictionary<int, Dictionary<int, float>>();
            foreach (int g in active)
            {
                var nbs = topo.Neighbours[g];
                if (nbs.Length == 0) continue;
                float own = w[g].GetValueOrDefault(bone);
                float avg = nbs.Average(o => w[o].GetValueOrDefault(bone));
                float now = own + (avg - own) * factor;
                if (MathF.Abs(now - own) < 1e-6f) continue;
                var acc = new Dictionary<int, float>();
                float othersSum = w[g].Where(x => x.Key != bone).Sum(x => x.Value);
                if (othersSum > 1e-6f)
                    foreach (var (b, x) in w[g]) { if (b != bone) acc[b] = x * (1 - now) / othersSum; }
                else
                {
                    // all on this bone before: the rest comes from the neighbours' other bones, in their proportions
                    var from = new Dictionary<int, float>();
                    foreach (int o in nbs) foreach (var (b, x) in w[o]) if (b != bone) from[b] = from.GetValueOrDefault(b) + x;
                    float fs = from.Values.Sum();
                    if (fs <= 1e-6f) continue;   // nothing else near: stays
                    foreach (var (b, x) in from) acc[b] = x / fs * (1 - now);
                }
                acc[bone] = now;
                next[g] = acc;
            }
            foreach (var (g, acc) in next)
            {
                w[g] = acc; touched.Add(g);
                foreach (int o in topo.Neighbours[g]) active.Add(o);
            }
        }
        int changed = 0;
        foreach (int g in touched)
        {
            // at most 4 bones, summing to 1 (as Retarget's Top4)
            var top = w[g].Where(x => x.Value > 1e-4f).OrderByDescending(x => x.Value).Take(4).ToList();
            float sum = top.Sum(x => x.Value);
            var final = top.Select(x => (x.Key, x.Value / sum)).ToArray();
            foreach (var (si, v) in topo.Members[g]) { r.Sections[si].Weights[v] = final; changed++; }
        }
        return changed;
    }
}
