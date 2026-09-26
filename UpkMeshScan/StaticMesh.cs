using System.Numerics;

namespace UpkMeshScan;

public sealed record StaticMeshSection(int MaterialRef, string MaterialName, bool EnableCollision, int FirstIndex, int NumTriangles, int MinVertexIndex, int MaxVertexIndex,
    int ShadowCasting = 1, int MaterialIndex = 0, byte TrailingFlag = 0);

/// <summary>
/// LOD 0 geometry of a cooked StaticMesh export (v868/L3). Layout worked out from real bytes
/// (nyc_midtown_bldg_b_buildinga_a in SCS__OpDailyBugleRegionBand_SF.upk), then run over all
/// 37,107 StaticMesh exports in the game folder: all pass the hard checks (buffer headers agree,
/// index count = section triangles x 3, every index inside its section's Min/MaxVertexIndex).
/// Softer cross-checks (collision tree vs sections, bounds, bulk-data self-offset) only add notes,
/// because real meshes legitimately differ there.
/// </summary>
public sealed class StaticMesh
{
    public required string Name { get; init; }
    public required int InternalVersion { get; init; }
    public required int LodCount { get; init; }
    public required int NumTexCoords { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[][] TexCoords { get; init; }   // [channel][vertex]
    public required ushort[] Indices { get; init; }
    public required StaticMeshSection[] Sections { get; init; }
    /// <summary>Soft cross-check mismatches that didn't stop the parse.</summary>
    public required List<string> Notes { get; init; }
    /// <summary>Layout facts for the folder-wide statistics (not problems).</summary>
    public required List<string> Facts { get; init; }
    public required uint[] TangentX { get; init; }          // packed, as stored
    public required uint[] TangentZ { get; init; }          // packed, as stored (W = binormal sign)
    public required int ColorStride { get; init; }
    public required StaticMeshLayout Layout { get; init; }
    public required bool FullPrecisionUVs { get; init; }
    public required bool HasVertexColors { get; init; }
    /// <summary>Vertex colours as stored (B, G, R, A per vertex), or null: e.g. the paint of vertex-blended terrain.</summary>
    public byte[]? ColorsBgra { get; init; }
    public required ushort[] Adjacency { get; init; }
    public required int KdopTriangleCount { get; init; }
    public required Vector3 BoundsOrigin { get; init; }
    public required Vector3 BoundsExtent { get; init; }
    public required float BoundsRadius { get; init; }

    /// <summary>Just the stored bounds (origin, box extent) of a StaticMesh export, without decoding its geometry.</summary>
    public static (Vector3 Origin, Vector3 Extent)? ReadBounds(Package pkg, ExportEntry export)
    {
        try
        {
            byte[] d = pkg.ReadExportBytes(export);
            var r = new Cursor(d);
            r.I32();
            while (true)
            {
                string name = r.Name(pkg);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
                string type = r.Name(pkg).ToLowerInvariant();
                int size = r.I32(); r.I32();
                if (type is "structproperty" or "byteproperty") r.Name(pkg);
                if (type == "boolproperty") r.Skip(1);
                r.Skip(size);
            }
            return (r.Vec3(), r.Vec3());
        }
        catch (PackageFormatException) { return null; }
    }

    public static StaticMesh Read(Package pkg, ExportEntry export) =>
        Parse(pkg, export.ObjectName, pkg.ReadExportBytes(export), export.SerialOffset);

    /// <summary>Parses StaticMesh export bytes. <paramref name="serialOffset"/> is where they sit in the (uncompressed) package.</summary>
    public static StaticMesh Parse(Package pkg, string exportName, byte[] d, long serialOffset)
    {
        var notes = new List<string>();
        var facts = new List<string>();
        var r = new Cursor(d);

        // NetIndex + tagged properties up to "None".
        r.I32();
        while (true)
        {
            string name = r.Name(pkg);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            string type = r.Name(pkg).ToLowerInvariant();
            int size = r.I32(); r.I32();
            if (type is "structproperty" or "byteproperty") r.Name(pkg);
            if (type == "boolproperty") r.Skip(1);
            r.Skip(size);
        }

        // Bounds: Origin, BoxExtent, SphereRadius. Then BodySetup ref.
        int boundsAt = r.Pos;
        Vector3 boundsOrigin = r.Vec3(), boundsExtent = r.Vec3();
        float boundsRadius = r.F32();
        r.I32();

        // kDOP tree: root bound (min xyz, max xyz), nodes, triangles.
        Vector3 kMin = r.Vec3(), kMax = r.Vec3();
        r.SkipBulkArray();
        int kdopTriangles = r.BulkArrayHeader(out int kdopTriSize);
        r.Skip(kdopTriangles * kdopTriSize);

        int internalVersionAt = r.Pos;
        int internalVersion = r.I32();
        // Four unknown int32s. All zero on 37,103 of 37,107 meshes; the other 4 (all copies of
        // savjngle_rocks_c) have the third one = 1. Not tied to LOD count: 29 meshes have 2+ LODs
        // with all four zero. LOD 0 decodes fine either way.
        var unknown = new[] { r.I32(), r.I32(), r.I32(), r.I32() };
        if (unknown.Any(x => x != 0)) notes.Add($"unknown fields after InternalVersion = [{string.Join(", ", unknown)}]");

        int lodCount = r.I32();
        if (lodCount < 1 || lodCount > 16) Fail(exportName, $"implausible LOD count {lodCount}");
        if (lodCount > 1) notes.Add($"{lodCount} LODs (only LOD 0 is read)");

        // LOD 0. Leading bulk data (raw triangles, stripped when cooked): flags, count, size, file offset.
        int bulkAt = r.Pos;
        r.I32(); r.I32(); int rawSize = r.I32(); int rawOffset = r.I32();
        long expectedOffset = serialOffset + bulkAt + 16;
        if (rawOffset != expectedOffset)
            notes.Add($"LOD bulk-data offset 0x{rawOffset:X} doesn't point at itself (0x{expectedOffset:X}); so far only seen in packages modified after the 2024-03-14 stock date");
        r.Skip(rawSize);

        int sectionsAt = r.Pos;
        int sectionCount = r.I32();
        if (sectionCount < 0 || sectionCount > 4096) Fail(exportName, $"implausible section count {sectionCount}");
        var sections = new StaticMeshSection[sectionCount];
        for (int s = 0; s < sectionCount; s++)
        {
            int material = r.I32();
            bool collision = r.I32() != 0;
            r.I32();                                    // OldEnableCollision
            int shadowCasting = r.I32();                // bEnableShadowCasting
            int firstIndex = r.I32(), numTriangles = r.I32(), minVertex = r.I32(), maxVertex = r.I32();
            int materialIndex = r.I32();
            int fragments = r.I32();
            if (fragments < 0 || fragments > 65536) Fail(exportName, $"section {s}: implausible fragment count {fragments}");
            r.Skip(fragments * 8);                      // FFragmentRange: BaseIndex, NumPrimitives
            byte flag = r.U8();                         // one-byte flag, always 0 so far
            sections[s] = new StaticMeshSection(material, pkg.RefName(material), collision, firstIndex, numTriangles, minVertex, maxVertex,
                shadowCasting, materialIndex, flag);
        }

        // Sections are identified by material name on export/import. Meshes cooked into level packages
        // have no section materials (ref 0; the placed components supply them), and two sections can share
        // a material, so those get a positional name instead. Section order is preserved either way,
        // because components' Materials overrides are applied by section index.
        for (int s = 0; s < sectionCount; s++)
        {
            var sec = sections[s];
            bool shared = sections.Count(o => o.MaterialName.Equals(sec.MaterialName, StringComparison.OrdinalIgnoreCase)) > 1;
            if (sec.MaterialRef == 0) sections[s] = sec with { MaterialName = $"section{s}" };
            else if (shared) sections[s] = sec with { MaterialName = $"{sec.MaterialName}_section{s}" };
        }

        // Position buffer: stride, count, then bulk array of float3.
        int posStride = r.I32(), numVerts = r.I32();
        int posCount = r.BulkArrayHeader(out int posElem);
        if (posStride != 12 || posElem != 12 || posCount != numVerts) Fail(exportName, $"position buffer header {posStride}/{numVerts}/{posElem}/{posCount}");
        var positions = new Vector3[numVerts];
        for (int v = 0; v < numVerts; v++) positions[v] = r.Vec3();

        // UV/tangent buffer: NumTexCoords, Stride, NumVertices, bUseFullPrecisionUVs, bulk array.
        int numTexCoords = r.I32(), uvStride = r.I32(), uvVerts = r.I32(); bool fullUVs = r.I32() != 0;
        int uvCount = r.BulkArrayHeader(out int uvElem);
        int expectedStride = 8 + numTexCoords * (fullUVs ? 8 : 4);
        if (numTexCoords < 1 || numTexCoords > 8 || uvStride != expectedStride || uvElem != uvStride || uvVerts != numVerts || uvCount != numVerts)
            Fail(exportName, $"UV buffer header tc={numTexCoords} stride={uvStride} verts={uvVerts} full={fullUVs} elem={uvElem} count={uvCount}");
        var normals = new Vector3[numVerts];
        var tangentX = new uint[numVerts];
        var tangentZ = new uint[numVerts];
        var uvs = new Vector2[numTexCoords][];
        for (int c = 0; c < numTexCoords; c++) uvs[c] = new Vector2[numVerts];
        for (int v = 0; v < numVerts; v++)
        {
            tangentX[v] = BitConverter.ToUInt32(d, r.Pos);
            r.Skip(4);                                  // TangentX
            tangentZ[v] = BitConverter.ToUInt32(d, r.Pos);
            normals[v] = r.PackedNormal();              // TangentZ
            for (int c = 0; c < numTexCoords; c++)
                uvs[c][v] = fullUVs ? new Vector2(r.F32(), r.F32()) : new Vector2(r.Half(), r.Half());
        }

        // Color buffer: stride, count, and data only when count > 0.
        int colorStride = r.I32(), colorVerts = r.I32();
        byte[]? colors = null;                                   // FColor per vertex, as stored: B, G, R, A
        if (colorVerts > 0)
        {
            int n = r.BulkArrayHeader(out int e);
            if (e == 4) { colors = new byte[n * 4]; for (int k = 0; k < colors.Length; k++) colors[k] = r.U8(); }
            else r.Skip(n * e);
        }
        if (colorVerts != 0 && colorVerts != numVerts) Fail(exportName, $"color buffer has {colorVerts} verts (stride {colorStride}), mesh has {numVerts}");

        if (r.I32() != numVerts) Fail(exportName, "NumVertices after the vertex buffers doesn't match");

        int indexCount = r.BulkArrayHeader(out int indexElem);
        if (indexElem != 2) Fail(exportName, $"index element size {indexElem} (only 16-bit seen)");
        var indices = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++) indices[i] = r.U16();

        // Wireframe indices, then adjacency indices (12 per triangle when present). LOD 0 ends there;
        // what follows (for single-LOD meshes) is copied verbatim when rebuilding: it starts with an
        // int32 then what looks like ThumbnailAngle (rotator) and ThumbnailDistance.
        int wireframeCount = r.BulkArrayHeader(out int wireElem); r.Skip(wireframeCount * wireElem);
        int adjacencyCount = r.BulkArrayHeader(out int adjElem);
        if (adjElem != 2 && adjacencyCount > 0) Fail(exportName, $"adjacency element size {adjElem}");
        var adjacency = new ushort[adjacencyCount];
        for (int i = 0; i < adjacencyCount; i++) adjacency[i] = r.U16();
        int lod0End = r.Pos;

        facts.Add($"wireframe: {(wireframeCount == 0 ? "empty" : wireframeCount == indexCount * 2 ? "2 per index" : "other")}");
        facts.Add($"adjacency: {(adjacencyCount == 0 ? "empty" : adjacencyCount == indexCount * 4 ? "12 per triangle" : "other")} (elem {adjElem})");
        if (lodCount == 1 && d.Length - lod0End >= 4) facts.Add($"tail first int32: {BitConverter.ToInt32(d, lod0End)}");
        facts.Add($"color buffer: stride {colorStride}, {(colorVerts == 0 ? "empty" : "per-vertex")}");
        facts.Add($"UVs: {numTexCoords} channel(s), {(fullUVs ? "float" : "half")}");
        facts.Add($"LODs: {lodCount}");
        foreach (byte w in tangentZ.Select(t => (byte)(t >> 24)).Distinct().Order().Take(6)) facts.Add($"TangentZ.W byte 0x{w:X2}");
        foreach (byte w in tangentX.Select(t => (byte)(t >> 24)).Distinct().Order().Take(6)) facts.Add($"TangentX.W byte 0x{w:X2}");
        if (lodCount == 1) facts.Add($"tail after LOD 0: {d.Length - lod0End} bytes");

        // Cross-checks against independently stored data.
        int triTotal = sections.Sum(s => s.NumTriangles);
        if (triTotal * 3 != indexCount) Fail(exportName, $"sections hold {triTotal} triangles but the index buffer has {indexCount} indices");
        int collisionTotal = sections.Where(s => s.EnableCollision).Sum(s => s.NumTriangles);
        // The collision tree usually holds exactly the collision-enabled sections' triangles (1,888 of 2,105
        // mismatches in the full scan), but ~200 meshes carry separate, larger collision geometry — so this
        // is a note, not a layout check. The index checks below are the real ones.
        if (kdopTriangles != collisionTotal)
            notes.Add($"collision tree has {kdopTriangles} triangles; collision-enabled sections hold {collisionTotal}");
        foreach (var s in sections)
        {
            if (s.FirstIndex < 0 || s.FirstIndex + s.NumTriangles * 3 > indexCount) Fail(exportName, "section index range outside the index buffer");
            for (int i = s.FirstIndex; i < s.FirstIndex + s.NumTriangles * 3; i++)
                if (indices[i] < s.MinVertexIndex || indices[i] > s.MaxVertexIndex) Fail(exportName, $"index {indices[i]} outside its section's vertex range {s.MinVertexIndex}-{s.MaxVertexIndex}");
        }
        if (numVerts > 0 && kdopTriangles > 0)   // an empty collision tree stores ±FLT_MAX bounds
        {
            Vector3 pMin = positions.Aggregate(Vector3.Min), pMax = positions.Aggregate(Vector3.Max);
            if (Vector3.Distance(pMin, kMin) > 1f || Vector3.Distance(pMax, kMax) > 1f)
                notes.Add($"vertex bounds {pMin}–{pMax} differ from collision bounds {kMin}–{kMax}");
        }

        return new StaticMesh
        {
            Name = exportName, InternalVersion = internalVersion, LodCount = lodCount, NumTexCoords = numTexCoords,
            Positions = positions, Normals = normals, TexCoords = uvs, Indices = indices, Sections = sections, Notes = notes, Facts = facts,
            TangentX = tangentX, TangentZ = tangentZ, ColorStride = colorStride, FullPrecisionUVs = fullUVs, HasVertexColors = colorVerts > 0, ColorsBgra = colors,
            Adjacency = adjacency, KdopTriangleCount = kdopTriangles,
            BoundsOrigin = boundsOrigin, BoundsExtent = boundsExtent, BoundsRadius = boundsRadius,
            Layout = new StaticMeshLayout(boundsAt, internalVersionAt, bulkAt, sectionsAt, lod0End, d),
        };
    }

