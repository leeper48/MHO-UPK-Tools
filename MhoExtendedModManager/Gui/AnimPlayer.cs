using AnimExportCli.Animation;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A 3D preview's playback, one way for the Mods tab's preview (StorePreview) and the Model tab's (PreviewPanel): the
/// animation and its length, where it is, paused or playing, looping or stopping on its last frame, and the timer that
/// moves it (15 ms). The preview draws each moment (<see cref="Moved"/>) and shows the state on its buttons
/// (<see cref="StateChanged"/>). An animation always loads paused. Phase 3 of the preview merge, step 3.
/// </summary>
sealed class AnimPlayer : IDisposable
{
    // 15 ms (Kurt, 2026-10-19: playback felt slow): Windows' timers tick every 15.6 ms unless a program asks for finer
    // ones, so 33 ms waited 3 ticks (46.9 ms: 21 frames a second, measured by --powers-perf, whatever the frame cost); one
    // tick gives up to 64. Time follows the clock either way; heavy frames shrink the drawing (ModelView.Moving).
    readonly System.Windows.Forms.Timer timer = new() { Interval = FrameMs };
    /// <summary>(perf patch, contributed by pants on a tiger) Milliseconds between frames: 15 by default (Kurt keeps the rate); MHO_EXTMM_PREVIEW_FPS
    /// sets the frames a second (e.g. 60). Time still follows the clock, so playback speed is the same either way.</summary>
    public static readonly int FrameMs = int.TryParse(Environment.GetEnvironmentVariable("MHO_EXTMM_PREVIEW_FPS"), out int fps) && fps is >= 5 and <= 120 ? Math.Max(1, 1000 / fps) : 15;   // (15 ms as before the port; MHO_EXTMM_PREVIEW_FPS still sets another rate)
    /// <summary>(perf patch) Hold the frame while none of this app's windows is the active one (the game, Blender or a browser
    /// in front): no drawing then, and playback goes on from the same moment when the app is back in front.</summary>
    public static bool HoldWhenInactive = Environment.GetEnvironmentVariable("MHO_EXTMM_PREVIEW_BACKGROUND") == "0";   // (off: test windows are never the active one; MHO_EXTMM_PREVIEW_BACKGROUND=0 turns it on)
    readonly System.Diagnostics.Stopwatch clock = new();
    double lastTick;

    /// <summary>Whether the animation loops (else it plays once and stops on its last frame).</summary>
    public required Func<bool> Loop { get; init; }
    /// <summary>A moment to draw: the frame, and whether a play-once animation just reached its end.</summary>
    public required Action<float, bool> Moved { get; init; }
    /// <summary>Paused or playing changed (the ▶ / ❚❚ button, smaller frames while moving).</summary>
    public Action? StateChanged { get; init; }
    /// <summary>(perf patch) The view it plays in: while it isn't on screen (another tab or page shows, its window hidden or
    /// minimized) the frame is held and nothing is drawn; playback goes on from the same moment when it shows again.</summary>
    public System.Windows.Forms.Control? Owner { get; init; }
    /// <summary>Whether it may run now; false pauses it (the Mods tab: a picture shows, or the preview is hidden).</summary>
    public Func<bool>? CanRun { get; init; }

    public AnimPlayer() => timer.Tick += (_, _) => Tick();

    public BoneAnimation? Anim { get; private set; }
    public float Frames { get; private set; }
    public float Seconds { get; private set; }
    /// <summary>Where the animation is, in seconds (within one loop).</summary>
    public double Time { get; set; }
    public bool Paused { get; private set; } = true;
    /// <summary>Where the animation is, in frames.</summary>
    public float Frame => Seconds > 0 && Frames > 0 ? (float)(Time / Seconds * Frames) : 0;
    /// <summary>Where the animation is, as a share of its length (0 to 1).</summary>
    public double Fraction => Seconds > 0 ? Math.Clamp(Time / Seconds, 0, 1) : 0;

