using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>Hooks for the preview self-test (--preview-selftest).</summary>
sealed partial class StorePreview
{
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
        await Wait(() => show3D && animator != null && anims.Count > 0 && animBox != null);
        Check($"3D view with {anims.Count} animation(s)", show3D && animator != null && anims.Count > 0);
        if (animBox == null || playBtn == null || loopBtn == null || restBtn == null || animator == null || viewer == null) return log;
        bool loopWas = PreviewViews.Loop;
        PreviewViews.Loop = true; UpdateButtons();
        animBox.SelectedIndex = 1;
        await Wait(() => playing != null);
        Check("an animation loads paused on frame 0 (▶ shown)", playing != null && paused && playTime == 0 && playBtn.Text == "▶");
        var p0 = (System.Numerics.Vector3[])animator.Positions.Clone();
        playBtn.PerformClick();
        await Idle(Math.Min(700, (int)(playSeconds * 1000 * 0.6)));
        Check($"Play runs it (time {playTime:0.00} s, ❚❚ shown) and the mesh moves", !paused && playTime > 0.1 && playBtn.Text == "❚❚" && animator.Positions.Where((p, i) => System.Numerics.Vector3.Distance(p, p0[i]) > 0.01f).Any());
        playBtn.PerformClick();
        double held = playTime;
        if (frameSlider != null)
        {
            Check("the frame slider follows the animation", frameSlider.Enabled && Math.Abs(frameSlider.Max - Math.Max(1, playFrames)) < 1e-4 && frameSlider.Value > 0);
            playBtn.PerformClick();   // playing again, then a drag on the slider
            await Idle(200);
            frameSlider.Value = playFrames * 0.5f;
            Check("dragging the frame slider pauses and shows that frame", paused && Math.Abs(playTime - playSeconds * 0.5) < 1e-3);
            held = playTime;
        }
        await Idle(300);
        Check("Pause holds the frame", paused && playTime == held);
        PreviewViews.Loop = false; UpdateButtons();
        playTime = Math.Max(0, playSeconds - 0.15);
        playBtn.PerformClick();
        await Idle(600);
        Check($"Loop off: it stops on its last frame (time {playTime:0.00} of {playSeconds:0.00} s, ▶ shown)", paused && Math.Abs(playTime - playSeconds) < 1e-6 && playBtn.Text == "▶");
        playBtn.PerformClick();
        await Idle(150);
        Check("Play after the end starts over", !paused && playTime < playSeconds * 0.5);
        playBtn.PerformClick();
        string key = PreviewViews.Key(mod, meshes[meshIndex]);
        viewer.ViewState = [1f, 0.2f, 1.5f, 0f, 0f, 0f];
        PreviewViews.Set(key, viewer.ViewState);
        Check("a turned view is saved for the mesh", PreviewViews.Get(key) is { Length: 6 });
        restBtn.PerformClick();
        Check("Reset View resets the camera and forgets the saved view", PreviewViews.Get(key) == null && Math.Abs(viewer.ViewState[2] - 2.6f) < 0.01f);
        // A mod carrying its author's view (an exported one): Reset View goes back to that view, not the default framing.
        var authorViews = mod.Manifest.PreviewViews;
        mod.Manifest.PreviewViews = new() { [meshes[meshIndex].Key] = [0.5f, 0.1f, 2.0f, 0f, 0f, 0f] };
        viewer.ViewState = [1f, 0.2f, 1.2f, 0f, 0f, 0f];
        restBtn.PerformClick();
        Check("with the mod's own view, Reset View goes to it", Math.Abs(viewer.ViewState[2] - 2.0f) < 0.01f && Math.Abs(viewer.ViewState[0] - 0.5f) < 0.01f);
        mod.Manifest.PreviewViews = authorViews;

