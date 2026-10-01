using System.Text;

namespace MhoExtendedModManager.Fx;

// Ported from the MHO Hero Creator's Calligraphy.cs (2026-09-30; power effects in the 3D preview), READ ONLY: its
// prototype, directory and asset-type writers were removed 2026-09-30 (Kurt: nothing in the public repos may change what
// a stock server install expects; game data is only ever read here).
/// <summary>
/// Calligraphy data files inside Calligraphy.sip: prototypes (.prototype / .defaults), the prototype and blueprint
/// directories, and blueprints (for field names). Read only.
///
/// Format (MHServerEmu 1.0.0: CalligraphyHeader, PrototypeDataHeader, CalligraphySerializer, DataDirectory):
///   Every file starts with a 4-byte header: 3-byte magic + version byte.
///   Prototype data: u8 flags (1 = parent reference follows, 2 = field groups follow, 4 = polymorphic: skipped by the
///   game), [u64 parent prototype id], [i16 group count; groups: u64 blueprint id, u8 copy number, i16 simple field
///   count, fields (u64 field id, u8 base type, value), i16 list field count, list fields (u64 id, u8 type, i16 count,
///   values)]. A value is 8 bytes, except type R (a struct): nested prototype data in the same format.
///   The server copies the parent's fields first, then applies these.
///   Prototype.directory: header, i32 count, entries (u64 id, u64 guid, u64 blueprint id, u8 flags, u16 length + path).
///   Flags: 1 abstract, 2 protected, 4 editor only.
/// </summary>
static class Calligraphy
{
    public sealed class Data
    {
        public byte Flags;
        public ulong Parent;
        public List<Group> Groups = new();
        public bool HasParent => (Flags & 1) != 0;
        public bool HasGroups => (Flags & 2) != 0;

        public Data Clone()
        {
            var d = new Data { Flags = Flags, Parent = Parent };
            foreach (var g in Groups) d.Groups.Add(g.Clone());
            return d;
        }
    }

    public sealed class Group
    {
        public ulong Blueprint;
        public byte Copy;
        public List<Field> Simple = new();
        public List<ListField> Lists = new();
        public Group Clone() => new()
        {
            Blueprint = Blueprint, Copy = Copy,
            Simple = Simple.Select(f => f.Clone()).ToList(),
            Lists = Lists.Select(f => f.Clone()).ToList(),
        };
    }

    public sealed class Value
    {
        public ulong Raw;          // everything but R
        public Data? Struct;       // R
        public Value Clone() => new() { Raw = Raw, Struct = Struct?.Clone() };
    }

    public sealed class Field
    {
        public ulong Id;
        public char Type;
        public Value Value = new();
        public Field Clone() => new() { Id = Id, Type = Type, Value = Value.Clone() };
    }

    public sealed class ListField
    {
        public ulong Id;
        public char Type;
        public List<Value> Values = new();
        public ListField Clone() => new() { Id = Id, Type = Type, Values = Values.Select(v => v.Clone()).ToList() };
    }

    /// <summary>A prototype file: its 4-byte header and its data.</summary>
    public sealed class PrototypeFile
    {
        public byte[] Header = new byte[4];
        public Data Data = new();

        public static PrototypeFile Parse(byte[] b)
        {
            var r = new Reader(b, 4);
            var f = new PrototypeFile { Header = b[..4] };
            f.Data = ReadData(r);
            if (r.Pos != b.Length) throw new InvalidDataException($"{b.Length - r.Pos} bytes left after the prototype data");
            return f;
        }

    }

    static Data ReadData(Reader r)
    {
        var d = new Data { Flags = r.U8() };
        if ((d.Flags & ~7) != 0) throw new InvalidDataException($"unknown prototype data flags {d.Flags}");
        if (d.HasParent) d.Parent = r.U64();
        if (!d.HasGroups) return d;
        int groups = r.I16();
        for (int i = 0; i < groups; i++)
        {
            var g = new Group { Blueprint = r.U64(), Copy = r.U8() };
            int n = r.I16();
            for (int k = 0; k < n; k++)
            {
                var f = new Field { Id = r.U64(), Type = (char)r.U8() };
                f.Value = ReadValue(r, f.Type);
                g.Simple.Add(f);
            }
            n = r.I16();
            for (int k = 0; k < n; k++)
            {
                var f = new ListField { Id = r.U64(), Type = (char)r.U8() };
                int count = r.I16();
                for (int v = 0; v < count; v++) f.Values.Add(ReadValue(r, f.Type));
                g.Lists.Add(f);
            }
            d.Groups.Add(g);
        }
        return d;
    }

