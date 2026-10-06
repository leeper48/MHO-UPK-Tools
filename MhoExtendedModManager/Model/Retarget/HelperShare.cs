using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Helper bones of team-up and NPC skeletons (Kurt, 2026-10-06: an MFF Kamala Khan on her team-up kinked at the upper arms
/// and thighs). Their skeletons carry extra bones beside the limbs (g_l_uprarm, g_l_lwrarm, g_l_uprleg, g_l_lwrlegmid,
/// g_l_lwrlegend …) that their animations turn (Kamala's run: g_l_uprarm up to about 31°), and the stock mesh gives them a
/// share of the limb (g_l_uprarm 27 of the left arm's weight). A new model weighted only to the main limb bones misses that
/// share and folds at the joint. Here each new vertex on a limb's main bone takes the share the stock mesh's nearest vertices
/// of that limb give its helpers (inverse-distance blend of 4). Only bones the stock mesh uses and the new one doesn't;
/// player skeletons have none of these, so their builds don't change.
/// </summary>
static class HelperShare
{
    static readonly System.Text.RegularExpressions.Regex HelperName = new("^g_[lr]_(uprarm|lwrarm|uprleg|lwrleg)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static void Apply(Retargeted r, MhoSkeleton sk)
    {
        var lod = sk.Mesh.HighestDetail;
        if (lod == null) return;
        var sw = MhoAnim.Weights(lod);
        int nb = sk.Bones.Count;
        var stockTotal = new float[nb]; var ourTotal = new float[nb];
        foreach (var v in sw) foreach (var (b, w) in v) if (b >= 0 && b < nb) stockTotal[b] += w;
        foreach (var s in r.Sections) foreach (var v in s.Weights) foreach (var (b, w) in v) if (b >= 0 && b < nb) ourTotal[b] += w;
        var helpers = Enumerable.Range(0, nb).Where(b => HelperName.IsMatch(sk.Bones[b].Name) && stockTotal[b] > 1 && ourTotal[b] < 1e-3f).ToList();
        if (helpers.Count == 0) return;
        var isHelper = helpers.ToHashSet();
        // each helper's main bone: the non-helper bone it shares the stock vertices with most
        var main = new Dictionary<int, int>();
        foreach (int h in helpers)
        {
            var with = new float[nb];
            foreach (var v in sw)
                if (v.Any(x => x.Item1 == h && x.Item2 > 0)) foreach (var (b, w) in v) if (b != h && !isHelper.Contains(b) && b >= 0 && b < nb) with[b] += w;
            int best = Array.IndexOf(with, with.Max());
            if (best >= 0 && with[best] > 0) main[h] = best;
        }
        int moved = 0;
        foreach (var group in main.GroupBy(kv => kv.Value))
        {
            int m = group.Key;
            var hs = group.Select(kv => kv.Key).ToList();
            // stock vertices of that limb, and the share of it each helper has there
            var src = new List<(Vector3 P, float[] F)>();
            for (int v = 0; v < sw.Length; v++)
            {
                float wm = sw[v].Where(x => x.Item1 == m).Sum(x => x.Item2);
                var wh = hs.Select(h => sw[v].Where(x => x.Item1 == h).Sum(x => x.Item2)).ToArray();
                float tot = wm + wh.Sum();
                if (tot < 0.05f) continue;
                src.Add((lod.Positions[v], wh.Select(w => w / tot).ToArray()));
            }
            if (src.Count == 0) continue;
            foreach (var s in r.Sections)
                for (int v = 0; v < s.Pos.Length; v++)
                {
                    var ws = s.Weights[v];
                    float wm = ws.Where(x => x.Bone == m).Sum(x => x.Weight);
                    if (wm <= 0) continue;
                    var p = s.Pos[v];
                    var near = src.Select((x, i) => (i, d: Vector3.DistanceSquared(x.P, p))).OrderBy(x => x.d).Take(4).ToList();
                    var iw = near.Select(x => 1f / (MathF.Sqrt(x.d) + 1e-3f)).ToArray(); float iws = iw.Sum();
                    var f = new float[hs.Count];
                    for (int n = 0; n < near.Count; n++) for (int k = 0; k < hs.Count; k++) f[k] += src[near[n].i].F[k] * iw[n] / iws;
                    float give = f.Sum();
                    if (give < 0.01f) continue;
                    var acc = new Dictionary<int, float>();
                    foreach (var (b, w) in ws) acc[b] = acc.GetValueOrDefault(b) + (b == m ? w * (1 - give) : w);
                    for (int k = 0; k < hs.Count; k++) if (f[k] > 0) acc[hs[k]] = acc.GetValueOrDefault(hs[k]) + wm * f[k];
                    // at most 4 bones, summing to 1
                    var top = acc.Where(x => x.Value > 1e-4f).OrderByDescending(x => x.Value).Take(4).ToList();
                    float sum = top.Sum(x => x.Value);
                    s.Weights[v] = top.Select(x => (x.Key, x.Value / sum)).ToArray();
                    moved++;
                }
        }
        if (moved > 0)
            r.Notes.Add($"helper bones: {moved:N0} vertices share their limb with {string.Join(", ", main.Keys.Select(h => sk.Bones[h].Name))}, as the stock mesh does");
    }
}
