namespace MhoPackageModifier.Gui;

/// <summary>
/// The Placements tab (the Blender round trip for a tiled zone: --export-placements, edit, --import-placements) and the
/// Zones tab's "export placed meshes for baking" section (--export-placed). Zone presets fill in the layout, region
/// library and offset; "Custom" lets the user give their own.
/// </summary>
sealed partial class MainForm
{
    const string CustomZone = "Custom (enter the files below)";
    readonly ComboBox plZone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly Label plNotes = new() { AutoSize = true, MaximumSize = new Size(1100, 0), Tag = "hint" };
    readonly TextBox plLayout = new(), plLibrary = new(), plOffset = new() { PlaceholderText = "X,Y (tile coordinates to in-game)" };
    readonly TextBox plOut = new(), plSkip = new(), plSkipMat = new() { PlaceholderText = "e.g. water,glass (optional)" };
    readonly NumericUpDown plMinFoot = Num(64, 0, 100000), plMinHeight = Num(0, 0, 100000);
    readonly TextBox plSidecar = new() { PlaceholderText = "<export>_placements.txt, written next to the exported FBX" };
    readonly TextBox plEdited = new() { PlaceholderText = "the FBX you exported from Blender" };
    readonly CheckBox plKeepLighting = new() { Text = "Keep baked lighting (recommended: new pieces glow like their originals; without it they look dark)", AutoSize = true, Checked = true };
    readonly CheckBox plApplyDeletes = new() { Text = "Remove originals I deleted in Blender", AutoSize = true };
    readonly List<Control> plExportControls = new(), plImportControls = new();

    TabPage BuildPlacementsTab()
    {
        plZone.Items.AddRange([.. ZonePresets.All, CustomZone]);
        plZone.SelectedIndexChanged += (_, _) => ApplyZonePreset();
        plSkip.Text = "terrain_flat_filler,godray,lightbeam";

        var zone = new FieldGrid();
        zone.Row("Zone:", plZone);
        zone.Full(plNotes);
        zone.Row("Tile layout:", plLayout, FileButton(plLayout, "Layout (*.txt)|*.txt|All files|*.*"));
        zone.Row("Region library:", plLibrary, FileButton(plLibrary, UpkFilter));
        zone.Row("Offset:", plOffset);

        var export = new FieldGrid();
        export.Full(Hint("Writes every placed mesh of the zone's tiles as its own object (named T<tile>_E<export>_<mesh>, one child per material section) into an FBX, " +
                         "with textures, plus a <name>_placements.txt that the import needs. Nothing in the game changes. An existing file is never overwritten: a new name is used."));
        export.Row("FBX to write:", plOut, FileButton(plOut, FbxFilter, save: true));
        export.Row("Smallest footprint:", plMinFoot);
        export.Row("Smallest height:", plMinHeight);
        export.Full(Hint("Leave out pieces smaller than this (world units; 0 = everything). 64 / 0 keeps thin pieces like awnings and roof edges."));
        export.Row("Skip meshes:", plSkip);
        export.Row("Skip materials:", plSkipMat);
        var exportButtons = NoWrap(Btn("Export for Blender", ExportPlacements), Btn("Open job folder", () => OpenFolder(Path.GetDirectoryName(plOut.Text) ?? "")));
        export.Full(exportButtons);
        plExportControls.AddRange([export]);

        var blender = new FieldGrid();
        blender.Full(Hint(
            "Import the FBX in Blender (File > Import > FBX), then:\n" +
            "• Add a piece: select it with its children (right-click > Select Hierarchy), Shift+D, move it. Copies keep the name plus .001, .002.\n" +
            "• Move, rotate or scale a piece: in Object Mode, on the piece itself.\n" +
            "• Reshape a piece: Edit Mode on its section objects (…_s0, …_s1) is fine: extend a wall, copy windows, delete faces. Duplicated or mirrored section " +
            "objects are fine too, as long as they stay parented under the piece.\n" +
            "• Delete a piece: delete it with its children, and tick \"Remove originals I deleted\" below.\n" +
            "• Don't rename objects, join pieces, or apply transforms to the pieces. Leave the untouched pieces in the file: they tell the tool how Blender placed everything.\n" +
            "• Export the whole scene as FBX (default settings), under a new name in the same folder, e.g. with Blender's +/- file number."));

        var import = new FieldGrid();
        import.Row("Placements file:", plSidecar, FileButton(plSidecar, "Placements (*_placements.txt)|*_placements.txt|All files|*.*"));
        import.Row("Edited FBX:", plEdited, NoWrap(FileButton(plEdited, FbxFilter), Btn("Newest in folder", PickNewestEdited)));
        import.Full(plKeepLighting);
        import.Full(plApplyDeletes);
        import.Full(Hint("Import the whole file again after each change in Blender: the app replaces the earlier import of the same export, so nothing is added twice. " +
                         "Each tile is written once, verified; Undo on the Backups tab steps back. Pieces edited in Edit Mode become new meshes in their tile, without collision."));
        import.Full(NoWrap(
            Btn("Check (dry run)", () => ImportPlacements(dryRun: true)),
            Btn("Write to game…", () => ImportPlacements(dryRun: false)),
            Btn("Self-test", TestPlacements)));
        plImportControls.AddRange([import]);

        plSidecar.Text = settings.PlacementSidecar;
        plEdited.Text = settings.PlacementEdited;

        return Stacked("Placements",
            Heading("Edit a zone's buildings in Blender"),
            Hint("Add missing walls, move, rotate, scale, reshape or delete the placed meshes of a tiled zone. Three steps: export, edit in Blender, bring the changes back."),
            Section("Zone", zone),
            Section("1. Export the zone for Blender", export),
            Section("2. Edit in Blender", blender),
            Section("3. Bring your edits into the game", import));
    }

