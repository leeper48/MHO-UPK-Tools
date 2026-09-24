using System.Text;

namespace UpkMeshScan;

/// <summary>
/// Lists StaticMeshComponents in one package whose StaticMesh property points at the named mesh
/// (export or import), with the bytes that follow their property block (where LODData lives). Read-only.
/// </summary>
static class MeshUsers
{
    public static int Run(string upkPath, string meshName)
    {
        var pkg = Package.Open(upkPath);
        int found = 0;
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!pkg.ClassOf(e).Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = pkg.ReadExportBytes(e);
            foreach (int start in new[] { 4, 8, 16 })   // NetIndex; MHO component (int32 0, NetIndex); stock component preamble
            {
                var props = TryProps(pkg, d, start, out int end);
                if (props is null) continue;
                if (!props.TryGetValue("staticmesh", out int meshRef) || !pkg.RefName(meshRef).Equals(meshName, StringComparison.OrdinalIgnoreCase)) break;
                found++;
                Console.WriteLine($"{pkg.ClassOf(e)} {pkg.PathOf(e)}  ({d.Length:N0} B, properties from 0x{start:X}, native from 0x{end:X})");
                Console.WriteLine($"  properties: {string.Join(", ", props.Keys)}");
                var sb = new StringBuilder();
                for (int p = end; p < Math.Min(d.Length, end + 96); p += 16)
                    sb.AppendLine($"  {p:X6}  {string.Join(' ', d.Skip(p).Take(Math.Min(16, d.Length - p)).Select(b => b.ToString("X2")))}");
                Console.Write(sb);
                break;
            }
        }
        Console.WriteLine($"{found} component(s) use '{meshName}' in {Path.GetFileName(upkPath)}.");
        if (found == 0)
        {
            // Diagnose: which classes look like components, and do their property blocks parse at all?
            foreach (var g in pkg.Exports.GroupBy(x => pkg.ClassOf(x)).Where(g => g.Key.Contains("component", StringComparison.OrdinalIgnoreCase) || g.Key.Contains("staticmesh", StringComparison.OrdinalIgnoreCase)).OrderByDescending(g => g.Count()))
            {
                int parsed = 0, withMesh = 0;
                var meshTargets = new Dictionary<string, int>();
                foreach (var x in g)
                {
                    byte[] d = pkg.ReadExportBytes(x);
                    var props = TryProps(pkg, d, 4, out _) ?? TryProps(pkg, d, 8, out _) ?? TryProps(pkg, d, 16, out _);
                    if (props is null) continue;
                    parsed++;
                    if (props.TryGetValue("staticmesh", out int r)) { withMesh++; string n = pkg.RefName(r); meshTargets[n] = meshTargets.GetValueOrDefault(n) + 1; }
                }
                if (parsed == 0)
                {
                    var first = g.First(); byte[] fd = pkg.ReadExportBytes(first);
                    Console.WriteLine($"    first: {pkg.PathOf(first)} ({fd.Length} B)");
                    for (int p = 0; p < Math.Min(fd.Length, 64); p += 4)
                    {
                        int v = BitConverter.ToInt32(fd, p);
                        string asName = v >= 0 && v < pkg.Names.Length ? pkg.Names[v] : "";
                        string asRef = v != 0 && Math.Abs(v) <= Math.Max(pkg.Exports.Length, pkg.Imports.Length) ? pkg.RefName(v) : "";
                        Console.WriteLine($"      +{p,2}: {v,11}  name?{asName,-28} ref?{asRef}");
                    }
                }
                Console.WriteLine($"  class {g.Key}: {g.Count()} exports, {parsed} property blocks parsed, {withMesh} with a StaticMesh property");
                foreach (var t in meshTargets.Where(t => t.Key.Contains("bldg_b", StringComparison.OrdinalIgnoreCase)).Take(10)) Console.WriteLine($"      -> {t.Key} x{t.Value}");
            }
        }
        return 0;
    }

    /// <summary>Property name -> int32 value (first 4 bytes of each value), or null on desync.</summary>
    static Dictionary<string, int>? TryProps(Package pkg, byte[] d, int p, out int end)
    {
        end = -1;
        var props = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int guard = 0; guard < 1024; guard++)
            {
                string name = Name(pkg, d, ref p);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { end = p; return props; }
                string type = Name(pkg, d, ref p).ToLowerInvariant();
                int size = BitConverter.ToInt32(d, p), arrayIndex = BitConverter.ToInt32(d, p + 4); p += 8;
                if (type is "structproperty" or "byteproperty") Name(pkg, d, ref p);
                if (type == "boolproperty") p += 1;
                if (size < 0 || p + size > d.Length) return null;
                if (size >= 4 && arrayIndex == 0) props.TryAdd(name, BitConverter.ToInt32(d, p));
                else props.TryAdd(name, 0);
                p += size;
            }
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException or ArgumentException) { }
        return null;
    }

    static string Name(Package pkg, byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new PackageFormatException("past end");
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException("bad name");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}
