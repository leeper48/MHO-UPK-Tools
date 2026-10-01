using MhoPackageModifier;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// Particle materials' textures for the power effects (ported from the MHO Hero Creator's HeroTextures / ParticleMaterials,
/// the particle part; same rules so both show the same). Cooked materials keep no expressions: the textures the shader
/// samples are in the native data after the properties, found by scanning for references to Texture2D objects; the shown
/// one shares the most words with the material's name, a sub-image sheet ("2x2", "suv") for an emitter with sub-images,
/// not a mask / noise / fade / normal map. A material instance takes its parent's blending and its own texture parameters
/// first. Texture2D: properties, a 16-byte empty source-art header, the mips (flags, count, size, offset, inline data,
/// width, height; 0x01 = in a .tfc, 0x20 = unused, 0x10 = LZO), then the cache GUID; a streamed mip's place is in
/// TextureFileCacheManifest.bin. DXT1 / DXT5 / A8R8G8B8 decoded to BGRA.
/// </summary>
sealed class FxTextures(IReadOnlyList<FxPkg> packages, string cooked)
{
    readonly Dictionary<string, Gui.ModelView.Map?> cache = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Notes = new();
    static Dictionary<string, List<TfcCache.Entry>>? byPath;
    static string? byPathFolder;

    /// <summary>A particle material's texture and blending (additive: glows).</summary>
    public (Gui.ModelView.Map? Tex, bool Additive) ParticleMaterial(FxPkg p, int materialRef, bool subImages)
    {
        var pick = ParticleTexture(p, materialRef, subImages, out bool additive);
        return (pick == null ? null : Texture(pick.Value.P, pick.Value.Export), additive);
    }

