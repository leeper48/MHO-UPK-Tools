using System.Text;

namespace UpkMeshScan;

/// <summary>
/// --export-deps: everything one export pulls in, i.e. what a copy of it into another package would need.
/// Follows object references in tagged properties (nested structs, struct arrays, and arrays of 4-byte object
/// references), outer chains, and texture reference lists in native data (heuristic: a count followed by that
/// many references that all resolve to Texture* objects — the layout seen in Material / MaterialInstanceConstant
/// resources). Reports each reached export with class, size and native-data size, and the imports reached. Read-only.
/// </summary>
static class ExportDeps
{
    public static int Run(string upkPath, string exportName, int maxDepth, IReadOnlyCollection<string>? cut = null)
    {
        var pkg = Package.Open(upkPath);
        int root = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(exportName, StringComparison.OrdinalIgnoreCase));
        if (root < 0) root = Array.FindIndex(pkg.Exports, e => e.ObjectName.Equals(exportName, StringComparison.OrdinalIgnoreCase));
        if (root < 0) { Console.WriteLine($"No export named '{exportName}' in {Path.GetFileName(upkPath)}."); return 2; }

        var depth = new Dictionary<int, int> { [root + 1] = 0 };   // object reference -> depth reached at
        var via = new Dictionary<int, string>();
        var natives = new Dictionary<int, (int Bytes, int Refs)>();
        var queue = new Queue<int>();
        queue.Enqueue(root + 1);
        while (queue.Count > 0)
        {
            int r = queue.Dequeue();
            void Reach(int target, string how)
            {
                if (target == 0 || depth.ContainsKey(target) || !Valid(pkg, target)) return;
                depth[target] = depth[r] + 1;
                via[target] = how;
                if (target > 0 && depth[target] <= maxDepth) queue.Enqueue(target);
            }
            if (r < 0)
            {
                Reach(pkg.Imports[-r - 1].OuterIndex, "outer");
                continue;
            }
            var e = pkg.Exports[r - 1];
            Reach(e.OuterIndex, "outer");
            byte[] d = pkg.ReadExportBytes(e);
            string cls = pkg.ClassOf(e);
            int start = cls.EndsWith("Component", StringComparison.OrdinalIgnoreCase) ? 8 : 4;
            var refs = new List<(int Ref, string Where)>();
            int end = Tags(pkg, d, start, d.Length, "", refs);
            foreach (var (t, w) in refs)
                if (cut == null || !cut.Any(c => w.Split('.', '[')[0].Equals(c, StringComparison.OrdinalIgnoreCase))) Reach(t, w);
            if (end > 0 && end < d.Length)
            {
                var native = NativeTextureRefs(pkg, d, end);
                natives[r] = (d.Length - end, native.Count);
                foreach (int t in native) Reach(t, "native texture list");
            }
        }

