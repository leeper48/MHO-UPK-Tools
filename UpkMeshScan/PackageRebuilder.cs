using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>A new export: a copy of an existing export-table entry under a new name instance number, with new data.</summary>
public sealed record NewExport(int TemplateIndex, int NameNumber, Func<long, byte[]> Build);

/// <summary>
/// Rewrites a whole package, uncompressed, so exports can be added (PackageWriter can only replace one).
/// Layout checked on MidTown_Static (and required here): summary | names | imports | exports | depends |
/// export data, with no gaps, ImportExportGuids empty and pointing at TotalHeaderSize, no thumbnail table.
/// The output keeps names and imports byte for byte, copies every existing export-table entry (only
/// SerialSize/SerialOffset change), appends the new entries (and an empty depends entry each), and writes
/// all export data packed in order. Stale "offset in file" fields inside moved data (inline texture mips,
/// empty mesh bulk data) are left as is: the other tools' working packages leave them stale too (all 27
/// stock textures in the modded WinterSoldier package), so the game doesn't rely on them.
/// </summary>
static class PackageRebuilder
{
    public static byte[] Rebuild(Package pkg, IReadOnlyDictionary<int, Func<long, byte[]>> replace, IReadOnlyList<NewExport> add,
        out Dictionary<int, byte[]> writtenData)
    {
        CheckLayout(pkg);
        byte[] body = pkg.Body;
        byte[] summary = PackageWriter.UncompressedSummary(pkg);
        int nameOffset = pkg.NameOffset, exportOffset = pkg.ExportOffset, oldExports = pkg.Exports.Length;

        // Export-table entries: existing ones copied, new ones cloned from their template with a new name number.
        var entries = new List<byte[]>();
        for (int i = 0; i < oldExports; i++) entries.Add(body.AsSpan(pkg.ExportEntryStart[i], pkg.ExportEntryEnd[i] - pkg.ExportEntryStart[i]).ToArray());
        foreach (var a in add)
        {
            byte[] e = entries[a.TemplateIndex].ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), a.NameNumber);     // Class, Super, Outer, Name(index, number)
            entries.Add(e);
        }
        int tableLength = entries.Sum(e => e.Length);
        byte[] depends = body.AsSpan(pkg.DependsOffset, pkg.TotalHeaderSize - pkg.DependsOffset).ToArray();
        int dependsOffset = exportOffset + tableLength;
        int headerSize = dependsOffset + depends.Length + 4 * add.Count;

        // Export data, packed in the original data order, then the new exports.
        var order = Enumerable.Range(0, oldExports).OrderBy(i => pkg.Exports[i].SerialOffset).ThenBy(i => i).ToList();
        order.AddRange(Enumerable.Range(oldExports, add.Count));
        writtenData = new Dictionary<int, byte[]>();
        long offset = headerSize;
        var placedAt = new Dictionary<int, long>();
        foreach (int i in order)
        {
            byte[] data;
            Func<long, byte[]>? build = i < oldExports ? (replace.TryGetValue(i, out var r) ? r : null) : add[i - oldExports].Build;
            if (build == null) data = pkg.ReadExportBytes(pkg.Exports[i]);
            else
            {
                int size = build(offset).Length;                         // data may embed its own offset; size must not depend on it
                data = build(offset);
                if (data.Length != size) throw new InvalidDataException($"export {i}: size changed with its offset");
            }
            writtenData[i] = data;
            placedAt[i] = offset;
            offset += data.Length;
        }

        var output = new byte[checked((int)offset)];
        summary.CopyTo(output, 0);
        body.AsSpan(nameOffset, exportOffset - nameOffset).CopyTo(output.AsSpan(nameOffset));     // names + imports, as they were
        int at = exportOffset;
        for (int i = 0; i < entries.Count; i++)
        {
            int serialField = (i < oldExports ? pkg.ExportSerialFieldAt[i] - pkg.ExportEntryStart[i] : pkg.ExportSerialFieldAt[add[i - oldExports].TemplateIndex] - pkg.ExportEntryStart[add[i - oldExports].TemplateIndex]);
            BinaryPrimitives.WriteInt32LittleEndian(entries[i].AsSpan(serialField), writtenData[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entries[i].AsSpan(serialField + 4), checked((int)placedAt[i]));
            entries[i].CopyTo(output, at);
            at += entries[i].Length;
        }
        depends.CopyTo(output, dependsOffset);                            // new exports: empty depends lists (count 0)
        foreach (int i in order) writtenData[i].CopyTo(output, placedAt[i]);

        // Summary fields that change.
        var s = output.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.TotalHeaderSizeAt..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[(pkg.TableCountsAt + 8)..], entries.Count);   // ExportCount
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.DependsOffsetAt..], dependsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.ImportExportGuidsAt..], headerSize);
        int gens = BinaryPrimitives.ReadInt32LittleEndian(s[pkg.GenerationsAt..]);
        int lastGen = pkg.GenerationsAt + 4 + (gens - 1) * 12;
        BinaryPrimitives.WriteInt32LittleEndian(s[lastGen..], entries.Count);                  // generation ExportCount
        return output;
    }

    static void CheckLayout(Package pkg)
    {
        byte[] b = pkg.Body;
        void Need(bool ok, string what) { if (!ok) throw new PackageFormatException($"package layout not as expected for a rebuild: {what}"); }
        int n = pkg.Exports.Length;
        Need(pkg.FileVersion >= 623, "file version");
        Need(pkg.SummaryEnd > 0 && pkg.TotalHeaderSizeAt > 0 && pkg.DependsOffsetAt > 0 && pkg.GenerationsAt > 0, "summary fields");
        Need(pkg.NameOffset < pkg.ImportOffset && pkg.ImportOffset < pkg.ExportOffset && pkg.ExportOffset < pkg.DependsOffset, "table order");
        Need(n > 0 && pkg.ExportEntryStart[0] == pkg.ExportOffset && pkg.ExportEntryEnd[n - 1] == pkg.DependsOffset, "export table not contiguous with depends");
        int r = pkg.DependsOffset;
        for (int i = 0; i < n; i++) r += 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(r));
        Need(r == pkg.TotalHeaderSize, "depends table doesn't end at TotalHeaderSize");
        Need(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pkg.ImportExportGuidsAt)) == pkg.TotalHeaderSize
             && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pkg.ImportExportGuidsAt + 4)) == 0
             && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pkg.ImportExportGuidsAt + 8)) == 0, "ImportExportGuids not empty");
        Need(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pkg.ThumbnailTableAt)) == 0, "thumbnail table present");
        Need(pkg.Exports.All(e => e.SerialSize == 0 || e.SerialOffset >= pkg.TotalHeaderSize), "export data inside the header");
    }

    /// <summary>
    /// Re-reads a rebuilt package and compares it with the original: uncompressed summary ending at the names,
    /// names and imports identical, every original export's table entry identical apart from size/offset,
    /// untouched exports' data byte-identical, replaced/added exports' data exactly as built, depends lists
    /// kept, header size = first data offset. Returns problems (empty = pass).
    /// </summary>
    public static List<string> Verify(Package original, byte[] rebuilt, IReadOnlyCollection<int> replaced, IReadOnlyList<NewExport> add, IReadOnlyDictionary<int, byte[]> writtenData)
    {
        var problems = new List<string>();
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }
        Package w;
        try { w = Package.FromBytes(rebuilt); }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { return [$"rebuilt package doesn't open: {ex.Message}"]; }

        Check(w.Chunks.Count == 0 && w.CompressionFlags == 0, "not uncompressed");
        Check(w.SummaryEnd == w.NameOffset, "summary doesn't end at the name table");
        Check(w.Names.SequenceEqual(original.Names), "name table differs");
        Check(w.Imports.SequenceEqual(original.Imports), "import table differs");
        Check(w.Exports.Length == original.Exports.Length + add.Count, $"export count {w.Exports.Length}, expected {original.Exports.Length + add.Count}");
        Check(w.Exports.Where(e => e.SerialSize > 0).Min(e => e.SerialOffset) == w.TotalHeaderSize, "TotalHeaderSize isn't where the data starts");
        int different = 0;
        for (int i = 0; i < Math.Min(original.Exports.Length, w.Exports.Length); i++)
        {
            var a = original.Exports[i]; var b = w.Exports[i];
            Check(a with { SerialSize = 0, SerialOffset = 0 } == b with { SerialSize = 0, SerialOffset = 0 }, $"export {i} entry changed");
            byte[] data = w.ReadExportBytes(b);
            if (replaced.Contains(i)) Check(data.AsSpan().SequenceEqual(writtenData[i]), $"replaced export {i} not as built");
            else if (!data.AsSpan().SequenceEqual(original.ReadExportBytes(a))) different++;
        }
        Check(different == 0, $"{different} untouched export(s) have different bytes");
        for (int k = 0; k < add.Count; k++)
        {
            int i = original.Exports.Length + k;
            if (i >= w.Exports.Length) break;
            var t = original.Exports[add[k].TemplateIndex]; var e = w.Exports[i];
            Check(e.ClassIndex == t.ClassIndex && e.OuterIndex == t.OuterIndex, $"new export {i}: class/outer differ from its template");
            Check(w.ReadExportBytes(e).AsSpan().SequenceEqual(writtenData[i]), $"new export {i} not as built");
        }
        // Depends: the original lists unchanged, new ones empty.
        int ro = original.DependsOffset, rw = w.DependsOffset;
        byte[] ob = original.Body, wb = w.Body;
        for (int i = 0; i < original.Exports.Length; i++)
        {
            int no = 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(ob.AsSpan(ro)), nw = 4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(wb.AsSpan(rw));
            if (no != nw || !ob.AsSpan(ro, no).SequenceEqual(wb.AsSpan(rw, nw))) { problems.Add($"depends list {i} differs"); break; }
            ro += no; rw += nw;
        }
        for (int k = 0; k < add.Count; k++) { Check(BinaryPrimitives.ReadInt32LittleEndian(wb.AsSpan(rw)) == 0, "new depends list not empty"); rw += 4; }
        Check(rw == w.TotalHeaderSize, "depends table doesn't end at TotalHeaderSize");
        return problems;
    }
}
