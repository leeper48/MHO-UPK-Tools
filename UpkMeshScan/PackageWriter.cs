using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// Writes a package with one export replaced, uncompressed. Layout rules, all checked on real files:
///   - an uncompressed header is the stored summary with CompressionFlags = 0, chunk count 0 and the
///     chunk entries dropped; it then ends exactly at NameOffset (as in the modded packages that load);
///   - PackageFlags loses 0x02000000 (StoreCompressed), as the other tools' working output does;
///   - every other byte keeps its uncompressed position, so no offset anywhere changes;
///   - the replacement export is appended at the end and only its export-table entry is rewritten.
/// The old export bytes stay in place, unreferenced.
/// </summary>
static class PackageWriter
{
    const uint StoreCompressed = 0x02000000;

    public static byte[] ReplaceExport(Package pkg, int exportIndex, Func<long, byte[]> buildExport)
    {
        byte[] file = pkg.RawFile;
        byte[] body = pkg.Chunks.Count > 0 ? pkg.FullBody() : file;
        int nameOffset = pkg.NameOffset;

        byte[] header;
        if (pkg.Chunks.Count > 0)
        {
            if (pkg.ChunkSource != "header" || pkg.ChunkTableStart < 0 || pkg.SummaryEnd < 0)
                throw new PackageFormatException("package summary could not be fully parsed; not writing it");
            using var ms = new MemoryStream();
            ms.Write(file, 0, pkg.ChunkTableStart);
            ms.Write(BitConverter.GetBytes(0));                                 // chunk count
            ms.Write(file, pkg.ChunkTableEnd, pkg.SummaryEnd - pkg.ChunkTableEnd);
            header = ms.ToArray();
        }
        else
        {
            header = file.AsSpan(0, nameOffset).ToArray();
        }
        if (header.Length != nameOffset)
            throw new PackageFormatException($"uncompressed summary is {header.Length} bytes but the name table starts at {nameOffset}; layout not as expected");

        if (pkg.CompressionFlagsAt >= 0) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pkg.CompressionFlagsAt), 0);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pkg.PackageFlagsAt));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pkg.PackageFlagsAt), flags & ~StoreCompressed);

        long bodyEnd = pkg.Chunks.Count > 0 ? pkg.Chunks.Max(c => (long)c.UncompOffset + c.UncompSize) : file.Length;
        byte[] export = buildExport(bodyEnd);

        var output = new byte[checked(bodyEnd + export.Length)];
        header.CopyTo(output, 0);
        Buffer.BlockCopy(body, nameOffset, output, nameOffset, checked((int)(bodyEnd - nameOffset)));
        export.CopyTo(output, bodyEnd);

        int field = pkg.ExportSerialFieldAt[exportIndex];
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field), export.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field + 4), checked((int)bodyEnd));
        return output;
    }

    /// <summary>
    /// Re-reads a written package and compares it with the original: summary is uncompressed and
    /// ends at the name table, names/imports/exports match, every export but the replaced one has
    /// identical bytes, and the replaced one matches <paramref name="expected"/>. Returns problems (empty = pass).
    /// </summary>
    public static List<string> Verify(Package original, Package written, int exportIndex, byte[] expected)
    {
        var problems = new List<string>();
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }

        Check(written.Chunks.Count == 0, $"written package still has {written.Chunks.Count} chunks");
        Check(written.CompressionFlags == 0, $"CompressionFlags = {written.CompressionFlags}");
        Check(written.SummaryEnd == written.NameOffset, $"summary ends at {written.SummaryEnd}, names start at {written.NameOffset}");
        Check(written.FileVersion == original.FileVersion && written.LicenseeVersion == original.LicenseeVersion, "version changed");
        Check(written.Names.SequenceEqual(original.Names), "name table differs");
        Check(written.Imports.SequenceEqual(original.Imports), "import table differs");
        Check(written.Exports.Length == original.Exports.Length, "export count differs");

        int different = 0;
        for (int i = 0; i < Math.Min(written.Exports.Length, original.Exports.Length); i++)
        {
            var a = original.Exports[i]; var b = written.Exports[i];
            if (i == exportIndex)
            {
                Check(a with { SerialSize = 0, SerialOffset = 0 } == b with { SerialSize = 0, SerialOffset = 0 }, "replaced export's table entry changed beyond size/offset");
                Check(written.ReadExportBytes(b).AsSpan().SequenceEqual(expected), "replaced export's bytes don't match what was built");
                continue;
            }
            if (a != b) { problems.Add($"export {i} ({a.ObjectName}) table entry differs"); continue; }
            if (!original.ReadExportBytes(a).AsSpan().SequenceEqual(written.ReadExportBytes(b))) different++;
        }
        Check(different == 0, $"{different} other export(s) have different bytes");
        return problems;
    }
}
