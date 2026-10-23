namespace MhoExtendedModManager.Gui;

/// <summary>
/// Powers tab → a Character entry → Parts → DODGE GHOSTS (Kurt, 2026-10-17: Captain Carter's ghosts on Taskmaster; pick the
/// poses instead of coming back to a build that guessed them): the dodge's ghosts made from the mod's model. Where each ghost
/// sits comes from the game (DodgeGhosts.Analyze); its pose is the one picked here (the animation and frame showing in the
/// preview: Use This Pose), else automatic. Every change builds a preview copy of the package (shown at once through the
/// preview's overrides); Save builds it into the mod (before the power colors and parts, which apply on top). The package
/// before any ghosts (WorkFolder\ghost_base) is kept with the mod, so Game's Ghosts puts it back.
/// </summary>
sealed partial class ModEditorView
{
    readonly Label ghostsNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4) };
    DataGridView? ghostsGrid;
    Control? ghostsBox;
    Button? ghostsUse, ghostsPose, ghostsAuto, ghostsGame, ghostsShow;
    readonly DropDown ghostsLean = new() { Width = 150 };
    LightSlider? ghostsDegrees;
    readonly System.Windows.Forms.Timer ghostsDegreesTimer = new() { Interval = 450 };
    CheckBox? ghostsExact;
    /// <summary>The Character entry's base package the ghosts panel shows, and its stock ghosts once read.</summary>
    string? ghostsFile;
    List<DodgeGhosts.Slot>? ghostSlots;
    int ghostsRequest, ghostBuildRequest;
    /// <summary>Preview builds: package → (the choices it was built with, the file).</summary>
    readonly Dictionary<string, (string Key, string Path)> ghostBuilt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages the preview reads in place of the draft's: borrowed looks and the ghosts' preview builds.</summary>
    Dictionary<string, string> PreviewOverrides()
    {
        var d = new Dictionary<string, string>(borrowFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var (f, b) in ghostBuilt) if (draft.DodgeGhosts.Any(g => g.Package.Equals(f, StringComparison.OrdinalIgnoreCase)) && GhostKey(f) == b.Key) d[f] = b.Path;
        foreach (string f in propTouched) if (DraftPath(f) is string pp) d[f] = pp;   // (added props: the work copy, after the borrowed looks)
        return d;
    }

    Control GhostsSection()
    {
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0, (int)(8 * S), 0, 0), Visible = false };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) box.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        box.Controls.Add(new Label { Text = "DODGE GHOSTS", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(0, 4, 0, 2) }, 0, 0);
        box.Controls.Add(ghostsNote, 0, 1);
        ghostsUse = Ui.FlatButton("Make Them From This Model", UseGhostModel, tip: "Makes the dodge's ghosts from the mod's model, at the character's own size and where it stands, as the game's are (each starts in the hero's animation frame closest to the game's ghost). Impact: The dodge is the hero's, so every costume of the hero shows these ghosts; Save writes them into the hero's package.");
        ghostsPose = Ui.FlatButton("Use This Pose", UseGhostPose, tip: "The selected ghost takes the pose the preview shows now: pick an animation in the preview and drag Frame to the moment you want, then click this. The preview shows the ghost at once (▶ on Dodge Effect plays it).");
        ghostsAuto = Ui.FlatButton("Automatic", AutoGhostPose, tip: "The selected ghost goes back to its automatic pose (the one closest to the game's ghost).");
        ghostsGame = Ui.FlatButton("Game's Ghosts", GameGhosts, tip: "Back to the game's own ghosts: Save puts the package back as it was before the ghosts were made.");
        ghostsShow = Ui.FlatButton("Show Pose", ShowGhostPose, tip: "Takes the preview to the selected ghost's pose: its animation, at its frame, paused (double-click a row does the same).");
        var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4) };
        bar.Controls.AddRange([ghostsUse, ghostsPose, ghostsShow, ghostsAuto, ghostsGame]);
        ghostsExact = new CheckBox { Text = "Exact Size, on the Ground", AutoSize = true, Checked = true, Margin = new Padding(8, 8, 0, 0) };
        Ui.Tip(ghostsExact, "On: The ghosts come out at the character's own size, standing on the ground. The game's dodge effect draws its ghosts 1.1× bigger and 5 units lower; on, the meshes are made to undo that. Off: As the effect draws them (the game's own ghosts are like that).");
        ghostsExact.CheckedChanged += (_, _) =>
        {
            if (fillingGhosts || ghostsFile == null) return;
            foreach (var e in draft.DodgeGhosts.Where(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase))) e.KeepEffectSize = !ghostsExact.Checked;
            if (draft.DodgeGhosts.Any(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase))) BuildGhostPreview();
        };
        bar.Controls.Add(ghostsExact);
        ghostsLean.Items.AddRange(["No Lean", "Lean Forward", "Lean Back", "Lean Left", "Lean Right"]);
        ghostsLean.SelectedIndex = 0;
        ghostsLean.Margin = new Padding(8, 3, 0, 0);
        Ui.Tip(ghostsLean, $"The selected ghost's lean: its whole pose tipped {DodgeGhosts.LeanDegrees:0}° about the feet, to look like it's moving.");
        ghostsLean.SelectedIndexChanged += (_, _) =>
        {
            if (fillingGhosts || ghostsFile == null || ghostsGrid?.CurrentRow?.Tag is not DodgeGhosts.Slot s) return;
            if (draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.Ghost.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) is not { } e) return;
            e.Lean = ghostsLean.SelectedIndex <= 0 ? "" : DodgeGhosts.Leans[ghostsLean.SelectedIndex - 1];
            FillGhosts();
            BuildGhostPreview();
        };
        bar.Controls.Add(ghostsLean);
        // how far the leans tip (all the ghosts; built a moment after the slider stops)
        ghostsDegrees = new LightSlider { Label = "Lean", Min = 0, Max = 30, Step = 1, Mark = DodgeGhosts.LeanDegrees, Format = v => $"{v:0}°", Home = () => DodgeGhosts.LeanDegrees, Value = DodgeGhosts.LeanDegrees,
            Width = (int)(260 * S), Height = (int)(28 * S), Margin = new Padding(8, 2, 0, 0) };
        Ui.Tip(ghostsDegrees, $"How far the ghosts lean (all of them), in degrees: {DodgeGhosts.LeanDegrees:0} at first. Double-click: back to {DodgeGhosts.LeanDegrees:0}.");
        ghostsDegrees.ValueChanged += () =>
        {
            if (fillingGhosts || ghostsFile == null) return;
            foreach (var e in draft.DodgeGhosts.Where(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase))) e.LeanDegrees = ghostsDegrees.Value;
            ghostsDegreesTimer.Stop(); ghostsDegreesTimer.Start();
        };
        ghostsDegreesTimer.Tick += (_, _) => { ghostsDegreesTimer.Stop(); if (draft.DodgeGhosts.Any(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase))) BuildGhostPreview(); };
        Disposed += (_, _) => ghostsDegreesTimer.Dispose();
        bar.Controls.Add(ghostsDegrees);
        box.Controls.Add(bar, 0, 2);
        var g = Ui.Grid(S, false, ("Ghost", 150), ("Pose", 0));
        g.Dock = DockStyle.None; g.Anchor = AnchorStyles.Left | AnchorStyles.Right; g.MultiSelect = false;
        g.SelectionChanged += (_, _) => { RefreshGhostButtons(); ShowGhostLean(); };
        g.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowGhostPose(); };
        Ui.Tip(g, "The dodge's ghosts: each one's pose (an animation and frame of the model, or Automatic). Select one, show the pose you want in the preview, then Use This Pose.");
        ghostsGrid = g;
        box.Controls.Add(g, 0, 3);
        ghostsBox = box;
        return box;
    }

    /// <summary>A Character entry selected: its dodge ghosts (read from the game in the background, once per hero).</summary>
    void ShowGhosts(Fx.PowerList.Power p)
    {
        if (ghostsBox == null || game == null) return;
        int req = ++ghostsRequest;
        ghostsFile = p.Prototype[CharacterPrefix.Length..];
        ghostSlots = null;
        ghostsBox.Visible = true;
        ghostsGrid?.Rows.Clear();
        ghostsNote.Text = "Reading the game's dodge ghosts (a few seconds the first time for a hero)…";
        RefreshGhostButtons();
        string file = ghostsFile, cooked = game.Cooked;
        Task.Run(() => DodgeGhosts.Analyze(file, cooked)).ContinueWith(t =>
        {
            if (IsDisposed || req != ghostsRequest) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                string why = t.Exception?.GetBaseException().Message ?? "";
                if (why.Contains("no ghosts")) { ghostsBox.Visible = false; return; }   // (a dodge without ghosts: nothing to show)
                ghostsNote.Text = "The dodge's ghosts couldn't be read: " + why;
                return;
            }
            ghostSlots = t.Result;
            FillGhosts();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Hides the ghosts panel (another entry selected).</summary>
    void HideGhosts() { ghostsRequest++; ghostsFile = null; if (ghostsBox != null) ghostsBox.Visible = false; }

    bool fillingGhosts;

    void FillGhosts()
    {
        if (ghostsGrid == null || ghostSlots == null || ghostsFile == null) return;
        if (ghostsExact != null)
        {
            fillingGhosts = true;
            ghostsExact.Checked = !draft.DodgeGhosts.Any(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.KeepEffectSize);
            if (ghostsDegrees != null) ghostsDegrees.Value = draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase))?.LeanDegrees ?? DodgeGhosts.LeanDegrees;
            fillingGhosts = false;
        }
        int sel = ghostsGrid.CurrentRow?.Index ?? 0;
        ghostsGrid.Rows.Clear();
        var mine = draft.DodgeGhosts.Where(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase)).ToList();
        for (int i = 0; i < ghostSlots.Count; i++)
        {
            var e = mine.FirstOrDefault(x => x.Ghost.Equals(ghostSlots[i].Name, StringComparison.OrdinalIgnoreCase));
            string pose = e == null ? "The Game's" : e.Anim.Length == 0 ? $"Automatic ({(ghostSlots[i].Anim.Length == 0 ? "rest pose" : $"{ghostSlots[i].Anim} · frame {ghostSlots[i].Frame:0}")})" : $"{e.Anim} · frame {e.Frame:0}";
            if (e != null && e.Lean.Length > 0) pose += $" · leaning {e.Lean}";
            int r = ghostsGrid.Rows.Add($"Ghost {i + 1}", pose);
            ghostsGrid.Rows[r].Tag = ghostSlots[i];
            ghostsGrid.Rows[r].Cells[0].ToolTipText = ghostSlots[i].Name;
            ghostsGrid.Rows[r].DefaultCellStyle.ForeColor = e == null ? Ui.Subtle : Ui.Text;
        }
        ghostsGrid.Height = ghostsGrid.ColumnHeadersHeight + ghostsGrid.RowTemplate.Height * Math.Max(1, ghostsGrid.Rows.Count) + 4;
        if (ghostsGrid.Rows.Count > 0) { ghostsGrid.ClearSelection(); var row = ghostsGrid.Rows[Math.Clamp(sel, 0, ghostsGrid.Rows.Count - 1)]; row.Selected = true; ghostsGrid.CurrentCell = row.Cells[0]; }
        string? hero = HeroOf.Package(ghostsFile, game?.Cooked);
        ghostsNote.Text = mine.Count == 0
            ? $"The game's ghosts. Make Them From This Model to use the mod's model ({ghostSlots.Count} ghost(s); the dodge is the hero's, so every costume of {hero ?? "the hero"} shows them)."
            : $"Made from {mine[0].ModelMesh} ({mine[0].ModelPackage}). Pick a ghost, show a pose in the preview, Use This Pose. ▶ on Dodge Effect plays them. Every costume of {hero ?? "the hero"} shows them; Save writes them.";
        RefreshGhostButtons();
    }

    void RefreshGhostButtons()
    {
        bool on = ghostsFile != null && draft.DodgeGhosts.Any(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase));
        bool row = ghostsGrid?.CurrentRow?.Tag is DodgeGhosts.Slot;
        if (ghostsUse != null) ghostsUse.Enabled = ghostSlots != null && !on;
        if (ghostsPose != null) ghostsPose.Enabled = on && row;
        if (ghostsAuto != null) ghostsAuto.Enabled = on && row;
        if (ghostsGame != null) ghostsGame.Enabled = on;
        if (ghostsShow != null) ghostsShow.Enabled = row;
        if (ghostsExact != null) ghostsExact.Enabled = on;
        ghostsLean.Enabled = on && row;
        if (ghostsDegrees != null) ghostsDegrees.Enabled = on;
    }

    /// <summary>The model the ghosts are made from: the mod's own model for that hero (a costume's before the hero's base
    /// package, which in most mods is the game's).</summary>
    (string File, string Mesh)? GhostModel(string baseFile)
    {
        string stem = baseFile[..^7];   // UC__MarvelPlayer_<Hero>
        foreach (var (f, path) in draft.Packages.Where(x => x.File.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.File.Equals(baseFile, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            if (File.Exists(path) && DodgeGhosts.CharacterModel(f, path) is { } mr) return (f, mr.Name);
        return null;
    }

    void UseGhostModel()
    {
        if (ghostsFile == null || ghostSlots == null) return;
        if (GhostModel(ghostsFile) is not { } m) { Dialog.Show(this, "The mod has no model of this hero to make the ghosts from: Add the costume's package (Packages tab) first.", "No Model", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        draft.DodgeGhosts.RemoveAll(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase));
        // every ghost in the combat idle, each leaning another way (Kurt, 2026-10-18), else the automatic poses
        string? combat = CombatIdle();
        int k = 0;
        foreach (var s in ghostSlots)
            draft.DodgeGhosts.Add(new DodgeGhostEntry { Package = ghostsFile, Ghost = s.Name, ModelPackage = m.File, ModelMesh = m.Mesh, KeepEffectSize = ghostsExact?.Checked == false,
                Anim = combat ?? "", Frame = 0, Lean = combat != null ? DodgeGhosts.Leans[k++ % DodgeGhosts.Leans.Length] : "", LeanDegrees = ghostsDegrees?.Value ?? DodgeGhosts.LeanDegrees });
        FillGhosts();
        BuildGhostPreview();
    }

    void UseGhostPose()
    {
        if (ghostsFile == null || ghostsGrid?.CurrentRow?.Tag is not DodgeGhosts.Slot s || powerPreview == null) return;
        if (powerPreview.CurrentPose is not { } pose || pose.Anim.Length == 0)
        { Dialog.Show(this, "Pick an animation in the preview first (the drop-down under it), and drag Frame to the moment you want.", "No Pose Showing", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.Ghost.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) is not { } e) return;
        e.Anim = pose.Anim; e.Frame = (float)Math.Round(pose.Frame, 1);
        FillGhosts();
        BuildGhostPreview();
    }

    /// <summary>The hero's combat idle (idle_combat, else an animation with idle and combat in its name, else idle), or null.</summary>
    string? CombatIdle()
    {
        var names = powerPreview?.AllAnimations.Select(a => a.Name).ToList() ?? [];
        return names.FirstOrDefault(n => n.Equals("idle_combat", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase) && n.Contains("combat", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Equals("idle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The Lean drop-down shows the selected ghost's lean.</summary>
    void ShowGhostLean()
    {
        if (ghostsFile == null || ghostsGrid?.CurrentRow?.Tag is not DodgeGhosts.Slot s) return;
        var e = draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.Ghost.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
        fillingGhosts = true;
        ghostsLean.SelectedIndex = e == null || e.Lean.Length == 0 ? 0 : Array.IndexOf(DodgeGhosts.Leans, e.Lean.ToLowerInvariant()) + 1;
        fillingGhosts = false;
    }

    /// <summary>The preview to the selected ghost's pose (its animation at its frame, paused), the dodge playing over it.</summary>
    void ShowGhostPose()
    {
        if (ghostsFile == null || ghostsGrid?.CurrentRow?.Tag is not DodgeGhosts.Slot s) return;
        var e = draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.Ghost.Equals(s.Name, StringComparison.OrdinalIgnoreCase));
        var (anim, frame) = e != null && e.Anim.Length > 0 ? (e.Anim, e.Frame) : (s.Anim, s.Frame);
        if (anim.Length == 0) { ghostsNote.Text = "That ghost is the rest pose (no animation)."; return; }
        ReplayDodge((anim, frame));
        ghostsNote.Text = $"Showing {anim} at frame {frame:0} (the pose of Ghost {ghostsGrid.CurrentRow.Index + 1}).";
    }

    void AutoGhostPose()
    {
        if (ghostsFile == null || ghostsGrid?.CurrentRow?.Tag is not DodgeGhosts.Slot s) return;
        if (draft.DodgeGhosts.FirstOrDefault(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase) && x.Ghost.Equals(s.Name, StringComparison.OrdinalIgnoreCase)) is not { } e) return;
        e.Anim = ""; e.Frame = 0; e.Lean = "";
        FillGhosts();
        BuildGhostPreview();
    }

    void GameGhosts()
    {
        if (ghostsFile == null) return;
        draft.DodgeGhosts.RemoveAll(x => x.Package.Equals(ghostsFile, StringComparison.OrdinalIgnoreCase));
        FillGhosts();
        var keep = powerPreview?.CurrentPose;
        if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
        ReplayDodge(keep is { Anim.Length: > 0 } ? keep : null);
    }

    /// <summary>The choices for a package, as text (a preview build is reused while they're the same).</summary>
    string GhostKey(string file) => string.Join(";", draft.DodgeGhosts.Where(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Ghost, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Ghost}={x.ModelPackage}|{x.ModelMesh}|{x.Anim}|{x.Frame:0.0}|{x.KeepEffectSize}|{x.Lean}|{x.LeanDegrees:0.#}"));

    /// <summary>The package before any ghosts were made (kept in the work folder, Model\ghost_base, with the mod): the one
    /// before an earlier recolor when there is one, else the mod's copy, else the game's (marked, so Game's Ghosts takes the
    /// package out of the mod again).</summary>
    string GhostBase(string file)
    {
        string dir = Path.Combine(WorkFolder, "ghost_base"), kept = Path.Combine(dir, file);
        if (File.Exists(kept)) return kept;
        Directory.CreateDirectory(dir);
        string colorBase = Path.Combine(WorkFolder, "color_base", file);
        var own = draft.Packages.FirstOrDefault(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        string? src = File.Exists(colorBase) ? colorBase : own.Source != null && File.Exists(own.Source) ? own.Source : null;
        if (src == null)
        {
            src = game != null ? new Originals(lib.DataFolder, game).Find(file) ?? StockFiles.For(game.Cooked, file) : null;
            if (src == null || !File.Exists(src)) throw new InvalidDataException($"{file}: no clean original of this package (the game's copy is changed)");
            File.WriteAllText(kept + ".stock", "the package came into the mod for its ghosts");
        }
        File.Copy(src, kept, true);
        return kept;
    }

    /// <summary>Builds the ghosts of <paramref name="file"/> as chosen (off the UI thread). Returns the built file.</summary>
    string BuildGhosts(string file, List<string> log)
    {
        if (game == null) throw new InvalidDataException("the game folder isn't set");
        var mine = draft.DodgeGhosts.Where(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        var first = mine[0];
        var pkgs = draft.Packages.ToList();
        string? mpath = pkgs.FirstOrDefault(x => x.File.Equals(first.ModelPackage, StringComparison.OrdinalIgnoreCase)).Source;
        if (first.ModelPackage.Equals(file, StringComparison.OrdinalIgnoreCase)) mpath = GhostBase(file);
        if (mpath == null || !File.Exists(mpath)) throw new InvalidDataException($"{first.ModelPackage} (the ghosts' model) isn't in the mod any more");
        var model = ModMeshes.List([(first.ModelPackage, mpath)]).FirstOrDefault(m => m.Name.Equals(first.ModelMesh, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{first.ModelMesh} isn't in {first.ModelPackage} any more");
        var slots = DodgeGhosts.Analyze(file, game.Cooked);
        var poses = mine.ToDictionary(x => x.Ghost, x => (x.Anim, x.Frame), StringComparer.OrdinalIgnoreCase);
        var leans = mine.Where(x => x.Lean.Length > 0).ToDictionary(x => x.Ghost, x => x.Lean, StringComparer.OrdinalIgnoreCase);
        byte[] bytes = DodgeGhosts.Build(GhostBase(file), file, model, pkgs, game.Cooked, slots, poses, log, keepEffectSize: mine.Any(x => x.KeepEffectSize), leans: leans, leanDegrees: mine[0].LeanDegrees);
        string dir = Path.Combine(WorkFolder, "ghost_build", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string outFile = Path.Combine(dir, file);
        File.WriteAllBytes(outFile, bytes);
        return outFile;
    }

    /// <summary>A preview build of the current choices (in the background); the preview reads it, and the dodge plays again.</summary>
    void BuildGhostPreview()
    {
        if (ghostsFile == null) return;
        string file = ghostsFile, key = GhostKey(file);
        int req = ++ghostBuildRequest;
        ghostsNote.Text = "Making the ghosts…";
        var log = new List<string>();
        Task.Run(() => BuildGhosts(file, log)).ContinueWith(t =>
        {
            if (IsDisposed || req != ghostBuildRequest) return;
            if (t.Status != TaskStatus.RanToCompletion) { ghostsNote.Text = "The ghosts couldn't be made: " + t.Exception?.GetBaseException().Message; return; }
            ghostBuilt[file] = (key, t.Result);
            FillGhosts();
            var keep = powerPreview?.CurrentPose;   // (the animation and frame showing stay, paused)
            if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
            ReplayDodge(keep is { Anim.Length: > 0 } ? keep : null);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- test hooks (--ghosts-test)
    internal async Task<List<string>> GhostsTestAsync(string anim, double fraction)
    {
        var lines = new List<string>();
        void Check(string what, bool ok) => lines.Add($"{(ok ? "PASS" : "FAIL")} {what}");
        int tab = Enumerable.Range(0, TabCount).First(i => TabTitle(i) == "Powers");
        SelectTab(tab);
        for (int i = 0; i < 600 && !powerList.Items.Cast<Fx.PowerList.Power>().Any(x => IsCharacterEntry(x.Prototype)); i++) await Task.Delay(100);
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => IsCharacterEntry(x.Prototype));
        Check("a Character entry", at >= 0);
        if (at < 0) return lines;
        await Task.Delay(4000);   // (the list fills again when the hero's powers are in)
        at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => IsCharacterEntry(x.Prototype));
        powerList.SelectedIndex = at;
        for (int i = 0; i < 900 && ghostSlots == null; i++) await Task.Delay(100);
        Check($"the game's ghosts read: {ghostSlots?.Count ?? 0} (selected {(powerList.SelectedItem as Fx.PowerList.Power)?.Name}; {ghostsNote.Text}; panel {ghostsBox?.Visible})", ghostSlots is { Count: > 0 });
        if (ghostSlots == null) return lines;
        UseGhostModel();
        Check("made from this model: " + string.Join(", ", draft.DodgeGhosts.Select(x => x.ModelMesh + " in " + x.ModelPackage).Distinct()), draft.DodgeGhosts.Count == ghostSlots.Count);
        for (int i = 0; i < 900 && !(ghostsFile != null && ghostBuilt.TryGetValue(ghostsFile, out var b) && b.Key == GhostKey(ghostsFile)); i++) await Task.Delay(100);
        Check("preview build (automatic poses)", ghostsFile != null && ghostBuilt.ContainsKey(ghostsFile));
        await Task.Delay(3000);
        bool played = powerPreview!.PlayAnimation(anim, false);
        await Task.Delay(1500);
        powerPreview.ScrubForTest(fraction);
        await Task.Delay(500);
        var pose = powerPreview.CurrentPose;
        Check($"the preview shows {anim} at frame {pose?.Frame:0.0}", played && pose?.Anim == anim);
        ghostsGrid!.CurrentCell = ghostsGrid.Rows[0].Cells[0];
        UseGhostPose();
        var e0 = draft.DodgeGhosts.First(x => x.Ghost.Equals(ghostSlots[0].Name, StringComparison.OrdinalIgnoreCase));
        Check($"ghost 1 takes it: {e0.Anim} frame {e0.Frame}", e0.Anim == anim);
        for (int i = 0; i < 900 && !(ghostBuilt.TryGetValue(ghostsFile!, out var b) && b.Key == GhostKey(ghostsFile!)); i++) await Task.Delay(100);
        Check("preview build (ghost 1's pose)", ghostBuilt[ghostsFile!].Key == GhostKey(ghostsFile!));
        await Task.Delay(5000);
        var after = powerPreview.CurrentPose;
        Check($"the preview stays on {anim} frame {after?.Frame:0.0}, paused ({(powerPreview.IsPlaying ? "playing" : "paused")})", after?.Anim == anim && Math.Abs((after?.Frame ?? -9) - e0.Frame) < 1.5f && !powerPreview.IsPlaying);
        lines.Add("    " + ghostsNote.Text);
        return lines;
    }
    internal void GameGhostsForTest() => GameGhosts();
    internal object? PowerPreviewForTest => powerPreview;
    /// <summary>Test (--keep-state-test): a hero power selected (the <paramref name="k"/>th), held at a pose, the Parts tab.</summary>
    internal async Task<PowersState?> SetPowersStateForTest(int k)
    {
        SelectTab(Enumerable.Range(0, TabCount).First(i => TabTitle(i) == "Powers"));
        for (int i = 0; i < 600 && !(powerPreview?.HeroPowersReady == true && powerList.Items.Count > k); i++) await Task.Delay(100);
        await Task.Delay(1500);
        powerList.SelectedIndex = k;
        await Task.Delay(3000);
        if (powerPreview?.AllAnimations.FirstOrDefault(a => a.Name.Contains("run", StringComparison.OrdinalIgnoreCase)) is { } a) powerPreview.HoldPose(a.Name, 9);
        powerSideTabs?.Select(1);
        await Task.Delay(1000);
        return CapturePowersState();
    }
    internal async Task<bool> SelectCharacterForTest()
    {
        SelectTab(Enumerable.Range(0, TabCount).First(i => TabTitle(i) == "Powers"));
        for (int i = 0; i < 600 && !powerList.Items.Cast<Fx.PowerList.Power>().Any(x => IsCharacterEntry(x.Prototype)); i++) await Task.Delay(100);
        await Task.Delay(4000);
        int at = powerList.Items.Cast<Fx.PowerList.Power>().ToList().FindIndex(x => IsCharacterEntry(x.Prototype));
        if (at < 0) return false;
        powerList.SelectedIndex = at;
        for (int i = 0; i < 900 && ghostSlots == null; i++) await Task.Delay(100);
        return ghostSlots != null;
    }
    internal string? GhostsFileForTest => ghostsFile;

    /// <summary>The dodge effect again in the preview, as its ▶ in the Parts list (after the package changed).</summary>
    void ReplayDodge((string Anim, float Frame)? keep = null)
    {
        if (partsGrid?.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PowerParts.Switch>().FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase)) is { } dodge)
            BeginInvoke(() => PlayPart(dodge, keep));
    }

    /// <summary>
    /// Save (before the power colors and parts, which apply on top): each package with ghosts chosen gets them (the preview's
    /// build when the choices are the same); one whose ghosts went back to the game's gets the package from before the ghosts
    /// (or leaves the mod when it came in for them). The kept copy before a recolor takes the ghosts too, so a recolor applies
    /// on top of them. Returns the work folder, or null.
    /// </summary>
    string? ApplyGhosts(List<string> log)
    {
        var before = editing?.Manifest.DodgeGhosts?.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var wanted = draft.DodgeGhosts.Select(x => x.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (before.Count == 0 && wanted.Count == 0) return null;
        foreach (string f in wanted)
        {
            string built = ghostBuilt.TryGetValue(f, out var b) && b.Key == GhostKey(f) && File.Exists(b.Path) ? b.Path : BuildGhosts(f, log);
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (f, built); else draft.Packages.Add((f, built));
            string colorBase = Path.Combine(WorkFolder, "color_base", f);
            if (File.Exists(colorBase)) File.Copy(built, colorBase, true);
            log.Add($"{f}: dodge ghosts made from the mod's model");
        }
        foreach (string f in before.Where(f => !wanted.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            string kept = Path.Combine(WorkFolder, "ghost_base", f);
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(kept + ".stock")) { if (k >= 0) draft.Packages.RemoveAt(k); log.Add($"{f}: the game's dodge ghosts again (the package is out of the mod)"); }
            else if (File.Exists(kept))
            {
                if (k >= 0) draft.Packages[k] = (f, kept); else draft.Packages.Add((f, kept));
                string colorBase = Path.Combine(WorkFolder, "color_base", f);
                if (File.Exists(colorBase)) File.Copy(kept, colorBase, true);
                log.Add($"{f}: the game's dodge ghosts again");
            }
            else log.Add($"{f}: the package from before the ghosts wasn't kept; its ghosts stay as they are");
        }
        return null;
    }
}
