using MhoExtendedModManager.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// The Materials tab (Kurt, 2026-10-04): each material's color, normal, spec and alpha maps and where they come from (found
/// with the model, generated, none, or your file), with Use a File (an override, copied into the mod's Model folder), Back to
/// Automatic and Flip Green (an OpenGL-style normal map). Saved per source in materials\&lt;source&gt;.json (MaterialOverrides);
/// the preview, Build and Export FBX use it, and Undo takes a change back.
/// </summary>
sealed partial class ModelPage
{
    readonly DataGridView matGrid = new() { Dock = DockStyle.Fill };
    Button matUse = null!, matAuto = null!, matFlip = null!, matRecipe = null!, matTags = null!;
    List<(string Material, Textures Tex)> shownMaterials = [];

    static readonly (string Label, ModelView.MapView Mode)[] ShowMapChoices =
        [("All Maps", ModelView.MapView.All), ("Color Map", ModelView.MapView.Colour), ("Normal Map", ModelView.MapView.Normal),
         ("Spec: Shine (R)", ModelView.MapView.Spec), ("Spec: Power (G)", ModelView.MapView.SpecPower), ("Spec: Reflectivity (A)", ModelView.MapView.Reflectivity),
         ("Spec: Skin Mask (B)", ModelView.MapView.SkinMask), ("Spec Color", ModelView.MapView.SpecColor), ("Alpha", ModelView.MapView.Alpha)];