    ZonePresets.Preset? SelectedPreset() => plZone.SelectedItem as ZonePresets.Preset;

    /// <summary>On load, once the game and export folders are known: the saved zone and files.</summary>
    void InitPlacements()
    {
        plZone.SelectedItem = ZonePresets.All.FirstOrDefault(z => z.Name == settings.PlacementZone) is { } saved ? saved : ZonePresets.All[0];
        if (settings.PlacementFbx.Length > 0) plOut.Text = settings.PlacementFbx;
        bkOut.Text = settings.BakeFbx.Length > 0 ? settings.BakeFbx : Path.Combine(exportFolder.Text, "Hightown_bake", "Hightown_bake.fbx");
    }

    void ApplyZonePreset()
    {
        var p = SelectedPreset();
        bool custom = p == null;
        foreach (var box in new[] { plLayout, plLibrary, plOffset }) box.ReadOnly = !custom;
        if (p != null)
        {
            plLayout.Text = p.Layout;
            plLibrary.Text = Path.Combine(gameFolder.Text, p.Library);
            plOffset.Text = $"{p.Offset.X:0},{p.Offset.Y:0}";
            string job = Path.Combine(exportFolder.Text, $"{p.Name}_placements");
            if (plOut.Text.Length == 0 || !plOut.Text.Contains(p.Name, StringComparison.OrdinalIgnoreCase)) plOut.Text = Path.Combine(job, $"{p.Name}_placements.fbx");
            plNotes.Text = p.Notes + (p.Tiled ? "" : "\nThe Blender round trip isn't available for this zone.");
            settings.PlacementZone = p.Name;
        }
        else plNotes.Text = "Give the tile layout (a text file: one tile name per line, then x and y = 0), the region library the tiles take their meshes from " +
                            "(find it with \"Where does this mesh come from?\" on the Browse tab), and the offset from tile coordinates to in-game positions.";
        bool ok = custom || p!.Tiled;
        foreach (var c in plExportControls.Concat(plImportControls)) c.Enabled = ok;
    }

    void CapturePlacementSettings()
    {
        settings.PlacementFbx = plOut.Text;
        settings.PlacementSidecar = plSidecar.Text;
        settings.PlacementEdited = plEdited.Text;
        settings.BakeFbx = bkOut.Text;
    }

    string OffsetArg(TextBox box) => box.Text.Trim().Length == 0 ? "0,0" : box.Text.Trim().Replace(" ", "");

