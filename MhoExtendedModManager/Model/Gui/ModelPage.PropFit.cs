using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Prop Fit (Kurt, 2026-10-26, props phase 2): while the target is a prop (a weapon or shield: Thor's hammer), the right
/// column's Size in Game section shows Move X / Y / Z, Turn X / Y / Z and Size instead, so a model that doesn't line up with
/// the game's grip can be fitted in the app. They're applied after the fit, around the bone the prop is held on
/// (PropFit.Adjust), in the preview at once and in the build; kept per prop (package | model) with the tab's state.
/// Double-click a slider: back to none.
/// </summary>
sealed partial class ModelPage
{
    readonly LightSlider propMoveX = MoveSlider("Move X"), propMoveY = MoveSlider("Move Y"), propMoveZ = MoveSlider("Move Z");
    readonly LightSlider propTurnX = TurnSlider("Turn X"), propTurnY = TurnSlider("Turn Y"), propTurnZ = TurnSlider("Turn Z");
    readonly LightSlider propScale = new() { Label = "Size", Min = 0.1f, Max = 4f, Step = 0.01f, Mark = 1f, Value = 1, Home = () => 1f, Dock = DockStyle.Top };
    Control? propFitBox, sizeBoxRef;
    Label? sizeCaption;
    readonly Dictionary<string, PropAdjust> propFits = new(StringComparer.OrdinalIgnoreCase);
    bool settingPropFit;
    /// <summary>Held On ▾ (Kurt, 2026-10-27, props step 3): the character bone the prop is held on, its own or another.</summary>
    readonly DropDown heldOn = new() { Width = 260 };
    List<string> heldBones = [];
    string? heldClass;
    int heldRun;
    bool settingHeld;

    static LightSlider MoveSlider(string label) => new() { Label = label, Min = -60, Max = 60, Step = 0.5f, Mark = 0, Value = 0, Home = () => 0, Format = v => $"{v:+0.0;-0.0;0}", Dock = DockStyle.Top };
    static LightSlider TurnSlider(string label) => new() { Label = label, Min = -180, Max = 180, Step = 1, Mark = 0, Value = 0, Home = () => 0, Format = v => $"{v:+0;-0;0}°", Dock = DockStyle.Top };

    IEnumerable<LightSlider> PropSliders => [propMoveX, propMoveY, propMoveZ, propTurnX, propTurnY, propTurnZ, propScale];

    /// <summary>The Prop Fit sliders' box (shown in place of the size sliders for a prop target).</summary>
    Control PropFitBox(int sliderHeight)
    {
        // two columns (Move | Turn), so the Target list and the tabs above keep their room
        var box = new TableLayoutPanel { ColumnCount = 2, RowCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0), Visible = false, Dock = DockStyle.Fill };
        box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        LightSlider[] left = [propMoveX, propMoveY, propMoveZ, propScale], right = [propTurnX, propTurnY, propTurnZ];
        for (int k = 0; k < left.Length; k++) Place(left[k], 0, k);
        for (int k = 0; k < right.Length; k++) Place(right[k], 1, k);
        void Place(LightSlider s, int col, int row)
        {
            s.Height = sliderHeight; s.Dock = DockStyle.Fill; s.Margin = new Padding(col == 0 ? 0 : 8, row == 0 ? 0 : 2, col == 0 ? 8 : 0, 0);
            box.Controls.Add(s, col, row);
            s.ValueChanged += PropFitChanged;
            s.Committed += AddedFitCommitted;
        }
        propScale.Format = v => $"{v * 100:0} %";
        Ui.Tip(propMoveX, "Moves the prop along X (game units), after it's laid over the game's prop. Double-click: back to 0.");
        Ui.Tip(propTurnX, "Turns the prop around X, around the bone it's held on (the grip). Double-click: back to 0.");
        Ui.Tip(propScale, "The prop's size, around the bone it's held on. Double-click: back to 100 %.");
        var reset = Ui.FlatButton("Reset Fit", ResetFit, "Puts every Prop Fit slider back (no move, no turn, 100 %; an added prop: turned as it's made for this hand).");
        reset.Margin = new Padding(0, 0, 6, 0);
        // the gizmo on or off (Kurt, 2026-10-28; remembered)
        gizmoButton = Ui.FlatButton("Gizmo", ToggleGizmo, "Shows or hides the move / turn gizmo on the added prop in the preview: drag a colored arrow to move it along X (red), Y (green) or Z (blue), a ring to turn it around that axis (5° steps, Shift: 1°).");
        gizmoButton.Margin = new Padding(0);
        float sc = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        Icons.Make(reset, "Reset Fit", Icons.Reset, sc);
        Icons.Make(gizmoButton, "Gizmo", Icons.Gizmo, sc);
        Ui.Lit(gizmoButton, Settings.Current.Preview.Gizmo);
        var fitButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(8, 2, 0, 0), Anchor = AnchorStyles.Left };
        fitButtons.Controls.AddRange([reset, gizmoButton]);
        box.Controls.Add(fitButtons, 1, 3);
        heldOn.Dock = DockStyle.Fill; heldOn.Margin = new Padding(0, 6, 0, 0);
        box.Controls.Add(heldOn, 0, 4);
        box.SetColumnSpan(heldOn, 2);
        Ui.Tip(heldOn, "The character bone the prop is held on: its own (as its character holds it), or another (the left hand, the back, the hip). It goes into the package at once; the sliders then fit it on that bone. Only this costume changes.");
        heldOn.SelectedIndexChanged += async (_, _) =>
        {
            if (settingHeld || heldClass is not string cls || ChosenPackage is not CharacterList.Item pkg) return;
            string? bone = heldOn.SelectedIndex <= 0 ? null : heldBones[heldOn.SelectedIndex - 1];
            status.Text = "Putting the prop on its bone…";
            string before = PropBefore();
            bool ok = addedPick is { } e ? await SetAddedBone(e, bone) : await host.SetPropBone(pkg.Key, cls, bone);
            if (ok) PropDone(before, $"Held On {bone ?? "its own bone"}");
            if (IsDisposed) return;
            Log(ok ? $"{cls}: held on {bone ?? "its own bone"} (in {pkg.Key} now; Save Changes keeps it)." : $"{cls}: the bone wasn't changed (see the message).");
            UpdateStatus();
        };
        return propFitBox = box;
    }

    /// <summary>The key of the prop the target is: its package and model.</summary>
    string? PropFitKey() => ChosenPackage is CharacterList.Item t && MhoSkeleton.ChosenFor(t.Key) is string m ? $"{t.Key}|{m}" : null;

    PropAdjust CurrentPropFit() => new(propMoveX.Value, propMoveY.Value, propMoveZ.Value, propTurnX.Value, propTurnY.Value, propTurnZ.Value, propScale.Value);

    /// <summary>The Prop Fit for the build and the preview: the sliders while the target is a prop, else none.</summary>
    PropAdjust? PropFitForTarget() => addedPick == null && TargetIsProp() ? CurrentPropFit() : null;   // (an added prop's fit goes on in its package)

    void PropFitChanged()
    {
        if (addedPick != null) return;   // (an added prop's fit goes in when a slider is let go: AddedFitCommitted)
        if (settingPropFit || PropFitKey() is not string key) return;
        var a = CurrentPropFit();
        if (a.IsNone) propFits.Remove(key); else propFits[key] = a;
        SaveState();
        SchedulePreview();
        UpdateStatus();
    }

    void SetPropFit(PropAdjust a, bool save)
    {
        settingPropFit = true;
        (propMoveX.Value, propMoveY.Value, propMoveZ.Value, propTurnX.Value, propTurnY.Value, propTurnZ.Value, propScale.Value) = (a.MoveX, a.MoveY, a.MoveZ, a.TurnX, a.TurnY, a.TurnZ, a.Scale);
        foreach (var s in PropSliders) s.Invalidate();
        settingPropFit = false;
        if (save) PropFitChanged();
    }

    /// <summary>The right column's sizes or Prop Fit, as the target is; the sliders show that prop's fit (an added prop's when
    /// one is picked in the selector).</summary>
    void UpdatePropFitUi()
    {
        if (propFitBox == null || sizeBoxRef == null) return;
        var ap = addedPick;
        bool prop = ap != null || TargetIsProp();
        if (propFitBox.Visible != prop) { propFitBox.Visible = prop; sizeBoxRef.Visible = !prop; }
        if (sizeCaption != null) sizeCaption.Text = ap != null ? "PROP FIT  ·  ADDED PROP" : prop ? "PROP FIT" : "SIZE IN GAME";
        // an added prop: its fit as the Animations tab kept it (moved further: ±120), turned for this hand by default
        float range = ap != null ? 120 : 60, dz = ap != null ? PowerBorrow.DefaultTurnZ(ap.Class) : 0;
        foreach (var s in new[] { propMoveX, propMoveY, propMoveZ }) { s.Min = -range; s.Max = range; }
        propTurnZ.Home = () => dz; propTurnZ.Mark = dz;
        if (ap != null) SetPropFit(AddedAdjust(ap.Fit), save: false);
        else if (prop && PropFitKey() is string key) SetPropFit(propFits.GetValueOrDefault(key) ?? new PropAdjust(), save: false);
        if (prop) FillHeldOn();
    }

    async Task<bool> SetAddedBone(AddedPropEntry e, string? bone)
    {
        string? was = e.Bone;
        e.Bone = bone;
        if (await host.RefitAddedProp(e)) return true;
        e.Bone = was;
        return false;
    }

    /// <summary>Held On ▾ for the prop target: the character's bones (hands first), its own bone, the one it's on now.</summary>
    async void FillHeldOn()
    {
        if (ChosenPackage is not CharacterList.Item pkg) return;
        string key = pkg.Key;
        var ap = addedPick;
        string? mesh = MhoSkeleton.ChosenFor(key), cooked = Settings.Current.CookedFolder;
        int run = ++heldRun;
        string start = StartPackageCore(key), now = ModCopy(key) ?? start;
        (List<string> Bones, string? Class, string? Own) info;
        try
        {
            info = await Task.Run(() =>
            {
                var bones = HandsFirst(MhoSkeleton.Load(start, null, body: true).Bones.Select(b => b.Name));
                // the hero's base package: a costume's own attachment takes its bone from the class it extends there
                string? parent = System.Text.RegularExpressions.Regex.Match(key, @"^(UC__MarvelPlayer_[^_]+)_.+_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } pm && cooked != null
                    && StockFiles.For(cooked, pm.Groups[1].Value + "_SF.upk") is var pp && File.Exists(pp) ? pp : null;
                if (ap != null)
                {
                    string? own0 = cooked == null ? null : MhoExtendedModManager.ModMeshes.Attachments(StockFiles.For(cooked, ap.From)).FirstOrDefault(a => a.Class.Equals(ap.Class, StringComparison.OrdinalIgnoreCase))?.Bone;
                    return (bones, (string?)ap.Class, own0);
                }
                // a game prop: the attachment whose model it is (its own, or a fitted copy: <model>_x100_fit…), as the game has it
                bool Mine(MhoExtendedModManager.ModMeshes.Attachment a) => mesh != null && a.Mesh.Split('.').Last() is string am
                    && (am.Equals(mesh, StringComparison.OrdinalIgnoreCase) || am.StartsWith(mesh + "_x", StringComparison.OrdinalIgnoreCase));
                var att = MhoExtendedModManager.ModMeshes.Attachments(start, parent).FirstOrDefault(Mine) ?? MhoExtendedModManager.ModMeshes.Attachments(now, parent).FirstOrDefault(Mine);
                return (bones, att?.Class, att?.Bone);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { info = ([], null, null); }
        if (IsDisposed || run != heldRun) return;
        heldBones = info.Bones; heldClass = info.Class;
        string? current = ap != null ? ap.Bone : info.Class is string c ? host.PropBones(key).FirstOrDefault(p => p.Class.Equals(c, StringComparison.OrdinalIgnoreCase))?.Bone : null;
        settingHeld = true;
        try
        {
            heldOn.Items.Clear();
            heldOn.Items.Add($"Held On: Its Own ({info.Own ?? "its character's"})");
            foreach (var b in heldBones) heldOn.Items.Add("Held On: " + b);
            int at = current == null ? -1 : heldBones.FindIndex(b => b.Equals(current, StringComparison.OrdinalIgnoreCase));
            heldOn.SelectedIndex = at + 1;
            heldOn.Enabled = heldClass != null && heldBones.Count > 0;
        }
        finally { settingHeld = false; }
    }

    /// <summary>A skeleton's bones with the ones props are held on first (palms, wrists, hands), then the rest in order.</summary>
    static List<string> HandsFirst(IEnumerable<string> bones)
    {
        var all = bones.Where(b => b.StartsWith("g_", StringComparison.OrdinalIgnoreCase) || b.Equals("root", StringComparison.OrdinalIgnoreCase)).ToList();
        static int Rank(string b) => b.Contains("palm", StringComparison.OrdinalIgnoreCase) ? 0 : b.Contains("wrist", StringComparison.OrdinalIgnoreCase) ? 1
            : b.Contains("hand", StringComparison.OrdinalIgnoreCase) ? 2 : b.Contains("weapon", StringComparison.OrdinalIgnoreCase) || b.Contains("back", StringComparison.OrdinalIgnoreCase) || b.Contains("hip", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
        return [.. all.Select((b, i) => (b, i)).OrderBy(x => Rank(x.b)).ThenBy(x => x.i).Select(x => x.b)];
    }

    static PropAdjust AddedAdjust(MhoExtendedModManager.PropFit f) => new(f.X, f.Y, f.Z, f.TurnX, f.TurnY, f.TurnZ, f.Scale is float s && s > 0 ? s : 1);

    /// <summary>An added prop's slider let go: its fit kept in the mod and put into the package (the preview reloads after).</summary>
    async void AddedFitCommitted() => await PutAddedFit();

    /// <summary>The added prop's fit as the sliders have it, kept in the mod and put into its package (also Build into Mod's).</summary>
    async Task<bool> PutAddedFit()
    {
        if (settingPropFit || addedPick is not { } e) return false;
        string before = PropBefore();
        var f = e.Fit;
        f.Scale = Math.Abs(propScale.Value - 1) > 0.005f ? MathF.Round(propScale.Value, 2) : null;
        (f.X, f.Y, f.Z) = (MathF.Round(propMoveX.Value, 2), MathF.Round(propMoveY.Value, 2), MathF.Round(propMoveZ.Value, 2));
        (f.TurnX, f.TurnY, f.TurnZ) = (propTurnX.Value, propTurnY.Value, propTurnZ.Value);
        status.Text = "Putting the prop's fit in…";
        bool ok = await host.RefitAddedProp(e);
        if (ok) PropDone(before, $"Prop Fit of {e.Name}");
        if (IsDisposed) return ok;
        if (!ok) Log($"{e.Name}: the fit couldn't be put in (see the message).");
        else Log($"{e.Name}: fit in {e.Package} (move {f.X:0.#}, {f.Y:0.#}, {f.Z:0.#}; turn {f.TurnX:0}°, {f.TurnY:0}°, {f.TurnZ:0}°; held on {e.Bone ?? "its own bone"}).");
        UpdateStatus();
        return ok;
    }

    void ResetFit()
    {
        if (addedPick is { } e)
        {
            SetPropFit(new PropAdjust() with { TurnZ = PowerBorrow.DefaultTurnZ(e.Class) }, save: false);
            AddedFitCommitted();
        }
        else SetPropFit(new PropAdjust(), save: true);
    }

    static Label? FindCaption(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l && l.Text.Equals(text, StringComparison.Ordinal)) return l;
            if (FindCaption(c, text) is { } inner) return inner;
        }
        return null;
    }
}
