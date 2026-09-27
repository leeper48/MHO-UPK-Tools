using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Extract (MHModManager's Extract window): stock icon / achievement / store images as .dds, and a language's original
/// strings as .json in the mod format, both from the verified originals, as starting points for new mods.
/// </summary>
sealed class ExtractForm : Form
{
    readonly StockCatalog catalog;
    readonly ComboBox kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly TextBox search = new() { Width = 380 };
    readonly ListBox names = new() { Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
    readonly PictureBox pic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 18) };
    readonly Label info = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
    readonly ComboBox lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
    List<string> all = [];

    public ExtractForm(StockCatalog catalog)
    {
        this.catalog = catalog;
        Text = "Extract originals";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 1200; Height = 800; StartPosition = FormStartPosition.CenterParent;
        var tabs = new ThemedTabControl { Dock = DockStyle.Fill };

        // Textures
        var tex = new TabPage("Textures");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        foreach (string k in new[] { "Icons", "Achievement icons", "Store images" }) kind.Items.Add(k);
        var save = new Button { Text = "Save selected as .dds…", AutoSize = true };
        save.Click += (_, _) => SaveTextures();
        bar.Controls.AddRange([new Label { Text = "Package", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, kind,
            new Label { Text = "Find", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, search, save]);
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(names);
        split.Panel2.Controls.Add(pic); split.Panel2.Controls.Add(info);
        tex.Controls.Add(split); tex.Controls.Add(bar);
        tex.Controls.Add(new Label { Text = "The game's own images (from the verified originals, not the modded live files). Select one or more (Ctrl/Shift), then save; use them as the starting point for a replacement.", Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(1150, 0), Padding = new Padding(6) });
        tabs.TabPages.Add(tex);

        // Strings
        var str = new TabPage("Strings");
        var sbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var saveStr = new Button { Text = "Save all as .json…", AutoSize = true };
        saveStr.Click += (_, _) => SaveStrings();
        sbar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, lang, saveStr]);
        str.Controls.Add(new Label { Text = "Every original string of a language, in the mod format ({ file: { id: { String … } } }). Edit the ones you want and use Import changes (.json) in the mod editor's Strings tab, or copy entries into a mod's <lang>.json.", Dock = DockStyle.Fill, Padding = new Padding(6) });
        str.Controls.Add(sbar);
        tabs.TabPages.Add(str);

        Controls.Add(tabs); Controls.Add(status);
        kind.SelectedIndexChanged += async (_, _) =>
        {
            names.Items.Clear(); names.Items.Add("Loading…");
            string pkg = Applier.IconPackages[kind.SelectedIndex].File;
            var t = await Task.Run(() => catalog.Textures(pkg));
            all = t?.Keys.ToList() ?? [];
            Filter();
            if (t == null) names.Items.Add("(no verified original of this package)");
        };
        search.TextChanged += (_, _) => Filter();
        names.SelectedIndexChanged += async (_, _) =>
        {
            if (names.SelectedItem is not string n || n.StartsWith('(') || n == "Loading…") return;
            string pkg = Applier.IconPackages[kind.SelectedIndex].File;
            var p = await Task.Run(() => catalog.Preview(pkg, n));
            pic.Image = p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null;
            info.Text = p is { } y ? $"{n}: {y.W}x{y.H} {y.Format}" : $"{n}: no preview";
        };
        Load += (_, _) =>
        {
            Theme.Apply(this, Palette.Dark);
            kind.SelectedIndex = 0;
            foreach (string l in catalog.Languages()) lang.Items.Add(l);
            lang.SelectedItem = lang.Items.Contains("eng") ? "eng" : lang.Items.Count > 0 ? lang.Items[0] : null;
        };
    }

    void Filter()
    {
        string q = search.Text.Trim();
        names.BeginUpdate(); names.Items.Clear();
        foreach (string n in all.Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5000)) names.Items.Add(n);
        names.EndUpdate();
    }

    async void SaveTextures()
    {
        var sel = names.SelectedItems.Cast<string>().Where(n => !n.StartsWith('(') && n != "Loading…").ToList();
        if (sel.Count == 0) { MessageBox.Show(this, "Select one or more textures first.", Text); return; }
        string pkg = Applier.IconPackages[kind.SelectedIndex].File;
        string? folder;
        if (sel.Count == 1)
        {
            using var d = new SaveFileDialog { Title = "Save original", Filter = "DDS texture (*.dds)|*.dds", FileName = sel[0] + ".dds" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string? why = catalog.ExportDds(pkg, sel[0], d.FileName);
            status.Text = why == null ? $"Saved {d.FileName}" : $"Not saved: {why}";
            return;
        }
        using (var d = new FolderBrowserDialog { Description = $"Folder for {sel.Count} textures", UseDescriptionForTitle = true })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            folder = d.SelectedPath;
        }
        UseWaitCursor = true;
        var failed = await Task.Run(() => sel.Select(n => (n, catalog.ExportDds(pkg, n, Path.Combine(folder, n + ".dds")))).Where(x => x.Item2 != null).ToList());
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
