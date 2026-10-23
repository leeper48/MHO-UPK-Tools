using AnimExportCli.Animation;

namespace MhoExtendedModManager.Gui;

/// <summary>Where a 3D preview keeps its look and playback switches (the Model tab: Settings; the Mods tab: per mod).</summary>
interface IPreviewPrefs
{
    bool Spec { get; set; }
    bool Reflect { get; set; }
    bool Glow { get; set; }
    bool Bloom { get; set; }
    /// <summary>The ground plane at the feet (ModelView.ShowGround).</summary>
    bool Ground { get; set; }
    bool Props { get; set; }
    bool Powers { get; set; }
    bool Loop { get; set; }
    float Light { get; set; }
    float Lens { get; set; }
    float GlowStrength { get; set; }
    void Save();
}

/// <summary>What a 3D preview shows the powers and props of: the hero (whose powers), the game's packages, the character's
/// mesh and its package's meshes, the mod's packages (read before the game's), the costume class (its own effects), and the
/// condition material swaps for a power (null: none).</summary>
sealed record PreviewContext(string? Hero, string? Cooked, MeshRef? MainRef, IReadOnlyList<MeshRef> PackageMeshes,
    IReadOnlyList<(string File, string Path)> ModPackages, Func<string?> Entity, Func<string, Func<Fx.GameData, string, ModelView.Look?[]?>?>? SwapsFor)
{
    /// <summary>The packages the props are read from (null: ModPackages; the Powers tab leaves its rebuilt power packages
    /// out of ModPackages, not out of the props).</summary>
    public IReadOnlyList<(string File, string Path)>? PropPackages { get; init; }
}

