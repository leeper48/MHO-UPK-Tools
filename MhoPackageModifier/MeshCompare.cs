using System.Numerics;

namespace MhoPackageModifier;

/// <summary>
/// --mesh-compare &lt;stock.upk&gt; &lt;mesh&gt; &lt;edited.upk&gt; &lt;mesh&gt;: how an edited mesh's vertices compare with the stock
/// mesh's. Vertices at a stock position with the same UV0 are "kept": their normal, tangent, binormal sign and UV1 are
/// compared with the stock vertex's. The rest are "new": their stored normal against the normal their triangles' winding
/// gives (and the same for stock, as the reference), binormal signs and UV ranges. For diagnosing how edited faces render.
/// </summary>
static class MeshCompare
{
    static Vector3 Unpack(uint p)
    {
        var n = new Vector3((p & 0xFF) / 127.5f - 1f, ((p >> 8) & 0xFF) / 127.5f - 1f, ((p >> 16) & 0xFF) / 127.5f - 1f);
        return n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.Zero;
    }
    static int Sign(uint tz) => (tz >> 24) >= 0x80 ? 1 : -1;

    public static int Run(string stockPkg, string stockMesh, string editPkg, string editMesh)
    {
        StaticMesh Load(string pkgPath, string mesh)
        {
            var p = Package.Open(pkgPath);
            int i = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(mesh, StringComparison.OrdinalIgnoreCase)
                || (e.ObjectName.Equals(mesh, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)));
            if (i < 0) throw new InvalidDataException($"no mesh {mesh} in {Path.GetFileName(pkgPath)}");
            return StaticMesh.Read(p, p.Exports[i]);
        }
        var a = Load(stockPkg, stockMesh);
        var b = Load(editPkg, editMesh);
        Console.WriteLine($"stock  {a.Name}: {a.Positions.Length:N0} verts, {a.Indices.Length / 3:N0} tris, {a.NumTexCoords} UV channel(s)");
        Console.WriteLine($"edited {b.Name}: {b.Positions.Length:N0} verts, {b.Indices.Length / 3:N0} tris, {b.NumTexCoords} UV channel(s)");

        // Per vertex, the normal its triangles' winding gives (area-weighted, engine convention unknown: compared as is).
        static Vector3[] FaceNormals(StaticMesh m)
        {
            var n = new Vector3[m.Positions.Length];
            for (int i = 0; i + 2 < m.Indices.Length; i += 3)
            {
                int i0 = m.Indices[i], i1 = m.Indices[i + 1], i2 = m.Indices[i + 2];
                var f = Vector3.Cross(m.Positions[i1] - m.Positions[i0], m.Positions[i2] - m.Positions[i0]);
                n[i0] += f; n[i1] += f; n[i2] += f;
            }
            return n.Select(v => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero).ToArray();
        }
        var fa = FaceNormals(a); var fb = FaceNormals(b);
        void WindingStats(string label, StaticMesh m, Vector3[] f, IEnumerable<int> verts)
        {
            int agree = 0, oppose = 0;
            foreach (int v in verts) { float d = Vector3.Dot(Unpack(m.TangentZ[v]), f[v]); if (d > 0.5f) agree++; else if (d < -0.5f) oppose++; }
            Console.WriteLine($"  {label}: stored normal vs winding normal: {agree:N0} same side, {oppose:N0} opposite");
        }
        WindingStats("stock (reference)", a, fa, Enumerable.Range(0, a.Positions.Length));

