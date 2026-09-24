namespace UpkMeshScan;

/// <summary>Texture2D header and mip table: size, format, cache name, and where each mip's data lives. Read-only.</summary>
public sealed record TextureMip(int Width, int Height, uint Flags, int Count, int Size, int Offset, int InlineAt)
{
    public bool InSeparateFile => (Flags & 0x01) != 0;
    public bool Unused => (Flags & 0x20) != 0;
    public bool Lzo => (Flags & 0x10) != 0;
    public bool Inline => !InSeparateFile && !Unused && Size > 0 && InlineAt >= 0;
}

public sealed record TextureInfo(string Name, string Format, int SizeX, int SizeY, string Cache, List<TextureMip> Mips, byte[] Data)
{
    /// <summary>Largest mip whose bytes are inside the package (null if none).</summary>
    public TextureMip? BestInline => Mips.Where(m => m.Inline).OrderByDescending(m => m.Width).FirstOrDefault();

    public static TextureInfo Read(Package pkg, ExportEntry e)
    {
        byte[] d = pkg.ReadExportBytes(e);
        int p = 4;
        string format = "?", cache = ""; int sx = 0, sy = 0;
        while (true)
        {
            string name = ReadName(pkg, d, ref p);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) break;
            string type = ReadName(pkg, d, ref p).ToLowerInvariant();
            int size = BitConverter.ToInt32(d, p); p += 8;
            if (type is "structproperty" or "byteproperty") ReadName(pkg, d, ref p);
            if (type == "boolproperty") p += 1;
            switch (name.ToLowerInvariant())
            {
                case "sizex": sx = BitConverter.ToInt32(d, p); break;
                case "sizey": sy = BitConverter.ToInt32(d, p); break;
                case "format" when size == 8: { int q = p; format = ReadName(pkg, d, ref q); break; }
                case "texturefilecachename" when size == 8: { int q = p; cache = ReadName(pkg, d, ref q); break; }
            }
            p += size;
        }
        p += 16;                                    // source-art bulk data header (always empty when cooked)
        int count = BitConverter.ToInt32(d, p); p += 4;
        if (count < 0 || count > 20) throw new PackageFormatException($"texture '{e.ObjectName}': implausible mip count {count}");
        var mips = new List<TextureMip>();
        for (int m = 0; m < count; m++)
        {
            uint flags = BitConverter.ToUInt32(d, p); int cnt = BitConverter.ToInt32(d, p + 4), size = BitConverter.ToInt32(d, p + 8), off = BitConverter.ToInt32(d, p + 12);
            p += 16;
            int inlineAt = -1;
            if ((flags & 0x21) == 0 && size > 0) { inlineAt = p; p += size; }
            int w = BitConverter.ToInt32(d, p), h = BitConverter.ToInt32(d, p + 4); p += 8;
            mips.Add(new TextureMip(w, h, flags, cnt, size, off, inlineAt));
        }
        return new TextureInfo(e.ObjectName, format, sx, sy, cache, mips, d);
    }

    static string ReadName(Package pkg, byte[] d, ref int p)
    {
        int idx = BitConverter.ToInt32(d, p), num = BitConverter.ToInt32(d, p + 4); p += 8;
        if ((uint)idx >= (uint)pkg.Names.Length) throw new PackageFormatException($"name index {idx} out of range");
        return num > 0 ? $"{pkg.Names[idx]}_{num - 1}" : pkg.Names[idx];
    }

    public override string ToString()
    {
        var best = BestInline;
        return $"{Name}: {SizeX}x{SizeY} {Format} cache='{Cache}' mips={Mips.Count} " +
               $"best inline={(best is null ? "none" : $"{best.Width}x{best.Height}{(best.Lzo ? " (LZO)" : "")}")}  " +
               string.Join(" ", Mips.Select(m => $"{m.Width}:{(m.Unused ? "unused" : m.InSeparateFile ? "tfc" : m.Inline ? "in" : "-")}"));
    }
}
