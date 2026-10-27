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
    internal static List<PropRow> PropsFor(string hero, string? costumeClass)
    {
        var mine = Props.Where(p => p.Kind == "prop" && p.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase));
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
        // MHO_TEST_ADD_PROP=<from package>|<class>|<name>: a prop added, picked, fitted and removed on this tab (Kurt, 2026-10-27)
        if (Environment.GetEnvironmentVariable("MHO_TEST_ADD_PROP")?.Split('|') is [var from, var cls, var name]) await TestAddedProp(file, from, cls, name, Check);
        return fails;
    }

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
        // a slider let go: the fit into the package
        string? afterAdd = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        propMoveZ.Value = 15;
        AddedFitCommitted();
        for (int i = 0; i < 300 && host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path == afterAdd; i++) await Task.Delay(100);
        string? afterFit = host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;
        Check(e.Fit.Z == 15 && afterFit != afterAdd && afterAdd != before, $"Move Z let go: the fit kept (Z {e.Fit.Z}) and put into the package (a new copy)");
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
            if (hammerSource != null && addedBases.TryGetValue(file, out var ab)) targetSources[file + "|" + ab.Mesh] = hammerSource;
            PickAdded(e);
            for (int i = 0; i < 300 && (AddedTarget != e || refreshing); i++) await Task.Delay(100);
            Check(hammerSource == null || chosenKey == null || !chosenKey.Equals(hammerSource, StringComparison.OrdinalIgnoreCase), $"from {meshNames[hammerAt]} (source {hammerSource ?? "none"}) to it: not that source ({chosenKey ?? "none"})");
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
        // Remove
        RemovePropClicked();
        for (int i = 0; i < 300 && (meshAdded.Contains(e) || HasProp() || refreshing); i++) await Task.Delay(100);
        Check(!meshAdded.Contains(e) && addedPick == null && !addedRows.Contains(e), "Remove: gone from the selector and the list");
        for (int i = 0; i < 150 && preview.PropClassesForTest.Length == 0; i++) await Task.Delay(100);   // (the props read again)
        Check(!HasProp() && preview.PropClassesForTest.Length > 0, $"Remove: gone from the preview, the costume's own props still held ({preview.PropClassesForTest})");
    }
}
