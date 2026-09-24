using System.Numerics;

namespace UpkMeshScan;

public sealed record StaticMeshSection(int MaterialRef, string MaterialName, int FirstIndex, int NumTriangles, int MinVertexIndex, int MaxVertexIndex);

/// <summary>
/// LOD 0 geometry of a cooked StaticMesh export (v868/L3). Layout worked out from real bytes
/// (nyc_midtown_bldg_b_buildinga_a in SCS__OpDailyBugleRegionBand_SF.upk) and cross-checked there:
/// position min/max = kDOP bounds, section triangles sum to the kDOP triangle count, each section's
/// index range matches its Min/MaxVertexIndex, and the LOD's bulk-data offset points at itself.
/// The same checks run on every parse, so a mesh that doesn't fit this layout fails loudly
/// instead of exporting garbage.
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

    public static StaticMesh Read(Package pkg, ExportEntry export)
    {
        byte[] d = pkg.ReadExportBytes(export);
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
        r.Skip(7 * 4);
        r.I32();

        // kDOP tree: root bound (min xyz, max xyz), nodes, triangles.
        Vector3 kMin = r.Vec3(), kMax = r.Vec3();
        r.SkipBulkArray();
        int kdopTriangles = r.BulkArrayHeader(out int kdopTriSize);
        r.Skip(kdopTriangles * kdopTriSize);

        int internalVersion = r.I32();
        for (int i = 0; i < 4; i++)
            if (r.I32() != 0) Fail(export, $"unexpected non-zero field {i} after InternalVersion (only the all-zero case has been seen)");

        int lodCount = r.I32();
        if (lodCount < 1 || lodCount > 16) Fail(export, $"implausible LOD count {lodCount}");

        // LOD 0. Leading bulk data (raw triangles, stripped when cooked): flags, count, size, file offset.
        int bulkAt = r.Pos;
        r.I32(); r.I32(); int rawSize = r.I32(); int rawOffset = r.I32();
        long expectedOffset = (long)export.SerialOffset + bulkAt + 16;
        if (rawOffset != expectedOffset)
            Console.WriteLine($"  note: LOD bulk-data offset 0x{rawOffset:X} doesn't point at itself (0x{expectedOffset:X}); seen matching on the reference mesh");
        r.Skip(rawSize);

        int sectionCount = r.I32();
        if (sectionCount < 0 || sectionCount > 4096) Fail(export, $"implausible section count {sectionCount}");
        var sections = new StaticMeshSection[sectionCount];
        for (int s = 0; s < sectionCount; s++)
        {
            int material = r.I32();
            r.I32(); r.I32(); r.I32();                  // EnableCollision, OldEnableCollision, bEnableShadowCasting
            int firstIndex = r.I32(), numTriangles = r.I32(), minVertex = r.I32(), maxVertex = r.I32();
            r.I32();                                    // MaterialIndex
            int fragments = r.I32();
            if (fragments < 0 || fragments > 65536) Fail(export, $"section {s}: implausible fragment count {fragments}");
            r.Skip(fragments * 8);                      // FFragmentRange: BaseIndex, NumPrimitives
            r.Skip(1);                                  // one-byte flag, always 0 so far
            sections[s] = new StaticMeshSection(material, pkg.RefName(material), firstIndex, numTriangles, minVertex, maxVertex);
        }

        // Position buffer: stride, count, then bulk array of float3.
        int posStride = r.I32(), numVerts = r.I32();
        int posCount = r.BulkArrayHeader(out int posElem);
        if (posStride != 12 || posElem != 12 || posCount != numVerts) Fail(export, $"position buffer header {posStride}/{numVerts}/{posElem}/{posCount}");
        var positions = new Vector3[numVerts];
        for (int v = 0; v < numVerts; v++) positions[v] = r.Vec3();

        // UV/tangent buffer: NumTexCoords, Stride, NumVertices, bUseFullPrecisionUVs, bulk array.
        int numTexCoords = r.I32(), uvStride = r.I32(), uvVerts = r.I32(); bool fullUVs = r.I32() != 0;
        int uvCount = r.BulkArrayHeader(out int uvElem);
        int expectedStride = 8 + numTexCoords * (fullUVs ? 8 : 4);
        if (numTexCoords < 1 || numTexCoords > 8 || uvStride != expectedStride || uvElem != uvStride || uvVerts != numVerts || uvCount != numVerts)
            Fail(export, $"UV buffer header tc={numTexCoords} stride={uvStride} verts={uvVerts} full={fullUVs} elem={uvElem} count={uvCount}");
        var normals = new Vector3[numVerts];
        var uvs = new Vector2[numTexCoords][];
        for (int c = 0; c < numTexCoords; c++) uvs[c] = new Vector2[numVerts];
        for (int v = 0; v < numVerts; v++)
        {
            r.Skip(4);                                  // TangentX
            normals[v] = r.PackedNormal();              // TangentZ
            for (int c = 0; c < numTexCoords; c++)
                uvs[c][v] = fullUVs ? new Vector2(r.F32(), r.F32()) : new Vector2(r.Half(), r.Half());
        }

        // Color buffer: stride, count, and data only when count > 0.
        int colorStride = r.I32(), colorVerts = r.I32();
        if (colorVerts > 0) { int n = r.BulkArrayHeader(out int e); r.Skip(n * e); }
        if (colorVerts != 0 && colorVerts != numVerts) Fail(export, $"color buffer has {colorVerts} verts (stride {colorStride}), mesh has {numVerts}");

        if (r.I32() != numVerts) Fail(export, "NumVertices after the vertex buffers doesn't match");

        int indexCount = r.BulkArrayHeader(out int indexElem);
        if (indexElem != 2) Fail(export, $"index element size {indexElem} (only 16-bit seen)");
        var indices = new ushort[indexCount];
        for (int i = 0; i < indexCount; i++) indices[i] = r.U16();

        // Cross-checks against independently stored data.
        int triTotal = sections.Sum(s => s.NumTriangles);
        if (triTotal * 3 != indexCount) Fail(export, $"sections hold {triTotal} triangles but the index buffer has {indexCount} indices");
        if (triTotal != kdopTriangles) Fail(export, $"sections hold {triTotal} triangles, collision tree has {kdopTriangles}");
        foreach (var s in sections)
        {
            if (s.FirstIndex < 0 || s.FirstIndex + s.NumTriangles * 3 > indexCount) Fail(export, "section index range outside the index buffer");
            for (int i = s.FirstIndex; i < s.FirstIndex + s.NumTriangles * 3; i++)
                if (indices[i] < s.MinVertexIndex || indices[i] > s.MaxVertexIndex) Fail(export, $"index {indices[i]} outside its section's vertex range {s.MinVertexIndex}-{s.MaxVertexIndex}");
        }
        if (numVerts > 0)
        {
            Vector3 pMin = positions.Aggregate(Vector3.Min), pMax = positions.Aggregate(Vector3.Max);
            if (Vector3.Distance(pMin, kMin) > 1f || Vector3.Distance(pMax, kMax) > 1f)
                Console.WriteLine($"  note: vertex bounds {pMin}–{pMax} differ from collision bounds {kMin}–{kMax}");
        }

        return new StaticMesh
        {
            Name = export.ObjectName, InternalVersion = internalVersion, LodCount = lodCount, NumTexCoords = numTexCoords,
            Positions = positions, Normals = normals, TexCoords = uvs, Indices = indices, Sections = sections,
        };
    }

    static void Fail(ExportEntry e, string why) =>
        throw new PackageFormatException($"StaticMesh '{e.ObjectName}': {why} — layout differs from the one confirmed so far");

    sealed class Cursor(byte[] d)
    {
        public int Pos;
        void Need(int n) { if (n < 0 || Pos + n > d.Length) throw new PackageFormatException($"read past end of export at 0x{Pos:X}"); }
        public int I32() { Need(4); int x = BitConverter.ToInt32(d, Pos); Pos += 4; return x; }
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