    /// <summary>A path that doesn't exist yet: the given one, or with _2, _3 ... before the extension.</summary>
    static string FreshPath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 2; ; n++) { string p = Path.Combine(dir, $"{name}_{n}{ext}"); if (!File.Exists(p)) return p; }
    }

    void ExportPlacements()
    {
        if (!File.Exists(plLayout.Text)) { Log($"Tile layout not found: {plLayout.Text}"); return; }
        if (!File.Exists(plLibrary.Text)) { Log($"Region library not found: {plLibrary.Text}"); return; }
        if (plOut.Text.Trim().Length == 0) { Log("Choose where to write the FBX."); return; }
        string outFbx = FreshPath(Path.GetFullPath(plOut.Text.Trim()));
        if (outFbx != Path.GetFullPath(plOut.Text.Trim())) Log($"{Path.GetFileName(plOut.Text)} exists; writing {Path.GetFileName(outFbx)} instead (your files are never overwritten).");
        Directory.CreateDirectory(Path.GetDirectoryName(outFbx)!);
        var args = new List<string> { "--export-placements", gameFolder.Text, plLayout.Text, plLibrary.Text, "--out", outFbx, "--offset", OffsetArg(plOffset),
            "--min-footprint", F(plMinFoot.Value), "--min-height", F(plMinHeight.Value), "--skip", plSkip.Text.Trim() };
        if (plSkipMat.Text.Trim().Length > 0) args.AddRange(["--skip-material", plSkipMat.Text.Trim()]);
        RunCommand("Export placements", [.. args], writes: false, after: () =>
        {
            string side = Path.Combine(Path.GetDirectoryName(outFbx)!, Path.GetFileNameWithoutExtension(outFbx) + "_placements.txt");
            if (!File.Exists(side)) return;
            plOut.Text = outFbx;
            plSidecar.Text = side;
            Log($"Next: open {Path.GetFileName(outFbx)} in Blender, make your changes, export under a new name in the same folder, then step 3.");
            RevealFile(outFbx);
        });
    }

    /// <summary>The newest FBX next to the placements file that isn't an export of this app (those have a _placements.txt) or a self-test.</summary>
    void PickNewestEdited()
    {
        string dir = Path.GetDirectoryName(plSidecar.Text.Length > 0 ? plSidecar.Text : plOut.Text) ?? "";
        if (!Directory.Exists(dir)) { Log("Choose the placements file first."); return; }
        var fbx = Directory.EnumerateFiles(dir, "*.fbx")
            .Where(f => !File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + "_placements.txt")) && !f.EndsWith("_selftest.fbx", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if (fbx == null) { Log($"No edited FBX in {dir} yet."); return; }
        plEdited.Text = fbx;
        Log($"Edited FBX: {Path.GetFileName(fbx)} ({File.GetLastWriteTime(fbx):yyyy-MM-dd HH:mm})");
    }

    string? PlacementLibrary()
    {
        // The sidecar names its library (new exports); otherwise the zone preset's / the typed one.
        string lib = plLibrary.Text.Trim();
        return File.Exists(lib) ? lib : null;
    }

    void ImportPlacements(bool dryRun)
    {
        if (!File.Exists(plSidecar.Text)) { Log($"Placements file not found: {plSidecar.Text}"); return; }
        if (!File.Exists(plEdited.Text)) { Log($"Edited FBX not found: {plEdited.Text}"); return; }
        var args = new List<string> { "--import-placements", gameFolder.Text, plSidecar.Text, plEdited.Text };
        if (plKeepLighting.Checked) args.Add("--keep-lighting");
        if (plApplyDeletes.Checked) args.Add("--apply-deletes");
        if (PlacementLibrary() is string lib) args.AddRange(["--library", lib]);
        if (dryRun) args.Add("--dry-run");
        else if (!Confirm($"Apply {Path.GetFileName(plEdited.Text)} to the zone's tiles?\n\nEach changed tile is verified and written once; an earlier import of the same export is replaced. " +
                          "Originals are kept as .bak; Undo on the Backups tab steps back.")) return;
        RunCommand(dryRun ? "Placements dry run" : "Import placements", [.. args], writes: !dryRun, after: () => { if (!dryRun) { RefreshBackups(); ReopenPackage(); } });
    }

    void TestPlacements()
    {
        if (!File.Exists(plSidecar.Text)) { Log("Choose the placements file first."); return; }
        string exported = Path.Combine(Path.GetDirectoryName(plSidecar.Text)!, Path.GetFileName(plSidecar.Text)[..^"_placements.txt".Length] + ".fbx");
        if (!File.Exists(exported)) { Log($"The exported FBX for this placements file isn't there: {exported}"); return; }
        var args = new List<string> { "--test-placements", gameFolder.Text, plSidecar.Text, exported };
        if (PlacementLibrary() is string lib) args.AddRange(["--library", lib]);
        RunCommand("Placements self-test", [.. args], writes: false);
    }

    /// <summary>Shows a file selected in Explorer.</summary>
    static void RevealFile(string file)
    {
        if (File.Exists(file)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    // ---------------------------------------------------------------- Zones tab: export placed meshes for baking

    readonly ComboBox bkZone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    readonly TextBox bkOut = new(), bkSkip = new(), bkSkipMat = new() { PlaceholderText = "optional" };
    readonly NumericUpDown bkMinHeight = Num(100, 0, 100000), bkMinFoot = Num(100, 0, 100000), bkMaxHeight = Num(0, 0, 100000),
        bkMinZ = Num(-100000, -1000000, 1000000), bkMaxZ = Num(100000, -1000000, 1000000);

    Control BuildBakeSection()
    {
        bkZone.Items.AddRange(ZonePresets.All);
        bkZone.SelectedIndex = 0;
        bkZone.SelectedIndexChanged += (_, _) => { if (bkZone.SelectedItem is ZonePresets.Preset p) bkOut.Text = Path.Combine(exportFolder.Text, $"{p.Name}_bake", $"{p.Name}_bake.fbx"); };
        BakeBuildings();
        var g = new FieldGrid();
        g.Full(Hint("Writes the zone's real placed meshes, in their in-game positions, with materials and textures, as one Blender scene. Bake them there into a " +
                    "low-poly LOD with a texture atlas, or bake the ground top-down into one texture. The zone recipe then uses the baked files (see the manual). Nothing in the game changes."));
        g.Row("Zone:", bkZone);
        g.Row("FBX to write:", bkOut, FileButton(bkOut, FbxFilter, save: true));
        g.Full(NoWrap(Lbl("Presets:"), Btn("Buildings (for LODs)", BakeBuildings), Btn("Flat ground (for a ground bake)", BakeGround)));
        g.Row("Smallest height:", bkMinHeight);
        g.Row("Smallest footprint:", bkMinFoot);
        g.Row("Tallest (0 = any):", bkMaxHeight);
        g.Row("Lowest z:", bkMinZ);
        g.Row("Highest z:", bkMaxZ);
        g.Row("Skip meshes:", bkSkip);
        g.Row("Skip materials:", bkSkipMat);
        g.Full(NoWrap(Btn("Export for baking", ExportForBaking), Btn("Open folder", () => OpenFolder(Path.GetDirectoryName(bkOut.Text) ?? ""))));
        return Section("Export placed meshes for baking in Blender", g);
    }

    void BakeBuildings()
    {
        bkMinHeight.Value = 100; bkMinFoot.Value = 100; bkMaxHeight.Value = 0; bkMinZ.Value = -100000; bkMaxZ.Value = 100000;
        bkSkip.Text = "terrain_flat_filler,godray,lightbeam,fence,tree,pole,railing,cable,ladder"; bkSkipMat.Text = "";
    }

    void BakeGround()
    {
        bkMinHeight.Value = 0; bkMinFoot.Value = 128; bkMaxHeight.Value = 16; bkMinZ.Value = -400; bkMaxZ.Value = 150;
        bkSkip.Text = "godray,lightbeam"; bkSkipMat.Text = "water,cell_dev.,(no material)";
    }

    void ExportForBaking()
    {
        if (bkZone.SelectedItem is not ZonePresets.Preset p) return;
        string lib = Path.Combine(gameFolder.Text, p.Library);
        if (!File.Exists(p.Layout) || !File.Exists(lib)) { Log($"Zone data missing: {p.Layout} / {lib}"); return; }
        string outFbx = FreshPath(Path.GetFullPath(bkOut.Text.Trim()));
        Directory.CreateDirectory(Path.GetDirectoryName(outFbx)!);
        var args = new List<string> { "--export-placed", gameFolder.Text, p.Layout, lib, "--out", outFbx, "--offset", $"{p.Offset.X:0},{p.Offset.Y:0}",
            "--min-height", F(bkMinHeight.Value), "--min-footprint", F(bkMinFoot.Value), "--min-z", F(bkMinZ.Value), "--max-z", F(bkMaxZ.Value), "--skip", bkSkip.Text.Trim() };
        if (bkMaxHeight.Value > 0) args.AddRange(["--max-height", F(bkMaxHeight.Value)]);
        if (bkSkipMat.Text.Trim().Length > 0) args.AddRange(["--skip-material", bkSkipMat.Text.Trim()]);
        RunCommand("Export for baking", [.. args], writes: false, after: () => { bkOut.Text = outFbx; RevealFile(outFbx); });
    }
}
