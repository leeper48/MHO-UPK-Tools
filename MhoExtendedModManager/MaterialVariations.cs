using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MhoExtendedModManager.Model;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Material variations (Kurt, 2026-10-26: Squirrel Girl's squirrels come out brown, gray or red at random): a character class's
/// default lists materials in <c>materialvariations</c> (an array of object references), and the game puts one of them, picked
/// at random, on the model's first material slot each time the character spawns (every stock user's model has one material:
/// squirrels, Magik's small demons, the raptor (two sections, one material), Venom's symbiotes, thugs, punks, MGH gang members,
/// food carts). A class without a list of its own uses its parent class's (the class a class export extends, its SuperStruct).
///
/// The Model tab's Variations tab shows every class that uses the target's model (the package's own class, and the summon
/// classes that name it: Pet Squirrel Modern and Triple Squirrel use the pet squirrel's model), each with its list. Unticking
/// writes the class's own list without that material (its parent's stays as it is); adding makes a new material in the model's
/// package (a renamed copy of the model's first material with a new color map: an image, or the current one with its hue
/// turned) and puts it in the list, the model's own first material first, so the original look stays one of the choices.
/// </summary>
static class MaterialVariations
{
    /// <summary>One class's list: the class, the class it extends, and the materials (their paths; an import is a material
    /// in another package, as squirrel_mat in the squirrel's own package).</summary>
    public sealed record Info(string Package, string Class, string? Super, List<string> Materials, int DefaultExport);

    /// <summary>A class that uses a model: its package, and the list it has (its own, or <see cref="From"/> its parent's).</summary>
    /// <param name="Materials">The list as the mod has it now (its own, or its parent's: <paramref name="From"/>).</param>
    /// <param name="Stock">The list in the game's own packages (own or inherited): what "back to the game's" means.</param>
    public sealed record ClassList(string Class, string File, string Label, List<string> Materials, string? From, List<string> Stock)
    {
        public bool Inherited => From != null;
    }

    /// <summary>The class default in <paramref name="packagePath"/> and its materialvariations (null: none of its own), or null
    /// when the package has no class default.</summary>
    public static (string Class, string? Super, List<string>? Materials, int DefaultExport)? ReadClass(string packagePath)
    {
        try
        {
            var pkg = Package.Open(packagePath);
            var t = new Fx.FxTables(pkg);
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                var e = pkg.Exports[i];
                if (!e.ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase)) continue;
                string cls = e.ObjectName["default__".Length..];
                if (!pkg.Exports.Any(x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase))) continue;
                var d = pkg.ReadExportBytes(e);
                List<string>? mats = null;
                if (TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(x => x.Name.Equals("materialvariations", StringComparison.OrdinalIgnoreCase)) is { } mv)
                {
                    int n = BitConverter.ToInt32(d, mv.ValueAt);
                    mats = [];
                    for (int k = 0; k < n; k++) mats.Add(t.PathOf(BitConverter.ToInt32(d, mv.ValueAt + 4 + 4 * k)));
                }
                return (cls, SuperOf(pkg, cls), mats, i);
            }
        }
        catch (Exception ex) when (ex is IOException or PackageFormatException or ArgumentException or IndexOutOfRangeException) { }
        return null;
    }

    /// <summary>The class default with a materialvariations property in <paramref name="packagePath"/>, if any.</summary>
    public static Info? Read(string packagePath) =>
        ReadClass(packagePath) is { Materials: { } m } c ? new Info(Path.GetFileName(packagePath), c.Class, c.Super, m, c.DefaultExport) : null;

    /// <summary>The class a class export extends: a character class has NetIndex, UField.Next (0), then SuperStruct at 8
    /// (Triple Squirrel's: an import of marvelagent_squirrelgirlpetsquirrel); an attachment class has a property list first.</summary>
    public static string? SuperOf(Package pkg, string className)
    {
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!e.ObjectName.Equals(className, StringComparison.OrdinalIgnoreCase) || !pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var d = pkg.ReadExportBytes(e);
                foreach (int at in new[] { 8, TagWalker.Walk(pkg, d, 4) is { } tags ? tags.NoneAt + 12 : -1 })
                {
                    if (at < 0 || at + 4 > d.Length) continue;
                    int r = BitConverter.ToInt32(d, at);
                    if (r > 0 && r <= pkg.Exports.Length && pkg.ClassOf(pkg.Exports[r - 1]).Equals("Class", StringComparison.OrdinalIgnoreCase)) return pkg.Exports[r - 1].ObjectName;
                    if (r < 0 && -r <= pkg.Imports.Length && pkg.Imports[-r - 1].ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase)) return pkg.Imports[-r - 1].ObjectName;
                }
                return null;
            }
            catch (Exception ex) when (ex is PackageFormatException or ArgumentException or IndexOutOfRangeException) { return null; }
        }
        return null;
    }

    /// <summary>The game's file name for a class's package (UC__&lt;class&gt;_SF.upk in the game's own letter case), if there is one.</summary>
    public static string? FileOfClass(string cooked, string cls)
    {
        try { return Directory.GetFiles(cooked, $"UC__{cls}_SF.upk").Select(Path.GetFileName).FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The classes that use the model in <paramref name="targetFile"/>: its own class and the given others (summon classes
    /// naming its model), each with its list (its own, else its parent's, up to four steps). <paramref name="pathOf"/>: a
    /// package's file as the mod has it now (its copy, else the game's stock one; null when neither exists).
    /// </summary>
    public static List<ClassList> ClassesFor(string targetFile, string cooked, Func<string, string?> pathOf, IEnumerable<(string Class, string Label)> others)
    {
        var list = new List<ClassList>();
        var wanted = new List<(string File, string Label)>();
        if (pathOf(targetFile) is string tp && ReadClass(tp) is { }) wanted.Add((targetFile, FileLabel(targetFile) + " (this package's own class)"));
        foreach (var (cls, label) in others)
            if (FileOfClass(cooked, cls) is string f && !wanted.Any(w => w.File.Equals(f, StringComparison.OrdinalIgnoreCase))) wanted.Add((f, label));
        // a class's list, its own or its nearest parent's, read through pathOf (the mod's copies, else the game's)
        (List<string> Mats, string? From)? Effective(string file, Func<string, string?> at)
        {
            if (at(file) is not string path || ReadClass(path) is not { } c) return null;
            List<string>? mats = c.Materials; string? from = null;
            string? sup = c.Super;
            for (int step = 0; mats == null && sup != null && step < 4; step++)
            {
                if (FileOfClass(cooked, sup) is not string sf || at(sf) is not string sp || ReadClass(sp) is not { } sc) break;
                if (sc.Materials != null) { mats = sc.Materials; from = sc.Class; }
                sup = sc.Super;
            }
            return (mats ?? [], from);
        }
        string? Stock(string f) => StockFiles.For(cooked, f) is string s && File.Exists(s) ? s : null;
        foreach (var (file, label) in wanted)
        {
            if (pathOf(file) is not string path || ReadClass(path) is not { } c || Effective(file, pathOf) is not { } now) continue;
            var stock = Effective(file, Stock)?.Mats ?? [];
            list.Add(new ClassList(c.Class, file, label, now.Mats, now.From, stock));
        }
        return list;
    }

    /// <summary>"UC__MarvelAgent_SquirrelGirlPetSquirrel_SF.upk" → "Squirrel Girl Pet Squirrel" (the file keeps the capitals the
    /// class name lost).</summary>
    public static string FileLabel(string file)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(file), @"^UC__(MarvelAgent|MarvelVanityPet|MarvelNPC|MarvelTeamUp|MarvelDestructible|MarvelEntity)_|_SF$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(s.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])", " ").Trim();
    }

    /// <summary>"marvelagent_squirrelgirlpetsquirrelmodern" → "Squirrel Girl Pet Squirrel Modern".</summary>
    public static string Readable(string cls)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(cls, "^(marvelagent|marvelvanitypet|marvelnpc|marvelteamup|marveldestructible|marvelentity)_", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.Join(" ", s.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    // ---- writing ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="classPkgPath"/> with its class default's materialvariations set to <paramref name="materialPaths"/>
    /// (written as the class's own list, added when it had none; an empty list: no variations, the model's own material).
    /// A path the package doesn't have as an export or import is added as an import (a material in the model's package,
    /// loaded with the model). The rebuild is verified.
    /// </summary>
    public static byte[] SetList(string classPkgPath, IReadOnlyList<string> materialPaths)
    {
        var pkg = Package.Open(classPkgPath);
        var t = new Fx.FxTables(pkg);
        var c = ReadClass(classPkgPath) ?? throw new InvalidDataException($"{Path.GetFileName(classPkgPath)} has no character class");
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, y => y.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(y => y.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return pkg.Names.Length + j;
        }
        var addImports = new List<NewImport>();
        int RefOf(string path)
        {
            for (int k = 0; k < pkg.Exports.Length; k++) if (t.PathOf(k + 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return k + 1;
            for (int k = 0; k < pkg.Imports.Length; k++) if (t.PathOf(-k - 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return -k - 1;
            string[] parts = path.Split('.');
            int outer = 0;
            for (int k = 0; k < parts.Length; k++)
            {
                bool last = k == parts.Length - 1;
                string cn = last ? "MaterialInstanceConstant" : "Package", cp = last ? "Engine" : "Core";
                int found = Array.FindIndex(pkg.Imports, im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer && (last || im.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase)));
                if (found >= 0) { outer = -1 - found; continue; }
                int added = addImports.FindIndex(im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer);
                if (added < 0)
                {
                    foreach (string n in new[] { cp, cn, parts[k] }) NameIdx(n);
                    addImports.Add(new NewImport(cp, cn, outer, parts[k]));
                    added = addImports.Count - 1;
                }
                outer = -(pkg.Imports.Length + added + 1);
            }
            return outer;
        }
        var refs = materialPaths.Select(RefOf).ToList();
        var value = new byte[4 + 4 * refs.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, refs.Count);
        for (int k = 0; k < refs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), refs[k]);

        byte[] d = pkg.ReadExportBytes(pkg.Exports[c.DefaultExport]).ToArray();
        var tags = TagWalker.Walk(pkg, d, 4) ?? throw new InvalidDataException("its class default's properties don't read");
        byte[] nd;
        if (tags.FirstOrDefault(x => x.Name.Equals("materialvariations", StringComparison.OrdinalIgnoreCase)) is { } mv)
        {
            nd = [.. d.AsSpan(0, mv.ValueAt), .. value, .. d.AsSpan(mv.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(mv.Start + 16), value.Length);
        }
        else
        {
            // a new tag before None: name, type (ArrayProperty), size, array index, value
            var tag = new byte[24 + value.Length];
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("materialvariations"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), value.Length);
            value.CopyTo(tag, 24);
            nd = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        }
        var replace = new Dictionary<int, Func<long, byte[]>> { [c.DefaultExport] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the class's list didn't check out: " + string.Join("; ", problems.Take(5)));
        // read back: the list as asked
        string tmp = Path.Combine(Path.GetTempPath(), $"mho_var_{Guid.NewGuid():N}.upk");
        try
        {
            File.WriteAllBytes(tmp, output);
            var back = ReadClass(tmp)?.Materials ?? [];
            if (!back.SequenceEqual(materialPaths, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"the list reads back as [{string.Join(", ", back)}], not [{string.Join(", ", materialPaths)}]");
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        return output;
    }

    /// <summary>The model's first material (its material list's slot 0) in <paramref name="meshPkgPath"/>: export index, or
    /// -1 (an import: the material lives in another package), and its path.</summary>
    public static (int Export, string Path)? FirstMaterial(string meshPkgPath, string? meshName)
    {
        var ap = AnimExportCli.Packages.Package.Open(meshPkgPath);
        var pkg = Package.Open(meshPkgPath);
        var t = new Fx.FxTables(pkg);
        int mesh = -1;
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (pkg.ClassOf(pkg.Exports[i]).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && (meshName == null || pkg.Exports[i].ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase))) { mesh = i; break; }
        if (mesh < 0) return null;
        var refs = ModMeshes.MaterialRefs(ap, mesh);
        if (refs.Count == 0 || refs[0] == 0) return null;
        return (refs[0] > 0 ? refs[0] - 1 : -1, t.PathOf(refs[0]));
    }

    /// <summary>A material's color map (diffusetex) decoded, from the package holding the material.</summary>
    public static Bitmap? ColorMap(string pkgPath, string materialPath, string? cooked)
    {
        try
        {
            var pkg = Package.Open(pkgPath);
            var t = new Fx.FxTables(pkg);
            int mic = Enumerable.Range(0, pkg.Exports.Length).FirstOrDefault(k => t.PathOf(k + 1).Equals(materialPath, StringComparison.OrdinalIgnoreCase), -1);
            if (mic < 0 || !MaterialOut.TextureSlots(pkg, mic).TryGetValue("diffusetex", out int tex) || tex < 0) return null;
            var mip = TextureExport.ReadBestMip(pkg, tex, out _, cooked);
            if (mip?.Pixels == null) return null;
            var bgra = TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _);
            if (bgra == null) return null;
            var bmp = new Bitmap(mip.Width, mip.Height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, mip.Width, mip.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < mip.Height; y++) Marshal.Copy(bgra, y * mip.Width * 4, bd.Scan0 + y * bd.Stride, mip.Width * 4);
            bmp.UnlockBits(bd);
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or PackageFormatException or ArgumentException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// <paramref name="meshPkgBytes"/> with a new material: a renamed copy (<paramref name="newName"/>) of the material at
    /// <paramref name="sourceMic"/> whose color map is <paramref name="image"/> (encoded like its old one; its other maps stay).
    /// Returns the package and the new material's path.
    /// </summary>
    public static (byte[] Package, string Path) AddMaterial(byte[] meshPkgBytes, int sourceMic, string image, string newName)
    {
        var pkg = Package.FromBytes(meshPkgBytes);
        var slots = MaterialOut.TextureSlots(pkg, sourceMic);
        if (!slots.TryGetValue("diffusetex", out int tex) || tex < 0) throw new InvalidDataException("its first material's color map isn't in its own package");
        var mip = TextureExport.ReadBestMip(pkg, tex, out _, Model.Settings.Current.CookedFolder);
        bool dxt5 = mip?.Format.Contains("DXT5", StringComparison.OrdinalIgnoreCase) == true;
        string texName = newName + "_diff";
        if (pkg.Exports.Any(e => e.ObjectName.Equals(newName, StringComparison.OrdinalIgnoreCase) || e.ObjectName.Equals(texName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{newName} is in the package already");
        byte[] withTex = MaterialOut.AddTextures(pkg, [new MaterialOut.PlannedTexture("diffusetex", null, texName, tex, image, dxt5)]);
        var cur = Package.FromBytes(withTex);
        string oldTex = pkg.PathOf(pkg.Exports[tex]);
        string newTex = cur.PathOf(cur.Exports.First(e => e.ObjectName.Equals(texName, StringComparison.OrdinalIgnoreCase) && cur.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)));
        int src = Array.FindIndex(cur.Exports, e => cur.PathOf(e).Equals(pkg.PathOf(pkg.Exports[sourceMic]), StringComparison.OrdinalIgnoreCase));
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [oldTex] = newTex };
        var copy = MaterialChoice.Explained(() => ExportCopy.Copy(cur, src, cur, Array.Empty<string>(), newName, replace), out string why)
            ?? throw new InvalidDataException($"copying the material as {newName} failed: {why}");
        var check = copy.Check(copy.Output);
        if (check.Count > 0) throw new InvalidDataException($"{newName}: " + string.Join("; ", check.Take(5)));
        var outPkg = Package.FromBytes(copy.Output);
        return (copy.Output, new Fx.FxTables(outPkg).PathOf(copy.RootRef));
    }

    /// <summary>The package <paramref name="pkgBytes"/> with the color map (diffusetex) of the material at
    /// <paramref name="materialPath"/> replaced by <paramref name="image"/> (same texture, same name: every list naming the
    /// material keeps working; encoded like the old one). The rebuild and the texture are verified.</summary>
    public static byte[] ReplaceColorMap(byte[] pkgBytes, string materialPath, string image)
    {
        var pkg = Package.FromBytes(pkgBytes);
        var t = new Fx.FxTables(pkg);
        int mic = Enumerable.Range(0, pkg.Exports.Length).FirstOrDefault(k => t.PathOf(k + 1).Equals(materialPath, StringComparison.OrdinalIgnoreCase), -1);
        if (mic < 0) throw new InvalidDataException($"{materialPath} isn't in this package");
        if (!MaterialOut.TextureSlots(pkg, mic).TryGetValue("diffusetex", out int tex) || tex < 0) throw new InvalidDataException($"{materialPath}'s color map isn't in its package");
        var mip = TextureExport.ReadBestMip(pkg, tex, out _, Model.Settings.Current.CookedFolder);
        bool dxt5 = mip?.Format.Contains("DXT5", StringComparison.OrdinalIgnoreCase) == true;
        var enc = TextureEncode.FromImage(image, dxt5 ? "dxt5" : "dxt1", 85, 1f, false, 0, refine: true);
        byte[] dds = TextureImport.WriteDds(enc);
        var output = TextureImport.ReplaceMany(pkg, [new TextureImport.Replacement(pkg.PathOf(pkg.Exports[tex]), dds, Path.GetFileName(image))], out var problems, out var verify)
            ?? throw new InvalidDataException(string.Join("; ", problems.Take(5)));
        var check = verify(output);
        if (check.Count > 0) throw new InvalidDataException("the new color map didn't check out: " + string.Join("; ", check.Take(5)));
        return output;
    }

    /// <summary>A color map with its hue turned by <paramref name="degrees"/> (saturation and brightness kept), as a PNG.</summary>
    public static void HueShift(Bitmap src, float degrees, string png)
    {
        using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, src.Width, src.Height);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var px = new byte[bd.Stride * bmp.Height];
        Marshal.Copy(bd.Scan0, px, 0, px.Length);
        // a rotation about the gray axis (luma-preserving, as image editors' hue sliders): no HSV round trip, no banding
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double[] m =
        [
            0.213 + cos * 0.787 - sin * 0.213, 0.715 - cos * 0.715 - sin * 0.715, 0.072 - cos * 0.072 + sin * 0.928,
            0.213 - cos * 0.213 + sin * 0.143, 0.715 + cos * 0.285 + sin * 0.140, 0.072 - cos * 0.072 - sin * 0.283,
            0.213 - cos * 0.213 - sin * 0.787, 0.715 - cos * 0.715 + sin * 0.715, 0.072 + cos * 0.928 + sin * 0.072,
        ];
        static byte C(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            double b = px[i], gr = px[i + 1], r = px[i + 2];
            px[i + 2] = C(m[0] * r + m[1] * gr + m[2] * b);
            px[i + 1] = C(m[3] * r + m[4] * gr + m[5] * b);
            px[i] = C(m[6] * r + m[7] * gr + m[8] * b);
        }
        Marshal.Copy(px, 0, bd.Scan0, px.Length);
        bmp.UnlockBits(bd);
        bmp.Save(png, ImageFormat.Png);
    }
}
