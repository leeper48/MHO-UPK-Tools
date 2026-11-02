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

    /// <summary>The Height gizmo where it is now (null: off, or the animation can't take keys).</summary>
    ModelView.TransformGizmo? HeightGizmoNow()
    {
        if (!heightGizmoOn || heightStrip == null || heightStrip.Message != null || heightBusy || animPreview?.Viewer3D is not { Animator: { } an }) return null;
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
            if (HeightGizmoNow() == null) return false;
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
        Ui.Tip(heightStrip, "Height keys for this changed animation: left to right is the animation, up raises the body (game units; the middle line is the animation as it is). Click to add a key, drag a key to move it (Shift: tenths), right-click a key to remove it. Look → Ground in the 3D view shows the floor.");
        heightAdd = Ui.FlatButton("Add Key at Frame", () => { if (animPreview?.Viewer3D is { } v) heightStrip.AddAt(v.Fraction); }, "A key where the animation is now (pause it on the frame first), at the height the curve has there.");
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
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 6, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(100 * S)));
        t.Controls.Add(bar, 0, 0);
        t.Controls.Add(heightStrip, 0, 1);
        heightTimer = new System.Windows.Forms.Timer { Interval = 50 };
        heightTimer.Tick += (_, _) =>
        {
            if (heightStrip == null || !heightStrip.Visible) return;
            heightStrip.Playhead = animPreview?.Viewer3D is { Playing: not null } v && v.CurrentAnimation?.Equals(SelectedAnim?.Name, StringComparison.OrdinalIgnoreCase) == true ? v.Fraction : null;
        };
        heightTimer.Start();
        Disposed += (_, _) => heightTimer?.Dispose();
        ShowHeights();
        return t;
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

    /// <summary>The strip for the selected animation: its keys, or why it can't have any.</summary>
    void ShowHeights()
    {
        if (heightStrip == null || heightBusy) return;
        int req = ++heightRequest;
        var a = SelectedAnim;
        void Off(string why) { heightStrip.SetKeys([]); heightStrip.Message = why; heightAdd!.Enabled = heightClear!.Enabled = false; }
        heightStatus.Text = "";
        if (HeightTargetOf(a, out string whyNot) is not { } target) { Off(whyNot); return; }
        if (borrowBuilding) { Off("The borrowed looks are being built: one moment."); return; }
        if (DraftPath(target.File) is not string path) { Off("The package isn't found."); return; }
        Off("Checking…");
        string slot = target.Slot;
        Task.Run(() => { AnimSwap.ReHeight(path, slot, [], [], null, out string why, check: true); return why; }).ContinueWith(t =>
        {
            if (IsDisposed || req != heightRequest || heightStrip == null) return;
            string why = t.IsFaulted ? t.Exception?.InnerException?.Message ?? "it doesn't read" : t.Result;
            if (why.Length > 0) { Off($"This animation can't take height keys: {why}."); return; }
            heightStrip.Message = null;
            heightStrip.SetKeys(target.Keys);
            heightAdd!.Enabled = heightClear!.Enabled = true;
            if (target.Borrow != null) { heightStatus.Text = "Borrowed Power"; Ui.Tip(heightStatus, $"This animation comes with the borrowed look of {target.Borrow.DonorName}. Its keys are kept with the borrow and made again whenever the borrows are rebuilt (a change on the Powers tab, Save)."); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The next commit's step name, when it isn't the strip's own (the gizmo, Clear Keys).</summary>
    string? heightLabel;

    /// <summary>The strip's keys put on the selected animation (SetHeights), as an Undo step.</summary>
    async void CommitHeights()
    {
        if (heightStrip == null) return;
        if (heightBusy) { heightAgain = true; return; }
        if (HeightTargetOf(SelectedAnim, out _) is not { } target || borrowBuilding) return;
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

    /// <summary>Test hook: Start Into Loop on, <paramref name="start"/> played from its first frame into the loop, then paused
    /// in the loop's first play; the floor at 0.1 s steps there.</summary>
    public async Task<string> ChainFloorTest(string start)
    {
        if (!await WaitAnims()) return "no animations";
        bool was = PreviewViews.StartIntoLoop;
        PreviewViews.StartIntoLoop = false;
        try
        {
            RestoreRow(start);
            await Task.Delay(6000);
            if (animPreview?.Viewer3D is not { } p3 || p3.Playing == null) return "nothing playing";
            p3.Pause(); p3.Time = 0;
            PreviewViews.StartIntoLoop = true;
            p3.Play();
            for (int i = 0; i < 40 && p3.CurrentAnimation?.EndsWith("_start", StringComparison.OrdinalIgnoreCase) == true; i++) await Task.Delay(50);
            p3.Pause();
            var line = new List<string>();
            for (float t = 0; t <= Math.Min(0.6f, p3.Seconds) + 1e-3f; t += 0.1f) { p3.Time = t; line.Add($"{t:0.0}s {p3.View.GroundZ:0}"); }
            return $"after {start}: playing {p3.CurrentAnimation}; its first play's floor " + string.Join(" · ", line) + " | " + p3.FloorStateForTest;
        }
        finally { PreviewViews.StartIntoLoop = was; }
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
