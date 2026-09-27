using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// + New Mod / Edit: the main window's Editor tab (Kurt: tabs, not pop-up windows), with a sub-tab per kind of change
/// (MHModManager's wizard steps, reachable in any order), in the main window's look (flat tabs, styled tables, accent Save).
/// Info (name, author, version) · Packages (.upk) · Icons / Achievement icons / Store images (stock texture ← .dds, with
/// both previews) · Strings (search the game's original text, set replacements) · Sound packs (.mhsfx).
/// Saving writes MHModManager's mod format (ModWriter); nothing in the game folder changes until Apply.
/// </summary>
sealed class ModEditorView : UserControl
{
    /// <summary>Saved (the mod's folder name) or cancelled: the main window goes back to the mod list.</summary>
    public event Action<string>? Saved;
    public event Action? Cancelled;
    public string Title { get; }
    public Mod? Editing => editing;
    readonly ModLibrary lib;
    readonly GameState? game;
    readonly StockCatalog? catalog;
    readonly Mod? editing;
    readonly ModDraft draft;
    public string? SavedName { get; private set; }

    // The mod's own tags and note (manifest extension fields: they travel with the mod).
    readonly TextBox tagsBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10f) };
    readonly TextBox notesBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f), Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly Label autoLabel = new() { AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left, Font = Ui.Regular(8.5f), Margin = new Padding(3, 2, 3, 6) };
    readonly TextBox nameBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) }, authorBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) }, versionBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) };
    readonly DataGridView packages, sounds;
    readonly TexturePage[] texturePages;
    readonly StringsPage stringsPage;
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    float S => DeviceDpi / 96f;
    /// <summary>The info and bottom bars (the host colours them as bars after theming).</summary>
    public Control[] Bars { get; private set; } = [];

    public ModEditorView(ModLibrary lib, GameState? game, Mod? editing)
    {
        this.lib = lib; this.game = game; this.editing = editing;
        catalog = game == null ? null : new StockCatalog(lib, game);
        draft = editing == null ? new ModDraft { Author = LastAuthor() } : ModDraft.From(editing);
        Title = editing == null ? "New Mod" : $"Edit: {editing.Name}";
        Dock = DockStyle.Fill;
        Font = Ui.Regular(9.5f);
        packages = Ui.Grid(S, false, ("Package", 0), ("Size", 90), ("From", 520));
        sounds = Ui.Grid(S, false, ("Sound Pack", 0), ("Events", 80), ("Sound Files It Patches", 360), ("From", 360));

        // ---- Info bar: name, author, version
        var info = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(12, 10, 12, 10) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        info.Controls.Add(Caption("Name"), 0, 0); info.Controls.Add(nameBox, 1, 0);
        info.Controls.Add(Caption("Author"), 2, 0); info.Controls.Add(authorBox, 3, 0);
        info.Controls.Add(Caption("Version"), 4, 0); info.Controls.Add(versionBox, 5, 0);
        nameBox.Text = draft.Name; authorBox.Text = draft.Author; versionBox.Text = draft.Version;
        info.Controls.Add(Caption("Tags"), 0, 1); info.Controls.Add(tagsBox, 1, 1); info.SetColumnSpan(tagsBox, 5);
        info.Controls.Add(autoLabel, 1, 2); info.SetColumnSpan(autoLabel, 5);
        info.Controls.Add(Caption("Note"), 0, 3); info.Controls.Add(notesBox, 1, 3); info.SetColumnSpan(notesBox, 5);
        notesBox.Dock = DockStyle.None; notesBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;   // Fill would shrink it to one line in the auto-sized row
        notesBox.Height = (int)(58 * S);
        tagsBox.Text = string.Join(", ", draft.Tags);
        notesBox.Text = draft.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n");
        tagsBox.AutoCompleteMode = AutoCompleteMode.Append; tagsBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        tagsBox.AutoCompleteCustomSource.AddRange(lib.AllTags().ToArray());
        autoLabel.Text = editing != null && editing.AutoTags.Count > 0
            ? "Separate tags with commas; they go with the mod. Added automatically from the content: " + string.Join(", ", editing.AutoTags)
            : "Separate tags with commas; they go with the mod. Characters, teams, costume, powers … are added automatically from the content.";

        tabs.Add("Packages", PackagesPage());
        texturePages = Enumerable.Range(0, 4).Select(k => new TexturePage(this, k)).ToArray();
        foreach (var (tp, title) in texturePages.Zip(new[] { "Icons", "Achievement Icons", "Store Images", "More Icon Packages" })) tabs.Add(title, tp);
        stringsPage = new StringsPage(this);
        tabs.Add("Strings", stringsPage);
        tabs.Add("Sound Packs", SoundsPage());
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 0) };
        body.Controls.Add(tabs);

        // ---- Bottom bar
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(12, 8, 12, 8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(new Label { Text = editing == null ? "New mods are added at the top of the list, turned off. Nothing in the game changes until Apply Changes." : "Saving replaces the mod's folder (the old one goes to the Recycle Bin). Nothing in the game changes until Apply Changes.", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" }, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var cancel = Ui.FlatButton("Cancel", () => Cancelled?.Invoke());
        var save = Ui.AccentButton(editing == null ? "Create Mod" : "Save Changes", Save);
        buttons.Controls.AddRange([cancel, save]);
        bottom.Controls.Add(buttons, 1, 0);
        Controls.Add(body); Controls.Add(info); Controls.Add(bottom);
        Bars = [info, bottom];
        RefreshPackages(); RefreshSounds();
    }

    string LastAuthor() => lib.Mods.OrderByDescending(m => Directory.GetLastWriteTimeUtc(m.Folder)).Select(m => m.Manifest.Author).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";

    static Label Caption(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(10, 0, 6, 0), Tag = "subtle" };
    static FlowLayoutPanel Toolbar(params Control[] c) { var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) }; f.Controls.AddRange(c); return f; }
    static Control Page(Control fill, Control toolbar, string hint)
    {
        var p = new Panel { Dock = DockStyle.Fill };
        p.Controls.Add(fill);
        p.Controls.Add(toolbar);
        p.Controls.Add(new Label { Text = hint, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2, 8, 2, 2), Tag = "subtle" });
        return p;
    }

    // ---- Packages
    Control PackagesPage() => Page(packages, Toolbar(Ui.FlatButton("Add .upk Files…", AddPackages), Ui.FlatButton("Remove", () =>
        {
            foreach (DataGridViewRow r in packages.SelectedRows) draft.Packages.RemoveAll(x => x.File == (string)r.Tag!);
            RefreshPackages();
        })), "Whole packages that replace the game's own (same file name, in CookedPCConsole).");

    void AddPackages()
    {
        using var d = new OpenFileDialog { Title = "Add Packages", Filter = "Unreal packages (*.upk)|*.upk", Multiselect = true };
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
        packages.Rows.Clear();
        foreach (var (file, src) in draft.Packages)
            packages.Rows[packages.Rows.Add(file, File.Exists(src) ? $"{new FileInfo(src).Length / 1048576.0:0.0} MB" : "missing", src)].Tag = file;
        packages.ClearSelection();
    }

    // ---- Sound packs
    Control SoundsPage() => Page(sounds, Toolbar(Ui.FlatButton("Add .mhsfx Files…", AddSounds), Ui.FlatButton("Remove", () =>
        {
            foreach (DataGridViewRow r in sounds.SelectedRows) draft.SoundPacks.Remove((string)r.Tag!);
            RefreshSounds();
        })), "Sound packs (.mhsfx) add new voice or sound events to the game's sound files; the mod's packages play them by name.");

    void AddSounds()
    {
        using var d = new OpenFileDialog { Title = "Add Sound Packs", Filter = "Sound packs (*.mhsfx)|*.mhsfx", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string f in d.FileNames)
        {
            try { SoundPack.Load(f); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { MessageBox.Show(this, $"{Path.GetFileName(f)} isn't a readable sound pack: {ex.Message}", "Sound Packs"); continue; }
            if (!draft.SoundPacks.Contains(f, StringComparer.OrdinalIgnoreCase)) draft.SoundPacks.Add(f);
        }
        RefreshSounds();
    }

    void RefreshSounds()
    {
        sounds.Rows.Clear();
        foreach (string f in draft.SoundPacks)
        {
            string events = "?", pcks = "";
            try { var p = SoundPack.Load(f); events = p.Patches.Count.ToString(); pcks = string.Join(", ", p.Patches.Select(x => x.PckFile).Distinct()); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { pcks = "unreadable"; }
            sounds.Rows[sounds.Rows.Add(Path.GetFileName(f), events, pcks, f)].Tag = f;
        }
        sounds.ClearSelection();
    }

    /// <summary>Test / snapshot hooks: the tab count, selecting a tab, its title.</summary>
    public int TabCount => tabs.Count;
    public void SelectTab(int i) => tabs.Select(i);
    public string TabTitle(int i) => tabs.TitleAt(i);

    /// <summary>Test hook (--editor-save-test): visits every tab so each page loads, then saves as the Save button does.</summary>
    public async Task<string?> SaveForTest()
    {
        for (int i = 0; i < tabs.Count; i++) { tabs.Select(i); await Task.Delay(i is 1 or 3 or 4 ? 5000 : 500); }
        Save();
        return SavedName;
    }

    // ---- Save
    void Save()
    {
        draft.Name = nameBox.Text; draft.Author = authorBox.Text; draft.Version = versionBox.Text;
        draft.Tags = tagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => lib.CleanTag(t) ?? t).ToList();
        draft.Notes = notesBox.Text;
        draft.Strings = stringsPage.Collect();
        string? saved = ModWriter.Save(lib, draft, editing, out string? error);
        if (saved == null) { MessageBox.Show(this, error, "Can't Save Yet"); return; }
        SavedName = saved;
        Saved?.Invoke(saved);
    }

    /// <summary>
    /// One texture view's replacements: stock textures (searchable, with preview) ← .dds files (with preview and size check).
    /// Views 0 icons, 1 achievement icons, 2 store images (MHModManager's three packages), 3 "more icon packages": any other
    /// stock ICO__ package (Silver Surfer's, HD, character select, …), picked from a list and saved in the manifest's
    /// ExtraIconReplacements extension (MHModManager ignores it; see ExtraIconReplacement).
    /// </summary>
    sealed class TexturePage : UserControl
    {
        readonly ModEditorView f;
        readonly int view;
        readonly TextBox search = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
        readonly NameList names = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
        readonly ComboBox packagePick = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly DataGridView rows;
        readonly PictureBox stockPic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24) };
        readonly PictureBox newPic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24) };
        readonly Label stockInfo = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 0) }, newInfo = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 0) };
        List<TexEntry> all = [];
        bool loaded;
        int thumbRequest;
        bool Extra => view == 3;

        /// <summary>This view's replacements (package file, texture, .dds source).</summary>
        IEnumerable<(string File, string Texture, string Source)> ViewRows() => Extra
            ? f.draft.Extra.Select(x => (x.Package, x.Texture, x.Source))
            : f.draft.Textures[view].Select(r => (Applier.IconPackages[view].File, r.Texture, r.Source));

        public TexturePage(ModEditorView f, int view)
        {
            this.f = f; this.view = view; Dock = DockStyle.Fill;
            float s = f.S;
            rows = Ui.Grid(s, true, ("Texture", 0), ("Replacement .dds", 240), ("Check", 330));
            rows.Tag = "keepselection";

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = (int)(360 * s), ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 0, 8, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label { Text = Extra ? "STOCK TEXTURES  ·  pick an icon package" : $"STOCK TEXTURES  ·  {Applier.IconPackages[view].File}", AutoSize = true, MaximumSize = new Size((int)(350 * s), 0), Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(0, 8, 0, 4) }, 0, 0);
            if (Extra) { packagePick.Margin = new Padding(0, 0, 0, 6); left.Controls.Add(packagePick, 0, 1); }
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
            searchRow.Controls.Add(search, 1, 0);
            left.Controls.Add(searchRow, 0, 2);
            left.Controls.Add(names, 0, 3);

            var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 4), Margin = new Padding(0) };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previews.Controls.Add(Ui.CardPanel("ORIGINAL", stockPic, stockInfo), 0, 0);
            previews.Controls.Add(Ui.CardPanel("REPLACEMENT", newPic, newInfo), 1, 0);

            // Right: hint, buttons, then the replacements table over the two previews (55 / 45).
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            string hintText = "Pick the stock texture on the left (search, then click), then choose its replacement: a .dds (DXT1, or DXT5 for soft alpha) or a PNG / JPG, converted to match the original. Double-click a name to choose straight away.";
            if (Extra) hintText += "  These packages are an extension: the old MHModManager installs the mod but skips these images.";
            var hint = new Label { Text = hintText, AutoSize = true, Tag = "subtle", Padding = new Padding(2, 8, 2, 2), Dock = DockStyle.Fill };
            right.Controls.Add(hint, 0, 0);
            var tools = Toolbar(Ui.AccentButton("Choose .dds or .png for the Selected Texture…", ChooseDds), Ui.FlatButton("Remove Replacement", RemoveRow), Ui.FlatButton("Save Original as .dds / .png…", SaveOriginal));
            tools.Dock = DockStyle.Fill;
            right.Controls.Add(tools, 0, 1);
            right.Controls.Add(rows, 0, 2);
            right.Controls.Add(previews, 0, 3);
            right.Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(100, right.Width - 10), 0);
            Controls.Add(right); Controls.Add(left);

            search.TextChanged += (_, _) => Filter();
            names.SelectedIndexChanged += (_, _) => { if (names.SelectedItem is TexEntry e) ShowStock(e.File, e.Name); };
            names.DoubleClick += (_, _) => ChooseDds();
            rows.SelectionChanged += (_, _) => { if (rows.SelectedRows.Count == 1 && rows.SelectedRows[0].Tag is (string file, string t, string src)) { ShowStock(file, t); ShowNew(src); } };
            packagePick.SelectedIndexChanged += async (_, _) => await LoadNames();
            VisibleChanged += async (_, _) =>
            {
                if (!Visible || loaded) return;
                loaded = true;
                if (f.catalog == null || f.game == null) { names.Items.Add("(game folder not set)"); return; }
                if (Extra)
                {
                    foreach (string p in IconCapture.ExtraPackages(f.game)) packagePick.Items.Add(p);
                    // Start on the package of the first existing replacement, else Silver Surfer's (the reason this exists), else the first.
                    string? first = f.draft.Extra.Select(x => x.Package).FirstOrDefault() ?? packagePick.Items.Cast<string>().FirstOrDefault(p => p.Contains("SilverSurfer", StringComparison.OrdinalIgnoreCase));
                    packagePick.SelectedItem = first != null && packagePick.Items.Contains(first) ? first : packagePick.Items.Count > 0 ? packagePick.Items[0] : null;
                }
                else await LoadNames();
                RefreshRows();
                if (rows.Rows.Count > 0) { rows.ClearSelection(); rows.Rows[0].Selected = true; }   // shows both previews
            };
            RefreshRows();
        }

        string? CurrentPackage => Extra ? packagePick.SelectedItem as string : Applier.IconPackages[view].File;

        async Task LoadNames()
        {
            if (f.catalog == null || CurrentPackage is not string pkg) return;
            names.Items.Clear(); names.Items.Add("Loading…");
            var t = await Task.Run(() => f.catalog.EntriesFor(pkg));
            all = t ?? [];
            if (t == null) { names.Items.Clear(); names.Items.Add("(no verified original of this package)"); return; }
            Filter();
        }

        void Filter()
        {
            string q = search.Text.Trim();
            names.BeginUpdate(); names.Items.Clear();
            foreach (var e in all.Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(5000)) names.Items.Add(e);
            names.EndUpdate();
        }

        async void ShowStock(string file, string texture)
        {
            if (f.catalog == null) return;
            var p = await Task.Run(() => f.catalog.Preview(file, texture));
            stockPic.Image = p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null;
            stockInfo.Text = p is { } y ? $"{texture}  ·  {y.W}×{y.H} {y.Format.Replace("pf_", "").Replace("PF_", "").ToUpperInvariant()}" : $"{texture}  ·  no preview";
        }

        void ShowNew(string path)
        {
            var d = TextureDecode.ReadDds(path, out string note);
            byte[]? bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            newPic.Image = bgra != null ? TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H) : null;
            newInfo.Text = d is { } z ? $"{Path.GetFileName(path)}  ·  {z.W}×{z.H} {z.Format.Replace("PF_", "").ToUpperInvariant()}" : $"{Path.GetFileName(path)}  ·  {note}";
        }

        /// <summary>DXT1/DXT5, sizes divisible by 4 (what Apply can write); a size other than the original's is only a warning.</summary>
        (string Text, bool Ok) Check(string file, string texture, string dds)
        {
            if (!File.Exists(dds)) return ("file missing", false);
            var img = TextureImport.ParseDds(File.ReadAllBytes(dds), out string? err);
            if (img == null) return ("can't be used: " + err, false);
            var stock = f.catalog?.Size(file, texture);
            string size = $"{img.Width}×{img.Height} {img.FourCC}, {img.Levels.Count} mip(s)";
            return stock is { } s && (s.W != img.Width || s.H != img.Height) ? ($"{size}; original is {s.W}×{s.H} (may show scaled)", false) : (size + "  ✓", true);
        }

        string? convertNote;

        void ChooseDds()
        {
            if (names.SelectedItem is not TexEntry e) { MessageBox.Show(this, "Select the stock texture to replace first (search on the left).", "Textures"); return; }
            using var d = new OpenFileDialog { Title = $"Replacement for {e.Name}", Filter = "Textures and images (*.dds;*.png;*.jpg;*.jpeg;*.bmp)|*.dds;*.png;*.jpg;*.jpeg;*.bmp|DDS textures (*.dds)|*.dds|Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string chosen = d.FileName;
            if (!chosen.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                // An image: made into a .dds like the original (size, DXT1 / DXT5); the mod gets the .dds.
                if (f.catalog == null) { MessageBox.Show(this, "Set the game folder first: the original texture's size and format are needed to convert an image.", "Textures"); return; }
                string outDds = Path.Combine(Settings.Home, "converted", ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(chosen)) + ".dds");
                try { convertNote = $"{Path.GetFileName(chosen)}: " + f.catalog.ImageToDds(e.File, e.Name, chosen, outDds); chosen = outDds; }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, $"{Path.GetFileName(chosen)} can't be converted: {ex.Message}", "Textures"); return; }
            }
            else convertNote = null;
            var (check, _) = Check(e.File, e.Name, chosen);
            if (check.StartsWith("can't")) { MessageBox.Show(this, $"{Path.GetFileName(chosen)} {check}\n\nSave it as DXT1 (no or 1-bit alpha) or DXT5 (soft alpha), or choose a PNG and it's converted.", "Textures"); return; }
            if (Extra)
            {
                f.draft.Extra.RemoveAll(x => x.Package.Equals(e.File, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                f.draft.Extra.Add((e.File, e.Name, chosen));
            }
            else
            {
                var list = f.draft.Textures[view];
                list.RemoveAll(r => r.Texture.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                list.Add((e.Name, chosen));
            }
            RefreshRows();
            ShowNew(chosen);
            if (convertNote != null) newInfo.Text = convertNote + "  ·  " + newInfo.Text;
        }

        /// <summary>The selected stock texture as .dds (a starting point for its replacement).</summary>
        void SaveOriginal()
        {
            if (f.catalog == null || names.SelectedItem is not TexEntry e) { MessageBox.Show(this, "Select a stock texture on the left first.", "Textures"); return; }
            using var d = new SaveFileDialog { Title = $"Save Original {e.Name}", Filter = "DDS texture (*.dds)|*.dds|PNG image (*.png)|*.png", FileName = e.Name + ".dds" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string? why = f.catalog.ExportImage(e.File, e.Name, d.FileName);
            if (why != null) MessageBox.Show(this, "Not saved: " + why, "Textures");
        }

        void RemoveRow()
        {
            foreach (DataGridViewRow r in rows.SelectedRows)
                if (r.Tag is (string file, string t, string _))
                {
                    if (Extra) f.draft.Extra.RemoveAll(x => x.Package == file && x.Texture == t);
                    else f.draft.Textures[view].RemoveAll(x => x.Texture == t);
                }
            RefreshRows();
        }

        void RefreshRows()
        {
            rows.Rows.Clear();
            var paths = new List<(string Path, DataGridViewRow Row)>();
            foreach (var (file, t, src) in ViewRows())
            {
                var (check, ok) = Check(file, t, src);
                var row = rows.Rows[rows.Rows.Add(null, Extra ? $"{t}   · {Path.GetFileNameWithoutExtension(file)}" : t, Path.GetFileName(src), check)];
                row.Tag = (file, t, src);
                row.Cells["Check"].Style.ForeColor = ok ? Ui.Enabled : Ui.Packages;
                paths.Add((src, row));
            }
            // Thumbnails of the replacements, in the background.
            int req = ++thumbRequest;
            int size = (int)(96 * f.S);
            Task.Run(() => paths.Select(p => Thumb(p.Path, size)).ToList()).ContinueWith(task =>
            {
                if (task.IsFaulted || req != thumbRequest || rows.IsDisposed) return;
                for (int i = 0; i < paths.Count; i++) if (task.Result[i] is Image img && i < rows.Rows.Count) rows.Rows[i].Cells[0].Value = img;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    static Image? Thumb(string path, int size) => Ui.DdsThumb(path, size);

    /// <summary>String replacements: search the game's original text (per language), add rows, type the new text.</summary>
    sealed class StringsPage : UserControl
    {
        readonly ModEditorView f;
        readonly ComboBox lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        readonly TextBox search = new() { Width = 420 };
        readonly DataGridView results;
        readonly DataGridView grid;
        readonly Label found = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0), Tag = "subtle" };

        public StringsPage(ModEditorView f)
        {
            this.f = f; Dock = DockStyle.Fill;
            float s = f.S;
            search.Width = (int)(420 * s); lang.Width = (int)(90 * s);
            results = Ui.Grid(s, false, ("ID", 190), ("Original Text", 0));
            results.Tag = "keepselection";
            grid = Ui.Grid(s, false, ("Lang", 60), ("File", 250), ("ID", 190), ("Original", 0), ("Replacement (Type Here)", 0));
            grid.ReadOnly = false;
            foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != "Replacement (type here)";
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Ui.StyleGrid(grid);
            foreach (var st in f.draft.Strings) grid.Rows[grid.Rows.Add(st.Language, st.File, st.Id.ToString(), "", st.Text)].Tag = st;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            bar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 8, 4, 0), Tag = "subtle" }, lang,
                new Label { Text = "Find Text or ID", AutoSize = true, Padding = new Padding(12, 8, 4, 0), Tag = "subtle" }, search, Ui.AccentButton("Search", Search), found]);
            search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            var split = new GradientSplit { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
            split.Panel1.Controls.Add(results);
            split.Panel1.Controls.Add(Toolbar(Ui.FlatButton("Add Selected to the Mod  ↓", AddSelected), new Label { Text = "GAME TEXT", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(Toolbar(Ui.FlatButton("Remove Selected Rows", () => { foreach (DataGridViewRow r in grid.SelectedRows) grid.Rows.Remove(r); }),
                Ui.FlatButton("Import Changes (.json)…", ImportJson), new Label { Text = "THIS MOD'S CHANGES", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            Controls.Add(split); Controls.Add(bar);
            results.CellDoubleClick += (_, _) => AddSelected();
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
            results.Rows.Clear();
            foreach (var h in hits.Take(500)) results.Rows[results.Rows.Add(h.Id.ToString(), h.Text)].Tag = (l, h.File, h.Id, h.Text);
            results.ClearSelection();
            found.Text = hits.Count > 500 ? "500+ found (showing 500; search more precisely)" : $"{hits.Count} found  ·  double-click or select and Add";
        }

        void AddSelected()
        {
            foreach (DataGridViewRow r in results.SelectedRows)
            {
                if (r.Tag is not (string l, string file, ulong id, string text)) continue;
                if (grid.Rows.Cast<DataGridViewRow>().Any(x => (string)x.Cells["ID"].Value! == id.ToString() && (string)x.Cells["Lang"].Value! == l)) continue;
                grid.Rows.Add(l, file, id.ToString(), text, text);
            }
        }

        /// <summary>Shows each existing row's original text (for comparison).</summary>
        async void FillOriginals()
        {
            if (f.catalog == null) return;
            foreach (string l in grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells["Lang"].Value!).Distinct().ToList())
            {
                var byId = (await Task.Run(() => f.catalog.Strings(l))).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Text);
                foreach (DataGridViewRow r in grid.Rows)
                    if ((string)r.Cells["Lang"].Value! == l && ulong.TryParse((string)r.Cells["ID"].Value!, out ulong id) && byId.TryGetValue(id, out string? t)) { r.Cells["Original"].Value = t; r.Cells["Original"].Style.ForeColor = Ui.Subtle; }
            }
        }

        /// <summary>A &lt;lang&gt;.json in MHModManager's format (e.g. from another mod or an extract).</summary>
        void ImportJson()
        {
            using var d = new OpenFileDialog { Title = "Import String Changes", Filter = "JSON (*.json)|*.json" };
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
                            foreach (var dup in grid.Rows.Cast<DataGridViewRow>().Where(r => (string)r.Cells["ID"].Value! == e.Name && (string)r.Cells["Lang"].Value! == l).ToList()) grid.Rows.Remove(dup);
                            grid.Rows.Add(l, file.Name, e.Name, "", s.GetString() ?? ""); n++;
                        }
            }
            catch (System.Text.Json.JsonException ex) { MessageBox.Show(this, "Not a string file: " + ex.Message, "Import"); return; }
            FillOriginals();
            MessageBox.Show(this, $"Imported {n} string(s) as language '{l}'.", "Import");
        }

        public List<StringReplacement> Collect()
        {
            grid.EndEdit();
            var list = new List<StringReplacement>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                string l = (string)r.Cells["Lang"].Value!, file = (string)r.Cells["File"].Value!, text = r.Cells["Replacement (type here)"].Value as string ?? "";
                ulong id = ulong.Parse((string)r.Cells["ID"].Value!);
                // Keep an edited string's variants and flags as they were in the mod.
                var old = r.Tag as StringReplacement;
                list.Add(new StringReplacement(l, file, id, text, old?.FlagsProduced ?? 0, old?.Variants));
            }
            return list;
        }
    }
}
