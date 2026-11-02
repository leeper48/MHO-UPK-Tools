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

    Control HeightSection()
    {
        heightStrip = new HeightStrip { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        heightStrip.Committed += CommitHeights;
        heightStrip.Dragging += (at, z) => heightStatus.Text = $"Key at {at * 100:0} % · {(z >= 0 ? "+" : "")}{z:0.#} Units";
        Ui.Tip(heightStrip, "Height keys for this changed animation: left to right is the animation, up raises the body (game units; the middle line is the animation as it is). Click to add a key, drag a key to move it (Shift: tenths), right-click a key to remove it. Look → Ground in the 3D view shows the floor.");
        heightAdd = Ui.FlatButton("Add Key at Frame", () => { if (animPreview?.Viewer3D is { } v) heightStrip.AddAt(v.Fraction); }, "A key where the animation is now (pause it on the frame first), at the height the curve has there.");
        heightClear = Ui.FlatButton("Clear Keys", () => { if (heightStrip.Keys.Count > 0) { heightStrip.SetKeys([]); CommitHeights(); } }, "Take every height key off this animation: it plays at its own height again.");
        var caption = new Label { Text = "HEIGHT", AutoSize = true, Font = Ui.Bold(9f), Padding = new Padding(0, 6, 8, 0) };
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        bar.Controls.AddRange([caption, heightAdd, heightClear, heightStatus]);
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

    /// <summary>The strip's keys put on the work copy (the difference from the keys it has), then played from there at the
    /// same moment. A borrow's keys also go on the borrow, so its next build makes them again.</summary>
    async void CommitHeights()
    {
        if (heightStrip == null) return;
        if (heightBusy) { heightAgain = true; return; }
        if (HeightTargetOf(SelectedAnim, out _) is not { } target || DraftPath(target.File) is not string path || borrowBuilding) return;
        heightBusy = true;
        string slot = target.Slot, file = target.File;
        var from = target.Keys;
        var to = heightStrip.Keys.ToList();
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
        if (IsDisposed) return;
        if (bytes == null)
        {
            heightStatus.Text = "";
            heightStrip.SetKeys(from);
            Dialog.Show(this, $"\"{slot}\" couldn't take these height keys: {why}", "Height Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        UseWorkCopy(file, bytes);
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
        if (heightAgain) { heightAgain = false; CommitHeights(); return; }
        // the list read again from the work copy, then the animation where it was
        ReloadAt(slot);
        void Back()
        {
            animLoaded -= Back;
            if (animPreview?.Viewer3D is not { } p3 || p3.Playing == null) return;
            p3.Time = at * p3.Seconds;
            if (!playing) p3.Pause();
        }
        animLoaded += Back;
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
