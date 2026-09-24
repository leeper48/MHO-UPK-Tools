using System.IO.Compression;
using System.Text;

namespace UpkMeshScan;

public sealed class PackageFormatException(string msg) : Exception(msg);

public readonly record struct CompressedChunk(int UncompOffset, int UncompSize, int CompOffset, int CompSize);
public readonly record struct ImportEntry(string ClassName, int OuterIndex, string ObjectName);
public readonly record struct ExportEntry(int ClassIndex, int OuterIndex, string ObjectName, int SerialSize, int SerialOffset);

/// <summary>
/// Minimal UE3 package reader: header, (optional) chunk decompression, name/import/export tables.
/// Only what a scan needs — no object bodies are parsed. Chunks are decompressed lazily, so
/// only the chunks that hold the tables are ever unpacked.
/// </summary>
public sealed class Package
{
    public const uint Magic = 0x9E2A83C1;

    public int FileVersion { get; private set; }
    public int LicenseeVersion { get; private set; }
    public uint CompressionFlags { get; private set; }
    public List<CompressedChunk> Chunks { get; } = new();
    public string ChunkSource { get; private set; } = "none";
    public string[] Names { get; private set; } = Array.Empty<string>();
    public ImportEntry[] Imports { get; private set; } = Array.Empty<ImportEntry>();
    public ExportEntry[] Exports { get; private set; } = Array.Empty<ExportEntry>();

    int nameCount, nameOffset, exportCount, exportOffset, importCount, importOffset;

    public int NameOffset => nameOffset;
    /// <summary>Raw-file positions of summary fields the package writer patches (-1 if unknown).</summary>
    public int PackageFlagsAt { get; private set; } = -1;
    public int CompressionFlagsAt { get; private set; } = -1;
    /// <summary>Raw-file range of the chunk table (count field through last entry).</summary>
    public int ChunkTableStart { get; private set; } = -1;
    public int ChunkTableEnd { get; private set; } = -1;
    /// <summary>Raw-file end of the whole summary (after TextureAllocations).</summary>
    public int SummaryEnd { get; private set; } = -1;
    /// <summary>Body positions of each export's SerialSize field (SerialOffset follows it).</summary>
    public int[] ExportSerialFieldAt { get; private set; } = Array.Empty<int>();
    public byte[] RawFile => file;

    // Summary field positions (raw file; all come before the chunk table, so they're the same in an
    // uncompressed rewrite) and table layout, for rebuilding the package with added exports.
    public int TotalHeaderSizeAt { get; private set; } = -1;
    public int TotalHeaderSize { get; private set; }
    public int TableCountsAt { get; private set; } = -1;          // NameCount, NameOffset, ExportCount, ExportOffset, ImportCount, ImportOffset
    public int DependsOffsetAt { get; private set; } = -1;
    public int DependsOffset { get; private set; }
    public int ImportExportGuidsAt { get; private set; } = -1;    // offset, import guid count, export guid count
    public int ThumbnailTableAt { get; private set; } = -1;
    public int GenerationsAt { get; private set; } = -1;          // count, then {ExportCount, NameCount, NetObjectCount} each
    public int ImportOffset => importOffset;
    public int ExportOffset => exportOffset;
    /// <summary>Body range of each export-table entry.</summary>
    public int[] ExportEntryStart { get; private set; } = Array.Empty<int>();
    public int[] ExportEntryEnd { get; private set; } = Array.Empty<int>();
    /// <summary>The whole uncompressed body (for an uncompressed package this is the file).</summary>
    public byte[] Body => Chunks.Count > 0 ? FullBody() : file;

    readonly byte[] file;
    byte[] body = Array.Empty<byte>();
    bool[] chunkDone = Array.Empty<bool>();

    Package(byte[] file) => this.file = file;

    public static Package Open(string path) => FromBytes(File.ReadAllBytes(path));

    public static Package FromBytes(byte[] bytes)
    {
        var p = new Package(bytes);
        p.ReadHeader();
        p.PrepareBody();
        p.ReadTables();
        return p;
    }

    // ---------------------------------------------------------------- header