        if (lightSlider != null)
        {
            float was = lightSlider.Value;
            lightSlider.Value = 1.5f;
            Check("the light slider sets the 3D view's brightness", Math.Abs(viewer.Brightness - 1.5f) < 1e-4 && lightSlider.Visible);
            float saved = PreviewViews.Light(mod);
            PreviewViews.SetLight(mod, 1.5f);
            Check("and is kept for this mod", Math.Abs(PreviewViews.Light(mod) - 1.5f) < 1e-4);
            PreviewViews.SetLight(mod, saved);
            lightSlider.Value = was;
        }

        // Kurt: posed, zoomed, then a picture, then 3D again forgot it all. Clicks go through the window (a pick saves
        // and reloads the list), as a user's do.
        if (thumbRects.Any(t => t.Index == Offset))
        {
            PreviewViews.Loop = true; UpdateButtons();
            animBox.SelectedIndex = Math.Min(2, anims.Count);
            await Wait(() => playing != null && animBox.SelectedIndex - 1 < anims.Count);
            string animWas = animBox.SelectedItem?.ToString() ?? "";
            playTime = playSeconds * 0.4; animator.Pose(playing, (float)(playTime / playSeconds * playFrames)); ShowPose();
            double frameWas = playTime;
            viewer.ViewState = [0.7f, 0.3f, 1.4f, 0.05f, 0f, 0f];
            PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState);   // as a drag saves it
            var viewWas = viewer.ViewState;
            var pic = thumbRects.First(t => t.Index == Offset).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(800);
            Check("a picture shows (the 3D view hidden)", !show3D && viewer.Visible == false);
            var tile = thumbRects.First(t => t.Index == 0).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(800);
            Check($"back to 3D: same animation ({animWas}), same frame, same camera",
                show3D && viewer.Visible && animBox.SelectedItem?.ToString() == animWas && playing != null && Math.Abs(playTime - frameWas) < 1e-6
                && viewer.ViewState.Zip(viewWas).All(p => Math.Abs(p.First - p.Second) < 1e-4));
            playBtn.PerformClick();
            await Idle(300);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(500);
            double heldAt = playTime;
            await Idle(300);
            Check("playing, then a picture: it holds its frame", paused && playTime == heldAt);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(400);
            Check("and plays on when 3D shows again", !paused && playTime > heldAt);
            playBtn.PerformClick();
        }
        PreviewViews.Loop = loopWas; UpdateButtons();
        return log;
    }

    /// <summary>--preview-selftest: poses the 3D view (an animation, a frame, a camera) as a user would; returns it.</summary>
    internal async Task<(string Anim, double Time, float[] View)?> PoseForTest()
    {
        if (animBox == null || viewer == null || animator == null || anims.Count < 3 || !MeshOk) return null;
        animBox.SelectedIndex = 3;
        for (int t = 0; t < 10000 && playing == null; t += 50) { await Task.Delay(50); Application.DoEvents(); }
        if (playing == null) return null;
        playTime = playSeconds * 0.55; animator.Pose(playing, (float)(playTime / playSeconds * playFrames)); ShowPose();
        Pause();   // as the pause button: saved
        viewer.ViewState = [0.9f, -0.2f, 1.3f, 0f, 0.04f, 0f];
        PreviewViews.Set(PreviewViews.Key(mod!, meshes[meshIndex]), viewer.ViewState);   // as a drag saves it
        return (anims[2].Name, playTime, viewer.ViewState);
    }

    /// <summary>--preview-selftest: the 3D view's light now, or null.</summary>
    internal float? ShownLight => show3D && viewer != null ? viewer.Brightness : null;

    /// <summary>--preview-selftest: what the 3D view shows now (animation, time, camera), or null when it isn't showing.</summary>
    internal (string? Anim, double Time, float[] View)? Shown3D() =>
        !show3D || viewer == null || animBox == null ? null : (animBox.SelectedIndex > 0 && playing != null ? anims[animBox.SelectedIndex - 1].Name : null, playTime, viewer.ViewState);
}
