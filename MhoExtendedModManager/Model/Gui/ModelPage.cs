using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

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
    readonly FolderStrip foldersLabel = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 0) };

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
    /// <summary>The source FBX when it has no armature (it's rigged in Blender per base hero: <see cref="RigFor"/>); else null.</summary>
    string? unrigged;
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
        Icons.Make(undoButton, "Undo", Icons.Undo, DeviceDpi / 96f);
        Icons.Make(redoButton, "Redo", Icons.Redo, DeviceDpi / 96f);
        mapReset = Ui.FlatButton("Reset to Automatic", ResetMap, "Forgets your bone map edits for this character on this base hero; the importer's own pairing is used again.");
        defaultParts = Ui.FlatButton("Default Parts", ResetParts, "Back to the parts the importer picks for this model (the body ticked; props, effects and swap parts not).");
        Font = Ui.Regular();
        DoubleBuffered = true;
        BackColor = Color.Transparent;

        buildButton = Ui.AccentButton("Build into Mod", Build, "Builds the model onto the picked package and puts it into this mod (Save Changes keeps it; Apply Changes puts it into the game). Starts from the package as it was before the model, or from the game's stock copy (Build From).");
        openButton = Ui.FlatButton("Open Folder", OpenFolder, "Shows the last export in Explorer (model.fbx).");
        openButton.Enabled = false;
        Icons.Make(openButton, "Open Folder", Icons.Folder, DeviceDpi / 96f);
        Icons.Make(buildButton, "Build into Mod", Icons.Build, DeviceDpi / 96f);
        // Full Export ▾ (0.16.5, Kurt): the model and every animation, as FBX files or straight into Blender (one animation:
        // the Single Animation ▾ menu under the preview)
        fbxButton = Ui.FlatButton("Full Export ▾", ShowFullExportMenu, "The model and all of the base hero's animations: Export FBX (one FBX each, the folder opens) or Open in Blender (a new Blender scene, every animation an Action on the NLA; Ctrl+S there sends your changes back). For one animation: Single Animation ▾ under the preview.");
        Icons.Make(fbxButton, "Full Export", Icons.WithMenu(Icons.Export), DeviceDpi / 96f);

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
        foreach (var b in new[] { undoButton, redoButton }) { b.Margin = new Padding(6, 0, 0, 0); topRight.Controls.Add(b); }
        top.Controls.Add(foldersLabel, 0, 0); top.Controls.Add(topRight, 1, 0);
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
        right.Controls.Add(Column("TARGET", Row(packageFilter, buildFrom), packages), 0, 0);   // (Kurt: Source and Target)
        var partsHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        smooth.Margin = new Padding(0, 6, 10, 0); capeBox.Margin = new Padding(0, 0, 6, 0); defaultParts.Margin = new Padding(0);
        hairBox.Margin = new Padding(0, 0, 6, 0);
        partsHead.Controls.AddRange([smooth, capeBox, hairBox, defaultParts]);
        modelTabs.Add("Parts", Column("", partsHead, parts));
        var mapButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { mapBones, mapWeights, mapSmooth }) { b.Margin = new Padding(0, 0, 6, 0); mapButtons.Controls.Add(b); }
        mapReset.Margin = new Padding(0); mapButtons.Controls.Add(mapReset);
        modelTabs.Add("Bone Map", Column("", Row(mapFilter, mapButtons), mapGrid));
        modelTabs.Add("Materials", MaterialsTab());
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
        mapGrid.CurrentCellChanged += (_, _) => { if (!fillingMap) MapSelectionChanged(); };   // (not mid-refill: see fillingMat)
        SearchBox.AddClear(mapFilter);
        foreach (var m in Materials) material.Items.Add(m.Label);
        material.SelectedIndex = 0;
        material.SelectedIndexChanged += (_, _) => Ui.Tip(material, Materials[Math.Max(0, material.SelectedIndex)].Tip);
        Ui.Tip(material, Materials[0].Tip);
        Ui.Tip(heroesOnly, "List only characters: heroes, villains, bosses, enemies, NPCs and person-like summons (anything with a character rig); props such as cocoons, drones and boxes are left out.");
        Ui.Tip(smooth, "One level of smooth subdivision on the ticked parts: every triangle becomes four and the surface rounds off. For the older low-poly models; about 4x the triangles.");
        smooth.CheckedChanged += (_, _) => SchedulePreview();
        capeBox.Items.Add("No Added Cape"); hairBox.Items.Add("No Added Hair");   // (Kurt: "No Cape Motion" read as removing one)
        for (int k = 1; k <= BorrowedRig.CapeDonors.Length; k++) capeBox.Items.Add($"Add Cape {k}");
        for (int k = 1; k <= BorrowedRig.HairDonors.Length; k++) hairBox.Items.Add($"Add Hair {k}");
        hairBox.Items.Add("Add Mega Hair");
        capeBox.SelectedIndex = 0; hairBox.SelectedIndex = 0;
        capeBox.SelectedIndexChanged += (_, _) => SchedulePreview();
        hairBox.SelectedIndexChanged += (_, _) => SchedulePreview();
        Ui.Tip(capeBox, CapeTip);
        Ui.Tip(hairBox, HairTip);

        SearchBox.AddClear(characterFilter);
        SearchBox.AddClear(packageFilter);
        characterFilter.TextChanged += (_, _) => FillCharacters();
        heroesOnly.CheckedChanged += (_, _) => FillCharacters();
        sourceKind.Items.AddRange(["MFF Characters", "FBX Files"]);
        sourceKind.SelectedIndex = 0;
        Ui.Tip(sourceKind, "Where the model comes from: an MFF character (retargeted onto the base hero), or an FBX file with an MHO skeleton (g_ bone names), e.g. one made with Export FBX and cleaned up in Blender.");
        sourceKind.SelectedIndexChanged += (_, _) =>
        {
            chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear();
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
        characters.Thumb = it => it.Key.StartsWith("browse:") ? null
            : it.Key.StartsWith("fbx:") ? (File.Exists(it.Key[4..]) ? Thumbs.Fbx(it.Key[4..]) : null)   // (its color map: Kurt, 2026-10-04)
            : Thumbs.Model(it.ThumbKey ?? it.Key);
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
        MhoAnim.BaseLookup = ModCopy;   // the export's animations: the hero's base package from the mod first
        Disposed += (_, _) => { Thumbs.Ready -= OnThumbReady; StopBlenderWatch(); if (MhoAnim.BaseLookup == ModCopy) MhoAnim.BaseLookup = null; };
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

    // --- a target with its own cape / hair (Kurt, 2026-10-04): the lists add nothing then, so they're grayed out and say so --------
    const string CapeTip = "Adds a moving cape to a base hero that has none; a hero with its own cape bones (Angela, Doctor Strange, Thor) always keeps them and their motion, and this does nothing for them. No Added Cape adds nothing (it removes nothing either). The added cape: cape bones are added so the model's cape strips ride them, and every animation gets cape motion matched from an MHO hero's hand-animated cape (for each frame, the one whose body moves most alike). Cape 1-3 are Thor's, Doctor Strange's and Vision's. Build includes it when the base hero is a base package (its animation sets are copied into the mod with the cape's motion added), and so do Full Export and Open in Blender.";
    const string HairTip = "Adds moving long hair to a base hero that has none; a hero with its own hair bones (Angela, Psylocke, Black Widow) always keeps them and their motion, and this does nothing for them. No Added Hair adds nothing (it removes nothing either). The added hair: hair bones are added so the model's hair strands ride them, and every animation gets hair motion matched from an MHO hero's hand-animated hair. Hair 1 is short, Hair 3 the longest; Mega Hair stretches Hair 3's strands to the model's own hair length (for manes such as Scream's or Medusa's). Build includes it when the base hero is a base package (its animation sets are copied into the mod with the hair's motion added).";
    static readonly Dictionary<string, (bool Cape, bool Hair)> ownRigs = new(StringComparer.OrdinalIgnoreCase);

    async Task ShowOwnRigs(string packagePath)
    {
        string key = packagePath + "|" + File.GetLastWriteTimeUtc(packagePath).Ticks;
        if (!ownRigs.TryGetValue(key, out var own))
        {
            try
            {
                own = await Task.Run(() =>
                {
                    var names = MhoSkeleton.Load(packagePath, null).Bones.Select(b => b.Name).ToList();
                    return (names.Any(BorrowedRig.Pattern(BorrowedRig.Kind.Cape).IsMatch), names.Any(BorrowedRig.Pattern(BorrowedRig.Kind.Hair).IsMatch));
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
            ownRigs[key] = own;
        }
        if (IsDisposed) return;
        void Set(DropDown box, bool has, string ownText, string noneText, string what)
        {
            if (box.Items.Count == 0) return;
            if (has && box.SelectedIndex != 0) box.SelectedIndex = 0;
            if ((string)box.Items[0]! != (has ? ownText : noneText)) box.Items[0] = has ? ownText : noneText;
            box.Enabled = !has;
            box.Invalidate();
            Ui.Tip(box, has ? $"This hero has {what} bones of its own: they're always kept, with their hand-animated motion, so nothing is added." : box == capeBox ? CapeTip : HairTip);
        }
        Set(capeBox, own.Cape, "Hero's Own Cape", "No Added Cape", "cape");
        Set(hairBox, own.Hair, "Hero's Own Hair", "No Added Hair", "hair");
    }

    /// <summary>Test: the cape / hair lists as shown (text, enabled).</summary>
    internal (string Cape, bool CapeOn, string Hair, bool HairOn) TestOwnRigs =>
        ((string)capeBox.Items[capeBox.SelectedIndex < 0 ? 0 : capeBox.SelectedIndex]!, capeBox.Enabled, (string)hairBox.Items[hairBox.SelectedIndex < 0 ? 0 : hairBox.SelectedIndex]!, hairBox.Enabled);

    // --- data -------------------------------------------------------------------------------------------------------------------
    void Reload()
    {
        var s = Settings.Current;
        foldersLabel.SetFolders([("MFF", s.MffSource, "Not Set"), ("GAME", s.GameFolder, "Not Found"), .. (s.StockFolder != null ? [("STOCK", s.StockFolder, "")] : Array.Empty<(string, string?, string)>())]);
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
        chosenKey = it.Key; sourceFbx = null; unrigged = null;
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
        chosenKey = key; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        try
        {
            // another skeleton family (Mixamo …): read like an MFF model, so its bones are paired with the hero's by the
            // retarget (Bone Map, proportions, scale) instead of needing MHO bone names
            string? whyNot = null;
            string? family = await Task.Run(() => SkeletonProfile.DetectFile(file, out whyNot));
            if (chosenKey != key) return;
            if (family != null)
            {
                var m = await Task.Run(() => MffModel.Load(file));
                if (chosenKey != key) return;
                model = m;
                foreach (var p in m.Parts)
                {
                    string kind = p.IsProp ? "Prop" : p.IsAlternate ? "Swap" : !p.Weighted ? "Unrigged" : "Body";
                    parts.Rows.Add(p.DefaultOn, p.Name, kind, p.Verts.ToString("N0"));
                }
                SchedulePreview();
                Log($"FBX source: {file} ({(family == "Guessed" ? "skeleton guessed from its shape" : family + " skeleton")}, paired with the hero's bones like an MFF model: see the Bone Map tab{(family == "Guessed" ? ", where the guessed pairs are in amber" : "")}; {m.Parts.Count} part(s), {m.Parts.Count(p => p.DefaultOn)} ticked).");
                foreach (var w in m.Warnings) Log("  " + w);
                RestoreState();
                UpdateStatus();
                return;
            }
            bool rigged = await Task.Run(() => AutoRig.HasArmature(file));
            if (chosenKey != key) return;
            if (whyNot != null && rigged) Log($"{Path.GetFileName(file)}: {whyNot}; it's read as an FBX with MHO bone names (g_...).");
            var meshes = await Task.Run(() => FbxReimport.Meshes(file));
            if (chosenKey != key) return;
            sourceFbx = file;
            if (!rigged)
            {
                unrigged = file;
                Log($"{Path.GetFileName(file)} has no armature: on each base hero it's stood up, scaled to the hero and rigged to its skeleton in Blender (Automatic Weights, in the background; all its meshes are used). Full Export ▾ → Open the Rig in Blender to fix the weights; each Ctrl+S there comes back here.");
            }
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
        if (!HasSource && ChosenPackage is CharacterList.Item tpkg)
        {
            // no source yet: the target's own model (as Compare)
            preview.ShowMessage("Loading…");
            try
            {
                var tp = await Task.Run(() => PreviewPanel.PrepareTarget(StartPackage(tpkg.Key)));
                if (id != previewId || IsDisposed) return;
                preview.Show(tp);
                shownMap = null; mhoBones = tp.MhoBones; mhoParents = tp.MhoParents; FillMap();
                shownMaterials = []; FillMaterials();
                _ = ShowOwnRigs(StartPackage(tpkg.Key));
                Log("Preview: " + tp.Note);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                if (id == previewId && !IsDisposed) { preview.ShowMessage("No preview: " + ex.Message); Log("Preview: " + ex.Message); }
            }
            return;
        }
        if (!HasSource) { preview.ShowMessage(FbxMode ? "Pick an FBX (left) and a target (right)" : "Pick a character (left) and a target (right)"); return; }
        if (ChosenPackage is not CharacterList.Item pkg) { preview.ShowMessage("Pick a target on the right"); return; }
        var picked = SelectedParts();
        if (picked.Count == 0) { preview.ShowMessage("Tick at least one part"); return; }
        Remember(pkg.Key);
        var m = model; string parts = string.Join(",", picked); string? donor = Materials[Math.Max(0, material.SelectedIndex)].Value; bool sub = smooth.Checked; string? sfbx = sourceFbx;
        string? uf = unrigged;
        int capeChoice = capeBox.SelectedIndex, hairChoice = hairBox.SelectedIndex;
        preview.ShowMessage(uf != null && !File.Exists(AutoRig.RiggedFbx(AutoRig.Live(uf, pkg.Key))) && !File.Exists(AutoRig.RiggedFbx(AutoRig.Kept(host.WorkFolder, uf, pkg.Key))) ? "Rigging in Blender…" : "Loading…");
        try
        {
            string? mapFile = MapPath() is string mp && File.Exists(mp) ? mp : null;
            string? ovr = OverridesFile();
            string? modelFbx = EnsureEdits().ModelFbx is string mf && File.Exists(mf) ? mf : null;
            var prepared = await Task.Run(() => sfbx != null
                ? PreviewPanel.PrepareFbx(uf != null ? RigFor(uf, pkg.Key) : sfbx, uf != null ? null : picked.ToHashSet(StringComparer.OrdinalIgnoreCase), StartPackage(pkg.Key), donor, mapFile, ovr, modelFbx)
                : PreviewPanel.Prepare(m!, parts, StartPackage(pkg.Key), donor, sub, modelFbx, mapFile, capeChoice, hairChoice, ovr));
            if (id != previewId || IsDisposed) return;   // something changed meanwhile
            preview.Show(prepared);
            shownMap = prepared.Map; mhoBones = prepared.MhoBones; mhoParents = prepared.MhoParents; FillMap();
            shownMaterials = prepared.MaterialList; FillMaterials();
            _ = ShowOwnRigs(StartPackage(pkg.Key));
            WatchBlender();   // this work's Blender folder (a sync that came meanwhile is applied)
            WatchRig(uf != null ? AutoRig.Live(uf, pkg.Key) : null);
            DateTime? fromBlender = uf != null ? rigSent : null;
            // what Automatic picked, shown in the drop-down (0.10.19, Kurt)
            material.Items[0] = prepared.Material.Length > 0 ? $"Automatic · {prepared.Material}" : "Automatic";
            Log("Preview: " + prepared.Note);
            if (fromBlender is DateTime sent)
            {
                // last, so it's the log's visible line (Kurt: the preview's line hid it) and on the status line
                Log($"Blender: the rig you saved (Ctrl+S at {sent:HH:mm:ss}) is loaded.");
                status.Text = $"Rig from Blender loaded (Ctrl+S at {sent:HH:mm:ss})."; status.ForeColor = Ui.Enabled; rigSent = null;
            }
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

    void Log(string line) { log.AppendText(line + Environment.NewLine); }

}
