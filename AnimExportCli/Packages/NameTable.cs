namespace AnimExportCli.Packages;

public readonly record struct NameEntry(string Name, ulong Flags)
{
    public override string ToString() => Name;
}

/// <summary>
/// Every string a package refers to, stored once and referenced by index from
/// the import and export tables. Each entry is a length-prefixed string
/// followed by eight bytes of engine object flags. Names are stored
/// lower-cased, so lookups here are case-insensitive.
/// </summary>
public sealed class NameTable
{
    private readonly NameEntry[] _entries;
    private readonly Dictionary<string, int> _byName;

    private NameTable(NameEntry[] entries)
    {
        _entries = entries;
        _byName = new Dictionary<string, int>(entries.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Length; i++) _byName.TryAdd(entries[i].Name, i);
    }

    public int Count => _entries.Length;

    public NameEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException($"Name index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    public string GetName(int index) => this[index].Name;

    /// <summary>A name index plus its disambiguating number renders as "name_N" for a non-zero number.</summary>
    public string Resolve(int index, int number) => number > 0 ? $"{GetName(index)}_{number - 1}" : GetName(index);

    public int IndexOf(string name) => _byName.TryGetValue(name, out int index) ? index : -1;

    public static NameTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.NameCount < 0) throw new InvalidPackageException($"Negative name count {header.NameCount}.");

        int start = header.NameOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException($"Name table offset {header.NameOffset} is outside the expanded body.");

        const int minimumEntrySize = 4 + 8;
        if ((long)header.NameCount * minimumEntrySize > body.Length - start)
            throw new InvalidPackageException($"Name count {header.NameCount} cannot fit in the space remaining.");

        var cursor = new PackageCursor(body, start);
        var entries = new NameEntry[header.NameCount];

        for (int i = 0; i < header.NameCount; i++)
            entries[i] = new NameEntry(cursor.ReadString($"name {i}"), cursor.ReadUInt64($"name {i} flags"));

        return new NameTable(entries);
    }
}
