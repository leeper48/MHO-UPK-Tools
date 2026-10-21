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
    // Vehicle Fit (Kurt, 2026-10-21: adjusted after the vehicle is in, here, not in the Borrow window): the borrowed ride's
    // vehicle size, the rider moved and leaned; each change rebuilds the borrow and the preview shows it
    readonly FlowLayoutPanel vehicleFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 4, 0, 0) };
    LightSlider? vSize, vForward, vUp, vLean;
    bool vShowing;
    readonly HashSet<string> vehicleChecked = new(StringComparer.OrdinalIgnoreCase);

    Control BorrowSection()
    {
        var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        box.Controls.Add(new Label { Text = "BORROWED LOOK", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 2) });
        borrowButton = Ui.FlatButton("Borrow Look", PickBorrow, tip: "Give this power another hero's power look: its animation, effects and projectile, or only what its effects draw while this power keeps its timing. Only the look changes (what the power does stays this one's), on this hero's power packages: every costume of the hero, anyone using this mod; the other hero stays as it is.");
        borrowRemove = Ui.FlatButton("Game's Look", RemoveBorrow, tip: "Take this power back to its own look (the borrowed one comes out of the mod at Save).");
        box.Controls.Add(Toolbar(borrowButton, borrowRemove));
        box.Controls.Add(borrowCaption);
        LightSlider Slider(string label, float min, float max, float step, Func<float, string> format, string tip)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Mark = 0, Home = () => 0, Format = format, Width = (int)(320 * S), Height = (int)(30 * S), Margin = new Padding(0, 2, 0, 0) };
            Ui.Tip(sl, tip);
            sl.Committed += VehicleFitChanged;
            return sl;
        }
        vSize = Slider("Size", 0.5f, 2f, 0.01f, v => $"{v * 100:0} %", "The vehicle's size. It starts sized for this hero (this hero's height over the other hero's); move it to make the vehicle bigger or smaller. Double-click: back to that size. The preview shows it once the borrow is rebuilt.");
        vForward = Slider("Forward / Back", -40, 40, 1, v => $"{v:+0;-0;0}", "Moves the rider forward (+) or back (−) on the vehicle, in game units; the vehicle stays where it is. Double-click: back to 0.");
        vUp = Slider("Up / Down", -40, 40, 1, v => $"{v:+0;-0;0}", "Moves the rider up (+) or down (−) on the vehicle, in game units. Double-click: back to 0.");
        vLean = Slider("Lean", -30, 30, 1, v => $"{v:+0;-0;0}°", "Leans the rider forward (+) or back (−), in degrees. Double-click: back to 0.");
        var head = new Label { Text = "VEHICLE FIT", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 0, 2) };
        Ui.Tip(head, "This borrowed power brings the other hero's vehicle: fit it to this hero. Each change rebuilds the borrow, then the 3D preview shows it; Save Changes puts it into the mod.");
        vehicleFit.Controls.AddRange([head, vSize, vForward, vUp, vLean]);
        box.Controls.Add(vehicleFit);
        return box;
    }

    /// <summary>The selected borrow's Vehicle Fit into its sliders (shown when its last build brought a vehicle).</summary>
    void ShowVehicleFit(PowerBorrowEntry? e)
    {
        // a borrow not built in this session yet: whether it brings a vehicle, worked out in the background
        if (e != null && e.VehicleAuto == null && game is { } g0 && !vehicleChecked.Contains(e.Power + "|" + e.Donor))
        {
            vehicleChecked.Add(e.Power + "|" + e.Donor);
            var entry = e;
            Task.Run(() =>
            {
                powerDb ??= new Fx.GameData(Fx.SipArchive.Load(Path.Combine(g0.Root, "Data", "Game", "Calligraphy.sip")));
                return entry.DonorHero is string dh ? PowerBorrow.VehicleAutoScale(powerDb, new Fx.FxGame(g0.Cooked, []), entry.Power, entry.Donor, dh, entry.Hero) : null;
            }).ContinueWith(t =>
            {
                if (IsDisposed || t.Status != TaskStatus.RanToCompletion || t.Result == null) return;
                entry.VehicleAuto ??= t.Result;
                if (powerList.SelectedItem is Fx.PowerList.Power s1 && s1.Prototype.Equals(entry.Power, StringComparison.OrdinalIgnoreCase)) ShowBorrow(s1);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        bool show = e?.VehicleAuto is float && !borrowBuilding;
        vehicleFit.Visible = e?.VehicleAuto is float;
        foreach (var sl in new[] { vSize, vForward, vUp, vLean }) if (sl != null) sl.Enabled = show;
        if (e?.VehicleAuto is not float auto || vSize == null || vForward == null || vUp == null || vLean == null) return;
        vShowing = true;
        vSize.Home = () => auto; vSize.Mark = auto;
        vSize.Value = e.VehicleScale is float vs && vs > 0 ? vs : auto;
        vForward.Value = e.RiderForward; vUp.Value = e.RiderUp; vLean.Value = e.RiderLean;
        vShowing = false;
    }

    /// <summary>A Vehicle Fit slider let go: the selected borrow takes the values and is built again.</summary>
    void VehicleFitChanged()
    {
        if (vShowing || borrowBuilding || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { VehicleAuto: float auto } e
            || vSize == null || vForward == null || vUp == null || vLean == null) return;
        e.VehicleScale = Math.Abs(vSize.Value - auto) > 0.005f ? MathF.Round(vSize.Value, 2) : null;
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
        ShowVehicleFit(e);
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