    /// <summary>Where the pieces sit inside the export's bytes, for rebuilding it.</summary>
    public sealed record StaticMeshLayout(int BoundsAt, int InternalVersionAt, int Lod0At, int SectionsAt, int Lod0End, byte[] Original);

    static void Fail(string name, string why) =>
        throw new PackageFormatException($"StaticMesh '{name}': {why} — layout differs from the one confirmed so far");

    sealed class Cursor(byte[] d)
    {
        public int Pos;
        void Need(int n) { if (n < 0 || Pos + n > d.Length) throw new PackageFormatException($"read past end of export at 0x{Pos:X}"); }
        public int I32() { Need(4); int x = BitConverter.ToInt32(d, Pos); Pos += 4; return x; }
        public byte U8() { Need(1); return d[Pos++]; }
        public ushort U16() { Need(2); ushort x = BitConverter.ToUInt16(d, Pos); Pos += 2; return x; }
        public float F32() { Need(4); float x = BitConverter.ToSingle(d, Pos); Pos += 4; return x; }
        public float Half() { Need(2); float x = (float)BitConverter.ToHalf(d, Pos); Pos += 2; return x; }
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public void Skip(int n) { Need(n); Pos += n; }
        public Vector3 PackedNormal()
        {
            Need(4);
            var n = new Vector3(d[Pos] / 127.5f - 1f, d[Pos + 1] / 127.5f - 1f, d[Pos + 2] / 127.5f - 1f);
            Pos += 4;
            return n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;
        }
        public string Name(Package pkg)
        {
            int idx = I32(), num = I32();
            if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range at 0x{Pos - 8:X}");
            return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
        }
        /// <summary>UE3 bulk-serialized TArray: element size, count. Returns count.</summary>
        public int BulkArrayHeader(out int elementSize)
        {
            elementSize = I32(); int count = I32();
            if (elementSize < 0 || count < 0 || (long)elementSize * count > d.Length - Pos) throw new PackageFormatException($"bad bulk array ({elementSize} x {count}) at 0x{Pos - 8:X}");
            return count;
        }
        public void SkipBulkArray() { int n = BulkArrayHeader(out int e); Skip(n * e); }
    }
}
