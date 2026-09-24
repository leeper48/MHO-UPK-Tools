using System.Text;

namespace UpkMeshScan;

/// <summary>
/// Writes one export's raw bytes (.bin) and an annotated text dump (.txt): export info, the
/// tagged-property block walked tag by tag, then hex of the native data that follows it.
/// For working out an object layout from real bytes before writing a parser for it.
/// </summary>
static class ExportDump
{
    const int HexHeadBytes = 16 * 1024;
    const int HexTailBytes = 1024;

    public static int Run(string upkPath, string exportName, string outDir)
    {
        Package pkg;
        try { pkg = Package.Open(upkPath); }
        catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException)
        {
            Console.WriteLine($"Could not read '{upkPath}': {ex.Message}");
            return 1;
        }

        var matches = pkg.Exports.Select((e, i) => (e, i))
            .Where(x => x.e.ObjectName.Equals(exportName, StringComparison.OrdinalIgnoreCase) || pkg.PathOf(x.e).Equals(exportName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            Console.WriteLine($"No export named '{exportName}' in {Path.GetFileName(upkPath)}.");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        foreach (var (e, index) in matches)
        {
            byte[] data = pkg.ReadExportBytes(e);
            string stem = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(upkPath)}__{e.ObjectName}{(matches.Count > 1 ? $"__{index}" : "")}");
            File.WriteAllBytes(stem + ".bin", data);
            File.WriteAllText(stem + ".txt", Describe(pkg, e, index, data, upkPath), new UTF8Encoding(true));
            Console.WriteLine($"Dumped {pkg.ClassOf(e)} {pkg.PathOf(e)} ({data.Length:N0} bytes)");
            Console.WriteLine($"  {stem}.bin");
            Console.WriteLine($"  {stem}.txt");
        }
        return 0;
    }

    static string Describe(Package pkg, ExportEntry e, int index, byte[] data, string upkPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Package     : {Path.GetFileName(upkPath)}  (v{pkg.FileVersion}/L{pkg.LicenseeVersion}, chunks: {pkg.ChunkSource})");
        sb.AppendLine($"Export      : #{index + 1}  {pkg.PathOf(e)}");
        sb.AppendLine($"Class       : {pkg.ClassOf(e)}");
        sb.AppendLine($"SerialSize  : {e.SerialSize:N0} (0x{e.SerialSize:X})");
        sb.AppendLine($"SerialOffset: 0x{e.SerialOffset:X} (uncompressed body)");
        sb.AppendLine();

        int nativeStart = WalkProperties(pkg, data, sb);

        sb.AppendLine();
        if (nativeStart < 0)
        {
            sb.AppendLine("Property walk did not reach \"None\" — native start unknown. Hex from byte 0:");
            nativeStart = 0;
        }
        else
        {
            sb.AppendLine($"Native data : starts at 0x{nativeStart:X}, {data.Length - nativeStart:N0} bytes");
        }

        sb.AppendLine();
        sb.AppendLine($"---- hex: first {Math.Min(HexHeadBytes, data.Length - nativeStart):N0} bytes of native data ----");
        Hex(sb, data, nativeStart, Math.Min(HexHeadBytes, data.Length - nativeStart));
        int tailStart = Math.Max(nativeStart + HexHeadBytes, data.Length - HexTailBytes);
        if (tailStart < data.Length)
        {
            sb.AppendLine();
            sb.AppendLine($"---- hex: last {data.Length - tailStart:N0} bytes ----");
            Hex(sb, data, tailStart, data.Length - tailStart);
        }
        return sb.ToString();
    }

