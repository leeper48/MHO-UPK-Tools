using System.Text.Json;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Summons and the Blender loop made shorter (Kurt, 2026-10-25: the summon swap worked but the workflow was clunky): the
/// Summons toggles over both From the Game lists (every hero's summons and power models, each hero's under a heading after its
/// costumes), Edit in Blender (no source picked: the target's own model becomes the source; a scene made before for this work
/// is opened again instead of a new export), the Blender link's status line, and Build When Blender Saves.
/// </summary>
sealed partial class ModelPage
{
    Button summonsTarget = null!, summonsSource = null!, blenderButton = null!;
    readonly Label blenderStatus = new() { AutoSize = true, Margin = new Padding(2, 4, 12, 0), Visible = false };
    readonly CheckBox buildOnSave = new() { Text = "Build When Blender Saves", AutoSize = true, Margin = new Padding(0, 2, 0, 0), Visible = false };
    bool summonsOnlyTarget, summonsOnlySource;

    void InitSummons()
    {
        summonsTarget = Ui.FlatButton("Summons", () => { summonsOnlyTarget = !summonsOnlyTarget; Ui.Lit(summonsTarget, summonsOnlyTarget); FillPackages(); },
            "Show only summons and power models: the characters and models heroes' powers bring in (Squirrel Girl's squirrels, Magik's demons, Cyclops's Strike Team …), under each hero. Click again for the whole list.");
        summonsSource = Ui.FlatButton("Summons", () => { summonsOnlySource = !summonsOnlySource; Ui.Lit(summonsSource, summonsOnlySource); FillCharacters(); },
            "Show only summons and power models from the game, under each hero. Click again for the whole list. Right-click any one to make it the target too.");
        summonsTarget.Visible = gameList && Summons.Count > 0;
        summonsSource.Visible = GameMode;
        blenderButton = Ui.FlatButton("Edit in Blender", EditInBlender,
            "Opens this work in Blender: Ctrl+S there sends your changes back here. With no source picked, the target's own model becomes the source (a summon, a costume: change the game's model itself). Opened in Blender before: that same scene opens again, with your earlier work in it (Full Export ▾ → Open in Blender makes a fresh one).");
        Ui.Tip(blenderStatus, "This work is linked to a Blender scene: each Ctrl+S in Blender sends what you changed back here.");
        Ui.TipTitled(buildOnSave, "Build When Blender Saves", "Each Ctrl+S in Blender that sends a change also runs Build into Mod, so the mod has your edit at once (then Save Changes and Apply Changes, as after any build). Kept with the mod.");
        buildOnSave.CheckedChanged += (_, _) => { if (!restoring2) SaveState(); };
    }

    static bool IsSummon(string file) => Summons.ContainsKey(file);

    /// <summary>A partner build is running (its own partners aren't offered again).</summary>
    bool buildingPartners;

