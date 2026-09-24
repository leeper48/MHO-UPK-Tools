using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace UpkMeshScan;

static class Program
{
    static readonly HashSet<string> StaticClasses = new(StringComparer.OrdinalIgnoreCase) { "StaticMesh", "FracturedStaticMesh" };
    static readonly HashSet<string> SkeletalClasses = new(StringComparer.OrdinalIgnoreCase) { "SkeletalMesh" };

    sealed record MeshHit(string Class, string Name, string Path, int Size);
    sealed record Result(string File, long Bytes, string Version, string ChunkSource, List<MeshHit> Meshes, string? Error);

    static int Main(string[] args)
    {
        string version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";
        Console.WriteLine($"UpkMeshScan v{version}");
        bool pause = LaunchedFromExplorer();
        try { return Run(args, version); }
        finally
        {
            if (pause) { Console.WriteLine(); Console.WriteLine("Press any key to close..."); Console.ReadKey(true); }
        }
    }

    static int Run(string[] args, string version)
    {
        int exportAt = Array.FindIndex(args, a => a.Equals("--export-fbx", StringComparison.OrdinalIgnoreCase));
        if (exportAt >= 0)
        {
            if (exportAt + 2 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            string exportDir = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : Path.Combine(AppContext.BaseDirectory, "exports");
            return StaticMeshExport.Run(args[exportAt + 1], args[exportAt + 2], exportDir);
        }

        int dumpAt = Array.FindIndex(args, a => a.Equals("--dump-export", StringComparison.OrdinalIgnoreCase));
        if (dumpAt >= 0)
        {
            if (dumpAt + 2 >= args.Length) { Usage(); return 2; }
            int outAt = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase) || a.Equals("-o", StringComparison.OrdinalIgnoreCase));
            string dumpDir = outAt >= 0 && outAt + 1 < args.Length ? args[outAt + 1] : Path.Combine(AppContext.BaseDirectory, "dumps");
            return ExportDump.Run(args[dumpAt + 1], args[dumpAt + 2], dumpDir);
        }

