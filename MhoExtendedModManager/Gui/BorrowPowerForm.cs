using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Borrow Look (Kurt, 2026-10-10: a hero borrowing another hero's power look, visual only): pick a hero and
/// one of their powers; choose the whole power (its animation, effects and projectile) or the target's timing with only what
/// its effects draw. The choice is suggested from how the two powers are built (PowerBorrow.Suggest).
/// </summary>
sealed class BorrowPowerForm : Form
{
    readonly DropDown heroBox = new() { Width = 320 }, powerBox = new() { Width = 320 };
    readonly RadioButton whole = new() { Text = "Whole Power: Its Animation, Effects and Projectile", AutoSize = true, Checked = true };
    readonly RadioButton look = new() { Text = "Keep This Power's Timing: Only What Its Effects Draw", AutoSize = true };
    readonly CheckBox fit = new() { Text = "Fit the Animation to This Power's Timing", AutoSize = true, Checked = true };
    readonly Label suggestion = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 8) };
    readonly Fx.GameData db;
    readonly string cooked, target;
    int request;

    /// <summary>The chosen donor power (prototype, display name), the mode and the fit.</summary>
    public PowerBorrowEntry? Result { get; private set; }

    public BorrowPowerForm(Fx.GameData db, string cooked, string targetPower, string targetName, PowerBorrowEntry? current)
    {
        this.db = db; this.cooked = cooked; target = targetPower;
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
        modes.Controls.AddRange([whole, fit, look, suggestion]);
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
        Ui.Tip(powerBox, "The power whose look this power takes. Only powers with an animation of their own are listed.");
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
        powerBox.Format = o => o is Fx.PowerList.Power p ? p.Name : o.ToString() ?? "";
        powerBox.SelectedIndexChanged += (_, _) => Suggest();
        string? startHero = current != null ? current.DonorHero ?? PowerBorrow.HeroOf(current.Donor) : null;
        if (current != null) { whole.Checked = !current.KeepTiming; look.Checked = current.KeepTiming; fit.Checked = current.Fit; }
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
        Ui.FitToScreen(this, 640, 400);
    }

    string? wantPower;

    void LoadPowers()
    {
        if (heroBox.SelectedItem is not string hero) return;
        int req = ++request;
        powerBox.Items.Clear();
        suggestion.Text = "Reading the powers…";
        Task.Run(() => Fx.PowerList.For(db, hero, cooked, []).Where(p => p.Animations.Count > 0).ToList()).ContinueWith(t =>
        {
            if (IsDisposed || req != request) return;
            if (t.Status != TaskStatus.RanToCompletion) { suggestion.Text = "This hero's powers couldn't be read."; return; }
            powerBox.Items.AddRange(t.Result);
            int at = wantPower == null ? -1 : t.Result.FindIndex(p => p.Prototype.Equals(wantPower, StringComparison.OrdinalIgnoreCase));
            wantPower = null;
            if (t.Result.Count > 0) powerBox.SelectedIndex = Math.Max(0, at);
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
        Task.Run(() => PowerBorrow.Suggest(db, new Fx.FxGame(c, []), target, p.Prototype)).ContinueWith(t =>
        {
            if (IsDisposed || req != request || t.Status != TaskStatus.RanToCompletion) return;
            var (keep, why) = t.Result;
            whole.Checked = !keep; look.Checked = keep;
            suggestion.Text = "Suggested: " + why;
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
        DialogResult = DialogResult.OK;
    }
}
