using System.Numerics;
using AnimExportCli.Animation;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A 3D preview's power effects, one way for the Mods tab's preview (StorePreview, also the Editor's Powers tab) and the
/// Model tab's (PreviewPanel): a power's effects read in the background (the preview says how: by animation, an effect-only
/// power, a character's own effects), played in time with the animation (PowerPreview.Clock) or on their own over the rest
/// pose, its animated actors (Avengers Assemble's Hulk, Iron Man and Thor) in the rig, its condition material swaps, the
/// Powers tab's colour, and the caption ("Shockwave · 5 Effects"). Phase 3 of the preview merge, step 2.
/// </summary>
sealed class PreviewEffects : IDisposable
{
    readonly PropRig rig;
    readonly Control owner;
    readonly PowerPreview.Clock clock = new();
    readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 33 };
    readonly System.Diagnostics.Stopwatch idleClock = new();
    double idleLast;
    int request;
    Dictionary<string, (string Bone, Matrix4x4 Local)> sockets = new();
    List<(Fx.PowerEffects.Actor Spec, ModMeshes.Loaded Mesh, MeshAnimator Anim, BoneAnimation? Seq)> actors = [];

    /// <summary>The view, the character's animator and its mesh as shown now (null: nothing in 3D).</summary>
    public required Func<(ModelView View, MeshAnimator Animator, ModMeshes.Loaded Mesh)?> Shown { get; init; }
    /// <summary>The animation playing (null: rest pose), its length in seconds and frames, and where it is (seconds; within
    /// one loop).</summary>
    public required Func<BoneAnimation?> Playing { get; init; }
    public required Func<float> Seconds { get; init; }
    public required Func<float> Frames { get; init; }
    public required Func<double> Time { get; init; }
    /// <summary>Draws the current pose; the rig built again (the actors joined or left); the caption changed.</summary>
    public required Action ShowPose { get; init; }
    public required Action RebuildRig { get; init; }
    public Action? NoteChanged { get; init; }
    /// <summary>Whether the effects may run on their own (the preview shows 3D and is visible).</summary>
    public Func<bool>? Running { get; init; }
    /// <summary>The Powers tab's colour for a power (by prototype path or "own:&lt;file&gt;"; null = the game's).</summary>
    public Func<string, PowerColor?>? ColorFor { get; set; }

    public PreviewEffects(PropRig rig, Control owner)
    {
        this.rig = rig; this.owner = owner;
        idleTimer.Tick += IdleTick;
    }

    /// <summary>The effects playing (null: none).</summary>
    public Fx.PowerEffects.Player? Player => clock.Player;
    /// <summary>The caption: the power and how many effects play ("Reading Powers…" while reading; "" for none).</summary>
    public string Note { get; private set; } = "";
    /// <summary>The power (or own-effects entry) playing: what its colour is looked up by.</summary>
    public string Power { get; private set; } = "";
    /// <summary>Where effects at the world position go: the ground 250 units in front (characters face +X).</summary>
    public Vector3 Target { get; private set; }

    void SetNote(string n) { Note = n; NoteChanged?.Invoke(); }

    /// <summary>No effects (nothing loading either); the actors leave the rig.</summary>
    public void Clear()
    {
        idleTimer.Stop();
        request++; clock.Clear(); Power = "";
        SetNote("");
        if (Shown() is { } s) { s.View.Effects = []; s.View.EffectTris = []; s.View.LookOverride = null; }
        if (actors.Count > 0) { actors = []; RebuildRig(); }
    }

    /// <summary>
    /// Reads effects in the background and plays them: <paramref name="read"/> gets the game data (null when
    /// <paramref name="needsDb"/> is false) and returns them with the power's name and sockets; <paramref name="swaps"/> reads
    /// the power's condition material swaps; <paramref name="label"/> overrides the caption (own effects).
    /// </summary>
    /// <summary>Effects left out by their part's name (the editor's Powers tab: parts switched off, before and after Save).</summary>
    public Func<string, bool>? HiddenEffect { get; set; }
    /// <summary>Only this effect plays (the Powers tab's Parts ▶ on one of a power's effects), else all; by its part's name.</summary>
    public string? OnlyEffect { get; set; }

    public void Load(string cooked, bool needsDb, Func<Fx.GameData?, PowerPreview.Loaded?> read, Func<Fx.GameData, string, ModelView.Look?[]?>? swaps,
        string reading = "Reading Powers…", Func<PowerPreview.Loaded, string>? label = null)
    {
        Clear();
        if (Shown() is not { } at) return;
        int req = request;
        SetNote(reading);
        var dbTask = needsDb ? PowerPreview.GameDb(cooked) : Task.FromResult<Fx.GameData?>(null);
        dbTask.ContinueWith(dbt => Task.Run(() =>
        {
            using var busy = Busy.Begin("3D view: reading the power's effects");
            return read(dbt.Result);
        })).Unwrap().ContinueWith(t =>
        {
            if (owner.IsDisposed || req != request || Shown() is not { } s || s.Animator != at.Animator) return;
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } loaded) { SetNote(""); return; }
            sockets = loaded.Sockets;
            float ground = s.Mesh.Positions.Length == 0 ? 0 : s.Mesh.Positions.Min(v => v.Z);
            Target = new Vector3(250, 0, ground);
            var playing = Playing();
            // (an effect part switched off in the editor isn't played: Powers tab → Parts)
            var phase = HiddenEffect is { } hidden ? e => loaded.Phase(e) && !hidden(e.Name) : loaded.Phase;
            if (OnlyEffect is { } only) { var before = phase; phase = e => before(e) && e.Name.Equals(only, StringComparison.OrdinalIgnoreCase); }
            clock.Player = new Fx.PowerEffects.Player(loaded.Effects, Socket, Target, phase) { AnimSeconds = Math.Max(0.1f, Seconds()), Color = ColorFor?.Invoke(loaded.Power) };
            rig.Recolor = clock.Player.Color is { IsNone: false } pc ? pc.Apply : null;
            Power = loaded.Power;
            s.View.EffectStrength = PreviewViews.FxPower;   // the effects' opacity / glow
            SetNote(OnlyEffect != null ? $"{Ui.TitleCase(PowerPreview.SplitWords(Path.GetFileNameWithoutExtension(loaded.Power)))} · Only {Ui.TitleCase(OnlyEffect)}" : label?.Invoke(loaded) ?? loaded.Label);
            LoadActors(loaded.Effects, req);
            if (swaps != null && loaded.Power.Length > 0 && !loaded.Power.StartsWith("own:", StringComparison.Ordinal)) LoadSwaps(cooked, loaded.Power, swaps, req);
            if (playing != null)
            {
                double time = Time();
                Replay(time);
                s.Animator.Pose(playing, Seconds() > 0 ? (float)(time / Seconds() * Frames()) : 0);
            }
            else StartIdle();   // no animation: the effects run on their own
            ShowPose();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A power's condition material swaps on the shown model (The Will of Doom's force field), read in the background.</summary>
    void LoadSwaps(string cooked, string power, Func<Fx.GameData, string, ModelView.Look?[]?> swaps, int req)
    {
        PowerPreview.GameDb(cooked).ContinueWith(dbt => Task.Run(() => dbt.Result is { } db ? swaps(db, power) : null)).Unwrap().ContinueWith(t =>
        {
            if (owner.IsDisposed || req != request || t.Status != TaskStatus.RanToCompletion || t.Result is not { } looks || Shown() is not { } s) return;
            s.View.LookOverride = looks;
            SetNote(Note.Length > 0 ? Note + " · Material Swap" : "Material Swap");
            ShowPose();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The power's animated actors (Kurt, 2026-10-03: Avengers Assemble's Hulk, Iron Man and Thor, the Fantastic Four, Jean
    /// Grey's Phoenix …): each one's model and its own animation read in the background, then shown in the rig.
    /// </summary>
    void LoadActors(Fx.PowerEffects fx, int req)
    {
        if (fx.Actors.Count == 0) return;
        var specs = fx.Actors.ToList();
        string? cookedFolder = specs.Count > 0 ? CookedFor?.Invoke() : null;
        Task.Run(() =>
        {
            using var busy = Busy.Begin("3D view: loading the power's characters");
            var list = new List<(Fx.PowerEffects.Actor, ModMeshes.Loaded, MeshAnimator, BoneAnimation?)>();
            foreach (var s in specs)
                try
                {
                    var lm = ModMeshes.Load(new MeshRef(Path.GetFileName(s.MeshFile), s.MeshFile, s.Name, s.MeshExport), cookedFolder, out _);
                    if (lm == null) continue;
                    var an = new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents) { InPlace = false };
                    var seq = s.AnimName != null && ModAnimations.Named(s.SetFile, s.SetExport, s.MeshFile, s.AnimName) is { } ar ? ModAnimations.Load(ar) : null;
                    list.Add((s, lm, an, seq));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
            return list;
        }).ContinueWith(t =>
        {
            if (owner.IsDisposed || req != request || t.Status != TaskStatus.RanToCompletion || t.Result.Count == 0) return;
            actors = t.Result;
            RebuildRig();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The game's package folder (the actors' textures).</summary>
    public Func<string?>? CookedFor { get; init; }

    /// <summary>The power's animated actors in the rig: scale, turn (UE3 rotator), offset, then at the hero or the target;
    /// when they show (PreviewProps.AddMore).</summary>
    public void AddActors(PropRig r)
    {
        if (clock.Player is not { } player) return;
        foreach (var (s, m, an, seq) in actors)
        {
            float own = seq == null ? 0 : MeshAnimator.Span(seq).Seconds;
            if (player.ActorWindow(s, own) is not { } win) continue;
            var place = Matrix4x4.CreateScale(s.Scale) * Fx.PowerEffects.Player.UeRotation(s.Turn.X, s.Turn.Y, s.Turn.Z)
                * Matrix4x4.CreateTranslation(s.Shift + (s.AtTarget ? Target : Vector3.Zero));
            r.AddActor(m, an, seq, win.Start, win.End, place);
        }
    }

    /// <summary>A socket's place in the pose last made: the mesh's socket on its bone, a prop's socket, else a bone of that name.</summary>
    Matrix4x4? Socket(string name) => PowerPreview.Socket(name, sockets, Shown()?.Animator, rig);

    /// <summary>The effects moved on to <paramref name="seconds"/> (started over when time went back: a loop).</summary>
    public void Advance(double seconds, bool ended) => clock.Advance(seconds, ended);

    /// <summary>The effects as they are at <paramref name="seconds"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    public void Replay(double seconds)
    {
        if (Playing() is not { } p || Shown()?.Animator is not { } an) return;
        float secs = Seconds(), frames = Frames();
        clock.Replay(seconds, t => an.Pose(p, secs > 0 ? (float)(t / secs * frames) : 0));
    }

    /// <summary>The colour of the power playing changed (the Powers tab): shown at once.</summary>
    public void RefreshColor()
    {
        if (clock.Player is not { } player) return;
        player.Color = ColorFor?.Invoke(Power);
        rig.Recolor = player.Color is { IsNone: false } c ? c.Apply : null;
        ShowPose();
    }

    /// <summary>Effects with no animation to play them with (a package with a model but no animations: Jean Grey's Phoenix
    /// form, 2026-10-07): they run on their own, in real time, over the rest pose.</summary>
    void StartIdle()
    {
        if (clock.Player == null || Playing() != null) return;
        idleClock.Restart(); idleLast = 0; clock.ResetTime();
        idleTimer.Start();
    }

    void IdleTick(object? sender, EventArgs e)
    {
        if (clock.Player is not { } player || Playing() != null || Shown() is not { } s || Running?.Invoke() == false) { idleTimer.Stop(); return; }
        double now = idleClock.Elapsed.TotalSeconds;
        player.Step((float)Math.Min(0.1, now - idleLast), false);
        idleLast = now;
        ShowPose();
        s.View.Invalidate();
    }

    public void Dispose() => idleTimer.Dispose();
}