        string? folder = null, outPath = null;
        bool recursive = true, skeletal = false, includeEmpty = false, includeBackups = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--out": case "-o": outPath = i + 1 < args.Length ? args[++i] : null; break;
                case "--top-only": recursive = false; break;
                case "--skeletal": skeletal = true; break;
                case "--include-empty": includeEmpty = true; break;
                case "--include-backups": includeBackups = true; break;
                case "--help": case "-h": case "/?": Usage(); return 0;
                default:
                    if (args[i].StartsWith('-')) { Console.WriteLine($"Unknown option: {args[i]}"); Usage(); return 2; }
                    folder = args[i]; break;
            }
        }

        if (folder == null)
        {
            Console.Write("Folder to scan: ");
            folder = Console.ReadLine()?.Trim().Trim('"');
        }
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            Usage();
            return 2;
        }
        folder = Path.GetFullPath(folder);
        outPath ??= Path.Combine(AppContext.BaseDirectory, $"{new DirectoryInfo(folder).Name}_MeshScan.txt");

        var files = Directory.EnumerateFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .ToList();
        int skippedBackups = 0;
        if (!includeBackups)
        {
            int before = files.Count;
            files = files.Where(f => !IsBackupName(f)).ToList();
            skippedBackups = before - files.Count;
        }
        Console.WriteLine($"Scanning {files.Count} package(s) in {folder}{(recursive ? " (recursive)" : "")}{(skippedBackups > 0 ? $", skipped {skippedBackups} bak/copy file(s)" : "")} ...");

        var sw = Stopwatch.StartNew();
        var results = new ConcurrentBag<Result>();
        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, f =>
        {
            results.Add(ScanOne(f, skeletal));
            int n = Interlocked.Increment(ref done);
            if (n % 200 == 0 || n == files.Count) Console.WriteLine($"  {n}/{files.Count}");
        });

        var ordered = results.OrderBy(r => Path.GetRelativePath(folder, r.File), StringComparer.OrdinalIgnoreCase).ToList();
        WriteReport(outPath, folder, version, ordered, skeletal, includeEmpty, skippedBackups, sw.Elapsed);

        int meshes = ordered.Sum(r => r.Meshes.Count);
        int failed = ordered.Count(r => r.Error != null);
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1}s: {meshes} mesh(es) in {ordered.Count(r => r.Meshes.Count > 0)} package(s); {failed} package(s) failed to read.");
        Console.WriteLine($"Report: {outPath}");
        return 0;
    }

    static Result ScanOne(string file, bool skeletal)
    {
        long bytes = 0;
        try
        {
            bytes = new FileInfo(file).Length;
            var pkg = Package.Open(file);
            var hits = new List<MeshHit>();
            foreach (var e in pkg.Exports)
            {
                string cls = pkg.ClassOf(e);
                if (StaticClasses.Contains(cls) || (skeletal && SkeletalClasses.Contains(cls)))
                    hits.Add(new MeshHit(cls, e.ObjectName, pkg.PathOf(e), e.SerialSize));
            }
            hits.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return new Result(file, bytes, $"v{pkg.FileVersion}/L{pkg.LicenseeVersion}", pkg.ChunkSource, hits, null);
        }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return new Result(file, bytes, "?", "?", new List<MeshHit>(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    static void WriteReport(string outPath, string folder, string version, List<Result> results, bool skeletal, bool includeEmpty, int skippedBackups, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        var ok = results.Where(r => r.Error == null).ToList();
        var withMeshes = ok.Where(r => r.Meshes.Count > 0).ToList();
        int staticCount = ok.Sum(r => r.Meshes.Count(m => !SkeletalClasses.Contains(m.Class)));
        int skelCount = ok.Sum(r => r.Meshes.Count(m => SkeletalClasses.Contains(m.Class)));

        sb.AppendLine($"UpkMeshScan v{version} — mesh scan report");
        sb.AppendLine($"Folder   : {folder}");
        sb.AppendLine($"Date     : {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Packages : {results.Count} scanned, {ok.Count} read OK, {results.Count - ok.Count} failed ({elapsed.TotalSeconds:F1}s){(skippedBackups > 0 ? $"; {skippedBackups} bak/copy file(s) skipped" : "")}");
        sb.AppendLine($"Meshes   : {staticCount} static{(skeletal ? $", {skelCount} skeletal" : "")} in {withMeshes.Count} package(s)");
        sb.AppendLine($"Versions : {string.Join(", ", ok.GroupBy(r => r.Version).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}"))}");
        sb.AppendLine($"Chunks   : {string.Join(", ", ok.GroupBy(r => r.ChunkSource).Select(g => $"{g.Key} x{g.Count()}"))}");
        sb.AppendLine(new string('=', 100));

        foreach (var r in includeEmpty ? ok : withMeshes)
        {
            sb.AppendLine();
            sb.AppendLine($"{Path.GetRelativePath(folder, r.File)}   ({r.Meshes.Count} mesh{(r.Meshes.Count == 1 ? "" : "es")})");
            if (r.Meshes.Count == 0) continue;
            string Label(MeshHit m) =>
                (SkeletalClasses.Contains(m.Class) ? "[Skel] " : m.Class.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ? "" : $"[{m.Class}] ") + m.Name;
            int w = Math.Min(64, r.Meshes.Max(m => Label(m).Length));
            foreach (var m in r.Meshes)
            {
                string path = m.Path.Equals(m.Name, StringComparison.Ordinal) ? "" : $"  {m.Path}";
                sb.AppendLine($"    {Label(m).PadRight(w)}  {m.Size,12:N0} B{path}");
            }
        }

        var failed = results.Where(r => r.Error != null).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', 100));
            sb.AppendLine($"FAILED TO READ ({failed.Count})");
            foreach (var r in failed) sb.AppendLine($"    {Path.GetRelativePath(folder, r.File)}  —  {r.Error}");
        }

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Backups and copies of packages ("Foo - Copy.upk", "Foo_bak.upk") sit beside the live files; skip them by default.</summary>
    static bool IsBackupName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Contains("bak", StringComparison.OrdinalIgnoreCase) || name.Contains("copy", StringComparison.OrdinalIgnoreCase);
    }

    static void Usage()
    {
        Console.WriteLine("""

        Usage: UpkMeshScan <folder> [options]
          Scans every .upk/.umap under <folder> and lists the static meshes each one contains.

          --out <file>       Report path (default: <FolderName>_MeshScan.txt next to the exe)
          --top-only         Don't recurse into subfolders
          --skeletal         Also list SkeletalMesh exports (tagged [Skel])
          --include-empty    Also list packages that contain no meshes
          --include-backups  Also scan files with "bak" or "copy" in the name (skipped by default)

        Usage: UpkMeshScan --dump-export <package.upk> <export-name-or-path> [--out <folder>]
          Writes that export's raw bytes (.bin) and an annotated dump (.txt: property tags, then
          hex of the native data) to <folder> (default: dumps\ next to the exe). Read-only.

        Usage: UpkMeshScan --export-fbx <package.upk> <staticmesh-name-or-path> [--out <folder>]
          Writes LOD 0 of that StaticMesh to <folder>\<name>.fbx (default: exports\ next to the exe),
          one part per material section. Read-only on the package.
        """);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint[] list, uint count);

    /// <summary>True when double-clicked / drag-dropped (we own the console), so the window doesn't vanish.</summary>
    static bool LaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return false;
        try { return GetConsoleProcessList(new uint[4], 4) <= 1; } catch { return false; }
    }
}
