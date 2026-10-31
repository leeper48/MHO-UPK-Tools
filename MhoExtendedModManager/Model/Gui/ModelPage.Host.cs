using System.Diagnostics;
using System.Text.Json;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>What the Model tab needs from the editor around it.</summary>
interface IModelHost
{
    /// <summary>This editing session's work folder (bone maps, FBX edits, the tab's choices, builds): seeded from the mod's
    /// Model folder when the editor opens, copied back into the mod's Model folder on Save.</summary>
    string WorkFolder { get; }
    /// <summary>The draft's packages: file name and where its bytes are now.</summary>
    IReadOnlyList<(string File, string Path)> Packages { get; }
    /// <summary>The game's packages as the mod list has them (From Current Modded Game): each package an enabled mod replaces,
    /// with the copy of the highest one in the list (as Apply puts it in the game) and that mod's name.</summary>
    IReadOnlyDictionary<string, (string Path, string Mod)> ActivePackages();
    /// <summary>The icon textures the mod list replaces (texture name → the image of the highest enabled mod that replaces it),
    /// for Current Modded Game's portraits.</summary>
    IReadOnlyDictionary<string, string> ActiveIcons();
    /// <summary>The mod's saved copy of a package (before this editing session), or null.</summary>
    string? SavedPath(string file);
    /// <summary>A built package into the draft (replacing that file's bytes).</summary>
    void SetPackage(string file, string path);
    /// <summary>A package from the game added to the draft.</summary>
    void AddPackage(string file, string path);
    /// <summary>A package taken out of the draft.</summary>
    void RemovePackage(string file);
    /// <summary>The draft's parts switched off (Powers tab → Parts, and the target's props here): written into the packages on
    /// Save (PowerParts.Build).</summary>
    List<PartOffEntry> PartsOff { get; }
    /// <summary>The props added to a costume package from other characters (manifest AddedProps; Kurt, 2026-10-27: set up on
    /// the Model tab, the Animations tab keeps how they move).</summary>
    IReadOnlyList<AddedPropEntry> AddedProps(string file);
    /// <summary>Adds another character's prop to <paramref name="file"/> (<paramref name="pick"/>: that one, else the Add a Prop
    /// window asks) and puts it into the package; null when nothing was added.</summary>
    Task<AddedPropEntry?> AddPropTo(string file, AddPropForm.Pick? pick = null);
    /// <summary>Takes an added prop off its package.</summary>
    Task<bool> RemoveAddedProp(AddedPropEntry e);
    /// <summary>An added prop's fit (changed in <see cref="AddedPropEntry.Fit"/>) put into its package again.</summary>
    Task<bool> RefitAddedProp(AddedPropEntry e);
    /// <summary>An added prop given the model built at <paramref name="relPath"/> (under <see cref="WorkFolder"/>), or its
    /// character's own again (null), put into its package.</summary>
    Task<bool> SetAddedModel(AddedPropEntry e, string? relPath);
    /// <summary>The props as they are (added props, the costume's props on other bones), for Undo.</summary>
    string PropState();
    /// <summary>The props back to <paramref name="state"/> (from <see cref="PropState"/>), put into their packages (Undo / Redo).</summary>
    Task<bool> SetPropState(string state);
    /// <summary>An added prop left off the costume (its settings kept) or put back on.</summary>
    Task<bool> SetAddedPropOn(AddedPropEntry e, bool on);
    /// <summary>An added prop's Pivot on the Hand set (the Animations and Powers tabs' box too).</summary>
    Task<bool> SetAddedPin(AddedPropEntry e, bool on);
    /// <summary>Why an added prop can't be adjusted in animation <paramref name="slot"/> alone ("" when it can: one taken from
    /// another character, or a borrowed power's).</summary>
    string AnimFitWhy(AddedPropEntry e, string? slot);
    /// <summary>An added prop's adjustment in one animation set (null: none), built into its package; every tab shows it.</summary>
    void SetAnimFit(AddedPropEntry e, string slot, PropAnimFit? a);
    /// <summary>The package's own props held on other bones (manifest PropBones).</summary>
    IReadOnlyList<PropBoneEntry> PropBones(string file);
    /// <summary>One of the package's own props held on <paramref name="bone"/>, or on its own again (null).</summary>
    Task<bool> SetPropBone(string file, string cls, string? bone);
}

sealed partial class ModelPage
{
    bool loaded;
    /// <summary>The package list shows the game's base heroes (to add one) instead of the mod's packages.</summary>
    bool gameList;
    readonly DropDown buildFrom = new() { Width = 210 };
    /// <summary>The character in the chosen package to fit models to, when it has several skeletal meshes (Kurt, 2026-10-07).</summary>
    readonly DropDown meshPick = new() { Width = 210 };
    /// <summary>The model selector under the preview (Kurt, 2026-10-27: ◀ name ▶ as the Mods tab's 3D view): ◀, the list
    /// (meshPick), ▶; shown for a package with several models.</summary>
    Control meshRow = null!;
    /// <summary>The model Automatic picks in the chosen package (the selector counts from it).</summary>
    string? autoMesh;
    readonly HashSet<string> meshProps = new(StringComparer.OrdinalIgnoreCase);
    List<string> meshNames = [];
    /// <summary>The chosen package's added props (the Animations tab's, now set up here: Kurt, 2026-10-27), after its own
    /// models in the selector.</summary>
    List<AddedPropEntry> meshAdded = [];
    /// <summary>The added prop picked in the selector (its fit on the Prop Fit sliders; the character stays the build's
    /// target), else null.</summary>
    AddedPropEntry? addedPick;
    bool fillingMesh;
    /// <summary>Package file → has a skeletal mesh (by size and date), for the mod's own packages in the list.</summary>
    readonly Dictionary<string, (long Len, DateTime At, bool Has)> hasCharacter = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages this tab built in this session or before (file → the built file).</summary>
    readonly Dictionary<string, string> built = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages this tab added to the mod from the game (From the Game), in this session or before.</summary>
    readonly HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fixed widths were the importer window's, which Windows scaled as a whole; inside the editor they're
    /// scaled here, once (at 200 % the drop-downs showed "MFF Charact…").</summary>
    void ScaleToDpi()
    {
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        // the declared widths (layout may have changed Width already), held as a minimum: the auto-sized source head shrank it to its arrow
        foreach (var (d, w) in new[] { (sourceKind, 220), (targetKind, 220), (buildFrom, 210) }) { d.MinimumSize = new Size((int)(w * s), 0); d.Width = (int)(w * s); }
        foreach (var g in new[] { parts, mapGrid })
            foreach (DataGridViewColumn c in g.Columns) if (c.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill) c.Width = (int)(c.Width * s);
    }

