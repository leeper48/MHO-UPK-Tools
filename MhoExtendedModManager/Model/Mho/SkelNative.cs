using System.Buffers.Binary;

namespace MhoMffImporter;

/// <summary>
/// A SkeletalMesh export's native data (everything after its properties), parsed completely so it can be written back
/// byte for byte (the Phase 3 ground truth) and then built from new geometry. The layout is the one AnimExportCli's
/// SkeletalMeshReader and the Mod Manager's MeshCopy worked out on real packages; this keeps every field instead of
/// skipping the parts a reader doesn't need. Opaque but length-known pieces are kept as raw bytes.
/// </summary>
sealed class SkelNative
{
    public byte[] Bounds = [];                    // 28
    public List<int> Materials = new();           // object references
    public byte[] Origins = [];                   // origin + rotation origin, 24
    public List<byte[]> Bones = new();            // 52 each: name (8), flags, quat (16), pos (12), child count, parent, colour
    public int SkeletalDepth;
    public List<Lod> Lods = new();
    public List<(long Name, int Index)> NameIndexMap = new();   // name reference (index + number) → bone
    public byte[] Tail = [];                      // the 44 bytes after the name map

    public sealed class Section { public ushort Material, Chunk; public uint BaseIndex, Triangles; public byte Sort; }
    public sealed class Chunk
    {
        public uint BaseVertex;
        public int RigidCount, SoftCount;          // legacy per-chunk vertex arrays (61 / 68 bytes each)
        public byte[] RigidData = [], SoftData = [];
        public List<ushort> BoneMap = new();
        public int NumRigid, NumSoft, MaxInfluences;
    }
    public sealed class Bulk { public uint Flags; public int Count, SizeOnDisk, Offset; public byte[] Payload = []; }
    public sealed class Lod
    {
        public List<Section> Sections = new();
        public uint IndexCpu; public byte IndexWidth; public int IndexDeclared; public List<int> Indices = new();
        public List<ushort> ActiveBones = new();
        public List<Chunk> Chunks = new();
        public int Size; public uint VertexCount;
        public List<byte> RequiredBones = new();
        public Bulk RawPointIndices = new();
        public uint UvSets;
        public uint VbUvSets, VbFullUv, VbPacked; public byte[] VbExtOrigin = []; public int VbElementSize, VbCount; public byte[] VbData = [];
        public int ColourElementSize, ColourCount; public byte[]? ColourData;   // only when bHasVertexColors
        public byte[] InfluenceSets = [];          // raw: count + sets (empty on every mesh seen so far is count 0)
        public int InfluenceSetCount;
        public uint ContainerCpu; public byte ContainerWidth; public int ContainerElementSize, ContainerCount; public byte[] ContainerData = [];
    }

    // ---------------------------------------------------------------- reading

