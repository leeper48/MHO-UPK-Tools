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
    readonly StorePreview storePreview = new() { Dock = DockStyle.Left };
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(9f), Padding = new Padding(12, 0, 0, 0) };
    readonly Button applyButton;
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
        menu.Items.Add("Change game folder…", null, (_, _) => BrowseGame());
        menu.Items.Add("Move library…", null, (_, _) => MoveLibrary());
        menu.Items.Add("Open library folder", null, (_, _) => { if (Settings.LibraryData(settings.LibraryPath) is string d) Process.Start("explorer.exe", $"\"{d}\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Capture icon changes", null, (_, _) => CaptureIcons());
        menu.Items.Add("Migrate from MHModManager…", null, (_, _) => Migrate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, (_, _) => Reload());
        menu.Items.Add("About", null, (_, _) => About());
        settingsButton.Click += (_, _) => menu.Show(settingsButton, new Point(0, settingsButton.Height));
        topButtons.Controls.AddRange([newMod, extractButton, install, settingsButton]);
        writeControls.AddRange([newMod, install]);
        top.Controls.Add(topButtons, 3, 0);

        // ---- Left: installed mods
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6, 4, 2, 0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var lhead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 7, Margin = new Padding(0) };
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lhead.Controls.Add(new Label { Text = "INSTALLED MODS", AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Bold(8.5f), Tag = "subtle" }, 0, 0);
        lhead.Controls.Add(countLabel, 1, 0);
        lhead.Controls.Add(new Label { Text = "Priority:", AutoSize = true, Anchor = AnchorStyles.Right, Font = Ui.Regular(8.5f), Tag = "subtle" }, 2, 0);
        var first = Ui.FlatButton("▲", () => MoveSelected(-1, toEnd: true)); var up = Ui.FlatButton("▲", () => MoveSelected(-1));
        var down = Ui.FlatButton("▼", () => MoveSelected(1)); var last = Ui.FlatButton("▼", () => MoveSelected(1, toEnd: true));
        Ui.AddEndBar(first, top: true); Ui.AddEndBar(last, top: false);   // ▲ with a bar over it = to the top; ▼ with one under it = to the bottom
        var priorityTips = new ToolTip();
        priorityTips.SetToolTip(first, "To the top (below any mods locked there)"); priorityTips.SetToolTip(up, "Up one");
        priorityTips.SetToolTip(down, "Down one"); priorityTips.SetToolTip(last, "To the bottom (above any mods locked there)");
        first.Padding = up.Padding = down.Padding = last.Padding = new Padding(2, 0, 2, 0);
        lhead.Controls.Add(first, 3, 0); lhead.Controls.Add(up, 4, 0); lhead.Controls.Add(down, 5, 0); lhead.Controls.Add(last, 6, 0);
        writeControls.AddRange([first, up, down, last]);
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.Controls.Add(new Label { Text = "Filter", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
        filterRow.Controls.Add(filter, 1, 0);
        left.Controls.Add(lhead, 0, 0); left.Controls.Add(filterRow, 0, 1); left.Controls.Add(list, 0, 2);
        filter.TextChanged += (_, _) => FillList(Selected?.FolderName);
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        list.CheckClicked += m => Toggle(m);
        list.LockClicked += m => ToggleLock(m);
        list.CanLock = m => lib?.CanLock(m) ?? ModLock.None;
        list.DoubleClick += (_, _) => { if (Selected is Mod m) EditMod(m); };

        // ---- Right: the selected mod
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        right.Controls.Add(tabs); right.Controls.Add(header);
        header.PillClicked += () => { if (Selected is Mod m) Toggle(m); };

        // Middle column: the selected mod's store image (Kurt), between the list and the details.
        var middleAndRight = new Panel { Dock = DockStyle.Fill };
        middleAndRight.Controls.Add(right); middleAndRight.Controls.Add(storePreview);

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
        leftButtons.Controls.AddRange([remove, edit, export]);
        writeControls.AddRange([remove, edit, applyButton]);
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(status, 1, 0);
        applyButton.Anchor = AnchorStyles.Right;
        bottom.Controls.Add(applyButton, 2, 0);

        var modsPage = new Panel { Dock = DockStyle.Fill };
        modsPage.Controls.Add(split); modsPage.Controls.Add(bottom);

        // Editor tab: a placeholder until + New Mod or Edit Mod opens one.
        var ph = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        ph.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        ph.Controls.Add(new Label { Text = "No mod open", AutoSize = true, Anchor = AnchorStyles.None, Font = Ui.Bold(14f), Tag = "subtle" }, 0, 1);
        var phButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(0, 10, 0, 0) };
        phButtons.Controls.AddRange([Ui.AccentButton("+  New Mod", () => EditMod(null)), Ui.FlatButton("Edit the selected mod", () => { if (Selected is Mod m) EditMod(m); })]);
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
            storePreview.Width = (int)(250 * DeviceDpi / 96f);   // DeviceDpi is only right once the handle exists
            Reload();
        };
        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.30);   // after maximizing
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
        extractHost.Controls.Clear();
        extract = new ExtractView(new StockCatalog(lib, game));
        extractKey = key;
        extractHost.Controls.Add(extract);
        Theme.Apply(this, Palette.Dark);
        Restyle(this);
    }

    void UpdateRunning()
    {
        bool running = Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;
        runningLabel.Text = running ? "●  Game running: close it to apply" : "●  Game not running";
        runningLabel.ForeColor = running ? Ui.Warn : Ui.Subtle;
    }

    void About() => MessageBox.Show(this,
        $"MHO Extended Mod Manager {Program.Version}\n\nMods for Marvel Heroes Omega: packages, icons, strings and sound packs, in the MHModManager mod format. " +
        "Every change is written from verified originals, checked, and can be undone.\n\nSettings and mods: " + Settings.Home, "About");

    void BrowseGame()
    {
        using var d = new FolderBrowserDialog { Description = "The Marvel Heroes folder (holds UnrealEngine3 and Data)", UseDescriptionForTitle = true, SelectedPath = settings.GameRoot ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(Settings.Cooked(d.SelectedPath))) { MessageBox.Show(this, "No UnrealEngine3\\MarvelGame\\CookedPCConsole there.", Text); return; }
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
        string? gameRoot = settings.ResolvedGameRoot(data);
        gameLabel.Text = gameRoot != null ? Settings.TrueCase(gameRoot) : "(not set: Settings → Change game folder)";
        if (gameRoot != null && Directory.Exists(Settings.Cooked(gameRoot))) game = new GameState(gameRoot, data);
        winners = lib.PackageWinners();
        var conflicts = lib.Conflicts();
        conflicted = conflicts.SelectMany(c => c.Mods).ToHashSet();
        list.Conflicted = conflicted;
        countLabel.Text = $"{lib.Mods.Count(m => m.Enabled)} of {lib.Mods.Count} on";
        countLabel.ForeColor = Ui.Subtle;
        status.Text = (game == null ? "Game folder not found" : conflicts.Count > 0 ? $"{conflicts.Count} conflicting change(s): the mod higher in the list wins" : "No conflicts") +
                      (readOnly ? "  ·  MHModManager's own folder: read-only here (Settings → Migrate)" : "");
        status.ForeColor = Ui.Subtle;
        applyButton.Text = "Apply Changes";
        FillList(keep);
        if (!readOnly && game != null && game.HasStockList) CountPending();
    }

    /// <summary>The list, filtered by the filter box (name or author), keeping the selection.</summary>
    void FillList(string? keep)
    {
        if (lib == null) return;
        string q = filter.Text.Trim();
        shown = lib.Mods.Where(m => q.Length == 0 || m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || (m.Manifest.Author ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var m in shown) list.Items.Add(m);
        list.EndUpdate();
        int i = shown.FindIndex(m => m.FolderName == keep);
        if (i < 0 && shown.Count > 0) i = 0;
        if (i >= 0) { list.SelectedIndex = i; } else { header.Mod = null; header.Invalidate(); tabs.Clear(); }
    }

    void SelectMod(string? folderName)
    {
        int i = shown.FindIndex(m => m.FolderName == folderName);
        if (i >= 0) list.SelectedIndex = i;
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
            status.Text = baseText + (n == 0 ? "  ·  the game matches your list" : $"  ·  {n} file(s) to change") +
                          (t.Result.Problems.Count > 0 ? $"  ·  {t.Result.Problems.Count} can't be (see Apply)" : "");
            status.ForeColor = n == 0 ? Ui.Subtle : Ui.Text;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Toggle(Mod m)
    {
        if (readOnly || lib == null) return;
        m.Enabled = !m.Enabled;
        lib.SaveState();
        Reload();
    }

    void MoveSelected(int delta, bool toEnd = false)
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        if (!(toEnd ? lib.MoveToEnd(m, delta) : lib.Move(m, delta)))
        {
            status.Text = $"'{m.Name}' is locked at the {(m.Lock == ModLock.Top ? "top" : "bottom")}: click its padlock to unlock it first.";
            return;
        }
        lib.SaveState();
        Reload();
    }

    /// <summary>Padlock: locks a mod at the top or bottom (it must be there, or next to a mod locked there), or unlocks it.</summary>
    void ToggleLock(Mod m)
    {
        if (readOnly || lib == null) return;
        bool wasLocked = m.Lock != ModLock.None;
        if (!lib.ToggleLock(m)) return;
        lib.SaveState();
        Reload();
        status.Text = wasLocked ? $"Unlocked '{m.Name}'." : $"Locked '{m.Name}' at the {(m.Lock == ModLock.Top ? "top" : "bottom")}: it stays there and other mods can't move past it.";
    }

    // ---- Details

    void ShowDetails()
    {
        if (Selected is not Mod m || lib == null) return;
        header.Mod = m; header.Conflicted = conflicted.Contains(m); header.Invalidate();
        storePreview.Mod = m;
        int keepTab = Math.Max(0, tabs.SelectedIndex);
        tabs.SuspendLayout();
        tabs.Clear();

        if (m.Manifest.UpkReplacements.Count > 0)
        {
            var grid = Grid(false, ("UPK Filename", 0), ("Size", 90), ("In the game", 330));
            var rows = new Dictionary<string, DataGridViewRow>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in m.Manifest.UpkReplacements)
            {
                string path = Path.Combine(m.Folder, f);
                string size = File.Exists(path) ? $"{new FileInfo(path).Length / 1048576.0:0.0} MB" : "missing";
                string state = !m.Enabled ? "turned off" : winners.TryGetValue(f, out var w) && w != m ? $"overridden by {w.Name}" : game == null ? "" : "checking…";
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
                                row.Cells[2].Value = s == PackageState.Applied ? "applied ✓" : GameState.Describe(s) + " (Apply)";
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
            var grid = Grid(false, ("Sound pack", 0), ("Events", 80), ("Sound files it patches", 380));
            foreach (string f in m.Manifest.AudioPacks)
            {
                string events = "?", pcks = "";
                try { var p = SoundPack.Load(Path.Combine(m.Folder, f)); events = p.Patches.Count.ToString(); pcks = string.Join(", ", p.Patches.Select(x => x.PckFile).Distinct()); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException) { pcks = "unreadable"; }
                grid.Rows.Add(f, events, pcks);
            }
            tabs.Add("Sound packs", grid);
        }

        var mineConflicts = lib.Conflicts().Where(c => c.Mods.Contains(m)).ToList();
        if (mineConflicts.Count > 0)
        {
            var grid = Grid(false, ("Change", 0), ("Result", 420));
            foreach (var (claim, mods) in mineConflicts)
            {
                int i = grid.Rows.Add(claim, mods[0] == m ? "wins over " + string.Join(", ", mods.Skip(1).Select(x => x.Name)) : "loses to " + mods[0].Name);
                grid.Rows[i].Cells[1].Style.ForeColor = mods[0] == m ? Ui.Enabled : Ui.Warn;
            }
            tabs.Add($"Conflicts ({mineConflicts.Count})", grid);
        }

        var info = new List<string>
        {
            $"Name:      {m.Name}", $"Author:    {m.Manifest.Author}", $"Version:   {m.Manifest.Version}", $"Priority:  {m.Priority + 1} of {lib.Mods.Count} (higher wins)",
            $"State:     {(m.Enabled ? "enabled" : "disabled")}", $"Folder:    {m.Folder}",
        };
        if (m.LoadError != null) info.Add($"\nERROR: {m.LoadError}");
        var missing = m.MissingFiles().ToList();
        if (missing.Count > 0) info.Add("\nMISSING FILES: " + string.Join(", ", missing));
        tabs.Add("Info", new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Ui.Back, ForeColor = Ui.Text, Font = mono, Text = string.Join("\r\n", info).Replace("\n", "\r\n").Replace("\r\r", "\r") });

        Theme.Apply(this, Palette.Dark);   // colours for the new controls
        Restyle(this);
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
        if (!game.HasStockList) { MessageBox.Show(this, "No stock checksum list in the library, so originals can't be verified.", Text); return; }
        if (Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { MessageBox.Show(this, "The game is running. Close it first.", Text); return; }
        var (l, g) = (lib, game);
        var originals = new Originals(l.DataFolder, g);
        UseWaitCursor = true;
        var plan = await Task.Run(() => Applier.MakePlan(l, g, originals));
        UseWaitCursor = false;
        string planText = CaptureOutput(() => Applier.Print(plan));
        if (plan.Steps.Count == 0) { ShowLog("Apply", planText); return; }
        if (MessageBox.Show(this, planText + "\n\nWrite these changes to the game folder? Each file is verified and can be undone.", "Apply", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        UseWaitCursor = true;
        string log = await Task.Run(() => CaptureOutput(() => Applier.Execute(plan, g, originals, l.DataFolder)));
        UseWaitCursor = false;
        ShowLog("Apply", log);
        Reload();
    }

    /// <summary>Icon changes in the game that no mod accounts for, saved as a mod so Apply's rebuild from stock keeps them.</summary>
    async void CaptureIcons()
    {
        if (readOnly || lib == null || game == null) return;
        var (l, g) = (lib, game);
        UseWaitCursor = true;
        string log = await Task.Run(() => CaptureOutput(() => IconCapture.Run(l, g, new Originals(l.DataFolder, g), null)));
        UseWaitCursor = false;
        ShowLog("Capture icon changes", log);
        Reload();
    }

    void InstallMod()
    {
        using var d = new OpenFileDialog { Title = "Install mod", Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*", Multiselect = true };
        if (d.ShowDialog(this) == DialogResult.OK) Install(d.FileNames);
    }

    async void Install(string[] sources)
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
                try { all.AddRange(ModInstaller.Install(s, ModLibrary.Load(l.DataFolder), log)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { log.Add("  couldn't read it: " + ex.Message); }
            }
            return (log, all);
        });
        UseWaitCursor = false;
        filter.Text = "";
        Reload();
        if (installed.Count > 0) SelectMod(installed[0]);
        ShowLog("Install", string.Join("\n", log) + (installed.Count > 0 ? "\n\nNew mods are added at the top of the list, turned off: tick one, then Apply Changes." : ""));
    }

    /// <summary>Opens a mod (or a new one) in the Editor tab. Saving or cancelling returns to the Mods tab.</summary>
    void EditMod(Mod? m)
    {
        if (readOnly || lib == null) return;
        if (editor != null)
        {
            if (editor.Editing?.FolderName == m?.FolderName && m != null) { pages.Select(1); return; }
            if (MessageBox.Show(this, $"\"{editor.Title}\" is open in the Editor. Close it (unsaved changes are lost) and open {(m == null ? "a new mod" : $"\"{m.Name}\"")}?", "Editor", MessageBoxButtons.OKCancel) != DialogResult.OK) { pages.Select(1); return; }
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
            status.Text = (m == null ? $"Created '{name}' (top of the list, off: tick it, then Apply Changes)." : $"Saved '{name}'.") + (m?.Enabled == true ? "  Apply Changes to update the game." : "");
        };
        ed.Cancelled += () => { CloseEditor(); pages.Select(0); };
        editor = ed;
        editorHost.Controls.Clear();
        editorHost.Controls.Add(ed);
        pages.SetTitle(1, ed.Title.Length > 40 ? ed.Title[..40] + "…" : ed.Title);
        Theme.Apply(this, Palette.Dark);
        Restyle(this);
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
            var answer = MessageBox.Show(this, $"{m.Name} replaces {m.Manifest.Extra.Count()} image(s) in other icon packages (an extension of the mod format).\n\n" +
                "Yes: export everything. The old MHModManager still installs it, and simply skips those images.\nNo: export a legacy copy without them.", "Export", MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel) return;
            legacy = answer == DialogResult.No;
        }
        using var d = new SaveFileDialog { Title = legacy ? "Export mod (legacy)" : "Export mod", Filter = "Zip archive (*.zip)|*.zip", FileName = ModInstaller.Sanitise(m.Name) + (legacy ? " (legacy)" : "") + ".zip" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { ModInstaller.Export(m, d.FileName, legacy); status.Text = $"Exported {m.Name} to {d.FileName}" + (legacy ? " (legacy: other icon packages left out)" : ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Export failed: " + ex.Message, Text); }
    }

    async void RemoveMod()
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        if (MessageBox.Show(this, $"Remove '{m.Name}' from the library? Its folder goes to the Recycle Bin.", "Remove", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var (l, g) = (lib, game);
        UseWaitCursor = true;
        string? why = await Task.Run(() => ModInstaller.Remove(m, l, g));
        UseWaitCursor = false;
        if (why != null) { MessageBox.Show(this, $"Not removed. {why}", "Remove"); return; }
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
        if (error != null) { MessageBox.Show(this, "Not moved: " + error, Text); return; }
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
        { MessageBox.Show(this, "Your library already has mods; migration only fills an empty library.", "Migrate"); return; }
        if (MessageBox.Show(this, $"Copy its mods, order, verified stock backups and settings into\n{target}\n\nThe old folder is left as it is. Stop using the old manager afterwards, or the two will undo each other's changes.", "Migrate", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
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

    void ShowLog(string title, string text)
    {
        using var f = new Form { Text = title, StartPosition = FormStartPosition.Manual, Icon = Icon, Owner = this };
        Ui.FitToScreen(f, 1000, 600);
        f.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = mono, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n") });
        Theme.Apply(f, Palette.Dark);
        f.ShowDialog(this);
    }

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
