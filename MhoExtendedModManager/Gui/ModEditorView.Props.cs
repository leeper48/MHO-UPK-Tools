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
    /// <summary>In This Animation (phase 2): the selected prop in the selected animation only.</summary>
    AnimFitBlock? animBlock;
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
        Ui.Tip(head, "Props this costume holds as well as its own (Thor's hammer in Captain America's hand): they show in every animation, in a slot of their own, and a borrowed power that shows that slot uses them. They're added, removed and fitted on the Model tab; here, how they move in the animations.");
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
        propPinButton = Icons.PivotToggle(propPin, "The animations this costume took from another character (Animations tab: Thor's idles and fidgets) hold the prop as that character holds it: in the hand where they hold it, swinging where theirs swings (Thor's fidget02 swings the hammer by its strap). Worked out from that character's own animation and baked into the copies. Look ▾ → Prop Pivots shows the pivot. The same setting as the Model and Powers tabs' button.", S);
        propPinButton.Margin = new Padding((int)(6 * S), 0, 0, 0);
        propPin.CheckedChanged += (_, _) => PinChanged();
        animBlock = new AnimFitBlock(S, "The prop moved and turned further in one animation (the row selected above), for a pose that wants it elsewhere. Needs an animation from another character (a Changed row); with Pivot on the Hand or without.",
            () => { var e = propList.SelectedItem as AddedPropEntry; AnimFitSlot(e, out string why); return (e, SelectedAnim?.Name, why); }, SetAnimFit, c => Toolbar(c), gizmo: true);
        animBlock.GizmoTargetChanged += () => PickAnimGizmo(thisAnimation: true);
        // the fit (left) and this animation's adjustment (right), side by side so the list above keeps its room
        var fitCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        fitCol.Controls.AddRange([apSize, apX, apY, apZ, apTX, apTY, apTZ, AnimGizmoButtons()]);
        var animCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding((int)(24 * S), 0, 0, 0) };
        animCol.Controls.Add(animBlock.Root);
        var cols = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        cols.Controls.AddRange([fitCol, animCol]);
        // added, removed and fitted on the Model tab (Kurt, 2026-10-27: what the character holds there, how it moves here);
        // the buttons and sliders stay for the tests and the Powers tab's held props, hidden
        add.Visible = false; propRemove.Visible = false;
        var toModel = Ui.FlatButton("Set Up Props on the Model Tab", OpenModelTab, "Props are added, removed and fitted on the Model tab (Target's Props, and ◀ ▶ under its preview). Here: its fit, Pivot on the Hand and In This Animation (the same as on the Model and Powers tabs).");
        box.Controls.AddRange([head, Toolbar(add, propRemove, toModel), propList, cols, propNote]);
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
        // (the fit here too, Kurt 2026-10-30: the same as the Model and Powers tabs')
        foreach (var sl in new[] { apSize, apX, apY, apZ, apTX, apTY, apTZ }) if (sl != null) sl.Visible = on;
        if (animFitButtons != null) animFitButtons.Visible = on;
        HookAnimGizmo();
        propPin.Visible = on;
        if (propRemove != null) propRemove.Enabled = on;
        ShowAnimFit();
        propNote.Text = on ? "" : "No props added to this costume (Add a Prop is on the Model tab).";
        if (e == null || apSize == null || apX == null || apY == null || apZ == null || apTX == null || apTY == null || apTZ == null) return;
        propShowing = true;
        try
        {
            var f = e.Fit;
            apSize.Value = f.Scale is float s && s > 0 ? s : 1;
            // global axes: the prop's own move shown along the character's axes, as it's held now
            apAxes = AnimHeldFrame(e.Class) is { } pf ? Turn(WithoutAnimFit(e, pf)) : System.Numerics.Matrix4x4.Identity;
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
        PropFitsChanged(e, from: "anims");
        _ = ApplyPropsTo(e.Package);
    }

    // ---- In This Animation (phase 2) --------------------------------------------------------------------------------------------

    /// <summary>The selected animation's slot when the selected prop can be adjusted in it (Pivot on the Hand, an animation
    /// from another character), else null; <paramref name="why"/> says what's missing.</summary>
    string? AnimFitSlot(AddedPropEntry? e, out string why)
    {
        why = "";
        string? slot = SelectedAnim?.Name;
        if (e == null) return null;
        if (slot == null || AnimFile is not string file || DonorsOf(file)(slot) == null) { why = "Pick an animation changed to another character's (a Changed row) to adjust the prop in it only."; return null; }
        return slot;
    }

    static PropAnimFit? AnimFitOf(AddedPropEntry e, string? slot) => AnimFitBlock.Of(e, slot);

    /// <summary>A prop's frame as shown, turned back by the shown animation's own adjustment (its turn about the character's
    /// axes), for the fit's axes.</summary>
    System.Numerics.Matrix4x4 WithoutAnimFit(AddedPropEntry e, System.Numerics.Matrix4x4 frame)
    {
        if (AnimFitOf(e, animPreview?.Visible == true ? animPreview.CurrentAnimationName : null) is not { IsZero: false } a) return frame;
        return Turn(frame) * System.Numerics.Matrix4x4.Transpose(a.Turn());
    }

    void ShowAnimFit() => animBlock?.Show();

    void SetAnimFit(AddedPropEntry e, string slot, PropAnimFit? a)
    {
        var list = e.Anims ?? [];
        list.RemoveAll(x => x.Slot.Equals(slot, StringComparison.OrdinalIgnoreCase));
        if (a is { IsZero: false }) list.Add(a);
        e.Anims = list.Count > 0 ? list : null;
        PropFitsChanged(e, from: "");
        _ = ApplyPropsTo(e.Package);
    }

    /// <summary>
    /// An added prop's fit or one of its animations' adjustments changed in one tab: the others show it (Kurt, 2026-10-30:
    /// the fit and In This Animation in the Model, Animations and Powers tabs, kept in step). <paramref name="from"/>: the tab
    /// it was changed in ("model", "anims", "powers"), whose fit sliders already show it.
    /// </summary>
    void PropFitsChanged(AddedPropEntry e, string from)
    {
        if (from != "anims" && ReferenceEquals(propList.SelectedItem, e)) ShowPropFit();
        else animBlock?.Show();
        if (from != "powers" && propPick.SelectedItem is PowerBorrow.SpawnedProp sp && HeldAs(sp.Class) == e && powerList.SelectedItem is Fx.PowerList.Power pw && !borrowBuilding)
            ShowFits(BorrowOf(pw.Prototype));
        if (powerAnimBlock?.Root.Visible == true) powerAnimBlock.Show();
        modelPage?.AddedFitChanged(e, refit: from != "model");
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
    List<string> propLastLog = [];
    internal string PropTestLog() => string.Join(" / ", propLastLog);
    internal string PropTestPinState() => $"box {propPin.Checked}, draft [{string.Join(", ", draft.AddedProps.Select(p => p.Package + ":" + p.Fit.PinPivot))}]";
    /// <summary>Tests: an In This Animation adjustment on <paramref name="slot"/> (in the bone's own frame), applied.</summary>
    internal async Task PropTestAnimFit(string slot, float x, float y, float z, float turnX)
    {
        if (propList.SelectedItem is not AddedPropEntry e) return;
        PropTestSelect(slot);
        await Task.Delay(1500);
        SetAnimFit(e, slot, new PropAnimFit { Slot = slot, X = x, Y = y, Z = z, TurnX = turnX });
        await Task.Delay(8000);
        ShowAnimFit();
    }
    /// <summary>Tests: an adjustment on any animation (a borrowed power's too), applied.</summary>
    internal async Task PropTestAnimFitAny(string slot, float x, float y, float z, float turnX)
    {
        if (propList.SelectedItem is not AddedPropEntry e) return;
        SetAnimFit(e, slot, new PropAnimFit { Slot = slot, X = x, Y = y, Z = z, TurnX = turnX });
        await Task.Delay(12000);
    }
    internal string PropTestAnimFitState() => $"{animBlock?.State()}, anims [{string.Join(", ", (propList.SelectedItem as AddedPropEntry)?.Anims?.Select(a => $"{a.Slot} {a.X}/{a.Y}/{a.Z} {a.TurnX}°") ?? [])}]";
    /// <summary>Tests: the selected prop's fit set (its own move along X, a size), applied; the prop's place in the frame shown.</summary>
    internal async Task<string> PropTestFitMove(float x, float size)
    {
        if (propList.SelectedItem is not AddedPropEntry e) return "no prop";
        string before = animPreview?.PropFrame(e.Class)?.Translation.ToString("0.0", null) ?? "-";
        e.Fit.X = x; e.Fit.Scale = size;
        await ApplyPropsTo(e.Package);
        await Task.Delay(8000);
        return $"pivot {before} -> {animPreview?.PropFrame(e.Class)?.Translation.ToString("0.0", null) ?? "-"}, preview props {animPreview?.PropsForTest}";
    }
    /// <summary>Tests: the fit's Move X slider set and let go (as a drag does), applied; the fit as stored after.</summary>
    internal async Task<string> PropTestFitSlider(float moveX)
    {
        if (propList.SelectedItem is not AddedPropEntry e || apX == null) return "no prop";
        string before = $"{e.Fit.X}/{e.Fit.Y}/{e.Fit.Z} x{e.Fit.Scale}";
        apX.Value = moveX;
        PropFitCommitted();
        await Task.Delay(8000);
        return $"fit {before} -> {e.Fit.X}/{e.Fit.Y}/{e.Fit.Z} x{e.Fit.Scale} (shown {apX.Value}), propShowing {propShowing}, preview props {animPreview?.PropsForTest}";
    }
    /// <summary>Tests: the fit's Size slider set and let go, applied; the fit, the build's log and the preview's props after.</summary>
    internal async Task<string> PropTestSizeSlider(float size)
    {
        if (propList.SelectedItem is not AddedPropEntry e || apSize == null) return "no prop";
        string before = $"x{e.Fit.Scale} props {animPreview?.PropsForTest}";
        apSize.Value = size;
        PropFitCommitted();
        await Task.Delay(9000);
        return $"{before} -> x{e.Fit.Scale} props {animPreview?.PropsForTest} | log {PropTestLog()}";
    }
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
        await SetPin(e, propPin.Checked);
    }

    /// <summary>Both Pivot on the Hand boxes showing a held prop's setting (the Animations tab's and the Powers tab's are one
    /// setting, but each box was only read when its panel was filled: Kurt, 2026-10-30, they disagreed).</summary>
    void SyncPinBoxes(AddedPropEntry e)
    {
        if (propList.SelectedItem == e && propPin.Checked != e.Fit.PinPivot)
        {
            propShowing = true;
            try { propPin.Checked = e.Fit.PinPivot; } finally { propShowing = false; }
        }
        modelPage?.ShowAddedPin(e);
        if (propPick.SelectedItem is PowerBorrow.SpawnedProp sp && HeldAs(sp.Class) == e && pinPivot.Checked != e.Fit.PinPivot)
        {
            vShowing = true;
            try { pinPivot.Checked = e.Fit.PinPivot; } finally { vShowing = false; }
        }
    }

    /// <summary>An added prop's Pivot on the Hand set (the Animations tab's box, or the Powers tab's for a held prop).</summary>
    async Task SetPin(AddedPropEntry e, bool on)
    {
        e.Fit.PinPivot = on;
        SyncPinBoxes(e);
        if (!e.Fit.PinPivot && DraftPath(e.Package) is string src)
        {
            // let go: the bone back in the sets' positions, then the props as they are
            int v = ++propVersion;
            var log = new List<string>();
            byte[] bytes;
            var letGo = LetGo(e.Package, src);
            try { bytes = await Task.Run(() => { byte[] b = File.ReadAllBytes(src); return letGo(b, PowerBorrow.HeldPropBones(b, e.Class), log); }); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, ex.Message, "Pivot Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (IsDisposed || v != propVersion) return;
            UseWorkCopy(e.Package, bytes);
            OnBorrowBase(e.Package, b => { byte[] k = File.ReadAllBytes(b); return LetGo(e.Package, b)(k, PowerBorrow.HeldPropBones(k, e.Class), log); });
            // the borrowed powers' animations in the hero's package: their own keys back too
            if (Borrows(e.Package) is { } bw && !bw.Anims.File.Equals(e.Package, StringComparison.OrdinalIgnoreCase))
            {
                byte[]? bb = null;
                try { bb = await Task.Run(() => PropHold.Apply(File.ReadAllBytes(bw.Path), PowerBorrow.HeldPropBones(bytes, e.Class), false, bw.Anims.Donors, PowerBorrow.HeroSkeleton(bw.Path), log, onlyKnown: true)); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { log.Add(ex.Message); }
                if (bb != null && !IsDisposed && v == propVersion) { UseWorkCopy(bw.Anims.File, bb); propTouched.Add(bw.Anims.File); }
            }
        }
        if (!await ApplyPropsTo(e.Package) || IsDisposed) return;
        // the list read again: a changed row plays the sets as pinned now (it held the ones read before)
        if (SelectedAnim is { } sel && AnimFile is string af && af.Equals(e.Package, StringComparison.OrdinalIgnoreCase)) ReloadAt(sel.Name);
    }

    /// <summary>A copied animation's source in <paramref name="package"/> (the Animations tab's change of that slot): the
    /// donor's package and animation, else null.</summary>
    /// <remarks>Read on the UI thread: the result is a lookup the background build can call.</remarks>
    Func<string, PropHold.Donor?> DonorsOf(string package)
    {
        var map = new Dictionary<string, PropHold.Donor>(StringComparer.OrdinalIgnoreCase);
        if (game != null)
            foreach (var s in draft.AnimSwaps.Where(x => x.Package.Equals(package, StringComparison.OrdinalIgnoreCase)))
            {
                if (!s.Donor.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)) continue;   // (a mod's own package: not known here)
                string path = StockFiles.For(game.Cooked, s.Donor);
                if (File.Exists(path)) map[s.Slot] = new PropHold.Donor(path, s.Animation);
            }
        return slot => map.TryGetValue(slot, out var d) ? d : null;
    }

    /// <summary>Pivot on the Hand for <paramref name="package"/> (its skeleton read from <paramref name="path"/>): the prop's
    /// bones held in the copied animations, baked in (PropHold).</summary>
    Func<byte[], AddedPropEntry, IReadOnlyList<string>, List<string>, byte[]> Hold(string package, string path)
    {
        var swaps = DonorsOf(package);
        // (the hero's own package can hold a borrowed power's animations too: their source is the donor hero's)
        var borrowed = BorrowAnims(package) is { } ba && ba.File.Equals(package, StringComparison.OrdinalIgnoreCase) ? ba.Donors : null;
        Func<string, PropHold.Donor?> donors = slot => swaps(slot) ?? borrowed?.Invoke(slot);
        return (b, e, bones, l) => PropHold.Apply(b, bones, e.Fit.PinPivot, donors, PowerBorrow.HeroSkeleton(path), l, PowerBorrow.FitMatrix(e), ExtraOf(e));
    }

    /// <summary>A prop's In This Animation adjustments, by animation (its slot).</summary>
    static Func<string, (System.Numerics.Matrix4x4 Turn, System.Numerics.Vector3 Move)?> ExtraOf(AddedPropEntry e)
    {
        var anims = (e.Anims ?? []).Where(a => !a.IsZero).ToDictionary(a => a.Slot, a => (a.Turn(), a.Move), StringComparer.OrdinalIgnoreCase);
        return slot => anims.TryGetValue(slot, out var m) ? m : null;
    }

    /// <summary>
    /// The hero's own package holding the borrowed powers' animations of this costume's hero (Kurt, 2026-10-23: Hammer of
    /// Storms on Captain America is in UC__MarvelPlayer_CaptainAmerica_SF, its costumes play it from there), with their
    /// sources (the donor hero's animations of the same names: a borrowed power keeps its animations' names), else null.
    /// </summary>
    (string File, Func<string, PropHold.Donor?> Donors)? BorrowAnims(string costumeFile)
    {
        if (game == null) return null;
        var borrows = draft.PowerBorrows.Where(b => b.Hero != null && b.DonorHero != null && costumeFile.StartsWith($"UC__MarvelPlayer_{b.Hero}_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (borrows.Count == 0) return null;
        string baseFile = $"UC__MarvelPlayer_{borrows[0].Hero}_SF.upk";
        if (DraftPath(baseFile) == null) return null;
        var donorPkgs = borrows.Select(b => StockFiles.For(game.Cooked, $"UC__MarvelPlayer_{b.DonorHero}_SF.upk")).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, PropHold.Donor?>(StringComparer.OrdinalIgnoreCase);
        return (baseFile, slot => cache.GetOrAdd(slot, sl => donorPkgs.Where(p => ModAnimations.Named(p, -1, null, sl) != null).Select(p => new PropHold.Donor(p, sl)).FirstOrDefault()));
    }

    ((string File, Func<string, PropHold.Donor?> Donors) Anims, string Path)? Borrows(string costumeFile)
        => BorrowAnims(costumeFile) is { } ba && DraftPath(ba.File) is string p ? (ba, p) : null;

    /// <summary>The borrowed powers' sets in the hero's package holding this costume's pinned props as the costume's own
    /// animations do (and each power animation's own adjustment); null when there's nothing to do.</summary>
    /// <param name="borrows">BorrowAnims(costumeFile) and the hero's package's path, read on the UI thread.</param>
    static byte[]? HoldInBorrows(string costumeFile, byte[] costume, IReadOnlyList<AddedPropEntry> props, List<string> log, ((string File, Func<string, PropHold.Donor?> Donors) Anims, string Path)? borrows, out string? baseFile)
    {
        baseFile = null;
        var pinned = props.Where(PowerBorrow.NeedsHold).ToList();   // (pinned, or adjusted in one of its animations)
        if (pinned.Count == 0 || borrows is not { } bw || bw.Anims.File.Equals(costumeFile, StringComparison.OrdinalIgnoreCase)) return null;
        var ba = bw.Anims; string basePath = bw.Path;
        baseFile = ba.File;
        byte[] b = File.ReadAllBytes(basePath);
        var skeleton = PowerBorrow.HeroSkeleton(basePath);
        foreach (var p in pinned) b = PropHold.Apply(b, PowerBorrow.HeldPropBones(costume, p.Class), p.Fit.PinPivot, ba.Donors, skeleton, log, PowerBorrow.FitMatrix(p), ExtraOf(p), onlyKnown: true);
        return b;
    }

    /// <summary>Pivot on the Hand off: the copied animations' own keys of the prop's bones back (PropHold).</summary>
    Func<byte[], IReadOnlyList<string>, List<string>, byte[]> LetGo(string package, string path)
    {
        var donors = DonorsOf(package);
        return (b, bones, l) => PropHold.Apply(b, bones, false, donors, PowerBorrow.HeroSkeleton(path), l);
    }

    /// <summary>Add a Prop: a hero, then one of their props; added to the selected costume package.</summary>
    void OpenModelTab()
    {
        int t = Enumerable.Range(0, TabCount).FirstOrDefault(i => TabTitle(i) == "Model", -1);
        if (t >= 0) SelectTab(t);
    }

    async void AddPropAsked()
    {
        if (AnimFile is not string file || await AddPropTo(file) is not { } e) return;
        propList.SelectedIndex = propList.Items.IndexOf(e);
    }

    /// <summary>Another character's prop added to <paramref name="file"/> (<paramref name="pick"/>, else the Add a Prop window
    /// asks) and put into the package; null when nothing was added. The Animations tab's button and the Model tab's.</summary>
    async Task<AddedPropEntry?> AddPropTo(string file, AddPropForm.Pick? pick = null)
    {
        if (game == null) return null;
        if (pick == null)
        {
            using var f = new AddPropForm(game.Cooked);
            if (f.ShowDialog(this) != DialogResult.OK || f.Chosen is not { } chosen) return null;
            pick = chosen;
        }
        if (draft.AddedProps.Any(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Class.Equals(pick.Class, StringComparison.OrdinalIgnoreCase)))
        { Dialog.Show(this, $"{pick.Name} is on this costume already.", "Prop Not Added"); return null; }
        var e = new AddedPropEntry
        {
            Package = file, From = pick.File, Class = pick.Class, Name = pick.Name,
            Slot = PowerBorrow.PropSlot(StockFiles.For(game.Cooked, pick.File), pick.Class),
            Fit = new PropFit { Class = pick.Class, TurnZ = PowerBorrow.DefaultTurnZ(pick.Class) },
        };
        draft.AddedProps.Add(e);
        if (!await ApplyPropsTo(file)) { draft.AddedProps.Remove(e); return null; }
        FillPropList();
        return e;
    }

    async void RemovePropAsked()
    {
        if (propList.SelectedItem is AddedPropEntry e) await RemoveAddedProp(e);
    }

    /// <summary>
    /// One of the costume's own props held on another bone (<paramref name="bone"/>), or on its own again (null): kept in the
    /// draft (manifest PropBones) and put into the package; builds and Save put it in again.
    /// </summary>
    async Task<bool> SetPropBone(string file, string cls, string? bone)
    {
        if (DraftPath(file) is not string src) return false;
        var had = draft.PropBones.FirstOrDefault(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
        List<string> original;
        try { original = had?.Original ?? PowerBorrow.HeldBones(File.ReadAllBytes(src), cls) ?? throw new InvalidDataException($"{cls}'s settings (its class default) aren't in {file}: it's another package's prop"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { Dialog.Show(this, "The prop's bone can't be changed: " + ex.Message, "Bone Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        if (had != null) draft.PropBones.Remove(had);
        if (bone != null) draft.PropBones.Add(new PropBoneEntry { Package = file, Class = cls, Bone = bone, Original = original });
        int v = ++propVersion;
        var log = new List<string>();
        byte[] bytes;
        try { bytes = await Task.Run(() => PowerBorrow.SetHeldBones(File.ReadAllBytes(src), cls, bone != null ? [bone] : original, log)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException)
        {
            draft.PropBones.RemoveAll(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase) && p.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
            if (had != null) draft.PropBones.Add(had);
            Dialog.Show(this, "The prop's bone can't be changed: " + ex.Message, "Bone Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (IsDisposed || v != propVersion) return false;
        UseWorkCopy(file, bytes);
        propTouched.Add(file);
        ReloadPropPreviews();
        modelPage?.PackageReplaced(file);
        return true;
    }

    /// <summary>An added prop's own model (the Model tab's build, kept in the mod's Model folder), else null: its character's.</summary>
    string? PropModel(AddedPropEntry p) => p.ModelFrom is string r && Path.Combine(WorkFolder, r) is var f && File.Exists(f) ? f : null;

    /// <summary>An added prop given a model of its own (the Model tab's build, kept under the mod's Model folder at
    /// <paramref name="relPath"/>), or its character's again (null), put into its package.</summary>
    async Task<bool> SetAddedModel(AddedPropEntry e, string? relPath)
    {
        string? was = e.ModelFrom;
        e.ModelFrom = relPath;
        if (await ApplyPropsTo(e.Package)) return true;
        e.ModelFrom = was;
        return false;
    }

    /// <summary>An added prop taken off its package (the Animations tab's Remove and the Model tab's).</summary>
    async Task<bool> RemoveAddedProp(AddedPropEntry e)
    {
        if (DraftPath(e.Package) is not string src) return false;
        int v = ++propVersion;
        draft.AddedProps.Remove(e);
        var log = new List<string>();
        byte[] bytes;
        try { bytes = await Task.Run(() => PowerBorrow.RemoveProp(File.ReadAllBytes(src), e.Class, log)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or PackageFormatException) { draft.AddedProps.Add(e); Dialog.Show(this, "The prop couldn't be taken off: " + ex.Message, "Prop Not Removed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        if (IsDisposed || v != propVersion) return false;
        UseWorkCopy(e.Package, bytes);
        propTouched.Add(e.Package);
        // checked: the package's props without it
        bool still = DraftPath(e.Package) is string now && (ModMeshes.AttachmentClasses(now) ?? []).Contains(e.Class, StringComparer.OrdinalIgnoreCase);
        FillPropList();
        propNote.Text = still ? $"{e.Name} Is Still in the Package (Please Report This)" : $"{e.Name} Taken Off";
        ReloadPropPreviews();
        modelPage?.PackageReplaced(e.Package);
        return !still;
    }

    /// <summary>The package's added props applied to its current copy (again: AddProp keeps what's there), into a work copy;
    /// the previews read it. False (with a message) when it can't be done.</summary>
    async Task<bool> ApplyPropsTo(string file)
    {
        if (game == null || DraftPath(file) is not string src) return false;
        int v = ++propVersion;
        var all = draft.AddedProps.Where(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        var props = all.Where(p => !p.Off).ToList();
        var off = all.Where(p => p.Off).Select(p => p.Class).ToList();   // (unticked: taken off the costume's list, settings kept)
        string cooked = game.Cooked;
        var log = new List<string>();
        List<string> bad = [];
        var hold = Hold(file, src);
        var borrows = Borrows(file);
        var models = props.ToDictionary(p => p, p => PropModel(p));
        var bones = draft.PropBones.Where(p => p.Package.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        propNote.Text = "Putting the Prop In…";
        byte[] bytes;
        byte[]? baseBytes = null; string? baseFile = null;
        try
        {
            bytes = await Task.Run(() =>
            {
                byte[] b = File.ReadAllBytes(src);
                foreach (string c in off) b = PowerBorrow.RemoveProp(b, c, log);
                foreach (var p in props) b = PowerBorrow.AddProp(b, p, StockFiles.For(cooked, p.From), log, hold, models[p]);
                b = PowerBorrow.FixClassImports(b, log);   // (a prop list made by 0.60.206-216: its class imports put right)
                bad = PowerBorrow.UnloadableClasses(b, file);
                foreach (var pb in bones) b = PowerBorrow.SetHeldBones(b, pb.Class, [pb.Bone], log);   // (the costume's own props on other bones)
                baseBytes = HoldInBorrows(file, b, props, log, borrows, out baseFile);
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
        if (baseBytes != null && baseFile != null) { UseWorkCopy(baseFile, baseBytes); propTouched.Add(baseFile); }
        propLastLog = log;
        propNote.Text = "";
        ReloadPropPreviews();
        modelPage?.PackageReplaced(file);   // (the Model tab's preview holds them too)
        // a prop class left in the package that the game can't load for this costume (a removed one from another costume's
        // package: Remove takes a prop off the list only): said before it reaches the game (Kurt, 2026-10-28: Doom crashed)
        if (bad.Count > 0 && warnedUnloadable.Add(file + "|" + string.Join(",", bad)))
            Dialog.Show(this, $"{file} still holds {string.Join(", ", bad)}: a prop class built on another hero's, which the game doesn't load for this costume. The game will crash loading it.\n\nOn the Model tab, right-click the costume under Target → Revert to Original (or Revert to Last Build, from before that prop), then add its props again. A prop from a costume's own package now comes in as a model of its own.",
                "This Package Would Crash the Game", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    readonly HashSet<string> warnedUnloadable = new(StringComparer.OrdinalIgnoreCase);

    void ReloadPropPreviews()
    {
        if (animPreview != null) { animPreview.OverrideFiles = PreviewOverrides; animPreview.ReloadOverrides(); }
        if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
    }

    /// <summary>After a borrowed look's rebuild (which starts from the packages before it): the added props put in again.</summary>
    async void ReapplyProps()
    {
        foreach (string f in draft.AddedProps.Select(p => p.Package).Concat(draft.PropBones.Select(p => p.Package)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) await ApplyPropsTo(f);
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
            foreach (var p in g) b = p.Off ? PowerBorrow.RemoveProp(b, p.Class, log) : PowerBorrow.AddProp(b, p, StockFiles.For(game.Cooked, p.From), log, Hold(g.Key, src), PropModel(p));
            b = PowerBorrow.FixClassImports(b, log);
            foreach (var pb in draft.PropBones.Where(x => x.Package.Equals(g.Key, StringComparison.OrdinalIgnoreCase))) b = PowerBorrow.SetHeldBones(b, pb.Class, [pb.Bone], log);
            string path = Path.Combine(work, g.Key);
            File.WriteAllBytes(path, b);
            int k = draft.Packages.FindIndex(x => x.File.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (g.Key, path);
            // the borrowed powers' animations in the hero's package (after the borrowed looks' rebuild)
            if (HoldInBorrows(g.Key, b, [.. g], log, Borrows(g.Key), out string? baseFile) is { } bb && baseFile != null)
            {
                string bp = Path.Combine(work, baseFile);
                File.WriteAllBytes(bp, bb);
                int bk = draft.Packages.FindIndex(x => x.File.Equals(baseFile, StringComparison.OrdinalIgnoreCase));
                if (bk >= 0) draft.Packages[bk] = (baseFile, bp);
            }
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
