using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A power's ground decal image replaced by the mod (manifest extension DecalImages): the package, the texture's
/// path in it, and the image (a PNG in the mod's folder, relative: Decals\&lt;package&gt;\&lt;texture&gt;.png).</summary>
sealed class DecalImageEntry
{
    public string Package { get; set; } = "";
    public string Texture { get; set; } = "";
    public string Image { get; set; } = "";
}

/// <summary>A power's ground decal colored by the mod (manifest extension DecalColors): the package, the decal part
/// (marvelgamecontent.default__&lt;class&gt;.&lt;part&gt;: its material is read from the package at Save, as Power Colors may have
/// copied it under its own group) and the color (#RRGGBB).</summary>
sealed class DecalColorEntry
{
    public string Package { get; set; } = "";
    public string Part { get; set; } = "";
    public string Color { get; set; } = "";
}

/// <summary>
/// Ground decals' images (Kurt, 2026-11-06: Luke Cage's "CAGE" mark from Cage Was Here, which a user couldn't change; "opening
/// in the image editor like GIMP with the update would be really powerful"). A power's PowerFXDecal names a decal material
/// (Cage Was Here: a MaterialInstanceTimeVarying, dmat_cage_impact_8sec_inst, whose parent DecalMaterial dmat_cage_impact
/// samples tex_cage_impact and tex_cage_impact_glow: 512×512 uncompressed, streamed from Textures.tfc). An image is read at its
/// largest size (from the .tfc too) and a new one is written into the package in place, every mip inline and NeverStream (MHO
/// Package Modifier's --replace-texture), DXT5 so its alpha stays.
/// </summary>
static class DecalImages
{
    /// <summary>An object's export in a package by its path, else one whose path ends with it (Power Colors copies a power's
    /// materials under a group of its own: powerlukecage_punchtheground_recolor_fx.vfx_lukecage_b.…); -1 when none.</summary>
    public static int Resolve(Package pkg, string path)
    {
        int i = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).EndsWith("." + path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The material a decal part shows in this package now (its decalmat), or null.</summary>
    public static string? PartMaterial(string packageFile, string packagePath, string part) =>
        PowerParts.List(packageFile, packagePath).FirstOrDefault(s => s.Kind == "Decal" && s.Part.Equals(part, StringComparison.OrdinalIgnoreCase))?.Original is { Length: > 0 } m ? m : null;

    /// <summary>The folder in the mod for a package's decal images.</summary>
    public static string Folder(string package) => Path.Combine("Decals", Path.GetFileNameWithoutExtension(package));

    /// <summary>The textures a decal material shows, by path: the material (an instance's parent chain followed to the base
    /// DecalMaterial / Material), each texture its compiled resource samples, then an instance's own texture parameters.</summary>
    public static List<string> Textures(string packagePath, string materialPath)
    {
        var pkg = Package.Open(packagePath);
        var list = new List<string>();
        int i = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(materialPath, StringComparison.OrdinalIgnoreCase));
        for (int depth = 0; depth < 8 && i >= 0; depth++)
        {
            var e = pkg.Exports[i];
            string cls = pkg.ClassOf(e);
            byte[] d = pkg.ReadExportBytes(e).ToArray();
            var tags = TagWalker.Walk(pkg, d, 4);
            // an instance's texture parameters (textureparametervalues: struct array of { parametername, parametervalue })
            if (cls.StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase))
                foreach (int r in TextureParameterRefs(pkg, d, tags))
                    Add(r);
            if (cls.Equals("DecalMaterial", StringComparison.OrdinalIgnoreCase) || cls.Equals("Material", StringComparison.OrdinalIgnoreCase))
            {
                try { foreach (int r in ExportCopy.MaterialNativeTextures(pkg, d, cls)) Add(r); }
                catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
                break;
            }
            // up to the parent
            if (tags?.FirstOrDefault(t => t.Name.Equals("parent", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } pt) break;
            int pr = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pt.ValueAt));
            i = pr > 0 ? pr - 1 : -1;
        }
        return list;

        void Add(int r)
        {
            if (r <= 0 || r > pkg.Exports.Length || !pkg.ClassOf(pkg.Exports[r - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) return;
            string p = pkg.PathOf(pkg.Exports[r - 1]);
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
    }

    /// <summary>The texture references in an instance's textureparametervalues (each struct's parametervalue).</summary>
    static IEnumerable<int> TextureParameterRefs(Package pkg, byte[] d, TagWalker? tags)
    {
        if (tags?.FirstOrDefault(t => t.Name.Equals("textureparametervalues", StringComparison.OrdinalIgnoreCase)) is not { } tp) yield break;
        int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(tp.ValueAt));
        int p = tp.ValueAt + 4;
        for (int k = 0; k < n && p < tp.End; k++)
        {
            var inner = TagWalker.Walk(pkg, d, p);
            if (inner == null) yield break;
            if (inner.FirstOrDefault(t => t.Name.Equals("parametervalue", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } v)
                yield return BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(v.ValueAt));
            p = inner.NoneAt + 8;
        }
    }

    /// <summary>A texture's largest image as a PNG (from the package, else the game's texture cache). Null when it worked, else why not.</summary>
    public static string? ExportPng(string packagePath, string texturePath, string png, string? cooked)
    {
        var pkg = Package.Open(packagePath);
        int i = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(texturePath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return $"no {texturePath} in {Path.GetFileName(packagePath)}";
        var mip = TextureExport.ReadBestMip(pkg, i, out string note, cooked);
        if (mip == null) return note;
        var bgra = TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out string why);
        if (bgra == null) return why.Length > 0 ? why : "the image can't be read";
        using var bmp = TextureDecode.ToBitmap(bgra, mip.Width, mip.Height);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
        string tmp = png + ".tmp";
        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
        File.Move(tmp, png, overwrite: true);
        return null;
    }

    /// <summary>The package with these images in place of those textures (DXT5, every mip, inline). Verified; throws when it
    /// can't be built.</summary>
    public static byte[] Apply(string packagePath, IReadOnlyList<(string Texture, string Png)> images, List<string> log)
    {
        var pkg = Package.Open(packagePath);
        var reps = new List<TextureImport.Replacement>();
        foreach (var (tex, png) in images)
        {
            if (!File.Exists(png)) throw new FileNotFoundException($"the image for {tex} is missing: {png}");
            // whole multiples of 4 (DXT blocks): an odd-sized edit is scaled to fit
            string use = png, tmp = "";
            using (var img = new System.Drawing.Bitmap(png))
            {
                int w = Math.Max(4, img.Width / 4 * 4), h = Math.Max(4, img.Height / 4 * 4);
                if (w != img.Width || h != img.Height)
                {
                    tmp = Path.Combine(Path.GetTempPath(), "mho_decal_" + Guid.NewGuid().ToString("N")[..8] + ".png");
                    using var fit = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(fit)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
                    fit.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                    use = tmp;
                    log.Add($"{Path.GetFileName(png)}: {img.Width}×{img.Height} scaled to {w}×{h} (whole blocks of 4)");
                }
            }
            try
            {
                var r = TextureEncode.FromImage(use, "dxt5", 85, 1f, noMips: false, refine: true);
                int ti = Resolve(pkg, tex);
            if (ti < 0) throw new InvalidDataException($"no {tex} in {Path.GetFileName(packagePath)}");
            reps.Add(new TextureImport.Replacement(pkg.PathOf(pkg.Exports[ti]), TextureImport.WriteDds(r), Path.GetFileName(png)));
            }
            finally { if (tmp.Length > 0) try { File.Delete(tmp); } catch (IOException) { } }
        }
        byte[]? output = TextureImport.ReplaceMany(pkg, reps, out var problems, out var verify);
        if (output == null) throw new InvalidDataException(string.Join("; ", problems));
        var bad = verify(output);
        if (bad.Count > 0) throw new InvalidDataException(string.Join("; ", bad));
        foreach (var (tex, png) in images) log.Add($"{Path.GetFileName(packagePath)}: {tex[(tex.LastIndexOf('.') + 1)..]} ← {Path.GetFileName(png)}");
        return output;
    }

    /// <summary>The game's own texture data back for these textures (copied from the game's package as they are there: the
    /// mod's copy keeps the game's names in their places, only adding after them, so the data's references still hold).</summary>
    public static byte[] RestoreStock(string packagePath, string stockPath, IReadOnlyList<string> textures, List<string> log)
    {
        var pkg = Package.Open(packagePath);
        var stock = Package.Open(stockPath);
        if (stock.Names.Length > pkg.Names.Length || Enumerable.Range(0, stock.Names.Length).Any(k => !stock.Names[k].Equals(pkg.Names[k], StringComparison.Ordinal)))
            throw new InvalidDataException($"{Path.GetFileName(packagePath)}: its names aren't the game's in the same places; the game's image can't be copied back");
        var replace = new Dictionary<int, Func<long, byte[]>>();
        foreach (string tex in textures)
        {
            int i = Resolve(pkg, tex);
            int j = Resolve(stock, tex);
            if (i < 0 || j < 0) { log.Add($"{tex}: not in both packages, left as it is"); continue; }
            byte[] data = stock.ReadExportBytes(stock.Exports[j]).ToArray();
            replace[i] = _ => data;
            log.Add($"{Path.GetFileName(packagePath)}: {tex[(tex.LastIndexOf('.') + 1)..]} back to the game's image");
        }
        if (replace.Count == 0) return File.ReadAllBytes(packagePath);
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        return output;
    }

    /// <summary>A decal's color now: its material instance's first color value (largest channel 1), or null when the material
    /// has no color of its own to change (a plain decal material).</summary>
    public static System.Numerics.Vector3? CurrentColor(string packagePath, string materialPath)
    {
        var pkg = Package.Open(packagePath);
        int i = Resolve(pkg, materialPath);
        if (i < 0 || !pkg.ClassOf(pkg.Exports[i]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase)) return null;
        var t = new Fx.FxTables(pkg);
        byte[] b = pkg.Body;
        foreach (var (_, at) in PowerRecolor.VectorParameters(b, t, i))
            return ColorMap.Tint(new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8)));
        foreach (var (outAt, _, _) in PowerRecolor.CurveColors(b, t, i))
            return ColorMap.Tint(new(BitConverter.ToSingle(b, outAt), BitConverter.ToSingle(b, outAt + 4), BitConverter.ToSingle(b, outAt + 8)));
        return null;
    }

    /// <summary>
    /// A decal's color (Kurt, 2026-11-06: "add the decal color too"): its material instance's color values and every point of
    /// their fade curves take this hue and saturation, each keeping its own brightness (Cage Was Here's orange glows at 10×
    /// and fades out over 8 seconds: so does the new color). The tangents follow (the new point plus tangent, less the point).
    /// </summary>
    public static byte[] ApplyColor(string packagePath, string materialPath, System.Numerics.Vector3 color, List<string> log)
    {
        var pkg = Package.Open(packagePath);
        int i = Resolve(pkg, materialPath);
        if (i < 0) throw new InvalidDataException($"no {materialPath} in {Path.GetFileName(packagePath)}");
        var t = new Fx.FxTables(pkg);
        byte[] b = pkg.Body.ToArray();
        var tint = ColorMap.Tint(color);
        System.Numerics.Vector3 Read(int at) => new(BitConverter.ToSingle(b, at), BitConverter.ToSingle(b, at + 4), BitConverter.ToSingle(b, at + 8));
        void Write(int at, System.Numerics.Vector3 v) { BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v.X); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 4), v.Y); BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at + 8), v.Z); }
        System.Numerics.Vector3 Recolor(System.Numerics.Vector3 v) => tint * MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        int n = 0;
        foreach (var (_, at) in PowerRecolor.VectorParameters(b, t, i)) { Write(at, Recolor(Read(at))); n++; }
        foreach (var (outAt, arrAt, leaveAt) in PowerRecolor.CurveColors(b, t, i))
        {
            var v = Read(outAt); var r = Recolor(v);
            foreach (int tanAt in new[] { arrAt, leaveAt })
                if (tanAt >= 0 && Read(tanAt) is var tan && tan != System.Numerics.Vector3.Zero) Write(tanAt, Recolor(v + tan) - r);
            Write(outAt, r); n++;
        }
        if (n == 0) throw new InvalidDataException($"{materialPath[(materialPath.LastIndexOf('.') + 1)..]} has no color of its own to change");
        var e0 = pkg.Exports[i];
        byte[] data = b.AsSpan(e0.SerialOffset, e0.SerialSize).ToArray();
        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>> { [i] = _ => data }, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, [i], [], written);
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems));
        log.Add($"{Path.GetFileName(packagePath)}: {materialPath[(materialPath.LastIndexOf('.') + 1)..]} colored {ColorMap.Hex(tint)} ({n} color value(s) and curve point(s))");
        return output;
    }
}
