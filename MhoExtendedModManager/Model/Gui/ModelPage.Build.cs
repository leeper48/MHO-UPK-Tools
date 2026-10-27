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
        await propTicks;   // (a Character just picked: a prop ticks its own part first)
        if (AddedTarget is { } back && !HasSource && !building) return await AddedOwnModel(back);
        if (ChosenPackage is CharacterList.Item only && !HasSource && !building) return await BuildSizeOnly(only);
        if (!HasSource || ChosenPackage is not CharacterList.Item pkg || building) return false;
        var picked = SelectedParts();
        if (picked.Count == 0) { Log("Tick at least one part."); return false; }
        string? print = Fingerprint();
        bool ok = false;
        using var busy = Busy.Begin($"Model: building onto {ChosenPackage?.Key}");
        var options = new ImportOptions { Parts = string.Join(",", picked), FeetShift = feetCustom ?? (feetKnown ? feetAuto : null), FeetMeasured = feetCustom == null && feetKnown, KeepSourceSize = keepSourceSize.Checked, Lean = LeanToUse, JointFit = jointFit.Checked, Material = Materials[Math.Max(0, material.SelectedIndex)].Value, Subdivide = smooth.Checked, SourceFbx = sourceFbx,
            MapFile = MapPath() is string mp && File.Exists(mp) ? mp : null,   // (an FBX source's: its smoothing)
            MaterialOverrides = OverridesFile(),
            PaintFile = PaintPath() is string paintPath && File.Exists(paintPath) ? paintPath : null,
            Hair = HairChoice, Cape = CapeChoice, Rigs = OtherRigs(),
            ModelFbx = EnsureEdits().ModelFbx is string mfb && File.Exists(mfb) ? mfb : null,
            AnimFbx = new Dictionary<string, string>(edits.Anims.Where(kv => File.Exists(kv.Value)), StringComparer.OrdinalIgnoreCase),
            Size = sizeSlider.Value, NoMod = true, PropFit = PropFitForTarget(), PropAsIs = AddedTarget?.KeepShape == true };
        string mff = model?.Folder ?? ImportBuild.SourceName(sourceFbx!);
        // a Mixamo … FBX, or an MFF folder picked on its own (Single Model): its file (not a repository folder name)
        string mffSource = model != null && (model.Profile != null || chosenKey?.StartsWith(MffDir) == true) ? model.File : mff;
        string outDir = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"{mff} on {Path.GetFileNameWithoutExtension(pkg.Key)}"));
        building = true; UpdateStatus();
        log.Clear();
        string baseStart = StartPackage(pkg.Key);
        var addedTarget = AddedTarget;   // (an added prop: built in its copy, kept as its own model)
        // another model of this package built already (the body, then its shield): this build starts from that build, so both stay
        string? keepOther = addedTarget != null ? null : await Task.Run(() => OtherModelBuilt(pkg.Key, baseStart));
        if (keepOther != null) options = options with { SizeFrom = baseStart };
        string start = keepOther != null ? ModCopy(pkg.Key)! : baseStart;
        lastStart = start;
        Log($"Building {mff} on {pkg.Key} (from {(keepOther != null ? $"the mod's copy, which holds your build of {keepOther}: it stays" : StartLabel(pkg.Key))}) → {outDir}");
        string? uf = unrigged;
        try
        {
            var runOptions = uf != null ? options with { SourceFbx = RigFor(uf, pkg.Key), Parts = "all" } : options;
            var result = await Task.Run(() => ImportBuild.Run(mffSource, start, outDir, runOptions, line => BeginInvoke(() => Log(line))));
            if (addedTarget != null)
            {
                ok = result != null && await KeepAddedModel(addedTarget, pkg.Key, result.Package);
                building = false; UpdateStatus();
                return ok;
            }
            // the other models the character swaps to (Ant-Man shrunk: his "smallmesh" alias, the same skeleton at 0.4 scale):
            // the same model is built onto each, fitted to its own skeleton (a user's Cassie Lang stayed Ant-Man when shrunk)
            if (result != null)
            {
                string? target = null;
                try { target = await Task.Run(() => MhoSkeleton.Load(start, null).Name); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                var aliases = target == null ? [] : await Task.Run(() => MhoSkeleton.Aliases(baseStart, target));
                string? pickWas = MhoSkeleton.ChosenFor(pkg.Key);
                foreach (string alias in aliases)
                {
                    Log($"{target} has another model the game swaps to, {alias}: building onto it too");
                    string aliasOut = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"{mff} on {Path.GetFileNameWithoutExtension(pkg.Key)} ({alias})"));
                    string from = result!.Package;
                    MhoSkeleton.Choose(pkg.Key, alias);
                    try
                    {
                        var more = await Task.Run(() => ImportBuild.Run(mffSource, from, aliasOut, runOptions with { SizeFrom = baseStart }, line => BeginInvoke(() => Log(line))));
                        if (more != null) result = more;
                        else Log($"{alias}: the build stopped (see above); the model's normal size is built, its {alias} isn't.");
                    }
                    finally { MhoSkeleton.Choose(pkg.Key, pickWas); }
                }
            }
            if (result != null)
            {
                // added bones (a cape, dress, wings): the mirror table gets an entry for each (else mirroring an animation
                // reads past its end: the Array.h:575 crash; Kurt, 2026-10-12)
                try
                {
                    if (await Task.Run(() => MhoExtendedModManager.MirrorTables.Fix(result.Package)) is { } mf)
                    {
                        File.WriteAllBytes(result.Package, mf.Bytes);
                        foreach (string c in mf.Changes) Log("Mirror table: " + c);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { Log("Mirror table: " + ex.Message); }
                // started from the mod's copy (another model built there stays): the earlier builds' textures nothing uses any
                // more made tiny (Kurt, 2026-10-27: 6.5 → 30 MB over a few builds)
                if (keepOther != null)
                    try
                    {
                        string built0 = result.Package;
                        var said = new List<string>();
                        byte[]? clean = await Task.Run(() => BuildLeftovers.Shrink(File.ReadAllBytes(built0), said.Add));
                        if (clean != null) File.WriteAllBytes(built0, clean);
                        foreach (string line in said) Log(line);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { Log("cleanup: " + ex.Message); }
                await TimeSteps(result.Package, pkg.Key);
                if (uf != null) KeepRig(uf, pkg.Key);   // the mod keeps the rig of a hero it's built onto
                host.SetPackage(pkg.Key, result.Package);
                built[pkg.Key] = result.Package;
                if (print != null) builtPrint[pkg.Key] = print;
                takenOff.Remove(pkg.Key);
                ok = true;
                await VariationsAfterBuild(pkg.Key);   // (added random materials made again from the new model)
                var copiesBuilt = await BuildCopies(pkg.Key, mffSource, runOptions, print, mff);
                SaveState();
                Log($"Done: {pkg.Key} is in the mod now (Save Changes keeps it; Apply Changes puts it into the game).");
                await LogCrashChecks([(pkg.Key, result.Package), .. copiesBuilt]);
            }
            else Log("The build stopped: see the package problems above.");
        }
        catch (Exception ex) { Log("ERROR: " + ex.Message); }
        building = false; UpdateStatus();
        if (built.ContainsKey(pkg.Key)) { status.Text = "Built into the mod: Save Changes keeps it."; status.ForeColor = Ui.Enabled; }
        if (ok && !buildingPartners) await OfferSummonPartners(pkg.Key);   // (Strike Team: the tumble's model and the summoned Beast's)
        return ok;
    }

    /// <summary>
    /// The same summon model in the hero's other packages (<see cref="SummonCopies"/>; Kurt, 2026-10-25: Squirrelpocalypse's
    /// squirrel is in its projectile's package and in the power's, and the game shows the copy it loads first): the model is
    /// built onto each with the same options, and each goes into the mod (added from the game when the mod lacks it).
    /// </summary>
    async Task<List<(string File, string Path)>> BuildCopies(string pkgKey, string mffSource, ImportOptions runOptions, string? print, string mff)
    {
        var done = new List<(string File, string Path)>();
        var propCopies = PropCopies(pkgKey);
        var copies = propCopies is { } pc0 ? [] : SummonCopies(pkgKey);
        // the summon's copies in characters' packages (Kurt, 2026-10-26): the summoning hero's own ones go too; other
        // characters' (Strike Team's Beast is Beast's Astonishing costume) only when the user says so, remembered per target
        // (a prop picked in the Props view: its copies, the same way: Thor's hammer is also in the Skrull Thor enemy and the team-ups)
        var (ownChars, otherChars) = propCopies is { } pc ? (pc.Own, pc.Others) : SummonCharacterCopies(pkgKey);
        copies.AddRange(ownChars);
        if (otherChars.Count > 0)
        {
            if (!copyOthers.TryGetValue(pkgKey, out bool yes))
            {
                yes = Environment.GetEnvironmentVariable("MFF_GUI_NOASK") != "1" && Dialog.Choose(this,
                    $"The same model is also in other characters' packages:\n\n{string.Join("\n", otherChars.Take(8).Select(f => "  • " + f))}{(otherChars.Count > 8 ? "\n  • …" : "")}\n\nThe game shows whichever copy it loads first: near those characters (another player's hero, an NPC), theirs can show instead of yours, and yours can show on them.\n\n  • Change Them Too: Your model goes into those packages as well (those characters look like it).\n  • Leave Them: They keep the game's look.",
                    "Same Model in Other Characters", "Change Them Too", "Leave Them") == 0;
                copyOthers[pkgKey] = yes;
                SaveState();
            }
            if (yes) copies.AddRange(otherChars);
            else Log($"Left as the game's: the same model in {string.Join(", ", otherChars)} (other characters).");
        }
        string? meshName = propCopies?.Mesh ?? (Summons.TryGetValue(pkgKey, out var sums) ? sums[0].Mesh : null);
        foreach (string copy in copies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string file = copy;
            if (host.Packages.FirstOrDefault(p => p.File.Equals(copy, StringComparison.OrdinalIgnoreCase)) is { File: not null } have) file = have.File;
            else
            {
                string stock;
                try { stock = BasePackage.Resolve(copy); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { Log($"{copy} has the same model, but it couldn't be read: {ex.Message}"); continue; }
                host.AddPackage(file, stock);
                added.Add(file);
            }
            Log($"{file} holds the same model under the same path (the game shows whichever copy it loads first): building onto it too");
            string outCopy = UniqueDir(Path.Combine(host.WorkFolder, "builds", $"{mff} on {Path.GetFileNameWithoutExtension(file)}"));
            string start = StartPackage(file);
            // (the copy of this model in that package, not its own character: a hero's costume package holds both)
            string? pickWas = MhoSkeleton.ChosenFor(file);
            if (meshName != null) MhoSkeleton.Choose(file, meshName);
            try
            {
                var more = await Task.Run(() => ImportBuild.Run(mffSource, start, outCopy, runOptions, line => BeginInvoke(() => Log(line))));
                if (more == null) { Log($"{file}: the build stopped (see above); its copy of the model is the game's."); continue; }
                // the mirror table, as for the first package (added bones need entries: the Array.h:575 crash)
                if (await Task.Run(() => MhoExtendedModManager.MirrorTables.Fix(more.Package)) is { } mf)
                {
                    File.WriteAllBytes(more.Package, mf.Bytes);
                    foreach (string c in mf.Changes) Log($"Mirror table ({file}): " + c);
                }
                host.SetPackage(file, more.Package);
                built[file] = more.Package;
                if (print != null) builtPrint[file] = print;
                takenOff.Remove(file);
                done.Add((file, more.Package));
                Log($"{file} is in the mod too, with the same model.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Log($"{file}: {ex.Message}"); }
            finally { MhoSkeleton.Choose(file, pickWas); }
        }
        return done;
    }

    /// <summary>Per summon target: build its copies in other characters' packages too (asked once, kept with the mod).</summary>
    readonly Dictionary<string, bool> copyOthers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The crash checks on a build (CrashCheck: added bones, other copies of the model, the mirror table, sockets) in
    /// the log (Kurt, 2026-10-12: a loincloth's added bones crashed on one hero, not another). Save's Make Unique gives the
    /// model a path of its own, which takes care of the shared copies.</summary>
    async Task LogCrashChecks(List<(string File, string Path)> packages)
    {
        if (Settings.Current.CookedFolder is not string cooked) return;
        List<MhoExtendedModManager.CrashCheck.Issue> issues;
        // (the copies built with it are checked together: each counts the others as the mod's own)
        try { issues = await Task.Run(() => MhoExtendedModManager.CrashCheck.For(packages, cooked)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        foreach (var i in issues.OrderByDescending(i => i.Crash))
            Log($"{(i.Crash ? "CRASH RISK" : "Note")}: {i.Mesh}: {i.Text}" + (i.Crash && i.Text.Contains("Make Unique") ? " Save Changes does that for this mod." : ""));
    }

    /// <summary>
    /// Another model of the package that the mod's copy has changed (Kurt, 2026-10-08: US Agent's body and shield on Cap: two
    /// builds of one package): the first other skeletal mesh whose data differs from the start package's, else null. Then the
    /// build starts from the mod's copy, so the earlier build stays (objects of the earlier build of the same model stay
    /// unused, under their own names).
    /// </summary>
    string? OtherModelBuilt(string pkgKey, string baseStart)
    {
        string? current = ModCopy(pkgKey);
        if (current == null || !File.Exists(current) || Path.GetFullPath(current).Equals(Path.GetFullPath(baseStart), StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            string target = MhoSkeleton.Load(baseStart, null).Name;
            var a = AnimExportCli.Packages.Package.Open(baseStart);
            var b = AnimExportCli.Packages.Package.Open(current);
            // props' models the start doesn't have aren't a model built here (an added prop's, its fitted copies, a borrowed
            // power's: Kurt, 2026-10-24, Thor's hammer on Captain America made every build start from the last one, so its
            // textures piled up and Feet seemed to do nothing). The costume's own props are (Kurt, 2026-10-27: Thor Age of
            // Ultron's hammer, built here, went back to the game's when his body was built after it).
            foreach (int i in b.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName))
            {
                string name = b.GetExportName(i);
                if (name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"_x\d+(_fit\d+)?$|_m\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;   // (a prop's fitted copy: …_x85_fit49176; an added prop's own model)
                int j = a.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).FirstOrDefault(k => a.GetExportName(k).Equals(name, StringComparison.OrdinalIgnoreCase), -1);
                // (only a model the game's package has, changed: the tab builds onto those; one it doesn't have is a prop's)
                if (j >= 0 && !a.GetExportData(j).SequenceEqual(b.GetExportData(i))) return name;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        return null;
    }

    /// <summary>
    /// No source picked (Kurt, 2026-10-05: "make size work without a source model"): Build into Mod writes only the size, into
    /// the package as the mod has it now (a model built earlier stays), relative to the game's size (the package before the
    /// Model tab changed it), so building again replaces the size instead of multiplying it; 100 % puts the game's back.
    /// </summary>
    /// <summary>
    /// An added prop's build kept as its own model (Model\added_models\&lt;package&gt;\&lt;class&gt;.upk, saved with the mod) and
    /// put into the costume's package with its fit (the editor: PowerBorrow.AddProp copies the model in).
    /// </summary>
    async Task<bool> KeepAddedModel(AddedPropEntry e, string key, string builtPackage)
    {
        string rel = Path.Combine("added_models", Path.GetFileNameWithoutExtension(key), e.Class + ".upk");
        string keep = Path.Combine(host.WorkFolder, rel);
        try { Directory.CreateDirectory(Path.GetDirectoryName(keep)!); File.Copy(builtPackage, keep, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("ERROR: the added prop's model couldn't be kept: " + ex.Message); return false; }
        if (!await host.SetAddedModel(e, rel)) { Log($"{e.Name}: its new model couldn't be put into {key} (see the message)."); return false; }
        Log($"Done: {e.Name} has its new model in {key}, with its fit (Save Changes keeps it; Apply Changes puts it into the game).");
        if (!IsDisposed) { status.Text = "Built into the mod: Save Changes keeps it."; status.ForeColor = Ui.Enabled; }
        return true;
    }

    /// <summary>No source on an added prop with a model of its own: its character's model back (asked first).</summary>
    async Task<bool> AddedOwnModel(AddedPropEntry e)
    {
        if (e.ModelFrom == null) { Log($"{e.Name}: pick a source to give it a new model (its fit is on the sliders, no build needed)."); return false; }
        if (Dialog.Choose(this, $"Put the character's own model back on {e.Name}? The model built here is dropped from it.", "Added Prop", "Put It Back", "Cancel") != 0) return false;
        building = true; UpdateStatus();
        bool ok = await host.SetAddedModel(e, null);
        building = false; UpdateStatus();
        if (ok) Log($"{e.Name}: its character's own model is back in {e.Package}.");
        return ok;
    }

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
                takenOff.Remove(pkg.Key);
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
    /// <summary>Packages whose model was taken off (Remove → Restore the Mod's Copy), with the settings then: until they change,
    /// leaving or saving doesn't ask to build it again.</summary>
    readonly Dictionary<string, string?> takenOff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything a build of the picked package depends on: the package, Build From, Size, Match Steps, and with a
    /// source: the source, parts, material, smoothing, hair, cape, and the bone map / material overrides (by content), the
    /// unrigged FBX, Blender edits (by file size and date). Null with no package picked.</summary>
    string? Fingerprint()
    {
        if (ChosenPackage is not CharacterList.Item pkg) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append(pkg.Key).Append('|').Append(MhoSkeleton.ChosenFor(pkg.Key)).Append('|').Append(FromStock).Append('|').Append(sizeSlider.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|').Append(matchSteps.Checked).Append('|').Append(feetCustom?.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) ?? "auto").Append('|').Append(keepSourceSize.Checked).Append('|').Append(leanSlider.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)).Append(jointFit.Checked ? "|joints" : "").Append(PaintText() is string paintText ? "|paint" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(paintText)))[..10] : "");
        if (PropFitForTarget() is { IsNone: false } pfp) sb.Append("|propfit").Append(pfp);   // (only then: builds from before keep their print)
        if (HasSource)
        {
            sb.Append('|').Append(chosenKey).Append('|').Append(sourceFbx).Append('|').Append(string.Join(",", SelectedParts()))
              .Append('|').Append(material.SelectedIndex).Append('|').Append(smooth.Checked).Append('|').Append(HairChoice).Append('|').Append(CapeChoice);
            if (OtherRigs().Count > 0) sb.Append('|').Append(OtherRigsText());   // (only then: builds from before keep their print)
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
            if (takenOff.TryGetValue(pkg.Key, out var off) && off == fp) return false;   // the model just taken off: not asked again
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
        else { Log("Blender: the add-on wasn't installed: " + failed); Dialog.Show(this, $"The MHO Actions add-on wasn't installed: {failed}\n\nThe scene will be built without it.\n\nTo install the add-on by hand: Blender → Edit → Preferences → Get Extensions → Install from Disk, then pick the file:\n  {BlenderLaunch.BundledAddon()}", "Add-On Not Installed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
            int pick = Dialog.Choose(this, "No Blender was found (none under Program Files\\Blender Foundation, and none chosen in Settings).\n\n  • Pick blender.exe if it's installed somewhere else.\n  • Or get Blender (5.0 or newer, for the MHO Actions add-on) and try again.",
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
            int pick = Dialog.Choose(this, $"{Path.GetFileName(Path.GetDirectoryName(bexe))} doesn't have the MHO Actions add-on.\n\n  • It's optional: Without it the scene is built with Blender's own FBX import, and Ctrl+S still sends your changes back.\n" +
                "  • With it you also get the action library (anims\\anim.blend), a Root pose track, and its tools (Batch Export Animations, Offset, the weight tools and more).\n\nInstall it into this Blender now? Your Blender preferences are kept.",
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
        int hairChoice = sfbx == null ? HairChoice : 0, capeChoice = sfbx == null ? CapeChoice : 0;
        var moreRigs = sfbx == null ? OtherRigs() : [];
        var exportEdits = AnimEdits.Parse(EnsureEdits().Serialize());
        bool joints = jointFit.Checked; float leanUse = LeanToUse;
        string? paintFile = PaintPath() is string pp && File.Exists(pp) ? pp : null;
        var prefer = preview.AnimationsForExport();   // (each animation as the preview plays it: a borrowed power's under its slot name)
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
                    var tsk = MhoSkeleton.Load(package, null);
                    var r = FbxReimport.LoadAny(uf != null ? RigFor(uf, pkg.Key) : sfbx, tsk, uf != null ? null : picked.ToHashSet(StringComparer.OrdinalIgnoreCase), _ => { }, AddedTarget?.KeepShape == true);
                    if (!PropFit.IsProp(tsk)) FbxLean.Apply(r, leanUse);
                    if (mapFile != null) WeightSmooth.Apply(r, BoneMapFile.Load(mapFile).Smooth);
                    // the FBX edits as the preview and Build use them: the mesh from Blender, the replaced animations
                    if (exportEdits.ModelFbx is string emf && File.Exists(emf)) FbxReimport.ApplyAny(r, emf, tsk, _ => { });
                    MaterialOverrides.Apply(r, ovr);
                    WeightPaint.Apply(r, paintFile);
                    FbxExport.Run(r, package, outDir, onlyAnim != null ? [onlyAnim] : [], line => BeginInvoke(() => Log(line)), FbxExport.EditsAdjust(exportEdits), exact: onlyAnim != null, prefer: prefer);
                }
                else FbxExport.Work(m!, picked, sub, package, mapFile, hairChoice, exportEdits, outDir, line => BeginInvoke(() => Log(line)), onlyAnim != null ? [onlyAnim] : null, exact: onlyAnim != null, cape: capeChoice, overrides: ovr, rigsMore: moreRigs, prefer: prefer, jointFit: joints, paintFile: paintFile, lean: leanUse);
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
