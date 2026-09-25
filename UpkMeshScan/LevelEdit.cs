using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// --add-level-actor: registers an actor export with the persistent level, so the game spawns it — needed when an
/// actor was copied into a level that had none of its kind (e.g. a StaticMeshCollectionActor with a sky dome into
/// Madripoor_HighTown_B). ULevel's native data starts with the Actors list (a TTransArray: owner reference = the
/// level itself, count, actor references; checked on Madripoor_HighTown_B: owner #3 = the level, 4 actors
/// worldinfo / null / light collection / fog); the reference is appended and the count raised, everything after
/// the list is copied as is. Same .bak / verified temp / swap as the other writers.
/// </summary>
static class LevelEdit
{
    public static int AddActor(string upkPath, string actorPath, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Add level actor: {actorPath} in {Path.GetFileName(upkPath)}{(dryRun ? "  [dry run]" : "")}");
        int level = Array.FindIndex(pkg.Exports, e => pkg.ClassOf(e).Equals("Level", StringComparison.OrdinalIgnoreCase));
        int actor = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(actorPath, StringComparison.OrdinalIgnoreCase));
        if (level < 0 || actor < 0) { Console.WriteLine($"  {(level < 0 ? "no Level export" : $"no export '{actorPath}'")}"); return 2; }
        if (pkg.Exports[actor].OuterIndex != level + 1) { Console.WriteLine("  the actor isn't inside the level"); return 1; }

        byte[] d = pkg.ReadExportBytes(pkg.Exports[level]);
        var tags = TagWalker.Walk(pkg, d, 4) ?? throw new InvalidDataException("level properties don't parse");
        int p = tags.NoneAt + 8;
        int owner = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)), count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
        if (owner != level + 1) { Console.WriteLine($"  the Actors list's owner is {owner}, not the level (#{level + 1}); layout not as expected"); return 1; }
        if (count < 0 || count > 100000 || p + 8 + 4 * count > d.Length) { Console.WriteLine($"  implausible actor count {count}"); return 1; }
        var actors = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8 + 4 * i))).ToList();
        if (actors.Any(a => a != 0 && (a < 0 || a > pkg.Exports.Length || !pkg.ClassOf(pkg.Exports[a - 1]).Contains("Actor", StringComparison.OrdinalIgnoreCase) && !pkg.ClassOf(pkg.Exports[a - 1]).Equals("WorldInfo", StringComparison.OrdinalIgnoreCase) && !pkg.ClassOf(pkg.Exports[a - 1]).Contains("Fog", StringComparison.OrdinalIgnoreCase))))
            Console.WriteLine("  note: some existing entries aren't obviously actors; continuing (the list is copied as is)");
        if (actors.Contains(actor + 1)) { Console.WriteLine("  already in the level's actor list"); return 0; }
        Console.WriteLine($"  actors: {string.Join(", ", actors.Select(a => a == 0 ? "null" : pkg.Exports[a - 1].ObjectName))} + {pkg.Exports[actor].ObjectName}");

        int end = p + 8 + 4 * count;
        byte[] nd = [.. d.AsSpan(0, p + 4), .. BitConverter.GetBytes(count + 1), .. d.AsSpan(p + 8, 4 * count), .. BitConverter.GetBytes(actor + 1), .. d.AsSpan(end)];
        var replace = new Dictionary<int, Func<long, byte[]>> { [level] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [level], [], written);
            var w = Package.FromBytes(bytes);
            byte[] wd = w.ReadExportBytes(w.Exports[level]);
            int wc = BinaryPrimitives.ReadInt32LittleEndian(wd.AsSpan(p + 4));
            var wa = Enumerable.Range(0, wc).Select(i => BinaryPrimitives.ReadInt32LittleEndian(wd.AsSpan(p + 8 + 4 * i))).ToList();
            if (!wa.SequenceEqual(actors.Append(actor + 1))) problems.Add("actor list doesn't read back as the old list + the new actor");
            if (!wd.AsSpan(0, p + 4).SequenceEqual(d.AsSpan(0, p + 4)) || !wd.AsSpan(p + 8 + 4 * wc).SequenceEqual(d.AsSpan(end))) problems.Add("level data outside the actor list changed");
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (actor list = old + new; the rest of the level and every other export identical)");
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
