using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// + New Mod / Edit: one window with a tab per kind of change (MHModManager's wizard steps, reachable in any order).
/// Info (name, author, version) · Packages (.upk) · Icons / Achievement icons / Store images (stock texture ← .dds, with
/// both previews) · Strings (search the game's original text, set replacements) · Sound packs (.mhsfx).
/// Saving writes MHModManager's mod format (ModWriter); nothing in the game folder changes until Apply.
/// </summary>
sealed class ModEditorForm : Form
{
    readonly ModLibrary lib;
    readonly GameState? game;
    readonly StockCatalog? catalog;
    readonly Mod? editing;
    readonly ModDraft draft;
    public string? SavedName { get; private set; }

    readonly TextBox nameBox = new() { Dock = DockStyle.Fill }, authorBox = new() { Dock = DockStyle.Fill }, versionBox = new() { Dock = DockStyle.Fill };
    readonly ListView packages = NewList(("Package", 420), ("Size", 90), ("From", 600));
    readonly ListView sounds = NewList(("Sound pack", 300), ("Events", 70), ("Sound files it patches", 360), ("From", 400));
    readonly TexturePage[] texturePages;
    readonly StringsPage stringsPage;

    public ModEditorForm(ModLibrary lib, GameState? game, Mod? editing)
    {
        this.lib = lib; this.game = game; this.editing = editing;
        catalog = game == null ? null : new StockCatalog(lib, game);
        draft = editing == null ? new ModDraft { Author = LastAuthor() } : ModDraft.From(editing);
        Text = editing == null ? "New mod" : $"Edit mod: {editing.Name}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 1500; Height = 900; StartPosition = FormStartPosition.CenterParent;

        var info = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(8) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        info.Controls.Add(Caption("Name"), 0, 0); info.Controls.Add(nameBox, 1, 0);
        info.Controls.Add(Caption("Author"), 2, 0); info.Controls.Add(authorBox, 3, 0);
        info.Controls.Add(Caption("Version"), 4, 0); info.Controls.Add(versionBox, 5, 0);
        nameBox.Text = draft.Name; authorBox.Text = draft.Author; versionBox.Text = draft.Version;

        var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(PackagesTab());
        texturePages = Applier.IconPackages.Select((p, k) => new TexturePage(this, k)).ToArray();
        foreach (var (tp, title) in texturePages.Zip(new[] { "Icons", "Achievement icons", "Store images" })) { var page = new TabPage(title); page.Controls.Add(tp); tabs.TabPages.Add(page); }
        stringsPage = new StringsPage(this);
        var sp = new TabPage("Strings"); sp.Controls.Add(stringsPage); tabs.TabPages.Add(sp);
        tabs.TabPages.Add(SoundsTab());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = editing == null ? "Create mod" : "Save changes", AutoSize = true };
        save.Click += (_, _) => Save();
        bottom.Controls.Add(cancel); bottom.Controls.Add(save);
        bottom.Controls.Add(new Label { Text = "New mods are added at the top of the list, turned off. Nothing in the game changes until Apply.", AutoSize = true, Padding = new Padding(0, 8, 12, 0) });
        CancelButton = cancel;

