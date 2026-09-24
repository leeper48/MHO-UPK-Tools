namespace AnimExportCli.Packages;

/// <summary>One object this package references but does not contain.</summary>
public readonly record struct ImportEntry(NameReference ClassPackage, NameReference ClassName, ObjectReference Outer, NameReference ObjectName);

/// <summary>The import table: fixed-size entries, 28 bytes each.</summary>
public sealed class ImportTable
{
    public const int EntrySize = (4 * 2 * 3) + 4;

    private readonly ImportEntry[] _entries;
    private ImportTable(ImportEntry[] entries) => _entries = entries;

    public int Count => _entries.Length;

    public ImportEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_entries.Length)
                throw new InvalidPackageException($"Import index {index} is outside the table ({_entries.Length} entries).");
            return _entries[index];
        }
    }

    public static ImportTable Read(ReadOnlySpan<byte> body, PackageHeader header, int bodyStart)
    {
        if (header.ImportCount < 0) throw new InvalidPackageException($"Negative import count {header.ImportCount}.");

        int start = header.ImportOffset - bodyStart;
        if (start < 0 || start > body.Length)
            throw new InvalidPackageException($"Import table offset {header.ImportOffset} is outside the expanded body.");
        if ((long)header.ImportCount * EntrySize > body.Length - start)
            throw new InvalidPackageException($"Import count {header.ImportCount} does not fit in the space remaining.");

        var cursor = new PackageCursor(body, start);
        var entries = new ImportEntry[header.ImportCount];

        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new ImportEntry(
                ReadName(ref cursor, i, "class package"),
                ReadName(ref cursor, i, "class name"),
                new ObjectReference(cursor.ReadInt32($"import {i} outer")),
                ReadName(ref cursor, i, "object name"));
        }

        return new ImportTable(entries);
    }

    private static NameReference ReadName(ref PackageCursor cursor, int index, string what) =>
        new(cursor.ReadInt32($"import {index} {what} index"), cursor.ReadInt32($"import {index} {what} number"));
}
