namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Borrowed Look (Kurt, 2026-10-10: a hero borrowing another hero's power look, client side only): the
/// selected power takes another hero's power's look (PowerBorrow: the whole power, or its timing kept with only what its
/// effects draw). Picking or removing one rebuilds every borrow into a work folder at once, so the 3D preview shows it (its
/// OverrideFiles); Save rebuilds them again, before the power colors, which then recolor the borrowed look.
/// </summary>
sealed partial class ModEditorView
{
    readonly Label borrowCaption = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 2, 0, 4), MaximumSize = new Size(560, 0) };
    Button? borrowButton, borrowRemove;
    /// <summary>The borrowed packages built for the preview (file → path), and their folder.</summary>
    Dictionary<string, string> borrowFiles = new(StringComparer.OrdinalIgnoreCase);
    string? borrowWork;
    bool borrowBuilding;
    internal bool BorrowBuildingForTest => borrowBuilding;
    // Prop Fit and Rider Fit (Kurt, 2026-10-21: adjusted after the borrow is in, here, not in the Borrow window): each prop
    // the borrowed power brings (a vehicle, a mount, a weapon) sized, moved and turned; on a vehicle the rider moved and
    // leaned. Each change rebuilds the borrow and the preview shows it.
    readonly FlowLayoutPanel propFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 4, 0, 0) };
    readonly FlowLayoutPanel riderFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 4, 0, 0) };
    readonly DropDown propPick = new() { Width = 320 };
    LightSlider? pSize, pX, pY, pZ, pTX, pTY, pTZ, vForward, vUp, vLean;
    readonly CheckBox keepWeapons = new() { Text = "Keep This Hero's Weapons", AutoSize = true, Margin = new Padding(0, 6, 0, 2), Visible = false };
    readonly CheckBox pinPivot = new() { Text = "Pivot on the Hand", AutoSize = true, Margin = new Padding(0, 6, 0, 2) };
    bool vShowing;
    readonly HashSet<string> fitBuilt = new(StringComparer.OrdinalIgnoreCase);
    // the power throws the prop the costume holds (an added prop, Animations tab): that prop in this power's animations
    // (Kurt, 2026-10-23: the Prop Fit sliders changed a hammer the costume no longer shows)
    readonly Label heldNote = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2), MaximumSize = new Size(560, 0), Visible = false };
    AnimFitBlock? powerAnimBlock;

    /// <summary>Tests: the Powers tab on the power named <paramref name="name"/>; what its Prop Fit shows.</summary>
    internal async Task<string> PowerFitTest(string name)
    {
        for (int i = 0; i < TabCount; i++) if (TabTitle(i) == "Powers") SelectTab(i);
        await Task.Delay(8000);
        for (int i = 0; i < powerList.Items.Count; i++)
            if (powerList.Items[i] is Fx.PowerList.Power p && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { powerList.SelectedIndex = i; break; }
        for (int w = 0; w < 60 && powerPreview?.CurrentAnimationName == null; w++) await Task.Delay(1000);
        await Task.Delay(3000);
        string state = $"props shown: {powerPreview?.PropsForTest} | prop fit {(propFit.Visible ? "shown" : "hidden")}, note '{heldNote.Text}', fit sliders {(pSize?.Visible == true ? $"shown at {pSize.Value * 100:0} %" : "hidden")}, block {(powerAnimBlock?.Root.Visible == true ? powerAnimBlock.State() : "hidden")}, playing {powerPreview?.CurrentAnimationName}";
        // the held prop's Size from here (MHO_TEST_POWER_SIZE): its fit, the Animations tab's slider, the preview's props
        if (Environment.GetEnvironmentVariable("MHO_TEST_POWER_SIZE") is string sz && pSize != null && propPick.SelectedItem is PowerBorrow.SpawnedProp sp0 && HeldAs(sp0.Class) is { } held)
        {
            pSize.Value = float.Parse(sz, System.Globalization.CultureInfo.InvariantCulture);
            PropFitChanged();
            await Task.Delay(15000);
            state += $" | after Size {sz}: held fit x{held.Fit.Scale}, Animations tab slider {(apSize != null ? $"{apSize.Value * 100:0} %" : "-")}, power preview props {powerPreview?.PropsForTest}";
        }
        // the gizmo (MHO_TEST_POWER_GIZMO=1): shown on the picked prop, the X arrow dragged and let go, the fit put in
        if (Environment.GetEnvironmentVariable("MHO_TEST_POWER_GIZMO") == "1" && pX != null)
        {
            HookBorrowGizmo();
            if (!Model.Settings.Current.Preview.Gizmo) ToggleBorrowGizmo();
            await Task.Delay(1500);
            bool shown = BorrowGizmoNow() != null;
            float x0 = pX.Value;
            float y0 = pY!.Value, z0 = pZ!.Value;
            bool dragged = gizmoHooked?.View.TestGizmoDrag(0, 12, 0) == true;
            float x1 = pX.Value, y1 = pY.Value, z1 = pZ.Value;   // (right after the drag: only X should have moved)
            for (int w = 0; w < 60 && !borrowBuilding; w++) await Task.Delay(100);
            for (int w = 0; w < 300 && borrowBuilding; w++) await Task.Delay(200);
            await Task.Delay(2000);
            var fitNow = propPick.SelectedItem is PowerBorrow.SpawnedProp gsp && powerList.SelectedItem is Fx.PowerList.Power gp && BorrowOf(gp.Prototype) is { } ge
                ? (HeldAs(gsp.Class)?.Fit ?? ge.Props?.FirstOrDefault(f => f.Class.Equals(gsp.Class, StringComparison.OrdinalIgnoreCase))) : null;
            state += $" | gizmo shown {shown}, X arrow dragged {dragged}: on the drag Move X {x0} -> {x1}, Y {y0} -> {y1}, Z {z0} -> {z1}; after the rebuild X {pX.Value}, Y {pY.Value}, Z {pZ.Value}; fit in the borrow {(fitNow == null ? "none" : $"{fitNow.X:0.#}, {fitNow.Y:0.#}, {fitNow.Z:0.#}")}";
        }
        return state;
    }

    /// <summary>A held prop's fit changed from the Powers tab: its pin (letting go needs the sets' own keys back), then the
    /// prop built again into its package and the borrowed powers' animations; the Animations tab's sliders follow.</summary>
    async Task HeldFitApplied(AddedPropEntry held, bool pin)
    {
        if (held.Fit.PinPivot != pin) await SetPin(held, pin);
        else await ApplyPropsTo(held.Package);
        if (propList.SelectedItem == held) ShowPropFit();
        if (powerAnimBlock?.Root.Visible == true) powerAnimBlock.Show();
    }

    /// <summary>The added prop (Animations tab) a borrowed power's prop of <paramref name="cls"/> is, else null.</summary>
    AddedPropEntry? HeldAs(string cls) => draft.AddedProps.FirstOrDefault(a => !a.Off && a.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Powers tab's In This Animation: the held prop, the power's animation playing, and what's missing.</summary>
    (AddedPropEntry? Entry, string? Slot, string Why) PowerAnimTarget()
    {
        var held = propPick.SelectedItem is PowerBorrow.SpawnedProp sp ? HeldAs(sp.Class) : null;
        string? slot = powerPreview?.CurrentAnimationName;
        if (held == null) return (null, slot, "");
        if (!held.Fit.PinPivot) return (held, slot, "Tick Pivot on the Hand for the prop in the Animations tab to adjust it in this power's animations.");
        if (slot == null) return (held, null, "Play one of this power's animations.");
        if (BorrowAnims(held.Package)?.Donors(slot) == null) return (held, slot, "This animation isn't one the borrowed power brought: adjust the prop in it in the Animations tab.");
        return (held, slot, "");
    }

    Control BorrowSection()
    {
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        box.Controls.Add(new Label { Text = "BORROWED LOOK", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 2) });
        borrowButton = Ui.FlatButton("Borrow Look", PickBorrow, tip: "Give this power another hero's power look: its animation, effects and projectile, or only what its effects draw while this power keeps its timing. Only the look changes (what the power does stays this one's), on this hero's power packages: every costume of the hero, anyone using this mod; the other hero stays as it is.");
        borrowRemove = Ui.FlatButton("Game's Look", RemoveBorrow, tip: "Take this power back to its own look (the borrowed one comes out of the mod at Save).");
        box.Controls.Add(Toolbar(borrowButton, borrowRemove));
        box.Controls.Add(borrowCaption);
        Ui.Tip(keepWeapons, "Keeps the weapons this hero always holds (Captain America's shield) shown while the borrowed power runs. Off: they're hidden then, since the other hero's animation isn't made to hold them (a weapon can stick out of the body).");
        keepWeapons.CheckedChanged += (_, _) =>
        {
            if (vShowing || borrowBuilding || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e || e.KeepWeapons == keepWeapons.Checked) return;
            e.KeepWeapons = keepWeapons.Checked;
            RebuildBorrows();
        };
        box.Controls.Add(keepWeapons);
        box.Controls.Add(NameIconControls());
        LightSlider Slider(string label, float min, float max, float step, Func<float, string> format, string tip, Action changed)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Mark = 0, Home = () => 0, Format = format, Width = (int)(320 * S), Height = (int)(30 * S), Margin = new Padding(0, 2, 0, 0) };
            Ui.Tip(sl, tip);
            sl.Committed += changed;
            return sl;
        }
        string Units(float v) => $"{v:+0;-0;0}";
        string Deg(float v) => $"{v:+0;-0;0}°";
        pSize = Slider("Size", 0.25f, 3f, 0.01f, v => $"{v * 100:0} %", "The prop's size. A vehicle starts sized for this hero (this hero's height over the other hero's), anything else as it was made. Double-click: back to that size.", PropFitChanged);
        pX = Slider("Move X", -120, 120, 1, Units, "Moves the prop along the global X axis (red on the 3D view's axis gizmo: the way the character faces), in game units, as the prop is held in the frame shown. Double-click: back to 0.", PropFitChanged);
        pY = Slider("Move Y", -120, 120, 1, Units, "Moves the prop along the global Y axis (green on the 3D view's axis gizmo), in game units, as the prop is held in the frame shown. Double-click: back to 0.", PropFitChanged);
        pZ = Slider("Move Z", -120, 120, 1, Units, "Moves the prop along the global Z axis (blue on the 3D view's axis gizmo: up), in game units, as the prop is held in the frame shown. Double-click: back to 0.", PropFitChanged);
        pTX = Slider("Turn X", -180, 180, 1, Deg, "Turns the prop about its own X axis (roll), in degrees. Double-click: back to 0.", PropFitChanged);
        pTY = Slider("Turn Y", -180, 180, 1, Deg, "Turns the prop about its own Y axis (pitch), in degrees. Double-click: back to 0.", PropFitChanged);
        pTZ = Slider("Turn Z", -180, 180, 1, Deg, "Turns the prop about its own Z axis (yaw), in degrees. Some props start turned for another hero's hand (Thor's hammer). Double-click: back to that start.", PropFitChanged);
        var propHead = new Label { Text = "PROP FIT", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(propHead, "The props this borrowed power brings (the other hero's vehicle, mount or weapons): pick one, then size, move and turn it. Each change rebuilds the borrow, then the 3D preview shows it; Save Changes puts it into the mod.");
        propPick.Width = (int)(420 * S);
        propPick.Format = o => o is PowerBorrow.SpawnedProp sp ? PropName(sp) : o.ToString() ?? "";
        propPick.SelectedIndexChanged += (_, _) => { if (!vShowing && powerList.SelectedItem is Fx.PowerList.Power p0) ShowFits(BorrowOf(p0.Prototype)); };
        Ui.Tip(propPick, "The prop to fit.");
        Ui.Tip(pinPivot, "The borrowed animations stop moving the bone this prop hangs on, so it turns about the hand (Thor's Hammer of Storms moves his palm bone out along the strap to swing the hammer). Then Move places the prop from the hand. Look ▾ → Prop Pivots shows where the pivot is.");
        pinPivot.CheckedChanged += (_, _) => PropFitChanged();
        powerAnimBlock = new AnimFitBlock(S, "The prop the costume holds, moved and turned further in this power's animation playing (the throw), for a pose that wants it elsewhere. Its fit is in the Animations tab (Props).",
            PowerAnimTarget, SetAnimFit, c => Toolbar(c));
        powerAnimBlock.Root.Visible = false;
        // the fit (left) and In This Animation (right), side by side as in the Animations tab (Kurt, 2026-10-23: no scrolling)
        var fitCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        fitCol.Controls.AddRange([pinPivot, pSize, pX, pY, pZ, pTX, pTY, pTZ, BorrowFitButtons()]);
        powerAnimBlock.Root.Margin = new Padding((int)(24 * S), 0, 0, 0);
        var cols = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        cols.Controls.AddRange([fitCol, powerAnimBlock.Root]);
        propFit.Controls.AddRange([propHead, propPick, heldNote, cols]);
        vForward = Slider("Forward / Back", -80, 80, 1, Units, "Moves the rider forward (+) or back (−) on the vehicle, in game units; the vehicle stays where it is. Double-click: back to 0.", RiderFitChanged);
        vUp = Slider("Up / Down", -80, 80, 1, Units, "Moves the rider up (+) or down (−) on the vehicle, in game units. Double-click: back to 0.", RiderFitChanged);
        vLean = Slider("Lean", -30, 30, 1, Deg, "Leans the rider forward (+) or back (−), in degrees. Double-click: back to 0.", RiderFitChanged);
        var riderHead = new Label { Text = "RIDER FIT", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(riderHead, "This borrowed power brings a vehicle or mount: move and lean this hero on it.");
        riderFit.Controls.AddRange([riderHead, vForward, vUp, vLean]);
        box.Controls.Add(propFit);
        box.Controls.Add(riderFit);
        return box;
    }

    /// <summary>A prop's name for the picker: marvelattachmentanimated_cyclopsbike → Cyclopsbike (Vehicle).</summary>
    static string PropName(PowerBorrow.SpawnedProp sp)
    {
        string n = sp.Class;
        foreach (string pre in new[] { "marvelattachmentanimated_", "marvelattachment_" })
            if (n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { n = n[pre.Length..]; break; }
        n = n.Replace('_', ' ');
        return (n.Length > 0 ? char.ToUpperInvariant(n[0]) + n[1..] : n) + (sp.Vehicle ? " (Vehicle)" : "");
    }

    /// <summary>The selected borrow's props and rider into the fit sliders (shown once a build says what it brings; a borrow
    /// not built in this session is built once, so the props are known).</summary>
    void ShowFits(PowerBorrowEntry? e)
    {
        if (e != null && e.Spawned == null && !borrowBuilding && fitBuilt.Add(e.Power + "|" + e.Donor)) { RebuildBorrows(); return; }
        var props = e?.Spawned ?? [];
        propFit.Visible = props.Count > 0;
        HookBorrowGizmo();   // (the Powers preview's 3D view: made by now when it shows the power)
        gizmoHooked?.View.Invalidate();
        riderFit.Visible = props.Any(x => x.Vehicle);
        bool on = !borrowBuilding;
        foreach (var sl in new[] { pSize, pX, pY, pZ, pTX, pTY, pTZ, vForward, vUp, vLean }) if (sl != null) sl.Enabled = on;
        propPick.Enabled = on; pinPivot.Enabled = on;
        if (e == null || pSize == null || pX == null || pY == null || pZ == null || pTX == null || pTY == null || pTZ == null || vForward == null || vUp == null || vLean == null) return;
        vShowing = true;
        try
        {
            if (props.Count > 0)
            {
                string? was = (propPick.SelectedItem as PowerBorrow.SpawnedProp)?.Class;
                if (propPick.Items.Count != props.Count || !props.Select(x => x.Class).SequenceEqual(propPick.Items.Cast<PowerBorrow.SpawnedProp>().Select(x => x.Class)))
                {
                    propPick.Items.Clear();
                    propPick.Items.AddRange(props.Cast<object>().ToArray());
                }
                int at = was == null ? 0 : Math.Max(0, props.FindIndex(x => x.Class.Equals(was, StringComparison.OrdinalIgnoreCase)));
                if (propPick.SelectedIndex != at) propPick.SelectedIndex = at;
                var sp = props[at];
                var fit = e.Props?.FirstOrDefault(f => f.Class.Equals(sp.Class, StringComparison.OrdinalIgnoreCase));
                float auto = sp.Vehicle && e.VehicleScale is float vs && vs > 0 ? vs : sp.Auto;
                pSize.Home = () => sp.Auto; pSize.Mark = sp.Auto;
                pSize.Value = fit?.Scale is float fs && fs > 0 ? fs : auto;
                // global axes (Kurt, 2026-10-23): the fit's own move shown along the character's axes, as the prop is held now
                fitAxes = powerPreview?.PropFrame(sp.Class) is { } pf ? Turn(pf) : System.Numerics.Matrix4x4.Identity;
                var world = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(fit?.X ?? 0, fit?.Y ?? 0, fit?.Z ?? 0), fitAxes);
                pX.Value = world.X; pY.Value = world.Y; pZ.Value = world.Z;
                pTX.Value = fit?.TurnX ?? 0; pTY.Value = fit?.TurnY ?? 0; pTZ.Value = fit?.TurnZ ?? sp.TurnZAuto;
                pTZ.Home = () => sp.TurnZAuto; pTZ.Mark = sp.TurnZAuto;
                pinPivot.Checked = fit?.PinPivot == true;
                // a prop the costume holds (added in the Animations tab): the power throws that one; the sliders are its own
                // fit (the same as the Animations tab's: every animation), In This Animation below for this power's
                var held = HeldAs(sp.Class);
                heldNote.Visible = held != null;
                heldNote.Text = held == null ? "" : $"This power uses the {PropName(sp)} the costume holds ({held.Name}): these sliders are its fit in every animation, the same as the Animations tab's (Props). In This Animation adjusts it in this power's animation only.";
                if (held != null)
                {
                    var hf = held.Fit;
                    float dz = PowerBorrow.DefaultTurnZ(held.Class);
                    pSize.Home = () => 1; pSize.Mark = 1;
                    pSize.Value = hf.Scale is float hs && hs > 0 ? hs : 1;
                    fitAxes = powerPreview?.PropFrame(held.Class) is { } hpf ? Turn(hpf) : System.Numerics.Matrix4x4.Identity;
                    var hw = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(hf.X, hf.Y, hf.Z), fitAxes);
                    pX.Value = hw.X; pY.Value = hw.Y; pZ.Value = hw.Z;
                    pTX.Value = hf.TurnX; pTY.Value = hf.TurnY; pTZ.Value = hf.TurnZ == 0 && hf.IsIdentity ? dz : hf.TurnZ;
                    pTZ.Home = () => dz; pTZ.Mark = dz;
                    pinPivot.Checked = hf.PinPivot;
                }
                if (powerAnimBlock != null) { powerAnimBlock.Root.Visible = held != null; powerAnimBlock.Show(); }
            }
            vForward.Value = e.RiderForward; vUp.Value = e.RiderUp; vLean.Value = e.RiderLean;
        }
        finally { vShowing = false; }
    }

    /// <summary>The prop's own axes in the character's, as shown when the sliders were filled (identity: the preview doesn't
    /// show the prop, so the sliders move along its own axes).</summary>
    System.Numerics.Matrix4x4 fitAxes = System.Numerics.Matrix4x4.Identity;

    /// <summary>A frame's turn only: its rows made unit length (a size in it left out), no move.</summary>
    static System.Numerics.Matrix4x4 Turn(System.Numerics.Matrix4x4 m)
    {
        static System.Numerics.Vector3 N(float x, float y, float z) { var v = new System.Numerics.Vector3(x, y, z); return v.LengthSquared() > 1e-12f ? System.Numerics.Vector3.Normalize(v) : v; }
        var a = N(m.M11, m.M12, m.M13); var b = N(m.M21, m.M22, m.M23); var c = N(m.M31, m.M32, m.M33);
        return new System.Numerics.Matrix4x4(a.X, a.Y, a.Z, 0, b.X, b.Y, b.Z, 0, c.X, c.Y, c.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>A Prop Fit slider let go: the selected prop's fit into the borrow, which is built again.</summary>
    void PropFitChanged()
    {
        if (vShowing || borrowBuilding || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e || propPick.SelectedItem is not PowerBorrow.SpawnedProp sp
            || pSize == null || pX == null || pY == null || pZ == null || pTX == null || pTY == null || pTZ == null) return;
        // a prop the costume holds: these sliders are its own fit, the Animations tab's (Kurt, 2026-10-23: the base here too)
        if (HeldAs(sp.Class) is { } held)
        {
            var hown = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(pX.Value, pY.Value, pZ.Value), System.Numerics.Matrix4x4.Transpose(fitAxes));
            held.Fit.Scale = Math.Abs(pSize.Value - 1) > 0.005f ? MathF.Round(pSize.Value, 2) : null;
            held.Fit.X = MathF.Round(hown.X, 2); held.Fit.Y = MathF.Round(hown.Y, 2); held.Fit.Z = MathF.Round(hown.Z, 2);
            held.Fit.TurnX = pTX.Value; held.Fit.TurnY = pTY.Value; held.Fit.TurnZ = pTZ.Value;
            _ = HeldFitApplied(held, pinPivot.Checked);
            return;
        }
        e.Props ??= [];
        var fit = e.Props.FirstOrDefault(f => f.Class.Equals(sp.Class, StringComparison.OrdinalIgnoreCase));
        if (fit == null) e.Props.Add(fit = new PropFit { Class = sp.Class });
        fit.Scale = Math.Abs(pSize.Value - sp.Auto) > 0.005f ? MathF.Round(pSize.Value, 2) : null;
        if (sp.Vehicle) e.VehicleScale = null;   // (the older Vehicle Fit size: the prop's own now)
        // the sliders are global (along the character's axes, as the prop is held in the frame shown): into the prop's own
        var own = System.Numerics.Vector3.TransformNormal(new System.Numerics.Vector3(pX.Value, pY.Value, pZ.Value), System.Numerics.Matrix4x4.Transpose(fitAxes));
        fit.X = MathF.Round(own.X, 2); fit.Y = MathF.Round(own.Y, 2); fit.Z = MathF.Round(own.Z, 2);
        fit.TurnX = pTX.Value; fit.TurnY = pTY.Value; fit.TurnZ = pTZ.Value;
        fit.PinPivot = pinPivot.Checked;
        e.Props.RemoveAll(f => f.IsIdentity);
        if (e.Props.Count == 0) e.Props = null;
        RebuildBorrows();
    }

    /// <summary>A Rider Fit slider let go: the rider's fit into the borrow, which is built again.</summary>
    void RiderFitChanged()
    {
        if (vShowing || borrowBuilding || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e || vForward == null || vUp == null || vLean == null) return;
        e.RiderForward = vForward.Value; e.RiderUp = vUp.Value; e.RiderLean = vLean.Value;
        RebuildBorrows();
    }

    PowerBorrowEntry? BorrowOf(string proto) => draft.PowerBorrows.FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase));

    /// <summary>The selected power's borrow, in the caption and the buttons (a hero's power with an animation only).</summary>
    void ShowBorrow(Fx.PowerList.Power p)
    {
        bool can = !PowerColorBuild.IsOwn(p.Prototype) && !PowerColorBuild.IsPackage(p.Prototype) && !agentOwner.ContainsKey(p.Prototype) && p.Animations.Count > 0 && PowerBorrow.HeroOf(p.Prototype) != null;
        var e = BorrowOf(p.Prototype);
        if (borrowButton != null) borrowButton.Enabled = can && !borrowBuilding;
        if (borrowRemove != null) borrowRemove.Enabled = e != null && !borrowBuilding;
        vShowing = true;
        try { keepWeapons.Visible = e != null && !e.KeepTiming; keepWeapons.Checked = e?.KeepWeapons == true; keepWeapons.Enabled = !borrowBuilding; }
        finally { vShowing = false; }
        ShowNameIcon(e);
        ShowFits(e);
        borrowCaption.Text = borrowBuilding ? "Building the borrowed looks…"
            : e != null ? $"Looks like {e.DonorName} ({(e.KeepTiming ? "its effects, this power's timing" : e.Fit ? "the whole power, fitted to this power's timing" : "the whole power, at its own speed")})"
            : can ? "Its own look" : "Only a hero's powers with an animation can borrow a look";
    }

    void PickBorrow()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p || game == null) return;
        var g = game;
        powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
        using var f = new BorrowPowerForm(powerDb, g.Cooked, p.Prototype, p.Name, BorrowOf(p.Prototype));
        if (f.ShowDialog(this) != DialogResult.OK || f.Result is not { } chosen) return;
        if (chosen.Donor.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase)) { borrowCaption.Text = "That's this power's own look"; return; }
        if (BorrowOf(p.Prototype) is { } old) TakeOffNameAndIcon(old);
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        chosen.Power = p.Prototype; chosen.Name = p.Name;
        PutNameAndIcon(chosen, f.NewName, f.UseIcon);
        draft.PowerBorrows.Add(chosen);
        RebuildBorrows();
    }

    void RemoveBorrow()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        if (BorrowOf(p.Prototype) is { } old) TakeOffNameAndIcon(old);
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        RebuildBorrows();
    }

    /// <summary>
    /// A borrow's name and icon (Kurt, 2026-10-23): the power's DisplayName string set to <paramref name="name"/> (English,
    /// in the Strings tab) and its icons (IconPath, IconPathHiRes) replaced by the donor's, converted to this power's icon
    /// size and format (Icons tab). Both are the mod format's own strings and icons, so the old manager installs them too.
    /// </summary>
    void PutNameAndIcon(PowerBorrowEntry e, string? name, bool icon)
    {
        if (powerDb is not { } db) return;
        ulong? Str(string proto, string field) => db.Find(proto) is { } d && Fx.PowerList.Field(db, d.Id, field, 'S', true) is { Found: true } f ? f.Raw : null;
        string? Asset(string proto, string field) => db.Find(proto) is { } d && Fx.PowerList.Field(db, d.Id, field, 'A', true) is { Found: true } f && db.Assets.TryGetValue(f.Raw, out var a) ? a.Asset.Name : null;
        if (name != null && Str(e.Power, "DisplayName") is ulong id && id != 0)
        {
            var s = new StringReplacement("eng", CostumeMove.StringFileFor("eng", id), id, name);
            draft.Strings.RemoveAll(x => x.Language == "eng" && x.Id == id);
            draft.Strings.Add(s);
            stringsPage.Put(s);
            e.NameId = id;
        }
        if (!icon || catalog == null) return;
        var icons = new List<string>();
        var notes = new List<string>();
        foreach (string field in new[] { "IconPath", "IconPathHiRes" })
        {
            if (Costume.IconTexture(Asset(e.Power, field)) is not { } mine || Costume.IconTexture(Asset(e.Donor, field)) is not { } theirs) continue;
            string png = Path.Combine(Settings.Home, "converted", $"borrow_{ModInstaller.Sanitise(theirs.Texture)}.png");
            string dds = Path.Combine(Settings.Home, "converted", $"borrow_{ModInstaller.Sanitise(theirs.Texture)}_{ModInstaller.Sanitise(mine.Texture)}.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            try
            {
                if (catalog.ExportImage(theirs.Package, theirs.Texture, png) is string why) { notes.Add($"{theirs.Texture} can't be read ({why})."); continue; }
                catalog.ImageToDds(mine.Package, mine.Texture, png, dds);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException) { notes.Add($"{theirs.Texture} can't be used ({ex.Message})."); continue; }
            int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(mine.Package, StringComparison.OrdinalIgnoreCase));
            if (view >= 0)
            {
                draft.Textures[view].RemoveAll(r => r.Texture.Equals(mine.Texture, StringComparison.OrdinalIgnoreCase));
                draft.Textures[view].Add((mine.Texture, dds));
            }
            else
            {
                draft.Extra.RemoveAll(x => x.Package.Equals(mine.Package, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(mine.Texture, StringComparison.OrdinalIgnoreCase));
                draft.Extra.Add((mine.Package, mine.Texture, dds));
            }
            icons.Add(mine.Package + "|" + mine.Texture);
        }
        if (icons.Count > 0) e.Icons = icons;
        if (notes.Count > 0) Dialog.Show(this, "The icon wasn't changed:" + Environment.NewLine + string.Join(Environment.NewLine, notes.Select(n => "  • " + n)), "Icon Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        foreach (var tp in texturePages) tp.Refresh();
    }

    /// <summary>The name and icons a borrow put in, taken off (Game's Look, or another borrow for the power).</summary>
    void TakeOffNameAndIcon(PowerBorrowEntry e) { TakeOffName(e); TakeOffIcons(e); }

    void TakeOffName(PowerBorrowEntry e)
    {
        if (e.NameId is ulong id)
        {
            draft.Strings.RemoveAll(x => x.Language == "eng" && x.Id == id);
            stringsPage.Drop("eng", id);
        }
        e.NameId = null;
    }

    void TakeOffIcons(PowerBorrowEntry e)
    {
        foreach (string k in e.Icons ?? [])
        {
            string pkg = k.Split('|')[0], tex = k.Split('|')[^1];
            int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
            if (view >= 0) draft.Textures[view].RemoveAll(r => r.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase));
            else draft.Extra.RemoveAll(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase));
        }
        if (e.Icons != null) foreach (var tp in texturePages) tp.Refresh();
        e.Icons = null;
    }

    // --- the name and icon of a power that has a borrowed look, in the Powers tab (Kurt, 2026-10-23: the Borrow window was
    // the only place to set them) ---------------------------------------------------------------------------------------------
    readonly CheckBox renamePower = new() { Text = "Rename This Power", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    readonly TextBox renameText = new() { Width = 300, Margin = new Padding(22, 2, 0, 0) };
    readonly CheckBox useIcon = new() { Text = "Use Its Icon", AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
    /// <summary>The three together (shown for a power with a borrowed look; the name box's frame doesn't follow the box itself).</summary>
    readonly FlowLayoutPanel nameIconBox = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0), Visible = false };

    /// <summary>The borrowed power's own name ("Thor: Hammer of Storms" → "Hammer of Storms").</summary>
    static string DonorPowerName(PowerBorrowEntry e) => e.DonorName.Contains(": ") ? e.DonorName[(e.DonorName.IndexOf(": ", StringComparison.Ordinal) + 2)..] : e.DonorName;

    Control NameIconControls()
    {
        var box = nameIconBox;
        renameText.Width = (int)(300 * S);
        Ui.Tip(renamePower, "The power's name in game becomes the one in the box (at first the borrowed power's; type your own). It's put in the Strings tab (English), and Game's Look takes it off again.");
        Ui.Tip(renameText, "The power's new name in game: Enter (or clicking away) puts it in.");
        Ui.Tip(useIcon, "The power's icon becomes the borrowed power's (the power bar's icon, and the large one where the game has it), converted to this power's icon size; it's put in the Icons tab, and Game's Look takes it off again.");
        renamePower.CheckedChanged += (_, _) =>
        {
            renameText.Enabled = renamePower.Checked;
            if (vShowing || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e) return;
            if (renamePower.Checked) { if (renameText.Text.Trim().Length == 0) renameText.Text = DonorPowerName(e); PutNameAndIcon(e, renameText.Text.Trim(), false); }
            else TakeOffName(e);
        };
        void Rename()
        {
            if (vShowing || !renamePower.Checked || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e || renameText.Text.Trim().Length == 0) return;
            PutNameAndIcon(e, renameText.Text.Trim(), false);
        }
        renameText.KeyDown += (_, ke) => { if (ke.KeyCode == Keys.Enter) { ke.SuppressKeyPress = true; Rename(); } };
        renameText.Leave += (_, _) => Rename();
        useIcon.CheckedChanged += (_, _) =>
        {
            if (vShowing || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e) return;
            if (useIcon.Checked) { PutNameAndIcon(e, null, true); if (e.Icons == null) { vShowing = true; useIcon.Checked = false; vShowing = false; } }
            else TakeOffIcons(e);
        };
        box.Controls.AddRange([renamePower, renameText, useIcon]);
        return box;
    }

    /// <summary>The selected power's borrow into the name and icon controls.</summary>
    void ShowNameIcon(PowerBorrowEntry? e)
    {
        bool on = e != null;
        nameIconBox.Visible = on;
        if (e == null) return;
        vShowing = true;
        try
        {
            renamePower.Checked = e.NameId != null;
            renameText.Text = e.NameId is ulong id && draft.Strings.FirstOrDefault(x => x.Language == "eng" && x.Id == id) is { } s ? s.Text : DonorPowerName(e);
            renameText.Enabled = renamePower.Checked;
            useIcon.Checked = e.Icons != null;
        }
        finally { vShowing = false; }
    }

    /// <summary>Every borrow built again (in the background), then the preview reads the built packages.</summary>
    void RebuildBorrows()
    {
        if (game == null || powerDb == null && draft.PowerBorrows.Count > 0 && game == null) return;
        var g = game;
        borrowBuilding = true;
        if (powerList.SelectedItem is Fx.PowerList.Power sel) ShowBorrow(sel);
        string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
        string model = WorkFolder;
        var log = new List<string>();
        Task.Run(() =>
        {
            powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g.Root, "Data", "Game", "Calligraphy.sip")));
            return PowerBorrow.Rebuild(draft, editing, lib, g, powerDb, work, model, log);
        }).ContinueWith(t =>
        {
            borrowBuilding = false;
            if (IsDisposed) return;
            if (t.Status != TaskStatus.RanToCompletion)
            {
                string why = t.Exception?.InnerException?.Message ?? "unknown";
                Dialog.Show(this, "The borrowed look couldn't be made: " + why, "Look Not Borrowed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (powerList.SelectedItem is Fx.PowerList.Power s0) ShowBorrow(s0);
                return;
            }
            string? old = borrowWork;
            borrowWork = work;
            borrowFiles = t.Result;
            if (old != null) try { Directory.Delete(old, true); } catch (IOException) { }
            RefreshPackages();
            if (draft.AddedProps.Count > 0 || draft.PropBones.Count > 0) ReapplyProps();   // (the rebuild started from the packages before the added props)
            paletteCache.Clear();   // a borrowed power's colors are the borrowed look's
            powerList.Invalidate();
            if (powerPreview != null) { powerPreview.OverrideFiles = PreviewOverrides; powerPreview.ReloadOverrides(); }
            if (powerList.SelectedItem is Fx.PowerList.Power s) ShowBorrow(s);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Before saving: the borrows built again into the draft (before the power colors). The packages they built
    /// (for the colors to start from), and the work folder.</summary>
    (HashSet<string> Built, string? Work) ApplyPowerBorrows(List<string> log)
    {
        bool had = editing?.Manifest.PowerBorrows is { Count: > 0 };
        if (draft.PowerBorrows.Count == 0 && !had) return ([], null);
        if (game == null) throw new InvalidDataException("the game folder isn't set");
        powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(game.Root, "Data", "Game", "Calligraphy.sip")));
        string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
        var built = PowerBorrow.Rebuild(draft, editing, lib, game, powerDb, work, WorkFolder, log);
        return (built.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), work);
    }
}
