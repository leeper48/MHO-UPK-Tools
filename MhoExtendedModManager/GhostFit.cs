using System.Numerics;

namespace MhoExtendedModManager;

/// <summary>
/// Fits a dodge ghost (a static mesh the hero's dodge effect emits, 54 of 66 heroes have them) to the hero's own model posed
/// at a frame (2026-10-17, Kurt: replace the ghost with the mod's model, per costume). A similarity transform (scale, turn,
/// offset) from the posed model to the ghost, by ICP: every ghost vertex to its nearest point of the posed model (the ghost
/// is a reduced, often cut copy, so only that direction is measured), solved with Horn's quaternion method, from 24
/// axis-aligned starting turns. The error is the mean distance in the ghost's units, also as a share of its size.
/// </summary>
static class GhostFit
{
    public sealed record Fit(float Error, float ErrorShare, float Scale, Quaternion Turn, Vector3 Offset, int Start);

    /// <summary>A posed model made ready for fitting (its nearest-point grid, built once).</summary>
    public sealed class Model
    {
        internal readonly Vector3[] Points;
        internal readonly Grid Grid;
        /// <summary>The sampled points' normals (by point), when given: the fit then also checks which way the surfaces face.</summary>
        internal readonly Dictionary<Vector3, Vector3>? Normals;
        public Model(Vector3[] points, Vector3[]? normals = null)
        {
            Points = points;
            var sp = Sample(points, 20000);
            Grid = new(sp);
            if (normals != null && normals.Length == points.Length)
            {
                var sn = Sample(normals, 20000);
                Normals = [];
                for (int i = 0; i < sp.Length; i++) Normals.TryAdd(sp[i], sn[i]);
            }
        }
    }

    /// <summary>The share of the ghost's surface (sampled) that faces the way the model's nearest surface faces after the fit:
    /// near 1 for a good fit, low for one turned back to front (a body is nearly the same shape from behind; Kurt,
    /// 2026-10-17: ghosts facing the wrong way).</summary>
    public static float Agreement(Model model, Vector3[] ghost, Vector3[] ghostNormals, Fit f)
    {
        if (model.Normals == null || ghostNormals.Length != ghost.Length) return 1;
        var g = Sample(ghost, 400); var gn = Sample(ghostNormals, 400);
        var inv = Quaternion.Inverse(f.Turn);
        int agree = 0, n = 0;
        for (int i = 0; i < g.Length; i++)
        {
            if (gn[i].LengthSquared() < 1e-6f) continue;
            var pm = Vector3.Transform((g[i] - f.Offset) / f.Scale, inv);
            var near = model.Grid.Nearest(pm, out _);
            if (!model.Normals.TryGetValue(near, out var nm)) continue;
            n++;
            if (Vector3.Dot(Vector3.Transform(gn[i], inv), nm) > 0) agree++;
        }
        return n == 0 ? 1 : (float)agree / n;
    }

    /// <summary>The best transform of the model onto <paramref name="ghost"/>: from every one of the 24 starting turns
    /// (<paramref name="start"/> null), or from that one only.</summary>
    /// <param name="rigid">The model's own size (scale 1: turn and offset only). Kurt, 2026-10-17: a free scale blew a crouch or
    /// a roll up to a standing body's height, so every ghost came out bigger than the character.</param>
    public static Fit Best(Model model, Vector3[] ghost, int iterations = 30, int? start = null, bool rigid = false, Vector3[]? ghostNormals = null)
    {
        var g = Sample(ghost, 400);
        float diag = Diagonal(ghost);
        var starts = Starts();
        if (start is int only) return Run(model.Grid, g, model.Points, starts[only], iterations, diag, rigid) with { Start = only };
        Fit? best = null;
        var trial = new List<(Fit F, int K)>();
        for (int k = 0; k < starts.Count; k++) trial.Add((Run(model.Grid, g, model.Points, starts[k], 5, diag, rigid), k));
        // with the ghost's normals: more starts refined, and a fit turned the wrong way (its surfaces facing away from the
        // model's) counts as worse: its error is raised by how much of the ghost faces the wrong way
        bool judge = ghostNormals != null && model.Normals != null;
        float Score(Fit r) => judge ? r.Error * (1 + 3 * (1 - Agreement(model, ghost, ghostNormals!, r))) : r.Error;
        float bestScore = float.MaxValue;
        foreach (var (f, k) in trial.OrderBy(t => t.F.Error).Take(judge ? 8 : 3))
        {
            var r = Run(model.Grid, g, model.Points, starts[k], iterations, diag, rigid) with { Start = k };
            float sc = Score(r);
            if (best == null || sc < bestScore) { best = r; bestScore = sc; }
        }
        return judge ? best! with { ErrorShare = bestScore / Math.Max(1e-6f, diag) } : best!;
    }

