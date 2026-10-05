using System.Text;
using MhoPackageModifier;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// A package as the power-effect code reads it (Kurt, 2026-09-30: power effects in the 3D preview, ported from the MHO
/// Hero Creator, which stays separate: no reference to it). The whole uncompressed body with absolute offsets (MPM's
/// Package.Body), and tables shaped like the Hero Creator's (exports with outer / class / serial range, imports, names,
/// paths with the import chain), so its property and particle readers carry over unchanged.
/// </summary>
sealed class FxTables
{
    public sealed record Export(string ObjectName, int Class, int Outer, int SerialOffset, int SerialSize, int EntryStart);
    public sealed record Import(string ClassName, int Outer, string ObjectName);
    public sealed record Name(string Text);

    public readonly List<Export> Exports = [];
    public readonly List<Import> Imports = [];
    public readonly List<Name> Names = [];

    public FxTables(Package p)
    {
        foreach (string n in p.Names) Names.Add(new Name(n));
        foreach (var im in p.Imports) Imports.Add(new Import(im.ClassName, im.OuterIndex, im.ObjectName));
        for (int i = 0; i < p.Exports.Length; i++)
        {
            var e = p.Exports[i];
            Exports.Add(new Export(e.ObjectName, e.ClassIndex, e.OuterIndex, e.SerialOffset, e.SerialSize, p.ExportEntryStart[i]));
        }
    }

    public string ClassOf(Export e) => e.Class < 0 ? Imports[-e.Class - 1].ObjectName : e.Class > 0 ? Exports[e.Class - 1].ObjectName : "Class";

    /// <summary>1-based export (positive) or import (negative): its path through its outers (imports too).</summary>
    public string PathOf(int index)
    {
        if (index == 0) return "";
        if (index > Exports.Count || -index > Imports.Count) return $"#{index}";   // (a reference past the tables: shown, not followed)
        var (name, outer) = index > 0 ? (Exports[index - 1].ObjectName, Exports[index - 1].Outer) : (Imports[-index - 1].ObjectName, Imports[-index - 1].Outer);
        string o = PathOf(outer);
        return o.Length > 0 ? o + "." + name : name;
    }
}

/// <summary>An opened package: file name, whole body, tables (kept per file and write time).</summary>
sealed record FxPkg(string Name, byte[] Bytes, FxTables T)
{
    Dictionary<string, int>? byPath;
    /// <summary>An export by its full path (0-based), or -1.</summary>
    public int Find(string path)
    {
        if (byPath == null)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < T.Exports.Count; i++) d.TryAdd(T.PathOf(i + 1), i);
            byPath = d;
        }
        return byPath.TryGetValue(path, out int k) ? k : -1;
    }

    static readonly Dictionary<string, FxPkg> cache = new(StringComparer.OrdinalIgnoreCase);

    public static FxPkg Open(string file)
    {
        string key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks;
        lock (cache)
        {
            if (cache.TryGetValue(key, out var hit)) return hit;
            if (cache.Count > 64) cache.Clear();
            var p = Package.Open(file);
            return cache[key] = new FxPkg(Path.GetFileName(file), p.Body, new FxTables(p));
        }
    }
}

/// <summary>
/// An export's UE3 tagged properties (read only; the MHO Hero Creator's reader). A property: FName name, FName type, i32
/// size, i32 array index, then (by type) BoolProperty 1 value byte, ByteProperty an enum FName then the value,
/// StructProperty a struct FName then the data; "None" ends the list. Components carry a header before their properties
/// whose length varies, so <see cref="Find"/> tries the start offsets.
/// </summary>
static class FxProps
{
    /// <summary>A property; <paramref name="TagAt"/> = where its tag starts, <paramref name="ValueAt"/> = its value (absolute
    /// offsets in the package), <paramref name="SizeAt"/> = its i32 size field.</summary>
    public sealed record Prop(string Name, string Type, int Size, int Index, string Value, int TagAt = 0, int SizeAt = 0, int ValueAt = 0);