    // --- the package list: the mod's packages first ------------------------------------------------------------------------------
    static bool CharacterPackage(string file) =>
        (file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) && !file.StartsWith("UC__MarvelPlayerAudio", StringComparison.OrdinalIgnoreCase))
        || file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase) || OtherTargets.Contains(file);

    /// <summary>The mod's character packages (Kurt: the base comes from the mod's packages; several = a choice), then "From the
    /// Game" to add one; or, after that, the game's base heroes with "Back to the Mod's Packages" on top.</summary>
    void FillPackages()
    {
        targetKindStrip.Visible = gameList;
        summonsTarget.Visible = gameList && Summons.Count > 0;   // (summons parked: WhatsNew.Summons)
        if (targetKind.SelectedIndex != (gameList ? 1 : 0)) { settingTargetKind = true; targetKind.SelectedIndex = gameList ? 1 : 0; settingTargetKind = false; }
        if (gameList) { FillGamePackages(); return; }
        string? keep = ChosenPackage?.Key;
        packages.BeginUpdate(); packages.Items.Clear();
        foreach (var (file, _) in host.Packages.Where(p => CharacterPackage(p.File) || added.Contains(p.File) || built.ContainsKey(p.File) || HasCharacter(p.File, p.Path))
                     .OrderBy(p => DescribePackage(p.File).Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            var (title, detail) = DescribePackage(file);
            packages.Items.Add(new CharacterList.Item(file, Dot(file, title), detail + (built.ContainsKey(file) ? " · built by the Model tab" : "")));
        }
        packages.EndUpdate();
        if (keep != null) Reselect(packages, keep);
        else if (packages.Items.Count >= 1) packages.SelectedIndex = 0;   // the mod's character (Kurt, 2026-10-19: several = the first by name)
        UpdateStatus();
    }

    void InitTargetKind()
    {
        targetKind.Items.AddRange(["The Mod's Packages", "From the Game", "Browse for a Package"]);
        targetKind.SelectedIndex = 0;
        Ui.Tip(targetKind, "What to build onto: The Mod's Packages (the characters this mod holds); From the Game (add another hero, team-up, NPC, enemy or summon package from the game to the mod); Browse for a Package (any .upk with a character in it, named like a game package: a pet, a vehicle, another mod's package).");
        targetKind.SelectedIndexChanged += (_, _) =>
        {
            if (settingTargetKind) return;
            int k = targetKind.SelectedIndex;
            if (k == 2)
            {
                // a file picker; the list goes back to the mod's packages (the picked one is added there)
                gameList = false; packageFilter.Text = ""; FillPackages();
                BeginInvoke(BrowsePackage);
                return;
            }
            if ((k == 1) == gameList) return;
            gameList = k == 1; RememberTargetList(); packageFilter.Text = ""; FillPackages();
        };
    }

    /// <summary>The Target list the user switched to (From the Game or the mod's packages), for the next time.</summary>
    void RememberTargetList()
    {
        if (Settings.Current.TargetFromGame == gameList) return;
        Settings.Current.TargetFromGame = gameList; Settings.Current.Save();
    }

    /// <summary>The list's special rows, and a game package picked: added to the mod (asked first).</summary>
    void PackagePicked()
    {
        if (packages.SelectedItem is not CharacterList.Item { Header: false, Label: false } it) return;
        if (it.Key == "game:") { gameList = true; RememberTargetList(); packageFilter.Text = ""; FillPackages(); return; }
        if (it.Key == "back:") { gameList = false; RememberTargetList(); packageFilter.Text = ""; FillPackages(); return; }
        if (it.Key == "browse:") { BeginInvoke(BrowsePackage); return; }
        if (!gameList) return;
        if (host.Packages.Any(p => p.File.Equals(it.Key, StringComparison.OrdinalIgnoreCase))) { gameList = false; FillPackages(); Reselect(packages, it.Key); return; }
        if (Dialog.Show(this, $"Add {it.Key} ({it.Title}, {it.Detail.Split(" · ")[0]}) to this mod? The model is built onto it; until then it's the game's own package.",
                "Add a Package", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        string stock;
        try { stock = BasePackage.Resolve(it.Key); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Dialog.Show(this, ex.Message, "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        host.AddPackage(it.Key, stock);
        added.Add(it.Key);
        Log($"Added {it.Key} to the mod (the game's stock copy, until it's built).");
        gameList = false; packageFilter.Text = "";
        FillPackages();
        Reselect(packages, it.Key);
        SaveState();
    }

    /// <summary>
    /// Takes a target out of the list (Kurt, 2026-10-06): right-click → Remove, or Delete. A package this tab added from the game
    /// leaves the mod with its build; one of the mod's own packages can go back to the mod's copy (when built here) or leave the
    /// mod. Only the draft changes: the editor's Cancel brings it all back.
    /// </summary>
    void RemoveTarget(CharacterList.Item it)
    {
        if (gameList || it.Header || it.Key.Contains(':')) return;
        string file = it.Key;
        bool isBuilt = built.ContainsKey(file);
        string? saved = host.SavedPath(file);
        bool ours = added.Contains(file) || saved == null;
        int pick;
        if (ours)
            pick = Dialog.Choose(this, $"Remove {file} ({it.Title}) from the list? The Model tab added it to the mod{(isBuilt ? " and built the model onto it" : "")}, so it leaves the mod too.",
                "Remove a Target", "Remove", "Cancel") == 0 ? 1 : -1;
        else if (isBuilt)
            pick = Dialog.Choose(this, $"{file} ({it.Title}) is one of the mod's own packages, with the model built onto it. Put the mod's own copy back (the model comes off), or take the package out of the mod?",
                "Remove a Target", "Restore the Mod's Copy", "Remove From the Mod", "Cancel") switch { 0 => 0, 1 => 1, _ => -1 };
        else
            pick = Dialog.Choose(this, $"{file} ({it.Title}) is one of the mod's own packages. Take it out of the mod?",
                "Remove a Target", "Remove From the Mod", "Cancel") == 0 ? 1 : -1;
        if (pick < 0) return;
        if (pick == 0 && saved != null)
        {
            host.SetPackage(file, saved);
            takenOff[file] = Fingerprint();
            Log($"{file}: the mod's own copy is back (the model built onto it is off).");
        }
        else
        {
            host.RemovePackage(file);
            takenOff.Remove(file);
            Log($"Removed {file} from the mod{(ours ? " (the Model tab had added it)" : "")}.");
        }
        built.Remove(file); builtPrint.Remove(file); added.Remove(file);
        FillPackages();
        SaveState();
        SchedulePreview();
    }

    // --- what a build starts from --------------------------------------------------------------------------------------------------
    void InitBuildFrom()
    {
        buildFrom.Items.AddRange(["Build From: Before the Model", "Build From: The Game's Stock"]);
        buildFrom.SelectedIndex = 0;
        buildFrom.SelectedIndexChanged += (_, _) => { SaveState(); SchedulePreview(); };
        Ui.Tip(buildFrom, "Before the Model (default): the mod's package as it was before the Model tab built onto it, so the mod's other changes in it (recolors, swapped animations, voice) stay and a rebuild replaces the earlier build. The Game's Stock: the game's own package, without the mod's other changes.");
    }

    bool FromStock => buildFrom.SelectedIndex == 1;

    /// <summary>A mod package of another kind (UC__ only: zones and icon packages aren't opened) that holds a skeletal mesh.</summary>
    bool HasCharacter(string file, string path)
    {
        if (!file.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        var fi = new FileInfo(path);
        if (hasCharacter.TryGetValue(path, out var c) && c.Len == fi.Length && c.At == fi.LastWriteTimeUtc) return c.Has;
        bool has;
        try { has = MhoSkeleton.List(path).Count > 0; } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { has = false; }
        hasCharacter[path] = (fi.Length, fi.LastWriteTimeUtc, has);
        return has;
    }

    /// <summary>
    /// Browse for a Package (Kurt, 2026-10-07: any package as a target): a .upk with a skeletal mesh, named like a game package
    /// (the mod replaces that file). From the game folder its stock copy is used (a live file may be modded); from anywhere
    /// else that file (another mod's package, say).
    /// </summary>
    void BrowsePackage()
    {
        string? cooked = MhoExtendedModManager.Model.Settings.Current.CookedFolder;
        using var d = new OpenFileDialog { Title = "Browse for a Package", Filter = "Unreal packages (*.upk)|*.upk", InitialDirectory = cooked != null && Directory.Exists(cooked) ? cooked : "" };
        if (d.ShowDialog(this) != DialogResult.OK) { FillPackages(); return; }
        string file = Path.GetFileName(d.FileName);
        int meshes;
        try { meshes = MhoSkeleton.List(d.FileName).Count; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { Dialog.Show(this, $"{file} couldn't be read: {ex.Message}", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); FillPackages(); return; }
        if (meshes == 0) { Dialog.Show(this, $"{file} has no skeletal mesh: There's no character in it to fit a model to.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); FillPackages(); return; }
        if (cooked != null && !File.Exists(Path.Combine(cooked, file)))
        { Dialog.Show(this, $"{file} isn't the name of a game package, so the game would never load it: A mod replaces a game package of the same name.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); FillPackages(); return; }
        bool fromGame = cooked != null && Path.GetFullPath(Path.GetDirectoryName(d.FileName)!).TrimEnd('\\').Equals(Path.GetFullPath(cooked).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string src = d.FileName;
        if (fromGame)
            try { src = BasePackage.Resolve(file); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Dialog.Show(this, ex.Message, "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); FillPackages(); return; }
        if (host.Packages.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
        {
            if (fromGame) { FillPackages(); Reselect(packages, file); return; }   // the mod has it already
            if (Dialog.Choose(this, $"The mod has {file} already. Put this file in its place?", "Browse for a Package", "Replace It", "Cancel") != 0) { FillPackages(); return; }
            built.Remove(file); builtPrint.Remove(file);
        }
        host.AddPackage(file, src);
        added.Add(file);
        Log($"Added {file} to the mod ({(fromGame ? "the game's stock copy" : d.FileName)}, until it's built){(meshes > 1 ? $"; it has {meshes} characters: pick one under Character" : "")}.");
        FillPackages();
        Reselect(packages, file);
        SaveState();
    }

    void InitMeshPick()
    {
        Ui.Tip(meshPick, "The target: the model in this package that the build replaces, when the package holds several: the character (a hero's base package can hold other characters too) or one of its props, marked Prop (a shield, hammer, gun or blade: then an MFF part, such as a weapon, takes its place). Click for the list; Automatic picks the costume's own character, else the biggest. Changing it brings back that model's own source and Prop Fit, and Paint works on it.");
        meshPick.ClosedText = MeshCaption;
        meshPick.SelectedIndexChanged += (_, _) =>
        {
            if (fillingMesh || ChosenPackage is not CharacterList.Item pkg) return;
            int i = meshPick.SelectedIndex;
            if (i > meshNames.Count && i - meshNames.Count - 1 < meshAdded.Count)
            {
                // an added prop: the target (a new model, paint), its fit on the sliders
                _ = PickAddedTarget(pkg.Key, meshAdded[i - meshNames.Count - 1]);
                return;
            }
            RememberTargetSource();   // (the target left: its source kept for when it's picked again)
            addedPick = null;
            MhoSkeleton.Choose(pkg.Key, i <= 0 ? null : meshNames[i - 1]);
            PropTicks(changed: true);
            SourceForTarget();   // (first: the save below would note the old target's source as the new one's)
            SaveState(); SchedulePreview(); UpdateStatus(); UpdatePropFitUi();
        };
        packages.SelectedIndexChanged += (_, _) => FillMeshPick();
    }

    /// <summary>Package file → the copy an added prop target builds from: the package as the build would start it, with that
    /// prop added unfitted (its own model, else its character's), and the prop's model in it.</summary>
    readonly Dictionary<string, (string Class, string Path, string Mesh)> addedBases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The added prop picked as the target, while its copy (<see cref="addedBases"/>) is the start: else null.</summary>
    AddedPropEntry? AddedTarget => addedPick is { } a && ChosenPackage is CharacterList.Item p && a.Package.Equals(p.Key, StringComparison.OrdinalIgnoreCase)
        && addedBases.TryGetValue(p.Key, out var b) && b.Class.Equals(a.Class, StringComparison.OrdinalIgnoreCase)
        && b.Mesh.Equals(MhoSkeleton.ChosenFor(p.Key), StringComparison.OrdinalIgnoreCase) ? a : null;

    string? AddedStart(string file) => addedPick is { } a && a.Package.Equals(file, StringComparison.OrdinalIgnoreCase)
        && addedBases.TryGetValue(file, out var b) && b.Class.Equals(a.Class, StringComparison.OrdinalIgnoreCase) && File.Exists(b.Path) ? b.Path : null;

    /// <summary>The added prop's own model (built here before), else null.</summary>
    string? AddedModel(AddedPropEntry e) => e.ModelFrom is string r && Path.Combine(host.WorkFolder, r) is var f && File.Exists(f) ? f : null;

    /// <summary>
    /// An added prop as the build's target (Kurt, 2026-10-27: a new model or paint for Angela's axe on Thor): a copy of the
    /// package with only that prop added, unfitted, with its model (its own if it was built here, else its character's), made
    /// once per state (worker thread). The build lays a source over that model, as over a game prop; its fit goes on after.
    /// </summary>
    (string Path, string Mesh) AddedBase(string key, AddedPropEntry e)
    {
        string core = StartPackageCore(key), cooked = Settings.Current.CookedFolder ?? throw new InvalidDataException("the game folder isn't set");
        string donor = StockFiles.For(cooked, e.From);
        string? own = AddedModel(e);
        uint h = 2166136261;
        foreach (char c in $"{Stamp(core)}|{Stamp(donor)}|{(own != null ? Stamp(own) : "-")}|{e.Class}") h = (h ^ c) * 16777619;
        // (under the package's own name: the model picked in it is kept by file name, MhoSkeleton.Choose)
        string dir = Path.Combine(host.WorkFolder, "builds", "added_base", $"{e.Class}_{h:x8}");
        string path = Path.Combine(dir, key);
        if (!File.Exists(path))
        {
            var clean = new AddedPropEntry { Package = key, From = e.From, Class = e.Class, Name = e.Name, Slot = e.Slot, Fit = new MhoExtendedModManager.PropFit { Class = e.Class, Scale = 1 } };
            byte[] b = PowerBorrow.AddProp(File.ReadAllBytes(core), clean, donor, [], modelFrom: own);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, b);
        }
        string mesh = MhoExtendedModManager.ModMeshes.Attachments(path).FirstOrDefault(x => PowerBorrow.SameClass(x.Class, e.Class))?.Mesh.Split('.').Last()
            ?? throw new InvalidDataException($"{e.Name} names no model of its own in {key}");
        return (path, mesh);
        static string Stamp(string f) { var i = new FileInfo(f); return $"{i.Length}:{i.LastWriteTimeUtc.Ticks}"; }
    }

    /// <summary>An added prop picked in the selector: the build's target (its copy made first), its fit on the sliders.</summary>
    async Task PickAddedTarget(string key, AddedPropEntry e)
    {
        addedPick = e;
        UpdatePropFitUi(); meshPick.Invalidate();
        status.Text = "Reading the added prop…";
        (string Path, string Mesh) b;
        try { b = await Task.Run(() => AddedBase(key, e)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or UnauthorizedAccessException)
        {
            Log($"{e.Name}: it can't be the target ({ex.Message}). Its fit still works here.");
            UpdateStatus();
            return;
        }
        if (IsDisposed || !ReferenceEquals(addedPick, e) || ChosenPackage?.Key.Equals(key, StringComparison.OrdinalIgnoreCase) != true) return;
        RememberTargetSource();   // (the target left: its source kept for when it's picked again)
        addedBases[key] = (e.Class, b.Path, b.Mesh);
        MhoSkeleton.Choose(key, b.Mesh);
        PropTicks(changed: true);
        SourceForTarget();
        SaveState(); SchedulePreview(); UpdateStatus(); UpdatePropFitUi(); meshPick.Invalidate();
    }

    /// <summary>◀ [model (n of N) ▾] ▶ under the preview.</summary>
    Control MeshRow()
    {
        const string tip = " model in this package as the target (what the build replaces and Paint works on; its own source and Prop Fit come back).";
        var prev = Ui.FlatButton("◀", () => StepMesh(-1), "The previous" + tip);
        var next = Ui.FlatButton("▶", () => StepMesh(1), "The next" + tip);
        var t = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0), Visible = false };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        prev.Margin = new Padding(0, 0, 6, 0); next.Margin = new Padding(6, 0, 0, 0);
        meshPick.Dock = DockStyle.Fill; meshPick.Margin = new Padding(0);
        t.Controls.Add(prev, 0, 0); t.Controls.Add(meshPick, 1, 0); t.Controls.Add(next, 2, 0);
        return meshRow = t;
    }

    /// <summary>The selector's text: the model, Prop for a prop, and where it is in the package's list.</summary>
    string? MeshCaption()
    {
        int at = MeshAt(), total = meshNames.Count + meshAdded.Count;
        if (at < 0) return null;
        if (at >= meshNames.Count) return $"{meshAdded[at - meshNames.Count].Name} · Added Prop ({at + 1} of {total})";
        string n = meshNames[at];
        return $"{n}{(meshProps.Contains(n) ? " · Prop" : "")}{(meshPick.SelectedIndex <= 0 ? " · Automatic" : "")} ({at + 1} of {total})";
    }

    /// <summary>The shown model's place in the selector's list (the package's models, then its added props; Automatic: the
    /// model it picks), or -1.</summary>
    int MeshAt() => meshPick.SelectedIndex > 0 ? meshPick.SelectedIndex - 1
        : autoMesh == null ? -1 : meshNames.FindIndex(n => n.Equals(autoMesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>◀ / ▶: the previous or next model or added prop (round the list).</summary>
    void StepMesh(int by)
    {
        int total = meshNames.Count + meshAdded.Count;
        if (total < 2) return;
        int at = MeshAt();
        meshPick.SelectedIndex = (Math.Max(0, at) + by + total) % total + 1;
    }

    /// <summary>The selector's entry for <paramref name="e"/> picked.</summary>
    void PickAdded(AddedPropEntry e)
    {
        int k = meshAdded.IndexOf(e);
        if (k >= 0) meshPick.SelectedIndex = meshNames.Count + k + 1;
    }

    /// <summary>The summons whose "not shaped like a person" note was logged (once each).</summary>
    readonly HashSet<string> creatureHinted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Character drop-down for the chosen package: shown when it holds more than one skeletal mesh.</summary>
    async void FillMeshPick()
    {
        if (ChosenPackage is not CharacterList.Item pkg) { meshRow.Visible = false; return; }
        string key = pkg.Key, path = StartPackageCore(key);   // (the package's own models; added props come after)
        var adds = host.AddedProps(key).ToList();
        string? cookedNow = Settings.Current.CookedFolder;
        AddedPropEntry? addedWas = null;   // (an added prop the saved state has as the target: picked again)
        List<string> names;
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool creature = false;
        string? auto = null;
        try
        {
            names = await Task.Run(() =>
            {
                var unused = BuildLeftovers.UnusedModels(path);   // (an added prop's old copies: Monarch, 2026-10-29)
                var list = MhoSkeleton.List(path).Select(m => m.Name).Where(n => !unused.Contains(n)).ToList();
                // the one Automatic picks (the selector says which)
                if (list.Count > 1)
                    try { auto = MhoSkeleton.Load(path, body: true).Name; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                // which of them are props (a weapon or shield: PropFit), marked in the list
                if (list.Count > 1)
                    foreach (var n in list)
                        try { if (PropFit.IsProp(MhoSkeleton.Load(path, n))) props.Add(n); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                // a summon or power model without the humanoid skeleton (arms with elbows and wrists, legs with knees: a squirrel's and
                // Magik's small demon's arms are g_l_arm, g_l_hand; a car): models from MFF or another
                // hero can't be fitted to it
                if (Summons.ContainsKey(key))
                    try { var bn = MhoSkeleton.Load(path).Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); creature = !new[] { "g_pelvis", "g_head", "g_l_hip", "g_r_hip", "g_l_knee", "g_r_knee", "g_l_elbow", "g_r_elbow", "g_l_wrist", "g_r_wrist" }.All(bn.Contains); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                // the saved target an added prop's model (its character's, or one of its own: <model>_m12345)
                if (addedPick == null && MhoSkeleton.ChosenFor(key) is string was && !list.Contains(was, StringComparer.OrdinalIgnoreCase) && cookedNow != null)
                    foreach (var a in adds)
                        try
                        {
                            if (MhoExtendedModManager.ModMeshes.Attachments(StockFiles.For(cookedNow, a.From)).FirstOrDefault(x => x.Class.Equals(a.Class, StringComparison.OrdinalIgnoreCase))?.Mesh.Split('.').Last() is string dm
                                && System.Text.RegularExpressions.Regex.IsMatch(was, "^" + System.Text.RegularExpressions.Regex.Escape(dm) + @"(_on_[a-z0-9_]+?)?(_m\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { addedWas = a; break; }
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                return list;
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { names = []; }
        if (IsDisposed || ChosenPackage?.Key != key) return;
        if (creature && !HasSource && chosenKey == null && pendingState == null)
        {
            Log($"{key} isn't shaped like a person, so an MFF model or another hero can't be fitted to it: its own model is the source. Edit in Blender opens it there (Ctrl+S sends your changes back); or Single Model → an FBX rigged to its own bones.");
            creatureHinted.Add(key);
            _ = UseOwnModel(key);
        }
        else if (creature && creatureHinted.Add(key))
            Log($"{key} isn't shaped like a person, so an MFF model or another hero can't be fitted to it. To change it: Edit in Blender with no source picked (its own model), or Single Model → an FBX rigged to its own bones.");
        fillingMesh = true;
        try
        {
            meshNames = names; autoMesh = auto;
            meshProps.Clear(); meshProps.UnionWith(props);
            meshPick.Items.Clear();
            meshPick.Items.Add("Automatic" + (auto != null ? $" ({auto})" : ""));
            foreach (var n in names) meshPick.Items.Add(n + (props.Contains(n) ? " · Prop" : ""));
            meshAdded = [.. host.AddedProps(key)];
            foreach (var a in meshAdded) meshPick.Items.Add(a.Name + " · Added Prop");
            string? pick = MhoSkeleton.ChosenFor(key);
            int at = pick == null ? -1 : names.FindIndex(n => n.Equals(pick, StringComparison.OrdinalIgnoreCase));
            // (an added prop stays picked while it's there)
            int added = addedPick == null ? -1 : meshAdded.FindIndex(a => a.Class.Equals(addedPick.Class, StringComparison.OrdinalIgnoreCase));
            addedPick = added >= 0 ? meshAdded[added] : null;
            meshPick.SelectedIndex = added >= 0 ? names.Count + added + 1 : at + 1;
            meshRow.Visible = names.Count + meshAdded.Count > 1 || at >= 0;
            UpdatePropFitUi();
            if (addedWas != null && addedPick == null) _ = PickAddedTarget(key, addedWas);
            // a saved target that isn't in the package any more (a removed added prop): automatic again, before anything is
            // kept under its name
            else if (addedPick == null && pick != null && at < 0) { MhoSkeleton.Choose(key, null); SaveState(); }
            meshPick.Invalidate();
            PropTicks(changed: false);
        }
        finally { fillingMesh = false; }
    }

    /// <summary>The file a build of <paramref name="file"/> starts from (also what the preview shows the model on).</summary>
    /// <summary>The package a build or preview of <paramref name="file"/> starts from: an added prop picked as the target, a
    /// copy with that prop in it (<see cref="AddedStart"/>); else the mod's package before the model, or the game's.</summary>
    string StartPackage(string file) => AddedStart(file) ?? StartPackageCore(file);

    string StartPackageCore(string file)
    {
        // a costume that shows its hero's model (no model of its own): a copy with that model in it, under the costume's name
        string start = StartPackageAsIs(file);
        try { return InheritedMesh.Start(start, line => Later(() => Log(line))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or UnauthorizedAccessException)
        {
            Later(() => Log($"{file}: its hero's model couldn't be copied in: {ex.Message}"));
            return start;
        }
    }

    string StartPackageAsIs(string file)
    {
        if (FromStock) return BasePackage.Resolve(file);
        string kept = Path.Combine(host.WorkFolder, "base", file);
        if (File.Exists(kept)) return kept;
        // the mod's own copy, unless the Model tab made it (then the start was stock: nothing was kept)
        if (!built.ContainsKey(file) && host.SavedPath(file) is string saved && File.Exists(saved))
        {
            string stock = BasePackage.Resolve(file, true);
            if (new FileInfo(saved).Length == new FileInfo(stock).Length && File.ReadAllBytes(saved).AsSpan().SequenceEqual(File.ReadAllBytes(stock))) return stock;
            Protected.CheckWrite(kept);
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            File.Copy(saved, kept, true);   // kept with the mod (Model\base): later builds start from it again
            return kept;
        }
        return BasePackage.Resolve(file);
    }

    /// <summary>The hero's base package (its animations) from the mod when it has one: the draft's copy.</summary>
    string? ModCopy(string file) => host.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Path;

    string StartLabel(string file) => FromStock ? "the game's stock copy" : File.Exists(Path.Combine(host.WorkFolder, "base", file)) ? "the mod's package before the model" : "the game's stock copy (the mod had none of its own)";

    // --- the tab's settings: in the main window's Settings menu (Kurt, 2026-10-03: one Settings button) --------------------------
    /// <summary>Settings ▾ → Model: the MFF folder, the Blender the exports open in, its add-on, the exports folder.
    /// <paramref name="changed"/> runs after a change (the open Model tab reloads).</summary>
    internal static ToolStripMenuItem SettingsMenu(Control owner, Action changed)
    {
        var model = new ToolStripMenuItem("Model") { ToolTipText = "The Editor's Model tab: your MFF folder, the Blender its exports open in, and Blender's MHO Actions add-on." };
        model.DropDownItems.Add(new ToolStripMenuItem("(filled when it opens)"));
        model.DropDownOpening += (_, _) =>
        {
            model.DropDownItems.Clear();
            string? mff = (Settings.App ?? MhoExtendedModManager.Settings.Load()).MffFolder;
            model.DropDownItems.Add(new ToolStripMenuItem("Change MFF Folder", null, (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = "Your MFF rip folder (it holds Models\\Models or Models, and Texture2D); read only", UseDescriptionForTitle = true, InitialDirectory = mff ?? "" };
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                Settings.Change(s => s.MffFolder = d.SelectedPath);
                Source.ForgetLayouts();
                Settings.Reset();
                changed();
            }) { ToolTipText = "Now: " + (mff ?? "not set") + ". Only read, never changed." });
            string? models = (Settings.App ?? MhoExtendedModManager.Settings.Load()).ModelsFolder;
            model.DropDownItems.Add(new ToolStripMenuItem("Change Models Folder", null, (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = "The folder Browse for a Model opens in (your own model files); read only", UseDescriptionForTitle = true, InitialDirectory = models ?? Settings.Current.LastModelFolder ?? "" };
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                Settings.Change(s => s.ModelsFolder = d.SelectedPath);
                Settings.Reset();
                changed();
            }) { ToolTipText = "Now: " + (models ?? "not set: Browse for a Model opens where you last picked a model") + ". Only read, never changed." });
            string? exe = BlenderLaunch.Find();
            model.DropDownItems.Add(new ToolStripMenuItem($"Choose Blender ({(exe != null ? BlenderLaunch.Describe(exe).Split(" (")[0].Replace("with the MHO Actions add-on", "with the Add-On").Replace("without the MHO Actions add-on", "without the Add-On") : "None Found")})", null, (_, _) =>
            {
                using var d = new OpenFileDialog { Title = "blender.exe", Filter = "Blender (blender.exe)|blender.exe", InitialDirectory = Path.GetDirectoryName(exe ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) };
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                Settings.Current.BlenderPath = d.FileName; Settings.Current.Save();
                changed();
            }) { ToolTipText = "The Blender that Open in Blender starts (the newest with the MHO Actions add-on unless you pick one)." });
            // the image editor the Materials tab opens maps in (Kurt, 2026-10-06)
            string? editor = ImageEditor.Find();
            var editors = new ToolStripMenuItem($"Choose Image Editor ({(editor != null ? ImageEditor.Describe(editor) : "None Found")})") { ToolTipText = "The program the Materials tab's Edit in Image Editor opens a map in (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita, Paint.NET, or any other). Each save there comes back into the Model tab." };
            foreach (var ed in ImageEditor.Installed())
            {
                string e2 = ed;
                editors.DropDownItems.Add(new ToolStripMenuItem(ImageEditor.Describe(ed), null, (_, _) => { Settings.Current.ImageEditorPath = e2; Settings.Current.Save(); changed(); })
                { Checked = editor != null && editor.Equals(ed, StringComparison.OrdinalIgnoreCase), ToolTipText = ed });
            }
            editors.DropDownItems.Add(new ToolStripMenuItem("Another Program", null, (_, _) =>
            {
                using var d = new OpenFileDialog { Title = "Your image editor's program file", Filter = "Programs (*.exe)|*.exe", InitialDirectory = Path.GetDirectoryName(editor ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) };
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                Settings.Current.ImageEditorPath = d.FileName; Settings.Current.Save();
                changed();
            }) { ToolTipText = "Pick the .exe of any image editor that opens a PNG file given to it." });
            model.DropDownItems.Add(editors);
            if (exe != null && BlenderLaunch.CanOfferAddon(exe))
                model.DropDownItems.Add(new ToolStripMenuItem("Install the MHO Actions Add-On", null, async (_, _) =>
                {
                    string? failed = await Task.Run(() => BlenderLaunch.InstallAddon(exe));
                    if (failed == null) Dialog.Show(owner, "The MHO Actions add-on is installed and enabled in " + exe + ".", "Add-On Installed");
                    else Dialog.Show(owner, $"The MHO Actions add-on wasn't installed: {failed}\n\nYou can install it by hand: Blender → Edit → Preferences → Get Extensions → Install from Disk, the file {BlenderLaunch.BundledAddon()}.", "Add-On Not Installed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    changed();
                }) { ToolTipText = "Into " + exe + " (optional: Open in Blender works without it). Your Blender preferences are kept." });
            model.DropDownItems.Add(new ToolStripMenuItem("Open the Exports Folder", null, (_, _) =>
            {
                string dd = Path.Combine(Settings.Home, "fbx"); Directory.CreateDirectory(dd);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dd}\"") { UseShellExecute = false });
            }) { ToolTipText = "Where Export FBX and Open in Blender put their files (data\\model\\fbx)." });
            model.DropDownItems.Add(new ToolStripMenuItem("Clean Up Exports and Rigs", null, (_, _) =>
            {
                using var f = new MhoExtendedModManager.Gui.ModelCleanUpForm();
                f.ShowDialog(owner);
            }) { ToolTipText = "The exports (data\\model\\fbx) and the Blender rigs of models without an armature (data\\model\\rigs), with their sizes: delete what you don't need. Mods keep their own copies." });
        };
        return model;
    }

    /// <summary>After Settings ▾ → Model changed something: the MFF list and the top line again.</summary>
    internal void SettingsChanged() { Settings.Reset(); Reload(); UpdateStatus(); }

    // --- the tab's choices, kept with the mod (Model\state.json) ---------------------------------------------------------------------
    sealed class State
    {
        public string? Source { get; set; }
        public string? Package { get; set; }
        public List<string> Parts { get; set; } = new();
        public bool Subdivide { get; set; }
        public int Material { get; set; }
        public int Cape { get; set; }
        /// <summary>The added rigs other than the cape and hair ("Coat:1,Tail:2"; null = none).</summary>
        public string? Rigs { get; set; }
        public int Hair { get; set; }
        public bool FromStock { get; set; }
        /// <summary>Packages the tab built (names: the built bytes are the mod's own package once saved).</summary>
        public List<string> Built { get; set; } = new();
        /// <summary>The Size slider (1 = the game's size).</summary>
        public float Size { get; set; } = 1;
        /// <summary>Match Steps to Size: movement animations at 1 / size (StepRate).</summary>
        public bool MatchSteps { get; set; } = true;
        /// <summary>The Feet slider when moved off its automatic amount (null: automatic).</summary>
        public float? FeetCustom { get; set; }
        /// <summary>Keep the Source's Size (ImportOptions.KeepSourceSize).</summary>
        public bool KeepSourceSize { get; set; }
        /// <summary>Lean (ImportOptions.Lean), degrees: the one used before 0.60.136 (always the user's, 0 = none).</summary>
        public float Lean { get; set; }
        /// <summary>From 0.60.136 (LeanVersion 2): the user's Lean, null = automatic. Older states keep their Lean as the user's.</summary>
        public float? LeanCustom { get; set; }
        public int LeanVersion { get; set; }
        /// <summary>Game Joint Blend (ImportOptions.JointFit).</summary>
        public bool JointFit { get; set; }
        /// <summary>Build When Blender Saves.</summary>
        public bool BuildOnBlenderSave { get; set; }
        /// <summary>Per summon target: whether its copies in other characters' packages are built too (asked once).</summary>
        public Dictionary<string, bool>? CopyOthers { get; set; }
        /// <summary>Each prop's Prop Fit sliders ("package|model" → the fit; Kurt, 2026-10-26).</summary>
        public Dictionary<string, PropAdjust>? PropFits { get; set; }
        public Dictionary<string, List<string>>? PiecesOff { get; set; }
        /// <summary>Each target's last source ("package|model" → source key; Kurt, 2026-10-26): Model ▾ back to the body puts its
        /// source back, not the prop's.</summary>
        public Dictionary<string, string>? TargetSources { get; set; }
        /// <summary>Each target's All Materials choice ("package|model" → its index; Monarch, 2026-10-29: the body's Metal
        /// went onto the prop built from the same source, and back).</summary>
        public Dictionary<string, int>? TargetMaterials { get; set; }
        /// <summary>The settings each built package was built with (unbuilt changes are told apart from built ones).</summary>
        public Dictionary<string, string> BuiltPrints { get; set; } = new();
        /// <summary>Packages the tab added from the game (removing such a target takes it out of the mod).</summary>
        public List<string> Added { get; set; } = new();
        /// <summary>Package → the model picked in it (Model ▾), when not the automatic one.</summary>
        public Dictionary<string, string> Meshes { get; set; } = new();
    }

    string StateFile => Path.Combine(host.WorkFolder, "state.json");
    State? pendingState;
    bool restoring2;

    void SaveState()
    {
        if (restoring2 || !loaded) return;
        var st = new State
        {
            Source = chosenKey, Package = ChosenPackage?.Key, Parts = SelectedParts(), Subdivide = smooth.Checked,
            Material = Math.Max(0, material.SelectedIndex), Cape = CapeChoice, Hair = HairChoice, Rigs = OtherRigs().Count > 0 ? OtherRigsText() : null,
            FromStock = FromStock, Built = [.. built.Keys.Order(StringComparer.OrdinalIgnoreCase)], Size = sizeSlider.Value, MatchSteps = matchSteps.Checked, FeetCustom = feetCustom, KeepSourceSize = keepSourceSize.Checked, Lean = leanCustom ?? 0, LeanCustom = leanCustom, LeanVersion = 2, JointFit = jointFit.Checked, BuildOnBlenderSave = buildOnSave.Checked, CopyOthers = copyOthers.Count > 0 ? new(copyOthers) : null, PropFits = propFits.Count > 0 ? new(propFits) : null, PiecesOff = piecesOff.Any(kv => kv.Value.Count > 0) ? piecesOff.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase) : null, TargetSources = RememberTargetSource(), TargetMaterials = targetMaterials.Count > 0 ? new(targetMaterials) : null,
            BuiltPrints = new Dictionary<string, string>(builtPrint), Added = [.. added.Order(StringComparer.OrdinalIgnoreCase)],
            Meshes = host.Packages.Select(p => (p.File, M: MhoSkeleton.ChosenFor(p.File))).Where(x => x.M != null).ToDictionary(x => x.File, x => x.M!, StringComparer.OrdinalIgnoreCase),
        };
        try { Directory.CreateDirectory(host.WorkFolder); File.WriteAllText(StateFile, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException) { }
    }

    /// <summary>At the start: the mod's last choices (its Model folder) picked again: the package, Build From, the source (the
    /// rest follows when the source has loaded: <see cref="RestoreState"/>).</summary>
    void LoadState()
    {
        MhoSkeleton.ClearChoices();   // another mod's picks don't carry over
        if (!File.Exists(StateFile)) return;
        State? st;
        try { st = JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)); }
        catch (Exception ex) when (ex is IOException or JsonException) { return; }
        if (st == null) return;
        foreach (var f in st.Built) built.TryAdd(f, "");
        foreach (var f in st.Added ?? []) added.Add(f);
        foreach (var (f, mesh) in st.Meshes ?? []) MhoSkeleton.Choose(f, mesh);
        foreach (var (k, v) in st.BuiltPrints ?? []) builtPrint[k] = v;
        foreach (var (k, v) in st.CopyOthers ?? []) copyOthers[k] = v;
        foreach (var (k, v) in st.PropFits ?? []) propFits[k] = v;
        foreach (var (k, v) in st.PiecesOff ?? []) piecesOff[k] = new(v, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in st.TargetMaterials ?? []) targetMaterials[k] = v;
        foreach (var (k, v) in st.TargetSources ?? []) targetSources[k] = v;   // built in an earlier session: its bytes are the mod's package
        restoring2 = true;
        buildFrom.SelectedIndex = st.FromStock ? 1 : 0;
        if (st.Package != null) gameList = false;   // (the mod's saved target is one of its packages)
        FillPackages();
        if (st.Package != null) Reselect(packages, st.Package);
        restoring2 = false;
        if (st.Source == null) { if (st.Package != null) AssumeBuilt(); return; }
        pendingState = st;
        SelectSourceKey(st.Source);
    }

    /// <summary>A source picked again by its key (the saved state's, or a target's own when Model ▾ changes): its kind and
    /// list, then the row (a summon or prop: in the panel below), which loads it.</summary>
    void SelectSourceKey(string source)
    {
        autoSource++;
        try { SelectSourceKeyCore(source); } finally { autoSource--; }
    }

    void SelectSourceKeyCore(string source)
    {
        var st = new { Source = source };
        kindByState = true;
        // (already on From the Game: the list was filled before the saved source was known, with its hero's group shut, so it's
        // filled again to open that group: Kurt 2026-10-25, a summon source wasn't picked again)
        if (st.Source.StartsWith(GameSrc)) { SourceTabFor(SplitGameKey(st.Source).File); if (!GameMode) sourceKind.SelectedIndex = 2; else FillCharacters(); }
        else if (st.Source.StartsWith("fbx:") || st.Source.StartsWith(MffDir)) { if (!FbxMode) sourceKind.SelectedIndex = 1; }
        else
        {
            if (sourceKind.SelectedIndex != 0) sourceKind.SelectedIndex = 0;   // (an MFF character: From the Game is the default now)
            characterFilter.Text = st.Source;
        }
        kindByState = false;
        Reselect(characters, st.Source);
        // a summon: picked in the summons panel below the list
        if (st.Source.StartsWith(GameSrc) && IsSummon(st.Source[GameSrc.Length..])) { ShowSummonSource(st.Source[GameSrc.Length..]); GameChosen(st.Source); }
        else if (st.Source.StartsWith(GameSrc) && SplitGameKey(st.Source).Mesh is not null) { ShowPropSource(st.Source); GameChosen(st.Source); }   // (a prop, from the panel)
        else if (st.Source.StartsWith(GameSrc) && pendingState == null && !st.Source.Equals(chosenKey, StringComparison.OrdinalIgnoreCase)) GameChosen(st.Source);   // (its group was shut: not in the list)
    }

    /// <summary>Each target's last source (TargetSources).</summary>
    readonly Dictionary<string, string> targetSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The target as a key: its package and the model picked in it ("*": automatic).</summary>
    string? TargetSourceKey() => ChosenPackage is CharacterList.Item t ? $"{t.Key}|{MhoSkeleton.ChosenFor(t.Key) ?? "*"}" : null;

    /// <summary>The models in a package the tab has worked on (a source or a Prop Fit kept for them): a body target holds its
    /// props among them as built into the mod.</summary>
    List<string> WorkedModels(string file) => [.. targetSources.Keys.Concat(propFits.Keys)
        .Where(k => k.StartsWith(file + "|", StringComparison.OrdinalIgnoreCase)).Select(k => k[(file.Length + 1)..])
        .Where(m => m != "*").Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Each target's All Materials choice (State.TargetMaterials).</summary>
    readonly Dictionary<string, int> targetMaterials = new(StringComparer.OrdinalIgnoreCase);
    bool settingMaterial;

    /// <summary>The All Materials choice kept for the target it was made on.</summary>
    void RememberMaterial()
    {
        if (!settingMaterial && !restoring2 && loaded && TargetSourceKey() is string tk) { targetMaterials[tk] = Math.Max(0, material.SelectedIndex); SaveState(); }
    }

    /// <summary>The target's own All Materials choice back (a target without one keeps what shows).</summary>
    void MaterialForTarget()
    {
        if (TargetSourceKey() is not string tk || !targetMaterials.TryGetValue(tk, out int i) || i < 0 || i >= material.Items.Count || i == material.SelectedIndex) return;
        settingMaterial = true;
        try { material.SelectedIndex = i; }
        finally { settingMaterial = false; }
    }

    Dictionary<string, string>? RememberTargetSource()
    {
        if (chosenKey != null && HasSource && !PropSourceOnBody && TargetSourceKey() is string tk) targetSources[tk] = chosenKey;   // (a prop on a body: nothing fitted)
        return targetSources.Count > 0 ? new(targetSources) : null;
    }

    /// <summary>
    /// Model ▾ changed (Kurt, 2026-10-26: back on Thor's body after the hammer, the hammer stayed the source and there was
    /// nothing to paint): the target's own last source comes back; a body with none while the source is a prop from the panel
    /// takes its own model from the game.
    /// </summary>
    async void SourceForTarget()
    {
        if (ChosenPackage is not CharacterList.Item pkg || TargetSourceKey() is not string tk) return;
        MaterialForTarget();
        // (not a prop's model on a body, nor a body's own model on a prop)
        // another model of this very package as a source (its body, the hammer's own model) belongs to that model
        bool OtherOwn(string k) => k.Equals(GameSrc + pkg.Key, StringComparison.OrdinalIgnoreCase)
            || IsPropKey(k) && SplitGameKey(k) is var (kf, km) && kf.Equals(pkg.Key, StringComparison.OrdinalIgnoreCase) && km != null && !km.Equals(MhoSkeleton.ChosenFor(pkg.Key), StringComparison.OrdinalIgnoreCase);
        if (targetSources.TryGetValue(tk, out var src) && !(IsPropKey(src) && !TargetIsProp()) && !(TargetIsProp() && OtherOwn(src)))
        {
            if (!src.Equals(chosenKey, StringComparison.OrdinalIgnoreCase)) { Log($"Source: back to this target's own ({SourceTitle(src)})."); SelectSourceKey(src); }
            return;
        }
        if (PropSourceOnBody) await UseOwnModel(pkg.Key);
        // a body with no source at all (Kurt, 2026-10-28: stepping back to Doom after his added sword, the Parts list was empty;
        // "the body and cape would normally show"): its own model is the source, as Paint does (not in the Props view, whose
        // list on the left only shows props)
        else if (chosenKey == null && !HasSource && !TargetIsProp() && AddedTarget == null && !propMode) await UseOwnModel(pkg.Key);
        // a prop target with no source of its own yet, and the source is another model's in this package (Kurt, 2026-10-27: the
        // character's body fitted onto Angela's axe; the hammer's new model swapped in for the axe): no source, the prop's own
        // model shows
        else if (chosenKey is string ck && TargetIsProp() && (OtherOwn(ck)
                 || targetSources.Any(kv => kv.Key.StartsWith(pkg.Key + "|", StringComparison.OrdinalIgnoreCase) && !kv.Key.Equals(tk, StringComparison.OrdinalIgnoreCase) && kv.Value.Equals(ck, StringComparison.OrdinalIgnoreCase))))
        {
            chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear();
            if (!propMode) characters.ClearSelected();   // (the Props view's pick on the left isn't the source: its props stay listed)
            Log("Source: none (that source is another model's in this package): the prop's own model shows; pick a source to give it a new one.");
            UpdateStatus(); SchedulePreview();
        }
    }

    /// <summary>A prop from the panel is the source while the target is a body (nothing to fit: no preview, nothing to paint).</summary>
    bool PropSourceOnBody => !TargetIsProp() && chosenKey is string k && IsPropKey(k);
    static bool IsPropKey(string k) => k.StartsWith(GameSrc) && SplitGameKey(k).Mesh != null;

    string SourceTitle(string key) => key.StartsWith(GameSrc) ? (SplitGameKey(key) is var (f, m) && m != null ? $"{m} in {f}" : key[GameSrc.Length..]) : Path.GetFileName(key.Replace("fbx:", ""));

    /// <summary>The rest of the saved choices, once the source has loaded (parts, Smooth, material, Cape / Hair).</summary>
    /// <summary>A package built before the tab kept build settings (no BuiltPrints entry): its restored settings are taken as
    /// what it was built with, so later changes count as not built.</summary>
    void AssumeBuilt()
    {
        if (ChosenPackage is CharacterList.Item p && built.ContainsKey(p.Key) && !builtPrint.ContainsKey(p.Key) && Fingerprint() is string fp)
        { builtPrint[p.Key] = fp; SaveState(); }
    }

    void RestoreState()
    {
        if (pendingState is not { } st || st.Source != chosenKey) return;
        pendingState = null;
        restoring2 = true;
        var want = st.Parts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (want.Count > 0) foreach (DataGridViewRow r in parts.Rows) r.Cells["use"].Value = want.Contains((string)r.Cells["part"].Value);
        if (smooth.Enabled) smooth.Checked = st.Subdivide;
        if (st.Material < material.Items.Count) material.SelectedIndex = st.Material;
        MaterialForTarget();
        SetRigs(st.Cape <= BorrowedRig.CapeDonors.Length ? st.Cape : 0, st.Hair <= BorrowedRig.MegaHair ? st.Hair : 0, st.Rigs, preview: false);
        buildOnSave.Checked = st.BuildOnBlenderSave;
        sizeSlider.Value = st.Size > 0 ? st.Size : 1; preview.Size = sizeSlider.Value; matchSteps.Checked = st.MatchSteps; keepSourceSize.Checked = st.KeepSourceSize; jointFit.Checked = st.JointFit; leanCustom = st.LeanVersion >= 2 ? st.LeanCustom : st.Lean; restoringLean = true; leanSlider.Value = leanCustom ?? leanAuto; restoringLean = false; feetCustom = st.FeetCustom; preview.FeetShift = st.FeetCustom; if (st.FeetCustom is float fc) feetSlider.Value = fc;
        restoring2 = false;
        AssumeBuilt();
        SchedulePreview();
    }
}
