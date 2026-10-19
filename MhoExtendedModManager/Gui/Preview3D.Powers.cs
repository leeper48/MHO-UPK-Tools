using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's power buttons, power effects and props: the hero's powers as icon buttons under the view (a click
/// shows only that power's animations and plays the loop of a start / loop / end power; again = all; an effect-only power
/// plays over the idle), the effects of the power an animation belongs to (Power FXs), and the hero's props switching with
/// the powers. Everything is read from the game's (and the mod's) packages; nothing is written.</summary>
sealed partial class Preview3D
{
    readonly PropRig rig = new();
    PreviewEffects? effects;
    /// <summary>The power effects (PreviewEffects: actors, idle effects, swaps), the caption in fxLabel.</summary>
    PreviewEffects Effects => effects ??= new PreviewEffects(rig, this)
    {
        Shown = () => animator != null && shownLoaded != null ? (view, animator, shownLoaded) : null,
        Playing = () => anim,
        Seconds = () => seconds,
        Frames = () => frames,
        Time = () => playTime,
        ShowPose = ShowPose,
        RebuildRig = RebuildRig,
        NoteChanged = () => { fxLabel.Text = effects?.Note ?? ""; CaptionChanged?.Invoke(); },
        Running = () => Visible,
        CookedFor = () => ctx?.Cooked,
        ColorFor = colorFor,
    };
    Fx.PowerEffects.Player? fxPlayer => effects?.Player;
    int heroPowersRequest;
    List<Fx.PowerList.Power> heroPowers = [];
    string? powerFilter;
    /// <summary>An effect-only power picked (no animation of its own): its effects play over the idle.</summary>
    string? effectOnly;
    readonly WrapPanel powerStrip = new() { BackColor = Color.Transparent, Margin = new Padding(0), Dock = DockStyle.Fill };
    readonly List<Button> powerButtons = [];

    /// <summary>The hero's powers shown as buttons, and with them those that have effects but no animation (procs, passives:
    /// the editor's colors); loaded (HeroPowersLoaded) after the animations.</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => heroPowers;
    public IReadOnlyList<Fx.PowerList.Power> AllHeroPowers => allHeroPowers;
    List<Fx.PowerList.Power> allHeroPowers = [];
    public bool PowersLoaded { get; private set; }
    public event Action? HeroPowersLoaded;

    /// <summary>The editor's Powers tab: a power's colour (by prototype path or "own:&lt;file&gt;"; null = the game's).</summary>
    public Func<string, PowerColor?>? ColorFor { get => colorFor; set { colorFor = value; if (effects != null) effects.ColorFor = value; } }
    Func<string, PowerColor?>? colorFor;
    /// <summary>The editor's Powers tab: effects play even with Power FXs off (they're what's being coloured).</summary>
    public bool ForceEffects { get; set; }
    /// <summary>Effects read by the host instead (the Powers tab: a character's own effects, an NPC's power); true = handled.</summary>
    public Func<bool>? CustomEffects { get; set; }
    /// <summary>The shared effects (the host's own readers load through it).</summary>
    public PreviewEffects EffectsPlayer => Effects;
    /// <summary>The caption changed (the Mods tab paints it in its own caption too).</summary>
    public event Action? CaptionChanged;
    /// <summary>The caption: the power and its effects ("Shockwave · 5 Effects").</summary>
    public string EffectsCaption => effects?.Note ?? "";
    /// <summary>The colour of the power playing changed (the Powers tab): shown at once.</summary>
    public void RefreshPowerColor() => effects?.RefreshColor();

    /// <summary>Shows a power of the hero (as its button): its animations only, the main one picked at the share of its length
    /// the last one was at; paused unless <paramref name="play"/>.</summary>
    public bool PlayPower(string prototype, bool play = false)
    {
        int k = heroPowers.FindIndex(p => p.Prototype.Equals(prototype, StringComparison.OrdinalIgnoreCase));
        if (k < 0) return false;
        // The frame slider keeps its place (Kurt, 2026-10-03: compare powers at the same moment)
        if (anim != null && seconds > 0) keepFraction = Fraction;
        if (powerFilter == heroPowers[k].Prototype) PowerClicked(k);   // (filtering already: back to all, then again)
        PowerClicked(k);
        if (!play) { autoPlay = false; Pause(); }
        return true;
    }

    /// <summary>Buttons in rows, wrapped to the width it's given; its preferred height fits them (a FlowLayoutPanel set to
    /// AutoSize asked its table for an unbounded width and the whole preview column collapsed).</summary>
    sealed class WrapPanel : Panel
    {
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Math.Max(1, Width);
            return new Size(w, Arrange(w, false));
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Arrange(ClientSize.Width, true); }
        int Arrange(int width, bool place)
        {
            int x = 0, y = 0, rowH = 0;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                int cw = c.Width + c.Margin.Horizontal, ch = c.Height + c.Margin.Vertical;
                if (x > 0 && x + cw > width) { x = 0; y += rowH; rowH = 0; }
                if (place) c.Location = new Point(x + c.Margin.Left, y + c.Margin.Top);
                x += cw; rowH = Math.Max(rowH, ch);
            }
            return y + rowH;
        }
    }

    // --- power buttons -------------------------------------------------------------------------------------------------------
    Control PowerBlock()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        powersButton.Margin = new Padding(0, 0, 8, 0); powersButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        t.Controls.Add(powersButton, 0, 0);
        fxLabel.Anchor = AnchorStyles.Left; fxLabel.Margin = new Padding(0, 4, 0, 0);
        t.Controls.Add(fxLabel, 1, 0);
        t.Controls.Add(powerStrip, 0, 1); t.SetColumnSpan(powerStrip, 2);
        return t;
    }

    void ClearPowers()
    {
        heroPowersRequest++;
        heroPowers = []; allHeroPowers = []; powerFilter = null;
        foreach (var b in powerButtons) b.Dispose();   // (disposed while still in the strip, then cleared)
        powerButtons.Clear();
        powerStrip.Controls.Clear();
        FitPowerRow();
    }

    /// <summary>The hero's powers for the power buttons (in the background; their icons decoded there too).</summary>
    void LoadHeroPowers()
    {
        ClearPowers();
        int req = heroPowersRequest;
        PowersLoaded = false;
        if (ctx is not { Cooked: string cooked, Hero: string hero } c) { PowersLoaded = true; return; }
        var names = allAnims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modFiles = c.ModPackages.Select(x => x.Path).ToList();
        PowerPreview.GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            using var busy = Busy.Begin("3D view: listing the powers");
            if (dbt.Result is not { } db) return (Play: new List<Fx.PowerList.Power>(), All: new List<Fx.PowerList.Power>());
            return PowerPreview.Powers(db, hero, cooked, modFiles, names);
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != heroPowersRequest || t.Status != TaskStatus.RanToCompletion) return;
            heroPowers = t.Result.Play;
            allHeroPowers = t.Result.All;
            PowersLoaded = true;
            FillPowerButtons(cooked);
            HeroPowersLoaded?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void FillPowerButtons(string cooked)
    {
        powerStrip.SuspendLayout();
        int side = (int)(38 * S);   // 20 % bigger (Kurt, 2026-10-02)
        for (int k = 0; k < heroPowers.Count; k++)
        {
            var pw = heroPowers[k]; int idx = k;
            var b = Ui.FlatButton("", () => PowerClicked(idx), pw.Name + " (" + pw.Animations.Count + " animation" + (pw.Animations.Count == 1 ? "" : "s") + "): click to show only its animations and play it; click again for all.");
            b.AutoSize = false; b.Padding = new Padding(0); b.Size = new Size(side, side); b.Margin = new Padding(0, 0, 3, 3);
            var img = pw.Icon != null ? Fx.PowerList.Icon(pw.Icon, cooked) : null;
            string initials = string.Concat(pw.Name.Split(' ').Where(w => w.Length > 0).Take(2).Select(w => w[0]));
            Ui.IconPainters.AddOrUpdate(b, (g, r, c) =>
            {
                if (img != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, Rectangle.Inflate(r, -(int)(3 * S), -(int)(3 * S))); }
                else TextRenderer.DrawText(g, initials, Ui.Heavy(10f), r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            });
            powerButtons.Add(b);
            powerStrip.Controls.Add(b);
        }
        Ui.RestyleButtons(powerStrip);
        powerStrip.ResumeLayout();
        FitPowerRow();
        RefreshPowerButtons();
    }

    /// <summary>Lit: the power filtering the list, or the one whose animation is playing.</summary>
    void RefreshPowerButtons()
    {
        string? current = anims.SelectedIndex > 0 && anims.SelectedIndex - 1 < animList.Count ? animList[anims.SelectedIndex - 1].Name : null;
        for (int k = 0; k < powerButtons.Count && k < heroPowers.Count; k++)
            Ui.Lit(powerButtons[k], powerFilter == heroPowers[k].Prototype || (current != null && heroPowers[k].Animations.Contains(current, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>A power button: its animations only (its loop / first plays), or all again when it was filtering.</summary>
    void PowerClicked(int k)
    {
        if (k < 0 || k >= heroPowers.Count) return;
        var pw = heroPowers[k];
        // its form first (Jean Grey's Phoenix: her flying animations, wings and flames), read once; then the click goes on
        if (SetForm(FormOf(pw), () => PowerClicked(k))) return;
        bool reload = formChanged; formChanged = false;
        if (pw.Animations.Count == 0)
        {
            // effects only: the idle plays with them (again: back to all)
            bool again = effectOnly == pw.Prototype;
            effectOnly = again ? null : pw.Prototype;
            powerFilter = again ? null : pw.Prototype;
            animList = allAnims; FillAnims();
            int idle = PowerPreview.IdleIndex(allAnims.Select(a => a.Name).ToList());
            if (!again) { autoPlay = true; if (anims.SelectedIndex == idle + 1 && !reload) LoadEffects(); else Pick(idle + 1, reload); }
            else if (reload) Pick(anims.SelectedIndex, true);
            else LoadEffects();
            RefreshPowerButtons();
            FrameShot(Framing.Shot.Full);
            return;
        }
        effectOnly = null;
        string? current = anims.SelectedIndex > 0 && anims.SelectedIndex - 1 < animList.Count ? animList[anims.SelectedIndex - 1].Name : null;
        if (powerFilter == pw.Prototype)
        {
            powerFilter = null;
            animList = allAnims;
            FillAnims();
            int at = current == null ? -1 : animList.FindIndex(a => a.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
            Pick(at + 1, reload);   // the playing one stays (no change: nothing reloads, unless the form changed)
        }
        else
        {
            powerFilter = pw.Prototype;
            animList = [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))];
            FillAnims();
            if (animList.Count > 0) { autoPlay = true; Pick(PowerPreview.PreferredAnim(animList.Select(a => a.Name).ToList()) + 1, reload); }
        }
        RefreshPowerButtons();
        // a power picked: the whole character, centred (Kurt, 2026-10-18: some powers left the camera far off)
        FrameShot(Framing.Shot.Full);
    }

    /// <summary>The power picked now (its button: filtering the list, or an effect-only power playing over the idle).</summary>
    public bool ShowsPower(string prototype) => string.Equals(powerFilter, prototype, StringComparison.OrdinalIgnoreCase) || string.Equals(effectOnly, prototype, StringComparison.OrdinalIgnoreCase);

    /// <summary>Clicks a power button by name (checks).</summary>
    public bool ClickPower(string name)
    {
        int k = heroPowers.FindIndex(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (k < 0) return false;
        PowerClicked(k);
        return true;
    }

    // --- power effects ---------------------------------------------------------------------------------------------------------
    void ClearEffects() => Effects.Clear();

    /// <summary>
    /// The effects of the power the animation belongs to (Power FXs on): the hero's power packages name it (PowerIndex), the
    /// game data gives the power with what it sets off (PowerEffects.For), then they play from the animation's time.
    /// </summary>
    /// <summary>The power's effects read and played again (parts switched on or off).</summary>
    public void ReloadEffects() => LoadEffects();

    void LoadEffects()
    {
        if (CustomEffects?.Invoke() == true) return;
        ClearEffects();
        // an effect-only power clicked plays its effects whatever Power FXs says (they're all it shows)
        if (!(prefs.Powers || ForceEffects) && effectOnly == null || anim == null || animator == null || ctx is not { Cooked: string cooked, Hero: string hero, MainRef: MeshRef main } c) { ShowPose(); return; }
        int ai = anims.SelectedIndex - 1;
        if (ai < 0 || ai >= animList.Count) return;
        string name = animList[ai].Name;
        string? only = effectOnly;
        string? entity = c.Entity();
        var modFiles = c.ModPackages.Select(x => x.Path).ToList();
        string? formNow = form;
        // a form's version of a power picked (Pyrokinetic Barrier (Unleash the Phoenix)): its own effects, not those of the plain
        // power its animation's name also belongs to
        var formPower = form != null && powerFilter != null ? heroPowers.FirstOrDefault(p => p.Prototype == powerFilter && p.Form != null) : null;
        Effects.Load(cooked, true, db => db == null ? null
            : FormEffects(db, only != null ? PowerPreview.ForPower(db, only, hero, cooked, modFiles, entity, main.File, main.Name)
                : formPower != null ? PowerPreview.ForPowerAnimation(db, formPower.Prototype, name, formPower.Animations, hero, cooked, modFiles, entity, main.File, main.Name)
                : PowerPreview.ForAnimation(db, hero, cooked, modFiles, name, entity, main.File, main.Name), hero, cooked, modFiles, entity, main, formNow),
            c.SwapsFor?.Invoke(cooked));
    }

    /// <summary>The effects moved on to <paramref name="t"/> (started over when time went back: a loop).</summary>
    void FxAdvance(double t, bool ended) => effects?.Advance(t, ended);

    /// <summary>The effects as they are at <paramref name="t"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double t) => effects?.Replay(t);

    // --- props -----------------------------------------------------------------------------------------------------------------
    PreviewProps? props;
    PreviewProps Props => props ??= new PreviewProps(rig, this)
    {
        Shown = () => animator != null && shownLoaded != null ? (view, animator, shownLoaded) : null,
        Playing = () => anim,
        Seconds = () => seconds,
        ShowPose = ShowPose,
        Display = Display,
        AddMore = r => effects?.AddActors(r),
    };

    /// <summary>The character's props (PropRig.Attached: what its attachment classes name), loaded in the background and held
    /// on their bones; none with Props off. The camera stays.</summary>
    void LoadProps()
    {
        if (animator == null || shownLoaded == null || ctx?.MainRef is not MeshRef main) return;
        Props.Load(main, ctx.PackageMeshes, ctx.Cooked, ctx.PropPackages ?? ctx.ModPackages, prefs.Props, LoadPropSwitches);
    }

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept (framed by the character alone).</summary>
    public void RebuildRig() => Props.Rebuild();

    /// <summary>Props left out by attachment class (the Model tab's target props switched off); shown again at once.</summary>
    public Func<string, bool>? HiddenProps { get => Props.Hidden; set { Props.Hidden = value; Props.Rebuild(); } }

    /// <summary>The props for the animation picked: the power playing it switches weapon slots, and a prop the hero doesn't
    /// have (a thrown car) comes from the power's own package.</summary>
    void LoadPropSwitches()
    {
        int ai = anims.SelectedIndex - 1;
        string? name = anim != null && ai >= 0 && ai < animList.Count ? animList[ai].Name : null;
        Props.LoadSwitches(prefs.Props, ctx?.Cooked, ctx?.Hero, name, ctx?.ModPackages.Select(x => x.Path).ToList() ?? [], form != null ? formRules : null, form != null ? formFiles : null);
    }
}
