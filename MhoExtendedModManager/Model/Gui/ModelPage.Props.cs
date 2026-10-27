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
        Log($"Target: {PropLabel(mesh)} in {file} (Model ▾ shows it). To change it: Edit in Blender, or pick an FBX as the source.");
    }

    /// <summary>The target is a prop (a weapon or shield picked under Model ▾ or from the Source's panel).</summary>
    bool TargetIsProp()
    {
        if (ChosenPackage is not CharacterList.Item t) return false;
        try { return PropFit.IsProp(MhoSkeleton.Load(StartPackage(t.Key), null)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException) { return false; }
    }

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
        // another game model as the source: laid over the prop by its shape
        GameChosen(GameSrc + otherSource);
        for (int i = 0; i < 600 && !(HasSource && (GameSrc + otherSource).Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && !refreshing); i++) await Task.Delay(100);
        await Task.Delay(500);
        bool ok2 = await BuildAsync();
        Check(ok2 && log.Text.Contains("laid over"), $"{otherSource}'s model built onto it, laid over it by its shape");
        var copies = PropCopies(file);
        Check(copies is { Others.Count: > 0 } cp && (cp.Others.All(c => built.ContainsKey(c)) || log.Text.Contains("Left as the game's")), $"its copies in other characters' packages were offered ({string.Join(", ", copies?.Others ?? [])})");
        return fails;
    }
}
