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
    // Description tab: the mod's description and this version's changes (for its Nexus / Discord posts; they travel with the mod).
    readonly TextBox descriptionBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10f), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly TextBox changesBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10f), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    readonly Label changesCaption = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 10, 0, 4) };
    readonly TextBox nexusBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10f) };
    // The picture the manager shows big for the mod (manifest PreviewImage; users can still pick their own).
    readonly ComboBox previewBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460, Font = Ui.Regular(9.5f), FlatStyle = FlatStyle.Flat };
    List<string> previewKeys = [];   // parallel to previewBox's items after "Automatic"
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

        tabs.Add("Description", DescriptionPage());
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
        var cancel = Ui.FlatButton("Cancel", () => Cancelled?.Invoke(), tip: "Close the editor without saving (the mod stays as it was).");
        var post = Ui.FlatButton("Create Post…", CreatePost, tip: "Make the Nexus and Discord posts for this mod (text and pictures); they are kept with the mod.");
        var save = Ui.AccentButton(editing == null ? "Create Mod" : "Save Changes", Save, tip: "Save the mod to the library. Nothing in the game changes until Apply Changes.");
        buttons.Controls.AddRange([post, cancel, save]);
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
    Control PackagesPage() => Page(packages, Toolbar(Ui.FlatButton("Add .UPK Files…", AddPackages, tip: "Add game packages (.UPK) this mod replaces; each file name must match the package it replaces."), Ui.Tip(Ui.FlatButton("Remove", () =>
        {
            foreach (DataGridViewRow r in packages.SelectedRows) draft.Packages.RemoveAll(x => x.File == (string)r.Tag!);
            RefreshPackages();
        }), "Take the selected packages out of the mod.")), "Whole packages that replace the game's own (same file name, in CookedPCConsole).");

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
        if (notGame.Count > 0) Dialog.Show(this, "Not a game package name, so Apply won't find a file to replace:\n" + string.Join("\n", notGame) + "\n\nThe file name must match the package it replaces.", "Packages");
    }

    void RefreshPackages()
    {
        packages.Rows.Clear();
        foreach (var (file, src) in draft.Packages)
            packages.Rows[packages.Rows.Add(file, File.Exists(src) ? $"{new FileInfo(src).Length / 1048576.0:0.0} MB" : "missing", src)].Tag = file;
        packages.ClearSelection();
    }

    // ---- Sound packs
    Control SoundsPage() => Page(sounds, Toolbar(Ui.FlatButton("Add .MHSFX Files…", AddSounds, tip: "Add sound packs (.MHSFX): new voice lines and sounds."), Ui.Tip(Ui.FlatButton("Remove", () =>
        {
            foreach (DataGridViewRow r in sounds.SelectedRows) draft.SoundPacks.Remove((string)r.Tag!);
            RefreshSounds();
        }), "Take the selected sound packs out of the mod.")), "Sound packs (.MHSFX) add new voice or sound events to the game's sound files; the mod's packages play them by name.");

    void AddSounds()
    {
        using var d = new OpenFileDialog { Title = "Add Sound Packs", Filter = "Sound packs (*.mhsfx)|*.mhsfx", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string f in d.FileNames)
        {
            try { SoundPack.Load(f); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { Dialog.Show(this, $"{Path.GetFileName(f)} isn't a readable sound pack: {ex.Message}", "Sound Packs"); continue; }
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

    /// <summary>A costume the icon tabs can filter to (Kurt): from UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF.</summary>
    public sealed record CostumeFilter(string Label, List<string> Heroes, string Costume)
    {
        /// <summary>costume_storm_classic, costumestorm_classic, store_storm_classicblack, herohor_storm_classicblack …</summary>
        public bool Matches(string texture)
        {
            string t = texture.ToLowerInvariant();
            foreach (string prefix in new[] { "costume_", "costume", "store_", "herohor_", "teamup_" })
                if (t.StartsWith(prefix)) { t = t[prefix.Length..]; break; }
            int u = t.IndexOf('_');
            string hero = u < 0 ? t : t[..u], rest = u < 0 ? "" : t[(u + 1)..];
            if (!Heroes.Contains(hero)) return false;
            if (Costume.Length == 0) return true;
            string c = rest.Split('_')[0];
            return c.Length > 0 && (c.StartsWith(Costume) || Costume.StartsWith(c));
        }

        /// <summary>The filter for a UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF package, or null for other packages.</summary>
        public static CostumeFilter? FromPackage(string file)
        {
            if (!file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) return null;
            var p = Path.GetFileNameWithoutExtension(file).Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return null;
            // Only the final _SF is the seek-free suffix: uc__marvelplayer_blade_sf_SF is Blade's "sf" costume (herohor_blade_sf).
            var costume = p.Skip(3).ToList();
            if (costume.Count > 0 && costume[^1].Equals("SF", StringComparison.OrdinalIgnoreCase)) costume.RemoveAt(costume.Count - 1);
            string hero = AutoTags.DisplayName(p[2]) ?? p[2];
            string spaced = System.Text.RegularExpressions.Regex.Replace(string.Join(" ", costume), "(?<=[a-z0-9])(?=[A-Z])", " ");   // CivilWarMovie → Civil War Movie
            return new CostumeFilter(costume.Count > 0 ? $"{hero} {spaced}" : hero, AutoTags.Spellings(p[2]), string.Join("", costume).ToLowerInvariant());
        }
    }

    /// <summary>The selected costume package on the Packages tab, else the only one in the mod; null if none.</summary>
    CostumeFilter? SelectedCostume()
    {
        string? file = packages.SelectedRows.Count == 1 ? packages.SelectedRows[0].Tag as string : null;
        var costumes = draft.Packages.Select(p => p.File).Where(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (file == null || !file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) file = costumes.Count == 1 ? costumes[0] : null;
        return file == null ? null : CostumeFilter.FromPackage(file);
    }
    public string TabTitle(int i) => tabs.TitleAt(i);

    /// <summary>Test hook (--editor-save-test): visits every tab so each page loads, then saves as the Save button does.</summary>
    public async Task<string?> SaveForTest()
    {
        for (int i = 0; i < tabs.Count; i++) { tabs.Select(i); await Task.Delay(i is 1 or 3 or 4 ? 5000 : 500); }
        Save();
        return SavedName;
    }

    /// <summary>Test hook: the Strings tab's state (replacement column editable, rows with a Used By text).</summary>
    public string StringsCheck() => stringsPage.Check();

    /// <summary>Test hook: search the Strings tab and wait for the results and the Used By column.</summary>
    public async Task SearchStringsForTest(string text)
    {
        tabs.Select(tabs.Count - 2);   // Strings
        await Task.Delay(4000);
        stringsPage.SearchForTest(text);
        await Task.Delay(3000);
    }

    // ---- Save
    void Save()
    {
        Collect();
        draft.Strings = stringsPage.Collect();
        string? saved = ModWriter.Save(lib, draft, editing, out string? error);
        if (saved == null) { Dialog.Show(this, error ?? "", "Can't Save Yet"); return; }
        SavedName = saved;
        Saved?.Invoke(saved);
    }

    /// <summary>The Preview Image choices from the draft's images now (they may have changed on the texture tabs).</summary>
    void FillPreviewChoices()
    {
        string? keep = previewBox.SelectedIndex > 0 && previewBox.SelectedIndex <= previewKeys.Count ? previewKeys[previewBox.SelectedIndex - 1] : draft.PreviewImage;
        var images = draft.Textures[2].Select(t => (t.Texture, t.Source, Applier.IconPackages[2].File))
            .Concat(draft.Textures[0].Select(t => (t.Texture, t.Source, Applier.IconPackages[0].File)))
            .Concat(draft.Extra.Select(t => (t.Texture, t.Source, t.Package)));
        List<PreviewCandidate> pics; List<MeshRef> meshes;
        try { lock (Ui.StockLock) pics = PreviewImages.For(images, [], catalog); } catch (Exception ex) when (ex is IOException or InvalidDataException) { pics = []; }
        try { meshes = ModMeshes.List(draft.Packages.Select(p => (p.File, p.Source))); } catch (Exception ex) when (ex is IOException or InvalidDataException) { meshes = []; }
        previewKeys = [.. pics.Select(c => c.Key), .. meshes.Select(m => m.Key)];
        previewBox.BeginUpdate();
        previewBox.Items.Clear();
        previewBox.Items.Add("Automatic (the first store image)");
        foreach (var c in pics) previewBox.Items.Add($"{c.Texture}  ·  {c.Source}");
        foreach (var m in meshes) previewBox.Items.Add($"3D: {m.Name}  ·  {m.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}");
        int i = keep == null ? -1 : previewKeys.FindIndex(k => k.Equals(keep, StringComparison.OrdinalIgnoreCase));
        previewBox.SelectedIndex = i + 1;
        previewBox.EndUpdate();
    }

    /// <summary>The info bar's and the Description tab's fields into the draft.</summary>
    void Collect()
    {
        draft.Name = nameBox.Text; draft.Author = authorBox.Text; draft.Version = versionBox.Text;
        draft.Description = descriptionBox.Text;
        draft.Changes = changesBox.Text;
        draft.NexusModId = Nexus.ParseModId(nexusBox.Text);
        if (previewBox.Items.Count > 0) draft.PreviewImage = previewBox.SelectedIndex > 0 && previewBox.SelectedIndex <= previewKeys.Count ? previewKeys[previewBox.SelectedIndex - 1] : null;
        draft.Tags = tagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => lib.CleanTag(t) ?? t).ToList();
        draft.Notes = notesBox.Text;
    }

    /// <summary>Create Post from what's in the editor now (saved or not).</summary>
    void CreatePost()
    {
        Collect();
        draft.Strings = stringsPage.Collect();
        using var f = new PostForm(PostWriter.From(draft), "", (draft.PostNexus, draft.PostDiscord, draft.PostImages),
            (n, d, imgs) => { draft.PostNexus = n; draft.PostDiscord = d; draft.PostImages = imgs; }, keptWithDraft: true);
        f.ShowDialog(this);
    }

    Control DescriptionPage()
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(0, 8, 0, 0) };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(new Label { Text = "DESCRIPTION  ·  what the mod is, for its Nexus / Discord post (it travels with the mod)", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        p.Controls.Add(descriptionBox, 0, 1);
        p.Controls.Add(changesCaption, 0, 2);
        p.Controls.Add(changesBox, 0, 3);
        var older = draft.Changelog.Where(e => !e.Version.Trim().Equals(draft.Version.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var nexusRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        nexusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); nexusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nexusRow.Controls.Add(new Label { Text = "NEXUS PAGE  ·  address or mod number (users get update notices)", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        nexusRow.Controls.Add(nexusBox, 1, 0);
        nexusBox.Text = draft.NexusModId is int nid ? Nexus.SiteMods + nid : "";
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(nexusRow, 0, 5);
        var previewRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        previewRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); previewRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewRow.Controls.Add(new Label { Text = "PREVIEW IMAGE  ·  shown big in the manager (users can pick their own)", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        previewRow.Controls.Add(previewBox, 1, 0);
        Ui.Tip(previewBox, "What the manager shows big for this mod: one of its own images, the game's original, or a 3D view of one of its meshes. Automatic: the mod's first store image, else the game's store image.");
        FillPreviewChoices();
        previewBox.DropDown += (_, _) => FillPreviewChoices();
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(previewRow, 0, 6);
        p.Controls.Add(new Label { Text = older.Count > 0 ? "Earlier versions: " + string.Join(", ", older.Select(e => "v" + e.Version.TrimStart('v', 'V'))) + " (kept in the changelog)" : "Each version's changes are kept in the mod's changelog.", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 0) }, 0, 4);
        descriptionBox.Text = draft.Description.Replace("\r\n", "\n").Replace("\n", "\r\n");
        changesBox.Text = draft.Changes.Replace("\r\n", "\n").Replace("\n", "\r\n");
        void Caption() => changesCaption.Text = $"CHANGES IN THIS VERSION{(versionBox.Text.Trim().Length > 0 ? " (v" + versionBox.Text.Trim().TrimStart('v', 'V') + ")" : "")}  ·  what's new, shown in the post";
        Caption();
        changesCaption.Font = Ui.Bold(8.5f);
        versionBox.TextChanged += (_, _) => Caption();
        return p;
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
        // Costume filter from the Packages tab (Kurt: open on the costume of the selected package when there is one).
        CostumeFilter? costume;
        string? dismissed;   // the costume the user chose Show All for
        readonly Label costumeLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(8.75f) };
        readonly Button showAll;
        readonly FlowLayoutPanel costumeRow = new() { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), Visible = false };
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
            rows = Ui.Grid(s, true, ("Texture", 0), ("Replacement .DDS", 240), ("Check", 330));
            rows.Tag = "keepselection";

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = (int)(360 * s), ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 0, 8, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label { Text = Extra ? "STOCK TEXTURES  ·  pick an icon package" : $"STOCK TEXTURES  ·  {Applier.IconPackages[view].File}", AutoSize = true, MaximumSize = new Size((int)(350 * s), 0), Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(0, 8, 0, 4) }, 0, 0);
            if (Extra) { packagePick.Margin = new Padding(0, 0, 0, 6); left.Controls.Add(packagePick, 0, 1); }
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
            searchRow.Controls.Add(search, 1, 0);
            var searchBlock = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            searchBlock.Controls.Add(searchRow, 0, 0);
            showAll = Ui.FlatButton("Show All", () => { dismissed = costume?.Label; costume = null; costumeRow.Visible = false; Filter(); }, tip: "Show every texture again, not only the selected package's costume.");
            showAll.Padding = new Padding(4, 0, 4, 0); showAll.Font = Ui.Regular(8.5f);
            costumeRow.Controls.AddRange([costumeLabel, showAll]);
            searchBlock.Controls.Add(costumeRow, 0, 1);
            left.Controls.Add(searchBlock, 0, 2);
            left.Controls.Add(names, 0, 3);

            var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 4), Margin = new Padding(0) };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previews.Controls.Add(Ui.CardPanel("ORIGINAL", stockPic, stockInfo), 0, 0);
            previews.Controls.Add(Ui.CardPanel("REPLACEMENT", newPic, newInfo), 1, 0);

            // Right: hint, buttons, then the replacements table over the two previews (55 / 45).
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            string hintText = "Pick the stock texture on the left (search, then click), then choose its replacement: a .DDS (DXT1, or DXT5 for soft alpha) or a .PNG / .JPG, converted to match the original. Double-click a name to choose straight away.";
            if (Extra) hintText += "  These packages are an extension: the old MHModManager installs the mod but skips these images.";
            var hint = new Label { Text = hintText, AutoSize = true, Tag = "subtle", Padding = new Padding(2, 8, 2, 2), Dock = DockStyle.Fill };
            right.Controls.Add(hint, 0, 0);
            var tools = Toolbar(Ui.AccentButton("Choose .DDS or .PNG for the Selected Texture…", ChooseDds, tip: "Pick the replacement for the texture selected on the left: a .DDS, or a .PNG / .JPG converted to match the original."), Ui.FlatButton("Remove Replacement", RemoveRow, tip: "Take the selected replacements out of the mod."), Ui.FlatButton("Save Original as .DDS / .PNG…", SaveOriginal, tip: "Save the game's original of the selected texture, as a starting point for your replacement."));
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
            VisibleChanged += (_, _) => { if (Visible && loaded) ApplyCostume(); };
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
            ApplyCostume();
        }

        /// <summary>Filters to the Packages tab's costume when this page has textures for it; otherwise shows everything.</summary>
        void ApplyCostume()
        {
            var c = f.SelectedCostume();
            if (c != null && (c.Label == dismissed || !all.Any(e => c.Matches(e.Name)))) c = null;
            costume = c;
            costumeRow.Visible = c != null;
            if (c != null) { costumeLabel.Text = $"Showing {c.Label} (from the selected package)"; costumeLabel.ForeColor = Ui.TagCharacter; }
            Filter();
        }

        void Filter()
        {
            string q = search.Text.Trim();
            names.BeginUpdate(); names.Items.Clear();
            foreach (var e in all.Where(e => (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) && (costume == null || costume.Matches(e.Name))).Take(5000)) names.Items.Add(e);
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
            if (names.SelectedItem is not TexEntry e) { Dialog.Show(this, "Select the stock texture to replace first (search on the left).", "Textures"); return; }
            using var d = new OpenFileDialog { Title = $"Replacement for {e.Name}", Filter = "Textures and images (*.dds;*.png;*.jpg;*.jpeg;*.bmp)|*.dds;*.png;*.jpg;*.jpeg;*.bmp|DDS textures (*.dds)|*.dds|Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string chosen = d.FileName;
            if (!chosen.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                // An image: made into a .dds like the original (size, DXT1 / DXT5); the mod gets the .dds.
                if (f.catalog == null) { Dialog.Show(this, "Set the game folder first: the original texture's size and format are needed to convert an image.", "Textures"); return; }
                string outDds = Path.Combine(Settings.Home, "converted", ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(chosen)) + ".dds");
                try { convertNote = $"{Path.GetFileName(chosen)}: " + f.catalog.ImageToDds(e.File, e.Name, chosen, outDds); chosen = outDds; }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or System.Runtime.InteropServices.ExternalException) { Dialog.Show(this, $"{Path.GetFileName(chosen)} can't be converted: {ex.Message}", "Textures"); return; }
            }
            else convertNote = null;
            var (check, _) = Check(e.File, e.Name, chosen);
            if (check.StartsWith("can't")) { Dialog.Show(this, $"{Path.GetFileName(chosen)} {check}\n\nSave it as DXT1 (no or 1-bit alpha) or DXT5 (soft alpha), or choose a PNG and it's converted.", "Textures"); return; }
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
            if (f.catalog == null || names.SelectedItem is not TexEntry e) { Dialog.Show(this, "Select a stock texture on the left first.", "Textures"); return; }
            using var d = new SaveFileDialog { Title = $"Save Original {e.Name}", Filter = "DDS texture (*.dds)|*.dds|PNG image (*.png)|*.png", FileName = e.Name + ".dds" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string? why = f.catalog.ExportImage(e.File, e.Name, d.FileName);
            if (why != null) Dialog.Show(this, "Not saved: " + why, "Textures");
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
        // Columns are found by these names, not by their (Title Case) headers: 0.21.1 changed the replacement column's
        // header and a lookup by the old text made it read-only (Kurt couldn't type replacements until 0.22.3).
        const string ReplacementCol = "replacement", UsedByCol = "usedby";
        // What each game string is attached to (hero name, NPC, item, power …), from the game's data (StringUsage).
        StringUsage? usage;

        public StringsPage(ModEditorView f)
        {
            this.f = f; Dock = DockStyle.Fill;
            float s = f.S;
            search.Width = (int)(420 * s); lang.Width = (int)(90 * s);
            results = Ui.Grid(s, false, ("ID", 190), ("Original Text", 0), ("Used By", 420));
            results.Columns[^1].Name = UsedByCol;
            results.Tag = "keepselection";
            grid = Ui.Grid(s, false, ("Lang", 60), ("File", 250), ("ID", 190), ("Original", 0), ("Replacement (Type Here)", 0), ("Used By", 360));
            grid.Columns[4].Name = ReplacementCol; grid.Columns[5].Name = UsedByCol;
            grid.ReadOnly = false;
            foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != ReplacementCol;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            Ui.StyleGrid(grid);
            foreach (var st in f.draft.Strings) grid.Rows[grid.Rows.Add(st.Language, st.File, st.Id.ToString(), "", st.Text)].Tag = st;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
            bar.Controls.AddRange([new Label { Text = "Language", AutoSize = true, Padding = new Padding(0, 8, 4, 0), Tag = "subtle" }, lang,
                new Label { Text = "Find Text or ID", AutoSize = true, Padding = new Padding(12, 8, 4, 0), Tag = "subtle" }, search, Ui.AccentButton("Search", Search, tip: "Search the game's original text of the chosen language (text or string ID)."), found]);
            search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            var split = new GradientSplit { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5 };
            split.Panel1.Controls.Add(results);
            split.Panel1.Controls.Add(Toolbar(Ui.FlatButton("Add Selected to the Mod  ↓", AddSelected, tip: "Add the selected game strings to the mod, to type their new text below."), new Label { Text = "GAME TEXT", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            split.Panel2.Controls.Add(grid);
            split.Panel2.Controls.Add(Toolbar(Ui.Tip(Ui.FlatButton("Remove Selected Rows", () => { foreach (DataGridViewRow r in grid.SelectedRows) grid.Rows.Remove(r); }), "Take the selected string changes out of the mod."),
                Ui.FlatButton("Import Changes (.JSON)…", ImportJson, tip: "Load string changes from a .JSON in the mod format (e.g. one saved from Extract)."), new Label { Text = "THIS MOD'S CHANGES", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(12, 8, 0, 0) }));
            Controls.Add(split); Controls.Add(bar);
            results.CellDoubleClick += (_, _) => AddSelected();
            VisibleChanged += (_, _) =>
            {
                if (!Visible || lang.Items.Count > 0 || f.catalog == null) return;
                foreach (string l in f.catalog.Languages()) lang.Items.Add(l);
                lang.SelectedItem = lang.Items.Contains("eng") ? "eng" : lang.Items.Count > 0 ? lang.Items[0] : null;
                FillOriginals();
                LoadUsage();
            };
        }

        public string Check()
        {
            bool editable = !grid.Columns[ReplacementCol].ReadOnly && grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Name != ReplacementCol).All(c => c.ReadOnly);
            int used = results.Rows.Cast<DataGridViewRow>().Count(r => (r.Cells[UsedByCol].Value as string ?? "").Length > 0);
            string first = results.Rows.Count > 0 ? $"{results.Rows[0].Cells["Original Text"].Value} | {results.Rows[0].Cells[UsedByCol].Value}" : "";
            return $"replacement editable: {editable}; results {results.Rows.Count}, with Used By {used}; first: {first}";
        }

        public void SearchForTest(string text) { search.Text = text; Search(); }

        Task<StringUsage?>? usageTask;

        /// <summary>Starts building the index (once); Search waits for it, so hero names can be put first.</summary>
        Task<StringUsage?> UsageTask()
        {
            if (usageTask != null) return usageTask;
            string? root = f.game?.Root;
            return usageTask = root == null ? Task.FromResult<StringUsage?>(null) : Task.Run(() => StringUsage.Load(root));
        }

        async void LoadUsage()
        {
            usage = await UsageTask();
            FillUsage(results); FillUsage(grid);
        }

        /// <summary>The Used By column: the first use in plain words (+N more); every use in the cell's tooltip.</summary>
        void FillUsage(DataGridView g)
        {
            if (usage == null) return;
            foreach (DataGridViewRow r in g.Rows)
            {
                ulong id = r.Tag is (string, string, ulong i, string) ? i : ulong.TryParse(r.Cells["ID"].Value as string, out ulong j) ? j : 0;
                var uses = usage.For(id);
                var cell = r.Cells[UsedByCol];
                cell.Value = uses.Count == 0 ? "(not used by the game's data)" : StringUsage.Describe(uses[0]) + (uses.Count > 1 ? $"  (+{uses.Count - 1} more)" : "");
                cell.ToolTipText = uses.Count == 0 ? "" : string.Join("\n", uses.Take(25).Select(StringUsage.Describe)) + (uses.Count > 25 ? $"\n… {uses.Count - 25} more" : "");
                cell.Style.ForeColor = uses.Count > 0 && StringUsage.Rank(uses) == 0 ? Ui.TagCharacter : Ui.Subtle;
            }
        }

        async void Search()
        {
            if (f.catalog == null || lang.SelectedItem is not string l) return;
            string q = search.Text.Trim();
            if (q.Length < 2) return;
            found.Text = "Searching…";
            var u = usage ??= await UsageTask();
            var hits = await Task.Run(() =>
            {
                var all = f.catalog.Strings(l).Where(s => s.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Id.ToString() == q);
                // Hero names first, then costumes, team-ups, powers, NPCs, the rest (exact matches before partial ones).
                if (u != null) all = all.OrderBy(s => s.Text.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(s => StringUsage.Rank(u.For(s.Id)));
                return all.Take(501).ToList();
            });
            results.Rows.Clear();
            foreach (var h in hits.Take(500)) results.Rows[results.Rows.Add(h.Id.ToString(), h.Text)].Tag = (l, h.File, h.Id, h.Text);
            FillUsage(results);
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
            FillUsage(grid);
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
            catch (System.Text.Json.JsonException ex) { Dialog.Show(this, "Not a string file: " + ex.Message, "Import"); return; }
            FillOriginals();
            Dialog.Show(this, $"Imported {n} string(s) as language '{l}'.", "Import");
        }

        public List<StringReplacement> Collect()
        {
            grid.EndEdit();
            var list = new List<StringReplacement>();
            foreach (DataGridViewRow r in grid.Rows)
            {
                string l = (string)r.Cells["Lang"].Value!, file = (string)r.Cells["File"].Value!, text = r.Cells[ReplacementCol].Value as string ?? "";
                ulong id = ulong.Parse((string)r.Cells["ID"].Value!);
                // Keep an edited string's variants and flags as they were in the mod.
                var old = r.Tag as StringReplacement;
                list.Add(new StringReplacement(l, file, id, text, old?.FlagsProduced ?? 0, old?.Variants));
            }
            return list;
        }
    }
}