        var sb = new StringBuilder();
        var exports = depth.Keys.Where(k => k > 0).OrderBy(k => depth[k]).ThenBy(k => k).ToList();
        var imports = depth.Keys.Where(k => k < 0).OrderBy(k => k).ToList();
        sb.AppendLine($"Closure of {pkg.PathOf(pkg.Exports[root])} in {Path.GetFileName(upkPath)} (depth limit {maxDepth}{(cut?.Count > 0 ? $", not following: {string.Join(", ", cut)}" : "")}):");
        sb.AppendLine($"  {exports.Count} export(s), {exports.Sum(k => pkg.Exports[k - 1].SerialSize):N0} bytes; {imports.Count} import(s)");
        foreach (var grp in exports.GroupBy(k => pkg.ClassOf(pkg.Exports[k - 1])).OrderByDescending(g => g.Count()))
            sb.AppendLine($"    {grp.Count(),5} x {grp.Key}  ({grp.Sum(k => pkg.Exports[k - 1].SerialSize):N0} bytes)");
        sb.AppendLine();
        foreach (int k in exports)
        {
            var e = pkg.Exports[k - 1];
            string nat = natives.TryGetValue(k, out var n) ? $"  native {n.Bytes:N0} B{(n.Refs > 0 ? $", {n.Refs} texture ref(s)" : "")}" : "";
            sb.AppendLine($"  d{depth[k]} #{k,-6} {pkg.ClassOf(e),-34} {e.SerialSize,9:N0} B  {pkg.PathOf(e)}{nat}{(via.TryGetValue(k, out var v) ? $"   <- {v}" : "")}");
        }
        sb.AppendLine();
        sb.AppendLine("Imports reached (must exist wherever the copy goes, or be cut):");
        foreach (int k in imports)
        {
            var i = pkg.Imports[-k - 1];
            sb.AppendLine($"  import {-k,-6} {i.ClassName,-26} {ImportPath(pkg, k)}   <- {via.GetValueOrDefault(k, "")}");
        }
        Console.Write(sb.ToString());
        return 0;
    }

    static bool Valid(Package pkg, int r) => r > 0 ? r <= pkg.Exports.Length : -r <= pkg.Imports.Length;

    static string ImportPath(Package pkg, int r)
    {
        var parts = new List<string>();
        for (int guard = 0; r != 0 && guard < 16; guard++)
        {
            if (r < 0) { var i = pkg.Imports[-r - 1]; parts.Add(i.ObjectName); r = i.OuterIndex; }
            else { parts.Add(pkg.PathOf(pkg.Exports[r - 1])); break; }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>Walks tagged properties from p to "None" collecting object references; returns the offset after None, or -1.</summary>
    static int Tags(Package pkg, byte[] d, int p, int end, string prefix, List<(int, string)> refs)
    {
        try
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                string name = Name(pkg, d, ref p);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return p;
                string type = Name(pkg, d, ref p).ToLowerInvariant();
                int size = BitConverter.ToInt32(d, p), _ = BitConverter.ToInt32(d, p + 4);
                p += 8;
                if (type is "structproperty" or "byteproperty") Name(pkg, d, ref p);
                else if (type == "boolproperty") p += 1;
                if (size < 0 || p + size > end) return -1;
                string where = prefix + name;
                switch (type)
                {
                    case "objectproperty" or "classproperty" or "componentproperty" or "interfaceproperty" when size >= 4:
                        refs.Add((BitConverter.ToInt32(d, p), where));
                        break;
                    case "structproperty":
                        Tags(pkg, d, p, p + size, where + ".", refs);   // native structs (vectors, guids) just fail to parse
                        break;
                    case "arrayproperty" when size >= 4:
                        RefArray(pkg, d, p, size, where, refs);
                        break;
                }
                p += size;
            }
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException) { }
        return -1;
    }

    static void RefArray(Package pkg, byte[] d, int p, int size, string where, List<(int, string)> refs)
    {
        int count = BitConverter.ToInt32(d, p), end = p + size;
        if (count <= 0) return;
        // Array of tagged structs?
        var tmp = new List<(int, string)>();
        int q = p + 4;
        for (int i = 0; i < count && q > 0; i++) q = Tags(pkg, d, q, end, $"{where}[{i}].", tmp);
        if (q == end) { refs.AddRange(tmp); return; }
        // Array of object references (4 bytes each, every non-zero entry valid)?
        if (size - 4 == count * 4)
        {
            var vals = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(d, p + 4 + 4 * i)).ToList();
            if (vals.All(v => v == 0 || Valid(pkg, v)) && vals.Any(v => v != 0))
                for (int i = 0; i < count; i++) if (vals[i] != 0) refs.Add((vals[i], $"{where}[{i}]"));
        }
    }

    /// <summary>Heuristic: int32 count (1..64) followed by that many references to Texture* objects.</summary>
    static List<int> NativeTextureRefs(Package pkg, byte[] d, int start)
    {
        var found = new List<int>();
        for (int p = start; p + 8 <= d.Length; p++)
        {
            int count = BitConverter.ToInt32(d, p);
            if (count < 1 || count > 64 || p + 4 + 4 * count > d.Length) continue;
            var vals = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(d, p + 4 + 4 * i)).ToList();
            if (vals.All(v => v != 0 && Valid(pkg, v) && ClassOfRef(pkg, v).StartsWith("Texture", StringComparison.OrdinalIgnoreCase)))
            {
                found.AddRange(vals);
                p += 4 * count;
            }
        }
        return found.Distinct().ToList();
    }

    static string ClassOfRef(Package pkg, int r) => r > 0 ? pkg.ClassOf(pkg.Exports[r - 1]) : pkg.Imports[-r - 1].ClassName;

    static string Name(Package pkg, byte[] d, ref int p)
    {
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4);
        p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}
