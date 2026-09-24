namespace UpkMeshScan;

/// <summary>--material-params: every parameter expression of a Material (name, kind, default). Read-only.</summary>
static class MaterialParams
{
    public static int Run(string upkPath, string material)
    {
        var pkg = Package.Open(upkPath);
        int m = Enumerable.Range(0, pkg.Exports.Length).FirstOrDefault(i =>
            pkg.ClassOf(pkg.Exports[i]).Equals("Material", StringComparison.OrdinalIgnoreCase)
            && (pkg.Exports[i].ObjectName.Equals(material, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(pkg.Exports[i]).Equals(material, StringComparison.OrdinalIgnoreCase)), -1);
        if (m < 0) { Console.WriteLine($"No Material '{material}'."); return 1; }
        Console.WriteLine($"{pkg.PathOf(pkg.Exports[m])}: material settings");
        byte[] md = pkg.ReadExportBytes(pkg.Exports[m]);
        foreach (var t in TagWalker.Walk(pkg, md, 4) ?? new TagWalker())
            if (!t.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) || t.Size <= 16)
                Console.WriteLine($"    {t.Name} : {t.Type}{Value(pkg, md, t)}");
        var kinds = new SortedDictionary<string, int>();
        var rows = new List<string>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            string cls = pkg.ClassOf(e);
            if (e.OuterIndex != m + 1 || !cls.StartsWith("MaterialExpression", StringComparison.OrdinalIgnoreCase)) continue;
            string kind = cls["MaterialExpression".Length..];
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            byte[] d = pkg.ReadExportBytes(e);
            var tags = TagWalker.Walk(pkg, d, 4);
            var pn = tags?.FirstOrDefault(t => t.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase));
            if (pn == null) continue;
            var def = tags!.FirstOrDefault(t => t.Name.StartsWith("Default", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("Texture", StringComparison.OrdinalIgnoreCase));
            rows.Add($"    {kind,-28} {TagWalker.NameAt(pkg, d, pn.ValueAt),-36} {(def == null ? "(default not stored)" : def.Name + Value(pkg, d, def))}");
        }
        Console.WriteLine("  parameters:");
        rows.Order().ToList().ForEach(Console.WriteLine);
        Console.WriteLine("  expression kinds: " + string.Join(", ", kinds.Select(k => $"{k.Key} x{k.Value}")));
        return 0;
    }

    static string Value(Package pkg, byte[] d, TagWalker.Tag t) => t.Type.ToLowerInvariant() switch
    {
        "floatproperty" => $" = {BitConverter.ToSingle(d, t.ValueAt)}",
        "intproperty" => $" = {BitConverter.ToInt32(d, t.ValueAt)}",
        "objectproperty" => $" = {pkg.RefName(BitConverter.ToInt32(d, t.ValueAt))}",
        "boolproperty" => $" = {d[t.ValueAt - 1] != 0}",
        "byteproperty" when t.Size == 8 => $" = {TagWalker.NameAt(pkg, d, t.ValueAt)}",
        "nameproperty" => $" = {TagWalker.NameAt(pkg, d, t.ValueAt)}",
        "structproperty" when t.Size == 16 => $" = ({BitConverter.ToSingle(d, t.ValueAt):0.###}, {BitConverter.ToSingle(d, t.ValueAt + 4):0.###}, {BitConverter.ToSingle(d, t.ValueAt + 8):0.###}, {BitConverter.ToSingle(d, t.ValueAt + 12):0.###})",
        _ => "",
    };
}
