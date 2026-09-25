using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>A new export: a copy of an existing export-table entry under a new name instance number, with new data.</summary>
public sealed record NewExport(int TemplateIndex, int NameNumber, Func<long, byte[]> Build)
{
    /// <summary>
    /// A complete export-table entry to use instead of cloning the template (e.g. an object copied from another
    /// package). Its SerialSize/SerialOffset (at byte 32: Class, Super, Outer, Name, Archetype, ObjectFlags) are filled in.
    /// </summary>
    public byte[]? Entry { get; init; }
}

/// <summary>
/// A new import-table entry. Names must already be in the name table or among the added names (number 0).
/// OuterIndex uses package references: 0 = none, -(k+1) = import k (a new import may point at an earlier new one),
/// k+1 = export k (seen in stock packages: imports inside a forced-export package, e.g. a cubemap from MarvelGame.upk).
/// </summary>
public sealed record NewImport(string ClassPackage, string ClassName, int OuterIndex, string ObjectName);

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
        out Dictionary<int, byte[]> writtenData, IReadOnlyList<string>? addNames = null, IReadOnlyList<NewImport>? addImports = null)
    {
        CheckLayout(pkg);
        byte[] body = pkg.Body;
        byte[] summary = PackageWriter.UncompressedSummary(pkg);
        int nameOffset = pkg.NameOffset, oldExports = pkg.Exports.Length;

        // New names go after the existing ones (existing indices keep their meaning); imports and exports move down.
        // New imports go after the existing ones too, so every existing reference (negative = import) keeps its meaning.
        byte[] newNames = NameEntries(pkg, addNames ?? []);
        byte[] newImports = ImportEntries(pkg, addNames ?? [], addImports ?? [], oldExports + add.Count);
        int shift = newNames.Length;
        int importOffset = pkg.ImportOffset + shift, exportOffset = pkg.ExportOffset + shift + newImports.Length;

        // Export-table entries: existing ones copied, new ones cloned from their template with a new name number.
        var entries = new List<byte[]>();
        for (int i = 0; i < oldExports; i++) entries.Add(body.AsSpan(pkg.ExportEntryStart[i], pkg.ExportEntryEnd[i] - pkg.ExportEntryStart[i]).ToArray());
        foreach (var a in add)
        {
            if (a.Entry != null) { entries.Add(a.Entry.ToArray()); continue; }
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
        body.AsSpan(nameOffset, pkg.ImportOffset - nameOffset).CopyTo(output.AsSpan(nameOffset));                 // names, as they were
        newNames.CopyTo(output, pkg.ImportOffset);                                                                  // + added names
        body.AsSpan(pkg.ImportOffset, pkg.ExportOffset - pkg.ImportOffset).CopyTo(output.AsSpan(importOffset));     // imports, as they were
        newImports.CopyTo(output, importOffset + pkg.ExportOffset - pkg.ImportOffset);                             // + added imports
        int at = exportOffset;
        for (int i = 0; i < entries.Count; i++)
        {
            int serialField = i < oldExports ? pkg.ExportSerialFieldAt[i] - pkg.ExportEntryStart[i]
                : add[i - oldExports].Entry != null ? RawEntrySerialField
                : pkg.ExportSerialFieldAt[add[i - oldExports].TemplateIndex] - pkg.ExportEntryStart[add[i - oldExports].TemplateIndex];
            BinaryPrimitives.WriteInt32LittleEndian(entries[i].AsSpan(serialField), writtenData[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entries[i].AsSpan(serialField + 4), checked((int)placedAt[i]));
            entries[i].CopyTo(output, at);
            at += entries[i].Length;
        }
        depends.CopyTo(output, dependsOffset);                            // new exports: empty depends lists (count 0)
        foreach (int i in order) writtenData[i].CopyTo(output, placedAt[i]);

        // Summary fields that change.
        var s = output.AsSpan();
        int names = pkg.Names.Length + (addNames?.Count ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.TableCountsAt..], names);                           // NameCount
        BinaryPrimitives.WriteInt32LittleEndian(s[(pkg.TableCountsAt + 12)..], exportOffset);              // ExportOffset
        BinaryPrimitives.WriteInt32LittleEndian(s[(pkg.TableCountsAt + 16)..], pkg.Imports.Length + (addImports?.Count ?? 0)); // ImportCount
        BinaryPrimitives.WriteInt32LittleEndian(s[(pkg.TableCountsAt + 20)..], importOffset);              // ImportOffset
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.TotalHeaderSizeAt..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[(pkg.TableCountsAt + 8)..], entries.Count);   // ExportCount
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.DependsOffsetAt..], dependsOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[pkg.ImportExportGuidsAt..], headerSize);
        int gens = BinaryPrimitives.ReadInt32LittleEndian(s[pkg.GenerationsAt..]);
        int lastGen = pkg.GenerationsAt + 4 + (gens - 1) * 12;
        BinaryPrimitives.WriteInt32LittleEndian(s[lastGen..], entries.Count);                  // generation ExportCount
        BinaryPrimitives.WriteInt32LittleEndian(s[(lastGen + 4)..], names);                    // generation NameCount
        return output;
    }

    /// <summary>Where SerialSize sits in a v868 export entry: Class, Super, Outer (4 each), Name (8), Archetype (4), ObjectFlags (8).</summary>
    public const int RawEntrySerialField = 32;

    /// <summary>Name-table entries for new names: FString (ASCII, null-terminated) + 64-bit flags copied from an existing name.</summary>
    static byte[] NameEntries(Package pkg, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return [];
        foreach (string n in names)
        {
            if (pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"name '{n}' is already in the table");
            if (n.Any(c => c > 127)) throw new InvalidDataException($"name '{n}' isn't ASCII");
        }
        // Flags of the last existing entry (the name table is FString + flags per entry).
        byte[] b = pkg.Body;
        int p = pkg.NameOffset;
        ulong flags = 0;
        for (int i = 0; i < pkg.Names.Length; i++)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
            p += 4 + (len >= 0 ? len : -2 * len);
            flags = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
            p += 8;
        }
        if (p != pkg.ImportOffset) throw new PackageFormatException("name table doesn't end where the import table starts");
        using var ms = new MemoryStream();
        foreach (string n in names)
        {
            ms.Write(BitConverter.GetBytes(n.Length + 1));
            ms.Write(System.Text.Encoding.ASCII.GetBytes(n));
            ms.WriteByte(0);
            ms.Write(BitConverter.GetBytes(flags));
        }
        return ms.ToArray();
    }

    /// <summary>Import-table entries: ClassPackage, ClassName (FName = index + number), OuterIndex, ObjectName.</summary>
    static byte[] ImportEntries(Package pkg, IReadOnlyList<string> addNames, IReadOnlyList<NewImport> imports, int exportCount)
    {
        if (imports.Count == 0) return [];
        int NameIndex(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int k = addNames.ToList().FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return pkg.Names.Length + k;
            throw new InvalidDataException($"import name '{n}' is neither in the name table nor added");
        }
        using var ms = new MemoryStream();
        for (int k = 0; k < imports.Count; k++)
        {
            var im = imports[k];
            if (im.OuterIndex > exportCount || -im.OuterIndex > pkg.Imports.Length + k) throw new InvalidDataException($"import '{im.ObjectName}': outer {im.OuterIndex} isn't an earlier import or an export");
            foreach (int v in new[] { NameIndex(im.ClassPackage), 0, NameIndex(im.ClassName), 0, im.OuterIndex, NameIndex(im.ObjectName), 0 })
                ms.Write(BitConverter.GetBytes(v));
        }
        return ms.ToArray();
    }

    sealed class ImportComparer : IEqualityComparer<ImportEntry>
    {
        public bool Equals(ImportEntry a, ImportEntry b) => a.OuterIndex == b.OuterIndex
            && a.ClassName.Equals(b.ClassName, StringComparison.OrdinalIgnoreCase) && a.ObjectName.Equals(b.ObjectName, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ImportEntry e) => e.OuterIndex;
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
    public static List<string> Verify(Package original, byte[] rebuilt, IReadOnlyCollection<int> replaced, IReadOnlyList<NewExport> add, IReadOnlyDictionary<int, byte[]> writtenData,
        IReadOnlyList<string>? addNames = null, IReadOnlyList<NewImport>? addImports = null)
    {
        var problems = new List<string>();
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }
        Package w;
        try { w = Package.FromBytes(rebuilt); }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { return [$"rebuilt package doesn't open: {ex.Message}"]; }

        Check(w.Chunks.Count == 0 && w.CompressionFlags == 0, "not uncompressed");
        Check(w.SummaryEnd == w.NameOffset, "summary doesn't end at the name table");
        Check(w.Names.SequenceEqual(original.Names.Concat(addNames ?? [])), "name table isn't the original plus the added names");
        Check(w.Imports.SequenceEqual(original.Imports.Concat((addImports ?? []).Select(i => new ImportEntry(i.ClassName, i.OuterIndex, i.ObjectName))),
            new ImportComparer()), "import table isn't the original plus the added imports");
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
            var e = w.Exports[i];
            if (add[k].Entry is { } raw)
                Check(e.ClassIndex == BinaryPrimitives.ReadInt32LittleEndian(raw) && e.OuterIndex == BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8)), $"new export {i}: class/outer not as given");
            else
            {
                var t = original.Exports[add[k].TemplateIndex];
                Check(e.ClassIndex == t.ClassIndex && e.OuterIndex == t.OuterIndex, $"new export {i}: class/outer differ from its template");
            }
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
