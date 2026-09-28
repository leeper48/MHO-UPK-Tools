namespace MhoPackageModifier.Gui;

/// <summary>
/// The Objects tab: copy an object (material, mesh, texture, component, actor) with everything it needs into another
/// package (--copy-export), take placed meshes off a tile (--remove-components), and find material instances by parent
/// and static switches (--find-mic).
/// </summary>
sealed partial class MainForm
{
    readonly TextBox cpSource = new(), cpExport = new() { PlaceholderText = "full path, e.g. madripoor_shops.madripoor_shops_misc_mat" };
    readonly TextBox cpTarget = new(), cpRename = new() { PlaceholderText = "optional: a new name (recommended when the copy will be changed)" };
    readonly TextBox cpRefs = new() { Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, PlaceholderText = "one per line: source.object.path=target.object.path   (or =none)" };
    readonly TextBox cpCut = new() { PlaceholderText = "optional, e.g. physmaterial" };
    readonly TextBox rmPackage = new(), rmMesh = new() { PlaceholderText = "part of the mesh name, e.g. terrain_flat_filler" };
    readonly TextBox rmMaterial = new() { PlaceholderText = "part of the material name, e.g. water" }, rmLibrary = new() { PlaceholderText = "optional: region library, to match meshes by their materials there" };
    readonly TextBox fmParent = new() { PlaceholderText = "part of the parent material's name, e.g. storefront" };
    readonly TextBox fmSwitches = new() { PlaceholderText = "optional: switch=true|false ..., e.g. useemissive=true" };
    readonly NumericUpDown fmLimit = Num(20, 1, 1000);

    TabPage BuildObjectsTab()
    {
        var copy = new FieldGrid();
        copy.Full(Hint("Copies an object and everything it references (textures, expressions, sub-materials…) into another package, renumbered, and checks every " +
                       "reference of the copy. An object is known by its path: a copy with the same path as an object in another package replaces that object for " +
                       "everything loaded after it, so give a copy you'll change a new name. \"Replace references\" points the copy at objects the target already " +
                       "has (e.g. your imported texture) instead of copying them, or at nothing (=none)."));
        copy.Row("From Package:", cpSource, NoWrap(FileButton(cpSource, UpkFilter), Btn("Use Open Package", () => cpSource.Text = packagePath)));
        copy.Row("Object:", cpExport, Btn("Use Browse Selection", () => { if (package != null && SelectedExport() is int i) { cpSource.Text = packagePath; cpExport.Text = package.PathOf(package.Exports[i]); } }));
        copy.Row("Into Package:", cpTarget, NoWrap(FileButton(cpTarget, UpkFilter), Btn("Use Open Package", () => cpTarget.Text = packagePath)));
        copy.Row("New Name:", cpRename);
        copy.Row("Replace References:", cpRefs);
        copy.Row("Leave Out Properties:", cpCut);
        copy.Full(Hint("Leaving out physmaterial (footstep sounds and splashes) is fine. Never leave out materialfunctioninfos: the material then renders as the default checker. " +
                       "A copied actor must also be added to its level's actor list (Tools: --add-level-actor)."));
        copy.Full(NoWrap(
            Btn("What Would Be Copied?", () => { if (CopyReady(false)) RunCommand("Dependencies", ["--export-deps", cpSource.Text, cpExport.Text.Trim(), .. CutArgs()], false); }),
            Btn("Check (Dry Run)", () => CopyObject(dryRun: true)),
            Btn("Copy into Game File…", () => CopyObject(dryRun: false))));

        var remove = new FieldGrid();
        remove.Full(Hint("Takes placed meshes off their tile's list so the game stops drawing them (their data stays in the package). Both filters must match; " +
                         "give at least one. Example: the stock water planes are mesh terrain_flat_filler with a water material (add the mesh filter: sidewalk fillers and fountains use water too)."));
        remove.Row("Package:", rmPackage, NoWrap(FileButton(rmPackage, UpkFilter), Btn("Use Open Package", () => rmPackage.Text = packagePath)));
        remove.Row("Mesh Contains:", rmMesh);
        remove.Row("Material Contains:", rmMaterial);
        remove.Row("Library:", rmLibrary, FileButton(rmLibrary, UpkFilter));
        remove.Full(NoWrap(Btn("Check (Dry Run)", () => RemoveComponents(dryRun: true)), Btn("Remove from Game File…", () => RemoveComponents(dryRun: false))));

        var find = new FieldGrid();
        find.Full(Hint("Shaders are compiled into the game, so a material can only use a combination of switches (translucent, emissive, masked…) that some existing " +
                       "material already has. Find one here, copy it above under a new name, and change only its textures and parameters. Read-only; scans the game folder."));
        find.Row("Parent Contains:", fmParent);
        find.Row("Switches:", fmSwitches);
        find.Row("Show at Most:", fmLimit);
        find.Full(NoWrap(Btn("Find Material Instances", () =>
        {
            if (fmParent.Text.Trim().Length == 0) { Log("Give part of the parent material's name."); return; }
            RunCommand("Find Material Instances", ["--find-mic", gameFolder.Text, fmParent.Text.Trim(), .. SplitArgs(fmSwitches.Text), "--limit", F(fmLimit.Value)], false);
        })));

        return Stacked("Objects",
            Heading("Copy, Remove and Find Objects"),
            Section("Copy an Object into Another Package", copy),
            Section("Remove Placed Meshes from a Tile", remove),
            Section("Find a Material with the Features You Need", find));
    }

