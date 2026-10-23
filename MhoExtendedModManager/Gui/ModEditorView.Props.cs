using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Props added to a costume from another character (Kurt, 2026-10-23: Thor's hammer in Captain America's hand for his idles,
/// with Cap's shield on his arm): the Animations tab's Props section. Add a Prop picks a hero and one of their props; it goes
/// into the selected costume package at once (PowerBorrow.AddProp, into a work copy until Save, as the animation changes), with
/// Size / Move / Turn sliders (Move along the global axes, as the prop is held in the frame shown). Save applies them again
/// after the borrowed looks (whose rebuild starts from the package before them), and a borrowed power that shows a held prop
/// uses it instead of spawning its own (Hammer of Storms throws the held hammer).
/// </summary>
sealed partial class ModEditorView
{
    readonly DropDown propList = new() { Width = 320 };
    readonly Label propNote = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 4) };
    readonly CheckBox propPin = new() { Text = "Pivot on the Hand", AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
    LightSlider? apSize, apX, apY, apZ, apTX, apTY, apTZ;
    Button? propRemove;
    bool propShowing;
    /// <summary>The prop's own axes in the character's when the sliders were filled (identity: the 3D view doesn't show it).</summary>
    System.Numerics.Matrix4x4 apAxes = System.Numerics.Matrix4x4.Identity;
    /// <summary>Packages an added prop changed since the last save (the previews read their work copies).</summary>
    readonly HashSet<string> propTouched = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bumped by every add, remove and refit: a prop build that finishes after a newer one started is thrown away
    /// (Kurt, 2026-10-23: a re-apply after a borrowed look's rebuild could put a just-removed hammer back).</summary>
    int propVersion;

    Control PropsSection()
    {
        var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0) };
        var head = new Label { Text = "PROPS", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 4, 0, 2) };
        Ui.Tip(head, "Props this costume holds as well as its own (Thor's hammer in Captain America's hand): they show in every animation, in a slot of their own, and a borrowed power that shows that slot uses them.");
        var add = Ui.FlatButton("Add a Prop", AddPropAsked, "Give this costume another character's prop (a hero's hammer, sword, shield …): it holds it in every animation, on the bone the prop's own character holds it on. Save puts it into the mod.");
        propRemove = Ui.FlatButton("Remove", RemovePropAsked, "Take the selected prop off this costume.");
        propList.Width = (int)(320 * S);
        propList.Format = o => o is AddedPropEntry e ? e.Name : o.ToString() ?? "";
        propList.SelectedIndexChanged += (_, _) => ShowPropFit();
        Ui.Tip(propList, "The prop to fit.");
        LightSlider Slider(string label, float min, float max, float step, Func<float, string> format, string tip)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Mark = 0, Home = () => 0, Format = format, Width = (int)(320 * S), Height = (int)(30 * S), Margin = new Padding(0, 2, 0, 0) };
            Ui.Tip(sl, tip);
            sl.Committed += PropFitCommitted;
            return sl;
        }
        string Units(float v) => $"{v:+0;-0;0}";
        string Deg(float v) => $"{v:+0;-0;0}°";
        apSize = Slider("Size", 0.25f, 3f, 0.01f, v => $"{v * 100:0} %", "The prop's size (100 %: as made). Double-click: back to 100 %.");
        apSize.Home = () => 1; apSize.Mark = 1;
        apX = Slider("Move X", -120, 120, 1, Units, "Moves the prop along the global X axis (red on the 3D view's axis gizmo: the way the character faces), as it's held in the frame shown. Double-click: back to 0.");
        apY = Slider("Move Y", -120, 120, 1, Units, "Moves the prop along the global Y axis (green on the axis gizmo), as it's held in the frame shown. Double-click: back to 0.");
        apZ = Slider("Move Z", -120, 120, 1, Units, "Moves the prop along the global Z axis (blue on the axis gizmo: up), as it's held in the frame shown. Double-click: back to 0.");
        apTX = Slider("Turn X", -180, 180, 1, Deg, "Turns the prop about its own X axis (roll). Double-click: back to 0.");
        apTY = Slider("Turn Y", -180, 180, 1, Deg, "Turns the prop about its own Y axis (pitch). Double-click: back to 0.");
        apTZ = Slider("Turn Z", -180, 180, 1, Deg, "Turns the prop about its own Z axis (yaw). Some props start turned for this hand (Thor's hammer). Double-click: back to that start.");
        Ui.Tip(propPin, "The animations this costume took from another character (Animations tab: Thor's idles and fidgets) stop moving the bone this prop hangs on, so it stays in the hand and turns with it (Thor's animations move his palm bone). Look ▾ → Prop Pivots shows the pivot.");
        propPin.CheckedChanged += (_, _) => PinChanged();
        box.Controls.AddRange([head, Toolbar(add, propRemove), propList, propPin, apSize, apX, apY, apZ, apTX, apTY, apTZ, propNote]);
        FillPropList();
        return box;
    }

    /// <summary>The selected package's added props into the list.</summary>
    void FillPropList()
    {
        string? keep = (propList.SelectedItem as AddedPropEntry)?.Class;
        var props = draft.AddedProps.Where(p => AnimFile != null && p.Package.Equals(AnimFile, StringComparison.OrdinalIgnoreCase)).ToList();
        propShowing = true;
        try
        {
            propList.Items.Clear();
            propList.Items.AddRange([.. props.Cast<object>()]);
            int at = keep == null ? 0 : Math.Max(0, props.FindIndex(p => p.Class.Equals(keep, StringComparison.OrdinalIgnoreCase)));
            if (props.Count > 0) propList.SelectedIndex = at;
        }
        finally { propShowing = false; }
        ShowPropFit();
    }

    void ShowPropFit()
    {
        var e = propList.SelectedItem as AddedPropEntry;
        bool on = e != null;
        propList.Visible = on;
        foreach (var sl in new[] { apSize, apX, apY, apZ, apTX, apTY, apTZ }) if (sl != null) sl.Visible = on;
        propPin.Visible = on;
        if (propRemove != null) propRemove.Enabled = on;
        propNote.Text = on ? "" : "No props added to this costume.";
        if (e == null || apSize == null || apX == null || apY == null || apZ == null || apTX == null || apTY == null || apTZ == null) return;
        propShowing = true;
        try
        {
            var f = e.Fit;
            apSize.Value = f.Scale is float s && s > 0 ? s : 1;
            // global axes: the prop's own move shown along the character's axes, as it's held now
            apAxes = (animPreview?.Visible == true ? animPreview.PropFrame(e.Class) : powerPreview?.PropFrame(e.Class)) is { } pf ? Turn(pf) : System.Numerics.Matrix4x4.Identity;
            var w = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(f.X, f.Y, f.Z), apAxes);
            apX.Value = w.X; apY.Value = w.Y; apZ.Value = w.Z;
            float dz = PowerBorrow.DefaultTurnZ(e.Class);
            apTX.Value = f.TurnX; apTY.Value = f.TurnY; apTZ.Value = f.TurnZ;
            apTZ.Home = () => dz; apTZ.Mark = dz;
            propPin.Checked = f.PinPivot;
        }
        finally { propShowing = false; }
    }

    void PropFitCommitted()
    {
        if (propShowing || propList.SelectedItem is not AddedPropEntry e || apSize == null || apX == null || apY == null || apZ == null || apTX == null || apTY == null || apTZ == null) return;
        var own = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(apX.Value, apY.Value, apZ.Value), System.Numerics.Matrix4x4.Transpose(apAxes));
        e.Fit.Scale = Math.Abs(apSize.Value - 1) > 0.005f ? MathF.Round(apSize.Value, 2) : null;
        e.Fit.X = MathF.Round(own.X, 2); e.Fit.Y = MathF.Round(own.Y, 2); e.Fit.Z = MathF.Round(own.Z, 2);
        e.Fit.TurnX = apTX.Value; e.Fit.TurnY = apTY.Value; e.Fit.TurnZ = apTZ.Value;
        _ = ApplyPropsTo(e.Package);
    }

    // ---- Test hooks (--prop-tab-test, scratch libraries only)
    /// <summary>Picks the Animations tab's package whose name has <paramref name="part"/>.</summary>
    internal void PropTestPackage(string part)
    {
        if (animPackage == null) return;
        int i = animPackages.FindIndex(f => f.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) animPackage.SelectedIndex = i;
        if (animPreview != null) { animPreview.TestMeshPart = part; animPreview.ReloadOverrides(); }
    }
    internal string PropTestState() => $"list [{string.Join(", ", draft.AddedProps.Select(p => p.Package + ":" + p.Class))}] package {AnimFile} draft {DraftPath(AnimFile ?? "") ?? "-"} | preview props: {animPreview?.PropsForTest ?? "(no preview)"} | playing: {animPreview?.CurrentAnimationForTest ?? "-"}";
    /// <summary>Tests: the Animations tab's preview plays <paramref name="name"/>.</summary>
    internal void PropTestPlay(string name) => animPreview?.PlayAnimation(name, false);
    /// <summary>Tests: the Animations tab's row of <paramref name="slot"/> selected (it plays as a click does).</summary>
    internal void PropTestSelect(string slot)
    {
        if (animGrid?.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is CostumeAnims.Anim x && x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase)) is not { } row) return;
        animGrid.ClearSelection();
        row.Selected = true;
        animGrid.CurrentCell = row.Cells[0];
    }
    internal async Task PropTestFit(float size)
    {
        if (propList.SelectedItem is not AddedPropEntry e) return;
        e.Fit.Scale = size;
        await ApplyPropsTo(e.Package);
        await Task.Delay(6000);
    }
    /// <summary>Tests: slider steps in quick succession (each a new apply and preview reload before the last one is in).</summary>
    internal async Task PropTestFitQuick(params float[] sizes)
    {
        if (propList.SelectedItem is not AddedPropEntry e) return;
        foreach (float s in sizes) { e.Fit.Scale = s; _ = ApplyPropsTo(e.Package); await Task.Delay(250); }
        await Task.Delay(8000);
    }
    /// <summary>Tests: Pivot on the Hand ticked or not on the selected prop, as a click does.</summary>
    internal async Task PropTestPin(bool on) { propPin.Checked = on; await Task.Delay(6000); }
    /// <summary>Tests: the selected prop's pin as the editor holds it (the box, the draft's entry).</summary>
    internal string PropTestPinState() => $"box {propPin.Checked}, draft [{string.Join(", ", draft.AddedProps.Select(p => p.Package + ":" + p.Fit.PinPivot))}]";
    internal async Task PropTestRemove()
    {
        if (propList.Items.Count > 0) propList.SelectedIndex = 0;
        RemovePropAsked();
        await Task.Delay(6000);
    }
    internal async Task PropTestAdd(string hero, string cls)
    {
        if (game == null || AnimFile is not string file) return;
        string from = $"UC__MarvelPlayer_{hero}_SF.upk";
        var e = new AddedPropEntry { Package = file, From = from, Class = cls, Name = $"{hero}: {cls}", Slot = PowerBorrow.PropSlot(StockFiles.For(game.Cooked, from), cls), Fit = new PropFit { Class = cls, TurnZ = PowerBorrow.DefaultTurnZ(cls) } };
        draft.AddedProps.Add(e);
        if (!await ApplyPropsTo(file)) draft.AddedProps.Remove(e);
        FillPropList();
        await Task.Delay(6000);
    }

    /// <summary>Pivot on the Hand ticked or not: the costume's borrowed animations stop or start moving the prop's bone.</summary>
    async void PinChanged()
    {
        if (propShowing || propList.SelectedItem is not AddedPropEntry e || e.Fit.PinPivot == propPin.Checked) return;
        e.Fit.PinPivot = propPin.Checked;
        if (!e.Fit.PinPivot && DraftPath(e.Package) is string src)
        {
            // let go: the bone back in the sets' positions, then the props as they are
            int v = ++propVersion;
            var log = new List<string>();
            byte[] bytes;
            try { bytes = await Task.Run(() => { byte[] b = File.ReadAllBytes(src); return PowerBorrow.PinPropBones(b, PowerBorrow.HeldPropBones(b, e.Class), false, log); }); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, ex.Message, "Pivot Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (IsDisposed || v != propVersion) return;
            UseWorkCopy(e.Package, bytes);
            OnBorrowBase(e.Package, b => { byte[] k = File.ReadAllBytes(b); return PowerBorrow.PinPropBones(k, PowerBorrow.HeldPropBones(k, e.Class), false, log); });
        }
        if (!await ApplyPropsTo(e.Package) || IsDisposed) return;
        // the list read again: a changed row plays the sets as pinned now (it held the ones read before)
        if (SelectedAnim is { } sel && AnimFile is string af && af.Equals(e.Package, StringComparison.OrdinalIgnoreCase)) ReloadAt(sel.Name);
    }

    /// <summary>Add a Prop: a hero, then one of their props; added to the selected costume package.</summary>
    async void AddPropAsked()
    {
        if (game == null || AnimFile is not string file) return;
        using var f = new AddPropForm(game.Cooked);
        if (f.ShowDialog(this) != DialogResult.OK || f.Chosen is not { } pick) return;
        if (draft.AddedProps.Any(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Class.Equals(pick.Class, StringComparison.OrdinalIgnoreCase)))
        { Dialog.Show(this, $"{pick.Name} is on this costume already.", "Prop Not Added"); return; }
        var e = new AddedPropEntry
        {
            Package = file, From = pick.File, Class = pick.Class, Name = pick.Name,
            Slot = PowerBorrow.PropSlot(StockFiles.For(game.Cooked, pick.File), pick.Class),
            Fit = new PropFit { Class = pick.Class, TurnZ = PowerBorrow.DefaultTurnZ(pick.Class) },
        };
        draft.AddedProps.Add(e);
        if (!await ApplyPropsTo(file)) { draft.AddedProps.Remove(e); return; }
        FillPropList();
        propList.SelectedIndex = propList.Items.IndexOf(e);
    }

    async void RemovePropAsked()
    {
        if (propList.SelectedItem is not AddedPropEntry e || DraftPath(e.Package) is not string src) return;
        int v = ++propVersion;
        draft.AddedProps.Remove(e);
        var log = new List<string>();
        byte[] bytes;
        try { bytes = await Task.Run(() => PowerBorrow.RemoveProp(File.ReadAllBytes(src), e.Class, log)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { draft.AddedProps.Add(e); Dialog.Show(this, "The prop couldn't be taken off: " + ex.Message, "Prop Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (IsDisposed || v != propVersion) return;
        UseWorkCopy(e.Package, bytes);
        propTouched.Add(e.Package);
        // checked: the package's props without it
        bool still = DraftPath(e.Package) is string now && (ModMeshes.AttachmentClasses(now) ?? []).Contains(e.Class, StringComparer.OrdinalIgnoreCase);
        FillPropList();
        propNote.Text = still ? $"{e.Name} Is Still in the Package (Please Report This)" : $"{e.Name} Taken Off";
        ReloadPropPreviews();
    }

    /// <summary>The package's added props applied to its current copy (again: AddProp keeps what's there), into a work copy;
    /// the previews read it. False (with a message) when it can't be done.</summary>
    async Task<bool> ApplyPropsTo(string file)
    {
        if (game == null || DraftPath(file) is not string src) return false;
        int v = ++propVersion;
        var props = draft.AddedProps.Where(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        string cooked = game.Cooked;
        var log = new List<string>();
        propNote.Text = "Putting the Prop In…";
        byte[] bytes;
        try
        {
            bytes = await Task.Run(() =>
            {
                byte[] b = File.ReadAllBytes(src);
                foreach (var p in props) b = PowerBorrow.AddProp(b, p, StockFiles.For(cooked, p.From), log);
                return b;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException or ArgumentException)
        {
            propNote.Text = "";
            Dialog.Show(this, "The prop couldn't be put in: " + ex.Message, "Prop Not Added", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (IsDisposed) return false;
        if (v != propVersion) return true;   // (a newer add / remove / refit started meanwhile: its result counts)
        UseWorkCopy(file, bytes);
        propTouched.Add(file);
        propNote.Text = "";
        ReloadPropPreviews();
        return true;
    }

    void ReloadPropPreviews()
    {
        if (animPreview != null) { animPreview.OverrideFiles = PreviewOverrides; animPreview.ReloadOverrides(); }
        if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
    }

    /// <summary>After a borrowed look's rebuild (which starts from the packages before it): the added props put in again.</summary>
    async void ReapplyProps()
    {
        foreach (string f in draft.AddedProps.Select(p => p.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) await ApplyPropsTo(f);
    }

    /// <summary>Save: every added prop applied again to its package as it is now (after the borrowed looks). The work folder.</summary>
    string? ApplyAddedProps(List<string> log)
    {
        if (draft.AddedProps.Count == 0 || game == null) return null;
        string work = Path.Combine(lib.DataFolder, "props-work-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        foreach (var g in draft.AddedProps.GroupBy(p => p.Package, StringComparer.OrdinalIgnoreCase))
        {
            if (DraftPath(g.Key) is not string src) throw new InvalidDataException($"{g.Key} isn't in the mod");
            byte[] b = File.ReadAllBytes(src);
            foreach (var p in g) b = PowerBorrow.AddProp(b, p, StockFiles.For(game.Cooked, p.From), log);
            string path = Path.Combine(work, g.Key);
            File.WriteAllBytes(path, b);
            int k = draft.Packages.FindIndex(x => x.File.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (g.Key, path);
        }
        return work;
    }
}

/// <summary>Add a Prop: a hero (every hero base package in the game) and one of its props (PowerBorrow.PropClasses).</summary>
sealed class AddPropForm : Form
{
    public sealed record Pick(string File, string Class, string Name);
    public Pick? Chosen { get; private set; }
    readonly DropDown heroBox = new() { Width = 420 }, propBox = new() { Width = 420 };
    readonly string cooked;

    public AddPropForm(string cooked)
    {
        this.cooked = cooked;
        Text = "Add a Prop";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new Label { Text = "Add a Prop From a Character", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
        t.Controls.Add(head, 0, 0); t.SetColumnSpan(head, 2);
        var hint = new Label { Text = "The costume holds it in every animation, on the bone its own character holds it on. Size, move and turn it in the Props section afterwards.", AutoSize = true, Tag = "subtle", MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 10) };
        t.Controls.Add(hint, 0, 1); t.SetColumnSpan(hint, 2);
        t.Controls.Add(new Label { Text = "Hero", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 2);
        t.Controls.Add(heroBox, 1, 2);
        t.Controls.Add(new Label { Text = "Prop", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 3);
        t.Controls.Add(propBox, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Add nothing (Esc)."),
            Ui.AccentButton("Add", Choose, tip: "Give the costume this prop (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 4); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        Ui.Tip(heroBox, "The character whose prop to take.");
        Ui.Tip(propBox, "One of the character's props (its own first).");
        var heroes = Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(f => f.Split('_').Length == 5 && !f.Contains("bak", StringComparison.OrdinalIgnoreCase)).Select(f => f.Split('_')[3])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => AutoTags.DisplayName(h) ?? h, StringComparer.OrdinalIgnoreCase).ToList();
        heroBox.Format = o => AutoTags.DisplayName((string)o) ?? (string)o;
        heroBox.Items.AddRange([.. heroes]);
        heroBox.SelectedIndexChanged += (_, _) => LoadProps();
        propBox.Format = o => o is Pick p ? p.Name.Split(": ")[^1] : o.ToString() ?? "";
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; Choose(); }
        };
        Shown += (_, _) => { int thor = heroes.FindIndex(h => h.Equals("Thor", StringComparison.OrdinalIgnoreCase)); heroBox.SelectedIndex = Math.Max(0, thor); };
        Theme.Apply(this, MhoPackageModifier.Gui.Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 600, 300);
    }

    void LoadProps()
    {
        if (heroBox.SelectedItem is not string hero) return;
        string file = $"UC__MarvelPlayer_{hero}_SF.upk";
        propBox.Items.Clear();
        List<string> classes;
        try { classes = PowerBorrow.PropClasses(StockFiles.For(cooked, file)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { classes = []; }
        string heroName = AutoTags.DisplayName(hero) ?? hero;
        foreach (string c in classes) propBox.Items.Add(new Pick(file, c, $"{heroName}: {Nice(c)}"));
        if (propBox.Items.Count > 0) propBox.SelectedIndex = 0;
    }

    /// <summary>marvelattachment_thorhammer → Thorhammer.</summary>
    static string Nice(string cls)
    {
        string n = cls;
        foreach (string pre in new[] { "marvelattachmentanimated_", "marvelattachment_" }) if (n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { n = n[pre.Length..]; break; }
        n = n.Replace('_', ' ');
        return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n[1..] : n;
    }

    void Choose()
    {
        if (propBox.SelectedItem is not Pick p) return;
        Chosen = p;
        DialogResult = DialogResult.OK;
    }
}