    /// <summary>How close <paramref name="ghost"/> is to the model as they both stand (no transform): the mean distance from
    /// the ghost's (sampled) vertices to the model, also as a share of the ghost's size. The game keeps its dodge ghosts in the
    /// character's own space, upright and at its size (Taskmaster's: Z 0 … 111 against the model's 0 … 98), so the right pose
    /// is the one that lies on the ghost as it is.</summary>
    public static Fit Direct(Model model, Vector3[] ghost)
    {
        var g = Sample(ghost, 400);
        float sum = 0;
        // (a few rings at most: a pose far from the ghost is a poor match anyway, and a full search there took minutes)
        float far = model.Grid.CellSize * 4;
        foreach (var p in g) { model.Grid.Nearest(p, out float d2, 4); sum += d2 == float.MaxValue ? far : MathF.Min(far, MathF.Sqrt(d2)); }
        float err = sum / Math.Max(1, g.Length);
        return new Fit(err, err / Math.Max(1e-6f, Diagonal(ghost)), 1, Quaternion.Identity, Vector3.Zero, 0);
    }

    /// <summary>The model's points through a fit's transform (into the ghost's space).</summary>
    public static Vector3[] Apply(Model model, Fit f) => [.. model.Points.Select(p => f.Scale * Vector3.Transform(p, f.Turn) + f.Offset)];

    static Fit Run(Grid grid, Vector3[] ghost, Vector3[] model, Quaternion start, int iterations, float diag, bool rigid = false)
    {
        // start: centers and sizes matched (rigid: the model's own size), turned by the start
        Vector3 cm = Center(model), cg = Center(ghost);
        float s = rigid ? 1 : Diagonal(ghost) / Math.Max(1e-6f, Diagonal(model));
        var q = start;
        Vector3 t = cg - s * Vector3.Transform(cm, q);
        var src = new Vector3[ghost.Length];
        float err = float.MaxValue;
        for (int it = 0; it < iterations; it++)
        {
            // ghost into model space, its nearest model points
            var inv = Quaternion.Inverse(q);
            float sum = 0;
            for (int i = 0; i < ghost.Length; i++)
            {
                var p = Vector3.Transform((ghost[i] - t) / s, inv);
                src[i] = grid.Nearest(p, out float d2);
                sum += MathF.Sqrt(d2) * s;
            }
            err = sum / ghost.Length;
            (s, q, t) = Similarity(src, ghost);
            if (rigid) { s = 1; t = Center(ghost) - Vector3.Transform(Center(src), q); }
        }
        return new Fit(err, err / Math.Max(1e-6f, diag), s, q, t, 0);
    }

