namespace MhoExtendedModManager.Gui;

/// <summary>
/// The editor's Animations tab (Kurt, 2026-10-02; phase 1, read only): every animation a character package of the mod
/// plays, as the game finds it (CostumeAnims: the mesh component's AnimSets list, the last set with a name wins), where it
/// comes from (the costume's own set, the hero's base package, the shared sets), what it overrides, and the power it
/// belongs to; a click plays it in the 3D view. Phase 2: Change Animation puts another character's animation in a slot
/// (AnimSwap, into a work copy of the package until Save; the manifest's AnimSwaps say where it came from), Back to the
/// Original takes it out again.
/// </summary>
sealed partial class ModEditorView
{
    StorePreview? animPreview;
    DataGridView? animGrid;
    readonly TextBox animFind = new() { Width = 220 };
    readonly Label animCount = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    readonly Label animStatus = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(10, 8, 0, 0) };
    DropDown? animPackage;
    CostumeAnims? animData;
    List<string> animPackages = [];
    int animRequest;
    Button? animChange, animBack, animCopy;
    string? animWork;
    static Task<List<Costume>?>? costumesTask;

    /// <summary>A package's current file: the draft's (a work copy after a change, else the mod's own).</summary>
    string? DraftPath(string file) => draft.Packages.FirstOrDefault(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
    string? AnimFile => animPackages.Count == 0 ? null : animPackages[Math.Max(0, animPackage?.SelectedIndex ?? 0)];
    CostumeAnims.Anim? SelectedAnim => animGrid?.SelectedRows.Count > 0 ? animGrid.SelectedRows[0].Tag as CostumeAnims.Anim : null;
    /// <summary>An animation a change put in (a set of the package's own named …_on_…).</summary>
    static bool IsSwapped(CostumeAnims.Anim a) => a.From.Kind == CostumeAnims.Source.Costume && a.From.Path.Split('.')[^1].Contains("_on_", StringComparison.OrdinalIgnoreCase);

    Control AnimationsPage()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        const string hint = "The animations this costume plays, as the game finds them (a later set replaces the same name in an earlier one). Click a row to play it.";
        if (editing == null || game == null)
        {
            var note = new Label { Text = "Save the mod once, then come back here: the animations are read from its saved packages.", AutoSize = true, Tag = "subtle", Padding = new Padding(8) };
            return Page(note, Toolbar(), hint);
        }
        // The 3D view is made when the tab first shows (as on the Powers tab: the editor's theme pass would give its surface a
        // transparent background, which crashed the editor).
        var host = new Panel { Dock = DockStyle.Fill };
        host.VisibleChanged += (_, _) =>
        {
            if (!host.Visible || animPreview != null || editing == null || game == null) return;
            animPreview = new StorePreview { Dock = DockStyle.Fill, CookedFolder = game.Cooked, Catalog = catalog, PowerStrip = false, Always3D = true };
            animPreview.ControlAdded += (_, _) => Ui.RestyleButtons(animPreview);
            animPreview.HeroPowersLoaded += FillAnimGrid;   // the Power column
            host.Controls.Add(animPreview);
            animPreview.Mod = editing;
        };
        page.Controls.Add(host, 0, 0);

        animGrid = Ui.Grid(S, false, ("Animation", 250), ("Kind", 110), ("Power", 170), ("From", 250), ("Replaces", 0));
        foreach (var (c, name) in animGrid.Columns.Cast<DataGridViewColumn>().Zip(new[] { "anim", "kind", "power", "from", "replaces" })) c.Name = name;
        animGrid.MultiSelect = false;
        animGrid.SelectionChanged += (_, _) =>
        {
            if (animRefilling) return;
            var a = SelectedAnim;
            if (animBack != null) animBack.Enabled = a != null && IsSwapped(a);
            if (animChange != null) animChange.Enabled = a != null;
            if (a == null || animPreview == null) return;
            // A change not saved yet is in a work copy: played from there (the 3D view shows the saved mod).
            bool saved = editing != null && a.From.File != null && a.From.File.StartsWith(editing.Folder, StringComparison.OrdinalIgnoreCase);
            if (saved ? !animPreview.PlayAnimation(a.Name) : !animPreview.PlayRef(a.Ref))
                animStatus.Text = animPreview.AllAnimations.Count == 0 ? "The 3D View Is Still Loading" : "The 3D View Shows Another Model (Not This Package's)";
            else animStatus.Text = "";
        };
        animGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ChangeAnimation(); };
        animFind.TextChanged += (_, _) => FillAnimGrid();
        MhoPackageModifier.Gui.SearchBox.AddClear(animFind);
        Ui.Tip(animFind, "Show only animations whose name, kind, power or source has these words.");
        var find = new Label { Text = "Find", AutoSize = true, Tag = "subtle", Padding = new Padding(0, 8, 4, 0) };
        animPackages = [.. editing.Manifest.UpkReplacements.Where(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(editing.Folder, f)))];
        var tools = new List<Control>();
        if (animPackages.Count > 1)
        {
            animPackage = new DropDown { Width = (int)(300 * S) };
            animPackage.Items.AddRange([.. animPackages]);
            animPackage.SelectedIndexChanged += (_, _) => LoadAnimations();
            Ui.Tip(animPackage, "The mod's package whose animations are listed.");
            tools.Add(animPackage);
        }
        animChange = Ui.AccentButton("Change Animation", ChangeAnimation, "Play another character's animation (or another of this one's) in place of the selected one: its source and target characters first, then any hero, team-up or mod. Double-click a row does the same.");
        animBack = Ui.FlatButton("Back to the Original", BackToOriginal, "Undo the change on the selected animation: the costume plays its own again.");
        animChange.Enabled = animBack.Enabled = false;
        animCopy = Ui.FlatButton("Copy From a Character", CopyFromCharacter, "Use many of another character's animations at once: each one with the same name as one of this costume's (ticked; untick what to leave). The Target undoes the ticked ones.");
        tools.AddRange([animChange, animBack, animCopy, find, animFind, animCount, animStatus]);
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
        right.Controls.Add(Page(animGrid, Toolbar([.. tools]), hint));
        page.Controls.Add(right, 1, 0);
        Disposed += (_, _) => { animPreview?.Dispose(); if (animWork != null) try { Directory.Delete(animWork, true); } catch (IOException) { } };
        if (animPackage != null) animPackage.SelectedIndex = 0; else LoadAnimations();
        return page;
    }

    /// <summary>Reads the selected package's animations in the background.</summary>
    void LoadAnimations()
    {
        if (editing == null || game == null || AnimFile is not string file || DraftPath(file) is not string path) { animCount.Text = "No Character Package in This Mod"; return; }
        int req = ++animRequest;
        var mod = editing; string cooked = game.Cooked;
        animCount.Text = "Reading…";
        Task.Run(() => { try { return CostumeAnims.Read(path, file, CostumeAnims.FilesFor(mod, cooked)); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return null; } })
            .ContinueWith(t =>
            {
                if (IsDisposed || req != animRequest) return;
                animData = t.Result;
                FillAnimGrid();
                animLoaded?.Invoke();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>What an animation is for, from its name (absattack_ = a power or attack, defensive_ = being hit …).</summary>
    static string AnimKind(string n)
    {
        n = n.ToLowerInvariant();
        if (n.StartsWith("emote")) return "Emote";
        if (n.StartsWith("idle") || n.StartsWith("fidget")) return "Idle";
        if (n.StartsWith("absattack") || n.StartsWith("attack") || n.StartsWith("power")) return "Attack / Power";
        if (n.StartsWith("defensive") || n.Contains("death") || n.Contains("dead") || n.StartsWith("hit") || n.Contains("knock") || n.Contains("stun")) return "Hit Reaction";
        if (n.StartsWith("interactive")) return "Interaction";
        if (n.StartsWith("blink")) return "Face";
        if (new[] { "run", "walk", "jog", "sprint", "jump", "fall", "land", "fly", "hover", "travel", "turn", "dodge", "ride", "mount" }.Any(n.Contains)) return "Movement";
        return "Other";
    }

    /// <summary>True while the grid is refilled: selection changes then aren't the user's (no preview, no buttons).</summary>
    bool animRefilling;

    void FillAnimGrid()
    {
        if (animGrid == null) return;
        // The selected animation is kept across a refill (Find typed, the list read again): a refill that arrived after a
        // click used to wipe the pick (Kurt, 2026-10-02).
        string? keep = SelectedAnim?.Name;
        animRefilling = true;
        try { FillAnimRows(keep); }
        finally { animRefilling = false; }
        var a = SelectedAnim;
        if (animChange != null) animChange.Enabled = a != null;
        if (animBack != null) animBack.Enabled = a != null && IsSwapped(a);
    }

    void FillAnimRows(string? keep)
    {
        animGrid!.Rows.Clear();
        if (animData == null) { animCount.Text = animPackages.Count == 0 ? "No Character Package in This Mod" : "No Animation List in This Package"; return; }
        // The power each animation belongs to (from the hero's powers, once the 3D view has loaded them).
        var powerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in animPreview?.AllHeroPowers ?? [])
            foreach (string an in p.Animations) powerOf.TryAdd(an, p.Name);
        string[] words = animFind.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int shown = 0, replaced = 0;
        string From(CostumeAnims.Set s) => $"{s.Label}{(s.FromMod ? " (This Mod)" : "")} · {s.Path.Split('.')[^1]}";
        string? file = AnimFile;
        foreach (var a in animData.Anims)
        {
            string kind = AnimKind(a.Name), power = powerOf.GetValueOrDefault(a.Name, ""), from = From(a.From);
            var swap = IsSwapped(a) ? draft.AnimSwaps.FirstOrDefault(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && x.Slot.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) : null;
            if (IsSwapped(a)) from = swap != null ? $"Changed: {swap.Title} · {swap.Animation}" : "Changed";
            string replaces = string.Join(", ", a.Overrides.Select(From).Distinct());
            if (words.Length > 0 && !words.All(w => $"{a.Name} {kind} {power} {from} {replaces}".Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            int i = animGrid.Rows.Add(a.Name, kind, power, from, replaces);
            var row = animGrid.Rows[i];
            row.Tag = a;
            row.Cells["from"].ToolTipText = $"{a.From.PackageName}.upk · {a.From.Path} (set {a.From.Order + 1} of {animData.Sets.Count})";
            if (a.Overrides.Count > 0)
                row.Cells["replaces"].ToolTipText = "This animation replaces the one of the same name in: " + string.Join("; ", a.Overrides.Select(o => $"{o.PackageName}.upk · {o.Path}"));
            if (IsSwapped(a)) { row.Cells["from"].Style.ForeColor = Ui.Packages; row.Cells["from"].Style.SelectionForeColor = Ui.Packages; row.Cells["anim"].Style.ForeColor = Ui.Packages; row.Cells["anim"].Style.SelectionForeColor = Ui.Packages; }
            else if (a.From.Kind == CostumeAnims.Source.Costume) { row.Cells["from"].Style.ForeColor = Ui.Accent; row.Cells["from"].Style.SelectionForeColor = Ui.Accent; }
            if (a.Overrides.Count > 0) replaced++;
            shown++;
        }
        // The grid makes the first row it adds current and selected (before its Tag is set), and a click on the current cell
        // doesn't select it, so the first match of a Find couldn't be picked (Kurt, 2026-10-02; --anim-find-test). After a
        // refill: the kept animation selected again if it's shown, else nothing selected and no current cell.
        var again = keep == null ? null : animGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CostumeAnims.Anim ka && ka.Name.Equals(keep, StringComparison.OrdinalIgnoreCase));
        if (again != null) { animGrid.CurrentCell = again.Cells[0]; animGrid.ClearSelection(); again.Selected = true; }
        else { animGrid.CurrentCell = null; animGrid.ClearSelection(); }
        int changed = animData.Anims.Count(IsSwapped);
        bool unsaved = editing != null && file != null && DraftPath(file) is string dp && !dp.StartsWith(editing.Folder, StringComparison.OrdinalIgnoreCase);
        animCount.Text = $"{shown} of {animData.Anims.Count} Animations · {animData.Sets.Count} Sets" + (animData.Inherited ? " · The Hero's List (This Costume Sets None of Its Own)" : "")
            + (changed > 0 ? $" · {changed} Changed" : "") + (unsaved ? " · Not Saved Yet" : "");
        Ui.Tip(animCount, "Sets in the order the game searches them backwards (the last one with a name wins):\n" + string.Join("\n", animData.Sets.Select(s => $"{s.Order + 1}. {s.Label}{(s.FromMod ? " (this mod)" : "")}: {s.PackageName}.upk · {s.Path}, {s.Sequences.Count} animations")));
    }

    /// <summary>The picker for the selected animation; the choice goes into a work copy of the package (until Save).</summary>
    async void ChangeAnimation()
    {
        if (SelectedAnim is not { } slot || animData == null || editing == null || game == null || AnimFile is not string file) return;
        if (costumesTask == null || costumesTask.IsFaulted) { string root = game.Root; costumesTask = Task.Run(() => Costume.All(root)); }
        animStatus.Text = "Loading the Characters…";
        var costumes = await costumesTask;
        string cooked = game.Cooked;
        var donors = await Task.Run(() => AnimPickerForm.Donors(editing, file, costumes, lib, cooked));
        animStatus.Text = "";
        if (IsDisposed) return;
        AnimPickerForm.Donor? donor; CostumeAnims.Anim? pick;
        using (var f = new AnimPickerForm(slot.Name, donors, cooked) { Try = r => animPreview?.PlayRef(r) })
        {
            if (f.ShowDialog(this) != DialogResult.OK) { animGrid?.ClearSelection(); RestoreRow(slot.Name); return; }
            donor = f.ChosenDonor; pick = f.Chosen;
        }
        if (donor == null || pick?.From.File == null) return;
        await ApplyChange(slot, donor, pick);
    }

    /// <summary>Puts <paramref name="pick"/> (of <paramref name="donor"/>) in place of <paramref name="slot"/>.</summary>
    async Task ApplyChange(CostumeAnims.Anim slot, AnimPickerForm.Donor donor, CostumeAnims.Anim pick)
    {
        if (animData == null || AnimFile is not string file || pick.From.File == null) return;
        // The original's own animation of that name: the change undone.
        if (donor.Kind == "Target" && pick.Name.Equals(slot.Name, StringComparison.OrdinalIgnoreCase)) { if (IsSwapped(slot)) await BackTo(slot); else RestoreRow(slot.Name); return; }
        string src = DraftPath(file)!, cls = animData.Class;
        var inherited = animData.Inherited ? animData.Sets.Select(x => x.Path).ToList() : null;
        var swap = new AnimSwap.Swap(slot.Name, pick.From.File, pick.From.Export, pick.Export);
        var log = new List<string>();
        animStatus.Text = "Copying the Animation…";
        byte[] bytes;
        try { bytes = await Task.Run(() => AnimSwap.Build(src, cls, [swap], log, inherited)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException)
        { animStatus.Text = ""; Dialog.Show(this, $"{donor.Title}'s \"{pick.Name}\" couldn't be put in: {ex.Message}", "Animation Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed) return;
        UseWorkCopy(file, bytes);
        draft.AnimSwaps.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && x.Slot.Equals(slot.Name, StringComparison.OrdinalIgnoreCase));
        draft.AnimSwaps.Add(new AnimSwapEntry { Package = file, Slot = slot.Name, Donor = donor.Mod?.Name ?? donor.File, Animation = pick.Name, Title = donor.Kind == "Target" ? donor.Title.Replace(" (the Original)", "") : donor.Title });
        animStatus.Text = $"\"{slot.Name}\" Now Plays {donor.Title.Replace(" (the Original)", "")} · {pick.Name}";
        ReloadAt(slot.Name);
    }

    /// <summary>Copy From a Character: the picker with tick boxes; the ticked animations go in at once (one rebuild).</summary>
    async void CopyFromCharacter()
    {
        if (animData == null || editing == null || game == null || AnimFile is not string file) return;
        if (costumesTask == null || costumesTask.IsFaulted) { string root = game.Root; costumesTask = Task.Run(() => Costume.All(root)); }
        animStatus.Text = "Loading the Characters…";
        var costumes = await costumesTask;
        string cooked = game.Cooked;
        var donors = await Task.Run(() => AnimPickerForm.Donors(editing, file, costumes, lib, cooked));
        animStatus.Text = "";
        if (IsDisposed || animData == null) return;
        AnimPickerForm.Donor? donor; List<CostumeAnims.Anim> picks;
        using (var f = new AnimPickerForm("", donors, cooked, animData.Anims.Select(a => a.Name), AnimKind) { Try = r => animPreview?.PlayRef(r) })
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            donor = f.ChosenDonor; picks = f.ChosenMany;
        }
        if (donor != null && picks.Count > 0) await ApplyMany(donor, picks);
    }

    /// <summary>Each pick in place of the costume's animation of the same name (the Target's: back to the original).</summary>
    async Task ApplyMany(AnimPickerForm.Donor donor, List<CostumeAnims.Anim> picks)
    {
        if (animData == null || AnimFile is not string file || DraftPath(file) is not string src) return;
        string cls = animData.Class;
        var inherited = animData.Inherited ? animData.Sets.Select(x => x.Path).ToList() : null;
        bool undo = donor.Kind == "Target";
        var swaps = undo ? [] : picks.Where(p => p.From.File != null).Select(p => new AnimSwap.Swap(p.Name, p.From.File!, p.From.Export, p.Export)).ToList();
        var slots = picks.Select(p => p.Name).ToList();
        var log = new List<string>();
        animStatus.Text = undo ? "Changing Back…" : $"Copying {swaps.Count} Animations…";
        byte[]? bytes;
        try
        {
            bytes = await Task.Run(() => undo ? AnimSwap.Unswap(MhoPackageModifier.Package.Open(src), cls, slots, log) : AnimSwap.Build(src, cls, swaps, log, inherited));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException)
        { animStatus.Text = ""; Dialog.Show(this, $"{donor.Title}'s animations couldn't be put in: {ex.Message}", "Animations Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed) return;
        if (bytes == null) { animStatus.Text = "Nothing Was Changed"; return; }
        UseWorkCopy(file, bytes);
        draft.AnimSwaps.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && slots.Contains(x.Slot, StringComparer.OrdinalIgnoreCase));
        if (!undo)
            foreach (var p in picks)
                draft.AnimSwaps.Add(new AnimSwapEntry { Package = file, Slot = p.Name, Donor = donor.Mod?.Name ?? donor.File, Animation = p.Name, Title = donor.Title });
        animStatus.Text = undo ? $"{slots.Count} Animations Are the Original Again" : $"{swaps.Count} Animations Now From {donor.Title}";
        ReloadAt(slots[0]);
    }

    /// <summary>Test hook: Copy From a Character with every animation of the same names (Find = <paramref name="find"/>
    /// narrows them by name or kind; empty: all). Returns the tab's count line after.</summary>
    public async Task<string> AnimTestCopyMany(string donorTitle, string find)
    {
        if (!await WaitAnims() || editing == null || game == null || AnimFile is not string file) return "no animations";
        costumesTask ??= Task.Run(() => Costume.All(game.Root));
        var donors = AnimPickerForm.Donors(editing, file, await costumesTask, lib, game.Cooked);
        var d = donors.FirstOrDefault(x => x.Title.Contains(donorTitle, StringComparison.OrdinalIgnoreCase));
        if (d == null) return "no donor " + donorTitle;
        var names = animData!.Anims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picks = CostumeAnims.Read(d.Path, d.File, CostumeAnims.FilesFor(d.Mod, game.Cooked))?.Anims
            .Where(a => names.Contains(a.Name) && (find.Length == 0 || a.Name.Contains(find, StringComparison.OrdinalIgnoreCase) || AnimKind(a.Name).Contains(find, StringComparison.OrdinalIgnoreCase))).ToList() ?? [];
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        await ApplyMany(d, picks);
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        return $"{picks.Count} picked from {d.Title} | {animCount.Text}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>
    /// For --anim-find-test: Find set to <paramref name="find"/>, then a real click (window messages) on the first match.
    /// Lines: before the click (selected rows, Change on), after it (the selected animation, Change on).
    /// </summary>
    internal async Task<List<string>> FindClickForTest(string find)
    {
        var lines = new List<string>();
        if (!await WaitAnims() || animGrid == null) { lines.Add("FAIL no animations"); return lines; }
        SelectTab(pages.FindIndex(p => p.Title == "Animations"));
        await Task.Delay(500);
        animFind.Text = find;
        await Task.Delay(500);
        if (animGrid.Rows.Count == 0) { lines.Add($"FAIL no rows for \"{find}\""); return lines; }
        string first = ((CostumeAnims.Anim)animGrid.Rows[0].Tag!).Name;
        lines.Add($"after Find \"{find}\": {animGrid.Rows.Count} rows, first {first}, selected {animGrid.SelectedRows.Count}, Change {(animChange!.Enabled ? "on" : "off")}");
        var r = animGrid.GetCellDisplayRectangle(0, 0, false);
        IntPtr lp = (IntPtr)(((r.Top + r.Height / 2) << 16) | (r.Left + r.Width / 2));
        SendMessage(animGrid.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
        SendMessage(animGrid.Handle, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
        await Task.Delay(300);
        bool ok = SelectedAnim?.Name == first && animChange.Enabled;
        lines.Add($"{(ok ? "PASS" : "FAIL")} click on the first match: selected {SelectedAnim?.Name ?? "nothing"}, Change {(animChange.Enabled ? "on" : "off")}");
        if (animGrid.Rows.Count > 1)
        {
            string second = ((CostumeAnims.Anim)animGrid.Rows[1].Tag!).Name;
            var r2 = animGrid.GetCellDisplayRectangle(0, 1, false);
            IntPtr lp2 = (IntPtr)(((r2.Top + r2.Height / 2) << 16) | (r2.Left + r2.Width / 2));
            SendMessage(animGrid.Handle, 0x0201, (IntPtr)1, lp2); SendMessage(animGrid.Handle, 0x0202, IntPtr.Zero, lp2);
            await Task.Delay(300);
            lines.Add($"{(SelectedAnim?.Name == second ? "PASS" : "FAIL")} click on the second row: selected {SelectedAnim?.Name ?? "nothing"} (wanted {second})");
            await Task.Delay(4000);
            lines.Add($"{(SelectedAnim?.Name == second && animChange.Enabled ? "PASS" : "FAIL")} 4 s later (after any late refill): selected {SelectedAnim?.Name ?? "nothing"}, Change {(animChange.Enabled ? "on" : "off")}");
            animFind.Text = "";
            await Task.Delay(300);
            lines.Add($"{(SelectedAnim?.Name == second ? "PASS" : "FAIL")} Find cleared: still selected {SelectedAnim?.Name ?? "nothing"} ({animGrid.Rows.Count} rows)");
        }
        return lines;
    }

    /// <summary>The selected animation back to the costume's own (AnimSwap.Unswap into a work copy).</summary>
    async void BackToOriginal() { if (SelectedAnim is { } slot) await BackTo(slot); }

    async Task BackTo(CostumeAnims.Anim slot)
    {
        if (!IsSwapped(slot) || animData == null || AnimFile is not string file || DraftPath(file) is not string src) return;
        string cls = animData.Class;
        var log = new List<string>();
        byte[]? bytes;
        try { bytes = await Task.Run(() => AnimSwap.Unswap(MhoPackageModifier.Package.Open(src), cls, [slot.Name], log)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException)
        { Dialog.Show(this, $"\"{slot.Name}\" couldn't be changed back: {ex.Message}", "Animation Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (IsDisposed || bytes == null) return;
        UseWorkCopy(file, bytes);
        draft.AnimSwaps.RemoveAll(x => x.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && x.Slot.Equals(slot.Name, StringComparison.OrdinalIgnoreCase));
        animStatus.Text = $"\"{slot.Name}\" Is the Original Again";
        ReloadAt(slot.Name);
    }

    void UseWorkCopy(string file, byte[] bytes)
    {
        animWork ??= Path.Combine(lib.DataFolder, "anim-work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(animWork);
        string path = Path.Combine(animWork, Guid.NewGuid().ToString("N")[..6] + "_" + file);
        File.WriteAllBytes(path, bytes);
        int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (k >= 0) draft.Packages[k] = (file, path);
        RefreshPackages();
    }

    /// <summary>Reads the package again and selects (and plays) <paramref name="name"/>.</summary>
    void ReloadAt(string name)
    {
        string? keep = name;
        void Once() { animLoaded -= Once; RestoreRow(keep); }
        animLoaded += Once;
        LoadAnimations();
    }

    event Action? animLoaded;

    void RestoreRow(string name)
    {
        if (animGrid == null) return;
        foreach (DataGridViewRow r in animGrid.Rows)
            if (r.Tag is CostumeAnims.Anim a && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                animGrid.ClearSelection(); r.Selected = true;
                animGrid.FirstDisplayedScrollingRowIndex = Math.Max(0, r.Index - 3);
                return;
            }
    }

    // ---- Test hooks (--anim-tab-test, scratch libraries only)
    async Task<bool> WaitAnims()
    {
        for (int i = 0; i < 100 && animData == null; i++) await Task.Delay(200);
        return animData != null;
    }

    /// <summary>Test hook: changes <paramref name="slot"/> to <paramref name="anim"/> of the first character whose title
    /// has <paramref name="donorTitle"/> (as Use This Animation would). Returns the slot's From text after.</summary>
    public async Task<string> AnimTestChange(string slot, string donorTitle, string anim)
    {
        if (!await WaitAnims() || editing == null || game == null || AnimFile is not string file) return "no animations";
        var a = animData!.Anims.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (a == null) return "no slot " + slot;
        costumesTask ??= Task.Run(() => Costume.All(game.Root));
        var donors = AnimPickerForm.Donors(editing, file, await costumesTask, lib, game.Cooked);
        var d = donors.FirstOrDefault(x => x.Title.Contains(donorTitle, StringComparison.OrdinalIgnoreCase));
        if (d == null) return "no donor " + donorTitle + " (have: " + string.Join(", ", donors.Take(4).Select(x => x.Kind + " " + x.Title)) + " …)";
        var da = CostumeAnims.Read(d.Path, d.File, CostumeAnims.FilesFor(d.Mod, game.Cooked))?.Anims.FirstOrDefault(x => x.Name.Equals(anim, StringComparison.OrdinalIgnoreCase));
        if (da == null) return $"{d.Title} has no {anim}";
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        await ApplyChange(a, d, da);
        await Task.WhenAny(tcs.Task, Task.Delay(20000));
        return RowFrom(slot) + " | " + animCount.Text + " | donors: " + string.Join(", ", donors.Where(x => x.Pinned).Select(x => x.Kind + "=" + x.Title));
    }

    /// <summary>Test hook: Back to the Original on <paramref name="slot"/>. Returns the slot's From text after.</summary>
    public async Task<string> AnimTestBack(string slot)
    {
        if (!await WaitAnims()) return "no animations";
        var a = animData!.Anims.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (a == null) return "no slot " + slot;
        var tcs = new TaskCompletionSource();
        void Done() { animLoaded -= Done; tcs.TrySetResult(); }
        animLoaded += Done;
        await BackTo(a);
        await Task.WhenAny(tcs.Task, Task.Delay(20000));
        return RowFrom(slot) + " | " + animCount.Text;
    }

    string RowFrom(string slot) => animGrid?.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CostumeAnims.Anim x && x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase))?.Cells["from"].Value as string ?? "(no row)";

    /// <summary>Test hook: the picker for <paramref name="slot"/>, off screen, with <paramref name="donorTitle"/> picked, as PNG.</summary>
    public async Task AnimPickerSnapshot(string slot, string? donorTitle, string png)
    {
        if (!await WaitAnims() || editing == null || game == null || AnimFile is not string file) return;
        costumesTask ??= Task.Run(() => Costume.All(game.Root));
        var donors = AnimPickerForm.Donors(editing, file, await costumesTask, lib, game.Cooked);
        // slot "*": Copy From a Character (tick boxes), with Find = the donor title's second part after '|' if any.
        bool many = slot == "*";
        using var f = new AnimPickerForm(slot, donors, game.Cooked, many ? animData!.Anims.Select(a => a.Name) : null, many ? AnimKind : null) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, 0) };
        f.Show(this);
        await Task.Delay(800);
        f.PickForTest(donorTitle);
        await Task.Delay(4000);
        using var b = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
        b.Save(png);
        f.Close();
    }
}
