namespace AnimExportCli.Packages;

/// <summary>One object this package contains.</summary>
public sealed record ExportEntry
{
    public required ObjectReference Class { get; init; }
    public required ObjectReference Super { get; init; }
    public required ObjectReference Outer { get; init; }
    public required NameReference ObjectName { get; init; }
    public required ObjectReference Archetype { get; init; }
    public required ulong ObjectFlags { get; init; }
    public required int SerialSize { get; init; }
    public required int SerialOffset { get; init; }
}

/// <summary>
/// The export table. Entries are variable length: after the fixed fields
/// comes a net-object count and then that many extra integers, so entries
/// must be walked one at a time rather than addressed by a fixed stride.
/// </summary>
public sealed class ExportTable
{
    private readonly ExportEntry[] _entries;
    private ExportTable(ExportEntry[] entries) => _entries = entries;

    public int Count => _entries.Length;

    public ExportEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException($"Export index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    public static ExportTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.ExportCount < 0) throw new InvalidPackageException($"Negative export count {header.ExportCount}.");

        int start = header.ExportOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException($"Export table offset {header.ExportOffset} is outside the expanded body.");

        const int minimumEntrySize = 68;
        if ((long)header.ExportCount * minimumEntrySize > body.Length - start)
            throw new InvalidPackageException($"Export count {header.ExportCount} cannot fit in the space remaining.");

        var cursor = new PackageCursor(body, start);
        var entries = new ExportEntry[header.ExportCount];

        for (int i = 0; i < entries.Length; i++)
        {
            var classRef = new ObjectReference(cursor.ReadInt32($"export {i} class"));
            var superRef = new ObjectReference(cursor.ReadInt32($"export {i} super"));
            var outerRef = new ObjectReference(cursor.ReadInt32($"export {i} outer"));
            var objectName = new NameReference(cursor.ReadInt32($"export {i} name index"), cursor.ReadInt32($"export {i} name number"));
            var archetype = new ObjectReference(cursor.ReadInt32($"export {i} archetype"));
            ulong objectFlags = cursor.ReadUInt64($"export {i} object flags");

            int serialSize = cursor.ReadInt32($"export {i} serial size");
            int serialOffset = cursor.ReadInt32($"export {i} serial offset");
            cursor.Skip(4); // export flags — not needed downstream

            if (serialSize < 0) throw new InvalidPackageException($"Export {i} declares a negative serial size.");
            if (serialOffset < 0) throw new InvalidPackageException($"Export {i} declares a negative serial offset.");

            int netObjectCount = cursor.ReadInt32($"export {i} net object count");
            if (netObjectCount < 0 || (long)netObjectCount * 4 > cursor.Remaining)
                throw new InvalidPackageException($"Export {i} declares {netObjectCount} net objects, which does not fit.");
            cursor.Skip(netObjectCount * 4);

            cursor.Skip(16); // package guid
            cursor.Skip(4);  // package flags

            entries[i] = new ExportEntry
            {
                Class = classRef,
                Super = superRef,
                Outer = outerRef,
                ObjectName = objectName,
                Archetype = archetype,
                ObjectFlags = objectFlags,
                SerialSize = serialSize,
                SerialOffset = serialOffset,
            };
        }

        return new ExportTable(entries);
    }
}
