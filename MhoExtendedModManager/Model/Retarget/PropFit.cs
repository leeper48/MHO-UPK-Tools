using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// A prop target (Kurt, 2026-10-08, a user: US Agent's shield from MFF in place of Captain America's): a weapon or shield is
/// its own small skeletal mesh in the costume package (captainamerica_classicvu_shield: g_handle, g_muzzle), held by an
/// attachment on a hand bone. There is no body to fit, so the picked MFF part is laid over the game's prop as a rigid object:
/// both shapes' principal axes (area-weighted, so dense spots don't pull), centers on each other, the source turned so its axes
/// lie along the game prop's and scaled to its length, every vertex on the bone the game's prop hangs on. The game moves the
/// same prop mesh in every animation and power (Cap's throw), so the new one goes wherever the old one went.
/// </summary>
static class PropFit
{
    /// <summary>A prop skeleton: no pelvis and only a few bones (the game's props have 1–6: g_handle, g_muzzle …).</summary>
    public static bool IsProp(MhoSkeleton sk) => sk.Find("g_pelvis") < 0 && sk.Bones.Count <= 12;

    static Vector3 Mirror(Vector3 v) => new(v.X, -v.Y, v.Z);

    /// <summary>A shape's frame: area-weighted center, axes by decreasing spread (unit, right-handed after <see cref="Signs"/>),
    /// the spread (standard deviation) along each, and the extent along each.</summary>
    internal sealed record Shape(Vector3 Center, Vector3[] Axes, float[] Spread, float[] Extent, float[] Skew);

