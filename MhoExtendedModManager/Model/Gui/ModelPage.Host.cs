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
    /// <summary>The mod's saved copy of a package (before this editing session), or null.</summary>
    string? SavedPath(string file);
    /// <summary>A built package into the draft (replacing that file's bytes).</summary>
    void SetPackage(string file, string path);
    /// <summary>A package from the game added to the draft.</summary>
    void AddPackage(string file, string path);
    /// <summary>A package taken out of the draft.</summary>
    void RemovePackage(string file);
}

sealed partial class ModelPage
{
    bool loaded;
    /// <summary>The package list shows the game's base heroes (to add one) instead of the mod's packages.</summary>
    bool gameList;
    readonly DropDown buildFrom = new() { Width = 210 };
    /// <summary>The character in the chosen package to fit models to, when it has several skeletal meshes (Kurt, 2026-10-07).</summary>
    readonly DropDown meshPick = new() { Width = 210, Visible = false };
    List<string> meshNames = [];
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
        foreach (var (d, w) in new[] { (sourceKind, 220), (buildFrom, 210), (capeBox, 150), (hairBox, 150) }) { d.MinimumSize = new Size((int)(w * s), 0); d.Width = (int)(w * s); }
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
        if (gameList)
        {
            FillGamePackages();
            packages.Items.Insert(0, new CharacterList.Item("back:", "← The Mod's Packages", "back to the packages this mod holds"));
            return;
        }
        string? keep = ChosenPackage?.Key;
        packages.BeginUpdate(); packages.Items.Clear();
        foreach (var (file, _) in host.Packages.Where(p => CharacterPackage(p.File) || added.Contains(p.File) || built.ContainsKey(p.File) || HasCharacter(p.File, p.Path))
                     .OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase))
        {
            var (title, detail) = DescribePackage(file);
            packages.Items.Add(new CharacterList.Item(file, title, detail + (built.ContainsKey(file) ? " · built by the Model tab" : "")));
        }
        packages.Items.Add(new CharacterList.Item("game:", "From the Game", "add another base hero's package to the mod"));
        packages.Items.Add(new CharacterList.Item("browse:", "Browse for a Package", "any .upk with a character in it (a pet, a vehicle, a prop …)"));
        packages.EndUpdate();
        if (keep != null) Reselect(packages, keep);
        else if (packages.Items.Count == 3) packages.SelectedIndex = 0;   // one package: it's the one
        UpdateStatus();
    }

    /// <summary>The list's special rows, and a game package picked: added to the mod (asked first).</summary>
    void PackagePicked()
    {
        if (packages.SelectedItem is not CharacterList.Item { Header: false } it) return;
        if (it.Key == "game:") { gameList = true; packageFilter.Text = ""; FillPackages(); return; }
        if (it.Key == "back:") { gameList = false; packageFilter.Text = ""; FillPackages(); return; }
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
        if (meshes == 0) { Dialog.Show(this, $"{file} has no skeletal mesh: there's no character in it to fit a model to.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); FillPackages(); return; }
        if (cooked != null && !File.Exists(Path.Combine(cooked, file)))
        { Dialog.Show(this, $"{file} isn't the name of a game package, so the game would never load it: a mod replaces a game package of the same name.", "Package Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning); FillPackages(); return; }
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
        Ui.Tip(meshPick, "The model in this package that the build replaces, when the package holds several: the character (a hero's base package can hold other characters too) or one of its props, marked Prop (a shield, hammer, gun or blade: then an MFF part, such as a weapon, takes its place). Automatic picks the costume's own character, else the biggest.");
        meshPick.SelectedIndexChanged += (_, _) =>
        {
            if (fillingMesh || ChosenPackage is not CharacterList.Item pkg) return;
            MhoSkeleton.Choose(pkg.Key, meshPick.SelectedIndex <= 0 ? null : meshNames[meshPick.SelectedIndex - 1]);
            PropTicks(changed: true);
            SaveState(); SchedulePreview(); UpdateStatus();
        };
        packages.SelectedIndexChanged += (_, _) => FillMeshPick();
    }

    /// <summary>The Character drop-down for the chosen package: shown when it holds more than one skeletal mesh.</summary>
    async void FillMeshPick()
    {
        if (ChosenPackage is not CharacterList.Item pkg) { meshPick.Visible = false; return; }
        string key = pkg.Key, path = StartPackage(key);
        List<string> names;
        var props = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            names = await Task.Run(() =>
            {
                var list = MhoSkeleton.List(path).Select(m => m.Name).ToList();
                // which of them are props (a weapon or shield: PropFit), marked in the list
                if (list.Count > 1)
                    foreach (var n in list)
                        try { if (PropFit.IsProp(MhoSkeleton.Load(path, n))) props.Add(n); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                return list;
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { names = []; }
        if (IsDisposed || ChosenPackage?.Key != key) return;
        fillingMesh = true;
        try
        {
            meshNames = names;
            meshPick.Items.Clear();
            meshPick.Items.Add("Model: Automatic");
            foreach (var n in names) meshPick.Items.Add("Model: " + n + (props.Contains(n) ? " · Prop" : ""));
            string? pick = MhoSkeleton.ChosenFor(key);
            int at = pick == null ? -1 : names.FindIndex(n => n.Equals(pick, StringComparison.OrdinalIgnoreCase));
            meshPick.SelectedIndex = at + 1;
            meshPick.Visible = names.Count > 1 || at >= 0;
            PropTicks(changed: false);
        }
        finally { fillingMesh = false; }
    }

    /// <summary>The file a build of <paramref name="file"/> starts from (also what the preview shows the model on).</summary>
    string StartPackage(string file)
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
        public int Hair { get; set; }
        public bool FromStock { get; set; }
        /// <summary>Packages the tab built (names: the built bytes are the mod's own package once saved).</summary>
        public List<string> Built { get; set; } = new();
        /// <summary>The Size slider (1 = the game's size).</summary>
        public float Size { get; set; } = 1;
        /// <summary>Match Steps to Size: movement animations at 1 / size (StepRate).</summary>
        public bool MatchSteps { get; set; } = true;
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
            Material = Math.Max(0, material.SelectedIndex), Cape = Math.Max(0, capeBox.SelectedIndex), Hair = Math.Max(0, hairBox.SelectedIndex),
            FromStock = FromStock, Built = [.. built.Keys.Order(StringComparer.OrdinalIgnoreCase)], Size = sizeSlider.Value, MatchSteps = matchSteps.Checked,
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
        foreach (var (k, v) in st.BuiltPrints ?? []) builtPrint[k] = v;   // built in an earlier session: its bytes are the mod's package
        restoring2 = true;
        buildFrom.SelectedIndex = st.FromStock ? 1 : 0;
        FillPackages();
        if (st.Package != null) Reselect(packages, st.Package);
        restoring2 = false;
        if (st.Source == null) { if (st.Package != null) AssumeBuilt(); return; }
        pendingState = st;
        if (st.Source.StartsWith("fbx:") || st.Source.StartsWith(MffDir)) { if (!FbxMode) sourceKind.SelectedIndex = 1; }
        else characterFilter.Text = st.Source;
        Reselect(characters, st.Source);
    }

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
        if (st.Cape < capeBox.Items.Count) capeBox.SelectedIndex = st.Cape;
        if (st.Hair < hairBox.Items.Count) hairBox.SelectedIndex = st.Hair;
        sizeSlider.Value = st.Size > 0 ? st.Size : 1; preview.Size = sizeSlider.Value; matchSteps.Checked = st.MatchSteps;
        restoring2 = false;
        AssumeBuilt();
        SchedulePreview();
    }
}