        var kept = new List<(int B, int A)>(); var fresh = new List<int>();
        for (int v = 0; v < b.Positions.Length; v++)
        {
            int best = -1; float bd = float.MaxValue;
            for (int s = 0; s < a.Positions.Length; s++)
            {
                if (Vector3.DistanceSquared(a.Positions[s], b.Positions[v]) > 1e-3f) continue;
                float d = Vector2.DistanceSquared(a.TexCoords[0][s], b.TexCoords[0][v]);
                if (d < bd) { bd = d; best = s; }
            }
            if (best >= 0 && bd < 1e-5f) kept.Add((v, best)); else fresh.Add(v);
        }
        Console.WriteLine($"  kept (stock position + UV0): {kept.Count:N0}; new: {fresh.Count:N0}");
        if (kept.Count > 0)
        {
            float nd = kept.Average(p => Vector3.Dot(Unpack(b.TangentZ[p.B]), Unpack(a.TangentZ[p.A])));
            float td = kept.Average(p => Vector3.Dot(Unpack(b.TangentX[p.B]), Unpack(a.TangentX[p.A])));
            int signSame = kept.Count(p => Sign(b.TangentZ[p.B]) == Sign(a.TangentZ[p.A]));
            Console.WriteLine($"  kept: mean normal dot stock {nd:0.###}, mean tangent dot stock {td:0.###}, binormal sign same {signSame:N0}/{kept.Count:N0}");
            if (a.NumTexCoords > 1 && b.NumTexCoords > 1)
                Console.WriteLine($"  kept: UV1 max difference {kept.Max(p => Vector2.Distance(a.TexCoords[1][p.A], b.TexCoords[1][p.B])):0.####}");
            WindingStats("kept", b, fb, kept.Select(p => p.B));
        }
        // Triangle-exact: edited triangles on the same three stock corners, corner normals and UV1 compared with that
        // stock triangle's (per-vertex pairing is ambiguous where a hard edge shares a UV).
        {
            var stockTris = new Dictionary<(Vector3, Vector3, Vector3), int>();
            static Vector3 R(Vector3 p) => new(MathF.Round(p.X * 100) / 100, MathF.Round(p.Y * 100) / 100, MathF.Round(p.Z * 100) / 100);
            for (int i = 0; i + 2 < a.Indices.Length; i += 3) stockTris.TryAdd((R(a.Positions[a.Indices[i]]), R(a.Positions[a.Indices[i + 1]]), R(a.Positions[a.Indices[i + 2]])), i);
            int onStock = 0, normalOk = 0, uv1Ok = 0;
            for (int i = 0; i + 2 < b.Indices.Length; i += 3)
            {
                var key = (R(b.Positions[b.Indices[i]]), R(b.Positions[b.Indices[i + 1]]), R(b.Positions[b.Indices[i + 2]]));
                if (!stockTris.TryGetValue(key, out int j)) continue;
                onStock++;
                bool n = true, u = true;
                for (int k = 0; k < 3; k++)
                {
                    if (Vector3.Dot(Unpack(b.TangentZ[b.Indices[i + k]]), Unpack(a.TangentZ[a.Indices[j + k]])) < 0.99f) n = false;
                    if (a.NumTexCoords > 1 && b.NumTexCoords > 1 && Vector2.Distance(b.TexCoords[1][b.Indices[i + k]], a.TexCoords[1][a.Indices[j + k]]) > 1e-3f) u = false;
                }
                if (n) normalOk++; if (u) uv1Ok++;
            }
            Console.WriteLine($"  triangles on stock corners: {onStock:N0} of {b.Indices.Length / 3:N0}; normals as stock {normalOk:N0}, UV1 as stock {uv1Ok:N0}");
        }
        if (fresh.Count > 0)
        {
            WindingStats("new", b, fb, fresh);
            Console.WriteLine($"  new: binormal sign +1 {fresh.Count(v => Sign(b.TangentZ[v]) > 0):N0}, -1 {fresh.Count(v => Sign(b.TangentZ[v]) < 0):N0}");
            for (int c = 0; c < b.NumTexCoords; c++)
            {
                var uv = fresh.Select(v => b.TexCoords[c][v]).ToList();
                Console.WriteLine($"  new: uv{c} {uv.Min(p => p.X):0.###}..{uv.Max(p => p.X):0.###}, {uv.Min(p => p.Y):0.###}..{uv.Max(p => p.Y):0.###}");
            }
            // Directions the new faces face (by stored normal), counted.
            var dirs = fresh.GroupBy(v => { var n = Unpack(b.TangentZ[v]); var ab = Vector3.Abs(n); return ab.X >= ab.Y && ab.X >= ab.Z ? (n.X > 0 ? "+x" : "-x") : ab.Y >= ab.Z ? (n.Y > 0 ? "+y" : "-y") : (n.Z > 0 ? "+z" : "-z"); })
                .Select(g => $"{g.Key} {g.Count()}");
            Console.WriteLine($"  new: stored normals face {string.Join(", ", dirs)}");
            var sdirs = Enumerable.Range(0, a.Positions.Length).GroupBy(v => { var n = Unpack(a.TangentZ[v]); var ab = Vector3.Abs(n); return ab.X >= ab.Y && ab.X >= ab.Z ? (n.X > 0 ? "+x" : "-x") : ab.Y >= ab.Z ? (n.Y > 0 ? "+y" : "-y") : (n.Z > 0 ? "+z" : "-z"); })
                .Select(g => $"{g.Key} {g.Count()}");
            Console.WriteLine($"  stock: stored normals face {string.Join(", ", sdirs)}");
        }
        return 0;
    }
}

