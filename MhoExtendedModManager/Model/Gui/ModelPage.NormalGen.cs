using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Generate Normal Map (the Materials tab's right-click menu, Kurt 2026-10-19): a normal map made from the material's color map
/// (NormalMapGen, DirectX green) at a chosen strength, used in place of the one it has. A source's material: the image goes into
/// the mod's Model folder as its Normal file (as Replace File). A package's own texture: it replaces the material's normal-map
/// texture in the mod's copy of the package. Either way the game reads it as a normal map (sRGB off, TC_NormalMap: MPM 2.53.26).
/// </summary>
sealed partial class ModelPage
{
    static readonly (string Label, float Strength, bool Invert, string Tip)[] NormalStrengths =
    [
        ("Soft", 0.5f, false, "Gentle bumps: half the usual slope."),
        ("Medium", 1f, false, "The usual strength (what the importer makes on its own)."),
        ("Strong", 2f, false, "Twice the usual slope: deep stitching and panels."),
        ("Medium, Dark Is Raised", 1f, true, "The usual strength, with dark parts of the color map raised instead of bright ones."),
    ];

    /// <summary>The menu item, or null when the selected row has no color map to make one from.</summary>
    ToolStripMenuItem? GenerateNormalItem()
    {
        bool can = SelectedPackageMap() is { } pm
            ? pm.Kind is "Color" or "Normal" && PackageNormalPair(pm) is { Normal: not null }
            : SelectedMap() is { Kind: "Color" or "Normal" } sel && shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex?.Diffuse != null && OverridesPath() != null;
        if (!can) return null;
        var item = new ToolStripMenuItem("Generate Normal Map") { ToolTipText = "Makes the material's normal map from its color map (bright = raised) and uses it in place of the one it has. The game reads it as a normal map (sRGB off). Undo (Ctrl+Z) takes it back." };
        foreach (var (label, strength, invert, tip) in NormalStrengths)
            item.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => GenerateNormal(strength, invert, label)) { ToolTipText = tip });
        return item;
    }

    /// <summary>A package texture's material: its color and normal-map textures.</summary>
    (PackageMap? Color, PackageMap? Normal) PackageNormalPair(PackageMap m)
    {
        var same = (packageMaps ?? []).Where(x => x.Material == m.Material).ToList();
        return (same.FirstOrDefault(x => x.Kind == "Color"), same.FirstOrDefault(x => x.Kind == "Normal"));
    }

    void GenerateNormal(float strength, bool invert, string label)
    {
        var settings = new NormalMapSettings { Strength = strength, Invert = invert };
        string tag = label.ToLowerInvariant().Replace(", ", "_").Replace(" ", "");
        if (SelectedPackageMap() is { } pm)
        {
            var (color, normal) = PackageNormalPair(pm);
            if (normal == null) return;
            string png = Path.Combine(MapsCache, $"normal_{FbxExport.SafeName(pm.Material)}_{Guid.NewGuid().ToString("N")[..6]}.png");
            try
            {
                Directory.CreateDirectory(MapsCache);
                if (color == null || !ExportPackageMap(color, png)) { Log($"Materials: {pm.Material} has no color map to make a normal map from."); return; }
                var (w, h, px) = NormalMapGen.LoadArgb(png);
                // (the color map's alpha isn't a cut-out mask in every material: ignored, every texel gets its slope)
                var opaque = px.Select(c => c | unchecked((int)0xFF000000)).ToArray();
                NormalMapGen.SaveArgb(w, h, NormalMapGen.Make(w, h, opaque, settings), png);
                if (ReplacePackageMap(normal, png)) { Log($"  generated from {color.Texture} ({label.ToLowerInvariant()})"); TestGeneratedPackage = (built.GetValueOrDefault(ChosenPackage?.Key ?? ""), normal.Texture); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the normal map couldn't be made: {ex.Message}"); }
            finally { try { File.Delete(png); } catch (IOException) { } }
            return;
        }
        if (SelectedMap() is not { } sel || OverridesPath() is not string path) return;
        if (shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex is not { Diffuse: string diffuse } tex) return;
        string dir = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string name = $"{FbxExport.SafeName(sel.Material)}_normal_generated_{tag}.png";
        try
        {
            var (w, h, px) = NormalMapGen.LoadArgb(diffuse);
            int[]? mask = null;
            if (tex.Alpha != null) { var (aw, ah, apx) = NormalMapGen.LoadArgb(tex.Alpha); if (aw == w && ah == h) mask = apx; }
            NormalMapGen.SaveArgb(w, h, NormalMapGen.Make(w, h, px, settings, mask), Path.Combine(dir, name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: the normal map couldn't be made: {ex.Message}"); return; }
        string rel = Path.Combine(Path.GetFileName(dir), name);
        ChangeOverrides((_, e) => { e.Normal = rel; e.FlipGreen = false; }, sel.Material,
            $"{sel.Material}'s normal map is now generated from its color map ({label.ToLowerInvariant()}).");
    }

    /// <summary>Test: Generate Normal Map on the first row of <paramref name="kind"/> at the given strength.</summary>
    internal void TestGenerateNormal(string kind, int strengthIndex)
    {
        int row = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[1].Value == kind);
        if (row < 0) return;
        matGrid.CurrentCell = matGrid.Rows[row].Cells[0];
        var (label, strength, invert, _) = NormalStrengths[strengthIndex];
        GenerateNormal(strength, invert, label);
    }

    /// <summary>Test: Undo (Ctrl+Z); true when there was a step to take back.</summary>
    internal int TestUndoSteps => undo.Count;
    /// <summary>Test: the package and texture the last Generate Normal Map on a package texture wrote.</summary>
    internal (string? File, string Texture)? TestGeneratedPackage { get; private set; }
    internal bool TestUndo() { bool any = undoButton.Enabled; Undo(); return any; }
}
