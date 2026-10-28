using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Copy Material Setup (Kurt, 2026-10-28): on the Materials tab with no source picked (the target's own materials listed),
/// This Material ▾ → Copy Setup From a Costume: a costume picked from a searchable list (Default Game or Current Modded Game,
/// as the Source drop-down is set), then its material when it has several; the selected material's slot gets a copy of that
/// material's shader setup (parent, switches, values) with the target's own maps (MaterialSetupCopy). A popup says what
/// happens first. (Picked in the menu, not as the Source: Kurt didn't want the Source changed for it.)
/// </summary>
sealed partial class ModelPage
{
    /// <summary>This Material ▾ on a row of the target's own materials: Copy Setup From a Costume.</summary>
    void ShowCopySetupMenu(PackageMap pm)
    {
        if (matGame == null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add(CopySetupItem(pm.Material, pm.InFile));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Ui.ShowUnder(menu, matGame);
    }

    /// <summary>The menu item: Copy Setup From a Costume, for <paramref name="material"/> (the target's slot of that name, else
    /// the one it has, else picked from its slots). <paramref name="inFile"/>: the hero's base package it's in (can't be changed).</summary>
    ToolStripMenuItem CopySetupItem(string material, string? inFile)
    {
        var item = new ToolStripMenuItem("Copy Setup From a Costume", null, (_, _) => BeginInvoke(() => PickCopySource(material)))
        {
            ToolTipText = "Gives the costume's material another costume's material setup (its shader, switches and values: spec, rim, glow strength, colors) and keeps its own maps. Pick the costume from the list (as the Source drop-down is set: Default Game or Current Modded Game)."
                + (HasSource ? " It changes the target package as it is; building a model onto it later replaces its materials." : ""),
        };
        if (inFile != null) { item.Enabled = false; item.ToolTipText = $"{material} is in {inFile}, the hero's base package: only a material of this package can be changed here."; }
        return item;
    }

    /// <summary>The costume to copy from (a searchable list of the game's characters), then its material when it has several.</summary>
    void PickCopySource(string material)
    {
        if (matGame == null || ChosenPackage is not CharacterList.Item t) return;
        if (host.Packages.FirstOrDefault(p => p.File.Equals(t.Key, StringComparison.OrdinalIgnoreCase)).Path is not string tpath || !File.Exists(tpath)) return;
        string tmesh;
        List<MaterialSetupCopy.SlotInfo> mine;
        try
        {
            tmesh = MhoSkeleton.ChosenFor(t.Key) ?? ModMeshes.CostumeMesh(tpath) ?? ModMeshes.List([(t.Key, tpath)], anyPackage: true).FirstOrDefault()?.Name ?? throw new InvalidDataException($"{t.Key} has no model");
            mine = MaterialSetupCopy.Slots(File.ReadAllBytes(tpath), t.Key, tmesh);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Log($"Copy Material Setup: {ex.Message}"); return; }
        // the target's slot: the one named like the selected material (its own, or the source's when the source is this costume),
        // else its only one, else picked
        var named = mine.FirstOrDefault(s => s.Material.Split('.').Last().Equals(material, StringComparison.OrdinalIgnoreCase));
        if (named != null || mine.Count == 1) PickCopySourceFor(t, tpath, tmesh, named ?? mine[0]);
        else if (mine.Count > 1)
            DropList.Show(matGame, matGame.RectangleToScreen(matGame.ClientRectangle), [.. mine.Select(s => $"{t.Key} slot {s.Index}: {s.Material.Split('.').Last()}")], -1,
                i => BeginInvoke(() => PickCopySourceFor(t, tpath, tmesh, mine[i])), thumb: MaterialThumbs(tpath, mine));
        else Log($"Copy Material Setup: {tmesh} has no material slots.");
    }

    void PickCopySourceFor(CharacterList.Item t, string tpath, string tmesh, MaterialSetupCopy.SlotInfo to)
    {
        if (matGame == null) return;
        var sources = allPackages.Where(p => !p.File.Equals(t.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        // (as the Source list shows them: the costume's picture, "• " in front of a hero's default costume; Kurt, 2026-10-28)
        var texts = sources.Select(p => $"{Dot(p.File, p.Title)} · {p.Detail}" + (ModdedMode && moddedPackages.TryGetValue(p.File, out var mm) ? $" · from {mm.Mod}" : "")).ToList();
        if (texts.Count == 0) { Log("Copy Material Setup: no costumes from the game are listed (see the log above for why)."); return; }
        DropList.Show(matGame, matGame.RectangleToScreen(matGame.ClientRectangle), texts, -1, pick =>
        {
            var (file, title, _) = sources[pick];
            string spath, smesh;
            List<MaterialSetupCopy.SlotInfo> theirs;
            try
            {
                spath = GameSource.PathFor(file);
                smesh = ModMeshes.CostumeMesh(spath) ?? ModMeshes.List([(file, spath)], anyPackage: true).FirstOrDefault()?.Name ?? throw new InvalidDataException($"{file} has no model");
                theirs = MaterialSetupCopy.Slots(File.ReadAllBytes(spath), file, smesh).Where(s => s.Instance).GroupBy(s => s.Material, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { Log($"Copy Material Setup: {ex.Message}"); return; }
            if (theirs.Count == 0) { Log($"Copy Material Setup: {title}'s model has no material instance of its own to copy."); return; }
            string who = $"{title} ({Path.GetFileNameWithoutExtension(file)})";
            if (theirs.Count == 1) { _ = CopyMaterialSetup(t.Key, tpath, tmesh, to, spath, who, theirs[0]); return; }
            BeginInvoke(() => DropList.Show(matGame, matGame.RectangleToScreen(matGame.ClientRectangle), [.. theirs.Select(s => $"{title}: {s.Material.Split('.').Last()}")], -1,
                m => _ = CopyMaterialSetup(t.Key, tpath, tmesh, to, spath, who, theirs[m]), thumb: MaterialThumbs(spath, theirs)));
        }, thumb: i => SourceThumb(sources[i].File));
    }

    /// <summary>The materials' color maps for a list (Kurt, 2026-10-28), made in the background: null until each is ready.</summary>
    Func<int, Image?> MaterialThumbs(string packagePath, List<MaterialSetupCopy.SlotInfo> slots)
    {
        var made = new Image?[slots.Count];
        int size = (int)(64 * Ui.Dpi(DeviceDpi));
        _ = Task.Run(() => { for (int i = 0; i < slots.Count; i++) if (slots[i].Export >= 0) made[i] = MaterialSetupCopy.ColorMap(packagePath, slots[i].Export, size); });
        return i => i >= 0 && i < made.Length ? made[i] : null;
    }

    async Task CopyMaterialSetup(string key, string tpath, string tmesh, MaterialSetupCopy.SlotInfo to, string spath, string stitle, MaterialSetupCopy.SlotInfo from)
    {
        static string Leaf(string path) => path.Split('.').Last();
        string text = $"{key} slot {to.Index} ({Leaf(to.Material)}) gets a copy of {stitle}'s {Leaf(from.Material)}.\n\n"
            + "What's copied:\n"
            + "  • Its shader setup: the parent material, the switches, and the values (spec, rim, glow strength, colors).\n\n"
            + "What stays this costume's own:\n"
            + "  • Its maps: color, normal and the others it has (the two models' maps are laid out differently).\n"
            + "  • A map it hasn't got gets a flat stand-in: a flat normal, no spec, no glow. Replace File on this tab can give it its own.\n\n"
            + "The copy goes into this package under a name of its own; the old material stays in it unused. Revert to Original (right-click the costume under Target) takes it back off.";
        // the costume's older packed spec map made into the source's layout, so its shine stays (Kurt, 2026-10-28)
        string? conv = MaterialSetupCopy.SpecConversion(File.ReadAllBytes(tpath), key, tmesh, to.Index, spath, from.Material);
        bool convertSpec = false;
        if (conv != null)
        {
            int c = Dialog.Choose(this, text + "\n\nSpec map:\n"
                + $"  • {stitle}'s material reads a packed spec map in another layout than this costume's: {conv}.\n"
                + "  • Convert makes the map it reads from this costume's own, so the costume keeps its own shine and reflections.\n"
                + "  • Flat Stand-In uses no spec instead (a matte look).",
                "Copy Material Setup", "Copy and Convert My Spec Map", "Copy With a Flat Stand-In", "Cancel");
            if (c is < 0 or 2) return;
            convertSpec = c == 0;
        }
        else if (Dialog.Show(this, text, "Copy Material Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"material {Path.GetFileNameWithoutExtension(key)}"));
        string tag = "on_" + MaterialOut.Safe(Path.GetFileNameWithoutExtension(key).Replace("UC__MarvelPlayer_", "", StringComparison.OrdinalIgnoreCase).Replace("_SF", "", StringComparison.OrdinalIgnoreCase)).ToLowerInvariant();
        MaterialSetupCopy.Result r;
        using (Busy.Begin("Model: copying the material setup"))
            try { r = await Task.Run(() => MaterialSetupCopy.Copy(File.ReadAllBytes(tpath), key, tmesh, to.Index, spath, from.Material, tag, Path.Combine(outDir, "standins"), convertSpec)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { Log($"Copy Material Setup: {ex.Message}"); Dialog.Show(this, ex.Message, "Material Setup Not Copied", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed) return;
        string outFile = Path.Combine(outDir, key);
        Protected.CheckWrite(outFile);
        File.WriteAllBytes(outFile, r.Package);
        host.SetPackage(key, outFile);
        RememberBuild(key, outFile);
        // the source was this very costume (its own model, as in Kurt's test): cleared, so the preview shows the package as it
        // is now and a Build doesn't put the old material back; another source is left, with a note
        if (chosenKey is string ck && ck.StartsWith(GameSrc) && SplitGameKey(ck).File.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            SourceStep(null);
            chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear();
            if (!propMode) characters.ClearSelected();
            Log("Source: none now (it was this costume's own model): the preview shows the costume with its new material.");
        }
        else if (HasSource) Log("Note: a source is still picked; building it onto this costume replaces its materials, the copied one too.");
        Log($"Copy Material Setup: {key} slot {to.Index} uses {r.NewMaterial} ({stitle}'s setup):");
        foreach (var l in r.Lines) Log("  " + l);
        SchedulePreview(); UpdateStatus();
    }

    /// <summary>
    /// Test (--model-material-test; a scratch MHO_EXTMM_HOME with MHO_EXTMM_TEST_DIALOGS; the mod isn't saved) and the manual's
    /// shots: <paramref name="target"/> added and picked with no source, its first material given <paramref name="source"/>'s
    /// setup (as This Material ▾ → Copy Setup From a Costume does after the pick), the slot checked; <paramref name="shot"/>: the
    /// Materials tab then; <paramref name="shot2"/>: Source → Current Modded Game filtered to <paramref name="filter"/>.
    /// </summary>
    internal async Task<int> TestMaterialCopy(string target, string source, string? shot, string? shot2, string filter, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        void Shot(string png) { var form = FindForm()!; using var bmp = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(png); say("shot: " + png); }
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        if (Settings.Current.CookedFolder is null) { say("no game folder"); return 1; }
        preview.ShowBones = false;
        if (!host.Packages.Any(p => p.File.Equals(target, StringComparison.OrdinalIgnoreCase))) { host.AddPackage(target, BasePackage.Resolve(target, true)); added.Add(target); }
        if (sourceKind.SelectedIndex != 2) { sourceKind.SelectedIndex = 2; await Task.Delay(300); }
        chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear(); characters.ClearSelected();
        gameList = false; packageFilter.Text = ""; FillPackages(); Reselect(packages, target);
        for (int i = 0; i < modelTabs.Count; i++) if (modelTabs.TitleAt(i) == "Materials") modelTabs.Select(i);
        for (int i = 0; i < 300 && (packageMaps == null || packageMaps.Count == 0); i++) await Task.Delay(100);
        Check(packageMaps is { Count: > 0 }, $"no source: the Materials tab lists {target}'s own materials ({packageMaps?.Count ?? 0} maps)");
        if (packageMaps is not { Count: > 0 } || host.Packages.FirstOrDefault(p => p.File.Equals(target, StringComparison.OrdinalIgnoreCase)).Path is not string tpath) return fails + 1;
        string mat = packageMaps[0].Material;
        SelectMaterialRow(mat);
        Check(matGame?.Text.Contains("Its Own") == true, $"This Material on the costume's own material: \"{matGame?.Text}\"");
        string tmesh = MhoSkeleton.ChosenFor(target) ?? ModMeshes.CostumeMesh(tpath)!;
        var to = MaterialSetupCopy.Slots(File.ReadAllBytes(tpath), target, tmesh).First(x => x.Material.Split('.').Last().Equals(mat, StringComparison.OrdinalIgnoreCase));
        string spath = GameSource.PathFor(source);
        var from = MaterialSetupCopy.Slots(File.ReadAllBytes(spath), source, ModMeshes.CostumeMesh(spath)!).First(x => x.Instance);
        await CopyMaterialSetup(target, tpath, tmesh, to, spath, DescribePackage(source).Title, from);
        string after = host.Packages.First(p => p.File.Equals(target, StringComparison.OrdinalIgnoreCase)).Path;
        var now = MaterialSetupCopy.Slots(File.ReadAllBytes(after), target, tmesh);
        Check(after != tpath && now[to.Index].Material.Contains("_on_", StringComparison.OrdinalIgnoreCase), $"the slot uses the copy: {now[to.Index].Material}");
        for (int i = 0; i < 300 && !(packageMaps?.Any(m => m.Material.Contains("_on_", StringComparison.OrdinalIgnoreCase)) ?? false); i++) await Task.Delay(100);
        Check(packageMaps?.Any(m => m.Material.Contains("_on_", StringComparison.OrdinalIgnoreCase)) == true, "the Materials tab lists the copied material");
        await Task.Delay(2500);
        if (!string.IsNullOrEmpty(shot)) Shot(shot);
        // Current Modded Game: the list as the mod list has it
        sourceKind.SelectedIndex = 3; await Task.Delay(300);
        characterFilter.Text = filter; await Task.Delay(500);
        bool modded = characters.Items.Cast<CharacterList.Item>().Any(x => x.Detail.Contains("· from ", StringComparison.Ordinal));
        Check(modded, $"Current Modded Game names the mod a package is read from ({characters.Items.Count} rows for \"{filter}\")");
        if (!string.IsNullOrEmpty(shot2)) { await Task.Delay(4000); Shot(shot2); }
        sourceKind.SelectedIndex = 2;
        return fails;
    }

    /// <summary>Test (--model-lists-test): the Source and Target lists as the tab came up (a user's were empty), and the log.</summary>
    internal async Task<int> TestLists(Action<string> say)
    {
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        await Task.Delay(1500);
        say($"loaded: {loaded}; game folder: {Settings.Current.GameFolder ?? "none"}; cooked: {Settings.Current.CookedFolder ?? "none"}; MFF: {Settings.Current.MffSource ?? "none"}; stock: {Settings.Current.StockFolder ?? "none"}");
        say($"source kind: {sourceKind.SelectedIndex} ({(sourceKind.SelectedIndex >= 0 ? sourceKind.Items[sourceKind.SelectedIndex] : "-")}); game packages: {allPackages.Count}; source rows: {characters.Items.Count}; target rows: {packages.Items.Count}");
        say("log: " + log.Text.Replace("\r\n", " | ").Replace("\n", " | "));
        // MHO_TEST_LISTSHOT=<png> [MHO_TEST_LISTSOURCE=<game package>]: that source picked, the Parts tab shot (its scrolling)
        if (Environment.GetEnvironmentVariable("MHO_TEST_LISTSHOT") is { Length: > 0 } shot)
        {
            if (host.Packages.FirstOrDefault().File is string tp) { gameList = false; packageFilter.Text = ""; FillPackages(); Reselect(packages, tp); await Task.Delay(1500); }
            if (Environment.GetEnvironmentVariable("MHO_TEST_LISTSOURCE") is { Length: > 0 } srcPkg) { GameChosen(GameSrc + srcPkg); for (int i = 0; i < 600 && parts.Rows.Count == 0; i++) await Task.Delay(100); }
            for (int i = 0; i < modelTabs.Count; i++) if (modelTabs.TitleAt(i) == "Parts") modelTabs.Select(i);
            await Task.Delay(2000);
            say($"parts rows: {parts.Rows.Count}; parts height {parts.Height}; list scrolls: {(partsPage?.Parent as ScrollableControl)?.VerticalScroll.Visible}");
            var form = FindForm()!; using var bmp = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(shot);
        }
        return allPackages.Count > 0 && characters.Items.Count > 0 ? 0 : 1;
    }
}
