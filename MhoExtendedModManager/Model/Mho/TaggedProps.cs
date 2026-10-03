using System.Buffers.Binary;

namespace MhoMffImporter;

/// <summary>
/// UE3 tagged properties as this game's packages store them (v868), read so they can be written back byte for byte
/// and edited: each tag = name (index + number, 8), type name (8), value size (4), array index (4), then for a
/// StructProperty the struct name (8), for a ByteProperty its enum name (8), for a BoolProperty the value byte (size 0),
/// then the value; a list ends with the
/// tag named None (8 bytes). Array values are a count + elements; arrays of structs hold tagged lists, one per element.
/// Checked by <c>--props-roundtrip</c> on every stock skeletal mesh.
/// </summary>
sealed class TaggedProps
{
    public sealed class Tag
    {
        public long Name, Type;           // name references (index + number)
        public int ArrayIndex;
        public long StructName;           // StructProperty: struct name; ByteProperty: enum name (8 bytes, v868; found in TriangleSortSettings)
        public byte BoolValue;            // BoolProperty only
        public byte[] Value = [];
        public string NameText = "", TypeText = "";
    }

    public List<Tag> Tags { get; } = new();
    public long NoneName;                 // the terminating None tag's name

    readonly Func<long, string> nameOf;
    TaggedProps(Func<long, string> nameOf) => this.nameOf = nameOf;

    public static string NameOf(IReadOnlyList<string> names, long nameRef)
    {
        int idx = (int)(nameRef & 0xFFFFFFFF), num = (int)(nameRef >> 32);
        string n = idx >= 0 && idx < names.Count ? names[idx] : $"#{idx}";
        return num > 0 ? $"{n}_{num - 1}" : n;
    }

    /// <summary>Reads a tagged list starting at <paramref name="at"/>; returns the position after its None tag.</summary>
    public static TaggedProps Read(byte[] d, ref int at, Func<long, string> nameOf)
    {
        var t = new TaggedProps(nameOf);
        while (true)
        {
            if (at + 8 > d.Length) throw new InvalidDataException($"property list runs past the end at {at}");
            long name = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(at));
            string nameText = nameOf(name);
            if (nameText.Equals("None", StringComparison.OrdinalIgnoreCase)) { t.NoneName = name; at += 8; return t; }
            var tag = new Tag { Name = name, NameText = nameText };
            tag.Type = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(at + 8));
            tag.TypeText = nameOf(tag.Type).ToLowerInvariant();
            int size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 16));
            tag.ArrayIndex = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 20));
            at += 24;
            if (tag.TypeText is "structproperty" or "byteproperty") { tag.StructName = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(at)); at += 8; }
            if (tag.TypeText == "boolproperty") { tag.BoolValue = d[at]; at += 1; }
            if (size < 0 || at + size > d.Length) throw new InvalidDataException($"property {nameText}: size {size} runs past the end");
            tag.Value = d.AsSpan(at, size).ToArray();
            at += size;
            t.Tags.Add(tag);
        }
    }

    public byte[] Write()
    {
        var o = new MemoryStream(); var w = new BinaryWriter(o);
        foreach (var tag in Tags)
        {
            w.Write(tag.Name); w.Write(tag.Type); w.Write(tag.Value.Length); w.Write(tag.ArrayIndex);
            if (tag.TypeText is "structproperty" or "byteproperty") w.Write(tag.StructName);
            if (tag.TypeText == "boolproperty") w.Write(tag.BoolValue);
            w.Write(tag.Value);
        }
        w.Write(NoneName);
        return o.ToArray();
    }

    public Tag? Find(string name) => Tags.FirstOrDefault(t => t.NameText.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>An array of structs: its elements as tagged lists.</summary>
    public List<TaggedProps> StructArray(Tag tag)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(tag.Value);
        int at = 4;
        var list = new List<TaggedProps>();
        for (int i = 0; i < count; i++) list.Add(Read(tag.Value, ref at, nameOf));
        if (at != tag.Value.Length) throw new InvalidDataException($"{tag.NameText}: {count} struct elements end at {at} of {tag.Value.Length}");
        return list;
    }

    public static byte[] StructArrayValue(IReadOnlyList<TaggedProps> elements)
    {
        var o = new MemoryStream(); var w = new BinaryWriter(o);
        w.Write(elements.Count);
        foreach (var e in elements) w.Write(e.Write());
        return o.ToArray();
    }

    /// <summary>
    /// Resizes a skeletal mesh LOD's per-section arrays to <paramref name="sections"/> (the stock ones are sized for the
    /// stock mesh's sections): bEnableShadowCasting (one byte each) and TriangleSortSettings (one struct each); new
    /// entries repeat the last stock entry. Returns what changed.
    /// </summary>
    public static List<string> ResizeLodSectionArrays(TaggedProps top, int sections)
    {
        var changes = new List<string>();
        var lodTag = top.Find("lodinfo") ?? throw new InvalidDataException("no lodinfo property");
        var lods = top.StructArray(lodTag);
        foreach (var lod in lods)
        {
            if (lod.Find("benableshadowcasting") is { } sc)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(sc.Value);
                var bytes = sc.Value.AsSpan(4, n).ToArray();
                var nb = new byte[sections];
                for (int i = 0; i < sections; i++) nb[i] = n == 0 ? (byte)1 : bytes[Math.Min(i, n - 1)];
                sc.Value = BitConverter.GetBytes(sections).Concat(nb).ToArray();
                changes.Add($"benableshadowcasting {n} → {sections}");
            }
            if (lod.Find("trianglesortsettings") is { } ts)
            {
                var elems = lod.StructArray(ts);
                int n = elems.Count;
                if (n > 0)
                {
                    var bytes = elems.Select(e => e.Write()).ToList();
                    var list = new List<TaggedProps>();
                    for (int i = 0; i < sections; i++)
                    {
                        int at = 0;
                        list.Add(Read(bytes[Math.Min(i, n - 1)], ref at, lod.nameOf));
                    }
                    ts.Value = StructArrayValue(list);
                }
                changes.Add($"trianglesortsettings {n} → {sections}");
            }
        }
        lodTag.Value = StructArrayValue(lods);
        return changes;
    }
}
