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
    /// <summary>The last save was Save and Keep Editing (Kurt, 2026-10-17): the editor opens again on the saved mod, at the same tab.</summary>
    public bool KeepEditing { get; private set; }
    public event Action? Cancelled;
    public string Title { get; private set; }
    public Mod? Editing => editing;
    readonly ModLibrary lib;
    readonly GameState? game;
    readonly StockCatalog? catalog;
    Mod? editing;   // (Save and Keep Editing moves it to the saved mod: AfterSave)
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

        // ---- Name, author, version, tags and note: in Overview → Details (Kurt, 2026-10-17: above the tabs they took a fifth
        // of the screen on every tab)
        nameBox.Text = draft.Name; authorBox.Text = draft.Author; versionBox.Text = draft.Version;
        if (editing == null) authorBox.PlaceholderText = "Your name (Settings → Your Author Name fills it in)";
        Ui.Tip(authorBox, "Who made the mod. New mods start with the name in Settings → Your Author Name.");
        tagsBox.Text = string.Join(", ", draft.Tags);
        notesBox.Text = draft.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n");
        tagsBox.AutoCompleteMode = AutoCompleteMode.Append; tagsBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        tagsBox.AutoCompleteCustomSource.AddRange(lib.AllTags().ToArray());
        autoLabel.Text = editing != null && editing.AutoTags.Count > 0
            ? "Added automatically from the content: " + string.Join(", ", editing.AutoTags)
            : "Characters, teams, costume, powers … are added automatically from the content.";

        // Grouped (Kurt, 2026-10-02: a UI pass): Overview (description, packages), Icons (the icon packages nested),
        // Strings, Audio (voice first, then sound packs; Kurt 2026-10-02), then Powers and Animations on their own.
        texturePages = Enumerable.Range(0, 4).Select(k => new TexturePage(this, k)).ToArray();
        AddGroup("Overview", ("Details", DescriptionPage()), ("Packages", PackagesPage()));
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
        var save = Ui.AccentButton(editing == null ? "Create Mod" : "Save Changes", () => { KeepEditing = false; SaveAsked(); }, tip: "Save the mod to the library and go back to the mod list. Nothing in the game changes until Apply Changes.");
        var saveStay = Ui.FlatButton(editing == null ? "Create and Keep Editing" : "Save and Keep Editing", () => { KeepEditing = true; SaveAsked(); },
            tip: "Save the mod to the library and stay in the Editor, on the same tab. Nothing in the game changes until Apply Changes.");
        // icons (Kurt, 2026-10-04): the names head the tooltips
        float isc = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        Icons.Make(cancel, "Cancel", Icons.Cancel, isc);
        Icons.Make(post, "Create Post", Icons.Post, isc);
        Icons.Make(save, editing == null ? "Create Mod" : "Save Changes", Icons.Save, isc);
        Icons.Make(saveStay, editing == null ? "Create and Keep Editing" : "Save and Keep Editing", Icons.SaveStay, isc);
        buttons.Controls.AddRange([post, cancel, saveStay, save]);
        bottom.Controls.Add(buttons, 1, 0);
        Controls.Add(body); Controls.Add(bottom);
        Bars = [bottom];
        RefreshPackages(); RefreshSounds();
        // Unsaved changes (Kurt, 2026-10-18: a dot after the name on the Editor tab): what would be saved, looked at twice a
        // second against what was saved (or opened); the first look waits for the pages to fill
        dirtyTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            dirtyTimer.Interval = 500;
            string now = Fingerprint();
            if (savedPrint == null) { savedPrint = now; return; }
            SetDirty(now != savedPrint);
        };
        dirtyTimer.Start();
        Disposed += (_, _) => dirtyTimer.Dispose();
    }

    // ---- unsaved changes
    readonly System.Windows.Forms.Timer dirtyTimer = new() { Interval = 1500 };
    string? savedPrint;
    /// <summary>Something differs from the saved mod (the Editor tab shows a dot).</summary>
    public bool IsDirty { get; private set; }
    public event Action? DirtyChanged;
    void SetDirty(bool on) { if (on == IsDirty) return; IsDirty = on; DirtyChanged?.Invoke(); }
    /// <summary>Takes what the editor holds now as saved (after a save).</summary>
    void MarkSaved() { savedPrint = IsHandleCreated ? Fingerprint() : null; SetDirty(false); }
    internal string FingerprintForTest() => Fingerprint();
    internal string NameForTest { get => nameBox.Text; set => nameBox.Text = value; }
    internal string NotesForTest { get => notesBox.Text; set => notesBox.Text = value; }

    static readonly System.Text.Json.JsonSerializerOptions printJson = new() { IncludeFields = true };
    // the draft's fields the boxes hold until Save (Collect) and the ones that aren't the mod's content
    static readonly HashSet<string> boxFields = [nameof(ModDraft.Name), nameof(ModDraft.Author), nameof(ModDraft.Version), nameof(ModDraft.Description), nameof(ModDraft.Changes),
        nameof(ModDraft.NexusModId), nameof(ModDraft.PreviewImage), nameof(ModDraft.CardPicture), nameof(ModDraft.Tags), nameof(ModDraft.Notes), nameof(ModDraft.Strings), nameof(ModDraft.ModelFolder)];

    /// <summary>What a save would write, as text (doesn't change the draft: Collect does that on Save).</summary>
    string Fingerprint()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var box in new[] { nameBox, authorBox, versionBox, descriptionBox, changesBox, nexusBox, tagsBox, notesBox }) sb.Append(box.Text.Replace("\r\n", "\n").Trim()).Append('\u0001');
        sb.Append(previewBox.Items.Count > 0 ? (previewBox.SelectedIndex > 0 && previewBox.SelectedIndex <= previewKeys.Count ? previewKeys[previewBox.SelectedIndex - 1] : null) : draft.PreviewImage).Append('\u0001');
        sb.Append(cardBox.Items.Count > 0 ? (cardBox.SelectedIndex > 0 && cardBox.SelectedIndex <= cardKeys.Count ? cardKeys[cardBox.SelectedIndex - 1] : null) : draft.CardPicture).Append('\u0001');
        foreach (var f in typeof(ModDraft).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            if (!boxFields.Contains(f.Name)) sb.Append(f.Name).Append('=').Append(System.Text.Json.JsonSerializer.Serialize(f.GetValue(draft), printJson)).Append('\u0001');
        stringsPage.AppendKey(sb);
        foreach (var l in voiceLines) if (IsOn(l) == l.Off) sb.Append(l.Package).Append(l.Offset).Append(';');
        sb.Append(ModelUnbuilt);
        return sb.ToString();
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

    /// <summary>
    /// Save and Keep Editing (Kurt, 2026-10-18: the editor closed and opened again on every save, slow): the same editor goes
    /// on with the saved mod. The draft is read again from it (its files are in the mod's folder now: the work copies are gone),
    /// and the pages that list files read them again; the 3D views keep their model, pose and camera.
    /// </summary>
    public void AfterSave(Mod saved)
    {
        var fresh = ModDraft.From(saved);
        foreach (var f in typeof(ModDraft).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            if (f.Name != nameof(ModDraft.ModelFolder)) f.SetValue(draft, f.GetValue(fresh));   // (the Model tab's work folder stays: it is what was saved)
        editing = saved;
        Title = $"Edit: {saved.Name}";
        autoLabel.Text = saved.AutoTags.Count > 0
            ? "Added automatically from the content: " + string.Join(", ", saved.AutoTags)
            : "Characters, teams, costume, powers … are added automatically from the content.";
        RefreshPackages(); RefreshSounds();
        foreach (var t in texturePages) t.Refresh();
        voiceWanted.Clear();
        if (voice != null) RefreshVoice();
        powerBuilt = saved.Manifest.PowerColors?.SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        powerPreview?.Rebind(saved);
        animPreview?.Rebind(saved);
        if (animGrid != null) LoadAnimations();
        FillGhosts();
        MarkSaved();
        Saves++;
    }
    /// <summary>How many saves this editor went on after (tests).</summary>
    public int Saves { get; private set; }
    internal IEnumerable<string> DraftSourcesForTest => draft.Packages.Select(p => p.Source);

    // ---- Save
    void Save()
    {
        Collect();
        draft.Strings = stringsPage.Collect();
        string? work, powerWork, borrowWorkSave = null;
        try { work = ApplyVoice(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, "The voice lines couldn't be changed: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        var powerLog = new List<string>();
        HashSet<string> borrowed;
        try { (borrowed, borrowWorkSave) = ApplyPowerBorrows(powerLog); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
            Dialog.Show(this, "The borrowed power looks couldn't be made: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        try { ApplyGhosts(powerLog); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
            Dialog.Show(this, "The dodge ghosts couldn't be made: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        try { powerWork = ApplyPowerColors(powerLog, borrowed); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
            Dialog.Show(this, "The power colors couldn't be made: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        string? partsWork;
        try { partsWork = ApplyParts(powerLog); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            foreach (string? w in new[] { work, powerWork }) if (w != null) try { Directory.Delete(w, true); } catch (IOException) { }
            Dialog.Show(this, "The parts couldn't be switched off: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        string? uniqueWork;
        try { uniqueWork = MakeDraftUnique(powerLog); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException)
        {
            foreach (string? w in new[] { work, powerWork, partsWork }) if (w != null) try { Directory.Delete(w, true); } catch (IOException) { }
            Dialog.Show(this, "The mod's models couldn't be made unique: " + ex.Message, "Can't Save Yet", MessageBoxButtons.OK, MessageBoxIcon.Error); return;
        }
        string? saved = ModWriter.Save(lib, draft, editing, out string? error);
        if (uniqueWork != null) try { Directory.Delete(uniqueWork, true); } catch (IOException) { }
        if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
        if (powerWork != null) try { Directory.Delete(powerWork, true); } catch (IOException) { }
        if (partsWork != null) try { Directory.Delete(partsWork, true); } catch (IOException) { }
        foreach (string? bw in new[] { borrowWorkSave, saved != null ? borrowWork : null })
            if (bw != null) try { Directory.Delete(bw, true); } catch (IOException) { }
        if (saved != null) borrowWork = null;
        if (saved != null && voiceWork != null) try { Directory.Delete(voiceWork, true); voiceWork = null; } catch (IOException) { }
        if (saved != null && animWork != null) try { Directory.Delete(animWork, true); animWork = null; } catch (IOException) { }
        if (saved == null) { Dialog.Show(this, error ?? "", "Can't Save Yet"); return; }
        SavedName = saved;
        OfferAuthorName();
        Saved?.Invoke(saved);
    }

    /// <summary>
    /// Every Save makes the mod's character packages unique (Kurt, 2026-10-11: mods made here never get the OVERLAP warning):
    /// a changed model or material instance that other stock packages also carry gets paths of its own (UniqueNames), so a
    /// stock copy loaded first can't show instead (a Model tab build into Jessica's team-up is jessica_jones.jessicajones_tv,
    /// like her agent's and NPC's). A package already unique, or one that ships every copy, is left as it is. The work folder.
    /// </summary>
    string? MakeDraftUnique(List<string> log)
    {
        if (game == null || draft.KeepShared) return null;   // (Overview → Keep Shared Paths)
        var files = draft.Packages.Where(p => UniqueNames.Covers(p.File) && File.Exists(p.Source)).ToList();
        if (files.Count == 0) return null;
        var shared = UniqueNames.Shared(UniqueNames.Folder(game.Cooked));
        var shipped = draft.Packages.Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string tag = UniqueNames.Tag(draft.Name), work = Path.Combine(lib.DataFolder, "make-unique-" + Guid.NewGuid().ToString("N")[..8]);
        bool any = false;
        foreach (var (file, src) in files)
        {
            var plan = UniqueNames.PlanFor(src, StockFiles.For(game.Cooked, file), file, shared, shipped, tag, UniqueNames.OwnFiles(file, game.Cooked), game.Cooked);
            if (!plan.Changes) continue;
            Directory.CreateDirectory(work);
            string dst = Path.Combine(work, file);
            byte[] built = UniqueNames.Build(src, plan);
            File.WriteAllBytes(dst, built);
            draft.BeforeUnique[file] = (src, UniqueNames.Sha(built));   // (Make Unique's undo copy: the package before this rename)
            int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
            draft.Packages[k] = (file, dst);
            log.AddRange(plan.Notes.Select(n => $"unique: {file}: {n}"));
            any = true;
        }
        return any ? work : null;
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

    /// <summary>A caption above a field: "NAME  ·  what it is" (bold, subtle).</summary>
    Label FieldCaption(string text) => new() { Text = text, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, (int)(10 * S), 0, (int)(3 * S)) };

    /// <summary>Overview → Details: the mod's name, author, version, tags and note, its description and this version's
    /// changes (left), and its Nexus page, pictures and Keep Shared Paths (right). The text boxes are sized to what they
    /// usually hold (Kurt, 2026-10-17: they filled the page); they scroll beyond that.</summary>
    Control DescriptionPage()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };   // (scrolls only when the window is too short for it)
        var page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, (int)(2 * S), (int)(6 * S), 0) };
        scroll.Controls.Add(page);
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, (int)(24 * S), 0) };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var side = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(p, 0, 0); page.Controls.Add(side, 1, 0);
        void Add(TableLayoutPanel t, Control c) { t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.Controls.Add(c, 0, t.RowCount++); }
        TextBox Sized(TextBox b, int lines)
        {
            b.Dock = DockStyle.None; b.Anchor = AnchorStyles.Left | AnchorStyles.Right;   // (Fill would shrink it to one line in an auto-sized row)
            b.Height = lines * TextRenderer.MeasureText("Ag", b.Font).Height + (int)(12 * S);
            b.MinimumSize = new Size(0, b.Height);   // (its frame takes the minimum along: Modern.Wrap)
            b.Margin = new Padding(0);
            return b;
        }

        // name, author, version on one row
        var ids = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Margin = new Padding(0) };
        ids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); ids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); ids.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        int col = 0;
        foreach (var (cap, box) in new[] { ("NAME", nameBox), ("AUTHOR", authorBox), ("VERSION", versionBox) })
        {
            var c = FieldCaption(cap); c.Margin = new Padding(0, (int)(4 * S), 0, (int)(3 * S));
            ids.Controls.Add(c, col, 0);
            box.Dock = DockStyle.Fill; box.Margin = new Padding(0, 0, col < 2 ? (int)(12 * S) : 0, 0);
            ids.Controls.Add(box, col, 1);
            col++;
        }
        Add(p, ids);
        Add(p, FieldCaption("TAGS  ·  separate with commas; they go with the mod"));
        tagsBox.Dock = DockStyle.Fill; tagsBox.Margin = new Padding(0);
        Add(p, tagsBox);
        autoLabel.Margin = new Padding(0, (int)(3 * S), 0, 0);
        Add(p, autoLabel);
        Add(p, FieldCaption("NOTE  ·  shown under the mod's picture in the mod list (it travels with the mod)"));
        Add(p, Sized(notesBox, 2));
        Add(p, FieldCaption("DESCRIPTION  ·  what the mod is, for its Nexus / Discord post (it travels with the mod)"));
        Add(p, Sized(descriptionBox, 8));
        changesCaption.Margin = new Padding(0, (int)(10 * S), 0, (int)(3 * S));
        Add(p, changesCaption);
        Add(p, Sized(changesBox, 5));
        var older = draft.Changelog.Where(e => !e.Version.Trim().Equals(draft.Version.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        Add(p, new Label { Text = older.Count > 0 ? "Earlier versions: " + string.Join(", ", older.Select(e => "v" + e.Version.TrimStart('v', 'V'))) + " (kept in the changelog)" : "Each version's changes are kept in the mod's changelog.", AutoSize = true, Tag = "subtle", Margin = new Padding(0, (int)(4 * S), 0, 0) });

        // right: Nexus page, pictures, Keep Shared Paths
        var nexusCap = FieldCaption("NEXUS PAGE  ·  address or mod number (users get update notices)"); nexusCap.Margin = new Padding(0, (int)(4 * S), 0, (int)(3 * S));
        Add(side, nexusCap);
        nexusBox.Dock = DockStyle.Fill; nexusBox.Margin = new Padding(0);
        Add(side, nexusBox);
        nexusBox.Text = draft.NexusModId is int nid ? Nexus.SiteMods + nid : "";
        Add(side, FieldCaption("PREVIEW IMAGE  ·  shown big in the manager (users can pick their own)"));
        previewBox.Margin = new Padding(0);
        Add(side, previewBox);
        Ui.Tip(previewBox, "What the manager shows big for this mod: one of its own images, the game's original, or a 3D view of one of its meshes. Automatic: the mod's first store image, else the game's store image.");
        FillPreviewChoices();
        previewBox.Opening += (_, _) => FillPreviewChoices();
        previewBox.SelectedIndexChanged += (_, _) =>
        {
            if (previewBox.SelectedItem as string != CustomChoice) { previewWas = previewBox.SelectedIndex; return; }
            if (ChooseCustomPicture("Preview Picture") is string key) { draft.PreviewImage = key; FillPreviewChoices(); previewBox.SelectedIndex = previewWas = previewKeys.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) + 1; }
            else previewBox.SelectedIndex = previewWas;
        };
        Add(side, FieldCaption("CARD PICTURE  ·  the mod's picture in the list (users can pick their own)"));
        cardBox.Margin = new Padding(0);
        Add(side, cardBox);
        Ui.Tip(cardBox, "Which of the mod's images its card in the mod list shows (handy when it has many), or a custom picture of your own. Automatic: its hero portrait, else costume icon, else store image.");
        FillCardChoices();
        cardBox.Opening += (_, _) => FillCardChoices();
        cardBox.SelectedIndexChanged += (_, _) =>
        {
            if (cardBox.SelectedItem as string != CustomChoice) { cardWas = cardBox.SelectedIndex; return; }
            if (ChooseCustomPicture("Card Picture") is string key) FillCardChoices(key);
            else cardBox.SelectedIndex = cardWas;
        };
        // Keep Shared Paths (a user, 2026-10-13): some mods want the stock path shared (a modded S.H.I.E.L.D. agent package
        // also changed Nick Fury's summoned agents, whose copy is in his own package; Make Unique took that away).
        var keepShared = new CheckBox { Text = "Keep Shared Paths  ·  don't make this mod's models unique", AutoSize = true, Checked = draft.KeepShared, Margin = new Padding(0, (int)(16 * S), 0, 0) };
        var keepNote = new Label { AutoSize = true, Tag = "subtle", Margin = new Padding((int)(20 * S), 2, 0, 0), MaximumSize = new Size((int)(600 * S), 0) };
        void KeepNote() => keepNote.Text = keepShared.Checked
            ? "On: Save leaves the mod's model, material and effect paths as the game's. Impact: Wherever another file carries the same path (a summon, a team-up, an NPC, a boss's power), the game shows whichever copy loads first, so your look can spread to those characters (useful for a summoner's copy of an agent) or the game's look can show instead of yours near other players and in hubs. The OVERLAP warning isn't shown for this mod, and it travels with the mod. A CRASH RISK warning still shows."
            : "Off (recommended): Save gives the mod's changed models, materials and effects paths of their own, so the game's copies elsewhere can't replace your look. Impact: Other characters that share those paths keep the game's look, including summons, team-ups and NPCs whose copy lives in another file (Nick Fury's summoned agents stay stock when you change the agents' own package).";
        KeepNote();
        keepShared.CheckedChanged += (_, _) => { draft.KeepShared = keepShared.Checked; KeepNote(); };
        Ui.Tip(keepShared, "On: Save leaves this mod's paths shared with the game's other copies (no Make Unique, no OVERLAP warning): Your look can reach other characters that share them, or the game's can show instead of yours. Off: Save makes the mod's changed models unique. See the note under it.");
        var keepBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        keepBox.Controls.Add(keepShared); keepBox.Controls.Add(keepNote);
        Add(side, keepBox);
        // the wrapped notes follow their column's width
        side.SizeChanged += (_, _) => keepNote.MaximumSize = new Size(Math.Max(100, side.ClientSize.Width - keepNote.Margin.Horizontal - (int)(8 * S)), 0);
        p.SizeChanged += (_, _) => autoLabel.MaximumSize = new Size(Math.Max(100, p.ClientSize.Width - (int)(8 * S)), 0);
        descriptionBox.Text = draft.Description.Replace("\r\n", "\n").Replace("\n", "\r\n");
        changesBox.Text = draft.Changes.Replace("\r\n", "\n").Replace("\n", "\r\n");
        void Caption() => changesCaption.Text = $"CHANGES IN THIS VERSION{(versionBox.Text.Trim().Length > 0 ? " (v" + versionBox.Text.Trim().TrimStart('v', 'V') + ")" : "")}  ·  what's new, shown in the post";
        Caption();
        changesCaption.Font = Ui.Bold(8.5f);
        versionBox.TextChanged += (_, _) => Caption();
        return scroll;
    }

}
