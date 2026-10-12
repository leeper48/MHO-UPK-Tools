using System.Numerics;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The power side of the 3D previews, shared (Kurt, 2026-10-12: the Mods tab, the Editor's Powers tab and its Model tab must
/// handle power previews the same; the Model tab's copy had drifted: The Will of Doom showed nowhere but the Mods tab). Both
/// previews (StorePreview, Model.Gui.PreviewPanel) keep their controls and call these for every decision: the game data,
/// which powers get buttons, which power an animation plays and its effects, an effect-only power's effects, the material
/// swaps a power's conditions make, sockets, and playing effects in time.
/// </summary>
static class PowerPreview
{
    static readonly Dictionary<string, Task<Fx.GameData?>> dbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game data (Calligraphy.sip) for a CookedPCConsole folder, read once in the background for every preview.</summary>
    public static Task<Fx.GameData?> GameDb(string cooked)
    {
        string sip = Path.GetFullPath(Path.Combine(cooked, "..", "..", "..", "Data", "Game", "Calligraphy.sip"));
        lock (dbs)
        {
            if (dbs.TryGetValue(sip, out var t)) return t;
            return dbs[sip] = Task.Run(() => { try { return File.Exists(sip) ? new Fx.GameData(Fx.SipArchive.Load(sip)) : null; } catch (Exception ex) when (ex is IOException or InvalidDataException) { return (Fx.GameData?)null; } });
        }
    }

    /// <summary>
    /// The hero's powers: <c>Play</c> = the power buttons (powers with an animation the model has, and those with effects but no
    /// animation, which play over the idle: The Will of Doom), <c>All</c> = the same list (the editor's colors). Icons decoded
    /// here (call off the UI thread).
    /// </summary>
    public static (List<Fx.PowerList.Power> Play, List<Fx.PowerList.Power> All) Powers(Fx.GameData db, string hero, string cooked, IEnumerable<string> modFiles, IReadOnlySet<string> animNames)
    {
        var all = Fx.PowerList.For(db, hero, cooked, modFiles, effectOnly: true);
        foreach (var p in all) if (p.Icon != null) Fx.PowerList.Icon(p.Icon, cooked);
        var shown = all.Where(p => p.Animations.Count == 0 || p.Animations.Any(animNames.Contains)).ToList();
        return (shown, shown);
    }

    /// <summary>A power's effects ready to play: the effects, the power's prototype, the mesh's sockets and which effects play
    /// in this animation (start / loop / end phases).</summary>
    public sealed record Loaded(Fx.PowerEffects Effects, string Power, Dictionary<string, (string Bone, Matrix4x4 Local)> Sockets, Func<Fx.PowerEffects.Effect, bool> Phase)
    {
        public int Count => Effects.Effects.Count(e => Phase(e));
        /// <summary>The caption: "Diplomatic Immunity · 3 Effects".</summary>
        public string Label => $"{Ui.TitleCase(SplitWords(Path.GetFileNameWithoutExtension(Power)))} · {Count} Effect{(Count == 1 ? "" : "s")}";
    }

    public static string SplitWords(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>The power an animation of the hero belongs to (its power packages name it: PowerIndex) and its effects, for the
    /// character class shown (<paramref name="entity"/>: its own versions of effects). Null when the animation has none.</summary>
    public static Loaded? ForAnimation(Fx.GameData db, string hero, string cooked, IReadOnlyList<string> modFiles, string anim, string? entity, string meshFile, string meshName)
    {
        var idx = Fx.PowerIndex.For(hero, cooked, modFiles, db);
        if (!idx.TryGetValue(anim, out var powers) || powers.Count == 0) return null;
        var byClass = Fx.PowerIndex.PrototypesByClass(db);
        var hit = powers.Select(p => (p.Class, Proto: byClass.TryGetValue(p.Class, out var list) ? list.FirstOrDefault() : null)).FirstOrDefault(x => x.Proto != null);
        if (hit.Proto == null) return null;
        var fx = Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, hit.Proto, hero, entity: entity);
        var phase = Fx.PowerEffects.Player.PhaseFor(anim, idx.Where(kv => kv.Value.Any(p => p.Class == hit.Class)).Select(kv => kv.Key));
        return new(fx, hit.Proto, Fx.FxSockets.Of(meshFile, meshName), phase);
    }

    /// <summary>A power by its prototype (an effect-only power, an NPC's power): all its effects play.</summary>
    public static Loaded ForPower(Fx.GameData db, string power, string? hero, string cooked, IReadOnlyList<string> modFiles, string? entity, string meshFile, string meshName) =>
        new(Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, power, hero, entity: entity), power, Fx.FxSockets.Of(meshFile, meshName), _ => true);

    /// <summary>
    /// The material swaps a power's conditions make on the shown character (Doctor Doom's force field, Hulk's revive): per
    /// section, the look drawn instead of its own, or null. Sections by their slot (<paramref name="slots"/>: a model not built
    /// yet, in the build's slot order), else by <paramref name="mesh"/>'s own sections. A condition package the mod has is read
    /// from the mod (<paramref name="modFiles"/>: file name → path). Null when nothing is swapped.
    /// </summary>
    public static ModelView.Look?[]? Swaps(Fx.GameData db, string power, string cooked, IReadOnlyDictionary<string, string> modFiles, string costumeClass, IReadOnlyList<int>? slots, MeshRef? mesh)
    {
        var notes = new List<string>();
        ModelView.Look?[]? merged = null;
        foreach (string c in Model.ConditionSwaps.ConditionsOf(db, power))
        {
            string file = $"UC__{c}_SF.upk";
            string path = modFiles.TryGetValue(file, out var own) ? own : StockFiles.For(cooked, file);
            if (!File.Exists(path)) continue;
            try
            {
                var swaps = Model.ConditionSwaps.Read(MhoPackageModifier.Package.Open(path), file);
                var looks = slots != null ? ModMeshes.SwapLooks(slots, path, swaps, costumeClass, cooked, notes) : mesh != null ? ModMeshes.SwapLooks(mesh, path, swaps, costumeClass, cooked, notes) : null;
                if (looks == null) continue;
                merged ??= new ModelView.Look?[looks.Length];
                for (int k = 0; k < looks.Length && k < merged.Length; k++) if (looks[k] != null) merged[k] = looks[k];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        }
        return merged;
    }

    /// <summary>A socket's place in the pose last made: the mesh's socket on its bone, else a bone of that name, else null.
    /// "&lt;socket&gt;@bone": the socket's turn at its bone's origin (an effect whose local start already holds the offset).</summary>
    public static Matrix4x4? Socket(string name, Dictionary<string, (string Bone, Matrix4x4 Local)> sockets, MeshAnimator? animator)
    {
        if (animator == null) return null;
        if (name.EndsWith("@bone", StringComparison.Ordinal) && sockets.TryGetValue(name[..^5], out var sb) && animator.BoneIndex(sb.Bone) is int bb && bb >= 0)
        { var turn = sb.Local; turn.Translation = Vector3.Zero; return turn * animator.BoneMatrix(bb); }
        if (sockets.TryGetValue(name, out var sk) && animator.BoneIndex(sk.Bone) is int b && b >= 0) return sk.Local * animator.BoneMatrix(b);
        int bi = animator.BoneIndex(name);
        return bi >= 0 ? animator.BoneMatrix(bi) : null;
    }

    /// <summary>The animation a power button plays: one ending in "_loop", else the plain one beside a "_start", else the first.</summary>
    public static int PreferredAnim(IReadOnlyList<string> names)
    {
        int loopAt = names.ToList().FindIndex(n => n.EndsWith("_loop", StringComparison.OrdinalIgnoreCase));
        if (loopAt >= 0) return loopAt;
        for (int i = 0; i < names.Count; i++)
            if (names.Any(n => n.Equals(names[i] + "_start", StringComparison.OrdinalIgnoreCase))) return i;
        return 0;
    }

    /// <summary>The idle animation's index in a list (else the first with "idle" in its name, else 0).</summary>
    public static int IdleIndex(IReadOnlyList<string> names)
    {
        var list = names.ToList();
        int i = list.FindIndex(n => n.Equals("idle", StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = list.FindIndex(n => n.Contains("idle", StringComparison.OrdinalIgnoreCase));
        return Math.Max(0, i);
    }

    /// <summary>Plays a power's effects in time: moved on as the animation plays (started over when time goes back: a loop),
    /// or replayed from the start in 1/30 s steps to a scrubbed time, posing the character at each step for the sockets.</summary>
    public sealed class Clock
    {
        public Fx.PowerEffects.Player? Player;
        double time;

        public void Advance(double t, bool ended)
        {
            if (Player == null) return;
            if (t < time - 1e-6) { Player.Reset(); time = 0; }
            float dt = (float)(t - time);
            if (dt > 0 || ended) Player.Step(Math.Max(0, dt), ended);
            time = t;
        }

        /// <param name="poseAt">Poses the character at a time (seconds into the animation).</param>
        public void Replay(double t, Action<double> poseAt)
        {
            if (Player == null) return;
            Player.Reset(); time = 0;
            for (double x = 0; x + 1e-6 < t; x += 1.0 / 30)
            {
                poseAt(x);
                Player.Step(1f / 30, false);
                time = x + 1.0 / 30;
            }
        }

        public void Clear() { Player = null; time = 0; }
        /// <summary>Time back to 0, the player kept (effects that run on their own, without an animation).</summary>
        public void ResetTime() => time = 0;
    }
}
