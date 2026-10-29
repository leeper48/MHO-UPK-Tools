using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The target's props under the Parts list (Kurt, 2026-10-17: Black Cat's belt stayed on a model built onto her costume): the
/// chosen package's own props (PowerParts.CostumeProps: belts, weapons, whips), ticked = kept. Unticking one keeps it in the
/// draft's PartsOff, and Save sets that prop's mesh to none in the package (PowerParts.Build, after the build); ticking it again
/// puts the game's mesh back. The preview leaves unticked props out at once. Under them, the props added from other
/// characters (Kurt, 2026-10-27: added in the Props view, Remove here; picked in the selector, their fit is on the Prop Fit sliders).
/// </summary>
sealed partial class ModelPage
{
    readonly DataGridView targetProps = new() { Dock = DockStyle.Fill };
    readonly Label targetPropsCap = new() { Text = "THE TARGET'S PROPS  ·  untick one to leave it off this costume (a belt, a weapon)", AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f), Margin = new Padding(2, 8, 0, 4) };
    List<AddedPropEntry> addedRows = [];
    TableLayoutPanel? partsPage;
    readonly Label partsCap = new() { Text = "SOURCE PARTS", AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f) };
    List<PowerParts.Switch> propSwitches = [];
    int targetPropsRun;

    /// <summary>
    /// The Parts tab (Kurt, 2026-10-27: one list, not two short panels): the source's parts, then the target's props, each
    /// table as tall as its rows in one scrolling list with a heading per group; Remove in the toolbar
    /// (<paramref name="partsHead"/>) with Smooth, Add Rigs and Default Parts.
    /// </summary>
    Control PartsTab(FlowLayoutPanel partsHead)
    {
        targetProps.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 34, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "part", HeaderText = "Prop", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Kind", Width = 80, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "mesh", HeaderText = "Mesh", Width = 200, ReadOnly = true });
        targetProps.AllowUserToAddRows = false; targetProps.AllowUserToDeleteRows = false; targetProps.RowHeadersVisible = false;
        targetProps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Ui.StyleGrid(targetProps);
        Ui.Tip(targetProps, "The target costume's own props (belts, weapons, whips), which stay on whatever model is built onto it. Untick one to leave it off: Save sets that prop's mesh to none in this costume's package only (the hero's other costumes keep theirs). Tick it again for the game's. Added props are listed after them: untick to leave one off (its settings kept). Del or right-click → Remove takes the selected prop off (an added one is removed, the game's is left off).");
        targetProps.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.RowIndex >= 0 && e.ColumnIndex >= 0 && targetProps.Columns[e.ColumnIndex].Name == "use")
                SetPropKept(targetProps.Rows[e.RowIndex], targetProps.Rows[e.RowIndex].Cells["use"].Value is not true);
        };
        // an added prop's row picked: picked in the selector too (its fit on the sliders)
        targetProps.SelectionChanged += (_, _) =>
        {
            if (targetProps.SelectedRows.Count == 1 && targetProps.SelectedRows[0].Tag is AddedPropEntry ap && !ReferenceEquals(ap, addedPick)) PickAdded(ap);
        };
        // Del (Kurt, 2026-10-27): the selected added prop removed (Ctrl+Z brings it back)
        targetProps.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || targetProps.SelectedRows.Count != 1) return;
            var sel = targetProps.SelectedRows[0];
            if (sel.Tag is AddedPropEntry) { RemovePropClicked(); e.Handled = true; }
            else if (sel.Tag is PowerParts.Switch && sel.Cells["use"].Value is true) { SetPropKept(sel, false); e.Handled = true; }   // (the game's own: left off)
        };
        // right-click: a row's menu (Kurt, 2026-10-27)
        targetProps.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            var row = targetProps.Rows[e.RowIndex];
            targetProps.ClearSelection(); row.Selected = true;
            var menu = new ContextMenuStrip();
            bool on = row.Cells["use"].Value is true;
            if (row.Tag is AddedPropEntry ap)
            {
                // Remove first (Kurt, 2026-10-27: a right-click is expected to remove)
                menu.Items.Add(new ToolStripMenuItem("Remove", null, (_, _) => RemovePropClicked()) { ShortcutKeyDisplayString = "Del", ToolTipText = "Takes it away from this costume (Ctrl+Z brings it back)." });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Pick as Target", null, (_, _) => PickAdded(ap)) { ToolTipText = "Picks it under the preview: its fit, bone and model on the right." });
                menu.Items.Add(new ToolStripMenuItem(on ? "Leave Off for Now" : "Put Back", null, (_, _) => SetPropKept(row, !on)) { ToolTipText = on ? "Takes it off the costume for now; its fit, bone and model are kept (tick it to put it back)." : "Puts it back on the costume as it was." });
                if (ap.ModelFrom != null)
                    menu.Items.Add(new ToolStripMenuItem("Use Its Character's Model", null, async (_, _) => { PickAdded(ap); await AddedOwnModel(ap); })
                    { ToolTipText = "Drops the model built for it here and puts its own character's model back (asked first)." });
            }
            else if (row.Tag is PowerParts.Switch sw)
            {
                // Swap Model (Kurt, 2026-10-28: replace Cap's shield with an MFF one instead of adding a second shield): the
                // prop made the target under the preview, so the build puts the source part in its place (fitted over it,
                // held as it is, used by every power that uses it)
                int swapAt = SwapTargetIndex(sw);
                menu.Items.Add(new ToolStripMenuItem("Swap Model", null, (_, _) => SwapModel(row, swapAt)) { Enabled = swapAt >= 0, ToolTipText = swapAt >= 0
                    ? "Makes this prop the target, so Build into Mod replaces its model with the part ticked under Parts (laid over it: centered, turned and sized like it, on the same bone). Every power that uses it then uses the new model."
                    : "This prop's model isn't in this package (it comes from the hero's own package): pick the hero's package under Target to swap it." });
                menu.Items.Add(new ToolStripSeparator());
                // the game's own prop can't leave the hero's package: Remove leaves it off this costume (its mesh set to none here)
                menu.Items.Add(on
                    ? new ToolStripMenuItem("Remove", null, (_, _) => SetPropKept(row, false)) { ShortcutKeyDisplayString = "Del", ToolTipText = "Takes the game's prop off this costume (Save sets its mesh to none in this package only; Put Back or Ctrl+Z brings it back)." }
                    : new ToolStripMenuItem("Put Back", null, (_, _) => SetPropKept(row, true)) { ToolTipText = "Puts the game's prop back on this costume." });
            }
            else return;
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(targetProps, targetProps.PointToClient(Cursor.Position));
        };
        targetProps.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || targetProps.SelectedRows.Count == 0) return;
            var rows = targetProps.SelectedRows.Cast<DataGridViewRow>().ToList();
            bool keep = rows.Any(r => r.Cells["use"].Value is not true);
            foreach (var r in rows) SetPropKept(r, keep);
            e.Handled = true;
        };
        // Remove in the tab's toolbar (props are added in the Props view: Kurt, 2026-10-27, it replaced Add a Prop ▾)
        var removeProp = Ui.FlatButton("Remove", RemovePropClicked, "Takes the added prop picked in the list (or with ◀ ▶) off this costume. The game's own props are left off by unticking them instead.");
        removeProp.Margin = new Padding(14, 0, 0, 0);
        partsHead.Controls.Add(removeProp);
        // the list: SOURCE PARTS and its table, THE TARGET'S PROPS and its table, each as tall as its rows
        // (in a scrolling panel, the table as tall as its content: Kurt, 2026-10-28, a user's parts were cut off with no
        // scrollbar; a table with a fill row lays itself out to the panel's height, so it never overflowed and never scrolled)
        var list = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0) };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int r = 0; r < 4; r++) list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        scroller.Controls.Add(list);
        MhoExtendedModManager.Gui.Modern.DarkScrollbars(scroller);   // (the app's dark scrollbars)
        partsCap.Margin = new Padding(2, 2, 0, 4);
        parts.Dock = DockStyle.None; parts.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; parts.Margin = new Padding(0);
        targetProps.Dock = DockStyle.None; targetProps.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; targetProps.Margin = new Padding(0);
        targetPropsCap.Text = "THE TARGET'S PROPS  ·  untick one to leave it off this costume (a belt, a weapon)";
        list.Controls.Add(partsCap, 0, 0); list.Controls.Add(parts, 0, 1); list.Controls.Add(targetPropsCap, 0, 2); list.Controls.Add(targetProps, 0, 3);
        foreach (var g in new[] { parts, targetProps })
        {
            g.ScrollBars = ScrollBars.None;   // (the list scrolls)
            g.RowsAdded += (_, _) => SizePropsBox();
            g.RowsRemoved += (_, _) => SizePropsBox();
            // the wheel over a table scrolls the list
            g.MouseWheel += (_, e) => { int y = -scroller.AutoScrollPosition.Y - e.Delta / 2; scroller.AutoScrollPosition = new Point(0, Math.Max(0, y)); };
        }
        partsPage = list;
        scroller.SizeChanged += (_, _) => SizePropsBox();
        return Column("", partsHead, scroller);
    }

    /// <summary>A game prop's model in the target selector (meshNames), or -1 when this package doesn't hold it.</summary>
    int SwapTargetIndex(PowerParts.Switch sw)
    {
        string what = sw.What ?? "";
        return meshNames.FindIndex(n => meshProps.Contains(n) && (what.Equals(n, StringComparison.OrdinalIgnoreCase) || what.EndsWith("." + n, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Swap Model: the game prop made the target (kept on if it was left off), so the next build replaces it.</summary>
    void SwapModel(DataGridViewRow row, int at)
    {
        if (at < 0) return;
        if (row.Cells["use"].Value is not true) SetPropKept(row, true);   // (a swapped prop is shown, with its new model)
        meshPick.SelectedIndex = at + 1;
        status.Text = $"Swapping {meshNames[at]}: tick the part to use under Parts, then Build into Mod.";
        Log($"Swap Model: {meshNames[at]} is the target; the build replaces its model with the part ticked under Parts.");
    }

    /// <summary>The chosen package's props, read in the background; each row ticked unless the draft has it off.</summary>
    async void FillTargetProps()
    {
        int run = ++targetPropsRun;
        if (ChosenPackage is not CharacterList.Item pkg) { ShowTargetProps([], null); return; }
        string key = pkg.Key, path;
        // the game's own copy (what the props are in the game): the copy a build starts from can be a saved one with a prop
        // already off; else that copy
        try { path = BasePackage.Resolve(key, true); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException) { path = ""; }
        try { if (path.Length == 0) path = StartPackageAsIs(key); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { ShowTargetProps([], null); return; }
        // the costume's own props, then those it gets from its hero (Doom Classic's pistol and throne: classes in his base package)
        string? cooked = Settings.Current.CookedFolder;
        var list = await Task.Run(() =>
        {
            var own = PowerParts.CostumeProps(key, path);
            string? basePath = System.Text.RegularExpressions.Regex.Match(key, @"^(UC__MarvelPlayer_[^_]+)_.+_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } bm && cooked != null
                && StockFiles.For(cooked, bm.Groups[1].Value + "_SF.upk") is var bp && File.Exists(bp) ? bp : null;
            var added = host.AddedProps(key).Select(a => a.Class).ToList();
            own.AddRange(PowerParts.HeroProps(key, path, basePath).Where(h => !added.Any(a => PowerBorrow.SameClass(h.PartName, a))));
            return own;
        });
        if (IsDisposed || run != targetPropsRun || ChosenPackage?.Key != key) return;
        ShowTargetProps(list, key);
    }

    void ShowTargetProps(List<PowerParts.Switch> list, string? key)
    {
        propSwitches = list;
        addedRows = key == null ? [] : [.. host.AddedProps(key)];
        targetProps.Rows.Clear();
        foreach (var s in list)
        {
            int i = targetProps.Rows.Add(PropOff(s) == null, s.Label, s.Kind == "Hero Prop" ? "Hero Prop" : "Target Prop", s.What);
            targetProps.Rows[i].Tag = s;
            if (PropOff(s) != null) targetProps.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
        }
        foreach (var a in addedRows)
        {
            int i = targetProps.Rows.Add(!a.Off, a.Name, "Added Prop", a.Class);
            targetProps.Rows[i].Tag = a;
            if (a.Off) targetProps.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
        }
        SizePropsBox();
        targetProps.ClearSelection();
        preview.HiddenProps = key == null ? null : cls => host.PartsOff.Any(e => e.Package.Equals(key, StringComparison.OrdinalIgnoreCase)
            && e.Switch is "modelmesh" or "skeletalmesh" or PowerParts.ListEntry && PowerParts.PropClass(e.Part).Equals(cls, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Each table as tall as its rows (the list scrolls past the tab); a group's heading and table shown when it has
    /// rows (the props also for a costume without any: Add a Prop).</summary>
    void SizePropsBox()
    {
        if (partsPage == null) return;
        int count = propSwitches.Count + addedRows.Count;
        bool costume = ChosenPackage is CharacterList.Item c && c.Key.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase);
        bool showProps = count > 0 || costume, showParts = parts.Rows.Count > 0;
        static int Tall(DataGridView g) => g.ColumnHeadersHeight + g.Rows.Cast<DataGridViewRow>().Where(r => r.Visible).Sum(r => r.Height) + 2;
        partsCap.Visible = showParts; parts.Visible = showParts;
        targetPropsCap.Visible = showProps; targetProps.Visible = showProps;
        if (showParts) parts.Height = Tall(parts);
        if (showProps) targetProps.Height = Tall(targetProps);
        targetPropsCap.Margin = new Padding(2, showParts ? 12 : 2, 0, 4);
    }

    /// <summary>Test hook: the target's props listed (labels) and one kept or left off by label.</summary>
    internal IReadOnlyList<string> TargetPropLabels => [.. propSwitches.Select(s => s.Label)];
    internal bool TestSetProp(string label, bool keep)
    {
        var row = targetProps.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is PowerParts.Switch s && s.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (row == null) return false;
        SetPropKept(row, keep);
        return true;
    }

    PartOffEntry? PropOff(PowerParts.Switch s) => host.PartsOff.FirstOrDefault(e => e.Package.Equals(s.Package, StringComparison.OrdinalIgnoreCase)
        && e.Part.Equals(s.Part, StringComparison.OrdinalIgnoreCase) && e.Switch.Equals(s.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The FBX at <paramref name="fbx"/> added to <paramref name="key"/> as a prop of its own and built (also the tests').</summary>
    /// <param name="name">The prop's name in the lists (else the file's name + " (FBX)").</param>
    /// <param name="bone">The bone it's held on (else the carrier's own; Held On ▾ changes it).</param>
    internal async Task<AddedPropEntry?> AddFbxProp(string key, string fbx, string? name = null, string? bone = null)
    {
        status.Text = "Finding a prop class to carry it…";
        string before = PropBefore();
        var have = host.AddedProps(key).Select(a => a.Class).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string start = StartPackageCore(key);
        var carrier = await Task.Run(() => FreeCarrier(start, have));
        if (IsDisposed) return null;
        if (carrier == null) { Log("From an FBX: no free prop class was found to carry it (every one tried is on this costume already)."); UpdateStatus(); return null; }
        name ??= Path.GetFileNameWithoutExtension(fbx) + " (FBX)";
        if (await host.AddPropTo(key, carrier with { Name = name }) is not { } e || IsDisposed) { UpdateStatus(); return null; }
        e.KeepShape = true;
        if (bone != null && !bone.Equals(e.Bone, StringComparison.OrdinalIgnoreCase))
        {
            e.Bone = bone;
            if (!await host.RefitAddedProp(e)) { e.Bone = null; Log($"{name}: it couldn't be put on {bone} (see above); it's on its carrier's bone: Held On ▾ moves it."); }
            if (IsDisposed) return e;
        }
        PropDone(before, $"Add {name}");
        await AddedPropsChanged(key, e);
        for (int i = 0; i < 300 && AddedTarget != e; i++) await Task.Delay(100);
        if (AddedTarget != e) { Log($"{name}: added, but it couldn't be made the target; pick it with ◀ ▶ and pick the FBX as its source."); return e; }
        string src = "fbx:" + Path.GetFullPath(fbx);
        if (sourceKind.SelectedIndex != 1) { sourceKind.SelectedIndex = 1; await Task.Delay(300); }
        FbxChosen(src);
        for (int i = 0; i < 600 && !(src.Equals(chosenKey, StringComparison.OrdinalIgnoreCase) && HasSource && !refreshing); i++) await Task.Delay(100);
        if (!HasSource) { Log($"{name}: the FBX didn't load as its model (see above)."); return e; }
        Log(bone != null && bone.Equals(e.Bone, StringComparison.OrdinalIgnoreCase)
            ? $"{name}: added on {carrier.Name} (its class carries the model), held on {bone}. Building its model as made."
            : $"{name}: added on {carrier.Name} (its class carries the model; it's held where that prop is: Held On ▾ moves it). Building its model as made.");
        await BuildAsync();
        return e;
    }

    /// <summary>
    /// Add as a Prop (Kurt, 2026-10-27): a part of the MFF source (a weapon on Bone_w, BoneW…) as a prop of its own. It's put on
    /// the costume's skeleton as the build would (<see cref="MffPropExport"/>: on the hand that holds it, sized to the hero),
    /// written as an FBX in the work folder (it goes into the mod's Model folder on Save) and added From an FBX, on the bone
    /// that carries it.
    /// </summary>
    internal Task<AddedPropEntry?> AddPartProp(string partName) => AddPartProp(model, partName);

    internal async Task<AddedPropEntry?> AddPartProp(MffModel? m, string partName)
    {
        if (ChosenPackage is not CharacterList.Item pkg) { Log("Pick the costume to add a prop to under Target first."); return null; }
        if (m == null || m.Parts.FirstOrDefault(p => p.Name.Equals(partName, StringComparison.OrdinalIgnoreCase)) is not { } part)
        { Log($"Add as a Prop: {partName} isn't a part of the source (pick the MFF character as the source)."); return null; }
        string key = pkg.Key, start = StartPackageCore(key);
        string mp = Path.Combine(host.WorkFolder, "maps", $"{m.Folder} on {Path.GetFileNameWithoutExtension(key)}.json");
        string? mapFile = File.Exists(mp) ? mp : null;
        string dir = Path.Combine(host.WorkFolder, "props", FbxExport.SafeName(m.Folder));
        var (title, _) = MffNames.Describe(m.Folder);
        status.Text = $"Making {part.Name} a prop…";
        MffPropExport.Result res;
        try
        {
            res = await Task.Run(() => MffPropExport.Write(m, part, MhoSkeleton.Load(start, null, body: true),
                mapFile != null ? BoneMapFile.Load(mapFile) : null, dir, line => BeginInvoke(() => Log(line))));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Assimp.AssimpException)
        { Log($"Add as a Prop: {part.Name} couldn't be made a prop ({ex.Message})."); UpdateStatus(); return null; }
        if (IsDisposed) return null;
        Log($"{part.Name}: {res.Vertices:N0} vertices, from {title} ({res.Fbx}).");
        return await AddFbxProp(key, res.Fbx, $"{title} · {part.Name} (MFF)", res.Bone);
    }

    /// <summary>
    /// A plain prop class from the game to carry a model of the user's own: a rigid one (not animated, not riding the
    /// character's skeleton, not a turned vehicle) with a model of its own, which neither the costume (<paramref name="start"/>)
    /// nor its added props (<paramref name="have"/>) has. From a few heroes' packages, the first that fits.
    /// </summary>
    static AddPropForm.Pick? FreeCarrier(string start, IReadOnlySet<string> have)
    {
        if (Settings.Current.CookedFolder is not string cooked) return null;
        var own = MhoExtendedModManager.ModMeshes.Attachments(start).Select(a => a.Class).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string hero in new[] { "Angela", "Thor", "BlackWidow", "Hawkeye", "Elektra", "Deadpool", "Blade", "Daredevil", "Gamora", "Taskmaster" })
        {
            string file = $"UC__MarvelPlayer_{hero}_SF.upk", path = StockFiles.For(cooked, file);
            if (!File.Exists(path)) continue;
            List<MhoExtendedModManager.ModMeshes.Attachment> list;
            try { list = MhoExtendedModManager.ModMeshes.Attachments(path); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
            foreach (var a in list)
            {
                if (a.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase) || a.UseParentAnim || a.OffsetRotation != (0, 0, 0)) continue;
                if (string.IsNullOrEmpty(a.Mesh) || own.Contains(a.Class) || have.Contains(a.Class)) continue;
                return new AddPropForm.Pick(file, a.Class, a.Class);
            }
        }
        return null;
    }

    /// <summary>An added prop ticked (on the costume) or unticked (left off, its fit, bone and model kept for when it's ticked again).</summary>
    async Task SetAddedKept(DataGridViewRow row, AddedPropEntry ap, bool keep)
    {
        if (ap.Off == !keep) return;
        string before = PropBefore();
        row.Cells["use"].Value = keep;
        row.DefaultCellStyle.ForeColor = keep ? Ui.Text : Ui.Subtle;
        if (!await host.SetAddedPropOn(ap, keep) || IsDisposed) { row.Cells["use"].Value = !keep; row.DefaultCellStyle.ForeColor = !keep ? Ui.Text : Ui.Subtle; return; }
        PropDone(before, keep ? $"Tick {ap.Name}" : $"Untick {ap.Name}");
        Log(keep ? $"{ap.Name}: back on {ap.Package}, as it was." : $"{ap.Name}: left off {ap.Package} (its fit, bone and model are kept: tick it to put it back; Remove takes it away).");
    }

    /// <summary>Test hook: an added prop ticked or unticked, as a click does.</summary>
    internal async Task TestSetAdded(AddedPropEntry ap, bool keep)
    {
        var row = targetProps.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, ap));
        if (row != null) await SetAddedKept(row, ap, keep);
    }

    /// <summary>Remove: the added prop picked in the list, else in the selector.</summary>
    async void RemovePropClicked()
    {
        var e = targetProps.SelectedRows.Count == 1 && targetProps.SelectedRows[0].Tag is AddedPropEntry r ? r : addedPick;
        if (e == null) { Log("Pick an added prop to remove (in the list above, or with ◀ ▶ under the preview). The game's own props are left off by unticking them."); return; }
        string before = PropBefore();
        if (!await host.RemoveAddedProp(e) || IsDisposed) return;
        PropDone(before, $"Remove {e.Name}");
        if (ReferenceEquals(addedPick, e))
        {
            // the target was the removed prop: the character's own (automatic) again
            addedPick = null;
            MhoSkeleton.Choose(e.Package, null);
            PropTicks(changed: true);
            SourceForTarget();
            SaveState(); SchedulePreview();
        }
        await AddedPropsChanged(e.Package, null);
        Log($"{e.Name} taken off {e.Package}.");
    }

    /// <summary>The selector and the list read again after an add or remove, <paramref name="pick"/> picked (the character stays
    /// the build's target).</summary>
    async Task AddedPropsChanged(string key, AddedPropEntry? pick)
    {
        if (pick != null) _ = PickAddedTarget(key, pick);
        FillMeshPick();
        FillTargetProps();
        for (int i = 0; i < 50 && pick != null && !meshAdded.Contains(pick); i++) await Task.Delay(100);
        UpdatePropFitUi(); UpdateStatus(); meshPick.Invalidate();
    }

    /// <summary>Keeps or leaves off one of the target's props (in the draft; Save writes it).</summary>
    void SetPropKept(DataGridViewRow row, bool keep)
    {
        if (row.Tag is AddedPropEntry ap) { _ = SetAddedKept(row, ap, keep); return; }
        if (row.Tag is not PowerParts.Switch s) return;
        if (!keep && s.Original.Length == 0) { Log($"{s.Label}: that prop shows nothing in the game already."); return; }
        var had = PropOff(s);
        if (keep && had != null) host.PartsOff.Remove(had);
        else if (!keep && had == null) host.PartsOff.Add(new PartOffEntry { Package = s.Package, Part = s.Part, Switch = s.Key, Original = s.Original });
        else return;
        row.Cells["use"].Value = keep;
        row.DefaultCellStyle.ForeColor = keep ? Ui.Text : Ui.Subtle;
        Log(keep ? $"{s.Label} ({s.What}): kept on {s.Package} (the game's)." : $"{s.Label} ({s.What}): left off {s.Package}; Save Changes sets its mesh to none in this costume's package.");
        preview.RefreshProps();
    }
}