    static Value ReadValue(Reader r, char type)
    {
        if (type == 'R') return new Value { Struct = ReadData(r) };
        if ("ABCDLPSTR".IndexOf(type) < 0) throw new InvalidDataException($"unknown field type '{type}'");
        return new Value { Raw = r.U64() };
    }


    // ---- directories

    public sealed class DirEntry
    {
        public ulong Id, Guid, Blueprint;   // for Blueprint.directory: Id, Guid, then Blueprint unused
        public byte Flags;
        public string Path = "";
    }

    /// <summary>Prototype.directory (hasBlueprint) or Blueprint.directory (no blueprint id per entry).</summary>
    public sealed class Directory
    {
        public byte[] Header = new byte[4];
        public List<DirEntry> Entries = new();
        public bool HasBlueprint;

        public static Directory Parse(byte[] b, bool hasBlueprint)
        {
            var r = new Reader(b, 4);
            var d = new Directory { Header = b[..4], HasBlueprint = hasBlueprint };
            int n = r.I32();
            for (int i = 0; i < n; i++)
            {
                var e = new DirEntry { Id = r.U64(), Guid = r.U64() };
                if (hasBlueprint) e.Blueprint = r.U64();
                e.Flags = r.U8();
                e.Path = r.S16();
                d.Entries.Add(e);
            }
            if (r.Pos != b.Length) throw new InvalidDataException("bytes left after the directory");
            return d;
        }

    }

    /// <summary>A prototype's data-ref id from its directory path (MHServerEmu: HashPath(path.ToCalligraphyPath()),
    /// '.' → '?', '/' → '.'; the directory stores paths with '\').</summary>
    public static ulong PrototypeId(string directoryPath) =>
        SipArchive.HashPath(directoryPath.Replace('\\', '/').Replace('.', '?').Replace('/', '.'));

    // ---- asset types (.type files: the named assets that A fields refer to)

    public sealed class AssetValue
    {
        public ulong Id, Guid;
        public byte Flags;
        public string Name = "";
    }

    /// <summary>A .type file (MHServerEmu AssetType): header, u16 count, assets (u64 id, u64 guid, u8 flags, u16 + name).</summary>
    public sealed class AssetTypeFile
    {
        public byte[] Header = new byte[4];
        public List<AssetValue> Assets = new();

        public static AssetTypeFile Parse(byte[] b)
        {
            var r = new Reader(b, 4);
            var t = new AssetTypeFile { Header = b[..4] };
            for (int n = r.U16(), i = 0; i < n; i++)
                t.Assets.Add(new AssetValue { Id = r.U64(), Guid = r.U64(), Flags = r.U8(), Name = r.S16() });
            if (r.Pos != b.Length) throw new InvalidDataException("bytes left after the asset type");
            return t;
        }

    }

    // ---- blueprints (field names)

    public sealed class Blueprint
    {
        public string Binding = "";
        public ulong DefaultPrototype;
        public List<ulong> Parents = new();
        public List<ulong> Contributing = new();
        public Dictionary<ulong, (string Name, char Type)> Fields = new();

        public static Blueprint Parse(byte[] b)
        {
            var r = new Reader(b, 4);
            var bp = new Blueprint { Binding = r.S16(), DefaultPrototype = r.U64() };
            for (int n = r.U16(), i = 0; i < n; i++) { bp.Parents.Add(r.U64()); r.U8(); }
            for (int n = r.U16(), i = 0; i < n; i++) { bp.Contributing.Add(r.U64()); r.U8(); }
            for (int n = r.U16(), i = 0; i < n; i++)
            {
                ulong id = r.U64(); string name = r.S16(); char t = (char)r.U8(); r.U8();
                if ("ACPR".IndexOf(t) >= 0) r.U64();   // subtype: A C P R only (MHServerEmu BlueprintMember), not T
                bp.Fields[id] = (name, t);
            }
            if (r.Pos != b.Length) throw new InvalidDataException("bytes left after the blueprint");
            return bp;
        }
    }

    public sealed class Reader(byte[] b, int p = 0)
    {
        public int Pos = p;
        public byte U8() => b[Pos++];
        public short I16() { var v = BitConverter.ToInt16(b, Pos); Pos += 2; return v; }
        public ushort U16() { var v = BitConverter.ToUInt16(b, Pos); Pos += 2; return v; }
        public int I32() { var v = BitConverter.ToInt32(b, Pos); Pos += 4; return v; }
        public ulong U64() { var v = BitConverter.ToUInt64(b, Pos); Pos += 8; return v; }
        public string S16() { int n = U16(); var s = Encoding.Latin1.GetString(b, Pos, n); Pos += n; return s; }
    }
}
