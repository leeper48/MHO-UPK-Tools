using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The target's props under the Parts list (Kurt, 2026-10-17: Black Cat's belt stayed on a model built onto her costume): the
/// chosen package's own props (PowerParts.CostumeProps: belts, weapons, whips), ticked = kept. Unticking one keeps it in the
/// draft's PartsOff, and Save sets that prop's mesh to none in the package (PowerParts.Build, after the build); ticking it again
/// puts the game's mesh back. The preview leaves unticked props out at once. Under them, the props added from other
/// characters (Kurt, 2026-10-27: Add a Prop / Remove here; picked in the selector, their fit is on the Prop Fit sliders).
/// </summary>
sealed partial class ModelPage
{
    readonly DataGridView targetProps = new() { Dock = DockStyle.Fill };
    readonly Label targetPropsCap = new() { Text = "THE TARGET'S PROPS  ·  untick one to leave it off this costume (a belt, a weapon)", AutoSize = true, ForeColor = Ui.Subtle, Font = Ui.Bold(8.5f), Margin = new Padding(2, 8, 0, 4) };
    List<AddedPropEntry> addedRows = [];
    Control? targetPropsBox;
    TableLayoutPanel? partsPage;
    List<PowerParts.Switch> propSwitches = [];
    int targetPropsRun;

    /// <summary>The Parts tab: the source's parts, and under them the target's props (shown when it has any).</summary>
    Control PartsTab(Control partsColumn)
    {
        targetProps.Columns.Add(new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = 34, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "part", HeaderText = "Prop", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Kind", Width = 80, ReadOnly = true });
        targetProps.Columns.Add(new DataGridViewTextBoxColumn { Name = "mesh", HeaderText = "Mesh", Width = 200, ReadOnly = true });
        targetProps.AllowUserToAddRows = false; targetProps.AllowUserToDeleteRows = false; targetProps.RowHeadersVisible = false;
        targetProps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Ui.StyleGrid(targetProps);
        Ui.Tip(targetProps, "The target costume's own props (belts, weapons, whips), which stay on whatever model is built onto it. Untick one to leave it off: Save sets that prop's mesh to none in this costume's package only (the hero's other costumes keep theirs). Tick it again for the game's.");
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
        targetProps.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Space || targetProps.SelectedRows.Count == 0) return;
            var rows = targetProps.SelectedRows.Cast<DataGridViewRow>().ToList();
            bool keep = rows.Any(r => r.Cells["use"].Value is not true);
            foreach (var r in rows) SetPropKept(r, keep);
            e.Handled = true;
        };
        var box = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(4, 0, 4, 4), Visible = false };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        box.RowStyles.Add(new RowStyle(SizeType.AutoSize)); box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var addProp = Ui.FlatButton("Add a Prop", AddPropClicked, "Give this costume another character's prop (a hero's hammer, sword, shield): it holds it in every animation, on the bone the prop's own character holds it on. It goes into the package at once; Save puts it into the mod. Its fit: pick it with ◀ ▶ under the preview.");
        var removeProp = Ui.FlatButton("Remove", RemovePropClicked, "Takes the added prop picked here (or with ◀ ▶) off this costume. The game's own props are left off by unticking them instead.");
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        addProp.Margin = new Padding(0, 4, 6, 2); removeProp.Margin = new Padding(0, 4, 0, 2);
        buttons.Controls.AddRange([addProp, removeProp]);
        var head = Row(targetPropsCap, buttons);
        head.Dock = DockStyle.Fill;
        box.Controls.Add(head, 0, 0); box.Controls.Add(targetProps, 0, 1);
        targetPropsBox = box;
        // the parts over the props: the props take up to a third of the tab, only when the target has any
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        page.Controls.Add(partsColumn, 0, 0); page.Controls.Add(box, 0, 1);
        partsPage = page;
        page.SizeChanged += (_, _) => SizePropsBox();
        return page;
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
        var list = await Task.Run(() => PowerParts.CostumeProps(key, path));
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
            int i = targetProps.Rows.Add(PropOff(s) == null, s.Label, "Target Prop", s.What);
            targetProps.Rows[i].Tag = s;
            if (PropOff(s) != null) targetProps.Rows[i].DefaultCellStyle.ForeColor = Ui.Subtle;
        }
        foreach (var a in addedRows)
        {
            int i = targetProps.Rows.Add(true, a.Name, "Added Prop", a.Class);
            targetProps.Rows[i].Tag = a;
        }
        SizePropsBox();
        targetProps.ClearSelection();
        preview.HiddenProps = key == null ? null : cls => host.PartsOff.Any(e => e.Package.Equals(key, StringComparison.OrdinalIgnoreCase)
            && e.Switch is "modelmesh" or "skeletalmesh" && PowerParts.PropClass(e.Part).Equals(cls, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The props section as tall as its rows need (up to 5), at most a third of the tab; none without props.</summary>
    void SizePropsBox()
    {
        if (targetPropsBox == null || partsPage == null) return;
        int count = propSwitches.Count + addedRows.Count;
        // (shown for a costume without props too: Add a Prop)
        bool costume = ChosenPackage is CharacterList.Item c && c.Key.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase);
        targetPropsBox.Visible = count > 0 || costume;
        // as tall as its rows need (up to 5), at most two fifths of the tab
        float sc = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        int want = targetPropsCap.PreferredHeight + (int)(16 * sc) + targetProps.ColumnHeadersHeight + targetProps.RowTemplate.Height * Math.Min(5, Math.Max(1, count)) + 6;
        int h = !targetPropsBox.Visible ? 0 : Math.Min(want, partsPage.ClientSize.Height * 2 / 5);   // (the parts keep the rest)
        partsPage.RowStyles[1] = new RowStyle(SizeType.Absolute, h);
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

    /// <summary>Add a Prop: another character's prop on the chosen costume, picked in the selector after.</summary>
    async void AddPropClicked()
    {
        if (ChosenPackage is not CharacterList.Item pkg) { Log("Pick the costume to add a prop to under Target first."); return; }
        if (await host.AddPropTo(pkg.Key) is not { } e || IsDisposed) return;
        await AddedPropsChanged(pkg.Key, e);
        Log($"{e.Name} added to {pkg.Key}: it's picked under the preview, so the Prop Fit sliders move it (let go of a slider and it goes in). Save Changes keeps it.");
    }

    /// <summary>Remove: the added prop picked in the list, else in the selector.</summary>
    async void RemovePropClicked()
    {
        var e = targetProps.SelectedRows.Count == 1 && targetProps.SelectedRows[0].Tag is AddedPropEntry r ? r : addedPick;
        if (e == null) { Log("Pick an added prop to remove (in the list above, or with ◀ ▶ under the preview). The game's own props are left off by unticking them."); return; }
        if (!await host.RemoveAddedProp(e) || IsDisposed) return;
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
        if (row.Tag is AddedPropEntry ap) { Log($"{ap.Name} is an added prop: Remove takes it off."); return; }
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
