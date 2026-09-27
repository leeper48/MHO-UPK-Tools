using System.Buffers.Binary;
using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// The game's localized string files, Data\Game\Loco\&lt;lang&gt;.all\&lt;lang&gt;.all_{3F,7F,BF,FF}FFFFFFFFFFFFFF.string.
/// Layout (worked out from the files 2026-09-27, all 28 stock-backup files and Kurt's live English ones):
/// "STR", version byte (2), entry count (u16), then per entry sorted by ID: ID (u64), string count (u16, main + variants),
/// FlagsProduced (u16, usually 0xFFFF), offset (u32) of the main string; then per variant FlagsConsumed (u64),
/// FlagsProduced (u16), offset (u32). After the table come the strings, UTF-8 and null-terminated, in table order.
/// Parse then Write gives back every backup byte for byte, and backup + the enabled mods' JSON (top of the order wins;
/// empty Variants keeps the original ones) gives Kurt's live files byte for byte, as MHModManager wrote them.
/// </summary>
sealed class StringFile
{
    public sealed record Variant(ulong FlagsConsumed, ushort FlagsProduced, string Text);
    public sealed record Entry(ushort FlagsProduced, string Text, IReadOnlyList<Variant> Variants);

    public byte Version { get; init; } = 2;
    public SortedDictionary<ulong, Entry> Entries { get; } = [];

    public static StringFile Parse(byte[] b)
    {
        if (b.Length < 6 || b[0] != (byte)'S' || b[1] != (byte)'T' || b[2] != (byte)'R') throw new InvalidDataException("not a .string file (no STR magic)");
        var f = new StringFile { Version = b[3] };
        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)), p = 6;
        string At(uint o)
        {
            int end = Array.IndexOf(b, (byte)0, (int)o);
            if (end < 0) throw new InvalidDataException($"string at {o} has no terminator");
            return Encoding.UTF8.GetString(b, (int)o, end - (int)o);
        }
        for (int i = 0; i < n; i++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 8));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 10));
            uint off = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 12));
            p += 16;
            var vs = new List<Variant>();
            for (int k = 1; k < count; k++, p += 14)
                vs.Add(new Variant(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p + 8)), At(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p + 10)))));
            if (!f.Entries.TryAdd(id, new Entry(flags, At(off), vs))) throw new InvalidDataException($"ID {id} appears twice");
        }
        return f;
    }

    public byte[] Write()
    {
        if (Entries.Count > ushort.MaxValue) throw new InvalidDataException($"{Entries.Count} entries: more than the format's 65,535");
        int tableSize = 6 + Entries.Values.Sum(e => 16 + 14 * e.Variants.Count);
        using var table = new MemoryStream();
        using var strings = new MemoryStream();
        uint Add(string s)
        {
            uint o = (uint)(tableSize + strings.Length);
            strings.Write(Encoding.UTF8.GetBytes(s)); strings.WriteByte(0);
            return o;
        }
        Span<byte> buf = stackalloc byte[16];
        table.Write("STR"u8); table.WriteByte(Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)Entries.Count); table.Write(buf[..2]);
        foreach (var (id, e) in Entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buf, id);
            BinaryPrimitives.WriteUInt16LittleEndian(buf[8..], (ushort)(e.Variants.Count + 1));
            BinaryPrimitives.WriteUInt16LittleEndian(buf[10..], e.FlagsProduced);
            BinaryPrimitives.WriteUInt32LittleEndian(buf[12..], Add(e.Text));
            table.Write(buf[..16]);
            foreach (var v in e.Variants)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buf, v.FlagsConsumed);
                BinaryPrimitives.WriteUInt16LittleEndian(buf[8..], v.FlagsProduced);
                BinaryPrimitives.WriteUInt32LittleEndian(buf[10..], Add(v.Text));
                table.Write(buf[..14]);
            }
        }
        strings.Position = 0; strings.CopyTo(table);
        return table.ToArray();
    }

    public StringFile Clone()
    {
        var c = new StringFile { Version = Version };
        foreach (var (k, v) in Entries) c.Entries[k] = v;
        return c;
    }

    /// <summary>Same entries (IDs, flags, texts, variants)?</summary>
    public static List<string> Differences(StringFile a, StringFile b, int max = 5)
    {
        var d = new List<string>();
        foreach (ulong id in a.Entries.Keys.Union(b.Entries.Keys))
        {
            a.Entries.TryGetValue(id, out var x); b.Entries.TryGetValue(id, out var y);
            if (x == null || y == null || x.FlagsProduced != y.FlagsProduced || x.Text != y.Text || !x.Variants.SequenceEqual(y.Variants)) d.Add($"ID {id} differs");
            if (d.Count >= max) break;
        }
        return d;
    }
}
