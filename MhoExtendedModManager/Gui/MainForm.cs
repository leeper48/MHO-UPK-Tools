using System.Diagnostics;
using System.Text;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The main window: the library's mods in priority order (top wins) on the left, the selected mod's contents and live
/// state on the right. The library is automatic (Settings… moves it); the game folder is shown, changed under Settings….
/// Install (buttons or drag and drop), Export, Remove, enable/disable, order and Apply; New Mod / Edit / Extract are a
/// later phase. A library that is still MHModManager's own folder is shown read-only.
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
    readonly List<Button> writeButtons = [];

    readonly Label gameLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Label runningLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(8, 0, 0, 0) };
    readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9.5f), WordWrap = false };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };

    public MainForm()
    {
        Text = $"MHO Extended Mod Manager v{Program.Version}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Width = 1400; Height = 850; WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, Padding = new Padding(6) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.Controls.Add(Caption("Marvel Heroes:"), 0, 0);
        paths.Controls.Add(gameLabel, 1, 0);
        paths.Controls.Add(runningLabel, 2, 0);
        paths.Controls.Add(MakeButton("Refresh", Reload), 3, 0);
        var settingsButton = MakeButton("Settings…", () => { });
        var menu = new ContextMenuStrip();
        menu.Items.Add("Change game folder…", null, (_, _) => BrowseGame());
        menu.Items.Add("Move library…", null, (_, _) => MoveLibrary());
        menu.Items.Add("Open library folder", null, (_, _) => { if (Settings.LibraryData(settings.LibraryPath) is string d) Process.Start("explorer.exe", $"\"{d}\""); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Migrate from MHModManager…", null, (_, _) => Migrate());
        settingsButton.Click += (_, _) => menu.Show(settingsButton, new Point(0, settingsButton.Height));
        paths.Controls.Add(settingsButton, 4, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4, 0, 4, 0) };
        foreach (var (label, act) in new (string, Action)[] { ("Apply", Apply), ("Enable / Disable", Toggle), ("Move Up", () => MoveSelected(-1)), ("Move Down", () => MoveSelected(1)),
            ("Install Mod…", InstallMod), ("Export…", ExportMod), ("Remove", RemoveMod), ("Capture icon changes", CaptureIcons) })
        {
            var b = MakeButton(label, act); writeButtons.Add(b); actions.Controls.Add(b);
        }
        // The old manager's mod authoring tools: a later phase.
        foreach (string a in new[] { "+ New Mod", "Edit", "Extract…" })
            actions.Controls.Add(new Button { Text = a, AutoSize = true, Enabled = false });

        // Drop .zip / .7z / folders on the window to install them.
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = !readOnly && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Install(files); };

        list.Columns.Add("#", 40, HorizontalAlignment.Right);
        list.Columns.Add("State", 70);
        list.Columns.Add("Name", 320);
        list.Columns.Add("Author", 110);
        list.Columns.Add("Version", 90);
        list.Columns.Add("Contents", 260);
        list.Columns.Add("Conflict", 70);
        list.SelectedIndexChanged += (_, _) => ShowDetails();
        list.DoubleClick += (_, _) => Toggle();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(details);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(paths, 0, 0);
        root.Controls.Add(actions, 0, 1);
        root.Controls.Add(split, 0, 2);
        root.Controls.Add(status, 0, 3);
        Controls.Add(root);

        Load += (_, _) => { Theme.Apply(this, Palette.Dark); Reload(); };
        Shown += (_, _) => split.SplitterDistance = (int)(split.Width * 0.58);   // after maximizing
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) => UpdateRunning();
        timer.Start();
        UpdateRunning();
    }

    /// <summary>Each column as wide as the wider of its content and its header (fixed pixel widths don't follow display scaling).</summary>
    static void FitColumns(ListView lv)
    {
        foreach (ColumnHeader c in lv.Columns)
        {
            lv.AutoResizeColumn(c.Index, ColumnHeaderAutoResizeStyle.ColumnContent);
            int content = c.Width;
            lv.AutoResizeColumn(c.Index, ColumnHeaderAutoResizeStyle.HeaderSize);
            c.Width = Math.Max(content, c.Width);
        }
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 0, 6, 0) };
    static Button MakeButton(string text, Action onClick) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => onClick(); return b; }

    void UpdateRunning()
    {
        bool running = Process.GetProcessesByName("MarvelHeroesOmega").Length > 0;
        runningLabel.Text = running ? "Game running" : "Game not running";
        runningLabel.ForeColor = running ? Color.FromArgb(230, 120, 90) : Theme.Current.Subtle;
    }

    void BrowseGame()
    {
        using var d = new FolderBrowserDialog { Description = "The Marvel Heroes folder (holds UnrealEngine3 and Data)", UseDescriptionForTitle = true, SelectedPath = settings.GameRoot ?? "" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(Settings.Cooked(d.SelectedPath))) { MessageBox.Show(this, "No UnrealEngine3\\MarvelGame\\CookedPCConsole there.", Text); return; }
        settings.GameRoot = d.SelectedPath; settings.Save(); Reload();
    }

    void Reload()
    {
        string? keep = list.SelectedItems.Count > 0 ? ((Mod)list.SelectedItems[0].Tag!).FolderName : null;
        string? data = Settings.LibraryData(settings.LibraryPath);
        readOnly = data == null || Program.IsOldManager(data);
        foreach (var b in writeButtons) b.Enabled = !readOnly;
        list.Items.Clear(); details.Clear();
        lib = null; game = null;
        if (data == null) { status.Text = "No mod library yet: Settings… → Migrate from MHModManager, or restart for the first-run setup."; return; }
        lib = ModLibrary.Load(data);
        string? gameRoot = settings.ResolvedGameRoot(data);
        gameLabel.Text = gameRoot ?? "(not set: Settings… → Change game folder)";
        if (gameRoot != null && Directory.Exists(Settings.Cooked(gameRoot))) game = new GameState(gameRoot, data);

        winners = lib.PackageWinners();
        var conflicts = lib.Conflicts();
        conflicted = conflicts.SelectMany(c => c.Mods).ToHashSet();
        foreach (var m in lib.Mods)
        {
            var item = new ListViewItem([(m.Priority + 1).ToString(), m.Enabled ? "on" : "off", m.Name, m.Manifest.Author ?? "", m.Manifest.Version ?? "", m.LoadError ?? m.Summary(), conflicted.Contains(m) ? "yes" : ""]) { Tag = m };
            if (!m.Enabled) item.ForeColor = Theme.Current.Subtle;
            if (m.LoadError != null || m.MissingFiles().Any()) item.ForeColor = Color.FromArgb(230, 120, 90);
            list.Items.Add(item);
        }
        status.Text = $"{lib.Mods.Count} mods, {lib.Mods.Count(m => m.Enabled)} enabled, {conflicts.Count} conflicting change(s)" +
                      (game == null ? " · game folder not found" : game.HasStockList ? $" · stock list {game.StockCount:N0} packages" : " · no stock checksum list");
        if (readOnly) status.Text += " · MHModManager's own folder: read-only here, Migrate to manage it";
        FitColumns(list);
        var sel = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((Mod)i.Tag!).FolderName == keep) ?? (list.Items.Count > 0 ? list.Items[0] : null);
        if (sel != null) { sel.Selected = true; sel.EnsureVisible(); }
        if (!readOnly && game != null && game.HasStockList) CountPending();
    }

    /// <summary>Adds "Apply: N package(s) to change" to the status line once the (hashing) plan is ready.</summary>
    void CountPending()
    {
        var (l, g) = (lib!, game!);
        string baseText = status.Text;
        pending = Task.Run(() => Applier.MakePlan(l, g, new Originals(l.DataFolder, g))).ContinueWith(t =>
        {
            if (t.IsFaulted || lib != l) return;
            status.Text = baseText + (t.Result.Steps.Count == 0 ? " · game matches" : $" · Apply: {t.Result.Steps.Count} file(s) to change") +
                          (t.Result.Problems.Count > 0 ? $", {t.Result.Problems.Count} can't be (see Apply)" : "");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    Mod? Selected => list.SelectedItems.Count > 0 ? (Mod)list.SelectedItems[0].Tag! : null;

    void Toggle()
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        m.Enabled = !m.Enabled;
        lib.SaveState();
        Reload();
    }

    void MoveSelected(int delta)
    {
        if (readOnly || lib == null || Selected is not Mod m) return;
        lib.Move(m, delta);
        lib.SaveState();
        Reload();
    }

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
        Reload();
        if (installed.Count > 0)
        {
            var first = list.Items.Cast<ListViewItem>().FirstOrDefault(i => ((Mod)i.Tag!).FolderName == installed[0]);
            if (first != null) { list.SelectedItems.Clear(); first.Selected = true; first.EnsureVisible(); }
        }
        ShowLog("Install", string.Join("\n", log) + (installed.Count > 0 ? "\n\nNew mods are added at the top of the list, turned off: select one, Enable, then Apply." : ""));
    }

    void ExportMod()
    {
        if (Selected is not Mod m) return;
        using var d = new SaveFileDialog { Title = "Export mod", Filter = "Zip archive (*.zip)|*.zip", FileName = ModInstaller.Sanitise(m.Name) + ".zip" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { ModInstaller.Export(m, d.FileName); status.Text = $"Exported {m.Name} to {d.FileName}"; }
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
        using var f = new Form { Text = title, Width = 1000, Height = 600, StartPosition = FormStartPosition.CenterParent, Icon = Icon };
        f.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = details.Font, Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n") });
        Theme.Apply(f, Palette.Dark);
        f.ShowDialog(this);
    }

    /// <summary>Selected mod's contents; package states are hashed in the background and filled in when done.</summary>
    void ShowDetails()
    {
        if (list.SelectedItems.Count == 0 || lib == null) return;
        var m = (Mod)list.SelectedItems[0].Tag!;
        details.Text = Describe(m, null);
        if (game == null || m.Manifest.UpkReplacements.Count == 0) return;
        var g = game;
        loading = Task.Run(() => m.Manifest.UpkReplacements.ToDictionary(f => f, f => g.Check(m, f), StringComparer.OrdinalIgnoreCase))
            .ContinueWith(t =>
            {
                if (t.IsFaulted || list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag != m) return;
                details.Text = Describe(m, t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    string Describe(Mod m, Dictionary<string, PackageState>? states)
    {
        var sb = new StringBuilder();
        sb.AppendLine(m.Name);
        sb.AppendLine($"by {m.Manifest.Author ?? "?"}, version {m.Manifest.Version ?? "?"}, type {m.Manifest.Type}, priority {m.Priority + 1}, {(m.Enabled ? "enabled" : "disabled")}");
        sb.AppendLine($"folder: {m.Folder}");
        if (m.LoadError != null) { sb.AppendLine().AppendLine("ERROR: " + m.LoadError); return sb.ToString(); }
        var missing = m.MissingFiles().ToList();
        if (missing.Count > 0) sb.AppendLine().AppendLine("MISSING FILES: " + string.Join(", ", missing));

        if (m.Manifest.UpkReplacements.Count > 0)
        {
            sb.AppendLine().AppendLine($"Packages ({m.Manifest.UpkReplacements.Count}):");
            foreach (string f in m.Manifest.UpkReplacements)
            {
                string size = File.Exists(Path.Combine(m.Folder, f)) ? $"{new FileInfo(Path.Combine(m.Folder, f)).Length / 1048576.0:0.0} MB" : "missing";
                string state = states == null ? (game == null ? "" : "checking…") : GameState.Describe(states[f]);
                string owner = m.Enabled && winners.TryGetValue(f, out var w) && w != m ? $"  [overridden by {w.Name}]" : "";
                sb.AppendLine($"  {f}  ({size})  {state}{owner}");
            }
        }
        Textures(sb, "Icons", m.Manifest.Replacements);
        Textures(sb, "Achievements", m.Manifest.AchievementReplacements);
        Textures(sb, "Store", m.Manifest.StoreReplacements);
        if (m.Strings.Count > 0)
        {
            sb.AppendLine().AppendLine($"Strings ({m.Strings.Count}, {string.Join(", ", m.Manifest.Languages)}):");
            foreach (var s in m.Strings.Take(200)) sb.AppendLine($"  [{s.Language}] {s.Id}: {s.Text}");
            if (m.Strings.Count > 200) sb.AppendLine($"  … {m.Strings.Count - 200} more");
        }
        if (m.Manifest.AudioPacks.Count > 0)
            sb.AppendLine().AppendLine("Sound packs: " + string.Join(", ", m.Manifest.AudioPacks));

        if (lib != null && m.Enabled)
        {
            var mine = lib.Conflicts().Where(c => c.Mods.Contains(m)).ToList();
            if (mine.Count > 0)
            {
                sb.AppendLine().AppendLine($"Conflicts ({mine.Count}):");
                foreach (var (claim, mods) in mine)
                    sb.AppendLine($"  {claim}: {(mods[0] == m ? "wins over " + string.Join(", ", mods.Skip(1).Select(x => x.Name)) : "loses to " + mods[0].Name)}");
            }
        }
        return sb.ToString();
    }

    static void Textures(StringBuilder sb, string label, List<TextureReplacement> list)
    {
        if (list.Count == 0) return;
        sb.AppendLine().AppendLine($"{label} textures ({list.Count}):");
        foreach (var r in list) sb.AppendLine($"  {r.TextureName}  <-  {r.DdsFileName}");
    }

    /// <summary>--gui-snapshot: waits for the first mod's package check, then saves the window as PNG.</summary>
    public async Task Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var t in new[] { loading, pending }) if (t != null) { try { await t; } catch { } }
        await Task.Delay(300);
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(Path.Combine(dir, "main.png"));
    }
}