    /// <summary>Walks the tagged-property block after the 4-byte NetIndex; returns the offset just past "None", or -1.</summary>
    static int WalkProperties(Package pkg, byte[] d, StringBuilder sb)
    {
        if (d.Length < 4) { sb.AppendLine("Too short for a NetIndex."); return -1; }
        sb.AppendLine($"NetIndex    : {BitConverter.ToInt32(d, 0)}");
        sb.AppendLine("Properties  :");
        int p = 4;
        try
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                int tagAt = p;
                string name = Name(pkg, d, ref p);
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"    0x{tagAt:X6}  None");
                    return p;
                }
                string type = Name(pkg, d, ref p);
                int size = I32(d, ref p), arrayIndex = I32(d, ref p);
                string extra = "";
                switch (type.ToLowerInvariant())
                {
                    case "structproperty": extra = Name(pkg, d, ref p); break;
                    case "byteproperty": extra = Name(pkg, d, ref p); break;
                    case "boolproperty": extra = d[p] != 0 ? "true" : "false"; p += 1; break;
                }
                if (size < 0 || p + size > d.Length) throw new PackageFormatException($"bad size {size}");
                string value = Summarize(pkg, type, d, p, size);
                sb.AppendLine($"    0x{tagAt:X6}  {name}{(arrayIndex > 0 ? $"[{arrayIndex}]" : "")} : {type}{(extra.Length > 0 ? $" <{extra}>" : "")}  size={size}  {value}");
                p += size;
            }
            sb.AppendLine("    (gave up: too many tags)");
        }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            sb.AppendLine($"    desync at 0x{p:X}: {ex.Message}");
        }
        return -1;
    }

    static string Summarize(Package pkg, string type, byte[] d, int p, int size)
    {
        switch (type.ToLowerInvariant())
        {
            case "intproperty" when size == 4: return $"= {BitConverter.ToInt32(d, p)}";
            case "floatproperty" when size == 4: return $"= {BitConverter.ToSingle(d, p)}";
            case "objectproperty" when size == 4: return $"= {ObjectRef(pkg, BitConverter.ToInt32(d, p))}";
            case "nameproperty" when size == 8: { int q = p; return $"= {Name(pkg, d, ref q)}"; }
            case "byteproperty" when size == 8: { int q = p; return $"= {Name(pkg, d, ref q)}"; }
            case "byteproperty" when size == 1: return $"= {d[p]}";
            case "arrayproperty" when size >= 4: return $"count={BitConverter.ToInt32(d, p)}  {HexInline(d, p + 4, Math.Min(size - 4, 32))}";
            default: return size == 0 ? "" : HexInline(d, p, Math.Min(size, 32));
        }
    }

    static string ObjectRef(Package pkg, int i) => i switch
    {
        0 => "null",
        > 0 when i <= pkg.Exports.Length => $"export {pkg.PathOf(pkg.Exports[i - 1])}",
        < 0 when -i <= pkg.Imports.Length => $"import {pkg.Imports[-i - 1].ObjectName} ({pkg.Imports[-i - 1].ClassName})",
        _ => $"?{i}",
    };

    static string Name(Package pkg, byte[] d, ref int p)
    {
        int idx = I32(d, ref p), num = I32(d, ref p);
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }

    static int I32(byte[] d, ref int p)
    {
        if (p + 4 > d.Length) throw new PackageFormatException("read past end");
        int x = BitConverter.ToInt32(d, p); p += 4; return x;
    }

    static string HexInline(byte[] d, int p, int n) =>
        string.Join(' ', Enumerable.Range(p, Math.Max(0, n)).Select(i => d[i].ToString("X2"))) + (n >= 32 ? " …" : "");

    static void Hex(StringBuilder sb, byte[] d, int start, int len)
    {
        for (int row = start; row < start + len; row += 16)
        {
            int n = Math.Min(16, start + len - row);
            sb.Append($"{row:X8}  ");
            for (int i = 0; i < 16; i++) sb.Append(i < n ? $"{d[row + i]:X2} " : "   ").Append(i == 7 ? " " : "");
            sb.Append(' ');
            for (int i = 0; i < n; i++) { byte b = d[row + i]; sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.'); }
            sb.AppendLine();
        }
    }
}
