using System.Text;

namespace UpkMeshScan;

/// <summary>One texture parameter of a material, resolved to a Texture2D export in the same package.</summary>
public sealed record MaterialTexture(string Parameter, string Texture, int ExportIndex);

/// <summary>
/// Texture export using only data inside the package (no .tfc). Writes the largest inline mip of a
/// Texture2D as a .dds, keeping the DXT blocks untouched. Stock game textures only carry small mips
/// inline (64x64 on 893 of 1,053 textures in SCS__OpDailyBugleRegionBand_SF); the full-size mips are
/// in .tfc files, which this deliberately doesn't read. Textures injected by mod tools carry one
/// full-size inline mip (seen in the modded UC__MarvelPlayer_WinterSoldier_SF.upk).
/// </summary>
static class TextureExport
{
    /// <summary>Texture parameters of a MaterialInstanceConstant, following Parent while it stays a MIC in this package.</summary>
    public static List<MaterialTexture> MaterialTextures(Package pkg, int materialRef, List<string> notes)
    {
        var result = new List<MaterialTexture>();
        var seenParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reference = materialRef;
        for (int depth = 0; depth < 8 && reference != 0; depth++)
        {
            if (reference < 0) { notes.Add($"material '{pkg.RefName(reference)}' is imported from another package; its textures aren't in this one"); break; }
            var e = pkg.Exports[reference - 1];
            string cls = pkg.ClassOf(e);
            if (!cls.Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) notes.Add($"material '{e.ObjectName}' is a {cls}; only MaterialInstanceConstant texture parameters are read");
                break;
            }
            var (parameters, parent) = ReadMic(pkg, e);
            foreach (var (param, texRef) in parameters)
            {
                if (!seenParams.Add(param) || texRef == 0) continue;     // a child's value overrides its parent's
                if (texRef < 0) { notes.Add($"{e.ObjectName}.{param} -> '{pkg.RefName(texRef)}' is imported; not in this package"); continue; }
                var te = pkg.Exports[texRef - 1];
                string texClass = pkg.ClassOf(te);
                if (!texClass.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{param} -> '{te.ObjectName}' is a {texClass}; only Texture2D is exported"); continue; }
                result.Add(new MaterialTexture(param, te.ObjectName, texRef - 1));
            }
            reference = parent;
        }
        return result;
    }

