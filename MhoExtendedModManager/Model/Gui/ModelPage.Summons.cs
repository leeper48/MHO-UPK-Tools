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
            "Show only summons and power models from the game, under each hero. Click again for the whole list. Right-click one to make it the target too.");
        summonsTarget.Visible = gameList;
        summonsSource.Visible = GameMode;
        blenderButton = Ui.FlatButton("Edit in Blender", EditInBlender,
            "Opens this work in Blender: Ctrl+S there sends your changes back here. With no source picked, the target's own model becomes the source (a summon, a costume: change the game's model itself). Opened in Blender before: that same scene opens again, with your earlier work in it (Full Export ▾ → Open in Blender makes a fresh one).");
        Ui.Tip(blenderStatus, "This work is linked to a Blender scene: each Ctrl+S in Blender sends what you changed back here.");
        Ui.TipTitled(buildOnSave, "Build When Blender Saves", "Each Ctrl+S in Blender that sends a change also runs Build into Mod, so the mod has your edit at once (then Save Changes and Apply Changes, as after any build). Kept with the mod.");
        buildOnSave.CheckedChanged += (_, _) => { if (!restoring2) SaveState(); };
    }

    static bool IsSummon(string file) => Summons.ContainsKey(file);

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
    void SummonBoth(int row, string file)
    {
        characters.SelectedIndex = row;   // the source (CharacterChosen)
        if (!host.Packages.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
        {
            string stock;
            try { stock = BasePackage.Resolve(file); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Log($"{file}: {ex.Message}"); return; }
            host.AddPackage(file, stock);
            added.Add(file);
            Log($"Added {file} to the mod as the target too (the game's stock copy, until it's built).");
        }
        gameList = false; packageFilter.Text = "";
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
        FillCharacters();
        Reselect(characters, key);
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
