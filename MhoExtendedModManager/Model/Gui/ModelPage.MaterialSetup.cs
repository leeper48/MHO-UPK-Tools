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
        var menu = CopySetupMenu(pm);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        Ui.ShowUnder(menu, matGame);
    }

    /// <summary>The manual's picture (MHO_EXTMM_SNAP_MATMENU): the Materials tab, the first of the costume's own materials
    /// selected, and This Material ▾'s menu built (not shown) with the button it opens under.</summary>
    internal async Task<(ContextMenuStrip Menu, Control Anchor)?> MaterialMenuForTest(string package)
    {
        // that package of the mod as the target (its own materials listed: no source)
        if (targetKind.SelectedIndex != 0) { targetKind.SelectedIndex = 0; await Task.Delay(500); }
        FillPackages(); Reselect(packages, package);
        for (int i = 0; i < 300 && (ChosenPackage?.Key != package || refreshing); i++) await Task.Delay(100);
        for (int i = 0; i < modelTabs.Count; i++) if (modelTabs.TitleAt(i) == "Materials") modelTabs.Select(i);
        for (int i = 0; i < 300 && !matGrid.Rows.Cast<DataGridViewRow>().Any(r => r.Tag is PackageMap); i++) await Task.Delay(100);
        await Task.Delay(4000);   // (the preview and the list's last reads)
        var row = matGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is PackageMap pm && pm.InFile == null && pm.Kind == "Color");
        if (row == null || matGame == null) return null;
        matGrid.CurrentCell = row.Cells[0];
        return (CopySetupMenu((PackageMap)row.Tag!), matGame);
    }

    ContextMenuStrip CopySetupMenu(PackageMap pm)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(CopySetupItem(pm.Material, pm.InFile));
        // the game materials the model builds offer (Kurt, 2026-11-10: Glow, Not Metal on a costume's own TORSO), one click each
        menu.Items.Add(new ToolStripSeparator());
        foreach (var tm in MaterialChoice.Templates.Where(x => x.Donor != null))
        {
            var (_, label, donor, tip) = tm;
            var item = new ToolStripMenuItem(label, null, (_, _) => BeginInvoke(() => CopyTemplate(pm.Material, label, donor!)))
                { ToolTipText = $"{tip}\n\nGives this material that one's setup (as Copy Setup From a Costume does) and keeps its own maps." };
            if (pm.InFile != null) { item.Enabled = false; item.ToolTipText = $"{pm.Material} is in {pm.InFile}, the hero's base package: only a material of this package can be changed here."; }
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>The menu item: Copy Setup From a Costume, for <paramref name="material"/> (the target's slot of that name, else
    /// the one it has, else picked from its slots). <paramref name="inFile"/>: the hero's base package it's in (can't be changed).</summary>
    ToolStripMenuItem CopySetupItem(string material, string? inFile)
    {
        var item = new ToolStripMenuItem("Copy Setup From a Costume", null, (_, _) => BeginInvoke(() => PickCopySource(material)))
        {
            ToolTipText = "Gives the costume's material another costume's material setup (its shader, switches and values: spec, rim, glow strength, colors) and keeps its own maps. Pick the costume from the list (as the Source drop-down is set: Default Game or Current Modded Game), then one of its materials: its body's, or a prop's of that package (Captain America's shield)."
                + (HasSource ? " It changes the target package as it is; building a model onto it later replaces its materials." : ""),
        };
        if (inFile != null) { item.Enabled = false; item.ToolTipText = $"{material} is in {inFile}, the hero's base package: only a material of this package can be changed here."; }
        return item;
    }

    /// <summary>The costume to copy from (a searchable list of the game's characters), then its material when it has several.</summary>
    void PickCopySource(string material) => PickTargetSlot(material, PickCopySourceFor);

    /// <summary>A template material (MaterialChoice.Templates: "package:material") copied onto the selected material's slot.</summary>
    void CopyTemplate(string material, string label, string donor) => PickTargetSlot(material, (t, tpath, tmesh, to) =>
    {
        string file = donor.Split(':')[0] + ".upk", leaf = donor.Split(':')[^1];
        try
        {
            string spath = GameSource.PathFor(file);
            byte[] sbytes = File.ReadAllBytes(spath);
            foreach (string mesh in ModMeshes.List([(file, spath)], anyPackage: true).Select(m => m.Name))
            {
                List<MaterialSetupCopy.SlotInfo> slots;
                try { slots = MaterialSetupCopy.Slots(sbytes, file, mesh); }
                catch (InvalidDataException) { continue; }
                if (slots.FirstOrDefault(x => x.Instance && x.Material.Split('.').Last().Equals(leaf, StringComparison.OrdinalIgnoreCase)) is { } from)
                { _ = CopyMaterialSetup(t.Key, tpath, tmesh, to, spath, $"{label} ({Path.GetFileNameWithoutExtension(file)})", from); return; }
            }
            Log($"Copy Material Setup: {leaf} isn't on a model of {file}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Log($"Copy Material Setup: {ex.Message}"); }
    });

    /// <summary>The target's slot for <paramref name="material"/> (named like it, else its only one, else picked), then <paramref name="then"/>.</summary>
    void PickTargetSlot(string material, Action<CharacterList.Item, string, string, MaterialSetupCopy.SlotInfo> then)
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
        if (named != null || mine.Count == 1) then(t, tpath, tmesh, named ?? mine[0]);
        else if (mine.Count > 1)
            DropList.Show(matGame, matGame.RectangleToScreen(matGame.ClientRectangle), [.. mine.Select(s => $"{t.Key} slot {s.Index}: {s.Material.Split('.').Last()}")], -1,
                i => BeginInvoke(() => then(t, tpath, tmesh, mine[i])), thumb: MaterialThumbs(tpath, mine));
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
            // every model of the costume's package: its body first, then its props (Kurt, 2026-11-08: Captain America's shield
            // material, on the shield model beside his body), each material once
            var theirs = new List<MaterialSetupCopy.SlotInfo>();
            var labels = new List<string>();
            try
            {
                spath = GameSource.PathFor(file);
                var models = ModMeshes.List([(file, spath)], anyPackage: true).Select(m => m.Name).ToList();
                smesh = ModMeshes.CostumeMesh(spath) ?? models.FirstOrDefault() ?? throw new InvalidDataException($"{file} has no model");
                byte[] sbytes = File.ReadAllBytes(spath);
                foreach (string mesh in models.Where(m => !m.Equals(smesh, StringComparison.OrdinalIgnoreCase)).Prepend(smesh))
                {
                    List<MaterialSetupCopy.SlotInfo> slots;
                    try { slots = MaterialSetupCopy.Slots(sbytes, file, mesh); }
                    catch (InvalidDataException) { continue; }
                    foreach (var sl in slots.Where(x => x.Instance && !theirs.Any(y => y.Material.Equals(x.Material, StringComparison.OrdinalIgnoreCase))))
                    {
                        theirs.Add(sl);
                        labels.Add(mesh.Equals(smesh, StringComparison.OrdinalIgnoreCase) ? $"{title}: {sl.Material.Split('.').Last()}" : $"{title} · Prop {mesh}: {sl.Material.Split('.').Last()}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { Log($"Copy Material Setup: {ex.Message}"); return; }
            if (theirs.Count == 0) { Log($"Copy Material Setup: {title}'s models have no material instance of their own to copy."); return; }
            string who = $"{title} ({Path.GetFileNameWithoutExtension(file)})";
            if (theirs.Count == 1) { _ = CopyMaterialSetup(t.Key, tpath, tmesh, to, spath, who, theirs[0]); return; }
            BeginInvoke(() => DropList.Show(matGame, matGame.RectangleToScreen(matGame.ClientRectangle), labels, -1,
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
        // the Pets tab (Kurt, 2026-11-06): every pet with a mesh as a source, the humanoid ones as targets
        int petSources = allPackages.Count(p => IsPet(p.File)), petTargets = allPackages.Count(p => IsPet(p.File) && CanTarget(p.File));
        say($"pets: {petSources} sources, {petTargets} targets");
        if (Environment.GetEnvironmentVariable("MHO_TEST_PETSHOT") is { Length: > 0 } petShot)
        {
            int game = Enumerable.Range(0, sourceKind.Items.Count).FirstOrDefault(i => sourceKind.Items[i]?.ToString()?.Contains("Game", StringComparison.OrdinalIgnoreCase) == true, -1);
            if (game >= 0) sourceKind.SelectedIndex = game;
            await Task.Delay(1500);
            sourceKindTab = 3; FillCharacters();
            gameList = true; targetKindTab = 3; FillPackages();
            await Task.Delay(8000);   // (the rows' pictures are made in the background)
            characters.Invalidate(); packages.Invalidate();
            await Task.Delay(500);
            say($"pets tab: source rows {characters.Items.Count}, target rows {packages.Items.Count}");
            var form = FindForm()!; using var bmp = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size)); bmp.Save(petShot);
            if (petSources == 0 || petTargets == 0 || characters.Items.Count == 0 || packages.Items.Count == 0) return 1;
            // right-click a pet source: the same pet picked as the target (Kurt, 2026-11-06: it didn't)
            foreach (string pet in new[] { "UC__MarvelVanityPet_Vision_SF.upk", "UC__MarvelVanityPet_Cosmo_SF.upk" })
            {
                int row = Enumerable.Range(0, characters.Items.Count).FirstOrDefault(i => characters.Items[i] is CharacterList.Item it && it.Key.Equals(GameSrc + pet, StringComparison.OrdinalIgnoreCase), -1);
                if (row >= 0) SourceBoth(row, pet);
                await Task.Delay(3000);
                say($"right-click {pet}: row {row}; target tab {targetKindTab}; target picked: {ChosenPackage?.Key ?? "none"}; target rows {packages.Items.Count}; gameList {gameList}");
                if (ChosenPackage?.Key.Equals(pet, StringComparison.OrdinalIgnoreCase) != true) return 1;
            }
            // Cosmo onto Baby Groot: Build asks Whole Pet or Fit (Kurt, 2026-11-06: no pop-up showed)
            if (Environment.GetEnvironmentVariable("MHO_TEST_PETBUILD") == "1")
            {
                gameList = true; packageFilter.Text = ""; FillPackages();
                Reselect(packages, "UC__MarvelVanityPet_BabyGroot_SF.upk");
                for (int i = 0; i < 100 && !HasSource; i++) await Task.Delay(100);
                await Task.Delay(1000);
                say($"build test: source {chosenKey}; has source {HasSource}; target {ChosenPackage?.Key}; building {building}");
                await Task.Delay(6000);   // (the preview of the choice)
                { var bf = FindForm()!; using var pb = new Bitmap(bf.Width, bf.Height); bf.DrawToBitmap(pb, new Rectangle(Point.Empty, bf.Size)); pb.Save(Path.ChangeExtension(petShot, ".build.png")); }
                // ◀ / ▶ with Chain off step through the animation list (Kurt, 2026-11-06)
                bool chainWas = MhoExtendedModManager.PreviewViews.Chain;
                MhoExtendedModManager.PreviewViews.Chain = false;
                try
                {
                    say($"arrows: start {preview.CurrentAnimationName ?? "rest pose"}");
                    foreach (int by in new[] { 1, 1, -1, -1, -1 }) say($"arrows: {(by > 0 ? "▶" : "◀")} → " + await preview.StepForTest(by));
                }
                finally { MhoExtendedModManager.PreviewViews.Chain = chainWas; }
                bool built0 = await BuildAsync();
                say($"build test: built {built0}; log: " + log.Text.Replace("\r\n", " | ").Replace("\n", " | "));
            }
        }
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