    void ReadHeader()
    {
        var r = new Reader(file, 0, file.Length);
        uint tag = r.U32();
        if (tag != Magic)
            throw new PackageFormatException(tag == 0xC1832A9E ? "big-endian package (console) not supported" : $"not a UE3 package (magic 0x{tag:X8})");

        int ver = r.I32();
        FileVersion = ver & 0xFFFF;
        LicenseeVersion = (ver >> 16) & 0xFFFF;
        int v = FileVersion;

        TotalHeaderSizeAt = r.Pos;
        if (v >= 249) TotalHeaderSize = r.I32();              // TotalHeaderSize
        if (v >= 269) r.FString();          // FolderName
        PackageFlagsAt = r.Pos;
        r.U32();                            // PackageFlags
        TableCountsAt = r.Pos;
        nameCount = r.I32(); nameOffset = r.I32();
        exportCount = r.I32(); exportOffset = r.I32();
        importCount = r.I32(); importOffset = r.I32();

        // Everything above is stable across UE3. Everything below can drift in a licensee fork,
        // so the chunk table is validated and, failing that, located by signature (TryFindChunks).
        try
        {
            DependsOffsetAt = r.Pos;
            if (v >= 415) DependsOffset = r.I32();          // DependsOffset
            ImportExportGuidsAt = r.Pos;
            if (v >= 623) { r.I32(); r.I32(); r.I32(); }    // ImportExportGuids offset/counts
            ThumbnailTableAt = r.Pos;
            if (v >= 584) r.I32();                          // ThumbnailTableOffset
            r.Skip(16);                                     // Guid
            GenerationsAt = r.Pos;
            int gens = r.I32();
            if (gens < 0 || gens > 10000) throw new PackageFormatException("bad generation count");
            r.Skip(gens * (v >= 322 ? 12 : 8));
            if (v >= 245) r.I32();                          // EngineVersion
            if (v >= 277) r.I32();                          // CookerVersion
            int flagsAt = r.Pos;
            uint flags = v >= 334 ? r.U32() : 0;
            int tableAt = r.Pos;
            int n = v >= 334 ? r.I32() : 0;
            if (n < 0 || n > 100000) throw new PackageFormatException("bad chunk count");
            var list = new List<CompressedChunk>(n);
            for (int i = 0; i < n; i++) list.Add(new(r.I32(), r.I32(), r.I32(), r.I32()));

            if (n == 0 || ChunksLookValid(list))
            {
                CompressionFlagsAt = flagsAt;
                ChunkTableStart = tableAt;
                ChunkTableEnd = r.Pos;
                try
                {
                    // PackageSource, AdditionalPackagesToCook (TArray<FString>), TextureAllocations.
                    r.U32();
                    int extra = r.I32();
                    if (extra < 0 || extra > 10000) throw new PackageFormatException("bad AdditionalPackagesToCook count");
                    for (int i = 0; i < extra; i++) r.FString();
                    int texTypes = r.I32();
                    if (texTypes < 0 || texTypes > 100000) throw new PackageFormatException("bad TextureAllocations count");
                    for (int i = 0; i < texTypes; i++)
                    {
                        r.Skip(5 * 4);                  // SizeX, SizeY, NumMips, Format, TexCreateFlags
                        int idx = r.I32();
                        if (idx < 0 || idx > 10_000_000) throw new PackageFormatException("bad texture export index count");
                        r.Skip(idx * 4);
                    }
                    SummaryEnd = r.Pos;
                }
                catch (PackageFormatException) { SummaryEnd = -1; }
                CompressionFlags = flags;
                Chunks.AddRange(list);
                ChunkSource = n == 0 ? "none" : "header";
                return;
            }
        }
        catch (PackageFormatException) { }

        if (!TryFindChunks())
        {
            // No valid chunk table anywhere: treat as uncompressed if the tables are in range.
            if (TablesInRange(file.Length)) { ChunkSource = "none"; return; }
            throw new PackageFormatException($"could not locate compressed chunk table (v{FileVersion}/L{LicenseeVersion})");
        }
    }