    /// <summary>The shape of a triangle soup (positions, corner indices).</summary>
    internal static Shape? Measure(IReadOnlyList<Vector3> pos, IReadOnlyList<int> tris)
    {
        // triangle centers weighted by area
        var pts = new List<(Vector3 P, float W)>();
        double tw = 0; Vector3 c = Vector3.Zero;
        for (int t = 0; t + 2 < tris.Count; t += 3)
        {
            var a = pos[tris[t]]; var b = pos[tris[t + 1]]; var d = pos[tris[t + 2]];
            float w = Vector3.Cross(b - a, d - a).Length() * 0.5f;
            if (w <= 0) continue;
            var m = (a + b + d) / 3;
            pts.Add((m, w)); tw += w; c += m * w;
        }
        if (tw <= 0) return null;
        c /= (float)tw;
        // covariance (each triangle as its center; the corners add its own spread)
        double[,] cov = new double[3, 3];
        void Acc(Vector3 v, double w)
        {
            double[] x = [v.X, v.Y, v.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] += w * x[i] * x[j];
        }
        for (int t = 0, k = 0; t + 2 < tris.Count; t += 3)
        {
            var a = pos[tris[t]]; var b = pos[tris[t + 1]]; var d = pos[tris[t + 2]];
            float w = Vector3.Cross(b - a, d - a).Length() * 0.5f;
            if (w <= 0) continue;
            // a triangle's second moment about the center: the corners and the center, as a quadrature
            foreach (var v in new[] { a, b, d }) Acc(v - c, w / 4.0);
            Acc(pts[k].P - c, w / 4.0);
            k++;
        }
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cov[i, j] /= tw;
        var (vals, vecs) = Eigen(cov);
        var axes = vecs.Select(v => Vector3.Normalize(v)).ToArray();
        var spread = vals.Select(v => (float)Math.Sqrt(Math.Max(0, v))).ToArray();
        // extents (corner positions) and the third moment (sign of the lopsided side) along each axis
        var ext = new float[3]; var skew = new float[3];
        for (int i = 0; i < 3; i++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int ix in tris) { float s = Vector3.Dot(pos[ix] - c, axes[i]); lo = MathF.Min(lo, s); hi = MathF.Max(hi, s); }
            ext[i] = hi - lo;
            double m3 = 0;
            foreach (var (p, w) in pts) { double s = Vector3.Dot(p - c, axes[i]); m3 += w * s * s * s; }
            skew[i] = spread[i] > 1e-6f ? (float)(m3 / tw / Math.Pow(spread[i], 3)) : 0;
        }
        return new Shape(c, axes, spread, ext, skew);
    }

    /// <summary>Symmetric 3×3 eigenvalues and eigenvectors (Jacobi), largest first.</summary>
    static (double[] Values, Vector3[] Vectors) Eigen(double[,] a0)
    {
        var a = (double[,])a0.Clone();
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 50; sweep++)
        {
            double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-15) break;
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-20) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double cs = 1 / Math.Sqrt(t * t + 1), sn = t * cs;
                    for (int k = 0; k < 3; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = cs * akp - sn * akq; a[k, q] = sn * akp + cs * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = cs * apk - sn * aqk; a[q, k] = sn * apk + cs * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = cs * vkp - sn * vkq; v[k, q] = sn * vkp + cs * vkq;
                    }
                }
        }
        var order = new[] { 0, 1, 2 }.OrderByDescending(i => a[i, i]).ToArray();
        return (order.Select(i => a[i, i]).ToArray(), order.Select(i => new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i])).ToArray());
    }

    /// <summary>How alike two shapes are in proportion (0 = the same): the middle and short axes against the long one.</summary>
    internal static float Unlike(Shape a, Shape b)
    {
        float R(Shape s, int i) => s.Spread[0] > 1e-6f ? s.Spread[i] / s.Spread[0] : 0;
        return MathF.Abs(R(a, 1) - R(b, 1)) + MathF.Abs(R(a, 2) - R(b, 2));
    }

    /// <summary>The MFF part most like the game's prop: prop-like parts (weapons, shields) first, then by proportions; the
    /// part named after the prop's kind (shield, hammer …) wins among those. Null when no part has triangles.</summary>
    public static Part? BestPart(MffModel m, MhoSkeleton sk)
    {
        var target = TargetShape(sk);
        string kind = KindWord(sk.Name);
        Part? best = null; (int, int, float) bestScore = default;
        foreach (var p in m.Parts)
        {
            var s = Measure(p.Sections.SelectMany(x => x.Pos).ToList(), Corners(p));
            if (s == null) continue;
            var score = (p.IsProp ? 1 : 0, kind.Length > 0 && p.Name.Contains(kind, StringComparison.OrdinalIgnoreCase) ? 1 : 0, target != null ? -Unlike(s, target) : 0);
            if (best == null || score.CompareTo(bestScore) > 0) { best = p; bestScore = score; }
        }
        return best;
    }

    static readonly string[] Kinds = ["shield", "hammer", "sword", "knife", "axe", "spear", "staff", "bow", "gun", "pistol", "rifle", "claw", "blade", "mace"];
    static string KindWord(string mesh) => Kinds.FirstOrDefault(k => mesh.Contains(k, StringComparison.OrdinalIgnoreCase)) ?? "";

    /// <summary>A part's corner indices into its sections' positions laid end to end.</summary>
    static List<int> Corners(Part p)
    {
        var list = new List<int>(); int at = 0;
        foreach (var s in p.Sections) { list.AddRange(s.Tris.Select(i => i + at)); at += s.Pos.Length; }
        return list;
    }

    static Shape? TargetShape(MhoSkeleton sk) =>
        sk.Mesh.HighestDetail is { } lod ? Measure(lod.Positions, lod.Indices) : null;

    /// <summary>The bone the game's prop hangs on: the one carrying most of its skin.</summary>
    static int HoldBone(MhoSkeleton sk)
    {
        var lod = sk.Mesh.HighestDetail;
        if (lod == null || lod.Influences.Count == 0) return 0;
        var sum = new float[sk.Bones.Count];
        foreach (var inf in lod.Influences)
            for (int k = 0; k < inf.Bones.Count; k++)
                if (inf.Bones[k] >= 0 && inf.Bones[k] < sum.Length) sum[inf.Bones[k]] += inf.Weights[k];
        return Array.IndexOf(sum, sum.Max());
    }

    /// <summary>The picked parts as one rigid prop laid over the game's prop (see the class summary).</summary>
    public static Retargeted Run(MffModel m, IEnumerable<Part> parts, MhoSkeleton sk)
    {
        var r = new Retargeted { Source = m, Target = sk };
        for (int i = 0; i < sk.Bones.Count; i++)
            r.Bones.Add(new RefBone { Name = sk.Bones[i].Name, Parent = sk.Bones[i].ParentIndex == i ? -1 : sk.Bones[i].ParentIndex, Global = sk.BoneToModel[i], Mapped = false });
        var list = parts.ToList();
        // the source in MHO's handedness (MFF → MHO negates Y: the triangles keep MHO's winding)
        var srcPos = new List<Vector3>(); var srcTris = new List<int>();
        foreach (var s in list.SelectMany(p => p.Sections)) { srcTris.AddRange(s.Tris.Select(i => i + srcPos.Count)); srcPos.AddRange(s.Pos.Select(Mirror)); }
        var src = Measure(srcPos, srcTris);
        var dst = TargetShape(sk);
        int bone = HoldBone(sk);
        Matrix4x4 place = Matrix4x4.Identity;
        float scale = 1;
        if (src == null || dst == null) r.Notes.Add("prop: nothing to fit (no triangles): the part is left where it is");
        else
        {
            // the source's axes signed to match the target's lopsided side (the dome of a shield, the head of a hammer); an axis with
            // no clear side takes whatever keeps the turn a rotation (no mirror)
            var sa = (Vector3[])src.Axes.Clone();
            var da = dst.Axes;
            var sure = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (src.Skew[i] * dst.Skew[i] < 0) sa[i] = -sa[i];
                sure[i] = MathF.Min(MathF.Abs(src.Skew[i]), MathF.Abs(dst.Skew[i]));
            }
            float Det(Vector3[] a) => Vector3.Dot(a[0], Vector3.Cross(a[1], a[2]));
            if (Det(sa) * Det(da) < 0) { int weak = Array.IndexOf(sure, sure.Min()); sa[weak] = -sa[weak]; }
            // rotation: source axis i → target axis i
            var S = new Matrix4x4(sa[0].X, sa[0].Y, sa[0].Z, 0, sa[1].X, sa[1].Y, sa[1].Z, 0, sa[2].X, sa[2].Y, sa[2].Z, 0, 0, 0, 0, 1);
            var D = new Matrix4x4(da[0].X, da[0].Y, da[0].Z, 0, da[1].X, da[1].Y, da[1].Z, 0, da[2].X, da[2].Y, da[2].Z, 0, 0, 0, 0, 1);
            Matrix4x4.Invert(S, out var Si);
            var rot = Si * D;   // row vectors: v · S⁻¹ = its coordinates on the source axes; · D = the same on the target's
            scale = src.Extent[0] > 1e-6f ? dst.Extent[0] / src.Extent[0] : 1;
            place = Matrix4x4.CreateTranslation(-src.Center) * Matrix4x4.CreateScale(scale) * rot * Matrix4x4.CreateTranslation(dst.Center);
            r.Notes.Add($"prop: {string.Join(", ", list.Select(p => p.Name))} laid over {sk.Name}: centered, turned onto its axes, scaled ×{scale:0.###} to its length ({dst.Extent[0]:0.#} units), held on {sk.Bones[bone].Name}; proportions {src.Spread[1] / Math.Max(1e-6f, src.Spread[0]):0.00} / {src.Spread[2] / Math.Max(1e-6f, src.Spread[0]):0.00} against the game's {dst.Spread[1] / Math.Max(1e-6f, dst.Spread[0]):0.00} / {dst.Spread[2] / Math.Max(1e-6f, dst.Spread[0]):0.00}");
        }
        var turn = place; turn.Translation = Vector3.Zero;
        foreach (var s in list.SelectMany(p => p.Sections))
        {
            int nv = s.Pos.Length;
            var pp = new Vector3[nv]; var nn = new Vector3[nv]; var w = new (int, float)[nv][];
            for (int v = 0; v < nv; v++)
            {
                pp[v] = Vector3.Transform(Mirror(s.Pos[v]), place);
                var n = Vector3.TransformNormal(Mirror(s.Normal[v]), turn);
                nn[v] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : n;
                w[v] = [(bone, 1f)];
            }
            r.Sections.Add(new RefSection { Material = s.Material, Tex = s.Tex, Pos = pp, Normal = nn, Uv = s.Uv, Tris = (int[])s.Tris.Clone(), Weights = w });
        }
        r.Scale = scale;
        return r;
    }
}
