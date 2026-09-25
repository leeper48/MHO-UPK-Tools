using System.Numerics;

namespace UpkMeshScan;

/// <summary>Engine-space LOD 0 ready to serialize: one shared vertex buffer, sections in the original order.</summary>
public sealed class BuiltMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[][] TexCoords { get; init; }
    public required uint[] TangentX { get; init; }
    public required uint[] TangentZ { get; init; }
    public required ushort[] Indices { get; init; }
    public required ushort[] Adjacency { get; init; }
    public required StaticMeshSection[] Sections { get; init; }
    public required Vector3 BoundsOrigin { get; init; }
    public required Vector3 BoundsExtent { get; init; }
    public required float BoundsRadius { get; init; }
}

/// <summary>
/// Builds a replacement StaticMesh export from imported sections. Every choice here was checked
/// against real data (see the scan and the reference building):
///   - tangent = standard UV-gradient tangent in engine space, orthogonalised to the normal
///     (matches stored TangentX on 32,673/32,673 vertices); TangentX.W = 0x80;
///     TangentZ.W = 0xFF for +1 binormal sign, 0x00 for -1 (100% on the same data).
///   - adjacency: 12 entries per triangle, as every one of 37,107 stock meshes has: the triangle,
///     then per edge the neighbouring triangle's matching edge found by position (else the edge
///     itself), then per corner a "dominant" vertex at the same position (lowest UV0, ties to the
///     highest index — reproduces ~84% of stock dominants; edges reproduce 99.99%).
///   - empty collision tree exactly as stock no-collision meshes store it, sections with
///     EnableCollision = 0.
///   - one fragment per section = (FirstIndex, NumTriangles), as the stock data has.
/// </summary>
static class StaticMeshBuilder
{
    public static BuiltMesh Build(StaticMesh original, List<ImportedSection> imported)
    {
        var byMaterial = imported.ToDictionary(s => s.Material, StringComparer.OrdinalIgnoreCase);
        var unknown = imported.Select(s => s.Material).Where(m => !original.Sections.Any(o => o.MaterialName.Equals(m, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"FBX uses material(s) the mesh doesn't have: {string.Join(", ", unknown)}. Existing: {string.Join(", ", original.Sections.Select(s => s.MaterialName))}");
        var missing = original.Sections.Where(o => !byMaterial.ContainsKey(o.MaterialName) || byMaterial[o.MaterialName].Indices.Count == 0).Select(o => o.MaterialName).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"FBX has no triangles for material(s): {string.Join(", ", missing)} (sections are matched by material name; removing a section isn't supported yet)");

        int channels = original.NumTexCoords;
        var positions = new List<Vector3>(); var normals = new List<Vector3>();
        var uvs = Enumerable.Range(0, channels).Select(_ => new List<Vector2>()).ToArray();
        var indices = new List<int>();
        var sections = new List<StaticMeshSection>();

        foreach (StaticMeshSection o in original.Sections)
        {
            ImportedSection s = byMaterial[o.MaterialName];
            int baseVertex = positions.Count, firstIndex = indices.Count;
            positions.AddRange(s.Positions); normals.AddRange(s.Normals);
            for (int c = 0; c < channels; c++) uvs[c].AddRange(c < s.TexCoords.Length && s.TexCoords[c].Count == s.Positions.Count ? s.TexCoords[c] : Enumerable.Repeat(Vector2.Zero, s.Positions.Count));
            indices.AddRange(s.Indices.Select(i => i + baseVertex));
            sections.Add(o with
            {
                EnableCollision = false,
                FirstIndex = firstIndex,
                NumTriangles = s.Indices.Count / 3,
                MinVertexIndex = baseVertex,
                MaxVertexIndex = positions.Count - 1,
            });
        }

        if (positions.Count > 65535)
            throw new InvalidDataException($"{positions.Count:N0} vertices after welding; this mesh uses 16-bit indices (max 65,535). Reduce the geometry.");

        var P = positions.ToArray(); var N = normals.ToArray();
        var UV = uvs.Select(u => u.ToArray()).ToArray();
        var I = indices.Select(i => (ushort)i).ToArray();
        var (tx, tz) = Tangents(P, N, UV[0], I);

        Vector3 min = P.Aggregate(Vector3.Min), max = P.Aggregate(Vector3.Max);
        Vector3 origin = (min + max) * 0.5f, extent = (max - min) * 0.5f;
        float radius = MathF.Sqrt(P.Max(p => Vector3.DistanceSquared(p, origin)));

        return new BuiltMesh
        {
            Positions = P, Normals = N, TexCoords = UV, TangentX = tx, TangentZ = tz, Indices = I,
            Adjacency = Adjacency(P, UV[0], I), Sections = sections.ToArray(),
            BoundsOrigin = origin, BoundsExtent = extent, BoundsRadius = radius,
        };
    }

    /// <summary>
    /// The original LOD 0 kept exactly (positions, normals, UVs, stored tangents, sections), plus one new section
    /// of extra geometry (engine space, already in the mesh's local space) drawn with <paramref name="materialRef"/>.
    /// New vertices get <paramref name="uv0"/> in channel 0 (zero if not given) and zero in the others; their tangents
    /// are computed. Adjacency and bounds cover everything.
    /// </summary>
    public static BuiltMesh AddSection(StaticMesh original, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<int> indices, int materialRef, string materialName,
        IReadOnlyList<Vector2>? uv0 = null)
    {
        if (uv0 != null && uv0.Count != positions.Count) throw new ArgumentException("uv0 needs one entry per added vertex");
        var newUv = uv0?.ToArray() ?? new Vector2[positions.Count];
        int baseVertex = original.Positions.Length, firstIndex = original.Indices.Length, channels = original.NumTexCoords;
        int total = baseVertex + positions.Count;
        if (total > 65535) throw new InvalidDataException($"{total:N0} vertices with the added geometry; this mesh uses 16-bit indices (max 65,535).");
        var P = original.Positions.Concat(positions).ToArray();
        var N = original.Normals.Concat(normals).ToArray();
        var UV = Enumerable.Range(0, channels).Select(c => original.TexCoords[c].Concat(c == 0 ? newUv : new Vector2[positions.Count]).ToArray()).ToArray();
        var I = original.Indices.Concat(indices.Select(i => (ushort)(i + baseVertex))).ToArray();

        // Original vertices keep their stored tangents; the new part's are computed on its own triangles.
        var localIdx = indices.Select(i => (ushort)i).ToArray();
        var (nx, nz) = Tangents(positions.ToArray(), normals.ToArray(), newUv, localIdx);
        var tx = original.TangentX.Concat(nx).ToArray();
        var tz = original.TangentZ.Concat(nz).ToArray();

        var template = original.Sections[0];
        var added = new StaticMeshSection(materialRef, materialName, false, firstIndex, indices.Count / 3, baseVertex, total - 1,
            template.ShadowCasting, original.Sections.Length, 0);
        var sections = original.Sections.Append(added).ToArray();

        Vector3 min = P.Aggregate(Vector3.Min), max = P.Aggregate(Vector3.Max);
        Vector3 origin = (min + max) * 0.5f, extent = (max - min) * 0.5f;
        float radius = MathF.Sqrt(P.Max(p => Vector3.DistanceSquared(p, origin)));
        return new BuiltMesh
        {
            Positions = P, Normals = N, TexCoords = UV, TangentX = tx, TangentZ = tz, Indices = I,
            Adjacency = Adjacency(P, UV[0], I), Sections = sections,
            BoundsOrigin = origin, BoundsExtent = extent, BoundsRadius = radius,
        };
    }

    /// <summary>
    /// A new single-section mesh from plain geometry (engine space), using <paramref name="template"/> only for its
    /// layout (UV channel count, section flags). UVs are zero; tangents are computed.
    /// </summary>
    public static BuiltMesh BuildGeometry(StaticMesh template, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<int> indices, int materialRef, string materialName)
    {
        if (positions.Count > 65535) throw new InvalidDataException($"{positions.Count:N0} vertices; 16-bit indices allow 65,535.");
        var P = positions.ToArray(); var N = normals.ToArray();
        var UV = Enumerable.Range(0, template.NumTexCoords).Select(_ => new Vector2[P.Length]).ToArray();
        var I = indices.Select(i => (ushort)i).ToArray();
        var (tx, tz) = Tangents(P, N, UV[0], I);
        var section = template.Sections[0] with
        {
            MaterialRef = materialRef, MaterialName = materialName, EnableCollision = false,
            FirstIndex = 0, NumTriangles = I.Length / 3, MinVertexIndex = 0, MaxVertexIndex = P.Length - 1, MaterialIndex = 0, TrailingFlag = 0,
        };
        Vector3 min = P.Aggregate(Vector3.Min), max = P.Aggregate(Vector3.Max);
        Vector3 origin = (min + max) * 0.5f, extent = (max - min) * 0.5f;
        return new BuiltMesh
        {
            Positions = P, Normals = N, TexCoords = UV, TangentX = tx, TangentZ = tz, Indices = I,
            Adjacency = Adjacency(P, UV[0], I), Sections = [section],
            BoundsOrigin = origin, BoundsExtent = extent, BoundsRadius = MathF.Sqrt(P.Max(p => Vector3.DistanceSquared(p, origin))),
        };
    }

    public static (uint[] X, uint[] Z) Tangents(Vector3[] p, Vector3[] n, Vector2[] uv, ushort[] idx)
    {
        var t = new Vector3[p.Length]; var b = new Vector3[p.Length];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
            Vector3 e1 = p[i1] - p[i0], e2 = p[i2] - p[i0];
            float du1 = uv[i1].X - uv[i0].X, dv1 = uv[i1].Y - uv[i0].Y, du2 = uv[i2].X - uv[i0].X, dv2 = uv[i2].Y - uv[i0].Y;
            float det = du1 * dv2 - du2 * dv1;
            if (MathF.Abs(det) < 1e-12f) continue;
            float r = 1f / det;
            Vector3 tt = (e1 * dv2 - e2 * dv1) * r, bb = (e2 * du1 - e1 * du2) * r;
            t[i0] += tt; t[i1] += tt; t[i2] += tt;
            b[i0] += bb; b[i1] += bb; b[i2] += bb;
        }
        var X = new uint[p.Length]; var Z = new uint[p.Length];
        for (int v = 0; v < p.Length; v++)
        {
            Vector3 nn = n[v].LengthSquared() > 1e-12f ? Vector3.Normalize(n[v]) : Vector3.UnitZ;
            Vector3 tt = t[v] - nn * Vector3.Dot(nn, t[v]);
            if (tt.LengthSquared() < 1e-12f)
            {
                tt = Vector3.Cross(nn, MathF.Abs(nn.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
                if (tt.LengthSquared() < 1e-12f) tt = Vector3.UnitX;
            }
            tt = Vector3.Normalize(tt);
            bool positive = Vector3.Dot(Vector3.Cross(nn, tt), b[v]) >= 0f;
            X[v] = Pack(tt, 0x80);
            Z[v] = Pack(nn, positive ? (byte)0xFF : (byte)0x00);
        }
        return (X, Z);
    }

    static uint Pack(Vector3 v, byte w)
    {
        static uint B(float x) => (uint)Math.Clamp((int)(x * 127.5f + 128f), 0, 255);
        return B(v.X) | (B(v.Y) << 8) | (B(v.Z) << 16) | ((uint)w << 24);
    }

    public static ushort[] Adjacency(Vector3[] p, Vector2[] uv, ushort[] idx)
    {
        // Dominant vertex per position: lowest UV0 (u, then v), ties to the highest index.
        var dominantByPos = new Dictionary<Vector3, int>();
        for (int v = 0; v < p.Length; v++)
        {
            if (!dominantByPos.TryGetValue(p[v], out int d)) { dominantByPos[p[v]] = v; continue; }
            if (uv[v].X < uv[d].X || (uv[v].X == uv[d].X && (uv[v].Y < uv[d].Y || uv[v].Y == uv[d].Y))) dominantByPos[p[v]] = v;
        }

        // Directed edges by position -> first triangle edge (a, b) that has them.
        var edges = new Dictionary<(Vector3, Vector3), (int Tri, int A, int B)>();
        int triangles = idx.Length / 3;
        for (int t = 0; t < triangles; t++)
            for (int k = 0; k < 3; k++)
            {
                int a = idx[3 * t + k], b = idx[3 * t + (k + 1) % 3];
                edges.TryAdd((p[a], p[b]), (t, a, b));
            }

        var output = new ushort[triangles * 12];
        for (int t = 0; t < triangles; t++)
        {
            int o = 12 * t;
            for (int k = 0; k < 3; k++)
            {
                int a = idx[3 * t + k], b = idx[3 * t + (k + 1) % 3];
                output[o + k] = (ushort)a;
                if (edges.TryGetValue((p[b], p[a]), out var nb) && nb.Tri != t)
                {
                    // Stored as the neighbour's edge reversed: its vertex at a's position, then at b's.
                    output[o + 3 + 2 * k] = (ushort)nb.B;
                    output[o + 4 + 2 * k] = (ushort)nb.A;
                }
                else
                {
                    output[o + 3 + 2 * k] = (ushort)a;
                    output[o + 4 + 2 * k] = (ushort)b;
                }
                output[o + 9 + k] = (ushort)dominantByPos[p[a]];
            }
        }
        return output;
    }

    /// <summary>Serializes the replacement export: original properties and tail, new bounds/geometry, empty collision tree.</summary>
    public static byte[] Serialize(StaticMesh original, BuiltMesh m, long serialOffset, bool dropBodySetup = false)
    {
        if (original.LodCount != 1) throw new InvalidDataException($"mesh has {original.LodCount} LODs; only single-LOD meshes can be imported so far");
        if (original.HasVertexColors) throw new InvalidDataException("mesh has per-vertex colors; importing would drop them, which isn't supported yet");
        var L = original.Layout;
        byte[] src = L.Original;
        using var ms = new MemoryStream(src.Length * 2);
        using var w = new BinaryWriter(ms);

        w.Write(src, 0, L.BoundsAt);                                        // NetIndex + properties
        Vec(w, m.BoundsOrigin); Vec(w, m.BoundsExtent); w.Write(m.BoundsRadius);
        w.Write(dropBodySetup ? 0 : BitConverter.ToInt32(src, L.BoundsAt + 28));   // BodySetup (kept, or none for new meshes)

        // Empty collision tree, as stock no-collision meshes store it.
        w.Write(float.MaxValue); w.Write(float.MaxValue); w.Write(float.MaxValue);
        w.Write(-float.MaxValue); w.Write(-float.MaxValue); w.Write(-float.MaxValue);
        w.Write(6); w.Write(0);
        w.Write(8); w.Write(0);

        w.Write(src, L.InternalVersionAt, L.Lod0At - L.InternalVersionAt);  // InternalVersion, 4 unknown, LOD count

        // LOD 0: empty raw-triangle bulk data whose offset field points just past itself.
        w.Write(0); w.Write(0); w.Write(0);
        w.Write(checked((int)(serialOffset + ms.Position + 4)));

        w.Write(m.Sections.Length);
        foreach (var s in m.Sections)
        {
            w.Write(s.MaterialRef);
            w.Write(s.EnableCollision ? 1 : 0); w.Write(s.EnableCollision ? 1 : 0);
            w.Write(s.ShadowCasting);
            w.Write(s.FirstIndex); w.Write(s.NumTriangles); w.Write(s.MinVertexIndex); w.Write(s.MaxVertexIndex);
            w.Write(s.MaterialIndex);
            w.Write(1); w.Write(s.FirstIndex); w.Write(s.NumTriangles);   // one fragment
            w.Write(s.TrailingFlag);
        }

        int nv = m.Positions.Length;
        w.Write(12); w.Write(nv); w.Write(12); w.Write(nv);
        foreach (var p in m.Positions) Vec(w, p);

        int channels = m.TexCoords.Length;
        bool full = original.FullPrecisionUVs;                               // keep the original's UV format
        int stride = 8 + channels * (full ? 8 : 4);
        w.Write(channels); w.Write(stride); w.Write(nv); w.Write(full ? 1 : 0);
        w.Write(stride); w.Write(nv);
        for (int v = 0; v < nv; v++)
        {
            w.Write(m.TangentX[v]); w.Write(m.TangentZ[v]);
            for (int c = 0; c < channels; c++)
            {
                if (full) { w.Write(m.TexCoords[c][v].X); w.Write(m.TexCoords[c][v].Y); }
                else { w.Write((Half)m.TexCoords[c][v].X); w.Write((Half)m.TexCoords[c][v].Y); }
            }
        }

        w.Write(original.ColorStride); w.Write(0);                          // no vertex colors
        w.Write(nv);
        w.Write(2); w.Write(m.Indices.Length); foreach (ushort i in m.Indices) w.Write(i);
        w.Write(2); w.Write(0);                                             // wireframe (empty on every stock mesh)
        w.Write(2); w.Write(m.Adjacency.Length); foreach (ushort i in m.Adjacency) w.Write(i);

        w.Write(src, L.Lod0End, src.Length - L.Lod0End);                    // tail, verbatim
        w.Flush();
        return ms.ToArray();
    }

    static void Vec(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
}
