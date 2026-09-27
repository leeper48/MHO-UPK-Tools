namespace MhoPackageModifier;

/// <summary>
/// --find-mic: lists MaterialInstanceConstants in a folder whose Parent's name contains a text and whose static switches
/// include every given name=value (e.g. a masked environment instance with useemissive=true, to reuse its compiled
/// shaders). Read-only; skips .bak/copy files like the scan.
/// </summary>
static class FindMic
{
    public static int Run(string folder, string parentContains, IReadOnlyList<(string Name, bool Value)> wanted, int limit)
    {
        int found = 0, scanned = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "*.upk").Where(p => !Program.IsBackupName(p)))
        {
            Package pkg;
            try { pkg = Package.Open(path); } catch (Exception) { continue; }
            scanned++;
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                if (!pkg.ClassOf(e).Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] d;
                try { d = pkg.ReadExportBytes(e); } catch (Exception) { continue; }
                var parent = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(t => t.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase));
                if (parent == null) continue;
                string pname = pkg.RefName(BitConverter.ToInt32(d, parent.ValueAt));
                if (!pname.Contains(parentContains, StringComparison.OrdinalIgnoreCase)) continue;
                List<string> sw;
                try { sw = ExportCopy.StaticSwitches(pkg, d); } catch (Exception) { continue; }
                bool ok = wanted.All(w => sw.Any(s => s.Contains($": {w.Name} = {(w.Value ? "true" : "false")}", StringComparison.OrdinalIgnoreCase)));
                if (!ok) continue;
                Console.WriteLine($"{Path.GetFileName(path)}  {pkg.PathOf(e)}  (parent {pname})");
                if (++found >= limit) { Console.WriteLine($"... stopped at {limit}"); return 0; }
            }
        }
        Console.WriteLine($"{found} match(es) in {scanned} package(s).");
        return 0;
    }
}
