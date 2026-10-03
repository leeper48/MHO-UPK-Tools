using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// The Mod Manager editor's Model tab (2026-10-03, Kurt: the MHO MFF Importer moved into the Mod Manager): an MFF model (or an
/// FBX) retargeted onto one of the mod's packages, previewed, and built into the mod. It was the importer's window: MFF
/// characters on the left, the 3D view in the middle, the mod's package, parts, bone map, material and Build on the right,
/// the log at the bottom. The build is <see cref="ImportBuild"/>, run in the background; its package goes into the editor's
/// draft (<see cref="IModelHost"/>), the work (bone map, FBX edits, choices) into the mod's Model folder on Save.
/// </summary>
sealed partial class ModelPage : UserControl
{
    readonly IModelHost host;
    readonly Label foldersLabel = new() { AutoSize = true, Margin = new Padding(0, 7, 0, 0) };
    readonly Button settingsButton;

    readonly TextBox characterFilter = new() { PlaceholderText = "Filter Characters" };
    readonly CheckBox heroesOnly = new() { Text = "Characters Only", Checked = true, AutoSize = true };
    readonly CharacterList characters = new() { Dock = DockStyle.Fill };

    readonly PreviewPanel preview = new() { Dock = DockStyle.Fill };
    /// <summary>Waits a moment after a change before the preview is rebuilt (several quick clicks = one rebuild).</summary>
    readonly System.Windows.Forms.Timer previewDelay = new() { Interval = 350 };
    int previewId;

    readonly TextBox packageFilter = new() { PlaceholderText = "Filter Base Heroes" };
    readonly CharacterList packages = new() { Dock = DockStyle.Fill };
    readonly DataGridView parts = new() { Dock = DockStyle.Fill };
    readonly DropDown material = new();
    readonly CheckBox smooth = new() { Text = "Smooth (Subdivide)", AutoSize = true };
    /// <summary>A cape / long hair borrowed from other heroes (preview prototype; Kurt: generic numbers, matched motion).</summary>
    readonly DropDown capeBox = new() { Width = 150 }, hairBox = new() { Width = 150 };
    // the bone map editor (0.11.5)
    readonly DataGridView mapGrid = new() { Dock = DockStyle.Fill };
    readonly TextBox mapFilter = new() { PlaceholderText = "Filter bones" };
    readonly Button mapReset, mapSmooth, mapBones, mapWeights;
    readonly Button undoButton, redoButton;
    List<int> mhoParents = [];
    /// <summary>A bone Ctrl+clicked in the view that no row stands for (no MFF bone drives it); null when a row is selected.</summary>
    string? pickedBone;
    readonly FlatTabs modelTabs = new() { Dock = DockStyle.Fill };
    BoneMapFile? shownMap;
    List<string> mhoBones = [];
    readonly Button defaultParts;
    readonly Button buildButton, openButton, fbxButton;
    /// <summary>Where models come from (0.11.3, Kurt: "MFF or FBX as the source"; MSF / MCoC may follow).</summary>
    readonly DropDown sourceKind = new() { Width = 220 };
    bool FbxMode => sourceKind.SelectedIndex == 1;
    /// <summary>The FBX file in use as the source (FBX mode); null in MFF mode.</summary>
    string? sourceFbx;
    /// <summary>A source is picked: an MFF model, or an FBX file.</summary>
    bool HasSource => model != null || sourceFbx != null;
    readonly Label status = new() { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
    readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false };

