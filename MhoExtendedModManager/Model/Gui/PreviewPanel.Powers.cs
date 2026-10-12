using System.Drawing.Drawing2D;
using System.Numerics;
using AnimExportCli.Animation;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;
using Fx = MhoExtendedModManager.Fx;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The preview's power buttons, power effects and props (0.13.0), ported from the Mod Manager's preview
/// (StorePreview.Powers.cs) with the vendored Fx code: the base hero's powers as icon buttons under the view (a click shows
/// only that power's animations and plays the loop of a start / loop / end power; again = all), the effects of the power
/// an animation belongs to played with it (Power FXs), and the base hero's props (its attachment classes, held on their
/// bones) switching with the powers. Everything is read from the game's packages; nothing is written. Team-up base
/// packages have no power list (as in the Mod Manager).
/// </summary>
sealed partial class PreviewPanel
{
    readonly PropRig rig = new();
    PreviewEffects? effects;
    /// <summary>The power effects (shared with the Mods tab's preview: actors, idle effects, swaps), the caption in fxLabel.</summary>
    PreviewEffects Effects => effects ??= new PreviewEffects(rig, this)
    {
        Shown = () => animator != null && shownLoaded != null ? (view, animator, shownLoaded) : null,
        Playing = () => anim,
        Seconds = () => seconds,
        Frames = () => frames,
        Time = () => playTime,
        ShowPose = ShowPose,
        RebuildRig = RebuildRig,
        NoteChanged = () => fxLabel.Text = effects?.Note ?? "",
        Running = () => Visible,
        CookedFor = () => Cooked,
    };
    Fx.PowerEffects.Player? fxPlayer => effects?.Player;
    int heroPowersRequest;
    List<Fx.PowerList.Power> heroPowers = [];
    string? powerFilter;
    readonly WrapPanel powerStrip = new() { BackColor = Color.Transparent, Margin = new Padding(0), Dock = DockStyle.Fill };

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
    readonly List<Button> powerButtons = [];

    static string? Cooked => Settings.Current.CookedFolder;

    /// <summary>The game data, shared with the Mods tab's preview (PowerPreview).</summary>
    static Task<Fx.GameData?> GameDb(string cooked) => PowerPreview.GameDb(cooked);

    /// <summary>The hero of the base package (UC__MarvelPlayer_&lt;Hero&gt;_…), else null (team-ups).</summary>
    string? Hero()
    {
        if (shown == null) return null;
        var parts = Path.GetFileNameWithoutExtension(shown.PackagePath).Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[2] : null;
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
        heroPowers = []; powerFilter = null;
        foreach (var b in powerButtons) b.Dispose();
        powerButtons.Clear();
        powerStrip.Controls.Clear();
    }