    public (FxPkg P, int Export)? ParticleTexture(FxPkg p, int materialRef, bool subImages, out bool additiveOut)
    {
        additiveOut = true;
        var at = Resolve(p, materialRef);
        if (at == null) return null;
        string matName = at.Value.P.T.Exports[at.Value.Export].ObjectName;
        // Distortion (heat haze) and radial remaps can't be shown from a texture alone: left out.
        string ml = matName.ToLowerInvariant();
        if (ml.Contains("distort") || ml.Contains("warp") || ml.Contains("refract") || ml.Contains("radial")) return null;
        var candidates = new List<(FxPkg P, int Export)>();
        bool? additive = null;
        for (int depth = 0; depth < 8 && at != null; depth++)
        {
            var (q, i) = at.Value;
            var e = q.T.Exports[i];
            var props = FxProps.Find(q.Bytes, q.T, e)?.Props ?? [];
            if (additive == null && props.FirstOrDefault(x => x.Name.Equals("BlendMode", StringComparison.OrdinalIgnoreCase))?.Value is { } bm)
                additive = bm.Contains("additive", StringComparison.OrdinalIgnoreCase);
            if (q.T.ClassOf(e).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase))
            {
                var tpv = props.FirstOrDefault(x => x.Name.Equals("TextureParameterValues", StringComparison.OrdinalIgnoreCase));
                if (tpv != null) foreach (var (_, tr) in Parameters(q, tpv.ValueAt, tpv.Size)) if (tr != 0 && Resolve(q, tr) is { } t) candidates.Add(t);
                var parent = props.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase));
                at = parent == null ? null : Resolve(q, BitConverter.ToInt32(q.Bytes, parent.ValueAt));
                continue;
            }
            if (props.Count > 0)
            {
                int from = End(props), end = e.SerialOffset + e.SerialSize;
                for (int o = from; o + 4 <= end; o++)
                {
                    int v = BitConverter.ToInt32(q.Bytes, o);
                    if (v == 0 || v == int.MinValue || v > q.T.Exports.Count || -v > q.T.Imports.Count) continue;
                    string cls = v > 0 ? q.T.ClassOf(q.T.Exports[v - 1]) : q.T.Imports[-v - 1].ClassName;
                    if (!cls.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Resolve(q, v) is { } t && !candidates.Contains(t)) candidates.Add(t);
                    o += 3;
                }
            }
            break;
        }
        additiveOut = additive ?? true;
        if (candidates.Count == 0) return null;
        static string[] Words(string n) => n.ToLowerInvariant().Split('_', '.', ' ').Where(w => w.Length > 1 && w is not ("mat" or "tex" or "mi" or "ca" or "01" or "02")).ToArray();
        var mw = Words(matName);
        int Score((FxPkg P, int Export) t)
        {
            string n = t.P.T.Exports[t.Export].ObjectName.ToLowerInvariant();
            int sc = Words(n).Count(w => mw.Contains(w)) * 4;
            if (subImages && (n.Contains("2x2") || n.Contains("4x4") || n.Contains("suv") || n.Contains("subuv"))) sc += 6;
            if (n.Contains("mask") || n.Contains("noise") || n.Contains("fade") || n.Contains("cloud_basic") || n.Contains("gradient") || n.Contains("distort")) sc -= 5;
            if (n.Contains("norm") || n.EndsWith("_n") || n.Contains("bump") || n.EndsWith("_spec") || n.Contains("_specular")) sc -= 12;
            return sc;
        }
        return candidates.OrderByDescending(Score).First();
    }

    /// <summary>Where an export's native data starts: after its last property and the closing None.</summary>
    public static int End(List<FxProps.Prop> props)
    {
        var last = props[^1];
        return last.ValueAt + (last.Type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : last.Size) + 8;
    }

    /// <summary>An import resolved to the export of that path in one of the known packages (or the same export).</summary>
    public (FxPkg P, int Export)? Resolve(FxPkg p, int reference)
    {
        if (reference > 0) return (p, reference - 1);
        if (reference == 0) return null;
        string path = p.T.PathOf(reference);
        foreach (var q in packages)
            if (q.Find(path) is int i && i >= 0) return (q, i);
        Notes.Add($"{path} is imported from a package the view doesn't read");
        return null;
    }

    /// <summary>TextureParameterValues: count, then per struct its tagged properties up to None.</summary>
    static List<(string Name, int Tex)> Parameters(FxPkg q, int at, int size)
    {
        var b = q.Bytes; var list = new List<(string, int)>();
        int end = at + size, count = BitConverter.ToInt32(b, at), p = at + 4;
        string N(int o) => q.T.Names[BitConverter.ToInt32(b, o)].Text;
        for (int k = 0; k < count && p < end; k++)
        {
            string param = ""; int tex = 0;
            while (p + 8 <= end)
            {
                string n = N(p);
                if (n.Equals("None", StringComparison.OrdinalIgnoreCase)) { p += 8; break; }
                string t = N(p + 8); int s = BitConverter.ToInt32(b, p + 16); p += 24;
                if (t.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase)) { p += 1; continue; }
                if (t.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) || t.Equals("ByteProperty", StringComparison.OrdinalIgnoreCase)) p += 8;
                if (n.Equals("ParameterName", StringComparison.OrdinalIgnoreCase) && s == 8) param = N(p);
                if (n.Equals("ParameterValue", StringComparison.OrdinalIgnoreCase) && s == 4) tex = BitConverter.ToInt32(b, p);
                p += s;
            }
            list.Add((param, tex));
        }
        return list;
    }

    /// <summary>A texture's largest available mip (inline, or streamed from its .tfc), decoded, with its smaller mips.</summary>
    public Gui.ModelView.Map? Texture(FxPkg p, int export)
    {
        string key = p.Name + "|" + p.T.PathOf(export + 1);
        if (cache.TryGetValue(key, out var hit)) return hit;
        var d = Decoded(p, export);
        return cache[key] = d == null ? null : new Gui.ModelView.Map(d.Value.Bgra, d.Value.W, d.Value.H);
    }

    /// <summary>The manifest entry: the one with the path and GUID, else the first with the path (the Hero Creator's rule).</summary>
    TfcCache.Entry? ManifestEntry(string path, byte[] guid)
    {
        if (byPath == null || byPathFolder != cooked)
        {
            byPath = TfcCache.All(cooked).GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            byPathFolder = cooked;
        }
        if (!byPath.TryGetValue(path, out var list)) return null;
        return list.FirstOrDefault(x => x.Guid.AsSpan().SequenceEqual(guid)) ?? list.FirstOrDefault();
    }

    public (byte[] Bgra, int W, int H)? Decoded(FxPkg p, int export)
    {
        string path = p.T.PathOf(export + 1);
        var e = p.T.Exports[export];
        if (!p.T.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) return null;
        var b = p.Bytes;
        var props = FxProps.Find(b, p.T, e)?.Props ?? throw new InvalidDataException("texture properties unreadable");
        string format = props.FirstOrDefault(x => x.Name.Equals("Format", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        format = format[(format.LastIndexOf('.') + 1)..].ToUpperInvariant().Replace("PF_", "");
        int q = End(props) + 16;
        int count = BitConverter.ToInt32(b, q); q += 4;
        if (count < 0 || count > 20) throw new InvalidDataException($"{path}: {count} mips");
        var mips = new List<(uint Flags, int Count, int Size, int InlineAt, int W, int H)>();
        for (int m = 0; m < count; m++)
        {
            uint flags = BitConverter.ToUInt32(b, q); int cnt = BitConverter.ToInt32(b, q + 4), size = BitConverter.ToInt32(b, q + 8);
            q += 16;
            int inlineAt = -1;
            if ((flags & 0x21) == 0 && size > 0) { inlineAt = q; q += size; }
            mips.Add((flags, cnt, size, inlineAt, BitConverter.ToInt32(b, q), BitConverter.ToInt32(b, q + 4)));
            q += 8;
        }
        byte[] guid = b.AsSpan(q, 16).ToArray();
        var entry = ManifestEntry(path, guid);
        for (int m = 0; m < mips.Count; m++)
        {
            var mip = mips[m];
            if ((mip.Flags & 0x20) != 0 || mip.W <= 0 || mip.H <= 0) continue;
            byte[]? data = null;
            if ((mip.Flags & 0x01) != 0)
            {
                if (entry == null || entry.Mips.FirstOrDefault(x => x.Mip == m).Size <= 0) continue;
                try { data = TfcCache.ReadMip(cooked, entry, m, mip.Count); }
                catch (FileNotFoundException) { Notes.Add(entry.Cache + ".tfc not found"); continue; }
            }
            else if (mip.InlineAt >= 0)
            {
                data = b.AsSpan(mip.InlineAt, mip.Size).ToArray();
                if ((mip.Flags & 0x10) != 0) data = TextureExport.DecompressChunk(data, mip.Count);
            }
            if (data == null) continue;
            var bgra = Decode(format, mip.W, mip.H, data);
            if (bgra == null) { Notes.Add($"{path}: format {format} not shown"); return null; }
            return (bgra, mip.W, mip.H);
        }
        Notes.Add($"{path}: no mip found (inline or in its .tfc)");
        return null;
    }

    /// <summary>DXT1 / DXT5 / A8R8G8B8 to BGRA (null for other formats).</summary>
    public static byte[]? Decode(string f, int w, int h, byte[] d)
    {
        var o = new byte[w * h * 4];
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        switch (f)
        {
            case "DXT1":
                if (d.Length < bw * bh * 8) return null;
                for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++) ColourBlock(d, (by * bw + bx) * 8, o, w, h, bx, by, true);
                return o;
            case "DXT5":
                if (d.Length < bw * bh * 16) return null;
                for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
                {
                    int at = (by * bw + bx) * 16;
                    ColourBlock(d, at + 8, o, w, h, bx, by, false);
                    var a = AlphaBlock(d, at);
                    for (int i = 0; i < 16; i++) Put(o, w, h, bx, by, i, 3, a[i]);
                }
                return o;
            case "A8R8G8B8":
                if (d.Length < w * h * 4) return null;
                Array.Copy(d, o, w * h * 4);
                return o;
            default: return null;
        }
    }

    static void Put(byte[] o, int w, int h, int bx, int by, int i, int ch, byte v)
    {
        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
        if (x < w && y < h) o[(y * w + x) * 4 + ch] = v;
    }

    static void ColourBlock(byte[] d, int at, byte[] o, int w, int h, int bx, int by, bool dxt1)
    {
        ushort c0 = BitConverter.ToUInt16(d, at), c1 = BitConverter.ToUInt16(d, at + 2);
        uint bits = BitConverter.ToUInt32(d, at + 4);
        var c = new (int R, int G, int B, int A)[4];
        (int, int, int, int) Rgb(ushort v) => (((v >> 11) & 31) * 255 / 31, ((v >> 5) & 63) * 255 / 63, (v & 31) * 255 / 31, 255);
        c[0] = Rgb(c0); c[1] = Rgb(c1);
        if (!dxt1 || c0 > c1)
        {
            c[2] = ((2 * c[0].R + c[1].R) / 3, (2 * c[0].G + c[1].G) / 3, (2 * c[0].B + c[1].B) / 3, 255);
            c[3] = ((c[0].R + 2 * c[1].R) / 3, (c[0].G + 2 * c[1].G) / 3, (c[0].B + 2 * c[1].B) / 3, 255);
        }
        else
        {
            c[2] = ((c[0].R + c[1].R) / 2, (c[0].G + c[1].G) / 2, (c[0].B + c[1].B) / 2, 255);
            c[3] = (0, 0, 0, 0);
        }
        for (int i = 0; i < 16; i++)
        {
            var k = c[(bits >> (2 * i)) & 3];
            Put(o, w, h, bx, by, i, 0, (byte)k.B); Put(o, w, h, bx, by, i, 1, (byte)k.G); Put(o, w, h, bx, by, i, 2, (byte)k.R); Put(o, w, h, bx, by, i, 3, (byte)k.A);
        }
    }

    static byte[] AlphaBlock(byte[] d, int at)
    {
        int a0 = d[at], a1 = d[at + 1];
        var v = new int[8];
        v[0] = a0; v[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i < 5; i++) v[i + 1] = ((5 - i) * a0 + i * a1) / 5; v[6] = 0; v[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)d[at + 2 + i] << (8 * i);
        var r = new byte[16];
        for (int i = 0; i < 16; i++) r[i] = (byte)v[(int)((bits >> (3 * i)) & 7)];
        return r;
    }
}
