using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// --copy-export: copies an export and everything it references (its closure) from one package into another,
/// e.g. a material and its parent, expressions and textures, so the target no longer depends on a package that
/// may not be loaded. Every name and object reference in the copied data is found by an exact parser (property
/// tags, struct arrays, known object arrays, and the native Material / MaterialInstanceConstant resource layout:
/// quality mask, then per quality level CompileErrors, TextureDependencyLengthMap, MaxTextureDependencyLength, Id,
/// NumUserTexCoords, UniformExpressionTextures, 6 ints, TextureLookups, 4 ints, and for an instance its
/// StaticParameters: BaseMaterialId, StaticSwitchParameters, 3 more empty arrays — decoded from the water
/// material in SCS__CH0201ShippingYardRegion_SF, confirmed by each layout ending exactly at the data end).
/// Anything the parser doesn't know stops the copy. Objects already in the target by path and class are reused;
/// missing names and imports are appended; references through a --cut property become null. Texture native data
/// (inline mip bulk data) is copied as is, with its stale "offset in file" fields, as other tools' working packages do.
/// Same .bak / verified temp / swap as the other writers; verified by re-parsing every copied export.
/// </summary>
static class ExportCopy
{
    enum Kind { Name, Object, SelfOffset }
    sealed record Patch(int Offset, Kind Kind, string Where);

    /// <summary>Structs stored without tags that hold no names or object references.</summary>
    static readonly HashSet<string> PlainStructs = new(StringComparer.OrdinalIgnoreCase)
        { "vector", "vector2d", "vector4", "guid", "color", "linearcolor", "rotator", "box", "matrix", "plane", "quat", "intpoint", "sphere", "twovectors" };
    /// <summary>Array properties known to hold object references (4 bytes each).</summary>
    static readonly HashSet<string> ObjectArrays = new(StringComparer.OrdinalIgnoreCase) { "expressions", "functionexpressions", "staticmeshcomponents", "materials" };

