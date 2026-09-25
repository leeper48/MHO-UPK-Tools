using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --sky-coverage: which view directions a placed sky/backdrop mesh covers, seen from a point (default: the centre of
/// the level's other placed static meshes). Casts a ray per azimuth/elevation bin against the mesh as the component
/// places it (translation, rotation, scale) and prints a map: '#' = the ray hits the mesh, '.' = nothing (the sky shows
/// the clear colour, i.e. black). Read-only; for skies that look fine on one side and black on the other.
/// </summary>
static class SkyCoverage
{
    /// <param name="componentPath">one component path, several separated by commas (their union is mapped), or a
    /// path ending in '*' (every component whose path starts with the rest).</param>
    public static int Run(string upkPath, string componentPath, Vector3? from)
    {
        var pkg = Package.Open(upkPath);
        var wanted = componentPath.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var comps = Enumerable.Range(0, pkg.Exports.Length).Where(i => wanted.Any(p => p.EndsWith('*')
            ? pkg.PathOf(pkg.Exports[i]).StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
            : pkg.PathOf(pkg.Exports[i]).Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (comps.Count == 0) { Console.WriteLine($"No export '{componentPath}'."); return 2; }
        int ci = comps[0];
        var c = ComponentTransform.Read(pkg, pkg.ReadExportBytes(pkg.Exports[ci]));
        if (c == null || c.MeshRef <= 0) { Console.WriteLine("Not a static mesh component with a mesh in this package."); return 2; }
        var mesh = StaticMesh.Read(pkg, pkg.Exports[c.MeshRef - 1]);
        // Every matching component's triangles, each placed by its own transform.
        var wl = new List<Vector3>(); var il = new List<int>();
        foreach (int k in comps)
        {
            var ck = ComponentTransform.Read(pkg, pkg.ReadExportBytes(pkg.Exports[k]));
            if (ck == null || ck.MeshRef <= 0) continue;
            var mk = StaticMesh.Read(pkg, pkg.Exports[ck.MeshRef - 1]);
            var rk = ZonePlaceholders.RotatorMatrix(ck.Rotation);
            int b = wl.Count;
            wl.AddRange(mk.Positions.Select(v => Vector3.Transform(v * ck.Scale, rk) + ck.Translation));
            il.AddRange(mk.Indices.Select(i => i + b));
        }
        var w = wl.ToArray();
        var idx = il.ToArray();
        if (comps.Count > 1) Console.WriteLine($"{comps.Count} components (union):");

        // Viewpoint: the centre of every other placed mesh's translation.
        Vector3 eye = from ?? Vector3.Zero;
        if (from == null)
        {
            var others = new List<Vector3>();
            for (int i = 0; i < pkg.Exports.Length; i++)
                if (i != ci && pkg.ClassOf(pkg.Exports[i]).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)
                    && ComponentTransform.Read(pkg, pkg.ReadExportBytes(pkg.Exports[i])) is { } o && o.Translation != Vector3.Zero)
                    others.Add(o.Translation);
            var mine = comps.Select(k => ComponentTransform.Read(pkg, pkg.ReadExportBytes(pkg.Exports[k]))?.Translation).ToHashSet();
            others.RemoveAll(o => mine.Contains(o));
            if (others.Count > 0) eye = others.Aggregate(Vector3.Zero, (a, b) => a + b) / others.Count;
        }
        Vector3 min = w.Aggregate(Vector3.Min), max = w.Aggregate(Vector3.Max);
        Console.WriteLine($"{pkg.PathOf(pkg.Exports[c.MeshRef - 1])}: {w.Length} verts, {mesh.Indices.Length / 3} tris, {mesh.Sections.Length} section(s)");
        Console.WriteLine($"  placed: translation {c.Translation}, rotation (units) {c.Rotation}, scale {c.Scale}");
        Console.WriteLine($"  world bounds ({min.X:0}, {min.Y:0}, {min.Z:0}) .. ({max.X:0}, {max.Y:0}, {max.Z:0})");
        Console.WriteLine($"  viewpoint ({eye.X:0}, {eye.Y:0}, {eye.Z:0}){(from == null ? " (centre of the level's other placed meshes)" : "")}");
        Console.WriteLine("  coverage: rows = elevation (+90 top .. -90 bottom), columns = azimuth 0..350 in 10-degree steps (0 = +X, 90 = +Y); # = mesh, . = nothing");
        Console.WriteLine("            " + string.Concat(Enumerable.Range(0, 36).Select(a => a % 9 == 0 ? (a * 10).ToString().PadRight(9) : "")));
        int hits = 0, total = 0;
        for (int el = 90; el >= -90; el -= 15)
        {
            var row = new System.Text.StringBuilder();
            for (int az = 0; az < 360; az += 10)
            {
                float a = az * MathF.PI / 180f, e = el * MathF.PI / 180f;
                var dir = new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e));
                bool hit = false;
                for (int t = 0; t + 2 < idx.Length && !hit; t += 3)
                    hit = Ray(eye, dir, w[idx[t]], w[idx[t + 1]], w[idx[t + 2]]);
                row.Append(hit ? '#' : '.'); total++; if (hit) hits++;
            }
            Console.WriteLine($"  {el,4}deg   {row}");
        }
        Console.WriteLine($"  {hits}/{total} directions covered ({100.0 * hits / total:0}%)");
        return 0;
    }

    /// <summary>Möller–Trumbore, both faces, hits in front of the eye.</summary>
    static bool Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1 / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return false;
        return Vector3.Dot(e2, q) * inv > 0;
    }
}
