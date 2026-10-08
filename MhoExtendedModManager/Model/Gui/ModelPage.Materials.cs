using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Materials tab (Kurt, 2026-10-04): each material's color, normal, spec and alpha maps and where they come from (found
/// with the model, generated, none, or your file), with Replace File (an override, copied into the mod's Model folder), Back to
/// Automatic and OpenGL Normal Map (its green flipped to DirectX). Saved per source in materials\&lt;source&gt;.json (MaterialOverrides);
/// the preview, Build and Export FBX use it, and Undo takes a change back.
/// </summary>
sealed partial class ModelPage
{
    readonly DataGridView matGrid = new() { Dock = DockStyle.Fill };
    Button matUse = null!, matAdjust = null!, matAuto = null!, matFlip = null!, matRecipe = null!, matTags = null!, matChannels = null!, matLayout = null!, matNoGlow = null!, matExport = null!;
    List<(string Material, Textures Tex)> shownMaterials = [];
    /// <summary>The table is being refilled: the buttons wait (Kurt, 0.37.139 crash on Next Recipe: clearing the rows selects
    /// nothing, which disabled the focused button; Windows moved the focus into the table mid-refill and the grid threw
    /// "reentrant call to SetCurrentCellAddressCore").</summary>
    bool fillingMat;

    static readonly (string Label, ModelView.MapView Mode)[] ShowMapChoices =
        [("All Maps", ModelView.MapView.All), ("Color Map", ModelView.MapView.Colour), ("Normal Map", ModelView.MapView.Normal),
         ("Spec: Shine (R)", ModelView.MapView.Spec), ("Spec: Power (G)", ModelView.MapView.SpecPower), ("Spec: Reflectivity (A)", ModelView.MapView.Reflectivity),
         ("Spec: Skin Mask (B)", ModelView.MapView.SkinMask), ("Spec: Combined (RGBA)", ModelView.MapView.SpecPacked),
         ("Spec Color", ModelView.MapView.SpecColor), ("Glow Map", ModelView.MapView.Glow), ("Alpha", ModelView.MapView.Alpha)];

    static readonly string[] MapKinds = ["Color", "Normal", "Spec", "MHO Spec", "Spec Color", "Glow", "Alpha"];

    /// <summary>The source's overrides file (materials\&lt;source&gt;.json in the Model work folder); null without a source.</summary>
    string? OverridesPath()
    {
        string? key = model?.Folder ?? (sourceFbx != null ? ImportBuild.SourceName(sourceFbx) : null);
        return key == null ? null : Path.Combine(host.WorkFolder, "materials", FbxExport.SafeName(key) + ".json");
    }

    /// <summary>The overrides file when it exists (what the preview, Build and Export are given).</summary>
    string? OverridesFile() => OverridesPath() is string p && File.Exists(p) ? p : null;

