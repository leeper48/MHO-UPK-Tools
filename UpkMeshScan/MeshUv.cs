using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --uv-info: each UV channel's range per section of a StaticMesh (read-only).
/// --scale-uv: multiplies one UV channel of one section's vertices by a factor, in place — e.g. to change how often
/// a material's layer tiles on a sky dome when the tiling is fixed in the compiled shader. Everything else in the
/// mesh (positions, normals, stored tangents, indices, adjacency, other channels, other sections) is kept, and the
/// mesh is re-serialized with StaticMeshBuilder. Same .bak / verified temp / swap as the other writers; verified by
/// re-reading the mesh (UVs are stored as half floats unless the mesh uses full-precision UVs).
/// </summary>
static class MeshUv
{
    static int FindMesh(Package pkg, string name) => Array.FindIndex(pkg.Exports, e => pkg.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
        && (pkg.PathOf(e).Equals(name, StringComparison.OrdinalIgnoreCase) || e.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public static int Info(string upkPath, string meshName)
    {
        var pkg = Package.Open(upkPath);
        int i = FindMesh(pkg, meshName);
        if (i < 0) { Console.WriteLine($"No StaticMesh '{meshName}'."); return 2; }
        var m = StaticMesh.Read(pkg, pkg.Exports[i]);
        Console.WriteLine($"{pkg.PathOf(pkg.Exports[i])}: {m.Positions.Length:N0} verts, {m.NumTexCoords} UV channel(s), {(m.FullPrecisionUVs ? "full-precision" : "half-float")} UVs, vertex colours: {(m.HasVertexColors ? "yes" : "no")}");
        if (m.ColorsBgra is { } cb)
        {
            // Per channel (stored B, G, R, A): range, mean, and how many vertices are 0 / 255 / in between.
            foreach (var (ch, k) in new[] { ("R", 2), ("G", 1), ("B", 0), ("A", 3) })
            {
                var vals = Enumerable.Range(0, cb.Length / 4).Select(v => (int)cb[v * 4 + k]).ToArray();
                Console.WriteLine($"  colour {ch}: {vals.Min()}..{vals.Max()}, mean {vals.Average():0}, zero {vals.Count(x => x == 0):N0}, full {vals.Count(x => x == 255):N0}, between {vals.Count(x => x is > 0 and < 255):N0}");
            }
        }
        for (int s = 0; s < m.Sections.Length; s++)
        {
            var sec = m.Sections[s];
            Console.WriteLine($"  section {s}: verts {sec.MinVertexIndex}..{sec.MaxVertexIndex}, material {sec.MaterialName} (ref {sec.MaterialRef})");
            for (int c = 0; c < m.NumTexCoords; c++)
            {
                var uv = m.TexCoords[c][sec.MinVertexIndex..(sec.MaxVertexIndex + 1)];
                Console.WriteLine($"    uv{c}: u {uv.Min(t => t.X):0.###}..{uv.Max(t => t.X):0.###}, v {uv.Min(t => t.Y):0.###}..{uv.Max(t => t.Y):0.###}");
            }
            // How v relates to height (e.g. a sky dome: which rows of a panorama land at the horizon): uv0 in 10 v-bands,
            // each with its vertices' mean elevation angle seen from the bounds centre.
            var idx = Enumerable.Range(sec.MinVertexIndex, sec.MaxVertexIndex - sec.MinVertexIndex + 1).ToList();
            var ctr = idx.Aggregate(Vector3.Zero, (a, v) => a + m.Positions[v]) / idx.Count;
            double El(int v) { var d = m.Positions[v] - ctr; return Math.Atan2(d.Z, Math.Sqrt(d.X * d.X + d.Y * d.Y)) * 180 / Math.PI; }
            var lo = idx.Aggregate(m.Positions[idx[0]], (a, v) => Vector3.Min(a, m.Positions[v])); var hi = idx.Aggregate(m.Positions[idx[0]], (a, v) => Vector3.Max(a, m.Positions[v]));
            Console.WriteLine($"    section centre {ctr.X:0.#}, {ctr.Y:0.#}, {ctr.Z:0.#}; bounds {lo.X:0.#},{lo.Y:0.#},{lo.Z:0.#} .. {hi.X:0.#},{hi.Y:0.#},{hi.Z:0.#} (mesh units)");
            // Seen from the mesh-space origin (where the level is, for a component at the origin): elevation of the uv0 v bands.
            double El0(int v) { var d = m.Positions[v]; return Math.Atan2(d.Z, Math.Sqrt(d.X * d.X + d.Y * d.Y)) * 180 / Math.PI; }
            for (int b = 0; b < 10; b++)
            {
                var vs = idx.Where(v => m.TexCoords[0][v].Y >= b / 10f && m.TexCoords[0][v].Y < (b + 1) / 10f + (b == 9 ? 0.01f : 0)).ToList();
                if (vs.Count > 0) Console.WriteLine($"    from origin: uv0 v {b / 10f:0.0}..{(b + 1) / 10f:0.0}: elevation {vs.Min(El0),6:0.0}..{vs.Max(El0),6:0.0}°");
            }
            foreach (var (axis, get) in new (string, Func<int, float>)[] { ("u", v => m.TexCoords[0][v].X), ("v", v => m.TexCoords[0][v].Y) })
                for (int b = 0; b < 10; b++)
                {
                    var vs = idx.Where(v => get(v) >= b / 10f && get(v) < (b + 1) / 10f + (b == 9 ? 0.01f : 0)).ToList();
                    if (vs.Count == 0) continue;
                    Console.WriteLine($"    uv0 {axis} {b / 10f:0.0}..{(b + 1) / 10f:0.0}: {vs.Count,5} verts, elevation {vs.Min(El),6:0.0}..{vs.Max(El),6:0.0}° (mean {vs.Average(El):0.0})");
                }
        }
        return 0;
    }

    public static int Scale(string upkPath, string meshName, int channel, Vector2 factor, int section, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        int index = FindMesh(pkg, meshName);
        if (index < 0) { Console.WriteLine($"No StaticMesh '{meshName}'."); return 2; }
        var m = StaticMesh.Read(pkg, pkg.Exports[index]);
        Console.WriteLine($"Scale UV: {pkg.PathOf(pkg.Exports[index])} uv{channel} x({factor.X}, {factor.Y}) (section {section}){(dryRun ? "  [dry run]" : "")}");
        if (channel < 0 || channel >= m.NumTexCoords) { Console.WriteLine($"  the mesh has {m.NumTexCoords} UV channel(s)"); return 2; }
        if (section < 0 || section >= m.Sections.Length) { Console.WriteLine($"  the mesh has {m.Sections.Length} section(s)"); return 2; }
        var sec = m.Sections[section];
        // Only this section's vertices; they must not be shared with another section.
        for (int s = 0; s < m.Sections.Length; s++)
            if (s != section && m.Sections[s].MinVertexIndex <= sec.MaxVertexIndex && m.Sections[s].MaxVertexIndex >= sec.MinVertexIndex)
            { Console.WriteLine($"  section {s} shares vertices with section {section}; not supported"); return 1; }

        var uv = m.TexCoords.Select(c => c.ToArray()).ToArray();
        for (int v = sec.MinVertexIndex; v <= sec.MaxVertexIndex; v++) uv[channel][v] *= factor;
        var built = new BuiltMesh
        {
            Positions = m.Positions, Normals = m.Normals, TexCoords = uv, TangentX = m.TangentX, TangentZ = m.TangentZ,
            Indices = m.Indices, Adjacency = m.Adjacency, Sections = m.Sections,
            BoundsOrigin = m.BoundsOrigin, BoundsExtent = m.BoundsExtent, BoundsRadius = m.BoundsRadius,
        };
        var replace = new Dictionary<int, Func<long, byte[]>> { [index] = off => StaticMeshBuilder.Serialize(m, built, off) };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);

        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [index], [], written);
            var w = Package.FromBytes(bytes);
            var e = w.Exports[index];
            var r = StaticMesh.Parse(w, e.ObjectName, w.ReadExportBytes(e), e.SerialOffset);
            if (!r.Positions.SequenceEqual(m.Positions) || !r.Indices.SequenceEqual(m.Indices)) problems.Add("geometry changed");
            if (!r.TangentX.SequenceEqual(m.TangentX) || !r.TangentZ.SequenceEqual(m.TangentZ)) problems.Add("tangents changed");
            if (r.Sections.Length != m.Sections.Length) problems.Add("sections changed");
            for (int c = 0; c < m.NumTexCoords; c++)
                for (int v = 0; v < m.Positions.Length; v++)
                {
                    Vector2 want = uv[c][v], got = r.TexCoords[c][v];
                    float tol = m.FullPrecisionUVs ? 1e-6f : MathF.Max(1e-3f, MathF.Abs(want.X) * 2e-3f + MathF.Abs(want.Y) * 2e-3f);
                    bool touched = c == channel && v >= sec.MinVertexIndex && v <= sec.MaxVertexIndex;
                    if (touched ? Vector2.Distance(want, got) > tol : got != m.TexCoords[c][v])
                    { problems.Add($"uv{c} vertex {v}: {got}, expected {want}"); c = m.NumTexCoords; break; }
                }
            return problems;
        }
        var problems = Check(output);
        var before = m.TexCoords[channel][sec.MinVertexIndex..(sec.MaxVertexIndex + 1)];
        var after = uv[channel][sec.MinVertexIndex..(sec.MaxVertexIndex + 1)];
        Console.WriteLine($"  uv{channel} of {sec.MaxVertexIndex - sec.MinVertexIndex + 1:N0} verts: u {before.Min(t => t.X):0.###}..{before.Max(t => t.X):0.###} -> {after.Min(t => t.X):0.###}..{after.Max(t => t.X):0.###}, v {before.Min(t => t.Y):0.###}..{before.Max(t => t.Y):0.###} -> {after.Min(t => t.Y):0.###}..{after.Max(t => t.Y):0.###}");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.Take(10).ToList().ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (only that channel of that section changed; geometry, tangents, other channels and every other export identical)");
        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(target, output);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }
}