/// <summary>
/// One 3D preview (phase 3 of the preview merge, Kurt 2026-10-12: "the Model, Powers and Mods home previewer should handle
/// power previews the same"): the 3D view (ModelView), the playback bar (animation, ▶, Loop, Frame), Look ▾, Reset View,
/// the Head / Bust / Full Body framing and full screen (⛶ / F11), then the hero's power buttons with the Power FXs toggle
/// (Preview3D.Powers.cs). The playback, effects and props are the shared helpers (AnimPlayer, PreviewEffects,
/// PreviewProps). The host shows a character (ShowCharacter) and adds what's its own: the Model tab its Compare and edits
/// buttons, the bones overlay and the weight paint (DisplayMesh, MeshShown).
/// </summary>
sealed partial class Preview3D : UserControl
{
    readonly ModelView view = new() { Dock = DockStyle.Fill };
    readonly DropDown anims = new() { Dock = DockStyle.Fill };
    readonly Button play, loop, reset, look, frameHead, frameBust, frameFull, fullScreen, powersButton;
    readonly LightSlider frame = new() { Dock = DockStyle.Fill, Label = "Frame", Min = 0, Max = 1, Step = 0, Mark = null, Height = 26 };
    readonly LightSlider light = new() { Label = "Light", Height = 26 };
    readonly LightSlider glowSlider = new() { Label = "Glow", Min = 0f, Max = 2f, Height = 26 };
    readonly LightSlider lens = new() { Label = "Lens", Min = 15, Max = 200, Step = 0.5f, Mark = 50, Height = 26, Format = v => $"{v:0} mm" };
    readonly LightSlider effectsSlider = new() { Label = "Effects", Min = 0.01f, Max = 1f, Step = 0.01f, Mark = PreviewViews.DefaultFxPower, Height = 26, Format = v => $"{v * 100:0} %" };
    readonly Label fxLabel = new() { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
    readonly Control bar;
    readonly Control powerBlock;
    readonly TableLayoutPanel rootTable;
    Control? powerHost, wantedHost;
    ContextMenuStrip? lookMenu;
    readonly IPreviewPrefs prefs;

    MeshAnimator? animator;
    ModMeshes.Loaded? shownLoaded;
    PreviewContext? ctx;
    /// <summary>The animations in the drop-down (a power button filters them) and all of the character's.</summary>
    List<AnimRef> animList = new(), allAnims = new();
    AnimPlayer? player;
    AnimPlayer Player => player ??= new AnimPlayer
    {
        Loop = () => prefs.Loop,
        Moved = (f, ended) => { FxAdvance(playTime, ended); Pose(f); },
        StateChanged = () => { play.Text = IsPlaying ? "❚❚" : "▶"; view.Moving = IsPlaying; if (!IsPlaying) PlaybackMoved?.Invoke(); },   // smaller frames while playing (ModelView.Moving)
        CanRun = () => CanRun?.Invoke() ?? true,
    };
    BoneAnimation? anim => player?.Anim;
    float frames => player?.Frames ?? 0;
    float seconds => player?.Seconds ?? 0;
    float playTime { get => (float)(player?.Time ?? 0); set => Player.Time = value; }
    bool settingFrame, autoPlay;

    float S => Ui.Dpi(DeviceDpi);

    // --- what the host adds -----------------------------------------------------------------------------------------------------
    /// <summary>An animation as played (the Model tab: an FBX edit in its place, a borrowed cape's tracks); null = as read.</summary>
    public Func<AnimRef, BoneAnimation?, BoneAnimation?>? TransformAnimation { get; set; }
    /// <summary>Animations shown with ✎ in gold (replaced by an edit).</summary>
    public Func<string, bool>? IsOverridden { get; set; }
    /// <summary>The character as drawn (the Model tab's weight paint); null = as loaded.</summary>
    public Func<ModMeshes.Loaded, ModMeshes.Loaded>? DisplayMesh { get; set; }
    /// <summary>A character mesh was put in the view (the Model tab's bones overlay measures it).</summary>
    public event Action<ModMeshes.Loaded>? MeshShown;
    /// <summary>More Look ▾ toggles after the standard ones (the Model tab's Bones and Weights); set before the menu opens.</summary>
    public List<PreviewControls.LookItem> ExtraLook { get; } = [];
    /// <summary>The playback bar (the host's buttons sit in it).</summary>
    public Control Bar => bar;
    /// <summary>Whether playback may run now; false pauses it (the Mods tab: a picture shows, or the preview is hidden).</summary>
    public Func<bool>? CanRun { get; set; }
    /// <summary>The light slider's double-click value (the Mods tab: the mod's own light).</summary>
    public Func<float>? LightHome { get; set; }
    /// <summary>Reset View (the Mods tab: back to the mod author's view); null = the default framing.</summary>
    public Action? ResetViewAction { get; set; }
    /// <summary>The camera was turned, zoomed, moved or framed (the Mods tab keeps it per mesh).</summary>
    public event Action? ViewChanged;
    /// <summary>An animation was picked: its name (null: rest pose), and whether the user picked it (false: the preview
    /// restored or picked it itself).</summary>
    public event Action<string?, bool>? AnimationPicked;
    /// <summary>Playback paused or the frame slider let go (the Mods tab saves the animation and the frame).</summary>
    public event Action? PlaybackMoved;

    /// <param name="prefs">Where the look and playback switches are kept.</param>
    /// <param name="barButtons">The host's buttons on the playback bar, before Look ▾ (the Model tab's edits and Compare).</param>
    public Preview3D(IPreviewPrefs prefs, params Button[] barButtons)
    {
        this.prefs = prefs;
        BackColor = Color.Transparent;
        play = Ui.FlatButton("▶", TogglePlay, "Play or pause the animation (P; Esc pauses).");
        loop = Ui.FlatButton("⟳ Loop", () => { prefs.Loop = !prefs.Loop; prefs.Save(); Ui.Lit(loop!, prefs.Loop); }, "Loop the animation, or play it once and stop on its last frame. Remembered.");
        reset = Ui.FlatButton("Reset View", () => { if (ResetViewAction != null) ResetViewAction(); else view.ResetView(); }, "Frame the whole model again. Drag to turn, right-drag to move, wheel to zoom (Shift: finer), double-click to centre.");
        look = Ui.FlatButton("Look ▾", ShowLookMenu, "How the model is shown: Spec, Reflect, Glow, Bloom, Props, Power FXs and Ground on or off, and the Light, Glow and Lens sliders. Remembered.");
        (frameHead, frameBust, frameFull) = PreviewControls.FramingButtons(FrameShot, S);
        fullScreen = PreviewControls.FullScreenButton(ToggleFull, () => IsFull, S);
        powersButton = Ui.FlatButton("Power FXs", () => { prefs.Powers = !prefs.Powers; prefs.Save(); ApplyLook(); LoadEffects(); }, "Play the effects of the power an animation belongs to with it (lightning, shockwaves, trails …, read from the game's power packages). Lit when on; remembered.");
        Icons.Make(loop, "Loop", Icons.Loop, S);
        Icons.Make(look, "Look", Icons.Look, S);
        Icons.Make(reset, "Reset View", Icons.ResetView, S);
        Ui.Tip(anims, "The character's animations (from its base package, in the game's order). Rest Pose = the bind pose the mesh is built in. A power button below shows only that power's.");
        Ui.Tip(frame, "The animation's frame: drag to scrub (pauses).");
        Ui.Tip(light, "Light strength (double-click: 100 %). Remembered.");
        Ui.Tip(glowSlider, "How strong the glowing (emissive) parts are (double-click: 100 %). Remembered.");
        Ui.Tip(lens, "The camera's lens (35 mm equivalent): short is wide with strong perspective, long is flatter; the model stays the same size (double-click: 50 mm). Remembered.");
        Ui.Tip(effectsSlider, "How strong the power effects are drawn (their opacity and glow; 100 % = the game's values, which look much brighter here than in game). Raise it when an effect is too faint to see (double-click: 10 %). Remembered for every 3D preview.");
        effectsSlider.Value = PreviewViews.FxPower; effectsSlider.Home = () => PreviewViews.DefaultFxPower; view.EffectStrength = PreviewViews.FxPower;
        effectsSlider.ValueChanged += () => { view.EffectStrength = effectsSlider.Value; view.Invalidate(); };
        effectsSlider.Committed += () => PreviewViews.FxPower = effectsSlider.Value;
        Ui.Tip(fxLabel, "The power the animation belongs to and how many of its effects play.");
        fxLabel.ForeColor = Ui.Subtle;
        light.Value = prefs.Light; light.Home = () => LightHome?.Invoke() ?? 1; view.Brightness = prefs.Light;
        lens.Value = prefs.Lens; lens.Home = () => 50; view.FocalLength = prefs.Lens;
        glowSlider.Value = prefs.GlowStrength; glowSlider.Home = () => 1; view.GlowStrength = prefs.GlowStrength;
        light.ValueChanged += () => view.Brightness = light.Value;
        light.Committed += () => { prefs.Light = light.Value; prefs.Save(); };
        lens.ValueChanged += () => view.FocalLength = lens.Value;
        lens.Committed += () => { prefs.Lens = lens.Value; prefs.Save(); };
        glowSlider.ValueChanged += () => view.GlowStrength = glowSlider.Value;
        glowSlider.Committed += () => { prefs.GlowStrength = glowSlider.Value; prefs.Save(); };
        frame.ValueChanged += ScrubTo;
        frame.Committed += () => PlaybackMoved?.Invoke();
        view.ViewChanged += () => ViewChanged?.Invoke();
        frame.Enabled = false; frame.Format = _ => "–";
        anims.SelectedIndexChanged += (_, _) => AnimationChosen();
        Ui.Lit(loop, prefs.Loop);
        ApplyLook();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 2; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        // the power block's row: an exact height for its button rows (an auto-sized row measured them as one row and the
        // second was clipped; Kurt, 2026-10-12), set in FitPowerRow
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.Controls.Add(view, 0, 0);
        // one row (Kurt, 2026-10-04: the animation list was twice as wide as it needs; Look, Reset View, the framing and full
        // screen buttons beside it): the list takes what the buttons leave
        bar = Strip(anims, [play, loop, .. barButtons, look, reset, frameHead, frameBust, frameFull, fullScreen]);
        root.Controls.Add(bar, 0, 1);
        root.Controls.Add(frame, 0, 2);
        powerBlock = PowerBlock();
        root.Controls.Add(powerBlock, 0, 3);   // Power FXs + the power buttons
        rootTable = root;
        Controls.Add(root);
        root.SizeChanged += (_, _) => FitPowerRow();
    }

    static Control Strip(Control? fill, Control[] buttons)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = buttons.Length + 1, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < buttons.Length; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (fill != null) { fill.Dock = DockStyle.Fill; fill.Margin = new Padding(0, 2, 6, 0); t.Controls.Add(fill, 0, 0); }
        for (int i = 0; i < buttons.Length; i++) { buttons[i].Margin = new Padding(0, 0, 6, 0); t.Controls.Add(buttons[i], i + 1, 0); }
        return t;
    }

