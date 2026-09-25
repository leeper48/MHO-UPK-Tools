using System.Buffers.Binary;
using System.Numerics;

namespace UpkMeshScan;

/// <summary>
/// --add-component-copies: adds copies of a placed StaticMeshComponent, each turned by an extra yaw around the same
/// pivot, to the same StaticMeshCollectionActor — e.g. a backdrop mesh that only covers part of the view (Asgard_Hub_B's
/// starfield: azimuth ~330..120 degrees, black elsewhere; --sky-coverage shows it). The copies keep every property of the
/// original (mesh, materials, translation, pitch/roll) with the yaw changed, and carry an empty lighting record (no
/// baked light or shadow maps — fine for unlit materials, as our placeholder components). Same .bak / verify / swap.
/// </summary>
static class ComponentCopies
{
    /// <summary>MHO component lighting record with nothing baked (LOD count 1, then empty), as the placeholder components.</summary>
    static readonly byte[] EmptyLighting = [1, 0, 0, 0, .. new byte[17]];

    /// <param name="turns">per copy: (yaw, pitch, roll) in degrees, added to the original's rotation.</param>
    public static int Run(string upkPath, string componentPath, IReadOnlyList<Vector3> turns, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Component copies: {componentPath} in {Path.GetFileName(upkPath)}, {turns.Count} copies{(dryRun ? "  [dry run]" : "")}");
        int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(componentPath, StringComparison.OrdinalIgnoreCase));
        if (comp < 0 || !pkg.ClassOf(pkg.Exports[comp]).Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  no StaticMeshComponent with that path"); return 2; }
        int actor = pkg.Exports[comp].OuterIndex - 1;
        if (actor < 0 || !pkg.ClassOf(pkg.Exports[actor]).Equals("StaticMeshCollectionActor", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("  the component isn't in a StaticMeshCollectionActor"); return 1; }

        byte[] src = pkg.ReadExportBytes(pkg.Exports[comp]);
        var tags = TagWalker.Walk(pkg, src, 8) ?? throw new InvalidDataException("component properties don't parse (MHO layout, from byte 8)");
        var rotTag = tags.FirstOrDefault(t => t.Name.Equals("Rotation", StringComparison.OrdinalIgnoreCase));
        if (rotTag == null) { Console.WriteLine("  the component has no Rotation property (not supported yet)"); return 1; }
        // Rotator: Pitch, Yaw, Roll as ints (65536 = 360 degrees).
        int pitch0 = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(rotTag.ValueAt)), yaw0 = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(rotTag.ValueAt + 4)),
            roll0 = BinaryPrimitives.ReadInt32LittleEndian(src.AsSpan(rotTag.ValueAt + 8));
        static int Units(float degrees) => (int)MathF.Round(degrees * 65536f / 360f);

        var tw = new TagWriter(pkg);
        var add = new List<NewExport>();
        var refs = new List<int>();
        var built = new List<(Vector3 Rot, byte[] Bytes)>();
        for (int k = 0; k < turns.Count; k++)
        {
            int yaw = yaw0 + Units(turns[k].X), pitch = pitch0 + Units(turns[k].Y), roll = roll0 + Units(turns[k].Z);
            using var ms = new MemoryStream();
            ms.Write(src, 0, 8);
            foreach (var t in tags)
            {
                if (t == rotTag)
                {
                    byte[] tag = src[t.Start..t.End];
                    int v = rotTag.ValueAt - t.Start;
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(v), pitch);
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(v + 4), yaw);
                    BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(v + 8), roll);
                    ms.Write(tag);
                }
                else ms.Write(src, t.Start, t.End - t.Start);
            }
            ms.Write(src, tags.NoneAt, 8);                                   // None
            ms.Write(EmptyLighting);
            byte[] bytes = ms.ToArray();
            built.Add((new Vector3(pitch, yaw, roll), bytes));
            refs.Add(pkg.Exports.Length + add.Count + 1);
            add.Add(new NewExport(comp, CellPlaceholders.NextNumber(pkg, comp, k), _ => bytes));
            Console.WriteLine($"  copy {k + 1}: yaw {yaw * 360.0 / 65536:0.#}, pitch {pitch * 360.0 / 65536:0.#}, roll {roll * 360.0 / 65536:0.#} degrees, {bytes.Length} bytes (source {src.Length}; baked lighting dropped)");
        }
        byte[] actorBytes = CellPlaceholders.BuildActor(pkg, actor, tw, refs);
        var replace = new Dictionary<int, Func<long, byte[]>> { [actor] = _ => actorBytes };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, add, out var written);

        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [actor], add, written);
            var w = Package.FromBytes(bytes);
            var o = ComponentTransform.Read(pkg, src);
            for (int k = 0; k < refs.Count; k++)
            {
                var c = ComponentTransform.Read(w, w.ReadExportBytes(w.Exports[refs[k] - 1]));
                if (c == null || o == null || c.MeshRef != o.MeshRef || c.Translation != o.Translation || c.Scale != o.Scale
                    || c.Rotation != built[k].Rot)
                    problems.Add($"copy {k + 1} doesn't read back with the original's mesh/placement and its new rotation");
            }
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (actor lists the {refs.Count} copies; each reads back with the original's mesh, translation, scale and its new rotation; every other export identical)");
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