    /// <summary>Horn's absolute orientation with scale: the s, R, t that map <paramref name="a"/> onto <paramref name="b"/>.</summary>
    static (float S, Quaternion Q, Vector3 T) Similarity(Vector3[] a, Vector3[] b)
    {
        Vector3 ca = Center(a), cb = Center(b);
        float sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i] - ca; var r = b[i] - cb;
            sxx += p.X * r.X; sxy += p.X * r.Y; sxz += p.X * r.Z;
            syx += p.Y * r.X; syy += p.Y * r.Y; syz += p.Y * r.Z;
            szx += p.Z * r.X; szy += p.Z * r.Y; szz += p.Z * r.Z;
            na += p.LengthSquared(); nb += r.LengthSquared();
        }
        var n = new double[4, 4]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        // largest eigenvector by power iteration on N + cI (all eigenvalues made positive)
        double c = Math.Sqrt(na * nb) + 1e-9;
        var v = new double[] { 1, 0.01, 0.01, 0.01 };
        for (int k = 0; k < 60; k++)
        {
            var w = new double[4];
            for (int i = 0; i < 4; i++) { w[i] = c * v[i]; for (int j = 0; j < 4; j++) w[i] += n[i, j] * v[j]; }
            double len = Math.Sqrt(w.Sum(x => x * x));
            for (int i = 0; i < 4; i++) v[i] = w[i] / len;
        }
        var q = Quaternion.Normalize(new Quaternion((float)v[1], (float)v[2], (float)v[3], (float)v[0]));
        float dot = 0;
        for (int i = 0; i < a.Length; i++) dot += Vector3.Dot(b[i] - cb, Vector3.Transform(a[i] - ca, q));
        float s = dot / Math.Max(1e-9f, na);
        return (s, q, cb - s * Vector3.Transform(ca, q));
    }

    /// <summary>The 24 turns that map axes onto axes.</summary>
    static List<Quaternion> Starts()
    {
        var list = new List<Quaternion>();
        var axes = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        foreach (var up in axes)
            foreach (var fwd in axes)
            {
                if (MathF.Abs(Vector3.Dot(up, fwd)) > 0.5f) continue;
                var side = Vector3.Cross(up, fwd);
                var m = new Matrix4x4(fwd.X, fwd.Y, fwd.Z, 0, side.X, side.Y, side.Z, 0, up.X, up.Y, up.Z, 0, 0, 0, 0, 1);
                list.Add(Quaternion.CreateFromRotationMatrix(m));
            }
        return list;
    }

    static Vector3 Center(Vector3[] p) { var c = Vector3.Zero; foreach (var x in p) c += x; return c / Math.Max(1, p.Length); }

    static float Diagonal(Vector3[] p)
    {
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var x in p) { lo = Vector3.Min(lo, x); hi = Vector3.Max(hi, x); }
        return Vector3.Distance(lo, hi);
    }

    public static Vector3[] Sample(Vector3[] p, int max)
    {
        if (p.Length <= max) return p;
        var o = new Vector3[max];
        for (int i = 0; i < max; i++) o[i] = p[(int)((long)i * p.Length / max)];
        return o;
    }

    /// <summary>A uniform grid over points for nearest-point queries (searched outward ring by ring).</summary>
    internal sealed class Grid
    {
        readonly Dictionary<(int, int, int), List<Vector3>> cells = [];
        readonly float size;
        readonly Vector3[] all;
        public Grid(Vector3[] pts)
        {
            all = pts;
            size = Math.Max(1e-3f, Diagonal(pts) / 40f);
            foreach (var p in pts)
            {
                var k = Key(p);
                if (!cells.TryGetValue(k, out var l)) cells[k] = l = [];
                l.Add(p);
            }
        }
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / size), (int)MathF.Floor(p.Y / size), (int)MathF.Floor(p.Z / size));
        /// <summary>The cell size (a search capped at a few rings misses points further than that many cells).</summary>
        public float CellSize => size;
        public Vector3 Nearest(Vector3 q, out float d2, int maxRings = 60)
        {
            var (x, y, z) = Key(q);
            Vector3 best = all[0]; d2 = float.MaxValue;
            for (int r = 0; r < maxRings; r++)
            {
                for (int i = -r; i <= r; i++) for (int j = -r; j <= r; j++) for (int k = -r; k <= r; k++)
                {
                    if (Math.Max(Math.Abs(i), Math.Max(Math.Abs(j), Math.Abs(k))) != r) continue;
                    if (!cells.TryGetValue((x + i, y + j, z + k), out var l)) continue;
                    foreach (var p in l) { float d = Vector3.DistanceSquared(p, q); if (d < d2) { d2 = d; best = p; } }
                }
                if (d2 < float.MaxValue && d2 <= (r * size) * (r * size)) break;
            }
            if (d2 == float.MaxValue && maxRings >= 60) foreach (var p in all) { float d = Vector3.DistanceSquared(p, q); if (d < d2) { d2 = d; best = p; } }
            return best;
        }
    }
}