    Control MaterialsTab()
    {
        matGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "material", HeaderText = "Material", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 35, ReadOnly = true });
        matGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "map", HeaderText = "Map", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, ReadOnly = true });   // (a fixed 70 clipped at 150 %: Kurt)
        matGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "from", HeaderText = "From", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 65, ReadOnly = true });
        matGrid.AllowUserToAddRows = false; matGrid.AllowUserToDeleteRows = false; matGrid.RowHeadersVisible = false;
        matGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; matGrid.MultiSelect = false;
        matGrid.ShowCellToolTips = true;
        Ui.StyleGrid(matGrid);
        Ui.Tip(matGrid, "Each material's maps and where they come from. Double-click a row to see its map large (made ones too: generated, converted, from your tags); right-click a row to view, edit in your image editor, use a file or go back to automatic. Ctrl+click the model in the preview to pick its material.");
        matGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) _ = ViewMap(e.RowIndex); };   // (Kurt, 2026-10-04: was Use a File, now Replace File)
        // right-click (Kurt, 2026-10-06): the row selected, then its menu: view large, edit, a file, back to automatic
        matGrid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            matGrid.CurrentCell = matGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            var m = new ContextMenuStrip();
            int row = e.RowIndex;
            m.Items.Add(new ToolStripMenuItem("View Large", null, (_, _) => _ = ViewMap(row)) { ToolTipText = "The map large, with zoom and Export as PNG (also: double-click the row)." });
            m.Items.Add(new ToolStripMenuItem("Edit in Image Editor", null, (_, _) => EditMapExternally()) { Enabled = matEdit.Enabled, ToolTipText = "Opens it in your image editor; each save there comes back here." });
            m.Items.Add(new ToolStripMenuItem("Replace File", null, (_, _) => UseMapFile()) { Enabled = matUse.Enabled, ToolTipText = "Puts in an image of yours for this map." });
            if (SelectedMap() is { Kind: "Color" } || SelectedPackageMap() is { Kind: "Color" })
                m.Items.Add(new ToolStripMenuItem("Adjust Colors", null, (_, _) => AdjustColors()) { Enabled = matAdjust.Enabled, ToolTipText = "Hue, saturation, brightness and levels for this color map." });
            m.Items.Add(new ToolStripMenuItem("Back to Automatic", null, (_, _) => MapBackToAutomatic()) { Enabled = matAuto.Enabled, ToolTipText = "Forgets your file for this map." });
            m.Closed += (_, _) => BeginInvoke(m.Dispose);
            m.Show(matGrid, matGrid.PointToClient(Cursor.Position));
        };
        matGrid.CurrentCellChanged += (_, _) => { if (!fillingMat) MatSelectionChanged(); };
        matUse = Ui.FlatButton("Replace File", UseMapFile, "Puts your own image (PNG, JPG or BMP) in for the selected map: copied into the mod's Model folder; the preview, Build and Export FBX use it. Undo (Ctrl+Z) takes it back. MHO Spec takes a map in the game's own packed layout (R shine, G spec power, B skin mask, A reflectivity), put in as it is with Angela's armor material; Spec Color tints its highlights.");
        matAuto = Ui.FlatButton("Back to Automatic", MapBackToAutomatic, "Forgets your file for the selected map (a Normal row: the green flip too); the importer's own choice is used again.");
        matFlip = Ui.FlatButton("OpenGL Normals", FlipGreen, "The selected normal map was made the OpenGL way (Blender, Unity, Maya, Substance's OpenGL preset: green up): its green is flipped to MHO's DirectX way (lit when on). Bumps that look dented instead of raised need it. Only for a normal map of the model's own or your file: one generated from the color map is already DirectX.");
        matRecipe = Ui.FlatButton("Next Recipe", NextSpecRecipe, "A spec row with no map of its own gets one made from the color map; this steps through the ways it's made: Soft, Strong, Dark Is Shiny, Detail, Flat. Preview Shows → Spec Map (above) shows it.");
        matAdjust = Ui.FlatButton("Adjust Colors", AdjustColors, "Adjusts the selected material's color map: hue, saturation and brightness, and levels (input black and white, gamma, output black and white), with the map before and after side by side. The file isn't changed: the preview, Build and Export FBX use an adjusted copy, and the values are kept with the material (lit while it's adjusted). Undo (Ctrl+Z) takes a change back; Back to Automatic on the Color row clears it.");
        matTags = Ui.FlatButton("Tag Colors", TagColors, "Tell it what each color group of the selected material is made of (Metal, Skin, Leather, Cloth): an MHO spec map is made from your tags, with Angela's armor material. Colors alone can't tell gold paint from gold metal.");
        matChannels = Ui.FlatButton("From Channels", FromChannels, "Makes the selected material's MHO spec map from separate gray images, one per channel (Shine R, Power G, Skin Mask B, Reflectivity A), or an RGB image plus a gray Reflectivity one. Image editors show a PNG's alpha as transparency and can change the colors under it when saving: editing the channels apart avoids that.");
        matLayout = Ui.FlatButton("Layout ▾", ShowLayoutMenu, "Which of the game's packed layouts the selected MHO spec map is in. Material types pack different things into R, G, B and A: " +
            string.Join("; ", SpecLayouts.All.Select(l => $"{l.Label}: R {l.Channels[0]}, G {l.Channels[1]}, B {l.Channels[2]}, A {l.Channels[3]}")) +
            ". The model is built with Angela's material, so another layout is converted to hers: rim mask, ambient and height are left out, and a glow channel becomes the material's glow map (the Glow row). Automatic reads the layout from the file name (a map saved from the game under its parameter name), else Angela's.");
        matNoGlow = Ui.FlatButton("No Glow", NoGlow, "Turns the selected material's glow off (lit when off), whatever its maps say: its own glow map, the glow channel of its MHO spec map, or the bright spots of an MFF color map. Click again to turn it back on.");
        matNoGlow.Visible = false;   // (in OpenGL Normals' place on a Glow row)
        // icons (Kurt, 2026-10-04): the old names lead their tooltips
        float sc = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        Icons.Make(matUse, "Replace File", Icons.Folder, sc);
        Icons.Make(matAuto, "Back to Automatic", Icons.Reset, sc);
        Icons.Make(matFlip, "OpenGL Normals", Icons.FlipVertical, sc);
        Icons.Make(matNoGlow, "No Glow", Icons.NoGlow, sc);
        Icons.Make(matRecipe, "Next Recipe", Icons.Next, sc);
        Icons.Make(matTags, "Tag Colors", Icons.Tag, sc);
        Icons.Make(matAdjust, "Adjust Colors", Icons.Sun, sc);
        Icons.Make(matChannels, "From Channels", Icons.Channels, sc);
        Icons.Make(matLayout, "Layout", Icons.Layout, sc);
        matExport = Ui.FlatButton("Export Maps", () => _ = ExportMaps(null), "Saves every material's maps as PNG files into a folder you pick, the ones the importer makes too (generated normal and spec maps, MHO spec maps converted or made from your tags, glow maps): <material>.png, _n, _sp, _mhospec (with its gray channel files), _speccolor, _glow, _alpha. The names the FBX import reads, so they can be edited and used again.");
        Icons.Make(matExport, "Export Maps", Icons.Export, sc);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        EditButton();
        foreach (var b in new[] { matUse, matEdit, matAdjust, matAuto, matFlip, matNoGlow, matRecipe, matTags, matChannels, matLayout, matExport }) { b.Margin = new Padding(0, 0, 6, 0); buttons.Controls.Add(b); }
        // what the preview shows (Kurt, 2026-10-04: moved here from Look ▾): the model lit, or one map on its own
        var showMap = new DropDown { Width = (int)(200 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi)), Margin = new Padding(0, 0, 0, 0) };
        showMap.Items.AddRange([.. ShowMapChoices.Select(x => (object)x.Label)]);
        showMap.SelectedIndex = Math.Max(0, Array.FindIndex(ShowMapChoices, x => x.Mode == preview.ShowMap));
        showMap.SelectedIndexChanged += (_, _) => { if (showMap.SelectedIndex >= 0) preview.ShowMap = ShowMapChoices[showMap.SelectedIndex].Mode; };
        Ui.Tip(showMap, "What the preview shows: All Maps is the model lit as usual; the others show one map or channel on its own. The spec map's channels: Shine (R: how strong the highlight is), Power (G: how tight and sharp it is), Reflectivity (A: how much the environment reflects, what makes metal look like metal), Skin Mask (B: where skin shading applies); Combined (RGBA) shows them together as an image editor would (the A channel as see-through over a checkerboard: it is reflectivity, not transparency); Spec Color tints the highlight (white: none of its own); Glow Map shows what glows, in its glow color (black: none). White = most; black = none or a channel the material doesn't use. Color Map unlit, Normal Map its own colors (flat blue: none), Alpha (black is cut out).");
        var showRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        showRow.Controls.Add(new Label { Text = "Preview Shows", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        showRow.Controls.Add(showMap);
        var head = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        head.Controls.Add(showRow, 0, 0); head.Controls.Add(buttons, 0, 1);
        MatSelectionChanged();
        return Column("", head, matGrid);
    }

    /// <summary>Ctrl+click on the model in the Materials tab: that material's first row (the color map) selected and shown.</summary>
    void SelectMaterialRow(string material)
    {
        for (int i = 0; i < matGrid.Rows.Count; i++)
        {
            string? m = matGrid.Rows[i].Tag switch { ValueTuple<string, string> t => t.Item1, PackageMap pm => pm.Material, _ => null };
            if (m == null || !m.Equals(material, StringComparison.OrdinalIgnoreCase)) continue;
            matGrid.CurrentCell = matGrid.Rows[i].Cells["material"];
            matGrid.FirstDisplayedScrollingRowIndex = i;
            Log($"Materials: {material} (Ctrl+click)");
            return;
        }
        Log($"Materials: {material} isn't in the list.");
    }

    /// <summary>The material list as the preview made it (after the overrides).</summary>
    void FillMaterials()
    {
        int keep = matGrid.CurrentCell?.RowIndex ?? -1;
        fillingMat = true;
        try
        {
        matGrid.Rows.Clear();
        if (!HasSource && packageMaps != null) FillPackageMaps();   // no source: the shown package's own textures
        var ov = OverridesFile() is string p ? MaterialOverrides.Load(p) : new MaterialOverrides();
        foreach (var (mat, tex) in shownMaterials)
        {
            ov.Materials.TryGetValue(mat, out var e);
            foreach (var kind in MapKinds)
            {
                var (from, path) = Describe(kind, tex, e);
                int i = matGrid.Rows.Add(mat, kind, from);
                matGrid.Rows[i].Tag = (mat, kind);
                matGrid.Rows[i].Cells["from"].ToolTipText = path ?? from;
                if (from.StartsWith("Your file", StringComparison.Ordinal)) matGrid.Rows[i].DefaultCellStyle.ForeColor = Ui.OverrideAmber;
                else if (path == null) matGrid.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
            }
        }
        if (keep >= 0 && keep < matGrid.Rows.Count) matGrid.CurrentCell = matGrid.Rows[keep].Cells["material"];
        }
        finally { fillingMat = false; }
        MatSelectionChanged();
    }

    /// <summary>Where a map comes from, in words, and its file.</summary>
    static (string From, string? Path) Describe(string kind, Textures tex, MaterialOverrides.Entry? e)
    {
        string? mine = kind switch { "Color" => e?.Color, "Normal" => e?.Normal, "Spec" => e?.Spec, "MHO Spec" => e?.SpecMho, "Spec Color" => e?.SpecColor, "Glow" => e?.Glow, _ => e?.Alpha };
        string? file = kind switch { "Color" => tex.Diffuse, "Normal" => tex.Normal, "Spec" => tex.Spec, "MHO Spec" => tex.SpecMho, "Spec Color" => tex.SpecColor, "Glow" => tex.Glow, _ => tex.Alpha };
        if (kind == "Glow" && tex.GlowOff) return ("Off (No Glow)", null);
        if (kind == "Glow" && file == null && tex.SpecMho == null && tex.ColorTags is { Count: > 0 } gtags && tex.GlowFile is string tagGlow)
            return ($"From your color tags: {gtags.Count(t => ColorTags.Name(t.Tag) == "glow")} group(s) tagged Glow", tagGlow);
        if (kind == "Glow" && file == null && tex.GlowFile is string derived)
            return ($"From the MHO spec map's {tex.SpecLayoutUsed.Channels[tex.SpecLayoutUsed.GlowChannel]} channel ({"RGBA"[tex.SpecLayoutUsed.GlowChannel]}) × the color map", derived);
        if (kind == "Spec" && tex.UsesMhoSpec) return ("Not used: the MHO Spec map is", null);
        if (kind == "MHO Spec" && mine == null && tex.SpecMho == null && tex.ColorTags is { Count: > 0 } ct)
            return ($"Made from your color tags: {string.Join(", ", ct.GroupBy(x => ColorTags.Name(x.Tag)).Select(g => $"{g.Count()} {ColorTags.Label(g.Key).ToLowerInvariant()}"))}", null);
        string flip = kind == "Normal" && tex.NormalFlipGreen ? " · OpenGL (green flipped)" : "";
        if (kind == "MHO Spec" && file != null && tex.SpecLayoutUsed.Id != "v2skin") flip = $" · {tex.SpecLayoutUsed.Label}, converted to Angela's";
        if (mine != null) return ($"Your file: {System.IO.Path.GetFileName(file ?? mine)}{flip}", file);
        if (file != null) return ((kind == "Color" && tex.Guessed ? "Found (by a near name): " : kind == "Normal" ? "The model's own: " : "Found: ") + System.IO.Path.GetFileName(file) + flip, file);
        return (kind switch
        {
            "Color" => "None: gray",
            "Normal" => tex.Diffuse != null ? "Generated from the color map (already DirectX: nothing to flip)" : "None: flat",
            "Spec" => tex.Diffuse != null ? "Generated from the color map: " + SpecMapGen.Label(tex.SpecRecipe) : "None: low shine, no reflection",
            "MHO Spec" => "None (an MHO packed map: shine, power, skin, reflectivity)",
            "Spec Color" => "None: the highlight takes the color map's tint",
            "Glow" => "None (MFF models with metal maps: near-white / cyan spots glow)",
            _ => "None: opaque",
        }, null);
    }

    (string Material, string Kind)? SelectedMap() =>
        matGrid.CurrentCell is { RowIndex: >= 0 } c && matGrid.Rows[c.RowIndex].Tag is ValueTuple<string, string> t ? t : null;

    void MatSelectionChanged()
    {
        var sel = SelectedMap();
        bool any = sel != null && OverridesPath() != null;
        matUse.Enabled = any;
        if (matEdit != null) matEdit.Enabled = any && SelectedMapFile() != null;
        if (SelectedPackageMap() != null) { matUse.Enabled = true; if (matEdit != null) matEdit.Enabled = true; }
        var e = sel is { } s && OverridesFile() is string p && MaterialOverrides.Load(p).Materials.TryGetValue(s.Material, out var x) ? x : null;
        matAuto.Enabled = any && e != null && (sel!.Value.Kind switch { "Color" => e.Color != null || e.Adjust is { IsNone: false }, "Normal" => e.Normal != null || e.FlipGreen, "Spec" => e.Spec != null || e.SpecRecipe != null, "MHO Spec" => e.SpecMho != null, "Spec Color" => e.SpecColor != null, "Glow" => e.Glow != null || e.GlowOff, _ => e.Alpha != null });
        bool glowRow = sel?.Kind == "Glow";
        matNoGlow.Visible = glowRow; matFlip.Visible = !glowRow;
        matNoGlow.Enabled = any && glowRow;
        Ui.Lit(matNoGlow, glowRow && e?.GlowOff == true);
        var tx = sel is { } s2 ? shownMaterials.FirstOrDefault(m => m.Material == s2.Material).Tex : null;
        matRecipe.Enabled = any && sel!.Value.Kind == "Spec" && tx is { Spec: null, SpecMho: null, Diffuse: not null } && tx.ColorTags is not { Count: > 0 };
        matTags.Enabled = any && tx is { Diffuse: not null, SpecMho: null };
        matAdjust.Enabled = any && sel!.Value.Kind == "Color" && tx?.Diffuse != null;
        Ui.Lit(matAdjust, sel?.Kind == "Color" && e?.Adjust is { IsNone: false });
        matChannels.Enabled = any && sel!.Value.Kind == "MHO Spec";
        matExport.Enabled = shownMaterials.Count > 0;
        matLayout.Enabled = any && sel!.Value.Kind == "MHO Spec" && tx?.SpecMho != null;
        matFlip.Enabled = any && sel!.Value.Kind == "Normal" && shownMaterials.FirstOrDefault(m => m.Material == sel.Value.Material).Tex?.Normal != null;
        Ui.Lit(matFlip, e?.FlipGreen == true && sel?.Kind == "Normal");
        if (SelectedPackageMap() is { Kind: "Color" }) { matAdjust.Enabled = true; Ui.Lit(matAdjust, false); }   // a package texture: applied to it
    }

    /// <summary>Test: the Materials tab's rows (material | map | from).</summary>
    internal List<string> TestMaterialRows() => matGrid.Rows.Cast<DataGridViewRow>().Select(r => $"{r.Cells[0].Value} | {r.Cells[1].Value} | {r.Cells[2].Value}").ToList();

    /// <summary>Test: Replace File on the first row of <paramref name="kind"/> with <paramref name="file"/>.</summary>
    internal void TestUseMapFile(string kind, string file)
    {
        int row = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[1].Value == kind);
        if (row < 0) return;
        matGrid.CurrentCell = matGrid.Rows[row].Cells[0];
        UseMapFile(file);
    }

    /// <summary>Changes the overrides (made if there are none) and shows the result; the preview's rebuild makes it an undo step.</summary>
    void ChangeOverrides(Action<MaterialOverrides, MaterialOverrides.Entry> change, string material, string what)
    {
        if (OverridesPath() is not string path) return;
        var ov = File.Exists(path) ? MaterialOverrides.Load(path) : new MaterialOverrides();
        if (!ov.Materials.TryGetValue(material, out var e)) ov.Materials[material] = e = new MaterialOverrides.Entry();
        change(ov, e);
        ov.Save(path);
        Log($"Materials: {what}");
        SchedulePreview();
    }

    void UseMapFile() => UseMapFile(null);

    /// <param name="file">The image (tests); null = ask with a file dialog.</param>
    void UseMapFile(string? file)
    {
        if (SelectedPackageMap() is { } pm) { if (file != null) ReplacePackageMap(pm, file); else UsePackageMapFile(pm); return; }
        if (SelectedMap() is not { } sel || OverridesPath() is not string path) return;
        if (file == null)
        {
            using var d = new OpenFileDialog { Title = $"{sel.Kind} map for {sel.Material}", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            file = d.FileName;
        }
        var dlg = (FileName: file, _: 0);
        string dir = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        Protected.CheckWrite(dir);
        Directory.CreateDirectory(dir);
        string name = $"{FbxExport.SafeName(sel.Material)}_{sel.Kind.ToLowerInvariant().Replace(" ", "")}{Path.GetExtension(dlg.FileName).ToLowerInvariant()}";
        File.Copy(dlg.FileName, Path.Combine(dir, name), true);
        string rel = Path.Combine(Path.GetFileName(dir), name);
        ChangeOverrides((_, e) =>
        {
            switch (sel.Kind) { case "Color": e.Color = rel; break; case "Normal": e.Normal = rel; break; case "Spec": e.Spec = rel; break; case "MHO Spec": e.SpecMho = rel; e.SpecLayout = SpecLayouts.FromName(dlg.FileName)?.Id;  /* the copy loses the name the layout was read from */ break; case "Spec Color": e.SpecColor = rel; break; case "Glow": e.Glow = rel; e.GlowOff = false; break; default: e.Alpha = rel; break; }
        }, sel.Material, $"{sel.Material}'s {sel.Kind.ToLowerInvariant()} map is now {Path.GetFileName(dlg.FileName)} (copied into the mod).");
    }

    void MapBackToAutomatic()
    {
        if (SelectedMap() is not { } sel) return;
        ChangeOverrides((_, e) =>
        {
            switch (sel.Kind) { case "Color": e.Color = null; e.Adjust = null; break; case "Normal": e.Normal = null; e.FlipGreen = false; break; case "Spec": e.Spec = null; e.SpecRecipe = null; break; case "MHO Spec": e.SpecMho = null; e.ColorTags = null; e.SpecLayout = null; break; case "Spec Color": e.SpecColor = null; break; case "Glow": e.Glow = null; e.GlowOff = false; break; default: e.Alpha = null; break; }
        }, sel.Material, $"{sel.Material}'s {sel.Kind.ToLowerInvariant()} map is back to automatic.");
    }

    void NextSpecRecipe()
    {
        if (SelectedMap() is not { Kind: "Spec" } sel) return;
        string? now = null;
        ChangeOverrides((_, e) => { e.SpecRecipe = SpecMapGen.Next(e.SpecRecipe); now = e.SpecRecipe; if (now == "soft") e.SpecRecipe = null; }, sel.Material,
            $"{sel.Material}'s generated spec map: next recipe.");
        Log("  " + SpecMapGen.Label(now));
    }

    void FromChannels()
    {
        if (SelectedMap() is not { Kind: "MHO Spec" } sel || OverridesPath() is not string path) return;
        var tex = shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex;
        using var f = new SpecChannelsForm(sel.Material, tex?.SpecMhoAngela);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        string dir = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        string name = $"{FbxExport.SafeName(sel.Material)}_mhospec_channels.png";
        SpecChannels.Combine(f.Channels, f.Rgb, f.KeepCurrent ? tex?.SpecMhoAngela : null, Path.Combine(dir, name));
        string rel = Path.Combine(Path.GetFileName(dir), name);
        ChangeOverrides((_, e) => { e.SpecMho = rel; e.ColorTags = null; e.SpecLayout = null; }, sel.Material,
            $"{sel.Material}'s MHO spec map made from {string.Join(", ", f.Channels.Select((c, i) => c != null ? SpecChannels.Names[i] : null).OfType<string>().Concat(f.Rgb != null ? ["an RGB image"] : []))}.");
    }

    void ShowLayoutMenu()
    {
        if (SelectedMap() is not { Kind: "MHO Spec" } sel || shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex is not { SpecMho: not null } tex) return;
        var menu = new ContextMenuStrip();
        string? chosen = OverridesFile() is string p && MaterialOverrides.Load(p).Materials.TryGetValue(sel.Material, out var e) ? e.SpecLayout : null;
        var auto = SpecLayouts.FromName(tex.SpecMho) ?? SpecLayouts.All[0];
        var ai = new ToolStripMenuItem($"Automatic ({auto.Label})") { Checked = chosen == null };
        ai.Click += (_, _) => SetSpecLayout(sel.Material, null);
        menu.Items.Add(ai);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var l in SpecLayouts.All)
        {
            var it = new ToolStripMenuItem($"{l.Label}: R {l.Channels[0]}, G {l.Channels[1]}, B {l.Channels[2]}, A {l.Channels[3]}") { Checked = chosen == l.Id, ToolTipText = $"{l.Parameter}: {SpecLayouts.Change(l)}" };
            it.Click += (_, _) => SetSpecLayout(sel.Material, l.Id);
            menu.Items.Add(it);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Ui.ShowUnder(menu, matLayout);
    }

    void SetSpecLayout(string material, string? id) =>
        ChangeOverrides((_, e) => e.SpecLayout = id, material, $"{material}'s MHO spec map layout: {(id == null ? "automatic" : SpecLayouts.ById(id)!.Label + ", " + SpecLayouts.Change(SpecLayouts.ById(id)!))}.");

    /// <summary>Test: sets the material's MHO spec layout (as the Layout ▾ menu does).</summary>
    internal void TestSetSpecLayout(string material, string? id) => SetSpecLayout(material, id);

    /// <summary>Adjust Colors: the selected material's color map with hue / saturation / brightness and levels (ColorAdjustForm);
    /// the values go with the material's overrides (one undo step).</summary>
    void AdjustColors()
    {
        if (SelectedPackageMap() is { Kind: "Color" } pm) { AdjustPackageMap(pm); return; }
        if (SelectedMap() is not { } sel || shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex is not { Diffuse: string } tex) return;
        var current = OverridesFile() is string p && MaterialOverrides.Load(p).Materials.TryGetValue(sel.Material, out var oe) ? oe.Adjust : null;
        using var f = new ColorAdjustForm(sel.Material, tex.TagsColor!, current);   // (the map before any adjustment)
        if (f.ShowDialog(this) != DialogResult.OK) return;
        var result = f.Result;
        ChangeOverrides((_, e) => e.Adjust = result, sel.Material,
            result == null ? $"{sel.Material}'s color map is back as it is." : $"{sel.Material}'s color map adjusted: {result.Describe()}.");
    }

    void TagColors()
    {
        if (SelectedMap() is not { } sel || shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex is not { Diffuse: string } tex) return;
        using var f = new ColorTagForm(sel.Material, tex.TagsColor!, tex.ColorTags ?? []);   // (tags are picked on the map before Adjust Colors)
        if (f.ShowDialog(this) != DialogResult.OK) return;
        SetColorTags(sel.Material, f.Result);
    }

    void SetColorTags(string material, List<(string Color, string Tag)> tags) =>
        ChangeOverrides((_, e) => e.ColorTags = tags.Count > 0 ? tags.ToDictionary(x => x.Color, x => x.Tag) : null, material,
            tags.Count > 0 ? $"{material}: {tags.Count} color group(s) tagged; the spec map is made from them." : $"{material}: color tags cleared.");

    /// <summary>Test: tags the material's color groups (as OK in the Tag Colors window does).</summary>
    internal void TestSetColorTags(string material, List<(string Color, string Tag)> tags) => SetColorTags(material, tags);

    /// <summary>The image a map row stands for, as the build makes it: the file, or a made one (generated normal / spec map, MHO
    /// spec map converted or from tags, glow map) written into <paramref name="dir"/>; null = none.</summary>
    static string? MapImage(string kind, Textures tex, string mat, string dir)
    {
        string safe = FbxExport.SafeName(mat);
        switch (kind)
        {
            case "Color": return tex.Diffuse;
            case "Normal":
                if (tex.Normal != null) return tex.NormalFlipGreen ? MaterialOverrides.NormalForGame(tex.Normal, true, dir, safe + "_n_directx") : tex.Normal;
                if (tex.Diffuse == null) return null;
                {
                    Directory.CreateDirectory(dir);
                    var (w, h, px) = NormalMapGen.LoadArgb(tex.Diffuse);
                    int[]? mask = null;
                    if (tex.Alpha != null) { var (aw, ah, apx) = NormalMapGen.LoadArgb(tex.Alpha); if (aw == w && ah == h) mask = apx; }
                    string f = Path.Combine(dir, safe + "_n_generated.png");
                    NormalMapGen.SaveArgb(w, h, NormalMapGen.Make(w, h, px, new NormalMapSettings(), mask), f);
                    return f;
                }
            case "Spec":
                if (tex.UsesMhoSpec) return null;
                return tex.Spec ?? (tex.Diffuse != null ? SpecMapGen.Write(tex.Diffuse, tex.Alpha, tex.SpecRecipe, dir, safe + "_generated") : null);
            case "MHO Spec":
                return tex.SpecMhoAngela ?? (tex.ColorTags is { Count: > 0 } ct && tex.Diffuse != null ? ColorTags.Write(tex.TagsColor!, tex.Alpha, ct, dir, safe) : null);
            case "Spec Color": return tex.SpecColor;
            case "Glow": return tex.GlowFile;
            default: return tex.Alpha;
        }
    }

    static string MapsCache => Path.Combine(Path.GetTempPath(), "MHO_ExtMM_maps");

    /// <summary>Double-click: the row's map, large (Kurt, 2026-10-04), in the image viewer (zoom, pan, Export).</summary>
    async Task ViewMap(int row)
    {
        if (row >= 0 && row < matGrid.Rows.Count && matGrid.CurrentCell?.RowIndex != row) matGrid.CurrentCell = matGrid.Rows[row].Cells["material"];
        // the viewer's Edit in … button (Kurt, 2026-10-06): when this row can be edited and an editor is there
        string? editIn = matEdit.Enabled && ImageEditor.Find() is string ed ? ImageEditor.Describe(ed) : null;
        if (matGrid.Rows[row].Tag is PackageMap pm)
        {
            // a texture of the package itself (no source): decoded from the package
            string png = Path.Combine(MapsCache, "package_" + FbxExport.SafeName(pm.Texture) + "_" + Guid.NewGuid().ToString("N")[..6] + ".png");
            bool made;
            try { made = await Task.Run(() => ExportPackageMap(pm, png)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: {pm.Texture} couldn't be read: {ex.Message}"); return; }
            if (!made || IsDisposed) return;
            bool editPkg;
            using (var pimg = Image.FromFile(png))
            using (var pv = new ImageViewerForm(pimg, $"{pm.Material} · {pm.Kind} ({pm.Texture}, {pimg.Width} × {pimg.Height})", FbxExport.SafeName(pm.Texture), editIn))
            { pv.ShowDialog(this); editPkg = pv.EditRequested; }
            try { File.Delete(png); } catch (IOException) { }
            if (editPkg) EditMapExternally();
            return;
        }
        if (matGrid.Rows[row].Tag is not ValueTuple<string, string> t) return;
        var (mat, kind) = t;
        var tex = shownMaterials.FirstOrDefault(m => m.Material == mat).Tex;
        if (tex == null) return;
        string? file;
        try { file = await Task.Run(() => MapImage(kind, tex, mat, MapsCache)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { Log($"Materials: {mat}'s {kind.ToLowerInvariant()} map couldn't be made: {ex.Message}"); return; }
        if (IsDisposed) return;
        if (file == null) { Log($"Materials: {mat} has no {kind.ToLowerInvariant()} map ({matGrid.Rows[row].Cells["from"].Value})."); return; }
        bool editIt;
        using (var img = Image.FromFile(file))
        using (var v = new ImageViewerForm(img, $"{mat} · {kind} Map ({img.Width} × {img.Height})", $"{FbxExport.SafeName(mat)}_{kind.ToLowerInvariant().Replace(" ", "")}", editIn))
        { v.ShowDialog(this); editIt = v.EditRequested; }
        if (editIt) EditMapExternally();
    }

    /// <summary>Test: the image the row's map stands for (null = none).</summary>
    internal string? TestMapImage(string kind) =>
        shownMaterials.Count > 0 ? MapImage(kind, shownMaterials[0].Tex, shownMaterials[0].Material, MapsCache) : null;

    static readonly (string Kind, string Suffix)[] ExportNames =
        [("Color", ""), ("Normal", "_n"), ("Spec", "_sp"), ("MHO Spec", "_mhospec"), ("Spec Color", "_speccolor"), ("Glow", "_glow"), ("Alpha", "_alpha")];

    /// <summary>Export Maps (Kurt, 2026-10-04): every material's maps as the build makes them, named as the FBX import reads them.</summary>
    /// <param name="folder">The folder (tests); null = ask.</param>
    async Task<int> ExportMaps(string? folder)
    {
        if (shownMaterials.Count == 0) return 0;
        if (folder == null)
        {
            using var d = new FolderBrowserDialog { Description = "Folder for the maps", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (d.ShowDialog(this) != DialogResult.OK) return 0;
            folder = d.SelectedPath;
        }
        Protected.CheckWrite(folder);
        var mats = shownMaterials.ToList();
        matExport.Enabled = false;
        Log($"Materials: exporting the maps of {mats.Count} material(s) to {folder} …");
        int n = 0;
        try
        {
            n = await Task.Run(() =>
            {
                int count = 0;
                string work = Path.Combine(MapsCache, "export");
                foreach (var (mat, tex) in mats)
                    foreach (var (kind, suffix) in ExportNames)
                    {
                        if (MapImage(kind, tex, mat, work) is not string f) continue;
                        string to = Path.Combine(folder, FbxExport.SafeName(mat) + suffix + ".png");
                        if (Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase)) File.Copy(f, to, true);
                        else { using var img = Image.FromFile(f); img.Save(to, System.Drawing.Imaging.ImageFormat.Png); }
                        count++;
                        if (kind == "MHO Spec") SpecChannels.Split(to, folder, FbxExport.SafeName(mat) + suffix);   // (and as gray channels)
                    }
                return count;
            });
            Log($"Materials: {n} map(s) saved in {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Log("Materials: export stopped: " + ex.Message); }
        finally { if (!IsDisposed) matExport.Enabled = shownMaterials.Count > 0; }
        return n;
    }

    /// <summary>Test: Export Maps into <paramref name="folder"/>; the number of maps written.</summary>
    internal Task<int> TestExportMaps(string folder) => ExportMaps(folder);

    void NoGlow()
    {
        if (SelectedMap() is not { Kind: "Glow" } sel) return;
        bool now = false;
        ChangeOverrides((_, e) => { e.GlowOff = !e.GlowOff; now = e.GlowOff; }, sel.Material, $"{sel.Material}'s glow toggled.");
        Log(now ? "  (no glow)" : "  (glow as its maps say)");
    }

    /// <summary>Test: No Glow on the material (as the button).</summary>
    internal void TestNoGlow(string material)
    {
        int row = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[0].Value == material && (string)r.Cells[1].Value == "Glow");
        if (row < 0) return;
        matGrid.CurrentCell = matGrid.Rows[row].Cells[0];
        NoGlow();
    }

    void FlipGreen()
    {
        if (SelectedMap() is not { Kind: "Normal" } sel) return;
        bool now = false;
        ChangeOverrides((_, e) => { e.FlipGreen = !e.FlipGreen; now = e.FlipGreen; }, sel.Material, $"{sel.Material}'s normal map: OpenGL toggled.");
        Log(now ? "  (OpenGL normal map: green flipped to DirectX)" : "  (DirectX normal map: green as the file has it)");
    }
}
