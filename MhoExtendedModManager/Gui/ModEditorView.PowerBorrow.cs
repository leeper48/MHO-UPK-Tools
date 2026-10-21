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
    // Prop Fit and Rider Fit (Kurt, 2026-10-21: adjusted after the borrow is in, here, not in the Borrow window): each prop
    // the borrowed power brings (a vehicle, a mount, a weapon) sized, moved and turned; on a vehicle the rider moved and
    // leaned. Each change rebuilds the borrow and the preview shows it.
    readonly FlowLayoutPanel propFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 4, 0, 0) };
    readonly FlowLayoutPanel riderFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 4, 0, 0) };
    readonly DropDown propPick = new() { Width = 320 };
    LightSlider? pSize, pX, pY, pZ, pTX, pTY, pTZ, vForward, vUp, vLean;
    bool vShowing;
    readonly HashSet<string> fitBuilt = new(StringComparer.OrdinalIgnoreCase);

    Control BorrowSection()
    {
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        box.Controls.Add(new Label { Text = "BORROWED LOOK", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 2) });
        borrowButton = Ui.FlatButton("Borrow Look", PickBorrow, tip: "Give this power another hero's power look: its animation, effects and projectile, or only what its effects draw while this power keeps its timing. Only the look changes (what the power does stays this one's), on this hero's power packages: every costume of the hero, anyone using this mod; the other hero stays as it is.");
        borrowRemove = Ui.FlatButton("Game's Look", RemoveBorrow, tip: "Take this power back to its own look (the borrowed one comes out of the mod at Save).");
        box.Controls.Add(Toolbar(borrowButton, borrowRemove));
        box.Controls.Add(borrowCaption);
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
        pX = Slider("Move X", -60, 60, 1, Units, "Moves the prop along its own X axis, in game units. Double-click: back to 0.", PropFitChanged);
        pY = Slider("Move Y", -60, 60, 1, Units, "Moves the prop along its own Y axis, in game units. Double-click: back to 0.", PropFitChanged);
        pZ = Slider("Move Z", -60, 60, 1, Units, "Moves the prop along its own Z axis, in game units. Double-click: back to 0.", PropFitChanged);
        pTX = Slider("Turn X", -180, 180, 1, Deg, "Turns the prop about its own X axis (roll), in degrees. Double-click: back to 0.", PropFitChanged);
        pTY = Slider("Turn Y", -180, 180, 1, Deg, "Turns the prop about its own Y axis (pitch), in degrees. Double-click: back to 0.", PropFitChanged);
        pTZ = Slider("Turn Z", -180, 180, 1, Deg, "Turns the prop about its own Z axis (yaw), in degrees. Double-click: back to 0.", PropFitChanged);
        var propHead = new Label { Text = "PROP FIT", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(propHead, "The props this borrowed power brings (the other hero's vehicle, mount or weapons): pick one, then size, move and turn it. Each change rebuilds the borrow, then the 3D preview shows it; Save Changes puts it into the mod.");
        propPick.Width = (int)(420 * S);
        propPick.Format = o => o is PowerBorrow.SpawnedProp sp ? PropName(sp) : o.ToString() ?? "";
        propPick.SelectedIndexChanged += (_, _) => { if (!vShowing && powerList.SelectedItem is Fx.PowerList.Power p0) ShowFits(BorrowOf(p0.Prototype)); };
        Ui.Tip(propPick, "The prop to fit.");
        propFit.Controls.AddRange([propHead, propPick, pSize, pX, pY, pZ, pTX, pTY, pTZ]);
        vForward = Slider("Forward / Back", -40, 40, 1, Units, "Moves the rider forward (+) or back (−) on the vehicle, in game units; the vehicle stays where it is. Double-click: back to 0.", RiderFitChanged);
        vUp = Slider("Up / Down", -40, 40, 1, Units, "Moves the rider up (+) or down (−) on the vehicle, in game units. Double-click: back to 0.", RiderFitChanged);
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
        riderFit.Visible = props.Any(x => x.Vehicle);
        bool on = !borrowBuilding;
        foreach (var sl in new[] { pSize, pX, pY, pZ, pTX, pTY, pTZ, vForward, vUp, vLean }) if (sl != null) sl.Enabled = on;
        propPick.Enabled = on;
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
                pX.Value = fit?.X ?? 0; pY.Value = fit?.Y ?? 0; pZ.Value = fit?.Z ?? 0;
                pTX.Value = fit?.TurnX ?? 0; pTY.Value = fit?.TurnY ?? 0; pTZ.Value = fit?.TurnZ ?? 0;
            }
            vForward.Value = e.RiderForward; vUp.Value = e.RiderUp; vLean.Value = e.RiderLean;
        }
        finally { vShowing = false; }
    }

    /// <summary>A Prop Fit slider let go: the selected prop's fit into the borrow, which is built again.</summary>
    void PropFitChanged()
    {
        if (vShowing || borrowBuilding || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e || propPick.SelectedItem is not PowerBorrow.SpawnedProp sp
            || pSize == null || pX == null || pY == null || pZ == null || pTX == null || pTY == null || pTZ == null) return;
        e.Props ??= [];
        var fit = e.Props.FirstOrDefault(f => f.Class.Equals(sp.Class, StringComparison.OrdinalIgnoreCase));
        if (fit == null) e.Props.Add(fit = new PropFit { Class = sp.Class });
        fit.Scale = Math.Abs(pSize.Value - sp.Auto) > 0.005f ? MathF.Round(pSize.Value, 2) : null;
        if (sp.Vehicle) e.VehicleScale = null;   // (the older Vehicle Fit size: the prop's own now)
        fit.X = pX.Value; fit.Y = pY.Value; fit.Z = pZ.Value;
        fit.TurnX = pTX.Value; fit.TurnY = pTY.Value; fit.TurnZ = pTZ.Value;
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
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        chosen.Power = p.Prototype; chosen.Name = p.Name;
        draft.PowerBorrows.Add(chosen);
        RebuildBorrows();
    }

    void RemoveBorrow()
    {
        if (powerList.SelectedItem is not Fx.PowerList.Power p) return;
        draft.PowerBorrows.RemoveAll(e => e.Power.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase));
        RebuildBorrows();
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
