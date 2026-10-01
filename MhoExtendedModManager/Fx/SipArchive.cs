using System.Text;

namespace MhoExtendedModManager.Fx;

// Ported from the MHO Hero Creator's SipArchive.cs (2026-09-30; power effects in the 3D preview), READ ONLY: the writer
// (Put / Write) and the LZ4 encoder were removed 2026-09-30 (Kurt: nothing in the public repos may change what a stock
// server install expects; the game data archives are only ever read here).
/// <summary>
/// The game's data archives (Data\Game\Calligraphy.sip, mu_cdata.sip), read only.
///
/// Format (checked 2026-09-29 on the stock Calligraphy.sip, 94,788 entries, and against MHServerEmu 1.0.0's PakFile):
///   "KAPG", u32 version 1, i32 count; entries (u64 hash, i32 name length, name, i32 mod time, i32 offset,
///   i32 compressed size, i32 size); then the body.
///   - The hash is <see cref="HashPath"/> of the entry's name (lower case, as stored: "Calligraphy/…").
///   - Every entry is one LZ4 block, also the 799 whose compressed size equals their size (all 799 decode as LZ4 to
///     different bytes; readers that took those as stored misread them).
/// </summary>
sealed class SipArchive
{
    public const uint Magic = 0x4750414B;   // "KAPG"

    public sealed class Entry
    {
        public ulong Hash;
        public byte[] NameBytes = [];
        public int ModTime;
        public int Size;
        public byte[] Compressed = [];     // one LZ4 block
        public string Name => Encoding.Latin1.GetString(NameBytes);
    }

    public uint Version = 1;
    public readonly List<Entry> Entries = new();
    readonly Dictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);

    public static SipArchive Load(string path) => Parse(File.ReadAllBytes(path));

    public static SipArchive Parse(byte[] file)
    {
        var a = new SipArchive();
        var r = new BinaryReader(new MemoryStream(file), Encoding.Latin1);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a .sip archive");
        a.Version = r.ReadUInt32();
        int count = r.ReadInt32();
        var offsets = new int[count];
        for (int i = 0; i < count; i++)
        {
            var e = new Entry { Hash = r.ReadUInt64() };
            e.NameBytes = r.ReadBytes(r.ReadInt32());
            e.ModTime = r.ReadInt32();
            offsets[i] = r.ReadInt32();
            int csize = r.ReadInt32();
            e.Size = r.ReadInt32();
            e.Compressed = new byte[csize];
            a.Entries.Add(e);
        }
        long body = r.BaseStream.Position;
        for (int i = 0; i < count; i++)
        {
            long at = body + offsets[i];
            if (at < body || at + a.Entries[i].Compressed.Length > file.Length) throw new InvalidDataException("entry outside the file: " + a.Entries[i].Name);
            Array.Copy(file, at, a.Entries[i].Compressed, 0, a.Entries[i].Compressed.Length);
        }
        foreach (var e in a.Entries) a.byName[Norm(e.Name)] = e;
        return a;
    }

    static string Norm(string name) => name.Replace('\\', '/');

    public Entry? Find(string name) => byName.TryGetValue(Norm(name), out var e) ? e : null;

    public byte[] Read(string name) => Read(Find(name) ?? throw new KeyNotFoundException(name));
    public static byte[] Read(Entry e) => Lz4.Decode(e.Compressed, e.Size);

    /// <summary>The game's data reference hash of a path (MHServerEmu HashHelper.HashPath): (Adler-32 | CRC-32 &lt;&lt; 32) − 1
    /// of the lower-cased path. Checked on every stock entry name.</summary>
    public static ulong HashPath(string path)
    {
        var b = Encoding.Latin1.GetBytes(path.ToLowerInvariant());
        uint a = 1, s = 0;
        foreach (byte x in b) { a = (a + x) % 65521; s = (s + a) % 65521; }
        ulong adler = s << 16 | a;
        return (adler | (ulong)Crc32(b) << 32) - 1;
    }

    static readonly uint[] crcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ c >> 1 : c >> 1;
        return c;
    }).ToArray();

    /// <summary>Standard CRC-32 (zlib / IEEE).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in data) c = crcTable[(c ^ x) & 0xFF] ^ c >> 8;
        return c ^ 0xFFFFFFFF;
    }
}

/// <summary>LZ4 block format (no frame): decoding only.</summary>
static class Lz4
{
    const int MinMatch = 4;

    public static byte[] Decode(byte[] src, int outLen)
    {
        var dst = new byte[outLen];
        int i = 0, o = 0;
        while (i < src.Length)
        {
            int token = src[i++], lit = token >> 4;
            if (lit == 15) { int b; do { b = src[i++]; lit += b; } while (b == 255); }
            if (o + lit > outLen || i + lit > src.Length) throw new InvalidDataException("bad LZ4 block");
            Array.Copy(src, i, dst, o, lit); i += lit; o += lit;
            if (i >= src.Length) break;
            int off = src[i] | src[i + 1] << 8; i += 2;
            int ml = token & 15;
            if (ml == 15) { int b; do { b = src[i++]; ml += b; } while (b == 255); }
            ml += MinMatch;
            if (off == 0 || off > o || o + ml > outLen) throw new InvalidDataException("bad LZ4 block");
            for (int k = 0; k < ml; k++, o++) dst[o] = dst[o - off];
        }
        if (o != outLen) throw new InvalidDataException("bad LZ4 block");
        return dst;
    }
}
