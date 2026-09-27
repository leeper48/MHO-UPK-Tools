namespace UpkMeshScan.Gui;

/// <summary>
/// The Textures tab: the open package's textures (export, details) and "import an image as a new texture" (--import-texture:
/// PNG/JPG/BMP encoded in the tool, or a ready .dds), built on a template texture from the same package.
/// </summary>
sealed partial class MainForm
{
    readonly ListView texList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Label texTemplate = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0), Text = "(select a texture in the list)" };
    readonly TextBox texName = new() { PlaceholderText = "name for the new texture, e.g. my_wall_diff" };
    readonly TextBox texImage = new() { PlaceholderText = "PNG, JPG, BMP or DDS file" };
    readonly ComboBox texFormat = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly NumericUpDown texSplit = Num(85, 1, 254);
    readonly NumericUpDown texScale = Num(1, 0.05m, 4, 2, 0.05m);
    readonly ComboBox texMax = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly CheckBox texNoMips = new() { Text = "No mipmaps (only for something always seen at one distance, e.g. a sky)", AutoSize = true };

    TabPage BuildTexturesTab()
    {
        texList.Columns.Add("Texture", 380); texList.Columns.Add("Size", 90, HorizontalAlignment.Right); texList.Columns.Add("Path", 500);
        texList.SelectedIndexChanged += (_, _) => texTemplate.Text = SelectedTexture() is string t ? t : "(select a texture in the list)";
        texList.Resize += (_, _) => FillLastColumn(texList);

        var listButtons = Flow(
            Btn("Export selected as .dds", () => { if (SelectedTexture() is string t) ExportTextures(t[(t.LastIndexOf('.') + 1)..]); }),
            Btn("Export all textures", () => ExportTextures(null)),
            Btn("Texture details", () => { if (package != null) RunCommand("Texture details", ["--texture-info", packagePath, .. SelectedTexture() is string t ? new[] { t[(t.LastIndexOf('.') + 1)..] } : []], false); }),
            Btn("Open textures folder", () => OpenFolder(Path.Combine(exportFolder.Text, "textures", Path.GetFileNameWithoutExtension(packagePath)))));
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(Lbl("Textures in the open package:"), 0, 0); left.Controls.Add(texList, 0, 1); left.Controls.Add(listButtons, 0, 2);

        texFormat.Items.AddRange(["Automatic (from the image's alpha)", "DXT1: opaque, or cut-out alpha", "DXT5: soft alpha (transparency)"]);
        texFormat.SelectedIndex = 0;
        texMax.Items.AddRange(["Original", "2048", "1024", "512", "256"]);
        texMax.SelectedIndex = 0;
        texImage.Text = settings.TextureImage;
        var g = new FieldGrid();
        g.Full(Hint("Adds a new texture to the open package. It copies its settings (compression type, texture group) from the template: pick a texture of the same kind in " +
                    "the list, e.g. a diffuse for a diffuse. To use the new texture, copy a material on the Objects tab and swap the material's texture for yours " +
                    "(\"Replace references\"). Changes go into the package open at the top.", 560));
        g.Row("Template:", texTemplate);
        g.Row("New name:", texName);
        g.Row("Image:", texImage, FileButton(texImage, "Images (*.png;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.dds|All files|*.*"));
        g.Row("Compression:", texFormat);
        g.Row("Alpha cut-off:", texSplit);
        g.Full(Hint("DXT1 with alpha keeps pixels whose alpha is above the cut-off and drops the rest (85 = the game's masked-material threshold). Ignored for DXT5 and opaque images.", 560));
        g.Row("Brightness:", texScale);
        g.Full(Hint("Multiplies the colour (0.6 = 40% darker), e.g. to match a baked texture to the real one.", 560));
        g.Row("Largest size:", texMax);
        g.Full(Hint("Halves the image until it fits. For distant LODs 512 is plenty; for anything seen up close keep Original.", 560));
        g.Full(texNoMips);
        g.Full(NoWrap(
            Btn("Check (dry run)", () => ImportTexture(dryRun: true)),
            Btn("Import into game file…", () => ImportTexture(dryRun: false))));

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left);
        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        right.Controls.Add(Section("Import an image as a new texture", g));
        split.Panel2.Controls.Add(right);
        var page = new TabPage("Textures");
        page.Controls.Add(split);
        Shown += (_, _) => { if (split.Width > 600) split.SplitterDistance = (int)(split.Width * 0.5); };
        return page;
    }

    void FillTextures()
    {
        texList.BeginUpdate();
        texList.Items.Clear();
        if (package != null)
            for (int i = 0; i < package.Exports.Length; i++)
            {
                var e = package.Exports[i];
                if (!package.ClassOf(e).Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) continue;
                texList.Items.Add(new ListViewItem([e.ObjectName, e.SerialSize.ToString("N0"), package.PathOf(e)]) { Tag = package.PathOf(e) });
            }
        texList.EndUpdate();
        FillLastColumn(texList);
        texTemplate.Text = "(select a texture in the list)";
    }

    string? SelectedTexture() => texList.SelectedItems.Count == 1 ? (string)texList.SelectedItems[0].Tag! : null;

    void ExportTextures(string? filter)
    {
        if (package == null) { Log("Open a package first."); return; }
        string dir = Path.Combine(exportFolder.Text, "textures", Path.GetFileNameWithoutExtension(packagePath));
        Run(filter == null ? "Export all textures" : $"Export texture {filter}", () => TextureExport.Run(packagePath, filter, dir), () => Log($"Written to {dir}"));
    }

    void ImportTexture(bool dryRun)
    {
        if (package == null) { Log("Open the package that should get the texture first."); return; }
        if (SelectedTexture() is not string template) { Log("Select a template texture in the list first."); return; }
        string name = texName.Text.Trim(), image = texImage.Text.Trim();
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c == '.')) { Log("Give the new texture a name without spaces or dots."); return; }
        if (!File.Exists(image)) { Log($"Image not found: {image}"); return; }
        settings.TextureImage = image;
        var args = new List<string> { "--import-texture", packagePath, template, name, image,
            "--split", F(texSplit.Value), "--scale", F(texScale.Value) };
        if (texFormat.SelectedIndex == 1) args.AddRange(["--format", "dxt1"]);
        if (texFormat.SelectedIndex == 2) args.AddRange(["--format", "dxt5"]);
        if (texMax.SelectedItem is string m && int.TryParse(m, out _)) args.AddRange(["--max-size", m]);
        if (texNoMips.Checked) args.Add("--no-mips");
        if (dryRun) args.Add("--dry-run");
        else if (!Confirm($"Add texture {name} (from {Path.GetFileName(image)}) to {Path.GetFileName(packagePath)}?\n\nThe original package is kept as .bak; Undo on the Backups tab takes it back.")) return;
        RunCommand(dryRun ? $"Texture dry run {name}" : $"Import texture {name}", [.. args], writes: !dryRun, after: () => { if (!dryRun) ReopenPackage(); });
    }
}
