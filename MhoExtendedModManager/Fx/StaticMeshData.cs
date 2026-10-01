using System.Numerics;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// LOD 0 of a cooked StaticMesh (v868/L3), for the 3D View's mesh effects (shockwave rings, debris: particle emitters with a
/// mesh type). Ported from the MHO Hero Creator's Fx/StaticMeshData.cs (2026-09-30), written from the layout MHO Package Modifier's
/// StaticMesh.cs confirmed on all 37,107 static meshes of the game: NetIndex + tagged properties; bounds (origin, extent,
/// radius), BodySetup; kDOP bounds, nodes, triangles (bulk arrays); InternalVersion + 4 ints; LOD count; LOD 0: raw bulk
/// data (flags, count, size, offset + the bytes), sections (material ref, collision ×2, shadow, first index, triangles,
/// min / max vertex, material index, fragments (8 bytes each), a byte), positions (stride 12, count, bulk), UVs
/// (NumTexCoords, stride, count, full precision, bulk: TangentX, TangentZ, UVs half or float), colors (stride, count, bulk
/// when count > 0), NumVertices, indices (bulk of uint16).
/// </summary>
sealed class StaticMeshData
{
    public Vector3[] Positions = [];
    public Vector3[] Normals = [];
    public Vector2[] Uvs = [];
    public ushort[] Indices = [];
    public List<(int MaterialRef, int First, int Triangles)> Sections = new();

    public static StaticMeshData? Read(FxPkg p, int export)
    {
        try { return Parse(p, export); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException) { return null; }
    }

    static StaticMeshData Parse(FxPkg p, int export)
    {
        var e = p.T.Exports[export];
        byte[] b = p.Bytes;
        int pos = e.SerialOffset + 4, end = e.SerialOffset + e.SerialSize;
        // Tagged properties up to None.
        while (true)
        {
            string name = p.T.Names[BitConverter.ToInt32(b, pos)].Text;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { pos += 8; break; }
            string type = p.T.Names[BitConverter.ToInt32(b, pos + 8)].Text.ToLowerInvariant();
            int size = BitConverter.ToInt32(b, pos + 16);
            pos += 24;
            if (type is "structproperty" or "byteproperty") pos += 8;
            if (type == "boolproperty") pos += 1;
            pos += size;
            if (pos > end) throw new InvalidDataException("properties run past the export");
        }
        int I32() { int x = BitConverter.ToInt32(b, pos); pos += 4; return x; }
        void Bulk() { int es = I32(), n = I32(); if (es < 0 || n < 0 || (long)es * n > end - pos) throw new InvalidDataException("bulk array"); pos += es * n; }
        pos += 28 + 4;              // bounds (origin, extent, radius), BodySetup
        pos += 24; Bulk(); Bulk();  // kDOP bounds, nodes, triangles
        pos += 4 + 16;              // InternalVersion, 4 ints
        int lods = I32();
        if (lods < 1 || lods > 16) throw new InvalidDataException("LOD count " + lods);
        pos += 8; int raw = I32(); pos += 4; pos += raw;
        var m = new StaticMeshData();
        int sections = I32();
        if (sections < 0 || sections > 4096) throw new InvalidDataException("sections " + sections);
        for (int s = 0; s < sections; s++)
        {
            int mat = I32(); pos += 12; int first = I32(), tris = I32(); pos += 12;
            int frags = I32(); pos += frags * 8 + 1;
            m.Sections.Add((mat, first, tris));
        }
        int stride = I32(), verts = I32(); int pes = I32(), pn = I32();
        if (stride != 12 || pes != 12 || pn != verts) throw new InvalidDataException("positions");
        m.Positions = new Vector3[verts];
        for (int v = 0; v < verts; v++) { m.Positions[v] = new Vector3(BitConverter.ToSingle(b, pos), BitConverter.ToSingle(b, pos + 4), BitConverter.ToSingle(b, pos + 8)); pos += 12; }
        int tc = I32(), uvStride = I32(), uvVerts = I32(); bool full = I32() != 0; int ues = I32(), un = I32();
        if (tc < 1 || un != verts || ues != uvStride) throw new InvalidDataException("uvs");
        m.Normals = new Vector3[verts]; m.Uvs = new Vector2[verts];
        for (int v = 0; v < verts; v++)
        {
            int at = pos + v * uvStride;
            var n = new Vector3(b[at + 4] / 127.5f - 1, b[at + 5] / 127.5f - 1, b[at + 6] / 127.5f - 1);
            m.Normals[v] = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitZ;
            m.Uvs[v] = full ? new Vector2(BitConverter.ToSingle(b, at + 8), BitConverter.ToSingle(b, at + 12)) : new Vector2((float)BitConverter.ToHalf(b, at + 8), (float)BitConverter.ToHalf(b, at + 10));
        }
        pos += verts * uvStride;
        int cs = I32(), cn = I32(); if (cn > 0) Bulk();
        pos += 4;                   // NumVertices
        int ies = I32(), idx = I32();
        if (ies != 2 || idx < 0 || idx * 2 > end - pos) throw new InvalidDataException("indices");
        m.Indices = new ushort[idx];
        for (int i = 0; i < idx; i++) { m.Indices[i] = BitConverter.ToUInt16(b, pos); pos += 2; }
        return m;
    }
}
