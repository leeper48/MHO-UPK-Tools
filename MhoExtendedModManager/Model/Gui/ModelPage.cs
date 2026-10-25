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
    readonly CheckBox matchSteps = new() { Text = "Match Steps to Size", AutoSize = true, Checked = true };
    readonly CheckBox keepSourceSize = new() { Text = "Keep the Source's Size", AutoSize = true };
    readonly CheckBox jointFit = new() { Text = "Game Joint Blend", AutoSize = true };
    // Feet (Kurt, 2026-10-22: a slider, not a checkbox; Hulk on Black Widow still poked through): the model moved up or down,
    // starting at the automatic amount (ImportBuild.FeetOffset); double-click goes back to it. A value moved off it is kept.
    readonly LightSlider feetSlider = new() { Label = "Feet", Min = -60, Max = 60, Step = 0.5f, Mark = 0, Dock = DockStyle.Top };
    // Lean (Kurt, 2026-10-23, like Feet): the body above the feet turned at the ankles in the fit, + forward, − back
    readonly LightSlider leanSlider = new() { Label = "Lean", Min = -20, Max = 20, Step = 0.5f, Mark = 0, Dock = DockStyle.Top, Value = 0 };
    float feetAuto;
    bool feetKnown;   // (feetAuto measured for the model shown: Build uses it)
    float? feetCustom;
    /// <summary>Lean: the automatic amount for the model shown, and the user's when moved off it (null: automatic).</summary>
    float leanAuto;
    float? leanCustom;
    bool restoringLean;
    /// <summary>The Lean to fit with: the user's, else automatic (NaN: worked out in the fit).</summary>
    float LeanToUse => leanCustom ?? float.NaN;
    bool restoringFeet;
    readonly LightSlider sizeSlider = new() { Label = "Size", Min = 0.05f, Max = 4f, Step = 0.01f, Mark = 1f, Home = () => 1f, Dock = DockStyle.Top };
    /// <summary>A cape / long hair borrowed from other heroes (preview prototype; Kurt: generic numbers, matched motion).</summary>
    // the bone map editor (0.11.5)
    readonly DataGridView mapGrid = new() { Dock = DockStyle.Fill };
    readonly TextBox mapFilter = new() { PlaceholderText = "Filter bones" };
    readonly Button mapReset, mapSmooth, mapBones, mapWeights;
    readonly Button undoButton, redoButton, historyButton;
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
    /// <summary>From the Game (Kurt, 2026-10-23): a player costume, team-up or NPC as the source (GameSource).</summary>
    bool GameMode => sourceKind.SelectedIndex == 2;
    const string GameSrc = "gamesrc:";
    /// <summary>The Source kind is being set from a mod's saved state (not the user's pick, so not remembered).</summary>
    bool kindByState;
    /// <summary>The FBX file in use as the source (FBX mode); null in MFF mode.</summary>
    string? sourceFbx;
    /// <summary>A source is picked: an MFF model, or an FBX file.</summary>
    bool HasSource => model != null || sourceFbx != null;
    /// <summary>The source FBX when it has no armature (it's rigged in Blender per base hero: <see cref="RigFor"/>); else null.</summary>
    string? unrigged;
    readonly Label status = new() { AutoSize = true, Margin = new Padding(8, 8, 0, 0) };
    /// <summary>Turns while the tab works in the background (Kurt, 2026-10-06): loading, preview, Blender, build, export.</summary>
    readonly Spinner modelSpinner = new() { Shows = w => w.StartsWith("Model:", StringComparison.Ordinal), IdleTip = "Turns while the Model tab works in the background (loading a model, the preview, Blender, a build or an export)." };
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
        ShortcutsUi.Attach(mapBones, "model.bones"); ShortcutsUi.Attach(mapWeights, "model.weights");   // (right-click: their shortcuts)
        preview.BonePicked += SelectBoneRow;
        preview.MaterialPicked += SelectMaterialRow;
        // condition material swaps in the preview (Kurt, 2026-10-12): the target costume and the build's slot order
        preview.TargetClass = () => ChosenPackage is CharacterList.Item it ? ConditionSwaps.ClassOf(it.Key) : null;
        preview.SlotOrder = names => MaterialOverrides.Ordered(names, OverridesFile());
        modelTabs.SelectedChanged += i => preview.PickMaterials = modelTabs.TitleAt(i) == "Materials";
        preview.Override = (ar, orig) => EnsureEdits().Anims.TryGetValue(ar.Name, out var f) && File.Exists(f) ? AnimEdits.ForGame(f, ar, orig) : null;
        preview.IsOverridden = n => edits.Anims.ContainsKey(n);
        preview.FillEditsMenu = FillEditsMenu;
        preview.TogglesChanged += () => { Ui.Lit(mapBones, preview.ShowBones); Ui.Lit(mapWeights, preview.ShowWeights); };
        undoButton = Ui.FlatButton("↶ Undo", Undo, "Undo the last change to the parts, Smooth, the material or the bone map (Ctrl+Z).");
        redoButton = Ui.FlatButton("↷ Redo", Redo, "Redo what Undo took back (Ctrl+Y).");
        undoButton.Enabled = redoButton.Enabled = false;
        ShortcutsUi.Attach(undoButton, "model.undo"); ShortcutsUi.Attach(redoButton, "model.redo");   // (right-click: their shortcuts; tooltips name them)
        historyButton = Ui.FlatButton("History", ShowHistory, "Every Undo step of the Model tab by name (parts, bone map, materials, each paint stroke …), the one now marked; click a step to go back or forward to it.");
        Icons.Make(historyButton, "History", Icons.History, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        Icons.Make(undoButton, "Undo", Icons.Undo, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        Icons.Make(redoButton, "Redo", Icons.Redo, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        mapReset = Ui.FlatButton("Reset to Automatic", ResetMap, "Forgets your bone map edits for this character on this base hero; the importer's own pairing is used again.");
        defaultParts = Ui.FlatButton("Default Parts", ResetParts, "Back to the parts the importer picks for this model (the body ticked; props, effects and swap parts not).");
        Font = Ui.Regular();
        DoubleBuffered = true;
        BackColor = Color.Transparent;

        buildButton = Ui.AccentButton("Build into Mod", Build, "Builds the model onto the picked package and puts it into this mod (Save Changes keeps it; Apply Changes puts it into the game). Starts from the package as it was before the model, or from the game's stock copy (Build From). With no source picked, it writes only Size in Game into the package.");
        openButton = Ui.FlatButton("Open Folder", OpenFolder, "Shows the last export in Explorer (model.fbx).");
        openButton.Enabled = false;
        Icons.Make(openButton, "Open Folder", Icons.Folder, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        Icons.Make(buildButton, "Build into Mod", Icons.Build, MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        // Full Export ▾ (0.16.5, Kurt): the model and every animation, as FBX files or straight into Blender (one animation:
        // the Single Animation ▾ menu under the preview)
        fbxButton = Ui.FlatButton("Full Export ▾", ShowFullExportMenu, "The model and all of the base hero's animations: Export FBX (one FBX each, the folder opens) or Open in Blender (a new Blender scene, every animation an Action on the NLA; Ctrl+S there sends your changes back). For one animation: Single Animation ▾ under the preview.");
        Icons.Make(fbxButton, "Full Export", Icons.WithMenu(Icons.Export), MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // unstyled, a column sizes to its widest child (MEMM 0.37.6)
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));          // the divider above the log
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layoutRoot = root;
        Controls.Add(root);

        // the tab's bar: the folders it reads, Undo / Redo, its settings
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, BackColor = Color.Transparent, Padding = new Padding(4, 2, 4, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var topRight = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { undoButton, redoButton, historyButton }) { b.Margin = new Padding(6, 0, 0, 0); topRight.Controls.Add(b); }
        top.Controls.Add(foldersLabel, 0, 0); top.Controls.Add(topRight, 1, 0);
        root.Controls.Add(top, 0, 0);

        // body: characters | 3D | target
        // (Kurt, 2026-10-06: dividers between the columns, ModelPage.Layout)
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, BackColor = Color.Transparent, Padding = new Padding(6) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 7));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 7));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        root.Controls.Add(body, 0, 1);
        layoutBody = body;
        barSource = Divider(body, true, 0, 2, "col0", "col1");
        barPreview = Divider(body, true, 2, 4, "col1", "col2");
        body.Controls.Add(barSource, 1, 0); body.Controls.Add(barPreview, 3, 0);

        var sourceHead = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        sourceHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceKind.Margin = new Padding(0, 0, 0, 6); sourceKind.Anchor = AnchorStyles.Left;
        sourceHead.Controls.Add(sourceKind, 0, 0);
        var filterRow = Row(characterFilter, heroesOnly); filterRow.Dock = DockStyle.Fill;
        sourceHead.Controls.Add(filterRow, 0, 1);
        var sourceColumn = Column("SOURCE", sourceHead, characters);
        body.Controls.Add(sourceColumn, 0, 0);
        body.Controls.Add(Column("PREVIEW", null, preview), 2, 0);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Color.Transparent, Margin = new Padding(0) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));             // the divider between the list and the tabs
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layoutRight = right;
        // the filter and Build From, then Model ▾ on its own line (shown for a package with several models)
        var targetHead = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        targetHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        targetHead.RowStyles.Add(new RowStyle(SizeType.AutoSize)); targetHead.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var targetRow = Row(packageFilter, buildFrom); targetRow.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        targetHead.Controls.Add(targetRow, 0, 0);
        meshPick.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; meshPick.Margin = new Padding(0, 4, 0, 0);
        targetHead.Controls.Add(meshPick, 0, 1);
        var targetColumn = Column("TARGET", targetHead, packages);
        right.Controls.Add(targetColumn, 0, 0);   // (Kurt: Source and Target)
        barTarget = Divider(right, false, 0, 2, "target", "tabs");
        right.Controls.Add(barTarget, 0, 1);
        var partsHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        InitRigs();
        smooth.Margin = new Padding(0, 6, 10, 0); rigsButton.Margin = new Padding(0, 0, 6, 0); defaultParts.Margin = new Padding(0);
        partsHead.Controls.AddRange([smooth, rigsButton, defaultParts]);
        modelTabs.Add("Parts", PartsTab(Column("", partsHead, parts)));
        var mapButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var b in new[] { mapBones, mapWeights, mapSmooth }) { b.Margin = new Padding(0, 0, 6, 0); mapButtons.Controls.Add(b); }
        mapReset.Margin = new Padding(0); mapButtons.Controls.Add(mapReset);
        var mapHead = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) };
        mapHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mapHead.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mapHead.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var mapTop = Row(mapFilter, mapButtons); mapTop.Dock = DockStyle.Top;
        var paintBox = PaintBar(); paintBox.Dock = DockStyle.Top;
        mapHead.Controls.Add(mapTop); mapHead.Controls.Add(paintBox);
        modelTabs.Add("Bone Map", Column("", mapHead, mapGrid));
        modelTabs.Add("Materials", MaterialsTab());
        // the manual's screenshots (MHO_EXTMM_SNAP_MODELTAB = Parts / Bone Map / Materials): that sub-tab first
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_SNAP_MODELTAB") is { Length: > 0 } snapTab)
            for (int k = 0; k < modelTabs.Count; k++) if (modelTabs.TitleAt(k).Equals(snapTab, StringComparison.OrdinalIgnoreCase)) modelTabs.Select(k);
        right.Controls.Add(modelTabs, 0, 2);
        var materialColumn = Column("MATERIAL", null, material, autoHeight: true);
        right.Controls.Add(materialColumn, 0, 3);
        // the size in game (Kurt, 2026-10-05): the costume's mesh component Scale, relative to the game's (ImportBuild.ApplySize)
        sizeSlider.Height = (int)(30 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi));
        var sizeBox = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) };
        sizeBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sizeSlider.Dock = DockStyle.Fill; sizeSlider.Margin = new Padding(0);
        matchSteps.Margin = new Padding(2, 4, 12, 0);
        feetSlider.Height = sizeSlider.Height; feetSlider.Dock = DockStyle.Fill; feetSlider.Margin = new Padding(0, 2, 0, 0);
        feetSlider.Home = () => feetAuto;
        feetSlider.Format = v => $"{v:+0.0;-0.0;0}" + (feetCustom == null && MathF.Abs(v - feetAuto) < 0.05f ? " Auto" : "");
        var checks = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        keepSourceSize.Margin = new Padding(2, 4, 0, 0);
        jointFit.Margin = new Padding(2, 4, 0, 0);
        checks.Controls.Add(matchSteps); checks.Controls.Add(keepSourceSize); checks.Controls.Add(jointFit);
        sizeBox.RowCount = 4;
        leanSlider.Height = sizeSlider.Height; leanSlider.Dock = DockStyle.Fill; leanSlider.Margin = new Padding(0, 2, 0, 0);
        leanSlider.Home = () => leanAuto;
        leanSlider.Format = v => $"{v:+0.0;-0.0;0}°" + (leanCustom == null && MathF.Abs(v - leanAuto) < 0.05f ? " Auto" : "");
        sizeBox.Controls.Add(sizeSlider, 0, 0); sizeBox.Controls.Add(feetSlider, 0, 1); sizeBox.Controls.Add(leanSlider, 0, 2); sizeBox.Controls.Add(checks, 0, 3);
        var sizeColumn = Column("SIZE IN GAME", null, sizeBox, autoHeight: true);
        right.Controls.Add(sizeColumn, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.AddRange([buildButton, fbxButton, openButton, modelSpinner, status]);
        modelSpinner.Margin = new Padding(8, 6, 0, 0);
        right.Controls.Add(actions, 0, 5);
        body.Controls.Add(right, 4, 0);

        // ⛶ inside the log's top right corner (Kurt: inline with the text field); it travels with the log to full screen
        logFullButton = Ui.FlatButton("⛶", ToggleLogFull, LogFullTip);
        logFullButton.Margin = new Padding(0);
        logFullButton.Cursor = Cursors.Default;
        log.Controls.Add(logFullButton);
        void PlaceLogButton() => logFullButton.Location = new Point(Math.Max(0, log.ClientSize.Width - logFullButton.Width - 4), 4);
        log.Resize += (_, _) => PlaceLogButton();
        logFullButton.SizeChanged += (_, _) => PlaceLogButton();
        logHome = Column("LOG", null, log);
        barLog = LogDivider();
        root.Controls.Add(barLog, 0, 2);
        root.Controls.Add(logHome, 0, 3);
        // folding and the remembered sizes (Kurt, 2026-10-06)
        Foldable(sourceColumn, "source"); Foldable(targetColumn, "target"); Foldable(materialColumn, "material"); Foldable(sizeColumn, "size"); Foldable(logHome, "log");
        HandleCreated += (_, _) => ApplyLayout();

        // parts table: a check per part, its kind, size
        // the check boxes are toggled by our own click / Space handling (0.10.18, Kurt: they couldn't be changed); read only
        // so the grid's own check-box editing can't fight it
        parts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 34, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "part", HeaderText = "Part", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Kind", Width = 80, ReadOnly = true });
        parts.Columns.Add(new DataGridViewTextBoxColumn { Name = "verts", HeaderText = "Vertices", Width = 96, ReadOnly = true });
        parts.Columns.Add(new DataGridViewCheckBoxColumn { Name = "own", HeaderText = "Own Material", Width = 110, ReadOnly = true,
            ToolTipText = "This part gets a material of its own (\"<material> @ <part>\" on the Materials tab), for parts that share one: its own color map, game material and slot. Undo (Ctrl+Z) takes it back." });
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
        Ui.TipTitled(sizeSlider, "Size in Game", "How big the character is in game, compared with the game's own size (100 %). The whole character is scaled as the game scales its own costumes (Spider-Man Homecoming, Carnage …), so the animations keep their shape and the feet stay on the ground. Drag, the mouse wheel or the arrow keys; double-click for 100 %. Compare shows the hero at the game's size.");
        Ui.Tip(matchSteps, "The game moves a character at the same speed whatever its size, so a bigger one would slide its feet. With this on, the costume's run, walk and other movement animations play slower for a bigger character and faster for a smaller one (1 / size), so the steps match the ground. Only this costume's animations change (copied into its own package, as the Animations tab does).");
        matchSteps.CheckedChanged += (_, _) => SaveState();
        Ui.TipTitled(feetSlider, "Feet", "Moves the whole model up (+) or down (-) in game, in units. The game's animations hold the hips at the stock hero's height and the legs hang from there, so a model with shorter legs would hover and one with longer legs would sink into the floor. It starts at the automatic amount for this model and target (marked Auto); move it if the feet still float or sink in an animation. Double-click: back to automatic. The preview shows it; Build writes it.");
        feetSlider.ValueChanged += () => { if (!restoringFeet) preview.FeetShift = feetSlider.Value; };
        Ui.Tip(keepSourceSize, "An MFF model is fitted to the target's height by default (Hulk on Black Widow comes out her height). With this on it keeps its own size against the other MFF characters (Hulk stays Hulk-sized on any hero); Size and Feet then start from there.");
        keepSourceSize.CheckedChanged += (_, _) => { SaveState(); SchedulePreview(); };
        Ui.TipTitled(jointFit, "Game Joint Blend", "Shares the skin around the hips, knees, shoulders and elbows between the limb and the body the way the target's own game model does, over a wide band, instead of the MFF model's narrow handover. It stops the seat creasing and the shoulders and thighs bulging when the hero moves. The pelvis also turns where the game's does. Test feature: compare it in the preview (Look ▾ → Weights shows the result), then in game. The preview shows it after a moment; Build writes it. (MFF models only.)");
        jointFit.CheckedChanged += (_, _) => { SaveState(); SchedulePreview(); };
        Ui.TipTitled(leanSlider, "Lean", "Tips the model forward (+) or back (−), in degrees: half at the ankles (the legs stay straight), half at the hips; the soles stay flat. It starts at the automatic amount (Auto): the head over the feet as the hero's own model stands. Double-click: back to automatic. The preview shows it after a moment; Build writes it. (MFF models only: an FBX keeps its own pose.)");
        leanSlider.Committed += () =>
        {
            if (restoringLean) return;
            leanCustom = MathF.Abs(leanSlider.Value - leanAuto) < 0.05f ? null : leanSlider.Value;
            leanSlider.Invalidate(); SaveState(); SchedulePreview();
        };
        preview.LeanAutoChanged += auto =>
        {
            leanAuto = auto;
            restoringLean = true; leanSlider.Value = leanCustom ?? auto; restoringLean = false;
            leanSlider.Invalidate();
        };
        feetSlider.Committed += () => { feetCustom = MathF.Abs(feetSlider.Value - feetAuto) < 0.05f ? null : feetSlider.Value; preview.FeetShift = feetCustom; feetSlider.Invalidate(); SaveState(); };
        preview.FeetAutoChanged += auto =>
        {
            // another model or target: back to automatic
            if (MathF.Abs(auto - feetAuto) > 0.01f) feetCustom = null;
            feetAuto = auto; feetKnown = true;
            restoringFeet = true; feetSlider.Value = feetCustom ?? auto; restoringFeet = false;
            feetSlider.Invalidate();
        };
        sizeSlider.ValueChanged += () => preview.Size = sizeSlider.Value;
        sizeSlider.Committed += () => { preview.Size = sizeSlider.Value; SchedulePreview(); };

        SearchBox.AddClear(characterFilter);
        SearchBox.AddClear(packageFilter);
        characterFilter.TextChanged += (_, _) => FillCharacters();
        heroesOnly.CheckedChanged += (_, _) => FillCharacters();
        // (Kurt, 2026-10-05: "MFF Repository" for the bulk library, "Single Model" for one FBX file or one MFF character folder)
        sourceKind.Items.AddRange(["MFF Repository", "Single Model", "From the Game"]);
        sourceKind.SelectedIndex = Settings.Current.SourceKind is int sk0 and >= 0 and <= 2 ? sk0 : 2;
        Ui.Tip(sourceKind, "Where the model comes from: MFF Repository = a character from your MFF repository folder (retargeted onto the base hero); Single Model = one FBX file (MHO, Mixamo or other skeleton, or no armature) or one MFF character folder picked on its own; From the Game = a hero costume, team-up or NPC from the game (its model and maps, fitted onto the target like an FBX with MHO bones).");
        sourceKind.SelectedIndexChanged += (_, _) =>
        {
            chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear();
            if (!kindByState && Settings.Current.SourceKind != sourceKind.SelectedIndex) { Settings.Current.SourceKind = sourceKind.SelectedIndex; Settings.Current.Save(); }
            bool mff = sourceKind.SelectedIndex == 0;
            heroesOnly.Visible = mff; smooth.Enabled = mff; if (!mff) smooth.Checked = false;
            characterFilter.Text = "";
            FillCharacters(); UpdateStatus(); SchedulePreview();
        };
        { bool mff0 = sourceKind.SelectedIndex == 0; heroesOnly.Visible = mff0; smooth.Enabled = mff0; }
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
        characters.Thumb = it => it.Key.StartsWith("browse") ? null
            : GameMode ? Thumbs.BaseHero(it.ThumbKey ?? it.Key)
            : it.Key.StartsWith("fbx:") ? (File.Exists(it.Key[4..]) ? Thumbs.Fbx(it.Key[4..]) : null)   // (its color map: Kurt, 2026-10-04)
            : it.Key.StartsWith(MffDir) ? (Directory.Exists(it.Key[MffDir.Length..]) ? Thumbs.Model(it.Key[MffDir.Length..]) : null)
            : Thumbs.Model(it.ThumbKey ?? it.Key);
        packages.Thumb = it => Thumbs.BaseHero(it.ThumbKey ?? it.Key);
        packages.MouseUp += (_, e) =>
        {
            int i = packages.IndexFromPoint(e.Location);
            if (i >= 0 && i < packages.Items.Count && packages.Items[i] is CharacterList.Item { Header: true } h) ToggleHero(h);
            // right-click a target: Remove (Kurt, 2026-10-06)
            else if (e.Button == MouseButtons.Right && !gameList && i >= 0 && i < packages.Items.Count && packages.Items[i] is CharacterList.Item { Header: false } t && !t.Key.Contains(':'))
            {
                packages.SelectedIndex = i;
                var m = new ContextMenuStrip();
                var rm = new ToolStripMenuItem("Remove", null, (_, _) => RemoveTarget(t)) { ShortcutKeyDisplayString = "Del",
                    ToolTipText = added.Contains(t.Key) || host.SavedPath(t.Key) == null ? "Take it off the list and out of the mod (the Model tab added it)." : "One of the mod's own packages: put its own copy back, or take it out of the mod." };
                m.Items.Add(rm);
                m.Closed += (_, _) => BeginInvoke(m.Dispose);
                m.Show(packages, e.Location);
            }
        };
        packages.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space && packages.SelectedItem is CharacterList.Item { Header: true } h) { ToggleHero(h); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete && packages.SelectedItem is CharacterList.Item t) { RemoveTarget(t); e.Handled = true; }
        };
        Thumbs.Ready += OnThumbReady;
        MhoAnim.BaseLookup = ModCopy;   // the export's animations: the hero's base package from the mod first
        Disposed += (_, _) => { Thumbs.Ready -= OnThumbReady; StopBlenderWatch(); if (MhoAnim.BaseLookup == ModCopy) MhoAnim.BaseLookup = null; };
        packages.SelectedIndexChanged += (_, _) => UpdateStatus();

        Ui.Lit(mapBones, preview.ShowBones); Ui.Lit(mapWeights, preview.ShowWeights);
        log.BackColor = Color.FromArgb(22, 22, 26); log.ForeColor = Ui.Subtle; log.Font = new Font("Consolas", 9f * Ui.UiScale);
        previewDelay.Tick += (_, _) => { previewDelay.Stop(); RefreshPreview(); };
        packages.SelectedIndexChanged += (_, _) => SchedulePreview();
        material.SelectedIndexChanged += (_, _) => SchedulePreview();
        parts.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0 && parts.Columns[e.ColumnIndex].Name == "use")
                SetUse([parts.Rows[e.RowIndex]], parts.Rows[e.RowIndex].Cells["use"].Value is not true);
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0 && parts.Columns[e.ColumnIndex].Name == "own")
                ToggleOwnMaterial(parts.Rows[e.RowIndex]);
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
        InitMeshPick();
    }

    void OnThumbReady(string key)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { characters.Invalidate(); packages.Invalidate(); }); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // --- layout helpers -------------------------------------------------------------------------------------------------------
    // --- the log full screen (Kurt, 2026-10-05): the same text box moved into a borderless window on this monitor, so it keeps
    // filling while a build runs, and moved back
    Control? logHome;
    Button? logFullButton;
    Form? logFull;
    const string LogFullTip = "Full screen: the log fills the screen (it keeps filling during a build). Esc or ⛶ come back.";

    void ToggleLogFull()
    {
        if (logFull != null) { logFull.Close(); return; }
        if (logHome is not TableLayoutPanel home || logFullButton == null) return;
        var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.FromControl(this).Bounds,
            BackColor = log.BackColor, KeyPreview = true, ShowInTaskbar = false, Text = "Log", Icon = FindForm()?.Icon,
        };
        var bar = new Label { Text = "LOG", Dock = DockStyle.Top, AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(9f), BackColor = Color.FromArgb(30, 30, 36), Padding = new Padding(10, 6, 0, 6) };   // its own height (a fixed 28 px clipped it at 150 %)
        Ui.Tip(logFullButton, "Back: the log returns to the Model tab (Esc).");
        home.Controls.Remove(log);
        log.Dock = DockStyle.Fill;
        f.Controls.Add(log); f.Controls.Add(bar);
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; f.Close(); } };
        f.FormClosed += (_, _) =>
        {
            f.Controls.Remove(log);
            home.Controls.Add(log, 0, 2);
            logFull = null;
            Ui.Tip(logFullButton, LogFullTip);
            log.SelectionStart = log.TextLength; log.ScrollToCaret();
            f.Dispose();
        };
        var (bc, fc, fo) = (log.BackColor, log.ForeColor, log.Font);
        Ui.Restyle(f);
        (log.BackColor, log.ForeColor, log.Font) = (bc, fc, fo);
        (bar.ForeColor, bar.BackColor, bar.Font) = (Ui.Subtle, Color.FromArgb(30, 30, 36), Ui.Bold(9f));
        logFull = f;
        f.Show(FindForm());
        log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }

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
                if (!(n.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase) || OtherTargets.Contains(n))) continue;
                if (n.StartsWith("UC__MarvelPlayerAudio", StringComparison.OrdinalIgnoreCase) || !seen.Add(n)) continue;
                var (t, det) = DescribePackage(n);
                allPackages.Add((n, t, det));
            }
        allPackages.Sort((a, b) => string.Compare(a.Title + a.Detail, b.Title + b.Detail, StringComparison.OrdinalIgnoreCase));
        if (!stateLoaded) gameList = Settings.Current.TargetFromGame ?? true;   // (a mod's saved target switches back: LoadState)
        FillCharacters();
        FillPackages();
        UpdateStatus();
        LoadDefaultCostumes();
        int others = allPackages.Count(p => OtherTargets.Contains(p.File));
        // (Kurt, 2026-10-06: a user found the old wording unclear)
        Log($"Source: {allCharacters.Count:N0} models in your MFF folder. Target: the mod's own packages, or From the Game: {allPackages.Count - others:N0} hero and team-up packages and {others:N0} NPCs, enemies and bosses.");
        if (!stateLoaded) { stateLoaded = true; LoadState(); }
    }

    /// <summary>The packages of the heroes' default costumes (Kurt, 2026-10-23: reworks and visual updates moved some off the
    /// package named Base or Classic): each hero avatar's StartingCostume → its class's package (Costume.All, the game data).
    /// Null until read.</summary>
    HashSet<string>? defaultCostumes;

    /// <summary>A list title with " •" when the package is a hero's default costume.</summary>
    string Dot(string file, string title) => defaultCostumes?.Contains(file) == true ? title + " •" : title;

    async void LoadDefaultCostumes()
    {
        if (defaultCostumes != null || Settings.Current.GameFolder is not string root) return;
        var set = await Task.Run(() => Costume.All(root)?.Where(c => c.IsDefault && !c.IsTeamUp && !c.Prototype.Contains("zTesting", StringComparison.OrdinalIgnoreCase)).Select(c => c.Package).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (set == null || IsDisposed) return;
        defaultCostumes = set;
        FillCharacters(); FillPackages();
    }

    /// <summary>The NPC and enemy / boss packages offered as targets (Kurt, 2026-10-05): those whose character has the humanoid
    /// skeleton models are fitted to (Model\StockData\model_targets.txt, from --target-census on the stock packages).</summary>
    static readonly HashSet<string> OtherTargets = LoadOtherTargets();

    static HashSet<string> LoadOtherTargets()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(ModelPage).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.model_targets.txt");
        if (s == null) return set;
        using var r = new StreamReader(s);
        for (string? line; (line = r.ReadLine()) != null;)
            if (line.Length > 0 && line[0] != '#') set.Add(line.Split('	')[0]);
        return set;
    }

    /// <summary>"BlackCat_Boss" → "Black Cat Boss".</summary>
    static string Spaced(string s) => System.Text.RegularExpressions.Regex.Replace(s.Replace('_', ' '), @"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])", " ").Trim();

    /// <summary>"Punisher", "Classic · UC__MarvelPlayer_Punisher_Classic_SF" from the file name; an NPC or enemy: its group
    /// ("NPCs", "Enemies and Bosses") and "Black Cat Boss · UC__MarvelAgent_BlackCat_Boss_SF".</summary>
    static (string Title, string Detail) DescribePackage(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);
        foreach (var (prefix, group) in new[] { ("UC__MarvelNPC_", "NPCs"), ("UC__MarvelAgent_", "Enemies and Bosses") })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string name = stem[prefix.Length..];
                if (name.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
                return (group, $"{Spaced(name)} · {stem}");
            }
        bool teamUp = stem.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase);
        if (!teamUp && !stem.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase))
        {
            // any other package (Browse for a Package): its name, readable
            string plain = System.Text.RegularExpressions.Regex.Replace(stem, @"^(UC__|ICO__|SCS__)|_SF$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return (Spaced(plain.Replace('_', ' ')), $"Package · {stem}");
        }
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
        if (GameMode) { FillGameSources(); return; }
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

    /// <summary>A Single Model pick that is one MFF character folder (outside the repository): "mffdir:" + its path.</summary>
    const string MffDir = "mffdir:";

    /// <summary>Single Model's list: Browse (an FBX, an MFF folder) first, then the exports (data\fbx\*\model.fbx) and the
    /// FBX files and MFF folders picked before.</summary>
    void FillFbx()
    {
        string f = characterFilter.Text.Trim();
        characters.BeginUpdate(); characters.Items.Clear();
        characters.Items.Add(new CharacterList.Item("browse:", "Browse for a Model", "FBX, OBJ, DAE, STL, Blender or XPS: MHO, Mixamo or other skeleton, no armature, or an MFF character's model"));
        foreach (var dir0 in Settings.Current.RecentFbx.Where(Directory.Exists))
        {
            string name = Path.GetFileName(dir0.TrimEnd('\\', '/'));
            var (title, _) = MffNames.Describe(name);
            if (f.Length > 0 && !$"{title} {name} {dir0}".Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            characters.Items.Add(new CharacterList.Item(MffDir + dir0, title, $"MFF character · {dir0}"));
        }
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

    /// <summary>From the Game's list: the same heroes, team-ups and NPCs as the target's game list, one header per hero with
    /// several packages.</summary>
    void FillGameSources()
    {
        string f = new string(characterFilter.Text.Where(char.IsLetterOrDigit).ToArray());
        int top = characters.TopIndex;
        characters.BeginUpdate(); characters.Items.Clear();
        var shown = allPackages.Where(p => f.Length == 0 || new string((p.Title + p.Detail).Where(char.IsLetterOrDigit).ToArray()).Contains(f, StringComparison.OrdinalIgnoreCase));
        var groups = shown.GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase);
        if (f.Length > 0) groups = groups.OrderBy(g => g.Min(p => TargetKind(p.File)));
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count == 1) { var p = list[0]; characters.Items.Add(new CharacterList.Item(GameSrc + p.File, Dot(p.File, p.Title), p.Detail, ThumbKey: p.File)); continue; }
            bool open = f.Length > 0 || expanded.Contains("game " + g.Key) || list.Any(p => (GameSrc + p.File).Equals(chosenKey ?? pendingState?.Source, StringComparison.OrdinalIgnoreCase));
            var first = list.FirstOrDefault(p => p.Detail.StartsWith("Base Package", StringComparison.OrdinalIgnoreCase));
            characters.Items.Add(new CharacterList.Item("group:game " + g.Key, list[0].Title, $"{list.Count} packages", Header: true, Expanded: open, ThumbKey: (first == default ? list[0] : first).File));
            if (open)
                foreach (var p in list)
                {
                    int dot = p.Detail.IndexOf(" · ", StringComparison.Ordinal);
                    characters.Items.Add(new CharacterList.Item(GameSrc + p.File, Dot(p.File, dot > 0 ? p.Detail[..dot] : p.Detail), dot > 0 ? p.Detail[(dot + 3)..] : p.File, Indent: 1, ThumbKey: p.File));
                }
        }
        characters.EndUpdate();
        if (chosenKey != null) Reselect(characters, chosenKey);
        if (top < characters.Items.Count && f.Length == 0) characters.TopIndex = top;
    }

    /// <summary>A game package picked as the source: its character written as an FBX once (GameSource), then read as one.</summary>
    async void GameChosen(string key)
    {
        string file = key[GameSrc.Length..];
        chosenKey = key; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        string fbx;
        using (Busy.Begin($"Model: reading {file} from the game"))
            try { fbx = await Task.Run(() => GameSource.Export(file, line => BeginInvoke(() => Log(line)))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { Log($"{file}: {ex.Message}"); UpdateStatus(); return; }
        if (chosenKey != key) return;
        FbxChosen(key, fbx);
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
        // while filtering (Kurt, 2026-10-19): heroes first, then team-ups, then NPCs and the rest; each kind in its own order
        var groups = shown.GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase);
        if (f.Length > 0) groups = groups.OrderBy(g => g.Min(p => TargetKind(p.File)));
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count == 1) { var p = list[0]; packages.Items.Add(new CharacterList.Item(p.File, Dot(p.File, p.Title), p.Detail)); continue; }
            bool open = f.Length > 0 || expandedHeroes.Contains(g.Key) || list.Any(p => p.File.Equals(keep, StringComparison.OrdinalIgnoreCase));
            var first = list.FirstOrDefault(p => p.Detail.StartsWith("Base Package", StringComparison.OrdinalIgnoreCase));
            packages.Items.Add(new CharacterList.Item("hero:" + g.Key, list[0].Title, $"{list.Count} packages", Header: true, Expanded: open, ThumbKey: (first == default ? list[0] : first).File));
            if (open)
                foreach (var p in list)
                {
                    int dot = p.Detail.IndexOf(" · ", StringComparison.Ordinal);
                    packages.Items.Add(new CharacterList.Item(p.File, Dot(p.File, dot > 0 ? p.Detail[..dot] : p.Detail), dot > 0 ? p.Detail[(dot + 3)..] : p.File, Indent: 1));
                }
        }
        packages.EndUpdate();
        if (keep != null) Reselect(packages, keep);
        if (top < packages.Items.Count && f.Length == 0) packages.TopIndex = top;
        UpdateStatus();
    }

    /// <summary>0 a hero's package, 1 a team-up's, 2 anything else (NPCs, pets …).</summary>
    static int TargetKind(string file) =>
        file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? 0 : file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

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
        if (it.Key.StartsWith(GameSrc)) { GameChosen(it.Key); return; }
        chosenKey = it.Key; sourceFbx = null; unrigged = null;
        model = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        using var busy = Busy.Begin("Model: loading the MFF character");
        // an MFF folder picked on its own (Single Model) loads by its path; a repository character by its folder name
        string src = it.Key.StartsWith(MffDir) ? it.Key[MffDir.Length..] : it.Key;
        try
        {
            var m = await Task.Run(() => MffModel.Load(Source.ResolveModelFile(src)));
            if ((characters.SelectedItem as CharacterList.Item)?.Key != it.Key) return;   // another one was picked meanwhile
            model = m;
            foreach (var p in m.Parts)
            {
                string kind = p.IsProp ? "Prop" : p.IsAlternate ? "Swap" : !p.Weighted ? "Unrigged" : "Body";
                parts.Rows.Add(p.DefaultOn, p.Name, kind, p.Verts.ToString("N0"));
            }
            // Suggest the base hero: filter the packages to the character's name, or the simplest form of it that finds any
            // ("Hulkbuster (Iron Man Mark 44)" → "Hulkbuster", "Iron Man Mark 44", "Iron Man" ...).
            var (title, _) = MffNames.Describe(Path.GetFileName(src.TrimEnd('\\', '/')));
            if (gameList && title != it.Key && SuggestFilter(title) is string sf) packageFilter.Text = sf;
            SchedulePreview();
            Log($"{it.Title} ({it.Key}): {m.Parts.Count} parts, {m.Parts.Count(p => p.DefaultOn)} ticked by default.");
            RestoreState();
            PropTicks(changed: false);
        }
        catch (Exception ex) { Log($"{it.Key}: {ex.Message}"); }
        UpdateStatus();
    }

    /// <summary>Picks any model file and adds it to the list (remembered). An MFF character's model (Bip001 bones; Kurt,
    /// 2026-10-06: one Browse, the MFF structure recognized) is read as that MFF character: its folder, its textures, the MFF
    /// retarget, as a repository character is.</summary>
    async void BrowseFbx()
    {
        // the Models Folder (Settings → Model), else where the last model was picked from, else data\model\fbx (a user,
        // 2026-10-08: it always opened in the app's own folder)
        string? start = new[] { Settings.Current.ModelsFolder, Settings.Current.LastModelFolder }.FirstOrDefault(d => d != null && Directory.Exists(d));
        if (start == null) { start = Path.Combine(Settings.Home, "fbx"); Directory.CreateDirectory(start); }
        using var dlg = new OpenFileDialog { Title = "A Model (FBX, OBJ, DAE, STL, Blender or XPS)", Filter = ModelConvert.Filter, InitialDirectory = start };
        if (dlg.ShowDialog(this) != DialogResult.OK) { if (chosenKey != null) Reselect(characters, chosenKey); return; }
        string file = dlg.FileName;
        Settings.Current.LastModelFolder = Path.GetDirectoryName(file);
        Settings.Current.Save();
        // .blend and XPS (Kurt, 2026-10-06): an FBX made by Blender stands in for the file (kept until the file changes)
        if (ModelConvert.NeedsBlender(file))
        {
            status.Text = "Reading It with Blender…"; status.ForeColor = Ui.Subtle;
            Log($"{Path.GetFileName(file)}: read through Blender (an FBX is made from it once, until the file changes)…");
            string src = file;
            using var busy = Busy.Begin($"Model: reading {Path.GetFileName(src)} with Blender");
            try { file = await Task.Run(() => ModelConvert.ToFbx(src, line => BeginInvoke(() => Log(line)))); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            { Log($"{Path.GetFileName(src)}: {ex.Message}"); UpdateStatus(); if (chosenKey != null) Reselect(characters, chosenKey); return; }
            UpdateStatus();
        }
        string dir = Path.GetDirectoryName(file)!;
        Cursor = Cursors.WaitCursor;
        // an MFF character's model: Bip001 bones, or an OBJ / DAE in an MFF character's folder (hero_…)
        bool mff = !ModelConvert.IsStl(file) && (SkeletonProfile.IsMff(file)
            || (!file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(dir).StartsWith("hero_", StringComparison.OrdinalIgnoreCase)));
        Cursor = Cursors.Default;
        string entry = file;
        if (mff)
        {
            // the MFF character's folder (its model must be the one the folder resolves to: one model per character folder)
            string? resolved = null;
            try { resolved = Source.ResolveModelFile(dir); } catch (FileNotFoundException) { }
            if (resolved != null && Path.GetFullPath(resolved).Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) entry = dir;
            else if (!file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) { Log($"{Path.GetFileName(file)}: its folder holds another model first ({Path.GetFileName(resolved ?? "")}); put the MFF character in a folder of its own."); if (chosenKey != null) Reselect(characters, chosenKey); return; }
            if (entry == dir) Log($"{Path.GetFileName(file)}: an MFF character's model (Bip001 skeleton): read with its folder's textures, as an MFF character.");
        }
        var recent = Settings.Current.RecentFbx;
        recent.RemoveAll(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, entry);
        if (recent.Count > 20) recent.RemoveRange(20, recent.Count - 20);
        Settings.Current.Save();
        FillFbx();
        Reselect(characters, entry == dir ? MffDir + dir : "fbx:" + file);
    }

    /// <summary>An FBX file as the source: its meshes become the parts (all ticked); the base hero is suggested from an
    /// export's folder name ("… on UC__…").</summary>
    /// <param name="file">The FBX, when the key isn't "fbx:" + it (From the Game).</param>
    async void FbxChosen(string key, string? file = null)
    {
        file ??= key[4..];
        chosenKey = key; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear(); UpdateStatus();
        status.Text = "Loading…";
        using var busy = Busy.Begin("Model: loading the model");
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
            // (a user, 2026-10-06: a model with a skeleton the tool doesn't know (neither MHO names, a known family nor one it
            // can guess) stopped at "only 0 of the FBX's bones are bones of this MHO skeleton": it's rigged again in Blender
            // instead, as a model without bones; its own skeleton and weights are left out)
            if (rigged && await Task.Run(() => AutoRig.MhoBoneCount(file)) < 4)
            {
                if (chosenKey != key) return;
                Log($"{Path.GetFileName(file)}: its skeleton isn't one this tool knows{(whyNot != null ? " (" + whyNot + ")" : "")}, so it's rigged again to the hero in Blender, as a model without bones (its own bones and weights are left out).");
                rigged = false;
            }
            else if (whyNot != null && rigged) Log($"{Path.GetFileName(file)}: {whyNot}; it's read as an FBX with MHO bone names (g_...).");
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
        buildButton.Enabled = ready || (ChosenPackage != null && !building);   // no source: the size only
        fbxButton.Enabled = ready;
        status.Text = building ? "Building…" : !HasSource ? (ChosenPackage != null ? "No source: Build into Mod writes only the size." : FbxMode ? "Pick an FBX or an MFF folder." : GameMode ? "Pick a character from the game." : "Pick an MFF character.") : ChosenPackage == null ? "Pick the package to build onto." : "Ready to build.";
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
        PreviewPanel.ModsNow = [.. host.Packages];   // (the mod's packages: its borrowed powers' animations and power packages)
        if (HasSource) packageMaps = null;
        using var busy = Busy.Begin("Model: preview");
        if (!HasSource && ChosenPackage is CharacterList.Item tpkg)
        {
            // no source yet: the target's own model (as Compare)
            preview.ShowMessage("Loading…");
            try
            {
                // the mod's own copy as it is now (a model built into it earlier, its textures edited here), else the start
                string shownPath = ModCopy(tpkg.Key) is string mc && File.Exists(mc) ? mc : StartPackage(tpkg.Key);
                var tp = await Task.Run(() => PreviewPanel.PrepareTarget(shownPath));
                if (id != previewId || IsDisposed) return;
                preview.Show(tp);
                shownMap = null; mhoBones = tp.MhoBones; mhoParents = tp.MhoParents; FillMap();
                // its textures, to edit in place (Kurt, 2026-10-06): only when the model is the package's own
                List<PackageMap>? maps = null;
                if (tp.MainRef is { } mainRef && Path.GetFullPath(mainRef.File).Equals(Path.GetFullPath(shownPath), StringComparison.OrdinalIgnoreCase))
                    try { maps = await Task.Run(() => ListPackageMaps(shownPath, mainRef)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { Log("Materials: " + ex.Message); }
                if (id != previewId || IsDisposed) return;
                packageMaps = maps; packageMapsFile = shownPath;
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
        var m = model; string parts = string.Join(",", picked); string? donor = Materials[Math.Max(0, material.SelectedIndex)].Value; bool sub = smooth.Checked; string? sfbx = sourceFbx; bool keepSize = keepSourceSize.Checked; float lean = LeanToUse; bool joints = jointFit.Checked;
        string? uf = unrigged;
        int capeChoice = CapeChoice, hairChoice = HairChoice;
        var moreRigs = OtherRigs();
        preview.ShowMessage(uf != null && !File.Exists(AutoRig.RiggedFbx(AutoRig.Live(uf, pkg.Key))) && !File.Exists(AutoRig.RiggedFbx(AutoRig.Kept(host.WorkFolder, uf, pkg.Key))) ? "Rigging in Blender…" : "Loading…");
        try
        {
            string? mapFile = MapPath() is string mp && File.Exists(mp) ? mp : null;
            string? ovr = OverridesFile();
            string? modelFbx = EnsureEdits().ModelFbx is string mf && File.Exists(mf) ? mf : null;
            string? paintFile = PaintPath() is string pp && File.Exists(pp) ? pp : null;
            var prepared = await Task.Run(() => sfbx != null
                ? PreviewPanel.PrepareFbx(uf != null ? RigFor(uf, pkg.Key) : sfbx, uf != null ? null : picked.ToHashSet(StringComparer.OrdinalIgnoreCase), StartPackage(pkg.Key), donor, mapFile, ovr, modelFbx, paintFile)
                : PreviewPanel.Prepare(m!, parts, StartPackage(pkg.Key), donor, sub, modelFbx, mapFile, capeChoice, hairChoice, ovr, moreRigs, keepSize, lean, joints, paintFile));
            if (id != previewId || IsDisposed) return;   // something changed meanwhile
            preview.Show(prepared);
            shownMap = prepared.Map; mhoBones = prepared.MhoBones; mhoParents = prepared.MhoParents; FillMap();
            shownMaterials = prepared.MaterialList; FillMaterials();
            if (BlenderLink().Folder is string paintFolder && Directory.Exists(paintFolder)) ApplyPaintedTextures(paintFolder);   // painted while the tab was closed
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
        if (propTarget != null && PropFit.BestPart(model, propTarget) is { } best)   // a prop target: the part most like it
        {
            for (int i = 0; i < parts.Rows.Count && i < model.Parts.Count; i++) parts.Rows[i].Cells["use"].Value = model.Parts[i] == best;
            return;
        }
        for (int i = 0; i < parts.Rows.Count && i < model.Parts.Count; i++)
            if ((parts.Rows[i].Cells["use"].Value is true) != model.Parts[i].DefaultOn) parts.Rows[i].Cells["use"].Value = model.Parts[i].DefaultOn;
    }

    /// <summary>The picked Character when it's a prop (a weapon or shield: PropFit.IsProp), else null.</summary>
    MhoSkeleton? propTarget;

    /// <summary>
    /// Parts for a prop target (Kurt, 2026-10-08: an MFF weapon or shield in place of the game's): when Model ▾ picks a
    /// prop, the MFF part most like it is ticked alone (PropFit.BestPart); back on a body, the importer's own choice. On a new
    /// source (<paramref name="changed"/> false) only parts still as the importer ticked them are changed.
    /// </summary>
    void PropTicks(bool changed) { propTicks = PropTicksAsync(changed, ++propTicksRun); FillTargetProps(); }

    /// <summary>The latest prop check's number: an older one still loading (the restored pick) doesn't override a newer pick.</summary>
    int propTicksRun;

    /// <summary>The last prop check (Build waits for it, so it builds with the parts it ticks).</summary>
    Task propTicks = Task.CompletedTask;

    async Task PropTicksAsync(bool changed, int run)
    {
        if (ChosenPackage is not CharacterList.Item pkg) return;
        string key = pkg.Key, path = StartPackage(key);
        MhoSkeleton? sk;
        try { sk = await Task.Run(() => MhoSkeleton.Load(path, null)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { sk = null; }
        if (IsDisposed || ChosenPackage?.Key != key || run != propTicksRun) return;
        bool wasProp = propTarget != null;
        propTarget = sk != null && PropFit.IsProp(sk) ? sk : null;
        if (model == null) return;
        bool asImporter = Enumerable.Range(0, Math.Min(parts.Rows.Count, model.Parts.Count)).All(i => (parts.Rows[i].Cells["use"].Value is true) == model.Parts[i].DefaultOn);
        if (propTarget != null && (changed || asImporter) && PropFit.BestPart(model, propTarget) is { } best)
        {
            for (int i = 0; i < parts.Rows.Count && i < model.Parts.Count; i++)
                if ((parts.Rows[i].Cells["use"].Value is true) != (model.Parts[i] == best)) parts.Rows[i].Cells["use"].Value = model.Parts[i] == best;
            Log($"{propTarget.Name} is a prop (a weapon or shield): only {best.Name}, the part most like it, is ticked. It's laid over the game's prop, scaled to its size and held where the game holds it; tick other parts to use them instead.");
        }
        else if (propTarget == null && wasProp && changed) ResetParts();
    }

    List<string> SelectedParts() => parts.Rows.Cast<DataGridViewRow>().Where(r => r.Cells["use"].Value is true).Select(r => (string)r.Cells["part"].Value).ToList();

    void Log(string line) { log.AppendText(line + Environment.NewLine); }

}
