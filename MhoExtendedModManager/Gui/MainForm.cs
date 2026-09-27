using System.Diagnostics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The main window, laid out like MHModManager's: a top bar (game folder, + New Mod, Extract, Install, Settings), the
/// mods as cards in priority order on the left (top wins; ▲▼ to reorder, checkbox to enable), the selected mod on the
/// right (header with badges and the Enabled pill, tabs: Packages / Textures / Strings / Sound packs / Conflicts / Info),
/// and a bottom bar (Remove, Edit, Export; Apply Changes). Everything writes through the same code as the CLI; the
/// game folder only changes on Apply. A library that is still MHModManager's own folder is shown read-only.
/// </summary>
sealed class MainForm : Form
{
    readonly Settings settings = Settings.Load();
    ModLibrary? lib;
    GameState? game;
    Dictionary<string, Mod> winners = [];
    HashSet<Mod> conflicted = [];
    Task? loading, pending;
    bool readOnly;
    readonly List<Control> writeControls = [];
    int thumbRequest;

    readonly Label gameLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(9.75f) };
    readonly Label runningLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(12, 0, 0, 0), Font = Ui.Regular(9f) };
    readonly ModListBox list = new() { Dock = DockStyle.Fill };
    readonly TextBox filter = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
    readonly Label countLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(8.25f), Padding = new Padding(4, 0, 0, 0) };
    readonly DetailsHeader header = new() { Dock = DockStyle.Top };
    readonly StorePreview storePreview = new() { Dock = DockStyle.Fill };
    // Middle column: the store image, and the note under it (the mod's note, or the user's own on this PC).
    readonly Panel middle = new() { Dock = DockStyle.Left };
    readonly TableLayoutPanel notesPanel = new() { Dock = DockStyle.Bottom, ColumnCount = 2, RowCount = 2, Padding = new Padding(6, 6, 6, 4) };
    readonly TextBox noteBox = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, Font = Ui.Regular(9.25f) };
    readonly Label noteSource = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(8f), Tag = "subtle" };
    readonly Button noteReset;
    Mod? noteMod;
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(9f), Padding = new Padding(12, 0, 0, 0) };
    readonly Button applyButton;
    // Dark hover tips on the buttons and boxes (the mod list shows its own, per part of a card).
    readonly ToolTip tips = Ui.NewTips();
    // Undo / redo of list changes (on/off, order, locks, tags): state.json as it was before each change, with a label.
    // Cleared when the set of mods changes (install, remove, rename): an older state.json would put those in the wrong place.
    readonly List<(string Json, string Label)> undo = [], redo = [];
    string? foldersKey;
    // A note for the status line, shown by the next Reload (a later status update would otherwise overwrite it).
    string? note;
    readonly Button undoButton, redoButton, sortButton, groupButton, tagsButton;
    readonly CheckBox allVisible = new() { Text = "All", AutoSize = true, AutoCheck = false, ThreeState = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 3, 2, 0) };
    readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Button> priorityButtons = [];
    readonly Font mono = new("Consolas", 9.5f);
    List<Mod> shown = [];
    Control? topBar, bottomBar;
    // Top-level tabs (Kurt: the editor and Extract as tabs, not pop-up windows).
    readonly FlatTabs pages = new() { Dock = DockStyle.Fill, TabPoints = 11f };
    readonly Panel editorHost = new() { Dock = DockStyle.Fill }, extractHost = new() { Dock = DockStyle.Fill };
    readonly Control editorPlaceholder;
    ModEditorView? editor;
    ExtractView? extract;
    string? extractKey;

    public MainForm()
    {
        Text = $"MHO Extended Mod Manager v{Program.Version}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 1400; Height = 850; WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Regular(9.5f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        applyButton = Ui.AccentButton("Apply Changes", Apply);

        // ---- Top bar
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(10, 6, 10, 6) };
        topBar = top;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Game Root:", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Font = Ui.Regular(9.75f) }, 0, 0);
        top.Controls.Add(gameLabel, 1, 0);
        top.Controls.Add(runningLabel, 2, 0);
        var topButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var newMod = Ui.AccentButton("+  New Mod", () => EditMod(null));
        var extractButton = Ui.FlatButton("Extract", () => pages.Select(2));
        var install = Ui.FlatButton("Install Mod…", InstallMod);
        var settingsButton = Ui.FlatButton("Settings  ▾", () => { });
        var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f), RenderMode = ToolStripRenderMode.System };
        menu.Items.Add("Change Game Folder…", null, (_, _) => BrowseGame());
        menu.Items.Add("Move Library…", null, (_, _) => MoveLibrary());
        menu.Items.Add("Open Library Folder", null, (_, _) => { if (Settings.LibraryData(settings.LibraryPath) is string d) Process.Start("explorer.exe", $"\"{d}\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Capture Icon Changes", null, (_, _) => CaptureIcons());
        menu.Items.Add("Migrate from MHModManager…", null, (_, _) => Migrate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, (_, _) => Reload());
        menu.Items.Add("Check for Updates…", null, (_, _) => CheckForUpdates(manual: true));
        var autoCheck = new ToolStripMenuItem("Check for Updates at Start") { CheckOnClick = true };
        autoCheck.CheckedChanged += (_, _) => { if (settings.CheckUpdates != autoCheck.Checked) { settings.CheckUpdates = autoCheck.Checked; settings.Save(); } };
        menu.Opening += (_, _) => autoCheck.Checked = settings.CheckUpdates;
        menu.Items.Add(autoCheck);
        menu.Items.Add("About", null, (_, _) => About());
        settingsButton.Click += (_, _) => menu.Show(settingsButton, new Point(0, settingsButton.Height));
        topButtons.Controls.AddRange([newMod, extractButton, install, settingsButton]);
        writeControls.AddRange([newMod, install]);
        tips.SetToolTip(newMod, "Make a new mod from packages, icons, store images, strings or sound packs (opens the Editor tab).");
        tips.SetToolTip(extractButton, "Save original game icons, store images or strings, to make replacements from.");
        tips.SetToolTip(install, "Add a mod from a .zip, .7z, .rar or folder. You can also drop it on the window.");
        tips.SetToolTip(settingsButton, "Game folder, library folder, capture icon changes, migrate from MHModManager, about.");
        tips.SetToolTip(runningLabel, "Changes can only be applied while the game is closed.");
        top.Controls.Add(topButtons, 3, 0);

        // ---- Left: installed mods
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6, 4, 2, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var lhead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 9, Margin = new Padding(0) };
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lhead.Controls.Add(new Label { Text = "INSTALLED MODS", AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Bold(8.5f), Tag = "subtle" }, 0, 0);
        lhead.Controls.Add(countLabel, 1, 0);
        undoButton = Ui.FlatButton("Undo", Undo); redoButton = Ui.FlatButton("Redo", Redo);
        undoButton.Padding = redoButton.Padding = new Padding(2, 0, 2, 0);
        lhead.Controls.Add(undoButton, 2, 0); lhead.Controls.Add(redoButton, 3, 0);
        lhead.Controls.Add(new Label { Text = "Priority:", AutoSize = true, Anchor = AnchorStyles.Right, Font = Ui.Regular(8.5f), Tag = "subtle", Margin = new Padding(10, 0, 0, 0) }, 4, 0);
        var first = Ui.FlatButton("▲", () => MoveSelected(-1, toEnd: true)); var up = Ui.FlatButton("▲", () => MoveSelected(-1));
        var down = Ui.FlatButton("▼", () => MoveSelected(1)); var last = Ui.FlatButton("▼", () => MoveSelected(1, toEnd: true));
        Ui.AddEndBar(first, top: true); Ui.AddEndBar(last, top: false);   // ▲ with a bar over it = to the top; ▼ with one under it = to the bottom
        tips.SetToolTip(first, "To the top (below any mods locked there)."); tips.SetToolTip(up, "Up one. Higher mods win where two change the same thing.");
        tips.SetToolTip(down, "Down one."); tips.SetToolTip(last, "To the bottom (above any mods locked there).");
        first.Padding = up.Padding = down.Padding = last.Padding = new Padding(2, 0, 2, 0);
        lhead.Controls.Add(first, 5, 0); lhead.Controls.Add(up, 6, 0); lhead.Controls.Add(down, 7, 0); lhead.Controls.Add(last, 8, 0);
        priorityButtons.AddRange([first, up, down, last]);

        // Filter row: search box, sort, group, and the box that turns every mod in the list on or off.
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 4, 0, 4) };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.Controls.Add(new Label { Text = "Filter", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
        filterRow.Controls.Add(filter, 1, 0);
        sortButton = Ui.FlatButton("Sort", () => { }); groupButton = Ui.FlatButton("Group", () => { });
        sortButton.Click += (_, _) => SortMenu().Show(sortButton, new Point(0, sortButton.Height));
        groupButton.Click += (_, _) => GroupMenu().Show(groupButton, new Point(0, groupButton.Height));
        filterRow.Controls.Add(sortButton, 2, 0); filterRow.Controls.Add(groupButton, 3, 0); filterRow.Controls.Add(allVisible, 4, 0);
        allVisible.Click += (_, _) => SetAllVisible();
        tips.SetToolTip(filter, "Search names, authors and tags (every word must match).\ntag:x or #x   tags only (tag:\"two words\")\nis:on   is:off   is:locked   is:untagged");
        tips.SetToolTip(sortButton, "Order the list by priority, name, author, tag, or on first. Priority buttons and padlocks work in the priority order only.");
        tips.SetToolTip(groupButton, "Group the list by tag or author. Click a group's header to fold it.");
        left.Controls.Add(lhead, 0, 0); left.Controls.Add(filterRow, 0, 1); left.Controls.Add(list, 0, 2);
        filter.TextChanged += (_, _) => FillList(Selected?.FolderName);
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        list.CheckClicked += m => Toggle(m);
        list.LockClicked += m => ToggleLock(m);
        list.CanLock = m => ReorderView ? lib?.CanLock(m) ?? ModLock.None : ModLock.None;
        list.DoubleClick += (_, _) => { if (Selected is Mod m) EditMod(m); };
        list.GroupClicked += g => { if (!collapsed.Remove(g.Key)) collapsed.Add(g.Key); FillList(Selected?.FolderName); };
        list.TagClicked += t => filter.Text = t.Contains(' ') ? $"tag:\"{t}\"" : $"tag:{t}";
        list.MenuRequested += (m, pt) => CardMenu(m).Show(pt);

        // ---- Right: the selected mod
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        right.Controls.Add(tabs); right.Controls.Add(header);
        header.PillClicked += () => { if (Selected is Mod m) Toggle(m); };
        header.TagsClicked += pt => { if (Selected is Mod m && !readOnly) TagsMenu(m).Show(pt); };
        tips.SetToolTip(header, "Click Enabled / Disabled to turn the mod on or off, and its tags or + Tag to change the tags.");

        // Middle column: the selected mod's store image (Kurt), between the list and the details.
        var middleAndRight = new Panel { Dock = DockStyle.Fill };
        noteReset = Ui.FlatButton("Use the Mod's Note", ResetNote);
        noteReset.Font = Ui.Regular(8f); noteReset.Padding = new Padding(2, 0, 2, 0); noteReset.Anchor = AnchorStyles.Right;
        notesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); notesPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        notesPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); notesPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var noteHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0) };
        noteHead.Controls.Add(new Label { Text = "NOTE", AutoSize = true, Font = Ui.Bold(8.5f), Tag = "subtle", Margin = new Padding(0, 3, 6, 0) });
        noteSource.Margin = new Padding(0, 4, 0, 0);
        noteHead.Controls.Add(noteSource);
        notesPanel.Controls.Add(noteHead, 0, 0); notesPanel.Controls.Add(noteReset, 1, 0);
        var noteCard = new Panel { Dock = DockStyle.Fill, Tag = "card", Padding = new Padding(8, 6, 4, 6), Margin = new Padding(0, 4, 0, 0) };
        noteCard.Controls.Add(noteBox);
        notesPanel.Controls.Add(noteCard, 0, 1); notesPanel.SetColumnSpan(noteCard, 2);
        noteBox.Leave += (_, _) => SaveNote();
        tips.SetToolTip(noteBox, "The mod's note travels with it. What you type here is kept on this PC only (it replaces the mod's note for you); Undo takes it back.");
        tips.SetToolTip(noteReset, "Throw away your note and show the mod's own note again.");
        middle.Controls.Add(storePreview); middle.Controls.Add(notesPanel);
        middleAndRight.Controls.Add(right); middleAndRight.Controls.Add(middle);
        // As large as the column's height allows (store images are 300×420), but at most 28% of the space beside the list (Kurt: between the first size and 40%).
        middleAndRight.Resize += (_, _) =>
        {
            float sc = DeviceDpi / 96f;
            notesPanel.Height = (int)(170 * sc);
            int cardH = middleAndRight.Height - notesPanel.Height - (int)(80 * sc);
            int w = (int)(cardH * 300f / 420f) + (int)(12 * sc);
            middle.Width = Math.Max((int)(200 * sc), Math.Min(w, (int)(middleAndRight.Width * 0.28f)));
        };

        var split = new GradientSplit { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(middleAndRight);

        // ---- Bottom bar
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 6, 10, 6) };
        bottomBar = bottom;
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var leftButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var remove = Ui.FlatButton("Remove Mod", RemoveMod);
        var edit = Ui.FlatButton("Edit Mod…", () => { if (Selected is Mod m) EditMod(m); });
        var export = Ui.FlatButton("Export to ZIP…", ExportMod);
        tagsButton = Ui.FlatButton("Tags  ▾", () => { });
        tagsButton.Click += (_, _) => { if (Selected is Mod m) TagsMenu(m).Show(tagsButton, new Point(0, tagsButton.Height)); };
        leftButtons.Controls.AddRange([remove, edit, export, tagsButton]);
        writeControls.AddRange([remove, edit, applyButton, tagsButton]);
        tips.SetToolTip(remove, "Send the selected mod to the Recycle Bin (turn it off and Apply first).");
        tips.SetToolTip(edit, "Open the selected mod in the Editor tab (or double-click it).");
        tips.SetToolTip(export, "Save the selected mod as a .zip to share.");
        tips.SetToolTip(tagsButton, "Add or remove the selected mod's tags, tag every mod in the list, rename or delete tags.");
        tips.SetToolTip(applyButton, "Write the mods that are on into the game: each file is built from its verified original, checked, and can be undone.  (Ctrl+Enter)");
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(status, 1, 0);
        applyButton.Anchor = AnchorStyles.Right;
        bottom.Controls.Add(applyButton, 2, 0);

        var modsPage = new Panel { Dock = DockStyle.Fill };
        modsPage.Controls.Add(split); modsPage.Controls.Add(bottom);

        // Editor tab: a placeholder until + New Mod or Edit Mod opens one.
        var ph = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        ph.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        ph.Controls.Add(new Label { Text = "No Mod Open", AutoSize = true, Anchor = AnchorStyles.None, Font = Ui.Bold(14f), Tag = "subtle" }, 0, 1);
        var phButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(0, 10, 0, 0) };
        phButtons.Controls.AddRange([Ui.AccentButton("+  New Mod", () => EditMod(null)), Ui.FlatButton("Edit the Selected Mod", () => { if (Selected is Mod m) EditMod(m); })]);
        ph.Controls.Add(phButtons, 0, 2);
        editorPlaceholder = ph;
        editorHost.Controls.Add(ph);

        pages.Add("Mods", modsPage);
        pages.Add("Editor", editorHost);
        pages.Add("Extract", extractHost);
        pages.SelectedChanged += i => { if (i == 2) EnsureExtract(); };
        var pagesWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 0) };
        pagesWrap.Controls.Add(pages);
        Controls.Add(pagesWrap); Controls.Add(top);

        // Drop .zip / .7z / folders on the window to install them.
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = !readOnly && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Install(files); };

        Load += (_, _) =>
        {
            Theme.Apply(this, Palette.Dark);
            Restyle(this);
            Reload();
        };
        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.30);   // after maximizing
        FormClosing += (_, _) => SaveNote();
        // A quiet look for a new version, at most once a day (Settings: Check for updates at start).
        // The first time: ask whether it may look (code signing policy: nothing goes over the network without consent).
        Shown += (_, _) =>
        {
            if (!settings.UpdateCheckAsked)
            {
                settings.CheckUpdates = Dialog.Show(this, "Look for new versions of MHO Extended Mod Manager when it starts (at most once a day)?" + Environment.NewLine + Environment.NewLine +
                    "It asks GitHub (api.github.com) for the latest release; nothing about you, your game or your mods is sent. " +
                    "You can change this in Settings, and Settings → Check for updates… works either way.",
                    "Updates", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                settings.UpdateCheckAsked = true;
                settings.Save();
            }
            if (settings.CheckUpdates && (settings.LastUpdateCheck == null || DateTime.Now - settings.LastUpdateCheck > TimeSpan.FromDays(1)))
                CheckForUpdates(manual: false);
        };
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => UpdateRunning();
        timer.Start();
        UpdateRunning();
    }

    // The window background: Kurt's navy-to-grey gradient (Ui.PaintGradient); the containers above it are transparent.
    protected override void OnPaintBackground(PaintEventArgs e) => Ui.PaintGradient(e.Graphics, this, ClientRectangle);
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; }   // WS_EX_COMPOSITED: no flicker with the transparent layers
    }

    void Restyle(Control root) => Ui.Restyle(root, [topBar!, bottomBar!, .. editor?.Bars ?? [], .. extract?.Bars ?? []]);

    /// <summary>The Extract tab's content, made on first view and again only if the library or game folder changed.</summary>
    void EnsureExtract()
    {
        if (lib == null || game == null)
        {
            if (extract == null && extractHost.Controls.Count == 0)
                extractHost.Controls.Add(new Label { Text = "Set the game folder first (Settings → Change game folder).", AutoSize = true, Tag = "subtle", Padding = new Padding(12) });
            return;
        }
        string key = lib.DataFolder + "|" + game.Root;
        if (extract != null && extractKey == key) return;
        extract?.Dispose();
        foreach (var c in extractHost.Controls.Cast<Control>().ToList()) c.Dispose();   // the "set the game folder" label, if any
        extract = new ExtractView(new StockCatalog(lib, game));
        extractKey = key;
        extractHost.Controls.Add(extract);
        Theme.ApplyTree(extractHost, Palette.Dark);   // not the whole form: see ShowDetails
        Restyle(extractHost);
    }

    void UpdateRunning()
    {
        bool running = Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;
        runningLabel.Text = running ? "●  Game Running: Close It to Apply" : "●  Game Not Running";
        runningLabel.ForeColor = running ? Ui.Warn : Ui.Subtle;
    }

    /// <summary>Looks for a newer release (GitHub); offers it. Quiet (no messages when there's nothing, or no network) unless <paramref name="manual"/>.</summary>
    async void CheckForUpdates(bool manual)
    {
        Updater.Release? r;
        try { r = await Updater.Latest(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            if (manual) Dialog.Show(this, ex.Message, "Couldn't Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        settings.LastUpdateCheck = DateTime.Now; settings.Save();
        if (r == null || r.Version <= Updater.Current)
        {
            if (manual) Dialog.Show(this, $"You have the latest version ({Program.Version}).", "Updates");
            return;
        }
        if (!manual && settings.SkipVersion == r.Version.ToString()) { status.Text = $"Version {r.Version} Is Available (Skipped: Settings → Check for Updates)"; return; }
        using var f = new UpdateForm(r);
        f.ShowDialog(this);
        if (f.SkipThis) { settings.SkipVersion = r.Version.ToString(); settings.Save(); }
        if (!f.Installed) return;
        SaveNote();
        Updater.Restart();
        Close();
    }

    void About() => Dialog.Show(this,
        $"MHO Extended Mod Manager {Program.Version}\n\nMods for Marvel Heroes Omega: packages, icons, strings and sound packs, in the MHModManager mod format. " +
        "Every change is written from verified originals, checked, and can be undone.\n\nSettings and mods: " + Settings.Home, "About");

    void BrowseGame()
    {
        using var d = new FolderBrowserDialog { Description = "The Marvel Heroes folder (holds UnrealEngine3 and Data)", UseDescriptionForTitle = true, SelectedPath = settings.GameRoot ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(Settings.Cooked(d.SelectedPath))) { Dialog.Show(this, "That folder has no UnrealEngine3\\MarvelGame\\CookedPCConsole. Pick the Marvel Heroes folder.", "Not the Game Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        settings.GameRoot = d.SelectedPath; settings.Save(); Reload();
    }

    Mod? Selected => list.SelectedItem as Mod;

    void Reload()
    {
        string? keep = Selected?.FolderName;
        string? data = Settings.LibraryData(settings.LibraryPath);
        readOnly = data == null || Program.IsOldManager(data);
        foreach (var c in writeControls) c.Enabled = !readOnly;
        lib = null; game = null;
        if (data == null) { list.Items.Clear(); header.Mod = null; header.Invalidate(); tabs.Clear(); status.Text = "No mod library yet: Settings → Migrate from MHModManager, or restart for the first-run setup."; return; }
        lib = ModLibrary.Load(data);
        string key = lib.DataFolder + "|" + string.Join("|", lib.Mods.Select(m => m.FolderName).Order(StringComparer.OrdinalIgnoreCase));
        if (foldersKey != null && key != foldersKey) { undo.Clear(); redo.Clear(); }
        foldersKey = key;
        string? gameRoot = settings.ResolvedGameRoot(data);
        gameLabel.Text = gameRoot != null ? Settings.TrueCase(gameRoot) : "(not set: Settings → Change game folder)";
        if (gameRoot != null && Directory.Exists(Settings.Cooked(gameRoot))) game = new GameState(gameRoot, data);
        winners = lib.PackageWinners();
        var conflicts = lib.Conflicts();
        conflicted = conflicts.SelectMany(c => c.Mods).ToHashSet();
        list.Conflicted = conflicted;
        countLabel.Text = $"{lib.Mods.Count(m => m.Enabled)} of {lib.Mods.Count} On";
        countLabel.ForeColor = Ui.Subtle;
        status.Text = Ui.TitleCase((note != null ? note + "  ·  " : "") + (game == null ? "Game folder not found" : conflicts.Count > 0 ? $"{conflicts.Count} conflicting change(s): the mod higher in the list wins" : "No conflicts") +
                      (readOnly ? "  ·  MHModManager's own folder: read-only here (Settings → Migrate)" : ""));
        status.ForeColor = Ui.Subtle;
        note = null;
        applyButton.Text = "Apply Changes";
        FillList(keep);
        UpdateUndo();
        if (!readOnly && game != null && game.HasStockList) CountPending();
    }

    static readonly (string Key, string Name)[] Sorts = [("priority", "Priority"), ("name", "Name"), ("author", "Author"), ("tag", "Tag"), ("enabled", "On First")];
    static readonly (string Key, string Name)[] Groups = [("none", "None"), ("tag", "Tag"), ("author", "Author")];

    /// <summary>The list shows the priority order as it is: the priority buttons and padlocks work only then.</summary>
    bool ReorderView => settings.ListSort == "priority" && settings.ListGroup == "none";

    /// <summary>One search word: plain text (name, author, tags), tag:x / #x (tags only), or is:on / off / locked / untagged.</summary>
    static bool Matches(Mod m, string t)
    {
        if (t.StartsWith("is:", StringComparison.OrdinalIgnoreCase))
            return t[3..].ToLowerInvariant() switch { "on" => m.Enabled, "off" => !m.Enabled, "locked" => m.Lock != ModLock.None, "untagged" => m.Tags.Count == 0, _ => true };
        if (t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || t.StartsWith('#'))
        {
            string x = t.StartsWith('#') ? t[1..] : t[4..];
            return m.Tags.Any(tag => tag.Contains(x, StringComparison.OrdinalIgnoreCase));
        }
        return m.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (m.Manifest.Author ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
               m.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The filter box's words; "quoted text" (also after tag:) stays one word.</summary>
    static List<string> SearchWords(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, "(?:tag:|#)?\"[^\"]*\"?|\\S+").Select(x => x.Value.Replace("\"", "")).Where(x => x.Length > 0).ToList();

    /// <summary>The list: filtered, sorted and grouped as chosen, keeping the selection.</summary>
    void FillList(string? keep)
    {
        if (lib == null) return;
        var words = SearchWords(filter.Text);
        var mods = lib.Mods.Where(m => words.All(w => Matches(m, w)));
        var cmp = StringComparer.OrdinalIgnoreCase;
        var modList = (settings.ListSort switch
        {
            "name" => mods.OrderBy(m => m.Name, cmp),
            "author" => mods.OrderBy(m => string.IsNullOrWhiteSpace(m.Manifest.Author) ? "\uffff" : m.Manifest.Author, cmp).ThenBy(m => m.Name, cmp),
            "tag" => mods.OrderBy(m => m.Tags.Count == 0 ? "\uffff" : m.Tags.Order(cmp).First(), cmp).ThenBy(m => m.Name, cmp),
            "enabled" => mods.OrderByDescending(m => m.Enabled).ThenBy(m => m.Priority),
            _ => mods.OrderBy(m => m.Priority),
        }).ToList();

        var items = new List<object>();
        string grouping = settings.ListGroup;
        if (grouping is "tag" or "author")
        {
            string none = grouping == "tag" ? "Untagged" : "(No Author)";
            List<string> KeysOf(Mod m) => grouping == "tag" ? (m.Tags.Count > 0 ? m.Tags : [none]) : [string.IsNullOrWhiteSpace(m.Manifest.Author) ? none : m.Manifest.Author.Trim()];
            foreach (string k in modList.SelectMany(KeysOf).Distinct(cmp).OrderBy(k => k == none ? 1 : 0).ThenBy(k => k, cmp))
            {
                var members = modList.Where(m => KeysOf(m).Contains(k, cmp)).ToList();
                string gk = grouping + ":" + k;
                bool folded = collapsed.Contains(gk);
                items.Add(new ModGroup(gk, k, members.Count, folded));
                if (!folded) items.AddRange(members);
            }
        }
        else items.AddRange(modList);
        shown = items.OfType<Mod>().Distinct().ToList();

        list.BeginUpdate();
        list.Items.Clear();
        foreach (var o in items) list.Items.Add(o);
        list.EndUpdate();
        int i = items.FindIndex(o => o is Mod m && m.FolderName == keep);
        if (i < 0) i = items.FindIndex(o => o is Mod);
        if (i >= 0) list.SelectedIndex = i; else { header.Mod = null; header.Invalidate(); tabs.Clear(); storePreview.Mod = null; ShowNote(null); }

        int on = shown.Count(m => m.Enabled);
        allVisible.CheckState = on == 0 ? CheckState.Unchecked : on == shown.Count ? CheckState.Checked : CheckState.Indeterminate;
        allVisible.Enabled = !readOnly && shown.Count > 0;
        tips.SetToolTip(allVisible, shown.Count == 0 ? "No mods in the list." :
            (on == shown.Count ? $"All {shown.Count} mods in the list are on: click to turn them all off." : $"{on} of {shown.Count} mods in the list are on: click to turn them all on.") +
            (shown.Count < lib.Mods.Count ? "\nOnly the mods the filter shows (and open groups) change." : ""));
        foreach (var b in priorityButtons) b.Enabled = !readOnly && ReorderView;
        sortButton.Text = "Sort: " + Sorts.First(x => x.Key == settings.ListSort || x.Key == "priority" && !Sorts.Any(y => y.Key == settings.ListSort)).Name + "  ▾";
        groupButton.Text = "Group: " + Groups.First(x => x.Key == settings.ListGroup || x.Key == "none" && !Groups.Any(y => y.Key == settings.ListGroup)).Name + "  ▾";
    }

    void SelectMod(string? folderName)
    {
        int i = list.Items.Cast<object>().ToList().FindIndex(o => o is Mod m && m.FolderName == folderName);
        if (i >= 0) list.SelectedIndex = i;
    }

    // Menus are made fresh each time; the previous one is disposed then (an undisposed, unreferenced menu keeps a
    // window whose .NET side the garbage collector can take: see FlatTabs.Clear).
    ContextMenuStrip? lastMenu;
    ContextMenuStrip NewMenu()
    {
        lastMenu?.Dispose();
        return lastMenu = new() { Font = Ui.Regular(9.5f), RenderMode = ToolStripRenderMode.System };
    }

    ContextMenuStrip SortMenu()
    {
        var menu = NewMenu();
        foreach (var (k, n) in Sorts)
            menu.Items.Add(new ToolStripMenuItem(n, null, (_, _) => { settings.ListSort = k; settings.Save(); FillList(Selected?.FolderName); }) { Checked = settings.ListSort == k });
        return menu;
    }

    ContextMenuStrip GroupMenu()
    {
        var menu = NewMenu();
        foreach (var (k, n) in Groups)
            menu.Items.Add(new ToolStripMenuItem(n, null, (_, _) => { settings.ListGroup = k; settings.Save(); FillList(Selected?.FolderName); }) { Checked = settings.ListGroup == k });
        if (settings.ListGroup != "none")
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open All Groups", null, (_, _) => { collapsed.Clear(); FillList(Selected?.FolderName); });
            menu.Items.Add("Fold All Groups", null, (_, _) =>
            {
                foreach (var g in list.Items.OfType<ModGroup>()) collapsed.Add(g.Key);
                FillList(Selected?.FolderName);
            });
        }
        return menu;
    }

    // ---- Undo / redo (list changes only; Apply keeps its own history of game files)

    string StatePath => Path.Combine(lib!.DataFolder, "state.json");
    string ReadState() => File.Exists(StatePath) ? File.ReadAllText(StatePath) : "";
    void WriteState(string json) { string tmp = StatePath + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, StatePath, overwrite: true); }

    /// <summary>Makes a list change as one undo step: <paramref name="change"/> edits the mods (false = nothing to do),
    /// then the state is saved and the list reloaded.</summary>
    bool Change(string label, Func<bool> change)
    {
        if (readOnly || lib == null) return false;
        string before = ReadState();
        if (!change()) return false;
        lib.SaveState();
        undo.Add((before, label));
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        note = Ui.TitleCase(label);
        Reload();
        return true;
    }


    void Undo()
    {
        if (readOnly || lib == null || undo.Count == 0) return;
        var (json, label) = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((ReadState(), label));
        WriteState(json);
        note = Ui.TitleCase($"Undone: {label}");
        Reload();
    }

    void Redo()
    {
        if (readOnly || lib == null || redo.Count == 0) return;
        var (json, label) = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((ReadState(), label));
        WriteState(json);
        note = Ui.TitleCase($"Redone: {label}");
        Reload();
    }

    void UpdateUndo()
    {
        undoButton.Enabled = !readOnly && undo.Count > 0;
        redoButton.Enabled = !readOnly && redo.Count > 0;
        tips.SetToolTip(undoButton, undo.Count > 0 ? $"Undo: {undo[^1].Label}  (Ctrl+Z)" : "Nothing to undo. Turning mods on or off, moving, locking and tags can be undone.");
        tips.SetToolTip(redoButton, redo.Count > 0 ? $"Redo: {redo[^1].Label}  (Ctrl+Y)" : "Nothing to redo.");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Enter: Apply Changes (Kurt), anywhere on the Mods tab.
        if (keyData == (Keys.Control | Keys.Enter) && pages.SelectedIndex == 0 && applyButton.Enabled && applyButton.Visible)
        {
            Apply();
            return true;
        }
        if (pages.SelectedIndex == 0 && ActiveControl is not TextBoxBase)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- Note (under the store image)

    void ShowNote(Mod? m)
    {
        noteMod = m;
        noteBox.Text = (m?.Note ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
        noteBox.ReadOnly = readOnly || m == null;
        noteSource.Text = m == null ? "" : m.LocalNote != null ? (m.Manifest.Notes != null ? "Yours (Replaces the Mod's)" : "Yours, on This PC") : m.Manifest.Notes != null ? "From the Mod" : "None Yet: Type to Add One";
        noteReset.Visible = m?.LocalNote != null && m.Manifest.Notes != null;
    }

    /// <summary>Keeps what's typed in the note box as the user's note (one undo step), or drops the user's note when the
    /// text is the mod's own again. No reload: the list doesn't show notes.</summary>
    void SaveNote()
    {
        if (readOnly || lib == null || noteMod == null) return;
        var m = lib.Find(noteMod.FolderName);
        if (m == null) return;
        string text = noteBox.Text.Replace("\r\n", "\n").TrimEnd();
        if (text == m.Note.Replace("\r\n", "\n").TrimEnd()) return;
        string? local = text == (m.Manifest.Notes ?? "").Replace("\r\n", "\n").TrimEnd() ? null : text;
        string before = ReadState();
        m.LocalNote = local;
        lib.SaveState();
        undo.Add((before, $"edit the note of \"{m.Name}\""));
        redo.Clear();
        UpdateUndo();
        ShowNote(m);
    }

    void ResetNote()
    {
        if (readOnly || lib == null || noteMod == null || lib.Find(noteMod.FolderName) is not Mod m) return;
        Change($"use the mod's note for \"{m.Name}\"", () => { if (m.LocalNote == null) return false; m.LocalNote = null; return true; });
    }

    // ---- Tags

    static string Short(string name) => name.Length > 40 ? name[..40] + "…" : name;

    /// <summary>Turns every mod in the list (as filtered, open groups only) on, or off when all are on already.</summary>
    void SetAllVisible()
    {
        var targets = shown.ToList();
        if (targets.Count == 0) return;
        bool turnOn = targets.Any(m => !m.Enabled);
        Change($"turn {(turnOn ? "on" : "off")} {targets.Count} mod(s) in the list", () =>
        {
            foreach (var m in targets) m.Enabled = turnOn;
            return true;
        });
    }

    /// <summary>A card's right-click menu: on/off, edit, export, then the tags.</summary>
    ContextMenuStrip CardMenu(Mod m)
    {
        var menu = TagsMenu(m);
        int at = 0;
        menu.Items.Insert(at++, new ToolStripMenuItem(m.Enabled ? "Turn Off" : "Turn On", null, (_, _) => Toggle(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Edit…", null, (_, _) => EditMod(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Export to ZIP…", null, (_, _) => ExportMod()));
        menu.Items.Insert(at++, new ToolStripMenuItem("Update from a File…", null, (_, _) => UpdateFromFile(m)) { Enabled = !readOnly });
        menu.Items.Insert(at, new ToolStripSeparator());
        return menu;
    }

    /// <summary>
    /// The tags menu of one mod. Its tags are listed with where they come from (automatic, from the mod, yours): unticking
    /// an automatic or mod tag hides it on this PC, ticking it again shows it. Then: add a tag, tag / untag every mod in
    /// the list, and rename or delete your own tags (automatic and mod tags can't be renamed).
    /// </summary>
    ContextMenuStrip TagsMenu(Mod m)
    {
        var menu = NewMenu();
        if (lib == null) return menu;
        var l = lib;
        var cmp = StringComparer.OrdinalIgnoreCase;
        var all = l.AllTags();
        menu.Items.Add(new ToolStripLabel($"Tags of \"{Short(m.Name)}\"") { ForeColor = Color.Gray });
        var own = m.Tags.Concat(m.HiddenTags.Where(h => m.AutoTags.Contains(h, cmp) || m.ModTags.Contains(h, cmp))).Distinct(cmp).ToList();
        foreach (string tag in own)
        {
            bool has = ModLibrary.HasTag(m, tag);
            string from = m.KindOf(tag) switch { Mod.TagKind.Auto => "automatic", Mod.TagKind.Mod => "from the mod", _ => "" };
            var item = new ToolStripMenuItem(tag + (from.Length > 0 ? $"      ({from}{(has ? "" : ", hidden here")})" : ""), null, (_, _) =>
                Change(has ? $"take tag \"{tag}\" off \"{m.Name}\"" : $"tag \"{m.Name}\" as \"{tag}\"", () =>
                {
                    if (has) ModLibrary.RemoveTag(m, tag); else ModLibrary.AddTag(m, tag);
                    return true;
                })) { Checked = has, Enabled = !readOnly };
            menu.Items.Add(item);
        }
        if (own.Count == 0) menu.Items.Add(new ToolStripMenuItem("(No Tags Yet)") { Enabled = false });

        var add = new ToolStripMenuItem("Add a Tag") { Enabled = !readOnly };
        add.DropDownItems.Add("New Tag…", null, (_, _) =>
        {
            string? t = l.CleanTag(Ui.Prompt(this, "New Tag", $"Tag for \"{Short(m.Name)}\":", "", all));
            if (t != null) Change($"tag \"{m.Name}\" as \"{t}\"", () => { if (ModLibrary.HasTag(m, t)) return false; ModLibrary.AddTag(m, t); return true; });
        });
        var others = all.Where(t => !own.Contains(t, cmp)).ToList();
        if (others.Count > 0) add.DropDownItems.Add(new ToolStripSeparator());
        foreach (string tag in others)
            add.DropDownItems.Add(tag, null, (_, _) => Change($"tag \"{m.Name}\" as \"{tag}\"", () => { ModLibrary.AddTag(m, tag); return true; }));
        menu.Items.Add(add);

        var targets = shown.ToList();
        if (targets.Count > 1 && !readOnly)
        {
            menu.Items.Add(new ToolStripSeparator());
            var addAll = new ToolStripMenuItem($"Tag All {targets.Count} Mods in the List");
            void TagAll(string t) => Change($"tag {targets.Count} mods as \"{t}\"", () =>
            {
                bool any = targets.Any(x => !ModLibrary.HasTag(x, t));
                foreach (var x in targets) ModLibrary.AddTag(x, t);
                return any;
            });
            addAll.DropDownItems.Add("New Tag…", null, (_, _) =>
            {
                string? t = l.CleanTag(Ui.Prompt(this, "Tag the List", $"Tag for all {targets.Count} mods in the list:", "", all));
                if (t != null) TagAll(t);
            });
            addAll.DropDownItems.Add(new ToolStripSeparator());
            foreach (string tag in all) addAll.DropDownItems.Add(tag, null, (_, _) => TagAll(tag));
            menu.Items.Add(addAll);
            var inList = targets.SelectMany(x => x.Tags).Distinct(cmp).Order(cmp).ToList();
            if (inList.Count > 0)
            {
                var remAll = new ToolStripMenuItem("Take a Tag Off the Mods in the List");
                foreach (string tag in inList)
                    remAll.DropDownItems.Add(tag, null, (_, _) => Change($"take tag \"{tag}\" off the list", () => { foreach (var x in targets) ModLibrary.RemoveTag(x, tag); return true; }));
                menu.Items.Add(remAll);
            }
        }
        var mine = l.Mods.SelectMany(x => x.UserTags).Distinct(cmp).Order(cmp).ToList();
        if (mine.Count > 0 && !readOnly)
        {
            menu.Items.Add(new ToolStripSeparator());
            var rename = new ToolStripMenuItem("Rename One of Your Tags");
            var delete = new ToolStripMenuItem("Delete One of Your Tags");
            foreach (string tag in mine)
            {
                rename.DropDownItems.Add(tag, null, (_, _) =>
                {
                    string? raw = Ui.Prompt(this, "Rename Tag", $"New name for \"{tag}\" (on every mod):", tag, all);
                    // Same name in other letter case = a case change (CleanTag would map it back to the old spelling).
                    string? t = raw == null ? null : raw.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase) ? raw.Trim() : l.CleanTag(raw);
                    if (t == null || t == tag) return;
                    Change($"rename tag \"{tag}\" to \"{t}\"", () =>
                    {
                        foreach (var x in l.Mods.Where(x => x.UserTags.Contains(tag, cmp)))
                        {
                            x.UserTags.RemoveAll(u => u.Equals(tag, StringComparison.OrdinalIgnoreCase));
                            ModLibrary.AddTag(x, t);
                        }
                        return true;
                    });
                });
                int users = l.Mods.Count(x => x.UserTags.Contains(tag, cmp));
                delete.DropDownItems.Add($"{tag}  ({users})", null, (_, _) =>
                    Change($"delete tag \"{tag}\" ({users} mod(s))", () => { foreach (var x in l.Mods) x.UserTags.RemoveAll(u => u.Equals(tag, StringComparison.OrdinalIgnoreCase)); return true; }));
            }
            menu.Items.Add(rename);
            menu.Items.Add(delete);
        }
        return menu;
    }

    /// <summary>"Apply Changes (N)" and a status note once the (hashing) plan is ready.</summary>
    void CountPending()
    {
        var (l, g) = (lib!, game!);
        string baseText = status.Text;
        pending = Task.Run(() => Applier.MakePlan(l, g, new Originals(l.DataFolder, g))).ContinueWith(t =>
        {
            if (t.IsFaulted || lib != l) return;
            int n = t.Result.Steps.Count;
            applyButton.Text = n == 0 ? "Apply Changes" : $"Apply Changes ({n})";
            status.Text = baseText + Ui.TitleCase((n == 0 ? "  ·  the game matches your list" : $"  ·  {n} file(s) to change") +
                          (t.Result.Problems.Count > 0 ? $"  ·  {t.Result.Problems.Count} can't be (see Apply)" : ""));
            status.ForeColor = n == 0 ? Ui.Subtle : Ui.Text;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Toggle(Mod m) => Change($"turn {(m.Enabled ? "off" : "on")} \"{m.Name}\"", () => { m.Enabled = !m.Enabled; return true; });

    void MoveSelected(int delta, bool toEnd = false)
    {
        if (readOnly || lib == null || Selected is not Mod m || !ReorderView) return;
        if (m.Lock != ModLock.None)
        {
            status.Text = Ui.TitleCase($"\"{m.Name}\" is locked at the {(m.Lock == ModLock.Top ? "top" : "bottom")}: click its padlock to unlock it first");
            return;
        }
        int before = m.Priority;
        var l = lib;
        Change($"move \"{m.Name}\" {(toEnd ? (delta < 0 ? "to the top" : "to the bottom") : delta < 0 ? "up" : "down")}",
            () => (toEnd ? l.MoveToEnd(m, delta) : l.Move(m, delta)) && m.Priority != before);
    }

    /// <summary>Padlock: locks a mod at the top or bottom (it must be there, or next to a mod locked there), or unlocks it.</summary>
    void ToggleLock(Mod m)
    {
        if (readOnly || lib == null) return;
        var l = lib;
        string label = m.Lock != ModLock.None ? $"unlock \"{m.Name}\"" : $"lock \"{m.Name}\" at the {(l.CanLock(m) == ModLock.Top ? "top" : "bottom")}";
        Change(label, () => l.ToggleLock(m));
    }

    // ---- Details

    void ShowDetails()
    {
        if (Selected is not Mod m || lib == null) return;
        header.Mod = m; header.Conflicted = conflicted.Contains(m); header.Invalidate();
        storePreview.Mod = m;
        ShowNote(m);
        int keepTab = Math.Max(0, tabs.SelectedIndex);
        tabs.SuspendLayout();
        tabs.Clear();

        if (m.Manifest.UpkReplacements.Count > 0)
        {
            var grid = Grid(false, ("UPK Filename", 0), ("Size", 90), ("In the Game", 330));
            var rows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in m.Manifest.UpkReplacements)
            {
                string path = Path.Combine(m.Folder, f);
                string size = File.Exists(path) ? $"{new FileInfo(path).Length / 1048576.0:0.0} MB" : "Missing";
                string state = !m.Enabled ? "Turned Off" : winners.TryGetValue(f, out var w) && w != m ? $"Overridden by {w.Name}" : game == null ? "" : "Checking…";
                rows[f] = grid.Rows[grid.Rows.Add(f, size, state)];
            }
            tabs.Add("Packages", grid);
            if (m.Enabled && game != null)
            {
                var g = game;
                var mine = m.Manifest.UpkReplacements.Where(f => !(winners.TryGetValue(f, out var w) && w != m)).ToList();
                loading = Task.Run(() => mine.ToDictionary(f => f, f => g.Check(m, f), StringComparer.OrdinalIgnoreCase))
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted || Selected != m) return;
                        foreach (var (f, s) in t.Result)
                            if (rows.TryGetValue(f, out var row))
                            {
                                row.Cells[2].Value = s == PackageState.Applied ? "Applied ✓" : Ui.TitleCase(GameState.Describe(s)) + " (Apply)";
                                row.Cells[2].Style.ForeColor = s == PackageState.Applied ? Ui.Enabled : Ui.Packages;
                            }
                    }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        int texCount = m.Manifest.Replacements.Count + m.Manifest.AchievementReplacements.Count + m.Manifest.StoreReplacements.Count + m.Manifest.Extra.Count();
        if (texCount > 0)
        {
            var grid = Grid(true, ("Texture", 0), ("Where", 140), ("File", 280));
            var rows = new List<(string Path, DataGridViewRow Row)>();
            foreach (var (where, reps) in new[] { ("Icon", m.Manifest.Replacements), ("Achievement", m.Manifest.AchievementReplacements), ("Store", m.Manifest.StoreReplacements) })
                foreach (var r in reps)
                    rows.Add((Path.Combine(m.Folder, r.DdsFileName), grid.Rows[grid.Rows.Add(null, r.TextureName, where, r.DdsFileName)]));
            foreach (var r in m.Manifest.Extra)   // extension: other icon packages
                rows.Add((Path.Combine(m.Folder, r.DdsFileName), grid.Rows[grid.Rows.Add(null, r.TextureName, Path.GetFileNameWithoutExtension(r.Package).Replace("ICO__", ""), r.DdsFileName)]));
            tabs.Add("Textures", grid);
            LoadThumbnails(rows, grid);
        }

        if (m.Strings.Count > 0)
        {
            var grid = Grid(false, ("Lang", 60), ("ID", 190), ("Text", 0));
            foreach (var s in m.Strings.Take(2000)) grid.Rows.Add(s.Language, s.Id.ToString(), s.Text);
            tabs.Add("Strings", grid);
        }

        if (m.Manifest.AudioPacks.Count > 0)
        {
            var grid = Grid(false, ("Sound Pack", 0), ("Events", 80), ("Sound Files It Patches", 380));
            foreach (string f in m.Manifest.AudioPacks)
            {
                string events = "?", pcks = "";
                try { var p = SoundPack.Load(Path.Combine(m.Folder, f)); events = p.Patches.Count.ToString(); pcks = string.Join(", ", p.Patches.Select(x => x.PckFile).Distinct()); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { pcks = "Unreadable"; }
                grid.Rows.Add(f, events, pcks);
            }
            tabs.Add("Sound Packs", grid);
        }

        var mineConflicts = lib.Conflicts().Where(c => c.Mods.Contains(m)).ToList();
        if (mineConflicts.Count > 0)
        {
            var grid = Grid(false, ("Change", 0), ("Result", 420));
            foreach (var (claim, mods) in mineConflicts)
            {
                int i = grid.Rows.Add(claim, mods[0] == m ? "Wins over " + string.Join(", ", mods.Skip(1).Select(x => x.Name)) : "Loses to " + mods[0].Name);
                grid.Rows[i].Cells[1].Style.ForeColor = mods[0] == m ? Ui.Enabled : Ui.Warn;
            }
            tabs.Add($"Conflicts ({mineConflicts.Count})", grid);
        }

        // Info: a table like the other tabs (it was monospaced text; Kurt: the font should match the others).
        var infoGrid = Grid(false, ("Field", 130), ("Value", 0));
        void InfoRow(string field, string value, Color? color = null) { int r = infoGrid.Rows.Add(field, value); if (color is Color c) infoGrid.Rows[r].Cells[1].Style.ForeColor = c; }
        InfoRow("Name", m.Name);
        InfoRow("Author", m.Manifest.Author ?? "");
        InfoRow("Version", m.Manifest.Version ?? "");
        InfoRow("Priority", $"{m.Priority + 1} of {lib.Mods.Count} (Higher Wins)" + (m.Lock != ModLock.None ? $", Locked at the {(m.Lock == ModLock.Top ? "Top" : "Bottom")}" : ""));
        InfoRow("State", m.Enabled ? "Enabled" : "Disabled", m.Enabled ? Ui.Enabled : null);
        InfoRow("Automatic Tags", m.AutoTags.Count > 0 ? string.Join(", ", m.AutoTags) : "None (Nothing Recognised in the Content)");
        InfoRow("The Mod's Tags", m.ModTags.Count > 0 ? string.Join(", ", m.ModTags) : "None (Set Them in Edit Mod → Tags)");
        InfoRow("Your Tags", m.UserTags.Count > 0 ? string.Join(", ", m.UserTags) : "None (Right-Click the Mod, or + Tag Above)");
        if (m.HiddenTags.Count > 0) InfoRow("Hidden Here", string.Join(", ", m.HiddenTags));
        InfoRow("Folder", m.Folder);
        if (m.LoadError != null) InfoRow("Error", m.LoadError, Ui.Warn);
        var missing = m.MissingFiles().ToList();
        if (missing.Count > 0) InfoRow("Missing Files", string.Join(", ", missing), Ui.Warn);
        tabs.Add("Info", infoGrid);

        // Colours for the new pages only. Theming the whole form set the mod list's border (FixedSingle, then back to
        // None), which recreates its window; from inside the list's own click (padlock, checkbox) that crashed the app.
        Theme.ApplyTree(tabs, Palette.Dark);
        Ui.Restyle(tabs);
        tabs.Select(Math.Min(keepTab, 99));
        tabs.ResumeLayout();
    }

    DataGridView Grid(bool thumbs, params (string Title, int Width)[] cols) => Ui.Grid(DeviceDpi / 96f, thumbs, cols);

    /// <summary>Texture thumbnails, decoded in the background (the table shows as soon as it's built).</summary>
    void LoadThumbnails(List<(string Path, DataGridViewRow Row)> rows, DataGridView grid)
    {
        int req = ++thumbRequest;
        int size = (int)(96 * DeviceDpi / 96f);
        Task.Run(() => rows.Select(r =>
        {
            var d = TextureDecode.ReadDds(r.Path, out _);
            var bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            if (bgra == null) return null;
            using var full = TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H);
            float k = Math.Min(1f, Math.Min((float)size / full.Width, (float)size / full.Height));
            var bmp = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
            return (Image?)bmp;
        }).ToList()).ContinueWith(t =>
        {
            if (t.IsFaulted || req != thumbRequest || grid.IsDisposed) return;
            for (int i = 0; i < rows.Count; i++) if (t.Result[i] is Image img) rows[i].Row.Cells[0].Value = img;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Actions

    async void Apply()
    {
        if (readOnly || lib == null || game == null) return;
        if (!game.HasStockList) { Dialog.Show(this, "There is no stock checksum list, so originals can't be verified. Reinstall the program to get it back (StockData folder).", "Can't Apply", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { Dialog.Show(this, "Close the game first: changes can only be applied while it isn't running.", "The Game Is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var (l, g) = (lib, game);
        var originals = new Originals(l.DataFolder, g);
        UseWaitCursor = true;
        var plan = await Task.Run(() => Applier.MakePlan(l, g, originals));
        UseWaitCursor = false;
        string planText = CaptureOutput(() => Applier.Print(plan));
        // One window (Kurt): the plan and the question, then "Success" or what went wrong.
        using var f = new ApplyForm(planText, plan.Steps.Count == 0 ? null : () => Task.Run(() =>
        {
            bool ok = false;
            string log = CaptureOutput(() => ok = Applier.Execute(plan, g, originals, l.DataFolder));
            return (ok, log);
        }));
        f.ShowDialog(this);
        if (plan.Steps.Count > 0 && f.DialogResult == DialogResult.OK) Reload();
    }

    /// <summary>Icon changes in the game that no mod accounts for, saved as a mod so Apply's rebuild from stock keeps them.</summary>
    async void CaptureIcons()
    {
        if (readOnly || lib == null || game == null) return;
        var (l, g) = (lib, game);
        UseWaitCursor = true;
        string log = await Task.Run(() => CaptureOutput(() => IconCapture.Run(l, g, new Originals(l.DataFolder, g), null)));
        UseWaitCursor = false;
        ShowLog("Capture Icon Changes", log);
        Reload();
    }

    void InstallMod()
    {
        using var d = new OpenFileDialog { Title = "Install Mod", Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*", Multiselect = true };
        if (d.ShowDialog(this) == DialogResult.OK) Install(d.FileNames);
    }

    /// <summary>Asked (on the UI thread) when an installed mod would be replaced.</summary>
    bool AskReplace(Mod existing, ModManifest incoming) => (bool)Invoke(() =>
        Dialog.Show(this, $"\"{existing.Name}\" is installed already (version {existing.Manifest.Version ?? "?"} by {existing.Manifest.Author ?? "?"}).\n\n" +
            $"Replace it with version {incoming.Version ?? "?"} by {incoming.Author ?? "?"}?\n\nIt keeps its place in the list, on/off, lock, tags and note. The old files go to the Recycle Bin." +
            (existing.Enabled ? "\n\nIt's on: Apply Changes afterwards puts the new version in the game." : ""),
            "Update Mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

    /// <summary>A card's "Update from a File…": the chosen archive / folder replaces this mod (even under another name).</summary>
    void UpdateFromFile(Mod m)
    {
        using var d = new OpenFileDialog { Title = $"Update \"{m.Name}\" from", Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) Install([d.FileName], m);
    }

    async void Install(string[] sources, Mod? into = null)
    {
        if (readOnly || lib == null) return;
        var l = lib;
        UseWaitCursor = true;
        var (log, installed) = await Task.Run(() =>
        {
            var log = new List<string>(); var all = new List<string>();
            foreach (string s in sources)
            {
                log.Add(Path.GetFileName(s) + ":");
                try { all.AddRange(ModInstaller.Install(s, ModLibrary.Load(l.DataFolder), log, AskReplace, into)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { log.Add("  couldn't read it: " + ex.Message); }
            }
            return (log, all);
        });
        UseWaitCursor = false;
        filter.Text = "";
        Reload();
        if (installed.Count > 0) SelectMod(installed[0]);
        // Like Apply's result: a coloured heading, then what happened.
        ShowLog(installed.Count > 0 ? "Installed" : "Nothing Installed",
            string.Join("\n", log) + (installed.Count > 0 ? "\n\nNew mods are added at the top of the list, turned off: tick one, then Apply Changes." : ""),
            installed.Count > 0 ? Dialog.Tone.Good : Dialog.Tone.Bad);
    }

    /// <summary>Opens a mod (or a new one) in the Editor tab. Saving or cancelling returns to the Mods tab.</summary>
    void EditMod(Mod? m)
    {
        if (readOnly || lib == null) return;
        if (editor != null)
        {
            if (editor.Editing?.FolderName == m?.FolderName && m != null) { pages.Select(1); return; }
            if (Dialog.Show(this, $"\"{editor.Title}\" is open in the Editor. Close it (unsaved changes are lost) and open {(m == null ? "a new mod" : $"\"{m.Name}\"")}?", "Editor", MessageBoxButtons.OKCancel) != DialogResult.OK) { pages.Select(1); return; }
        }
        OpenEditor(m);
        pages.Select(1);
    }

    void OpenEditor(Mod? m)
    {
        CloseEditor();
        var ed = new ModEditorView(lib!, game, m);
        ed.Saved += name =>
        {
            CloseEditor();
            filter.Text = "";
            Reload();
            SelectMod(name);
            pages.Select(0);
            status.Text = Ui.TitleCase((m == null ? $"Created \"{name}\" (top of the list, off: tick it, then Apply Changes)" : $"Saved \"{name}\"") + (m?.Enabled == true ? "  ·  Apply Changes to update the game" : ""));
        };
        ed.Cancelled += () => { CloseEditor(); pages.Select(0); };
        editor = ed;
        editorHost.Controls.Clear();
        editorHost.Controls.Add(ed);
        pages.SetTitle(1, ed.Title.Length > 40 ? ed.Title[..40] + "…" : ed.Title);
        Theme.ApplyTree(editorHost, Palette.Dark);   // not the whole form (a double-click on the list opens this): see ShowDetails
        Restyle(editorHost);
    }

    void CloseEditor()
    {
        if (editor == null) return;
        editorHost.Controls.Clear();
        editor.Dispose();
        editor = null;
        editorHost.Controls.Add(editorPlaceholder);
        pages.SetTitle(1, "Editor");
    }

    /// <summary>
    /// --ui-selftest (scratch library only): tags, on/off, "All", undo / redo back to the original state.json, filter,
    /// grouping, and a screenshot of a tooltip. Writes selftest.txt and PNGs to <paramref name="dir"/>.
    /// </summary>
    public async Task UiSelfTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        int fails = 0;
        void Check(string what, bool ok) { if (!ok) fails++; log.Add($"{(ok ? "ok  " : "FAIL")} {what}"); }
        if (lib == null || lib.Mods.Count < 3) { File.WriteAllText(Path.Combine(dir, "selftest.txt"), "no library"); return; }
        settings.ListSort = "priority"; settings.ListGroup = "none"; filter.Text = "";
        string original = ReadState();
        string a = lib.Mods[0].FolderName, b = lib.Mods[1].FolderName, c = lib.Mods[2].FolderName;
        Mod M(string n) => lib!.Find(n)!;
        bool aOn = M(a).Enabled;

        // Real mouse clicks (window messages) on a padlock and a checkbox, as a user makes them.
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        void Click(Point p)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();   // a leaked window whose .NET side is gone fails here, not by chance
            var lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            SendMessage(list.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
            SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
            Application.DoEvents();
        }
        IntPtr listHandle = list.Handle;
        int topIndex = list.Items.Cast<object>().ToList().FindIndex(o => o is Mod);
        var lockBefore = M(a).Lock;   // the library may have it locked already
        Click(list.PartCentre(topIndex, padlock: true));
        await Task.Delay(300);
        Check($"padlock click {(lockBefore == ModLock.None ? "locks" : "unlocks")} the top mod", M(a).Lock == (lockBefore == ModLock.None ? ModLock.Top : ModLock.None));
        Click(list.PartCentre(topIndex, padlock: true));
        await Task.Delay(300);
        Check("second padlock click puts it back", M(a).Lock == lockBefore);
        Click(list.PartCentre(topIndex, padlock: false));
        await Task.Delay(300);
        Check("checkbox click toggles", M(a).Enabled != aOn);
        Click(list.PartCentre(topIndex, padlock: false));
        await Task.Delay(300);
        Check("checkbox click again restores", M(a).Enabled == aOn);
        Check("the list keeps its window through the clicks", list.Handle == listHandle);
        {
            var pt = list.PartCentre(topIndex, padlock: false); pt.X -= (int)(200 * DeviceDpi / 96f);
            var lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            SendMessage(list.Handle, 0x0201, (IntPtr)1, lp); SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp);
            SendMessage(list.Handle, 0x0203, (IntPtr)1, lp); SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp);   // WM_LBUTTONDBLCLK
            Application.DoEvents();
            await Task.Delay(500);
            Check("double-click opens the Editor tab", editor != null && pages.SelectedIndex == 1);
            CloseEditor(); pages.Select(0);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check("the list keeps its window through the double-click", list.Handle == listHandle);
        }

        Change("tag a", () => { ModLibrary.AddTag(M(a), "Selftest-One"); return true; });
        Change("tag b", () => { ModLibrary.AddTag(M(b), "Selftest-One"); ModLibrary.AddTag(M(b), "Selftest-Two"); return true; });
        Change("tag c", () => { ModLibrary.AddTag(M(c), "selftest-two"); return true; });   // other case: same tag
        Check("tags saved and reloaded", M(a).UserTags.SequenceEqual(["Selftest-One"]) && M(b).UserTags.Count == 2 && M(c).UserTags.SequenceEqual(["selftest-two"]) && ReadState().Contains("\"Tags\""));
        Check("AllTags merges case", lib.AllTags().Count(t => t.StartsWith("selftest", StringComparison.OrdinalIgnoreCase)) == 2);
        Toggle(M(a));
        Check("toggle", M(a).Enabled != aOn);
        Undo();
        Check("undo toggle", M(a).Enabled == aOn);
        Redo();
        Check("redo toggle", M(a).Enabled != aOn);
        Undo();

        // Note: typed, saved as the user's (one undo step), undone.
        SelectMod(a);
        string noteBefore = M(a).Note;
        noteBox.Text = "Test note" + "\r\n" + "line two";
        SaveNote();
        Check("note saved as yours", M(a).LocalNote == "Test note" + "\n" + "line two" && ReadState().Contains("Test note"));
        Undo();
        Check("undo note", M(a).Note == noteBefore && noteBox.Text.Replace("\r\n", "\n") == noteBefore.Replace("\r\n", "\n"));
        // An automatic tag taken off one mod is hidden there, and comes back when ticked again.
        var withAuto = lib.Mods.FirstOrDefault(x => x.AutoTags.Count > 0);
        if (withAuto != null)
        {
            string autoTag = withAuto.AutoTags[0], wn = withAuto.FolderName;
            Change("hide auto tag", () => { ModLibrary.RemoveTag(M(wn), autoTag); return true; });
            Check($"automatic tag \"{autoTag}\" hidden", !ModLibrary.HasTag(M(wn), autoTag) && M(wn).HiddenTags.Contains(autoTag));
            Change("show auto tag", () => { ModLibrary.AddTag(M(wn), autoTag); return true; });
            Check("and shown again", ModLibrary.HasTag(M(wn), autoTag) && M(wn).HiddenTags.Count == 0 && !M(wn).UserTags.Contains(autoTag));
        }

        filter.Text = "tag:selftest-one";
        Check("filter tag:selftest-one shows 2", shown.Count == 2);
        filter.Text = "#selftest-two";
        Check("filter #selftest-two shows b and c", shown.Select(m => m.FolderName).Order().SequenceEqual(new[] { b, c }.Order()));
        bool bOn = M(b).Enabled, cOn = M(c).Enabled;
        SetAllVisible();
        Check("All: every mod in the list changed together", M(b).Enabled == M(c).Enabled && shown.All(m => m.Enabled == M(b).Enabled));
        Undo();
        Check("undo All", M(b).Enabled == bOn && M(c).Enabled == cOn);
        filter.Text = "";

        settings.ListGroup = "tag"; FillList(null);
        var groups = list.Items.OfType<ModGroup>().Select(g => g.Name).ToList();
        log.Add("groups: " + string.Join(", ", groups));
        Check("grouped by tag: test tags and automatic ones, Untagged last", groups.Contains("Selftest-One") && groups.Count >= 3 && groups[^1] == "Untagged");
        Check("priority buttons off while grouped", priorityButtons.All(x => !x.Enabled));
        await Task.Delay(1500);
        using (var bmp = new Bitmap(Width, Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(Path.Combine(dir, "grouped.png")); }
        var first = list.Items.OfType<ModGroup>().First();
        list.Items.OfType<ModGroup>().ToList();
        collapsed.Add(first.Key); FillList(null);
        Check("fold a group", list.Items.OfType<ModGroup>().First().Collapsed);
        collapsed.Clear();
        settings.ListGroup = "none"; FillList(null);

        // Tooltip: shown on the Apply button, captured from the screen.
        tips.Show("Sample tooltip: the dark tip the buttons and cards show." + Environment.NewLine + "Second line.", applyButton, 0, -(int)(60 * DeviceDpi / 96f), 5000);
        await Task.Delay(700);
        var at = applyButton.PointToScreen(new Point(-(int)(300 * DeviceDpi / 96f), -(int)(70 * DeviceDpi / 96f)));
        using (var shot = new Bitmap((int)(500 * DeviceDpi / 96f), (int)(80 * DeviceDpi / 96f)))
        {
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(at, Point.Empty, shot.Size);
            shot.Save(Path.Combine(dir, "tooltip.png"));
        }
        tips.Hide(applyButton);

        while (undo.Count > 0) Undo();
        Check("undo everything: state.json back byte for byte", ReadState() == original);
        Check("redo stack holds the undone steps", redo.Count >= 4);
        log.Add(fails == 0 ? "All UI checks passed." : fails + " FAILED");
        File.WriteAllLines(Path.Combine(dir, "selftest.txt"), log);
    }

    /// <summary>--editor-snapshot: every sub-tab of the Editor tab as PNG (layout check), for a new mod or the named one.</summary>
    public async Task EditorSnapshot(string dir, string? modName)
    {
        Directory.CreateDirectory(dir);
        var m = modName == null ? null : lib?.Find(modName);
        OpenEditor(m);
        pages.Select(1);
        for (int i = 0; i < editor!.TabCount; i++)
        {
            editor.SelectTab(i);
            await Task.Delay(i is 1 or 3 ? 6000 : 1500);   // icon names / previews load in the background
            using var b = new Bitmap(Width, Height);
            DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
            b.Save(Path.Combine(dir, $"editor_{i}_{editor.TabTitle(i).Replace(' ', '_')}.png"));
        }
        CloseEditor();
    }

    /// <summary>--editor-save-test: opens a mod in the Editor, changes nothing, saves (use with MHO_EXTMM_HOME on a scratch library).</summary>
    public async Task EditorSaveTest(string modName)
    {
        var m = lib?.Find(modName);
        if (m == null) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), "no such mod"); return; }
        OpenEditor(m);
        pages.Select(1);
        var ed = editor!;
        string? saved = await ed.SaveForTest();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), saved ?? "not saved");
    }

    /// <summary>--extract-snapshot: the Extract tab on the store images, with one selected (layout check).</summary>
    public async Task ExtractSnapshot(string dir, string texture = "store_vision_classic")
    {
        Directory.CreateDirectory(dir);
        pages.Select(2);
        await Task.Delay(2000);
        if (extract != null) await extract.ShowForSnapshot(2, texture);
        using var b = new Bitmap(Width, Height);
        DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
        b.Save(Path.Combine(dir, "extract.png"));
    }

    void ExportMod()
    {
        if (Selected is not Mod m) return;
        // Mods using the extension still install in MHModManager (it skips those images); a legacy copy drops them.
        bool legacy = false;
        if (m.Manifest.Extra.Any())
        {
            var answer = Dialog.Show(this, $"{m.Name} replaces {m.Manifest.Extra.Count()} image(s) in other icon packages (an extension of the mod format).\n\n" +
                "Yes: export everything. The old MHModManager still installs it, and simply skips those images.\nNo: export a legacy copy without them.", "Export", MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) return;
            legacy = answer == DialogResult.No;
        }
        // The user's own tags and note live on this PC; offered for the exported copy (the mod's own always go along).
        List<string>? addTags = null; string? note = null;
        var mineTags = m.UserTags.Where(t => !m.ModTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!legacy && (mineTags.Count > 0 || m.LocalNote != null))
        {
            string what = (mineTags.Count > 0 ? $"your tags ({string.Join(", ", mineTags)})" : "") + (mineTags.Count > 0 && m.LocalNote != null ? " and " : "") + (m.LocalNote != null ? "your note" : "");
            var a = Dialog.Show(this, $"Put {what} into the exported mod?\n\nThey are only on this PC so far. The mod's own tags and note go along anyway; automatic tags are worked out again by whoever installs it.",
                "Export", MessageBoxButtons.YesNoCancel);
            if (a == DialogResult.Cancel) return;
            if (a == DialogResult.Yes) { addTags = mineTags; note = m.LocalNote; }
        }
        using var d = new SaveFileDialog { Title = legacy ? "Export Mod (Legacy)" : "Export Mod", Filter = "Zip archive (*.zip)|*.zip", FileName = ModInstaller.ZipName(m, legacy) };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { ModInstaller.Export(m, d.FileName, legacy, addTags, note); status.Text = $"Exported {m.Name} to {d.FileName}" + (legacy ? " (legacy: other icon packages, tags and note left out)" : addTags != null ? " (with your tags / note)" : ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async void RemoveMod()
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        if (Dialog.Show(this, $"Remove '{m.Name}' from the library? Its folder goes to the Recycle Bin.", "Remove", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var (l, g) = (lib, game);
        UseWaitCursor = true;
        string? why = await Task.Run(() => ModInstaller.Remove(m, l, g));
        UseWaitCursor = false;
        if (why != null) { Dialog.Show(this, why, "Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        Reload();
    }

    async void MoveLibrary()
    {
        using var d = new FolderBrowserDialog { Description = "An empty folder for the mod library", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string from = settings.LibraryPath, to = d.SelectedPath;
        UseWaitCursor = true;
        string? error = await Task.Run(() => { try { ModInstaller.MoveLibrary(from, to); return null; } catch (IOException ex) { return ex.Message; } });
        UseWaitCursor = false;
        if (error != null) { Dialog.Show(this, error, "Not Moved", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        settings.Library = to.Equals(Settings.DefaultLibrary, StringComparison.OrdinalIgnoreCase) ? null : to;
        settings.Save();
        Reload();
    }

    async void Migrate()
    {
        using var d = new FolderBrowserDialog { Description = "MHModManager's folder (the one with MHModManager.exe and data)", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string target = settings.LibraryPath;
        if (Settings.LibraryData(target) is string existing && Directory.EnumerateDirectories(Path.Combine(existing, "mods")).Any())
        { Dialog.Show(this, "Your library already has mods; migration only fills an empty library.", "Migrate"); return; }
        if (Dialog.Show(this, $"Copy its mods, order, verified stock backups and settings into\n{target}\n\nThe old folder is left as it is. Stop using the old manager afterwards, or the two will undo each other's changes.", "Migrate", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        UseWaitCursor = true;
        string src = d.SelectedPath;
        string log = await Task.Run(() => CaptureOutput(() => { try { Migration.Run(src, target, settings); } catch (IOException ex) { Console.WriteLine("Migration stopped: " + ex.Message); } }));
        UseWaitCursor = false;
        ShowLog("Migrate", log);
        var fresh = Settings.Load();
        settings.Library = fresh.Library; settings.GameRoot = fresh.GameRoot;
        Reload();
    }

    /// <summary>Runs an action with Console output captured (the write path reports through Console, as in the CLI).</summary>
    static readonly object consoleLock = new();
    static string CaptureOutput(Action a)
    {
        lock (consoleLock)
        {
            var old = Console.Out; var sw = new StringWriter();
            Console.SetOut(sw);
            try { a(); } catch (Exception ex) { sw.WriteLine("ERROR: " + ex.Message); }
            finally { Console.SetOut(old); }
            return sw.ToString();
        }
    }

    void ShowLog(string title, string text, Dialog.Tone tone = Dialog.Tone.Normal) => Dialog.ShowLog(this, title, text, tone);

    /// <summary>--gui-snapshot: waits for the package check and thumbnails, then saves the window as PNG (and each details tab).</summary>
    public async Task Snapshot(string dir, string? modName = null)
    {
        Directory.CreateDirectory(dir);
        if (modName != null && lib?.Find(modName) is Mod pick) { SelectMod(pick.FolderName); list.TopIndex = Math.Max(0, list.SelectedIndex - 5); }
        foreach (var t in new[] { loading, pending }) if (t != null) { try { await t; } catch { } }
        await Task.Delay(1500);
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(Path.Combine(dir, "main.png"));
        // Each details tab too.
        for (int i = 1; i < 8; i++)
        {
            int before = tabs.SelectedIndex; tabs.Select(i); if (tabs.SelectedIndex != i) break;
            await Task.Delay(1200);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"main_tab{i}.png"));
        }
    }
}
