using MhoExtendedModManager.Gui;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Materials tab with no source picked (Kurt, 2026-10-06: Blue Marvel, already built into the mod's package, showed no
/// maps): the shown package's own textures, per material of its model (the material instance's texture parameters), with
/// Edit in Image Editor and Replace File. A change is encoded like the package's other textures (DXT1 / DXT5 as the original,
/// with mipmaps) and replaces that texture in the mod's copy of the package (MPM's texture replace, verified), as a build of
/// the tab: Save Changes keeps it.
/// </summary>
sealed partial class ModelPage
{
    /// <summary>One texture of the shown package: its material, parameter, Texture2D (export index) and how it's stored.</summary>
    /// <param name="InFile">The game package the texture is in when it isn't the shown one (a material the model imports
    /// from its hero's base package), with <paramref name="InPath"/> the copy it's read from; null: the shown package.</param>
    sealed record PackageMap(string Material, string Param, int Export, string Texture, int W, int H, string Format, string? InFile = null, string? InPath = null)
    {
        // (a plain material's textures have no parameter name: the texture's own name tells)
        public string Kind => (Param.Length > 0 ? Param : Texture).ToLowerInvariant() switch
        {
            var p when p.Contains("diffuse") || Param.Length == 0 && (p.Contains("_diff") || p.EndsWith("_d")) => "Color",
            var p when p.Contains("norm") => "Normal",
            var p when p.Contains("speccolor") => "Spec Color",
            var p when p.Contains("spec") => "Spec (Packed)",
            var p when p.Contains("emissive") || p.Contains("glow") => "Glow",
            var p when p.Contains("reflect") => "Reflection",
            _ => Param.Length > 0 ? Param : "Texture",
        };
    }

    List<PackageMap>? packageMaps;
    /// <summary>The package the listed textures are in.</summary>
    string? packageMapsFile;

    /// <summary>The textures of the model shown from <paramref name="packagePath"/> (its sections' materials, in the package).</summary>
    static List<PackageMap> ListPackageMaps(string packagePath, MeshRef main)
    {
        var list = new List<PackageMap>();
        var pkg = Package.Open(packagePath);
        foreach (int mat in ModMeshes.SectionMaterials(main).Select(x => x.Material).Where(m => m != 0).Distinct())
        {
            // a material in this package, or one the model imports from its hero's base package (Jean Grey's Phoenix wings,
            // 2026-10-07): that one's textures are in the other package
            Package mp = pkg; int mref = mat; string? inFile = null, inPath = null;
            if (mat < 0)
            {
                if (ModMeshes.ImportedMaterialAt(main, pkg, mat, Settings.Current.CookedFolder) is not { } at) continue;
                mp = at.Pkg; mref = at.Index + 1; inFile = at.File; inPath = at.Path;
            }
            MaterialInfo? mi;
            try { mi = ModMaterials.Read(mp, mref); } catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { continue; }
            if (mi == null) continue;
            var textures = mi.Textures.Select(kv => (Param: kv.Key, Export: kv.Value)).ToList();
            if (textures.Count == 0)
                // a plain material (no parameters): the textures its compiled shader uses, as the 3D view reads them
                try
                {
                    var me = mp.Exports[mref - 1];
                    foreach (int r in ExportCopy.MaterialNativeTextures(mp, mp.ReadExportBytes(me), mp.ClassOf(me)).Distinct())
                        if (r > 0 && mp.ClassOf(mp.Exports[r - 1]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) textures.Add(("", r - 1));
                }
                catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { }
            foreach (var (param, export) in textures)
            {
                var mip = TextureExport.ReadBestMip(mp, export, out _, Settings.Current.CookedFolder);
                list.Add(new PackageMap(mi.Name, param, export, mp.Exports[export].ObjectName, mip?.Width ?? 0, mip?.Height ?? 0, mip?.Format ?? "?", inFile, inPath));
            }
        }
        return list;
    }

    /// <summary>The rows of the Materials tab in package mode (FillMaterials).</summary>
    void FillPackageMaps()
    {
        foreach (var m in packageMaps ?? [])
        {
            int i = matGrid.Rows.Add(m.Material, m.Kind, $"{(m.InFile != null ? $"In {m.InFile}" : "In the package")}: {m.Texture} · {m.W}×{m.H} {m.Format.Replace("PF_", "")}");
            matGrid.Rows[i].Tag = m;
            matGrid.Rows[i].Cells["from"].ToolTipText = m.InFile != null
                ? $"{m.Texture} is in {m.InFile}, the hero's base package this model takes the material from: changing it adds that package to the mod (asked first), and every costume of the hero that uses the texture shows the change."
                : $"{m.Texture}{(m.Param.Length > 0 ? $" ({m.Param})" : "")} in {Path.GetFileName(packageMapsFile)}: Edit in Image Editor or Replace File changes it in the mod's copy of the package.";
        }
    }

    PackageMap? SelectedPackageMap() => matGrid.CurrentCell is { RowIndex: >= 0 } c && matGrid.Rows[c.RowIndex].Tag is PackageMap m ? m : null;

    /// <summary>The selected package texture as a PNG (largest mip, decoded) at <paramref name="png"/>.</summary>
    bool ExportPackageMap(PackageMap m, string png)
    {
        if (packageMapsFile == null) return false;
        var pkg = Package.Open(m.InPath ?? packageMapsFile);
        var mip = TextureExport.ReadBestMip(pkg, m.Export, out string note, Settings.Current.CookedFolder);
        if (mip == null || TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is not byte[] px) { Log($"Materials: {m.Texture} can't be read ({note})."); return false; }
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        using var bmp = TextureDecode.ToBitmap(px, mip.Width, mip.Height);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return true;
    }

    /// <summary>An image put in for a package texture: encoded as the original (DXT1 / DXT5, its size, mipmaps) and replaced in
    /// the mod's copy of the package, written to the work folder and made the mod's package (as a build). True when it went in.</summary>
    bool ReplacePackageMap(PackageMap m, string image)
    {
        if (packageMapsFile == null || ChosenPackage is not CharacterList.Item pkgItem) return false;
        // a texture in another package (an imported material): that package goes into the mod with the change, asked first
        string targetFile = pkgItem.Key, sourcePath = packageMapsFile;
        if (m.InFile != null)
        {
            bool inMod = host.Packages.FirstOrDefault(p => p.File.Equals(m.InFile, StringComparison.OrdinalIgnoreCase)) is { File: not null } own && File.Exists(own.Path);
            if (!inMod && Dialog.Choose(this, $"{m.Texture} is in {m.InFile}, the hero's base package the model takes its {m.Material} material from. Changing it adds that package to the mod (the game's copy with this texture changed); every costume of the hero that uses the texture shows the change.",
                    "Texture in Another Package", "Add It and Change the Texture", "Cancel") != 0) return false;
            targetFile = m.InFile;
            sourcePath = inMod ? host.Packages.First(p => p.File.Equals(m.InFile, StringComparison.OrdinalIgnoreCase)).Path : m.InPath!;
        }
        string temp = Path.Combine(Path.GetTempPath(), "mhoextmm_tex_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            // the original's size (divisible by 4 for DXT)
            int w = m.W > 0 ? m.W : 4, h = m.H > 0 ? m.H : 4;
            using (var src = new Bitmap(image))
            using (var fit = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(fit))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.DrawImage(src, 0, 0, w, h);
                }
                fit.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            }
            string? want = m.Format.Contains("DXT1", StringComparison.OrdinalIgnoreCase) ? "dxt1" : m.Format.Contains("DXT5", StringComparison.OrdinalIgnoreCase) ? "dxt5" : null;
            var enc = TextureEncode.FromImage(temp, want, 85, 1f, noMips: false, refine: true);
            var pkg = Package.Open(sourcePath);
            byte[]? output = TextureImport.ReplaceMany(pkg, [new TextureImport.Replacement(pkg.PathOf(pkg.Exports[m.Export]), TextureImport.WriteDds(enc), Path.GetFileName(image))], out var problems, out var verify);
            if (output == null) { foreach (var p in problems) Log("Materials: " + p); return false; }
            var check = verify(output);
            if (check.Count > 0) { foreach (var p in check.Take(3)) Log("Materials: PACKAGE PROBLEM: " + p); return false; }
            string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"textures on {Path.GetFileNameWithoutExtension(targetFile)}"));
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, targetFile);
            File.WriteAllBytes(file, output);
            host.SetPackage(targetFile, file);
            if (m.InFile == null)
            {
                built[pkgItem.Key] = file;
                if (Fingerprint() is string fp) builtPrint[pkgItem.Key] = fp;
            }
            else
            {
                added.Add(targetFile);
                // the list now reads the texture from the mod's copy
                packageMaps = packageMaps?.Select(x => x.InFile != null && x.InFile.Equals(targetFile, StringComparison.OrdinalIgnoreCase) ? x with { InPath = file } : x).ToList();
            }
            SaveState();
            Log($"Materials: {m.Material}'s {m.Kind.ToLowerInvariant()} ({m.Texture}) replaced from {Path.GetFileName(image)}, {enc.FourCC} {enc.Width}×{enc.Height} with {enc.Levels.Count} mips; {targetFile} is in the mod now (Save Changes keeps it).");
            status.Text = $"{m.Texture} Updated in the Package"; status.ForeColor = Ui.Enabled;
            SchedulePreview();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or PackageFormatException or UnauthorizedAccessException)
        { Log($"Materials: {m.Texture} not replaced: {ex.Message}"); return false; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>
    /// Adjust Colors on a texture in the package (no source picked; Kurt, 2026-10-07: "accommodate inside a package/target
    /// too"): the texture as it is now (the mod's copy, edits included) in the Adjust Colors window; OK writes the adjusted
    /// image into the mod's copy of the package as Replace File does (encoded like the original). Unlike a source model's
    /// map, the result is the texture itself: adjusting again starts from it.
    /// </summary>
    void AdjustPackageMap(PackageMap m, ColorAdjust? preset = null)
    {
        string png = Path.Combine(Path.GetTempPath(), "mhoextmm_adjust_" + Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            if (!ExportPackageMap(m, png)) return;
            ColorAdjust adj;
            if (preset != null) adj = preset;   // (the test: no window)
            else
            {
                using var f = new ColorAdjustForm(m.Texture, png, null);
                if (f.ShowDialog(this) != DialogResult.OK || f.Result is not { } chosen) return;
                adj = chosen;
            }
            string adjusted = ColorAdjust.FileFor(png, adj);
            if (adjusted == png) { Log($"Materials: {m.Texture} couldn't be adjusted."); return; }
            if (ReplacePackageMap(m, adjusted)) Log($"Materials: {m.Texture} adjusted in the package: {adj.Describe()}.");
        }
        finally { try { if (File.Exists(png)) File.Delete(png); } catch (IOException) { } }
    }

    /// <summary>Replace File in package mode: the picked image replaces the texture.</summary>
    void UsePackageMapFile(PackageMap m)
    {
        using var d = new OpenFileDialog { Title = $"{m.Kind} map for {m.Material} ({m.Texture})", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        ReplacePackageMap(m, d.FileName);
    }
}
