using MhoExtendedModManager.Gui;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Materials tab with no source picked (Kurt, 2026-10-06: Blue Marvel, already built into the mod's package, showed no
/// maps): the shown package's own textures, per material of its model (the material instance's texture parameters), with
/// Edit in Image Editor and Use a File. A change is encoded like the package's other textures (DXT1 / DXT5 as the original,
/// with mipmaps) and replaces that texture in the mod's copy of the package (MPM's texture replace, verified), as a build of
/// the tab: Save Changes keeps it.
/// </summary>
sealed partial class ModelPage
{
    /// <summary>One texture of the shown package: its material, parameter, Texture2D (export index) and how it's stored.</summary>
    sealed record PackageMap(string Material, string Param, int Export, string Texture, int W, int H, string Format)
    {
        public string Kind => Param.ToLowerInvariant() switch
        {
            var p when p.Contains("diffuse") => "Color",
            var p when p.Contains("norm") => "Normal",
            var p when p.Contains("speccolor") => "Spec Color",
            var p when p.Contains("spec") => "Spec (Packed)",
            var p when p.Contains("emissive") || p.Contains("glow") => "Glow",
            var p when p.Contains("reflect") => "Reflection",
            _ => Param,
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
        foreach (int mat in ModMeshes.SectionMaterials(main).Select(x => x.Material).Where(m => m > 0).Distinct())
        {
            MaterialInfo? mi;
            try { mi = ModMaterials.Read(pkg, mat); } catch (Exception ex) when (ex is InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException) { continue; }
            if (mi == null) continue;
            foreach (var (param, export) in mi.Textures)
            {
                var mip = TextureExport.ReadBestMip(pkg, export, out _, Settings.Current.CookedFolder);
                list.Add(new PackageMap(mi.Name, param, export, pkg.Exports[export].ObjectName, mip?.Width ?? 0, mip?.Height ?? 0, mip?.Format ?? "?"));
            }
        }
        return list;
    }

    /// <summary>The rows of the Materials tab in package mode (FillMaterials).</summary>
    void FillPackageMaps()
    {
        foreach (var m in packageMaps ?? [])
        {
            int i = matGrid.Rows.Add(m.Material, m.Kind, $"In the package: {m.Texture} · {m.W}×{m.H} {m.Format.Replace("PF_", "")}");
            matGrid.Rows[i].Tag = m;
            matGrid.Rows[i].Cells["from"].ToolTipText = $"{m.Texture} ({m.Param}) in {Path.GetFileName(packageMapsFile)}: Edit in Image Editor or Use a File changes it in the mod's copy of the package.";
        }
    }

    PackageMap? SelectedPackageMap() => matGrid.CurrentCell is { RowIndex: >= 0 } c && matGrid.Rows[c.RowIndex].Tag is PackageMap m ? m : null;

    /// <summary>The selected package texture as a PNG (largest mip, decoded) at <paramref name="png"/>.</summary>
    bool ExportPackageMap(PackageMap m, string png)
    {
        if (packageMapsFile == null) return false;
        var pkg = Package.Open(packageMapsFile);
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
            var pkg = Package.Open(packageMapsFile);
            byte[]? output = TextureImport.ReplaceMany(pkg, [new TextureImport.Replacement(pkg.PathOf(pkg.Exports[m.Export]), TextureImport.WriteDds(enc), Path.GetFileName(image))], out var problems, out var verify);
            if (output == null) { foreach (var p in problems) Log("Materials: " + p); return false; }
            var check = verify(output);
            if (check.Count > 0) { foreach (var p in check.Take(3)) Log("Materials: PACKAGE PROBLEM: " + p); return false; }
            string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"textures on {Path.GetFileNameWithoutExtension(pkgItem.Key)}"));
            Directory.CreateDirectory(outDir);
            string file = Path.Combine(outDir, pkgItem.Key);
            File.WriteAllBytes(file, output);
            host.SetPackage(pkgItem.Key, file);
            built[pkgItem.Key] = file;
            if (Fingerprint() is string fp) builtPrint[pkgItem.Key] = fp;
            SaveState();
            Log($"Materials: {m.Material}'s {m.Kind.ToLowerInvariant()} ({m.Texture}) replaced from {Path.GetFileName(image)}, {enc.FourCC} {enc.Width}×{enc.Height} with {enc.Levels.Count} mips; {pkgItem.Key} is in the mod now (Save Changes keeps it).");
            status.Text = $"{m.Texture} Updated in the Package"; status.ForeColor = Ui.Enabled;
            SchedulePreview();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or PackageFormatException or UnauthorizedAccessException)
        { Log($"Materials: {m.Texture} not replaced: {ex.Message}"); return false; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Use a File in package mode: the picked image replaces the texture.</summary>
    void UsePackageMapFile(PackageMap m)
    {
        using var d = new OpenFileDialog { Title = $"{m.Kind} map for {m.Material} ({m.Texture})", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        ReplacePackageMap(m, d.FileName);
    }
}
