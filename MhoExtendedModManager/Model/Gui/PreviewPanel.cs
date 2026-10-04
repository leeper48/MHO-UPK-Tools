using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// The window's 3D preview: the retargeted MFF model on the base hero's skeleton, in the Mod Manager's 3D view (vendored
/// ModelView), posed by the hero's own animations (MEMM's ModAnimations / MeshAnimator, in the game's AnimSet order).
/// Laid out and behaving as the Mod Manager's preview (0.13.0, Kurt: "all the items learned about the viewer from MEMM"):
/// the playback bar under the view (animation, ▶, Loop, Frame), then Look ▾ (Spec / Reflect / Glow / Bloom / Props /
/// Power FXs / Bones, the Light / Glow / Lens sliders), Compare, Reset View, the Head / Bust / Full Body framing icons and
/// full screen (⛶ / F11), then the hero's power buttons (PreviewPanel.Powers.cs: a click shows that power's animations
/// and plays its effects; the hero's props follow the powers). Smaller frames while playing (ModelView.Moving). The look
/// and playback switches are remembered (Settings.Preview). The model side is PreviewPanel.Model.cs.
/// </summary>
sealed partial class PreviewPanel : UserControl
{
    readonly ModelView view = new() { Dock = DockStyle.Fill };
    readonly DropDown anims = new() { Dock = DockStyle.Fill };
    readonly Button play, loop, reset, compare, look, frameHead, frameBust, frameFull, fullScreen, powersButton, edits;
    readonly LightSlider frame = new() { Dock = DockStyle.Fill, Label = "Frame", Min = 0, Max = 1, Step = 0, Mark = null, Height = 26 };
    readonly LightSlider light = new() { Label = "Light", Height = 26 };
    readonly LightSlider glowSlider = new() { Label = "Glow", Min = 0f, Max = 2f, Height = 26 };
    readonly LightSlider lens = new() { Label = "Lens", Min = 15, Max = 200, Step = 0.5f, Mark = 50, Height = 26, Format = v => $"{v:0} mm" };
    readonly Label fxLabel = new() { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    readonly Stopwatch clock = new();
    ContextMenuStrip? lookMenu;

    MeshAnimator? animator, mffAnimator, stockAnimator;
    Prepared? shown;
    /// <summary>The character mesh in the view (the model, or the base hero's own while comparing).</summary>
    ModMeshes.Loaded? shownLoaded;
    bool comparing, targetOnly;
    /// <summary>The animations in the drop-down (a power button filters them) and all of the hero's.</summary>
    List<AnimRef> animList = new(), allAnims = new();
    BoneAnimation? anim;
    float frames, seconds, playTime;
    bool settingFrame, autoPlay;
    int loadId;

    /// <summary>Edits from Blender (0.16.0): the imported animation for a name (null = the game's), whether a name has one,
    /// and the Single Animation ▾ menu (filled by the main window).</summary>
    public Func<AnimRef, BoneAnimation?, BoneAnimation?>? Override;
    public Func<string, bool>? IsOverridden;
    public Action<ContextMenuStrip>? FillEditsMenu;
    /// <summary>The animation picked in the drop-down (null = rest pose).</summary>
    public string? CurrentAnimation => anims.SelectedIndex > 0 ? anims.SelectedItem as string : null;
    /// <summary>Every animation of the shown model (not only the power filter's).</summary>
    public IReadOnlyList<string> AnimationNames => allAnims.Select(a => a.Name).ToList();
    /// <summary>The animations Full Export writes: the hero's and the costume's own, not the shared ones every hero plays
    /// (blink, interactions: sets in Startup / MarvelGame).</summary>
    public int OwnAnimationCount => allAnims.Count(a => !a.Package.StartsWith("startup", StringComparison.OrdinalIgnoreCase) && !a.Package.StartsWith("marvelgame", StringComparison.OrdinalIgnoreCase));

    static Settings.PreviewPrefs P => Settings.Current.Preview;
    static void SaveP() => Settings.Current.Save();
    float S => DeviceDpi / 96f;

    public PreviewPanel()
    {
        BackColor = Color.Transparent;
        play = Ui.FlatButton("▶", TogglePlay, "Play or pause the animation (P; Esc pauses).");
        loop = Ui.FlatButton("⟳ Loop", () => { P.Loop = !P.Loop; SaveP(); Ui.Lit(loop!, P.Loop); }, "Loop the animation, or play it once and stop on its last frame. Remembered.");
        reset = Ui.FlatButton("Reset View", () => view.ResetView(), "Frame the whole model again. Drag to turn, right-drag to move, wheel to zoom (Shift: finer), double-click to centre.");
        edits = Ui.FlatButton("Single Animation ▾", ShowEditsMenu, "The animation picked here and Blender: export just it (Export FBX: … Only, Open … in Blender: the model and this one animation, quick), bring edits back (an FBX's animation in place of it, and / or its mesh with its weights in place of the model's; kept in the edits folder, built into the mod) and revert them. The buttons on the right export all the animations.");
        compare = Ui.FlatButton("Compare", ToggleCompare, "Show the base hero's own in-game mesh instead, in the same animation, frame and view, to compare (click again for the MFF model).");
        look = Ui.FlatButton("Look ▾", ShowLookMenu, "How the model is shown: Spec, Reflect, Glow, Bloom, Props, Power FXs and Bones on or off, and the Light, Glow and Lens sliders. Remembered.");
        frameHead = Ui.FlatButton("Head", () => FrameShot(Framing.Shot.HeadShoulders), "Frame the head and shoulders.");
        frameBust = Ui.FlatButton("Bust", () => FrameShot(Framing.Shot.Bust), "Frame head and chest.");
        frameFull = Ui.FlatButton("Full Body", () => FrameShot(Framing.Shot.Full), "Frame the whole character.");
        fullScreen = Ui.FlatButton("⛶", ToggleFull, "Full screen: the preview fills the screen (F11). Esc (after pausing), F11 or this button come back.");
        powersButton = Ui.FlatButton("Power FXs", () => { P.Powers = !P.Powers; SaveP(); ApplyLook(); LoadEffects(); }, "Play the effects of the power an animation belongs to with it (lightning, shockwaves, trails …, read from the game's power packages). Lit when on; remembered.");
        Icons.Make(loop, "Loop", Icons.Loop, S);
        Icons.Make(look, "Look", Icons.Look, S);
        Icons.Make(reset, "Reset View", Icons.ResetView, S);
        foreach (var b in new[] { frameHead, frameBust, frameFull, fullScreen })
        {
            b.AutoSize = false; b.Padding = new Padding(0); b.Size = new Size((int)(34 * S), (int)(30 * S));
        }
        Ui.IconPainters.AddOrUpdate(frameFull, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Full));
        Ui.IconPainters.AddOrUpdate(frameHead, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.HeadShoulders));
        Ui.IconPainters.AddOrUpdate(frameBust, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Bust));
        Ui.IconPainters.AddOrUpdate(fullScreen, (g, r, c) => FullScreenIcon(g, r, c, fullForm != null));
        Ui.Tip(anims, "The base hero's own animations (from its base package, in the game's order). Rest Pose = the bind pose the mesh is built in. A power button below shows only that power's.");
        Ui.Tip(frame, "The animation's frame: drag to scrub (pauses).");
        Ui.Tip(light, "Light strength (double-click: 100 %). Remembered.");
        Ui.Tip(glowSlider, "How strong the glowing (emissive) parts are (double-click: 100 %). Remembered.");
        Ui.Tip(lens, "The camera's lens (35 mm equivalent): short is wide with strong perspective, long is flatter; the model stays the same size (double-click: 50 mm). Remembered.");
        Ui.Tip(fxLabel, "The power the animation belongs to and how many of its effects play.");
        fxLabel.ForeColor = Ui.Subtle;
        light.Value = P.Light; light.Home = () => 1; view.Brightness = P.Light;
        lens.Value = P.Lens; lens.Home = () => 50; view.FocalLength = P.Lens;
        glowSlider.Value = P.GlowStrength; glowSlider.Home = () => 1; view.GlowStrength = P.GlowStrength;
        light.ValueChanged += () => view.Brightness = light.Value;
        light.Committed += () => { P.Light = light.Value; SaveP(); };
        lens.ValueChanged += () => view.FocalLength = lens.Value;
        lens.Committed += () => { P.Lens = lens.Value; SaveP(); };
        glowSlider.ValueChanged += () => view.GlowStrength = glowSlider.Value;
        glowSlider.Committed += () => { P.GlowStrength = glowSlider.Value; SaveP(); };
        frame.ValueChanged += ScrubTo;
        frame.Enabled = false; frame.Format = _ => "–";
        anims.SelectedIndexChanged += (_, _) => AnimationChosen();
        timer.Tick += (_, _) => Tick();
        Ui.Lit(loop, P.Loop);
        showBones = P.Bones;
        ApplyLook();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(view, 0, 0);
        edits.AutoSize = true; edits.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // one row (Kurt, 2026-10-04: the animation list was twice as wide as it needs; Look, Reset View, the framing and full
        // screen buttons beside it): the list takes what the buttons leave
        var playBar = Strip(anims, play, loop, edits, compare, look, reset, frameHead, frameBust, frameFull, fullScreen);
        root.Controls.Add(playBar, 0, 1);                                   // the playback bar under the view
        // a narrow window: the menu's caption shortens so the animation drop-down keeps at least 160 px (0.16.5)
        playBar.SizeChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(edits.AccessibleName)) return;   // (an icon now: nothing to shorten)
            int others = new Control[] { play, loop, compare, look, reset, frameHead, frameBust, frameFull, fullScreen }.Sum(c => c.Width) + (int)(11 * 6 * S);
            string want = playBar.Width - others - TextRenderer.MeasureText("Single Animation ▾", edits.Font).Width - (int)(30 * S) >= (int)(160 * S) ? "Single Animation ▾" : "Single ▾";
            if (edits.Text != want) edits.Text = want;
        };
        root.Controls.Add(frame, 0, 2);
        root.Controls.Add(PowerBlock(), 0, 3);                                                // Power FXs + the power buttons
        Controls.Add(root);
        view.ShowMessage("Pick a character and a base hero");
        // the skeleton overlay, painted after the view's own frame (its canvas is the view's only child)
        view.Controls[0].Paint += (_, e) => PaintBones(e.Graphics, view.Controls[0].ClientSize);
        // Ctrl+click picks the bone under the mouse (0.13.3, Kurt): the Bone Map tab selects its row
        view.Controls[0].MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) == 0) return;
            if (PickBone(e.Location, view.Controls[0].ClientSize) is string bone) BonePicked?.Invoke(bone);
        };
        Ui.Tip(view.Controls[0], "Drag to turn, right-drag to move, wheel to zoom (Shift: finer), double-click to frame. Ctrl+click a bone to pick it in the Bone Map tab.");
    }

    static Control Strip(Control? fill, params Control[] buttons)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = buttons.Length + 1, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < buttons.Length; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (fill != null) { fill.Dock = DockStyle.Fill; fill.Margin = new Padding(0, 2, 6, 0); t.Controls.Add(fill, 0, 0); }
        for (int i = 0; i < buttons.Length; i++) { buttons[i].Margin = new Padding(0, 0, 6, 0); t.Controls.Add(buttons[i], i + 1, 0); }
        return t;
    }

    /// <summary>The look switches into the view and the buttons that show them.</summary>
    void ApplyLook()
    {
        view.ShowSpec = P.Spec; view.ShowReflections = P.Reflect; view.ShowGlow = P.Glow; view.ShowBloom = P.Bloom;
        Ui.Lit(powersButton, P.Powers);
    }

    /// <summary>Look ▾: the toggles (the menu stays open while toggling) and the Light / Glow / Lens sliders.</summary>
    void ShowLookMenu()
    {
        if (lookMenu == null)
        {
            lookMenu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            void Item(string text, string tip, Func<bool> get, Action set)
            {
                var it = new ToolStripMenuItem(text) { ToolTipText = tip };
                it.Click += (_, _) => { set(); SaveP(); it.Checked = get(); ApplyLook(); };
                lookMenu.Opening += (_, _) => it.Checked = get();
                lookMenu.Items.Add(it);
            }
            Item("Spec", "Specular highlights (shine).", () => P.Spec, () => P.Spec = !P.Spec);
            Item("Reflect", "Metal reflections.", () => P.Reflect, () => P.Reflect = !P.Reflect);
            Item("Glow", "Glowing (emissive) parts.", () => P.Glow, () => P.Glow = !P.Glow);
            Item("Bloom", "A soft halo around the brightest parts (glow, the hottest highlights), as the game draws it.", () => P.Bloom, () => P.Bloom = !P.Bloom);
            Item("Props", "The base hero's weapons and props (what its powers attach), held on their bones; they switch with the powers.", () => P.Props, () => { P.Props = !P.Props; LoadProps(); });
            Item("Power FXs", "The effects of the power an animation belongs to.", () => P.Powers, () => { P.Powers = !P.Powers; LoadEffects(); });
            Item("Bones", "The skeleton over the model; the Bone Map tab's selected row in orange.", () => showBones, () => ShowBones = !showBones);
            Item("Weights", "Weight paint: the model coloured by how much the Bone Map tab's selected bones move it (blue 0 → green → red 1), as Blender's weight painting.", () => showWeights, () => ShowWeights = !showWeights);
            lookMenu.Items.Add(new ToolStripSeparator());
            foreach (var sl in new Control[] { light, glowSlider, lens })
                lookMenu.Items.Add(new ToolStripControlHost(sl) { AutoSize = false, Size = new Size((int)(260 * S), (int)(26 * S)), Margin = new Padding((int)(6 * S), 2, (int)(6 * S), 2) });
            lookMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        }
        Ui.ShowUnder(lookMenu, look);
    }

    /// <summary>Test: the camera (ModelView.ViewState).</summary>
    internal float[] ViewForTest { get => view.ViewState; set => view.ViewState = value; }

    /// <summary>One map on its own instead of the lit model (the Materials tab's Preview Shows; ModelView.MapView).</summary>
    public ModelView.MapView ShowMap { get => view.ShowMap; set => view.ShowMap = value; }

    /// <summary>Test: the view as shown, <paramref name="w"/>×<paramref name="h"/>.</summary>
    internal Bitmap? SnapshotForTest(int w, int h) => view.Snapshot(w, h, 1);

    /// <summary>Test: Compare lit and locked (the target alone).</summary>
    internal (bool Lit, bool Enabled, bool TargetOnly) CompareForTest => (comparing, compare.Enabled, targetOnly);
    /// <summary>Test: the shown model's section looks.</summary>
    internal IEnumerable<ModelView.Look?> LooksForTest => shown?.Mesh.Looks ?? [];

    float[]? heldView;
    bool heldPlaying;
    float heldAt;

    public void ShowMessage(string text)
    {
        // a rebuild shows "Loading…" between two models: what was shown is held for the next Show (Kurt, 2026-10-04: Smooth
        // Weights re-framed the camera and stopped playback; the camera and the place in the animation are kept now)
        if (animator != null) { heldView = view.ViewState; heldPlaying = timer.Enabled; heldAt = seconds > 0 ? Math.Clamp(playTime / seconds, 0, 1) : 0; }
        loadId++; frameBones = null;
        timer.Stop(); animator = mffAnimator = stockAnimator = null; anim = null; shown = null; shownLoaded = null;
        ClearEffects(); rig.Clear(); ClearPowers();
        view.Moving = false;
        view.ShowMessage(text);
    }

    // --- showing a model -----------------------------------------------------------------------------------------------------
    /// <summary>Shows a prepared model (UI thread), keeping the animation picked before when the new list has it.</summary>
    public void Show(Prepared p)
    {
        // What the reload keeps (0.10.17, Kurt): the animation by name, where in it (a share of its length), playing or
        // paused, and the camera; the frame only when the new list has that animation.
        string? keep = anims.SelectedIndex > 0 ? anims.SelectedItem as string : null;
        bool live = animator != null;
        float keepAt = live ? (seconds > 0 ? Math.Clamp(playTime / seconds, 0, 1) : 0) : heldAt;
        bool wasPlaying = live ? timer.Enabled : heldPlaying;
        float[]? keepView = live ? view.ViewState : heldView;
        heldView = null; heldPlaying = false; heldAt = 0;
        bool samePackage = shown != null && shown.PackagePath.Equals(p.PackagePath, StringComparison.OrdinalIgnoreCase);
        timer.Stop(); anim = null; playTime = 0;
        shown = p;
        mffAnimator = new MeshAnimator(p.Bones, p.Mesh.Positions, p.Mesh.Normals, p.Mesh.Influences, p.Mesh.Tangents);
        stockAnimator = p.Stock is { } st ? new MeshAnimator(st.Bones.ToList(), st.Positions, st.Normals, st.Influences, st.Tangents) : null;
        if (stockAnimator == null && comparing) { comparing = false; Ui.Lit(compare, false); }
        compare.Enabled = stockAnimator != null;
        // no source: the target's own model, Compare on and locked; a source picked after it starts on the source again
        if (p.TargetOnly) { comparing = true; Ui.Lit(compare, true); compare.Enabled = false; }
        else if (targetOnly) { comparing = false; Ui.Lit(compare, false); }
        targetOnly = p.TargetOnly;
        Ui.Tip(compare, p.TargetOnly ? "No source is picked: this is the target's own model. Pick a source on the left to put a model on it; Compare then switches between the two."
            : "Show the target's own in-game mesh instead, in the same animation, frame and view, to compare (click again for the source's model).");
        animator = comparing ? stockAnimator : mffAnimator;
        animator!.Pose(null, 0);   // posed at once: a new animator's bones are all zero until its first pose (MEMM 0.37.37)
        allAnims = p.Animations;
        if (!samePackage) ClearPowers();
        animList = powerFilter != null && heroPowers.FirstOrDefault(x => x.Prototype == powerFilter) is { } pw
            ? [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))] : allAnims;
        ShowMeshFramed(comparing ? p.Stock! : p.Mesh);
        FillAnims();
        int pick = Math.Max(0, keep != null ? animList.FindIndex(a => a.Name == keep) + 1 : 0);
        int before = anims.SelectedIndex;
        anims.SelectedIndex = pick;
        if (anims.SelectedIndex == before) AnimationChosen();   // same index: the list fires no change, load it anyway
        if (keepView != null) view.ViewState = keepView;
        if (pick > 0 && anim != null && seconds > 0)
        {
            playTime = keepAt * seconds;
            FxReplay(playTime);
            Pose(keepAt * frames);
            if (wasPlaying) { clock.Restart(); timer.Start(); play.Text = "❚❚"; view.Moving = true; }
        }
        LoadProps();
        if (!samePackage || heroPowers.Count == 0) LoadHeroPowers();
        Ui.Tip(view, p.Note);
        ShowCount++;
    }

    void FillAnims()
    {
        anims.Items.Clear();
        string? filterName = powerFilter == null ? null : heroPowers.FirstOrDefault(x => x.Prototype == powerFilter)?.Name;
        anims.Items.Add(filterName != null ? $"Rest Pose · {animList.Count} of {allAnims.Count} animations ({filterName})" : $"Rest Pose · {animList.Count} animations");
        foreach (var a in animList) anims.Items.Add(a.Name);
        // an animation replaced by an FBX: marked ✎, in gold
        anims.Format = o => o is string n && IsOverridden?.Invoke(n) == true ? n + "  ✎" : o?.ToString() ?? "";
        anims.ItemColor = i => i > 0 && i < anims.Items.Count && anims.Items[i] is string n && IsOverridden?.Invoke(n) == true ? Color.FromArgb(240, 200, 90) : null;
    }

    /// <summary>The animation reloaded (an edit imported or reverted), at the same place in it.</summary>
    public void ReloadAnimation()
    {
        float at = seconds > 0 ? Math.Clamp(playTime / seconds, 0, 1) : 0;
        bool wasPlaying = timer.Enabled;
        FillAnimsKeep();
        AnimationChosen();
        if (anim != null && seconds > 0) { playTime = at * seconds; FxReplay(playTime); Pose(at * frames); if (wasPlaying) TogglePlay(); }
    }

    void FillAnimsKeep()
    {
        int sel = anims.SelectedIndex;
        FillAnims();
        anims.SelectedIndex = Math.Min(sel, anims.Items.Count - 1);
    }

    void ShowEditsMenu()
    {
        var m = new ContextMenuStrip();
        FillEditsMenu?.Invoke(m);
        if (m.Items.Count == 0) return;
        Ui.ShowUnder(m, edits);
    }

    /// <summary>The animation and where in it (checks): "name @ 0.35".</summary>
    public string PlaybackState => $"{anims.SelectedItem} @ {frame.Value:0.00}";
    /// <summary>How many models have been shown (checks wait for a reload).</summary>
    public int ShowCount { get; private set; }

    /// <summary>A model is shown (checks wait for it).</summary>
    public bool Ready => animator != null;

    /// <summary>Picks the first animation whose name contains <paramref name="part"/> and poses it at a fraction of its length (checks).</summary>
    public void PickAnimation(string part, float at)
    {
        int i = animList.FindIndex(a => a.Name.Contains(part, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        anims.SelectedIndex = i + 1;
        playTime = at * seconds;
        FxReplay(playTime);
        Pose(at * frames);
    }

    /// <summary>Turns Compare on (checks).</summary>
    public void CompareOn() { if (!comparing) ToggleCompare(); }

    /// <summary>Swaps between the MFF model and the base hero's own mesh, keeping the animation, frame and camera.</summary>
    void ToggleCompare()
    {
        if (shown == null || stockAnimator == null || mffAnimator == null) return;
        comparing = !comparing; Ui.Lit(compare, comparing);
        var keepView = view.ViewState;
        animator = comparing ? stockAnimator : mffAnimator;
        ShowMeshFramed(comparing ? shown.Stock! : shown.Mesh);
        view.ViewState = keepView;
        RebuildRig();
        Pose(frames > 0 ? frame.Value * frames : 0);
    }

    // --- animation ------------------------------------------------------------------------------------------------------------
    void AnimationChosen()
    {
        timer.Stop(); clock.Reset(); playTime = 0; play.Text = "▶"; view.Moving = false;
        int i = anims.SelectedIndex - 1;
        anim = i >= 0 && i < animList.Count ? ModAnimations.Load(animList[i]) : null;
        // an FBX in its place (0.16.0): the import's own tracks; a borrowed rig still gets its motion unless the FBX animates it
        BoneAnimation? imported = i >= 0 && i < animList.Count ? Override?.Invoke(animList[i], anim) : null;
        if (imported != null) anim = imported;
        // a borrowed cape (prototype): its tracks made for this animation (the base hero's own mesh ignores them in Compare)
        if (anim != null && shown != null)
            foreach (var rig in shown.Rigs)
                if (imported == null || !shown.Bones.Where(b => rig.Bones.IsMatch(b.Name)).All(b => imported.Tracks.ContainsKey(b.Name))) anim = rig.Apply(anim, shown.Bones);
        (frames, seconds) = anim != null ? MeshAnimator.Span(anim) : (0, 0);
        frame.Enabled = anim != null;
        float fr = frames; frame.Format = v => fr > 0 ? $"{v * fr:0} / {fr:0}" : "–";
        rig.SetParentAnimation(anim);
        LoadEffects();
        LoadPropSwitches();
        Pose(0);
        RefreshPowerButtons();
        if (autoPlay) { autoPlay = false; if (anim != null) TogglePlay(); }
    }

    void TogglePlay()
    {
        if (anim == null || animator == null) return;
        if (timer.Enabled) { timer.Stop(); clock.Reset(); play.Text = "▶"; view.Moving = false; return; }
        if (playTime >= seconds) { playTime = 0; FxReplay(0); }
        clock.Restart(); timer.Start(); play.Text = "❚❚"; view.Moving = true;   // smaller frames while playing (ModelView.Moving)
    }

    /// <summary>P (Kurt, 2026-10-04): plays or pauses the picked animation; false when none is picked.</summary>
    public bool TogglePlayback()
    {
        if (anim == null || animator == null) return false;
        TogglePlay();
        return true;
    }

    /// <summary>Esc: pauses a playing animation; false when nothing was playing.</summary>
    public bool PausePlayback()
    {
        if (!timer.Enabled) return false;
        TogglePlay();
        return true;
    }

    void Tick()
    {
        if (anim == null || seconds <= 0) { timer.Stop(); return; }
        playTime += (float)clock.Elapsed.TotalSeconds; clock.Restart();
        bool ended = false;
        if (playTime >= seconds)
        {
            if (P.Loop) playTime %= seconds;
            else { playTime = seconds; timer.Stop(); play.Text = "▶"; view.Moving = false; ended = true; }
        }
        FxAdvance(playTime, ended);
        Pose(playTime / seconds * frames);
    }

    void Pose(float f)
    {
        if (animator == null) return;
        animator.Pose(anim, f);
        ShowPose();
        settingFrame = true; frame.Value = frames > 0 ? f / frames : 0; settingFrame = false;
    }

    /// <summary>The pose last made: the effects, then the props on their bones (or the plain mesh).</summary>
    void ShowPose()
    {
        if (animator == null) return;
        if (fxPlayer != null) { view.Effects = fxPlayer.Quads(); view.EffectTris = fxPlayer.Tris(); }
        else if (view.Effects.Count > 0 || view.EffectTris.Count > 0) { view.Effects = []; view.EffectTris = []; }
        rig.At(anim == null || seconds <= 0 ? 0 : Math.Min(playTime, seconds));
        rig.Update(view, animator);
    }

    void ScrubTo()
    {
        if (settingFrame || anim == null) return;
        if (timer.Enabled) { timer.Stop(); clock.Reset(); play.Text = "▶"; view.Moving = false; }
        playTime = frame.Value * seconds;
        FxReplay(playTime);
        Pose(frame.Value * frames);
    }

    // --- framing and full screen ----------------------------------------------------------------------------------------------
    /// <summary>A framing button: aims the camera as the Mod Manager's preview does (head centred).</summary>
    void FrameShot(Framing.Shot shot)
    {
        if (animator == null) return;
        Framing.Apply(view, animator, anim != null, shot, centerHead: true);
        if (anim == null) animator.Pose(null, 0);
        ShowPose();
    }

    Form? fullForm;
    Control? homeParent;
    int homeIndex;

    /// <summary>Into or out of full screen: this panel moves into a borderless window on its monitor and back (F11).</summary>
    public void ToggleFull()
    {
        if (fullForm != null)
        {
            var f = fullForm;
            fullForm = null;
            if (homeParent != null && !homeParent.IsDisposed)
            {
                Parent = homeParent;
                homeParent.Controls.SetChildIndex(this, homeIndex);
            }
            f.Close(); f.Dispose();
            fullScreen.Invalidate();
            FindForm()?.Activate();
            return;
        }
        if (Parent == null) return;
        var owner = FindForm();
        homeParent = Parent;
        homeIndex = homeParent.Controls.GetChildIndex(this);
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.FromControl(this).Bounds,
            ShowInTaskbar = false, Text = "Preview", BackColor = Ui.GradientTop, KeyPreview = true, Padding = new Padding(10),
        };
        form.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Escape or Keys.F11) { e.Handled = true; if (e.KeyCode == Keys.F11 || !PausePlayback()) ToggleFull(); }   // Esc: pause first, then back
            else if (e.KeyData == Keys.P) { e.Handled = true; TogglePlayback(); }
        };
        form.FormClosing += (_, e) => { if (fullForm == form && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; ToggleFull(); } };
        fullForm = form;
        Parent = form;
        form.Show(owner);
        view.Focus();
        fullScreen.Invalidate();
    }

    public bool IsFull => fullForm != null;

    /// <summary>A framing button's icon: a figure cropped as the shot frames it (whole body, head and shoulders, head and chest).</summary>
    internal static void PersonIcon(Graphics g, Rectangle r, Color c, Framing.Shot shot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.18f;
        var box = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        float w = box.Width, h = box.Height, cx = box.X + w / 2;
        var clip = g.Clip;
        g.SetClip(box);
        using var br = new SolidBrush(c);
        void Head(float cy, float rad) => g.FillEllipse(br, cx - rad, cy - rad, 2 * rad, 2 * rad);
        void Block(RectangleF b, float radius) { using var p = RoundRect(b, radius); g.FillPath(br, p); }
        switch (shot)
        {
            case Framing.Shot.Full:
                Head(box.Y + h * 0.12f, h * 0.12f);
                Block(new RectangleF(cx - w * 0.17f, box.Y + h * 0.27f, w * 0.34f, h * 0.36f), w * 0.08f);
                g.FillRectangle(br, cx - w * 0.15f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                g.FillRectangle(br, cx + w * 0.03f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                break;
            case Framing.Shot.HeadShoulders:
                Head(box.Y + h * 0.36f, h * 0.26f);
                Block(new RectangleF(cx - w * 0.5f, box.Y + h * 0.72f, w, h * 1.2f), w * 0.35f);
                break;
            default:   // bust
                Head(box.Y + h * 0.22f, h * 0.18f);
                Block(new RectangleF(cx - w * 0.36f, box.Y + h * 0.46f, w * 0.72f, h * 1.2f), w * 0.25f);
                break;
        }
        g.Clip = clip;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The full screen button's icon: four corners pointing out, or a cross while full screen (back).</summary>
    static void FullScreenIcon(Graphics g, Rectangle r, Color c, bool full)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.27f, l = r.Width * 0.16f;
        var b = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        using var pen = new Pen(c, Math.Max(1.6f, r.Width / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (full) { g.DrawLine(pen, b.Left, b.Top, b.Right, b.Bottom); g.DrawLine(pen, b.Right, b.Top, b.Left, b.Bottom); return; }
        g.DrawLines(pen, new[] { new PointF(b.Left, b.Top + l), new PointF(b.Left, b.Top), new PointF(b.Left + l, b.Top) });
        g.DrawLines(pen, new[] { new PointF(b.Right - l, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Top + l) });
        g.DrawLines(pen, new[] { new PointF(b.Right, b.Bottom - l), new PointF(b.Right, b.Bottom), new PointF(b.Right - l, b.Bottom) });
        g.DrawLines(pen, new[] { new PointF(b.Left + l, b.Bottom), new PointF(b.Left, b.Bottom), new PointF(b.Left, b.Bottom - l) });
    }

    // --- skeleton overlay (0.12.0, Kurt: the skeleton over the model, the active bones highlighted) -----------------------------
    bool showBones;
    HashSet<string> hot = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<MeshBone>? frameBones;
    /// <summary>The bones drawn: those carrying skin and the bones above them (no IK targets, root lines to the floor).</summary>
    bool[] frameDrawn = [];
    Vector3 frameCenter; float frameRadius = 1;

    /// <summary>The bones to draw in orange (the Bone Map tab's pick); empty = none.</summary>
    public void Highlight(IEnumerable<string> bones)
    {
        var next = new HashSet<string>(bones, StringComparer.OrdinalIgnoreCase);
        bool changed = !next.SetEquals(hot);
        hot = next;
        if (showWeights && changed) RefreshDisplay();
        if (showBones) view.Controls[0].Invalidate();
    }

    /// <summary>Whether the skeleton is drawn (Look ▾ → Bones; remembered).</summary>
    public bool ShowBones { get => showBones; set { showBones = value; P.Bones = value; SaveP(); view.Controls[0].Invalidate(); TogglesChanged?.Invoke(); } }

    /// <summary>Shows a character mesh and keeps what the view frames it by (its bounds, display space: Y mirrored), so the
    /// overlay can use the same camera; props come back through RebuildRig.</summary>
    void ShowMeshFramed(ModMeshes.Loaded mesh)
    {
        shownLoaded = mesh;
        view.ShowMesh(Display(mesh));
        frameBones = mesh.Bones;
        frameDrawn = new bool[mesh.Bones.Count];
        foreach (var inf in mesh.Influences)
            for (int k = 0; k < inf.Bones.Count; k++)
                if (inf.Weights[k] > 0 && inf.Bones[k] >= 0 && inf.Bones[k] < frameDrawn.Length)
                    for (int b = inf.Bones[k], g = 0; b >= 0 && b < frameDrawn.Length && !frameDrawn[b] && g < 256; g++)
                    { frameDrawn[b] = true; int pb = mesh.Bones[b].ParentIndex; b = pb == b ? -1 : pb; }
        if (mesh.Positions.Length == 0) return;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var q in mesh.Positions) { var d = new Vector3(q.X, -q.Y, q.Z); lo = Vector3.Min(lo, d); hi = Vector3.Max(hi, d); }
        frameCenter = (lo + hi) / 2; frameRadius = Math.Max(1f, (hi - lo).Length() / 2);
    }

    /// <summary>
    /// Draws the posed skeleton over the frame: a line from each bone to its parent, a dot at each joint; the highlighted
    /// bones thicker in orange. The camera is ModelView's own, rebuilt from its public state (ViewState: yaw, pitch,
    /// distance / radius / lens scale, target offset / radius; FocalLength) and the bounds it framed the mesh by (not
    /// exposed; the vendored file stays unchanged).
    /// </summary>
    /// <summary>Check: a Ctrl+click where <paramref name="bone"/>'s joint is drawn (or, with <paramref name="segment"/>, halfway
    /// along the segment from it to its first drawn child); returns what was picked (and raises BonePicked).</summary>
    public string? TestPick(string bone, bool segment)
    {
        var size = view.Controls[0].ClientSize;
        if (frameBones == null || Project(size) is not { } pt) return null;
        int i = -1;
        for (int k = 0; k < frameBones.Count; k++) if (frameBones[k].Name.Equals(bone, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0 || pt[i] is not PointF a) return null;
        var at = a;
        if (segment)
            for (int c = 0; c < frameBones.Count; c++)
                if (frameBones[c].ParentIndex == i && c < frameDrawn.Length && frameDrawn[c] && pt[c] is PointF b) { at = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); break; }
        string? picked = PickBone(Point.Round(at), size);
        if (picked != null) BonePicked?.Invoke(picked);
        return picked;
    }

    /// <summary>A bone was Ctrl+clicked in the view (its MHO name).</summary>
    public event Action<string>? BonePicked;

    /// <summary>
    /// The bone under a point of the view: the nearest joint dot within 12 px, else the nearest drawn segment within 8 px
    /// (a segment is moved by the bone it starts from: the shin picks the knee). Null when nothing is that close.
    /// </summary>
    string? PickBone(Point at, Size size)
    {
        if (animator == null || frameBones == null || Project(size) is not { } pt) return null;
        float s = DeviceDpi / 96f;
        int n = frameBones.Count, joint = -1, seg = -1;
        float dj = 12 * s, ds = 8 * s;
        bool Drawn(int i) => i < frameDrawn.Length && frameDrawn[i] && frameBones[i].ParentIndex != i;
        // a helper "_offset" bone sits on its real bone's joint: the real one wins a tie
        float Penalty(int i) => frameBones[i].Name.EndsWith("_offset", StringComparison.OrdinalIgnoreCase) ? 3 * s : 0;
        for (int i = 0; i < n; i++)
            if (Drawn(i) && pt[i] is PointF a && Dist(at, a) + Penalty(i) is float d && d < dj) { dj = d; joint = i; }
        for (int i = 0; i < n; i++)
        {
            int p = frameBones[i].ParentIndex;
            if (!Drawn(i) || p < 0 || p == i || p >= n || !Drawn(p) || pt[i] is not PointF a || pt[p] is not PointF b) continue;
            float d = SegmentDist(at, b, a) + Penalty(p);
            if (d < ds) { ds = d; seg = p; }
        }
        // a dot wins when it's very close or nearer than any line (in a small view the next joint is only a few pixels away)
        int best = joint >= 0 && (dj <= 6 * s || seg < 0 || dj <= ds) ? joint : seg;
        return best >= 0 ? frameBones[best].Name : null;

        static float Dist(Point p, PointF q) => MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        static float SegmentDist(Point p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            float t = len < 1e-6f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return MathF.Sqrt(x * x + y * y);
        }
    }

    /// <summary>Where each bone of the pose last made is on the view (null: behind the camera); null without a model.</summary>
    PointF?[]? Project(Size size)
    {
        if (animator == null || frameBones == null || size.Width < 8 || size.Height < 8) return null;
        var vs = view.ViewState;
        float fov = 2 * MathF.Atan(12f / view.FocalLength);
        float lensScale = MathF.Tan(ModelView.ReferenceFov / 2) / MathF.Tan(fov / 2);
        float yaw = vs[0], pitch = vs[1], distance = vs[2] * frameRadius * lensScale;
        var target = frameCenter + new Vector3(vs[3], vs[4], vs[5]) * frameRadius;
        var eye = target + distance * new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var u = Vector3.Cross(r, f);
        float focal = size.Height / 2f / MathF.Tan(fov / 2), cx = size.Width / 2f, cy = size.Height / 2f, near = distance * 0.01f;
        int n = frameBones.Count;
        var pt = new PointF?[n];
        for (int i = 0; i < n; i++)
        {
            var e = animator.BonePosition(i);
            var d = new Vector3(e.X, -e.Y, e.Z) - eye;
            float z = Vector3.Dot(d, f);
            if (z < near) continue;
            pt[i] = new PointF(cx + Vector3.Dot(d, r) * focal / z, cy - Vector3.Dot(d, u) * focal / z);
        }
        return pt;
    }

    void PaintBones(Graphics g, Size size)
    {
        if (!showBones || frameBones == null || Project(size) is not { } pt) return;
        int n = frameBones.Count;
        float s = DeviceDpi / 96f;
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var cold = new Pen(Color.FromArgb(170, 120, 200, 255), 1.2f * s);
        using var warm = new Pen(Color.FromArgb(255, 255, 150, 40), 3f * s);
        using var coldDot = new SolidBrush(Color.FromArgb(190, 160, 215, 255));
        using var warmDot = new SolidBrush(Color.FromArgb(255, 255, 170, 60));
        // a highlighted bone: its dot, and the segments it moves (to its children) and into it, in orange
        bool Hot(int i) => i >= 0 && i < n && hot.Contains(frameBones[i].Name);
        for (int pass = 0; pass < 2; pass++)   // the highlighted bones last, on top
            for (int i = 0; i < n; i++)
            {
                int p = frameBones[i].ParentIndex;
                bool rootParent = p < 0 || p == i || p >= n || frameBones[p].ParentIndex == p;   // no line up from the root at the floor
                bool lineHot = Hot(i) || Hot(p);
                if (pt[i] is not PointF a || (!Hot(i) && i < frameDrawn.Length && !frameDrawn[i])) continue;
                if (lineHot == (pass == 1) && !rootParent && (p >= frameDrawn.Length || frameDrawn[p] || Hot(p)) && pt[p] is PointF b) g.DrawLine(lineHot ? warm : cold, b, a);
                if (Hot(i) != (pass == 1) || (frameBones[i].ParentIndex == i && !Hot(i))) continue;
                float rad = (Hot(i) ? 3.5f : 2f) * s;
                g.FillEllipse(Hot(i) ? warmDot : coldDot, a.X - rad, a.Y - rad, rad * 2, rad * 2);
            }
        g.SmoothingMode = mode;
    }

    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); fullForm?.Dispose(); } base.Dispose(disposing); }
}
