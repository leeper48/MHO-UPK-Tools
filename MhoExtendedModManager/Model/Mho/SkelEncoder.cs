using System.Buffers.Binary;
using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>Geometry for the encoder: one material's triangles, in MHO model space, winding as MHO's (clockwise).</summary>
sealed class EncSection
{
    public int Material;                         // index into the mesh's material list
    public required Vector3[] Pos;
    public required Vector3[] Normal;
    public Vector4[]? Tangent;                   // xyz + handedness (±1); null = computed from the UVs
    public required Vector2[] Uv;                // top-left origin (as stored in the vertex buffer)
    public required int[] Tris;
    public required (int Bone, float Weight)[][] Weights;   // skeleton bone indices
}

/// <summary>
/// Builds SkeletalMesh native data from new geometry on top of a base mesh's (the MHO hero's). Rules, all measured on
/// stock meshes (902 of 902 re-encode byte for byte with SkelNative; `--skel-dump` for the relations):
/// one LOD; one chunk per section (the section's chunk index = its own); ≤ 75 bones per chunk (the stock maximum: the
/// GPU skinning limit), a section needing more is split; in each chunk the single-weight (rigid) vertices first, then
/// the rest (soft), counts stored; active bones = the chunks' bone maps in first-appearance order; required bones 0…N−1;
/// size 0; 32-byte vertices: tangent X (xyz packed, w 0x80), tangent Z = normal (w = handedness, 0xFF or 0x00), 4 chunk-local
/// bone indices, 4 weight bytes (sum 255), position 3 floats, UV 2 halves; packed-position extension / origin 1 1 1 / 0 0 0;
/// index width 2 below 65 536 vertices; bounds from the vertex box: X / Y half-extents doubled, Z from 5 % of the height
/// below the lowest vertex to 50 % above the highest, radius = |extent|. Bones: the base mesh's records with only
/// rotation and position replaced (same names, hierarchy, flags, child counts). Name map, tail, origins, raw point
/// indices and index container: copied from the base mesh.
/// </summary>
static class SkelEncoder
{
    public const int MaxBonesPerChunk = 75;

    /// <summary>Handedness convention: tangent Z w byte = 0xFF when (N × T) · B_uv has this sign (B_uv = direction of
    /// increasing V in the stored UVs). Set from the stock re-encode test.</summary>
    public static int HandednessSignForFF = +1;