    static readonly string[] MapKinds = ["Color", "Normal", "Spec", "MHO Spec", "Spec Color", "Alpha"];

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
        Ui.Tip(matGrid, "Each material's maps and where they come from. Pick a row, then Use a File to put in your own (double-click does the same).");
        matGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) UseMapFile(); };
        matGrid.CurrentCellChanged += (_, _) => MatSelectionChanged();
        matUse = Ui.FlatButton("Use a File", UseMapFile, "Puts your own image (PNG, JPG or BMP) in for the selected map: copied into the mod's Model folder; the preview, Build and Export FBX use it. Undo (Ctrl+Z) takes it back. MHO Spec takes a map in the game's own packed layout (R shine, G spec power, B skin mask, A reflectivity), put in as it is with Angela's armor material; Spec Color tints its highlights.");
        matAuto = Ui.FlatButton("Back to Automatic", MapBackToAutomatic, "Forgets your file for the selected map (a Normal row: the green flip too); the importer's own choice is used again.");
        matFlip = Ui.FlatButton("Flip Green", FlipGreen, "For a normal map made the OpenGL way (Blender, Unity, Substance's OpenGL preset: green up): flips its green to MHO's DirectX way. Bumps that look dented instead of raised need it.");
        matRecipe = Ui.FlatButton("Next Recipe", NextSpecRecipe, "A spec row with no map of its own gets one made from the color map; this steps through the ways it's made: Soft, Strong, Dark Is Shiny, Detail, Flat. Preview Shows → Spec Map (above) shows it.");
        matTags = Ui.FlatButton("Tag Colors", TagColors, "Tell it what each color group of the selected material is made of (Metal, Skin, Leather, Cloth): an MHO spec map is made from your tags, with Angela's armor material. Colors alone can't tell gold paint from gold metal.");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { matUse, matAuto, matFlip, matRecipe, matTags }) { b.Margin = new Padding(0, 0, 6, 0); buttons.Controls.Add(b); }
        // what the preview shows (Kurt, 2026-10-04: moved here from Look ▾): the model lit, or one map on its own
        var showMap = new DropDown { Width = (int)(200 * DeviceDpi / 96f), Margin = new Padding(0, 0, 0, 0) };
        showMap.Items.AddRange([.. ShowMapChoices.Select(x => (object)x.Label)]);
        showMap.SelectedIndex = Math.Max(0, Array.FindIndex(ShowMapChoices, x => x.Mode == preview.ShowMap));
        showMap.SelectedIndexChanged += (_, _) => { if (showMap.SelectedIndex >= 0) preview.ShowMap = ShowMapChoices[showMap.SelectedIndex].Mode; };
        Ui.Tip(showMap, "What the preview shows: All Maps is the model lit as usual; the others show one map or channel on its own. The spec map's channels: Shine (R: how strong the highlight is), Power (G: how tight and sharp it is), Reflectivity (A: how much the environment reflects, what makes metal look like metal), Skin Mask (B: where skin shading applies); Spec Color tints the highlight (white: none of its own). White = most; black = none or a channel the material doesn't use. Color Map unlit, Normal Map its own colors (flat blue: none), Alpha (black is cut out).");
        var showRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        showRow.Controls.Add(new Label { Text = "Preview Shows", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        showRow.Controls.Add(showMap);
        var head = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        head.Controls.Add(showRow, 0, 0); head.Controls.Add(buttons, 0, 1);
        MatSelectionChanged();
        return Column("", head, matGrid);
    }

    /// <summary>The material list as the preview made it (after the overrides).</summary>
    void FillMaterials()
    {
        int keep = matGrid.CurrentCell?.RowIndex ?? -1;
        matGrid.Rows.Clear();
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
        MatSelectionChanged();
    }

    /// <summary>Where a map comes from, in words, and its file.</summary>
    static (string From, string? Path) Describe(string kind, Textures tex, MaterialOverrides.Entry? e)
    {
        string? mine = kind switch { "Color" => e?.Color, "Normal" => e?.Normal, "Spec" => e?.Spec, "MHO Spec" => e?.SpecMho, "Spec Color" => e?.SpecColor, _ => e?.Alpha };
        string? file = kind switch { "Color" => tex.Diffuse, "Normal" => tex.Normal, "Spec" => tex.Spec, "MHO Spec" => tex.SpecMho, "Spec Color" => tex.SpecColor, _ => tex.Alpha };
        if (kind == "Spec" && tex.UsesMhoSpec) return ("Not used: the MHO Spec map is", null);
        if (kind == "MHO Spec" && mine == null && tex.SpecMho == null && tex.ColorTags is { Count: > 0 } ct)
            return ($"Made from your color tags: {string.Join(", ", ct.GroupBy(x => x.Tag).Select(g => $"{g.Count()} {ColorTags.Label(g.Key).ToLowerInvariant()}"))}", null);
        string flip = kind == "Normal" && tex.NormalFlipGreen ? " · green flipped" : "";
        if (mine != null) return ($"Your file: {System.IO.Path.GetFileName(file ?? mine)}{flip}", file);
        if (file != null) return ((kind == "Color" && tex.Guessed ? "Found (by a near name): " : kind == "Normal" ? "The model's own: " : "Found: ") + System.IO.Path.GetFileName(file) + flip, file);
        return (kind switch
        {
            "Color" => "None: gray",
            "Normal" => tex.Diffuse != null ? "Generated from the color map" : "None: flat",
            "Spec" => tex.Diffuse != null ? "Generated from the color map: " + SpecMapGen.Label(tex.SpecRecipe) : "None: low shine, no reflection",
            "MHO Spec" => "None (an MHO packed map: shine, power, skin, reflectivity)",
            "Spec Color" => "None: the highlight takes the color map's tint",
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
        var e = sel is { } s && OverridesFile() is string p && MaterialOverrides.Load(p).Materials.TryGetValue(s.Material, out var x) ? x : null;
        matAuto.Enabled = any && e != null && (sel!.Value.Kind switch { "Color" => e.Color != null, "Normal" => e.Normal != null || e.FlipGreen, "Spec" => e.Spec != null || e.SpecRecipe != null, "MHO Spec" => e.SpecMho != null, "Spec Color" => e.SpecColor != null, _ => e.Alpha != null });
        var tx = sel is { } s2 ? shownMaterials.FirstOrDefault(m => m.Material == s2.Material).Tex : null;
        matRecipe.Enabled = any && sel!.Value.Kind == "Spec" && tx is { Spec: null, SpecMho: null, Diffuse: not null } && tx.ColorTags is not { Count: > 0 };
        matTags.Enabled = any && tx is { Diffuse: not null, SpecMho: null };
        matFlip.Enabled = any && sel!.Value.Kind == "Normal" && shownMaterials.FirstOrDefault(m => m.Material == sel.Value.Material).Tex?.Normal != null;
        Ui.Lit(matFlip, e?.FlipGreen == true && sel?.Kind == "Normal");
    }

    /// <summary>Test: the Materials tab's rows (material | map | from).</summary>
    internal List<string> TestMaterialRows() => matGrid.Rows.Cast<DataGridViewRow>().Select(r => $"{r.Cells[0].Value} | {r.Cells[1].Value} | {r.Cells[2].Value}").ToList();

    /// <summary>Test: Use a File on the first row of <paramref name="kind"/> with <paramref name="file"/>.</summary>
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
            switch (sel.Kind) { case "Color": e.Color = rel; break; case "Normal": e.Normal = rel; break; case "Spec": e.Spec = rel; break; case "MHO Spec": e.SpecMho = rel; break; case "Spec Color": e.SpecColor = rel; break; default: e.Alpha = rel; break; }
        }, sel.Material, $"{sel.Material}'s {sel.Kind.ToLowerInvariant()} map is now {Path.GetFileName(dlg.FileName)} (copied into the mod).");
    }

    void MapBackToAutomatic()
    {
        if (SelectedMap() is not { } sel) return;
        ChangeOverrides((_, e) =>
        {
            switch (sel.Kind) { case "Color": e.Color = null; break; case "Normal": e.Normal = null; e.FlipGreen = false; break; case "Spec": e.Spec = null; e.SpecRecipe = null; break; case "MHO Spec": e.SpecMho = null; e.ColorTags = null; break; case "Spec Color": e.SpecColor = null; break; default: e.Alpha = null; break; }
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

    void TagColors()
    {
        if (SelectedMap() is not { } sel || shownMaterials.FirstOrDefault(m => m.Material == sel.Material).Tex is not { Diffuse: string color } tex) return;
        using var f = new ColorTagForm(sel.Material, color, tex.ColorTags ?? []);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        SetColorTags(sel.Material, f.Result);
    }

    void SetColorTags(string material, List<(string Color, string Tag)> tags) =>
        ChangeOverrides((_, e) => e.ColorTags = tags.Count > 0 ? tags.ToDictionary(x => x.Color, x => x.Tag) : null, material,
            tags.Count > 0 ? $"{material}: {tags.Count} color group(s) tagged; the spec map is made from them." : $"{material}: color tags cleared.");

    /// <summary>Test: tags the material's color groups (as OK in the Tag Colors window does).</summary>
    internal void TestSetColorTags(string material, List<(string Color, string Tag)> tags) => SetColorTags(material, tags);

    void FlipGreen()
    {
        if (SelectedMap() is not { Kind: "Normal" } sel) return;
        bool now = false;
        ChangeOverrides((_, e) => { e.FlipGreen = !e.FlipGreen; now = e.FlipGreen; }, sel.Material, $"{sel.Material}'s normal map green flip toggled.");
        Log(now ? "  (green flipped: OpenGL → DirectX)" : "  (green as the file has it)");
    }
}