/// <summary>
/// --mesh-planes &lt;package.upk&gt; &lt;mesh&gt;: the mesh's vertical faces grouped by wall line (the plane they lie in) and the side
/// they face. Faces on one wall line normally all face the same way: a line with faces both ways has a part turned
/// round (a copy facing into a building shows its back from outside). The outward side is taken from the stored normals.
/// </summary>
static class MeshPlanes
{
    public static int Run(string pkgPath, string meshName)
    {
        var p = Package.Open(pkgPath);
        int i = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(meshName, StringComparison.OrdinalIgnoreCase)
            || (e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)));
        if (i < 0) { Console.WriteLine($"No mesh {meshName}."); return 1; }
        var m = StaticMesh.Read(p, p.Exports[i]);
        // Outward = the side the stored normals are on (majority over the triangles), as the 3D view decides it.
        int agree = 0;
        for (int t = 0; t + 2 < m.Indices.Length; t += 3)
        {
            var c = System.Numerics.Vector3.Cross(m.Positions[m.Indices[t + 1]] - m.Positions[m.Indices[t]], m.Positions[m.Indices[t + 2]] - m.Positions[m.Indices[t]]);
            agree += System.Numerics.Vector3.Dot(c, m.Normals[m.Indices[t]]) > 0 ? 1 : -1;
        }
        float sign = agree >= 0 ? 1 : -1;
        var lines = new Dictionary<(char Axis, int At), (float Pos, float Neg, System.Numerics.Vector3 Lo, System.Numerics.Vector3 Hi)>();
        for (int t = 0; t + 2 < m.Indices.Length; t += 3)
        {
            var a = m.Positions[m.Indices[t]]; var b = m.Positions[m.Indices[t + 1]]; var c = m.Positions[m.Indices[t + 2]];
            var n = System.Numerics.Vector3.Cross(b - a, c - a) * sign;
            float area = n.Length() / 2;
            if (area < 1) continue;
            n /= 2 * area;
            char axis = MathF.Abs(n.X) > 0.95f ? 'x' : MathF.Abs(n.Y) > 0.95f ? 'y' : ' ';
            if (axis == ' ') continue;
            var ce = (a + b + c) / 3;
            var key = (axis, (int)MathF.Round((axis == 'x' ? ce.X : ce.Y) / 4));
            var cur = lines.TryGetValue(key, out var v) ? v : (0f, 0f, new System.Numerics.Vector3(float.MaxValue), new System.Numerics.Vector3(float.MinValue));
            bool pos = (axis == 'x' ? n.X : n.Y) > 0;
            lines[key] = (cur.Item1 + (pos ? area : 0), cur.Item2 + (pos ? 0 : area), System.Numerics.Vector3.Min(cur.Item3, ce), System.Numerics.Vector3.Max(cur.Item4, ce));
        }
        Console.WriteLine($"{m.Name}: vertical wall lines (area facing + / -, extent of the faces):");
        foreach (var (k, v) in lines.OrderBy(k => k.Key.Axis).ThenBy(k => k.Key.At))
        {
            if (v.Pos + v.Neg < 500) continue;
            bool mixed = MathF.Min(v.Pos, v.Neg) > 0.1f * (v.Pos + v.Neg);
            Console.WriteLine($"  {k.Axis} = {k.At * 4,6}: +{k.Axis} {v.Pos,9:N0}  -{k.Axis} {v.Neg,9:N0}   x {v.Lo.X:0}..{v.Hi.X:0}, y {v.Lo.Y:0}..{v.Hi.Y:0}, z {v.Lo.Z:0}..{v.Hi.Z:0}{(mixed ? "   <- faces both ways" : "")}");
        }
        return 0;
    }
}
