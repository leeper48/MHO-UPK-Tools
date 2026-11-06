using System.Buffers.Binary;

using MhoPackageModifier;

namespace MhoExtendedModManager.FreeCam;

/// <summary>
/// Writes a package with some exports replaced, uncompressed (MHO Package Modifier's PackageWriter rules, proven in game):
/// chunk table dropped, CompressionFlags 0, StoreCompressed cleared, the summary ends at NameOffset; every other byte keeps
/// its position; replacements are appended at the end and only their export-table entries change.
/// </summary>
static class FreeCamPackageEdit
{
    const uint StoreCompressed = 0x02000000;

    static byte[] UncompressedSummary(Package pkg)
    {
        byte[] file = pkg.RawFile;
        byte[] header;
        if (pkg.Chunks.Count > 0)
        {
            if (pkg.ChunkSource != "header" || pkg.ChunkTableStart < 0 || pkg.SummaryEnd < 0)
                throw new PackageFormatException("package summary could not be fully parsed; not writing it");
            using var ms = new MemoryStream();
            ms.Write(file, 0, pkg.ChunkTableStart);
            ms.Write(BitConverter.GetBytes(0));
            ms.Write(file, pkg.ChunkTableEnd, pkg.SummaryEnd - pkg.ChunkTableEnd);
            header = ms.ToArray();
        }
        else header = file.AsSpan(0, pkg.NameOffset).ToArray();
        if (header.Length != pkg.NameOffset)
            throw new PackageFormatException($"uncompressed summary is {header.Length} bytes but names start at {pkg.NameOffset}");
        if (pkg.CompressionFlagsAt >= 0) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pkg.CompressionFlagsAt), 0);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pkg.PackageFlagsAt));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(pkg.PackageFlagsAt), flags & ~StoreCompressed);
        return header;
    }

    public static byte[] ReplaceExports(Package pkg, IReadOnlyDictionary<int, byte[]> replacements)
    {
        byte[] body = pkg.Body;
        byte[] header = UncompressedSummary(pkg);
        long bodyEnd = pkg.Chunks.Count > 0 ? pkg.Chunks.Max(c => (long)c.UncompOffset + c.UncompSize) : pkg.RawFile.Length;
        long total = bodyEnd + replacements.Values.Sum(v => (long)v.Length);
        var output = new byte[checked((int)total)];
        header.CopyTo(output, 0);
        Buffer.BlockCopy(body, pkg.NameOffset, output, pkg.NameOffset, checked((int)(bodyEnd - pkg.NameOffset)));
        long at = bodyEnd;
        foreach (var (index, bytes) in replacements.OrderBy(r => r.Key))
        {
            bytes.CopyTo(output, at);
            int field = pkg.ExportSerialFieldAt[index];
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field + 4), checked((int)at));
            at += bytes.Length;
        }
        return output;
    }

    /// <summary>Problems (empty = pass): tables equal, every other export byte-identical, replaced ones as built.</summary>
    public static List<string> Verify(Package original, Package written, IReadOnlyDictionary<int, byte[]> replacements)
    {
        var problems = new List<string>();
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }
        Check(written.Chunks.Count == 0, "written package still has chunks");
        Check(written.CompressionFlags == 0, $"CompressionFlags = {written.CompressionFlags}");
        Check(written.Names.SequenceEqual(original.Names), "name table differs");
        Check(written.Imports.SequenceEqual(original.Imports), "import table differs");
        Check(written.Exports.Length == original.Exports.Length, "export count differs");
        int different = 0;
        for (int i = 0; i < Math.Min(written.Exports.Length, original.Exports.Length); i++)
        {
            var a = original.Exports[i]; var b = written.Exports[i];
            if (replacements.TryGetValue(i, out var expected))
            {
                Check(a with { SerialSize = 0, SerialOffset = 0 } == b with { SerialSize = 0, SerialOffset = 0 }, $"{a.ObjectName}: table entry changed beyond size/offset");
                Check(written.ReadExportBytes(b).AsSpan().SequenceEqual(expected), $"{a.ObjectName}: bytes don't match what was built");
                continue;
            }
            if (a != b) { problems.Add($"export {i} ({a.ObjectName}) table entry differs"); continue; }
            if (!original.ReadExportBytes(a).AsSpan().SequenceEqual(written.ReadExportBytes(b))) different++;
        }
        Check(different == 0, $"{different} other export(s) have different bytes");
        return problems;
    }
}

