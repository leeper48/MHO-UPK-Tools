namespace MhoExtendedModManager.Gui;

/// <summary>Hooks for the preview self-test (--preview-selftest) and the power test (--power-preview-test); the 3D controls
/// are the shared 3D preview's (Preview3D).</summary>
sealed partial class StorePreview
{
    /// <summary>--power-preview-test: the 3D view shown, a power button clicked by name, then what the preview did (its
    /// caption, effects, material swap, animation), and the view's picture.</summary>
    internal async Task<(List<string> Log, Bitmap? Shot)> PowerTest(string powerName)
    {
        var log = new List<string>();
        async Task Wait(Func<bool> until, int ms = 20000) { for (int t = 0; t < ms && !until(); t += 50) { await Task.Delay(50); Application.DoEvents(); } }
        await Wait(() => mod != null && meshes.Count > 0 && thumbRects.Any(t => t.Index == 0));
        if (mod == null || meshes.Count == 0) { log.Add("no mesh"); return (log, null); }
        if (!show3D && thumbRects.Any(t => t.Index == 0)) OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, thumbRects.First(t => t.Index == 0).Rect.X + 4, thumbRects.First(t => t.Index == 0).Rect.Y + 4, 0));
        await Wait(() => show3D && p3?.Animator != null && p3.PowersLoaded);
        var powers = HeroPowers;
        log.Add($"3D: {show3D}, mesh {(MeshOk ? meshes[meshIndex].Name + " in " + meshes[meshIndex].Package : "none")}, {powers.Count} power button(s)");
        var pw = powers.FirstOrDefault(p => p.Name.Contains(powerName, StringComparison.OrdinalIgnoreCase));
        if (pw == null || p3 == null) { log.Add("no power button named " + powerName); return (log, null); }
        log.Add($"clicking {pw.Name} ({pw.Prototype}), {pw.Animations.Count} animation(s)");
        p3.ClickPower(pw.Name);
        await Wait(() => p3.EffectsPlayer.Player != null && !p3.EffectsCaption.StartsWith("Reading"), 20000);
        await Task.Delay(3000); Application.DoEvents();
        log.Add($"caption: {p3.EffectsCaption}");
        log.Add($"effects: {p3.EffectsPlayer.Player?.ToString() ?? "none"}; agent power {agentPower ?? "-"}; own package {ownFxPackage ?? "-"}; playing {p3.Playing?.Name ?? "-"}");
        log.Add($"material swap: {p3.SwapNote}");
        return (log, p3.ViewShot());
    }

    /// <summary>
    /// --preview-selftest: drives the 3D view's controls as a user would (use a scratch library: picks are saved). An
    /// animation loads paused on frame 0; Play advances it and moves the mesh; Pause holds it; without Loop it stops on
    /// its last frame; Reset View forgets the saved camera.
    /// </summary>
    internal async Task<List<string>> SelfTest()
    {
        var log = new List<string>();
        void Check(string what, bool ok) => log.Add($"{(ok ? "ok  " : "FAIL")} {what}");
        async Task Wait(Func<bool> until, int ms = 10000) { for (int t = 0; t < ms && !until(); t += 50) { await Task.Delay(50); Application.DoEvents(); } }
        async Task Idle(int ms) { for (int t = 0; t < ms; t += 20) { await Task.Delay(20); Application.DoEvents(); } }
        if (mod == null || meshes.Count == 0) { Check("the mod has a mesh", false); return log; }
        // 0.33.0 crash: an index left from a mod with more meshes, then a click on the 3D tile.
        if (!show3D && thumbRects.Any(t => t.Index == 0))
        {
            meshIndex = meshes.Count + 2;
            var tile = thumbRects.First(t => t.Index == 0).Rect;
            string? crash = null;
            try { OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0)); } catch (Exception ex) { crash = ex.GetType().Name; }
            Check("3D tile with an out-of-range mesh index doesn't crash" + (crash != null ? $" ({crash})" : ""), crash == null && MeshOk);
        }
        if (!show3D) { OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, thumbRects.First(t => t.Index == 0).Rect.X + 4, thumbRects.First(t => t.Index == 0).Rect.Y + 4, 0)); }
        await Wait(() => show3D && p3?.Animator != null && p3.AllAnimations.Count > 0);
        if (p3?.Animator is not { } animator) { Check("3D view", false); return log; }
        var box = p3.AnimationBox; var playBtn = p3.PlayButton; var frameSlider = p3.FrameSlider; var viewer = p3.View;
        Check($"3D view with {p3.AllAnimations.Count} animation(s)", show3D && p3.AllAnimations.Count > 0);
        bool loopWas = PreviewViews.Loop;
        PreviewViews.Loop = true;
        box.SelectedIndex = 1;
        await Wait(() => p3.Playing != null);
        Check("an animation loads paused on frame 0 (▶ shown)", p3.Playing != null && p3.Paused && p3.Time == 0 && playBtn.Text == "▶");
        var p0 = (System.Numerics.Vector3[])animator.Positions.Clone();
        playBtn.PerformClick();
        await Idle(Math.Min(700, (int)(p3.Seconds * 1000 * 0.6)));
        Check($"Play runs it (time {p3.Time:0.00} s, ❚❚ shown) and the mesh moves", !p3.Paused && p3.Time > 0.1 && playBtn.Text == "❚❚" && animator.Positions.Where((p, i) => System.Numerics.Vector3.Distance(p, p0[i]) > 0.01f).Any());
        playBtn.PerformClick();
        double held = p3.Time;
        Check("the frame slider follows the animation", frameSlider.Enabled && frameSlider.Value > 0);
        playBtn.PerformClick();   // playing again, then a drag on the slider
        await Idle(200);
        frameSlider.Value = 0.5f;
        Check("dragging the frame slider pauses and shows that frame", p3.Paused && Math.Abs(p3.Time - p3.Seconds * 0.5) < 1e-3);
        held = p3.Time;
        await Idle(300);
        Check("Pause holds the frame", p3.Paused && p3.Time == held);
        PreviewViews.Loop = false;
        p3.Time = Math.Max(0, p3.Seconds - 0.15);
        playBtn.PerformClick();
        await Idle(600);
        Check($"Loop off: it stops on its last frame (time {p3.Time:0.00} of {p3.Seconds:0.00} s, ▶ shown)", p3.Paused && Math.Abs(p3.Time - p3.Seconds) < 1e-3 && playBtn.Text == "▶");
        playBtn.PerformClick();
        await Idle(150);
        Check("Play after the end starts over", !p3.Paused && p3.Time < p3.Seconds * 0.5);
        playBtn.PerformClick();
        string key = PreviewViews.Key(mod, meshes[meshIndex]);
        viewer.ViewState = [1f, 0.2f, 1.5f, 0f, 0f, 0f];
        PreviewViews.Set(key, viewer.ViewState);
        Check("a turned view is saved for the mesh", PreviewViews.Get(key) is { Length: 6 });
        p3.ResetButton.PerformClick();
        Check("Reset View resets the camera and forgets the saved view", PreviewViews.Get(key) == null && Math.Abs(viewer.ViewState[2] - 2.6f) < 0.01f);
        // A mod carrying its author's view (an exported one): Reset View goes back to that view, not the default framing.
        var authorViews = mod.Manifest.PreviewViews;
        mod.Manifest.PreviewViews = new() { [meshes[meshIndex].Key] = [0.5f, 0.1f, 2.0f, 0f, 0f, 0f] };
        viewer.ViewState = [1f, 0.2f, 1.2f, 0f, 0f, 0f];
        p3.ResetButton.PerformClick();
        Check("with the mod's own view, Reset View goes to it", Math.Abs(viewer.ViewState[2] - 2.0f) < 0.01f && Math.Abs(viewer.ViewState[0] - 0.5f) < 0.01f);
        mod.Manifest.PreviewViews = authorViews;

        var lightSlider = p3.LightSlider;
        float was = lightSlider.Value;
        lightSlider.Value = 1.5f;
        Check("the light slider sets the 3D view's brightness", Math.Abs(viewer.Brightness - 1.5f) < 1e-4);
        float saved = PreviewViews.Light(mod);
        PreviewViews.SetLight(mod, 1.5f);
        Check("and is kept for this mod", Math.Abs(PreviewViews.Light(mod) - 1.5f) < 1e-4);
        PreviewViews.SetLight(mod, saved);
        lightSlider.Value = was;

        // Kurt: posed, zoomed, then a picture, then 3D again forgot it all. Clicks go through the window (a pick saves
        // and reloads the list), as a user's do.
        if (thumbRects.Any(t => t.Index == Offset))
        {
            PreviewViews.Loop = true;
            box.SelectedIndex = Math.Min(2, p3.AllAnimations.Count);
            await Wait(() => p3.Playing != null);
            string animWas = box.SelectedItem?.ToString() ?? "";
            p3.Time = p3.Seconds * 0.4;
            double frameWas = p3.Time;
            viewer.ViewState = [0.7f, 0.3f, 1.4f, 0.05f, 0f, 0f];
            PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState);   // as a drag saves it
            var viewWas = viewer.ViewState;
            var pic = thumbRects.First(t => t.Index == Offset).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(800);
            Check("a picture shows (the 3D view hidden)", !show3D && p3.Visible == false);
            var tile = thumbRects.First(t => t.Index == 0).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(800);
            Check($"back to 3D: same animation ({animWas}), same frame, same camera",
                show3D && p3.Visible && box.SelectedItem?.ToString() == animWas && p3.Playing != null && Math.Abs(p3.Time - frameWas) < 1e-3
                && viewer.ViewState.Zip(viewWas).All(p => Math.Abs(p.First - p.Second) < 1e-4));
            playBtn.PerformClick();
            await Idle(300);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(500);
            double heldAt = p3.Time;
            await Idle(300);
            Check("playing, then a picture: it holds its frame", p3.Paused && p3.Time == heldAt);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(400);
            Check("and plays on when 3D shows again", !p3.Paused && p3.Time > heldAt);
            playBtn.PerformClick();
        }
        PreviewViews.Loop = loopWas;
        return log;
    }

    /// <summary>--preview-selftest: poses the 3D view (an animation, a frame, a camera) as a user would; returns it.</summary>
    internal async Task<(string Anim, double Time, float[] View)?> PoseForTest()
    {
        if (p3?.Animator == null || p3.AllAnimations.Count < 3 || !MeshOk) return null;
        p3.AnimationBox.SelectedIndex = 3;
        for (int t = 0; t < 10000 && p3.Playing == null; t += 50) { await Task.Delay(50); Application.DoEvents(); }
        if (p3.Playing == null) return null;
        p3.Time = p3.Seconds * 0.55;
        p3.Pause(); SaveAnim();   // as the pause button: saved
        p3.View.ViewState = [0.9f, -0.2f, 1.3f, 0f, 0.04f, 0f];
        PreviewViews.Set(PreviewViews.Key(mod!, meshes[meshIndex]), p3.View.ViewState);   // as a drag saves it
        return (p3.AllAnimations[2].Name, p3.Time, p3.View.ViewState);
    }

    /// <summary>For --keep-frame-test: the animation shown and where the frame slider is (in frames), or null while none plays.</summary>
    internal (string Anim, double Fraction, float Slider, float Frames)? FrameForTest() =>
        p3?.Playing == null || p3.Seconds <= 0 ? null : (p3.CurrentAnimation ?? "", p3.Time / p3.Seconds, p3.FrameSlider.Value * p3.Frames, p3.Frames);

    /// <summary>For --keep-frame-test: the frame slider moved to <paramref name="fraction"/> of the animation (as a drag).</summary>
    internal void ScrubForTest(double fraction) { if (p3?.Playing != null && p3.Frames > 0) p3.FrameSlider.Value = (float)(Math.Round(fraction * p3.Frames) / p3.Frames); }

    internal object? P3ForTest => p3;
    /// <summary>Tests: the 3D view opens the model of the package whose name has this (the user's pick otherwise).</summary>
    internal string? TestMeshPart { get; set; }
    internal string PropsForTest => p3 == null ? "(no 3D view)" : string.Join(", ", p3.PropClassesForTest);
    internal object? ViewForTest => p3?.View;
    /// <summary>For --powers-perf: playing (if paused).</summary>
    internal void PlayForTest() { if (p3 != null && !p3.IsPlaying) p3.TogglePlayForTest(); }
    internal float? ShownLight => show3D && p3 != null ? p3.View.Brightness : null;

    /// <summary>--preview-selftest: what the 3D view shows now (animation, time, camera), or null when it isn't showing.</summary>
    internal (string? Anim, double Time, float[] View)? Shown3D() =>
        !show3D || p3 == null ? null : (p3.Playing != null ? p3.CurrentAnimation : null, p3.Time, p3.View.ViewState);
}