    static (List<(string Param, int TexRef)> Params, int Parent) ReadMic(Package pkg, ExportEntry e)
    {
        byte[] d = pkg.ReadExportBytes(e);
        var list = new List<(string, int)>();
        int parent = 0, p = 4;
        while (true)
        {
            string name = Name(pkg, d, ref p);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            string type = Name(pkg, d, ref p).ToLowerInvariant();
            int size = BitConverter.ToInt32(d, p); p += 8;
            if (type is "structproperty" or "byteproperty") Name(pkg, d, ref p);
            if (type == "boolproperty") p += 1;
            if (name.Equals("Parent", StringComparison.OrdinalIgnoreCase) && size == 4) parent = BitConverter.ToInt32(d, p);
            if (name.Equals("TextureParameterValues", StringComparison.OrdinalIgnoreCase))
            {
                int count = BitConverter.ToInt32(d, p), q = p + 4;
                for (int i = 0; i < count; i++)
                {
                    string param = ""; int tex = 0;
                    while (true)
                    {
                        string n = Name(pkg, d, ref q);
                        if (n.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
                        string t = Name(pkg, d, ref q).ToLowerInvariant();
                        int s = BitConverter.ToInt32(d, q); q += 8;
                        if (t is "structproperty" or "byteproperty") Name(pkg, d, ref q);
                        if (t == "boolproperty") q += 1;
                        if (n.Equals("ParameterName", StringComparison.OrdinalIgnoreCase) && s == 8) { int r = q; param = Name(pkg, d, ref r); }
                        if (n.Equals("ParameterValue", StringComparison.OrdinalIgnoreCase) && s == 4) tex = BitConverter.ToInt32(d, q);
                        q += s;
                    }
                    list.Add((param, tex));
                }
            }
            p += size;
        }
        return (list, parent);
    }

    /// <summary>Writes the largest inline mip as a DDS. Returns (width, height, note) or null with a reason in note.</summary>
    public static (int W, int H)? WriteDds(Package pkg, int exportIndex, string path, out string note)
    {
        TextureInfo tex;
        try { tex = TextureInfo.Read(pkg, pkg.Exports[exportIndex]); }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        { note = $"couldn't read texture header ({ex.Message})"; return null; }
        var mip = tex.BestInline;
        if (mip is null) { note = "no mip data inside the package"; return null; }
        byte[] pixels = tex.Data.AsSpan(mip.InlineAt, mip.Size).ToArray();
        if (mip.Lzo) pixels = DecompressChunk(pixels, mip.Count);
        byte[]? header = DdsHeader(tex.Format, mip.Width, mip.Height, pixels.Length, out note);
        if (header is null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var f = File.Create(path)) { f.Write(header); f.Write(pixels); }
        note = mip.Width >= tex.SizeX ? "full size" : $"full size {tex.SizeX}x{tex.SizeY} is only in '{tex.Cache}.tfc'";
        return (mip.Width, mip.Height);
    }

    /// <summary>UE3 compressed-chunk format: magic, block size, summary (comp, uncomp), block sizes, blocks.</summary>
    static byte[] DecompressChunk(byte[] src, int expected)
    {
        int p = 4, blockSize = BitConverter.ToInt32(src, p); p += 4;
        p += 4; int total = BitConverter.ToInt32(src, p); p += 4;
        if (blockSize <= 0) blockSize = 0x20000;
        int blocks = (total + blockSize - 1) / blockSize;
        var sizes = new (int C, int U)[blocks];
        for (int i = 0; i < blocks; i++) { sizes[i] = (BitConverter.ToInt32(src, p), BitConverter.ToInt32(src, p + 4)); p += 8; }
        var dst = new byte[total];
        int o = 0;
        foreach (var (c, u) in sizes) { Lzo1x.Decompress(src, p, c, dst, o, u); p += c; o += u; }
        if (total != expected) throw new InvalidDataException($"LZO mip is {total} bytes, expected {expected}");
        return dst;
    }

    static byte[]? DdsHeader(string format, int w, int h, int dataLength, out string note)
    {
        note = "";
        const int DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PITCH = 0x8, DDSD_PIXELFORMAT = 0x1000, DDSD_LINEARSIZE = 0x80000;
        const int DDPF_ALPHAPIXELS = 0x1, DDPF_FOURCC = 0x4, DDPF_RGB = 0x40, DDPF_LUMINANCE = 0x20000;
        string f = format.ToUpperInvariant();
        string? fourCC = f switch { "PF_DXT1" => "DXT1", "PF_DXT3" => "DXT3", "PF_DXT5" => "DXT5", "PF_BC5" => "ATI2", _ => null };
        int expected = fourCC switch
        {
            "DXT1" => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8,
            null => f switch { "PF_A8R8G8B8" => w * h * 4, "PF_G8" => w * h, _ => -1 },
            _ => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 16,
        };
        if (expected < 0) { note = $"format {format} not supported yet"; return null; }
        if (expected != dataLength) { note = $"{format} {w}x{h} should be {expected} bytes, found {dataLength}"; return null; }

        using var ms = new MemoryStream();
        using var b = new BinaryWriter(ms);
        b.Write(Encoding.ASCII.GetBytes("DDS "));
        b.Write(124);
        b.Write(DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | (fourCC != null ? DDSD_LINEARSIZE : DDSD_PITCH));
        b.Write(h); b.Write(w);
        b.Write(fourCC != null ? dataLength : (f == "PF_G8" ? w : w * 4));
        b.Write(0); b.Write(1);                                  // depth, mip count
        for (int i = 0; i < 11; i++) b.Write(0);
        b.Write(32);                                             // pixel format size
        if (fourCC != null) { b.Write(DDPF_FOURCC); b.Write(Encoding.ASCII.GetBytes(fourCC)); b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(0); }
        else if (f == "PF_G8") { b.Write(DDPF_LUMINANCE); b.Write(0); b.Write(8); b.Write(0xFF); b.Write(0); b.Write(0); b.Write(0); }
        else { b.Write(DDPF_RGB | DDPF_ALPHAPIXELS); b.Write(0); b.Write(32); b.Write(0x00FF0000); b.Write(0x0000FF00); b.Write(0x000000FF); b.Write(unchecked((int)0xFF000000)); }
        b.Write(0x1000);                                         // DDSCAPS_TEXTURE
        b.Write(0); b.Write(0); b.Write(0); b.Write(0);
        b.Flush();
        return ms.ToArray();
    }

    /// <summary>--export-textures: every Texture2D in a package (optionally filtered) to .dds.</summary>
    public static int Run(string upkPath, string? filter, string outDir)
    {
        var pkg = Package.Open(upkPath);
        int written = 0, skipped = 0;
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!pkg.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
            if (filter != null && !e.ObjectName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            string path = Path.Combine(outDir, SafeName(e.ObjectName) + ".dds");
            try
            {
                var size = WriteDds(pkg, i, path, out string note);
                if (size is { } s) { written++; Console.WriteLine($"  {e.ObjectName,-48} {s.W}x{s.H}  {note}"); }
                else { skipped++; Console.WriteLine($"  {e.ObjectName,-48} skipped: {note}"); }
            }
            catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException)
            { skipped++; Console.WriteLine($"  {e.ObjectName,-48} skipped: {ex.Message}"); }
        }
        Console.WriteLine($"{written} texture(s) written to {outDir}, {skipped} skipped.");
        return 0;
    }

    public static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static string Name(Package pkg, byte[] d, ref int p)
    {
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }
}