    /// <summary>One MFF model: its character (the group), its uniform (name, slot S02 / Base, wiki order) and folder.</summary>
    sealed record CharRow(string Folder, string Character, string Uniform, string Slot, int Order);
    List<CharRow> allCharacters = new();
    /// <summary>The characters whose group is open (Kurt, 0.9.3: group the uniforms under their character, as an accordion).</summary>
    readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> expandedHeroes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The chosen base package (a hero header isn't one).</summary>
    CharacterList.Item? ChosenPackage => packages.SelectedItem is CharacterList.Item { Header: false } it && !it.Key.Contains(':') && !gameList ? it : null;
    /// <summary>The model picked last (a header click doesn't change it).</summary>
    string? chosenKey;
    List<(string File, string Title, string Detail)> allPackages = new();
    MffModel? model;
    bool stateLoaded;
    string? lastZip;
    bool building;

    /// <summary>The material choices (label → MFF_MATERIAL value; null = automatic).</summary>
    static readonly (string Label, string? Value, string Tip)[] Materials =
    [
        ("Automatic", null, "Chosen from the MFF maps: metal over half the model → Angela's armour material (with glow when the colour map has glow spots), otherwise Punisher Modern VU's."),
        ("Cloth (Punisher Modern VU)", MaterialChoice.Default, "Matte cloth and skin: Punisher Modern VU's material. Checked in game on Punisher S07 and Gamora S02."),
        ("Metal (Angela's Armour)", MaterialChoice.Metal, "Reflective metal driven by the MFF metal mask: Angela's armour material."),
        ("Metal + Glow (Angela's Weapon)", MaterialChoice.Glow, "Angela's weapon material with her armour's values: metal plus glow where the colour map is painted near-white / bright cyan. Checked in game on Iron Man S01."),
        ("The Base Mesh's Own", "base", "The base hero's own material (older type; it gave a sheen on Punisher Classic)."),
    ];

    public ModelPage(IModelHost host)
    {
        this.host = host;
        mapSmooth = Ui.FlatButton("Smooth Weights", SmoothSelected, "Smooths the weights of the bone in the selected row (a chain row: all its bones), as Blender's Weight Paint Smooth (0.5, 5 times) per click: its border with the next bones softens. Saved in your bone map; Undo (Ctrl+Z) takes it back.");
        mapSmooth.Enabled = false;
        mapBones = Ui.FlatButton("Bones", () => preview.ShowBones = !preview.ShowBones, "Draw the skeleton over the model; the selected row's bones in orange (as Look ▾ → Bones).");
        mapWeights = Ui.FlatButton("Weights", () => preview.ShowWeights = !preview.ShowWeights, "Weight paint: colour the model by how much the selected row's bones move it, as Blender does (blue 0 → cyan → green → yellow → red 1). Pick rows to see each bone; Smooth Weights shows its effect at once.");
        preview.BonePicked += SelectBoneRow;
        preview.Override = (ar, orig) => EnsureEdits().Anims.TryGetValue(ar.Name, out var f) && File.Exists(f) ? AnimEdits.ForGame(f, ar, orig) : null;
        preview.IsOverridden = n => edits.Anims.ContainsKey(n);
        preview.FillEditsMenu = FillEditsMenu;
        preview.TogglesChanged += () => { Ui.Lit(mapBones, preview.ShowBones); Ui.Lit(mapWeights, preview.ShowWeights); };
        undoButton = Ui.FlatButton("↶ Undo", Undo, "Undo the last change to the parts, Smooth, the material or the bone map (Ctrl+Z).");
        redoButton = Ui.FlatButton("↷ Redo", Redo, "Redo what Undo took back (Ctrl+Y).");
        undoButton.Enabled = redoButton.Enabled = false;
        mapReset = Ui.FlatButton("Reset to Automatic", ResetMap, "Forgets your bone map edits for this character on this base hero; the importer's own pairing is used again.");
        defaultParts = Ui.FlatButton("Default Parts", ResetParts, "Back to the parts the importer picks for this model (the body ticked; props, effects and swap parts not).");
        Font = Ui.Regular();
        DoubleBuffered = true;
        BackColor = Color.Transparent;

        settingsButton = Ui.FlatButton("Model Settings ▾", ShowSettingsMenu, "The MFF folder (your MFF rip: Models\\Models and Texture2D), the Blender the exports open in, and its MHO Actions add-on.");
        buildButton = Ui.AccentButton("Build into Mod", Build, "Builds the model onto the picked package and puts it into this mod (Save Changes keeps it; Apply Changes puts it into the game). Starts from the package as it was before the model, or from the game's stock copy (Build From).");
        openButton = Ui.FlatButton("Open Folder", OpenFolder, "Shows the last export in Explorer (model.fbx).");
        openButton.Enabled = false;
        // Full Export ▾ (0.16.5, Kurt): the model and every animation, as FBX files or straight into Blender (one animation:
        // the Single Animation ▾ menu under the preview)
        fbxButton = Ui.FlatButton("Full Export ▾", ShowFullExportMenu, "The model and all of the base hero's animations: Export FBX (one FBX each, the folder opens) or Open in Blender (a new Blender scene, every animation an Action on the NLA; Ctrl+S there sends your changes back). For one animation: Single Animation ▾ under the preview.");

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // unstyled, a column sizes to its widest child (MEMM 0.37.6)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        Controls.Add(root);

        // the tab's bar: the folders it reads, Undo / Redo, its settings
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Padding = new Padding(4, 2, 4, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var topRight = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { undoButton, redoButton, settingsButton }) { b.Margin = new Padding(6, 0, 0, 0); topRight.Controls.Add(b); }
        top.Controls.Add(foldersLabel, 0, 0); top.Controls.Add(topRight, 1, 0);
        foldersLabel.AutoEllipsis = true; foldersLabel.AutoSize = false; foldersLabel.Dock = DockStyle.Fill; foldersLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(top, 0, 0);

        // body: characters | 3D | target
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent, Padding = new Padding(6) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        root.Controls.Add(body, 0, 1);

        var sourceHead = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        sourceHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceKind.Margin = new Padding(0, 0, 0, 6); sourceKind.Anchor = AnchorStyles.Left;
        sourceHead.Controls.Add(sourceKind, 0, 0);
        var filterRow = Row(characterFilter, heroesOnly); filterRow.Dock = DockStyle.Fill;
        sourceHead.Controls.Add(filterRow, 0, 1);
        body.Controls.Add(Column("SOURCE", sourceHead, characters), 0, 0);
        body.Controls.Add(Column("PREVIEW", null, preview), 1, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Column("PACKAGE", Row(packageFilter, buildFrom), packages), 0, 0);
        var partsHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        smooth.Margin = new Padding(0, 6, 10, 0); capeBox.Margin = new Padding(0, 0, 6, 0); defaultParts.Margin = new Padding(0);
        hairBox.Margin = new Padding(0, 0, 6, 0);
        partsHead.Controls.AddRange([smooth, capeBox, hairBox, defaultParts]);
        modelTabs.Add("Parts", Column("", partsHead, parts));
        var mapButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { mapBones, mapWeights, mapSmooth }) { b.Margin = new Padding(0, 0, 6, 0); mapButtons.Controls.Add(b); }
        mapReset.Margin = new Padding(0); mapButtons.Controls.Add(mapReset);
        modelTabs.Add("Bone Map", Column("", Row(mapFilter, mapButtons), mapGrid));
        right.Controls.Add(modelTabs, 0, 1);
        right.Controls.Add(Column("MATERIAL", null, material, autoHeight: true), 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange([buildButton, fbxButton, openButton, status]);
        right.Controls.Add(actions, 0, 3);
        body.Controls.Add(right, 2, 0);

        root.Controls.Add(Column("LOG", null, log), 0, 2);

        // parts table: a check per part, its kind, size
        // the check boxes are toggled by our own click / Space handling (0.10.18, Kurt: they couldn't be changed); read only
        // so the grid's own check-box editing can't fight it
        parts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 34, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "part", HeaderText = "Part", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Kind", Width = 80, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "verts", HeaderText = "Vertices", Width = 96, ReadOnly = true });
        parts.AllowUserToAddRows = false; parts.AllowUserToDeleteRows = false; parts.RowHeadersVisible = false;
        parts.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Ui.StyleGrid(parts);
        Ui.Tip(parts, "The model's parts: ticked = imported. The body is ticked by default; props (weapons, effects) and swap parts (e.g. alternate hands) are not.");

        mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "", Width = 62, ReadOnly = true });
        mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mff", HeaderText = "Source", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 40, ReadOnly = true });
        mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "mho", HeaderText = "Target (MHO) ▾", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 40, ReadOnly = true });
        mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "How", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30, ReadOnly = true });
        mapGrid.AllowUserToAddRows = false; mapGrid.AllowUserToDeleteRows = false; mapGrid.RowHeadersVisible = false;
        mapGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; mapGrid.MultiSelect = false;
        Ui.StyleGrid(mapGrid);
        Ui.Tip(mapGrid, "The bone map: which MHO bone each MFF bone drives, and which MHO chain each MFF chain (hair, cape, straps) rides. Click an MHO cell to change it; the preview, Build and Export FBX use your map.");
        mapGrid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0 && mapGrid.Columns[e.ColumnIndex].Name == "mho") PickMapTarget(e.RowIndex); };
        mapFilter.TextChanged += (_, _) => FillMap();
        mapGrid.CurrentCellChanged += (_, _) => MapSelectionChanged();
        SearchBox.AddClear(mapFilter);
        foreach (var m in Materials) material.Items.Add(m.Label);
        material.SelectedIndex = 0;
        material.SelectedIndexChanged += (_, _) => Ui.Tip(material, Materials[Math.Max(0, material.SelectedIndex)].Tip);
        Ui.Tip(material, Materials[0].Tip);
        Ui.Tip(heroesOnly, "List only characters: heroes, villains, bosses, enemies, NPCs and person-like summons (anything with a character rig); props such as cocoons, drones and boxes are left out.");
        Ui.Tip(smooth, "One level of smooth subdivision on the ticked parts: every triangle becomes four and the surface rounds off. For the older low-poly models; about 4x the triangles.");
        smooth.CheckedChanged += (_, _) => SchedulePreview();
        capeBox.Items.Add("No Cape Motion"); hairBox.Items.Add("No Hair Motion");
        for (int k = 1; k <= BorrowedRig.CapeDonors.Length; k++) capeBox.Items.Add($"Cape {k}");
        for (int k = 1; k <= BorrowedRig.HairDonors.Length; k++) hairBox.Items.Add($"Hair {k}");
        hairBox.Items.Add("Mega Hair");
        capeBox.SelectedIndex = 0; hairBox.SelectedIndex = 0;
        capeBox.SelectedIndexChanged += (_, _) => SchedulePreview();
        hairBox.SelectedIndexChanged += (_, _) => SchedulePreview();
        Ui.Tip(capeBox, "Cape motion for a base hero without cape bones: cape bones are added so the model's cape strips ride them, and every animation gets cape motion matched from an MHO hero's hand-animated cape (for each frame, the one whose body moves most alike). Cape 1-3 are Thor's, Doctor Strange's and Vision's. Build includes it when the base hero is a base package (its animation sets are copied into the mod with the cape's motion added), and so do Full Export and Open in Blender.");
        Ui.Tip(hairBox, "Long-hair motion for a base hero without hair bones hair bones are added so the model's hair strands ride them, and every animation gets hair motion matched from an MHO hero's hand-animated hair. Hair 1 is short, Hair 3 the longest; Mega Hair stretches Hair 3's strands to the model's own hair length (for manes such as Scream's or Medusa's). Build includes it when the base hero is a base package (its animation sets are copied into the mod with the hair's motion added).");

        SearchBox.AddClear(characterFilter);
        SearchBox.AddClear(packageFilter);
        characterFilter.TextChanged += (_, _) => FillCharacters();
        heroesOnly.CheckedChanged += (_, _) => FillCharacters();
        sourceKind.Items.AddRange(["MFF Characters", "FBX Files"]);
        sourceKind.SelectedIndex = 0;
        Ui.Tip(sourceKind, "Where the model comes from: an MFF character (retargeted onto the base hero), or an FBX file with an MHO skeleton (g_ bone names), e.g. one made with Export FBX and cleaned up in Blender.");
        sourceKind.SelectedIndexChanged += (_, _) =>
        {
            chosenKey = null; model = null; sourceFbx = null; parts.Rows.Clear();
            heroesOnly.Visible = !FbxMode; smooth.Enabled = !FbxMode; if (FbxMode) smooth.Checked = false;
            characterFilter.Text = "";
            FillCharacters(); UpdateStatus(); SchedulePreview();
        };
        packageFilter.TextChanged += (_, _) => FillPackages();
        packages.SelectedIndexChanged += (_, _) => PackagePicked();   // first: "From the Game" and the game list's picks
        characters.SelectedIndexChanged += (_, _) => CharacterChosen();
        characters.MouseUp += (_, e) =>
        {
            int i = characters.IndexFromPoint(e.Location);
            if (i >= 0 && i < characters.Items.Count && characters.Items[i] is CharacterList.Item { Header: true } h) ToggleGroup(h);
        };
        characters.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && characters.SelectedItem is CharacterList.Item { Header: true } h) { ToggleGroup(h); e.Handled = true; }
        };
        characters.Thumb = it => it.Key.StartsWith("fbx:") || it.Key.StartsWith("browse:") ? null : Thumbs.Model(it.ThumbKey ?? it.Key);
        packages.Thumb = it => Thumbs.BaseHero(it.ThumbKey ?? it.Key);
        packages.MouseUp += (_, e) =>
        {
            int i = packages.IndexFromPoint(e.Location);
            if (i >= 0 && i < packages.Items.Count && packages.Items[i] is CharacterList.Item { Header: true } h) ToggleHero(h);
        };
        packages.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && packages.SelectedItem is CharacterList.Item { Header: true } h) { ToggleHero(h); e.Handled = true; }
        };
        Thumbs.Ready += OnThumbReady;
        Disposed += (_, _) => Thumbs.Ready -= OnThumbReady;
        packages.SelectedIndexChanged += (_, _) => UpdateStatus();

        Ui.Lit(mapBones, preview.ShowBones); Ui.Lit(mapWeights, preview.ShowWeights);
        log.BackColor = Color.FromArgb(22, 22, 26); log.ForeColor = Ui.Subtle; log.Font = new Font("Consolas", 9f);
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); RefreshPreview(); };
        packages.SelectedIndexChanged += (_, _) => SchedulePreview();
        material.SelectedIndexChanged += (_, _) => SchedulePreview();
        parts.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0 && parts.Columns[e.ColumnIndex].Name == "use")
                SetUse([parts.Rows[e.RowIndex]], parts.Rows[e.RowIndex].Cells["use"].Value is not true);
        };
        parts.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || parts.SelectedRows.Count == 0) return;
            var rows = parts.SelectedRows.Cast<DataGridViewRow>().ToList();
            SetUse(rows, rows.Any(r => r.Cells["use"].Value is not true));
            e.Handled = true;
        };
        parts.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && parts.Columns[e.ColumnIndex].Name == "use") SchedulePreview(); };

        HandleCreated += (_, _) => { if (!loaded) { loaded = true; ScaleToDpi(); Reload(); } };
        InitBuildFrom();
    }

    void OnThumbReady(string key)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { characters.Invalidate(); packages.Invalidate(); }); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // --- layout helpers -------------------------------------------------------------------------------------------------------
    static Control Column(string caption, Control? head, Control content, bool autoHeight = false)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(4), AutoSize = autoHeight };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(autoHeight ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, 100));
        var label = new Label { Text = caption, AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f), Margin = new Padding(2, 2, 0, 4) };
        if (caption.Length > 0) t.Controls.Add(label, 0, 0);
        if (head != null) { head.Dock = DockStyle.Fill; head.Margin = new Padding(0, 0, 0, 4); t.Controls.Add(head, 0, 1); }
        content.Dock = autoHeight ? DockStyle.Top : DockStyle.Fill;
        t.Controls.Add(content, 0, 2);
        return t;
    }

    static Control Row(Control fill, Control right)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fill.Dock = DockStyle.Fill; right.Margin = new Padding(8, 4, 0, 0);
        t.Controls.Add(fill, 0, 0); t.Controls.Add(right, 1, 0);
        return t;
    }


    // --- data -------------------------------------------------------------------------------------------------------------------
    void Reload()
    {
        var s = Settings.Current;
        foldersLabel.Text = $"MFF: {s.MffSource ?? "(not set)"}   ·   Game: {s.GameFolder ?? "(not found)"}{(s.StockFolder != null ? $"   ·   Stock: {s.StockFolder}" : "")}";
        foldersLabel.ForeColor = Ui.Subtle;
        allCharacters = new();
        try
        {
            foreach (var dir in Source.AllModelFolders())
            {
                string folder = Path.GetFileName(dir);
                MffNames.All.TryGetValue(folder, out var e);
                string character = e?.Character ?? folder;
                int u = e?.Uniform ?? 0;
                string slot = e?.Character == null ? "" : u > 0 ? $"S{u:00}" : "Base";
                allCharacters.Add(new CharRow(folder, character, e?.UniformName is { Length: > 0 } un ? un : slot.Length > 0 ? slot : folder, slot, u));
            }
            allCharacters = allCharacters.OrderBy(c => c.Character, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Order).ThenBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log("Set the MFF folder in Settings (" + ex.Message + ")");
        }
        allPackages = new();
        var dirs = new[] { s.StockFolder, s.CookedFolder }.Where(d => d != null && Directory.Exists(d)).Select(d => d!).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dirs)
            foreach (var f in Directory.EnumerateFiles(d, "UC__Marvel*_SF.upk"))
            {
                string n = Path.GetFileName(f);
                if (n.Contains("bak", StringComparison.OrdinalIgnoreCase) || n.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(n.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase))) continue;
                if (n.StartsWith("UC__MarvelPlayerAudio", StringComparison.OrdinalIgnoreCase) || !seen.Add(n)) continue;
                var (t, det) = DescribePackage(n);
                allPackages.Add((n, t, det));
            }
        allPackages.Sort((a, b) => string.Compare(a.Title + a.Detail, b.Title + b.Detail, StringComparison.OrdinalIgnoreCase));
        FillCharacters();
        FillPackages();
        UpdateStatus();
        Log($"{allCharacters.Count} MFF models; the mod's packages, or {allPackages.Count} of the game's base heroes.");
        if (!stateLoaded) { stateLoaded = true; LoadState(); }
    }

    /// <summary>"Punisher", "Classic · UC__MarvelPlayer_Punisher_Classic_SF" from the file name.</summary>
    static (string Title, string Detail) DescribePackage(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        bool teamUp = stem.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase);
        string rest = stem[(teamUp ? "UC__MarvelTeamUp_".Length : "UC__MarvelPlayer_".Length)..];
        if (rest.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) rest = rest[..^3];
        int us = rest.IndexOf('_');
        string hero = us < 0 ? rest : rest[..us], costume = us < 0 ? (teamUp ? "Team-Up" : "Base Package") : rest[(us + 1)..] + (teamUp ? " (Team-Up)" : "");
        return (hero, $"{costume} · {stem}");
    }

    /// <summary>The character list as an accordion: one header per character with several models (its uniforms underneath
    /// when open, in wiki order); a character with one model is a plain row. While a filter is typed, matching groups open.</summary>
    void FillCharacters()
    {
        if (FbxMode) { FillFbx(); return; }
        string f = characterFilter.Text.Trim();
        int top = characters.TopIndex;
        characters.BeginUpdate(); characters.Items.Clear();
        var shown = allCharacters.Where(c => (!heroesOnly.Checked || MffNames.IsCharacter(c.Folder))
            && (f.Length == 0 || $"{c.Character} {c.Uniform} {c.Slot} {c.Folder}".Contains(f, StringComparison.OrdinalIgnoreCase)));
        foreach (var g in shown.GroupBy(c => c.Character, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            if (list.Count == 1)
            {
                var c = list[0];
                characters.Items.Add(new CharacterList.Item(c.Folder, c.Character, c.Slot.Length > 0 ? $"{c.Uniform}{(c.Uniform != c.Slot ? $" ({c.Slot})" : "")} · {c.Folder}" : c.Folder));
                continue;
            }
            bool open = f.Length > 0 || expanded.Contains(g.Key);
            characters.Items.Add(new CharacterList.Item("group:" + g.Key, g.Key, $"{list.Count} uniforms", Header: true, Expanded: open, ThumbKey: list[0].Folder));
            if (open)
                foreach (var c in list)
                    characters.Items.Add(new CharacterList.Item(c.Folder, c.Uniform, c.Slot.Length > 0 && c.Uniform != c.Slot ? $"{c.Slot} · {c.Folder}" : c.Folder, Indent: 1));
        }
        characters.EndUpdate();
        if (chosenKey != null) Reselect(characters, chosenKey);
        if (top < characters.Items.Count) characters.TopIndex = top;
    }

    /// <summary>FBX mode's list: Browse first, then the exports (data\fbx\*\model.fbx) and the FBX files picked before.</summary>
    void FillFbx()
    {
        string f = characterFilter.Text.Trim();
        characters.BeginUpdate(); characters.Items.Clear();
        characters.Items.Add(new CharacterList.Item("browse:", "Browse for an FBX", "any FBX with an MHO skeleton (g_ bone names)"));
        var files = new List<string>();
        string dir = Path.Combine(Settings.Home, "fbx");
        if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "model.fbx", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc));
        files.AddRange(Settings.Current.RecentFbx.Where(File.Exists));
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string title = ImportBuild.SourceName(file), folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            if (f.Length > 0 && !$"{title} {folder} {file}".Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            characters.Items.Add(new CharacterList.Item("fbx:" + file, title, $"{folder} · {File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}"));
        }
        characters.EndUpdate();
        if (chosenKey != null) Reselect(characters, chosenKey);
    }

    /// <summary>A click on a group header folds it open or shut (the chosen model stays chosen).</summary>
    void ToggleGroup(CharacterList.Item header)
    {
        string name = header.Key["group:".Length..];
        if (!expanded.Add(name)) expanded.Remove(name);
        FillCharacters();
    }

    /// <summary>The first of the character's name forms that matches a base hero package, or null.</summary>
    string? SuggestFilter(string title)
    {
        static string Key(string t) => new string(t.Where(char.IsLetterOrDigit).ToArray());
        var forms = new List<string> { title };
        int open = title.IndexOf('(');
        if (open > 0)
        {
            forms.Add(title[..open].Trim());
            string inner = title[(open + 1)..].TrimEnd(')').Trim();
            forms.Add(inner);
            var words = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int n = words.Length - 1; n >= 1; n--) forms.Add(string.Join(" ", words.Take(n)));
        }
        var tw = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = tw.Length - 1; n >= 1; n--) forms.Add(string.Join(" ", tw.Take(n)));
        foreach (var f in forms.Where(f => f.Length >= 3))
            if (allPackages.Any(p => Key(p.Title + p.Detail).Contains(Key(f), StringComparison.OrdinalIgnoreCase))) return f;
        return null;
    }

    /// <summary>The base heroes as an accordion (0.10.15, Kurt): one header per hero with several packages (its costumes
    /// underneath when open); a hero with one package is a plain row. While a filter is typed, matching groups open.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    void FillGamePackages()
    {
        string f = new string(packageFilter.Text.Where(char.IsLetterOrDigit).ToArray());
        string? keep = ChosenPackage?.Key;
        int top = packages.TopIndex;
        packages.BeginUpdate(); packages.Items.Clear();
        var shown = allPackages.Where(p => f.Length == 0 || new string((p.Title + p.Detail).Where(char.IsLetterOrDigit).ToArray()).Contains(f, StringComparison.OrdinalIgnoreCase));
        foreach (var g in shown.GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.ToList();
            if (list.Count == 1) { var p = list[0]; packages.Items.Add(new CharacterList.Item(p.File, p.Title, p.Detail)); continue; }
            bool open = f.Length > 0 || expandedHeroes.Contains(g.Key) || list.Any(p => p.File.Equals(keep, StringComparison.OrdinalIgnoreCase));
            var first = list.FirstOrDefault(p => p.Detail.StartsWith("Base Package", StringComparison.OrdinalIgnoreCase));
            packages.Items.Add(new CharacterList.Item("hero:" + g.Key, list[0].Title, $"{list.Count} packages", Header: true, Expanded: open, ThumbKey: (first == default ? list[0] : first).File));
            if (open)
                foreach (var p in list)
                {
                    int dot = p.Detail.IndexOf(" · ", StringComparison.Ordinal);
                    packages.Items.Add(new CharacterList.Item(p.File, dot > 0 ? p.Detail[..dot] : p.Detail, dot > 0 ? p.Detail[(dot + 3)..] : p.File, Indent: 1));
                }
        }
        packages.EndUpdate();
        if (keep != null) Reselect(packages, keep);
        if (top < packages.Items.Count && f.Length == 0) packages.TopIndex = top;
        UpdateStatus();
    }

    /// <summary>A click on a hero header folds its packages open or shut.</summary>
    void ToggleHero(CharacterList.Item header)
    {
        string name = header.Key["hero:".Length..];
        if (!expandedHeroes.Add(name)) expandedHeroes.Remove(name);
        FillPackages();
    }

    static void Reselect(CharacterList list, string key)
    {
        for (int i = 0; i < list.Items.Count; i++)
            if (((CharacterList.Item)list.Items[i]).Key.Equals(key, StringComparison.OrdinalIgnoreCase)) { list.SelectedIndex = i; return; }
    }

    async void CharacterChosen()
    {
        if (characters.SelectedItem is not CharacterList.Item it || it.Header || it.Key == chosenKey) return;
        if (it.Key == "browse:") { BrowseFbx(); return; }
        if (it.Key.StartsWith("fbx:")) { FbxChosen(it.Key); return; }
        chosenKey = it.Key; sourceFbx = null;
        model = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        try
        {
            var m = await Task.Run(() => MffModel.Load(Source.ResolveModelFile(it.Key)));
            if ((characters.SelectedItem as CharacterList.Item)?.Key != it.Key) return;   // another one was picked meanwhile
            model = m;
            foreach (var p in m.Parts)
            {
                string kind = p.IsProp ? "Prop" : p.IsAlternate ? "Swap" : !p.Weighted ? "Unrigged" : "Body";
                parts.Rows.Add(p.DefaultOn, p.Name, kind, p.Verts.ToString("N0"));
            }
            // Suggest the base hero: filter the packages to the character's name, or the simplest form of it that finds any
            // ("Hulkbuster (Iron Man Mark 44)" → "Hulkbuster", "Iron Man Mark 44", "Iron Man" ...).
            var (title, _) = MffNames.Describe(it.Key);
            if (gameList && title != it.Key && SuggestFilter(title) is string sf) packageFilter.Text = sf;
            SchedulePreview();
            Log($"{it.Title} ({it.Key}): {m.Parts.Count} parts, {m.Parts.Count(p => p.DefaultOn)} ticked by default.");
            RestoreState();
        }
        catch (Exception ex) { Log($"{it.Key}: {ex.Message}"); }
        UpdateStatus();
    }

    /// <summary>Picks any FBX file and adds it to the list (remembered).</summary>
    void BrowseFbx()
    {
        string start = Path.Combine(Settings.Home, "fbx"); Directory.CreateDirectory(start);
        using var dlg = new OpenFileDialog { Title = "FBX with an MHO skeleton", Filter = "FBX (*.fbx)|*.fbx", InitialDirectory = start };
        if (dlg.ShowDialog(this) != DialogResult.OK) { if (chosenKey != null) Reselect(characters, chosenKey); return; }
        var recent = Settings.Current.RecentFbx;
        recent.RemoveAll(x => x.Equals(dlg.FileName, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, dlg.FileName);
        if (recent.Count > 20) recent.RemoveRange(20, recent.Count - 20);
        Settings.Current.Save();
        FillFbx();
        Reselect(characters, "fbx:" + dlg.FileName);
    }

    /// <summary>An FBX file as the source: its meshes become the parts (all ticked); the base hero is suggested from an
    /// export's folder name ("… on UC__…").</summary>
    async void FbxChosen(string key)
    {
        string file = key[4..];
        chosenKey = key; model = null; sourceFbx = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        try
        {
            var meshes = await Task.Run(() => FbxReimport.Meshes(file));
            if (chosenKey != key) return;
            sourceFbx = file;
            foreach (var (name, verts) in meshes) parts.Rows.Add(true, name, "Mesh", verts.ToString("N0"));
            string folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            int on = folder.IndexOf(" on ", StringComparison.Ordinal);
            if (on > 0)
            {
                // the export's base hero: filter to the hero, pick that package
                string pkgFile = folder[(on + 4)..].Split(" (")[0] + ".upk";
                Reselect(packages, pkgFile);
            }
            SchedulePreview();
            Log($"FBX source: {file} ({meshes.Count} mesh(es))");
            RestoreState();
        }
        catch (Exception ex) { Log($"{file}: {ex.Message}"); }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        bool ready = HasSource && ChosenPackage != null && !building;
        buildButton.Enabled = ready; fbxButton.Enabled = ready;
        status.Text = building ? "Building…" : !HasSource ? (FbxMode ? "Pick an FBX." : "Pick an MFF character.") : ChosenPackage == null ? "Pick the package to build onto." : "Ready to build.";
        status.ForeColor = Ui.Subtle;
    }

    // --- preview ------------------------------------------------------------------------------------------------------------------
    /// <summary>Restarts the short delay before the preview is rebuilt (on the UI thread: the timer only ticks there).</summary>
    void SchedulePreview()
    {
        if (InvokeRequired) { BeginInvoke(SchedulePreview); return; }
        previewDelay.Stop(); previewDelay.Start();
    }

    /// <summary>Retargets the ticked parts onto the base hero in the background and shows the result in the 3D view.</summary>
    /// <summary>A preview rebuild is running (checks wait for none pending or running).</summary>
    bool refreshing;

    async void RefreshPreview()
    {
        refreshing = true;
        try { await RefreshPreviewCore(); } finally { refreshing = false; }
    }

    async Task RefreshPreviewCore()
    {
        int id = ++previewId;
        if (!HasSource) { preview.ShowMessage(FbxMode ? "Pick an FBX" : "Pick a character"); return; }
        if (ChosenPackage is not CharacterList.Item pkg) { preview.ShowMessage("Pick a base hero"); return; }
        var picked = SelectedParts();
        if (picked.Count == 0) { preview.ShowMessage("Tick at least one part"); return; }
        Remember(pkg.Key);
        var m = model; string parts = string.Join(",", picked); string? donor = Materials[Math.Max(0, material.SelectedIndex)].Value; bool sub = smooth.Checked; string? sfbx = sourceFbx;
        int capeChoice = capeBox.SelectedIndex, hairChoice = hairBox.SelectedIndex;
        preview.ShowMessage("Loading…");
        try
        {
            string? mapFile = sfbx == null && MapPath() is string mp && File.Exists(mp) ? mp : null;
            string? modelFbx = sfbx == null && EnsureEdits().ModelFbx is string mf && File.Exists(mf) ? mf : null;
            var prepared = await Task.Run(() => sfbx != null
                ? PreviewPanel.PrepareFbx(sfbx, picked.ToHashSet(StringComparer.OrdinalIgnoreCase), StartPackage(pkg.Key), donor)
                : PreviewPanel.Prepare(m!, parts, StartPackage(pkg.Key), donor, sub, modelFbx, mapFile, capeChoice, hairChoice));
            if (id != previewId || IsDisposed) return;   // something changed meanwhile
            preview.Show(prepared);
            shownMap = prepared.Map; mhoBones = prepared.MhoBones; mhoParents = prepared.MhoParents; FillMap();
            WatchBlender();   // this work's Blender folder (a sync that came meanwhile is applied)
            // what Automatic picked, shown in the drop-down (0.10.19, Kurt)
            material.Items[0] = prepared.Material.Length > 0 ? $"Automatic · {prepared.Material}" : "Automatic";
            Log("Preview: " + prepared.Note);
        }
        catch (Exception ex)
        {
            if (id != previewId || IsDisposed) return;
            preview.ShowMessage("No preview: " + ex.Message);
            Log("Preview: " + ex.Message);
        }
    }

    /// <summary>Ticks / unticks parts (the preview follows through CellValueChanged).</summary>
    void SetUse(IEnumerable<DataGridViewRow> rows, bool on)
    {
        foreach (var r in rows) r.Cells["use"].Value = on;
    }

    /// <summary>Back to the parts the importer picks for this model (its default selection).</summary>
    void ResetParts()
    {
        if (model == null) { SetUse(parts.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["use"].Value is not true), true); return; }   // FBX: all meshes
        for (int i = 0; i < parts.Rows.Count && i < model.Parts.Count; i++)
            if ((parts.Rows[i].Cells["use"].Value is true) != model.Parts[i].DefaultOn) parts.Rows[i].Cells["use"].Value = model.Parts[i].DefaultOn;
    }

    List<string> SelectedParts() => parts.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["use"].Value is true).Select(r => (string)r.Cells["part"].Value).ToList();

    // --- build --------------------------------------------------------------------------------------------------------------------
    async void Build()
    {
        if (!HasSource || ChosenPackage is not CharacterList.Item pkg || building) return;
        var picked = SelectedParts();
        if (picked.Count == 0) { Log("Tick at least one part."); return; }
        var options = new ImportOptions { Parts = string.Join(",", picked), Material = Materials[Math.Max(0, material.SelectedIndex)].Value, Subdivide = smooth.Checked, SourceFbx = sourceFbx,
            MapFile = sourceFbx == null && MapPath() is string mp && File.Exists(mp) ? mp : null,
            Hair = Math.Max(0, hairBox.SelectedIndex), Cape = Math.Max(0, capeBox.SelectedIndex),
            ModelFbx = sourceFbx == null && EnsureEdits().ModelFbx is string mfb && File.Exists(mfb) ? mfb : null,
            AnimFbx = sourceFbx == null ? new Dictionary<string, string>(edits.Anims.Where(kv => File.Exists(kv.Value)), StringComparer.OrdinalIgnoreCase) : null,
            NoMod = true };
        string mff = model?.Folder ?? ImportBuild.SourceName(sourceFbx!);
        string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"{mff} on {Path.GetFileNameWithoutExtension(pkg.Key)}"));
        building = true; UpdateStatus();
        log.Clear();
        string start = StartPackage(pkg.Key);
        Log($"Building {mff} on {pkg.Key} (from {StartLabel(pkg.Key)}) → {outDir}");
        try
        {
            var result = await Task.Run(() => ImportBuild.Run(mff, start, outDir, options, line => BeginInvoke(() => Log(line))));
            if (result != null)
            {
                host.SetPackage(pkg.Key, result.Package);
                built[pkg.Key] = result.Package;
                SaveState();
                Log($"Done: {pkg.Key} is in the mod now (Save Changes keeps it; Apply Changes puts it into the game).");
            }
            else Log("The build stopped: see the package problems above.");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        building = false; UpdateStatus();
        if (built.ContainsKey(pkg.Key)) { status.Text = "Built into the mod: Save Changes keeps it."; status.ForeColor = Ui.Enabled; }
    }

    /// <summary>FBX round trip, export (0.11.0): databx\&lt;model&gt; on &lt;package&gt;\ with model.fbx, its textures and anims\.</summary>
    /// <summary>Installs the shipped MHO Actions add-on into that Blender (background; logged).</summary>
    async Task InstallAddon(string exe)
    {
        status.Text = "Installing the MHO Actions add-on…";
        Log($"Blender: installing the MHO Actions add-on ({Path.GetFileName(BlenderLaunch.BundledAddon())}) into {exe}…");
        string? failed = await Task.Run(() => BlenderLaunch.InstallAddon(exe));
        if (failed == null) Log("Blender: the MHO Actions add-on is installed and enabled.");
        else { Log("Blender: the add-on wasn't installed: " + failed); Dialog.Show(this, $"The MHO Actions add-on wasn't installed: {failed}\n\nThe scene will be built without it. You can install the add-on by hand: Blender → Edit → Preferences → Get Extensions → Install from Disk, the file {BlenderLaunch.BundledAddon()}.", "Add-On Not Installed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        UpdateStatus();
    }

    void ShowFullExportMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add(new ToolStripMenuItem("Export FBX", null, (_, _) => ExportFbx(false, null)) { ToolTipText = "The model and every animation, one FBX each, into data\fbx; the folder opens." });
        m.Items.Add(new ToolStripMenuItem("Open in Blender", null, (_, _) => ExportFbx(true, null)) { Enabled = sourceFbx == null, ToolTipText = "The same, then a new Blender scene with every animation an Action on the NLA (saved as model.blend); Ctrl+S there sends your changes back. Settings ▾ → Choose Blender picks which Blender." });
        Ui.ShowUnder(m, fbxButton);
    }

    /// <param name="onlyAnim">The middle panel's export (0.16.4): just this animation (the right panel's: all of them).</param>
    async void ExportFbx(bool openInBlender, string? onlyAnim)
    {
        if (!HasSource || ChosenPackage is not CharacterList.Item pkg || building) return;
        var picked = SelectedParts();
        if (picked.Count == 0) { Log("Tick at least one part."); return; }
        if (onlyAnim != null && sourceFbx != null) { Log("One-animation exports are for MFF sources."); return; }
        // Blender first (0.16.6, Kurt): none found = ask for one before anything is exported
        if (openInBlender && BlenderLaunch.Find() == null)
        {
            if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") == "1") { Log("Blender: none found."); return; }
            int pick = Dialog.Choose(this, "No Blender was found (none under Program Files\\Blender Foundation, and none chosen in Settings).\n\nPick blender.exe if it's installed somewhere else, or get Blender (5.0 or newer, for the MHO Actions add-on) and try again.",
                "Blender Not Found", "Choose blender.exe", "Get Blender", "Cancel");
            if (pick == 1) { Process.Start(new ProcessStartInfo("https://www.blender.org/download/") { UseShellExecute = true }); return; }
            if (pick != 0) return;
            using var d = new OpenFileDialog { Title = "blender.exe", Filter = "Blender (blender.exe)|blender.exe", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Settings.Current.BlenderPath = d.FileName; Settings.Current.Save();
            Log("Blender: " + d.FileName);
        }
        // the MHO Actions add-on, offered (optional) when that Blender lacks it (0.16.7, Kurt)
        if (openInBlender && Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" && !Settings.Current.SkipAddonOffer && BlenderLaunch.Find() is string bexe && BlenderLaunch.CanOfferAddon(bexe))
        {
            int pick = Dialog.Choose(this, $"{Path.GetFileName(Path.GetDirectoryName(bexe))} doesn't have the MHO Actions add-on. It's optional: without it the scene is built with Blender's own FBX import, and Ctrl+S still sends your changes back.\n\n" +
                "With it you also get the action library (anims\\anim.blend), a Root pose track, and its tools (Batch Export Animations, Offset, the weight tools and more).\n\nInstall it into this Blender now? Your Blender preferences are kept.",
                "MHO Actions Add-On (Optional)", "Install and Continue", "Continue Without", "Don't Ask Again", "Cancel");
            if (pick == 3) return;
            if (pick == 2) { Settings.Current.SkipAddonOffer = true; Settings.Current.Save(); Log("Blender: the add-on won't be offered again (Settings ▾ → Install the MHO Actions Add-On does it any time)."); }
            if (pick == 0) await InstallAddon(bexe);
        }
        // what this one does, before it starts (Kurt: so the user knows the difference)
        int count = preview.OwnAnimationCount, shared = preview.AnimationNames.Count - count;
        string what = onlyAnim == null
            ? $"Exports the model and all {count} of the base hero's own animations, one FBX each{(shared > 0 ? $" (not the {shared} shared ones every hero plays: blink, interactions)" : "")}{(openInBlender ? ", then opens them in a new Blender scene with every animation as an Action on the NLA (with the MHO Actions add-on, a background Blender turns each FBX into an Action first: this takes a while for all of them)" : "")}.\n\nFor work on a single animation, the Single Animation ▾ menu under the preview exports just the one that's picked, much faster."
            : $"Exports the model and only \"{onlyAnim}\", the animation picked in the preview{(openInBlender ? ", then opens it in a new Blender scene with that one Action on the NLA" : "")}.\n\nThe hero's other animations aren't in it. For all of them, use Full Export ▾ → {(openInBlender ? "Open in Blender" : "Export FBX")} on the right.";
        what += openInBlender ? $"\n\nBlender: {BlenderLaunch.Describe(BlenderLaunch.Find()!)} (Settings ▾ → Choose Blender to change it).\n\nIn that Blender scene, every Ctrl+S sends what you changed (the mesh, the animations whose keys changed) back here as FBX edits." : "\n\nThe export folder opens when it's done; bring edits back with Single Animation ▾ → Import FBX.";
        if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" &&
            Dialog.Choose(this, what, onlyAnim == null ? (openInBlender ? "Open All Animations in Blender" : "Export All Animations") : (openInBlender ? $"Open \"{onlyAnim}\" in Blender" : $"Export \"{onlyAnim}\" Only"),
                openInBlender ? "Open in Blender" : "Export", "Cancel") != 0) return;
        var m = model; string parts = string.Join(",", picked); bool sub = smooth.Checked; string? sfbx = sourceFbx;
        string name = m?.Folder ?? ImportBuild.SourceName(sfbx!);
        string? mapFile = sfbx == null && MapPath() is string mp && File.Exists(mp) ? mp : null;
        int hairChoice = sfbx == null ? Math.Max(0, hairBox.SelectedIndex) : 0, capeChoice = sfbx == null ? Math.Max(0, capeBox.SelectedIndex) : 0;
        var exportEdits = sfbx == null ? AnimEdits.Parse(EnsureEdits().Serialize()) : null;
        string outDir = UniqueDir(Path.Combine(Settings.Home, "fbx", $"{name} on {Path.GetFileNameWithoutExtension(pkg.Key)}{(onlyAnim != null ? " - " + FbxExport.SafeName(onlyAnim) : "")}"));
        building = true; UpdateStatus(); status.Text = "Exporting FBX…";
        Log($"Exporting FBX: {name} on {pkg.Key} → {outDir}");
        try
        {
            await Task.Run(() =>
            {
                string package = StartPackage(pkg.Key);
                if (sfbx != null)
                {
                    var r = FbxReimport.Load(sfbx, MhoSkeleton.Load(package, null), picked.ToHashSet(StringComparer.OrdinalIgnoreCase), _ => { });
                    FbxExport.Run(r, package, outDir, [], line => BeginInvoke(() => Log(line)));
                }
                else FbxExport.Work(m!, picked, sub, package, mapFile, hairChoice, exportEdits, outDir, line => BeginInvoke(() => Log(line)), onlyAnim != null ? [onlyAnim] : null, exact: onlyAnim != null, cape: capeChoice);
            });
            lastZip = Path.Combine(outDir, "model.fbx");   // Open Folder shows the latest output (a build's .ZIP or this)
            // in a new Blender scene (0.16.2), else the folder
            if (openInBlender && BlenderLaunch.Open(outDir) is string why) Log("Blender: " + why);
            else if (openInBlender)
            {
                Log($"Blender: opening {outDir} in {BlenderLaunch.Find()} (model.fbx, every animation as an Action on the NLA; saved as model.blend there). Each Ctrl+S in Blender sends what you changed back here.");
                LinkBlender(outDir);
            }
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastZip}\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        building = false; openButton.Enabled = lastZip != null; UpdateStatus();
        if (lastZip != null && lastZip.EndsWith(".fbx")) { status.Text = "FBX exported: model.fbx and the anims folder."; status.ForeColor = Ui.Enabled; }
    }

    // --- bone map editor ----------------------------------------------------------------------------------------------------------
    /// <summary>This character on this base hero's map file (data\maps); null without an MFF source and a base hero.</summary>
    string? MapPath() => model != null && ChosenPackage is CharacterList.Item pkg
        ? Path.Combine(host.WorkFolder, "maps", $"{model.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}.json") : null;

    /// <summary>The map the preview used: chains first, then the bones (lines the chains set are shown, not editable).</summary>
    void FillMap()
    {
        int keepRow = mapGrid.CurrentCell?.RowIndex ?? -1;
        mapGrid.Rows.Clear();
        bool edited = MapPath() is string mp && File.Exists(mp);
        modelTabs.SetTitle(1, edited ? "Bone Map ✎" : "Bone Map");
        mapReset.Enabled = edited;
        if (shownMap == null) return;
        string f = mapFilter.Text.Trim();
        bool Show(string a, string? b) => f.Length == 0 || a.Contains(f, StringComparison.OrdinalIgnoreCase) || (b?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false);
        foreach (var c in shownMap.Chains)
            if (Show(c.Mff, c.Mho)) { int i = mapGrid.Rows.Add("Chain", c.Mff, c.Mho ?? "(not paired)", (c.Fit ?? "") + SmoothNote(c.Mho)); mapGrid.Rows[i].Tag = c; }
        foreach (var b in shownMap.Bones)
            if (Show(b.Mff, b.Mho))
            {
                int i = mapGrid.Rows.Add("Bone", b.Mff, b.Mho ?? "(nearest mapped parent)", b.How + SmoothNote(RowTarget(b)));
                mapGrid.Rows[i].Tag = b;
                if (b.How == "chain") mapGrid.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
            }
        if (keepRow >= 0 && keepRow < mapGrid.Rows.Count) mapGrid.CurrentCell = mapGrid.Rows[keepRow].Cells["mff"];
        MapSelectionChanged();
    }

    /// <summary>The MHO bone a bone row's weights go to: its own target, else the parent its weights fall to.</summary>
    static string? RowTarget(BoneMapFile.BoneEntry b) =>
        b.Mho ?? (b.How.StartsWith("parent → ", StringComparison.Ordinal) ? b.How["parent → ".Length..] : null);

    /// <summary>" · smoothed ×2" when the bone's weights are smoothed in the map.</summary>
    string SmoothNote(string? bone) =>
        bone != null && shownMap?.Smooth.FirstOrDefault(s => s.Bone.Equals(bone, StringComparison.OrdinalIgnoreCase)) is { Passes: > 0 } e ? $" · smoothed ×{e.Passes}" : "";

    /// <summary>The MHO bones a map row stands for: a bone row its target; a chain row its MHO chain (the root and every bone
    /// under it).</summary>
    List<string> RowBones(int row)
    {
        if (row < 0 || row >= mapGrid.Rows.Count) return [];
        switch (mapGrid.Rows[row].Tag)
        {
            case BoneMapFile.BoneEntry b: return RowTarget(b) is string t ? [t] : [];
            case BoneMapFile.ChainEntry { Mho: string root }:
            {
                int ri = mhoBones.FindIndex(n => n.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (ri < 0) return [root];
                var list = new List<string>();
                for (int i = 0; i < mhoBones.Count; i++)
                    for (int k = i, g = 0; k >= 0 && g < 256 && k < mhoParents.Count; k = mhoParents[k], g++)
                        if (k == ri) { list.Add(mhoBones[i]); break; }
                return list;
            }
            default: return [];
        }
    }

    /// <summary>The selected row's bones are drawn in orange on the skeleton overlay; Smooth Weights acts on them.</summary>
    /// <summary>The bones the Bone Map tab acts on: the selected row's, else a bone picked in the view without a row.</summary>
    List<string> SelectedBones() => mapGrid.CurrentCell != null ? RowBones(mapGrid.CurrentCell.RowIndex) : pickedBone != null ? [pickedBone] : [];

    void MapSelectionChanged()
    {
        if (mapGrid.CurrentCell != null) pickedBone = null;
        var bones = SelectedBones();
        preview.Highlight(bones);
        mapSmooth.Enabled = bones.Count > 0 && MapPath() != null;
    }

    /// <summary>
    /// A bone Ctrl+clicked in the view (0.13.3): the Bone Map tab opens on the row for it: the MFF bone that drives it, else
    /// one whose weights fall to it ("parent → it"), else the chain it belongs to. The filter is cleared when it hides it.
    /// </summary>
    void SelectBoneRow(string bone)
    {
        modelTabs.Select(1);
        int Find()
        {
            int fallback = -1;
            for (int i = 0; i < mapGrid.Rows.Count; i++)
            {
                if (mapGrid.Rows[i].Tag is BoneMapFile.BoneEntry b && RowTarget(b) is string t && t.Equals(bone, StringComparison.OrdinalIgnoreCase))
                {
                    if (b.Mho != null && b.How != "chain") return i;   // the MFF bone that drives it
                    if (fallback < 0) fallback = i;
                }
                else if (fallback < 0 && mapGrid.Rows[i].Tag is BoneMapFile.ChainEntry && RowBones(i).Contains(bone, StringComparer.OrdinalIgnoreCase)) fallback = i;
            }
            return fallback;
        }
        int row = Find();
        if (row < 0 && mapFilter.Text.Length > 0) { mapFilter.Text = ""; row = Find(); }
        if (row >= 0)
        {
            mapGrid.CurrentCell = mapGrid.Rows[row].Cells["mff"];
            mapGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, row - 3);
            Log($"Picked {bone}: {mapGrid.Rows[row].Cells["mff"].Value} → {mapGrid.Rows[row].Cells["mho"].Value}");
        }
        else
        {
            // no row: the bone itself is what Weights shows and Smooth Weights smooths
            mapGrid.CurrentCell = null;
            pickedBone = bone;
            MapSelectionChanged();
            Log($"Picked {bone}: no MFF bone or chain maps to it directly (its weights come from the retarget's blending); Weights and Smooth Weights act on it.");
        }
    }

    /// <summary>One more smoothing click on the selected row's bones, saved in the map (made from the automatic one if
    /// there's none yet); the preview follows.</summary>
    void SmoothSelected()
    {
        if (shownMap == null || MapPath() is not string path) return;
        var bones = SelectedBones();
        if (bones.Count == 0) return;
        foreach (var bone in bones)
        {
            var e = shownMap.Smooth.FirstOrDefault(s => s.Bone.Equals(bone, StringComparison.OrdinalIgnoreCase));
            if (e == null) shownMap.Smooth.Add(e = new BoneMapFile.SmoothEntry { Bone = bone });
            e.Passes++;
        }
        shownMap.Save(path);
        Log($"Smooth weights: {string.Join(", ", bones.Take(6))}{(bones.Count > 6 ? $" and {bones.Count - 6} more" : "")} (saved: {path})");
        FillMap(); SchedulePreview();
    }

    // --- undo / redo (0.12.0, Kurt) ---------------------------------------------------------------------------------------------
    /// <summary>What Undo / Redo restores: the ticked parts, Smooth, the material and the bone map file (its text; null = none).</summary>
    sealed record UiState(bool[] Parts, bool Subdivide, int Material, string? Map, int Cape = 0, int Hair = 0, string Edits = "")
    {
        public bool Same(UiState o) => Parts.SequenceEqual(o.Parts) && Subdivide == o.Subdivide && Material == o.Material && Map == o.Map && Cape == o.Cape && Hair == o.Hair && Edits == o.Edits;
    }
    readonly Stack<UiState> undo = new(), redo = new();
    UiState? committed;
    string? historyKey;
    bool restoring;

    UiState Capture() => new(parts.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["use"].Value is true).ToArray(), smooth.Checked,
        Math.Max(0, material.SelectedIndex), MapPath() is string p && File.Exists(p) ? File.ReadAllText(p) : null, Math.Max(0, capeBox.SelectedIndex), Math.Max(0, hairBox.SelectedIndex), EnsureEdits().Serialize());

    /// <summary>Called as the preview rebuilds (after the short delay, so quick clicks are one step): a change since the
    /// last state becomes an undo step. The history starts over for another character or base hero.</summary>
    void Remember(string package)
    {
        string key = (chosenKey ?? "") + "|" + package;
        var now = Capture();
        if (key != historyKey) { historyKey = key; undo.Clear(); redo.Clear(); committed = now; }
        else if (committed != null && !now.Same(committed) && !restoring) { undo.Push(committed); redo.Clear(); committed = now; }
        else committed = now;
        restoring = false;
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        SaveState();
    }

    void Undo() { if (undo.Count > 0) { redo.Push(Capture()); Restore(undo.Pop(), "Undo"); } }
    void Redo() { if (redo.Count > 0) { undo.Push(Capture()); Restore(redo.Pop(), "Redo"); } }

    void Restore(UiState s, string what)
    {
        restoring = true; committed = s;
        for (int i = 0; i < parts.Rows.Count && i < s.Parts.Length; i++)
            if ((parts.Rows[i].Cells["use"].Value is true) != s.Parts[i]) parts.Rows[i].Cells["use"].Value = s.Parts[i];
        if (smooth.Checked != s.Subdivide) smooth.Checked = s.Subdivide;
        if (material.SelectedIndex != s.Material) material.SelectedIndex = s.Material;
        if (capeBox.SelectedIndex != s.Cape) capeBox.SelectedIndex = s.Cape;
        if (hairBox.SelectedIndex != s.Hair) hairBox.SelectedIndex = s.Hair;
        if (MapPath() is string path)
        {
            if (s.Map == null) { if (File.Exists(path)) File.Delete(path); }
            else { Protected.CheckWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, s.Map); }
            if (shownMap != null && s.Map != null) shownMap = BoneMapFile.Load(path);
        }
        if (EnsureEdits().Serialize() != s.Edits) { edits = AnimEdits.Parse(s.Edits); SaveEdits(); }
        undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
        Log(what + ": back to the earlier parts / Smooth / material / bone map / FBX edits.");
        FillMap(); SchedulePreview();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool typing = ActiveControl is TextBox || (ActiveControl is ContainerControl c && c.ActiveControl is TextBox);
        if (keyData == Keys.F11) { preview.ToggleFull(); return true; }
        if (!typing && keyData == Keys.Escape && preview.PausePlayback()) return true;
        if (!typing && keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
        if (!typing && (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z))) { Redo(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Opens the list of MHO bones for a row; a pick saves the map and redraws the preview.</summary>
    void PickMapTarget(int row)
    {
        if (shownMap == null || MapPath() is not string path) return;
        var tag = mapGrid.Rows[row].Tag;
        if (tag is BoneMapFile.BoneEntry { How: "chain" }) { Log("That bone is set by its chain: change the chain's row instead."); return; }
        bool chain = tag is BoneMapFile.ChainEntry;
        var names = new List<string> { chain ? "(not paired)" : "(nearest mapped parent)" };
        names.AddRange(mhoBones.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        string? current = tag is BoneMapFile.ChainEntry ce ? ce.Mho : (tag as BoneMapFile.BoneEntry)?.Mho;
        int sel = current == null ? 0 : names.FindIndex(n => n.Equals(current, StringComparison.OrdinalIgnoreCase));
        var cell = mapGrid.GetCellDisplayRectangle(mapGrid.Columns["mho"]!.Index, row, false);
        DropList.Show(mapGrid, mapGrid.RectangleToScreen(cell), names, sel, pick =>
        {
            string? to = pick <= 0 ? null : names[pick];
            if (tag is BoneMapFile.ChainEntry c) { c.Mho = to; c.Fit = "edited"; }
            else if (tag is BoneMapFile.BoneEntry b) { b.Mho = to; b.How = "edited"; }
            shownMap.Save(path);
            Log($"Bone map: {(chain ? "chain " : "")}{mapGrid.Rows[row].Cells["mff"].Value} → {to ?? (chain ? "not paired" : "nearest mapped parent")} (saved: {path})");
            FillMap(); SchedulePreview();
        });
    }

    // --- edits from Blender (0.16.0, Kurt) --------------------------------------------------------------------------------------
    /// <summary>The FBX edits of the shown source on the shown base package (kept in its edits folder, edits.txt, like the bone
    /// map: they're there again next time).</summary>
    AnimEdits edits = new();
    string? editsKey;

    string? EditsFolder() => model != null && ChosenPackage is CharacterList.Item pkg ? Path.Combine(host.WorkFolder, "edits", $"{model.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}") : null;

    AnimEdits EnsureEdits()
    {
        string? folder = EditsFolder();
        if (folder == editsKey) return edits;
        editsKey = folder;
        string file = folder != null ? Path.Combine(folder, "edits.txt") : "";
        edits = folder != null && File.Exists(file) ? AnimEdits.Parse(File.ReadAllText(file)).Resolved(folder) : new AnimEdits();
        return edits;
    }

    void SaveEdits()
    {
        if (EditsFolder() is not string folder) return;
        Protected.CheckWrite(folder);
        string file = Path.Combine(folder, "edits.txt");
        if (!edits.Any) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, edits.Relative(folder).Serialize());   // relative: the folder travels with the mod
    }

    void FillEditsMenu(ContextMenuStrip m)
    {
        EnsureEdits();
        if (model == null || ChosenPackage == null) { m.Items.Add(new ToolStripMenuItem("Pick an MFF Character and a Base Hero First") { Enabled = false }); return; }
        string? anim = preview.CurrentAnimation;
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Import FBX for \"{anim}\"" : "Import FBX (Pick an Animation to Replace One)", null, (_, _) => ImportEditFbx(anim)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Export FBX: \"{anim}\" Only" : "Export FBX: Pick an Animation First", null, (_, _) => ExportFbx(false, anim)) { Enabled = anim != null && !building, ToolTipText = "The model and just this animation (the right panel's Export FBX: all of them)." });
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Open \"{anim}\" in Blender" : "Open in Blender: Pick an Animation First", null, (_, _) => ExportFbx(true, anim)) { Enabled = anim != null && !building, ToolTipText = "The model and just this animation in a new Blender scene; Ctrl+S there sends your changes back (the right panel's Open in Blender: all of them)." });
        m.Items.Add(new ToolStripSeparator());
        if (anim != null && edits.Anims.ContainsKey(anim)) m.Items.Add(new ToolStripMenuItem($"Back to the Game's \"{anim}\"", null, (_, _) => { edits.Anims.Remove(anim); EditsChanged($"\"{anim}\" is the game's again"); }));
        if (edits.ModelFbx != null) m.Items.Add(new ToolStripMenuItem("Back to the Retargeted Mesh", null, (_, _) => { edits.ModelFbx = null; EditsChanged("the mesh is the retarget's again"); }));
        if (edits.Anims.Count > 0)
        {
            var list = new ToolStripMenuItem($"Replaced Animations ({edits.Anims.Count})");
            foreach (var (name, file) in edits.Anims) list.DropDownItems.Add(new ToolStripMenuItem($"{name}  ←  {Path.GetFileName(file)}") { Enabled = false });
            m.Items.Add(list);
        }
        if (edits.Any)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Revert All FBX Edits", null, (_, _) => { if (Dialog.Show(this, "Revert every FBX edit of this model on this base hero (the mesh and the replaced animations)? Undo brings them back.", "Revert All FBX Edits", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return; edits = new AnimEdits(); EditsChanged("every FBX edit reverted"); }));
            m.Items.Add(new ToolStripMenuItem("Open the Edits Folder", null, (_, _) => { if (EditsFolder() is string f && Directory.Exists(f)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = false }); }));
        }
    }

    void EditsChanged(string what)
    {
        SaveEdits();
        Log("FBX edits: " + what + ".");
        preview.ReloadAnimation();
        SchedulePreview();   // the mesh may have changed; also records the undo step
    }

    /// <summary>
    /// An FBX from Blender for the animation <paramref name="anim"/> (null: the mesh only): its clip replaces that animation
    /// and / or its skinned mesh replaces the model's, as asked. Clip bones are checked against the base hero's skeleton.
    /// </summary>
    void ImportEditFbx(string? anim)
    {
        if (EditsFolder() is not string folder || ChosenPackage is not CharacterList.Item pkg) return;
        string exported = Path.Combine(Settings.Home, "fbx", $"{model!.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}");
        string start = Directory.Exists(Path.Combine(exported, "anims")) && anim != null ? Path.Combine(exported, "anims") : Directory.Exists(exported) ? exported : Settings.Home;
        using var dlg = new OpenFileDialog { Title = anim != null ? $"FBX for {anim}" : "FBX with the edited mesh", Filter = "FBX (*.fbx)|*.fbx", InitialDirectory = start };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AnimEdits.Contents c;
        try { c = AnimEdits.Inspect(dlg.FileName); }
        catch (Exception ex) { Dialog.Show(this, ex.Message, "FBX Not Read", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        bool hasClip = c.Clips.Count > 0 && anim != null, hasMesh = c.SkinnedMeshes > 0;
        if (!hasClip && !hasMesh)
        {
            Dialog.Show(this, anim == null && c.Clips.Count > 0 ? "This FBX has an animation but no skinned mesh. Pick the animation to replace in the drop-down first." : "This FBX has neither an animation nor a skinned mesh.", "Nothing to Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        bool useClip = hasClip, useMesh = hasMesh;
        if (hasClip && hasMesh)
        {
            int pick = Dialog.Choose(this, $"{Path.GetFileName(dlg.FileName)} has an animation ({c.Clips[0].Name}, {c.Clips[0].Frames:0} frames) and a skinned mesh ({c.SkinnedMeshes} part(s)).\n\nThe animation replaces \"{anim}\". The mesh, with its weights (weight painting from Blender), replaces the model's for every animation. Take the mesh only if you changed it: an FBX exported from here carries the mesh too.",
                "Import FBX", "Animation and Mesh", "Animation Only", "Mesh Only", "Cancel");
            if (pick == 3) return;
            useClip = pick != 2; useMesh = pick != 1;
        }
        else if (hasMesh && Dialog.Show(this, $"{Path.GetFileName(dlg.FileName)} has a skinned mesh ({c.SkinnedMeshes} part(s)){(anim != null ? " and no animation" : "")}. Use it, with its weights, in place of the model's mesh?", "Import FBX", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            var notes = new List<string>();
            if (useClip)
            {
                var clip = AnimExportCli.Fbx.FbxAnimationImporter.Read(dlg.FileName);
                int known = clip.Tracks.Keys.Count(k => mhoBones.Contains(k, StringComparer.OrdinalIgnoreCase));
                if (known < Math.Max(4, clip.Tracks.Count / 2))
                {
                    Dialog.Show(this, $"Only {known} of the animation's {clip.Tracks.Count} bones are bones of {pkg.Title}'s skeleton. Export from here (Export FBX) and edit that file, so the bone names stay the game's.", "Animation Not Imported", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                edits.Anims[anim!] = AnimEdits.Keep(dlg.FileName, folder, anim!);
                notes.Add($"\"{anim}\" ← {Path.GetFileName(dlg.FileName)} ({known} of {clip.Tracks.Count} bones known)");
            }
            if (useMesh)
            {
                edits.ModelFbx = AnimEdits.Keep(dlg.FileName, folder, useClip ? anim! : "model");
                notes.Add($"the mesh ← {Path.GetFileName(dlg.FileName)}");
            }
            EditsChanged(string.Join("; ", notes) + $" (kept in {folder})");
        }
        catch (Exception ex) when (ex is AnimExportCli.Fbx.AnimationImportException or IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "FBX Not Imported", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void ResetMap()
    {
        if (MapPath() is string p && File.Exists(p)) { File.Delete(p); Log("Bone map back to automatic."); }
        SchedulePreview();
    }

    static string UniqueDir(string dir)
    {
        if (!Directory.Exists(dir)) return dir;
        for (int i = 2; ; i++) if (!Directory.Exists($"{dir} ({i})")) return $"{dir} ({i})";
    }

    void OpenFolder()
    {
        if (lastZip == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastZip}\"") { UseShellExecute = false });
    }

    void Log(string line) { log.AppendText(line + Environment.NewLine); }

}

/// <summary>A dark two-line list (title, subtle detail line) in the Mod Manager's look.</summary>
sealed class CharacterList : ListBox
{
    /// <summary>A row; a group header (accordion) has a chevron and its first model's thumbnail; Indent 1 = under a header.</summary>
    public sealed record Item(string Key, string Title, string Detail, bool Header = false, int Indent = 0, bool Expanded = false, string? ThumbKey = null)
    { public override string ToString() => Title; }

    /// <summary>The row's thumbnail (null while it's being made: a dark tile).</summary>
    public Func<Item, Image?>? Thumb { get; set; }

    public CharacterList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
        // Drawn by us into one buffer (the Mod Manager's mod list, 0.33.2): the native owner-draw erases, then draws each row
        // on screen, which flickered (Kurt).
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Math.Min(255, (int)(50 * DeviceDpi / 96f)); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

    // No separate erase: OnPaint covers every pixel.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { m.Result = 1; return; }   // WM_ERASEBKGND
        if (m.Msg is 0x0317 or 0x0318)   // WM_PRINT / WM_PRINTCLIENT (DrawToBitmap): the same painting, copied with GDI (it honours the DC's offset)
        {
            if (m.Msg == 0x0317) base.WndProc(ref m);   // the scroll bar
            using var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using (var bg = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(bg, ClientRectangle));
            IntPtr hbm = bmp.GetHbitmap(), mem = CreateCompatibleDC(m.WParam), old = SelectObject(mem, hbm);
            BitBlt(m.WParam, 0, 0, bmp.Width, bmp.Height, mem, 0, 0, 0x00CC0020);   // SRCCOPY
            SelectObject(mem, old); DeleteDC(mem); DeleteObject(hbm);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == 0x0115) Invalidate();   // WM_VSCROLL: repaint the rows we draw
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    /// <summary>The visible rows and, below the last one, the window gradient, all into the double buffer.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int bottom = 0;
        for (int i = Math.Max(0, TopIndex); i < Items.Count; i++)
        {
            var r = GetItemRectangle(i);
            if (r.Height <= 0 || r.Top >= ClientSize.Height) break;   // past the view (the list gives an empty rectangle there)
            bottom = r.Bottom;
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            OnDrawItem(new DrawItemEventArgs(g, Font, r, i, SelectedIndex == i ? DrawItemState.Selected : DrawItemState.None));
        }
        if (bottom < ClientSize.Height) Ui.PaintGradient(g, this, new Rectangle(0, bottom, ClientSize.Width, ClientSize.Height - bottom));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not Item it) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        if (sel) { using var b = new SolidBrush(Ui.CardSelected); e.Graphics.FillRectangle(b, e.Bounds); }
        else Ui.PaintGradient(e.Graphics, this, e.Bounds);
        float s = DeviceDpi / 96f;
        int x = e.Bounds.X + (int)(8 * s) + (int)(22 * s * it.Indent);
        if (it.Header)
        {
            // chevron: pointing right when shut, down when open
            float cx = x + 5 * s, cy = e.Bounds.Y + e.Bounds.Height / 2f, a = 4.5f * s;
            var pts = it.Expanded ? new[] { new PointF(cx - a, cy - a / 2), new PointF(cx + a, cy - a / 2), new PointF(cx, cy + a / 1.2f) }
                                  : new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 1.2f, cy), new PointF(cx - a / 2, cy + a) };
            var mode = e.Graphics.SmoothingMode;   // restored after: left on, the next rows' fills got soft edges (grey seams)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var chev = new SolidBrush(Ui.Subtle)) e.Graphics.FillPolygon(chev, pts);
            e.Graphics.SmoothingMode = mode;
            x += (int)(16 * s);
        }
        if (Thumb != null)
        {
            // a square at the left: the thumbnail fitted in (kept aspect), on a dark tile while it's being made
            int side = e.Bounds.Height - (int)(6 * s);
            var box = new Rectangle(x - (int)(4 * s), e.Bounds.Y + (int)(3 * s), side, side);
            using (var tile = new SolidBrush(Color.FromArgb(24, 26, 34))) e.Graphics.FillRectangle(tile, box);
            if (Thumb(it) is Image img && img.Width > 0 && img.Height > 0)
            {
                float k = Math.Min((float)box.Width / img.Width, (float)box.Height / img.Height);
                int w = (int)(img.Width * k), h = (int)(img.Height * k);
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(img, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            x = box.Right + (int)(8 * s);
        }
        var r = new Rectangle(x, e.Bounds.Y + (int)(3 * s), e.Bounds.Right - x - (int)(4 * s), e.Bounds.Height / 2);
        using var bold = Ui.Bold(9.5f);
        TextRenderer.DrawText(e.Graphics, it.Title, bold, r, Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        r.Offset(0, e.Bounds.Height / 2 - (int)(3 * s));
        using var small = Ui.Regular(8.5f);
        TextRenderer.DrawText(e.Graphics, it.Detail, small, r, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
