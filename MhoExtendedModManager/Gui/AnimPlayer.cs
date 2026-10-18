using AnimExportCli.Animation;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A 3D preview's playback, one way for the Mods tab's preview (StorePreview) and the Model tab's (PreviewPanel): the
/// animation and its length, where it is, paused or playing, looping or stopping on its last frame, and the timer that
/// moves it (33 ms). The preview draws each moment (<see cref="Moved"/>) and shows the state on its buttons
/// (<see cref="StateChanged"/>). An animation always loads paused. Phase 3 of the preview merge, step 3.
/// </summary>
sealed class AnimPlayer : IDisposable
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly System.Diagnostics.Stopwatch clock = new();
    double lastTick;

    /// <summary>Whether the animation loops (else it plays once and stops on its last frame).</summary>
    public required Func<bool> Loop { get; init; }
    /// <summary>A moment to draw: the frame, and whether a play-once animation just reached its end.</summary>
    public required Action<float, bool> Moved { get; init; }
    /// <summary>Paused or playing changed (the ▶ / ❚❚ button, smaller frames while moving).</summary>
    public Action? StateChanged { get; init; }
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
        Time = Math.Clamp(time, 0, Math.Max(0, Seconds));
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

    public void Dispose() => timer.Dispose();
}
