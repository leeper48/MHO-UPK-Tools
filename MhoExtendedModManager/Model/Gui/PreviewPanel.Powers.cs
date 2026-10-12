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
    readonly PowerPreview.Clock fxClock = new();
    Fx.PowerEffects.Player? fxPlayer { get => fxClock.Player; set => fxClock.Player = value; }
    Dictionary<string, (string Bone, Matrix4x4 Local)> fxSockets = new();
    int fxRequest, heroPowersRequest, propSwitchRequest;
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
    void ClearEffects() { fxRequest++; fxClock.Clear(); fxLabel.Text = ""; view.Effects = []; view.EffectTris = []; view.LookOverride = null; }

    /// <summary>An effect-only power picked (no animation of its own): its effects play over the idle.</summary>
    string? effectOnly;
    /// <summary>The Model tab: the target costume's class (marvelplayer_drdoom_classic: a costume shown with its hero's model
    /// still has its own effects), and the build's slot order for a list of material names (MaterialOverrides.Ordered).</summary>
    public Func<string?>? TargetClass { get; set; }
    public Func<IReadOnlyList<string>, List<string>>? SlotOrder { get; set; }

    /// <summary>
    /// A power's condition material swaps on the shown model (Kurt, 2026-10-12: Doctor Doom's force field, Hulk's revive): each
    /// section's slot is its material's place in the build's slot order (the target's own model: its section's material index),
    /// and a swap for the target costume draws its material on the slots it doesn't keep. Read in the background.
    /// </summary>
    void LoadSwaps(string power, int req)
    {
        if (Cooked is not string cooked || shown is not { } sh) return;
        string cls = TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(sh.PackagePath);
        List<int>? slots = null;
        if (!sh.TargetOnly && sh.SectionNames.Count > 0)
        {
            var distinct = sh.SectionNames.Distinct().ToList();
            var order = SlotOrder?.Invoke(distinct) ?? distinct;
            slots = sh.SectionNames.Select(n => order.IndexOf(n)).ToList();
        }
        var main = sh.MainRef;
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
            dbt.Result is { } db ? PowerPreview.Swaps(db, power, cooked, new Dictionary<string, string>(), cls, slots, main) : null)).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != fxRequest || t.Status != TaskStatus.RanToCompletion || t.Result is not { } looks) return;
            view.LookOverride = looks;
            fxLabel.Text += fxLabel.Text.Length > 0 ? " · Material Swap" : "Material Swap";
            ShowPose();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The effects of the power the animation belongs to (Power FXs on): the hero's power packages name it (PowerIndex), the
    /// game data gives the power with what it sets off (PowerEffects.For), then they play from the animation's time.
    /// </summary>
    void LoadEffects()
    {
        ClearEffects();
        // an effect-only power clicked plays its effects whatever Power FXs says (they're all it shows; as the Mods tab)
        if (!P.Powers && effectOnly == null || anim == null || animator == null || Cooked is not string cooked || Hero() is not string hero || shown?.MainRef is not MeshRef main) { ShowPose(); return; }
        int ai = anims.SelectedIndex - 1;
        if (ai < 0 || ai >= animList.Count) return;
        string name = animList[ai].Name;
        int req = fxRequest;
        var a = animator; var l = shownLoaded;
        string? only = effectOnly;
        string? entity = TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(shown.PackagePath);
        fxLabel.Text = "Reading Powers…";
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
            dbt.Result is not { } db ? null
            : only != null ? PowerPreview.ForPower(db, only, hero, cooked, [], entity, main.File, main.Name)
            : PowerPreview.ForAnimation(db, hero, cooked, [], name, entity, main.File, main.Name))).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != fxRequest || animator != a || anim == null) return;
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } fx) { fxLabel.Text = ""; return; }
            fxSockets = fx.Sockets;
            // The target of effects at the world position: the ground 250 units in front (characters face +X).
            float ground = l == null || l.Positions.Length == 0 ? 0 : l.Positions.Min(v => v.Z);
            fxPlayer = new Fx.PowerEffects.Player(fx.Effects, Socket, new Vector3(250, 0, ground), fx.Phase) { AnimSeconds = Math.Max(0.1f, seconds) };
            view.EffectStrength = PreviewViews.FxPower;   // as the Mods tab
            fxLabel.Text = fx.Label;
            FxReplay(playTime);
            animator.Pose(anim, seconds > 0 ? playTime / seconds * frames : 0);
            ShowPose();
            LoadSwaps(fx.Power, req);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A socket's place in the pose last made: the mesh's socket on its bone, else a bone of that name, else null.</summary>
    Matrix4x4? Socket(string name) => PowerPreview.Socket(name, fxSockets, animator);

    /// <summary>The effects moved on to <paramref name="t"/> (started over when time went back: a loop).</summary>
    void FxAdvance(double t, bool ended) => fxClock.Advance(t, ended);

    /// <summary>The effects as they are at <paramref name="t"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double t)
    {
        if (anim == null || animator == null) return;
        var a = anim; var an = animator;
        fxClock.Replay(t, x => an.Pose(a, seconds > 0 ? (float)(x / seconds * frames) : 0));
    }

    // --- props -------------------------------------------------------------------------------------------------------------------
    List<(PropRig.Prop W, ModMeshes.Loaded M, MeshAnimator? A, List<AnimRef>? R)> baseProps = [];
    List<(PropRig.Prop W, ModMeshes.Loaded M)> powerProps = [];
    Dictionary<string, BoneAnimation> propSeqs = new(StringComparer.OrdinalIgnoreCase);
    List<ModMeshes.PropRule> propRules = [];
    float propContact;
    readonly Dictionary<string, Task<Dictionary<string, List<Fx.PowerIndex.PowerRef>>>> powerIndexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The base hero's props (PropRig.Attached: what its attachment classes name, from its base package), loaded in
    /// the background and held on their bones; none with Props off. The camera stays.</summary>
    void LoadProps()
    {
        if (animator == null || shownLoaded == null || shown?.MainRef is not MeshRef main) return;
        var l = shownLoaded; var a = animator;
        int req = ++propLoad;
        bool on = P.Props;
        var all = shown.PackageMeshes.ToList();
        string? cooked = Cooked;
        if (!on && rig.Count == 0 && baseProps.Count == 0) return;
        Task.Run(() => (on ? PropRig.Attached(main, all, cooked) : []).Select(w =>
        {
            try
            {
                var lm = ModMeshes.Load(w.Ref, cooked, out _);
                if (lm != null && w.UseParentAnim)
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: (List<AnimRef>?)null);
                if (lm != null && w.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase))
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: ModAnimations.For(w.Ref, lm.Bones, [], cooked, minBones: 1));
                return (W: w, Mesh: lm, A: (MeshAnimator?)null, R: (List<AnimRef>?)null);
            }
            catch { return (W: w, Mesh: (ModMeshes.Loaded?)null, A: (MeshAnimator?)null, R: (List<AnimRef>?)null); }
        }).ToList())
            .ContinueWith(t =>
            {
                if (IsDisposed || req != propLoad || animator != a || shownLoaded != l || t.Status != TaskStatus.RanToCompletion) return;
                baseProps = [.. t.Result.Where(x => x.Mesh != null).Select(x => (x.W, x.Mesh!, x.A, x.R))];
                RebuildRig();
                LoadPropSwitches();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    int propLoad;

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept (framed by the character alone).</summary>
    void RebuildRig()
    {
        if (animator == null || shownLoaded == null) return;
        rig.Clear();
        rig.SetParentAnimation(anim);
        // each prop with its attachment's offset rotation (Kurt, 2026-10-12: Ghost Rider sat 90° across his bike here, not on
        // the Mods tab: the bikes' OffsetRotation is yaw 16384)
        foreach (var (w, m, an, refs) in baseProps) rig.Add(m, w.UseParentAnim ? -1 : PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class, an, refs, w.UseParentAnim, w.Offset);
        foreach (var (w, m) in powerProps) rig.Add(m, PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class, offset: w.Offset);
        rig.SetRules(propRules, propContact, seconds);
        rig.SetMotions(propSeqs);
        var keep = view.ViewState;
        view.ShowMesh(rig.Combine(Display(shownLoaded)), shownLoaded.Positions.Length);
        view.ViewState = keep;
        ShowPose();
    }

    /// <summary>
    /// The props for the animation picked: the power playing it (PowerIndex) switches weapon slots (its power package's
    /// PowerFxMeshAttachment), read in the background; the rest pose shows what's always held. A prop the power shows that
    /// the hero doesn't have (a thrown car) comes from the power's own package.
    /// </summary>
    void LoadPropSwitches()
    {
        int req = ++propSwitchRequest;
        if (!P.Props || Cooked is not string cooked || Hero() is not string hero)
        {
            if (propRules.Count > 0 || powerProps.Count > 0) { propRules = []; bool had = powerProps.Count > 0; powerProps = []; if (had) RebuildRig(); else { rig.SetRules(propRules, 0, 0); ShowPose(); } }
            return;
        }
        var have = baseProps.Select(x => x.W).ToList();
        int ai = anims.SelectedIndex - 1;
        string? name = anim != null && ai >= 0 && ai < animList.Count ? animList[ai].Name : null;
        var motionRefs = rig.MotionRefs(name);
        var dbTask = GameDb(cooked);
        if (!powerIndexCache.TryGetValue(hero, out var idxTask))
            powerIndexCache[hero] = idxTask = dbTask.ContinueWith(t => t.Result is { } pdb ? Fx.PowerIndex.For(hero, cooked, [], pdb) : Fx.PowerIndex.For(hero, cooked, []));
        float secs = seconds;
        Task.WhenAll(idxTask, dbTask).ContinueWith(done =>
        {
            if (name == null || idxTask.Status != TaskStatus.RanToCompletion || !idxTask.Result.TryGetValue(name, out var refs))
                return (Rules: new List<ModMeshes.PropRule>(), Contact: 0f, Extra: new List<(PropRig.Prop, ModMeshes.Loaded)>(), Seqs: new Dictionary<string, BoneAnimation>(StringComparer.OrdinalIgnoreCase));
            var files = refs.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var rules = files.SelectMany(ModMeshes.PropRules).Distinct().ToList();
            var extra = new List<(PropRig.Prop, ModMeshes.Loaded)>();
            foreach (var r in rules.Where(r => r.Show && !have.Any(h => PropRig.Fills(h, r.Target))))
                foreach (string f in files)
                {
                    var found = ModMeshes.Attachments(f).Where(x => PropRig.Fills(new PropRig.Prop(null!, x.Bone) { Slots = x.Slots, Class = x.Class }, r.Target)).ToList();
                    if (found.Count == 0) continue;
                    var inPkg = ModMeshes.List([(Path.GetFileName(f), f)], anyPackage: true);
                    foreach (var x in found)
                        if (!extra.Any(e => e.Item1.Class.Equals(x.Class, StringComparison.OrdinalIgnoreCase)) && inPkg.FirstOrDefault(m => m.Name.Equals(x.Mesh, StringComparison.OrdinalIgnoreCase)) is { } mr)
                            try { if (ModMeshes.Load(mr, cooked, out _) is { } lm) extra.Add((new PropRig.Prop(mr, x.Bone) { Slots = x.Slots, OnDemand = true, Class = x.Class }, lm)); }
                            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                    break;
                }
            // the contact time: the power's AnimationContactTimePercent (else 0.4) of the animation
            float pct = 0.4f;
            if (dbTask.Status == TaskStatus.RanToCompletion && dbTask.Result is { } db)
            {
                var byClass = Fx.PowerIndex.PrototypesByClass(db);
                if (refs.SelectMany(r => byClass.TryGetValue(r.Class, out var l) ? l : []).FirstOrDefault() is string proto) pct = Fx.PowerEffects.ContactPercentOf(db, proto);
            }
            var seqs = new Dictionary<string, BoneAnimation>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cls, ar) in motionRefs) if (!seqs.ContainsKey(cls) && ModAnimations.Load(ar) is { } ba) seqs[cls] = ba;
            return (Rules: rules, Contact: pct * secs, Extra: extra, Seqs: seqs);
        })
        .ContinueWith(t =>
        {
            if (IsDisposed || req != propSwitchRequest || t.Status != TaskStatus.RanToCompletion) return;
            (propRules, propContact) = (t.Result.Rules, t.Result.Contact);
            propSeqs = t.Result.Seqs;
            bool rebuild = powerProps.Count > 0 || t.Result.Extra.Count > 0;
            powerProps = t.Result.Extra;
            if (rebuild) RebuildRig();
            else { rig.SetRules(propRules, propContact, seconds); rig.SetMotions(propSeqs); ShowPose(); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
