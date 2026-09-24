using System.Buffers.Binary;
using System.Globalization;

namespace UpkMeshScan;

/// <summary>
/// --set-property: change the value of a float or int property that already exists in an export's
/// tagged-property block. Same size, so only the value bytes change; the export is still re-appended
/// through PackageWriter and written with the .bak / verify / swap workflow. Properties at their default
/// value are omitted by UE3 and can't be set this way (adding a tag isn't supported yet).
/// </summary>
static class PropertyEdit
{
    public static int Run(string upkPath, string exportPath, IReadOnlyList<(string Name, string Value)> changes, bool dryRun)
    {
        upkPath = Path.GetFullPath(upkPath);
        if (Program.IsBackupName(upkPath)) { Console.WriteLine("Refusing to write a .bak/copy file."); return 2; }
        var pkg = Package.Open(upkPath);
        var matches = Enumerable.Range(0, pkg.Exports.Length)
            .Where(i => pkg.PathOf(pkg.Exports[i]).Equals(exportPath, StringComparison.OrdinalIgnoreCase) || pkg.Exports[i].ObjectName.Equals(exportPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1) { Console.WriteLine(matches.Count == 0 ? $"No export '{exportPath}'." : $"'{exportPath}' matches {matches.Count} exports; use the full path."); return 1; }
        int index = matches[0];
        var entry = pkg.Exports[index];
        byte[] original = pkg.ReadExportBytes(entry);
        byte[] edited = (byte[])original.Clone();

        var props = Locate(pkg, original);
        if (props is null) { Console.WriteLine("Couldn't walk the export's properties; nothing changed."); return 1; }
        Console.WriteLine($"{Path.GetFileName(upkPath)} :: {pkg.PathOf(entry)} ({pkg.ClassOf(entry)}){(dryRun ? "  [dry run]" : "")}");

        foreach (var (name, value) in changes)
        {
            if (!props.TryGetValue(name, out var prop))
            {
                Console.WriteLine($"  '{name}' isn't in this export (it may be at its default value, which UE3 doesn't store). Present: {string.Join(", ", props.Keys)}");
                return 1;
            }
            switch (prop.Type)
            {
                case "floatproperty" when prop.Size == 4:
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { Console.WriteLine($"  '{value}' isn't a number."); return 1; }
                    Console.WriteLine($"  {name}: {BinaryPrimitives.ReadSingleLittleEndian(edited.AsSpan(prop.ValueAt))} -> {f}");
                    BinaryPrimitives.WriteSingleLittleEndian(edited.AsSpan(prop.ValueAt), f);
                    break;
                case "intproperty" when prop.Size == 4:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { Console.WriteLine($"  '{value}' isn't an integer."); return 1; }
                    Console.WriteLine($"  {name}: {BinaryPrimitives.ReadInt32LittleEndian(edited.AsSpan(prop.ValueAt))} -> {n}");
                    BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(prop.ValueAt), n);
                    break;
                default:
                    Console.WriteLine($"  '{name}' is a {prop.Type} (size {prop.Size}); only float and int are supported so far."); return 1;
            }
        }

        byte[] packageBytes = PackageWriter.ReplaceExport(pkg, index, _ => edited);
        var written = Package.FromBytes(packageBytes);
        var problems = PackageWriter.Verify(pkg, written, index, edited);
        if (Locate(written, written.ReadExportBytes(written.Exports[index])) is null) problems.Add("re-read export's properties don't parse");
        int changedBytes = original.Zip(edited).Count(p => p.First != p.Second);
        if (problems.Count > 0) { Console.WriteLine($"  verify: FAIL ({string.Join("; ", problems)}). Nothing written."); return 1; }
        Console.WriteLine($"  verify: PASS (re-read package: tables identical, all other exports byte-identical; {changedBytes} byte(s) of this export changed)");

        if (dryRun)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "import_out");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, Path.GetFileName(upkPath));
            File.WriteAllBytes(target, packageBytes);
            Console.WriteLine($"  dry run: wrote {target} (game folder untouched)");
            return 0;
        }
        return MeshImport.WriteLive(upkPath, pkg, index, edited, packageBytes) ? 0 : 1;
    }

    sealed record Prop(string Type, int Size, int ValueAt);

    /// <summary>Top-level properties of an export with a display value; Editable = float/int (what --set-property can change).</summary>
    public sealed record PropertyView(string Name, string Type, int Size, string Value, bool Editable);

    public static List<PropertyView>? ReadProperties(Package pkg, int index)
    {
        byte[] d = pkg.ReadExportBytes(pkg.Exports[index]);
        var props = Locate(pkg, d);
        if (props is null) return null;
        var list = new List<PropertyView>();
        foreach (var (name, p) in props)
        {
            string value = p.Type switch
            {
                "floatproperty" when p.Size == 4 => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p.ValueAt)).ToString(CultureInfo.InvariantCulture),
                "intproperty" when p.Size == 4 => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p.ValueAt)).ToString(CultureInfo.InvariantCulture),
                "objectproperty" when p.Size == 4 => pkg.RefName(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p.ValueAt))),
                "structproperty" when p.Size == 4 => $"B{d[p.ValueAt]} G{d[p.ValueAt + 1]} R{d[p.ValueAt + 2]} A{d[p.ValueAt + 3]}",
                "boolproperty" => d[p.ValueAt - 1] != 0 ? "true" : "false",
                _ => $"({p.Size} bytes)",
            };
            bool editable = p.Size == 4 && p.Type is "floatproperty" or "intproperty";
            list.Add(new PropertyView(name, p.Type.Replace("property", ""), p.Size, value, editable));
        }
        return list;
    }

    /// <summary>Top-level properties by name, trying the plain (byte 4) and MHO component (byte 8) layouts.</summary>
    static Dictionary<string, Prop>? Locate(Package pkg, byte[] d)
    {
        foreach (int start in new[] { 4, 8 })
        {
            var result = new Dictionary<string, Prop>(StringComparer.OrdinalIgnoreCase);
            int p = start;
            try
            {
                for (int guard = 0; guard < 4096; guard++)
                {
                    string name = Name(pkg, d, ref p);
                    if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return result;
                    string type = Name(pkg, d, ref p).ToLowerInvariant();
                    int size = BitConverter.ToInt32(d, p), arrayIndex = BitConverter.ToInt32(d, p + 4); p += 8;
                    if (type is "structproperty" or "byteproperty") Name(pkg, d, ref p);
                    if (type == "boolproperty") p += 1;
                    if (size < 0 || p + size > d.Length) break;
                    if (arrayIndex == 0) result.TryAdd(name, new Prop(type, size, p));
                    p += size;
                }
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { }
        }
        return null;
    }

    static string Name(Package pkg, byte[] d, ref int p)
    {
        if (p + 8 > d.Length) throw new PackageFormatException("past end");
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}