    /// <summary>The look switches, sliders and Loop read again (the Mods tab: another mod's light, lens and glow).</summary>
    public void ReloadPrefs()
    {
        light.Value = prefs.Light; view.Brightness = prefs.Light;
        lens.Value = prefs.Lens; view.FocalLength = prefs.Lens;
        glowSlider.Value = prefs.GlowStrength; view.GlowStrength = prefs.GlowStrength;
        effectsSlider.Value = PreviewViews.FxPower; view.EffectStrength = PreviewViews.FxPower;
        Ui.Lit(loop, prefs.Loop);
        ApplyLook();
    }

    // --- look -------------------------------------------------------------------------------------------------------------------
    /// <summary>The look switches into the view and the buttons that show them.</summary>
    void ApplyLook()
    {
        view.ShowSpec = prefs.Spec; view.ShowReflections = prefs.Reflect; view.ShowGlow = prefs.Glow; view.ShowBloom = prefs.Bloom; view.ShowGround = prefs.Ground;
        view.Quad = PreviewViews.Quad;
        Ui.Lit(powersButton, prefs.Powers);
    }

    /// <summary>Look ▾: the toggles (the menu stays open while toggling) and the Light / Glow / Lens sliders, then the host's.</summary>
    void ShowLookMenu()
    {
        if (lookMenu == null)
        {
            var items = PreviewControls.StandardLook(
                () => prefs.Spec, () => prefs.Spec = !prefs.Spec, () => prefs.Reflect, () => prefs.Reflect = !prefs.Reflect, () => prefs.Glow, () => prefs.Glow = !prefs.Glow,
                () => prefs.Bloom, () => prefs.Bloom = !prefs.Bloom, () => prefs.Props, () => { prefs.Props = !prefs.Props; LoadProps(); },
                () => prefs.Powers, () => { prefs.Powers = !prefs.Powers; LoadEffects(); }, () => prefs.Ground, () => prefs.Ground = !prefs.Ground);
            items.Add(new("Quad View", "Splits the view into four, as Blender's: this camera, then Top, Front and Side. Drag and zoom in any of them to move this camera; the others follow its zoom.", () => PreviewViews.Quad, () => PreviewViews.Quad = !PreviewViews.Quad));
            items.AddRange(ExtraLook);
            lookMenu = PreviewControls.LookMenu(items, [light, glowSlider, lens, effectsSlider], S, () => { prefs.Save(); ApplyLook(); });
        }
        Ui.ShowUnder(lookMenu, look);
    }

