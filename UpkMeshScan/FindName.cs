namespace UpkMeshScan;

/// <summary>Lists every package whose imports or exports use a given object name. Read-only.</summary>
static class FindName
{
    public static int Run(string folder, string name, bool includeBackups)
    {
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Where(f => includeBackups || !Program.IsBackupName(f))
            .ToList();
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files, f =>
        {
            try
            {
                var pkg = Package.Open(f);
                if (!pkg.Names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
                var lines = new List<string>();
                foreach (var e in pkg.Exports.Where(e => e.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    lines.Add($"    export {pkg.ClassOf(e)} {pkg.PathOf(e)}");
                foreach (var i in pkg.Imports.Where(i => i.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    lines.Add($"    import {i.ClassName} {i.ObjectName}");
                if (lines.Count == 0) lines.Add("    (name table only)");
                hits.Add($"{Path.GetRelativePath(folder, f)}\n{string.Join('\n', lines)}");
            }
            catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException) { }
        });
        foreach (string h in hits.OrderBy(h => h, StringComparer.OrdinalIgnoreCase)) Console.WriteLine(h);
        Console.WriteLine($"{hits.Count} package(s) use the name '{name}' (of {files.Count} scanned).");
        return 0;
    }
}
