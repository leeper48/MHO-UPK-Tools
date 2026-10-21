using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Borrow Look (Kurt, 2026-10-10: a hero borrowing another hero's power look, visual only): pick a hero and
/// one of their powers; choose the whole power (its animation, effects and projectile) or the target's timing with only what
/// its effects draw. The choice is suggested from how the two powers are built (PowerBorrow.Suggest).
/// </summary>
sealed class BorrowPowerForm : Form
{
    readonly DropDown heroBox = new() { Width = 460 }, powerBox = new() { Width = 460 };
    readonly RadioButton whole = new() { Text = "Whole Power: Its Animation, Effects and Projectile", AutoSize = true, Checked = true };
    readonly RadioButton look = new() { Text = "Keep This Power's Timing: Only What Its Effects Draw", AutoSize = true };
    readonly CheckBox fit = new() { Text = "Fit the Animation to This Power's Timing", AutoSize = true, Checked = true };
    readonly Label suggestion = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 8), MaximumSize = new Size(560, 0) };
    readonly Label mismatchNote = new() { AutoSize = true, ForeColor = Color.FromArgb(240, 185, 70), Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(560, 0), Visible = false };
    readonly Dictionary<string, PowerBorrow.PowerKind?> kindOf = new(StringComparer.OrdinalIgnoreCase);
    PowerBorrow.PowerKind? targetKind;
    readonly string targetName;
    readonly Label enemyNote = new() { AutoSize = true, ForeColor = Color.FromArgb(240, 185, 70), Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(560, 0), Visible = false };
    readonly Fx.GameData db;
    readonly string cooked, target;
    int request;
    // Vehicle Fit (Kurt, 2026-10-21): a borrowed ride's vehicle sized, the rider moved and leaned on it; shown only for a
    // power that brings a vehicle
    readonly FlowLayoutPanel vehicleFit = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(0, 6, 0, 0) };
    readonly LightSlider vSize = new() { Label = "Size", Min = 0.5f, Max = 2f, Step = 0.01f, Format = v => $"{v * 100:0} %" };
    readonly LightSlider vForward = new() { Label = "Forward / Back", Min = -40, Max = 40, Step = 1, Mark = 0, Home = () => 0, Format = v => $"{v:+0;-0;0}" };
    readonly LightSlider vUp = new() { Label = "Up / Down", Min = -40, Max = 40, Step = 1, Mark = 0, Home = () => 0, Format = v => $"{v:+0;-0;0}" };
    readonly LightSlider vLean = new() { Label = "Lean", Min = -30, Max = 30, Step = 1, Mark = 0, Home = () => 0, Format = v => $"{v:+0;-0;0}°" };
    readonly Label vehicleHead = new() { Text = "VEHICLE FIT", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 2) };
    float autoSize = 1;
    bool sizeSet;

    /// <summary>The chosen donor power (prototype, display name), the mode and the fit.</summary>
    public PowerBorrowEntry? Result { get; private set; }

    public BorrowPowerForm(Fx.GameData db, string cooked, string targetPower, string targetName, PowerBorrowEntry? current)
    {
        this.db = db; this.cooked = cooked; target = targetPower; this.targetName = targetName;
        Text = "Borrow a Power's Look";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new Label { Text = "Borrow a Power's Look for " + targetName, AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
        t.Controls.Add(head, 0, 0); t.SetColumnSpan(head, 2);
        var hint = new Label
        {
            Text = "The look only: what the power does in the game stays this power's (the server decides that). Only this hero's power packages change, for every costume and anyone using this mod; the other hero stays as it is.",
            AutoSize = true, Tag = "subtle", MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10),
        };
        t.Controls.Add(hint, 0, 1); t.SetColumnSpan(hint, 2);
        t.Controls.Add(new Label { Text = "Hero", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 2);
        t.Controls.Add(heroBox, 1, 2);
        t.Controls.Add(new Label { Text = "Power", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 3);
        t.Controls.Add(powerBox, 1, 3);
        var modes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        modes.Controls.AddRange([whole, fit, look, suggestion, mismatchNote, enemyNote, vehicleFit]);
        foreach (var sl in new[] { vSize, vForward, vUp, vLean }) { sl.Width = 460; sl.Height = 30; sl.Margin = new Padding(0, 2, 0, 0); }
        vehicleFit.Controls.AddRange([vehicleHead, vSize, vForward, vUp, vLean]);
        vSize.Home = () => autoSize;
        vSize.ValueChanged += () => sizeSet = Math.Abs(vSize.Value - autoSize) > 0.005f;
        Ui.Tip(vSize, "The vehicle's size. It starts sized for this hero (this hero's height over the other hero's); move it to make the vehicle bigger or smaller. Double-click: back to that size.");
        Ui.Tip(vForward, "Moves the rider forward (+) or back (−) on the vehicle, in game units; the vehicle stays where it is. Double-click: back to 0.");
        Ui.Tip(vUp, "Moves the rider up (+) or down (−) on the vehicle, in game units (a seat is about 30 up). Double-click: back to 0.");
        Ui.Tip(vLean, "Leans the rider forward (+) or back (−), in degrees, from the hips up and down together. Double-click: back to 0.");
        Ui.Tip(vehicleHead, "This power brings the other hero's vehicle. Fit it to this hero; the 3D preview shows the result after Save.");
        fit.Margin = new Padding(22, 0, 0, 6);
        t.Controls.Add(modes, 0, 4); t.SetColumnSpan(modes, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Change nothing (Esc)."),
            Ui.AccentButton("Borrow", Choose, tip: "This power takes the chosen power's look; the preview shows it, Save puts it into the mod (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 5); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        Ui.Tip(heroBox, "The hero whose power look to borrow.");
        Ui.Tip(powerBox, "The power whose look this power takes. Only powers with an animation of their own are listed; powers built like this one (thrown, beam, or melee and area) come first, the others are dimmed with what they are.");
        Ui.Tip(whole, "The other power's animation, its effects and sounds, and its projectile's look (when both powers fire one). For powers built alike: a projectile from a projectile, a beam from a beam.");
        Ui.Tip(fit, "Plays the borrowed animation faster or slower so it lasts as long as this power's own (the game repeats the power at its own pace; an animation longer than that is cut off).");
        Ui.Tip(look, "This power keeps its own animation, timing and sounds; its beams and effects draw the other power's instead (and the other power's hand effects are added when this one has none). For powers built differently: a channeled beam's look on a quick beam.");
        whole.CheckedChanged += (_, _) => fit.Enabled = whole.Checked;

        // the heroes: every hero base package in the game
        var heroes = Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(f => f.Split('_').Length == 5 && !f.Contains("bak", StringComparison.OrdinalIgnoreCase)).Select(f => f.Split('_')[3])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => AutoTags.DisplayName(h) ?? h, StringComparer.OrdinalIgnoreCase).ToList();
        heroBox.Format = o => AutoTags.DisplayName((string)o) ?? (string)o;
        heroBox.Items.AddRange(heroes);
        heroBox.SelectedIndexChanged += (_, _) => LoadPowers();
        // powers built like this one first (a thrown power from a thrown one, a beam from a beam); the others dimmed, with their kind
        powerBox.Format = o => o is Fx.PowerList.Power p ? p.Name + (Different(p) is { } k ? "  ·  " + k.Label : "") : o.ToString() ?? "";
        powerBox.ItemColor = i => i >= 0 && i < powerBox.Items.Count && powerBox.Items[i] is Fx.PowerList.Power p && Different(p) != null ? Ui.Subtle : null;
        powerBox.SelectedIndexChanged += (_, _) => Suggest();
        string? startHero = current != null ? current.DonorHero ?? PowerBorrow.HeroOf(current.Donor) : null;
        if (current != null) { whole.Checked = !current.KeepTiming; look.Checked = current.KeepTiming; fit.Checked = current.Fit; vForward.Value = current.RiderForward; vUp.Value = current.RiderUp; vLean.Value = current.RiderLean; }
        savedSize = current?.VehicleScale;
        targetHero = current?.Hero;
        Shown += (_, _) =>
        {
            int at = startHero == null ? -1 : heroes.FindIndex(h => h.Equals(startHero, StringComparison.OrdinalIgnoreCase));
            heroBox.SelectedIndex = at >= 0 ? at : 0;
            wantPower = current?.Donor;
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; Choose(); }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 640, 720);
    }

    string? wantPower, targetHero;
    float? savedSize;

    /// <summary>The power's kind when it differs from this power's (null when it matches or isn't known).</summary>
    PowerBorrow.PowerKind? Different(Fx.PowerList.Power p) =>
        targetKind != null && kindOf.GetValueOrDefault(p.Prototype) is { } k && !k.Matches(targetKind) ? k : null;

    void LoadPowers()
    {
        if (heroBox.SelectedItem is not string hero) return;
        int req = ++request;
        powerBox.Items.Clear();
        suggestion.Text = "Reading the powers…";
        string c0 = cooked, tgt = target;
        Task.Run(() =>
        {
            var g = new Fx.FxGame(c0, []);
            var tk = PowerBorrow.KindOf(db, g, tgt);
            var list = Fx.PowerList.For(db, hero, c0, []).Where(p => p.Animations.Count > 0).ToList();
            var ks = list.ToDictionary(p => p.Prototype, p => PowerBorrow.KindOf(db, g, p.Prototype), StringComparer.OrdinalIgnoreCase);
            // matching first, each part in the list's own order
            var sorted = list.OrderBy(p => tk != null && ks[p.Prototype] is { } k && !k.Matches(tk) ? 1 : 0).ToList();
            return (tk, ks, sorted);
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != request) return;
            if (t.Status != TaskStatus.RanToCompletion) { suggestion.Text = "This hero's powers couldn't be read."; return; }
            targetKind = t.Result.tk;
            foreach (var (k, v) in t.Result.ks) kindOf[k] = v;
            powerBox.Items.AddRange(t.Result.sorted);
            int at = wantPower == null ? -1 : t.Result.sorted.FindIndex(p => p.Prototype.Equals(wantPower, StringComparison.OrdinalIgnoreCase));
            wantPower = null;
            if (t.Result.sorted.Count > 0) powerBox.SelectedIndex = Math.Max(0, at);
            else suggestion.Text = "This hero has no powers with an animation.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The mode suggested for the two powers (read in the background).</summary>
    void Suggest()
    {
        if (powerBox.SelectedItem is not Fx.PowerList.Power p) return;
        int req = ++request;
        suggestion.Text = "Comparing the two powers…";
        string c = cooked;
        string root = Path.GetFullPath(Path.Combine(c, "..", "..", ".."));
        string? donorHeroNow = heroBox.SelectedItem as string;
        Task.Run(() =>
        {
            var g = new Fx.FxGame(c, []);
            var (keep, why) = PowerBorrow.Suggest(db, g, target, p.Prototype);
            float? vehicle = null;
            try { vehicle = donorHeroNow is string dh ? PowerBorrow.VehicleAutoScale(db, g, target, p.Prototype, dh, targetHero) : null; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
            return (keep, why, PowerBorrow.KeepTimingTakes(db, g, target, p.Prototype), PowerBorrow.EnemyUsers(db, g, root, target, p.Prototype), vehicle);
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || t.Status != TaskStatus.RanToCompletion) return;
            var (keep, why, takes, enemies, vehicle) = t.Result;
            // a power that brings a vehicle: its fit
            vehicleFit.Visible = vehicle != null;
            if (vehicle is float auto)
            {
                autoSize = auto;
                vSize.Mark = auto;
                vSize.Value = savedSize is float ss && ss > 0 ? ss : auto;
                sizeSet = savedSize != null;
            }
            // Keep This Power's Timing only when it changes something (it redraws beams with beams, or adds start effects to a
            // power with none): otherwise it would borrow nothing
            look.Enabled = takes != null;
            Ui.Tip(look, takes != null
                ? $"This power keeps its own animation, timing and sounds; only what its effects draw changes: {takes}."
                : "Not for these two powers: this one has no beams the other's can redraw, and has effects of its own (the other's start effects are only added to a power with none). Keeping this power's timing would change nothing.");
            if (keep && takes == null) { keep = false; why = "the whole power (keeping this power's timing would change nothing for these two)."; }
            whole.Checked = !keep; look.Checked = keep;
            suggestion.Text = "Suggested: " + why + (takes != null && keep ? $" It changes: {takes}." : "");
            // the target power's class is an enemy's too: they'd get the borrowed look
            // built differently: what will (not) show
            string? mm = PowerBorrow.Mismatch(targetKind, kindOf.GetValueOrDefault(p.Prototype), targetName, p.Name);
            mismatchNote.Visible = mm != null;
            mismatchNote.ForeColor = enemyNote.ForeColor = Color.FromArgb(240, 185, 70);   // (the theme recolors labels: set again here)
            mismatchNote.Text = mm ?? "";
            enemyNote.Visible = enemies.Count > 0;
            enemyNote.Text = enemies.Count == 0 ? "" : "Enemies use this power's look too: " + string.Join(", ", enemies.Select(e => Path.GetFileNameWithoutExtension(e))) +
                ". Borrowing changes how they look as well (the game shares the power's package with them).";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Choose()
    {
        if (powerBox.SelectedItem is not Fx.PowerList.Power p || heroBox.SelectedItem is not string hero) return;
        Result = new PowerBorrowEntry
        {
            Donor = p.Prototype, DonorName = $"{AutoTags.DisplayName(hero) ?? hero}: {p.Name}",
            KeepTiming = look.Checked, Fit = fit.Checked, DonorHero = hero,
        };
        if (vehicleFit.Visible)
        {
            Result.VehicleScale = sizeSet ? MathF.Round(vSize.Value, 2) : null;
            Result.RiderForward = vForward.Value; Result.RiderUp = vUp.Value; Result.RiderLean = vLean.Value;
        }
        DialogResult = DialogResult.OK;
    }
}
