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
    Dictionary<Mod, Dictionary<Mod, int>> conflictWith = [];   // mod → other mod → shared changes
    Task? loading, pending;
    bool readOnly;
    readonly List<Control> writeControls = [];
    int thumbRequest;

    readonly Label gameLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(9.75f) };
    readonly Label runningLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(12, 0, 0, 0), Font = Ui.Regular(9f) };
    readonly ModListBox list = new() { Dock = DockStyle.Fill };
    readonly TextBox filter = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
    readonly NexusStatus nexusStatus = new() { Dock = DockStyle.Fill };
    string? nexusBusy;
    string? listCatalogKey;   // what Nexus work is running ("Checking 23 Linked Mods…"), shown in the Nexus strip
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
    bool reviewable;   // the status line lists changes or skipped files: a click opens the plan
    readonly Button applyButton;
    // Dark hover tips on the buttons and boxes (the mod list shows its own, per part of a card).
    readonly ToolTip tips = Ui.Tips;
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
    // Nexus: the last fetched info per mod ID (data\nexus_cache.json), and a watch on Downloads for a free account's update.
    NexusCache nexus = NexusCache.Load(Settings.Home);
    FileSystemWatcher? downloadWatch;
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

    bool wasMaximized = true;

    /// <summary>
    /// Where the window starts: where it was left (monitor, size, position, maximized) while that spot is still on a
    /// connected monitor (a user's request: with two monitors it always came up on the main one); else maximized on the
    /// main monitor, as before. Off in Settings → Remember Window Position.
    /// </summary>
    void PlaceWindow()
    {
        Width = 1400; Height = 850;
        if (settings.RememberWindow && settings.WindowBounds is [int x, int y, int w, int h] && w >= 400 && h >= 300)
        {
            var saved = new Rectangle(x, y, w, h);
            var screen = Screen.AllScreens.FirstOrDefault(s => Rectangle.Intersect(s.WorkingArea, saved) is { Width: >= 200, Height: >= 100 });
            if (screen != null)
            {
                var area = screen.WorkingArea;
                int cw = Math.Min(w, area.Width), ch = Math.Min(h, area.Height);
                StartPosition = FormStartPosition.Manual;
                Bounds = new Rectangle(Math.Clamp(x, area.Left, area.Right - cw), Math.Clamp(y, area.Top, area.Bottom - ch), cw, ch);
                WindowState = settings.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
                wasMaximized = settings.WindowMaximized;
                return;
            }
        }
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
    }

    /// <summary>The rectangle moved onto <paramref name="screen"/> (same offset from its working area, clamped) unless it's mostly there already.</summary>
    internal static Rectangle OnScreenOf(Rectangle r, Screen screen)
    {
        var area = screen.WorkingArea;
        var cut = Rectangle.Intersect(area, r);
        if (cut.Width * (long)cut.Height * 2 >= r.Width * (long)r.Height) return r;
        var from = (Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(r.Location)) ?? Screen.PrimaryScreen!).WorkingArea;
        int w = Math.Min(r.Width, area.Width), h = Math.Min(r.Height, area.Height);
        return new Rectangle(Math.Clamp(area.Left + r.X - from.Left, area.Left, area.Right - w), Math.Clamp(area.Top + r.Y - from.Top, area.Top, area.Bottom - h), w, h);
    }

    /// <summary>Keeps the window's place for the next start, only when it's on a monitor (test windows off-screen aren't kept).</summary>
    void SaveWindowPlace()
    {
        if (!settings.RememberWindow) return;
        var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        // A maximized window moved to another monitor (dragged, Win+Shift+Arrow) keeps its restored rectangle on the old one
        // (a user's report, 2026-09-30: always back on monitor 1 after the first, maximized start). Keep it on the monitor
        // the window is maximized on, at the same place relative to that monitor.
        if (WindowState == FormWindowState.Maximized) normal = OnScreenOf(normal, Screen.FromControl(this));
        if (normal.Width < 400 || normal.Height < 300 || !Screen.AllScreens.Any(s => Rectangle.Intersect(s.WorkingArea, normal) is { Width: >= 200, Height: >= 100 })) return;
        settings.WindowBounds = [normal.X, normal.Y, normal.Width, normal.Height];
        settings.WindowMaximized = WindowState == FormWindowState.Maximized || (WindowState == FormWindowState.Minimized && wasMaximized);
        settings.Save();
    }

    public MainForm()
    {
        MhoExtendedModManager.Model.Settings.App = settings;   // the Model tab's settings are changed on this copy (it's saved again on exit)
        current = this;
        Text = $"MHO Extended Mod Manager v{Program.Version}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this, Ui.GradientTop);   // the title bar continues the window's navy
        PlaceWindow();
        Font = Ui.Regular(9.5f);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        applyButton = Ui.AccentButton("Apply Changes", Apply);

        // ---- Top bar
        // Kurt's order: New Mod, Extract, Install Mod, the game folder and whether it runs on the left; the update alert and
        // Settings on the right.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(10, 6, 10, 6) };
        topBar = top;
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Game Root:", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Font = Ui.Regular(9.75f), Margin = new Padding(12, 0, 0, 0) }, 1, 0);
        top.Controls.Add(gameLabel, 2, 0);
        top.Controls.Add(runningLabel, 3, 0);
        var topButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left };
        var rightButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var newMod = Ui.AccentButton("+  New Mod", () => EditMod(null));
        var install = Ui.FlatButton("Install Mod", InstallMod);
        var settingsButton = Ui.FlatButton("Settings  ▾", () => { });
        var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f) };
        menu.Items.Add("Change Game Folder", null, (_, _) => BrowseGame());
        menu.Items.Add("Clean Game Files Folder", null, (_, _) => BrowseClean()).ToolTipText = "A folder with a clean copy of the game's CookedPCConsole (only read, never changed): previews, power colors and Apply take the game's own files from it when the game folder's are changed.";
        menu.Items.Add("Changed Game Files", null, (_, _) => ChangedGameFiles()).ToolTipText = "Game files changed by something other than this app: keep them as a mod, or put the game's originals back.";
        menu.Items.Add("Check Backups", null, (_, _) => CheckBackups()).ToolTipText = "Lists the game files that differ from the game's own, whether each one's .bak is truly the original, and where a clean copy is.";
        menu.Items.Add("Move Library", null, (_, _) => MoveLibrary());
        menu.Items.Add("Open Library Folder", null, (_, _) => { if (Settings.LibraryData(settings.LibraryPath) is string d) Process.Start("explorer.exe", $"\"{d}\""); });
        // The Editor's Model tab (in development): its settings here, grouped (Kurt: one Settings button)
        if (WhatsNew.ModelTab) menu.Items.Add(MhoExtendedModManager.Model.Gui.ModelPage.SettingsMenu(this, () => editor?.ModelPageForTest?.SettingsChanged()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Capture Icon Changes", null, (_, _) => CaptureIcons());
        menu.Items.Add("Migrate from MHModManager", null, (_, _) => Migrate());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, (_, _) => Reload());
        menu.Items.Add("Find My Mods on Nexus", null, (_, _) => FindOnNexus(null));
        menu.Items.Add("Check Mods on Nexus", null, (_, _) => CheckNexus(manual: true));
        var nexusAtStart = new ToolStripMenuItem("Check Mods on Nexus at Start") { CheckOnClick = true };
        nexusAtStart.CheckedChanged += (_, _) => { if (settings.NexusCheckAtStart != nexusAtStart.Checked) { settings.NexusCheckAtStart = nexusAtStart.Checked; settings.Save(); } };
        menu.Opening += (_, _) => nexusAtStart.Checked = settings.NexusCheckAtStart;
        menu.Items.Add(nexusAtStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check for Updates", null, (_, _) => CheckForUpdates(manual: true));
        var autoCheck = new ToolStripMenuItem("Check for Updates at Start") { CheckOnClick = true };
        autoCheck.CheckedChanged += (_, _) => { if (settings.CheckUpdates != autoCheck.Checked) { settings.CheckUpdates = autoCheck.Checked; settings.Save(); } };
        menu.Opening += (_, _) => autoCheck.Checked = settings.CheckUpdates;
        menu.Items.Add(autoCheck);
        menu.Items.Add("Your Author Name", null, (_, _) => EditAuthorName()).ToolTipText = "Your name as a mod author: filled in on every new mod you make.";
        var rememberWindow = new ToolStripMenuItem("Remember Window Position") { CheckOnClick = true, ToolTipText = "Start on the monitor, at the size and place you left the window (maximized if it was). Off: always start maximized on the main monitor." };
        rememberWindow.CheckedChanged += (_, _) => { if (settings.RememberWindow != rememberWindow.Checked) { settings.RememberWindow = rememberWindow.Checked; settings.Save(); } };
        menu.Opening += (_, _) => rememberWindow.Checked = settings.RememberWindow;
        menu.Items.Add(rememberWindow);
        menu.Items.Add("What's New", null, (_, _) => ShowNotice(0)).ToolTipText = "The notices of new features shown when a version starts the first time, newest first, with a link to the manual.";
        menu.Items.Add("Changelog", null, (_, _) => ShowChangelog());
        menu.Items.Add("Download Counts", null, (_, _) => { using var f = new DownloadsForm(settings); f.ShowDialog(this); }).ToolTipText = "How often each release of the app was downloaded from GitHub (downloads, not people).";
        menu.Items.Add("About", null, (_, _) => About());
        settingsButton.Click += (_, _) => Ui.ShowUnder(menu, settingsButton);
        // Update alert (Kurt): shown when a newer release is known; a click offers it (the update window).
        updateAlert = Ui.FlatButton("↑ Update Available", () => CheckForUpdates(manual: true), tip: "A new version of MHO Extended Mod Manager is out. Click to see what's new and update (it restarts).");
        updateAlert.Visible = false;
        topButtons.Controls.AddRange([newMod, install]);   // (one Extract: the tab, Kurt)
        var helpButton = Ui.FlatButton("Help", () => HelpForm.Show(this, settings), tip: "The manual: how everything works, shortcuts, troubleshooting (F1).");
        rightButtons.Controls.AddRange([updateAlert, helpButton, settingsButton]);
        writeControls.AddRange([newMod, install]);
        tips.SetToolTip(newMod, "Make a new mod from packages, icons, store images, strings or sound packs (opens the Editor tab).");
        tips.SetToolTip(install, "Add a mod from a .ZIP, .7Z, .RAR or folder. You can also drop it on the window.");
        tips.SetToolTip(settingsButton, "Game folder, library folder, the Model tab's MFF folder and Blender, capture icon changes, migrate from MHModManager, Nexus, updates, changelog, about.");
        tips.SetToolTip(runningLabel, "Changes can only be applied while the game is closed.");
        // icons (Kurt, 2026-10-04): the names head the tooltips
        Icons.Make(newMod, "New Mod", Icons.Plus, DeviceDpi / 96f);
        Icons.Make(install, "Install Mod", Icons.Install, DeviceDpi / 96f);
        Icons.Make(helpButton, "Help", Icons.Help, DeviceDpi / 96f);
        Icons.Make(settingsButton, "Settings", Icons.WithMenu(Icons.Gear), DeviceDpi / 96f);
        top.Controls.Add(topButtons, 0, 0);
        top.Controls.Add(rightButtons, 4, 0);

        // ---- Left: installed mods
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(6, 4, 2, 0) };
        // One column that fits the panel (Kurt: the Nexus strip's buttons were cut off). Without a style it sizes to its
        // widest row and runs past the list's edge.
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 46 * DeviceDpi / 96f));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var lhead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 9, Margin = new Padding(0) };
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lhead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) lhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var headLabel = new Label { Text = "INSTALLED MODS", AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Bold(8.5f), Tag = "subtle" };
        lhead.Controls.Add(headLabel, 0, 0);
        lhead.Controls.Add(countLabel, 1, 0);
        undoButton = Ui.FlatButton("Undo", Undo); redoButton = Ui.FlatButton("Redo", Redo);
        Icons.Make(undoButton, "Undo", Icons.Undo, DeviceDpi / 96f); Icons.Make(redoButton, "Redo", Icons.Redo, DeviceDpi / 96f);
        lhead.Controls.Add(undoButton, 2, 0); lhead.Controls.Add(redoButton, 3, 0);
        var priorityLabel = new Label { Text = "Priority:", AutoSize = true, Anchor = AnchorStyles.Right, Font = Ui.Regular(8.5f), Tag = "subtle", Margin = new Padding(10, 0, 0, 0) };
        lhead.Controls.Add(priorityLabel, 4, 0);
        var first = Ui.FlatButton("▲", () => MoveSelected(-1, toEnd: true)); var up = Ui.FlatButton("▲", () => MoveSelected(-1));
        var down = Ui.FlatButton("▼", () => MoveSelected(1)); var last = Ui.FlatButton("▼", () => MoveSelected(1, toEnd: true));
        Ui.AddEndBar(first, top: true); Ui.AddEndBar(last, top: false);   // ▲ with a bar over it = to the top; ▼ with one under it = to the bottom
        tips.SetToolTip(first, "To the top (below any mods locked there). Ctrl+Home. Moves all selected mods (Shift / Ctrl+click) together.");
        tips.SetToolTip(up, "Up one (Ctrl+Up). Higher mods win where two change the same thing. Moves all selected mods together.");
        tips.SetToolTip(down, "Down one (Ctrl+Down). Moves all selected mods together.");
        tips.SetToolTip(last, "To the bottom (above any mods locked there). Ctrl+End. Moves all selected mods together.");
        first.Padding = up.Padding = down.Padding = last.Padding = new Padding(2, 0, 2, 0);
        lhead.Controls.Add(first, 5, 0); lhead.Controls.Add(up, 6, 0); lhead.Controls.Add(down, 7, 0); lhead.Controls.Add(last, 8, 0);
        priorityButtons.AddRange([first, up, down, last]);

        // Filter row: search box, sort, group, and the box that turns every mod in the list on or off.
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 4, 0, 4) };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.Controls.Add(new Label { Text = "Filter", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
        filterRow.Controls.Add(filter, 1, 0);
        sortButton = Ui.FlatButton("Sort  ▾", () => { }); groupButton = Ui.FlatButton("Group  ▾", () => { });
        sortButton.Click += (_, _) => Ui.ShowUnder(SortMenu(), sortButton);
        groupButton.Click += (_, _) => Ui.ShowUnder(GroupMenu(), groupButton);
        filterRow.Controls.Add(sortButton, 2, 0); filterRow.Controls.Add(groupButton, 3, 0); filterRow.Controls.Add(allVisible, 4, 0);
        allVisible.Click += (_, _) => SetAllVisible();
        tips.SetToolTip(filter, "Search names, authors and tags (every word must match).\ntag:x or #x   tags only (tag:\"two words\")\nis:on   is:off   is:locked   is:untagged\nis:update   is:nexus   is:conflict   (Nexus updates, linked mods, conflicts)");
        tips.SetToolTip(sortButton, "Order the list by priority, name, author, tag, on first, or Nexus updates first. Priority buttons and padlocks work in the priority order only.");
        tips.SetToolTip(groupButton, "Group the list by tag or author. Click a group's header to fold it.");
        // Nexus strip (Kurt: status and actions by the list, not in Settings): status (click = the next step), Check for Updates, Find My Mods, ▾.
        var nexusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 2) };
        nexusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); nexusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nexusRow.Controls.Add(nexusStatus, 0, 0);
        var nexusButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(4, 0, 0, 0) };
        var nexusCheck = Ui.FlatButton("Check for Updates", () => CheckNexus(manual: true),
            "Ask Nexus whether your linked mods have newer versions (public information: no account or key needed). Mods with one get a green Update mark.");
        var nexusFind = Ui.FlatButton("Find My Mods", () => FindOnNexus(null),
            "Look up your mods that aren't linked yet among the Nexus mods for Marvel Heroes Omega, and link the right ones.");
        var nexusMore = Ui.FlatButton("▾", () => { }, "Nexus account, checking at start, the Nexus mod pages.");
        nexusMore.Click += (_, _) => Ui.ShowUnder(NexusBarMenu(), nexusMore);
        nexusCheck.Padding = nexusFind.Padding = new Padding(4, 0, 4, 0); nexusMore.Padding = new Padding(2, 0, 2, 0);
        var nexusBrowse = Ui.FlatButton("Browse Nexus", () => Process.Start(new ProcessStartInfo(Nexus.SiteMods) { UseShellExecute = true }),
            "Open the Marvel Heroes Omega mods on Nexus in your browser.");
        nexusBrowse.Padding = new Padding(4, 0, 4, 0);
        // icons (Kurt, 2026-10-04): the names head the tooltips (and name them in ▾ when folded away)
        Icons.Make(nexusCheck, "Check for Nexus Updates", Icons.CheckUpdates, DeviceDpi / 96f);
        Icons.Make(nexusFind, "Find My Mods on Nexus", Icons.Search, DeviceDpi / 96f);
        Icons.Make(nexusBrowse, "Browse Nexus", Icons.Globe, DeviceDpi / 96f);
        nexusButtons.Controls.AddRange([nexusCheck, nexusFind, nexusBrowse, nexusMore]);
        nexusRow.Controls.Add(nexusButtons, 1, 0);
        nexusFolded = [nexusBrowse, nexusFind, nexusCheck];
        nexusActions[nexusCheck] = () => CheckNexus(manual: true);
        nexusActions[nexusFind] = () => FindOnNexus(null);
        nexusActions[nexusBrowse] = () => Process.Start(new ProcessStartInfo(Nexus.SiteMods) { UseShellExecute = true });
        // A narrow list: buttons fold into ▾ (Browse, then Find, then Check) so the status keeps a readable width; the
        // head row drops its "Priority:" label before anything is cut.
        nexusRow.Resize += (_, _) => FitNexusRow(nexusRow, nexusButtons);
        lhead.Resize += (_, _) =>
        {
            // Narrow: drop "Priority:", then shorten the heading to MODS, then drop the count (nothing wraps or is cut).
            int W(Control c) => TextRenderer.MeasureText(c.Text, c.Font).Width + c.Margin.Horizontal + c.Padding.Horizontal + 4;
            int fixedW = lhead.Controls.Cast<Control>().Where(c => c is Button).Sum(c => c.GetPreferredSize(Size.Empty).Width + c.Margin.Horizontal);
            int count = W(countLabel), prio = W(priorityLabel);
            int headFull = TextRenderer.MeasureText("INSTALLED MODS", headLabel.Font).Width + 8, headShort = TextRenderer.MeasureText("MODS", headLabel.Font).Width + 8;
            int w = lhead.ClientSize.Width;
            bool showPrio = w >= fixedW + headFull + count + prio;
            bool full = w >= fixedW + headFull + count;
            bool showCount = w >= fixedW + headShort + count;
            if (priorityLabel.Visible != showPrio) priorityLabel.Visible = showPrio;
            string head = full || !showCount && w >= fixedW + headFull ? "INSTALLED MODS" : "MODS";
            if (headLabel.Text != head) headLabel.Text = head;
            if (countLabel.Visible != showCount) countLabel.Visible = showCount;
        };
        nexusStatus.StatusClicked += NexusStatusClicked;
        writeControls.Add(nexusFind);
        left.Controls.Add(nexusRow, 0, 0); left.Controls.Add(lhead, 0, 1); left.Controls.Add(filterRow, 0, 2); left.Controls.Add(list, 0, 3);
        filter.TextChanged += (_, _) => { FillList(Selected?.FolderName); UpdateNexusStatus(); };
        MhoPackageModifier.Gui.SearchBox.AddClear(filter);
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        list.CheckClicked += m => Toggle(m);
        // Several marked (Shift / Ctrl click): all to the clicked card's new state, one undo step.
        list.CheckManyClicked += (mods, on) => Change($"turn {(on ? "on" : "off")} {mods.Count} mods",
            () => { bool any = false; foreach (var m in mods) if (m.Enabled != on) { m.Enabled = on; any = true; } return any; });
        list.DroppedMany += (mods, target, below) =>
        {
            if (lib == null) return;
            var l = lib;
            Change($"move {mods.Count} mods next to \"{target.Name}\"", () => l.MoveGroup(mods, target, below));
        };
        list.MarksChanged += n =>
        {
            if (n > 1) status.Text = $"{n} Mods Selected · Space or a Checkbox Turns Them On or Off · Drag or the Priority Buttons Move Them Together · Esc Clears";
        };
        list.LockClicked += m => ToggleLock(m);
        list.CanLock = m => ReorderView ? lib?.CanLock(m) ?? ModLock.None : ModLock.None;
        list.DoubleClick += (_, _) => { if (Selected is Mod m) EditMod(m); };
        list.GroupClicked += g => { if (!collapsed.Remove(g.Key)) collapsed.Add(g.Key); FillList(Selected?.FolderName); };
        list.TagClicked += t => filter.Text = t.Contains(' ') ? $"tag:\"{t}\"" : $"tag:{t}";
        list.MenuRequested += (m, pt) => Ui.ShowAt(CardMenu(m), pt);
        list.UpdateFor = m => NexusUpdates.UpdateFor(m, nexus);
        list.ConflictText = ConflictSummary;
        // The picture shown big: the user's pick is kept per mod (state.json Previews), one undo step, no reload.
        storePreview.Picked += (m, key) =>
        {
            if (readOnly || lib?.Find(m.FolderName) is not Mod mm || mm.LocalPreview == key) return;
            string before = ReadState();
            mm.LocalPreview = key;
            lib.SaveState();
            undo.Add((before, key == null ? $"use the mod's picture for \"{mm.Name}\"" : $"pick the picture shown for \"{mm.Name}\""));
            redo.Clear();
            UpdateUndo();
            storePreview.ChoiceChanged();
        };
        storePreview.CustomRequested += m =>
        {
            if (readOnly || lib == null) return;
            using var d = new OpenFileDialog { Title = $"Preview Picture for {m.Name}", Filter = ModPictures.DialogFilter };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string key;
            try { key = ModPictures.KeepLocal(lib.DataFolder, m.FolderName, d.FileName, "preview"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (lib.Find(m.FolderName) is Mod mm && mm.LocalPreview == key) mm.LocalPreview = null;   // the same file name again: still a change
            storePreview.RaisePicked(m, key);
        };
        list.CanReorder = () => ReorderView && !readOnly;
        list.Dropped += (m, target, below) =>
        {
            if (lib == null) return;
            int to = target.Priority + (below ? 1 : 0);
            if (m.Priority < to) to--;
            if (to == m.Priority) return;
            var l = lib;
            int before = m.Priority;
            Change($"move \"{m.Name}\" next to \"{target.Name}\"", () => l.MoveTo(m, to) && m.Priority != before);
        };
        header.ConflictClicked += () => { for (int i = 0; i < tabs.Count; i++) if (tabs.TitleAt(i).StartsWith("Conflicts")) { tabs.Select(i); break; } };
        list.UpdateClicked += m => UpdateFromNexus(m);

        // ---- Right: the selected mod
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        right.Controls.Add(tabs); right.Controls.Add(header);
        header.PillClicked += () => { if (Selected is Mod m) Toggle(m); };
        header.CostumeClicked += pt => { if (Selected is Mod m && !readOnly) CostumeMenu(m, pt); };
        list.CostumeLabel = m => SingleCostume(m) is { } sc ? sc.Costume.Title + (sc.Costume.IsDefault ? " (Default)" : "") : null;
        // The "(on …)" a moved costume's name ends with: "(on Storm Modern)" = moved to another hero (amber), "(on Classic)" =
        // moved within its hero (teal); only when it names the costume the mod's package is for.
        Ui.OverrideSuffix = m =>
        {
            if (SingleCostume(m) is not { } c) return null;
            string other = $" (on {c.Costume.Short.Split('/')[0]} {c.Costume.Title})", same = $" (on {c.Costume.Title})";
            return m.Name.EndsWith(other, StringComparison.Ordinal) ? (other, true) : m.Name.EndsWith(same, StringComparison.Ordinal) ? (same, false) : null;
        };
        header.TagsClicked += pt => { if (Selected is Mod m && !readOnly) Ui.ShowAt(TagsMenu(m), pt); };
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
        // The note under the details (Kurt, 2026-09-30: more room for the preview in the middle).
        middle.Controls.Add(storePreview); right.Controls.Add(notesPanel);
        middleAndRight.Controls.Add(right); middleAndRight.Controls.Add(middle);
        // As large as the column's height allows (store images are 300×420), but at most 28% of the space beside the list (Kurt: between the first size and 40%);
        // then the right column gives up a fifth of its width to it (Kurt, 2026-09-30: more room for the 3D view).
        middleAndRight.Resize += (_, _) =>
        {
            float sc = DeviceDpi / 96f;
            notesPanel.Height = (int)(170 * sc);
            int cardH = middleAndRight.Height - (int)(80 * sc);
            int w = (int)(cardH * 300f / 420f) + (int)(12 * sc);
            int fit = Math.Max((int)(200 * sc), Math.Min(w, (int)(middleAndRight.Width * 0.28f)));
            middle.Width = middleAndRight.Width - (int)((middleAndRight.Width - fit) * 0.8f);
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
        var edit = Ui.FlatButton("Edit Mod", () => { if (Selected is Mod m) EditMod(m); });
        var export = Ui.FlatButton("Export to ZIP", ExportMod);
        tagsButton = Ui.FlatButton("Tags  ▾", () => { });
        tagsButton.Click += (_, _) => { if (Selected is Mod m) Ui.ShowUnder(TagsMenu(m), tagsButton); };
        leftButtons.Controls.AddRange([remove, edit, export, tagsButton]);
        writeControls.AddRange([remove, edit, applyButton, tagsButton]);
        tips.SetToolTip(remove, "Send the selected mod to the Recycle Bin: it's turned off and the game gets its files back first. Asks before it does anything (Del).");
        tips.SetToolTip(edit, "Open the selected mod in the Editor tab (or double-click it).");
        tips.SetToolTip(export, "Save the selected mod as a .ZIP to share.");
        tips.SetToolTip(tagsButton, "Add or remove the selected mod's tags, tag every mod in the list, rename or delete tags.");
        Icons.Make(remove, "Remove Mod", Icons.Trash, DeviceDpi / 96f);
        Icons.Make(edit, "Edit Mod", Icons.Pencil, DeviceDpi / 96f);
        Icons.Make(export, "Export to ZIP", Icons.Export, DeviceDpi / 96f);
        Icons.Make(tagsButton, "Tags", Icons.WithMenu(Icons.Tag), DeviceDpi / 96f);
        tips.SetToolTip(applyButton, "Write the mods that are on into the game: each file is built from its verified original, checked, and can be undone.  (Ctrl+Enter)");
        bottom.Controls.Add(leftButtons, 0, 0);
        bottom.Controls.Add(status, 1, 0);
        status.Click += (_, _) => { if (reviewable && !readOnly) Apply(); };
        applyButton.Anchor = AnchorStyles.Right;
        bottom.Controls.Add(applyButton, 2, 0);

        var modsPage = new Panel { Dock = DockStyle.Fill };
        modsPage.Controls.Add(split); modsPage.Controls.Add(bottom);

        // Editor tab: a placeholder until + New Mod or Edit Mod opens one.
        var ph = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        ph.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ph.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        ph.Controls.Add(new Label { Text = "No Mod Open", AutoSize = true, Anchor = AnchorStyles.None, Font = Ui.Bold(14f), Tag = "subtle" }, 0, 1);
        var phButtons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(0, 10, 0, 0) };
        phButtons.Controls.AddRange([Ui.AccentButton("+  New Mod", () => EditMod(null), "Make a new mod from packages, icons, store images, strings or sound packs."),
                                    Ui.FlatButton("Edit the Selected Mod", () => { if (Selected is Mod m) EditMod(m); }, "Open the mod selected on the Mods tab in the editor.")]);
        ph.Controls.Add(phButtons, 0, 2);
        editorPlaceholder = ph;
        editorHost.Controls.Add(ph);

        pages.Add("Mods", modsPage);
        pages.Add("Editor", editorHost);
        pages.Add("Extract", extractHost);
        pages.SelectedChanged += i =>
        {
            if (i == 2) EnsureExtract();
            // The Editor tab with nothing open: open the mod highlighted in the list (Kurt). A mod already open stays as it is.
            if (i == 1 && editor == null && !readOnly && Selected is Mod m) BeginInvoke(() => { if (editor == null) EditMod(m); });
        };
        var pagesWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 2, 6, 0) };
        pagesWrap.Controls.Add(pages);
        Controls.Add(pagesWrap); Controls.Add(top);

        // Drop .zip / .7z / folders on the window to install them.
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = !readOnly && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Install(files); };

        Load += (_, _) =>
        {
            Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
            Restyle(this);
            Reload();
        };
        // After maximizing: the list width the user left it at (a fraction of the window, so it fits any size), else 30%.
        Shown += (_, _) =>
        {
            float fr = settings.ListWidth is float f && f > 0.1f && f < 0.8f ? f : 0.30f;
            split.SplitterDistance = (int)(split.Width * fr);
            split.SplitterMoved += (_, _) => { if (split.Width > 0) { settings.ListWidth = (float)Math.Round((double)split.SplitterDistance / split.Width, 3); settings.Save(); } };
        };
        FormClosing += (_, _) => SaveNote();
        FormClosing += (_, _) => SaveWindowPlace();
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized) wasMaximized = WindowState == FormWindowState.Maximized; };
        // A quiet look for a new version at start and every hour while open (Kurt: frequent releases; Settings: Check for updates at start).
        // The first time: ask whether it may look (code signing policy: nothing goes over the network without consent).
        Shown += (_, _) =>
        {
            if (!settings.UpdateCheckAsked)
            {
                settings.CheckUpdates = Dialog.Show(this, "Look for new versions of MHO Extended Mod Manager when it starts and every hour while it's open?" + Environment.NewLine + Environment.NewLine +
                    "It asks GitHub (api.github.com) for the latest release; nothing about you, your game or your mods is sent. " +
                    "You can change this in Settings, and Settings → Check for updates works either way.",
                    "Updates", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                settings.UpdateCheckAsked = true;
                settings.Save();
            }
            ShowWhatsNew();
            ShowUpdateAlert();   // from the last check (a check under an hour ago isn't repeated)
            if (settings.CheckUpdates && (settings.LastUpdateCheck == null || DateTime.Now - settings.LastUpdateCheck > TimeSpan.FromHours(1)))
                CheckForUpdates(manual: false);
            // Left open: look again every hour (only when checks are allowed; GitHub allows 60 unauthenticated requests an hour).
            var updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
            updateTimer.Tick += (_, _) => { if (settings.CheckUpdates) CheckForUpdates(manual: false); };
            updateTimer.Start();
            // Nexus (public data, no account): only when the user turned it on (off by default), at most every 6 hours.
            if (settings.NexusCheckAtStart && lib?.Mods.Any(m => m.NexusModId != null) == true && (nexus.Checked == null || DateTime.Now - nexus.Checked > TimeSpan.FromHours(6)))
                CheckNexus(manual: false);
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
        UpdateNexusStatus();
        bool running = Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;
        runningLabel.Text = running ? "●  Game Running: Close It to Apply" : "●  Game Not Running";
        runningLabel.ForeColor = running ? Ui.Warn : Ui.Subtle;
    }

    Button updateAlert = null!;

    /// <summary>The top bar's update alert: shown while a release newer than this one is known and not skipped.</summary>
    void ShowUpdateAlert()
    {
        bool show = Version.TryParse(settings.LatestKnownVersion, out var v) && v > Updater.Current && settings.SkipVersion != v.ToString();
        if (show)
        {
            updateAlert.Text = $"↑ Update Available: v{v}";
            updateAlert.Tag = "accent";
            updateAlert.BackColor = Ui.Enabled; updateAlert.ForeColor = Ui.OnColor;
            updateAlert.FlatAppearance.BorderColor = Ui.Enabled;
            updateAlert.FlatAppearance.MouseOverBackColor = Color.FromArgb(96, 210, 130);
            Ui.Tip(updateAlert, $"Version {v} of MHO Extended Mod Manager is out (you have {Program.Version}). Click to see what's new and update; it restarts when done.");
        }
        updateAlert.Visible = show;
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
        settings.LastUpdateCheck = DateTime.Now;
        settings.LatestKnownVersion = r?.Version.ToString();
        settings.Save();
        ShowUpdateAlert();
        if (r == null || r.Version <= Updater.Current)
        {
            if (manual) Dialog.Show(this, $"You have the latest version ({Program.Version}).", "Updates");
            return;
        }
        // At start: the alert in the top bar is enough (Kurt: a prompt to update by hand, not a window in the way).
        if (!manual) { if (settings.SkipVersion != r.Version.ToString()) status.Text = Ui.TitleCase($"Version {r.Version} is available: click Update Available at the top"); return; }
        using var f = new UpdateForm(r);
        f.ShowDialog(this);
        if (f.SkipThis) { settings.SkipVersion = r.Version.ToString(); settings.Save(); ShowUpdateAlert(); }
        if (!f.Installed) return;
        SaveNote();
        Updater.Restart();
        Close();
    }

    /// <summary>Settings → Changelog (Kurt): what's new in each version, from CHANGELOG.txt next to the program.</summary>
    void ShowChangelog()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.txt");
        string text = File.Exists(path) ? File.ReadAllText(path) : "The changelog isn't next to the program (CHANGELOG.txt). See the release notes on GitHub: " + Updater.ReleasesPage;
        Dialog.ShowLog(this, $"Changelog (You Have {Program.Version})", text);
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

    /// <summary>Settings → Clean Game Files Folder: a clean CookedPCConsole copy, accepted when packages in it match the stock
    /// checksums (a sample of 40). Only ever read.</summary>
    void BrowseClean()
    {
        using var d = new FolderBrowserDialog { Description = "A clean copy of the game's CookedPCConsole folder (only read)", UseDescriptionForTitle = true, SelectedPath = settings.CleanGameFiles ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (game == null) { Dialog.Show(this, "Set the game folder first.", "Clean Game Files", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var files = Directory.EnumerateFiles(d.SelectedPath, "*.upk").Select(p => Path.GetFileName(p)!).Where(f => !f.Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
        var sample = files.Where((_, k) => k % Math.Max(1, files.Count / 40) == 0).Take(40).ToList();
        int ok = sample.Count(f => game.MatchesStock(f, Path.Combine(d.SelectedPath, f)));
        if (files.Count < 100 || ok < sample.Count * 9 / 10)
        {
            Dialog.Show(this, $"That folder doesn't look like a clean CookedPCConsole: {files.Count:N0} package(s), {ok} of {sample.Count} checked match the game's own files.", "Not a Clean Copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        settings.CleanGameFiles = d.SelectedPath; settings.Save();
        StockFiles.Init(game, settings.CleanGameFiles, Settings.LibraryData(settings.LibraryPath) is string dd ? Path.Combine(dd, "originals") : null);
        status.Text = Ui.TitleCase($"Clean game files: {d.SelectedPath} ({files.Count:N0} packages, {ok} of {sample.Count} checked are the game's own)");
    }

    /// <summary>Settings → Check Backups (BackupCheck): read only, in a log window.</summary>
    /// <summary>Settings → Changed Game Files / the Apply window's Review Changed Files (GameFilesForm).</summary>
    void ChangedGameFiles()
    {
        if (readOnly || lib == null || game == null) return;
        using var f = new GameFilesForm(lib, game);
        f.ShowDialog(this);
        if (f.Changed) Reload();
    }

    void CheckBackups()
    {
        if (game == null || lib == null) return;
        UseWaitCursor = true;
        try
        {
            var (lines, summary) = BackupCheck.Run(game, new Originals(lib.DataFolder, game), StockFiles.Clean);
            Dialog.ShowLog(this, "Backups", summary + Environment.NewLine + Environment.NewLine + (lines.Count == 0 ? "Every changed game file has a stock copy kept or a stock .bak." : string.Join(Environment.NewLine, lines)));
            // Backups that are the original but dated later: offer the game's date (Kurt: only those, never one that isn't
            // the original).
            var late = BackupCheck.FixDates(game, dryRun: true);
            if (late.Count > 0 && Dialog.Show(this, $"{late.Count} .bak file(s) are the game's original files but carry a later date, so they look modified. Give them the game's own date (2024-03-14)? Only the date changes; their contents stay exactly as they are. Backups that aren't the original keep their dates.",
                    "Fix Backup Dates", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var done = BackupCheck.FixDates(game, dryRun: false);
                status.Text = Ui.TitleCase($"{done.Count} of {late.Count} backup(s) got the game's date");
            }
        }
        finally { UseWaitCursor = false; }
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
        singleCostumes.Clear();
        if (gameRoot != null && Settings.IsGameRoot(gameRoot) && costumesRoot != gameRoot)
        {
            // The game's costume definitions (a second or so, once): which costume each mod is for.
            costumesRoot = gameRoot;
            string root = gameRoot;
            Task.Run(() => Costume.All(root)).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted) return;
                BeginInvoke(() => { costumes = t.Result; singleCostumes.Clear(); list.Invalidate(); if (Selected is Mod sm) { header.CostumeTitle = SingleCostume(sm) is { } hc ? hc.Costume.Title + (hc.Costume.IsDefault ? " (Default)" : "") : null; header.Invalidate(); } });
            });
        }
        gameLabel.Text = gameRoot != null ? Settings.TrueCase(gameRoot) : "(not set: Settings → Change game folder)";
        if (gameRoot != null && Directory.Exists(Settings.Cooked(gameRoot))) game = new GameState(gameRoot, data);
        StockFiles.Init(game, settings.CleanGameFiles, data == null ? null : Path.Combine(data, "originals"));
        // Stock pictures for mods without one: one catalog per library + game folder (its icon package loads once).
        string catKey = data + "|" + gameRoot;
        if (catKey != listCatalogKey) { listCatalogKey = catKey; list.Catalog = storePreview.Catalog = game != null ? new StockCatalog(lib, game) : null; storePreview.CookedFolder = game?.Cooked; }
        winners = lib.PackageWinners();
        var conflicts = lib.Conflicts();
        conflicted = conflicts.SelectMany(c => c.Mods).ToHashSet();
        list.Conflicted = conflicted;
        // Per mod: which mods it overrides and which override it (the highest mod wins each shared change).
        conflictWith = [];
        foreach (var (_, ms) in conflicts)
            for (int a = 0; a < ms.Count; a++)
                for (int b = 0; b < ms.Count; b++)
                    if (a != b)
                    {
                        var d = conflictWith.TryGetValue(ms[a], out var x) ? x : conflictWith[ms[a]] = [];
                        d[ms[b]] = (d.TryGetValue(ms[b], out var c) ? c : 0) + 1;
                    }
        list.Overridden = conflicted.Where(m => conflictWith[m].Keys.Any(o => o.Priority < m.Priority)).ToHashSet();
        countLabel.Text = $"{lib.Mods.Count(m => m.Enabled)} of {lib.Mods.Count} On";
        UpdateNexusStatus();
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

    static readonly (string Key, string Name)[] Sorts = [("priority", "Priority"), ("name", "Name"), ("author", "Author"), ("tag", "Tag"), ("enabled", "On First"), ("update", "Updates First")];
    static readonly (string Key, string Name)[] Groups = [("none", "None"), ("tag", "Tag"), ("author", "Author")];

    /// <summary>The list shows the priority order as it is: the priority buttons and padlocks work only then.</summary>
    bool ReorderView => settings.ListSort == "priority" && settings.ListGroup == "none";

    static MainForm? current;   // for the static filter (is:update reads the Nexus cache)

    /// <summary>One search word: plain text (name, author, tags), tag:x / #x (tags only), or is:on / off / locked / untagged.</summary>
    static bool Matches(Mod m, string t)
    {
        if (t.StartsWith("is:", StringComparison.OrdinalIgnoreCase))
            return t[3..].ToLowerInvariant() switch { "on" => m.Enabled, "off" => !m.Enabled, "locked" => m.Lock != ModLock.None, "untagged" => m.Tags.Count == 0,
                "update" => current?.nexus is NexusCache c && NexusUpdates.UpdateFor(m, c) != null, "nexus" => m.NexusModId != null,
                "conflict" => current?.conflicted.Contains(m) == true, _ => true };
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
            // Nexus updates first, then the other linked mods, then the rest; each in priority order.
            "update" => mods.OrderByDescending(m => NexusUpdates.UpdateFor(m, nexus) != null).ThenByDescending(m => m.NexusModId != null).ThenBy(m => m.Priority),
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
        return lastMenu = new() { Font = Ui.Regular(9.5f) };
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
        tips.SetToolTip(undoButton, Ui.Titled("Undo", undo.Count > 0 ? $"{undo[^1].Label}  (Ctrl+Z)" : "Nothing to undo. Turning mods on or off, moving, locking and tags can be undone."));
        tips.SetToolTip(redoButton, Ui.Titled("Redo", redo.Count > 0 ? $"{redo[^1].Label}  (Ctrl+Y)" : "Nothing to redo."));
    }

    /// <summary>The control with the keyboard focus, inside nested containers (a text box in the editor, the note box …).</summary>
    Control? FocusedLeaf()
    {
        Control? a = ActiveControl;
        while (a is ContainerControl cc && cc.ActiveControl != null) a = cc.ActiveControl;
        return a;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // F1: the manual, at the section for the tab shown.
        if (keyData == Keys.F1) { HelpForm.Show(this, settings, pages.SelectedIndex switch { 1 => "editor", 2 => "extract", _ => "contents" }); return true; }
        // F11: the 3D preview full screen (its window handles F11 / Esc to come back).
        if (keyData == Keys.F11 && pages.SelectedIndex == 0) { storePreview.ToggleFull(); return true; }
        // Ctrl+Enter: Apply Changes (Kurt), anywhere on the Mods tab.
        if (keyData == (Keys.Control | Keys.Enter) && pages.SelectedIndex == 0 && applyButton.Enabled && applyButton.Visible)
        {
            Apply();
            return true;
        }
        if (pages.SelectedIndex == 0 && FocusedLeaf() is not TextBoxBase)
        {
            // Esc: pauses the 3D preview's animation while one plays (Kurt); otherwise Esc does what it did.
            if (keyData == Keys.Escape && storePreview.PausePlayback()) return true;
            // P: play / pause the 3D preview (Kurt, 2026-10-04: one key in every 3D view)
            if (keyData == Keys.P && storePreview.TogglePlayback()) return true;
            // Del: Remove Mod (Kurt), which asks first as the button does.
            if (keyData == Keys.Delete && Selected is Mod && !readOnly) { RemoveMod(); return true; }
            // Ctrl+Up / Down: priority one step; Ctrl+Home / End: to the top / bottom (a marked group moves together).
            if (keyData == (Keys.Control | Keys.Up)) { MoveSelected(-1); return true; }
            if (keyData == (Keys.Control | Keys.Down)) { MoveSelected(1); return true; }
            if (keyData == (Keys.Control | Keys.Home)) { MoveSelected(-1, toEnd: true); return true; }
            if (keyData == (Keys.Control | Keys.End)) { MoveSelected(1, toEnd: true); return true; }
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Create Post for an installed mod: the post saved in it, kept in its Post\ folder (no reload needed).</summary>
    void CreatePost(Mod m)
    {
        using var f = new PostForm(PostWriter.From(m), m.Folder, ModPost.Read(m.Folder), (n, d, imgs) => ModPost.Write(m.Folder, n, d, imgs));
        f.ShowDialog(this);
    }

    // ---- Note (under the store image)

    void ShowNote(Mod? m)
    {
        noteMod = m;
        noteBox.Text = (m?.Note ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
        noteBox.ReadOnly = readOnly || m == null;
        noteSource.Text = m == null ? "" : m.LocalNote != null ? (m.Manifest.Notes != null ? "User (Replaces the Mod's)" : "User") : m.Manifest.Notes != null ? "From the Mod" : "None Yet: Type to Add One";
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
        menu.Items.Insert(at++, new ToolStripMenuItem("Edit", null, (_, _) => EditMod(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Export to ZIP", null, (_, _) => ExportMod()));
        menu.Items.Insert(at++, new ToolStripMenuItem("Update from a File", null, (_, _) => UpdateFromFile(m)) { Enabled = !readOnly });
        menu.Items.Insert(at++, new ToolStripMenuItem("Create Post", null, (_, _) => CreatePost(m)));
        menu.Items.Insert(at++, NexusMenu(m));
        menu.Items.Insert(at++, CardPictureMenu(m));
        if (SingleCostume(m) != null)
        {
            var move = new ToolStripMenuItem("Move to Another Costume") { Enabled = !readOnly };
            FillCostumeItems(move.DropDownItems, m);
            menu.Items.Insert(at++, move);
        }
        menu.Items.Insert(at, new ToolStripSeparator());
        return menu;
    }

    /// <summary>
    /// Right-click → Card Picture (Kurt): which of the mod's own images its card in the list shows, each with a small
    /// thumbnail; Automatic = the usual pick (hero portrait, costume icon, store image …). One undo step.
    /// </summary>
    ToolStripMenuItem CardPictureMenu(Mod m)
    {
        var item = new ToolStripMenuItem("Card Picture") { Enabled = !readOnly };
        string autoText = m.Manifest.CardPicture != null ? $"The Mod's Choice ({m.Manifest.CardPicture})" : "Automatic (Hero Portrait, Costume Icon, Store Image)";
        var auto = new ToolStripMenuItem(autoText, null, (_, _) =>
            Change($"Card picture of \"{m.Name}\": the mod's choice", () => { if (m.LocalCard == null) return false; m.LocalCard = null; return true; })) { Checked = m.LocalCard == null };
        item.DropDownItems.Add(auto);
        var candidates = m.CardCandidates();
        // A picture of your own (a user's request): copied into data\pictures, kept on this PC; Export can put it into the mod.
        var own = new ToolStripMenuItem("Custom Image", null, (_, _) =>
        {
            using var d = new OpenFileDialog { Title = $"Card Picture for {m.Name}", Filter = ModPictures.DialogFilter };
            if (d.ShowDialog(this) != DialogResult.OK || lib == null) return;
            string key;
            try { key = ModPictures.KeepLocal(lib.DataFolder, m.FolderName, d.FileName, "card"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            Change($"Card picture of \"{m.Name}\": {Path.GetFileName(d.FileName)}", () => { m.LocalCard = key; return true; });
        })
        { Checked = ModPictures.IsFile(m.LocalCard) && !candidates.Any(c => c.Texture.Equals(m.LocalCard, StringComparison.OrdinalIgnoreCase)) };
        if (ModPictures.Resolve(m.Folder, m.LocalCard) is string ownFile && own.Checked) try { own.Image = Ui.DdsThumb(ownFile, (int)(32 * DeviceDpi / 96f)); own.ImageScaling = ToolStripItemImageScaling.None; } catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { }
        item.DropDownItems.Add(own);
        if (candidates.Count == 0) return item;
        item.DropDownItems.Add(new ToolStripSeparator());
        int px = (int)(32 * DeviceDpi / 96f);
        string? current = m.LocalCard;
        foreach (var (tex, file) in candidates)
        {
            string t = tex;
            var pick = new ToolStripMenuItem(ModPictures.IsFile(tex) ? ModPictures.Label(tex) + "  ·  the Mod's Own Picture" : tex, null, (_, _) =>
                Change($"Card picture of \"{m.Name}\": {t}", () => { if (t.Equals(m.LocalCard, StringComparison.OrdinalIgnoreCase)) return false; m.LocalCard = t; return true; }))
            { Checked = tex.Equals(current, StringComparison.OrdinalIgnoreCase), ImageScaling = ToolStripItemImageScaling.None };
            try { if (Ui.DdsThumb(file, px) is Image img) pick.Image = img; } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OutOfMemoryException) { }
            item.DropDownItems.Add(pick);
        }
        return item;
    }

    // ---- Move to Another Costume

    List<Costume>? costumes;
    string? costumesRoot;
    readonly Dictionary<string, (string File, Costume Costume)?> singleCostumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The one costume a mod is for (null until the game's costume data is read, or for other mods).</summary>
    (string File, Costume Costume)? SingleCostume(Mod m)
    {
        if (costumes == null) return null;
        if (!singleCostumes.TryGetValue(m.FolderName, out var c)) singleCostumes[m.FolderName] = c = CostumeMove.Single(m, costumes, allowBase: true);
        return c;
    }

    /// <summary>The header's costume drop-down: the hero's other costumes.</summary>
    void CostumeMenu(Mod m, Point pt)
    {
        var menu = NewMenu();
        FillCostumeItems(menu.Items, m);
        Ui.ShowAt(menu, pt);
    }

    /// <summary>The hero's other costumes with their store images; one already made from this mod selects that mod.</summary>
    void FillCostumeItems(ToolStripItemCollection items, Mod m)
    {
        if (SingleCostume(m) is not { } src || lib == null || game == null || costumes == null) return;
        var catalog = list.Catalog;
        items.Add(new ToolStripMenuItem($"\"{m.Name}\" Is for {src.Costume.Title}. Move It To:") { Enabled = false });
        // A costume in the hero's main package (Star-Lord Infinity War) can't be renamed onto another costume: its model is
        // copied into the target's own package instead (as for another hero).
        bool copy = CostumeMove.IsBase(src.Costume);
        foreach (var target in CostumeMove.Targets(src.Costume, costumes, game.Cooked))
        {
            var made = lib.Mods.FirstOrDefault(x => x.Name.Equals(copy ? CrossMove.NewName(m, target) : CostumeMove.NewName(m, target), StringComparison.OrdinalIgnoreCase));
            var others = lib.Mods.Where(x => x != m && x != made && x.Manifest.UpkReplacements.Contains(target.Package, StringComparer.OrdinalIgnoreCase)).ToList();
            string text = target.Title + (target.IsDefault ? "  ·  Default" : "") + (made != null ? "  ·  Already Made (Select It)" : others.Count > 0 ? $"  ·  {others.Count} Other Mod(s)" : "");
            var item = new ToolStripMenuItem(text, null, (_, _) => { if (made != null) SelectMod(made.FolderName); else if (copy) CopyMove(m, src.File, src.Costume, target); else MoveCostume(m, src.File, src.Costume, target); });
            if (target.IsDefault) item.Tag = Ui.Enabled;
            if (MoveCostumeForm.Image(target, catalog) is Bitmap b) { item.Image = b; item.ImageScaling = ToolStripItemImageScaling.None; item.Image = new Bitmap(b, new Size((int)(24 * DeviceDpi / 96f), (int)(34 * DeviceDpi / 96f))); b.Dispose(); }
            items.Add(item);
        }
        // Another hero (Kurt): a picker of heroes, then their costumes.
        items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("Another Hero", null, (_, _) => PickOtherHero(m, src.File, src.Costume));
        other.ToolTipText = "Move the model and voice onto a costume of a different hero (that hero's animations and powers stay).";
        items.Add(other);
        // The hero's default costume when it lives in the hero's main package (Thor Modern): shown, but it can't be a target.
        var def = costumes.FirstOrDefault(c => c.IsDefault && c.Hero == src.Costume.Hero && CostumeMove.IsBase(c));
        if (def != null && !def.Class.Equals(src.Costume.Class, StringComparison.OrdinalIgnoreCase))
            items.Add(new ToolStripMenuItem($"{def.Title}  ·  Default (Can't Move Here: Its Package Holds the Hero's Animations)") { Enabled = false, Tag = Ui.Enabled });
    }

    /// <summary>Another Hero: the hero / costume picker, then the Move window and CrossMove.CreateMod.</summary>
    async void PickOtherHero(Mod m, string file, Costume source)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        Costume target;
        using (var pick = new HeroPickerForm(m, source, all, g.Cooked, catalog))
        {
            if (pick.ShowDialog(this) != DialogResult.OK || pick.Chosen == null) return;
            target = pick.Chosen;
        }
        CopyMove(m, file, source, target);
    }

    /// <summary>The copy route (CrossMove): the target costume's stock package with the mod's model copied in. For another hero,
    /// and for a costume in the hero's main package (Star-Lord Infinity War) moving to one of the hero's own costumes.</summary>
    async void CopyMove(Mod m, string file, Costume source, Costume target)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        var made0 = l.Mods.FirstOrDefault(x => x.Name.Equals(CrossMove.NewName(m, target), StringComparison.OrdinalIgnoreCase));
        if (made0 != null) { SelectMod(made0.FolderName); status.Text = Ui.TitleCase($"\"{made0.Name}\" is made already"); return; }
        UseWaitCursor = true;
        CostumeMove.Plan plan;
        try { plan = await Task.Run(() => CostumeMove.Make(m, file, source, target, all, g.Cooked, catalog)); }
        finally { UseWaitCursor = false; }
        var onTarget = l.Mods.Where(x => x.Manifest.UpkReplacements.Contains(target.Package, StringComparer.OrdinalIgnoreCase)).ToList();
        bool swap;
        using (var f = new MoveCostumeForm(m, plan, catalog, onTarget, crossHero: true))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            swap = f.SwapOn;
        }
        UseWaitCursor = true;
        status.Text = Ui.TitleCase($"Moving \"{m.Name}\" to {target.Title}…");
        string? error = null, made;
        var log = new List<string>();
        try { made = await Task.Run(() => CrossMove.CreateMod(l, m, file, source, target, all, g, catalog, log, out error)); }
        finally { UseWaitCursor = false; }
        if (made == null) { Dialog.Show(this, error ?? "Unknown error.", "Not Moved", MessageBoxButtons.OK, MessageBoxIcon.Error); Reload(); return; }
        if (log.Where(x => x.StartsWith("resized ")).Select(x => x[8..]).ToList() is { Count: > 0 } resizedImages)
            Dialog.Show(this, "These images weren't the target's size and were resized for the move:\n\n" + string.Join("\n", resizedImages), "Moved, Images Resized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
        if (swap && lib?.Mods.FirstOrDefault(x => x.FolderName == made) is Mod nm && lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod om)
            Change($"turn on \"{nm.Name}\" and off \"{om.Name}\"", () => { nm.Enabled = true; om.Enabled = false; return true; });
        SelectMod(made);
        status.Text = Ui.TitleCase($"Made \"{CrossMove.NewName(m, target)}\"") + (swap ? "  ·  Apply Changes to Put It in the Game" : "");
    }

    /// <summary>The plan in the Move window, then the new mod (CostumeMove.CreateMod); optionally on, with the original off.</summary>
    async void MoveCostume(Mod m, string file, Costume source, Costume target)
    {
        if (readOnly || lib == null || game == null || costumes == null) return;
        var (l, g, all) = (lib, game, costumes);
        var catalog = list.Catalog;
        UseWaitCursor = true;
        CostumeMove.Plan plan;
        try { plan = await Task.Run(() => CostumeMove.Make(m, file, source, target, all, g.Cooked, catalog)); }
        finally { UseWaitCursor = false; }
        var onTarget = l.Mods.Where(x => x.Manifest.UpkReplacements.Any(f => plan.Packages.Any(p => p.TargetFile.Equals(f, StringComparison.OrdinalIgnoreCase)))).ToList();
        bool swap;
        using (var f = new MoveCostumeForm(m, plan, catalog, onTarget))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            swap = f.SwapOn;
        }
        UseWaitCursor = true;
        string? error = null, made;
        var resized = new List<string>();
        try { made = await Task.Run(() => CostumeMove.CreateMod(l, m, plan, new Originals(l.DataFolder, g), out error, null, catalog, resized)); }
        finally { UseWaitCursor = false; }
        if (made == null) { Dialog.Show(this, error ?? "Unknown error.", "Not Moved", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (resized.Count > 0) Dialog.Show(this, "These images weren't the target's size and were resized for the move:\n\n" + string.Join("\n", resized), "Moved, Images Resized", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Reload();
        if (swap && lib?.Mods.FirstOrDefault(x => x.FolderName == made) is Mod nm && lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod om)
            Change($"turn on \"{nm.Name}\" and off \"{om.Name}\"", () => { nm.Enabled = true; om.Enabled = false; return true; });
        SelectMod(made);
        status.Text = Ui.TitleCase($"Made \"{CostumeMove.NewName(m, target)}\"") + (swap ? "  ·  Apply Changes to Put It in the Game" : "");
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
        add.DropDownItems.Add("New Tag", null, (_, _) =>
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
            addAll.DropDownItems.Add("New Tag", null, (_, _) =>
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
            var rename = new ToolStripMenuItem("Rename a User Tag");
            var delete = new ToolStripMenuItem("Delete a User Tag");
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
            int n = t.Result.Steps.Count, skipped = t.Result.Problems.Count;
            applyButton.Text = n == 0 ? "Apply Changes" : $"Apply Changes ({n})";
            status.Text = baseText + Ui.TitleCase((n == 0 ? "  ·  the game matches your list" : $"  ·  {n} file(s) to change") +
                          (skipped > 0 ? $"  ·  {skipped} skipped" : "") + (n + skipped > 0 ? "  ·  click for details" : ""));
            status.ForeColor = n == 0 ? Ui.Subtle : Ui.Text;
            // A user saw "7 Can't Be (See Apply)" and didn't know what it meant or whether Apply was safe: the line now
            // says it plainly and opens the plan (the Apply window asks before it writes anything).
            reviewable = n + skipped > 0;
            status.Cursor = reviewable ? Cursors.Hand : Cursors.Default;
            tips.SetToolTip(status, !reviewable ? "" :
                (n > 0 ? $"{n} game file(s) don't match your mod list yet. " : "") +
                (skipped > 0 ? $"{skipped} item(s) are skipped: files that can't be changed stay exactly as they are, and sound-pack lines that can't be added are left out (the rest of the pack still works). " : "") +
                "Click to see the list: the Apply window shows every file first, and nothing is written until you press Apply there.");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Toggle(Mod m) => Change($"turn {(m.Enabled ? "off" : "on")} \"{m.Name}\"", () => { m.Enabled = !m.Enabled; return true; });

    void MoveSelected(int delta, bool toEnd = false)
    {
        if (readOnly || lib == null || Selected is not Mod m || !ReorderView) return;
        // A marked group moves together (a user: arrow / fast-move the group).
        if (list.MarkedMods is { Count: > 1 } group)
        {
            var lg = lib;
            Change($"move {group.Count} mods {(toEnd ? (delta < 0 ? "to the top" : "to the bottom") : delta < 0 ? "up" : "down")}",
                () => toEnd ? lg.MoveGroupToEnd(group, delta) : lg.MoveGroupBy(group, delta));
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
        header.Mod = m; header.Conflicted = conflicted.Contains(m); header.CostumeTitle = SingleCostume(m) is { } hc ? hc.Costume.Title + (hc.Costume.IsDefault ? " (Default)" : "") : null; header.Invalidate();
        list.Partners = conflictWith.TryGetValue(m, out var partners) ? partners.Keys.ToHashSet() : [];
        list.Invalidate();
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
            // First one row per other mod (a user: show which mods it conflicts with), then every shared change.
            foreach (var (other, n) in conflictWith[m].OrderBy(kv => kv.Key.Priority))
            {
                bool wins = m.Priority < other.Priority;
                int i = grid.Rows.Add($"\"{other.Name}\"  ·  {n} shared change(s)", wins ? "This mod wins (it's higher in the list)" : "That mod wins (it's higher in the list)");
                grid.Rows[i].DefaultCellStyle.Font = Ui.Bold(9.5f);
                grid.Rows[i].Cells[1].Style.ForeColor = wins ? Ui.Enabled : Ui.Warn;
            }
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
        InfoRow("Automatic Tags", m.AutoTags.Count > 0 ? string.Join(", ", m.AutoTags) : "None (Nothing Recognized in the Content)");
        InfoRow("The Mod's Tags", m.ModTags.Count > 0 ? string.Join(", ", m.ModTags) : "None (Set Them in Edit Mod → Tags)");
        InfoRow("User Tags", m.UserTags.Count > 0 ? string.Join(", ", m.UserTags) : "None (Right-Click the Mod, or + Tag Above)");
        if (m.HiddenTags.Count > 0) InfoRow("Hidden Here", string.Join(", ", m.HiddenTags));
        if (m.NexusModId is int nid)
        {
            string latest = NexusUpdates.LatestFor(m, nexus) is { } lf
                ? $"  ·  Nexus Has v{lf.Version.TrimStart('v', 'V')} (Uploaded {DateTimeOffset.FromUnixTimeSeconds(lf.Uploaded).LocalDateTime:yyyy-MM-dd}){(m.NexusLink?.Ignore == lf.FileId ? ", Ignored" : "")}" : "";
            string how = m.NexusLink is { FromNexus: false, FileId: null } ? "  ·  Linked by Name (Update = a Higher Version Uploaded After Your Copy Was Made)" : "";
            string upd = NexusUpdates.UpdateFor(m, nexus) is string nv ? "  ·  Update Available" : latest.Length > 0 ? "  ·  Up to Date" : "";
            InfoRow("Nexus", $"{Nexus.SiteMods}{nid}  ·  Installed v{(m.NexusLink?.Version ?? m.Manifest.Version ?? "?").TrimStart('v', 'V')}{latest}{upd}{how}", upd.Contains("Update") ? Ui.Enabled : null);
            if (nexus.Mods.TryGetValue(nid, out var pageInfo) && Nexus.Lines(pageInfo).Count > 1)
                InfoRow("Nexus File", NexusUpdates.LineFor(m, nexus) is string line ? $"\"{line}\" (Updates Come Only From This File)" : "Not Known Yet: the Page Has Several Files (Right-Click → Nexus → Choose the Nexus File)",
                    NexusUpdates.LineFor(m, nexus) == null ? Ui.Warn : null);
        }
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
        var (plan, check) = await Task.Run(() => { var p = Applier.MakePlan(l, g, originals); return (p, ApplyCheck.Run(l, g, originals, p)); });
        UseWaitCursor = false;
        // The checks first (Kurt: anything unexpected, the clean folder, how to get a clean copy from Steam), then the plan.
        string planText = ApplyCheck.Text(check, g) + CaptureOutput(() => Applier.Print(plan));
        // One window (Kurt): the plan and the question, then "Success" or what went wrong.
        using var f = new ApplyForm(planText, plan.Steps.Count == 0 ? null : () => Task.Run(() =>
        {
            bool ok = false;
            string log = CaptureOutput(() => ok = Applier.Execute(plan, g, originals, l.DataFolder));
            return (ok, ok ? log : log + "\n\n" + ApplyCheck.Text(check, g).Replace("\nPLAN", "").TrimEnd());
        }), check.Any, review: check.Unexpected.Count > 0);
        f.ShowDialog(this);
        if (f.DialogResult == DialogResult.Retry) { ChangedGameFiles(); Apply(); return; }
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

    /// <summary>A card's "Update from a File": the chosen archive / folder replaces this mod (even under another name).</summary>
    void UpdateFromFile(Mod m)
    {
        using var d = new OpenFileDialog { Title = $"Update \"{m.Name}\" from", Filter = "Mod archives (*.zip;*.7z;*.rar)|*.zip;*.7z;*.rar|All files|*.*" };
        if (d.ShowDialog(this) == DialogResult.OK) Install([d.FileName], m);
    }

    void Install(string[] sources, Mod? into = null) => _ = InstallAsync(sources, into);

    /// <summary>Installs (or with <paramref name="into"/> updates) mods; Nexus downloads are linked to their page. Returns the installed folders.</summary>
    List<string> lastInstallLog = [];

    async Task<List<string>> InstallAsync(string[] sources, Mod? into = null, bool ask = true, bool showLog = true)
    {
        if (readOnly || lib == null) return [];
        var l = lib;
        UseWaitCursor = true;
        var (log, installed) = await Task.Run(() =>
        {
            var log = new List<string>(); var all = new List<string>();
            foreach (string s in sources)
            {
                log.Add(Path.GetFileName(s) + ":");
                try
                {
                    var lib2 = ModLibrary.Load(l.DataFolder);
                    var done = ModInstaller.Install(s, lib2, log, ask ? AskReplace : (_, _) => true, into);
                    all.AddRange(done);
                    if (done.Count > 0) NexusUpdates.Link(ModLibrary.Load(l.DataFolder), s, done, nexus, online: true).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { log.Add("  couldn't read it: " + ex.Message); }
            }
            return (log, all);
        });
        UseWaitCursor = false;
        lastInstallLog = log;
        filter.Text = "";
        Reload();
        if (installed.Count > 0) SelectMod(installed[0]);
        // Like Apply's result: a colored heading, then what happened.
        if (showLog)
            ShowLog(installed.Count > 0 ? "Installed" : "Nothing Installed",
                string.Join("\n", log) + (installed.Count > 0 ? "\n\nNew mods are added at the top of the list, turned off: tick one, then Apply Changes." : ""),
                installed.Count > 0 ? Dialog.Tone.Good : Dialog.Tone.Bad);
        return installed;
    }

    // ---- Nexus

    /// <summary>Fetches every linked mod's Nexus info; the list then shows ↑ on mods with a newer version.</summary>
    async void CheckNexus(bool manual)
    {
        if (lib == null) return;
        int linked = lib.Mods.Count(m => m.NexusModId != null);
        if (linked == 0) { if (manual) Dialog.Show(this, "No mod is linked to a Nexus page yet. Mods installed from a Nexus download are linked by their file name; for others, right-click → Nexus → Link to Nexus Page.", "Check Mods on Nexus"); return; }
        var l = lib;
        nexusBusy = $"Checking {linked} Linked Mod(s) on Nexus…"; UpdateNexusStatus();
        int n; List<string> problems;
        try { (n, problems) = await Task.Run(() => NexusUpdates.Check(l, nexus)); }
        finally { nexusBusy = null; UpdateNexusStatus(); }
        try { nexus.Save(Settings.Home); } catch (IOException) { }
        int updates = l.Mods.Count(m => NexusUpdates.UpdateFor(m, nexus) != null);
        note = updates > 0 ? $"{updates} Mod Update(s) on Nexus" : $"Checked {n} Mod(s) on Nexus: Up to Date";
        Reload();
        if (manual) Dialog.Show(this, (updates > 0 ? $"{updates} mod(s) have a newer version on Nexus (marked ↑ in the list; filter is:update)." : $"All {n} linked mod(s) are up to date.") +
            (problems.Count > 0 ? "\n\nNot checked: " + string.Join("; ", problems) : ""), "Check Mods on Nexus", MessageBoxButtons.OK, problems.Count > 0 && n == 0 ? MessageBoxIcon.Error : MessageBoxIcon.None);
    }

    /// <summary>The Nexus strip's state: connected?, anything linked?, checked when?, updates?</summary>
    void UpdateNexusStatus()
    {
        if (lib == null) { nexusStatus.Set(NexusStatus.State.NotConnected, "Nexus", "No mod library yet"); return; }
        int linked = lib.Mods.Count(m => m.NexusModId != null);
        int updates = lib.Mods.Count(m => NexusUpdates.UpdateFor(m, nexus) != null);
        string linkedText = $"{linked} of {lib.Mods.Count} Linked" + (NexusAuth.Available && NexusAuth.Load(Settings.Home) is { } signed ? $"  ·  {signed.UserName}{(signed.Premium ? " (Premium)" : "")}" : "");
        NexusStatus.State s; string one, two, tip;
        if (nexusBusy != null) { s = NexusStatus.State.Busy; one = nexusBusy; two = linkedText; tip = "Working with Nexus…"; }
        else if (linked == 0)
        {
            s = NexusStatus.State.NothingLinked; one = "Nexus: No Mods Linked Yet"; two = "Click to Find Your Mods on Nexus";
            tip = "Only linked mods can be checked for updates. Click to look up your mods on Nexus and link the right ones.";
        }
        else if (nexus.Checked is not DateTime at)
        {
            s = NexusStatus.State.NotChecked; one = "Nexus: Not Checked Yet"; two = linkedText + "  ·  Click to Check";
            tip = "Click to ask Nexus whether your linked mods have newer versions.";
        }
        else if (updates > 0)
        {
            s = NexusStatus.State.Updates; one = $"{updates} Update(s) on Nexus  ·  {(IsUpdateFilter ? "Show All" : "Show")}"; two = $"Checked {NexusStatus.Ago(at)}  ·  {linkedText}";
            tip = IsUpdateFilter ? "Click to show every mod again." : "Click to show only the mods with an update (filter is:update). Click a mod's green Update mark to update it.";
        }
        else if (DateTime.Now - at > TimeSpan.FromDays(1))
        {
            s = NexusStatus.State.Stale; one = $"Nexus: Last Checked {NexusStatus.Ago(at)}"; two = linkedText + "  ·  Click to Check";
            tip = "The last check is over a day old. Click to check again.";
        }
        else
        {
            s = NexusStatus.State.UpToDate; one = "Nexus: Up to Date"; two = $"Checked {NexusStatus.Ago(at)}  ·  {linkedText}";
            tip = "Every linked mod had its newest version at the last check. Click to check again.";
        }
        nexusStatus.Set(s, one, two);
        if (tips.GetToolTip(nexusStatus) != tip) tips.SetToolTip(nexusStatus, tip);
    }

    bool IsUpdateFilter => filter.Text.Trim().Equals("is:update", StringComparison.OrdinalIgnoreCase);

    /// <summary>A click on the Nexus status: its next step (connect, find, check, show the updates).</summary>
    void NexusStatusClicked()
    {
        switch (nexusStatus.Current)
        {
            case NexusStatus.State.Busy: return;
            case NexusStatus.State.NothingLinked: FindOnNexus(null); return;
            case NexusStatus.State.Updates: filter.Text = IsUpdateFilter ? "" : "is:update"; return;
            default: CheckNexus(manual: true); return;
        }
    }

    Button[] nexusFolded = [];
    readonly Dictionary<Button, Action> nexusActions = [];

    /// <summary>Shows as many of the Nexus strip's buttons as fit beside a status at least 230 px (scaled) wide; the rest are in ▾.</summary>
    void FitNexusRow(Control row, Control buttons)
    {
        float s = DeviceDpi / 96f;
        int room = row.ClientSize.Width - (int)(230 * s);
        static int W(Control c) => (c is Button { AutoSize: false } ? c.Width : c.GetPreferredSize(Size.Empty).Width) + c.Margin.Horizontal;   // (icon buttons: their own size)
        int always = buttons.Controls.Cast<Control>().Where(c => !nexusFolded.Contains(c)).Sum(W) + buttons.Margin.Horizontal;
        int used = always;
        // Keep from the most useful (Check, Find, Browse); fold from the other end.
        foreach (var b in nexusFolded.Reverse())
        {
            int w = W(b);
            bool fits = used + w <= room;
            if (fits) used += w;
            if (b.Visible != fits) b.Visible = fits;
        }
    }

    ContextMenuStrip NexusBarMenu()
    {
        var menu = NewMenu();
        // Buttons folded away on a narrow list are here instead.
        var folded = nexusFolded.Where(b => !b.Visible).ToList();
        foreach (var b in folded.AsEnumerable().Reverse()) { var act = nexusActions[b]; menu.Items.Add(string.IsNullOrEmpty(b.Text) ? b.AccessibleName : b.Text, null, (_, _) => act()); }
        if (folded.Count > 0) menu.Items.Add(new ToolStripSeparator());
        if (NexusAuth.Available)
        {
            if (NexusAuth.Load(Settings.Home) is { } who)
            {
                menu.Items.Add(new ToolStripMenuItem($"Signed In as {who.UserName} ({(who.Premium ? "Premium" : "Free Account")})") { Enabled = false });
                menu.Items.Add("Sign Out of Nexus", null, (_, _) => SignOutNexus());
            }
            else menu.Items.Add("Sign In with Nexus (One-Click Updates for Premium)", null, (_, _) => SignInNexus());
            menu.Items.Add(new ToolStripSeparator());
        }
        var atStart = new ToolStripMenuItem("Check for Updates at Start") { Checked = settings.NexusCheckAtStart };
        atStart.Click += (_, _) => { settings.NexusCheckAtStart = !settings.NexusCheckAtStart; settings.Save(); };
        menu.Items.Add(atStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Mods With Updates", null, (_, _) => filter.Text = "is:update");
        menu.Items.Add("Show Linked Mods", null, (_, _) => filter.Text = "is:nexus");
        menu.Items.Add("Sort: Updates First", null, (_, _) => { settings.ListSort = "update"; settings.Save(); FillList(Selected?.FolderName); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Marvel Heroes Omega Mods on Nexus", null, (_, _) => Process.Start(new ProcessStartInfo(Nexus.SiteMods) { UseShellExecute = true }));
        return menu;
    }

    /// <summary>"Overrides A, B · overridden by C" for a conflicting mod (list tooltips).</summary>
    string? ConflictSummary(Mod m)
    {
        if (!conflictWith.TryGetValue(m, out var d)) return null;
        var wins = d.Keys.Where(o => o.Priority > m.Priority).OrderBy(o => o.Priority).Select(o => $"\"{o.Name}\"").ToList();
        var loses = d.Keys.Where(o => o.Priority < m.Priority).OrderBy(o => o.Priority).Select(o => $"\"{o.Name}\"").ToList();
        return string.Join("\n", new[] { loses.Count > 0 ? "Overridden in part by " + string.Join(", ", loses) + " (higher in the list)" : null,
                                         wins.Count > 0 ? "Overrides " + string.Join(", ", wins) + " where they change the same thing" : null }.Where(x => x != null));
    }

    ToolStripMenuItem NexusMenu(Mod m)
    {
        var sub = new ToolStripMenuItem("Nexus");
        if (m.NexusModId is int id)
        {
            sub.DropDownItems.Add("Open the Nexus Page", null, (_, _) => Process.Start(new ProcessStartInfo(Nexus.SiteMods + id) { UseShellExecute = true }));
            if (NexusUpdates.UpdateFor(m, nexus) is string v)
            {
                sub.DropDownItems.Add($"Update to v{v.TrimStart('v', 'V')}", null, (_, _) => UpdateFromNexus(m));
                if (NexusAuth.Available && NexusAuth.Load(Settings.Home)?.Premium == true)
                    sub.DropDownItems.Add("Download From the Files Page Instead", null, (_, _) => UpdateFromNexus(m, manual: true));
                if (NexusUpdates.LatestFor(m, nexus) is { } lf) sub.DropDownItems.Add("Ignore This Update", null, (_, _) => IgnoreUpdate(m, lf.FileId));
            }
            if (nexus.Mods.TryGetValue(id, out var pi) && Nexus.Lines(pi).Count > 1)
                sub.DropDownItems.Add("Choose the Nexus File", null, (_, _) => ChooseNexusFile(m));
            sub.DropDownItems.Add("Check for an Update Now", null, (_, _) => CheckNexus(manual: true));
            sub.DropDownItems.Add("Change the Nexus Link", null, (_, _) => LinkToNexus(m));
            if (m.NexusLink != null) sub.DropDownItems.Add("Unlink", null, (_, _) => { if (lib != null) { m.NexusLink = null; lib.SaveState(); Reload(); } });
        }
        else
        {
            sub.DropDownItems.Add("Find on Nexus", null, (_, _) => FindOnNexus(m));
            sub.DropDownItems.Add("Link to Nexus Page", null, (_, _) => LinkToNexus(m));
        }
        sub.Enabled = !readOnly;
        return sub;
    }

    /// <summary>
    /// Find My Mods on Nexus: the game's mod list from Nexus (public, no key), matched against the unlinked mods (or one
    /// mod); the user ticks the right pages and they're linked.
    /// </summary>
    async void FindOnNexus(Mod? only)
    {
        if (readOnly || lib == null) return;
        var targets = only != null ? [only] : lib.Mods.Where(m => m.NexusModId == null).ToList();
        if (targets.Count == 0) { Dialog.Show(this, "Every mod is linked to a Nexus page already.", "Find My Mods on Nexus"); return; }
        List<NexusMatch.NexusMod> all;
        try
        {
            UseWaitCursor = true;
            nexusBusy = "Reading the Nexus Mod List…"; UpdateNexusStatus();
            var progress = new Progress<string>(s => status.Text = s);
            all = await NexusMatch.AllMods(progress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        { Dialog.Show(this, "Couldn't read the mod list from Nexus: " + ex.Message, "Find My Mods on Nexus", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        finally { UseWaitCursor = false; nexusBusy = null; UpdateNexusStatus(); }
        using var f = new NexusScanForm(targets, all);
        if (f.ShowDialog(this) != DialogResult.OK || f.Confirmed.Count == 0) { Reload(); return; }
        var l = lib;
        Change($"link {f.Confirmed.Count} mod(s) to Nexus", () =>
        {
            foreach (var (m, id, version) in f.Confirmed)
                if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod mm)
                    mm.NexusLink = new NexusLink { ModId = id, Version = mm.Manifest.Version, Installed = DateTime.Now };
            return true;
        });
        CheckNexus(manual: false);
    }

    void LinkToNexus(Mod m)
    {
        if (lib == null) return;
        string? text = Ui.Prompt(this, "Link to Nexus Page", $"The Nexus page of \"{m.Name}\" (paste its address, or the mod number):", m.NexusModId is int id ? Nexus.SiteMods + id : "");
        if (text == null) return;
        if (Nexus.ParseModId(text) is not int newId) { Dialog.Show(this, "That isn't a Nexus mod page (…/marvelheroesomega/mods/<number>) or a mod number.", "Not a Nexus Page", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        m.NexusLink = new NexusLink { ModId = newId, Version = m.Manifest.Version, Installed = DateTime.Now };
        lib.SaveState();
        CheckNexus(manual: false);
        Reload();
    }

    /// <summary>
    /// Updates a mod from Nexus. Signed in with Premium: downloads the newest main file and installs it in place (place,
    /// on/off, lock, tags and note kept). Otherwise (or <paramref name="manual"/>): opens its Files page, and when the
    /// downloaded file arrives in Downloads, installs it the same way. (Nexus gives apps download links only for signed-in
    /// Premium members; no personal API keys.)
    /// </summary>
    async void UpdateFromNexus(Mod m, bool manual = false)
    {
        if (readOnly || lib == null || m.NexusModId is not int id) return;
        // A page with several files (a variant): which one this mod is must be known first, or the wrong one goes in.
        if (NexusUpdates.NeedsChoice(m, nexus) && !ChooseNexusFile(m, "Before updating: this page has several files.")) return;
        if (lib.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod fresh) m = fresh;
        if (!manual && NexusAuth.Available && NexusAuth.Load(Settings.Home)?.Premium == true)
        {
            try
            {
                UseWaitCursor = true;
                string? token = await NexusAuth.AccessToken(Settings.Home);
                if (token == null) { UseWaitCursor = false; Dialog.Show(this, "Your Nexus sign-in has ended (it may have been revoked). Sign in again from the ▾ menu in the Nexus bar, or download the update from the Files page.", "Not Updated", MessageBoxButtons.OK, MessageBoxIcon.Warning); UpdateNexusStatus(); return; }
                var progress = new Progress<string>(s => status.Text = s);
                var (path, file) = await NexusUpdates.DownloadLatest(m, token, nexus, Settings.Home, progress);
                UseWaitCursor = false;
                await FinishNexusUpdate(m, id, path, file.FileId, file.Version);
            }
            catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
            {
                UseWaitCursor = false;
                if (ex is NexusAuth.SignedOutException) UpdateNexusStatus();
                if (Dialog.Show(this, ex.Message + "\n\nDownload it from the mod's Files page instead?", "Not Updated", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    UpdateFromNexus(m, manual: true);
            }
            return;
        }
        WatchDownloads(m, id);
        Process.Start(new ProcessStartInfo(Nexus.SiteMods + id + "?tab=files") { UseShellExecute = true });
        string? which = NexusUpdates.LineFor(m, nexus);
        status.Text = Ui.TitleCase($"Download the update of \"{m.Name}\" on Nexus" + (which != null ? $" (the file \"{which}\"" + ", Manual Download)" : " (Manual Download)") +
            ": the app installs it when it arrives in your Downloads folder");
    }

    /// <summary>
    /// Which file on the mod's Nexus page it is (a page can have a default and a variant side by side). Saved as
    /// NexusLink.File, one undo step; the update check then follows only that file. False when cancelled.
    /// </summary>
    bool ChooseNexusFile(Mod m, string? why = null)
    {
        if (readOnly || lib == null || m.NexusModId is not int id || !nexus.Mods.TryGetValue(id, out var info)) return false;
        using var f = new NexusFileForm(m, info, NexusUpdates.LineFor(m, nexus), why);
        if (f.ShowDialog(this) != DialogResult.OK || f.Chosen is not { } file) return false;
        var l = lib;
        Change($"follow the Nexus file \"{file.Name}\" for \"{m.Name}\"", () =>
        {
            if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is not Mod mm) return false;
            var link = mm.NexusLink ?? new NexusLink { ModId = id, Version = mm.Manifest.Version, Installed = DateTime.Now };
            link.File = file.Name;
            link.Ignore = null;
            mm.NexusLink = link;
            return true;
        });
        return true;
    }

    /// <summary>Sign in with Nexus (OAuth in the browser) for one-click updates.</summary>
    async void SignInNexus()
    {
        if (Dialog.Show(this, "Your browser opens Nexus Mods' sign-in page. Sign in there and allow MHO Extended Mod Manager; then come back here.\n\n" +
                "The app only uses the sign-in to download the updates you choose (Nexus gives apps download links for Premium members only). " +
                "Your password is never seen by the app, and the sign-in stays on this PC, encrypted for your Windows user. Sign out any time from this menu.",
                "Sign In with Nexus", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        nexusBusy = "Waiting for the Nexus Sign-In in Your Browser…"; UpdateNexusStatus();
        try
        {
            var login = await NexusAuth.SignIn(Settings.Home, url => { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return Task.CompletedTask; });
            nexusBusy = null; UpdateNexusStatus();
            Activate();
            Dialog.Show(this, login.Premium ? $"Signed in as {login.UserName} (Premium): Update now downloads and installs with one click." :
                $"Signed in as {login.UserName}. Nexus gives apps download links only for Premium members, so updates still open the mod's Files page, and the app installs the file from your Downloads folder.",
                "Signed In", MessageBoxButtons.OK, MessageBoxIcon.None);
        }
        catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or System.Security.Cryptography.CryptographicException)
        { nexusBusy = null; UpdateNexusStatus(); Activate(); Dialog.Show(this, ex.Message, "Not Signed In", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async void SignOutNexus()
    {
        await NexusAuth.SignOut(Settings.Home);
        UpdateNexusStatus();
        status.Text = Ui.TitleCase("Signed out of Nexus (the app's access was also revoked at Nexus)");
    }

    async Task FinishNexusUpdate(Mod m, int id, string archive, long? fileId, string? version)
    {
        string folder = m.FolderName;
        if (fileId == null && Nexus.FromFileName(archive) is { } fn && nexus.Mods.TryGetValue(id, out var ci)) fileId = Nexus.FileOf(ci, fn)?.FileId;
        var done = await InstallAsync([archive], m, ask: false, showLog: false);
        if (done.Count == 0 || lib == null)
        {
            // Say why (the install log), and offer to ignore that file: e.g. a Nexus file for another tool (no manifest.json).
            bool noManifest = !ArchiveHasManifest(archive);
            string why = noManifest
                ? "It isn't a mod for this manager or MHModManager: it has no manifest.json. It's probably for another tool; the mod's Nexus page says how to install it."
                : string.Join("\n", lastInstallLog.Skip(1).Take(8)).Trim();
            long? ignore = fileId ?? NexusUpdates.LatestFor(m, nexus)?.FileId;
            if (Dialog.Show(this, $"{Path.GetFileName(archive)} couldn't be installed as the update of \"{m.Name}\".\n\n{why}" +
                    (ignore != null ? "\n\nIgnore this Nexus file from now on? The Update mark goes away until a newer file is uploaded." : ""),
                    "Not Updated", ignore != null ? MessageBoxButtons.YesNo : MessageBoxButtons.OK, MessageBoxIcon.Error) == DialogResult.Yes && ignore != null)
                IgnoreUpdate(m, ignore.Value);
            return;
        }
        string? fileName = fileId is long fi && nexus.Mods.TryGetValue(id, out var ri) ? ri.Files.FirstOrDefault(x => x.FileId == fi)?.Name : null;
        NexusUpdates.Record(lib, folder, id, fileId, version ?? Nexus.FromFileName(archive)?.Version, fileName);
        Reload();
        SelectMod(folder);
        Dialog.ShowLog(this, "Updated", $"\"{m.Name}\" is now v{(version ?? "?").TrimStart('v', 'V')}, from Nexus.\n\nIt kept its place, on/off, lock, tags and note." +
            (m.Enabled ? "\n\nIt's on: Apply Changes (Ctrl+Enter) puts the new version in the game." : ""), Dialog.Tone.Good);
    }

    static bool ArchiveHasManifest(string archive)
    {
        try
        {
            using var a = SharpCompress.Archives.ArchiveFactory.Open(archive);
            return a.Entries.Any(e => !e.IsDirectory && (e.Key ?? "").Replace('\\', '/').Split('/').Last().Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or ArgumentException) { return true; }
    }

    /// <summary>Not an update for this user: this Nexus file no longer marks the mod (a newer upload does again).</summary>
    void IgnoreUpdate(Mod m, long fileId)
    {
        if (lib == null) return;
        var l = lib;
        Change($"ignore the Nexus update of \"{m.Name}\"", () =>
        {
            if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod mm && mm.NexusLink is NexusLink link) link.Ignore = fileId;
            else if (l.Mods.FirstOrDefault(x => x.FolderName == m.FolderName) is Mod m2 && m2.NexusModId is int mid) m2.NexusLink = new NexusLink { ModId = mid, Installed = DateTime.Now, Ignore = fileId };
            return true;
        });
    }

    /// <summary>Free accounts: watch Downloads (30 minutes) for this mod's file from Nexus ("Name-&lt;id&gt;-&lt;version&gt;-&lt;time&gt;.zip").</summary>
    void WatchDownloads(Mod m, int id)
    {
        downloadWatch?.Dispose();
        string? folder = DownloadsFolder();
        if (folder == null || !Directory.Exists(folder)) return;
        var w = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        downloadWatch = w;
        var until = DateTime.Now.AddMinutes(30);
        bool taken = false;
        async void Seen(string path)
        {
            if (taken || DateTime.Now > until || Nexus.FromFileName(path) is not { } f || f.ModId != id) return;
            // Wait until the browser has finished writing it (size steady, no .part / .crdownload).
            long last = -1;
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (!File.Exists(path)) return;
                long len = new FileInfo(path).Length;
                if (len > 0 && len == last) break;
                last = len;
            }
            if (taken) return;
            taken = true;
            BeginInvoke(async () =>
            {
                downloadWatch?.Dispose(); downloadWatch = null;
                if (Dialog.Show(this, $"{Path.GetFileName(path)} arrived in Downloads.\n\nInstall it as the update of \"{m.Name}\" (v{f.Version})?", "Nexus Update Downloaded", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    await FinishNexusUpdate(m, id, path, null, f.Version);
            });
        }
        w.Created += (_, e) => Seen(e.FullPath);
        w.Renamed += (_, e) => Seen(e.FullPath);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) != 0) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
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
        editor.Dispose();                  // while still in the window (see FlatTabs.Clear: tables' tooltips)
        editorHost.Controls.Clear();
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
            // The Editor tab itself with nothing open: the highlighted mod opens (Kurt).
            string? highlighted = Selected?.FolderName;
            pages.Select(1);
            for (int t = 0; t < 3000 && editor == null; t += 100) { await Task.Delay(100); Application.DoEvents(); }
            Check("the Editor tab opens the highlighted mod", editor != null && highlighted != null && editor.Editing?.FolderName == highlighted);
            CloseEditor(); pages.Select(0);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check("the list keeps its window through the double-click", list.Handle == listHandle);
        }
        // Drag and drop (a user's request): press on the third card, move with the button held over the top half of the
        // first, let go: it becomes first (unless the first is locked there). Undo puts it back.
        if (M(a).Lock == ModLock.None && M(c).Lock == ModLock.None)
        {
            int ci = list.Items.Cast<object>().ToList().FindIndex(o => o is Mod mm && mm.FolderName == c);
            var from = list.GetItemRectangle(ci); var to = list.GetItemRectangle(topIndex);
            IntPtr L(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            int x0 = from.X + (int)(140 * DeviceDpi / 96f);
            int y0 = from.Y + (int)(12 * DeviceDpi / 96f);   // the name row (the second row has tag chips)
            SendMessage(list.Handle, 0x0201, (IntPtr)1, L(x0, y0));   // down
            foreach (int y in new[] { y0 - 12, (from.Y + to.Y) / 2, to.Y + 4 })
                SendMessage(list.Handle, 0x0200, (IntPtr)1, L(x0, y));                        // WM_MOUSEMOVE, MK_LBUTTON
            SendMessage(list.Handle, 0x0202, IntPtr.Zero, L(x0, to.Y + 4));                  // up
            Application.DoEvents();
            await Task.Delay(300);
            bool dragged = lib.Mods[0].FolderName == c && M(c).Priority == 0;
            Check("drag the third mod onto the top: it becomes first", dragged);
            if (dragged) { Undo(); Check("undo the drag: back in third place", M(c).Priority == 2 && lib.Mods[0].FolderName == a); }
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

    /// <summary>
    /// --tooltip-audit (Kurt: tooltips on all buttons, now and later): every button in the main window, a new-mod editor,
    /// Extract, Create Post, Apply, Update and the first-run window that has no tooltip. Writes the list; exit code 1 if any.
    /// </summary>
    public async Task<int> TooltipAudit(string outFile)
    {
        var found = new List<string>();
        void Check(string where, Control root) { foreach (string b in Ui.MissingTips(root)) found.Add($"{where}: {b}"); }
        await Task.Delay(1500);
        Check("Main window", this);
        OpenEditor(null); pages.Select(1); await Task.Delay(800);
        if (editor != null) { foreach (int i in Enumerable.Range(0, editor.TabCount)) { editor.SelectTab(i); await Task.Delay(150); } Check("Editor", editor); }
        CloseEditor();
        pages.Select(2); EnsureExtract(); await Task.Delay(800);
        Check("Extract", extractHost);
        pages.Select(0);
        if (lib?.Mods.FirstOrDefault() is Mod m)
            using (var p = new PostForm(PostWriter.From(m), m.Folder, ModPost.Read(m.Folder), (_, _, _) => { })) Check("Create Post", p);
        using (var a = new ApplyForm("plan", () => Task.FromResult((true, "")), checks: true, review: true)) Check("Apply", a);
        if (lib != null && game != null) using (var gf = new GameFilesForm(lib, game)) Check("Changed Game Files", gf);
        using (var u = new UpdateForm(new Updater.Release(new Version(9, 9, 9), "extmm-v9.9.9", "", "", Updater.ReleasesPage, "", "", 0))) Check("Update", u);
        using (var fr = new FirstRunForm(settings)) Check("First-run setup", fr);
        File.WriteAllLines(outFile, found.Count == 0 ? ["Every button has a tooltip."] : found);
        return found.Count == 0 ? 0 : 1;
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
            await Task.Delay(editor.TabTitle(i) is "Packages" or "Store Images" ? 6000 : editor.TabTitle(i) is "Animations" or "Powers" ? 9000 : 1500);   // icon names / previews / 3D models load in the background
            using var b = new Bitmap(Width, Height);
            DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
            b.Save(Path.Combine(dir, $"editor_{i}_{editor.TabTitle(i).Replace(' ', '_')}.png"));
        }
        CloseEditor();
    }

    /// <summary>
    /// --model-tab-test (scratch library only): the editor on <paramref name="modName"/>, its Model tab, an MFF character built
    /// onto one of the mod's packages, Save Changes; then the saved mod must have that package changed and a Model folder.
    /// </summary>
    public async Task<int> ModelTabTest(string modName, string mff, string package, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        string pkgFile = Path.Combine(m.Folder, package);
        OpenEditor(m);
        pages.Select(1);
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == "Model", -1);
        if (tab < 0) { say("no Model tab (PreviewFeatures off?)"); return 1; }
        editor.SelectTab(tab);
        for (int i = 0; i < 50 && editor.ModelPageForTest == null; i++) await Task.Delay(100);
        if (editor.ModelPageForTest is not { } page) { say("the Model tab didn't open"); return 1; }
        string? builtFile = await page.TestBuild(mff, package, say);
        say("built into the draft: " + (builtFile != null));
        if (builtFile == null) { CloseEditor(); return 1; }
        if (page.TestHasRig && !await page.TestRigWatch(say)) { CloseEditor(); return 1; }
        string? liveRig = page.TestLiveRig;
        byte[] builtBytes = File.ReadAllBytes(builtFile);   // the work folder goes with the editor
        await editor.SaveForTest();
        await Task.Delay(500);
        var saved = lib?.Find(modName);
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        Check(saved != null, "the mod is still in the library");
        if (saved != null)
        {
            string after = Path.Combine(saved.Folder, package);
            Check(File.Exists(after) && File.ReadAllBytes(after).AsSpan().SequenceEqual(builtBytes), "its package is the build, byte for byte");
            Check(File.Exists(Path.Combine(saved.Folder, ModelWork.Folder, "state.json")), "Model/state.json is kept with the mod");
            Check(!Directory.Exists(Path.Combine(saved.Folder, ModelWork.Folder, "builds")), "no builds folder in the mod");
            Check(saved.Manifest.UpkReplacements.Contains(package, StringComparer.OrdinalIgnoreCase), "the manifest still lists the package");
        }
        Check(!Directory.EnumerateDirectories(lib!.DataFolder, "model-work-*").Any(), "the editor's work folder is gone");
        if (liveRig != null && saved != null)
        {
            // a model without an armature: the mod keeps its rig for the hero built onto (rig files only); the live rig, which an
            // open Blender works on, outlives the editor
            string rigs = Path.Combine(saved.Folder, ModelWork.Folder, "rigs");
            var files = Directory.Exists(rigs) ? Directory.GetFiles(rigs, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(rigs, f)).ToList() : [];
            say("  kept in the mod: " + string.Join(", ", files));
            Check(files.Count > 0 && files.All(f => f.Contains(" on " + Path.GetFileNameWithoutExtension(package)) && MhoExtendedModManager.Model.AutoRig.IsRigFile(Path.GetFileName(f)))
                && files.Any(f => f.EndsWith("rigged.fbx")) && files.Any(f => f.EndsWith("rig.blend")), "the mod keeps the built hero's rig, rig files only");
            Check(File.Exists(Path.Combine(liveRig, "rigged.fbx")) && File.Exists(Path.Combine(liveRig, "rig.blend")), "the live rig (data/model/rigs) is still there for Blender");
        }
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// --model-tab-timing (scratch library only): the editor's tab named by MHO_TIMING_TAB (default Model) shown for a few
    /// seconds, then how often the UI thread got a turn (a 10 ms timer). An idle window gets about 64 a second; the Model tab
    /// got 5 while an empty filter box showed .NET's placeholder text (0.37.116 fix: Field.UseCueBanner). Fails under 40.
    /// </summary>
    public async Task<int> ModelTabTiming(string modName, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        OpenEditor(m);
        pages.Select(1);
        string want = Environment.GetEnvironmentVariable("MHO_TIMING_TAB") ?? "Model";
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == want, -1);
        if (tab < 0) { say("no tab " + want); return 1; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = 0, worst = 0, ticks = 0;
        using var probe = new System.Windows.Forms.Timer { Interval = 10 };
        probe.Tick += (_, _) => { long now = sw.ElapsedMilliseconds; worst = Math.Max(worst, now - last); last = now; ticks++; };
        probe.Start();
        editor.SelectTab(tab);
        await Task.Delay(4000);   // loading, thumbnails
        int fails = 0;
        for (int s = 1; s <= 3; s++)
        {
            worst = 0; ticks = 0;
            await Task.Delay(1000);
            say($"{want}, idle second {s}: {ticks} turns, longest gap {worst} ms");
            if (ticks < 40) fails++;
        }
        CloseEditor();
        say(fails == 0 ? "PASS the window stays responsive" : "FAIL the window is busy while idle");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// --model-blender-test (scratch library only): the Model tab's Blender round trip (see ModelPage.TestBlender), then Save
    /// Changes: the edit must be in the mod's Model folder with a relative path.
    /// </summary>
    public async Task<int> ModelBlenderTest(string modName, string mff, string package, string anim, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        OpenEditor(m);
        pages.Select(1);
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == "Model", -1);
        if (tab < 0) { say("no Model tab (PreviewFeatures off?)"); return 1; }
        editor.SelectTab(tab);
        for (int i = 0; i < 50 && editor.ModelPageForTest == null; i++) await Task.Delay(100);
        if (editor.ModelPageForTest is not { } page) { say("the Model tab didn't open"); return 1; }
        string? rel = await page.TestBlender(mff, package, anim, say);
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        Check(rel != null, "the save in Blender came back as an FBX edit of " + anim);
        if (rel == null) { CloseEditor(); return 1; }
        await editor.SaveForTest();
        await Task.Delay(500);
        var saved = lib?.Find(modName);
        string? editsTxt = saved == null ? null : Directory.EnumerateFiles(Path.Combine(saved.Folder, ModelWork.Folder, "edits"), "edits.txt", SearchOption.AllDirectories).FirstOrDefault();
        Check(editsTxt != null, "the mod's Model folder has the edits");
        if (editsTxt != null)
        {
            string text = File.ReadAllText(editsTxt);
            say("edits.txt: " + text.Replace("\n", " | "));
            Check(text.Contains($"anim:{anim}=", StringComparison.OrdinalIgnoreCase) && !text.Contains(':' + "\\"), "the edit names its FBX relative to the edits folder");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(editsTxt)!, rel)), "the edited FBX is in the mod");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(editsTxt)!, "blender.txt")), "the Blender link is kept (blender.txt)");
        }
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Settings → Your Author Name: the name every new mod starts with (empty: none).</summary>
    void EditAuthorName()
    {
        var authors = (lib?.Mods ?? []).Select(m => m.Manifest?.Author).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a);
        if (Ui.Prompt(this, "Your Author Name", "Filled in as the author of every new mod you make (leave empty for none):", settings.AuthorName ?? "", authors) is not string name) return;
        settings.AuthorName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        settings.Save();
    }

    /// <summary>Set by a normal start when setup was already done before (not for a new user, not in tests).</summary>
    public bool ShowsWhatsNew { get; init; }

    /// <summary>
    /// The newest notice of new features (WhatsNew.Notices), shown once at start to people who used an earlier version
    /// (Kurt, 2026-10-02: point them at the Editor's new tools and the manual); a new user just has it marked as seen.
    /// Settings → What's New shows it again, and the older ones.
    /// </summary>
    void ShowWhatsNew()
    {
        var latest = WhatsNew.Notices[0];
        if (settings.WhatsNewSeen == latest.Id) return;
        settings.WhatsNewSeen = latest.Id;
        settings.Save();
        if (ShowsWhatsNew) ShowNotice(0);
    }

    /// <summary>A notice with Open the Manual, Older (to the one before it, when there is one) and Close.</summary>
    void ShowNotice(int i)
    {
        while (i < WhatsNew.Notices.Count)
        {
            var n = WhatsNew.Notices[i];
            bool older = i + 1 < WhatsNew.Notices.Count;
            string[] labels = older ? ["Open the Manual", "Older", "Close"] : ["Open the Manual", "Close"];
            int pick = Dialog.Choose(this, n.Text, $"What's New in Version {n.Version}", labels);
            if (pick == 0) { HelpForm.Show(this, settings, n.Anchor); return; }
            if (older && pick == 1) { i++; continue; }
            return;
        }
    }

    /// <summary>--editor-save-test: opens a mod in the Editor, changes nothing, saves (use with MHO_EXTMM_HOME on a scratch library).</summary>
    public async Task EditorSaveTest(string modName)
    {
        var m = lib?.Find(modName);
        if (m == null) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), "no such mod"); return; }
        OpenEditor(m);
        pages.Select(1);
        var ed = editor!;
        await ed.SearchStringsForTest("Vision");
        string check = ed.StringsCheck();
        using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_strings.png")); }
        string? saved = await ed.SaveForTest();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), (saved ?? "not saved") + Environment.NewLine + check);
    }

    /// <summary>--powers-shot (scratch libraries only): the Powers tab's preview on a power at a frame, as a PNG.</summary>
    public async Task PowersShot(string png, string modName, string power, double fraction)
    {
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            if (await editor!.PowersShot(power, fraction) is { } c)
                using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height)); b.Save(png); }
        }
        catch (Exception ex) { File.WriteAllText(png + ".txt", ex.ToString()); }
    }

    /// <summary>--keep-frame-test (scratch libraries only): the Powers tab keeps the frame slider's place across powers.</summary>
    public async Task KeepFrameTest(string dir, string modName, string a, string b)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.KeepFrameTest(a, b));
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>--power-map-test (scratch libraries only): a color replaced through the Powers tab, saved, and checked on
    /// disk (the manifest's PowerColors.Maps, the built packages and their colors).</summary>
    public async Task PowerMapTest(string dir, string modName, string power, string toHex)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.PowerMapTest(power, toHex));
            await Task.Delay(1500);
            using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(dir, "powers_tab.png")); }
            string? saved = await editor.SaveForTest();
            lines.Add($"{(saved != null ? "PASS" : "FAIL")} saved");
            m = lib!.Find(saved ?? modName)!;
            var pc = m.Manifest.PowerColors ?? [];
            var entry = pc.FirstOrDefault(x => x.Maps is { Count: > 0 });
            lines.Add($"{(entry != null && entry.Packages.Count > 0 ? "PASS" : "FAIL")} manifest: {entry?.Name}: maps {string.Join(", ", entry?.Maps?.Select(x => x.From + "→" + x.To) ?? [])}; packages {string.Join(", ", entry?.Packages ?? [])}");
            if (entry != null && game != null)
                foreach (string f in entry.Packages)
                {
                    var pal = PowerRecolor.Palette([Path.Combine(m.Folder, f)], game.Cooked);
                    lines.Add($"  {f}: {string.Join(" ", pal.Take(8).Select(x => ColorMap.Hex(x.Tint) + $" {x.Share * 100:0}%"))}");
                }
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>--anim-find-test (scratch libraries only): the Animations tab's Find, then a real click on the first match.</summary>
    public async Task AnimFindTest(string dir, string modName, string find)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.FindClickForTest(find));
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>
    /// --voice-shift-tab-test (scratch libraries only): Shift This Voice through the Editor, saved and checked on disk (the
    /// manifest's VoiceShifts, the pack, the voice set at …_mhoshift events), then Remove Shift, saved, and the costume package
    /// compared with the one before. Results in &lt;dir&gt;\result.txt.
    /// </summary>
    public async Task VoiceShiftTabTest(string dir, string modName, float pitch, float formant, float warmth)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        void Check(bool ok, string what) => Note((ok ? "PASS " : "FAIL ") + what);
        void Note(string l) { lines.Add(l); File.AppendAllText(Path.Combine(dir, "progress.txt"), l + Environment.NewLine); }
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            var before = m.Manifest.UpkReplacements.Where(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(m.Folder, f)));
            OpenEditor(m); pages.Select(1);
            if (await editor!.HeroVoiceForTest() is { } hint) Note("hint: " + hint);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await editor!.ShiftForTest(pitch, formant, warmth);
            Note($"shift: {sw.Elapsed.TotalSeconds:F1} s, note \"{editor.ShiftNote}\"");
            string? saved = await editor.SaveForTest();
            Check(saved != null, "saved after the shift");
            m = lib!.Find(saved ?? modName)!;
            var vs = m.Manifest.VoiceShifts ?? [];
            Check(vs.Count > 0 && vs.All(v => Math.Abs(v.Pitch - pitch) < 0.01f), $"manifest VoiceShifts ({vs.Count})");
            foreach (var v in vs)
            {
                string pack = VoiceShiftBuild.PackName(v.Package);
                Check(m.Manifest.AudioPacks.Any(a => a.Equals(pack, StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(m.Folder, pack)), "pack " + pack);
                var set = VoiceSet.Read(v.Package, Path.Combine(m.Folder, v.Package), m.Manifest.VoiceOff ?? []);
                int shifted = set.Count(l => l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase));
                Check(shifted > 0, $"{v.Package}: {shifted} of {set.Count} voice set entries shifted");
            }
            Note("reopening");
            OpenEditor(m); pages.Select(1);
            Note("remove shift");
            editor!.RemoveShiftForTest();
            saved = await editor.SaveForTest();
            Check(saved != null, "saved after Remove Shift");
            m = lib!.Find(saved ?? modName)!;
            Check((m.Manifest.VoiceShifts?.Count ?? 0) == 0, "no VoiceShifts left");
            Check(!m.Manifest.AudioPacks.Any(a => a.StartsWith("VoiceShift_", StringComparison.OrdinalIgnoreCase)), "no shift pack left");
            foreach (var (f, b) in before)
            {
                var set = VoiceSet.Read(f, Path.Combine(m.Folder, f), m.Manifest.VoiceOff ?? []);
                Check(!set.Any(l => l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase)), f + ": voice set back at the original events");
                lines.Add($"{f}: {(File.ReadAllBytes(Path.Combine(m.Folder, f)).AsSpan().SequenceEqual(b) ? "identical to before" : "differs from before (the shifted copies' events stay in the package)")}");
            }
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>
    /// --anim-tab-test (scratch libraries only): the Animations tab on a mod: the picker as PNG, a change through the tab
    /// (as Use This Animation), saved and checked on disk (the slot from a swapped-in set, the manifest's AnimSwaps), then
    /// Back to the Original, saved and checked again. Results in &lt;dir&gt;\result.txt.
    /// </summary>
    public async Task AnimTabTest(string dir, string modName, string slot, string donor, string anim)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        void Shot(string name) { using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(dir, name)); }
        async Task<ModEditorView?> Open(string name)
        {
            var m = lib == null ? null : ModLibrary.Load(lib.DataFolder).Find(name);
            if (m == null) return null;
            OpenEditor(m);
            pages.Select(1);
            var ed = editor!;
            for (int i = 0; i < ed.TabCount; i++) if (ed.TabTitle(i) == "Animations") ed.SelectTab(i);
            await Task.Delay(4000);
            return ed;
        }
        (bool Swapped, int Entries, string From) Check(string name)
        {
            var m = ModLibrary.Load(lib!.DataFolder).Find(name);
            if (m == null) return (false, -1, "no mod");
            string file = m.Manifest.UpkReplacements.First(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase));
            var ca = CostumeAnims.Read(Path.Combine(m.Folder, file), file, CostumeAnims.FilesFor(m, settings.ResolvedGameRoot(lib.DataFolder) is string r ? Settings.Cooked(r) : null));
            var a = ca?.Anims.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
            return (a != null && a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase), m.Manifest.AnimSwaps?.Count ?? 0, a?.From.Path ?? "none");
        }
        var ed = await Open(modName);
        if (ed == null) { File.WriteAllText(Path.Combine(dir, "result.txt"), "no such mod"); return; }
        await ed.AnimPickerSnapshot(slot, donor, Path.Combine(dir, "picker.png"));
        lines.Add("change: " + await ed.AnimTestChange(slot, donor, anim));
        await Task.Delay(2500);
        Shot("tab_changed.png");
        string? saved = await ed.SaveForTest();
        await Task.Delay(1500);
        var c1 = Check(saved ?? modName);
        lines.Add($"saved '{saved}': {slot} swapped={c1.Swapped} from {c1.From}; AnimSwaps entries {c1.Entries}");
        var ed2 = await Open(saved ?? modName);
        if (ed2 == null) { lines.Add("can't reopen"); File.WriteAllLines(Path.Combine(dir, "result.txt"), lines); return; }
        Shot("tab_reopened.png");
        lines.Add("back: " + await ed2.AnimTestBack(slot));
        string? saved2 = await ed2.SaveForTest();
        await Task.Delay(1500);
        var c2 = Check(saved2 ?? modName);
        lines.Add($"saved '{saved2}': {slot} swapped={c2.Swapped} from {c2.From}; AnimSwaps entries {c2.Entries}");
        bool ok = c1.Swapped && c1.Entries == 1 && !c2.Swapped && c2.Entries == 0;
        // Copy From a Character: Storm's emotes of the same names at once.
        var ed3 = await Open(saved2 ?? modName);
        if (ed3 != null)
        {
            await ed3.AnimPickerSnapshot("*", "Storm", Path.Combine(dir, "picker_many.png"));
            lines.Add("copy many: " + await ed3.AnimTestCopyMany("Storm", "Emote"));
            Shot("tab_many.png");
            string? saved3 = await ed3.SaveForTest();
            await Task.Delay(1500);
            var m3 = ModLibrary.Load(lib!.DataFolder).Find(saved3 ?? modName);
            string file3 = m3!.Manifest.UpkReplacements.First(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase));
            var ca3 = CostumeAnims.Read(Path.Combine(m3.Folder, file3), file3, CostumeAnims.FilesFor(m3, settings.ResolvedGameRoot(lib.DataFolder) is string r3 ? Settings.Cooked(r3) : null));
            int swapped = ca3?.Anims.Count(a => a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase)) ?? 0, entries = m3.Manifest.AnimSwaps?.Count ?? 0;
            lines.Add($"saved: {swapped} animations from a swapped-in set; AnimSwaps entries {entries}");
            ok &= swapped > 1 && swapped == entries;
        }
        lines.Add(ok ? "PASS" : "FAIL");
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
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
        List<string>? addTags = null; string? note = null, pick = null, card = null; Dictionary<string, float[]>? views = null; float? light = null;
        var mineTags = m.UserTags.Where(t => !m.ModTags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        var mineViews = PreviewViews.ForMod(m);
        var parts = new List<string>();
        if (mineTags.Count > 0) parts.Add($"your tags ({string.Join(", ", mineTags)})");
        if (m.LocalNote != null) parts.Add("your note");
        if (m.LocalPreview != null) parts.Add("your preview choice" + (m.LocalPreview.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase) ? " (the 3D view" + (m.LocalPreview.Contains('@') ? " and its animation" : "") + ")" : ""));
        if (mineViews.Count > 0) parts.Add($"your 3D camera view{(mineViews.Count > 1 ? "s" : "")}");
        var mineLight = PreviewViews.LocalLight(m);
        if (mineLight != null) parts.Add($"your 3D light level ({mineLight * 100:0} %)");
        if (m.LocalCard != null) parts.Add($"your card picture ({m.LocalCard})");
        if (!legacy && parts.Count > 0)
        {
            string what = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
            var a = Dialog.Show(this, $"Put {what} into the exported mod?\n\nThey are only on this PC so far. The mod's own tags, note and preview go along anyway; automatic tags are worked out again by whoever installs it. " +
                "Whoever installs it starts from your preview, view and light, and can still pick, turn and light their own.",
                "Export", MessageBoxButtons.YesNoCancel);
            if (a == DialogResult.Cancel) return;
            if (a == DialogResult.Yes) { addTags = mineTags; note = m.LocalNote; pick = m.LocalPreview; views = mineViews; light = mineLight; card = m.LocalCard; }
        }
        using var d = new SaveFileDialog { Title = legacy ? "Export Mod (Legacy)" : "Export Mod", Filter = "Zip archive (*.zip)|*.zip", FileName = ModInstaller.ZipName(m, legacy) };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { ModInstaller.Export(m, d.FileName, legacy, addTags, note, pick, views, light, card); ModPost.WriteBeside(m, d.FileName); status.Text = $"Exported {m.Name} to {d.FileName}" + (legacy ? " (legacy: other icon packages, tags, note and preview left out)" : addTags != null ? " (with the user tags / note / preview)" : ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>
    /// Remove (Kurt: say what will happen, ask once, then do it): turn the mod off, Apply Changes so the game gets its
    /// original files back (with everything else waiting in the list), then move its folder to the Recycle Bin. If Apply
    /// stops, nothing is removed: the mod stays in the list, turned off, and the log says where it stopped.
    /// </summary>
    async void RemoveMod()
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        bool inGame = game != null && (m.Enabled || m.Manifest.UpkReplacements.Count + m.Manifest.Replacements.Count + m.Manifest.StoreReplacements.Count
            + m.Manifest.AchievementReplacements.Count + m.Manifest.Extra.Count() + m.Strings.Count + m.Manifest.AudioPacks.Count > 0);
        string nl = Environment.NewLine;
        string steps = inGame
            ? $"This will:{nl}  1. Turn it off{(m.Enabled ? "" : " (it's off already)")}.{nl}  2. Apply Changes, so the game gets its original files back (other changes waiting in your list are applied too).{nl}  3. Move its folder to the Recycle Bin.{nl}{nl}The game must be closed."
            : "Its folder goes to the Recycle Bin.";
        if (Dialog.Show(this, $"Remove \"{m.Name}\" from the library?{nl}{nl}{steps}", "Remove Mod", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (inGame && Process.GetProcessesByName("MarvelHeroesOmega").Length > 0)
        { Dialog.Show(this, "Close the game first: the game's files can only be changed while it isn't running.", "The Game Is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var (l, g) = (lib, game);
        string name = m.Name;
        if (m.Enabled) { m.Enabled = false; l.SaveState(); }
        UseWaitCursor = true;
        status.Text = Ui.TitleCase($"Removing \"{name}\"…");
        (bool Ok, string Log, string? Why) r;
        try
        {
            r = await Task.Run(() =>
            {
                if (g != null)
                {
                    var originals = new Originals(l.DataFolder, g);
                    var plan = Applier.MakePlan(l, g, originals);
                    if (plan.Steps.Count > 0)
                    {
                        bool ok = false;
                        string log = CaptureOutput(() => ok = Applier.Execute(plan, g, originals, l.DataFolder));
                        if (!ok) return (false, log, (string?)null);
                    }
                }
                return (true, "", ModInstaller.Remove(m, l, g));
            });
        }
        finally { UseWaitCursor = false; }
        Reload();
        if (!r.Ok) { Dialog.ShowLog(this, "Not Removed", $"Apply Changes stopped, so \"{name}\" wasn't removed (it's turned off).{nl}{nl}" + r.Log, Dialog.Tone.Bad); return; }
        if (r.Why != null) { Dialog.Show(this, r.Why, "Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        status.Text = Ui.TitleCase($"Removed \"{name}\"  ·  its folder is in the Recycle Bin");
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
    internal static string CaptureOutput(Action a)
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
    /// <summary>--preview-selftest: the 3D view's controls on one mod (use a scratch library: picks are saved).</summary>
    public async Task PreviewSelfTest(string dir, string modName)
    {
        Directory.CreateDirectory(dir);
        if (lib?.Find(modName) is not Mod pick) { File.WriteAllText(Path.Combine(dir, "preview_selftest.txt"), "FAIL no such mod"); return; }
        SelectMod(pick.FolderName);
        await Task.Delay(1500);
        var log = await storePreview.SelfTest();
        // Kurt: the view is kept when leaving the mod and coming back (camera, animation and its frame).
        if (await storePreview.PoseForTest() is { } posed && lib?.Mods.FirstOrDefault(x => x.FolderName != pick.FolderName) is Mod other)
        {
            float lightWas = PreviewViews.Light(pick), otherWas = PreviewViews.Light(other);
            PreviewViews.SetLight(pick, 1.4f); PreviewViews.SetLight(other, 1f);
            SelectMod(other.FolderName);
            await Task.Delay(1500);
            float? otherShown = storePreview.ShownLight;
            SelectMod(pick.FolderName);
            (string? Anim, double Time, float[] View)? back = null;
            for (int t = 0; t < 15000; t += 100) { await Task.Delay(100); back = storePreview.Shown3D(); if (back?.Anim == posed.Anim) break; }
            bool ok = back is { } b && b.Anim == posed.Anim && Math.Abs(b.Time - posed.Time) < 1e-3 && b.View.Zip(posed.View).All(p => Math.Abs(p.First - p.Second) < 1e-4);
            log.Add($"{(ok ? "ok  " : "FAIL")} another mod and back: same animation ({posed.Anim}), frame ({posed.Time:0.00} s) and camera" +
                (ok ? "" : $" (got {back?.Anim ?? "none"}, {back?.Time:0.00} s)"));
            bool lightOk = (otherShown == null || Math.Abs(otherShown.Value - 1f) < 1e-4) && storePreview.ShownLight is float mine && Math.Abs(mine - 1.4f) < 1e-4;
            log.Add($"{(lightOk ? "ok  " : "FAIL")} the light is per mod (the other mod at 100 %, this one back at 140 %)");
            PreviewViews.SetLight(pick, lightWas); PreviewViews.SetLight(other, otherWas);
        }
        else log.Add("FAIL couldn't pose the 3D view, or no other mod to switch to");
        log.Add(log.Any(l => l.StartsWith("FAIL")) ? $"{log.Count(l => l.StartsWith("FAIL"))} FAILED" : "All preview checks passed.");
        File.WriteAllLines(Path.Combine(dir, "preview_selftest.txt"), log);
    }

    public async Task Snapshot(string dir, string? modName = null)
    {
        Directory.CreateDirectory(dir);
        if (modName != null && lib?.Find(modName) is Mod pick) { SelectMod(pick.FolderName); list.TopIndex = Math.Max(0, list.SelectedIndex - 5); }
        foreach (var t in new[] { loading, pending }) if (t != null) { try { await t; } catch { } }
        await Task.Delay(5000);   // card pictures (stock ones open the icons package) decode in the background
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