    /// <param name="extraBones">Bones after the base mesh's (borrowed hair, 0.15.0): each one's name reference (name table
    /// index | number &lt;&lt; 32) and parent; their rotation / position are the last entries of <paramref name="boneLocals"/>.
    /// A new record copies its parent's flags and colour, counts no children until one hangs on it, and the parent's child
    /// count grows; the name map gets an entry each; the skeletal depth is recounted.</param>
    public static SkelNative Build(SkelNative baseMesh, IReadOnlyList<(Quaternion Rot, Vector3 Pos)> boneLocals, IReadOnlyList<EncSection> sections, IReadOnlyList<int> materials,
        IReadOnlyList<(long Name, int Parent)>? extraBones = null)
    {
        if (baseMesh.Lods.Count != 1) throw new InvalidDataException("the base mesh has more than one LOD");
        int extra = extraBones?.Count ?? 0;
        if (boneLocals.Count != baseMesh.Bones.Count + extra) throw new InvalidDataException($"{boneLocals.Count} bones for a base mesh with {baseMesh.Bones.Count} and {extra} added");
        var bl = baseMesh.Lods[0];
        var n = new SkelNative
        {
            Materials = materials.ToList(), Origins = baseMesh.Origins, SkeletalDepth = baseMesh.SkeletalDepth,
            NameIndexMap = baseMesh.NameIndexMap.ToList(), Tail = baseMesh.Tail,
        };
        for (int i = 0; i < baseMesh.Bones.Count; i++)
        {
            var b = (byte[])baseMesh.Bones[i].Clone();
            var (q, p) = boneLocals[i];
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), q.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), q.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), q.Z); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), q.W);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28), p.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), p.Z);
            n.Bones.Add(b);
        }
        for (int k = 0; k < extra; k++)
        {
            var (name, parent) = extraBones![k];
            int i = n.Bones.Count;
            if (parent < 0 || parent >= i) throw new InvalidDataException($"added bone {k}: parent {parent} isn't an earlier bone");
            var b = new byte[52];
            BinaryPrimitives.WriteInt64LittleEndian(b, name);
            n.Bones[parent].AsSpan(8, 4).CopyTo(b.AsSpan(8));      // flags as its parent's
            n.Bones[parent].AsSpan(48, 4).CopyTo(b.AsSpan(48));    // colour as its parent's
            var (q, p) = boneLocals[i];
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), q.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16), q.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(20), q.Z); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), q.W);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28), p.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(32), p.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(36), p.Z);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), 0);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(44), parent);
            var pb = n.Bones[parent];
            BinaryPrimitives.WriteInt32LittleEndian(pb.AsSpan(40), BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(40)) + 1);
            n.Bones.Add(b);
            n.NameIndexMap.Add((name, i));
        }
        if (extra > 0) n.SkeletalDepth = Depth(n.Bones);

        var lod = new SkelNative.Lod
        {
            IndexCpu = bl.IndexCpu, RawPointIndices = bl.RawPointIndices, UvSets = 1,
            VbUvSets = 1, VbFullUv = 0, VbPacked = 1, VbExtOrigin = PackFloats(1, 1, 1, 0, 0, 0), VbElementSize = 32,
            InfluenceSets = BitConverter.GetBytes(0), ContainerCpu = bl.ContainerCpu, ContainerWidth = bl.ContainerWidth,
            ContainerElementSize = bl.ContainerElementSize, ContainerCount = 0, ContainerData = [],
        };
        var vb = new MemoryStream(); var vw = new BinaryWriter(vb);
        var indices = new List<int>();
        int vertexBase = 0;
        var active = new List<ushort>(); var activeSet = new HashSet<ushort>();
        var all = new List<Vector3>();

        foreach (var sec in sections)
        {
            var tan = sec.Tangent ?? Tangents(sec);
            foreach (var chunkTris in SplitByBones(sec))
            {
                // Chunk vertices: the ones its triangles use; rigid (one influence) first, then soft.
                // In vertex-index order (the stock cook's: with it, a stock mesh re-encodes byte for byte).
                var used = chunkTris.Distinct().OrderBy(v => v).ToList();
                var ordered = used.Where(v => Influences(sec, v).Count == 1).Concat(used.Where(v => Influences(sec, v).Count != 1)).ToList();
                var local = new Dictionary<int, int>();
                for (int k = 0; k < ordered.Count; k++) local[ordered[k]] = vertexBase + k;
                var boneMap = new List<ushort>(); var boneLocal = new Dictionary<int, int>();
                foreach (int v in used)   // bone map: first appearance walking the vertices by index (hypothesis, tested)
                    foreach (var (b, _) in Influences(sec, v))
                        if (!boneLocal.ContainsKey(b)) { boneLocal[b] = boneMap.Count; boneMap.Add((ushort)b); }
                foreach (var b in boneMap) if (activeSet.Add(b)) active.Add(b);
                int rigid = ordered.Count(v => Influences(sec, v).Count == 1);
                var chunk = new SkelNative.Chunk
                {
                    BaseVertex = (uint)vertexBase, BoneMap = boneMap, NumRigid = rigid, NumSoft = ordered.Count - rigid,
                    MaxInfluences = ordered.Count == 0 ? 1 : ordered.Max(v => Influences(sec, v).Count),
                };
                lod.Sections.Add(new SkelNative.Section
                {
                    Material = (ushort)sec.Material, Chunk = (ushort)lod.Chunks.Count, BaseIndex = (uint)indices.Count,
                    Triangles = (uint)(chunkTris.Count / 3), Sort = 0,
                });
                lod.Chunks.Add(chunk);
                foreach (int v in chunkTris) indices.Add(local[v]);
                foreach (int v in ordered)
                {
                    var inf = Influences(sec, v);
                    var t = tan[v];
                    vw.Write(PackDir(new Vector3(t.X, t.Y, t.Z), 0x80));
                    vw.Write(PackDir(sec.Normal[v], t.W * HandednessSignForFF >= 0 ? (byte)0xFF : (byte)0x00));
                    for (int k = 0; k < 4; k++) vw.Write(k < inf.Count ? (byte)boneLocal[inf[k].Bone] : (byte)0);
                    foreach (var q in Quantize(inf.Select(x => x.Weight).ToList())) vw.Write(q);
                    vw.Write(sec.Pos[v].X); vw.Write(sec.Pos[v].Y); vw.Write(sec.Pos[v].Z);
                    vw.Write((Half)sec.Uv[v].X); vw.Write((Half)sec.Uv[v].Y);
                    all.Add(sec.Pos[v]);
                }
                vertexBase += ordered.Count;
            }
        }
        lod.Indices = indices;
        lod.IndexWidth = vertexBase < 65536 ? (byte)2 : (byte)4; lod.IndexDeclared = lod.IndexWidth;
        lod.ActiveBones = active;
        lod.Size = 0; lod.VertexCount = (uint)vertexBase;
        lod.RequiredBones = Enumerable.Range(0, n.Bones.Count).Select(i => (byte)i).ToList();
        lod.VbCount = vertexBase; lod.VbData = vb.ToArray();
        n.Lods.Add(lod);
        n.Bounds = BoundsOf(all);
        return n;
    }

    /// <summary>The longest root-to-leaf chain, counting bones (the root alone = 1; checked against stock meshes by
    /// --skel-encode-test's depth line).</summary>
    public static int Depth(IReadOnlyList<byte[]> bones)
    {
        int best = 0;
        for (int i = 0; i < bones.Count; i++)
        {
            int d = 1;
            for (int k = i, g = 0; g < 4096; g++) { int p = BinaryPrimitives.ReadInt32LittleEndian(bones[k].AsSpan(44)); if (p == k || p < 0) break; d++; k = p; }
            best = Math.Max(best, d);
        }
        return best;
    }

    /// <summary>The stock bounds rule (measured on Punisher and Storm): X / Y half-extents doubled, Z from 5 % of the height
    /// below the lowest vertex to 50 % above the highest.</summary>
    public static byte[] BoundsOf(List<Vector3> pts)
    {
        var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
        foreach (var p in pts) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        float h = mx.Z - mn.Z;
        float zlo = mn.Z - 0.05f * h, zhi = mx.Z + 0.5f * h;
        var origin = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (zlo + zhi) / 2);
        var ext = new Vector3(mx.X - mn.X, mx.Y - mn.Y, (zhi - zlo) / 2);
        return PackFloats(origin.X, origin.Y, origin.Z, ext.X, ext.Y, ext.Z, ext.Length());
    }

    static List<(int Bone, float Weight)> Influences(EncSection s, int v) =>
        s.Weights[v].Where(w => w.Weight > 0).OrderByDescending(w => w.Weight).Take(4).ToList();

    /// <summary>Weights to bytes summing to 255 (largest remainder), in the given order.</summary>
    static byte[] Quantize(List<float> w)
    {
        var q = new byte[4];
        if (w.Count == 0) { q[0] = 255; return q; }
        float sum = w.Sum();
        var raw = w.Select(x => x / sum * 255f).ToList();
        var fl = raw.Select(x => (int)MathF.Floor(x)).ToList();
        int left = 255 - fl.Sum();
        foreach (int k in Enumerable.Range(0, raw.Count).OrderByDescending(k => raw[k] - fl[k]).Take(left)) fl[k]++;
        for (int k = 0; k < fl.Count; k++) q[k] = (byte)fl[k];
        return q;
    }

    /// <summary>Splits a section's triangles into runs whose vertices use at most 75 bones.</summary>
    static IEnumerable<List<int>> SplitByBones(EncSection s)
    {
        var cur = new List<int>(); var bones = new HashSet<int>();
        for (int t = 0; t + 2 < s.Tris.Length; t += 3)
        {
            var tb = new HashSet<int>();
            for (int k = 0; k < 3; k++) foreach (var (b, _) in Influences(s, s.Tris[t + k])) tb.Add(b);
            if (bones.Union(tb).Count() > MaxBonesPerChunk && cur.Count > 0) { yield return cur; cur = new(); bones = new(); }
            bones.UnionWith(tb);
            cur.Add(s.Tris[t]); cur.Add(s.Tris[t + 1]); cur.Add(s.Tris[t + 2]);
        }
        if (cur.Count > 0) yield return cur;
    }

    /// <summary>Per-vertex tangents from positions and UVs (summed per triangle, Gram-Schmidt against the normal), with the
    /// handedness w = sign((N × T) · B_uv), B_uv = direction of increasing V.</summary>
    public static Vector4[] Tangents(EncSection s)
    {
        int n = s.Pos.Length;
        var t = new Vector3[n]; var b = new Vector3[n];
        for (int i = 0; i + 2 < s.Tris.Length; i += 3)
        {
            int a = s.Tris[i], c = s.Tris[i + 1], d = s.Tris[i + 2];
            var e1 = s.Pos[c] - s.Pos[a]; var e2 = s.Pos[d] - s.Pos[a];
            var u1 = s.Uv[c] - s.Uv[a]; var u2 = s.Uv[d] - s.Uv[a];
            float r = u1.X * u2.Y - u2.X * u1.Y;
            if (MathF.Abs(r) < 1e-12f) continue;
            r = 1 / r;
            var sdir = (e1 * u2.Y - e2 * u1.Y) * r;
            var tdir = (e2 * u1.X - e1 * u2.X) * r;
            t[a] += sdir; t[c] += sdir; t[d] += sdir;
            b[a] += tdir; b[c] += tdir; b[d] += tdir;
        }
        var result = new Vector4[n];
        for (int v = 0; v < n; v++)
        {
            var nn = s.Normal[v];
            var tt = t[v] - nn * Vector3.Dot(nn, t[v]);
            if (tt.LengthSquared() < 1e-12f) tt = Vector3.Cross(nn, MathF.Abs(nn.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
            tt = Vector3.Normalize(tt);
            float w = Vector3.Dot(Vector3.Cross(nn, tt), b[v]) < 0 ? -1 : 1;
            result[v] = new Vector4(tt, w);
        }
        return result;
    }

    public static uint PackDir(Vector3 v, byte w)
    {
        static byte B(float x) => (byte)Math.Clamp((int)MathF.Round((x + 1) * 127.5f), 0, 255);
        return (uint)(B(v.X) | (B(v.Y) << 8) | (B(v.Z) << 16) | (w << 24));
    }

    public static Vector3 UnpackDir(uint p) => new(((p & 0xFF) / 127.5f) - 1, (((p >> 8) & 0xFF) / 127.5f) - 1, (((p >> 16) & 0xFF) / 127.5f) - 1);

    static byte[] PackFloats(params float[] f) { var b = new byte[f.Length * 4]; for (int i = 0; i < f.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), f[i]); return b; }

    /// <summary>Decodes an LOD's vertices (for tests and for reading the base mesh): per vertex position, normal, tangent (w
    /// from the byte: +1 for 0xFF), UV, and skeleton-bone weights. raw: normals / tangents exactly as stored (not normalized:
    /// some stock ones aren't unit length), so they pack back to the same bytes.</summary>
    public static (Vector3[] Pos, Vector3[] Normal, Vector4[] Tangent, Vector2[] Uv, (int, float)[][] Weights, byte[] TanW) Decode(SkelNative.Lod l, bool raw = false)
    {
        int n = l.VbCount, st = l.VbElementSize;
        var pos = new Vector3[n]; var nor = new Vector3[n]; var tan = new Vector4[n]; var uv = new Vector2[n]; var w = new (int, float)[n][]; var tw = new byte[n];
        foreach (var c in l.Chunks)
            for (int v = (int)c.BaseVertex; v < c.BaseVertex + c.NumRigid + c.NumSoft; v++)
            {
                int o = v * st;
                uint tx = BinaryPrimitives.ReadUInt32LittleEndian(l.VbData.AsSpan(o)), tz = BinaryPrimitives.ReadUInt32LittleEndian(l.VbData.AsSpan(o + 4));
                tw[v] = (byte)(tz >> 24);
                nor[v] = raw ? UnpackDir(tz) : Vector3.Normalize(UnpackDir(tz));
                tan[v] = new Vector4(raw ? UnpackDir(tx) : Vector3.Normalize(UnpackDir(tx)), tw[v] == 0xFF ? 1 : -1);
                var inf = new List<(int, float)>();
                for (int k = 0; k < 4; k++) { byte wt = l.VbData[o + 12 + k]; if (wt > 0) inf.Add((c.BoneMap[l.VbData[o + 8 + k]], wt / 255f)); }
                w[v] = inf.ToArray();
                pos[v] = new Vector3(BitConverter.ToSingle(l.VbData, o + 16), BitConverter.ToSingle(l.VbData, o + 20), BitConverter.ToSingle(l.VbData, o + 24));
                uv[v] = new Vector2((float)BitConverter.ToHalf(l.VbData, o + 28), (float)BitConverter.ToHalf(l.VbData, o + 30));
            }
        return (pos, nor, tan, uv, w, tw);
    }
}