    bool ChunksLookValid(List<CompressedChunk> list)
    {
        if (list.Count == 0) return false;
        int prevEnd = -1;
        foreach (var c in list)
        {
            if (c.UncompOffset < 0 || c.UncompSize <= 0 || c.CompOffset <= 0 || c.CompSize <= 0) return false;
            if ((long)c.CompOffset + c.CompSize > file.Length) return false;
            if (c.CompOffset + 4 > file.Length || BitConverter.ToUInt32(file, c.CompOffset) != Magic) return false;
            if (prevEnd >= 0 && c.UncompOffset < prevEnd) return false;
            prevEnd = c.UncompOffset + c.UncompSize;
        }
        return list[0].UncompOffset <= nameOffset; // header is stored raw ahead of the first chunk
    }

    bool TryFindChunks()
    {
        // Scan the header region for "count, then count x {UncompOff, UncompSize, CompOff, CompSize}"
        // where every CompOff lands on a chunk signature. Very unlikely to false-match.
        int limit = Math.Min(file.Length - 20, 64 * 1024);
        for (int p = 32; p < limit; p++) // byte steps: FolderName FString makes the header unaligned
        {
            int n = BitConverter.ToInt32(file, p);
            if (n <= 0 || n > 100000 || p + 4 + n * 16 > file.Length) continue;
            var list = new List<CompressedChunk>(n);
            for (int i = 0; i < n; i++)
            {
                int o = p + 4 + i * 16;
                list.Add(new(BitConverter.ToInt32(file, o), BitConverter.ToInt32(file, o + 4),
                             BitConverter.ToInt32(file, o + 8), BitConverter.ToInt32(file, o + 12)));
            }
            if (!ChunksLookValid(list)) continue;
            Chunks.AddRange(list);
            CompressionFlags = p >= 4 ? BitConverter.ToUInt32(file, p - 4) : 0;
            if ((CompressionFlags & 7) == 0) CompressionFlags = 2; // fall back to LZO (Marvel Heroes default)
            ChunkSource = "signature-scan";
            return true;
        }
        return false;
    }

    bool TablesInRange(long len) =>
        nameOffset > 0 && nameOffset < len && exportOffset > 0 && exportOffset < len && importOffset > 0 && importOffset < len;

    // ---------------------------------------------------------------- body

    void PrepareBody()
    {
        if (Chunks.Count == 0) { body = file; return; }
        long size = 0;
        foreach (var c in Chunks) size = Math.Max(size, (long)c.UncompOffset + c.UncompSize);
        if (size > int.MaxValue) throw new PackageFormatException("uncompressed size too large");
        body = new byte[size];
        int headerLen = Math.Min(Chunks[0].UncompOffset, file.Length);
        Buffer.BlockCopy(file, 0, body, 0, headerLen);
        chunkDone = new bool[Chunks.Count];
    }

    /// <summary>Decompress whatever chunks cover [start, start+len); returns a range known to be ready.</summary>
    (long, long) Ensure(long start, long len)
    {
        if (Chunks.Count == 0) return (0, body.Length);
        long end = start + len;
        long hdr = Chunks[0].UncompOffset;
        if (end <= hdr) return (0, hdr);
        long rs = long.MaxValue, re = long.MinValue;
        for (int i = 0; i < Chunks.Count; i++)
        {
            var c = Chunks[i];
            long cs = c.UncompOffset, ce = (long)c.UncompOffset + c.UncompSize;
            if (cs >= end || ce <= start) continue;
            if (!chunkDone[i]) { DecompressChunk(c); chunkDone[i] = true; ChunksExpanded++; }
            rs = Math.Min(rs, cs); re = Math.Max(re, ce);
        }
        if (start < hdr) rs = 0;
        if (rs > start || re < end) throw new PackageFormatException($"offset 0x{start:X} not covered by any chunk");
        return (rs, re);
    }

    public int ChunksExpanded { get; private set; }

