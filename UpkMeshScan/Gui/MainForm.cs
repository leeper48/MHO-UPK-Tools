using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UpkMeshScan.Gui;

/// <summary>
/// WinForms front end over the same code the CLI runs. Every write to the game folder still goes
/// through MeshImport.WriteLive / MeshImport.Revert (.bak once, verified temp, swap), and every
/// operation's console output is shown in the log panel. No write logic lives in this file.
/// </summary>
sealed partial class MainForm : Form
{
    // ---------------------------------------------------------------- settings

    sealed class Settings
    {
        public string GameFolder { get; set; } = @"G:\Program Files (x86)\Steam\steamapps\common\Marvel Heroes\UnrealEngine3\MarvelGame\CookedPCConsole";
        public string ExportFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "exports");
        public string LastPackage { get; set; } = "";
        public string LastFbx { get; set; } = "";
        public bool DarkMode { get; set; } = true;
        public string PlacementZone { get; set; } = "Hightown";
        public string PlacementFbx { get; set; } = "";
        public string PlacementSidecar { get; set; } = "";
        public string PlacementEdited { get; set; } = "";
        public string BakeFbx { get; set; } = "";
        public string TextureImage { get; set; } = "";
        public bool ShowStartOnLaunch { get; set; } = true;
        public int HelpTextSize { get; set; } = 100;
    }

    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpkMeshScan", "settings.json");
    Settings settings = LoadSettings();

    static Settings LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    void SaveSettings()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- state and controls

    Package? package;
    string packagePath = "";

    readonly TextBox gameFolder = new() { Dock = DockStyle.Fill };
    readonly ComboBox packageBox = new() { Dock = DockStyle.Fill, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    readonly Label packageInfo = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 4), MaximumSize = new Size(900, 0) };
    readonly ThemedTabControl tabs = new() { Dock = DockStyle.Fill };
    Button? themeToggle;
    Palette palette = Palette.Dark;
    readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f) };

    // Browse tab
    readonly TextBox classFilter = new() { Dock = DockStyle.Fill, PlaceholderText = "class or name filter (e.g. staticmesh, fog, texture2d)" };
    readonly ListView exports = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f) };

    // Properties tab
    readonly Label propTarget = new() { AutoSize = true, Text = "Select an export on the Browse tab.", Padding = new Padding(0, 4, 0, 4) };
    readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    readonly CheckedListBox alsoList = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly TextBox alsoFilter = new() { Width = 260, Margin = new Padding(3, 5, 3, 3), PlaceholderText = "filter the list (e.g. midtown)" };
    readonly Label alsoStatus = new() { AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
    readonly List<AlsoItem> alsoItems = new();          // everything found; the list box shows the filtered part
    const string AlsoHint = "Click \"Find matching packages\" to list other packages that have this same export.";

    sealed class AlsoItem(string file, string values)
    {
        public string File { get; } = file;
        public string Values { get; } = values;
        public bool Checked { get; set; }
        public override string ToString() => $"{File}    {Values}";
    }
    int propExport = -1;
    string propPackage = "";

    // Meshes tab
    readonly ListBox meshes = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox exportFolder = new() { Dock = DockStyle.Fill };
    readonly TextBox fbxPath = new() { Dock = DockStyle.Fill };

    // Backups tab
    readonly ListView zones = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Label zoneInfo = new() { AutoSize = true, MaximumSize = new Size(1100, 0), Margin = new Padding(3, 6, 3, 3) };
    readonly RadioButton wallsFacade = new() { Text = "Zone facade (textured walls)", AutoSize = true, Checked = true };
    readonly RadioButton wallsGrey = new() { Text = "Default grey", AutoSize = true };
    readonly ComboBox lodSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly ListView backups = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };

    readonly List<Control> busyDisabled = new();
    TabPage startPage = null!, browsePage = null!, propertiesPage = null!, meshesPage = null!, texturesPage = null!, placementsPage = null!,
        zonesPage = null!, objectsPage = null!, backupsPage = null!, toolsPage = null!;
    readonly Label gameStatus = new() { AutoSize = true, Padding = new Padding(0, 6, 8, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    readonly System.Windows.Forms.Timer gameTimer = new() { Interval = 2000 };
    bool? gameWasRunning;

    /// <summary>The header's game indicator: writes are blocked while the game runs (it holds the packages open).</summary>
    void UpdateGameStatus()
    {
        bool running = ZoneBuilds.GameRunning();
        if (gameWasRunning == running) return;
        gameWasRunning = running;
        gameStatus.Text = running ? "● Game running: close it before writing" : "● Game closed: ready to write";
        gameStatus.ForeColor = running ? Color.FromArgb(230, 140, 40) : Color.FromArgb(80, 180, 90);
    }

    /// <summary>The manual section for a tab (F1).</summary>
    string HelpAnchor(TabPage? page) =>
        page == startPage ? "start" : page == browsePage ? "browse" : page == propertiesPage ? "properties" : page == meshesPage ? "meshes"
        : page == texturesPage ? "textures" : page == placementsPage ? "placements" : page == zonesPage ? "zones" : page == objectsPage ? "objects"
        : page == backupsPage ? "backups" : page == toolsPage ? "tools" : "start";

    void ShowHelp(string anchor) => HelpForm.Show(this, anchor, settings.DarkMode, settings.HelpTextSize, v => { settings.HelpTextSize = v; SaveSettings(); });

    public MainForm(string version)
    {
        Text = $"MHO Package Modifier v{version}";
        Width = 1400; Height = 1000;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;                              // opens filling the screen; restore gives 1400 x 1000
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch (Exception ex) when (ex is ArgumentException or IOException) { }

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(BuildLog(), 0, 2);
        Controls.Add(root);

        tabs.TabPages.Add(startPage = BuildStartTab());
        tabs.TabPages.Add(browsePage = BuildBrowseTab());
        tabs.TabPages.Add(propertiesPage = BuildPropertiesTab());
        tabs.TabPages.Add(meshesPage = BuildMeshesTab());
        tabs.TabPages.Add(texturesPage = BuildTexturesTab());
        tabs.TabPages.Add(placementsPage = BuildPlacementsTab());
        tabs.TabPages.Add(zonesPage = BuildZonesTab());
        tabs.TabPages.Add(objectsPage = BuildObjectsTab());
        tabs.TabPages.Add(backupsPage = BuildBackupsTab());
        tabs.TabPages.Add(toolsPage = BuildToolsTab());
        busyDisabled.Add(tabs);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.F1) { e.Handled = true; ShowHelp(HelpAnchor(tabs.SelectedTab)); } };

        gameFolder.Text = settings.GameFolder;
        exportFolder.Text = settings.ExportFolder;
        fbxPath.Text = settings.LastFbx;

        Console.SetOut(new LogWriter(this, log));
        HandleCreated += (_, _) => SetTheme(settings.DarkMode);
        Load += (_, _) =>
        {
            FillPackageList();
            if (settings.LastPackage.Length > 0) packageBox.Text = settings.LastPackage;
            RefreshBackups();
            RefreshZones();
            RefreshStart();
            InitPlacements();
            if (!settings.ShowStartOnLaunch) tabs.SelectedTab = browsePage;
        };
        FormClosing += (_, _) => { CaptureSettings(); SaveSettings(); };
        gameTimer.Tick += (_, _) => UpdateGameStatus();
        gameTimer.Start();
    }

    // ---------------------------------------------------------------- layout

    Control BuildHeader()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var browseFolder = Btn("Browse…", () =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = gameFolder.Text };
            if (d.ShowDialog(this) == DialogResult.OK) { gameFolder.Text = d.SelectedPath; FillPackageList(); RefreshBackups(); RefreshZones(); }
        });
        var reload = Btn("Reload list", () => { FillPackageList(); RefreshBackups(); RefreshZones(); });
        t.Controls.Add(Lbl("Game folder:"), 0, 0); t.Controls.Add(gameFolder, 1, 0); t.Controls.Add(browseFolder, 2, 0); t.Controls.Add(reload, 3, 0);
        themeToggle = Btn(ThemeButtonText(settings.DarkMode), () => SetTheme(!settings.DarkMode));
        tips.SetToolTip(themeToggle, "Switch between dark and light mode (remembered next time).");
        var help = Btn("Help (F1)", () => ShowHelp(HelpAnchor(tabs.SelectedTab)));
        t.Controls.Add(NoWrap(themeToggle, help), 4, 0);

        var open = Btn("Open", OpenSelectedPackage);
        var openFile = Btn("Open file…", () =>
        {
            using var d = new OpenFileDialog { Filter = "UE3 packages (*.upk;*.umap)|*.upk;*.umap|All files|*.*", InitialDirectory = Directory.Exists(gameFolder.Text) ? gameFolder.Text : "" };
            if (d.ShowDialog(this) == DialogResult.OK) OpenPackage(d.FileName);
        });
        packageBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelectedPackage(); } };
        t.Controls.Add(Lbl("Package:"), 0, 1); t.Controls.Add(packageBox, 1, 1); t.Controls.Add(open, 2, 1); t.Controls.Add(openFile, 3, 1);
        t.Controls.Add(packageInfo, 1, 2); t.SetColumnSpan(packageInfo, 3);
        t.Controls.Add(gameStatus, 4, 1);
        UpdateGameStatus();
        busyDisabled.AddRange([browseFolder, reload, open, openFile, packageBox, gameFolder]);
        return t;
    }

    Control BuildLog()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var clear = Btn("Clear log", () => log.Clear());
        clear.Anchor = AnchorStyles.Top;
        t.Controls.Add(log, 0, 0); t.Controls.Add(clear, 1, 0);
        return t;
    }

    TabPage BuildBrowseTab()
    {
        var page = new TabPage("Browse");
        exports.Columns.Add("#", 60); exports.Columns.Add("Class", 200); exports.Columns.Add("Size", 90, HorizontalAlignment.Right); exports.Columns.Add("Path", 600);
        exports.SelectedIndexChanged += (_, _) => ShowSelectedExport();
        // Double-click (or Enter) on a row = "Edit properties →".
        exports.ItemActivate += (_, _) => { if (SelectedExport() is int i) { LoadProperties(i); tabs.SelectedTab = propertiesPage; } };
        classFilter.TextChanged += (_, _) => FillExports();

        var buttons = Flow(
            Btn("Edit properties →", () => { if (SelectedExport() is int i) { LoadProperties(i); tabs.SelectedTab = propertiesPage; } }),
            Btn("Where does this mesh come from?", () => { if (package != null) Run("Import sources", () => ImportSources.Run(gameFolder.Text, packagePath)); }),
            Btn("Find name in folder", () => { if (SelectedExport() is int i) { string n = package!.Exports[i].ObjectName; Run($"Find '{n}'", () => FindName.Run(gameFolder.Text, n, false)); } }),
            Btn("Export texture(s)", () =>
            {
                if (package == null) return;
                string? filter = SelectedExport() is int i && package.ClassOf(package.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) ? package.Exports[i].ObjectName : null;
                string dir = Path.Combine(exportFolder.Text, "textures", Path.GetFileNameWithoutExtension(packagePath));
                Run(filter == null ? "Export all textures" : $"Export texture {filter}", () => TextureExport.Run(packagePath, filter, dir));
            }),
            Btn("Preview", () =>
            {
                if (package == null || SelectedExport() is not int i) return;
                string cls = package.ClassOf(package.Exports[i]);
                if (cls.Equals("Texture2D", StringComparison.OrdinalIgnoreCase)) ShowTextureOnTab(package.PathOf(package.Exports[i]));
                else if (cls.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
                {
                    tabs.SelectedTab = meshesPage;
                    for (int k = 0; k < meshes.Items.Count; k++) if (meshes.Items[k] is MeshItem m && m.Index == i) { meshes.SelectedIndex = k; break; }
                }
                else Log("Preview shows a Texture2D or a StaticMesh: select one (filter the list by texture2d or staticmesh).");
            }),
            Btn("What does it need?", () => { if (SelectedExport() is int i) RunCommand("Dependencies", ["--export-deps", packagePath, package!.PathOf(package.Exports[i])], false); }),
            Btn("Save full dump", () => { if (SelectedExport() is int i) RunCommand("Dump export", ["--dump-export", packagePath, package!.PathOf(package.Exports[i]), "--out", Path.Combine(exportFolder.Text, "dumps")], false, () => OpenFolder(Path.Combine(exportFolder.Text, "dumps"))); }),
            Btn("Open in Tools…", () => tabs.SelectedTab = toolsPage));

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(classFilter, 0, 0); left.Controls.Add(exports, 0, 1); left.Controls.Add(buttons, 0, 2);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(details);
        page.Controls.Add(split);
        // Set after parenting (CLAUDE.md WinForms note); list gets 55% so class and path are readable.
        Shown += (_, _) => { if (split.Width > 400) split.SplitterDistance = (int)(split.Width * 0.55); };
        return page;
    }

    TabPage BuildPropertiesTab()
    {
        var page = new TabPage("Properties");
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Property", ReadOnly = true, FillWeight = 35 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", ReadOnly = true, FillWeight = 15 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value (editable; double-click a colour to pick)", FillWeight = 35 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Original", HeaderText = "Current in file", ReadOnly = true, FillWeight = 25 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Bak", HeaderText = "Original (.bak)", ReadOnly = true, FillWeight = 25,
            ToolTipText = "The value in this package's .bak, i.e. the original before any edits. Empty when the package has no .bak (it's still the original)." });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Revert", HeaderText = "", FillWeight = 6, FlatStyle = FlatStyle.Flat,
            DefaultCellStyle = new DataGridViewCellStyle { Font = new Font("Segoe UI Symbol", 13f, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter },
            ToolTipText = "Put the original (.bak) value back into Value. Nothing is written until Dry run / Apply." });
        grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["Revert"].Index) RevertRow(grid.Rows[e.RowIndex]); };
        grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) MarkChanged(grid.Rows[e.RowIndex]); };
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) PickColor(grid.Rows[e.RowIndex]); };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var also = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        also.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); also.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        also.RowStyles.Add(new RowStyle(SizeType.AutoSize)); also.RowStyles.Add(new RowStyle(SizeType.Absolute, 90)); also.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        also.Controls.Add(Lbl("Also apply to:"), 0, 0);
        also.Controls.Add(NoWrap(Btn("Find matching packages", FindMatchingPackages), alsoFilter,
            Btn("Check all shown", () => SetShownChecks(true)),
            Btn("Uncheck all", () => { alsoItems.ForEach(i => i.Checked = false); FillAlsoList(); })), 1, 0);
        also.Controls.Add(alsoList, 1, 1);
        also.Controls.Add(alsoStatus, 1, 2);
        alsoStatus.Text = AlsoHint;
        alsoFilter.TextChanged += (_, _) => FillAlsoList();
        alsoList.ItemCheck += (_, e) =>
        {
            if (alsoList.Items[e.Index] is AlsoItem it) it.Checked = e.NewValue == CheckState.Checked;
            BeginInvoke(UpdateAlsoStatus);
        };
        t.Controls.Add(propTarget, 0, 0); t.Controls.Add(grid, 0, 1); t.Controls.Add(also, 0, 2);
        t.Controls.Add(NoWrap(
            Btn("Dry run (writes to import_out, game untouched)", () => ApplyProperties(dryRun: true)),
            Btn("Apply to game file(s)…", () => ApplyProperties(dryRun: false)),
            Btn("Reset edits", () => { if (propExport >= 0) LoadProperties(propExport); })), 0, 3);
        page.Controls.Add(t);
        return page;
    }

    /// <summary>Background scan of the game folder for packages that have an export with the same path and class.</summary>
    void FindMatchingPackages()
    {
        if (package == null || propExport < 0) { Log("Pick an export first (Browse tab, Edit properties ->)."); return; }
        var e = package.Exports[propExport];
        string path = package.PathOf(e), cls = package.ClassOf(e), objectName = e.ObjectName;
        string baseName = System.Text.RegularExpressions.Regex.Replace(objectName, @"_\d+$", "");
        string self = packagePath, folder = gameFolder.Text;
        var editable = (PropertyEdit.ReadProperties(package, propExport) ?? new()).Where(p => p.Editable).Select(p => p.Name).ToList();
        var found = new System.Collections.Concurrent.ConcurrentBag<AlsoItem>();
        Run($"Find packages with {path}", () =>
        {
            var files = Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                .Where(f => !Program.IsBackupName(f) && !string.Equals(Path.GetFullPath(f), self, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Parallel.ForEach(files, f =>
            {
                try
                {
                    var pkg = Package.Open(f);
                    // Quick skip. The name table stores "foo_0" as "foo" plus an instance number, so check the base name.
                    if (!pkg.Names.Contains(baseName, StringComparer.OrdinalIgnoreCase)) return;
                    for (int i = 0; i < pkg.Exports.Length; i++)
                    {
                        var x = pkg.Exports[i];
                        if (!x.ObjectName.Equals(objectName, StringComparison.OrdinalIgnoreCase)
                            || !pkg.ClassOf(x).Equals(cls, StringComparison.OrdinalIgnoreCase)
                            || !pkg.PathOf(x).Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                        var props = PropertyEdit.ReadProperties(pkg, i) ?? new();
                        string values = string.Join("  ", props.Where(p => editable.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => $"{p.Name}={p.Value}"));
                        var missing = editable.Where(n => !props.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (missing.Count > 0) values += $"   (not stored: {string.Join(", ", missing)})";
                        found.Add(new AlsoItem(Path.GetFileName(f), values));
                        break;
                    }
                }
                catch (Exception ex) when (ex is PackageFormatException or IOException or InvalidDataException or ArgumentOutOfRangeException) { }
            });
            Console.WriteLine($"  {found.Count} other package(s) have {cls} {path}");
            return 0;
        }, after: () =>
        {
            string prefix = Path.GetFileName(self).Split('_')[0] + "_";
            alsoItems.Clear();
            alsoItems.AddRange(found.OrderBy(i => !i.File.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase));
            foreach (var i in alsoItems) i.Checked = i.File.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            FillAlsoList();
        });
    }

    void FillAlsoList()
    {
        alsoList.BeginUpdate();
        alsoList.Items.Clear();
        string f = alsoFilter.Text.Trim();
        foreach (var item in alsoItems.Where(i => f.Length == 0 || i.ToString().Contains(f, StringComparison.OrdinalIgnoreCase)))
            alsoList.Items.Add(item, item.Checked);
        alsoList.EndUpdate();
        UpdateAlsoStatus();
    }

    void SetShownChecks(bool value)
    {
        foreach (var o in alsoList.Items) if (o is AlsoItem it) it.Checked = value;
        FillAlsoList();
    }

    void UpdateAlsoStatus()
    {
        int n = alsoItems.Count(i => i.Checked);
        if (alsoItems.Count == 0) { alsoStatus.Text = AlsoHint; return; }
        string names = string.Join(", ", alsoItems.Where(i => i.Checked).Select(i => i.File).Take(6)) + (n > 6 ? ", ..." : "");
        alsoStatus.Text = $"{alsoItems.Count} package(s) have this export; {n} checked{(n > 0 ? ": " + names : "")}. Same-prefix packages are pre-checked.";
    }

    TabPage BuildMeshesTab()
    {
        var page = new TabPage("Meshes");
        // One column: what this tab is for, the mesh list (it takes the free height), then export and import.
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Hint("Click a mesh to see it. To replace it: export, edit in Blender, import (F1: the rules). To change placed buildings, use Placements.", 540), 0, 0);
        t.Controls.Add(Lbl("StaticMeshes in this package (click one to see it):"), 0, 1);
        t.Controls.Add(meshes, 0, 2);

        var g = new FieldGrid();
        g.Row("Export folder:", exportFolder, NoWrap(
            Btn("Browse…", () => { using var d = new FolderBrowserDialog { SelectedPath = exportFolder.Text }; if (d.ShowDialog(this) == DialogResult.OK) exportFolder.Text = d.SelectedPath; }),
            Btn("Open folder", () => OpenFolder(exportFolder.Text))));
        g.Full(Flow(Btn("Export to FBX (+ textures)", ExportSelectedMesh),
            Btn("UV info", () => { if (SelectedMesh() is { } m) RunCommand($"UV info {m.Name}", ["--uv-info", packagePath, m.Path], false); }),
            Btn("Round-trip self-test", () => { if (SelectedMesh() is { } m) RunCommand($"Round-trip test {m.Name}", ["--verify-import-roundtrip", packagePath, m.Path], false); })));
        g.Row("FBX to import:", fbxPath, Btn("Browse…", () =>
        {
            using var d = new OpenFileDialog { Filter = "FBX (*.fbx)|*.fbx|All files|*.*", FileName = fbxPath.Text };
            if (d.ShowDialog(this) == DialogResult.OK) fbxPath.Text = d.FileName;
        }));
        g.Full(Flow(
            Btn("Dry run import", () => ImportSelectedMesh(dryRun: true)),
            Btn("Import into game file…", () => ImportSelectedMesh(dryRun: false))));
        t.Controls.Add(g, 0, 3);
        meshes.SelectedIndexChanged += (_, _) => { if (meshes.SelectedItem is MeshItem m) PreviewMesh(m); };
        // Left: the list and the export/import controls; right: the 3D view of the selected mesh.
        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(t);
        split.Panel2.Controls.Add(meshViewer);
        page.Controls.Add(split);
        Shown += (_, _) => { if (split.Width > 600) split.SplitterDistance = (int)(split.Width * 0.42); };
        return page;
    }

    TabPage BuildBackupsTab()
    {
        var page = new TabPage("Backups");
        backups.Columns.Add("Package", 380); backups.Columns.Add("Status", 170); backups.Columns.Add("Live size", 110, HorizontalAlignment.Right);
        backups.Columns.Add("Live date", 140); backups.Columns.Add("Undo / redo", 100);
        backups.Columns.Add("Original (.bak) size", 140, HorizontalAlignment.Right);
        backups.Resize += (_, _) => FillLastColumn(backups);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Hint("Every package with its original kept as .bak. MODIFIED = the game file differs from its original. Select one: Undo steps back one write " +
            "(Ctrl+Z), Redo steps forward (Ctrl+Y), Revert puts the original back. Undo / redo shows the steps available."), 0, 0);
        t.Controls.Add(backups, 0, 1);
        t.Controls.Add(Flow(
            Btn("Refresh", RefreshBackups),
            Btn("Undo last change", () => UndoRedoSelected(undo: true)),
            Btn("Redo", () => UndoRedoSelected(undo: false)),
            Btn("Revert selected to original…", RevertSelected),
            Btn("Show history", () => { if (backups.SelectedItems.Count == 1) RunCommand("History", ["--history", Path.Combine(gameFolder.Text, backups.SelectedItems[0].Text)], false); }),
            Btn("Open selected package", () => { if (backups.SelectedItems.Count == 1) OpenPackage(Path.Combine(gameFolder.Text, backups.SelectedItems[0].Text)); })), 0, 2);
        backups.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Z) { UndoRedoSelected(undo: true); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Y) { UndoRedoSelected(undo: false); e.Handled = true; }
        };
        page.Controls.Add(t);
        return page;
    }

    TabPage BuildZonesTab()
    {
        var page = new TabPage("Zones");
        zones.Columns.Add("Zone", 160); zones.Columns.Add("Main level package", 260); zones.Columns.Add("Status", 200);
        zones.Columns.Add("Undo / redo", 100);
        zones.Resize += (_, _) => FillLastColumn(zones);
        zones.SelectedIndexChanged += (_, _) => ShowZoneInfo();
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 6 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Absolute, 175));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.AutoScroll = true;
        t.Controls.Add(Lbl("Rebuild a zone's main level from its original with the full placeholder recipe (sky, distant building boxes, ground, water). " +
            "One verified write: Undo on the Backups tab takes the whole rebuild back."), 0, 0);
        t.Controls.Add(zones, 0, 1);
        t.Controls.Add(zoneInfo, 0, 2);
        lodSize.Items.AddRange(["Original", "2048", "1024", "512", "256"]);
        lodSize.SelectedItem = "512";
        t.Controls.Add(Flow(Lbl("Walls:"), wallsFacade, wallsGrey, Lbl("   LOD textures:"), lodSize), 0, 3);
        tips.SetToolTip(lodSize, "Largest size for the distant LOD textures (building atlases, baked ground plane). They're only seen far away: 512 keeps the package small. Original = as supplied.");
        tips.SetToolTip(wallsFacade, "Building walls use the zone's own facade texture (Hightown: generated night windows). Roofs stay grey.");
        tips.SetToolTip(wallsGrey, "Building boxes are flat grey all over (the original placeholder look).");
        t.Controls.Add(Flow(
            Btn("Refresh", RefreshZones),
            Btn("Dry run (game untouched)", () => BuildSelectedZone(dryRun: true)),
            Btn("Build and write to game…", () => BuildSelectedZone(dryRun: false))), 0, 4);
        t.Controls.Add(BuildBakeSection(), 0, 5);
        page.Controls.Add(t);
        return page;
    }

    void RefreshZones()
    {
        zones.Items.Clear();
        foreach (var z in ZoneBuilds.Zones)
        {
            string live = Path.Combine(gameFolder.Text, z.Package);
            string status = !File.Exists(live) ? "not found in the game folder"
                : !File.Exists(live + ".bak") ? "original (never modified)"
                : new FileInfo(live).Length == new FileInfo(live + ".bak").Length && File.ReadAllBytes(live).AsSpan().SequenceEqual(File.ReadAllBytes(live + ".bak")) ? "original (same as .bak)"
                : "modified";
            var (u, r) = File.Exists(live) ? History.Counts(live) : (0, 0);
            string shown = ZonePresets.All.FirstOrDefault(p => p.Name == z.Name)?.Display ?? z.Name;
            zones.Items.Add(new ListViewItem([shown, z.Package, status, u + r == 0 ? "" : $"{u} / {r}"]) { Tag = z });
        }
        FitColumns(zones);
        if (zones.Items.Count > 0 && zones.SelectedItems.Count == 0) zones.Items[0].Selected = true;
        ShowZoneInfo();
    }

    void ShowZoneInfo() => zoneInfo.Text = zones.SelectedItems.Count == 1 && zones.SelectedItems[0].Tag is ZoneBuilds.Zone z ? z.Description : "";

    void BuildSelectedZone(bool dryRun)
    {
        if (zones.SelectedItems.Count != 1 || zones.SelectedItems[0].Tag is not ZoneBuilds.Zone z) { Log("Select a zone first."); return; }
        var walls = wallsGrey.Checked ? ZoneBuilds.Walls.Grey : ZoneBuilds.Walls.Facade;
        string wallsText = walls == ZoneBuilds.Walls.Grey ? "grey walls" : "zone facade";
        if (!dryRun)
        {
            if (ZoneBuilds.GameRunning()) { Log("The game is running; close it first. Nothing written."); return; }
            if (!Confirm($"Rebuild {z.Package} ({z.Name}, {wallsText}) from its original and write it to the game folder?\n\n" +
                "The .bak is kept; Undo on the Backups tab restores the current version.")) return;
        }
        int lod = lodSize.SelectedItem is string ls && int.TryParse(ls, out int n) ? n : 0;
        Run($"{(dryRun ? "Dry run" : "Build")} zone {z.Name} ({wallsText}, LOD textures {(lod > 0 ? lod.ToString() : "original")})", () => ZoneBuilds.Build(z.Name, gameFolder.Text, walls, dryRun, lod), after: () =>
        {
            RefreshZones();
            RefreshBackups();
            if (string.Equals(Path.GetFullPath(Path.Combine(gameFolder.Text, z.Package)), packagePath, StringComparison.OrdinalIgnoreCase)) ReopenPackage();
        }, writes: !dryRun);
    }

    /// <summary>Undo / redo the selected package's last change through the tool's history (verified restore; the .bak is untouched).</summary>
    void UndoRedoSelected(bool undo)
    {
        if (backups.SelectedItems.Count != 1) { Log("Select a package in the Backups list first."); return; }
        string live = Path.Combine(gameFolder.Text, backups.SelectedItems[0].Text);
        var (u, r) = History.Counts(live);
        if ((undo ? u : r) == 0) { Log($"{(undo ? "Nothing to undo" : "Nothing to redo")} for {Path.GetFileName(live)}."); return; }
        Run($"{(undo ? "Undo" : "Redo")} {Path.GetFileName(live)}", () => undo ? History.Undo(live, false) : History.Redo(live, false), after: () =>
        {
            RefreshBackups();
            if (string.Equals(Path.GetFullPath(live), packagePath, StringComparison.OrdinalIgnoreCase)) ReopenPackage();
        }, writes: true);
    }

    // ---------------------------------------------------------------- package

    void FillPackageList()
    {
        packageBox.Items.Clear();
        if (!Directory.Exists(gameFolder.Text)) { packageInfo.Text = "Game folder not found."; return; }
        var names = Directory.EnumerateFiles(gameFolder.Text)
            .Where(f => f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Program.IsBackupName(f))
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        packageBox.Items.AddRange(names!);
        packageInfo.Text = $"{names.Length:N0} packages in the folder (bak/copy files hidden). Type to search, then Open.";
    }

    void OpenSelectedPackage()
    {
        string name = packageBox.Text.Trim();
        if (name.Length == 0) return;
        OpenPackage(Path.IsPathRooted(name) ? name : Path.Combine(gameFolder.Text, name));
    }

    void OpenPackage(string path)
    {
        if (!File.Exists(path)) { Log($"Not found: {path}"); return; }
        try
        {
            package = Package.Open(path);
            packagePath = Path.GetFullPath(path);
            packageBox.Text = Path.GetFileName(path);
            settings.LastPackage = Path.GetFileName(path);
            var fi = new FileInfo(path);
            bool stock = fi.LastWriteTime.Date == new DateTime(2024, 3, 14);
            bool hasBak = File.Exists(path + ".bak");
            packageInfo.Text = $"{fi.Name}: {fi.Length:N0} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm} ({(stock ? "stock date" : "modified")}), " +
                               $"{package.Exports.Length:N0} exports, v{package.FileVersion}/L{package.LicenseeVersion}, {(package.Chunks.Count > 0 ? "compressed" : "uncompressed")}" +
                               (hasBak ? ", has .bak" : "");
            FillExports();
            FillMeshes();
            FillTextures();
            details.Clear();
            Log($"Opened {fi.Name}: {package.Exports.Length:N0} exports, {meshes.Items.Count} StaticMesh(es){(hasBak ? ", has .bak" : "")}");
        }
        catch (Exception ex) when (ex is PackageFormatException or IOException or InvalidDataException)
        {
            package = null;
            Log($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    void ReopenPackage()
    {
        if (packagePath.Length == 0) return;
        int? keep = SelectedExport();
        OpenPackage(packagePath);
        if (keep is int k) SelectExport(k);
        RefreshBackups();
    }

    void FillExports()
    {
        exports.BeginUpdate();
        exports.Items.Clear();
        if (package != null)
        {
            string f = classFilter.Text.Trim();
            var items = new List<ListViewItem>();
            for (int i = 0; i < package.Exports.Length; i++)
            {
                var e = package.Exports[i];
                string cls = package.ClassOf(e), path = package.PathOf(e);
                if (f.Length > 0 && !cls.Contains(f, StringComparison.OrdinalIgnoreCase) && !path.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new ListViewItem([(i + 1).ToString(), cls, e.SerialSize.ToString("N0"), path]) { Tag = i });
            }
            exports.Items.AddRange(items.ToArray());
            exports.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            exports.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.HeaderSize);
            if (exports.Columns[2].Width < 70) exports.Columns[2].Width = 70;
        }
        exports.EndUpdate();
    }

    void FillMeshes()
    {
        meshes.Items.Clear();
        meshRequest++;
        meshViewer.ShowMessage("Select a mesh in the list to see it here.");
        if (package == null) return;
        for (int i = 0; i < package.Exports.Length; i++)
            if (package.ClassOf(package.Exports[i]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase))
                meshes.Items.Add(new MeshItem(i, package.PathOf(package.Exports[i]), package.Exports[i].ObjectName, package.Exports[i].SerialSize));
    }

    sealed record MeshItem(int Index, string Path, string Name, int Size)
    {
        public override string ToString() => $"{Name}   ({Size:N0} B)   {Path}";
    }

    int? SelectedExport() => exports.SelectedItems.Count == 1 ? (int)exports.SelectedItems[0].Tag! : null;

    void SelectExport(int index)
    {
        foreach (ListViewItem item in exports.Items)
            if ((int)item.Tag! == index) { item.Selected = true; item.EnsureVisible(); return; }
    }

    void ShowSelectedExport()
    {
        if (package == null || SelectedExport() is not int i) return;
        try { details.Text = ExportDump.DescribeExport(package, i, packagePath).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"); }
        catch (Exception ex) when (ex is PackageFormatException or ArgumentOutOfRangeException) { details.Text = $"Could not describe: {ex.Message}"; }
    }

    // ---------------------------------------------------------------- properties

    void LoadProperties(int index)
    {
        if (package == null) return;
        if (propExport != index || !string.Equals(propPackage, packagePath, StringComparison.OrdinalIgnoreCase)) { alsoItems.Clear(); FillAlsoList(); }
        propExport = index;
        propPackage = packagePath;
        grid.Rows.Clear();
        var list = PropertyEdit.ReadProperties(package, index);
        propTarget.Text = $"{Path.GetFileName(packagePath)} :: {package.PathOf(package.Exports[index])} ({package.ClassOf(package.Exports[index])})";
        if (list == null) { propTarget.Text += "  —  properties couldn't be read"; return; }
        var original = BakValues(package.PathOf(package.Exports[index]), package.ClassOf(package.Exports[index]), out string bakNote);
        foreach (var p in list)
        {
            string bak = original == null ? "" : original.TryGetValue(p.Name, out var v) ? v : "(not stored)";
            int r = grid.Rows.Add(p.Name, p.Type, p.Value, p.Value, bak, "");
            var row = grid.Rows[r];
            row.Tag = p.Type;
            row.Cells["Value"].ReadOnly = !p.Editable;
            if (!p.Editable) row.DefaultCellStyle.ForeColor = palette.Subtle;
            Swatch(row.Cells["Original"], p.Type, p.Value);
            Swatch(row.Cells["Value"], p.Type, p.Value);
            if (original != null && bak != "(not stored)") Swatch(row.Cells["Bak"], p.Type, bak);
            UpdateRevert(row);
        }
        if (bakNote.Length > 0) propTarget.Text += $"  —  {bakNote}";
        if (list.Count == 0) propTarget.Text += "  —  no stored properties (all at defaults)";
        else if (!list.Any(p => p.Editable)) propTarget.Text += "  —  nothing editable here (only float/int are supported)";
    }

    /// <summary>
    /// The export's property values in this package's .bak (the original). Null when there's no .bak
    /// (the live file is the original) or the export isn't in it.
    /// </summary>
    Dictionary<string, string>? BakValues(string exportPath, string exportClass, out string note)
    {
        note = "";
        string bak = packagePath + ".bak";
        if (!File.Exists(bak)) { note = "no .bak: this package is still its original"; return null; }
        try
        {
            if (bakPackagePath != bak) { bakPackage = Package.Open(bak); bakPackagePath = bak; }
            var p = bakPackage!;
            for (int i = 0; i < p.Exports.Length; i++)
            {
                if (!p.PathOf(p.Exports[i]).Equals(exportPath, StringComparison.OrdinalIgnoreCase) || !p.ClassOf(p.Exports[i]).Equals(exportClass, StringComparison.OrdinalIgnoreCase)) continue;
                var list = PropertyEdit.ReadProperties(p, i);
                if (list == null) break;
                return list.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);
            }
            note = "export not found in the .bak";
        }
        catch (Exception ex) when (ex is PackageFormatException or IOException or InvalidDataException) { note = $".bak couldn't be read: {ex.Message}"; }
        return null;
    }

    Package? bakPackage;
    string bakPackagePath = "";

    /// <summary>Shows ↺ on a row whose value (edited or in the file) differs from the .bak, blank otherwise.</summary>
    void UpdateRevert(DataGridViewRow row)
    {
        string bak = row.Cells["Bak"].Value?.ToString() ?? "";
        bool can = bak.Length > 0 && bak != "(not stored)" && !row.Cells["Value"].ReadOnly && !SameValue(row.Cells["Value"].Value?.ToString(), bak);
        row.Cells["Revert"].Value = can ? "↺" : "";
        row.Cells["Revert"].ToolTipText = can ? $"Revert to the original: {bak}" : "";
    }

    void RevertRow(DataGridViewRow row)
    {
        if ((row.Cells["Revert"].Value?.ToString() ?? "") != "↺") return;
        grid.EndEdit();
        row.Cells["Value"].Value = row.Cells["Bak"].Value?.ToString();
        MarkChanged(row);
        Log($"{row.Cells["Name"].Value}: set back to the original {row.Cells["Bak"].Value} (not written yet: Dry run / Apply)");
    }

    /// <summary>Same value, allowing for number formatting ("1" vs "1.0") and colour text differences.</summary>
    static bool SameValue(string? a, string? b)
    {
        a ??= ""; b ??= "";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (float.TryParse(a, System.Globalization.NumberStyles.Float, inv, out float fa) && float.TryParse(b, System.Globalization.NumberStyles.Float, inv, out float fb)) return fa == fb;
        if (PropertyEdit.TryParseColor(a, -1, float.MaxValue, out var ca) && PropertyEdit.TryParseColor(b, -1, float.MaxValue, out var cb)) return ca.SequenceEqual(cb);
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    void MarkChanged(DataGridViewRow row)
    {
        UpdateRevert(row);
        bool changed = !Equals(row.Cells["Value"].Value?.ToString(), row.Cells["Original"].Value?.ToString());
        if (row.Tag is "color" or "linearcolor")
        {
            // Colour cells show the colour itself; a changed one gets a bold yellow border-ish marker via the text.
            Swatch(row.Cells["Value"], (string)row.Tag, row.Cells["Value"].Value?.ToString() ?? "");
            row.Cells["Name"].Style.BackColor = changed ? palette.Changed : Color.Empty;
            return;
        }
        row.Cells["Value"].Style.BackColor = changed ? palette.Changed : Color.Empty;
    }

    /// <summary>Paints a colour cell with its colour (text stays readable: black or white by brightness).</summary>
    static void Swatch(DataGridViewCell cell, string type, string text)
    {
        if (type is not ("color" or "linearcolor")) return;
        if (!ToColor(type, text, out Color c)) return;
        cell.Style.BackColor = c;
        cell.Style.SelectionBackColor = c;
        bool dark = c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 140;
        cell.Style.ForeColor = cell.Style.SelectionForeColor = dark ? Color.White : Color.Black;
    }

    static bool ToColor(string type, string text, out Color c)
    {
        c = Color.Empty;
        float max = type == "color" ? 255 : float.MaxValue;
        if (!PropertyEdit.TryParseColor(text, 0, max, out var v)) return false;
        int To255(float x) => type == "color" ? (int)x : (int)Math.Clamp(MathF.Round(x * 255f), 0, 255);
        c = Color.FromArgb(255, To255(v[0]), To255(v[1]), To255(v[2]));
        return true;
    }

    /// <summary>Double-click on a colour row: Windows colour picker; alpha is kept as it was.</summary>
    void PickColor(DataGridViewRow row)
    {
        if (row.Tag is not ("color" or "linearcolor") || row.Cells["Value"].ReadOnly) return;
        string type = (string)row.Tag, current = row.Cells["Value"].Value?.ToString() ?? "";
        float max = type == "color" ? 255 : float.MaxValue;
        if (!PropertyEdit.TryParseColor(current, 0, max, out var v)) return;
        ToColor(type, current, out Color start);
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = start };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var c = dlg.Color;
        string text = type == "color"
            ? $"R{c.R} G{c.G} B{c.B} A{v[3]:0}"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"R{c.R / 255f:0.###} G{c.G / 255f:0.###} B{c.B / 255f:0.###} A{v[3]:0.###}");
        grid.EndEdit();
        row.Cells["Value"].Value = text;
        MarkChanged(row);
    }

    void ApplyProperties(bool dryRun)
    {
        if (package == null || propExport < 0) { Log("Pick an export on the Browse tab first (Edit properties →)."); return; }
        grid.EndEdit();
        var changes = grid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.Cells["Value"].ReadOnly && !Equals(r.Cells["Value"].Value?.ToString(), r.Cells["Original"].Value?.ToString()))
            .Select(r => (r.Cells["Name"].Value!.ToString()!, r.Cells["Value"].Value?.ToString() ?? ""))
            .ToList();
        if (changes.Count == 0) { Log("No values changed."); return; }

        string exportPath = package.PathOf(package.Exports[propExport]);
        var targets = new List<string> { packagePath };
        foreach (var extra in alsoItems.Where(i => i.Checked))
            targets.Add(Path.Combine(gameFolder.Text, extra.File));

        string summary = string.Join("\n", changes.Select(c => $"  {c.Item1} = {c.Item2}"));
        if (!dryRun && !Confirm($"Write these changes into the game file(s)?\n\n{summary}\n\nPackages:\n  {string.Join("\n  ", targets.Select(Path.GetFileName))}\n\nA .bak of each original is kept; Backups tab reverts."))
            return;

        Run(dryRun ? "Property dry run" : "Apply properties", () =>
        {
            int worst = 0;
            foreach (string t in targets) worst = Math.Max(worst, PropertyEdit.Run(t, exportPath, changes, dryRun));
            return worst;
        }, after: () => { if (!dryRun) { bakPackagePath = ""; ReopenPackage(); LoadProperties(propExport); } }, writes: !dryRun);
    }

    // ---------------------------------------------------------------- meshes

    MeshItem? SelectedMesh()
    {
        if (meshes.SelectedItem is MeshItem m) return m;
        Log("Select a StaticMesh in the list first.");
        return null;
    }

    void ExportSelectedMesh()
    {
        if (SelectedMesh() is not { } m) return;
        string dir = exportFolder.Text;
        Run($"Export {m.Name}", () => StaticMeshExport.Run(packagePath, m.Path, dir), after: () => Log($"Exported to {dir}"));
    }

    void ImportSelectedMesh(bool dryRun)
    {
        if (SelectedMesh() is not { } m) return;
        string fbx = fbxPath.Text.Trim();
        if (!File.Exists(fbx)) { Log($"FBX not found: {fbx}"); return; }
        settings.LastFbx = fbx;
        if (!dryRun && !Confirm($"Import\n  {Path.GetFileName(fbx)}\ninto\n  {Path.GetFileName(packagePath)} :: {m.Name}\n\nThe mesh's collision will be empty. A .bak of the original is kept; Backups tab reverts.\n\nTip: run the dry run first."))
            return;
        Run(dryRun ? $"Dry run import {m.Name}" : $"Import {m.Name}", () => MeshImport.Import(packagePath, m.Path, fbx, dryRun, null),
            after: () => { if (!dryRun) ReopenPackage(); }, writes: !dryRun);
    }

    // ---------------------------------------------------------------- backups

    void RefreshBackups()
    {
        backups.BeginUpdate();
        backups.Items.Clear();
        if (Directory.Exists(gameFolder.Text))
        {
            foreach (string bak in Directory.EnumerateFiles(gameFolder.Text, "*.bak").Where(b => b.EndsWith(".upk.bak", StringComparison.OrdinalIgnoreCase) || b.EndsWith(".umap.bak", StringComparison.OrdinalIgnoreCase)).OrderBy(b => b))
            {
                string live = bak[..^4];
                var bi = new FileInfo(bak);
                string status; string size = "", date = "";
                if (!File.Exists(live)) status = "live file missing";
                else
                {
                    var li = new FileInfo(live);
                    size = li.Length.ToString("N0"); date = li.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
                    status = li.Length != bi.Length ? "MODIFIED" : SameContent(live, bak) ? "same as original" : "MODIFIED";
                }
                var (undoSteps, redoSteps) = History.Counts(live);
                var item = new ListViewItem([Path.GetFileName(live), status, size, date, undoSteps + redoSteps == 0 ? "" : $"{undoSteps} / {redoSteps}", bi.Length.ToString("N0")]);
                if (status == "MODIFIED") item.Font = new Font(backups.Font, FontStyle.Bold);
                backups.Items.Add(item);
            }
        }
        backups.EndUpdate();
        FitColumns(backups);
    }

    static bool SameContent(string a, string b)
    {
        using var fa = File.OpenRead(a); using var fb = File.OpenRead(b);
        var ba = new byte[1 << 16]; var bb = new byte[1 << 16];
        while (true)
        {
            int na = fa.Read(ba), nb = fb.Read(bb);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    void RevertSelected()
    {
        if (backups.SelectedItems.Count != 1) { Log("Select a package in the Backups list first."); return; }
        string live = Path.Combine(gameFolder.Text, backups.SelectedItems[0].Text);
        if (!Confirm($"Restore {Path.GetFileName(live)} from its .bak (the original)?\n\nThe .bak is kept.")) return;
        Run($"Revert {Path.GetFileName(live)}", () => MeshImport.Revert(live), after: () =>
        {
            RefreshBackups();
            if (string.Equals(Path.GetFullPath(live), packagePath, StringComparison.OrdinalIgnoreCase)) ReopenPackage();
        }, writes: true);
    }

    // ---------------------------------------------------------------- running work

    void Run(string title, Func<int> work, Action? after = null, bool writes = false)
    {
        if (writes && ZoneBuilds.GameRunning())
        {
            Log($"{title}: the game is running. Close it first (it keeps the packages open); nothing was written.");
            MessageBox.Show(this, "The game is running. Close it first: it keeps the packages open, and a changed package only loads on the next start.\n\nNothing was written.",
                "Close the game first", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        CaptureSettings();
        History.Label = title;                                            // what a write in this action is called in the undo history
        Log($"=== {title} ===");
        foreach (var c in busyDisabled) c.Enabled = false;
        UseWaitCursor = true;
        Task.Run(() =>
        {
            try { return work(); }
            catch (Exception ex) { Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}"); return 1; }
        }).ContinueWith(t =>
        {
            foreach (var c in busyDisabled) c.Enabled = true;
            UseWaitCursor = false;
            Log(t.Result == 0 ? $"=== {title}: done ===" : $"=== {title}: finished with problems (see above) ===");
            after?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void CaptureSettings()
    {
        settings.GameFolder = gameFolder.Text;
        settings.ExportFolder = exportFolder.Text;
        settings.LastFbx = fbxPath.Text;
        CapturePlacementSettings();
    }

    void Log(string line) => Console.WriteLine(line);

    bool Confirm(string message) => MessageBox.Show(this, message, "Confirm", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;

    static void OpenFolder(string dir)
    {
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    readonly ToolTip tips = new() { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };

    /// <summary>Hover text for every button, by its caption. Buttons without an entry get none.</summary>
    static readonly Dictionary<string, string> ButtonTips = new()
    {
        ["Browse…"] = "Choose a folder or file.",
        ["Reload list"] = "Re-read the package list from the game folder and refresh the Backups tab.",
        ["Open"] = "Open the package typed or picked in the box (pressing Enter does the same).",
        ["Open file…"] = "Open any .upk/.umap from disk, including ones outside the game folder.",
        ["Clear log"] = "Clear the log panel. Doesn't affect any file.",
        ["Edit properties →"] = "Load the selected export into the Properties tab. Double-clicking a row does the same.",
        ["Where does this mesh come from?"] = "Scan the game folder: for every StaticMesh this package imports, list the packages that hold its geometry. Read-only.",
        ["Find name in folder"] = "List every package that exports or imports an object with the selected export's name. Read-only.",
        ["Preview"] = "Show the selected texture (Textures tab) or mesh (Meshes tab, in 3D).",
        ["Export texture(s)"] = "Write the selected texture (or every texture, if the selection isn't a texture) as .dds into the export folder. The largest mip available is written, including full-size mips from the game's .tfc texture caches.",
        ["Find matching packages"] = "Scan the game folder for other packages that have this exact export, and list their current values. Packages with the same name prefix are pre-checked. Read-only.",
        ["Check all shown"] = "Tick every package currently shown in the list (use the filter first to narrow it).",
        ["Uncheck all"] = "Untick every package in the list.",
        ["Dry run (writes to import_out, game untouched)"] = "Build and verify the edited package(s) and write them to import_out next to the exe. Nothing in the game folder changes.",
        ["Apply to game file(s)…"] = "Write the edits into the game package(s): each gets a verified .bak of its original first, then a verified temp file is swapped in. Close the game first. The Backups tab reverts.",
        ["Reset edits"] = "Throw away unsaved edits and reload the values from the file.",
        ["Open folder"] = "Open the export folder in Explorer.",
        ["Export to FBX (+ textures)"] = "Write the selected StaticMesh as FBX, with its textures (.dds) linked, into the export folder.",
        ["Dry run import"] = "Build and verify the package with the FBX imported and write it to import_out. Nothing in the game folder changes.",
        ["Import into game file…"] = "Import the FBX into the game package: verified .bak first (if none yet), verified temp file, then swap. Collision on the mesh becomes empty. Close the game first.",
        ["Refresh"] = "Re-check every package that has a .bak: modified, or same as the original.",
        ["Revert selected to original…"] = "Copy the .bak (the original) back over the live package, verified. The .bak is kept. Also undoes mods made by other tools if their backup is the .bak.",
        ["Open selected package"] = "Open the selected package in the Browse tab.",
    };

    /// <summary>Last column takes the remaining width, so no unpainted header strip is left on the right.</summary>
    static void FillLastColumn(ListView lv) { if (lv.Columns.Count > 0) lv.Columns[lv.Columns.Count - 1].Width = -2; }

    static string ThemeButtonText(bool dark) => dark ? "Light mode" : "Dark mode";

    /// <summary>Dark (default) or light; applied to every control, remembered in the settings.</summary>
    void SetTheme(bool dark)
    {
        settings.DarkMode = dark;
        palette = dark ? Palette.Dark : Palette.Light;
        Theme.Apply(this, palette);
        if (themeToggle != null) themeToggle.Text = ThemeButtonText(dark);
        // Rows already in the grid: read-only text colour and the changed marker follow the theme.
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.DefaultCellStyle.ForeColor = row.Cells["Value"].ReadOnly ? palette.Subtle : Color.Empty;
            MarkChanged(row);
        }
        SaveSettings();
    }

    Button Btn(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3) };
        b.Click += (_, _) => onClick();
        if (ButtonTips.TryGetValue(text, out var tip)) tips.SetToolTip(b, tip);
        return b;
    }

    static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) };

    /// <summary>A single-line button row. A wrapping row inside an auto-sized table gets measured too tall.</summary>
    static FlowLayoutPanel NoWrap(params Control[] controls) { var f = Flow(controls); f.WrapContents = false; return f; }

    static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>Console output from any thread, appended to the log box line by line.</summary>
    sealed class LogWriter(Control owner, TextBox box) : TextWriter
    {
        readonly StringBuilder pending = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (pending)
            {
                if (value == '\r') return;
                if (value != '\n') { pending.Append(value); return; }
                string line = pending.ToString(); pending.Clear();
                Post(line);
            }
        }

        public override void Write(string? value) { if (value != null) foreach (char c in value) Write(c); }

        void Post(string line)
        {
            if (owner.IsDisposed) return;
            if (owner.InvokeRequired) owner.BeginInvoke(() => Append(line)); else Append(line);
        }

        void Append(string line)
        {
            if (box.IsDisposed) return;
            box.AppendText(line + Environment.NewLine);
        }
    }
}