    /// <summary>An animation (null: the rest pose), paused at <paramref name="time"/> seconds.</summary>
    public void Set(BoneAnimation? anim, double time = 0)
    {
        Stop();
        Anim = anim;
        (Frames, Seconds) = anim != null ? MeshAnimator.Span(anim) : (0, 0);
        if (anim != null && GameSeconds?.Invoke(anim.Name) is float gs && gs > 0.05f) { time *= gs / Math.Max(0.001f, Seconds); Seconds = gs; }
        Time = Math.Clamp(time, 0, Math.Max(0, Seconds));
    }

    /// <summary>How long the game plays an animation, when it differs from its own length (a power's: PowerPreview.GameAnimSeconds);
    /// null: its own length.</summary>
    public Func<string, float?>? GameSeconds { get; set; }

    /// <summary>The animation playing now stretched to <paramref name="seconds"/>, at the same point of it.</summary>
    public void Rescale(float seconds)
    {
        if (Anim == null || seconds <= 0.05f || Math.Abs(seconds - Seconds) < 1e-3f) return;
        Time = Fraction * seconds;
        Seconds = seconds;
    }

    /// <summary>Plays from where it is (from the start when a play-once animation already ended).</summary>
    public void Play()
    {
        if (Anim == null || !Paused) return;
        if (!Loop() && Time >= Seconds) Time = 0;
        if (Environment.GetEnvironmentVariable("MHO_PLAYTRACE") is { Length: > 0 } trace) File.AppendAllText(trace, $"{DateTime.Now:T}\n{Environment.StackTrace}\n\n");   // (debug: who starts playback)
        Paused = false;
        clock.Restart(); lastTick = 0;
        timer.Start();
        StateChanged?.Invoke();
    }

    /// <summary>Holds the frame it's on.</summary>
    public void Pause()
    {
        timer.Stop(); clock.Reset();
        if (Paused) return;
        Paused = true;
        StateChanged?.Invoke();
    }

    public void Toggle() { if (Paused) Play(); else Pause(); }

    /// <summary>No animation (rest pose), paused at 0.</summary>
    public void Stop()
    {
        timer.Stop(); clock.Reset();
        bool was = !Paused;
        Anim = null; Frames = Seconds = 0; Time = 0; Paused = true;
        if (was) StateChanged?.Invoke();
    }

    /// <summary>To <paramref name="time"/> seconds, paused (the frame slider dragged).</summary>
    public void Scrub(double time)
    {
        Pause();
        Time = Math.Clamp(time, 0, Math.Max(0, Seconds));
    }

    void Tick()
    {
        if (Anim == null || Paused) { timer.Stop(); return; }
        if (CanRun?.Invoke() == false) { Pause(); return; }   // hidden: hold the frame
        double now = clock.Elapsed.TotalSeconds;
        if (HoldWhenInactive && System.Windows.Forms.Form.ActiveForm == null) { lastTick = now; return; }   // (perf patch)
        if (Owner != null && !OnScreen(Owner)) { lastTick = now; return; }   // (perf patch: another tab or page is showing)
        Time += now - lastTick; lastTick = now;
        bool ended = false;
        if (Seconds <= 0) Time = 0;
        else if (Time >= Seconds)
        {
            if (Loop()) Time %= Seconds;
            else { Time = Seconds; ended = true; timer.Stop(); clock.Reset(); Paused = true; }
        }
        Moved(Frame, ended);
        if (ended) StateChanged?.Invoke();
    }

    /// <summary>(perf patch) The control and all its parents are shown, and its window isn't minimized.</summary>
    internal static bool OnScreen(System.Windows.Forms.Control c) =>
        !c.IsDisposed && c.Visible && c.Width > 0 && c.Height > 0 && c.FindForm() is { } f && f.WindowState != System.Windows.Forms.FormWindowState.Minimized;

    public void Dispose() => timer.Dispose();
}
