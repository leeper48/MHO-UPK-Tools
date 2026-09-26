using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// --import-texture: adds a new Texture2D to a package from a .dds (DXT1/DXT5), as other mod tools inject textures
/// (e.g. the WinterSoldier package's 2048x2048 DXT1 maps, which load in game): mips stored inline in the package, no
/// texture cache. Every mip in the .dds is stored (stock textures carry several inline mips too); a .dds without a
/// mip chain gives one full-size mip, which shimmers from a distance (Industry City's baked ground plane, 2026-09-26).
/// publish/scans/tools/make_mips.py writes a DXT1 .dds with the full chain. The new export's table entry is the template texture's under a
/// new name (same class and outer). Properties: SizeX, SizeY, OriginalSizeX, OriginalSizeY, Format, NeverStream
/// = true, and the template's LODGroup — no TextureFileCacheName / MipTailBaseIdx / FirstResourceMemMip, like
/// the injected ones. Native data as theirs: empty source-art bulk header (offset -1), one mip (flags 0, count =
/// size = data length, offset = the data's own position in the file, the data, width, height) per mip, then the
/// template's trailing 48 bytes with the cache GUID zeroed. Same .bak / verified temp / swap as the other writers;
/// verified by reading the texture back (every mip inline, pixels identical to the .dds, offsets pointing at them).
/// </summary>
static class TextureImport
{
    public static int Run(string upkPath, string templatePath, string newName, string ddsPath, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Import texture: {Path.GetFileName(ddsPath)} -> {Path.GetFileName(upkPath)} as '{newName}' (template {templatePath}){(dryRun ? "  [dry run]" : "")}");

        int template = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(templatePath, StringComparison.OrdinalIgnoreCase)
            && pkg.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
        if (template < 0) { Console.WriteLine($"  no Texture2D '{templatePath}' in the package (give the full path)"); return 2; }
        var t = pkg.Exports[template];
        string outerPath = t.OuterIndex > 0 ? pkg.PathOf(pkg.Exports[t.OuterIndex - 1]) + "." : "";
        if (pkg.Exports.Any(e => pkg.PathOf(e).Equals(outerPath + newName, StringComparison.OrdinalIgnoreCase)))
        { Console.WriteLine($"  '{outerPath + newName}' already exists in the package"); return 1; }

        // The .dds: header, pixel format, top mip.
        byte[] dds = File.ReadAllBytes(ddsPath);
        if (dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444) { Console.WriteLine("  not a .dds file"); return 2; }
        int height = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12)), width = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16));
        string fourCC = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        int blockBytes = fourCC switch { "DXT1" => 8, "DXT5" => 16, _ => 0 };
        if (blockBytes == 0) { Console.WriteLine($"  pixel format '{fourCC}' not supported (DXT1 or DXT5)"); return 2; }
        if (width % 4 != 0 || height % 4 != 0) { Console.WriteLine($"  {width}x{height}: DXT needs sizes divisible by 4"); return 2; }
        if ((width & (width - 1)) != 0 || (height & (height - 1)) != 0) Console.WriteLine($"  warning: {width}x{height} isn't a power of two");
        // Mip chain: DDSD_MIPMAPCOUNT (0x20000) in the header flags, then the count at byte 28; each level halves
        // (at least 1), stored in 4x4 blocks (at least one).
        int mipCount = (BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(8)) & 0x20000) != 0 ? Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(28))) : 1;
        var levels = new List<(int W, int H, byte[] Pixels)>();
        int at = 128;
        for (int m = 0, w = width, h = height; m < mipCount; m++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            int len = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
            if (dds.Length < at + len) { Console.WriteLine($"  .dds too short for mip {m} ({w}x{h} {fourCC})"); return 2; }
            levels.Add((w, h, dds.AsSpan(at, len).ToArray()));
            at += len;
        }
        string format = "PF_" + fourCC;
        Console.WriteLine($"  .dds: {width}x{height} {fourCC}, {levels.Count} mip(s) ({levels[^1].W}x{levels[^1].H} smallest), {levels.Sum(l => l.Pixels.Length):N0} bytes");

        // Names the new export needs.
        var addNames = new List<string>();
        foreach (string n in new[] { newName, "NeverStream", "BoolProperty", "IntProperty", "ByteProperty", "EPixelFormat", format })
            if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
        var tw = new TagWriter(pkg, addNames);

        // Template: its LODGroup tag (copied as is) and its trailing bytes after the mip array.
        byte[] td = pkg.ReadExportBytes(t);
        var tags = TagWalker.Walk(pkg, td, 4) ?? throw new InvalidDataException("template properties don't parse");
        var lod = tags.FirstOrDefault(x => x.Name.Equals("LODGroup", StringComparison.OrdinalIgnoreCase));
        int p = tags.NoneAt + 8 + 16;                                       // after "None" and the source-art header
        int mips = BinaryPrimitives.ReadInt32LittleEndian(td.AsSpan(p)); p += 4;
        for (int m = 0; m < mips; m++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(td.AsSpan(p)); int size = BinaryPrimitives.ReadInt32LittleEndian(td.AsSpan(p + 8));
            p += 16 + ((flags & 0x21) == 0 && size > 0 ? size : 0) + 8;
        }
        byte[] tail = td.AsSpan(p).ToArray();
        if (tail.Length != 48) { Console.WriteLine($"  template's data after the mips is {tail.Length} bytes, expected 48"); return 1; }
        Array.Clear(tail, 0, 16);                                           // TextureFileCacheGuid: none

        static byte[] I(int v) => BitConverter.GetBytes(v);
        using var props = new MemoryStream();
        props.Write(td, 0, 4);                                              // NetIndex
        props.Write(tw.Tag("SizeX", "IntProperty", null, I(width)));
        props.Write(tw.Tag("SizeY", "IntProperty", null, I(height)));
        props.Write(tw.Tag("OriginalSizeX", "IntProperty", null, I(width)));
        props.Write(tw.Tag("OriginalSizeY", "IntProperty", null, I(height)));
        props.Write(tw.Tag("Format", "ByteProperty", "EPixelFormat", tw.NameRef(format)));
        props.Write(tw.NameRef("NeverStream")); props.Write(tw.NameRef("BoolProperty")); props.Write(I(0)); props.Write(I(0)); props.WriteByte(1);
        if (lod != null) props.Write(td, lod.Start, lod.End - lod.Start);
        props.Write(tw.NameRef("None"));
        byte[] head = props.ToArray();
        byte[] Build(long offset)
        {
            using var ms = new MemoryStream();
            ms.Write(head);
            ms.Write(I(0)); ms.Write(I(0)); ms.Write(I(0)); ms.Write(I(-1));   // source-art bulk data: empty
            ms.Write(I(levels.Count));
            foreach (var (w, h, px) in levels)
            {
                ms.Write(I(0)); ms.Write(I(px.Length)); ms.Write(I(px.Length)); ms.Write(I(checked((int)(offset + ms.Position + 4))));
                ms.Write(px);
                ms.Write(I(w)); ms.Write(I(h));
            }
            ms.Write(tail);
            return ms.ToArray();
        }

        byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[template], pkg.ExportEntryEnd[template] - pkg.ExportEntryStart[template]).ToArray();
        int nameIndex = Array.FindIndex(pkg.Names, x => x.Equals(newName, StringComparison.OrdinalIgnoreCase));
        if (nameIndex < 0) nameIndex = pkg.Names.Length + addNames.FindIndex(x => x.Equals(newName, StringComparison.OrdinalIgnoreCase));
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), nameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
        var add = new List<NewExport> { new(template, 0, Build) { Entry = entry } };

        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), add, out var written, addNames);
        int newIndex = pkg.Exports.Length;
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [], add, written, addNames);
            var w = Package.FromBytes(bytes);
            var e = w.Exports[newIndex];
            if (!w.PathOf(e).Equals(outerPath + newName, StringComparison.OrdinalIgnoreCase)) problems.Add($"new export is {w.PathOf(e)}");
            try
            {
                var ti = TextureInfo.Read(w, e);
                if (ti.SizeX != width || ti.SizeY != height || !ti.Format.Equals(format, StringComparison.OrdinalIgnoreCase)) problems.Add($"reads back as {ti}");
                if (ti.Mips.Count != levels.Count || ti.Mips.Any(m => !m.Inline)) problems.Add($"{ti.Mips.Count} mip(s), expected {levels.Count} inline");
                else
                    for (int k = 0; k < levels.Count; k++)
                    {
                        var m = ti.Mips[k];
                        if (!ti.Data.AsSpan(m.InlineAt, m.Size).SequenceEqual(levels[k].Pixels)) problems.Add($"mip {k}: pixels differ from the .dds");
                        if (m.Offset != e.SerialOffset + m.InlineAt) problems.Add($"mip {k}: offset doesn't point at its data");
                        if (m.Width != levels[k].W || m.Height != levels[k].H) problems.Add($"mip {k}: size wrong");
                    }
                if (!string.IsNullOrEmpty(ti.Cache)) problems.Add($"has a texture cache '{ti.Cache}'");
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { problems.Add($"doesn't read back: {ex.Message}"); }
            return problems;
        }
        var problems = Check(output);
        Console.WriteLine($"  new export #{newIndex + 1} {outerPath}{newName}; names added: {(addNames.Count == 0 ? "none" : string.Join(", ", addNames))}");
        Console.WriteLine($"  package: {pkg.RawFile.Length:N0} -> {output.Length:N0} bytes");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (tables = original + additions, existing exports byte-identical; texture reads back with {levels.Count} inline mip(s), pixels identical to the .dds, offsets pointing at them, no cache)");

        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(target, output);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }
}