    sealed class R
    {
        readonly byte[] d; public int P;
        public R(byte[] d, int p) { this.d = d; P = p; }
        public int Left => d.Length - P;
        void Need(int n, string what) { if (n < 0 || P + n > d.Length) throw new InvalidDataException($"{what}: needs {n} bytes at {P}, {Left} left"); }
        public int I32(string w = "int") { Need(4, w); int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(P)); P += 4; return v; }
        public uint U32(string w = "uint") { Need(4, w); uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(P)); P += 4; return v; }
        public ushort U16(string w = "ushort") { Need(2, w); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(P)); P += 2; return v; }
        public byte U8(string w = "byte") { Need(1, w); return d[P++]; }
        public long I64(string w = "name") { Need(8, w); long v = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(P)); P += 8; return v; }
        public byte[] Bytes(int n, string w) { Need(n, w); var b = d.AsSpan(P, n).ToArray(); P += n; return b; }
        public int Count(int elem, string w, int max = 10_000_000) { int c = I32(w); if (c < 0 || c > max || (long)c * elem > Left) throw new InvalidDataException($"{w}: count {c} (× {elem}) doesn't fit ({Left} left)"); return c; }
    }

    /// <summary>Parses the native data starting at <paramref name="at"/> (the properties' end) to the end of the export.</summary>
    public static SkelNative Read(byte[] d, int at, bool hasVertexColours)
    {
        var r = new R(d, at);
        var n = new SkelNative { Bounds = r.Bytes(28, "bounds") };
        int mc = r.Count(4, "materials", 1024);
        for (int i = 0; i < mc; i++) n.Materials.Add(r.I32());
        n.Origins = r.Bytes(24, "origins");
        int bc = r.Count(52, "bones", 4096);
        for (int i = 0; i < bc; i++) n.Bones.Add(r.Bytes(52, "bone"));
        n.SkeletalDepth = r.I32("skeletal depth");
        int lc = r.Count(1, "LODs", 16);
        for (int l = 0; l < lc; l++) n.Lods.Add(ReadLod(r, hasVertexColours));
        int nm = r.Count(12, "name index map", 4096);
        for (int i = 0; i < nm; i++) n.NameIndexMap.Add((r.I64(), r.I32()));
        n.Tail = r.Bytes(r.Left, "tail");
        return n;
    }

    static Lod ReadLod(R r, bool colours)
    {
        var l = new Lod();
        int sc = r.Count(13, "sections", 1024);
        for (int i = 0; i < sc; i++) l.Sections.Add(new Section { Material = r.U16(), Chunk = r.U16(), BaseIndex = r.U32(), Triangles = r.U32(), Sort = r.U8() });
        l.IndexCpu = r.U32(); l.IndexWidth = r.U8(); l.IndexDeclared = r.I32();
        int ic = r.Count(l.IndexWidth, "indices");
        if (l.IndexWidth is not (2 or 4) || l.IndexDeclared != l.IndexWidth) throw new InvalidDataException($"index widths {l.IndexWidth} / {l.IndexDeclared}");
        for (int i = 0; i < ic; i++) l.Indices.Add(l.IndexWidth == 2 ? r.U16() : r.I32());
        int ab = r.Count(2, "active bones", 4096);
        for (int i = 0; i < ab; i++) l.ActiveBones.Add(r.U16());
        int cc = r.Count(4, "chunks", 1024);
        for (int i = 0; i < cc; i++)
        {
            var c = new Chunk { BaseVertex = r.U32() };
            c.RigidCount = r.Count(61, "rigid vertices"); c.RigidData = r.Bytes(c.RigidCount * 61, "rigid");
            c.SoftCount = r.Count(68, "soft vertices"); c.SoftData = r.Bytes(c.SoftCount * 68, "soft");
            int bm = r.Count(2, "chunk bone map", 4096);
            for (int b = 0; b < bm; b++) c.BoneMap.Add(r.U16());
            c.NumRigid = r.I32(); c.NumSoft = r.I32(); c.MaxInfluences = r.I32();
            l.Chunks.Add(c);
        }
        l.Size = r.I32("size"); l.VertexCount = r.U32("vertex count");
        int rb = r.Count(1, "required bones", 4096);
        for (int i = 0; i < rb; i++) l.RequiredBones.Add(r.U8());
        l.RawPointIndices = ReadBulk(r);
        l.UvSets = r.U32("uv sets");
        l.VbUvSets = r.U32(); l.VbFullUv = r.U32(); l.VbPacked = r.U32(); l.VbExtOrigin = r.Bytes(24, "vb extension / origin");
        l.VbElementSize = r.I32("vertex size"); l.VbCount = r.Count(Math.Max(1, l.VbElementSize), "vertices");
        l.VbData = r.Bytes(l.VbElementSize * l.VbCount, "vertex data");
        if (colours) { l.ColourElementSize = r.I32(); l.ColourCount = r.Count(Math.Max(1, l.ColourElementSize), "colours"); l.ColourData = r.Bytes(l.ColourElementSize * l.ColourCount, "colour data"); }
        int start = r.P;
        l.InfluenceSetCount = r.Count(1, "influence sets", 1024);
        for (int i = 0; i < l.InfluenceSetCount; i++) SkipInfluenceSet(r);
        l.InfluenceSets = new byte[r.P - start];
        r.P = start; l.InfluenceSets = r.Bytes(l.InfluenceSets.Length, "influence sets");
        l.ContainerCpu = r.U32(); l.ContainerWidth = r.U8(); l.ContainerElementSize = r.I32(); l.ContainerCount = r.Count(Math.Max(1, l.ContainerElementSize), "container");
        l.ContainerData = r.Bytes(l.ContainerElementSize * l.ContainerCount, "container data");
        return l;
    }

    static Bulk ReadBulk(R r)
    {
        var b = new Bulk { Flags = r.U32(), Count = r.I32(), SizeOnDisk = r.I32(), Offset = r.I32() };
        if ((b.Flags & (0x01 | 0x20)) == 0 && b.SizeOnDisk > 0) b.Payload = r.Bytes(b.SizeOnDisk, "bulk payload");
        return b;
    }

    static void SkipInfluenceSet(R r)
    {
        int infl = r.Count(8, "influences"); r.P += infl * 8;
        int maps = r.Count(12, "influence map");
        for (int m = 0; m < maps; m++) { r.P += 8; int k = r.Count(4, "map list"); r.P += k * 4; }
        int secs = r.Count(13, "sections"); r.P += secs * 13;
        int cc = r.Count(4, "chunks");
        for (int i = 0; i < cc; i++)
        {
            r.P += 4;
            int a = r.Count(61, "rigid"); r.P += a * 61;
            int b = r.Count(68, "soft"); r.P += b * 68;
            int bm = r.Count(2, "bone map"); r.P += bm * 2;
            r.P += 12;
        }
        int x = r.Count(1, "flags"); r.P += x;
        r.P += 1;
    }

    // ---------------------------------------------------------------- writing

    public byte[] Write()
    {
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Bounds);
        w.Write(Materials.Count); foreach (var m in Materials) w.Write(m);
        w.Write(Origins);
        w.Write(Bones.Count); foreach (var b in Bones) w.Write(b);
        w.Write(SkeletalDepth);
        w.Write(Lods.Count);
        foreach (var l in Lods)
        {
            w.Write(l.Sections.Count);
            foreach (var s in l.Sections) { w.Write(s.Material); w.Write(s.Chunk); w.Write(s.BaseIndex); w.Write(s.Triangles); w.Write(s.Sort); }
            w.Write(l.IndexCpu); w.Write(l.IndexWidth); w.Write(l.IndexDeclared); w.Write(l.Indices.Count);
            foreach (var i in l.Indices) { if (l.IndexWidth == 2) w.Write((ushort)i); else w.Write(i); }
            w.Write(l.ActiveBones.Count); foreach (var b in l.ActiveBones) w.Write(b);
            w.Write(l.Chunks.Count);
            foreach (var c in l.Chunks)
            {
                w.Write(c.BaseVertex);
                w.Write(c.RigidCount); w.Write(c.RigidData);
                w.Write(c.SoftCount); w.Write(c.SoftData);
                w.Write(c.BoneMap.Count); foreach (var b in c.BoneMap) w.Write(b);
                w.Write(c.NumRigid); w.Write(c.NumSoft); w.Write(c.MaxInfluences);
            }
            w.Write(l.Size); w.Write(l.VertexCount);
            w.Write(l.RequiredBones.Count); foreach (var b in l.RequiredBones) w.Write(b);
            w.Write(l.RawPointIndices.Flags); w.Write(l.RawPointIndices.Count); w.Write(l.RawPointIndices.SizeOnDisk); w.Write(l.RawPointIndices.Offset); w.Write(l.RawPointIndices.Payload);
            w.Write(l.UvSets);
            w.Write(l.VbUvSets); w.Write(l.VbFullUv); w.Write(l.VbPacked); w.Write(l.VbExtOrigin);
            w.Write(l.VbElementSize); w.Write(l.VbCount); w.Write(l.VbData);
            if (l.ColourData != null) { w.Write(l.ColourElementSize); w.Write(l.ColourCount); w.Write(l.ColourData); }
            w.Write(l.InfluenceSets);
            w.Write(l.ContainerCpu); w.Write(l.ContainerWidth); w.Write(l.ContainerElementSize); w.Write(l.ContainerCount); w.Write(l.ContainerData);
        }
        w.Write(NameIndexMap.Count); foreach (var (name, idx) in NameIndexMap) { w.Write(name); w.Write(idx); }
        w.Write(Tail);
        return o.ToArray();
    }
}
