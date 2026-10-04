using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's 3D view: loading a mesh, the animation list and playback, the Look menu, framing and full screen.</summary>
sealed partial class StorePreview
{
    /// <summary>Shows meshes[meshIndex] in the 3D view (read in the background; the last few are kept).</summary>
    void LoadMesh()
    {
        if (mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        if (viewer == null)
        {
            viewer = new ModelView { Background = Ui.Card, BackColor = Ui.Card, Visible = false };
            viewer.ViewChanged += () => { if (mod != null && meshIndex >= 0 && meshIndex < meshes.Count) PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState); };
            Controls.Add(viewer);
        }
        var r = meshes[meshIndex];
        viewer.Visible = true;
        SaveAnim();
        shownMesh = null;
        StopAnimation(); ClearEffects(); anims = []; animator = null; FillAnims();
        if (mod != null) { float lv = PreviewViews.Light(mod); viewer.Brightness = lv; if (lightSlider != null) lightSlider.Value = lv; }   // this mod's light
        if (mod != null) { float fl = PreviewViews.Lens(mod); viewer.FocalLength = fl; if (lensSlider != null) lensSlider.Value = fl; }    // and lens
        if (mod != null) { float gl = PreviewViews.GlowStrength(mod); viewer.GlowStrength = gl; if (glowSlider != null) glowSlider.Value = gl; }    // and glow
        Invalidate();
        if (meshCache.TryGetValue(r.File + "|" + r.Export + "|" + Ui.FileStamp(r.File), out var hit) && hit != null) { Show(hit); return; }
        viewer.ShowMessage("Loading the 3D view…");
        int req = ++request;
        string? cooked = CookedFolder;
        string key = r.File + "|" + r.Export + "|" + Ui.FileStamp(r.File);
        Task.Run(() => { try { var l = ModMeshes.Load(r, cooked, out string why); return (l, why); } catch (Exception ex) { return ((ModMeshes.Loaded?)null, ex.Message); } }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || viewer == null) return;
            var (loaded, why) = t.Result;
            if (meshCache.Count > 3) meshCache.Clear();   // with their mipmapped maps, a few are enough
            meshCache[key] = loaded;
            if (loaded == null) viewer.ShowMessage($"{r.Name} can't be shown in 3D ({why}).");
            else Show(loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        void Show(ModMeshes.Loaded l)
        {
            viewer!.ShowMesh(l);
            if (mod != null && StartView(r) is { } saved) viewer.ViewState = saved;
            shownMesh = r.Key;
            shownLoaded = l;
            rig.Clear();
            animator = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
            // Posed at once (rest): a new animator's positions and bone matrices are all zero until its first pose, and the
            // props redraw "the last pose" when they arrive; with no animation restored (Unworthy Thor, Thor Infinity War)
            // the whole model collapsed to a point and vanished.
            animator.Pose(null, 0);
            LoadProps();
            var m = mod; string? cooked2 = CookedFolder;
            var pkgs = m == null ? [] : m.Manifest.UpkReplacements.Select(f => (f, Path.Combine(m.Folder, f))).ToList();
            int req2 = request;
            Task.Run(() => { try { return ModAnimations.For(r, l.Bones, pkgs, cooked2); } catch { return []; } }).ContinueWith(t =>
            {
                if (IsDisposed || req2 != request || mod?.FolderName != m?.FolderName) return;   // (a reload of the same mod is a new object)
                anims = allAnims = t.Result;
                FillAnims();
                AnimationsLoaded?.Invoke();
                LoadHeroPowers();
                // This PC's last animation and frame for the mesh, else the pick's "@animation".
                var saved = mod == null ? null : PreviewViews.GetAnim(PreviewViews.Key(mod, r));
                string? name = saved?.Name ?? wantedAnim;
                int want = name == null ? -1 : anims.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (want >= 0 && animBox != null) { restoreTime = saved != null && want >= 0 && anims[want].Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase) ? saved.Time : null; animBox.SelectedIndex = want + 1; }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>The animation drop-down: "Rest Pose", then the mesh's animations (shown under the 3D caption).</summary>
    void FillAnims()
    {
        if (animBox == null)
        {
            animBox = new DropDown { Font = Ui.Regular(9f), Visible = false, MaxDropDownItems = 24 };
            Ui.Tip(animBox, "Play one of this mesh's animations in the 3D view (it loops). Remembered with the mesh for this mod.");
            animBox.SelectedIndexChanged += (_, _) => { if (!fillingAnims) PlaySelected(); };
            Controls.Add(animBox);
            playBtn = Ui.FlatButton("▶", TogglePlay, "Play or pause the animation (it loads paused on its first frame).");
            loopBtn = Ui.FlatButton("⟳ Loop", () => { PreviewViews.Loop = !PreviewViews.Loop; UpdateButtons(); }, "Loop the animation, or play it once and stop on its last frame. Remembered.");
            restBtn = Ui.FlatButton("Reset View", ResetView, "Back to the mod's own view of the model, or the default one (your turned, zoomed or panned view is saved per mesh; this forgets it).");
            Icons.Make(loopBtn, "Loop", Icons.Loop, S); Icons.Make(restBtn, "Reset View", Icons.ResetView, S);
            foreach (var b in new[] { playBtn, loopBtn, restBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
            powersBtn = Ui.FlatButton("Power FXs", () => { PreviewViews.Powers = !PreviewViews.Powers; ApplyShading(); LoadEffects(); }, "Play the effects of the power an animation belongs to with it (lightning, shockwaves, trails …, read from the game's or the mod's power packages). Lit when on; remembered on this PC.");
            powersBtn.AutoSize = false; powersBtn.Padding = new Padding(0); powersBtn.Visible = false; Controls.Add(powersBtn);
            frameFullBtn = Ui.FlatButton("Full Body", () => FrameShot(Framing.Shot.Full), "Frame the whole character (as Create from 3D does). Saved as this mesh's view.");
            frameHeadBtn = Ui.FlatButton("Head", () => FrameShot(Framing.Shot.HeadShoulders), "Frame the head and shoulders. Saved as this mesh's view.");
            frameBustBtn = Ui.FlatButton("Bust", () => FrameShot(Framing.Shot.Bust), "Frame head and chest. Saved as this mesh's view.");
            foreach (var b in new[] { frameFullBtn, frameHeadBtn, frameBustBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
            Ui.IconPainters.AddOrUpdate(frameFullBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Full));
            Ui.IconPainters.AddOrUpdate(frameHeadBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.HeadShoulders));
            Ui.IconPainters.AddOrUpdate(frameBustBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Bust));
            fullBtn = Ui.FlatButton("⛶", ToggleFull, "Full screen: the 3D view fills the screen with its controls beside it (F11). Esc, F11 or ✕ come back.");
            fullBtn.AutoSize = false; fullBtn.Padding = new Padding(0); fullBtn.Visible = false; Controls.Add(fullBtn);
            Ui.IconPainters.AddOrUpdate(fullBtn, (g, r, c) => FullScreenIcon(g, r, c, IsFull));
            // Frame (Kurt: like the icon maker's): where the animation is; dragging it pauses and scrubs.
            frameSlider = new LightSlider { Visible = false, Label = "Frame", Min = 0, Max = 1, Step = 1, Mark = null, Enabled = false, Home = () => 0, Format = v => playing == null ? "—" : $"{v:0} / {playFrames:0}" };
            frameSlider.ValueChanged += ScrubTo;
            frameSlider.Committed += SaveAnim;
            Ui.Tip(frameSlider, "The animation's frame: drag to scrub through it (it pauses), or use the arrow keys (Shift: 5 frames, Home / End: first / last). ▶ plays on from there.");
            Controls.Add(frameSlider);
            lightSlider = new LightSlider { Visible = false, Min = 0f, Home = () => mod == null ? 1f : PreviewViews.AuthorLight(mod) };
            lightSlider.ValueChanged += () => { if (viewer != null) viewer.Brightness = lightSlider.Value; };
            lightSlider.Committed += () => { if (mod != null) PreviewViews.SetLight(mod, lightSlider.Value); };
            Ui.Tip(lightSlider, "Light brightness in the 3D view for this mod (drag, or the mouse wheel; double-click: back to the mod's own level, else 100%). Remembered per mod on this PC; Export can put it into the mod.");
            Controls.Add(lightSlider);
            lensSlider = new LightSlider { Visible = false, Label = "Lens", Min = 15, Max = 200, Step = 0.5f, Mark = ModelView.DefaultFocalLength, Format = v => $"{v:0} mm", Home = () => ModelView.DefaultFocalLength };
            lensSlider.ValueChanged += () => { if (viewer != null) viewer.FocalLength = lensSlider.Value; };
            lensSlider.Committed += () => { if (mod != null) PreviewViews.SetLens(mod, lensSlider.Value); };
            Ui.Tip(lensSlider, "The camera's lens (35 mm equivalent): short is wide with strong perspective, long is flatter; the model stays the same size (double-click: 50 mm). Remembered per mod on this PC.");
            glowSlider = new LightSlider { Visible = false, Label = "Glow", Min = 0f, Max = 2f, Home = () => 1f };
            glowSlider.Value = mod == null ? 1f : PreviewViews.GlowStrength(mod);
            glowSlider.ValueChanged += () => { if (viewer != null) viewer.GlowStrength = glowSlider.Value; };
            glowSlider.Committed += () => { if (mod != null) PreviewViews.SetGlowStrength(mod, glowSlider.Value); };
            Ui.Tip(glowSlider, "How strong the glowing (emissive) parts are in the 3D view: 100 % is the materials' own (double-click: back to 100 %). Remembered per mod on this PC.");
            Controls.Remove(lightSlider);   // all three live in the Look ▾ menu
            // The playback bar over the view's bottom (shown while the mouse is over the view).
            playBar = new Panel { Visible = false, BackColor = Color.FromArgb(24, 26, 34) };
            foreach (Control c in new Control[] { frameSlider, animBox, playBtn, loopBtn }) { playBar.Controls.Add(c); c.Visible = true; }
            // Look, Reset View, the framing buttons and ⛶ on the animation's row (Kurt, 2026-10-04: the list was twice as wide
            // as it needs): moved into the bar once Look ▾ is made (below)
            Controls.Add(playBar);
            lookBtn = Ui.FlatButton("Look ▾", ShowLookMenu, "How the model is shown: Spec, Reflect, Glow and Props on or off, and the Light and Lens sliders. Remembered on this PC.");
            Icons.Make(lookBtn, "Look", Icons.Look, S);
            foreach (var b in new Control?[] { lookBtn, restBtn, frameHeadBtn, frameBustBtn, frameFullBtn, fullBtn }) if (b != null) { b.Parent?.Controls.Remove(b); playBar.Controls.Add(b); b.Visible = true; }
        }
        fillingAnims = true;
        animBox.BeginUpdate();
        animBox.Items.Clear();
        string? filterName = powerFilter == null ? null : heroPowers.FirstOrDefault(p => p.Prototype == powerFilter)?.Name;
        animBox.Items.Add(filterName != null ? $"Rest Pose  ·  {anims.Count} of {allAnims.Count} animations ({filterName})"
            : anims.Count > 0 ? $"Rest Pose  ·  {anims.Count} animations" : show3D ? "Rest Pose  ·  (looking for animations…)" : "Rest Pose");
        foreach (var a in anims) animBox.Items.Add(a.Name);
        animBox.SelectedIndex = 0;
        animBox.EndUpdate();
        fillingAnims = false;
        animBox.Enabled = anims.Count > 0;
        UpdateButtons();
        Invalidate();
    }

    /// <summary>The buttons' state: play shows ▶ or ❚❚; loop is filled (accent) when on; both need an animation.</summary>
    void UpdateButtons()
    {
        ApplyShading();
        if (viewer != null) viewer.Moving = playing != null && !paused;   // smaller frames while playing (ModelView.Moving)
        if (playBtn == null || loopBtn == null) return;
        bool has = playing != null;
        playBtn.Text = has && !paused ? "❚❚" : "▶";
        playBtn.Enabled = has; loopBtn.Enabled = has;
        bool on = PreviewViews.Loop;
        loopBtn.Tag = on ? "accent" : "flat";
        loopBtn.BackColor = on ? Ui.Accent : Ui.Bar; loopBtn.ForeColor = on ? Color.White : Ui.Text;
        loopBtn.FlatAppearance.BorderColor = on ? Ui.Accent : Ui.Line;
        loopBtn.FlatAppearance.MouseOverBackColor = on ? Ui.AccentHover : Ui.CardHover;
        loopBtn.Invalidate(); playBtn.Invalidate();
    }

    /// <summary>The Spec / Reflect / Glow toggles into the 3D view, and their look (accent when on, like Loop).</summary>
    void ApplyShading()
    {
        if (viewer != null) { viewer.ShowSpec = PreviewViews.Spec; viewer.ShowReflections = PreviewViews.Reflect; viewer.ShowGlow = PreviewViews.Glow; viewer.ShowBloom = PreviewViews.Bloom; }
        if (lookMenu != null)
            foreach (ToolStripItem it in lookMenu.Items)
                if (it is ToolStripMenuItem mi)
                    mi.Checked = mi.Text switch { "Spec" => PreviewViews.Spec, "Reflect" => PreviewViews.Reflect, "Glow" => PreviewViews.Glow, "Props" => PreviewViews.Props, "Powers" => PreviewViews.Powers, _ => mi.Checked };
        if (powersBtn != null) Ui.Lit(powersBtn, PreviewViews.Powers);
    }

    void TogglePlay()
    {
        if (playing == null) return;
        if (paused && !PreviewViews.Loop && playTime >= playSeconds) playTime = 0;   // played once to the end: start over
        paused = !paused;
        if (!paused) { lastTick = playClock.Elapsed.TotalSeconds; playClock.Start(); if (!playTimer.Enabled) { playTimer.Tick -= PlayTick; playTimer.Tick += PlayTick; playTimer.Start(); } }
        else playTimer.Stop();
        UpdateButtons();
    }

    /// <summary>The view a mesh starts from: the one turned to on this PC, else the mod author's (manifest PreviewViews).</summary>
    float[]? StartView(MeshRef r) =>
        mod == null ? null : PreviewViews.Get(PreviewViews.Key(mod, r)) ?? (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var v) ? v : null);

    /// <summary>Back to the mod author's view when it has one, else the default framing; this PC's view is forgotten.</summary>
    void ResetView()
    {
        if (viewer == null || mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        var r = meshes[meshIndex];
        PreviewViews.Set(PreviewViews.Key(mod, r), null);
        if (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var author)) viewer.ViewState = author;
        else viewer.ResetView();
    }

    void PlaySelected()
    {
        if (animBox == null || mod == null || animator == null || viewer == null) return;
        int i = animBox.SelectedIndex - 1;
        StopAnimation();
        if (i < 0 || i >= anims.Count)
        {
            ClearEffects();
            animator.Pose(null, 0); ShowPose();
            ShowFrame(0);
            if (!fillingAnims && MeshOk) { Picked?.Invoke(mod, meshes[meshIndex].Key); SaveAnim(); }
            return;
        }
        var a = anims[i];
        int req = request;
        Task.Run(() => ModAnimations.Load(a)).ContinueWith(t =>
        {
            if (IsDisposed || req != request || animBox == null || animBox.SelectedIndex - 1 != i || t.Result == null) return;
            playing = t.Result;
            (playFrames, playSeconds) = MeshAnimator.Span(playing);
            if (mod != null && MeshOk && StartView(meshes[meshIndex]) == null) viewer.ZoomOut(1.25f);   // room for reaching and lunging
            paused = true;
            bool restoring = restoreTime != null;
            playTime = Math.Clamp(restoreTime ?? (keepFraction is double kf ? kf * playSeconds : 0), 0, playSeconds); restoreTime = null; keepFraction = null;
            rig.SetParentAnimation(playing);
            animator.Pose(playing, playSeconds > 0 ? (float)(playTime / playSeconds * playFrames) : 0); ShowPose();
            ShowFrame(playSeconds > 0 ? (float)(playTime / playSeconds * playFrames) : 0);
            UpdateButtons();
            LoadEffects();
            LoadPropSwitches();
            if (autoPlay) { autoPlay = false; if (paused) TogglePlay(); }
            // A restored animation is shown as it was left, not a new pick (no undo step, the preview choice unchanged).
            if (mod != null && MeshOk && !restoring) Picked?.Invoke(mod, CurrentMeshKey());
            SaveAnim();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The shown mesh's animations (all of them, also while a power button filters the drop-down).</summary>
    public IReadOnlyList<AnimRef> AllAnimations => allAnims;
    /// <summary>The shown mesh's animations are loaded (AllAnimations).</summary>
    public event Action? AnimationsLoaded;

    /// <summary>Picks an animation by name (the editor's Animations tab): every animation back in the drop-down, that one
    /// selected and, with <paramref name="play"/>, playing. False when the shown mesh has no animation of that name.</summary>
    public bool PlayAnimation(string name, bool play = true)
    {
        if (animBox == null) return false;
        if (powerFilter != null || anims.Count != allAnims.Count) { powerFilter = null; anims = allAnims; FillAnims(); }
        int i = anims.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        autoPlay = play;
        if (animBox.SelectedIndex == i + 1) PlaySelected();   // (the same one again: from the start)
        else animBox.SelectedIndex = i + 1;
        return true;
    }

    /// <summary>Plays an animation from anywhere on the shown model (the Animations tab's picker: try another character's
    /// before using it). It shows at the top of the drop-down until the mesh's own list is shown again.</summary>
    public bool PlayRef(AnimRef a, bool play = true)
    {
        if (animBox == null) return false;
        powerFilter = null;
        anims = [a, .. allAnims.Where(x => !ReferenceEquals(x, a))];
        FillAnims();
        autoPlay = play;
        if (animBox.SelectedIndex == 1) PlaySelected(); else animBox.SelectedIndex = 1;
        return true;
    }

    /// <summary>The frame slider moved by the user: pause and show that frame.</summary>
    void ScrubTo()
    {
        if (settingFrame || frameSlider == null || playing == null || animator == null || viewer == null || playFrames <= 0) return;
        if (!paused) { playTimer.Stop(); playClock.Reset(); paused = true; UpdateButtons(); }
        playTime = frameSlider.Value / playFrames * playSeconds;
        FxReplay(playTime);
        animator.Pose(playing, frameSlider.Value);
        ShowPose();
    }

    /// <summary>The frame slider follows the animation (set without scrubbing).</summary>
    void ShowFrame(float frame)
    {
        if (frameSlider == null) return;
        settingFrame = true;
        frameSlider.Enabled = playing != null;
        frameSlider.Max = Math.Max(1, playFrames);
        frameSlider.Value = playing == null ? 0 : Math.Clamp(frame, 0, Math.Max(1, playFrames));
        settingFrame = false;
        frameSlider.Invalidate();
    }

    void PlayTick(object? sender, EventArgs e)
    {
        if (playing == null || animator == null || viewer == null) { StopAnimation(); return; }
        if (!show3D || !Visible) { Pause(); return; }   // hidden: hold the frame
        if (paused) return;
        double now = playClock.Elapsed.TotalSeconds;
        playTime += now - lastTick; lastTick = now;
        float frame;
        if (playSeconds <= 0 || playFrames <= 0) frame = 0;
        else if (PreviewViews.Loop) frame = (float)(playTime % playSeconds / playSeconds * playFrames);
        else if (playTime >= playSeconds) { playTime = playSeconds; frame = playFrames; paused = true; playTimer.Stop(); UpdateButtons(); }
        else frame = (float)(playTime / playSeconds * playFrames);
        animator.Pose(playing, frame);
        FxAdvance(PreviewViews.Loop && playSeconds > 0 ? playTime % playSeconds : playTime, ended: !PreviewViews.Loop && playTime >= playSeconds);
        ShowPose();
        ShowFrame(frame);
    }

    /// <summary>A picture shows: the 3D view is hidden but kept (mesh, animation paused where it was, camera).</summary>
    void Hide3D()
    {
        if (viewer != null) viewer.Visible = false;
        if (playing != null && !paused) resumeOnReveal = true;
        Pause();
        HideAnimControls();
    }
    bool resumeOnReveal;

    /// <summary>Back to 3D on the mesh the view still holds: shown as it was left. False when it has to be loaded.</summary>
    bool Reveal3D()
    {
        if (viewer == null || !MeshOk || shownMesh != meshes[meshIndex].Key) return false;
        viewer.Visible = true;
        UpdateButtons();
        if (resumeOnReveal && playing != null && paused) TogglePlay();   // it was playing: carry on
        resumeOnReveal = false;
        Invalidate();
        return true;
    }

    void Pause() { playTimer.Stop(); playClock.Reset(); paused = true; UpdateButtons(); SaveAnim(); }

    /// <summary>Esc (Kurt): pauses a playing animation; false when nothing was playing (Esc then does its usual job).</summary>
    public bool PausePlayback()
    {
        if (playing == null || paused || !show3D) return false;
        Pause();
        return true;
    }

    /// <summary>Stores the shown mesh's animation and the frame it's on (preview_views.json), so it comes back as it was.</summary>
    void SaveAnim()
    {
        if (mod == null || shownMesh == null || animBox == null || fillingAnims) return;
        int i = animBox.SelectedIndex - 1;
        if (i >= 0 && playing == null) return;   // still loading
        double t = playSeconds <= 0 ? 0 : PreviewViews.Loop ? playTime % playSeconds : Math.Min(playTime, playSeconds);   // a loop's clock runs on
        PreviewViews.SetAnim(PreviewViews.Key(mod, shownMesh), i >= 0 && i < anims.Count ? anims[i].Name : null, t);
    }

    void HideAnimControls()
    {
        // (Not the playback bar's own controls: hiding the bar hides them, and nothing showed them again: Kurt's
        // "animation filter and scrub bar keep disappearing".)
        foreach (Control? c in new Control?[] { restBtn, lightSlider, lensSlider, glowSlider, powersBtn, fullBtn, frameFullBtn, frameHeadBtn, frameBustBtn, playBar, lookBtn }) if (c != null) c.Visible = false;
        if (IsFull) ToggleFull();   // a picture (or another mod without 3D) shows: back from full screen
    }

    /// <summary>The pose last made by the animator, with the props on their bones.</summary>
    void ShowPose()
    {
        if (viewer == null || animator == null) return;
        if (fxPlayer != null) { viewer.Effects = fxPlayer.Quads(); viewer.EffectTris = fxPlayer.Tris(); }
        else if (viewer.Effects.Count > 0 || viewer.EffectTris.Count > 0) { viewer.Effects = []; viewer.EffectTris = []; }
        // Props shown at this moment of the animation (a power's rules have their times).
        rig.At(playing == null || playSeconds <= 0 ? 0 : PreviewViews.Loop ? playTime % playSeconds : Math.Min(playTime, playSeconds));
        rig.Update(viewer, animator);
    }

    /// <summary>A framing button's icon: a figure cropped as the shot frames it (whole body, head and shoulders, head and chest).</summary>
    static void PersonIcon(Graphics g, Rectangle r, Color c, Framing.Shot shot)
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

    /// <summary>Look ▾: the look toggles and the Light / Lens sliders; stays open while toggling.</summary>
    void ShowLookMenu()
    {
        if (lookBtn == null || lightSlider == null || lensSlider == null || glowSlider == null) return;
        if (lookMenu == null)
        {
            lookMenu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            ToolStripMenuItem Item(string text, string tip, Func<bool> get, Action set)
            {
                var it = new ToolStripMenuItem(text) { ToolTipText = tip };
                it.Click += (_, _) => { set(); it.Checked = get(); ApplyShading(); };
                lookMenu.Opening += (_, _) => it.Checked = get();
                return it;
            }
            lookMenu.Items.Add(Item("Spec", "Specular highlights (shine) the materials set.", () => PreviewViews.Spec, () => PreviewViews.Spec = !PreviewViews.Spec));
            lookMenu.Items.Add(Item("Reflect", "Reflections of the materials' own environment images.", () => PreviewViews.Reflect, () => PreviewViews.Reflect = !PreviewViews.Reflect));
            lookMenu.Items.Add(Item("Glow", "Glowing (emissive) parts.", () => PreviewViews.Glow, () => PreviewViews.Glow = !PreviewViews.Glow));
            lookMenu.Items.Add(Item("Bloom", "A soft halo around the brightest parts (glow, the hottest highlights), as the game draws it.", () => PreviewViews.Bloom, () => PreviewViews.Bloom = !PreviewViews.Bloom));
            lookMenu.Items.Add(Item("Props", "The weapons and props the game attaches to the character, held on their bones.", () => PreviewViews.Props, () => { PreviewViews.Props = !PreviewViews.Props; LoadProps(); }));
            lookMenu.Items.Add(new ToolStripSeparator());
            foreach (var sl in new Control[] { lightSlider, glowSlider, lensSlider })
            {
                sl.Visible = true;
                var host = new ToolStripControlHost(sl) { AutoSize = false, Size = new Size((int)(260 * S), (int)(26 * S)), Margin = new Padding((int)(6 * S), 2, (int)(6 * S), 2) };
                lookMenu.Items.Add(host);
            }
            // Clicking a toggle keeps the menu open (several at once); a click outside closes it.
            lookMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        }
        lightSlider.Visible = lensSlider.Visible = glowSlider.Visible = true;   // (hidden with the other 3D controls while a picture shows)
        Ui.ShowUnder(lookMenu, lookBtn);
    }

    /// <summary>A framing button: aims the camera like Create from 3D, and keeps it as this mesh's view (as a drag does).</summary>
    void FrameShot(Framing.Shot shot)
    {
        if (viewer == null || !MeshOk || mod == null) return;
        if (animator != null) { Framing.Apply(viewer, animator, playing != null, shot, centerHead: true); if (playing == null) { animator.Pose(null, 0); } }
        else Framing.Apply(viewer, null, false, shot);
        PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState);
    }

    /// <summary>Into or out of full screen: this control moves into a borderless window on the app's monitor and back.</summary>
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
            f.Close();
            f.Dispose();
            Invalidate();
            FindForm()?.Activate();
            return;
        }
        if (!show3D || viewer == null || Parent == null) return;
        var owner = FindForm();
        homeParent = Parent;
        homeIndex = homeParent.Controls.GetChildIndex(this);
        var screen = Screen.FromControl(this).Bounds;
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = screen, ShowInTaskbar = false,
            Text = mod == null ? "Preview" : $"Preview: {mod.Name}", BackColor = Ui.GradientTop, KeyPreview = true,
        };
        form.KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.F11) { e.Handled = true; if (e.KeyCode == Keys.F11 || !PausePlayback()) ToggleFull(); } };   // Esc: pause first, then back
        form.FormClosing += (_, e) => { if (fullForm == form && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; ToggleFull(); } };   // Alt+F4: back, not closed
        fullForm = form;
        Parent = form;
        form.Show(owner);
        viewer.Focus();
        Invalidate();
    }

    void StopAnimation() { resumeOnReveal = false; playTimer.Stop(); playing = null; rig.SetParentAnimation(null); playClock.Reset(); paused = true; playTime = 0; UpdateButtons(); ShowFrame(0); if (propRules.Count > 0) { propRules = []; rig.SetRules(propRules, 0, 0); } }
}