    // --- the view -----------------------------------------------------------------------------------------------------------------
    /// <summary>The 3D view (the host paints over it, sizes the model, picks sections).</summary>
    public ModelView View => view;
    /// <summary>The character's animator (posed as shown) and its mesh; null before a character is shown.</summary>
    public MeshAnimator? Animator => animator;
    public ModMeshes.Loaded? Mesh => shownLoaded;
    /// <summary>A character is shown (checks wait for it).</summary>
    public bool Ready => animator != null;

    /// <summary>A message in place of the model (loading, nothing picked); everything stops.</summary>
    public void ShowMessage(string text)
    {
        player?.Stop(); animator = null; shownLoaded = null; ctx = null;
        ClearEffects(); rig.Clear(); ClearPowers();
        view.Moving = false;
        view.ShowMessage(text);
    }

    /// <summary>
    /// Shows a character: its mesh posed by <paramref name="animator"/>, its animations (the one named
    /// <paramref name="keep"/> picked again, at <paramref name="keepAt"/> of its length, playing again with
    /// <paramref name="play"/>), its props and its hero's power buttons (read again unless <paramref name="samePowers"/>);
    /// the camera kept when <paramref name="keepView"/> is given.
    /// </summary>
    public void ShowCharacter(ModMeshes.Loaded mesh, MeshAnimator animator, List<AnimRef> animations, PreviewContext context, string? keep, float keepAt, bool play, float[]? keepView, bool samePowers)
    {
        player?.Stop();
        ctx = context;
        this.animator = animator;
        animator.Pose(null, 0);   // posed at once: a new animator's bones are all zero until its first pose (MEMM 0.37.37)
        if (!samePowers) { form = null; formAnims = []; formRules = []; formFiles = []; formCache.Clear(); formRequest++; untransformed = null; formIsModel = false; }   // (another character: its own forms)
        allAnims = WithForm(animations);
        if (!samePowers) ClearPowers();
        animList = powerFilter != null && heroPowers.FirstOrDefault(x => x.Prototype == powerFilter) is { } pw
            ? [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))] : allAnims;
        ShowMeshFramed(mesh);
        FillAnims();
        int pick = Math.Max(0, keep != null ? animList.FindIndex(a => a.Name == keep) + 1 : 0);
        int before = anims.SelectedIndex;
        quietPick = true;   // (the preview's own pick, not the user's)
        try
        {
            anims.SelectedIndex = pick;
            if (anims.SelectedIndex == before) AnimationChosen();   // same index: the list fires no change, load it anyway
        }
        finally { quietPick = false; }
        if (keepView != null) view.ViewState = keepView;
        if (pick > 0 && anim != null && seconds > 0)
        {
            playTime = keepAt * seconds;
            FxReplay(playTime);
            Pose(keepAt * frames);
            if (play) Player.Play();
        }
        LoadProps();
        if (animations.Count > 0 && (!samePowers || heroPowers.Count == 0)) LoadHeroPowers();
    }

    /// <summary>
    /// The shown character's animations, read after it (the Mods tab reads them in the background): the list filled, the
    /// hero's power buttons read, and <paramref name="select"/> picked at <paramref name="atSeconds"/> (restored, so not a
    /// user's pick), playing with <paramref name="play"/>.
    /// </summary>
    public void SetAnimations(List<AnimRef> animations, string? select, double? atSeconds, bool play)
    {
        allAnims = animList = WithForm(animations);
        powerFilter = null; effectOnly = null;
        FillAnims();
        LoadHeroPowers();
        int want = select == null ? -1 : animList.FindIndex(a => a.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
        if (want < 0) return;
        quietPick = true;
        try { anims.SelectedIndex = want + 1; } finally { quietPick = false; }
        if (atSeconds is double t && anim != null && seconds > 0) { playTime = Math.Clamp((float)t, 0, seconds); FxReplay(playTime); Pose(frames > 0 ? playTime / seconds * frames : 0); }
        if (play && anim != null) Player.Play();
    }
    bool quietPick;
    /// <summary>The next animation opens at this share of its length (PlayPower: compare powers at the same moment).</summary>
    double? keepFraction;

    /// <summary>Another mesh and animator for the same character (the Model tab's Compare): the animation, frame and camera kept.</summary>
    public void SwapCharacter(ModMeshes.Loaded mesh, MeshAnimator animator)
    {
        var keepView = view.ViewState;
        this.animator = animator;
        ShowMeshFramed(mesh);
        view.ViewState = keepView;
        RebuildRig();
        Pose(frames > 0 ? frame.Value * frames : 0);
    }

    void ShowMeshFramed(ModMeshes.Loaded mesh)
    {
        shownLoaded = mesh;
        view.ShowMesh(Display(mesh));
        MeshShown?.Invoke(mesh);
    }

    ModMeshes.Loaded Display(ModMeshes.Loaded l) => DisplayMesh?.Invoke(l) ?? l;

    /// <summary>The character drawn again (weights on / off, another bone picked), keeping the camera and the pose.</summary>
    public void RefreshDisplay()
    {
        if (shownLoaded == null || animator == null) return;
        var keep = view.ViewState;
        if (rig.Count > 0) RebuildRig();
        else { view.ShowMesh(Display(shownLoaded)); view.ViewState = keep; ShowPose(); }
        view.ViewState = keep;
    }

    // --- animations ---------------------------------------------------------------------------------------------------------------
    /// <summary>The animation picked in the drop-down (null = rest pose).</summary>
    public string? CurrentAnimation => anims.SelectedIndex > 0 ? anims.SelectedItem as string : null;
    /// <summary>Every animation of the shown character (not only the power filter's).</summary>
    public IReadOnlyList<AnimRef> AllAnimations => allAnims;
    public bool IsPlaying => player is { Paused: false };
    /// <summary>Where the animation is, as a share of its length.</summary>
    public float Fraction => seconds > 0 ? Math.Clamp(playTime / seconds, 0, 1) : 0;
    /// <summary>The animation and where in it (checks): "name @ 0.35".</summary>
    public string PlaybackState => $"{anims.SelectedItem} @ {frame.Value:0.00}";

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

    /// <summary>The animation read again (an edit imported or reverted), at the same place in it.</summary>
    public void ReloadAnimation()
    {
        float at = Fraction;
        bool wasPlaying = IsPlaying;
        int sel = anims.SelectedIndex;
        FillAnims();
        quietPick = true;
        try { anims.SelectedIndex = Math.Min(sel, anims.Items.Count - 1); AnimationChosen(); } finally { quietPick = false; }
        if (anim != null && seconds > 0) { playTime = at * seconds; FxReplay(playTime); Pose(at * frames); if (wasPlaying) TogglePlay(); }
    }

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

    void AnimationChosen()
    {
        Player.Stop();
        int i = anims.SelectedIndex - 1;
        BoneAnimation? a = null;
        if (i >= 0 && i < animList.Count)
        {
            a = ModAnimations.Load(animList[i]);
            a = TransformAnimation != null ? TransformAnimation(animList[i], a) : a;
        }
        Player.Set(a, keepFraction is double kf && a != null ? kf * MeshAnimator.Span(a).Seconds : 0);
        keepFraction = null;
        frame.Enabled = a != null;
        float fr = frames; frame.Format = v => fr > 0 ? $"{v * fr:0} / {fr:0}" : "–";
        rig.SetParentAnimation(a);
        LoadEffects();
        LoadPropSwitches();
        Pose(frames > 0 && seconds > 0 ? playTime / seconds * frames : 0);
        RefreshPowerButtons();
        AnimationPicked?.Invoke(CurrentAnimation, !quietPick);
        if (autoPlay) { autoPlay = false; if (a != null) TogglePlay(); }
    }

    /// <summary>Picks an animation by name (the editor's Animations tab): every animation back in the drop-down, that one
    /// selected and, with <paramref name="play"/>, playing. False when the shown character has no animation of that name.</summary>
    public bool PlayAnimation(string name, bool play = true)
    {
        if (powerFilter != null || animList.Count != allAnims.Count) { powerFilter = null; effectOnly = null; animList = allAnims; FillAnims(); RefreshPowerButtons(); }
        int i = animList.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        autoPlay = play;
        if (anims.SelectedIndex == i + 1) AnimationChosen();   // (the same one again: from the start)
        else anims.SelectedIndex = i + 1;
        return true;
    }

    /// <summary>Plays an animation from anywhere on the shown character (the Animations tab's picker: try another character's
    /// before using it). It shows at the top of the drop-down until the character's own list is shown again.</summary>
    public bool PlayRef(AnimRef a, bool play = true)
    {
        powerFilter = null; effectOnly = null;
        animList = [a, .. allAnims.Where(x => !ReferenceEquals(x, a))];
        FillAnims();
        autoPlay = play;
        if (anims.SelectedIndex == 1) AnimationChosen(); else anims.SelectedIndex = 1;
        return true;
    }

    /// <summary>The animation playing (null: rest pose), where it is (seconds), and its length.</summary>
    public BoneAnimation? Playing => anim;
    public double Time { get => playTime; set { if (anim == null) return; playTime = (float)Math.Clamp(value, 0, seconds); FxReplay(playTime); Pose(frames > 0 && seconds > 0 ? playTime / seconds * frames : 0); } }
    public float Seconds => seconds;
    public float Frames => frames;
    /// <summary>Pauses (holding the frame) or plays on.</summary>
    public void Pause() => player?.Pause();
    public void Play() { if (anim != null) TogglePlayIfPaused(); }
    void TogglePlayIfPaused() { if (!IsPlaying) TogglePlay(); }

    // --- playback -----------------------------------------------------------------------------------------------------------------
    void TogglePlay()
    {
        if (anim == null || animator == null) return;
        if (IsPlaying) { Player.Pause(); return; }
        if (playTime >= seconds) { playTime = 0; FxReplay(0); }
        Player.Play();
    }

    /// <summary>P (Kurt, 2026-10-04): plays or pauses the picked animation; false when none is picked.</summary>
    public bool TogglePlayback()
    {
        if (anim == null || animator == null) return false;
        TogglePlay();
        // the arrow keys then scrub, a frame at a time (Kurt, 2026-10-06): the frame slider takes the keyboard
        frame.ArrowStep = frames > 0 ? 1f / frames : 0;
        if (frame is { Visible: true, Enabled: true }) frame.Focus();
        return true;
    }

    internal void TogglePlayForTest() => TogglePlay();

    /// <summary>Esc: pauses a playing animation; false when nothing was playing.</summary>
    public bool PausePlayback()
    {
        if (!IsPlaying) return false;
        TogglePlay();
        return true;
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
        Player.Scrub(frame.Value * seconds);
        FxReplay(playTime);
        Pose(frame.Value * frames);
    }

    // --- framing and full screen ----------------------------------------------------------------------------------------------
    /// <summary>The whole character, centred (a power picked elsewhere: the editor's Powers tab).</summary>
    public void FrameFull() => FrameShot(Framing.Shot.Full);

    /// <summary>A framing button: aims the camera (head centred).</summary>
    void FrameShot(Framing.Shot shot)
    {
        if (animator == null) return;
        Framing.Apply(view, animator, anim != null, shot, centerHead: true);
        if (anim == null) animator.Pose(null, 0);
        ShowPose();
        // a transform whose props are the body (Rocket's mech suit around its pilot): the whole of it, not the pilot inside
        // summons beside the hero (PreviewEffects.SummonSpots): the hero and the spots they stand on, about a body's height around
        if (shot == Framing.Shot.Full && effects?.SummonCount is int sc && sc > 0 && untransformed == null && animator.Positions.Length > 0)
        {
            var clo = animator.Positions.Aggregate(System.Numerics.Vector3.Min); var chi = animator.Positions.Aggregate(System.Numerics.Vector3.Max);
            var (slo, shi) = PreviewEffects.SpotsBox(sc);
            var lo = System.Numerics.Vector3.Min(clo, new System.Numerics.Vector3(slo.X, slo.Y, clo.Z)); var hi = System.Numerics.Vector3.Max(chi, new System.Numerics.Vector3(shi.X, shi.Y, chi.Z));
            float size = Math.Max(hi.Z - lo.Z, Math.Max(hi.X - lo.X, hi.Y - lo.Y));
            view.Aim((lo + hi) * 0.5f, size * 1.25f * view.FocalLength / ModelView.ReferenceFocalLength, 0, 0.12f);
        }
        else if (shot == Framing.Shot.Full && untransformed != null && rig.Bounds() is { } b && animator.Positions.Length > 0)
        {
            var clo = animator.Positions.Aggregate(System.Numerics.Vector3.Min); var chi = animator.Positions.Aggregate(System.Numerics.Vector3.Max);
            float own = Math.Max(1f, Math.Max(chi.Z - clo.Z, Math.Max(chi.X - clo.X, chi.Y - clo.Y)));
            float size = Math.Max(b.Hi.Z - b.Lo.Z, Math.Max(b.Hi.X - b.Lo.X, b.Hi.Y - b.Lo.Y));
            if (size > own * 1.25f) view.Aim((b.Lo + b.Hi) * 0.5f, size * 1.42f * view.FocalLength / ModelView.ReferenceFocalLength, 0, 0.08f);
        }
        ViewChanged?.Invoke();   // (kept as a drag's view is)
    }

    readonly PreviewControls.FullScreen full = new();

    /// <summary>Into or out of full screen: this control moves into a borderless window on its monitor and back (F11).</summary>
    public void ToggleFull()
    {
        bool goingFull = !full.IsFull;
        if (goingFull && powerHost != null) AttachPowerBlock(null);   // (the power buttons come along)
        full.Toggle(this, FullTitle, PausePlayback, () => TogglePlayback(), view, new Padding(10), () => fullScreen.Invalidate());
        if (!goingFull && wantedHost != null) AttachPowerBlock(wantedHost);
    }

    /// <summary>The full screen window's title.</summary>
    public string FullTitle { get; set; } = "Preview";

    public bool IsFull => full.IsFull;

    // --- the power block's place ---------------------------------------------------------------------------------------------------
    /// <summary>
    /// Puts the power block (Power FXs, the caption, the power buttons) into <paramref name="host"/> (docked to fill it), or
    /// back under the frame slider (null). The Mods tab keeps its caption and pictures between the view and the powers.
    /// </summary>
    public void PlacePowerBlock(Control? host) { wantedHost = host; if (!full.IsFull) AttachPowerBlock(host); }

    void AttachPowerBlock(Control? host)
    {
        if (powerHost == host) return;
        powerBlock.Parent?.Controls.Remove(powerBlock);
        if (host == null) rootTable.Controls.Add(powerBlock, 0, 3);
        else { powerBlock.Dock = DockStyle.Fill; host.Controls.Add(powerBlock); }
        powerHost = host;
        FitPowerRow();
    }

    /// <summary>The power block shown (off in the editor's Powers and Animations tabs: their own lists pick).</summary>
    public bool ShowPowerBlock { get => powerBlock.Visible; set { powerBlock.Visible = value; FitPowerRow(); } }

    /// <summary>The power block's row in this control: as tall as its button rows at the width it has (none when the host
    /// holds the block, or it's hidden).</summary>
    void FitPowerRow()
    {
        if (rootTable == null || rootTable.RowStyles.Count < 4) return;
        float h = powerHost == null && powerBlock.Visible ? PowerBlockHeight(Math.Max(1, rootTable.ClientSize.Width - powerBlock.Margin.Horizontal)) : 0;
        var rs = rootTable.RowStyles[3];
        if (rs.SizeType != SizeType.Absolute || Math.Abs(rs.Height - h) > 0.5f) { rs.SizeType = SizeType.Absolute; rs.Height = h; }
    }
    /// <summary>The power block's height as laid out (the host sizes its place for it).</summary>
    public int PowerBlockHeight(int width)
    {
        // (measured part by part: the table's own preferred size measured the button rows at an unbounded width, one row,
        // and the Mods tab clipped the second; Kurt, 2026-10-12)
        int head = Math.Max(powersButton.Height + powersButton.Margin.Vertical, fxLabel.PreferredHeight + fxLabel.Margin.Vertical);
        int rows = powerStrip.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height;
        return powerBlock.Margin.Vertical + head + rows + (int)(4 * S);
    }

    // --- checks ---------------------------------------------------------------------------------------------------------------------
    /// <summary>Checks: the controls as a user clicks them.</summary>
    internal Button PlayButton => play;
    internal Button ResetButton => reset;
    internal LightSlider FrameSlider => frame;
    internal LightSlider LightSlider => light;
    internal DropDown AnimationBox => anims;
    internal bool Paused => !IsPlaying;
    /// <summary>The power line under the view (checks).</summary>
    public string EffectsNote => fxLabel.Text + $" · props {rig.Count}";
    /// <summary>The material swap drawn now (checks): "n of m section(s)", else "none".</summary>
    public string SwapNote => view.LookOverride is { } lo ? $"{lo.Count(x => x != null)} of {lo.Length} section(s)" : "none";
    /// <summary>The view's picture (checks).</summary>
    public Bitmap ViewShot() { var b = new Bitmap(Math.Max(1, view.Width), Math.Max(1, view.Height)); view.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); return b; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            player?.Dispose(); effects?.Dispose(); full.Dispose(); lookMenu?.Dispose();
            // the Look menu's sliders live in that menu, made only when it first opens: without it they were never disposed,
            // and their tips kept this preview (and the editor around it) in memory
            foreach (var sl in new Control[] { light, glowSlider, lens, effectsSlider }) if (!sl.IsDisposed) sl.Dispose();
        }
        base.Dispose(disposing);
    }
}