    void DecompressChunk(CompressedChunk c)
    {
        var r = new Reader(file, c.CompOffset, c.CompSize);
        if (r.U32() != Magic) throw new PackageFormatException("bad chunk signature");
        int blockSize = r.I32();
        r.I32(); int total = r.I32();  // summary: compressed, uncompressed
        if (blockSize <= 0) blockSize = 0x20000;
        if (total != c.UncompSize) throw new PackageFormatException("chunk size mismatch");
        int blocks = (total + blockSize - 1) / blockSize;
        var sizes = new (int comp, int uncomp)[blocks];
        for (int i = 0; i < blocks; i++) sizes[i] = (r.I32(), r.I32());

        int dst = c.UncompOffset;
        foreach (var (comp, uncomp) in sizes)
        {
            int src = r.Pos;
            r.Skip(comp);
            if (dst + uncomp > body.Length) throw new PackageFormatException("block overruns package");
            switch (CompressionFlags & 7)
            {
                case 2: Lzo1x.Decompress(file, src, comp, body, dst, uncomp); break;
                case 1:
                    using (var z = new ZLibStream(new MemoryStream(file, src, comp), CompressionMode.Decompress))
                    {
                        int got = 0;
                        while (got < uncomp) { int k = z.Read(body, dst + got, uncomp - got); if (k <= 0) break; got += k; }
                        if (got != uncomp) throw new PackageFormatException("zlib short block");
                    }
                    break;
                default: throw new PackageFormatException($"unsupported compression flags 0x{CompressionFlags:X}");
            }
            dst += uncomp;
        }
    }

    // ---------------------------------------------------------------- tables

    void ReadTables()
    {
        if (nameCount < 0 || nameCount > 5_000_000 || exportCount < 0 || exportCount > 5_000_000 || importCount < 0 || importCount > 5_000_000)
            throw new PackageFormatException("implausible table counts");
        if (!TablesInRange(body.Length)) throw new PackageFormatException("table offsets outside package");

        int v = FileVersion;

        // Names: FString + flags (64-bit on modern UE3).
        var lazy = Chunks.Count > 0 ? this : null;
        var r = new Reader(body, nameOffset, body.Length - nameOffset, lazy);
        Names = new string[nameCount];
        for (int i = 0; i < nameCount; i++)
        {
            Names[i] = r.FString();
            if (v >= 195) r.U64(); else r.U32();
        }

        r = new Reader(body, importOffset, body.Length - importOffset, lazy);
        Imports = new ImportEntry[importCount];
        for (int i = 0; i < importCount; i++)
        {
            Name(ref r);                    // ClassPackage
            string cls = Name(ref r);       // ClassName
            int outer = r.I32();
            string obj = Name(ref r);
            Imports[i] = new(cls, outer, obj);
        }

        r = new Reader(body, exportOffset, body.Length - exportOffset, lazy);
        Exports = new ExportEntry[exportCount];
        ExportSerialFieldAt = new int[exportCount];
        ExportEntryStart = new int[exportCount];
        ExportEntryEnd = new int[exportCount];
        for (int i = 0; i < exportCount; i++)
        {
            ExportEntryStart[i] = r.Pos;
            int cls = r.I32();
            r.I32();                            // SuperIndex
            int outer = r.I32();
            string name = Name(ref r);
            if (v >= 220) r.I32();              // ArchetypeIndex
            r.U64();                            // ObjectFlags
            ExportSerialFieldAt[i] = r.Pos;
            int size = r.I32();
            int off = r.I32();
            if (v < 543) { int m = r.I32(); r.Skip(m * 12); } // ComponentMap
            if (v >= 247) r.U32();              // ExportFlags
            if (v >= 322)
            {
                int net = r.I32();
                if (net < 0 || net > 1_000_000) throw new PackageFormatException($"export {i}: bad NetObjectCount — export layout differs in this fork");
                r.Skip(net * 4);
                r.Skip(16);                     // PackageGuid
            }
            if (v >= 475) r.U32();              // PackageFlags
            if (cls < -importCount || cls > exportCount || outer < -importCount || outer > exportCount)
                throw new PackageFormatException($"export {i}: index out of range — export layout differs in this fork");
            Exports[i] = new(cls, outer, name, size, off);
            ExportEntryEnd[i] = r.Pos;
        }
    }