    public static (int Start, List<Prop> Props)? Find(byte[] pkg, FxTables t, FxTables.Export e)
    {
        for (int start = 0; start <= 40 && start < e.SerialSize; start += 4)
        {
            var r = TryRead(pkg, t, e.SerialOffset + start, e.SerialOffset + e.SerialSize);
            if (r != null && r.Count > 0) return (start, r);
        }
        return null;
    }

    /// <summary>Tagged properties from <paramref name="p"/> to the closing None (null if they don't read); also for a struct's value.</summary>
    public static List<Prop>? TryRead(byte[] b, FxTables t, int p, int end)
    {
        var props = new List<Prop>();
        string? Name(ref int q)
        {
            if (q + 8 > end) return null;
            int i = BitConverter.ToInt32(b, q), n = BitConverter.ToInt32(b, q + 4); q += 8;
            if (i < 0 || i >= t.Names.Count || n < 0 || n > 10000) return null;
            return t.Names[i].Text + (n > 0 ? "_" + (n - 1) : "");
        }
        for (int guard = 0; guard < 500; guard++)
        {
            int tagAt = p;
            string? name = Name(ref p);
            if (name == null) return null;
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) return props;
            string? type = Name(ref p);
            if (type == null || !type.EndsWith("Property", StringComparison.OrdinalIgnoreCase)) return null;
            if (p + 8 > end) return null;
            int sizeAt = p;
            int size = BitConverter.ToInt32(b, p), idx = BitConverter.ToInt32(b, p + 4); p += 8;
            int valueAt = p;
            if (size < 0 || size > end - p + 64) return null;
            string value;
            switch (type.ToLowerInvariant())
            {
                case "boolproperty": value = b[p] != 0 ? "true" : "false"; p += 1; break;
                case "byteproperty":
                {
                    string? en = Name(ref p); if (en == null) return null;
                    if (size == 8) { int q = p; value = $"{en}.{Name(ref q)}"; } else value = size == 1 ? b[p].ToString() : "?";
                    p += size; break;
                }
                case "structproperty":
                {
                    string? sn = Name(ref p); if (sn == null) return null;
                    value = $"<{sn}> {size} bytes"; p += size; break;
                }
                case "nameproperty": { int q = p; value = Name(ref q) ?? "?"; p += size; break; }
                case "intproperty": value = BitConverter.ToInt32(b, p).ToString(); p += size; break;
                case "floatproperty": value = BitConverter.ToSingle(b, p).ToString("R"); p += size; break;
                case "objectproperty": case "componentproperty": case "classproperty":
                {
                    int o = BitConverter.ToInt32(b, p); value = o == 0 ? "none" : $"{(o > 0 ? "export" : "import")} {t.PathOf(o)}"; p += size; break;
                }
                case "strproperty":
                {
                    int n = BitConverter.ToInt32(b, p);
                    value = n > 0 ? "\"" + Encoding.Latin1.GetString(b, p + 4, Math.Max(0, n - 1)) + "\"" : n < 0 ? "\"" + Encoding.Unicode.GetString(b, p + 4, (-n - 1) * 2) + "\"" : "\"\"";
                    p += size; break;
                }
                case "arrayproperty":
                {
                    // An array of object references shows its objects (count, then i32 each); anything else its size.
                    int n = size >= 4 ? BitConverter.ToInt32(b, p) : -1;
                    bool Valid(int o) => o == 0 || (o > 0 ? o <= t.Exports.Count : -o <= t.Imports.Count);
                    if (n >= 0 && size == 4 + 4 * n && n <= 64 && Enumerable.Range(0, n).All(k => Valid(BitConverter.ToInt32(b, p + 4 + 4 * k))))
                        value = $"{n} × [" + string.Join(", ", Enumerable.Range(0, n).Select(k => { int o = BitConverter.ToInt32(b, p + 4 + 4 * k); return o == 0 ? "none" : $"{(o > 0 ? "export" : "import")} {t.PathOf(o)}"; })) + "]";
                    else value = $"{size} bytes";
                    p += size; break;
                }
                default: value = $"{size} bytes"; p += size; break;
            }
            if (p > end) return null;
            if (type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase)) valueAt = p - 1;
            else if (type.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase) || type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) valueAt = p - size;
            props.Add(new Prop(name, type, size, idx, value, tagAt, sizeAt, valueAt));
        }
        return null;
    }
}
