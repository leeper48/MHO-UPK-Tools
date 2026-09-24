namespace UpkMeshScan;

/// <summary>For each StaticMesh a package imports, which packages in the folder export a StaticMesh of that name. Read-only.</summary>
static class ImportSources
{
    public static int Run(string folder, string upkPath)
    {
        var target = Package.Open(upkPath);
        var wanted = target.Imports.Where(i => i.ClassName.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)).Select(i => i.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(folder, "*.upk", SearchOption.AllDirectories).Where(f => !Program.IsBackupName(f)).ToList();
        Parallel.ForEach(files, f =>
        {
            try
            {
                var pkg = Package.Open(f);
                foreach (var e in pkg.Exports)
                    if (wanted.Contains(e.ObjectName) && pkg.ClassOf(e).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
                        sources.GetOrAdd(e.ObjectName, _ => new()).Add(Path.GetFileName(f));
            }
            catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException) { }
        });
        Console.WriteLine($"{Path.GetFileName(upkPath)} imports {wanted.Count} StaticMesh(es):");
        foreach (var n in wanted.OrderBy(n => n))
            Console.WriteLine($"  {n}  <-  {(sources.TryGetValue(n, out var b) ? string.Join(", ", b.Distinct().Order()) : "(no exporter found)")}");
        var tally = sources.SelectMany(kv => kv.Value.Distinct()).GroupBy(x => x).OrderByDescending(g => g.Count());
        Console.WriteLine("Exporting packages by number of these meshes they provide:");
        foreach (var g in tally.Take(15)) Console.WriteLine($"  {g.Count(),4}  {g.Key}");
        return 0;
    }
}
