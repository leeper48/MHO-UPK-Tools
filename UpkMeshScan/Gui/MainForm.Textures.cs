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
    readonly TextureViewer texViewer = new() { Dock = DockStyle.Fill };
    readonly RadioButton texModeReplace = new() { Text = "Replace the selected texture", AutoSize = true, Checked = true };
    readonly RadioButton texModeNew = new() { Text = "Add as a new texture", AutoSize = true };
    readonly Label texModeHint = Hint("", 480);
    Control? texNameLabel;

    /// <summary>Replace (the selected texture gets the image) or add (a new texture, the selected one as its template).</summary>
    void UpdateTextureMode()
    {
        // A replacement keeps the texture's own name: the name row only shows when adding.
        texName.Visible = texModeNew.Checked;
        if (texNameLabel != null) texNameLabel.Visible = texModeNew.Checked;
        texModeHint.Text = texModeNew.Checked
            ? "Adds a new texture to the open package. It copies its settings (compression type, texture group) from the selected texture, the template: pick " +
              "one of the same kind, e.g. a diffuse for a diffuse. To use it, copy a material on the Objects tab and swap the material's texture for yours (\"Replace references\")."
            : "The selected texture gets your image: same name and path, so every material that uses it shows the new image. Its other settings are kept. " +
              "Other packages may hold their own copy of the same texture: \"Find name in folder\" on the Browse tab lists them.";
    }
    int texRequest;                                                        // the newest preview asked for; older ones are dropped
    readonly CheckBox texNoMips = new() { Text = "No mipmaps (only for something always seen at one distance, e.g. a sky)", AutoSize = true };

    TabPage BuildTexturesTab()
    {
        texList.Columns.Add("Texture", 380); texList.Columns.Add("Size", 90, HorizontalAlignment.Right); texList.Columns.Add("Path", 500);
        texList.SelectedIndexChanged += (_, _) =>
        {
            texTemplate.Text = SelectedTexture() is string t ? t : "(select a texture in the list)";
            if (SelectedTexture() is string sel) PreviewTexture(sel);
        };
        texList.Resize += (_, _) => FillLastColumn(texList);
        texViewer.PopOut = (n, px, w, h, info) => TexturePreviewForm.Open(this, palette, n, px, w, h, info);

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
        texModeNew.CheckedChanged += (_, _) => UpdateTextureMode();
        g.Full(texModeReplace);
        g.Full(texModeNew);
        g.Full(texModeHint);
        g.Row("Texture:", texTemplate);
        g.Row("New name:", texName);
        texNameLabel = g.GetControlFromPosition(0, g.GetRow(texName));
        g.Row("Image:", texImage, NoWrap(FileButton(texImage, "Images (*.png;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.dds|All files|*.*"),
            Btn("Preview", () => PreviewImageFile(texImage.Text.Trim()))));
        g.Row("Compression:", texFormat);
        g.Row("Alpha cut-off:", texSplit);
        g.Full(Hint("DXT1 with alpha keeps pixels whose alpha is above the cut-off and drops the rest (85 = the game's masked-material threshold). Ignored for DXT5 and opaque images.", 480));
        g.Row("Brightness:", texScale);
        g.Full(Hint("Multiplies the colour (0.6 = 40% darker), e.g. to match a baked texture to the real one.", 480));
        g.Row("Largest size:", texMax);
        g.Full(Hint("Halves the image until it fits. For distant LODs 512 is plenty; for anything seen up close keep Original.", 480));
        g.Full(texNoMips);
        g.Full(NoWrap(
            Btn("Check (dry run)", () => ImportTexture(dryRun: true)),
            Btn("Import into game file…", () => ImportTexture(dryRun: false))));

        // Left: the list above the importer; right: the viewer, showing whatever was clicked.
        var import = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        import.Controls.Add(Section("Import an image", g));
        UpdateTextureMode();
        var leftSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        leftSplit.Panel1.Controls.Add(left);
        leftSplit.Panel2.Controls.Add(import);
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(texViewer);
        var page = new TabPage("Textures");
        page.Controls.Add(split);
        Shown += (_, _) =>
        {
            if (split.Width > 600) split.SplitterDistance = (int)(split.Width * 0.42);
            if (leftSplit.Height > 400) leftSplit.SplitterDistance = (int)(leftSplit.Height * 0.45);
        };
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
        FitColumns(texList);
        texTemplate.Text = "(select a texture in the list)";
        texRequest++;
        texViewer.ShowMessage(texList.Items.Count == 0 ? "This package has no textures." : "Click a texture in the list to see it here.");
    }

    string? SelectedTexture() => texList.SelectedItems.Count == 1 ? (string)texList.SelectedItems[0].Tag! : null;

    /// <summary>
    /// A texture of the open package in the viewer: its largest mip, from the package or the game's .tfc caches, decoded in
    /// the background (a click on the next texture before it's done wins).
    /// </summary>
    void PreviewTexture(string path)
    {
        if (package == null) return;
        var pkg = package;
        int i = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        int request = ++texRequest;
        string name = pkg.Exports[i].ObjectName, folder = gameFolder.Text;
        texViewer.ShowMessage($"Loading {name}...");
        Task.Run<(byte[]? Px, int W, int H, string Info)>(() =>
        {
            try
            {
                if (TextureExport.ReadBestMip(pkg, i, out string note, folder) is not { } m) return (null, 0, 0, note);
                if (TextureDecode.ToBgra(m.Format, m.Width, m.Height, m.Pixels, out string why) is not byte[] px) return (null, 0, 0, why);
                string size = m.Width < m.FullWidth ? $" (full size {m.FullWidth} x {m.FullHeight} isn't stored anywhere)" : "";
                return (px, m.Width, m.Height, $"{m.Format.Replace("PF_", "")}, {note}{size}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or IndexOutOfRangeException or ArgumentException)
            { return (null, 0, 0, $"couldn't read it: {ex.Message}"); }
        }).ContinueWith(t =>
        {
            if (request != texRequest) return;                              // another texture was clicked meanwhile
            var r = t.Result;
            if (r.Px == null) texViewer.ShowMessage($"{name}: {r.Info}");
            else texViewer.ShowImage(name, r.Px, r.W, r.H, r.Info);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A texture from another tab (Browse): the Textures tab with it selected and shown.</summary>
    void ShowTextureOnTab(string path)
    {
        tabs.SelectedTab = texturesPage;
        foreach (ListViewItem item in texList.Items)
            if (string.Equals((string)item.Tag!, path, StringComparison.OrdinalIgnoreCase)) { item.Selected = true; item.Focused = true; item.EnsureVisible(); return; }
        PreviewTexture(path);
    }

    /// <summary>An image file (PNG, JPG, BMP, or a .dds's first mip) in the viewer, e.g. before importing it.</summary>
    void PreviewImageFile(string file)
    {
        if (!File.Exists(file)) { Log($"Image not found: {file}"); return; }
        texRequest++;
        try
        {
            if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                if (TextureDecode.ReadDds(file, out string note) is not { } d) { texViewer.ShowMessage($"{Path.GetFileName(file)}: {note}"); return; }
                if (TextureDecode.ToBgra(d.Format, d.W, d.H, d.Data, out string why) is not byte[] px) { texViewer.ShowMessage($"{Path.GetFileName(file)}: {why}"); return; }
                texViewer.ShowImage(Path.GetFileNameWithoutExtension(file), px, d.W, d.H, $"{d.Format}, {file}");
            }
            else
            {
                using var bmp = new Bitmap(file);
                texViewer.ShowImage(Path.GetFileNameWithoutExtension(file), TextureDecode.FromBitmap(bmp), bmp.Width, bmp.Height, file);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or OutOfMemoryException) { texViewer.ShowMessage($"{Path.GetFileName(file)}: couldn't read it: {ex.Message}"); }
    }

    void ExportTextures(string? filter)
    {
        if (package == null) { Log("Open a package first."); return; }
        string dir = Path.Combine(exportFolder.Text, "textures", Path.GetFileNameWithoutExtension(packagePath));
        Run(filter == null ? "Export all textures" : $"Export texture {filter}", () => TextureExport.Run(packagePath, filter, dir), () => Log($"Written to {dir}"));
    }

    void ImportTexture(bool dryRun)
    {
        if (package == null) { Log("Open the package that should get the texture first."); return; }
        bool replace = texModeReplace.Checked;
        if (SelectedTexture() is not string template) { Log(replace ? "Select the texture to replace in the list first." : "Select a template texture in the list first."); return; }
        string name = replace ? template[(template.LastIndexOf('.') + 1)..] : texName.Text.Trim(), image = texImage.Text.Trim();
        if (!replace && (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c == '.'))) { Log("Give the new texture a name without spaces or dots."); return; }
        if (!File.Exists(image)) { Log($"Image not found: {image}"); return; }
        settings.TextureImage = image;
        var args = replace
            ? new List<string> { "--replace-texture", packagePath, template, image, "--split", F(texSplit.Value), "--scale", F(texScale.Value) }
            : new List<string> { "--import-texture", packagePath, template, name, image, "--split", F(texSplit.Value), "--scale", F(texScale.Value) };
        if (texFormat.SelectedIndex == 1) args.AddRange(["--format", "dxt1"]);
        if (texFormat.SelectedIndex == 2) args.AddRange(["--format", "dxt5"]);
        if (texMax.SelectedItem is string m && int.TryParse(m, out _)) args.AddRange(["--max-size", m]);
        if (texNoMips.Checked) args.Add("--no-mips");
        if (dryRun) args.Add("--dry-run");
        else if (!Confirm(replace
            ? $"Replace texture {name} in {Path.GetFileName(packagePath)} with {Path.GetFileName(image)}?\n\nThe original package is kept as .bak; Undo on the Backups tab takes it back."
            : $"Add texture {name} (from {Path.GetFileName(image)}) to {Path.GetFileName(packagePath)}?\n\nThe original package is kept as .bak; Undo on the Backups tab takes it back.")) return;
        RunCommand(dryRun ? $"Texture dry run {name}" : replace ? $"Replace texture {name}" : $"Import texture {name}", [.. args], writes: !dryRun, after: () =>
        {
            if (dryRun) return;
            ReopenPackage();
            // Show the result: the same texture, now with the new image.
            foreach (ListViewItem item in texList.Items)
                if (((string)item.Tag!).EndsWith("." + name, StringComparison.OrdinalIgnoreCase)) { item.Selected = true; item.EnsureVisible(); break; }
        });
    }
}
