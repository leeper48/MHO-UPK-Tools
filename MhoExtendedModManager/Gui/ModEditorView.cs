using System.Text.RegularExpressions;
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
sealed partial class ModEditorView : UserControl
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
    readonly DropDown previewBox = new() { Width = 460 };
    List<string> previewKeys = [];   // parallel to previewBox's items after "Automatic"
    // The card picture in the mod list (manifest CardPicture; Kurt: authors choose it; users can pick their own).
    readonly DropDown cardBox = new() { Width = 460 };
    List<string> cardKeys = [];      // parallel to cardBox's items after "Automatic"
    const string CustomChoice = "Custom Image (Choose a File)";
    int previewWas, cardWas;

    /// <summary>"Custom Image" picked in one of the two picture drop-downs: a file becomes one of the mod's own pictures.</summary>
    string? ChooseCustomPicture(string what)
    {
        using var d = new OpenFileDialog { Title = $"{what} for {draft.Name}", Filter = ModPictures.DialogFilter };
        if (d.ShowDialog(this) != DialogResult.OK) return null;
        string stem = ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(d.FileName)), ext = Path.GetExtension(d.FileName).ToLowerInvariant();
        string file = $"{ModPictures.Folder}/{stem}{ext}";
        for (int n = 2; draft.Pictures.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase) && !p.Source.Equals(d.FileName, StringComparison.OrdinalIgnoreCase)); n++) file = $"{ModPictures.Folder}/{stem}_{n}{ext}";
        if (!draft.Pictures.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase))) draft.Pictures.Add((file, d.FileName));
        return ModPictures.Prefix + file;
    }

    /// <summary>The Card Picture choices: automatic, the mod's images (the texture tabs now), its own custom pictures.</summary>
    void FillCardChoices(string? select = null)
    {
        string? keep = select ?? (cardBox.SelectedIndex > 0 && cardBox.SelectedIndex <= cardKeys.Count ? cardKeys[cardBox.SelectedIndex - 1] : draft.CardPicture);
        cardKeys = [.. draft.Textures[0].Select(t => t.Texture).Concat(draft.Extra.Select(t => t.Texture)).Concat(draft.Textures[2].Select(t => t.Texture)).Concat(draft.Textures[1].Select(t => t.Texture))
            .Concat(draft.Pictures.Select(p => ModPictures.Prefix + p.File)).Distinct(StringComparer.OrdinalIgnoreCase)];
        cardBox.BeginUpdate();
        cardBox.Items.Clear();
        cardBox.Items.Add("Automatic (hero portrait, costume icon, store image)");
        foreach (string k in cardKeys) cardBox.Items.Add(ModPictures.IsFile(k) ? ModPictures.Label(k) + "  ·  Custom Image" : k);
        cardBox.Items.Add(CustomChoice);
        int i = keep == null ? -1 : cardKeys.FindIndex(k => k.Equals(keep, StringComparison.OrdinalIgnoreCase));
        cardBox.SelectedIndex = cardWas = i + 1;
        cardBox.EndUpdate();
    }
    readonly TextBox notesBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f), Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly Label autoLabel = new() { AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left, Font = Ui.Regular(8.5f), Margin = new Padding(3, 2, 3, 6) };
    readonly TextBox nameBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) }, authorBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) }, versionBox = new() { Dock = DockStyle.Fill, Font = Ui.Regular(10.5f) };
    readonly DataGridView packages, sounds;
    // ---- Voice (Kurt: turn lines of a voice set off)
    DataGridView voice = null!;
    readonly TextBox voiceFind = new() { Width = 240 };
    readonly Label voiceCount = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    List<VoiceLine> voiceLines = [];
    /// <summary>The on / off the user wants per line (package, offset); lines not here are as read.</summary>
    readonly Dictionary<(string, int), bool> voiceWanted = [];
    readonly TexturePage[] texturePages;
    readonly StringsPage stringsPage;
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    float S => MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
    /// <summary>The info and bottom bars (the host colours them as bars after theming).</summary>
    public Control[] Bars { get; private set; } = [];

    public ModEditorView(ModLibrary lib, GameState? game, Mod? editing)
    {
        this.lib = lib; this.game = game; this.editing = editing;
        catalog = game == null ? null : new StockCatalog(lib, game);
        draft = editing == null ? new ModDraft { Author = Settings.Load().AuthorName?.Trim() ?? "" } : ModDraft.From(editing);
        Title = editing == null ? "New Mod" : $"Edit: {editing.Name}";
        Dock = DockStyle.Fill;
        Font = Ui.Regular(9.5f);
        packages = Ui.Grid(S, false, ("Package", 0), ("Size", 90), ("From", 520));
        sounds = Ui.Grid(S, false, ("Sound Pack", 0), ("Events", 80), ("Sound Files It Patches", 360), ("From", 360));

        // ---- Info bar: name, author, version
        var info = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 7, Padding = new Padding(12, 10, 12, 10) };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        info.Controls.Add(Caption("Name"), 0, 0); info.Controls.Add(nameBox, 1, 0);
        info.Controls.Add(Caption("Author"), 2, 0); info.Controls.Add(authorBox, 3, 0);
        info.Controls.Add(Caption("Version"), 4, 0); info.Controls.Add(versionBox, 5, 0);
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // fold Tags and Note away (Kurt, 2026-10-06: on a small screen they took a third of the Model tab's room); kept
        var foldInfo = Ui.FlatButton("▴", () => { }, tip: "Hide or show the Tags and Note rows (more room for the tabs below). Remembered.");
        foldInfo.Margin = new Padding(8, 0, 0, 0);
        info.Controls.Add(foldInfo, 6, 0);
        void ShowInfo()
        {
            bool folded = MhoExtendedModManager.Model.Settings.Current.Folded.Contains("editorinfo");
            foreach (Control c in info.Controls) if (info.GetRow(c) > 0) c.Visible = !folded;
            foldInfo.Text = folded ? "▾" : "▴";
        }
        foldInfo.Click += (_, _) =>
        {
            var f = MhoExtendedModManager.Model.Settings.Current.Folded;
            if (!f.Remove("editorinfo")) f.Add("editorinfo");
            MhoExtendedModManager.Model.Settings.Current.Save();
            ShowInfo();
        };
        HandleCreated += (_, _) => ShowInfo();
        nameBox.Text = draft.Name; authorBox.Text = draft.Author; versionBox.Text = draft.Version;
        if (editing == null) authorBox.PlaceholderText = "Your name (Settings → Your Author Name fills it in)";
        Ui.Tip(authorBox, "Who made the mod. New mods start with the name in Settings → Your Author Name.");
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

        // Grouped (Kurt, 2026-10-02: a UI pass): Overview (description, packages), Icons (the icon packages nested),
        // Strings, Audio (voice first, then sound packs; Kurt 2026-10-02), then Powers and Animations on their own.
        texturePages = Enumerable.Range(0, 4).Select(k => new TexturePage(this, k)).ToArray();
        AddGroup("Overview", ("Description", DescriptionPage()), ("Packages", PackagesPage()));
        AddGroup("Icons", ("Icons", texturePages[0]), ("Store Images", texturePages[2]), ("Achievements", texturePages[1]), ("Other Packages", texturePages[3]));
        stringsPage = new StringsPage(this);
        AddGroup("Strings", ("Strings", stringsPage));
        var soundsPage = SoundsPage();   // built before the voice page, as before (the voice page refreshes the sound packs' list)
        AddGroup("Audio", ("Voice", VoicePage()), ("Sound Packs", soundsPage));
        // Not released yet (Kurt, 2026-10-01): only with "PreviewFeatures": true in settings.json. A mod's existing power colors
        // are kept and rebuilt on save either way.
        if (WhatsNew.PowersAndAnimations) AddGroup("Powers", ("Powers", PowersPage()));
        // In development (Kurt, 2026-10-02): the Animations tab, also only with "PreviewFeatures": true.
        if (WhatsNew.PowersAndAnimations) AddGroup("Animations", ("Animations", AnimationsPage()));
        // In development (Kurt, 2026-10-03): the Model tab, the MFF model importer inside the editor.
        if (WhatsNew.ModelTab) { modelTop = tabs.Count; AddGroup("Model", ("Model", ModelTabPage())); WatchModelLeave(); }
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 0) };
        body.Controls.Add(tabs);

        // ---- Bottom bar
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(12, 8, 12, 8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(new Label { Text = editing == null ? "New mods are added at the top of the list, turned off. Nothing in the game changes until Apply Changes." : "Saving replaces the mod's folder (the old one goes to the Recycle Bin). Nothing in the game changes until Apply Changes.", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" }, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var cancel = Ui.FlatButton("Cancel", () => Cancelled?.Invoke(), tip: "Close the editor without saving (the mod stays as it was).");
        var post = Ui.FlatButton("Create Post", CreatePost, tip: "Make the Nexus and Discord posts for this mod (text and pictures); they are kept with the mod.");
        var save = Ui.AccentButton(editing == null ? "Create Mod" : "Save Changes", SaveAsked, tip: "Save the mod to the library. Nothing in the game changes until Apply Changes.");
        // icons (Kurt, 2026-10-04): the names head the tooltips
        float isc = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        Icons.Make(cancel, "Cancel", Icons.Cancel, isc);
        Icons.Make(post, "Create Post", Icons.Post, isc);
        Icons.Make(save, editing == null ? "Create Mod" : "Save Changes", Icons.Save, isc);
        buttons.Controls.AddRange([post, cancel, save]);
        bottom.Controls.Add(buttons, 1, 0);
        Controls.Add(body); Controls.Add(info); Controls.Add(bottom);
        Bars = [info, bottom];
        RefreshPackages(); RefreshSounds();
    }

    // Null-safe: the tooltip audit opens an editor on an empty library (crashed 0.35.22 test run).

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
    Control PackagesPage() => Page(packages, Toolbar(Ui.FlatButton("Add .UPK Files", AddPackages, tip: "Add game packages (.UPK) this mod replaces; each file name must match the package it replaces."), Ui.Tip(Ui.FlatButton("Remove", () =>
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
        RefreshVoice();
    }

    // ---- Save
    void Save()
    {
        Collect();
        draft.Strings = stringsPage.Collect();
        string? work, powerWork;
        try { work = ApplyVoice(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, "The voice lines couldn't be changed: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        var powerLog = new List<string>();
        try { powerWork = ApplyPowerColors(powerLog); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
            Dialog.Show(this, "The power colors couldn't be made: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        string? saved = ModWriter.Save(lib, draft, editing, out string? error);
        if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
        if (powerWork != null) try { Directory.Delete(powerWork, true); } catch (IOException) { }
        if (saved != null && voiceWork != null) try { Directory.Delete(voiceWork, true); voiceWork = null; } catch (IOException) { }
        if (saved != null && animWork != null) try { Directory.Delete(animWork, true); animWork = null; } catch (IOException) { }
        if (saved == null) { Dialog.Show(this, error ?? "", "Can't Save Yet"); return; }
        SavedName = saved;
        OfferAuthorName();
        Saved?.Invoke(saved);
    }

    /// <summary>A new mod saved with an author while no author name is set: offers to keep it for new mods (once per name).</summary>
    void OfferAuthorName()
    {
        string author = draft.Author.Trim();
        if (editing != null || author.Length == 0) return;
        var settings = Settings.Load();
        if (!string.IsNullOrWhiteSpace(settings.AuthorName)) return;
        if (Dialog.Show(this, $"Fill in \"{author}\" as the author of every new mod you make?\n\nYou can change it any time in Settings → Your Author Name.",
                "Your Author Name", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        settings.AuthorName = author;
        settings.Save();
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
        previewKeys = [.. draft.Pictures.Select(p => ModPictures.Prefix + p.File), .. pics.Select(c => c.Key), .. meshes.Select(m => m.Key)];
        previewBox.BeginUpdate();
        previewBox.Items.Clear();
        previewBox.Items.Add("Automatic (the first store image)");
        foreach (var pic in draft.Pictures) previewBox.Items.Add($"{ModPictures.Label(ModPictures.Prefix + pic.File)}  ·  Custom Image");
        foreach (var c in pics) previewBox.Items.Add($"{c.Texture}  ·  {c.Source}");
        foreach (var m in meshes) previewBox.Items.Add($"3D: {m.Name}  ·  {m.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}");
        int i = keep == null ? -1 : previewKeys.FindIndex(k => k.Equals(keep, StringComparison.OrdinalIgnoreCase));
        previewBox.Items.Add(CustomChoice);
        previewBox.SelectedIndex = previewWas = i + 1;
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
        if (cardBox.Items.Count > 0) draft.CardPicture = cardBox.SelectedIndex > 0 && cardBox.SelectedIndex <= cardKeys.Count ? cardKeys[cardBox.SelectedIndex - 1] : null;
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
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(0, 8, 0, 0) };
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
        previewBox.Opening += (_, _) => FillPreviewChoices();
        previewBox.SelectedIndexChanged += (_, _) =>
        {
            if (previewBox.SelectedItem as string != CustomChoice) { previewWas = previewBox.SelectedIndex; return; }
            if (ChooseCustomPicture("Preview Picture") is string key) { draft.PreviewImage = key; FillPreviewChoices(); previewBox.SelectedIndex = previewWas = previewKeys.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) + 1; }
            else previewBox.SelectedIndex = previewWas;
        };
        var cardRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        cardRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); cardRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cardRow.Controls.Add(new Label { Text = "CARD PICTURE  ·  the mod's picture in the list (users can pick their own)", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        cardRow.Controls.Add(cardBox, 1, 0);
        Ui.Tip(cardBox, "Which of the mod's images its card in the mod list shows (handy when it has many), or a custom picture of your own. Automatic: its hero portrait, else costume icon, else store image.");
        FillCardChoices();
        cardBox.Opening += (_, _) => FillCardChoices();
        cardBox.SelectedIndexChanged += (_, _) =>
        {
            if (cardBox.SelectedItem as string != CustomChoice) { cardWas = cardBox.SelectedIndex; return; }
            if (ChooseCustomPicture("Card Picture") is string key) FillCardChoices(key);
            else cardBox.SelectedIndex = cardWas;
        };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(cardRow, 0, 7);
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

}