    /// <summary>
    /// The other models the same power of the same hero brings in (Kurt, 2026-10-26: Strike Team - Beast arrives as the
    /// power's own tumbling model, beast.beast in UC__PowerCyclops_CallBeast_SF, then stays as the summoned Ultimate Beast,
    /// beast_astonishing in UC__MarvelAgent_CyclopsUltimateBeast_SF: building one left the other Beast). Summons and power
    /// models only (not copies), with another model path than this package's.
    /// </summary>
    internal static List<(string File, string Name, string Power)> SummonPartners(string file)
    {
        if (!Summons.TryGetValue(Path.GetFileName(file), out var mine)) return [];
        var own = mine.Where(x => x.Kind is "summon" or "power").ToList();
        if (own.Count == 0) return [];
        string hero = own[0].Hero;
        var powers = own.Select(x => x.Power).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = mine.Select(x => x.MeshPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // one power model and one summon for the same power, one of each (Strike Team - Beast: the tumble and the Beast who
        // stays). A power that summons several characters (To Me, My X-Men!: Beast, Jean, Iceman …) pairs nothing: those are
        // other characters, not this one's other model.
        var list = new List<(string, string, string)>();
        foreach (string power in powers)
        {
            var users = Summons.Where(kv => kv.Value.Any(x => x.Kind is "summon" or "power" && x.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) && x.Power.Equals(power, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => (File: kv.Key, Row: kv.Value.First(x => x.Kind is "summon" or "power" && x.Power.Equals(power, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (users.Count(u => u.Row.Kind == "summon") != 1 || users.Count(u => u.Row.Kind == "power") != 1) continue;
            foreach (var (f, row) in users)
                if (!f.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && row.MeshPath != null && !paths.Contains(row.MeshPath)
                    && !list.Any(l => l.Item1.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    list.Add((f, SummonName(row), power));
        }
        return list;
    }

    /// <summary>
    /// Test (--model-partner-test; a scratch MHO_EXTMM_HOME with MHO_EXTMM_TEST_DIALOGS, which answers with the first button;
    /// the mod isn't saved): a game source built onto a power model (Strike Team's tumbling Beast), the offer answered Build
    /// onto It Too: the summoned Beast is built as well, with the same source.
    /// </summary>
    internal async Task<int> TestPartners(string target, string source, Action<string> say)
    {
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        if (Settings.Current.CookedFolder is not string cooked) { say("no game folder"); return 1; }
        var partners = SummonPartners(target);
        Check(partners.Count == 1, $"{target}'s partner: {string.Join(", ", partners.Select(p => p.File))}");
        if (partners.Count == 0) return fails;
        if (!host.Packages.Any(p => p.File.Equals(target, StringComparison.OrdinalIgnoreCase))) { host.AddPackage(target, StockFiles.For(cooked, target)); added.Add(target); }
        gameList = false; FillPackages(); Reselect(packages, target);
        if (!GameMode) { sourceKind.SelectedIndex = 2; await Task.Delay(300); }
        GameChosen(GameSrc + source);
        for (int i = 0; i < 600 && !(HasSource && !refreshing && preview.LooksForTest.Any()); i++) await Task.Delay(100);
        bool ok = await BuildAsync();
        Check(ok && built.ContainsKey(target), $"the source is built onto {target}");
        string pf = partners[0].File;
        Check(built.ContainsKey(pf) && host.Packages.Any(p => p.File.Equals(pf, StringComparison.OrdinalIgnoreCase)), $"Build onto It Too: {pf} is built as well and in the mod");
        if (built.TryGetValue(pf, out var bp))
        {
            var lm = ModMeshes.Load(new MeshRef(pf, bp, "", Array.FindIndex(MhoPackageModifier.Package.Open(bp).Exports, e => MhoPackageModifier.Package.Open(bp).ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))), cooked, out _);
            say($"  {pf}: {lm?.Positions.Length:N0} vertices after the build");
        }
        Check(ChosenPackage?.Key.Equals(target, StringComparison.OrdinalIgnoreCase) == true, "and the tab is back on the first target");
        return fails;
    }

    /// <summary>After a build: the power's other models (<see cref="SummonPartners"/>) not built yet, offered with the same
    /// source; a yes adds each to the mod (when it isn't in it) and builds it, then comes back to this target.</summary>
    async Task OfferSummonPartners(string file)
    {
        var partners = SummonPartners(file).Where(p => !built.ContainsKey(p.File)).ToList();
        if (partners.Count == 0 || Settings.Current.CookedFolder is not string cooked) return;
        string text = $"{string.Join(" and ", partners.Select(p => p.Power).Distinct(StringComparer.OrdinalIgnoreCase))} also brings in {(partners.Count == 1 ? "a character" : "characters")} with a model of {(partners.Count == 1 ? "its" : "their")} own:\n\n"
            + string.Join("\n", partners.Select(p => $"  • {p.Name} ({p.File})"))
            + $"\n\nWHAT IT MEANS\n  • Your build changed {file} only. In game, {(partners.Count == 1 ? "that one" : "those")} still {(partners.Count == 1 ? "shows its" : "show their")} own model (Strike Team: Beast tumbles in as yours, then stays as the game's Beast)."
            + "\n  • Build onto It Too builds the same source onto " + (partners.Count == 1 ? "it" : "each") + " (added to the mod when needed); its copies in other characters' packages are asked about as usual.";
        if (Dialog.Choose(this, text, "Build the Summon Too", "Build onto It Too", "Not Now") != 0) return;
        buildingPartners = true;
        try
        {
            foreach (var (pf, name, _) in partners)
            {
                if (!host.Packages.Any(p => p.File.Equals(pf, StringComparison.OrdinalIgnoreCase)))
                {
                    try { host.AddPackage(pf, BasePackage.Resolve(pf)); added.Add(pf); }
                    catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Log($"{pf}: {ex.Message}"); continue; }
                }
                gameList = false; packageFilter.Text = ""; FillPackages(); Reselect(packages, pf);
                await Task.Delay(200);
                Log($"Building onto {name} ({pf}) too:");
                await BuildAsync();
            }
        }
        finally
        {
            buildingPartners = false;
            gameList = false; FillPackages(); Reselect(packages, file);
            SaveState();
        }
    }

    /// <summary>One hero group's packages as rows: the costumes first, then its summons and power models under a heading
    /// (a label row: nothing to pick).</summary>
    static IEnumerable<CharacterList.Item> GroupRows(string group, List<(string File, string Title, string Detail)> list, Func<(string File, string Title, string Detail), CharacterList.Item> row)
    {
        foreach (var p in list.Where(p => !IsSummon(p.File))) yield return row(p);
        var sums = list.Where(p => IsSummon(p.File)).ToList();
        if (sums.Count == 0) yield break;
        yield return new CharacterList.Item(LabelKey + group, "SUMMONS AND POWER MODELS", "", Indent: 1, Label: true);
        foreach (var p in sums) yield return row(p);
    }

    const string LabelKey = "label:";

    /// <summary>A group header's detail: "12 packages", "12 packages · 3 summons".</summary>
    static string GroupDetail(List<(string File, string Title, string Detail)> list)
    {
        int sums = list.Count(p => IsSummon(p.File)), rest = list.Count - sums;
        return rest == 0 ? $"{sums} summon{(sums == 1 ? "" : "s")}" : $"{rest} package{(rest == 1 ? "" : "s")}{(sums > 0 ? $" · {sums} summon{(sums == 1 ? "" : "s")}" : "")}";
    }

    /// <summary>A summon's row thumbnail: the icon of the power that brings it in (summons have no portrait), else the package's.</summary>
    static Image? PackageThumb(string file) =>
        Summons.TryGetValue(Path.GetFileName(file), out var l) && l.FirstOrDefault(x => x.Icon != null)?.Icon is string icon ? Thumbs.Summon(file, icon) : Thumbs.BaseHero(file);

    /// <summary>A right-click on a summon in the Source list: it's the source, and the target too (added to the mod from the
    /// game when the mod doesn't hold it yet).</summary>
    void SourceBoth(int row, string file)
    {
        characters.SelectedIndex = row;   // the source (CharacterChosen)
        MirrorTarget(file);
    }

    void MirrorTarget(string file)
    {
        // the Target's From the Game list as the Source's (Kurt, 2026-10-26: NPCs › Enemies and Bosses › Abigail Brand on the
        // left shows the same on the right): its tab, filter, Summons toggle and open group, the same package picked, which
        // asks to add it to the mod as From the Game does (the mod's own copy is picked when it has one)
        gameList = true;
        targetKindTab = sourceKindTab;
        summonsOnlyTarget = summonsOnlySource; Ui.Lit(summonsTarget, summonsOnlyTarget);
        packageFilter.Text = characterFilter.Text;
        expandedHeroes.Add(DescribePackage(file).Title);
        FillPackages();
        Reselect(packages, file);
        SaveState();
    }

    // --- Edit in Blender ----------------------------------------------------------------------------------------------------------
    async void EditInBlender()
    {
        if (building) return;
        if (ChosenPackage is not CharacterList.Item pkg) { Log("Edit in Blender: pick the target first (the package to change)."); return; }
        if (!HasSource && !await UseOwnModel(pkg.Key)) return;
        var (folder, _) = BlenderLink();
        if (folder != null && File.Exists(Path.Combine(folder, "model.blend")))
        {
            if (BlenderLaunch.Reopen(folder) is string why) { Log("Blender: " + why); return; }
            Log($"Blender: opening this work's scene again ({Path.Combine(folder, "model.blend")}). Each Ctrl+S there sends what you changed back here.");
            WatchBlender();
            UpdateBlenderStatus();
            return;
        }
        ExportFbx(true, null);
    }

    /// <summary>The target's own model as the source (Source → From the Game → that package), loaded; false if it didn't load.</summary>
    async Task<bool> UseOwnModel(string file)
    {
        string key = GameSrc + file;
        if (!GameMode) sourceKind.SelectedIndex = 2;
        summonsOnlySource = false; Ui.Lit(summonsSource, false);
        characterFilter.Text = "";
        expanded.Add("game " + DescribePackage(file).Title);
        SourceTabFor(file);
        FillCharacters();
        Reselect(characters, key);
        ShowSummonSource(file);   // (a summon: in the panel below)
        if (!key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase)) GameChosen(key);   // (a package the list doesn't hold)
        for (int i = 0; i < 1200 && !(key.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource); i++) await Task.Delay(100);
        if (!HasSource) { Log($"{file}: its own model couldn't be read as the source."); return false; }
        for (int i = 0; i < 600 && preview.AnimationNames.Count == 0; i++) await Task.Delay(100);   // (the export names the preview's animations)
        Log($"Source: {file}'s own model (From the Game), to change it.");
        return true;
    }

    /// <summary>The line under Build: the Blender scene this work is linked to and the last save taken in (hidden when none).</summary>
    void UpdateBlenderStatus()
    {
        var (folder, applied) = HasSource && ChosenPackage != null ? BlenderLink() : (null, 0);
        bool linked = folder != null && Directory.Exists(folder);
        blenderStatus.Visible = buildOnSave.Visible = linked;
        if (!linked) return;
        string? time = null;
        try
        {
            string sync = Path.Combine(folder!, "from_blender", "sync.json");
            if (applied > 0 && File.Exists(sync) && JsonDocument.Parse(File.ReadAllText(sync)).RootElement.TryGetProperty("time", out var t) && DateTime.TryParse(t.GetString(), out var dt))
                time = dt.ToString("HH:mm");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        blenderStatus.Text = applied == 0 ? "Linked to Blender · Nothing Saved There Yet" : $"Linked to Blender · Save {applied} Taken In{(time != null ? " at " + time : "")}";
        blenderStatus.ForeColor = applied == 0 ? Ui.Subtle : Ui.Text;
    }

    /// <summary>Build When Blender Saves: a build once the change is in (after a build already running).</summary>
    async void BuildAfterBlender()
    {
        for (int i = 0; i < 6000 && building; i++) await Task.Delay(100);
        if (IsDisposed || ChosenPackage == null) return;
        // (Build clears the log: the note comes after it)
        bool ok = await BuildAsync();
        Log(ok ? "Blender: built into the mod after the save (Build When Blender Saves)." : "Blender: Build When Blender Saves couldn't build (see above).");
    }
}