        Controls.Add(tabs); Controls.Add(info); Controls.Add(bottom);
        Load += (_, _) => { Theme.Apply(this, Palette.Dark); RefreshPackages(); RefreshSounds(); };
    }

    string LastAuthor() => lib.Mods.OrderByDescending(m => Directory.GetLastWriteTimeUtc(m.Folder)).Select(m => m.Manifest.Author).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";

    static Label Caption(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 0, 4, 0) };
    static ListView NewList(params (string, int)[] cols)
    {
        var l = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
        foreach (var (t, w) in cols) l.Columns.Add(t, w);
        return l;
    }
    static Button Btn(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => a(); return b; }
    static FlowLayoutPanel Buttons(params Control[] c) { var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true }; f.Controls.AddRange(c); return f; }
    static TabPage Page(string title, Control fill, Control top, string hint)
    {
        var p = new TabPage(title);
        p.Controls.Add(fill); p.Controls.Add(new Label { Text = hint, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) }); p.Controls.Add(top);
        return p;
    }

    // ---- Packages
    TabPage PackagesTab() => Page("Packages", packages, Buttons(Btn("Add .upk files…", AddPackages), Btn("Remove", () => { foreach (ListViewItem i in packages.SelectedItems) draft.Packages.RemoveAll(x => x.File == i.Text); RefreshPackages(); })),
        "Whole packages that replace the game's own (same file name, in CookedPCConsole).");

    void AddPackages()
    {
        using var d = new OpenFileDialog { Title = "Add packages", Filter = "Unreal packages (*.upk)|*.upk", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        var notGame = new List<string>();
        foreach (string f in d.FileNames)
        {
            string name = Path.GetFileName(f);
            if (game != null && game.HasStockList && !game.IsStockName(name)) notGame.Add(name);
            draft.Packages.RemoveAll(x => x.File.Equals(name, StringComparison.OrdinalIgnoreCase));
            draft.Packages.Add((name, f));
        }
        RefreshPackages();
        if (notGame.Count > 0) MessageBox.Show(this, "Not a game package name, so Apply won't find a file to replace:\n" + string.Join("\n", notGame) + "\n\nThe file name must match the package it replaces.", "Packages");
    }

    void RefreshPackages()
    {
        packages.Items.Clear();
        foreach (var (file, src) in draft.Packages)
            packages.Items.Add(new ListViewItem([file, File.Exists(src) ? $"{new FileInfo(src).Length / 1048576.0:0.0} MB" : "missing", src]));
    }

    // ---- Sound packs
    TabPage SoundsTab() => Page("Sound packs", sounds, Buttons(Btn("Add .mhsfx files…", AddSounds), Btn("Remove", () => { foreach (ListViewItem i in sounds.SelectedItems) draft.SoundPacks.Remove((string)i.Tag!); RefreshSounds(); })),
        "Sound packs (.mhsfx) add new voice or sound events to the game's sound files; the mod's packages play them by name.");

    void AddSounds()
    {
        using var d = new OpenFileDialog { Title = "Add sound packs", Filter = "Sound packs (*.mhsfx)|*.mhsfx", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string f in d.FileNames)
        {
            try { SoundPack.Load(f); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { MessageBox.Show(this, $"{Path.GetFileName(f)} isn't a readable sound pack: {ex.Message}", "Sound packs"); continue; }
            if (!draft.SoundPacks.Contains(f, StringComparer.OrdinalIgnoreCase)) draft.SoundPacks.Add(f);
        }
        RefreshSounds();
    }

    void RefreshSounds()
    {
        sounds.Items.Clear();
        foreach (string f in draft.SoundPacks)
        {
            string events = "?", pcks = "";
            try { var p = SoundPack.Load(f); events = p.Patches.Count.ToString(); pcks = string.Join(", ", p.Patches.Select(x => x.PckFile).Distinct()); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { pcks = "unreadable"; }
            sounds.Items.Add(new ListViewItem([Path.GetFileName(f), events, pcks, f]) { Tag = f });
        }
    }

    /// <summary>Test hook (--editor-save-test): visits every tab so each page loads, then saves as the Save button does.</summary>
    public async Task<string?> SaveForTest()
    {
        var tabs = Controls.OfType<ThemedTabControl>().First();
        for (int i = 0; i < tabs.TabPages.Count; i++) { tabs.SelectedIndex = i; await Task.Delay(i is 1 or 3 or 4 ? 5000 : 500); }
        Save();
        return SavedName;
    }

    // ---- Save
    void Save()
    {
        draft.Name = nameBox.Text; draft.Author = authorBox.Text; draft.Version = versionBox.Text;
        draft.Strings = stringsPage.Collect();
        string? saved = ModWriter.Save(lib, draft, editing, out string? error);
        if (saved == null) { MessageBox.Show(this, error, "Can't save yet"); return; }
        SavedName = saved;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>One icon package's replacements: stock textures (searchable, with preview) ← .dds files (with preview and size check).</summary>
    sealed class TexturePage : UserControl
    {
        readonly ModEditorForm f;
        readonly int k;
        readonly TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Search textures (e.g. storm, costume, power_)" };
        readonly ListBox names = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        readonly ListView rows = NewList(("Texture", 320), ("Replacement .dds", 280), ("Check", 380));
        readonly PictureBox stockPic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 18) };
        readonly PictureBox newPic = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(18, 18, 18) };
        readonly Label stockInfo = new() { Dock = DockStyle.Bottom, AutoSize = true }, newInfo = new() { Dock = DockStyle.Bottom, AutoSize = true };
        List<string> all = [];
        bool loaded;

        string Package => Applier.IconPackages[k].File;
        List<(string Texture, string Source)> Rows => f.draft.Textures[k];

        public TexturePage(ModEditorForm f, int k)
        {
            this.f = f; this.k = k; Dock = DockStyle.Fill;
            var left = new Panel { Dock = DockStyle.Left, Width = 380 };
            left.Controls.Add(names); left.Controls.Add(search);
            left.Controls.Add(new Label { Text = $"Stock textures in {Package}. Search, pick one, then choose its .dds (or double-click it):", Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(370, 0), Padding = new Padding(2, 4, 2, 4) });
            var previews = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 330, ColumnCount = 2 };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var sp = new Panel { Dock = DockStyle.Fill }; sp.Controls.Add(stockPic); sp.Controls.Add(stockInfo); sp.Controls.Add(new Label { Text = "Original", Dock = DockStyle.Top, AutoSize = true });
            var np = new Panel { Dock = DockStyle.Fill }; np.Controls.Add(newPic); np.Controls.Add(newInfo); np.Controls.Add(new Label { Text = "Replacement", Dock = DockStyle.Top, AutoSize = true });
            previews.Controls.Add(sp, 0, 0); previews.Controls.Add(np, 1, 0);
            var right = new Panel { Dock = DockStyle.Fill };
            right.Controls.Add(rows); right.Controls.Add(previews);
            right.Controls.Add(Buttons(Btn("Choose .dds for the selected texture…", ChooseDds), Btn("Remove replacement", RemoveRow)));
            Controls.Add(right); Controls.Add(left);
            search.TextChanged += (_, _) => Filter();
            names.SelectedIndexChanged += (_, _) => { if (names.SelectedItem is string t) ShowStock(t); };
            names.DoubleClick += (_, _) => ChooseDds();
            rows.SelectedIndexChanged += (_, _) => { if (rows.SelectedItems.Count == 1) { var (t, s) = ((string, string))rows.SelectedItems[0].Tag!; ShowStock(t); ShowNew(s); } };
            VisibleChanged += async (_, _) =>
            {
                if (!Visible || loaded) return;
                loaded = true;
                if (f.catalog == null) { names.Items.Add("(game folder not set)"); return; }
                names.Items.Add("Loading…");
                var t = await Task.Run(() => f.catalog.Textures(Package));
                all = t?.Keys.ToList() ?? [];
                if (t == null) { names.Items.Clear(); names.Items.Add("(no verified original of this package)"); return; }
                Filter(); RefreshRows();
                if (rows.Items.Count > 0) rows.Items[0].Selected = true;   // shows both previews
            };
            RefreshRows();
        }

        void Filter()
        {
            string q = search.Text.Trim();
            names.BeginUpdate(); names.Items.Clear();
            foreach (string n in all.Where(n => q.Length == 0 || n.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5000)) names.Items.Add(n);
            names.EndUpdate();
        }

        async void ShowStock(string texture)
        {
            if (f.catalog == null) return;
            var p = await Task.Run(() => f.catalog.Preview(Package, texture));
            stockPic.Image = p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null;
            stockInfo.Text = p is { } y ? $"{texture}: {y.W}x{y.H} {y.Format}" : $"{texture}: no preview";
        }

        void ShowNew(string path)
        {
            var d = TextureDecode.ReadDds(path, out string note);
            byte[]? bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            newPic.Image = bgra != null ? TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H) : null;
            newInfo.Text = d is { } z ? $"{Path.GetFileName(path)}: {z.W}x{z.H} {z.Format}" : $"{Path.GetFileName(path)}: {note}";
        }

        /// <summary>DXT1/DXT5, sizes divisible by 4 (what Apply can write); a size other than the original's is only a warning.</summary>
        string Check(string texture, string dds)
        {
            if (!File.Exists(dds)) return "file missing";
            var img = TextureImport.ParseDds(File.ReadAllBytes(dds), out string? err);
            if (img == null) return "can't be used: " + err;
            var stock = f.catalog?.Size(Package, texture);
            string size = $"{img.Width}x{img.Height} {img.FourCC}, {img.Levels.Count} mip(s)";
            return stock is { } s && (s.W != img.Width || s.H != img.Height) ? $"{size}; original is {s.W}x{s.H} (may show scaled)" : size + " ✓";
        }

        void ChooseDds()
        {
            if (names.SelectedItem is not string texture || texture.StartsWith('(') || texture == "Loading…") { MessageBox.Show(this, "Select the stock texture to replace first (search on the left).", "Textures"); return; }
            using var d = new OpenFileDialog { Title = $"Replacement for {texture}", Filter = "DDS textures (*.dds)|*.dds" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string check = Check(texture, d.FileName);
            if (check.StartsWith("can't")) { MessageBox.Show(this, $"{Path.GetFileName(d.FileName)} {check}\n\nSave it as DXT1 (no or 1-bit alpha) or DXT5 (soft alpha).", "Textures"); return; }
            Rows.RemoveAll(r => r.Texture.Equals(texture, StringComparison.OrdinalIgnoreCase));
            Rows.Add((texture, d.FileName));
            RefreshRows();
            ShowNew(d.FileName);
        }

        void RemoveRow()
        {
            foreach (ListViewItem i in rows.SelectedItems) { var (t, _) = ((string, string))i.Tag!; Rows.RemoveAll(r => r.Texture == t); }
            RefreshRows();
        }

        void RefreshRows()
        {
            rows.Items.Clear();
            foreach (var (t, s) in Rows) rows.Items.Add(new ListViewItem([t, Path.GetFileName(s), Check(t, s)]) { Tag = (t, s) });
        }
    }

    /// <summary>String replacements: search the game's original text (per language), add rows, type the new text.</summary>
    sealed class StringsPage : UserControl
    {
        readonly ModEditorForm f;
        readonly ComboBox lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        readonly TextBox search = new() { Width = 420, PlaceholderText = "Text or ID to find (e.g. a costume name)" };
        readonly ListView results = NewList(("ID", 170), ("Original text", 900));
        readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        readonly Label found = new() { AutoSize = true, Padding = new Padding(6, 6, 0, 0) };

        public StringsPage(ModEditorForm f)
        {
            this.f = f; Dock = DockStyle.Fill;
            grid.Columns.Add("lang", "Lang"); grid.Columns.Add("file", "File"); grid.Columns.Add("id", "ID"); grid.Columns.Add("orig", "Original"); grid.Columns.Add("text", "Replacement (edit here)");
            grid.Columns["lang"]!.FillWeight = 8; grid.Columns["file"]!.FillWeight = 30; grid.Columns["id"]!.FillWeight = 22; grid.Columns["orig"]!.FillWeight = 60; grid.Columns["text"]!.FillWeight = 60;
            foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != "text";
            foreach (var s in f.draft.Strings) grid.Rows.Add(s.Language, s.File, s.Id.ToString(), "", s.Text);
            foreach (DataGridViewRow r in grid.Rows) r.Tag = f.draft.Strings[r.Index];

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            var go = Btn("Search", Search);
            bar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, lang, new Label { Text = "Find text or ID", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, search, go, found]);
            search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(results);
            split.Panel1.Controls.Add(Buttons(Btn("Add selected to the mod ↓", AddSelected)));
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(Buttons(Btn("Remove selected rows", () => { foreach (DataGridViewRow r in grid.SelectedRows) grid.Rows.Remove(r); }), Btn("Import changes (.json)…", ImportJson)));
            Controls.Add(split); Controls.Add(bar);
            results.DoubleClick += (_, _) => AddSelected();
            VisibleChanged += (_, _) =>
            {
                if (!Visible || lang.Items.Count > 0 || f.catalog == null) return;
                foreach (string l in f.catalog.Languages()) lang.Items.Add(l);
                lang.SelectedItem = lang.Items.Contains("eng") ? "eng" : lang.Items.Count > 0 ? lang.Items[0] : null;
                FillOriginals();
            };
        }

        async void Search()
        {
            if (f.catalog == null || lang.SelectedItem is not string l) return;
            string q = search.Text.Trim();
            if (q.Length < 2) return;
            found.Text = "Searching…";
            var hits = await Task.Run(() => f.catalog.Strings(l).Where(s => s.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.ToString() == q).Take(501).ToList());
            results.BeginUpdate(); results.Items.Clear();
            foreach (var h in hits.Take(500)) results.Items.Add(new ListViewItem([h.Id.ToString(), h.Text]) { Tag = (l, h.File, h.Id, h.Text) });
            results.EndUpdate();
            found.Text = hits.Count > 500 ? "500+ found (showing 500; search more precisely)" : $"{hits.Count} found";
        }

        void AddSelected()
        {
            foreach (ListViewItem i in results.SelectedItems)
            {
                var (l, file, id, text) = ((string, string, ulong, string))i.Tag!;
                if (grid.Rows.Cast<DataGridViewRow>().Any(r => (string)r.Cells["id"].Value! == id.ToString() && (string)r.Cells["lang"].Value! == l)) continue;
                grid.Rows.Add(l, file, id.ToString(), text, text);
            }
        }

        /// <summary>Shows each existing row's original text (for comparison).</summary>
        async void FillOriginals()
        {
            if (f.catalog == null) return;
            foreach (string l in grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells["lang"].Value!).Distinct().ToList())
            {
                var byId = (await Task.Run(() => f.catalog.Strings(l))).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Text);
                foreach (DataGridViewRow r in grid.Rows)
                    if ((string)r.Cells["lang"].Value! == l && ulong.TryParse((string)r.Cells["id"].Value!, out ulong id) && byId.TryGetValue(id, out string? t)) r.Cells["orig"].Value = t;
            }
        }

        /// <summary>A &lt;lang&gt;.json in MHModManager's format (e.g. from another mod or an extract).</summary>
        void ImportJson()
        {
            using var d = new OpenFileDialog { Title = "Import string changes", Filter = "JSON (*.json)|*.json" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string l = Path.GetFileNameWithoutExtension(d.FileName);
            if (l.Length != 3) l = lang.SelectedItem as string ?? "eng";
            int n = 0;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(d.FileName));
                foreach (var file in doc.RootElement.EnumerateObject())
                    foreach (var e in file.Value.EnumerateObject())
                        if (ulong.TryParse(e.Name, out _) && e.Value.TryGetProperty("String", out var s))
                        {
                            foreach (var dup in grid.Rows.Cast<DataGridViewRow>().Where(r => (string)r.Cells["id"].Value! == e.Name && (string)r.Cells["lang"].Value! == l).ToList()) grid.Rows.Remove(dup);
                            grid.Rows.Add(l, file.Name, e.Name, "", s.GetString() ?? ""); n++;
                        }
            }
            catch (System.Text.Json.JsonException ex) { MessageBox.Show(this, "Not a string file: " + ex.Message, "Import"); return; }
            FillOriginals();
            MessageBox.Show(this, $"Imported {n} string(s) as language '{l}'.", "Import");
        }

        public List<StringReplacement> Collect()
        {
            var list = new List<StringReplacement>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                string l = (string)r.Cells["lang"].Value!, file = (string)r.Cells["file"].Value!, text = r.Cells["text"].Value as string ?? "";
                ulong id = ulong.Parse((string)r.Cells["id"].Value!);
                // Keep an edited string's variants and flags as they were in the mod.
                var old = r.Tag as StringReplacement;
                list.Add(new StringReplacement(l, file, id, text, old?.FlagsProduced ?? 0, old?.Variants));
            }
            return list;
        }
    }
}
