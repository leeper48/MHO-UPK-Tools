namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Animations tab's Height section under the 3D view (Kurt, 2026-11-02; for travel powers: a changed animation's body
/// raised or lowered at chosen moments). The keys are the manifest's AnimSwapEntry.Heights; each change is applied in place to
/// the work copy (AnimSwap.ReHeight: the bones under the root move by the difference, the root never does: a root that moves
/// over time is root motion, which sank or blinked out heroes in game) and played from there.
/// </summary>
sealed partial class ModEditorView
{
    HeightStrip? heightStrip;
    Button? heightAdd, heightClear;
    readonly Label heightStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(8, 6, 0, 0) };
    System.Windows.Forms.Timer? heightTimer;
    int heightRequest;
    bool heightBusy, heightAgain;
    int heightBorrowsBuilt;

    // --- the Height gizmo (Kurt, 2026-11-02: lower and raise the character in the view): the move gizmo with only its Z arrow,
    // at the hips; a drag sets the key at the frame showing (live on the strip), letting go applies it (CommitHeights)
    Button? heightGizmo;
    static bool heightGizmoOn;
    (float At, List<(float At, float Z)> Keys, float Z0)? heightDrag;

    void ToggleHeightGizmo()
    {
        heightGizmoOn = !heightGizmoOn;
        if (heightGizmo != null) Ui.Lit(heightGizmo, heightGizmoOn);
        // (on: the props' gizmo gives way, both can't be dragged at once)
        HookAnimGizmo();
        animPreview?.Viewer3D?.View.Invalidate();
    }

    /// <summary>The strip's segment of the animation the 3D view plays (a chain's part), else -1.</summary>
    int PlayingSeg()
    {
        string? now = animPreview?.Viewer3D?.CurrentAnimation;
        return now == null ? -1 : heightParts.FindIndex(p => p.Name.Equals(now, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The segment showing in the 3D view made the strip's active one (the gizmo and Add Key at Frame key what shows);
    /// false when the view plays none of the strip's parts, or that part can't take keys.</summary>
    bool ActivatePlaying()
    {
        int s = PlayingSeg();
        if (s < 0 || heightStrip == null || s >= heightTargets.Count || heightTargets[s] == null) return false;
        heightStrip.SetActive(s);
        return true;
    }

    /// <summary>The Height gizmo where it is now (null: off, or the animation can't take keys).</summary>
    ModelView.TransformGizmo? HeightGizmoNow()
    {
        if (!heightGizmoOn || heightStrip == null || heightStrip.Message != null || heightBusy || PlayingSeg() is var ps && (ps < 0 || heightTargets[ps] == null) || animPreview?.Viewer3D is not { Animator: { } an }) return null;
        int b = an.BoneIndex("g_pelvis");
        if (b < 0) b = an.BoneIndex("g_spine01");
        if (b < 0) return null;
        System.Numerics.Vector3[] axes = [System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ];
        return new ModelView.TransformGizmo(an.BonePosition(b), axes, axes, 40) { Handles = [2] };
    }

    /// <summary>A drag on the Height gizmo's arrow: the key at the frame showing, moved by <paramref name="t"/> (live on the
    /// strip). False: not the Height gizmo's drag.</summary>
    bool HeightGizmoDrag(int axis, float t)
    {
        if (axis != 2 || heightStrip == null || animPreview?.Viewer3D is not { } p3) return false;
        if (heightDrag == null)
        {
            if (HeightGizmoNow() == null || !ActivatePlaying()) return false;
            p3.Pause();
            float at0 = p3.Fraction;
            var keys0 = heightStrip.Keys.ToList();
            heightDrag = (at0, keys0, AnimSwap.HeightAt(keys0, at0));
        }
        var (at, start, z0) = heightDrag.Value;
        bool fine = ModifierKeys.HasFlag(Keys.Shift);
        float z = fine ? MathF.Round((z0 + t) * 10) / 10 : MathF.Round(z0 + t);
        var keys = start.Where(k => Math.Abs(k.At - at) > 0.01f).ToList();
        keys.Add((at, z));
        heightStrip.SetKeys(keys);
        heightStatus.Text = $"Key at {at * 100:0} % · {(z >= 0 ? "+" : "")}{z:0.#} Units";
        p3.SetBodyLift(z - z0);   // (live: the body as the key will put it at this frame)
        return true;
    }

    /// <summary>The Height gizmo let go: the key applied. False: not the Height gizmo's drag.</summary>
    bool HeightGizmoReleased()
    {
        if (heightDrag == null) return false;
        heightDrag = null;
        heightLabel = "Gizmo";
        CommitHeights();
        // (the live lift off once the key is in: the reloaded animation has it; until then the body stays where it was dragged)
        void Off() { animLoaded -= Off; animPreview?.Viewer3D?.SetBodyLift(0); }
        if (heightBusy) animLoaded += Off; else Off();
        return true;
    }

    Control HeightSection()
    {
        heightStrip = new HeightStrip { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        heightStrip.Committed += CommitHeights;
        heightStrip.Dragging += (at, z) => heightStatus.Text = $"Key at {at * 100:0} % · {(z >= 0 ? "+" : "")}{z:0.#} Units";
        Ui.Tip(heightStrip, "Height keys for this changed animation: left to right is the animation, up raises the body (game units; the middle line is the animation as it is). Click to add a key, drag a key to move it (Shift: tenths), right-click a key to remove it.\n\nA power's start, loop and end show side by side, each as wide as it plays in game, with their own keys. The blue Feet line is the body's lowest point above the game's floor (the middle line): bring it to the middle line to put the feet on the floor. Chain in the 3D view plays the parts in a row.");
        heightAdd = Ui.FlatButton("Add Key at Frame", () => { if (animPreview?.Viewer3D is { } v && ActivatePlaying()) heightStrip.AddAt(v.Fraction); }, "A key where the animation is now (pause it on the frame first), at the height the curve has there.");
        heightClear = Ui.FlatButton("Clear Keys", () => { if (heightStrip.Keys.Count > 0) { heightStrip.SetKeys([]); heightLabel = "Clear Keys"; CommitHeights(); } },"Take every height key off this animation: it plays at its own height again.");
        heightGizmo = Ui.FlatButton("Height Gizmo", ToggleHeightGizmo, "A blue up arrow at the hips in the 3D view (every pane of the quad view): drag it up or down to raise or lower the body at the frame showing (Shift: tenths). Letting go puts a key there (or moves the one at that frame) and plays the animation from there. Pause on the frame first. Lit when it's on.");
        Icons.Make(heightGizmo, "Height Gizmo", Icons.Gizmo, S);
        Ui.Lit(heightGizmo, heightGizmoOn);
        var caption = new Label { Text = "HEIGHT", AutoSize = true, Font = Ui.Bold(9f), Padding = new Padding(0, 6, 8, 0) };
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        heightUndoButton = Ui.FlatButton("Undo", () => _ = HeightUndo(), "Nothing to undo.");
        heightRedoButton = Ui.FlatButton("Redo", () => _ = HeightRedo(), "Nothing to redo.");
        heightHistoryButton = Ui.FlatButton("History", () => ShowHeightHistory(heightHistoryButton!), "Every height-key step by name, from when this mod was opened (the strip, the gizmo, Add Key at Frame, Clear Keys). Click one to go back or forward to it.");
        Icons.Make(heightUndoButton, "Undo", Icons.Undo, S); Icons.Make(heightRedoButton, "Redo", Icons.Redo, S); Icons.Make(heightHistoryButton, "History", Icons.History, S);
        UpdateHeightButtons();
        bar.Controls.AddRange([caption, heightGizmo, heightAdd, heightClear, heightUndoButton, heightRedoButton, heightHistoryButton, heightStatus]);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 6, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(110 * S)));
        t.Controls.Add(bar, 0, 0);
        t.Controls.Add(HeightClimbRow(), 0, 1);
        t.Controls.Add(heightStrip, 0, 2);
        heightTimer = new System.Windows.Forms.Timer { Interval = 50 };
        heightTimer.Tick += (_, _) =>
        {
            if (heightStrip == null || !heightStrip.Visible) return;
            int seg = PlayingSeg();
            heightStrip.Playhead = animPreview?.Viewer3D is { Playing: not null } v && seg >= 0 ? (seg, v.Fraction) : null;
            if (heightFeetPending && animPreview?.Viewer3D?.Mesh != null) { heightFeetPending = false; MeasureFeet(heightRequest); }
        };
        heightTimer.Start();
        Disposed += (_, _) => heightTimer?.Dispose();
        heightSection = t;
        ShowHeights();
        return t;
    }

    // --- Climb Speed beside the keys (Kurt, 2026-11-02: one place for a travel power's height, not a slider on the Powers tab
    // and keys here): how fast the game lifts the hero for a borrowed high-flying power (PowerBorrowEntry.ClimbSpeed, written
    // into the hero's class); the only fix for the dip when the loop takes over, which keys can't reach (the loop repeats)
    readonly FlowLayoutPanel heightClimb = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Visible = false, Margin = Padding.Empty };
    LightSlider? heightClimbSlider;
    Button? heightLevel, heightGameSpeed;
    readonly Label heightClimbNote = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(8, 6, 0, 0) };
    Control? heightSection;

    Control HeightClimbRow()
    {
        heightClimbSlider = new LightSlider { Label = "Climb Speed", Min = 50, Max = 1500, Step = 10, Format = v => $"{v:0} / s", Width = (int)(300 * S), Height = (int)(28 * S) };
        Ui.Tip(heightClimbSlider, "How fast the game lifts the hero to the flying height once the power applies (units a second; the hero's own, from the game, until you change it; double-click: the game's). The borrowed start is lowered to match, and the Feet line and the 3D view's floor follow it. Fast enough to be fully up when the loop starts, the loop begins on the floor; slower, it starts low and catches up: keys can't fix that, because the loop repeats.\n\nIt's set on the hero, so while the mod is on it applies to every flying power of his, in every costume.");
        heightClimbSlider.ValueChanged += () => { if (HeightBorrow() is { } e) heightClimbNote.Text = ClimbNoteText(e, heightClimbSlider.Value); };
        heightClimbSlider.Committed += () => SetClimbSpeed(heightClimbSlider.Value);
        heightLevel = Ui.FlatButton("Stabilize Ground Contact", () =>
        {
            if (HeightBorrow() is { Climb: { } c } && LevelSpeed(c) is float need) SetClimbSpeed(need);
        }, "Sets the slowest climb speed that has the hero fully up when the loop starts, so the loop begins with his feet on the floor instead of dipping under it as it takes over. Then key the body for the look.");
        Icons.Make(heightLevel, "Stabilize Ground Contact", Icons.GroundContact, S);
        heightGameSpeed = Ui.FlatButton("Game's Climb Speed", () => { if (HeightBorrow() is { Climb: { } c }) SetClimbSpeed(c.OwnSpeed); }, "Puts the climb speed back to the hero's own, as the game has it (double-click the slider does the same).");
        Icons.Make(heightGameSpeed, "Game's Climb Speed", Icons.Reset, S);
        heightEnd = new DropDown { Width = (int)(260 * S) };
        heightEnd.Items.AddRange(["End: Nothing Borrowed", "End: The Donor's Loop, Landing"]);
        heightEnd.SelectedIndexChanged += (_, _) =>
        {
            if (heightEndShowing || borrowBuilding || HeightBorrow() is not { } e) return;
            string? want = heightEnd.SelectedIndex == 1 ? PowerBorrowEntry.EndLoop : null;
            if (want == e.End) return;
            e.End = want;
            heightClimbNote.Text = "Building the borrowed look with the new end…";
            RebuildBorrows();
        };
        Ui.Tip(heightEnd, "What the power ends with when the borrowed look has no end animation of its own (Cap's run on Hawkeye's Sky-Cycle).\n\nNothing Borrowed: the game plays what the power has (the Sky-Cycle's own end).\nThe Donor's Loop, Landing: the borrowed loop plays once more as the end, lowered by the game's own landing (down from the flying height at the hero's landing speed), so the feet stay on the floor; it gets an End part on the strip for keys.");
        heightClimb.Controls.AddRange([heightClimbSlider, heightLevel, heightGameSpeed, heightEnd, heightClimbNote]);
        return heightClimb;
    }

    DropDown? heightEnd;
    bool heightEndShowing;

    /// <summary>The climb speed that has the hero fully up as the loop starts (rounded up to tens), else null (no wind-up after the power applies).</summary>
    static float? LevelSpeed((float OwnSpeed, float ApplyAt, float LoopAt, float Height, float Fall) c)
        => c.LoopAt > c.ApplyAt + 0.01f ? MathF.Ceiling(c.Height / (c.LoopAt - c.ApplyAt) / 10) * 10 : null;

    /// <summary>When the hero is fully up at <paramref name="speed"/>, against when the loop starts (the Height panel and the Powers tab).</summary>
    static string ClimbNoteText(PowerBorrowEntry e, float speed)
    {
        if (e.Climb is not { } c) return "";
        float up = c.ApplyAt + c.Height / Math.Max(1, speed);
        return $"Fully up {up:0.00} s after activation; the loop starts at {c.LoopAt:0.00} s"
            + (up <= c.LoopAt + 0.005f ? " (it begins on the floor)." : $" (it begins {c.Height - PowerBorrow.LiftAt(c.LoopAt, c.ApplyAt, speed, c.Height):0} low).")
            + (LevelSpeed(c) is float need ? $" Level at {need:0} or more." : "") + $" The game's: {c.OwnSpeed:0}.";
    }

    /// <summary>The borrow's climb speed set (null when it's the hero's own) and the borrows built again.</summary>
    void SetClimbSpeed(float v)
    {
        if (borrowBuilding || HeightBorrow() is not { Climb: { } c } e) return;
        float? want = Math.Abs(v - c.OwnSpeed) < 0.5f ? null : v;
        if (want == e.ClimbSpeed) { ShowHeightClimb(); return; }
        e.ClimbSpeed = want;
        heightClimbNote.Text = "Building the borrowed look at the new climb speed…";
        RebuildBorrows();
    }

    /// <summary>The climb row shown for a borrowed high-flying power, with its speed and note.</summary>
    void ShowHeightClimb()
    {
        var e = HeightBorrow();
        heightClimb.Visible = e != null;
        if (heightSection != null) heightSection.Height = (int)((e != null ? 196 : 160) * S);
        if (e?.Climb is not { } c || heightClimbSlider == null) return;
        heightClimbSlider.Home = () => c.OwnSpeed; heightClimbSlider.Mark = c.OwnSpeed;
        heightClimbSlider.Value = e.ClimbNow;
        heightClimbSlider.Enabled = heightLevel!.Enabled = heightGameSpeed!.Enabled = !borrowBuilding;
        // the End choice: a borrowed look whose chain has no end of its own (else the one it chose)
        if (heightEnd != null)
        {
            heightEnd.Visible = e.End != null || e.EndChoosable || !heightParts.Any(p => p.Name.EndsWith("_end", StringComparison.OrdinalIgnoreCase));
            heightEndShowing = true;
            try { heightEnd.SelectedIndex = e.End == PowerBorrowEntry.EndLoop ? 1 : 0; } finally { heightEndShowing = false; }
            heightEnd.Enabled = !borrowBuilding;
        }
        string note = ClimbNoteText(e, e.ClimbNow);
        // the loop's first frame as measured (the Feet line): what the handover looks like in game
        int loop = heightParts.FindIndex(p => p.Name.EndsWith("_loop", StringComparison.OrdinalIgnoreCase));
        if (loop >= 0 && heightStrip?.FeetAt(loop, 0) is float f) note += $" Feet at the handover: {(f >= 0 ? "+" : "")}{f:0}.";
        heightClimbNote.Text = note;
    }

    /// <summary>What an animation's height keys belong to: an Animations-tab change (its AnimSwapEntry) or a borrowed power's
    /// animation (the borrow, rebuilt on every Powers-tab change and on Save, so its keys live on the borrow and the build
    /// applies them); <see cref="File"/> = the package holding the animation (a borrow's: the hero's base package).</summary>
    sealed record HeightTarget(string File, string Slot, AnimSwapEntry? Swap, PowerBorrowEntry? Borrow)
    {
        public List<(float At, float Z)> Keys => Swap?.HeightKeys ?? Borrow?.HeightKeys(Slot) ?? [];
    }

    /// <summary>The target of <paramref name="a"/>'s keys, else null with <paramref name="why"/>.</summary>
    HeightTarget? HeightTargetOf(CostumeAnims.Anim? a, out string why)
    {
        why = "";
        if (a == null) { why = "Pick an animation in the list."; return null; }
        if (IsSwapped(a) && AnimFile is string file
            && draft.AnimSwaps.FirstOrDefault(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && x.Slot.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) is { } sw)
            return new(file, a.Name, sw, null);
        // a borrowed power's animation: in a set the borrow made (…_on_<hero class>), in a package the borrows built
        if (a.From.Path.Split('.')[^1].Contains("_on_", StringComparison.OrdinalIgnoreCase) && a.From.File is string from && draft.PowerBorrows.Count > 0)
        {
            string pf = Path.GetFileName(from);
            var mine = draft.PowerBorrows.Where(b => b.Packages.Contains(pf, StringComparer.OrdinalIgnoreCase)).ToList();
            var b = mine.LastOrDefault(x => x.Slots?.Contains(a.Name, StringComparer.OrdinalIgnoreCase) == true)
                    ?? (mine.Count == 1 && mine[0].Slots == null ? mine[0] : null);
            if (b != null) return new(pf, a.Name, null, b);
            if (mine.Any(x => x.Slots == null))
            {
                // (borrows saved before 0.60.292 don't list their animations: built once now, as the Powers tab and Save do)
                if (!borrowBuilding && heightBorrowsBuilt++ == 0) RebuildBorrows();
                why = "Building the borrowed looks to find this animation's power: one moment.";
                return null;
            }
        }
        why = IsSwapped(a) || a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase) ? "This animation was changed in a way this strip can't follow." : "Height keys go on changed animations: Change Animation first, or borrow a power's look on the Powers tab.";
        return null;
    }

    /// <summary>The strip's parts (the selected animation, or every part of its power's chain) and what each one's keys go on
    /// (null: that part can't take keys).</summary>
    List<CostumeAnims.Anim> heightParts = [];
    List<HeightTarget?> heightTargets = [];
    /// <summary>The Feet lines wait for the 3D view's model.</summary>
    bool heightFeetPending;

    static readonly string[] ChainSuffixes = ["_start", "_loop", "_end"];
    static readonly string[] ChainLabels = ["Start", "Loop", "End"];

    /// <summary>The parts of <paramref name="a"/>'s chain in the list (its _start, _loop, _end), else just <paramref name="a"/>.</summary>
    List<CostumeAnims.Anim> ChainRows(CostumeAnims.Anim a)
    {
        if (animData == null || ChainSuffixes.FirstOrDefault(s => a.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) is not string suffix) return [a];
        string stem = a.Name[..^suffix.Length];
        var all = a.From.Alias != null ? animData.FormAnims : animData.Anims;
        var parts = ChainSuffixes.Select(s => s.Equals(suffix, StringComparison.OrdinalIgnoreCase) ? a : all.FirstOrDefault(x => x.Name.Equals(stem + s, StringComparison.OrdinalIgnoreCase))).OfType<CostumeAnims.Anim>().ToList();
        return parts.Count > 1 ? parts : [a];
    }

    /// <summary>The borrow of the strip's parts with a lift (a high-flying power's), else null.</summary>
    PowerBorrowEntry? HeightBorrow() => heightTargets.FirstOrDefault(t => t?.Borrow?.Climb != null)?.Borrow;

    /// <summary>The strip for the selected animation and the rest of its chain: their keys, or why they can't have any.</summary>
    void ShowHeights()
    {
        if (heightStrip == null || heightBusy) return;
        int req = ++heightRequest;
        var a = SelectedAnim;
        void Off(string why) { heightParts = []; heightTargets = []; heightStrip.SetSegments([], [], 0); heightStrip.Message = why; heightAdd!.Enabled = heightClear!.Enabled = false; ShowHeightClimb(); }
        heightStatus.Text = "";
        if (a == null) { Off("Pick an animation in the list."); return; }
        var parts = ChainRows(a);
        string whyNot = "";
        var targets = parts.Select(p => { var t = HeightTargetOf(p, out string w); if (ReferenceEquals(p, a)) whyNot = w; return t; }).ToList();
        if (targets.All(x => x == null)) { Off(whyNot.Length > 0 ? whyNot : "These animations can't take height keys."); return; }
        if (borrowBuilding) { Off("The borrowed looks are being built: one moment."); return; }
        Off("Checking…");
        var paths = targets.Select(x => x != null ? DraftPath(x.File) : null).ToList();
        // a borrow's climb (known after a build; else read from the game data, as its build does)
        var unclimbed = targets.Select(x => x?.Borrow).OfType<PowerBorrowEntry>().Distinct().Where(b => b.Climb == null).ToList();
        var g = game;
        Task.Run(() =>
        {
            if (unclimbed.Count > 0 && g != null)
                try
                {
                    powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
                    foreach (var b in unclimbed) b.Climb ??= PowerBorrow.ClimbOf(powerDb, g.Cooked, b);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { }
            return targets.Select((x, i) =>
            {
                if (x == null) return "it isn't changed";
                if (paths[i] is not string path) return "the package isn't found";
                try { AnimSwap.ReHeight(path, x.Slot, [], [], null, out string why, check: true); return why; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException) { return ex.Message; }
            }).ToList();
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != heightRequest || heightStrip == null) return;
            var whys = t.Status == TaskStatus.RanToCompletion ? t.Result : [.. targets.Select(_ => "it doesn't read")];
            for (int i = 0; i < targets.Count; i++) if (whys[i].Length > 0) targets[i] = null;
            int sel = parts.IndexOf(a);
            if (targets[sel] == null && parts.Count == 1) { Off($"This animation can't take height keys: {whys[sel]}."); return; }
            if (targets.All(x => x == null)) { Off($"These animations can't take height keys: {whys[sel]}."); return; }
            heightParts = parts; heightTargets = targets;
            bool chain = parts.Count > 1;
            var climb = HeightBorrow()?.Climb;
            var segs = parts.Select((p, i) => new HeightStrip.Segment(
                chain ? ChainLabels[Array.FindIndex(ChainSuffixes, s => p.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))] : p.Name,
                PartSeconds(p, climb), targets[i] == null ? $"Can't take keys: {whys[i]}" : null)).ToList();
            heightStrip.Message = null;
            heightStrip.SetSegments(segs, [.. targets.Select(x => x?.Keys ?? [])], targets[sel] != null ? sel : targets.FindIndex(x => x != null));
            heightAdd!.Enabled = heightClear!.Enabled = true;
            var borrow = targets.FirstOrDefault(x => x?.Borrow != null)?.Borrow;
            if (borrow != null) { heightStatus.Text = chain ? "Borrowed Power · Its Start, Loop and End" : "Borrowed Power"; Ui.Tip(heightStatus, $"These animations come with the borrowed look of {borrow.DonorName}. Their keys are kept with the borrow and made again whenever the borrows are rebuilt (a change on the Powers tab, Climb Speed, Save)."); }
            ShowHeightClimb();
            heightFeetPending = true;   // (measured once the 3D view has the model: the timer)
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>How long a part plays in game: a high-flying power's start over its wind-up (the client re-times it), else its own length.</summary>
    static float PartSeconds(CostumeAnims.Anim p, (float OwnSpeed, float ApplyAt, float LoopAt, float Height, float Fall)? climb)
    {
        if (climb is { LoopAt: > 0 } c && p.Name.EndsWith("_start", StringComparison.OrdinalIgnoreCase)) return c.LoopAt;
        return ModAnimations.Load(p.Ref) is { } ba ? Math.Max(0.05f, MeshAnimator.Span(ba).Seconds) : 1;
    }

    /// <summary>
    /// Each part's Feet line in the background: the 3D view's model posed through the part as the game plays it, its lowest
    /// point measured against the game's floor there (PreviewProps.FloorAt, the rule the 3D view draws the floor by: a
    /// high-flying power's climb at the Climb Speed, the loop's first play after its start carrying the climb on, the end
    /// coming down).
    /// </summary>
    void MeasureFeet(int req)
    {
        if (heightStrip == null || animPreview?.Viewer3D?.Mesh is not { } mesh) return;
        var parts = heightParts.ToList();
        var keysNow = heightTargets.Select(x => x?.Keys ?? []).ToList();
        var b = HeightBorrow();
        var climb = b?.Climb;
        float rise = b?.ClimbNow ?? 0;
        bool hasStart = parts.Any(p => p.Name.EndsWith("_start", StringComparison.OrdinalIgnoreCase));
        Task.Run(() =>
        {
            var an = new MeshAnimator(mesh.Bones, mesh.Positions, mesh.Normals, mesh.Influences);
            return parts.Select(p =>
            {
                if (ModAnimations.Load(p.Ref) is not { } ba) return null;
                var (frames, secs) = MeshAnimator.Span(ba);
                int phase = p.Name.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ? 1 : p.Name.EndsWith("_end", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
                bool chained = climb != null && ChainSuffixes.Any(s => p.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                float playSecs = PartSeconds(p, climb);
                const int n = 48;
                var line = new float[n + 1];
                for (int k = 0; k <= n; k++)
                {
                    an.Pose(ba, frames * k / n);
                    float lo = float.MaxValue;
                    foreach (var v in an.Positions) lo = Math.Min(lo, v.Z);
                    float floor = chained && climb is { } c ? PreviewProps.FloorAt(phase, playSecs * k / n, hasStart, c.Height, rise, c.Fall, c.ApplyAt, c.LoopAt) : 0;
                    line[k] = lo - floor;
                }
                return line;
            }).ToList();
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != heightRequest || heightStrip == null || t.Status != TaskStatus.RanToCompletion) return;
            for (int i = 0; i < t.Result.Count; i++) { heightStrip.SetFeetBase(i, keysNow[i]); heightStrip.SetFeet(i, t.Result[i]); }
            ShowHeightClimb();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The next commit's step name, when it isn't the strip's own (the gizmo, Clear Keys).</summary>
    string? heightLabel;

    /// <summary>The strip's active part's keys put on its animation (SetHeights), as an Undo step.</summary>
    async void CommitHeights()
    {
        if (heightStrip == null) return;
        if (heightBusy) { heightAgain = true; return; }
        if (heightStrip.Active >= heightTargets.Count || heightTargets[heightStrip.Active] is not { } target || borrowBuilding) return;
        string label = heightLabel ?? heightStrip.LastAction;
        heightLabel = null;
        await SetHeights(target, heightStrip.Keys.ToList(), label);
        if (heightAgain) { heightAgain = false; CommitHeights(); }
    }

    // --- Undo / Redo / History of the height keys (Kurt, 2026-11-02): every commit is a step with the keys before and after;
    // going back applies the keys it had (SetHeights works from whatever keys the animation has now, so a package another tab
    // rebuilt since is fine as long as the animation is still there)
    sealed record HeightStep(string Label, string ListFile, string Slot, List<(float At, float Z)> Was, List<(float At, float Z)> Is);
    readonly Stack<HeightStep> heightUndo = new(), heightRedo = new();
    Button? heightUndoButton, heightRedoButton, heightHistoryButton;

    void UpdateHeightButtons()
    {
        if (heightUndoButton != null)
        {
            heightUndoButton.Enabled = heightUndo.Count > 0;
            Ui.SetTipText(heightUndoButton, Ui.Titled("Undo", heightUndo.Count > 0 ? $"{heightUndo.Peek().Label}  (Ctrl+Z)" : "Nothing to undo. Every change to the height keys (the strip, the gizmo, Add Key at Frame, Clear Keys) can be taken back (Ctrl+Z)."));
        }
        if (heightRedoButton != null)
        {
            heightRedoButton.Enabled = heightRedo.Count > 0;
            Ui.SetTipText(heightRedoButton, Ui.Titled("Redo", heightRedo.Count > 0 ? $"{heightRedo.Peek().Label}  (Ctrl+Y)" : "Nothing to redo (Ctrl+Y)."));
        }
    }

    Task HeightUndo() => HeightGo(undo: true);
    Task HeightRedo() => HeightGo(undo: false);

    /// <summary>The last step undone (or redone): its animation selected and given the keys it had before (after).</summary>
    async Task HeightGo(bool undo)
    {
        var stack = undo ? heightUndo : heightRedo;
        if (heightBusy || borrowBuilding || stack.Count == 0) return;
        var step = stack.Peek();
        if (!string.Equals(AnimFile, step.ListFile, StringComparison.OrdinalIgnoreCase))
        {
            Dialog.Show(this, $"\"{step.Label}\" was made with {step.ListFile} picked above the list: pick it again, then {(undo ? "undo" : "redo")}.", undo ? "Not Undone" : "Not Redone", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        RestoreRow(step.Slot);
        if (HeightTargetOf(SelectedAnim is { } sa && sa.Name.Equals(step.Slot, StringComparison.OrdinalIgnoreCase) ? sa : null, out string why) is not { } target)
        {
            Dialog.Show(this, $"\"{step.Label}\" can't be {(undo ? "undone" : "redone")}: {(why.Length > 0 ? why : "its animation isn't in the list now.")}", undo ? "Not Undone" : "Not Redone", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!await SetHeights(target, undo ? step.Was : step.Is, null)) return;
        (undo ? heightRedo : heightUndo).Push(stack.Pop());
        UpdateHeightButtons();
        heightStatus.Text = (undo ? "Undone: " : "Redone: ") + step.Label;
    }

    /// <summary>History: every step (the oldest first, the one now marked); a pick undoes or redoes to it.</summary>
    void ShowHeightHistory(Button at)
    {
        var names = new List<string> { "Start" };
        names.AddRange(heightUndo.Reverse().Select(s => s.Label));
        int now = names.Count - 1;
        names.AddRange(heightRedo.Select(s => s.Label));
        DropList.Show(at, at.RectangleToScreen(at.ClientRectangle),
            [.. names.Select((t, i) => (i == now ? "▶ " : "    ") + t + (i > now ? " (undone)" : ""))], now, i => _ = HeightJump(i), i => i > now ? Ui.Subtle : i == now ? Ui.Accent : null);
    }

    async Task HeightJump(int pick)
    {
        while (heightUndo.Count > pick) { int was = heightUndo.Count; await HeightUndo(); if (heightUndo.Count == was) return; }
        while (heightUndo.Count < pick && heightRedo.Count > 0) { int was = heightRedo.Count; await HeightRedo(); if (heightRedo.Count == was) return; }
    }

    /// <summary>
    /// <paramref name="to"/> put on the target's animation in the work copy (the difference from the keys it has), then played
    /// from there at the same moment; a borrow's keys also go on the borrow, so its next build makes them again. With a
    /// <paramref name="label"/>, an Undo step. False when it couldn't be done (said in a message).
    /// </summary>
    async Task<bool> SetHeights(HeightTarget target, List<(float At, float Z)> to, string? label)
    {
        if (heightStrip == null || DraftPath(target.File) is not string path) return false;
        heightBusy = true;
        string slot = target.Slot, file = target.File;
        var from = target.Keys;
        heightStatus.Text = "Setting the Height…";
        var v = animPreview?.Viewer3D;
        float at = v?.Fraction ?? 0;
        bool playing = v?.IsPlaying == true;
        byte[]? bytes; string why = "";
        try
        {
            bytes = await Task.Run(() => AnimSwap.ReHeight(path, slot, from, to, null, out why));
            // (a borrow's package is built from the kept one before the borrows: nothing to change there)
            if (target.Swap != null) OnBorrowBase(file, b => AnimSwap.ReHeight(b, slot, from, to, null, out _));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException) { bytes = null; why = ex.Message; }
        heightBusy = false;
        if (IsDisposed) return false;
        if (bytes == null)
        {
            heightStatus.Text = "";
            heightStrip.SetKeys(from);
            animPreview?.Viewer3D?.SetBodyLift(0);   // (a gizmo drag's live lift)
            Dialog.Show(this, $"\"{slot}\" couldn't take these height keys: {why}", "Height Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        UseWorkCopy(file, bytes);
        if (label != null && !from.SequenceEqual(to) && AnimFile is string listFile)
        {
            heightUndo.Push(new HeightStep($"{label}: {slot}", listFile, slot, from, to));
            heightRedo.Clear();
            UpdateHeightButtons();
        }
        List<float[]>? saved = to.Count > 0 ? [.. to.Select(x => new[] { x.At, x.Z })] : null;
        if (target.Swap is { } entry)
        {
            int k = draft.AnimSwaps.IndexOf(entry);
            // a new entry (the draft's list shares its entries with the mod's loaded manifest until Save)
            var fresh = new AnimSwapEntry { Package = entry.Package, Slot = entry.Slot, Donor = entry.Donor, Animation = entry.Animation, Title = entry.Title, Heights = saved };
            if (k >= 0) draft.AnimSwaps[k] = fresh;
        }
        else if (target.Borrow is { } b)
        {
            b.Heights ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (string old in b.Heights.Keys.Where(x => x.Equals(slot, StringComparison.OrdinalIgnoreCase)).ToList()) b.Heights.Remove(old);
            if (saved != null) b.Heights[slot] = saved;
            if (b.Heights.Count == 0) b.Heights = null;
        }
        heightStatus.Text = to.Count == 0 ? "No Height Keys" : $"{to.Count} Height Key(s) · Not Saved Yet";
        // the list read again from the work copy, then the animation where it was
        var loaded = new TaskCompletionSource();
        ReloadAt(slot);
        void Back()
        {
            animLoaded -= Back;
            loaded.TrySetResult();
            if (animPreview?.Viewer3D is not { } p3 || p3.Playing == null) return;
            p3.Time = at * p3.Seconds;
            if (!playing) p3.Pause();
        }
        animLoaded += Back;
        await Task.WhenAny(loaded.Task, Task.Delay(20000));   // (Undo / History go on from the reloaded list)
        return true;
    }

    /// <summary>Test hook: <paramref name="slot"/> picked, then the 3D view's floor (GroundZ) at 0.1 s steps of its first play.</summary>
    public async Task<string> FloorTest(string slot)
    {
        if (!await WaitAnims()) return "no animations";
        RestoreRow(slot);
        await Task.Delay(6000);
        if (animPreview?.Viewer3D is not { } p3 || p3.Playing == null) return "nothing playing";
        p3.Pause();
        var line = new List<string>();
        for (float t = 0; t <= Math.Min(0.9f, p3.Seconds) + 1e-3f; t += 0.1f) { p3.Time = t; line.Add($"{t:0.0}s {p3.View.GroundZ:0}"); }
        return $"{slot} ({p3.Seconds:0.00} s, playing {p3.CurrentAnimation}): floor " + string.Join(" · ", line);
    }

    /// <summary>Test hook: Chain on, <paramref name="start"/> played from its first frame into the loop, then paused in the
    /// loop's first play (the floor at 0.1 s steps there); then on into the end, and what plays after it.</summary>
    public async Task<string> ChainFloorTest(string start)
    {
        if (!await WaitAnims()) return "no animations";
        bool was = PreviewViews.Chain;
        int wasLoops = PreviewViews.ChainLoops;
        PreviewViews.Chain = false;
        try
        {
            RestoreRow(start);
            await Task.Delay(6000);
            if (animPreview?.Viewer3D is not { } p3 || p3.Playing == null) return "nothing playing";
            p3.Pause(); p3.Time = 0;
            PreviewViews.Chain = true;
            PreviewViews.ChainLoops = 1;
            p3.Play();
            for (int i = 0; i < 40 && p3.CurrentAnimation?.EndsWith("_start", StringComparison.OrdinalIgnoreCase) == true; i++) await Task.Delay(50);
            p3.Pause();
            var line = new List<string>();
            for (float t = 0; t <= Math.Min(0.6f, p3.Seconds) + 1e-3f; t += 0.1f) { p3.Time = t; line.Add($"{t:0.0}s {p3.View.GroundZ:0}"); }
            string loopPart = $"after {start}: playing {p3.CurrentAnimation}; its first play's floor " + string.Join(" · ", line) + " | " + p3.FloorStateForTest;
            // on into the end (the loop plays once)
            p3.Time = 0; p3.Play();
            for (int i = 0; i < 100 && p3.CurrentAnimation?.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) == true; i++) await Task.Delay(50);
            await Task.Delay(100);
            string endPart = $"then {p3.CurrentAnimation}";
            if (p3.CurrentAnimation?.EndsWith("_end", StringComparison.OrdinalIgnoreCase) == true)
            {
                p3.Pause();
                var fl = new List<string>();
                for (float t = 0; t <= Math.Min(0.4f, p3.Seconds) + 1e-3f; t += 0.1f) { p3.Time = t; fl.Add($"{t:0.0}s {p3.View.GroundZ:0}"); }
                endPart += ": floor " + string.Join(" · ", fl);
                p3.Time = 0; p3.Play();
            }
            for (int i = 0; i < 100 && p3.CurrentAnimation?.EndsWith("_end", StringComparison.OrdinalIgnoreCase) == true; i++) await Task.Delay(50);
            await Task.Delay(150);
            return $"{loopPart} || {endPart} || after the end: {p3.CurrentAnimation} {(p3.IsPlaying ? "playing" : "paused")}";
        }
        finally { PreviewViews.Chain = was; PreviewViews.ChainLoops = wasLoops; }
    }

    /// <summary>Test hook (Kurt, 2026-11-02: "not playing" with Chain on): Chain and Loop on, <paramref name="slot"/>'s row
    /// clicked as a user does; what plays every half second, then the same after ▶ from a pause.</summary>
    public async Task<string> ChainPlayTest(string slot)
    {
        if (!await WaitAnims() || animPreview?.Viewer3D is not { } p3) return "no animations";
        bool was = PreviewViews.Chain, wasQuad = PreviewViews.Quad;
        PreviewViews.Chain = true;
        if (Environment.GetEnvironmentVariable("MHO_TEST_QUAD") == "1") { PreviewViews.Quad = true; p3.View.Quad = true; p3.View.ShowBloom = true; }
        try
        {
            // (another row first, then the start: the Height panel changes size under the view, as a click does)
            if (animData?.Anims.FirstOrDefault(x => !ChainSuffixes.Any(s => x.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) is { } other) { RestoreRow(other.Name); await Task.Delay(2500); }
            RestoreRow(slot);
            var lines = new List<string>();
            for (int i = 0; i < 12; i++) { await Task.Delay(500); lines.Add($"{p3.CurrentAnimation} {(p3.IsPlaying ? "▶" : "❚❚")} {p3.Time:0.00}/{p3.Seconds:0.00}"); }
            p3.Pause(); p3.Time = 0;
            p3.TogglePlayback();
            for (int i = 0; i < 6; i++) { await Task.Delay(500); lines.Add($"after ▶: {p3.CurrentAnimation} {(p3.IsPlaying ? "▶" : "❚❚")} {p3.Time:0.00}/{p3.Seconds:0.00}"); }
            return string.Join(" | ", lines);
        }
        finally { PreviewViews.Chain = was; PreviewViews.Quad = wasQuad; }
    }

    /// <summary>Test hook: <paramref name="slot"/> picked and paused, then ▶ ▶ ◀ beside the frame slider; what shows after each.</summary>
    public async Task<string> PartStepTest(string slot)
    {
        if (!await WaitAnims() || animPreview?.Viewer3D is not { } p3) return "no animations";
        RestoreRow(slot);
        await Task.Delay(5000);
        p3.Pause(); p3.Time = 0;
        var lines = new List<string> { $"{p3.CurrentAnimation} paused" };
        foreach (int by in new[] { 1, 1, -1 }) lines.Add((by > 0 ? "▶: " : "◀: ") + await p3.StepPartForTest(by));
        return string.Join(" | ", lines);
    }

    /// <summary>Test hook: <paramref name="slot"/> picked; once its Feet lines are measured, the strip's parts (label, seconds,
    /// why not), the active one, the Feet line at 0 / 25 / 50 / 75 / 100 % of each, and the climb row's note.</summary>
    public async Task<string> HeightChainTest(string slot)
    {
        if (!await WaitAnims()) return "no animations";
        RestoreRow(slot);
        await Task.Delay(1500);   // (the strip of the row picked before has gone)
        if (heightStrip == null) return "no strip";
        for (int i = 0; i < 150 && (heightStrip.Message != null || heightParts.Count == 0 || heightStrip.FeetAt(0, 0) == null && heightStrip.FeetAt(heightStrip.SegmentCount - 1, 0) == null); i++) await Task.Delay(200);
        if (heightStrip.Message != null) return "strip off: " + heightStrip.Message;
        var lines = new List<string>();
        for (int s = 0; s < heightParts.Count; s++)
            lines.Add($"{heightParts[s].Name}{(heightTargets[s] == null ? " (no keys)" : "")} feet " + string.Join(" ", new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }.Select(a => heightStrip.FeetAt(s, a) is float f ? $"{f:0}" : "-")));
        return $"{heightParts.Count} part(s), active {heightStrip.Active} ({heightParts[heightStrip.Active].Name}); " + string.Join("; ", lines) + (heightClimb.Visible ? " | climb: " + heightClimbNote.Text : " | no climb row");
    }

    /// <summary>Test hook: End → The Donor's Loop, Landing picked; once the borrows are built again, the strip as HeightChainTest reports it.</summary>
    public async Task<string> HeightEndTest(string slot)
    {
        await HeightChainTest(slot);
        if (heightEnd is not { Visible: true }) return "no End choice";
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        heightEnd.SelectedIndex = 1;
        await Task.WhenAny(tcs.Task, Task.Delay(120000));
        await Task.Delay(500);
        return $"end {HeightBorrow()?.End ?? "none"}: " + await HeightChainTest(slot);
    }

    /// <summary>Test hook: the climb row's Stabilize Ground Contact pressed; once the borrows are built again, the strip as HeightChainTest reports it.</summary>
    public async Task<string> HeightLevelTest(string slot)
    {
        if (heightLevel == null || !heightClimb.Visible) return "no climb row";
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        heightLevel.PerformClick();
        await Task.WhenAny(tcs.Task, Task.Delay(120000));
        await Task.Delay(500);
        return $"speed {HeightBorrow()?.ClimbSpeed?.ToString("0") ?? "the game's"}: " + await HeightChainTest(slot);
    }

    /// <summary>Test hook: the Height gizmo on, the animation paused half way, its arrow dragged 40 pixels up; the keys after.</summary>
    public async Task<string> HeightGizmoTest()
    {
        if (animPreview?.Viewer3D is not { } p3 || heightStrip == null) return "no view";
        if (!heightGizmoOn) ToggleHeightGizmo();
        p3.Pause(); p3.Time = p3.Seconds * 0.5;
        await Task.Delay(500);
        if (HeightGizmoNow() == null) return "no gizmo: " + (heightStrip.Message ?? "?");
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        if (!p3.View.TestGizmoDrag(2, 0, -40)) { animLoaded -= Done; return "the arrow isn't on screen"; }
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        await Task.Delay(1500);
        var k = HeightTargetOf(SelectedAnim, out _)?.Keys ?? [];
        return $"keys {string.Join(" ", k.Select(x => $"{x.At:0.00}:{x.Z:0.#}"))}; paused {!p3.IsPlaying} at {p3.Fraction:0.00}";
    }

    /// <summary>Test hook: Undo, Redo, then History back to the start; the keys and the animation's highest body bone after each.</summary>
    public async Task<string> HeightUndoTest()
    {
        string Now()
        {
            var k = HeightTargetOf(SelectedAnim, out _)?.Keys ?? [];
            float mid = SelectedAnim is { } a && ModAnimations.Load(a.Ref) is { } ba && ba.Tracks.TryGetValue("g_spine01", out var tr) && tr.PositionKeys.Count > 0
                ? tr.PositionKeys[tr.PositionKeys.Count / 2].Position.Length() : float.NaN;
            return $"[{string.Join(" ", k.Select(x => $"{x.At:0.00}:{x.Z:0.#}"))}] spine mid {mid:0.0}";
        }
        var lines = new List<string> { $"steps {heightUndo.Count}: {string.Join(" | ", heightUndo.Reverse().Select(s => s.Label))}", "now " + Now() };
        await HeightUndo(); lines.Add("undo " + Now());
        await HeightRedo(); lines.Add("redo " + Now());
        await HeightJump(0); lines.Add($"history to the start ({heightUndo.Count} undo / {heightRedo.Count} redo) " + Now());
        await HeightJump(heightRedo.Count); lines.Add($"history to the end ({heightUndo.Count} / {heightRedo.Count}) " + Now());
        return string.Join("; ", lines);
    }

    /// <summary>Test hook: a borrowed power's animation (a loop first), once the borrows are built; null: none.</summary>
    public async Task<string?> BorrowSlotForTest()
    {
        if (!borrowBuilding && draft.PowerBorrows.Any(b => b.Slots == null)) RebuildBorrows();
        for (int i = 0; i < 300 && (borrowBuilding || draft.PowerBorrows.All(b => b.Slots == null)); i++) await Task.Delay(200);
        var slots = draft.PowerBorrows.SelectMany(b => b.Slots ?? []).ToList();
        return slots.FirstOrDefault(s => s.Contains("_loop", StringComparison.OrdinalIgnoreCase)) ?? slots.FirstOrDefault();
    }

    /// <summary>Test hook (--anim-tab-test with MHO_TEST_HEIGHTS, scratch libraries only): keys 0 → <paramref name="mid"/>
    /// at the middle → 0 on <paramref name="slot"/> (a changed animation) through the strip, then Clear Keys; each step the
    /// largest change of any bone's position keys against the animation before.</summary>
    public async Task<string> HeightTest(string slot, float mid)
    {
        if (!await WaitAnims()) return "no animations";
        RestoreRow(slot);
        for (int i = 0; i < 50 && heightStrip?.Message != null; i++) await Task.Delay(200);
        if (heightStrip == null || SelectedAnim?.Name.Equals(slot, StringComparison.OrdinalIgnoreCase) != true) return "no row " + slot;
        if (heightStrip.Message != null) return "strip off: " + heightStrip.Message;
        var before = ModAnimations.Load(SelectedAnim.Ref);
        async Task<string> Step(IEnumerable<(float, float)> keys)
        {
            var tcs = new TaskCompletionSource();
            void Done() { animLoaded -= Done; tcs.TrySetResult(); }
            animLoaded += Done;
            heightStrip!.SetKeys(keys);
            CommitHeights();
            await Task.WhenAny(tcs.Task, Task.Delay(30000));
            await Task.Delay(1500);
            var after = SelectedAnim is { } a ? ModAnimations.Load(a.Ref) : null;
            if (before == null || after == null) return "doesn't read";
            var moves = before.Tracks.Where(kv => after.Tracks.TryGetValue(kv.Key, out var t) && t.PositionKeys.Count == kv.Value.PositionKeys.Count && kv.Value.PositionKeys.Count > 0)
                .Select(kv => (kv.Key, Max: kv.Value.PositionKeys.Zip(after.Tracks[kv.Key].PositionKeys).Max(p => System.Numerics.Vector3.Distance(p.First.Position, p.Second.Position))))
                .Where(x => x.Max > 0.001f).OrderByDescending(x => x.Max).ToList();
            var entry = HeightTargetOf(SelectedAnim, out _);
            return $"keys in the manifest entry {entry?.Keys.Count ?? -1}; moved: " + (moves.Count == 0 ? "none" : string.Join(", ", moves.Select(m => $"{m.Key} {m.Max:0.###}")));
        }
        // (mid 0: Clear Keys; "before" is then the animation with the keys a first call set, so "moved" is what clearing undid)
        return mid == 0 ? "cleared: " + await Step([]) : "set: " + await Step([(0f, 0f), (0.5f, mid), (1f, 0f)]);
    }
}
