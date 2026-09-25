using System.Buffers.Binary;
using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --add-mesh-instances: recreates a tile's placements of some meshes in a main level, so real geometry (with its own
/// materials) stands in for the tile at a distance — e.g. Odin's Palace's Bifrost deck instead of grey slabs. For every
/// StaticMeshComponent in the source package whose mesh is one of the named ones, a new component is added to the
/// template's StaticMeshCollectionActor with the source's placement (its own translation / rotation / scale, composed
/// with a standalone owning actor's), the source's materials, an empty lighting record and MinDrawDistance. The mesh
/// and materials must already be in the target under the same paths (copy them first with --copy-export). Same
/// .bak / verify / swap as the other writers.
/// </summary>
static class MeshInstances
{
    static readonly byte[] EmptyLighting = [1, 0, 0, 0, .. new byte[17]];

    public static int Run(string upkPath, string srcPath, IReadOnlyList<string> meshNames, string templatePath, float minDraw, float zOffset, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        var src = Package.Open(srcPath);
        Console.WriteLine($"Mesh instances: {string.Join(", ", meshNames)} from {Path.GetFileName(srcPath)} -> {Path.GetFileName(upkPath)}, MinDrawDistance {minDraw}{(zOffset != 0 ? $", z {zOffset:+0;-0}" : "")}{(dryRun ? "  [dry run]" : "")}");
        int template = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(templatePath, StringComparison.OrdinalIgnoreCase));
        if (template < 0 || !pkg.ClassOf(pkg.Exports[template]).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"  no template StaticMeshComponent '{templatePath}'"); return 2; }
        int actor = pkg.Exports[template].OuterIndex - 1;
        if (actor < 0 || !pkg.ClassOf(pkg.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  the template isn't in a StaticMeshCollectionActor"); return 1; }

        // Target exports by path (the copied mesh and materials).
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pkg.Exports.Length; i++) byPath.TryAdd(pkg.PathOf(pkg.Exports[i]), i + 1);
        string SrcPath(int r)
        {
            if (r > 0) return src.PathOf(src.Exports[r - 1]);
            var parts = new List<string>();
            for (int guard = 0; r < 0 && guard < 16; guard++) { var im = src.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
            if (r > 0) parts.Insert(0, src.PathOf(src.Exports[r - 1]));
            return string.Join('.', parts);
        }

        var names = pkg.Names.Any(n => n.Equals("MinDrawDistance", StringComparison.OrdinalIgnoreCase)) ? new List<string>() : ["MinDrawDistance"];
        var tw = new TagWriter(pkg, names);
        byte[] tsrc = pkg.ReadExportBytes(pkg.Exports[template]);
        var ttags = TagWalker.Walk(pkg, tsrc, 8) ?? throw new InvalidDataException("template component properties don't parse");

        var add = new List<NewExport>();
        var refs = new List<int>();
        var expect = new List<(int Mesh, Vector3 T, Vector3 R, Vector3 S)>();
        foreach (var e in src.Exports)
        {
            if (!src.ClassOf(e).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = src.ReadExportBytes(e);
            var c = ComponentTransform.Read(src, d);
            if (c == null || c.MeshRef == 0 || c.HiddenGame) continue;
            string meshName = src.RefName(c.MeshRef);
            if (!meshNames.Any(m => m.Equals(meshName, StringComparison.OrdinalIgnoreCase))) continue;
            Vector3 t = c.Translation, r = c.Rotation, s = c.Scale;
            if (e.OuterIndex > 0 && src.ClassOf(src.Exports[e.OuterIndex - 1]) is string oc && oc.EndsWith("Actor", StringComparison.OrdinalIgnoreCase)
                && !oc.Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)
                && ComponentTransform.ReadActor(src, src.ReadExportBytes(src.Exports[e.OuterIndex - 1])) is { } a)
            {
                t = a.Translation + Vector3.Transform(t * a.Scale, ZonePlaceholders.RotatorMatrix(a.Rotation));
                r = a.Rotation + r; s = a.Scale * s;
            }
            t.Z += zOffset;
            string meshPath = SrcPath(c.MeshRef);
            if (!byPath.TryGetValue(meshPath, out int meshRef)) { Console.WriteLine($"  mesh {meshPath} isn't in the target (copy it first with --copy-export)"); return 1; }
            // Materials: the source component's list, each found in the target by path.
            var stags = TagWalker.Walk(src, d, 8);
            var mtag = stags?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase));
            var mats = new List<int>();
            if (mtag != null)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mtag.ValueAt));
                for (int k = 0; k < n; k++)
                {
                    int mr = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(mtag.ValueAt + 4 + 4 * k));
                    if (mr == 0) { mats.Add(0); continue; }
                    string mp = SrcPath(mr);
                    if (!byPath.TryGetValue(mp, out int dm)) { Console.WriteLine($"  material {mp} isn't in the target (copy it first with --copy-export)"); return 1; }
                    mats.Add(dm);
                }
            }

            using var ms = new MemoryStream();
            ms.Write(tsrc, 0, 8);
            foreach (var tg in ttags)
            {
                switch (tg.Name.ToLowerInvariant())
                {
                    case "staticmesh": ms.Write(tw.Tag("StaticMesh", "ObjectProperty", null, BitConverter.GetBytes(meshRef))); break;
                    case "materials": if (mats.Count > 0) ms.Write(tw.Tag("Materials", "ArrayProperty", null, [.. BitConverter.GetBytes(mats.Count), .. mats.SelectMany(BitConverter.GetBytes)])); break;
                    case "translation" or "rotation" or "scale3d" or "scale" or "visibilityid" or "vertexpositionversionnumber" or "mindrawdistance": break;
                    default: ms.Write(tsrc, tg.Start, tg.End - tg.Start); break;
                }
            }
            if (mats.Count > 0 && !ttags.Any(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)))
                ms.Write(tw.Tag("Materials", "ArrayProperty", null, [.. BitConverter.GetBytes(mats.Count), .. mats.SelectMany(BitConverter.GetBytes)]));
            ms.Write(tw.Tag("Translation", "StructProperty", "Vector", [.. BitConverter.GetBytes(t.X), .. BitConverter.GetBytes(t.Y), .. BitConverter.GetBytes(t.Z)]));
            ms.Write(tw.Tag("Rotation", "StructProperty", "Rotator", [.. BitConverter.GetBytes((int)r.X), .. BitConverter.GetBytes((int)r.Y), .. BitConverter.GetBytes((int)r.Z)]));
            if (s != Vector3.One) ms.Write(tw.Tag("Scale3D", "StructProperty", "Vector", [.. BitConverter.GetBytes(s.X), .. BitConverter.GetBytes(s.Y), .. BitConverter.GetBytes(s.Z)]));
            ms.Write(tw.Tag("MinDrawDistance", "FloatProperty", null, BitConverter.GetBytes(minDraw)));
            ms.Write(tsrc, ttags.NoneAt, 8);
            ms.Write(EmptyLighting);
            byte[] bytes = ms.ToArray();
            refs.Add(pkg.Exports.Length + add.Count + 1);
            add.Add(new NewExport(template, CellPlaceholders.NextNumber(pkg, template, add.Count), _ => bytes));
            expect.Add((meshRef, t, new Vector3((int)r.X, (int)r.Y, (int)r.Z), s));
            Console.WriteLine($"  {meshName}: at ({t.X:0}, {t.Y:0}, {t.Z:0}), yaw {r.Y * 360f / 65536f:0.#}, scale ({s.X:0.##}, {s.Y:0.##}, {s.Z:0.##}), {mats.Count} material(s)");
        }
        if (add.Count == 0) { Console.WriteLine("  no placements of those meshes in the source"); return 1; }

        byte[] actorBytes = CellPlaceholders.BuildActor(pkg, actor, tw, refs);
        var replace = new Dictionary<int, Func<long, byte[]>> { [actor] = _ => actorBytes };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, add, out var written, names);
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [actor], add, written, names);
            var w = Package.FromBytes(bytes);
            for (int k = 0; k < refs.Count; k++)
            {
                var c = ComponentTransform.Read(w, w.ReadExportBytes(w.Exports[refs[k] - 1]));
                if (c == null || c.MeshRef != expect[k].Mesh || Vector3.Distance(c.Translation, expect[k].T) > 0.01f || c.Rotation != expect[k].R || Vector3.Distance(c.Scale, expect[k].S) > 1e-4f)
                    problems.Add($"instance {k + 1} doesn't read back with its mesh and placement");
            }
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS ({refs.Count} instance(s) read back with their mesh and placement; actor lists them; every other export identical)");
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
