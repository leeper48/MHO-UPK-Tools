using System.Buffers.Binary;

namespace UpkMeshScan;

/// <summary>
/// --set-object: points one object reference in an export at another object, by path — an ObjectProperty, or one
/// element of an array of object references (e.g. a StaticMeshComponent's Materials[0], to give a placed mesh a
/// different material). Same size, so only that export's 4 bytes change. Tagged properties start at byte 8 for
/// components (MHO: extra int32, then NetIndex), 4 otherwise. Same .bak / verified temp / swap as the other
/// writers; verified by reading the reference back and comparing every other byte.
/// </summary>
static class ObjectEdit
{
    public static int Run(string upkPath, string exportPath, string property, string targetPath, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        Console.WriteLine($"Set object: {exportPath} {property} -> {targetPath} in {Path.GetFileName(upkPath)}{(dryRun ? "  [dry run]" : "")}");

        int index = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(exportPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) { Console.WriteLine($"  no export '{exportPath}'"); return 2; }
        int target = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (target < 0) { Console.WriteLine($"  no export '{targetPath}' (only exports of this package can be targets)"); return 2; }
        string propName = property; int element = -1;
        if (property.EndsWith(']') && property.Contains('['))
        {
            propName = property[..property.IndexOf('[')];
            element = int.Parse(property[(property.IndexOf('[') + 1)..^1]);
        }

        byte[] d = pkg.ReadExportBytes(pkg.Exports[index]);
        bool component = pkg.ClassOf(pkg.Exports[index]).EndsWith("Component", StringComparison.OrdinalIgnoreCase);
        var tags = TagWalker.Walk(pkg, d, component ? 8 : 4) ?? throw new InvalidDataException("properties don't parse");
        var tag = tags.FirstOrDefault(t => t.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
        if (tag == null) { Console.WriteLine($"  no property '{propName}' in {exportPath} (only existing properties can be changed)"); return 1; }
        int at;
        if (tag.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && tag.Size == 4 && element < 0) at = tag.ValueAt;
        else if (tag.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase) && element >= 0)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(tag.ValueAt));
            if (tag.Size != 4 + 4 * count) { Console.WriteLine($"  {propName} isn't an array of object references ({count} elements, {tag.Size} bytes)"); return 1; }
            if (element >= count) { Console.WriteLine($"  {propName} has {count} element(s)"); return 1; }
            at = tag.ValueAt + 4 + 4 * element;
        }
        else { Console.WriteLine($"  {propName} is a {tag.Type} of {tag.Size} bytes; give name[i] for an array element"); return 1; }

        int old = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
        Console.WriteLine($"  {property}: {Describe(pkg, old)} -> {Describe(pkg, target + 1)}");
        byte[] nd = d.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at), target + 1);

        var replace = new Dictionary<int, Func<long, byte[]>> { [index] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        List<string> Check(byte[] bytes)
        {
            var problems = PackageRebuilder.Verify(pkg, bytes, [index], [], written);
            var w = Package.FromBytes(bytes);
            byte[] wd = w.ReadExportBytes(w.Exports[index]);
            if (wd.Length != d.Length) problems.Add("export size changed");
            else
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(wd.AsSpan(at)) != target + 1) problems.Add("reference doesn't read back");
                for (int k = 0; k < d.Length; k++)
                    if ((k < at || k >= at + 4) && wd[k] != d[k]) { problems.Add($"byte {k} changed"); break; }
            }
            return problems;
        }
        var problems = Check(output);
        if (problems.Count > 0) { Console.WriteLine("  verify: FAIL"); problems.ForEach(x => Console.WriteLine($"    - {x}")); Console.WriteLine("  Nothing written."); return 1; }
        Console.WriteLine("  verify: PASS (only those 4 bytes changed; every other export byte-identical)");
        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(outPath, output);
            Console.WriteLine($"  dry run: wrote {outPath} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, output, Check) ? 0 : 1;
    }

    static string Describe(Package pkg, int r) => r switch
    {
        0 => "null",
        > 0 when r <= pkg.Exports.Length => $"export #{r} {pkg.PathOf(pkg.Exports[r - 1])} ({pkg.ClassOf(pkg.Exports[r - 1])})",
        < 0 when -r <= pkg.Imports.Length => $"import {pkg.Imports[-r - 1].ObjectName} ({pkg.Imports[-r - 1].ClassName})",
        _ => $"?{r}",
    };
}