    string[] CutArgs() => cpCut.Text.Trim().Length > 0 ? ["--cut", cpCut.Text.Trim().Replace(" ", "")] : [];

    bool CopyReady(bool needTarget)
    {
        if (!File.Exists(cpSource.Text)) { Log($"Source package not found: {cpSource.Text}"); return false; }
        if (cpExport.Text.Trim().Length == 0) { Log("Give the object's full path (Browse tab: select it, then \"Use Browse Selection\")."); return false; }
        if (needTarget && !File.Exists(cpTarget.Text)) { Log($"Target package not found: {cpTarget.Text}"); return false; }
        return true;
    }

    void CopyObject(bool dryRun)
    {
        if (!CopyReady(true)) return;
        var args = new List<string> { "--copy-export", cpSource.Text, cpExport.Text.Trim(), cpTarget.Text };
        if (cpRename.Text.Trim().Length > 0) args.AddRange(["--rename", cpRename.Text.Trim()]);
        foreach (string line in cpRefs.Lines.Select(l => l.Trim()).Where(l => l.Contains('=')))
            args.AddRange(["--replace-ref", line.Replace(" ", "")]);
        args.AddRange(CutArgs());
        if (dryRun) args.Add("--dry-run");
        else if (!Confirm($"Copy {cpExport.Text.Trim()} into {Path.GetFileName(cpTarget.Text)}?\n\nThe original package is kept as .bak; Undo on the Backups tab takes it back.")) return;
        RunCommand(dryRun ? "Copy dry run" : "Copy object", [.. args], writes: !dryRun, after: () => { if (!dryRun) { RefreshBackups(); ReopenPackage(); } });
    }

    void RemoveComponents(bool dryRun)
    {
        if (!File.Exists(rmPackage.Text)) { Log($"Package not found: {rmPackage.Text}"); return; }
        if (rmMesh.Text.Trim().Length == 0 && rmMaterial.Text.Trim().Length == 0) { Log("Give a mesh or material filter (or both)."); return; }
        var args = new List<string> { "--remove-components", rmPackage.Text };
        if (rmMesh.Text.Trim().Length > 0) args.AddRange(["--mesh", rmMesh.Text.Trim()]);
        if (rmMaterial.Text.Trim().Length > 0) args.AddRange(["--material", rmMaterial.Text.Trim()]);
        if (File.Exists(rmLibrary.Text)) args.AddRange(["--library", rmLibrary.Text]);
        if (dryRun) args.Add("--dry-run");
        else if (!Confirm($"Remove the matching placed meshes from {Path.GetFileName(rmPackage.Text)}?\n\nRun the dry run first to see what matches. Undo on the Backups tab takes it back.")) return;
        RunCommand(dryRun ? "Remove dry run" : "Remove placed meshes", [.. args], writes: !dryRun, after: () => { if (!dryRun) { RefreshBackups(); ReopenPackage(); } });
    }
}
