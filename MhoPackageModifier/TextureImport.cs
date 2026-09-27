using System.Buffers.Binary;

namespace MhoPackageModifier;

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
/// <see cref="ReplaceMany"/> does the --replace-texture rebuild for many textures of one package at once (MHO Extended
/// Mod Manager's icon mods).
/// </summary>
static class TextureImport
{
    /// <summary>A parsed .dds: top size, FourCC (DXT1 / DXT5) and every stored mip level.</summary>
    public sealed record DdsImage(int Width, int Height, string FourCC, List<(int W, int H, byte[] Pixels)> Levels)
    {
        public string Format => "PF_" + FourCC;
    }

    /// <summary>A DDS file (header + mip chain) for encoded levels, so images take the same path as .dds files.</summary>
    static byte[] WriteDds(TextureEncode.Result r)
    {
        var h = new byte[128];
        void U(int at, uint v) => BitConverter.GetBytes(v).CopyTo(h, at);
        U(0, 0x20534444); U(4, 124); U(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        U(12, (uint)r.Height); U(16, (uint)r.Width); U(20, (uint)r.Levels[0].Data.Length); U(28, (uint)r.Levels.Count);
        U(76, 32); U(80, 4); System.Text.Encoding.ASCII.GetBytes(r.FourCC).CopyTo(h, 84);
        U(108, 0x1000 | 0x400000 | 0x8);
        using var ms = new MemoryStream();
        ms.Write(h);
        foreach (var l in r.Levels) ms.Write(l.Data);
        return ms.ToArray();
    }

    /// <summary>The .dds: header, pixel format (DXT1 or DXT5) and its mip chain. Null with the reason if it can't be used.</summary>
    public static DdsImage? ParseDds(byte[] dds, out string? error)
    {
        error = null;
        if (dds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(dds) != 0x20534444) { error = "not a .dds file"; return null; }
        int height = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(12)), width = BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(16));
        string fourCC = System.Text.Encoding.ASCII.GetString(dds, 84, 4);
        int blockBytes = fourCC switch { "DXT1" => 8, "DXT5" => 16, _ => 0 };
        if (blockBytes == 0) { error = $"pixel format '{fourCC}' not supported (DXT1 or DXT5)"; return null; }
        if (width % 4 != 0 || height % 4 != 0) { error = $"{width}x{height}: DXT needs sizes divisible by 4"; return null; }
        // Mip chain: DDSD_MIPMAPCOUNT (0x20000) in the header flags, then the count at byte 28; each level halves
        // (at least 1), stored in 4x4 blocks (at least one).
        int mipCount = (BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(8)) & 0x20000) != 0 ? Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(dds.AsSpan(28))) : 1;
        var levels = new List<(int W, int H, byte[] Pixels)>();
        int at = 128;
        for (int m = 0, w = width, h = height; m < mipCount; m++, w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            int len = Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
            if (dds.Length < at + len) { error = $".dds too short for mip {m} ({w}x{h} {fourCC})"; return null; }
            levels.Add((w, h, dds.AsSpan(at, len).ToArray()));
            at += len;
        }
        return new DdsImage(width, height, fourCC, levels);
    }

    /// <summary>Names a texture export in this form refers to (added to the package's name table if missing).</summary>
    static IEnumerable<string> NamesFor(DdsImage img) => ["NeverStream", "BoolProperty", "IntProperty", "ByteProperty", "EPixelFormat", img.Format];

    static void AddMissingNames(Package pkg, List<string> addNames, IEnumerable<string> names)
    {
        foreach (string n in names)
            if (!pkg.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
    }

    /// <summary>
    /// The export data for <paramref name="img"/> based on the template texture: with <paramref name="replace"/> every
    /// other tag of the template is kept, otherwise only its LODGroup. Returns a builder for the data at a given file
    /// offset (the inline mip offsets point at their own data). Every name used must already be in <paramref name="tw"/>'s
    /// added names.
    /// </summary>
    static Func<long, byte[]>? Builder(Package pkg, int template, DdsImage img, TagWriter tw, bool replace, out string? error)
    {
        error = null;
        var t = pkg.Exports[template];
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
        if (tail.Length != 48) { error = $"template's data after the mips is {tail.Length} bytes, expected 48"; return null; }
        Array.Clear(tail, 0, 16);                                           // TextureFileCacheGuid: none

        static byte[] I(int v) => BitConverter.GetBytes(v);
        using var props = new MemoryStream();
        props.Write(td, 0, 4);                                              // NetIndex
        props.Write(tw.Tag("SizeX", "IntProperty", null, I(img.Width)));
        props.Write(tw.Tag("SizeY", "IntProperty", null, I(img.Height)));
        props.Write(tw.Tag("OriginalSizeX", "IntProperty", null, I(img.Width)));
        props.Write(tw.Tag("OriginalSizeY", "IntProperty", null, I(img.Height)));
        props.Write(tw.Tag("Format", "ByteProperty", "EPixelFormat", tw.NameRef(img.Format)));
        props.Write(tw.NameRef("NeverStream")); props.Write(tw.NameRef("BoolProperty")); props.Write(I(0)); props.Write(I(0)); props.WriteByte(1);
        if (replace)
        {
            // Every other setting of the original, as it was: only the tags describing the old size, format and storage go.
            string[] storage = ["SizeX", "SizeY", "OriginalSizeX", "OriginalSizeY", "Format", "NeverStream", "TextureFileCacheName", "MipTailBaseIdx",
                "FirstResourceMemMip", "bIsStreamable", "bHasBeenLoadedFromPersistentArchive", "TextureFileCacheGuid"];
            foreach (var tg in tags) if (!storage.Any(x => tg.Name.Equals(x, StringComparison.OrdinalIgnoreCase))) props.Write(td, tg.Start, tg.End - tg.Start);
        }
        else if (lod != null) props.Write(td, lod.Start, lod.End - lod.Start);
        props.Write(tw.NameRef("None"));
        byte[] head = props.ToArray();
        var levels = img.Levels;
        return offset =>
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
        };
    }

    /// <summary>Reads the texture back from a written package: path, size, format, every mip inline with the .dds pixels, offsets pointing at them, no cache.</summary>
    static List<string> CheckTexture(Package w, int index, string expectedPath, DdsImage img)
    {
        var problems = new List<string>();
        var e = w.Exports[index];
        if (!w.PathOf(e).Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) problems.Add($"new export is {w.PathOf(e)}");
        try
        {
            var ti = TextureInfo.Read(w, e);
            if (ti.SizeX != img.Width || ti.SizeY != img.Height || !ti.Format.Equals(img.Format, StringComparison.OrdinalIgnoreCase)) problems.Add($"reads back as {ti}");
            if (ti.Mips.Count != img.Levels.Count || ti.Mips.Any(m => !m.Inline)) problems.Add($"{ti.Mips.Count} mip(s), expected {img.Levels.Count} inline");
            else
                for (int k = 0; k < img.Levels.Count; k++)
                {
                    var m = ti.Mips[k];
                    if (!ti.Data.AsSpan(m.InlineAt, m.Size).SequenceEqual(img.Levels[k].Pixels)) problems.Add($"mip {k}: pixels differ from the .dds");
                    if (m.Offset != e.SerialOffset + m.InlineAt) problems.Add($"mip {k}: offset doesn't point at its data");
                    if (m.Width != img.Levels[k].W || m.Height != img.Levels[k].H) problems.Add($"mip {k}: size wrong");
                }
            if (!string.IsNullOrEmpty(ti.Cache)) problems.Add($"has a texture cache '{ti.Cache}'");
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { problems.Add($"doesn't read back: {ex.Message}"); }
        return problems;
    }

    /// <param name="replace">
    /// --replace-texture: the image replaces <paramref name="templatePath"/>'s own pixels (same export, same path, so every
    /// material using it shows the new image) instead of becoming a new texture. All of the original's settings are kept
    /// (sRGB, compression settings, address modes, LOD group ...) except the ones describing its size, format and
    /// storage; its mips go inline (NeverStream, no texture cache), as for a new texture.
    /// </param>
    public static int Run(string upkPath, string templatePath, string newName, string ddsPath, bool dryRun, string? encodeFormat = null, int split = 85, float scale = 1f, bool noMips = false, int maxSize = 0, bool replace = false)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        int template = Array.FindIndex(pkg.Exports, e => (pkg.PathOf(e).Equals(templatePath, StringComparison.OrdinalIgnoreCase) || (replace && e.ObjectName.Equals(templatePath, StringComparison.OrdinalIgnoreCase)))
            && pkg.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
        if (template < 0) { Console.WriteLine($"  no Texture2D '{templatePath}' in the package (give the full path)"); return 2; }
        var t = pkg.Exports[template];
        if (replace) newName = t.ObjectName;
        Console.WriteLine(replace
            ? $"Replace texture: {Path.GetFileName(ddsPath)} -> {Path.GetFileName(upkPath)} :: {pkg.PathOf(t)}{(dryRun ? "  [dry run]" : "")}"
            : $"Import texture: {Path.GetFileName(ddsPath)} -> {Path.GetFileName(upkPath)} as '{newName}' (template {templatePath}){(dryRun ? "  [dry run]" : "")}");
        string outerPath = t.OuterIndex > 0 ? pkg.PathOf(pkg.Exports[t.OuterIndex - 1]) + "." : "";
        if (!replace && pkg.Exports.Any(e => pkg.PathOf(e).Equals(outerPath + newName, StringComparison.OrdinalIgnoreCase)))
        { Console.WriteLine($"  '{outerPath + newName}' already exists in the package (to change its image, replace it instead)"); return 1; }
        if (replace)
            try
            {
                var old = TextureInfo.Read(pkg, t);
                Console.WriteLine($"  original: {old.SizeX}x{old.SizeY} {old.Format}{(string.IsNullOrEmpty(old.Cache) ? "" : $", large mips in {old.Cache}.tfc")}");
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { Console.WriteLine($"  original: header doesn't read ({ex.Message})"); }

        // An image (PNG, JPG, BMP) is converted here (TextureEncode: DXT1 / DXT5 with every mip); a .dds is taken as it is.
        string ext = Path.GetExtension(ddsPath).ToLowerInvariant();
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
        {
            var enc = TextureEncode.FromImage(ddsPath, encodeFormat, split, scale, noMips, maxSize);
            Console.WriteLine($"  {Path.GetFileName(ddsPath)}: {enc.Width}x{enc.Height} -> {enc.FourCC}, {enc.Levels.Count} mips{(scale != 1f ? $", colour x{scale}" : "")}{(enc.FourCC == "DXT1" ? $" (alpha cut at {split})" : "")}");
            ddsPath = Path.Combine(Path.GetTempPath(), $"mhopackagemodifier_{Guid.NewGuid():N}.dds");
            File.WriteAllBytes(ddsPath, WriteDds(enc));
        }
        byte[] dds = File.ReadAllBytes(ddsPath);
        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp") File.Delete(ddsPath);
        var img = ParseDds(dds, out string? ddsError);
        if (img == null) { Console.WriteLine("  " + ddsError); return 2; }
        int width = img.Width, height = img.Height;
        if ((width & (width - 1)) != 0 || (height & (height - 1)) != 0) Console.WriteLine($"  warning: {width}x{height} isn't a power of two");
        var levels = img.Levels;
        // --max-size N: start at the first stored level no bigger than N (the mip chain already holds it, filtered as the file
        // was made); --no-mips: that level only.
        if (maxSize > 0 && Math.Max(width, height) > maxSize)
        {
            int first = levels.FindIndex(l => Math.Max(l.W, l.H) <= maxSize);
            if (first < 0) { Console.WriteLine($"  --max-size {maxSize}: the .dds has no level that small (give it a mip chain)"); return 2; }
            levels.RemoveRange(0, first);
            Console.WriteLine($"  --max-size {maxSize}: using the stored {levels[0].W}x{levels[0].H} level");
            width = levels[0].W; height = levels[0].H;
            img = img with { Width = width, Height = height };
        }
        if (noMips && levels.Count > 1) levels.RemoveRange(1, levels.Count - 1);   // --no-mips: the top level only
        Console.WriteLine($"  .dds: {width}x{height} {img.FourCC}, {levels.Count} mip(s) ({levels[^1].W}x{levels[^1].H} smallest), {levels.Sum(l => l.Pixels.Length):N0} bytes");

        // Names the new export needs.
        var addNames = new List<string>();
        AddMissingNames(pkg, addNames, (replace ? [] : new[] { newName }).Concat(NamesFor(img)));
        var tw = new TagWriter(pkg, addNames);
        var build = Builder(pkg, template, img, tw, replace, out string? buildError);
        if (build == null) { Console.WriteLine("  " + buildError); return 1; }

        var add = new List<NewExport>();
        var replaced = new Dictionary<int, Func<long, byte[]>>();
        if (replace) replaced[template] = build;
        else
        {
            byte[] entry = pkg.Body.AsSpan(pkg.ExportEntryStart[template], pkg.ExportEntryEnd[template] - pkg.ExportEntryStart[template]).ToArray();
            int nameIndex = Array.FindIndex(pkg.Names, x => x.Equals(newName, StringComparison.OrdinalIgnoreCase));
            if (nameIndex < 0) nameIndex = pkg.Names.Length + addNames.FindIndex(x => x.Equals(newName, StringComparison.OrdinalIgnoreCase));
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), nameIndex);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
            add.Add(new(template, 0, build) { Entry = entry });
        }

        byte[] output = PackageRebuilder.Rebuild(pkg, replaced, add, out var written, addNames);
        int newIndex = replace ? template : pkg.Exports.Length;
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [.. replaced.Keys], add, written, addNames);
            problems.AddRange(CheckTexture(Package.FromBytes(bytes), newIndex, outerPath + newName, img));
            return problems;
        }
        var problems = Check(output);
        Console.WriteLine($"  {(replace ? "replaced" : "new")} export #{newIndex + 1} {outerPath}{newName}; names added: {(addNames.Count == 0 ? "none" : string.Join(", ", addNames))}");
        Console.WriteLine($"  package: {pkg.RawFile.Length:N0} -> {output.Length:N0} bytes");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (tables = original + additions, {(replace ? "every other export" : "existing exports")} byte-identical; texture reads back with {levels.Count} inline mip(s), pixels identical to the .dds, offsets pointing at them, no cache)");
        if (replace) Console.WriteLine("  note: other packages may hold their own copy of this texture (same path); the game uses the one loaded first. \"Find name in folder\" lists them.");

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

    /// <summary>One texture to replace: the Texture2D's object name (or full path) and the .dds bytes.</summary>
    public sealed record Replacement(string Texture, byte[] Dds, string Source);

    /// <summary>
    /// --replace-texture for many textures of one package in a single rebuild (each exactly as the single command would
    /// write it). Returns the new package bytes and a verifier for them (tables = original + additions, every other
    /// export byte-identical, each texture read back), or null with the problems. Writes nothing.
    /// </summary>
    public static byte[]? ReplaceMany(Package pkg, IReadOnlyList<Replacement> items, out List<string> problems, out Func<byte[], List<string>> verify)
    {
        problems = [];
        verify = _ => ["nothing built"];
        var targets = new List<(int Index, string Path, DdsImage Img, string Source)>();
        foreach (var r in items)
        {
            int index = Array.FindIndex(pkg.Exports, e => (e.ObjectName.Equals(r.Texture, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(e).Equals(r.Texture, StringComparison.OrdinalIgnoreCase))
                && pkg.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase));
            if (index < 0) { problems.Add($"{r.Source}: no texture '{r.Texture}' in the package"); continue; }
            if (targets.Any(x => x.Index == index)) { problems.Add($"{r.Source}: '{r.Texture}' is replaced twice"); continue; }
            var img = ParseDds(r.Dds, out string? err);
            if (img == null) { problems.Add($"{r.Source}: {err}"); continue; }
            targets.Add((index, pkg.PathOf(pkg.Exports[index]), img, r.Source));
        }
        if (problems.Count > 0) return null;

        var addNames = new List<string>();
        foreach (var x in targets) AddMissingNames(pkg, addNames, NamesFor(x.Img));
        var tw = new TagWriter(pkg, addNames);
        var replaced = new Dictionary<int, Func<long, byte[]>>();
        foreach (var x in targets)
        {
            var build = Builder(pkg, x.Index, x.Img, tw, replace: true, out string? err);
            if (build == null) problems.Add($"{x.Source}: {err}");
            else replaced[x.Index] = build;
        }
        if (problems.Count > 0) return null;

        byte[] output = PackageRebuilder.Rebuild(pkg, replaced, [], out var written, addNames);
        verify = bytes =>
        {
            var found = PackageRebuilder.Verify(pkg, bytes, [.. replaced.Keys], [], written, addNames);
            var w = Package.FromBytes(bytes);
            foreach (var x in targets) found.AddRange(CheckTexture(w, x.Index, x.Path, x.Img).Select(p => $"{x.Path}: {p}"));
            return found;
        };
        problems = verify(output);
        return problems.Count == 0 ? output : null;
    }
}
