using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Powers tab's Borrow Look (Kurt, 2026-10-10: a hero borrowing another hero's power look, visual only): pick a hero and
/// one of their powers; choose the whole power (its animation, effects and projectile) or the target's timing with only what
/// its effects draw. The choice is suggested from how the two powers are built (PowerBorrow.Suggest).
/// </summary>
sealed class BorrowPowerForm : Form
{
    /// <summary>A team-up or NPC / enemy donor (Kurt, 2026-11-10): its package, class, kind and how many of its powers have an
    /// animation (Model\StockData\borrow_donors.txt, from --donor-census).</summary>
    sealed record DonorChar(string File, string Class, string Kind, int Count)
    {
        public string Display => System.Text.RegularExpressions.Regex.Replace(MaterialVariations.Readable(Class), "(?<=[a-z])(?=[A-Z])", " ");
    }

    static List<DonorChar> Donors()
    {
        using var s = typeof(BorrowPowerForm).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.borrow_donors.txt");
        if (s == null) return [];
        using var r = new StreamReader(s);
        var res = new List<DonorChar>();
        for (string? l = r.ReadLine(); l != null; l = r.ReadLine())
            if (l.Split('\t') is { Length: 4 } c && int.TryParse(c[3], out int n)) res.Add(new DonorChar(c[0], c[1], c[2], n));
        return res;
    }

