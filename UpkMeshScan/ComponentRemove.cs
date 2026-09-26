using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// --remove-components: takes StaticMeshComponents out of their StaticMeshCollectionActor's list, so the game doesn't
/// create them — e.g. a tile's stock water planes, when the main level's own water layers replace them (Hightown). A
/// component matches if its material (the component's Materials entry, else the mesh section's own) contains
/// --material, and/or its mesh name contains --mesh. The components' data stays in the package untouched (only the
/// actors' lists change), so it's easy to verify and undo. Same .bak / verify / swap; --dry-run lists the matches.
/// </summary>
static class ComponentRemove
{
    public static int Run(string upkPath, string? materialContains, string? meshContains, string? libraryPath, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Package? library = libraryPath != null ? Package.Open(libraryPath) : null;
        var libByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (library != null)
            for (int i = 0; i < library.Exports.Length; i++)
                if (library.ClassOf(library.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) libByName.TryAdd(library.Exports[i].ObjectName, i);

        string RefPath(Package p, int r)
        {
            if (r > 0) return p.PathOf(p.Exports[r - 1]);
            var parts = new List<string>();
            for (int guard = 0; r < 0 && guard < 16; guard++) { var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            return string.Join('.', parts);
        }

        // Matching components, grouped by their collection actor.
        var remove = new Dictionary<int, HashSet<int>>();                 // actor index -> component refs to drop
        int found = 0;
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!pkg.ClassOf(e).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = pkg.ReadExportBytes(e);
            var c = ComponentTransform.Read(pkg, d);
            if (c == null || c.MeshRef == 0) continue;
            string meshName = pkg.RefName(c.MeshRef);
            var mats = new List<string>();
            var mt = TagWalker.Walk(pkg, d, 8)?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            if (mt != null)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mt.ValueAt));
                for (int k = 0; k < n; k++) { int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mt.ValueAt + 4 + 4 * k)); if (r != 0) mats.Add(RefPath(pkg, r)); }
            }
            if (mats.Count == 0)
            {
                // No override: the mesh sections' own materials.
                (Package P, int I)? owner = c.MeshRef > 0 ? (pkg, c.MeshRef - 1) : library != null && libByName.TryGetValue(meshName, out int li) ? (library, li) : null;
                if (owner is { } o)
                    try { foreach (var s in StaticMesh.Read(o.P, o.P.Exports[o.I]).Sections) if (s.MaterialRef != 0) mats.Add(RefPath(o.P, s.MaterialRef)); }
                    catch (PackageFormatException) { }
            }
            bool matOk = materialContains == null || mats.Any(m => m.Contains(materialContains, StringComparison.OrdinalIgnoreCase));
            bool meshOk = meshContains == null || meshName.Contains(meshContains, StringComparison.OrdinalIgnoreCase);
            if (!matOk || !meshOk) continue;
            int actor = e.OuterIndex - 1;
            if (actor < 0 || !pkg.ClassOf(pkg.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine($"  {pkg.PathOf(e)}: matches but isn't in a collection actor; left alone"); continue; }
            if (!remove.TryGetValue(actor, out var set)) remove[actor] = set = [];
            set.Add(i + 1);
            found++;
            Console.WriteLine($"  {e.ObjectName}: mesh {meshName}, at ({c.Translation.X:0}, {c.Translation.Y:0}, {c.Translation.Z:0}), material {string.Join(", ", mats)}");
        }
        Console.WriteLine($"{Path.GetFileName(upkPath)}: {found} component(s) to remove{(dryRun ? "  [dry run]" : "")}");
        if (found == 0) return 0;

        var replace = new Dictionary<int, Func<long, byte[]>>();
        var expectLists = new Dictionary<int, List<int>>();
        foreach (var (actor, drop) in remove)
        {
            byte[] src = pkg.ReadExportBytes(pkg.Exports[actor]);
            var tags = TagWalker.Walk(pkg, src, 4) ?? throw new InvalidDataException("collection actor properties don't parse");
            var list = tags.First(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase));
            int count = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(list.ValueAt));
            var refs = Enumerable.Range(0, count).Select(k => BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(list.ValueAt + 4 + 4 * k))).ToList();
            var kept = refs.Where(r => !drop.Contains(r)).ToList();
            var tw = new TagWriter(pkg);
            using var ms = new MemoryStream();
            ms.Write(src, 0, 4);
            foreach (var t in tags)
                if (t == list) ms.Write(tw.Tag("StaticMeshComponents", "ArrayProperty", null, [.. BitConverter.GetBytes(kept.Count), .. kept.SelectMany(BitConverter.GetBytes)]));
                else ms.Write(src, t.Start, t.End - t.Start);
            ms.Write(src, tags.NoneAt, src.Length - tags.NoneAt);
            byte[] bytes = ms.ToArray();
            replace[actor] = _ => bytes;
            expectLists[actor] = kept;
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, replace.Keys.ToList(), [], written);
            var w = Package.FromBytes(bytes);
            foreach (var (actor, kept) in expectLists)
            {
                byte[] a = w.ReadExportBytes(w.Exports[actor]);
                var l = TagWalker.Walk(w, a, 4)?.FirstOrDefault(t => t.Name.Equals("StaticMeshComponents", StringComparison.OrdinalIgnoreCase));
                var got = l == null ? [] : Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(l.ValueAt))).Select(k => BinaryPrimitives.ReadInt32LittleEndian(a.AsSpan(l.ValueAt + 4 + 4 * k))).ToList();
                if (!got.SequenceEqual(kept)) problems.Add($"{w.Exports[actor].ObjectName}: component list doesn't read back as the old list minus the removed ones");
            }
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (actor lists = old minus {found}; every other export byte-identical, the removed components' data kept)");
        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, Path.GetFileName(upkPath)), output);
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }
}
