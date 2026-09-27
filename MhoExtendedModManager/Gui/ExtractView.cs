using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Extract (MHModManager's Extract window): the main window's Extract tab, in its look: stock icon / achievement / store images as .dds,
/// and a language's original strings as .json in the mod format, both from the verified originals, as starting points
/// for new mods.
/// </summary>
sealed class ExtractView : UserControl
{
    readonly StockCatalog catalog;
    readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox search = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
    readonly NameList names = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, Font = Ui.Regular(9.5f) };
    readonly PictureBox pic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24) };
    readonly Label info = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 0) };
    readonly ComboBox lang = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" };
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    List<TexEntry> all = [];

    public Control[] Bars { get; private set; } = [];

    public ExtractView(StockCatalog catalog)
    {
        this.catalog = catalog;
        Dock = DockStyle.Fill;
        Font = Ui.Regular(9.5f);
        float s = DeviceDpi / 96f;
        kind.Width = (int)(200 * s); lang.Width = (int)(110 * s);

        // ---- Textures
        // The three MHModManager packages, then every other stock icon package (Silver Surfer, HD, character select, …).
        foreach (string k in new[] { "Icons", "Achievement icons", "Store images" }) kind.Items.Add(k);
        foreach (string p in IconCapture.ExtraPackages(catalog.Game)) kind.Items.Add(p);
        var tex = new Panel { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
        bar.Controls.AddRange([new Label { Text = "Package", AutoSize = true, Padding = new Padding(0, 8, 4, 0), Tag = "subtle" }, kind,
            Ui.AccentButton("Save selected as .dds…", () => SaveTextures(".dds")), Ui.FlatButton("Save selected as .png…", () => SaveTextures(".png"))]);
        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = (int)(380 * s), ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 0, 8, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
        searchRow.Controls.Add(search, 1, 0);
        left.Controls.Add(searchRow, 0, 0); left.Controls.Add(names, 0, 1);
        var preview = Ui.CardPanel("PREVIEW (ORIGINAL)", pic, info);
        tex.Controls.Add(preview); tex.Controls.Add(left); tex.Controls.Add(bar);
        tex.Controls.Add(new Label { Text = "The game's own images, from the verified originals (not the modded live files). Select one or more (Ctrl / Shift), then save: a starting point for a replacement.", Dock = DockStyle.Top, AutoSize = true, Tag = "subtle", Padding = new Padding(2, 8, 2, 2) });
        tabs.Add("Textures", tex);

        // ---- Strings
        var str = new Panel { Dock = DockStyle.Fill };
        var sbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
        sbar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 8, 4, 0), Tag = "subtle" }, lang, Ui.AccentButton("Save all as .json…", SaveStrings)]);
        str.Controls.Add(sbar);
        str.Controls.Add(new Label { Text = "Every original string of a language, in the mod format ({ file: { id: { String … } } }). Edit the ones you want, then use Import changes (.json) on the mod editor's Strings tab, or copy entries into a mod's <lang>.json.", Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size((int)(1150 * s), 0), Tag = "subtle", Padding = new Padding(2, 8, 2, 2) });
        tabs.Add("Strings", str);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 0) };
        body.Controls.Add(tabs);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(12, 8, 12, 8) };
        bottom.Controls.Add(status, 0, 0);
        Controls.Add(body); Controls.Add(bottom);
        Bars = [bottom];

        kind.SelectedIndexChanged += async (_, _) =>
        {
            names.Items.Clear(); names.Items.Add("Loading…");
            int view = kind.SelectedIndex;
            string? extraPkg = view >= 3 ? kind.SelectedItem as string : null;
            var t = await Task.Run(() => extraPkg != null ? catalog.EntriesFor(extraPkg) : catalog.Entries(view));
            all = t ?? [];
            Filter();
            if (t == null) names.Items.Add("(no verified original of this package)");
        };
        search.TextChanged += (_, _) => Filter();
        names.SelectedIndexChanged += async (_, _) =>
        {
            if (names.SelectedItem is not TexEntry e) return;
            string n = e.Name, pkg = e.File;
            var p = await Task.Run(() => catalog.Preview(pkg, n));
            pic.Image = p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null;
            info.Text = p is { } y ? $"{n}  ·  {y.W}×{y.H} {y.Format.Replace("pf_", "").Replace("PF_", "").ToUpperInvariant()}" : $"{n}  ·  no preview";
        };
        kind.SelectedIndex = 0;
        foreach (string l in catalog.Languages()) lang.Items.Add(l);
        lang.SelectedItem = lang.Items.Contains("eng") ? "eng" : lang.Items.Count > 0 ? lang.Items[0] : null;
        status.Text = "Nothing is written to the game folder here.";
    }

    void Filter()
    {
        string q = search.Text.Trim();
        names.BeginUpdate(); names.Items.Clear();
        foreach (var e in all.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5000)) names.Items.Add(e);
        names.EndUpdate();
    }

    /// <summary>Snapshot hook: show a package kind (0 icons, 1 achievements, 2 store) and select a texture in it.</summary>
    public async Task ShowForSnapshot(int kindIndex, string texture)
    {
        kind.SelectedIndex = kindIndex;
        await Task.Delay(4000);
        int i = names.Items.Cast<object>().ToList().FindIndex(o => o is TexEntry e && e.Name == texture);
        if (i >= 0) { names.ClearSelected(); names.SelectedIndex = i; names.TopIndex = Math.Max(0, i - 3); }
        await Task.Delay(2500);
    }

    async void SaveTextures(string ext)
    {
        var sel = names.SelectedItems.OfType<TexEntry>().ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select one or more textures first.", "Extract"); return; }
        string Pkg(TexEntry e) => e.File;
        string? folder;
        if (sel.Count == 1)
        {
            using var d = new SaveFileDialog { Title = "Save original", Filter = ext == ".png" ? "PNG image (*.png)|*.png" : "DDS texture (*.dds)|*.dds", FileName = sel[0].Name + ext };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string? why = catalog.ExportImage(Pkg(sel[0]), sel[0].Name, d.FileName);
            status.Text = why == null ? $"Saved {d.FileName}" : $"Not saved: {why}";
            return;
        }
        using (var d = new FolderBrowserDialog { Description = $"Folder for {sel.Count} textures", UseDescriptionForTitle = true })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            folder = d.SelectedPath;
        }
        UseWaitCursor = true;
        var failed = await Task.Run(() => sel.Select(e => (n: e.Name, why: catalog.ExportImage(Pkg(e), e.Name, Path.Combine(folder, e.Name + ext)))).Where(x => x.why != null).ToList());
        UseWaitCursor = false;
        status.Text = $"Saved {sel.Count - failed.Count} of {sel.Count} to {folder}" + (failed.Count > 0 ? $" (not: {string.Join(", ", failed.Select(f => f.n))})" : "");
    }

    async void SaveStrings()
    {
        if (lang.SelectedItem is not string l) return;
        using var d = new SaveFileDialog { Title = "Save strings", Filter = "JSON (*.json)|*.json", FileName = $"{l}_strings.json" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        UseWaitCursor = true; status.Text = "Saving…";
        int n = await Task.Run(() => catalog.ExportStrings(l, d.FileName));
        UseWaitCursor = false;
        status.Text = $"Saved {n:N0} {l} strings to {d.FileName}";
    }
}
