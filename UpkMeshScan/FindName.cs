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
        // A '*' in the name makes it a wildcard pattern (e.g. "*sky*"); otherwise the match is exact.
        Func<string, bool> match = name.Contains('*')
            ? new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name).Replace(@"\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).IsMatch
            : n => n.Equals(name, StringComparison.OrdinalIgnoreCase);
        var hits = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(files, f =>
        {
            try
            {
                var pkg = Package.Open(f);
                if (!pkg.Names.Any(match)) return;
                var lines = new List<string>();
                foreach (var e in pkg.Exports.Where(e => match(e.ObjectName)))
                    lines.Add($"    export {pkg.ClassOf(e)} {pkg.PathOf(e)}");
                foreach (var i in pkg.Imports.Where(i => match(i.ObjectName)))
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
