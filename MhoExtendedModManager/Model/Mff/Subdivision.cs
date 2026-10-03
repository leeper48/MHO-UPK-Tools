using System.Numerics;

namespace MhoMffImporter;

/// <summary>
/// One level of Loop subdivision for the older, low-poly MFF models (Kurt, 0.10.10: Spider-Man is 702 vertices). Every
/// triangle becomes four and the surface is smoothed. The topology is welded by position across sections and parts, so UV
/// seams and material borders stay closed; the split vertices keep their own UVs. New vertices take the average UV, normal
/// and bone weights of their edge. Works on copies: the loaded model is unchanged.
/// </summary>
static class Subdivision
{
    public static List<Part> Apply(IEnumerable<Part> parts)
    {
        var list = parts.ToList();
        // welded vertex id per (section, vertex), by position (1/1000 cm)
        var ids = new Dictionary<(long, long, long), int>();
        var wpos = new List<Vector3>();
        var secs = list.SelectMany(p => p.Sections).ToList();
        var wid = new int[secs.Count][];
        for (int s = 0; s < secs.Count; s++)
        {
            var sec = secs[s];
            wid[s] = new int[sec.Pos.Length];
            for (int v = 0; v < sec.Pos.Length; v++)
            {
                var p = sec.Pos[v];
                var key = ((long)MathF.Round(p.X * 1000), (long)MathF.Round(p.Y * 1000), (long)MathF.Round(p.Z * 1000));
                if (!ids.TryGetValue(key, out int id)) { id = wpos.Count; ids[key] = id; wpos.Add(p); }
                wid[s][v] = id;
            }
        }
        int nw = wpos.Count;
        // edges (welded) with their opposite vertices, and each vertex's neighbours
        var opposite = new Dictionary<(int, int), List<int>>();
        var neigh = new HashSet<int>[nw];
        for (int i = 0; i < nw; i++) neigh[i] = new HashSet<int>();
        static (int, int) E(int a, int b) => a < b ? (a, b) : (b, a);
        for (int s = 0; s < secs.Count; s++)
        {
            var t = secs[s].Tris;
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                int a = wid[s][t[k]], b = wid[s][t[k + 1]], c = wid[s][t[k + 2]];
                if (a == b || b == c || a == c) continue;
                foreach (var (x, y, z) in new[] { (a, b, c), (b, c, a), (c, a, b) })
                {
                    var e = E(x, y);
                    if (!opposite.TryGetValue(e, out var o)) opposite[e] = o = new List<int>();
                    o.Add(z);
                    neigh[x].Add(y); neigh[y].Add(x);
                }
            }
        }
        // new positions: edge points and moved original vertices (Loop's rules; boundary / non-manifold edges by midpoint)
        var edgePoint = new Dictionary<(int, int), Vector3>();
        var boundaryNeigh = new List<int>[nw];
        foreach (var (e, o) in opposite)
        {
            if (o.Count == 2) edgePoint[e] = 0.375f * (wpos[e.Item1] + wpos[e.Item2]) + 0.125f * (wpos[o[0]] + wpos[o[1]]);
            else
            {
                edgePoint[e] = 0.5f * (wpos[e.Item1] + wpos[e.Item2]);
                (boundaryNeigh[e.Item1] ??= new()).Add(e.Item2);
                (boundaryNeigh[e.Item2] ??= new()).Add(e.Item1);
            }
        }
        var moved = new Vector3[nw];
        for (int i = 0; i < nw; i++)
        {
            if (boundaryNeigh[i] is { } bn)
                moved[i] = bn.Count == 2 ? 0.75f * wpos[i] + 0.125f * (wpos[bn[0]] + wpos[bn[1]]) : wpos[i];
            else if (neigh[i].Count >= 3)
            {
                int n = neigh[i].Count;
                float beta = n == 3 ? 3f / 16f : 3f / (8f * n);
                var sum = Vector3.Zero; foreach (int j in neigh[i]) sum += wpos[j];
                moved[i] = (1 - n * beta) * wpos[i] + beta * sum;
            }
            else moved[i] = wpos[i];
        }
        // rebuild each section: originals moved, one new vertex per (section) edge
        var result = new List<Part>();
        int si = 0;
        foreach (var part in list)
        {
            var np = new Part
            {
                Name = part.Name, OwnMaterial = part.OwnMaterial, IsProp = part.IsProp, IsAlternate = part.IsAlternate,
                DefaultOn = part.DefaultOn, Min = part.Min, Max = part.Max, BindError = part.BindError,
            };
            foreach (var sec in part.Sections)
            {
                var w = wid[si++];
                var pos = new List<Vector3>(); var nor = new List<Vector3>(); var uv = new List<Vector2>(); var wt = new List<(int, float)[]>();
                for (int v = 0; v < sec.Pos.Length; v++) { pos.Add(moved[w[v]]); nor.Add(sec.Normal[v]); uv.Add(sec.Uv[v]); wt.Add(sec.Weights[v]); }
                var mid = new Dictionary<(int, int), int>();
                int Mid(int a, int b)
                {
                    var k = E(a, b);
                    if (mid.TryGetValue(k, out int m)) return m;
                    m = pos.Count;
                    pos.Add(edgePoint.TryGetValue(E(w[a], w[b]), out var ep) ? ep : 0.5f * (sec.Pos[a] + sec.Pos[b]));
                    var nn = sec.Normal[a] + sec.Normal[b];
                    nor.Add(nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : sec.Normal[a]);
                    uv.Add(0.5f * (sec.Uv[a] + sec.Uv[b]));
                    var acc = new Dictionary<int, float>();
                    foreach (var (bone, x) in sec.Weights[a]) acc[bone] = acc.GetValueOrDefault(bone) + 0.5f * x;
                    foreach (var (bone, x) in sec.Weights[b]) acc[bone] = acc.GetValueOrDefault(bone) + 0.5f * x;
                    wt.Add(acc.Select(kv => (kv.Key, kv.Value)).ToArray());
                    mid[k] = m;
                    return m;
                }
                var tris = new List<int>(sec.Tris.Length * 4);
                for (int k = 0; k + 2 < sec.Tris.Length; k += 3)
                {
                    int a = sec.Tris[k], b = sec.Tris[k + 1], c = sec.Tris[k + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    tris.AddRange([a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca]);
                }
                np.Sections.Add(new Section
                {
                    Material = sec.Material, Tex = sec.Tex, Pos = pos.ToArray(), Normal = nor.ToArray(), Uv = uv.ToArray(),
                    Tris = tris.ToArray(), Weights = wt.ToArray(),
                });
            }
            result.Add(np);
        }
        return result;
    }
}
