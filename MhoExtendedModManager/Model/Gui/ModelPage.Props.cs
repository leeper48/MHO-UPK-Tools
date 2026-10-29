using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Props (Kurt, 2026-10-26: phase 1; first a Props tab, then "clunky: have the props show up in the source lower third" as the
/// summons do): the Source's panel under the From the Game list shows the props of the costume picked above (Thor Marvel NOW's
/// hammer; a hero's group row: all of the hero's), from Model\StockData\model_props.txt (--prop-census on the stock packages).
/// A click makes a prop the source (GameSource with that model: the key is gamesrc:&lt;package&gt;|&lt;mesh&gt;); a right-click
/// makes it the target too (its package added to the mod, asked first; the build pointed at that model), to edit it in Blender
/// or to put another model on it (pick an FBX as the source then). An FBX or a game model is laid over the game's prop by its
/// shape; the prop's own model, edited in Blender, comes back as it is (FbxReimport.LoadAny / ApplyAny). Build into Mod puts it
/// into every package with a copy: the hero's own along, other characters' (Skrull Thor, the team-ups) when the user says so.
/// </summary>
sealed partial class ModelPage
{
    internal sealed record PropRow(string CostumeFile, string Hero, string Costume, string Class, string Mesh, string MeshPackage, string MeshPath,
        string Bone, string Slots, bool OnDemand, string Kind, int Bones, List<string> Copies);

    /// <summary>Every hero costume's props (the census), in file order.</summary>
    internal static readonly List<PropRow> Props = LoadProps();

    static List<PropRow> LoadProps()
    {
        var list = new List<PropRow>();
        using var s = typeof(ModelPage).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.model_props.txt");
        if (s == null) return list;
        using var r = new StreamReader(s);
        for (string? line; (line = r.ReadLine()) != null;)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length < 12) continue;
            list.Add(new PropRow(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9] == "1", c[10], int.TryParse(c[11], out int b) ? b : 0,
                c.Length > 12 ? [.. c[12].Split(',', StringSplitOptions.RemoveEmptyEntries)] : []));
        }
        return list;
    }

    /// <summary>A game source key: its package, and the one model of it (a prop: gamesrc:&lt;package&gt;|&lt;mesh&gt;) or null.</summary>
    static (string File, string? Mesh) SplitGameKey(string key)
    {
        string rest = key.StartsWith(GameSrc, StringComparison.Ordinal) ? key[GameSrc.Length..] : key;
        int bar = rest.IndexOf('|');
        return bar < 0 ? (rest, null) : (rest[..bar], rest[(bar + 1)..]);
    }

    /// <summary>"thor_ageofultron_hammer" → "Thor Ageofultron Hammer".</summary>
    static string PropLabel(string mesh) => string.Join(" ", mesh.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>The props of a costume (by its class, as the panel's selection names it), or with no costume the hero's, one per
    /// model (props moved by the character's animation and vehicles are left out for now).</summary>
    internal static List<PropRow> PropsFor(string hero, string? costumeClass, bool allKinds = false)
    {
        var mine = Props.Where(p => (allKinds || p.Kind == "prop") && p.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase));
        if (costumeClass != null) mine = mine.Where(p => ConditionSwaps.ClassOf(p.CostumeFile)?.Equals(costumeClass, StringComparison.OrdinalIgnoreCase) == true);
        return mine.GroupBy(p => p.MeshPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(p => p.Mesh, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A prop as the target too (the panel's right-click): its package into the mod (asked first), the build pointed
    /// at that model, the Target list on the mod's packages with it picked.</summary>
    void PropToTarget(string file, string mesh)
    {
        if (!host.Packages.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
        {
            var row = Props.FirstOrDefault(p => p.MeshPackage.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase));
            if (Dialog.Show(this, $"Add {file} to this mod? It holds {(row != null ? Spaced(row.Hero) + "'s " : "")}{PropLabel(mesh)}; the new prop is built into it. Until then it's the game's own package.",
                    "Add a Package", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try { host.AddPackage(file, BasePackage.Resolve(file)); added.Add(file); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Dialog.Show(this, ex.Message, "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            Log($"Added {file} to the mod (the game's stock copy, until it's built).");
        }
        MhoSkeleton.Choose(file, mesh);
        gameList = false; packageFilter.Text = "";
        FillPackages();
        Reselect(packages, file);
        // a source read before as a body (guessed, or to be rigged in Blender) is read again as a prop's
        if ((unrigged != null || model != null) && chosenKey != null && sourceFbx != null) FbxChosen(chosenKey, sourceFbx);
        else if (unrigged != null && chosenKey?.StartsWith("fbx:") == true) FbxChosen(chosenKey);
        SchedulePreview(); UpdateStatus();
        SaveState();
        Log($"Target: {PropLabel(mesh)} in {file} (◀ ▶ under the preview shows it). To change it: Edit in Blender, or pick an FBX as the source.");
    }

    /// <summary>The target is a prop (a weapon or shield picked under Model ▾ or from the Source's panel).</summary>
    bool TargetIsProp()
    {
        if (ChosenPackage is not CharacterList.Item t) return false;
        try
        {
            // (asked often: the status line, the preview; kept per package, model and file date)
            string start = StartPackage(t.Key);
            string key = $"{start}|{MhoSkeleton.ChosenFor(t.Key)}|{File.GetLastWriteTimeUtc(start).Ticks}";
            if (propTargetCache.TryGetValue(key, out bool known)) return known;
            return propTargetCache[key] = PropFit.IsProp(MhoSkeleton.Load(start, null));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException) { return false; }
    }
    readonly Dictionary<string, bool> propTargetCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A saved prop source: its hero's panel open, the prop picked there.</summary>
    void ShowPropSource(string key)
    {
        var (file, mesh) = SplitGameKey(key);
        var row = Props.FirstOrDefault(p => p.MeshPackage.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase));
        if (row == null) return;
        summonsFor = (row.Hero, null);
        FillSourceSummons();
        fillingSummons = true;
        Reselect(sourceSummons, key);
        fillingSummons = false;
    }

    /// <summary>The prop a target package is built onto (its chosen model, when the props table has it): its copies, the hero's
    /// own packages and other characters' (team-ups, enemies); null when the target's model isn't a prop.</summary>
    (List<string> Own, List<string> Others, string Mesh)? PropCopies(string file)
    {
        if (MhoSkeleton.ChosenFor(file) is not string mesh) return null;
        var row = Props.FirstOrDefault(p => p.MeshPackage.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Mesh.Equals(mesh, StringComparison.OrdinalIgnoreCase));
        if (row == null) return null;
        string hero = $"UC__MarvelPlayer_{row.Hero}_";
        var copies = Props.Where(p => p.MeshPath.Equals(row.MeshPath, StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Copies.Append(p.MeshPackage)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => !c.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        return (copies.Where(c => c.StartsWith(hero, StringComparison.OrdinalIgnoreCase)).ToList(), copies.Where(c => !c.StartsWith(hero, StringComparison.OrdinalIgnoreCase)).ToList(), mesh);
    }

    /// <summary>
    /// Test (--model-props-test; a scratch MHO_EXTMM_HOME with MHO_EXTMM_TEST_DIALOGS; the mod isn't saved): props in the Source's
    /// panel: the costume's prop listed under it; a click makes it the source (that one model); a right-click the target too (its
    /// package added, the build pointed at it); its own model built back as it is; then another game model as the source, laid
    /// over it by its shape. MHO_TEST_PROPSHOT=&lt;png&gt;: the tab with the prop picked.
    /// </summary>
    internal async Task<int> TestProps(string costumeFile, string mesh, string otherSource, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        if (Environment.GetEnvironmentVariable("MHO_TEST_PROPSHOWSHOT") != null || Environment.GetEnvironmentVariable("MHO_TEST_GIZMOSHOT") != null) preview.ShowBones = false;   // (the manual's shots)
        // MHO_TEST_GIZMO_ONLY = <package>: that target in the mod's packages, its first added prop picked, the gizmo dragged
        // (a copy of a user's mod, to see what it does there)
        if (Environment.GetEnvironmentVariable("MHO_TEST_GIZMO_ONLY") is { Length: > 0 } gpk)
        {
            if (targetKind.SelectedIndex != 0) { targetKind.SelectedIndex = 0; await Task.Delay(500); }
            FillPackages(); Reselect(packages, gpk);
            for (int i = 0; i < 300 && (ChosenPackage?.Key != gpk || refreshing); i++) await Task.Delay(100);
            await Task.Delay(3000);
            var ga = host.AddedProps(gpk).FirstOrDefault();
            Check(ga != null, $"gizmo only: {gpk} has an added prop ({ga?.Name}; fit {System.Text.Json.JsonSerializer.Serialize(ga?.Fit)}; bone {ga?.Bone}); source {chosenKey ?? "none"}");
            if (ga == null) return fails;
            for (int i = 0; i < 100 && meshAdded.IndexOf(ga) < 0; i++) await Task.Delay(100);
            PickAdded(ga);
            for (int i = 0; i < 300 && (AddedTarget != ga || refreshing); i++) await Task.Delay(100);
            await Task.Delay(4000);
            Check(GizmoShownForTest, $"gizmo only: shown ({MeshCaption()}; props {preview.PropClassesForTest})");
            Check(true, "(info) X arrow: " + await GizmoLiveForTest(0, 80, 0) + $" | fit now {System.Text.Json.JsonSerializer.Serialize(ga.Fit)}");
            await Task.Delay(2000);
            Check(true, "(info) Z ring: " + await GizmoLiveForTest(5, 60, 60) + $" | fit now {System.Text.Json.JsonSerializer.Serialize(ga.Fit)}");
            // back to the body: its own model the source, its parts listed; the Target's Props list the hero's props too
            meshPick.SelectedIndex = meshPick.SelectedIndex == 0 ? 1 : 0;
            for (int i = 0; i < 600 && (parts.Rows.Count == 0 || refreshing); i++) await Task.Delay(100);
            Check(parts.Rows.Count > 0, $"gizmo only: back on the body ({MeshCaption()}), its parts listed: {string.Join(", ", parts.Rows.Cast<DataGridViewRow>().Select(r => r.Cells["part"].Value))} (source {chosenKey})");
            for (int i = 0; i < 100 && propSwitches.Count == 0; i++) await Task.Delay(100);
            Check(propSwitches.Any(p => p.Kind == "Hero Prop"), $"gizmo only: the hero's props listed: {string.Join(", ", propSwitches.Select(p => p.Label + " (" + p.Kind + ")"))}");
            if (propSwitches.FirstOrDefault(p => p.Kind == "Hero Prop") is { } hp && ModCopy(gpk) is string mc)
            {
                var e1 = new PartOffEntry { Package = hp.Package, Part = hp.Part, Switch = hp.Key, Original = hp.Original };
                var lg = new List<string>();
                string tmp = Path.Combine(Path.GetTempPath(), "mho_heroprop_" + gpk);
                File.WriteAllBytes(tmp, PowerParts.Apply(mc, [(e1, true)], lg));
                var offList = MhoExtendedModManager.ModMeshes.AttachmentClasses(tmp) ?? [];
                File.WriteAllBytes(tmp, PowerParts.Apply(tmp, [(e1, false)], lg));
                var onList = MhoExtendedModManager.ModMeshes.AttachmentClasses(tmp) ?? [];
                File.Delete(tmp);
                Check(!offList.Contains(hp.PartName, StringComparer.OrdinalIgnoreCase) && onList.Contains(hp.PartName, StringComparer.OrdinalIgnoreCase),
                    $"gizmo only: {hp.Label} left off ({offList.Count} props: {string.Join(", ", offList)}) and back ({onList.Count}) | {string.Join(" | ", lg)}");
            }
            Check(true, "(info) log: " + string.Join(" | ", log.Lines.TakeLast(12)));
            if (Environment.GetEnvironmentVariable("MHO_TEST_GIZMOSHOT") is { Length: > 0 } qshot)
            {
                // (the gizmo in all four panes of the quad view: MHO_TEST_QUAD=1)
                if (addedPick == null && host.AddedProps(gpk).FirstOrDefault() is { } qa) { PickAdded(qa); for (int i = 0; i < 100 && preview.PropBase(GizmoMatch) == null; i++) await Task.Delay(100); }
                if (!Settings.Current.Preview.Gizmo) ToggleGizmo();
                if (Environment.GetEnvironmentVariable("MHO_TEST_QUAD") == "1" && !PreviewViews.Quad) preview.ToggleQuad();
                await Task.Delay(2500);
                Check(GizmoShownForTest, $"gizmo shown for the shot ({addedPick?.Name ?? "no prop picked"})");
                var qform = FindForm()!;
                using var qbmp = new Bitmap(qform.Width, qform.Height);
                qform.DrawToBitmap(qbmp, new Rectangle(Point.Empty, qform.Size));
                qbmp.Save(qshot);
            }
            return fails;
        }
        Check(Props.Count > 1000, $"the props table: {Props.Count} rows");
        if (!GameMode) { sourceKind.SelectedIndex = 2; await Task.Delay(300); }
        sourceKindTab = 0; FillCharacters();
        string hero = DescribePackage(costumeFile).Title;
        summonsFor = (hero, ConditionSwaps.ClassOf(costumeFile)); FillSourceSummons();
        var row = sourceSummons.Items.OfType<CharacterList.Item>().FirstOrDefault(r => SplitGameKey(r.Key).Mesh?.Equals(mesh, StringComparison.OrdinalIgnoreCase) == true);
        say($"  panel: {sourceSummonsTitle.Text}: {string.Join(", ", sourceSummons.Items.OfType<CharacterList.Item>().Select(r => r.Title))}");
        Check(sourceSummonsPanel.Visible && row != null, $"{costumeFile}'s {mesh} is in the Source's panel");
        if (row == null) return fails;
        var (file, _) = SplitGameKey(row.Key);
        // a click: the source is that one model
        sourceSummons.SelectedItem = row;
        for (int i = 0; i < 600 && !(HasSource && row.Key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(100);
        Check(HasSource && row.Key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase), $"a click makes it the source ({chosenKey})");
        // a right-click: the target too
        PropToTarget(file, mesh);
        await Task.Delay(300);
        Check(ChosenPackage?.Key.Equals(file, StringComparison.OrdinalIgnoreCase) == true && MhoSkeleton.ChosenFor(file)?.Equals(mesh, StringComparison.OrdinalIgnoreCase) == true,
            $"right-click: {file} is the target, the build pointed at {mesh}");
        for (int i = 0; i < 100 && (refreshing || !preview.LooksForTest.Any()); i++) await Task.Delay(100);
        for (int i = 0; i < 100 && (refreshing || preview.ExtentValueForTest < 1); i++) await Task.Delay(100);
        say($"  preview model: {preview.ExtentForTest}; parts: {string.Join(", ", parts.Rows.Cast<DataGridViewRow>().Select(r => $"{r.Cells["part"].Value}={(r.Cells["use"].Value is true ? "on" : "off")}"))}; picked: {string.Join(",", SelectedParts())}");
        Check(preview.ExtentValueForTest > 30, $"the preview shows it at its size (a 46-unit hammer) ({string.Join(" | ", log.Lines.Where(l => l.StartsWith("Preview")).TakeLast(1))})");
        if (Environment.GetEnvironmentVariable("MHO_TEST_PROPSHOT") is { Length: > 0 } shot)
        {
            await Task.Delay(1500);
            var form = FindForm()!;
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(shot);
        }
        bool ok = await BuildAsync();
        Check(ok && built.ContainsKey(file) && log.Text.Contains("kept as it is"), "its own model built back as it is");
        // Prop Fit (phase 2): the sliders show for a prop target; a move, turn and size change the preview and go into the build
        UpdatePropFitUi();
        Check(propFitBox?.Visible == true && sizeBoxRef?.Visible == false && sizeCaption?.Text == "PROP FIT", "Prop Fit shows in place of the size sliders");
        // the gizmo on the game prop (Kurt, 2026-10-28): on its grip; an arrow moves it, a ring turns it, kept after letting go
        for (int i = 0; i < 150 && (!GizmoShownForTest || refreshing); i++) await Task.Delay(100);
        Check(GizmoShownForTest, $"Gizmo on the game prop: shown ({heldClass})");
        if (GizmoShownForTest)
        {
            var gp0 = CurrentPropFit();
            Check(true, "(info) game prop target, X arrow: " + await GizmoLiveForTest(0, 80, 0));
            for (int i = 0; i < 100 && refreshing; i++) await Task.Delay(100);
            await Task.Delay(1500);
            var gp1 = CurrentPropFit();
            Check(true, "(info) game prop target, Z ring: " + await GizmoLiveForTest(5, 60, 60));
            for (int i = 0; i < 100 && refreshing; i++) await Task.Delay(100);
            await Task.Delay(1500);
            var gp2 = CurrentPropFit();
            Check(gp1 != gp0 && PropFitKey() is string gpk2 && propFits.TryGetValue(gpk2, out var gkept) && gkept == CurrentPropFit(),
                $"Gizmo on the game prop: the fit follows and is kept (move {gp1.MoveX}, {gp1.MoveY}, {gp1.MoveZ}; turn {gp2.TurnX}, {gp2.TurnY}, {gp2.TurnZ}; kept {(PropFitKey() is string kk && propFits.TryGetValue(kk, out var kv) ? kv.ToString() : "none")}; now {CurrentPropFit()})");
            SetPropFit(new PropAdjust(), save: true);
            for (int i = 0; i < 100 && !refreshing; i++) await Task.Delay(50);
            for (int i = 0; i < 200 && refreshing; i++) await Task.Delay(100);
            await Task.Delay(1000);
        }
        float before = preview.ExtentValueForTest;
        SetPropFit(new PropAdjust(MoveZ: 10, TurnZ: 90, Scale: 1.5f), save: true);
        for (int i = 0; i < 100 && (refreshing || MathF.Abs(preview.ExtentValueForTest - before) < 1); i++) await Task.Delay(100);
        Check(preview.ExtentValueForTest > before * 1.4f, $"the preview follows: {before:0.#} → {preview.ExtentValueForTest:0.#} units ({preview.ExtentForTest})");
        Check(PropFitKey() is string pk && propFits.TryGetValue(pk, out var kept) && kept.Scale == 1.5f, "the fit is kept for this prop");
        // shown in the character's hand (Kurt, 2026-10-26), the game's prop there with Compare
        for (int i = 0; i < 100 && !preview.HeldForTest.InHand; i++) await Task.Delay(100);
        var held = preview.HeldForTest;
        Check(held.Holder != null && held.InHand, $"shown in the hand of {held.Holder ?? "(no one)"}");
        // MHO_TEST_ADDED_PROP=<class part>: a prop the mod added (Animations tab) is held too, whichever is the target
        string? addedProp = Environment.GetEnvironmentVariable("MHO_TEST_ADDED_PROP");
        async Task CheckAdded(string when)
        {
            if (string.IsNullOrEmpty(addedProp)) return;
            for (int i = 0; i < 100 && !preview.PropClassesForTest.Contains(addedProp, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
            Check(preview.PropClassesForTest.Contains(addedProp, StringComparison.OrdinalIgnoreCase), $"the mod's added prop {addedProp} is held {when} ({preview.PropClassesForTest})");
        }
        await CheckAdded("with the prop as the target");
        preview.CompareForTestClick();
        Check(!preview.HeldForTest.InHand, "Compare: the game's prop in the hand");
        preview.CompareForTestClick();
        Check(preview.HeldForTest.InHand, "Compare again: the model in the hand");
        // painting works on the prop in the hand (Kurt, 2026-10-27: the body and the prop stay together)
        preview.Painting = true;
        await Task.Delay(300);
        Check(preview.HeldForTest is { Holder: not null, InHand: true } && preview.PaintStartForTest > 0, $"Paint: the prop painted in the hand (its vertices from {preview.PaintStartForTest})");
        preview.Painting = false;
        if (preview.ShowWeights) preview.ShowWeights = false;
        Check(preview.HeldForTest.Holder != null && preview.HeldForTest.InHand, "Paint off: still in the hand");
        if (Environment.GetEnvironmentVariable("MHO_TEST_PROPFITSHOT") is { Length: > 0 } fshot)
        {
            await Task.Delay(1500);
            var form = FindForm()!;
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(fshot);
        }
        bool okFit = await BuildAsync();
        Check(okFit && log.Text.Contains("fit moved"), "Build into Mod applies it (the log says so)");
        SetPropFit(new PropAdjust(), save: true);
        // another game model as the source: laid over the prop by its shape
        GameChosen(GameSrc + otherSource);
        for (int i = 0; i < 600 && !(HasSource && (GameSrc + otherSource).Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
        await Task.Delay(500);
        bool ok2 = await BuildAsync();
        Check(ok2 && log.Text.Contains("laid over"), $"{otherSource}'s model built onto it, laid over it by its shape");
        var copies = PropCopies(file);
        Check(copies is { Others.Count: > 0 } cp && (cp.Others.All(c => built.ContainsKey(c)) || log.Text.Contains("Left as the game's")), $"its copies in other characters' packages were offered ({string.Join(", ", copies?.Others ?? [])})");
        // Model ▾ back to the body with the prop still the source (Kurt, 2026-10-26: nothing to paint): its own model comes in
        GameChosen(row.Key);
        for (int i = 0; i < 600 && !(HasSource && row.Key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
        string own = GameSrc + file;
        meshPick.SelectedIndex = 0;   // (Automatic: the costume's own character)
        for (int i = 0; i < 900 && !(HasSource && own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
        Check(own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource, $"Model ▾ on the body: its own model is the source ({chosenKey})");
        for (int i = 0; i < 100 && !preview.HoldsFittedPropForTest; i++) await Task.Delay(100);
        Check(preview.HoldsFittedPropForTest, "the body holds the prop as last fitted");
        await CheckAdded("with the body as the target");
        TogglePaint();
        await Task.Delay(500);
        Check(preview.Painting && HasSource && preview.PaintStartForTest == 0 && preview.HoldsFittedPropForTest, "Paint is on, with a body to paint, the prop still in its hand");
        TogglePaint();
        // the prop again: its source comes back; the body again: its own
        int propAt = meshNames.FindIndex(n => n.Equals(mesh, StringComparison.OrdinalIgnoreCase)) + 1;
        meshPick.SelectedIndex = propAt;
        for (int i = 0; i < 900 && !(row.Key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource && !refreshing); i++) await Task.Delay(100);
        Check(row.Key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase), $"Model ▾ on the prop: its source back ({chosenKey})");
        // Held On ▾ (step 3): the game's prop on the left palm, in the package at once
        string? heldCls = await HeldOnReady();
        Check(heldCls != null && heldOn.Enabled && heldBones.Contains("g_l_palm", StringComparer.OrdinalIgnoreCase), $"Held On ▾ for {mesh}: its class {heldCls}, {heldBones.Count} bones ({string.Join(", ", heldBones.Take(4))} …), \"{heldOn.Text}\"");
        string? ownBone = heldCls == null ? null : BoneIn(file, heldCls);
        if (heldCls != null && await PickHeldBone("g_l_palm", () => BoneIn(file, heldCls) == "g_l_palm"))
        {
            Check(host.PropBones(file).Any(x => x.Class.Equals(heldCls, StringComparison.OrdinalIgnoreCase) && x.Bone == "g_l_palm"), $"Held On g_l_palm: in the package and kept in the mod (was {ownBone ?? "its parent class's"})");
            // the preview follows at once (Kurt, 2026-10-27: only after stepping to another model)
            for (int i = 0; i < 200 && preview.PropBoneForTest(heldCls) != "g_l_palm"; i++) await Task.Delay(100);
            Check(preview.PropBoneForTest(heldCls) == "g_l_palm", $"the preview holds it on g_l_palm at once ({preview.PropBoneForTest(heldCls) ?? "not shown"})");
        }
        else Check(false, $"Held On g_l_palm: in the package (now {(heldCls == null ? "-" : BoneIn(file, heldCls))})");
        meshPick.SelectedIndex = 0;
        for (int i = 0; i < 900 && !(own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource && !refreshing); i++) await Task.Delay(100);
        Check(own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase), $"Model ▾ on the body again: its source back ({chosenKey})");
        // the body built after the prop: the prop's build stays (Kurt, 2026-10-27: Thor Age of Ultron's hammer went back to the game's)
        string? PropData(string? path)
        {
            if (path == null || !File.Exists(path)) return null;
            var pk = AnimExportCli.Packages.Package.Open(path);
            int i = pk.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).FirstOrDefault(k => pk.GetExportName(k).Equals(mesh, StringComparison.OrdinalIgnoreCase), -1);
            return i < 0 ? null : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pk.GetExportData(i).ToArray()));
        }
        string? builtProp = PropData(ModCopy(file)), stockProp = PropData(StartPackageCore(file));
        bool okBody = await BuildAsync();
        string? afterBody = PropData(ModCopy(file));
        Check(!log.Text.Contains("_uc__marvelplayer", StringComparison.Ordinal) && Path.GetFileName(ModCopy(file) ?? "").Equals(file, StringComparison.OrdinalIgnoreCase),
            $"after a prop's bone went in, the build reads the package under its own name ({Path.GetFileName(ModCopy(file) ?? "")}; {string.Join(" | ", log.Lines.Where(l => l.StartsWith("size:")))})");
        Check(okBody && builtProp != null && builtProp != stockProp && afterBody == builtProp, $"the body built after it: {mesh} as built stays ({(afterBody == builtProp ? "the build" : afterBody == stockProp ? "the game's" : "changed")})");
        if (heldCls != null)
        {
            for (int i = 0; i < 300 && BoneIn(file, heldCls) != "g_l_palm"; i++) await Task.Delay(100);
            Check(BoneIn(file, heldCls) == "g_l_palm", $"the body built after it: {mesh} still held on g_l_palm ({BoneIn(file, heldCls)})");
            // back on its own bone
            meshPick.SelectedIndex = propAt;
            for (int i = 0; i < 900 && !(HasSource && !refreshing && TargetIsProp()); i++) await Task.Delay(100);
            await HeldOnReady();
            bool back = await PickHeldBone(null, () => BoneIn(file, heldCls) == ownBone);
            Check(back && !host.PropBones(file).Any(x => x.Class.Equals(heldCls, StringComparison.OrdinalIgnoreCase)), $"Held On: Its Own: back on {BoneIn(file, heldCls) ?? "its parent class's"}");
            meshPick.SelectedIndex = 0;
            for (int i = 0; i < 900 && !(own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource && !refreshing); i++) await Task.Delay(100);
        }
        // the prop built again after the body (the build starts from the mod's copy, whose prop is the earlier build): the same
        // source comes out the same size, laid over the game's prop (Kurt, 2026-10-27: Cap's shield 3× too big)
        float Extent(string? path)
        {
            // (read from the file itself: no cache)
            if (path == null || !File.Exists(path)) return 0;
            var pk = AnimExportCli.Packages.Package.Open(path);
            int i = pk.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).FirstOrDefault(k => pk.GetExportName(k).Equals(mesh, StringComparison.OrdinalIgnoreCase), -1);
            var pos = i < 0 ? null : AnimExportCli.Meshes.SkeletalMeshReader.TryRead(pk, i, _ => { })?.HighestDetail?.Positions;
            return pos is not { Count: > 0 } ? 0 : (pos.Aggregate(System.Numerics.Vector3.Max) - pos.Aggregate(System.Numerics.Vector3.Min)).Length();
        }
        float firstSize = Extent(ModCopy(file));
        long bytesBefore = ModCopy(file) is string mb && File.Exists(mb) ? new FileInfo(mb).Length : 0;
        meshPick.SelectedIndex = propAt;
        for (int i = 0; i < 900 && !(HasSource && !refreshing && TargetIsProp()); i++) await Task.Delay(100);
        if (!(GameSrc + otherSource).Equals(chosenKey, StringComparison.OrdinalIgnoreCase))
        {
            GameChosen(GameSrc + otherSource);
            for (int i = 0; i < 600 && !(HasSource && (GameSrc + otherSource).Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
        }
        await Task.Delay(500);
        SetPropFit(new PropAdjust(Scale: 1.5f), save: true);
        bool big = await BuildAsync();
        float bigSize = Extent(ModCopy(file));
        SetPropFit(new PropAdjust(), save: true);
        bool again = await BuildAsync();
        float secondSize = Extent(ModCopy(file));
        Check(big && again && firstSize > 0 && MathF.Abs(bigSize / firstSize - 1.5f) < 0.08f && MathF.Abs(secondSize / firstSize - 1) < 0.05f && log.Text.Contains("laid over the game's"),
            $"{otherSource} built onto {mesh} again after the body, at 150 % then 100 %: {firstSize:0.#} → {bigSize:0.#} → {secondSize:0.#} units (laid over the game's, not the last build)");
        long bytesAfter = ModCopy(file) is string ma && File.Exists(ma) ? new FileInfo(ma).Length : 0;
        int tiny = ModCopy(file) is string mt && File.Exists(mt) && MhoPackageModifier.Package.Open(mt) is var pt
            ? pt.Exports.Count(x => x.ObjectName.StartsWith("mff", StringComparison.OrdinalIgnoreCase) && pt.ClassOf(x).Equals("Texture2D", StringComparison.OrdinalIgnoreCase) && x.SerialSize < 512) : 0;
        Check(log.Text.Contains("made 4×4") && tiny > 0 && bytesAfter < bytesBefore * 1.15, $"two more builds of it: the earlier builds' textures cleaned up ({bytesBefore / 1048576.0:0.0} → {bytesAfter / 1048576.0:0.0} MB; {string.Join(" | ", log.Lines.Where(l => l.StartsWith("cleanup")))})");
        say($"  (built {big}/{again}; target {MhoSkeleton.ChosenFor(file)}, source {chosenKey}) the tab's log: " + string.Join(" | ", log.Lines.Where(l => l.StartsWith("prop:") || l.StartsWith("Building") || l.Contains("ERROR")).TakeLast(14)));
        meshPick.SelectedIndex = 0;
        for (int i = 0; i < 900 && !(own.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource && !refreshing); i++) await Task.Delay(100);
        // MHO_TEST_ADD_PROP=<from package>|<class>|<name>: a prop added, picked, fitted and removed on this tab (Kurt, 2026-10-27)
        if (Environment.GetEnvironmentVariable("MHO_TEST_ADD_PROP")?.Split('|') is [var from, var cls, var name]) await TestAddedProp(file, from, cls, name, Check);
        return fails;
    }

    /// <summary>Test: a key pressed on <paramref name="c"/> (WM_KEYDOWN, as Windows sends it).</summary>
    static void SendKeyDown(Control c, Keys key) => SendMessage(c.Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);

    /// <summary>Test: the bone the package's attachment <paramref name="cls"/> names of its own (null: none, its parent's).</summary>
    string? BoneIn(string file, string cls) => ModCopy(file) is string mc && File.Exists(mc) && PowerBorrow.HeldBones(File.ReadAllBytes(mc), cls) is { Count: > 0 } b ? b[0] : null;

    /// <summary>Test: Held On ▾ filled for the target; its prop's class.</summary>
    async Task<string?> HeldOnReady()
    {
        UpdatePropFitUi();
        for (int i = 0; i < 100 && (heldClass == null || heldOn.Items.Count == 0); i++) await Task.Delay(100);
        await Task.Delay(300);
        return heldClass;
    }

    /// <summary>Test: a bone picked under Held On ▾ (null: its own), waited for until <paramref name="done"/>.</summary>
    async Task<bool> PickHeldBone(string? bone, Func<bool> done)
    {
        int at = bone == null ? 0 : heldBones.FindIndex(b => b.Equals(bone, StringComparison.OrdinalIgnoreCase)) + 1;
        if (bone != null && at <= 0) return false;
        heldOn.SelectedIndex = at;
        for (int i = 0; i < 300 && !done(); i++) await Task.Delay(100);
        return done();
    }

    /// <summary>The added-prop steps of <see cref="TestProps"/>: Add a Prop, the selector's entry and the list's row, the
    /// fit from the sliders, ◀ ▶ round it, Remove.</summary>
    async Task TestAddedProp(string file, string from, string cls, string name, Action<bool, string> Check)
    {
        string? before = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        // (picked with the character's own body as the source and nothing remembered for the prop: the body must not be fitted onto it)
        foreach (var k in targetSources.Keys.Where(k => k.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase) && !meshNames.Contains(k[(file.Length + 1)..], StringComparer.OrdinalIgnoreCase)).ToList()) targetSources.Remove(k);
        string? bodySource = chosenKey;
        if (await host.AddPropTo(file, new AddPropForm.Pick(from, cls, name)) is not { } e) { Check(false, $"Add a Prop: {name} added"); return; }
        await AddedPropsChanged(file, e);
        bool HasProp() => preview.PropClassesForTest.Contains(cls, StringComparison.OrdinalIgnoreCase);
        for (int i = 0; i < 200 && !(HasProp() && !refreshing); i++) await Task.Delay(100);
        for (int i = 0; i < 200 && AddedTarget == null; i++) await Task.Delay(100);
        Check(ReferenceEquals(addedPick, e) && MeshCaption()?.Contains("Added Prop") == true && AddedTarget == e,
            $"Add a Prop: {name} picked in the selector (\"{MeshCaption()}\"), the build's target ({MhoSkeleton.ChosenFor(file) ?? "automatic"} in its copy)");
        for (int i = 0; i < 100 && chosenKey != null && chosenKey.Equals(GameSrc + file, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
        Check(chosenKey == null || !chosenKey.Equals(GameSrc + file, StringComparison.OrdinalIgnoreCase), $"the character's body (the source before: {bodySource ?? "none"}) isn't its source ({chosenKey ?? "none"})");
        Check(addedRows.Contains(e), "it's listed under the target's props as an Added Prop");
        Check(propFitBox?.Visible == true && sizeCaption?.Text.Contains("ADDED") == true, $"the Prop Fit sliders show its fit ({sizeCaption?.Text})");
        Check(HasProp(), $"the preview holds it ({preview.PropClassesForTest})");
        if (!HasProp()) Check(false, "  the tab's log: " + string.Join(" | ", log.Lines.TakeLast(10)));
        // unticked: off the costume (out of the preview), its settings kept; ticked: back
        await TestSetAdded(e, false);
        for (int i = 0; i < 200 && (HasProp() || preview.PropClassesForTest.Length == 0); i++) await Task.Delay(100);
        Check(e.Off && !HasProp() && host.AddedProps(file).Contains(e), $"unticked: off the costume, still in the list ({preview.PropClassesForTest})");
        await TestSetAdded(e, true);
        for (int i = 0; i < 200 && !HasProp(); i++) await Task.Delay(100);
        Check(!e.Off && HasProp(), $"ticked again: back on ({preview.PropClassesForTest})");
        // a slider let go: the fit into the package
        string? afterAdd = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        propMoveZ.Value = 15;
        AddedFitCommitted();
        for (int i = 0; i < 300 && host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path == afterAdd; i++) await Task.Delay(100);
        string? afterFit = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        Check(e.Fit.Z == 15 && afterFit != afterAdd && afterAdd != before, $"Move Z let go: the fit kept (Z {e.Fit.Z}) and put into the package (a new copy)");
        // the gizmo (Kurt, 2026-10-27): shown on the picked added prop; an arrow dragged moves it, a ring dragged turns it
        for (int i = 0; i < 100 && !GizmoShownForTest; i++) await Task.Delay(100);
        Check(GizmoShownForTest, "Gizmo: shown on the picked added prop");
        ToggleGizmoForTest(); bool hidden = !GizmoShownForTest; ToggleGizmoForTest();
        Check(hidden && GizmoShownForTest, "Gizmo: the Gizmo button hides it and shows it again");
        await Task.Delay(3000);   // (the slider's fit put in and the preview read again)
        for (int i = 0; i < 100 && !GizmoShownForTest; i++) await Task.Delay(100);
        Check(true, "(info) game prop, X arrow: " + await GizmoLiveForTest(0, 80, 0));
        await Task.Delay(2000);
        Check(true, "(info) game prop, Z ring: " + await GizmoLiveForTest(5, 60, 60));
        await Task.Delay(2000);
        // world X / Y / Z (Kurt, 2026-10-28): the X arrow moves the prop along the world's X, the Z ring turns it around the world's Z
        var s0 = GizmoStateForTest();
        var g0 = (e.Fit.X, e.Fit.Y, e.Fit.Z);
        bool dragged = TestGizmo(0, 80, 0) || TestGizmo(0, 0, 80);
        for (int i = 0; i < 300 && (e.Fit.X, e.Fit.Y, e.Fit.Z) == g0; i++) await Task.Delay(100);
        await Task.Delay(3000);
        for (int i = 0; i < 100 && !GizmoShownForTest; i++) await Task.Delay(100);
        var s1 = GizmoStateForTest();
        var shift = s0 is { } a0 && s1 is { } a1 ? a1.Center - a0.Center : System.Numerics.Vector3.Zero;
        Check(dragged && MathF.Abs(shift.X) > 1 && MathF.Abs(shift.Y) < 0.3f && MathF.Abs(shift.Z) < 0.3f,
            $"Gizmo: the X arrow moves it along the world's X only (moved {shift.X:0.##}, {shift.Y:0.##}, {shift.Z:0.##}; fit {e.Fit.X}, {e.Fit.Y}, {e.Fit.Z}) and puts it in");
        var t0 = (e.Fit.TurnX, e.Fit.TurnY, e.Fit.TurnZ);
        bool turned = TestGizmo(5, 60, 60) || TestGizmo(5, -60, 60);
        for (int i = 0; i < 300 && (e.Fit.TurnX, e.Fit.TurnY, e.Fit.TurnZ) == t0; i++) await Task.Delay(100);
        await Task.Delay(3000);
        for (int i = 0; i < 100 && !GizmoShownForTest; i++) await Task.Delay(100);
        var s2 = GizmoStateForTest();
        string turnSay = "no state";
        bool aroundZ = false;
        if (s1 is { } b1 && s2 is { } b2 && System.Numerics.Matrix4x4.Invert(b1.World, out var inv1))
        {
            // the change from before to after, about the centre: its axis (row vectors: before · D = after)
            var d = inv1 * b2.World;
            var ax = new System.Numerics.Vector3(d.M23 - d.M32, d.M31 - d.M13, d.M12 - d.M21);
            // in world terms: the before placement's rotation carries it; compare the world change W = B1⁻¹·B2 on directions
            var wx = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, b2.World) - System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, b1.World);
            var dirs = new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ };
            // a world-Z turn keeps every direction's Z the same and the centre where it was
            float dzMax = dirs.Max(v => MathF.Abs(System.Numerics.Vector3.TransformNormal(v, b2.World).Z - System.Numerics.Vector3.TransformNormal(v, b1.World).Z));
            float centre = (b2.Center - b1.Center).Length();
            float change = dirs.Max(v => (System.Numerics.Vector3.TransformNormal(v, b2.World) - System.Numerics.Vector3.TransformNormal(v, b1.World)).Length());
            aroundZ = change > 0.05f && dzMax < 0.02f * Math.Max(1, change * 10) && centre < 0.3f;
            turnSay = $"directions changed by up to {change:0.###}, their Z by up to {dzMax:0.####}, centre moved {centre:0.##}";
        }
        Check(turned && aroundZ, $"Gizmo: the Z ring turns it around the world's Z only ({turnSay}; turn {t0} → {e.Fit.TurnX}, {e.Fit.TurnY}, {e.Fit.TurnZ})");
        await Task.Delay(800);
        // Build into Mod with no source: the fit as it shows put in, said in the log
        propMoveY.Value = 4;
        string? beforeBuild = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        bool builtFit = await BuildAsync();
        Check(builtFit && e.Fit.Y == 4 && host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path != beforeBuild && log.Text.Contains("fit is in"),
            $"Build into Mod with no source: the fit put in (Y {e.Fit.Y}) and said so");
        await Task.Delay(1500);
        // the live offset matches the built fit: the prop's frame where the gizmo put it after the package reloads
        if (Environment.GetEnvironmentVariable("MHO_TEST_GIZMOSHOT") is { Length: > 0 } gshot)
        {
            if (Environment.GetEnvironmentVariable("MHO_TEST_QUAD") == "1") preview.ToggleQuad();   // (the gizmo in all four panes)
            await Task.Delay(1500);
            var form = FindForm()!;
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(gshot);
        }
        // Held On ▾ for the added prop: the left palm, then its own again
        if (await HeldOnReady() is string ac && ac.Equals(cls, StringComparison.OrdinalIgnoreCase))
        {
            string? own0 = BoneIn(file, cls);
            string other = own0 == "g_l_palm" ? "g_r_palm" : "g_l_palm";
            bool moved = await PickHeldBone(other, () => BoneIn(file, cls) == other);
            Check(moved && e.Bone == other && own0 != other, $"Held On {other} for {name}: in the package (was {own0})");
            for (int i = 0; i < 200 && preview.PropBoneForTest(cls) != other; i++) await Task.Delay(100);
            Check(preview.PropBoneForTest(cls) == other, $"the preview holds {name} on {other} at once ({preview.PropBoneForTest(cls) ?? "not shown"})");
            bool back0 = await PickHeldBone(null, () => BoneIn(file, cls) == own0);
            Check(back0 && e.Bone == null, $"Held On: Its Own for {name}: back on {BoneIn(file, cls)}");
        }
        else Check(false, $"Held On ▾ for {name}: its class ({heldClass ?? "none"})");
        // from a game prop with a source of its own (the hammer's): that source stays the hammer's (Kurt, 2026-10-27: the hammer's
        // new model swapped in for the axe)
        int hammerAt = meshNames.FindIndex(n => meshProps.Contains(n));
        if (hammerAt >= 0)
        {
            meshPick.SelectedIndex = hammerAt + 1;
            for (int i = 0; i < 300 && (chosenKey == null || refreshing); i++) await Task.Delay(100);
            string? hammerSource = chosenKey;
            // (as a session that had it there saved it: the hammer's own model kept as the axe's source)
            if (hammerSource != null && hammerSource.Equals(GameSrc + file + "|" + meshNames[hammerAt], StringComparison.OrdinalIgnoreCase) && addedBases.TryGetValue(file, out var ab)) targetSources[file + "|" + ab.Mesh] = hammerSource;
            PickAdded(e);
            for (int i = 0; i < 300 && (AddedTarget != e || refreshing); i++) await Task.Delay(100);
            // (the hammer's own model as its source; another package's model could be the axe's too)
            Check(hammerSource == null || !hammerSource.Equals(GameSrc + file + "|" + meshNames[hammerAt], StringComparison.OrdinalIgnoreCase) || chosenKey == null || !chosenKey.Equals(hammerSource, StringComparison.OrdinalIgnoreCase), $"from {meshNames[hammerAt]} (source {hammerSource ?? "none"}) to it: not the hammer's own model ({chosenKey ?? "none"})");
        }
        // a new model: another game model as the source, built onto it; kept with the mod and put into the package
        string? Mesh() => host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path is string dp
            ? MhoExtendedModManager.ModMeshes.Attachments(dp).FirstOrDefault(x => x.Class.Equals(cls, StringComparison.OrdinalIgnoreCase))?.Mesh.Split('.').Last() : null;
        if (Environment.GetEnvironmentVariable("MHO_TEST_ADD_PROP_SOURCE") is { Length: > 0 } newSource)
        {
            GameChosen(GameSrc + newSource);
            for (int i = 0; i < 600 && !(HasSource && (GameSrc + newSource).Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
            for (int i = 0; i < 100 && !(preview.HeldForTest is { InHand: true }); i++) await Task.Delay(100);
            Check(preview.HeldForTest is { InHand: true } && preview.PaintStartForTest > 0, $"{newSource} as its source: shown in the hand (its vertices from {preview.PaintStartForTest})");
            await Task.Delay(500);
            bool built = await BuildAsync();
            for (int i = 0; i < 300 && Mesh()?.Contains("_m") != true; i++) await Task.Delay(100);
            string? keep = e.ModelFrom != null ? Path.Combine(host.WorkFolder, e.ModelFrom) : null;
            Check(built && keep != null && File.Exists(keep) && Mesh()?.Contains("_m") == true, $"Build into Mod: its new model kept ({e.ModelFrom}) and in the package ({Mesh()})");
            if (!built) Check(false, "  the tab's log: " + string.Join(" | ", log.Lines.TakeLast(12)));
            Check(e.Fit.Z == 15 && Mesh()?.Contains("_fit") == true, $"its fit kept on the new model (Z {e.Fit.Z}, {Mesh()})");
            // no source: its character's model back
            bool back = await AddedOwnModel(e);
            for (int i = 0; i < 300 && Mesh()?.Contains("_m") == true; i++) await Task.Delay(100);
            Check(back && e.ModelFrom == null && Mesh() is string m2 && !m2.Contains("_m"), $"no source: its character's model back ({Mesh()})");
        }
        // ◀ ▶ round it
        StepMesh(1);
        bool off = addedPick == null;
        StepMesh(-1);
        Check(off && ReferenceEquals(addedPick, e), "▶ steps off it, ◀ back onto it");
        // Remove, with the Del key on its row (the key itself, as Windows sends it)
        var delRow = targetProps.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, e));
        Check(delRow != null, $"{name} is a row of the target's props");
        if (delRow != null)
        {
            targetProps.ClearSelection(); delRow.Selected = true;
            SendKeyDown(targetProps, Keys.Delete);
        }
        else RemovePropClicked();
        for (int i = 0; i < 300 && (meshAdded.Contains(e) || HasProp() || refreshing); i++) await Task.Delay(100);
        Check(!meshAdded.Contains(e) && addedPick == null && !addedRows.Contains(e), "Remove: gone from the selector and the list");
        for (int i = 0; i < 150 && preview.PropClassesForTest.Length == 0; i++) await Task.Delay(100);   // (the props read again)
        Check(!HasProp() && preview.PropClassesForTest.Length > 0, $"Remove: gone from the preview, the costume's own props still held ({preview.PropClassesForTest})");
        // Ctrl+Z: the removed prop back (Kurt, 2026-10-27), Ctrl+Y: gone again
        bool Listed() => host.AddedProps(file).Any(a => a.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
        Undo();
        for (int i = 0; i < 300 && !(Listed() && HasProp()); i++) await Task.Delay(100);
        Check(Listed() && HasProp() && meshAdded.Any(a => a.Class.Equals(cls, StringComparison.OrdinalIgnoreCase)), $"Ctrl+Z after Remove: {name} back in the list, the selector and the preview ({preview.PropClassesForTest})");
        Redo();
        for (int i = 0; i < 300 && (Listed() || HasProp()); i++) await Task.Delay(100);
        Check(!Listed() && !HasProp(), $"Ctrl+Y: removed again ({preview.PropClassesForTest})");

        // Add as a Prop: an MFF part (a weapon) as a prop of its own, on the bone that holds it (MHO_TEST_ADD_PART = the part's name;
        // the source must be an MFF character with that part)
        if (Environment.GetEnvironmentVariable("MHO_TEST_ADD_PART") is { Length: > 0 } partSpec && partSpec.Split('|') is [var mffChar, var partName])
        {
            // the MFF character (MHO_TEST_ADD_PART = "<MFF folder>|<part>") as the source
            sourceKind.SelectedIndex = 0; await Task.Delay(300);
            characterFilter.Text = mffChar; FillCharacters();
            int ci = characters.Items.OfType<CharacterList.Item>().ToList().FindIndex(r => r.Key.Equals(mffChar, StringComparison.OrdinalIgnoreCase));
            if (ci >= 0) characters.SelectedIndex = ci;
            for (int i = 0; i < 600 && model?.Folder.Equals(mffChar, StringComparison.OrdinalIgnoreCase) != true; i++) await Task.Delay(100);
            Check(model?.Parts.Any(p => p.Name.Equals(partName, StringComparison.OrdinalIgnoreCase)) == true, $"Add as a Prop: the source has {partName} ({model?.Folder ?? "no MFF source"})");
            var ap = await AddPartProp(partName);
            string? PartMesh() => ap != null && ModCopy(file) is string dp ? MhoExtendedModManager.ModMeshes.Attachments(dp).FirstOrDefault(x => PowerBorrow.SameClass(x.Class, ap.Class))?.Mesh.Split('.').Last() : null;
            for (int i = 0; i < 300 && PartMesh()?.Contains("_m") != true; i++) await Task.Delay(100);
            Check(ap != null && ap.KeepShape && ap.Bone != null && ap.Name.EndsWith("(MFF)") && PartMesh()?.Contains("_m") == true,
                $"Add as a Prop: {ap?.Name ?? "-"} on {ap?.Bone ?? "-"}, its model built ({PartMesh()})");
            for (int i = 0; i < 200 && !preview.PropClassesForTest.Contains(ap?.Class ?? "?", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
            Check(ap != null && preview.PropClassesForTest.Contains(ap.Class, StringComparison.OrdinalIgnoreCase), $"Add as a Prop: in the preview ({preview.PropClassesForTest})");
            if (ap != null)
            {
                // the gizmo on it, held on another bone (Kurt's Doom: the sword on g_r_wrist)
                PickAdded(ap);
                await SetAddedBone(ap, "g_l_wrist");
                for (int i = 0; i < 100 && (!GizmoShownForTest || refreshing); i++) await Task.Delay(100);
                await Task.Delay(3000);
                Check(true, "(info) MFF prop on g_l_wrist, X arrow: " + await GizmoLiveForTest(0, 80, 0));
                await Task.Delay(2000);
                Check(true, "(info) MFF prop on g_l_wrist, Z ring: " + await GizmoLiveForTest(5, 60, 60));
            }
            if (ap != null) { addedPick = ap; RemovePropClicked(); for (int i = 0; i < 300 && meshAdded.Contains(ap); i++) await Task.Delay(100); }
        }

        // the Props view (MHO_TEST_PROPMODE = "<costume package>|<mesh>", another hero's prop): a click on the character lists its
        // props without changing the source or the target; a click on the prop adds it to the target
        if (Environment.GetEnvironmentVariable("MHO_TEST_PROPMODE") is { Length: > 0 } pmSpec && pmSpec.Split('|') is [var pmFile, var pmMesh])
        {
            string? targetBefore = ChosenPackage?.Key;
            SetView(1);
            Check(propMode && CurrentView == 1 && sourceSummonsPanel.Visible, "Props view: on, the props list under the characters");
            if (!GameMode) { sourceKind.SelectedIndex = 2; await Task.Delay(300); }
            string? sourceBefore = chosenKey;
            sourceKindTab = 0; characterFilter.Text = ""; expanded.Add("game " + DescribePackage(pmFile).Title); FillCharacters();
            int ci = characters.Items.OfType<CharacterList.Item>().ToList().FindIndex(r => r.Key.Equals(GameSrc + pmFile, StringComparison.OrdinalIgnoreCase));
            if (ci >= 0) characters.SelectedIndex = ci;
            await Task.Delay(500);
            Check(ci >= 0 && ChosenPackage?.Key == targetBefore && chosenKey == sourceBefore, $"Props view: a click on {pmFile} keeps the target ({ChosenPackage?.Key}) and the source ({chosenKey ?? "none"})");
            var pr = sourceSummons.Items.OfType<CharacterList.Item>().FirstOrDefault(r => r.Key.StartsWith(PropGame) && Props[int.Parse(r.Key[PropGame.Length..])].Mesh.Equals(pmMesh, StringComparison.OrdinalIgnoreCase));
            Check(true, $"(info) props list: {sourceSummonsTitle.Text}: {string.Join(", ", sourceSummons.Items.OfType<CharacterList.Item>().Select(r => r.Title))}");
            Check(pr != null, $"Props view: {pmMesh} listed");
            if (pr != null && targetBefore != null)
            {
                string pmCls = Props[int.Parse(pr.Key[PropGame.Length..])].Class;
                // a left click: the prop on its own over the preview
                ShowPropModel(pr);
                for (int i = 0; i < 300 && !PropShowForTest.HasMesh; i++) await Task.Delay(100);
                Check(PropShowForTest is { Shown: true, HasMesh: true } && ChosenPackage?.Key == targetBefore, $"Props view: a left click shows it ({PropShowForTest.Caption}), the target stays");
                PropPicked(pr);
                for (int i = 0; i < 300 && !host.AddedProps(targetBefore).Any(a => a.Class.Equals(pmCls, StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(100);
                var added = host.AddedProps(targetBefore).FirstOrDefault(a => a.Class.Equals(pmCls, StringComparison.OrdinalIgnoreCase));
                Check(added != null && ChosenPackage?.Key == targetBefore, $"Props view: a click adds {pmCls} to {targetBefore} ({added?.Name ?? "not added"}, from {added?.From ?? "-"})");
                bool animated = Props[int.Parse(pr.Key[PropGame.Length..])].Kind == "other";   // (shown only while an animation of its own plays)
                for (int i = 0; i < 200 && !animated && !preview.PropClassesForTest.Contains(pmCls, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                Check(animated || preview.PropClassesForTest.Contains(pmCls, StringComparison.OrdinalIgnoreCase), $"Props view: in the preview ({(animated ? "an animated prop: not without its animation" : preview.PropClassesForTest)})");
                if (Environment.GetEnvironmentVariable("MHO_TEST_PROPSHOWSHOT") is { Length: > 0 } pshot)
                {
                    await Task.Delay(2500);
                    var form = FindForm()!;
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                    bmp.Save(pshot);
                }

                Check(!PropShowForTest.Shown, "Props view: adding it shows the target again");
                // Revert to Last Build: the package as built, the props as they were then (the one just added gone)
                if (added != null && LastBuildOf(targetBefore) is { } lb)
                {
                    bool reverted = await RevertToLastBuild(targetBefore, ask: false);
                    for (int i = 0; i < 100 && preview.PropClassesForTest.Contains(pmCls, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                    for (int i = 0; i < 150 && !preview.PropClassesForTest.Contains("thorhammer", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                    Check(preview.PropClassesForTest.Contains("thorhammer", StringComparison.OrdinalIgnoreCase), $"Revert to Last Build: the costume's own prop still held ({preview.PropClassesForTest})");
                    Check(reverted && !host.AddedProps(targetBefore).Any(a => a.Class.Equals(pmCls, StringComparison.OrdinalIgnoreCase)) && !preview.PropClassesForTest.Contains(pmCls, StringComparison.OrdinalIgnoreCase),
                        $"Revert to Last Build: back to the build of {lb.At:HH:mm:ss}, {pmCls} gone ({preview.PropClassesForTest})");
                }
                else if (added != null) { Check(false, "Revert to Last Build: no last build of the target was kept"); addedPick = added; RemovePropClicked(); for (int i = 0; i < 300 && meshAdded.Contains(added); i++) await Task.Delay(100); }
            }
            // Revert to Original: the costume as before the Model tab, no added props, no source
            if (targetBefore != null)
            {
                bool orig = await RevertToOriginal(targetBefore, ask: false);
                string? now = host.Packages.FirstOrDefault(x => x.File.Equals(targetBefore, StringComparison.OrdinalIgnoreCase)).Path;
                for (int i = 0; i < 150 && !preview.PropClassesForTest.Contains("thorhammer", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                Check(orig && host.AddedProps(targetBefore).Count == 0 && chosenKey == null && !HasSource && now?.Contains("original ") == true && !built.ContainsKey(targetBefore),
                    $"Revert to Original: {targetBefore} back to its original ({now}), no added props, no source");
                Check(preview.PropClassesForTest.Contains("thorhammer", StringComparison.OrdinalIgnoreCase), $"Revert to Original: the preview shows the costume with its own prop ({preview.PropClassesForTest})");
            }
            // a summon listed with the costume's props (MHO_TEST_PROPMODE_SUMMON = its package): added as a model of its own
            if (Environment.GetEnvironmentVariable("MHO_TEST_PROPMODE_SUMMON") is { Length: > 0 } sumFile && targetBefore != null)
            {
                var sr = sourceSummons.Items.OfType<CharacterList.Item>().FirstOrDefault(r => r.Key.Equals(PropSummon + sumFile, StringComparison.OrdinalIgnoreCase));
                Check(sr != null, $"Props view: the summon {sumFile} listed");
                if (sr != null)
                {
                    for (int i = 0; i < 300 && addingProp; i++) await Task.Delay(100);   // (the add before it finishes first)
                    int had = host.AddedProps(targetBefore).Count;
                    PropPicked(sr);
                    for (int i = 0; i < 900 && (host.AddedProps(targetBefore).Count == had || addingProp); i++) await Task.Delay(100);
                    var sa = host.AddedProps(targetBefore).FirstOrDefault(a => a.Name.EndsWith("(Summon)") || a.Name.EndsWith("(Power Model)"));
                    Check(sa != null && sa.KeepShape, $"Props view: the summon added as a model of its own ({sa?.Name ?? "not added"}, carried by {sa?.Class ?? "-"})");
                    if (sa == null) Check(false, "  the tab's log: " + string.Join(" | ", log.Lines.TakeLast(25)));
                    for (int i = 0; i < 200 && sa != null && !preview.PropClassesForTest.Contains(sa.Class, StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
                    Check(sa != null && preview.PropClassesForTest.Contains(sa.Class, StringComparison.OrdinalIgnoreCase), $"Props view: the summon in the preview ({preview.PropClassesForTest})");
                    Check(GameMode && propMode, $"Props view: still From the Game in the Props view after the build (source kind {sourceKind.SelectedIndex})");
                    if (sa != null) { addedPick = sa; RemovePropClicked(); for (int i = 0; i < 300 && meshAdded.Contains(sa); i++) await Task.Delay(100); }
                }
            }
            SetView(0);
            Check(!propMode && CurrentView == 0, "Props view: back to Characters");
        }

        // Add a Prop → From an FBX (step 4): a model of the user's own, carried by a free prop class, built as made
        if (Environment.GetEnvironmentVariable("MHO_TEST_ADD_FBX") is { Length: > 0 } fbxProp && File.Exists(fbxProp))
        {
            var raw = FbxReimport.Raw(fbxProp);
            float want = (raw.Max - raw.Min).Length();
            var f = await AddFbxProp(file, fbxProp);
            string? FbxMesh() => f != null && ModCopy(file) is string dp ? MhoExtendedModManager.ModMeshes.Attachments(dp).FirstOrDefault(x => PowerBorrow.SameClass(x.Class, f.Class))?.Mesh.Split('.').Last() : null;
            float Size(string? meshName)
            {
                if (meshName == null || ModCopy(file) is not string path) return 0;
                var pk = AnimExportCli.Packages.Package.Open(path);
                int k = pk.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).FirstOrDefault(x => pk.GetExportName(x).Equals(meshName, StringComparison.OrdinalIgnoreCase), -1);
                var pos = k < 0 ? null : AnimExportCli.Meshes.SkeletalMeshReader.TryRead(pk, k, _ => { })?.HighestDetail?.Positions;
                return pos is not { Count: > 0 } ? 0 : (pos.Aggregate(System.Numerics.Vector3.Max) - pos.Aggregate(System.Numerics.Vector3.Min)).Length();
            }
            for (int i = 0; i < 300 && FbxMesh()?.Contains("_m") != true; i++) await Task.Delay(100);
            string? fm = FbxMesh();
            float got = Size(fm);
            Check(f != null && f.KeepShape && f.ModelFrom != null && fm?.Contains("_m") == true, $"From an FBX: {f?.Name ?? "-"} on {f?.Class ?? "-"}, its model built and in the package ({fm})");
            // (its fitted copy: the carrier's default turn may apply; the size is the model's own)
            Check(got > 0 && MathF.Abs(got / want - 1) < 0.05f && log.Text.Contains("kept as made"), $"From an FBX: kept as made ({got:0.#} units, the file's {want:0.#})");
            for (int i = 0; i < 200 && !preview.PropClassesForTest.Contains(f?.Class ?? "?", StringComparison.OrdinalIgnoreCase); i++) await Task.Delay(100);
            Check(f != null && preview.PropClassesForTest.Contains(f.Class, StringComparison.OrdinalIgnoreCase), $"From an FBX: in the preview ({preview.PropClassesForTest})");
            if (f != null)
            {
                for (int i = 0; i < 100 && (!GizmoShownForTest || refreshing); i++) await Task.Delay(100);
                await Task.Delay(2000);
                Check(true, "(info) FBX prop, X arrow: " + await GizmoLiveForTest(0, 80, 0));
                await Task.Delay(2000);
                Check(true, "(info) FBX prop, Z ring: " + await GizmoLiveForTest(5, 60, 60));
            }
            if (f != null) { addedPick = f; RemovePropClicked(); for (int i = 0; i < 300 && meshAdded.Contains(f); i++) await Task.Delay(100); }
        }
    }
}