    /// <summary>The hero's powers for the power buttons (in the background; their icons decoded there too).</summary>
    void LoadHeroPowers()
    {
        ClearPowers();
        int req = heroPowersRequest;
        if (Cooked is not string cooked || Hero() is not string hero) return;
        var names = allAnims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            if (dbt.Result is not { } db) return new List<Fx.PowerList.Power>();
            return PowerPreview.Powers(db, hero, cooked, [], names).Play;   // (shared: the buttons the Mods tab shows)
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != heroPowersRequest || t.Status != TaskStatus.RanToCompletion) return;
            heroPowers = t.Result;
            FillPowerButtons(cooked);
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
        RefreshPowerButtons();
    }

    /// <summary>Lit: the power filtering the list, or the one whose animation is playing.</summary>
    void RefreshPowerButtons()
    {
        string? current = anims.SelectedIndex > 0 && anims.SelectedIndex - 1 < animList.Count ? animList[anims.SelectedIndex - 1].Name : null;
        for (int k = 0; k < powerButtons.Count && k < heroPowers.Count; k++)
            Ui.Lit(powerButtons[k], powerFilter == heroPowers[k].Prototype || (current != null && heroPowers[k].Animations.Contains(current, StringComparer.OrdinalIgnoreCase)));
    }

    static int PreferredAnim(IReadOnlyList<string> names) => PowerPreview.PreferredAnim(names);

    /// <summary>A power button: its animations only (its loop / first plays), or all again when it was filtering.</summary>
    void PowerClicked(int k)
    {
        if (k < 0 || k >= heroPowers.Count) return;
        var pw = heroPowers[k];
        if (pw.Animations.Count == 0)
        {
            // effects only: the idle plays with them (again: back to all)
            bool again = effectOnly == pw.Prototype;
            effectOnly = again ? null : pw.Prototype;
            powerFilter = again ? null : pw.Prototype;
            animList = allAnims; FillAnims();
            int idle = PowerPreview.IdleIndex(allAnims.Select(a => a.Name).ToList());
            if (!again) { autoPlay = true; if (anims.SelectedIndex == idle + 1) LoadEffects(); else anims.SelectedIndex = idle + 1; }
            else LoadEffects();
            RefreshPowerButtons();
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
            anims.SelectedIndex = at + 1;   // the playing one stays (no change: nothing reloads)
        }
        else
        {
            powerFilter = pw.Prototype;
            animList = [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))];
            FillAnims();
            if (animList.Count > 0) { autoPlay = true; anims.SelectedIndex = PreferredAnim(animList.Select(a => a.Name).ToList()) + 1; }
        }
        RefreshPowerButtons();
    }

    /// <summary>The hero's powers shown as buttons (checks).</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => heroPowers;
    /// <summary>The power line under the view (checks).</summary>
    public string EffectsNote => fxLabel.Text + $" · props {rig.Count}";
    /// <summary>The material swap drawn now (checks): "n of m section(s)", else "none".</summary>
    public string SwapNote => view.LookOverride is { } lo ? $"{lo.Count(x => x != null)} of {lo.Length} section(s)" : "none";
    /// <summary>The view's picture (checks).</summary>
    public Bitmap ViewShot() { var b = new Bitmap(Math.Max(1, view.Width), Math.Max(1, view.Height)); view.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); return b; }
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

    /// <summary>An effect-only power picked (no animation of its own): its effects play over the idle.</summary>
    string? effectOnly;
    /// <summary>The Model tab: the target costume's class (marvelplayer_drdoom_classic: a costume shown with its hero's model
    /// still has its own effects), and the build's slot order for a list of material names (MaterialOverrides.Ordered).</summary>
    public Func<string?>? TargetClass { get; set; }
    public Func<IReadOnlyList<string>, List<string>>? SlotOrder { get; set; }

    /// <summary>
    /// A power's condition material swaps on the shown model (Kurt, 2026-10-12: Doctor Doom's force field, Hulk's revive): each
    /// section's slot is its material's place in the build's slot order (the target's own model: its section's material index),
    /// and a swap for the target costume draws its material on the slots it doesn't keep.
    /// </summary>
    Func<Fx.GameData, string, ModelView.Look?[]?>? SwapsFor(string cooked)
    {
        if (shown is not { } sh) return null;
        string cls = TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(sh.PackagePath);
        List<int>? slots = null;
        if (!sh.TargetOnly && sh.SectionNames.Count > 0)
        {
            var distinct = sh.SectionNames.Distinct().ToList();
            var order = SlotOrder?.Invoke(distinct) ?? distinct;
            slots = sh.SectionNames.Select(n => order.IndexOf(n)).ToList();
        }
        var main = sh.MainRef;
        return (db, power) => PowerPreview.Swaps(db, power, cooked, new Dictionary<string, string>(), cls, slots, main);
    }

    /// <summary>
    /// The effects of the power the animation belongs to (Power FXs on): the hero's power packages name it (PowerIndex), the
    /// game data gives the power with what it sets off (PowerEffects.For), then they play from the animation's time
    /// (PreviewEffects, shared with the Mods tab).
    /// </summary>
    void LoadEffects()
    {
        ClearEffects();
        // an effect-only power clicked plays its effects whatever Power FXs says (they're all it shows; as the Mods tab)
        if (!P.Powers && effectOnly == null || anim == null || animator == null || Cooked is not string cooked || Hero() is not string hero || shown?.MainRef is not MeshRef main) { ShowPose(); return; }
        int ai = anims.SelectedIndex - 1;
        if (ai < 0 || ai >= animList.Count) return;
        string name = animList[ai].Name;
        string? only = effectOnly;
        string? entity = TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(shown.PackagePath);
        Effects.Load(cooked, true, db => db == null ? null
            : only != null ? PowerPreview.ForPower(db, only, hero, cooked, [], entity, main.File, main.Name)
            : PowerPreview.ForAnimation(db, hero, cooked, [], name, entity, main.File, main.Name), SwapsFor(cooked));
    }

    /// <summary>The effects moved on to <paramref name="t"/> (started over when time went back: a loop).</summary>
    void FxAdvance(double t, bool ended) => effects?.Advance(t, ended);

    /// <summary>The effects as they are at <paramref name="t"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double t) => effects?.Replay(t);

    // --- props (shared with the Mods tab's preview: PreviewProps) ----------------------------------------------------------------
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

    /// <summary>The base hero's props (PropRig.Attached: what its attachment classes name, from its base package), loaded in
    /// the background and held on their bones; none with Props off. The camera stays.</summary>
    void LoadProps()
    {
        if (animator == null || shownLoaded == null || shown?.MainRef is not MeshRef main) return;
        Props.Load(main, shown.PackageMeshes, Cooked, [], P.Props, LoadPropSwitches);
    }

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept (framed by the character alone).</summary>
    void RebuildRig() => Props.Rebuild();

    /// <summary>The props for the animation picked: the power playing it switches weapon slots, and a prop the hero doesn't
    /// have (a thrown car) comes from the power's own package (PreviewProps.LoadSwitches).</summary>
    void LoadPropSwitches()
    {
        int ai = anims.SelectedIndex - 1;
        string? name = anim != null && ai >= 0 && ai < animList.Count ? animList[ai].Name : null;
        Props.LoadSwitches(P.Props, Cooked, Hero(), name, []);
    }
}