    readonly DropDown heroBox = new() { Width = 460 }, powerBox = new() { Width = 460 };
    readonly RadioButton whole = new() { Text = "Whole Power: Its Animation, Effects and Projectile", AutoSize = true, Checked = true };
    readonly RadioButton look = new() { Text = "Keep This Power's Timing: Only What Its Effects Draw", AutoSize = true };
    readonly CheckBox fit = new() { Text = "Fit the Animation to This Power's Timing", AutoSize = true, Checked = true };
    // Whole Power's parts (a user, 2026-11-09: Taskmaster's power brought his shield and sword with no way to leave them out;
    // Cyclops wanted Storm's Electrocute from his own eye-beam animation): unticked, this power keeps its own of that part
    readonly CheckBox partAnims = new() { Text = "Animations", AutoSize = true, Checked = true };
    readonly CheckBox partEffects = new() { Text = "Effects and Sounds", AutoSize = true, Checked = true };
    readonly CheckBox partProps = new() { Text = "Props (Weapons and Vehicles)", AutoSize = true, Checked = true };
    readonly Label suggestion = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 4, 0, 8), MaximumSize = new Size(560, 0) };
    readonly Label mismatchNote = new() { AutoSize = true, ForeColor = Color.FromArgb(240, 185, 70), Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(560, 0), Visible = false };
    readonly Dictionary<string, PowerBorrow.PowerKind?> kindOf = new(StringComparer.OrdinalIgnoreCase);
    PowerBorrow.PowerKind? targetKind;
    readonly string targetName;
    // Its Summon's Look Too (Kurt, 2026-11-05): shown when both powers summon a model
    readonly CheckBox summonLook = new() { Text = "Its Summon's Look Too", AutoSize = true, Checked = true, Visible = false, Margin = new Padding(0, 4, 0, 6) };
    bool? summonWanted;
    // Its Area's Look Too (Kurt, 2026-11-05): shown when the other power puts down an area with a look (Squirrel Girl's rain)
    readonly CheckBox areaLook = new() { Text = "Its Area's Look Too", AutoSize = true, Checked = true, Visible = false, Margin = new Padding(0, 0, 0, 6) };
    bool? areaWanted;
    // Its Triggered Powers' Look Too (a user, 2026-11-09): shown when both powers set off powers with a look of their own
    readonly CheckBox triggeredLook = new() { Text = "Its Triggered Powers' Look Too", AutoSize = true, Checked = true, Visible = false, Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(560, 0) };
    bool? triggeredWanted;
    readonly Label costumeNote = new() { AutoSize = true, ForeColor = Color.FromArgb(240, 185, 70), Margin = new Padding(0, 0, 0, 8), MaximumSize = new Size(560, 0), Visible = false };
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
    // the power's name and icon (Kurt, 2026-10-23): the borrowed power's, or a name typed in; into the Strings and Icons tabs
    readonly CheckBox useName = new() { Text = "Rename This Power", AutoSize = true, Checked = true, Margin = new Padding(0, 10, 0, 0) };
    readonly TextBox nameBox = new() { Width = 300, Margin = new Padding(22, 2, 0, 0) };
    readonly CheckBox useIcon = new() { Text = "Use Its Icon", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
    string? nameShown;

    /// <summary>The name to give this power (Rename This Power), or null.</summary>
    public string? NewName => useName.Checked && nameBox.Text.Trim() is { Length: > 0 } n ? n : null;
    /// <summary>This power takes the borrowed power's icon.</summary>
    public bool UseIcon => useIcon.Checked && useIcon.Enabled;
    bool iconWanted = true, settingIcon;
    const string IconTip = "This power's icon becomes the borrowed power's (the power bar's icon, and the large one where the game has it). They go into the Icons tab; Reset Power takes them off again.";

    /// <summary>The chosen donor power (prototype, display name), the mode and the fit.</summary>
    public PowerBorrowEntry? Result { get; private set; }

    public BorrowPowerForm(Fx.GameData db, string cooked, string targetPower, string targetName, PowerBorrowEntry? current, string? costumeWarning = null)
    {
        this.db = db; this.cooked = cooked; target = targetPower; this.targetName = targetName;
        Text = "Borrow Power";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        var t = layoutTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new Label { Text = "Borrow a Power for " + targetName, AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
        t.Controls.Add(head, 0, 0); t.SetColumnSpan(head, 2);
        var hint = new Label
        {
            Text = "The look only: what the power does in the game stays this power's (the server decides that). Only this hero's power packages change, for every costume and anyone using this mod; the other character (a hero, team-up or enemy) stays as it is.",
            AutoSize = true, Tag = "subtle", MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10),
        };
        t.Controls.Add(hint, 0, 1); t.SetColumnSpan(hint, 2);
        t.Controls.Add(KindStrip(), 1, 2);
        t.Controls.Add(new Label { Text = "Character", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 3);
        t.Controls.Add(heroBox, 1, 3);
        t.Controls.Add(new Label { Text = "Power", AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, 4);
        t.Controls.Add(powerBox, 1, 4);
        var modes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        var parts = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(22, 0, 0, 2) };
        parts.Controls.AddRange([partAnims, partEffects, partProps]);
        foreach (var c in new[] { partAnims, partEffects, partProps }) c.Margin = new Padding(0, 2, 14, 2);
        modes.Controls.AddRange([whole, parts, fit, look, suggestion, mismatchNote, enemyNote, costumeNote, summonLook, areaLook, triggeredLook, useName, nameBox, useIcon]);
        Ui.Tip(useName, "The power's name in game becomes this (the borrowed power's, or type your own). It goes into the Strings tab (English), where you can change it; Reset Power takes it off again.");
        Ui.Tip(nameBox, "The new name of this power in game.");
        Ui.Tip(useIcon, IconTip);
        useIcon.CheckedChanged += (_, _) => { if (!settingIcon) iconWanted = useIcon.Checked; };
        useName.CheckedChanged += (_, _) => nameBox.Enabled = useName.Checked;
        if (current != null && current.NameId == null) useName.Checked = false;
        if (current != null && current.Icons == null) useIcon.Checked = false;
        iconWanted = useIcon.Checked;
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
        t.Controls.Add(modes, 0, 5); t.SetColumnSpan(modes, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Change nothing (Esc)."),
            Ui.AccentButton("Borrow", Choose, tip: "This power takes the chosen power's look; the preview shows it, Save puts it into the mod (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 6); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        Ui.Tip(heroBox, "The character whose power look to borrow (the tabs above choose heroes, team-ups or NPCs and enemies). Type to search.");
        Ui.Tip(summonLook, "Both powers summon something. Ticked, what this power summons takes the other power's summon's model and animations (Hawkeye's turret as Squirrel Girl's squirrel); what it does in the game stays this power's.");
        summonLook.CheckedChanged += (_, _) => { if (summonLook.Visible) summonWanted = summonLook.Checked; };
        if (current != null) summonWanted = current.SummonLook;
        Ui.Tip(areaLook, "The other power's look comes partly from an area it puts down (Squirrel Girl's rain of squirrels). Ticked, those effects play at the target as part of this power, from the hit, for the effect's own length (one that loops: " + PowerBorrow.AreaLoopSeconds + " s). It's a look, not a real area: it doesn't follow a moving target.");
        areaLook.CheckedChanged += (_, _) => { if (areaLook.Visible) areaWanted = areaLook.Checked; };
        if (current != null) areaWanted = current.AreaLook;
        Ui.Tip(triggeredLook, "The other power's look comes partly from powers it sets off (Venom's Symbiotic Spikes: the spikes from the ground). Ticked, the powers this power sets off (Sorcerous Eruption's area) draw those instead: their effects and sounds. What they do in the game stays this power's.");
        triggeredLook.CheckedChanged += (_, _) => { if (triggeredLook.Visible) triggeredWanted = triggeredLook.Checked; };
        if (current != null) triggeredWanted = current.TriggeredLook;
        Ui.Tip(powerBox, "The power whose look this power takes. Only powers with an animation of their own are listed; powers built like this one (thrown, beam, or melee and area) come first, the others are dimmed with what they are.");
        Ui.Tip(whole, "The other power's animation, its effects and sounds, and its projectile's look (when both powers fire one). For powers built alike: a projectile from a projectile, a beam from a beam.");
        Ui.Tip(fit, "Plays the borrowed animation faster or slower so it lasts as long as this power's own (the game repeats the power at its own pace; an animation longer than that is cut off).");
        Ui.Tip(look, "This power keeps its own animation, timing and sounds; its beams and effects draw the other power's instead (and the other power's hand effects are added when this one has none). For powers built differently: a channeled beam's look on a quick beam.");
        Ui.Tip(partAnims, "Ticked: the other power's animation plays. Unticked: this power keeps its own animation (Cyclops firing Storm's Electrocute from his eyes), and this hero keeps his weapons.");
        Ui.Tip(partEffects, "Ticked: the other power's particles, sounds, beams, camera shakes and projectile look. Unticked: this power keeps its own.");
        Ui.Tip(partProps, "Ticked: the weapons and vehicles the other power shows or brings (Taskmaster's shield and sword). Unticked: none of them; this power shows its own as before.");
        void PartsChanged()
        {
            fit.Enabled = whole.Checked && partAnims.Checked;
            // (at least one part: nothing ticked would borrow nothing)
            foreach (var c in new[] { partAnims, partEffects, partProps }) c.Enabled = whole.Checked && !(c.Checked && new[] { partAnims, partEffects, partProps }.Count(x => x.Checked) == 1);
        }
        foreach (var c in new[] { partAnims, partEffects, partProps }) c.CheckedChanged += (_, _) => PartsChanged();
        whole.CheckedChanged += (_, _) => { PartsChanged(); costumeNote.Visible = costumeWarning != null && whole.Checked; };
        costumeNote.Text = costumeWarning ?? "";

        // the heroes: every hero base package in the game
        var heroes = Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Where(f => f.Split('_').Length == 5 && !f.Contains("bak", StringComparison.OrdinalIgnoreCase)).Select(f => f.Split('_')[3])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => AutoTags.DisplayName(h) ?? h, StringComparer.OrdinalIgnoreCase).ToList();
        // then the team-ups and the NPCs and enemies whose powers have an animation (Kurt, 2026-11-10)
        var others = Donors().OrderBy(d => d.Kind == "Team-Up" ? 0 : 1).ThenBy(d => d.Display, StringComparer.OrdinalIgnoreCase).ToList();
        heroBox.Format = o => o is DonorChar d ? d.Display : AutoTags.DisplayName((string)o) ?? (string)o;
        kindLists[0] = [.. heroes];
        kindLists[1] = [.. others.Where(d => d.Kind == "Team-Up")];
        kindLists[2] = [.. others.Where(d => d.Kind != "Team-Up")];
        ShowKind(0, select: false);
        heroBox.SelectedIndexChanged += (_, _) => LoadPowers();
        // powers built like this one first (a thrown power from a thrown one, a beam from a beam); the others dimmed, with their kind
        powerBox.Format = o => o is Fx.PowerList.Power p ? p.Name + (Different(p) is { } k ? "  ·  " + k.Label : "") : o.ToString() ?? "";
        powerBox.ItemColor = i => i >= 0 && i < powerBox.Items.Count && powerBox.Items[i] is Fx.PowerList.Power p && Different(p) != null ? Ui.Subtle : null;
        powerBox.SelectedIndexChanged += (_, _) =>
        {
            // the name follows the picked power, unless one was typed
            if (powerBox.SelectedItem is Fx.PowerList.Power np && (nameBox.Text.Length == 0 || nameBox.Text == nameShown)) nameBox.Text = nameShown = np.Name;
            // no icon of its own (only the game's "No Icon" placeholder): Use Its Icon grayed out (Kurt, 2026-11-10)
            bool hasIcon = powerBox.SelectedItem is not Fx.PowerList.Power ip || PowerBorrow.HasOwnIcon(db, ip.Prototype);
            settingIcon = true;
            useIcon.Enabled = hasIcon;
            useIcon.Checked = hasIcon && iconWanted;
            settingIcon = false;
            Ui.Tip(useIcon, hasIcon ? IconTip : "The chosen power has no icon of its own in the game (only the \"No Icon\" placeholder), so this power keeps its own icon.");
            Suggest();
        };
        string? startHero = current != null ? current.DonorHero ?? PowerBorrow.HeroOf(current.Donor) : null;
        if (current != null) { partAnims.Checked = !current.NoAnimations; partEffects.Checked = !current.NoEffects; partProps.Checked = !current.NoProps; }
        if (current != null) { whole.Checked = !current.KeepTiming; look.Checked = current.KeepTiming; fit.Checked = current.Fit; vForward.Value = current.RiderForward; vUp.Value = current.RiderUp; vLean.Value = current.RiderLean; }
        savedSize = current?.VehicleScale;
        keepFit = current;
        PartsChanged();
        targetHero = current?.Hero;
        Shown += (_, _) =>
        {
            // the tab that holds the saved donor (a hero's name, or a team-up's / NPC's package file)
            int kind = 0, at = -1;
            for (int k = 0; k < kindLists.Length && at < 0 && startHero != null; k++)
            {
                at = kindLists[k].FindIndex(o => (o is DonorChar d ? d.File : (string)o).Equals(startHero, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) kind = k;
            }
            ShowKind(kind, select: false);
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
        Ui.FitToScreen(this, 640, 560);
        // taller when its notes need it (Kurt, 2026-11-10: an enemy's power with its notes pushed Borrow off the bottom)
        bool fitting = false;
        layoutTable.Layout += (_, _) => { if (!fitting && IsHandleCreated) BeginInvoke(() => { fitting = true; try { FitContent(); } finally { fitting = false; } }); };
        Shown += (_, _) => FitContent();
    }

    readonly TableLayoutPanel layoutTable;

    // Heroes / Team-Ups / NPCs tabs over the Character list (Kurt, 2026-11-10), as the Model tab's
    static readonly string[] KindTitles = ["Heroes", "Team-Ups", "NPCs"];
    static readonly string[] KindTips =
    [
        "Heroes: borrow a power from any hero.",
        "Team-ups: borrow a team-up's power.",
        "NPCs, enemies and bosses: borrow one of their powers (the ones with an animation).",
    ];
    readonly List<object>[] kindLists = [[], [], []];
    readonly List<Label> kindTabs = [];
    int kindShown;

    Control KindStrip()
    {
        var strip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 2) };
        for (int k = 0; k < KindTitles.Length; k++)
        {
            int index = k;
            var tab = new Label { Text = KindTitles[k], AutoSize = true, Padding = new Padding(6, 3, 6, 5), Cursor = Cursors.Hand, Font = Ui.Regular(9f), ForeColor = k == 0 ? Ui.Text : Ui.Subtle, BackColor = Color.Transparent, Margin = new Padding(0, 0, 4, 0) };
            tab.Click += (_, _) => { if (index != kindShown) ShowKind(index, select: true); };
            tab.Paint += (_, e) =>
            {
                if (kindShown != index) return;
                using var b = new SolidBrush(Ui.Accent);
                float s = Ui.Dpi(DeviceDpi);
                e.Graphics.FillRectangle(b, 5 * s, tab.Height - 3 * s, tab.Width - 10 * s, 2.5f * s);
            };
            Ui.Tip(tab, KindTips[k]);
            kindTabs.Add(tab);
            strip.Controls.Add(tab);
        }
        return strip;
    }

    /// <summary>Fills the Character list with one tab's characters (the first picked when <paramref name="select"/>).</summary>
    void ShowKind(int kind, bool select)
    {
        kindShown = kind;
        for (int k = 0; k < kindTabs.Count; k++)
        {
            kindTabs[k].Text = $"{KindTitles[k]} ({kindLists[k].Count:N0})";
            kindTabs[k].ForeColor = k == kind ? Ui.Text : Ui.Subtle;
            kindTabs[k].Invalidate();
        }
        heroBox.Items.Clear();
        heroBox.Items.AddRange([.. kindLists[kind]]);
        if (select && heroBox.Items.Count > 0) heroBox.SelectedIndex = 0;
    }

    /// <summary>The window grown to show all of its content (only grown, never past the screen's working area).</summary>
    void FitContent()
    {
        if (IsDisposed) return;
        var pref = layoutTable.GetPreferredSize(new Size(ClientSize.Width - Padding.Horizontal, 0));
        int need = pref.Height + Padding.Vertical + 8;
        int max = Screen.FromControl(this).WorkingArea.Height - (Height - ClientSize.Height) - 20;
        if (need > ClientSize.Height) ClientSize = new Size(ClientSize.Width, Math.Min(need, max));
    }

    string? wantPower, targetHero;
    float? savedSize;
    readonly PowerBorrowEntry? keepFit;

    /// <summary>The power's kind when it differs from this power's (null when it matches or isn't known).</summary>
    PowerBorrow.PowerKind? Different(Fx.PowerList.Power p) =>
        targetKind != null && kindOf.GetValueOrDefault(p.Prototype) is { } k && !k.Matches(targetKind) ? k : null;

    void LoadPowers()
    {
        var donor = heroBox.SelectedItem as DonorChar;
        string? hero = heroBox.SelectedItem as string;
        if (donor == null && hero == null) return;
        int req = ++request;
        powerBox.Items.Clear();
        suggestion.Text = "Reading the powers…";
        string c0 = cooked, tgt = target;
        Task.Run(() =>
        {
            var g = new Fx.FxGame(c0, []);
            var tk = PowerBorrow.KindOf(db, g, tgt);
            var list = (donor != null
                ? Fx.AgentPowers.List(db, donor.Class, c0, "x").Select(p => new Fx.PowerList.Power(p.Prototype, p.Name.StartsWith("x: ") ? p.Name[3..] : p.Name, p.Icon, p.Animations) { HasName = true })
                : Fx.PowerList.For(db, hero!, c0, [])).Where(p => p.Animations.Count > 0).ToList();
            var ks = list.ToDictionary(p => p.Prototype, p => PowerBorrow.KindOf(db, g, p.Prototype), StringComparer.OrdinalIgnoreCase);
            // matching first, each part in the list's own order
            var sorted = list.OrderBy(p => tk != null && ks[p.Prototype] is { } k && !k.Matches(tk) ? 1 : 0).ToList();
            return (tk, ks, sorted);
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != request) return;
            if (t.Status != TaskStatus.RanToCompletion) { suggestion.Text = "This character's powers couldn't be read."; return; }
            targetKind = t.Result.tk;
            foreach (var (k, v) in t.Result.ks) kindOf[k] = v;
            powerBox.Items.AddRange(t.Result.sorted);
            int at = wantPower == null ? -1 : t.Result.sorted.FindIndex(p => p.Prototype.Equals(wantPower, StringComparison.OrdinalIgnoreCase));
            wantPower = null;
            if (t.Result.sorted.Count > 0) powerBox.SelectedIndex = Math.Max(0, at);
            else suggestion.Text = "This character has no powers with an animation.";
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
            var ts = SummonLook.Of(db, g, target, PowerBorrow.HeroOf(target));
            var ds = ts.Count == 0 ? [] : SummonLook.Of(db, g, p.Prototype, PowerBorrow.HeroOf(p.Prototype));
            string? summons = ts.Count > 0 && ds.Count > 0 ? $"{Pretty(ts[0].Class)} as {Pretty(ds[0].Class)}" : null;
            var areas = PowerBorrow.AreaLooks(db, g, p.Prototype);
            string? areaNames = areas.Count > 0 ? string.Join(", ", areas.Select(a => Pretty(a.Name))) : null;
            var trig = PowerBorrow.TriggeredPairs(db, g, target, p.Prototype);
            string? trigNames = trig.Count > 0 ? string.Join(", ", trig.Select(x => x.TargetName).Distinct()) + " as " + trig[0].DonorName : null;
            return (keep, why, PowerBorrow.KeepTimingTakes(db, g, target, p.Prototype), PowerBorrow.EnemyUsers(db, g, root, target, p.Prototype), vehicle, summons, areaNames, trigNames);
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || t.Status != TaskStatus.RanToCompletion) return;
            var (keep, why, takes, enemies, vehicle, summons, areaNames, trigNames) = t.Result;
            triggeredLook.Visible = trigNames != null;
            triggeredLook.Text = trigNames != null ? $"Its Triggered Powers' Look Too: {trigNames}" : "Its Triggered Powers' Look Too";
            triggeredLook.Checked = triggeredWanted ?? true;
            areaLook.Visible = areaNames != null;
            areaLook.Text = areaNames != null ? $"Its Area's Look Too: {areaNames}" : "Its Area's Look Too";
            areaLook.Checked = areaWanted ?? true;
            summonLook.Visible = summons != null;
            summonLook.Text = summons != null ? $"Its Summon's Look Too: {summons}" : "Its Summon's Look Too";
            summonLook.Checked = summonWanted ?? true;
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
            costumeNote.ForeColor = Color.FromArgb(240, 185, 70);
            costumeNote.Visible = costumeNote.Text.Length > 0 && whole.Checked;
            enemyNote.Visible = enemies.Count > 0;
            enemyNote.Text = enemies.Count == 0 ? "" : "Enemies use this power's look too: " + string.Join(", ", enemies.Select(e => Path.GetFileNameWithoutExtension(e))) +
                ". Borrowing changes how they look as well (the game shares the power's package with them).";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A summon's class as words: MarvelAgent_SquirrelGirlTripleSquirrel → "Squirrel Girl Triple Squirrel".</summary>
    static string Pretty(string cls)
    {
        string n = cls.StartsWith("MarvelAgent_", StringComparison.OrdinalIgnoreCase) ? cls["MarvelAgent_".Length..] : cls;
        return System.Text.RegularExpressions.Regex.Replace(n.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();
    }

    void Choose()
    {
        if (powerBox.SelectedItem is not Fx.PowerList.Power p || (heroBox.SelectedItem is DonorChar dc ? dc.File : heroBox.SelectedItem as string) is not string hero) return;
        Result = new PowerBorrowEntry
        {
            Donor = p.Prototype, DonorName = $"{(heroBox.SelectedItem is DonorChar dn ? dn.Display : AutoTags.DisplayName(hero) ?? hero)}: {p.Name}",
            KeepTiming = look.Checked, Fit = fit.Checked, DonorHero = hero,
            NoAnimations = whole.Checked && !partAnims.Checked, NoEffects = whole.Checked && !partEffects.Checked, NoProps = whole.Checked && !partProps.Checked, SummonLook = summonLook.Visible && summonLook.Checked, AreaLook = areaLook.Visible && areaLook.Checked, TriggeredLook = triggeredLook.Visible && triggeredLook.Checked,
        };
        // the same power again: its Vehicle Fit (set in the Powers tab) stays
        if (keepFit is { } kf && kf.Donor.Equals(p.Prototype, StringComparison.OrdinalIgnoreCase))
        {
            Result.VehicleScale = kf.VehicleScale; Result.RiderForward = kf.RiderForward; Result.RiderUp = kf.RiderUp; Result.RiderLean = kf.RiderLean;
        }
        DialogResult = DialogResult.OK;
    }
}
