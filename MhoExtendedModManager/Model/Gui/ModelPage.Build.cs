using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>The Model tab's Build into Mod and FBX exports.</summary>
sealed partial class ModelPage
{
    // --- build --------------------------------------------------------------------------------------------------------------------
    async void Build() => await BuildAsync();

    /// <summary>Build into Mod; true when the package went into the mod (also Save Changes' "Build and Save").</summary>
    public async Task<bool> BuildAsync()
    {
        if (ChosenPackage is CharacterList.Item only && !HasSource && !building) return await BuildSizeOnly(only);
        if (!HasSource || ChosenPackage is not CharacterList.Item pkg || building) return false;
        var picked = SelectedParts();
        if (picked.Count == 0) { Log("Tick at least one part."); return false; }
        string? print = Fingerprint();
        bool ok = false;
        using var busy = Busy.Begin($"Model: building onto {ChosenPackage?.Key}");
        var options = new ImportOptions { Parts = string.Join(",", picked), Material = Materials[Math.Max(0, material.SelectedIndex)].Value, Subdivide = smooth.Checked, SourceFbx = sourceFbx,
            MapFile = MapPath() is string mp && File.Exists(mp) ? mp : null,   // (an FBX source's: its smoothing)
            MaterialOverrides = OverridesFile(),
            Hair = Math.Max(0, hairBox.SelectedIndex), Cape = Math.Max(0, capeBox.SelectedIndex),
            ModelFbx = EnsureEdits().ModelFbx is string mfb && File.Exists(mfb) ? mfb : null,
            AnimFbx = new Dictionary<string, string>(edits.Anims.Where(kv => File.Exists(kv.Value)), StringComparer.OrdinalIgnoreCase),
            Size = sizeSlider.Value, NoMod = true };
        string mff = model?.Folder ?? ImportBuild.SourceName(sourceFbx!);
        // a Mixamo … FBX, or an MFF folder picked on its own (Single Model): its file (not a repository folder name)
        string mffSource = model != null && (model.Profile != null || chosenKey?.StartsWith(MffDir) == true) ? model.File : mff;
        string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"{mff} on {Path.GetFileNameWithoutExtension(pkg.Key)}"));
        building = true; UpdateStatus();
        log.Clear();
        string start = StartPackage(pkg.Key);
        Log($"Building {mff} on {pkg.Key} (from {StartLabel(pkg.Key)}) → {outDir}");
        string? uf = unrigged;
        try
        {
            var result = await Task.Run(() => ImportBuild.Run(mffSource, start, outDir,
                uf != null ? options with { SourceFbx = RigFor(uf, pkg.Key), Parts = "all" } : options, line => BeginInvoke(() => Log(line))));
            if (result != null)
            {
                await TimeSteps(result.Package, pkg.Key);
                if (uf != null) KeepRig(uf, pkg.Key);   // the mod keeps the rig of a hero it's built onto
                host.SetPackage(pkg.Key, result.Package);
                built[pkg.Key] = result.Package;
                if (print != null) builtPrint[pkg.Key] = print;
                ok = true;
                SaveState();
                Log($"Done: {pkg.Key} is in the mod now (Save Changes keeps it; Apply Changes puts it into the game).");
            }
            else Log("The build stopped: see the package problems above.");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        building = false; UpdateStatus();
        if (built.ContainsKey(pkg.Key)) { status.Text = "Built into the mod: Save Changes keeps it."; status.ForeColor = Ui.Enabled; }
        return ok;
    }

    /// <summary>
    /// No source picked (Kurt, 2026-10-05: "make size work without a source model"): Build into Mod writes only the size, into
    /// the package as the mod has it now (a model built earlier stays), relative to the game's size (the package before the
    /// Model tab changed it), so building again replaces the size instead of multiplying it; 100 % puts the game's back.
    /// </summary>
    async Task<bool> BuildSizeOnly(CharacterList.Item pkg)
    {
        float size = sizeSlider.Value;
        string? print = Fingerprint();
        bool ok = false;
        using var busy = Busy.Begin($"Model: building onto {ChosenPackage?.Key}");
        building = true; UpdateStatus();
        log.Clear();
        string current = ModCopy(pkg.Key) ?? StartPackage(pkg.Key), start = StartPackage(pkg.Key);
        Log($"Size only: {pkg.Key} at {size * 100:0} % of the game's size (no source: the model in it stays as it is)");
        try
        {
            string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"size on {Path.GetFileNameWithoutExtension(pkg.Key)}"));
            byte[]? bytes = await Task.Run(() => ImportBuild.Resize(File.ReadAllBytes(current), File.ReadAllBytes(start), pkg.Key, size, line => BeginInvoke(() => Log(line))));
            if (bytes != null)
            {
                Directory.CreateDirectory(outDir);
                string file = Path.Combine(outDir, pkg.Key);
                File.WriteAllBytes(file, bytes);
                await TimeSteps(file, pkg.Key);
                host.SetPackage(pkg.Key, file);
                built[pkg.Key] = file;
                if (print != null) builtPrint[pkg.Key] = print;
                ok = true;
                SaveState();
                Log($"Done: {pkg.Key} with the new size is in the mod now (Save Changes keeps it; Apply Changes puts it into the game).");
            }
            else Log("The size wasn't written: see above.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FileNotFoundException) { Log("ERROR: " + ex.Message); }
        building = false; UpdateStatus();
        if (built.ContainsKey(pkg.Key)) { status.Text = "Size built into the mod: Save Changes keeps it."; status.ForeColor = Ui.Enabled; }
        return ok;
    }

    // --- changes not built yet (Kurt, 2026-10-06: leaving the tab or Save Changes asks to build first) ------------------------
    /// <summary>The settings each built package was built with (<see cref="Fingerprint"/>), kept in the tab's state.</summary>
    readonly Dictionary<string, string> builtPrint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything a build of the picked package depends on: the package, Build From, Size, Match Steps, and with a
    /// source: the source, parts, material, smoothing, hair, cape, and the bone map / material overrides (by content), the
    /// unrigged FBX, Blender edits (by file size and date). Null with no package picked.</summary>
    string? Fingerprint()
    {
        if (ChosenPackage is not CharacterList.Item pkg) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append(pkg.Key).Append('|').Append(FromStock).Append('|').Append(sizeSlider.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(matchSteps.Checked);
        if (HasSource)
        {
            sb.Append('|').Append(chosenKey).Append('|').Append(sourceFbx).Append('|').Append(string.Join(",", SelectedParts()))
              .Append('|').Append(material.SelectedIndex).Append('|').Append(smooth.Checked).Append('|').Append(hairBox.SelectedIndex).Append('|').Append(capeBox.SelectedIndex);
            sb.Append('|').Append(Content(MapPath())).Append('|').Append(Content(OverridesPath())).Append('|').Append(Stamp(unrigged));
            var ed = EnsureEdits();
            sb.Append('|').Append(Stamp(ed.ModelFbx));
            foreach (var kv in ed.Anims.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.Append('|').Append(kv.Key).Append('=').Append(Stamp(kv.Value));
        }
        return sb.ToString();
        static string Stamp(string? f) => f != null && File.Exists(f) ? $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f).Ticks}" : "-";
        static string Content(string? f)
        {
            try { return f != null && File.Exists(f) ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))) : "-"; }
            catch (IOException) { return "?"; }
        }
    }

    /// <summary>The picked package has changes (a source picked, a size, materials, the bone map …) that Build into Mod hasn't
    /// put into the mod yet. Packages built in an earlier version (no settings kept) count as built.</summary>
    public bool HasUnbuiltChanges
    {
        get
        {
            if (!loaded || building || ChosenPackage is not CharacterList.Item pkg) return false;
            string? fp = Fingerprint();
            if (builtPrint.TryGetValue(pkg.Key, out var b)) return b != fp;
            if (built.ContainsKey(pkg.Key)) return false;
            return HasSource || Math.Abs(sizeSlider.Value - 1) > 1e-4;
        }
    }

    /// <summary>What isn't built, in a sentence for the question.</summary>
    public string UnbuiltWhat => ChosenPackage is not CharacterList.Item pkg ? ""
        : HasSource ? $"The model on {pkg.Key} has changes that aren't built into the mod yet."
        : $"The size of {pkg.Key} ({sizeSlider.Value * 100:0} %) isn't built into the mod yet.";

    /// <summary>Match Steps to Size: the built package's movement animations at 1 / size (StepRate), written over it; at
    /// 100 % only the ones an earlier size slowed are put back.</summary>
    async Task TimeSteps(string file, string packageName)
    {
        if (!matchSteps.Checked) return;
        float size = sizeSlider.Value;
        string? cooked = Settings.Current.CookedFolder;
        byte[]? timed = await Task.Run(() => StepRate.Apply(file, packageName, size, cooked, line => BeginInvoke(() => Log(line))));
        if (timed != null) File.WriteAllBytes(file, timed);
    }

    /// <summary>FBX round trip, export (0.11.0): databx\&lt;model&gt; on &lt;package&gt;\ with model.fbx, its textures and anims\.</summary>
    /// <summary>Installs the shipped MHO Actions add-on into that Blender (background; logged).</summary>
    async Task InstallAddon(string exe)
    {
        status.Text = "Installing the MHO Actions add-on…";
        Log($"Blender: installing the MHO Actions add-on ({Path.GetFileName(BlenderLaunch.BundledAddon())}) into {exe}…");
        string? failed = await Task.Run(() => BlenderLaunch.InstallAddon(exe));
        if (failed == null) Log("Blender: the MHO Actions add-on is installed and enabled.");
        else { Log("Blender: the add-on wasn't installed: " + failed); Dialog.Show(this, $"The MHO Actions add-on wasn't installed: {failed}\n\nThe scene will be built without it. You can install the add-on by hand: Blender → Edit → Preferences → Get Extensions → Install from Disk, the file {BlenderLaunch.BundledAddon()}.", "Add-On Not Installed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        UpdateStatus();
    }

    /// <summary>The rigged FBX for an unrigged source on a base hero (worker thread): made once in Blender, then kept in the work
    /// folder (rigs\&lt;fbx&gt; on &lt;package&gt;) until Rig Again.</summary>
    string RigFor(string file, string pkgKey)
    {
        string folder = AutoRig.Live(file, pkgKey), kept = AutoRig.Kept(host.WorkFolder, file, pkgKey);
        string rigged = AutoRig.RiggedFbx(folder);
        // the mod's copy when it's newer (the mod came from another PC, or this PC's rig folder was cleaned up)
        if (AutoRig.Newer(kept, folder)) { AutoRig.CopyRig(kept, folder); Later(() => Log($"Rig: the mod's copy of {AutoRig.Name(file, pkgKey)} is the newer one: working on it in {folder}.")); }
        if (File.Exists(rigged)) return rigged;
        Later(() => Log($"Rigging {Path.GetFileName(file)} on {pkgKey} in Blender (Automatic Weights)…"));
        string package = StartPackage(pkgKey);
        return AutoRig.Rig(file, MhoSkeleton.Load(package, null), package, folder, line => Later(() => Log(line)));
    }

    /// <summary>The mod's copy of a hero's rig, refreshed (after Build, and after each Ctrl+S in Blender on a hero built onto).</summary>
    void KeepRig(string file, string pkgKey)
    {
        string live = AutoRig.Live(file, pkgKey);
        if (File.Exists(AutoRig.RiggedFbx(live))) AutoRig.CopyRig(live, AutoRig.Kept(host.WorkFolder, file, pkgKey));
    }

    void RigAgain(string folder, string kept)
    {
        if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" &&
            Dialog.Choose(this, "Rig the model again with Automatic Weights? The rig on this hero is thrown away, with any weight fixes made in Blender.", "Rig Again", "Rig Again", "Cancel") != 0) return;
        foreach (var dir in new[] { folder, kept })
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir).Where(f => Path.GetFileName(f) is var n && (AutoRig.IsRigFile(n) || n.StartsWith("rig.blend", StringComparison.OrdinalIgnoreCase))))
                { Protected.CheckWrite(f); File.Delete(f); }
        SchedulePreview();
    }

    FileSystemWatcher? rigWatch;
    string? rigWatchFolder;

    /// <summary>Watches the shown rig's rigged.fbx (Blender's Ctrl+S writes it again): the preview reloads.</summary>
    void WatchRig(string? folder)
    {
        if (folder == rigWatchFolder) return;
        rigWatch?.Dispose(); rigWatch = null; rigWatchFolder = folder;
        if (folder == null || !Directory.Exists(folder)) return;
        rigWatch = new FileSystemWatcher(folder, "rigged.fbx") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler on = (_, _) => Later(() => { rigDelay.Stop(); rigDelay.Start(); });
        rigWatch.Changed += on; rigWatch.Created += on;
        rigWatch.Renamed += (_, _) => Later(() => { rigDelay.Stop(); rigDelay.Start(); });
        rigWatch.EnableRaisingEvents = true;
        if (!rigDelayHooked) { rigDelay.Tick += (_, _) =>
        {
            rigDelay.Stop(); rigSent = DateTime.Now; Log("Blender sent the rig (Ctrl+S): reloading.");
            if (unrigged != null && ChosenPackage is CharacterList.Item rp && built.ContainsKey(rp.Key)) KeepRig(unrigged, rp.Key);
            SchedulePreview();
        }; rigDelayHooked = true; }
    }

    readonly System.Windows.Forms.Timer rigDelay = new() { Interval = 900 };
    /// <summary>When Blender last sent the rig: the status line says so once the preview has it (the log's one visible line is
    /// the preview's by then; Kurt didn't see it).</summary>
    DateTime? rigSent;
    bool rigDelayHooked;

    void ShowFullExportMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add(new ToolStripMenuItem("Export FBX", null, (_, _) => ExportFbx(false, null)) { ToolTipText = "The model and every animation, one FBX each, into data\\model\\fbx; the folder opens." });
        m.Items.Add(new ToolStripMenuItem("Open in Blender", null, (_, _) => ExportFbx(true, null)) { ToolTipText = "The same, then a new Blender scene with every animation an Action on the NLA (saved as model.blend); Ctrl+S there sends your changes back. Settings ▾ → Model → Choose Blender picks which Blender." });
        if (unrigged != null && ChosenPackage is CharacterList.Item rp)
        {
            string folder = AutoRig.Live(unrigged, rp.Key);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Open the Rig in Blender", null, (_, _) => { if (AutoRig.Open(folder) is string why) Log("Blender: " + why); else Log($"Blender: opening the rig ({Path.Combine(folder, "rig.blend")}). Fix the weights (Weight Paint), then Ctrl+S: the preview here reloads."); })
            { Enabled = File.Exists(Path.Combine(folder, "rig.blend")), ToolTipText = "The model rigged to this hero's skeleton (Automatic Weights) in Blender; each Ctrl+S there sends it back here." });
            m.Items.Add(new ToolStripMenuItem("Rig Again", null, (_, _) => RigAgain(folder, AutoRig.Kept(host.WorkFolder, unrigged, rp.Key)))
            { Enabled = File.Exists(AutoRig.RiggedFbx(folder)), ToolTipText = "Throws away the rig on this hero (and your weight fixes in Blender) and rigs the model again with Automatic Weights." });
        }
        Ui.ShowUnder(m, fbxButton);
    }

    /// <param name="onlyAnim">The middle panel's export (0.16.4): just this animation (the right panel's: all of them).</param>
    async void ExportFbx(bool openInBlender, string? onlyAnim)
    {
        if (!HasSource || ChosenPackage is not CharacterList.Item pkg || building) return;
        var picked = SelectedParts();
        if (picked.Count == 0) { Log("Tick at least one part."); return; }
        using var busy = Busy.Begin(openInBlender ? "Model: exporting for Blender" : "Model: exporting FBX");
        // Blender first (0.16.6, Kurt): none found = ask for one before anything is exported
        if (openInBlender && BlenderLaunch.Find() == null)
        {
            if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") == "1") { Log("Blender: none found."); return; }
            int pick = Dialog.Choose(this, "No Blender was found (none under Program Files\\Blender Foundation, and none chosen in Settings).\n\nPick blender.exe if it's installed somewhere else, or get Blender (5.0 or newer, for the MHO Actions add-on) and try again.",
                "Blender Not Found", "Choose blender.exe", "Get Blender", "Cancel");
            if (pick == 1) { Process.Start(new ProcessStartInfo("https://www.blender.org/download/") { UseShellExecute = true }); return; }
            if (pick != 0) return;
            using var d = new OpenFileDialog { Title = "blender.exe", Filter = "Blender (blender.exe)|blender.exe", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Settings.Current.BlenderPath = d.FileName; Settings.Current.Save();
            Log("Blender: " + d.FileName);
        }
        // the MHO Actions add-on, offered (optional) when that Blender lacks it (0.16.7, Kurt)
        if (openInBlender && Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" && !Settings.Current.SkipAddonOffer && BlenderLaunch.Find() is string bexe && BlenderLaunch.CanOfferAddon(bexe))
        {
            int pick = Dialog.Choose(this, $"{Path.GetFileName(Path.GetDirectoryName(bexe))} doesn't have the MHO Actions add-on. It's optional: without it the scene is built with Blender's own FBX import, and Ctrl+S still sends your changes back.\n\n" +
                "With it you also get the action library (anims\\anim.blend), a Root pose track, and its tools (Batch Export Animations, Offset, the weight tools and more).\n\nInstall it into this Blender now? Your Blender preferences are kept.",
                "MHO Actions Add-On (Optional)", "Install and Continue", "Continue Without", "Don't Ask Again", "Cancel");
            if (pick == 3) return;
            if (pick == 2) { Settings.Current.SkipAddonOffer = true; Settings.Current.Save(); Log("Blender: the add-on won't be offered again (Settings ▾ → Model → Install the MHO Actions Add-On does it any time)."); }
            if (pick == 0) await InstallAddon(bexe);
        }
        // what this one does, before it starts (Kurt: so the user knows the difference)
        int count = preview.OwnAnimationCount, shared = preview.AnimationNames.Count - count;
        string what = onlyAnim == null
            ? $"Exports the model and all {count} of the base hero's own animations, one FBX each{(shared > 0 ? $" (not the {shared} shared ones every hero plays: blink, interactions)" : "")}{(openInBlender ? ", then opens them in a new Blender scene with every animation as an Action on the NLA (with the MHO Actions add-on, a background Blender turns each FBX into an Action first: this takes a while for all of them)" : "")}.\n\nFor work on a single animation, the Single Animation ▾ menu under the preview exports just the one that's picked, much faster."
            : $"Exports the model and only \"{onlyAnim}\", the animation picked in the preview{(openInBlender ? ", then opens it in a new Blender scene with that one Action on the NLA" : "")}.\n\nThe hero's other animations aren't in it. For all of them, use Full Export ▾ → {(openInBlender ? "Open in Blender" : "Export FBX")} on the right.";
        what += openInBlender ? $"\n\nBlender: {BlenderLaunch.Describe(BlenderLaunch.Find()!)} (Settings ▾ → Model → Choose Blender to change it).\n\nIn that Blender scene, every Ctrl+S sends what you changed (the mesh, the animations whose keys changed) back here as FBX edits." : "\n\nThe export folder opens when it's done; bring edits back with Single Animation ▾ → Import FBX.";
        if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" &&
            Dialog.Choose(this, what, onlyAnim == null ? (openInBlender ? "Open All Animations in Blender" : "Export All Animations") : (openInBlender ? $"Open \"{onlyAnim}\" in Blender" : $"Export \"{onlyAnim}\" Only"),
                openInBlender ? "Open in Blender" : "Export", "Cancel") != 0) return;
        var m = model; string parts = string.Join(",", picked); bool sub = smooth.Checked; string? sfbx = sourceFbx;
        string? uf = unrigged;
        string name = m?.Folder ?? ImportBuild.SourceName(sfbx!);
        string? mapFile = MapPath() is string mp && File.Exists(mp) ? mp : null;
        string? ovr = OverridesFile();
        int hairChoice = sfbx == null ? Math.Max(0, hairBox.SelectedIndex) : 0, capeChoice = sfbx == null ? Math.Max(0, capeBox.SelectedIndex) : 0;
        var exportEdits = AnimEdits.Parse(EnsureEdits().Serialize());
        string outDir = UniqueDir(Path.Combine(Settings.Home, "fbx", $"{name} on {Path.GetFileNameWithoutExtension(pkg.Key)}{(onlyAnim != null ? " - " + FbxExport.SafeName(onlyAnim) : "")}"));
        building = true; UpdateStatus(); status.Text = "Exporting FBX…";
        Log($"Exporting FBX: {name} on {pkg.Key} → {outDir}");
        try
        {
            await Task.Run(() =>
            {
                string package = StartPackage(pkg.Key);
                if (sfbx != null)
                {
                    var r = FbxReimport.Load(uf != null ? RigFor(uf, pkg.Key) : sfbx, MhoSkeleton.Load(package, null), uf != null ? null : picked.ToHashSet(StringComparer.OrdinalIgnoreCase), _ => { });
                    if (mapFile != null) WeightSmooth.Apply(r, BoneMapFile.Load(mapFile).Smooth);
                    // the FBX edits as the preview and Build use them: the mesh from Blender, the replaced animations
                    if (exportEdits.ModelFbx is string emf && File.Exists(emf)) FbxReimport.Apply(r, emf, _ => { });
                    MaterialOverrides.Apply(r, ovr);
                    FbxExport.Run(r, package, outDir, onlyAnim != null ? [onlyAnim] : [], line => BeginInvoke(() => Log(line)), FbxExport.EditsAdjust(exportEdits), exact: onlyAnim != null);
                }
                else FbxExport.Work(m!, picked, sub, package, mapFile, hairChoice, exportEdits, outDir, line => BeginInvoke(() => Log(line)), onlyAnim != null ? [onlyAnim] : null, exact: onlyAnim != null, cape: capeChoice, overrides: ovr);
            });
            lastZip = Path.Combine(outDir, "model.fbx");   // Open Folder shows the latest output (a build's .ZIP or this)
            // in a new Blender scene (0.16.2), else the folder
            if (openInBlender && BlenderLaunch.Open(outDir) is string why) Log("Blender: " + why);
            else if (openInBlender)
            {
                Log($"Blender: opening {outDir} in {BlenderLaunch.Find()} (model.fbx, every animation as an Action on the NLA; saved as model.blend there). Each Ctrl+S in Blender sends what you changed back here.");
                LinkBlender(outDir);
            }
            else if (Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1") Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastZip}\"") { UseShellExecute = false });   // (tests: no window)
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        building = false; openButton.Enabled = lastZip != null; UpdateStatus();
        if (lastZip != null && lastZip.EndsWith(".fbx")) { status.Text = "FBX exported: model.fbx and the anims folder."; status.ForeColor = Ui.Enabled; }
    }
}