    string Name(ref Reader r)
    {
        int idx = r.I32();
        int num = FileVersion >= 343 ? r.I32() : 0;
        if ((uint)idx >= (uint)Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{Names[idx]}_{num - 1}" : Names[idx];
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The whole uncompressed package (header region as stored, then every chunk expanded).</summary>
    public byte[] FullBody()
    {
        if (Chunks.Count > 0) Ensure(Chunks[0].UncompOffset, body.Length - Chunks[0].UncompOffset);
        return body;
    }

    /// <summary>Name of the object an object reference points at (export if &gt; 0, import if &lt; 0).</summary>
    public string RefName(int index) => index switch
    {
        0 => "None",
        > 0 when index <= Exports.Length => Exports[index - 1].ObjectName,
        < 0 when -index <= Imports.Length => Imports[-index - 1].ObjectName,
        _ => $"ref{index}",
    };

    /// <summary>The export's serialized bytes, decompressing only the chunks that cover it.</summary>
    public byte[] ReadExportBytes(ExportEntry e)
    {
        if (e.SerialSize < 0 || e.SerialOffset < 0 || (long)e.SerialOffset + e.SerialSize > body.Length)
            throw new PackageFormatException($"export '{e.ObjectName}' lies outside the package body");
        if (e.SerialSize > 0) Ensure(e.SerialOffset, e.SerialSize);
        return body.AsSpan(e.SerialOffset, e.SerialSize).ToArray();
    }

    public string ClassOf(ExportEntry e) => e.ClassIndex switch
    {
        0 => "Class",
        < 0 => Imports[-e.ClassIndex - 1].ObjectName,
        _ => Exports[e.ClassIndex - 1].ObjectName,
    };

    /// <summary>Group path inside the package, e.g. "Buildings.Tower.SM_Tower01".</summary>
    public string PathOf(ExportEntry e)
    {
        var parts = new List<string> { e.ObjectName };
        int outer = e.OuterIndex, guard = 0;
        while (outer != 0 && guard++ < 64)
        {
            if (outer > 0) { var o = Exports[outer - 1]; parts.Add(o.ObjectName); outer = o.OuterIndex; }
            else { var o = Imports[-outer - 1]; parts.Add(o.ObjectName); outer = o.OuterIndex; }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    ref struct Reader
    {
        readonly byte[] b; readonly int end; public int Pos;
        readonly Package? lazy; long readyStart, readyEnd;
        public Reader(byte[] buf, int start, int len, Package? lazyOwner = null)
        { b = buf; Pos = start; end = start + len; lazy = lazyOwner; readyStart = readyEnd = 0; }
        void Need(int n)
        {
            if (n < 0 || Pos + n > end) throw new PackageFormatException($"read past end at 0x{Pos:X}");
            if (lazy != null && (Pos < readyStart || Pos + n > readyEnd))
                (readyStart, readyEnd) = lazy.Ensure(Pos, Math.Max(n, 1));
        }
        public int I32() { Need(4); int x = BitConverter.ToInt32(b, Pos); Pos += 4; return x; }
        public uint U32() { Need(4); uint x = BitConverter.ToUInt32(b, Pos); Pos += 4; return x; }
        public ulong U64() { Need(8); ulong x = BitConverter.ToUInt64(b, Pos); Pos += 8; return x; }
        public void Skip(int n) { Need(n); Pos += n; }
        public string FString()
        {
            int len = I32();
            if (len == 0) return "";
            if (len > 0)
            {
                if (len > 4096) throw new PackageFormatException($"string too long at 0x{Pos:X}");
                Need(len); string s = Encoding.Latin1.GetString(b, Pos, len - 1); Pos += len; return s;
            }
            int chars = -len;
            if (chars > 4096) throw new PackageFormatException($"string too long at 0x{Pos:X}");
            Need(chars * 2); string u = Encoding.Unicode.GetString(b, Pos, (chars - 1) * 2); Pos += chars * 2; return u;
        }
    }
}