    public static int Run(string srcPath, string exportName, string dstPath, IReadOnlyCollection<string> cut, bool dryRun,
        string? rename = null, IReadOnlyDictionary<string, string>? replaceRefs = null)
    {
        dstPath = Path.GetFullPath(dstPath);
        if (Program.IsBackupName(dstPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var src = Package.Open(srcPath);
        var dst = Package.Open(dstPath);
        Console.WriteLine($"Copy: {exportName} from {Path.GetFileName(srcPath)} -> {Path.GetFileName(dstPath)}{(cut.Count > 0 ? $" (cut: {string.Join(", ", cut)})" : "")}{(dryRun ? "  [dry run]" : "")}");
        int root = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(exportName, StringComparison.OrdinalIgnoreCase));
        if (root < 0) { Console.WriteLine($"  no export '{exportName}' (give the full path, e.g. package.group.name)"); return 2; }

        // --rename: the copied root gets a new object name (so a copy that will differ from its source doesn't take over
        // the source's path in memory); everything inside it follows. --replace-ref: references to these source objects
        // point at existing target objects instead, and the source objects aren't copied (e.g. swap one texture).
        string rootPath = src.PathOf(src.Exports[root]);
        string newRootPath = rename == null ? rootPath : (rootPath.Contains('.') ? rootPath[..(rootPath.LastIndexOf('.') + 1)] : "") + rename;
        var replaced = new Dictionary<int, int>();             // source reference -> target reference
        foreach (var (from, to) in replaceRefs ?? new Dictionary<string, string>())
        {
            int f = Array.FindIndex(src.Exports, e => src.PathOf(e).Equals(from, StringComparison.OrdinalIgnoreCase));
            int t = Array.FindIndex(dst.Exports, e => dst.PathOf(e).Equals(to, StringComparison.OrdinalIgnoreCase));
            if (f < 0 || t < 0) { Console.WriteLine($"  --replace-ref {from}={to}: {(f < 0 ? "no such export in the source" : "no such export in the target")}"); return 2; }
            replaced[f + 1] = t + 1;
            Console.WriteLine($"  replacing references to {from} with the target's {to} ({dst.ClassOf(dst.Exports[t])})");
        }
        if (rename != null) Console.WriteLine($"  copied root renamed: {rootPath} -> {newRootPath}");
        // What a source reference must resolve to in the target (path|class), after renaming and replacing.
        string Expect(Package w, int r)
        {
            if (replaced.TryGetValue(r, out int t)) return Key(dst, t);
            string k = Key(src, r);
            if (rename != null && (k.StartsWith(rootPath + "|", StringComparison.OrdinalIgnoreCase) || k.StartsWith(rootPath + ".", StringComparison.OrdinalIgnoreCase)))
                k = newRootPath + k[rootPath.Length..];
            return k;
        }

        // Objects the target already has (same path and class) are reused, not parsed or followed — e.g. the
        // level and world a copied actor sits in.
        var dstByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < dst.Exports.Length; k++) dstByPath.TryAdd(Key(dst, k + 1), k + 1);
        var reusedEarly = new HashSet<int>();

        // 1. Closure, from the exact parser.
        var patches = new Dictionary<int, List<Patch>>();
        var order = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(root);
        int cutRefs = 0;
        void EnqueueImportOuters(int r)
        {
            for (int guard = 0; r < 0 && guard < 32; guard++)
            {
                r = src.Imports[-r - 1].OuterIndex;
                if (r > 0) queue.Enqueue(r - 1);
            }
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (patches.ContainsKey(i) || reusedEarly.Contains(i)) continue;
            var e = src.Exports[i];
            if (i != root && dstByPath.ContainsKey(Expect(dst, i + 1))) { reusedEarly.Add(i); order.Add(i); continue; }
            List<Patch> list;
            try { list = Parse(src, src.ReadExportBytes(e), src.ClassOf(e)); }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or ArgumentOutOfRangeException)
            { Console.WriteLine($"  can't copy {src.PathOf(e)} ({src.ClassOf(e)}): {ex.Message}"); return 1; }
            patches[i] = list;
            order.Add(i);
            byte[] d = src.ReadExportBytes(e);
            foreach (var pt in list.Where(x => x.Kind == Kind.Object))
            {
                int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pt.Offset));
                if (IsCut(pt.Where, cut)) { if (r != 0) cutRefs++; continue; }
                if (replaced.ContainsKey(r)) continue;
                if (r > 0) queue.Enqueue(r - 1);
                else EnqueueImportOuters(r);
            }
            var (cls, super, outer, arch) = EntryRefs(src, i);
            if (cls > 0) { Console.WriteLine($"  {src.PathOf(e)}: its class is defined in the package; not supported"); return 1; }
            if (super != 0) { Console.WriteLine($"  {src.PathOf(e)}: has a SuperIndex (a class/struct); not supported"); return 1; }
            if (outer > 0) queue.Enqueue(outer - 1);
            if (arch > 0) queue.Enqueue(arch - 1);
            EnqueueImportOuters(cls);
        }

        // 2. Target indices: reuse what's already there (same path and class), otherwise a new export.
        var map = new Dictionary<int, int>();                  // source reference -> target reference
        var copies = new List<int>();
        foreach (int i in order)
        {
            if (dstByPath.TryGetValue(Expect(dst, i + 1), out int existing))
            {
                if (i == root && rename != null) { Console.WriteLine($"  '{newRootPath}' already exists in the target"); return 1; }
                map[i + 1] = existing;
            }
            else { copies.Add(i); map[i + 1] = dst.Exports.Length + copies.Count; }
        }

        // 3. Names and imports the copies need.
        var addNames = new List<string>();
        var addImports = new List<NewImport>();
        int NameIdx(int srcIdx) => EnsureName(src.Names[srcIdx]);
        int EnsureName(string n)
        {
            int k = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return k;
            k = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (k < 0) { addNames.Add(n); k = addNames.Count - 1; }
            return dst.Names.Length + k;
        }
        var importMap = new Dictionary<int, int>();
        int MapImport(int r)
        {
            if (importMap.TryGetValue(r, out int m)) return m;
            var im = src.Imports[-r - 1];
            int outer = Map(im.OuterIndex);                     // an import's outer can be an export (e.g. a forced-export package)
            // Existing target import with the same name, class and (mapped) outer?
            int k = Array.FindIndex(dst.Imports, x => x.OuterIndex == outer && x.ClassName.Equals(im.ClassName, StringComparison.OrdinalIgnoreCase)
                && x.ObjectName.Equals(im.ObjectName, StringComparison.OrdinalIgnoreCase));
            if (k < 0)
            {
                int added = addImports.FindIndex(x => x.OuterIndex == outer && x.ClassName.Equals(im.ClassName, StringComparison.OrdinalIgnoreCase)
                    && x.ObjectName.Equals(im.ObjectName, StringComparison.OrdinalIgnoreCase));
                if (added < 0)
                {
                    var raw = src.Body.AsSpan(src.ImportOffset + 28 * (-r - 1));
                    string classPackage = src.Names[BinaryPrimitives.ReadInt32LittleEndian(raw)];
                    if (BinaryPrimitives.ReadInt32LittleEndian(raw[4..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(raw[12..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(raw[24..]) != 0)
                        throw new InvalidDataException($"import {im.ObjectName}: numbered names not supported");
                    foreach (string n in new[] { classPackage, im.ClassName, im.ObjectName }) EnsureName(n);
                    addImports.Add(new NewImport(classPackage, im.ClassName, outer, im.ObjectName));
                    added = addImports.Count - 1;
                }
                k = dst.Imports.Length + added;
            }
            return importMap[r] = -(k + 1);
        }
        int Map(int r) => r == 0 ? 0 : replaced.TryGetValue(r, out int rt) ? rt : r > 0 ? map.TryGetValue(r, out int m) ? m : throw new InvalidDataException($"reference {r} outside the closure") : MapImport(r);

        // 4. Data and table entries for the copies.
        var add = new List<NewExport>();
        foreach (int i in copies)
        {
            byte[] d = src.ReadExportBytes(src.Exports[i]).ToArray();
            foreach (var pt in patches[i].Where(x => x.Kind != Kind.SelfOffset))
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pt.Offset));
                int nv = pt.Kind == Kind.Name ? NameIdx(v) : IsCut(pt.Where, cut) ? 0 : Map(v);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(pt.Offset), nv);
            }
            byte[] entry = src.Body.AsSpan(src.ExportEntryStart[i], src.ExportEntryEnd[i] - src.ExportEntryStart[i]).ToArray();
            var (cls, _, outer, arch) = EntryRefs(src, i);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(0), Map(cls));
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), Map(outer));
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), i == root && rename != null ? EnsureName(rename) : NameIdx(BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(12))));
            if (i == root && rename != null) BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
            BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(20), Map(arch));
            byte[] data = d;
            var self = patches[i].Where(x => x.Kind == Kind.SelfOffset).Select(x => x.Offset).ToList();
            add.Add(new NewExport(0, 0, off =>
            {
                if (self.Count == 0) return data;
                byte[] b = data.ToArray();
                foreach (int at in self) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), checked((int)(off + at + 4)));   // absolute offset of the inline data
                return b;
            }) { Entry = entry });
        }

        int reused = order.Count - copies.Count;
        Console.WriteLine($"  closure: {order.Count} export(s); copying {copies.Count} ({copies.Sum(i => src.Exports[i].SerialSize):N0} bytes), reusing {reused} already in the target; {cutRefs} reference(s) cut to null");
        foreach (var g in copies.GroupBy(i => src.ClassOf(src.Exports[i])).OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {g.Count(),4} x {g.Key}");
        Console.WriteLine($"  names added: {addNames.Count}; imports added: {addImports.Count}{(addImports.Count > 0 ? " (" + string.Join(", ", addImports.Select(x => $"{x.ClassName} {x.ObjectName}")) + ")" : "")}");

        byte[] output = PackageRebuilder.Rebuild(dst, new Dictionary<int, Func<long, byte[]>>(), add, out var written, addNames, addImports);
        List<string> Check(byte[] bytes)
        {
            var p = PackageRebuilder.Verify(dst, bytes, [], add, written, addNames, addImports);
            p.AddRange(Semantic(src, bytes, copies, map, patches, cut, Expect));
            return p;
        }
        var problems = Check(output);
        Console.WriteLine($"  package: {dst.RawFile.Length:N0} -> {output.Length:N0} bytes, {dst.Exports.Length} -> {dst.Exports.Length + add.Count} exports");
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.Take(20).ToList().ForEach(p => Console.WriteLine($"    - {p}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (tables = original + additions, existing exports byte-identical; every copied export re-parsed: same layout, every name and reference resolves to the same path as in the source, all other bytes identical)");
        Console.WriteLine($"  copied root: {src.PathOf(src.Exports[root])} is export #{map[root + 1]} in the target");

        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(dstPath));
            File.WriteAllBytes(target, output);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(dstPath, output, Check) ? 0 : 1;
    }

    static bool IsCut(string where, IReadOnlyCollection<string> cut) => cut.Any(c => where.Split('.', '[')[0].Equals(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>Path + class of an object reference, for matching and verification.</summary>
    static string Key(Package pkg, int r)
    {
        if (r == 0) return "null";
        if (r > 0) { var e = pkg.Exports[r - 1]; return $"{pkg.PathOf(e)}|{pkg.ClassOf(e)}"; }
        var parts = new List<string>();
        string cls = pkg.Imports[-r - 1].ClassName;
        for (int guard = 0; r != 0 && guard < 32; guard++)
        {
            if (r > 0) { parts.Add(pkg.PathOf(pkg.Exports[r - 1])); break; }
            var im = pkg.Imports[-r - 1]; parts.Add(im.ObjectName); r = im.OuterIndex;
        }
        parts.Reverse();
        return $"{string.Join('.', parts)}|{cls}";
    }

    static (int Class, int Super, int Outer, int Archetype) EntryRefs(Package pkg, int i)
    {
        var s = pkg.Body.AsSpan(pkg.ExportEntryStart[i]);
        return (BinaryPrimitives.ReadInt32LittleEndian(s), BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[8..]), BinaryPrimitives.ReadInt32LittleEndian(s[20..]));
    }

    /// <summary>Every name and object reference in an export's data (offsets into it). Throws on anything unknown.</summary>
    static List<Patch> Parse(Package pkg, byte[] d, string cls)
    {
        string c = cls.ToLowerInvariant();
        bool component = c.EndsWith("component");
        if (component && c != "staticmeshcomponent") throw new InvalidDataException($"component class '{cls}' not supported");
        var list = new List<Patch>();
        // MHO components: an extra int32, then NetIndex, then properties (byte 8); everything else from byte 4.
        int p = Tags(pkg, d, component ? 8 : 4, d.Length, "", list);
        if (c is "package" or "materialfunction" || c.StartsWith("materialexpression"))
        {
            if (p != d.Length) throw new InvalidDataException($"{d.Length - p} bytes of native data (none expected)");
        }
        else if (c == "material") MaterialNative(pkg, d, p, false, list);
        else if (c == "materialinstanceconstant") { if (p != d.Length) MaterialNative(pkg, d, p, true, list); }
        else if (c == "texture2d") TextureMips(d, p, list);
        else if (c == "staticmeshcollectionactor")
        {
            if (p != d.Length) throw new InvalidDataException($"{d.Length - p} bytes of native data (none expected)");
        }
        else if (c == "staticmeshcomponent")
        {
            // Only the empty lighting record (LOD count, then zeros: no shadow maps, no light map) — no references.
            if (d.Length - p < 4 || I32(d, p) < 1 || d.AsSpan(p + 4).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("component has baked lighting data (only the empty lighting record is supported)");
        }
        else if (c == "staticmesh") MeshNative(pkg, d, p, list);
        else if (c == "texturecube") { if (d.Length - p != 16) throw new InvalidDataException("texture cube native data isn't the empty 16-byte source-art header"); }
        else throw new InvalidDataException($"native layout of class '{cls}' unknown");
        return list;
    }

    /// <summary>
    /// Texture2D mips (layout as in TextureInfo): empty source-art bulk header, count, then per mip bulk flags, element
    /// count, size, offset in file, [inline data], width, height. An inline mip's offset field must point at its data.
    /// </summary>
    static void TextureMips(byte[] d, int p, List<Patch> list)
    {
        p += 16;
        int count = I32(d, p); p += 4;
        if (count < 0 || count > 20) throw new InvalidDataException($"mip count {count}");
        for (int m = 0; m < count; m++)
        {
            uint flags = (uint)I32(d, p); int size = I32(d, p + 8);
            p += 16;
            if ((flags & 0x21) == 0 && size > 0) { list.Add(new Patch(p - 4, Kind.SelfOffset, $"native.mip{m}.offset")); p += size; }
            p += 8;
            if (p > d.Length) throw new InvalidDataException("mip table past the end");
        }
    }

    /// <summary>
    /// StaticMesh native references (layout as in StaticMesh.cs): bounds (28 bytes), BodySetup reference, kDOP tree,
    /// InternalVersion + 4 ints, LOD count, then LOD 0: raw-triangle bulk data (its offset-in-file points at itself),
    /// then the sections (material reference first in each). The vertex buffers and the tail after LOD 0 hold no
    /// references (the mesh import rewrites them the same way).
    /// </summary>
    static void MeshNative(Package pkg, byte[] d, int p, List<Patch> list)
    {
        p += 28;
        list.Add(new Patch(p, Kind.Object, "bodysetup")); p += 4;
        p += 24;                                                // kDOP root bound
        for (int k = 0; k < 2; k++)                             // kDOP nodes, triangles: element size, count
        {
            int size = I32(d, p), count = I32(d, p + 4); p += 8;
            if (size < 0 || count < 0 || (long)size * count > d.Length) throw new InvalidDataException("kDOP arrays implausible");
            p += size * count;
        }
        p += 4 + 16;                                            // InternalVersion, 4 unknown ints
        int lods = I32(d, p); p += 4;
        if (lods != 1) throw new InvalidDataException($"{lods} LODs (only single-LOD meshes are supported)");
        int rawSize = I32(d, p + 8);
        list.Add(new Patch(p + 12, Kind.SelfOffset, "lod0.rawtriangles.offset"));
        p += 16 + Math.Max(0, rawSize);
        int sections = I32(d, p); p += 4;
        if (sections < 0 || sections > 4096) throw new InvalidDataException($"{sections} sections");
        for (int s = 0; s < sections; s++)
        {
            list.Add(new Patch(p, Kind.Object, $"section{s}.material"));
            int fragments = I32(d, p + 36);
            if (fragments < 0 || fragments > 65536) throw new InvalidDataException($"section {s}: {fragments} fragments");
            p += 40 + fragments * 8 + 1;
        }
        if (p > d.Length) throw new InvalidDataException("sections run past the end");
    }

    static int I32(byte[] d, int p) => p + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) : throw new InvalidDataException("read past the end");

    static string NameAt(Package pkg, byte[] d, int p)
    {
        int idx = I32(d, p), num = I32(d, p + 4);
        if ((uint)idx >= (uint)pkg.Names.Length) throw new InvalidDataException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }

    static int Tags(Package pkg, byte[] d, int p, int end, string prefix, List<Patch> list)
    {
        for (int guard = 0; guard < 8192; guard++)
        {
            string name = NameAt(pkg, d, p);
            list.Add(new Patch(p, Kind.Name, prefix + name));
            p += 8;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return p;
            string type = NameAt(pkg, d, p).ToLowerInvariant();
            list.Add(new Patch(p, Kind.Name, prefix + name));
            p += 8;
            int size = I32(d, p);
            p += 8;                                                 // size, array index
            string sub = "";
            if (type is "structproperty" or "byteproperty") { sub = NameAt(pkg, d, p); list.Add(new Patch(p, Kind.Name, prefix + name)); p += 8; }
            else if (type == "boolproperty") p += 1;
            if (size < 0 || p + size > end) throw new InvalidDataException($"{prefix}{name}: size {size} past the end");
            string where = prefix + name;
            switch (type)
            {
                case "objectproperty" or "classproperty" or "componentproperty" or "interfaceproperty":
                    if (size != 4) throw new InvalidDataException($"{where}: {type} of size {size}");
                    list.Add(new Patch(p, Kind.Object, where));
                    break;
                case "nameproperty":
                    if (size != 8) throw new InvalidDataException($"{where}: name of size {size}");
                    list.Add(new Patch(p, Kind.Name, where));
                    break;
                case "byteproperty":
                    if (size == 8) list.Add(new Patch(p, Kind.Name, where));
                    else if (size != 1) throw new InvalidDataException($"{where}: byte of size {size}");
                    break;
                case "structproperty":
                    if (!PlainStructs.Contains(sub))
                    {
                        var inner = new List<Patch>();
                        if (TryTags(pkg, d, p, p + size, where + ".", inner) != p + size) throw new InvalidDataException($"{where}: struct '{sub}' isn't tagged and isn't a known plain struct");
                        list.AddRange(inner);
                    }
                    break;
                case "arrayproperty":
                    ArrayRefs(pkg, d, p, size, where, list);
                    break;
                case "intproperty" or "floatproperty" or "boolproperty" or "strproperty" or "qwordproperty":
                    break;
                default:
                    throw new InvalidDataException($"{where}: property type '{type}' not handled");
            }
            p += size;
        }
        throw new InvalidDataException("too many tags");
    }

    static int TryTags(Package pkg, byte[] d, int p, int end, string prefix, List<Patch> list)
    {
        try { return Tags(pkg, d, p, end, prefix, list); }
        catch (InvalidDataException) { return -1; }
    }

    static void ArrayRefs(Package pkg, byte[] d, int p, int size, string where, List<Patch> list)
    {
        int count = I32(d, p), end = p + size;
        if (count == 0) { if (size != 4) throw new InvalidDataException($"{where}: empty array of size {size}"); return; }
        // Array of tagged structs?
        var inner = new List<Patch>();
        int q = p + 4;
        for (int i = 0; i < count && q > 0; i++) q = TryTags(pkg, d, q, end, $"{where}[{i}].", inner);
        if (q == end) { list.AddRange(inner); return; }
        // Array of strings (no references): each element an FString, together filling the array exactly.
        {
            int r = p + 4, i = 0;
            for (; i < count && r + 4 <= end; i++)
            {
                int len = I32(d, r);
                r += 4 + (len >= 0 ? len : -2 * len);
                if (len == 0 || r > end) break;
            }
            if (i == count && r == end) return;
        }
        if (ObjectArrays.Contains(where.Split('.', '[').Last()) && size - 4 == count * 4)
        {
            for (int i = 0; i < count; i++) list.Add(new Patch(p + 4 + 4 * i, Kind.Object, $"{where}[{i}]"));
            return;
        }
        throw new InvalidDataException($"{where}: array of {count} with unknown element type ({size - 4} bytes)");
    }

    /// <summary>Material / MaterialInstanceConstant native data (layout in the class summary).</summary>
    static void MaterialNative(Package pkg, byte[] d, int p, bool instance, List<Patch> list)
    {
        int mask = I32(d, p); p += 4;
        int levels = mask switch { 1 or 2 => 1, 3 => 2, _ => throw new InvalidDataException($"material quality mask {mask}") };
        for (int level = 0; level < levels; level++)
        {
            if (I32(d, p) != 0) throw new InvalidDataException("material has compile errors stored"); p += 4;
            if (I32(d, p) != 0) throw new InvalidDataException("material has a texture dependency map"); p += 4;
            p += 4 + 16 + 4;                                    // MaxTextureDependencyLength, Id, NumUserTexCoords
            int n = I32(d, p); p += 4;
            if (n < 0 || n > 256) throw new InvalidDataException($"texture list of {n}");
            for (int i = 0; i < n; i++) { list.Add(new Patch(p, Kind.Object, $"native.resource{level}.textures[{i}]")); p += 4; }
            p += 24;                                            // scene colour/depth, dynamic parameter, lightmap UVs, vertex offset, transforms
            int lookups = I32(d, p); p += 4;
            if (lookups < 0 || lookups > 256) throw new InvalidDataException($"texture lookups {lookups}");
            p += 16 * lookups + 16;
            if (instance)
            {
                p += 16;                                        // BaseMaterialId
                int sw = I32(d, p); p += 4;
                if (sw < 0 || sw > 256) throw new InvalidDataException($"static switches {sw}");
                for (int i = 0; i < sw; i++) { list.Add(new Patch(p, Kind.Name, $"native.static{level}.switch[{i}]")); p += 8 + 4 + 4 + 16; }
                for (int i = 0; i < 3; i++) { if (I32(d, p) != 0) throw new InvalidDataException("static parameter array other than switches not empty"); p += 4; }
            }
        }
        if (p != d.Length) throw new InvalidDataException($"material native data: layout ends at {p}, data at {d.Length}");
    }

    /// <summary>Re-reads the copies from the rebuilt package and compares them with the source.</summary>
    static List<string> Semantic(Package src, byte[] bytes, List<int> copies, Dictionary<int, int> map, Dictionary<int, List<Patch>> patches, IReadOnlyCollection<string> cut,
        Func<Package, int, string> expect)
    {
        var problems = new List<string>();
        var w = Package.FromBytes(bytes);
        foreach (int i in copies)
        {
            int t = map[i + 1];
            var se = src.Exports[i]; var we = w.Exports[t - 1];
            string label = src.PathOf(se);
            if (!expect(w, i + 1).Equals(Key(w, t), StringComparison.OrdinalIgnoreCase)) { problems.Add($"{label}: path/class in the target is {Key(w, t)}, expected {expect(w, i + 1)}"); continue; }
            var (sc, _, so, sa) = EntryRefs(src, i);
            var (wc, _, wo, wa) = EntryRefs(w, t - 1);
            bool Same(int a, int b) => (a == 0 ? "null" : expect(w, a)).Equals(Key(w, b), StringComparison.OrdinalIgnoreCase);
            if (!Same(sc, wc) || !Same(so, wo) || !Same(sa, wa)) problems.Add($"{label}: class/outer/archetype don't match");
            byte[] sd = src.ReadExportBytes(se), wd = w.ReadExportBytes(we);
            if (sd.Length != wd.Length) { problems.Add($"{label}: {wd.Length} bytes, source {sd.Length}"); continue; }
            List<Patch> wp;
            try { wp = Parse(w, wd, w.ClassOf(we)); }
            catch (Exception ex) when (ex is InvalidDataException or PackageFormatException) { problems.Add($"{label}: doesn't parse in the target: {ex.Message}"); continue; }
            var sp = patches[i];
            if (!sp.Select(x => (x.Offset, x.Kind)).SequenceEqual(wp.Select(x => (x.Offset, x.Kind)))) { problems.Add($"{label}: reference layout differs"); continue; }
            var covered = new bool[sd.Length];
            foreach (var pt in sp)
            {
                int n = pt.Kind == Kind.Name ? 8 : 4;
                for (int k = 0; k < n; k++) covered[pt.Offset + k] = true;
                if (pt.Kind == Kind.SelfOffset)
                {
                    if (I32(wd, pt.Offset) != we.SerialOffset + pt.Offset + 4) problems.Add($"{label}: {pt.Where} doesn't point at its data");
                }
                else if (pt.Kind == Kind.Name)
                {
                    if (!NameAt(src, sd, pt.Offset).Equals(NameAt(w, wd, pt.Offset), StringComparison.OrdinalIgnoreCase)) problems.Add($"{label}: name at {pt.Offset} ({pt.Where}) differs");
                }
                else
                {
                    int sv = I32(sd, pt.Offset), wv = I32(wd, pt.Offset);
                    string want = IsCut(pt.Where, cut) || sv == 0 ? "null" : expect(w, sv);
                    if (!want.Equals(Key(w, wv), StringComparison.OrdinalIgnoreCase)) problems.Add($"{label}: {pt.Where} -> {Key(w, wv)}, expected {want}");
                }
            }
            for (int k = 0; k < sd.Length; k++)
                if (!covered[k] && sd[k] != wd[k]) { problems.Add($"{label}: byte {k} differs"); break; }
        }
        return problems;
    }
}
