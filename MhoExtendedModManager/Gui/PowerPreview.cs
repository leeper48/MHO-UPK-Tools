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
        // (a form's powers play on the form's own animations or model, which the character's list doesn't have yet)
        var shown = all.Where(p => p.Animations.Count == 0 || p.Form != null || p.Animations.Any(animNames.Contains)).ToList();
        return (shown, shown);
    }

    /// <summary>A power's effects ready to play: the effects, the power's prototype, the mesh's sockets and which effects play
    /// in this animation (start / loop / end phases).</summary>
    /// <summary>
    /// A power's effects with the character's own always-on ones added (Kurt, 2026-10-22: Ghost Rider's head didn't burn while
    /// a power played; in game it always does): PowerEffects.Own of the shown model's package, played in every phase.
    /// </summary>
    public static Loaded? WithOwn(Loaded? l, string cooked, IEnumerable<string> modFiles, string packageFile, string meshFile, string meshName)
    {
        Fx.PowerEffects own;
        try { own = Fx.PowerEffects.Own(new Fx.FxGame(cooked, modFiles), packageFile); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return l; }
        if (own.Effects.Count == 0) return l;
        // an animation of no power (idle, run): the own effects alone
        if (l == null) return new Loaded(own, OwnPrefix + packageFile, Fx.FxSockets.Of(meshFile, meshName), _ => true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in own.Effects)
            if (!l.Effects.Effects.Any(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))) { l.Effects.Effects.Add(e); names.Add(e.Name); }
        foreach (var (em, look) in own.Looks) l.Effects.Looks.TryAdd(em, look);
        var phase = l.Phase;
        return l with { Phase = e => names.Contains(e.Name) || phase(e) };
    }

    public sealed record Loaded(Fx.PowerEffects Effects, string Power, Dictionary<string, (string Bone, Matrix4x4 Local)> Sockets, Func<Fx.PowerEffects.Effect, bool> Phase)
    {
        public int Count => Effects.Effects.Count(e => Phase(e));
        /// <summary>The caption: "Diplomatic Immunity · 3 Effects".</summary>
        public string Label => Power.StartsWith(OwnPrefix, StringComparison.Ordinal) ? $"Own Effects · {Count} Effect{(Count == 1 ? "" : "s")}"
            : $"{Ui.TitleCase(SplitWords(Path.GetFileNameWithoutExtension(Power)))} · {Count} Effect{(Count == 1 ? "" : "s")}";
    }

    /// <summary>The Power of a Loaded that is a character's own effects only (no power).</summary>
    public const string OwnPrefix = "own:";

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

    /// <summary>
    /// A form's own effects (Jean Grey's Phoenix flames and wing trails): the effects of the form's power that its own
    /// conditions show, not those of what it sets off later (Unleash the Phoenix can turn into Dark Phoenix: those stay out).
    /// They play the whole time the form shows.
    /// </summary>
    public static Loaded ForForm(Fx.GameData db, string form, string? hero, string cooked, IReadOnlyList<string> modFiles, string? entity, string meshFile, string meshName)
    {
        var all = Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, form, hero, entity: entity);
        string own = Path.GetFileNameWithoutExtension(form);
        var fx = new Fx.PowerEffects();
        foreach (var e in all.Effects.Where(e => (e.TriggeredBy == null || e.TriggeredBy.Equals(own, StringComparison.OrdinalIgnoreCase)) && !e.Point.Contains("end", StringComparison.OrdinalIgnoreCase)))
            fx.Effects.Add(e);
        foreach (var kv in all.Looks) fx.Looks[kv.Key] = kv.Value;
        foreach (var kv in all.Meshes) fx.Meshes[kv.Key] = kv.Value;
        return new(fx, form, Fx.FxSockets.Of(meshFile, meshName), _ => true);
    }

    /// <summary>A power's effects with its form's under them (the form's play the whole time).</summary>
    public static Loaded? WithForm(Loaded? power, Loaded form)
    {
        if (power == null) return form;
        var fx = power.Effects;
        var mine = new HashSet<Fx.PowerEffects.Effect>(form.Effects.Effects);
        foreach (var e in form.Effects.Effects) fx.Effects.Add(e);
        foreach (var kv in form.Effects.Looks) fx.Looks.TryAdd(kv.Key, kv.Value);
        foreach (var kv in form.Effects.Meshes) fx.Meshes.TryAdd(kv.Key, kv.Value);
        var phase = power.Phase;
        var sockets = new Dictionary<string, (string Bone, Matrix4x4 Local)>(power.Sockets);
        foreach (var kv in form.Sockets) sockets.TryAdd(kv.Key, kv.Value);
        return power with { Sockets = sockets, Phase = e => mine.Contains(e) || phase(e) };
    }

    /// <summary>A power by its prototype playing one of its animations (a form's version of a power): its effects in that
    /// animation's phase (start / loop / end of <paramref name="powerAnims"/>).</summary>
    public static Loaded ForPowerAnimation(Fx.GameData db, string power, string anim, IEnumerable<string> powerAnims, string? hero, string cooked, IReadOnlyList<string> modFiles, string? entity, string meshFile, string meshName) =>
        new(Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, power, hero, entity: entity), power, Fx.FxSockets.Of(meshFile, meshName),
            Fx.PowerEffects.Player.PhaseFor(anim, powerAnims));

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
    public static Matrix4x4? Socket(string name, Dictionary<string, (string Bone, Matrix4x4 Local)> sockets, MeshAnimator? animator, PropRig? rig = null)
    {
        if (animator == null) return null;
        // a socket of a prop (the character has none of that name): Ghost Rider's bike's wheels
        if (!sockets.ContainsKey(name) && !name.EndsWith("@bone", StringComparison.Ordinal) && rig?.Socket(name, animator) is { } ps) return ps;
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

    // --- props ------------------------------------------------------------------------------------------------------------

    /// <summary>One of the character's own props, loaded: the attachment, its mesh, and for an animated prop its animator and
    /// animations.</summary>
    public sealed record BaseProp(PropRig.Prop W, ModMeshes.Loaded Mesh, MeshAnimator? Anim, List<AnimRef>? Refs)
    {
        public Dictionary<string, (string Bone, Matrix4x4 Local)> Sockets { get; init; } = new();
    }

    /// <summary>
    /// The character's props (PropRig.Attached: what its attachment classes name), loaded (call off the UI thread). A prop that
    /// follows the character's animation gets an animator for it; an animated prop (a ride) gets its own animations and is
    /// posed where they put it (Cyclops's bike rests 24 units back and 11.5 up; its ride animation moves it onto the attach
    /// point: in place it sat behind and below him, Kurt 2026-10-03).
    /// </summary>
    public static List<BaseProp> BaseProps(MeshRef main, IReadOnlyList<MeshRef> all, string? cooked, IEnumerable<(string File, string Path)> modPackages)
    {
        var pkgs = modPackages.ToList();
        var list = new List<BaseProp>();
        foreach (var w in PropRig.Attached(main, all, cooked))
        {
            try
            {
                if (ModMeshes.Load(w.Ref, cooked, out _) is not { } lm) continue;
                var sk = PropRig.SocketsOf(w.Ref);
                if (w.UseParentAnim) list.Add(new(w, lm, new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), null) { Sockets = sk });
                else if (w.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase))
                    list.Add(new(w, lm, new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents) { InPlace = false }, ModAnimations.For(w.Ref, lm.Bones, pkgs, cooked, minBones: 1)) { Sockets = sk });
                else list.Add(new(w, lm, null, null) { Sockets = sk });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or MhoPackageModifier.PackageFormatException) { }
        }
        return list;
    }

    /// <summary>The character with its props and the power's props in the rig, each held with its attachment's offset
    /// rotation (Ghost Rider's bike: yaw 90°).</summary>
    public static void AddProps(PropRig rig, MeshAnimator animator, IEnumerable<BaseProp> baseProps, IEnumerable<BaseProp> powerProps)
    {
        if (Environment.GetEnvironmentVariable("MHO_PROP_DEBUG") == "1")
            foreach (var p in baseProps.Concat(powerProps)) Console.WriteLine($"  prop {p.W.Class}: bone {p.W.Bone ?? "(none)"} -> #{(p.W.UseParentAnim ? -1 : PropRig.BoneFor(animator, p.W.Bone))}{(p.W.UseParentAnim ? " (parent anim)" : "")}, slots {string.Join("/", p.W.Slots)}");
        foreach (var p in baseProps.Concat(powerProps)) rig.Add(p.Mesh, p.W.UseParentAnim ? -1 : PropRig.BoneFor(animator, p.W.Bone), p.W.Slots, p.W.OnDemand, p.W.Class, p.Anim, p.Refs, p.W.UseParentAnim, p.W.Offset, p.Sockets);
    }

    static readonly Dictionary<string, Task<Dictionary<string, List<Fx.PowerIndex.PowerRef>>>> indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hero's power index (animation → the powers playing it), read once per hero and set of packages.</summary>
    public static Task<Dictionary<string, List<Fx.PowerIndex.PowerRef>>> PowerIndex(string hero, string cooked, IReadOnlyList<string> modFiles)
    {
        string key = hero + "|" + cooked + "|" + string.Join("|", modFiles);
        lock (indexes)
        {
            if (indexes.TryGetValue(key, out var t)) return t;
            return indexes[key] = GameDb(cooked).ContinueWith(d => d.Result is { } db ? Fx.PowerIndex.For(hero, cooked, modFiles, db) : Fx.PowerIndex.For(hero, cooked, modFiles));
        }
    }

    /// <summary>What a power does to the props while its animation plays: the rules (show / hide weapon slots, from its power
    /// packages' PowerFxMeshAttachment), when (its contact time), props it shows that the character doesn't have (a thrown car,
    /// from the power's own package), and the animated props' own animations of this name.</summary>
    public sealed record Switches(List<ModMeshes.PropRule> Rules, float Contact, List<BaseProp> Extra, Dictionary<string, AnimExportCli.Animation.BoneAnimation> Seqs)
    {
        /// <summary>How high the game lifts the hero while this plays (a high-flying travel power: Globals' HighFlyingHeight), else 0.</summary>
        public float Lift { get; init; }
        public static Switches None => new([], 0, [], new(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The prop switches for <paramref name="anim"/> (call off the UI thread).</summary>
    /// <param name="formRules">A form's own prop rules (its condition: Jean Grey's Phoenix wings on, cape off), always on.</param>
    /// <param name="formFiles">Where the form's props are looked for when the character doesn't have them.</param>
    public static Switches SwitchesFor(Fx.GameData? db, Dictionary<string, List<Fx.PowerIndex.PowerRef>> index, string? anim, string cooked, IReadOnlyList<PropRig.Prop> have, IEnumerable<(string Class, AnimRef Ref)> motionRefs, float seconds,
        IReadOnlyList<ModMeshes.PropRule>? formRules = null, IReadOnlyList<string>? formFiles = null)
    {
        List<Fx.PowerIndex.PowerRef>? refs = null;
        if (anim != null) index.TryGetValue(anim, out refs);
        if (refs == null && (formRules == null || formRules.Count == 0))
        {
            // no power plays it: no rules, but the animated props still play theirs (Rocket's mech suit, a transform's ability)
            var own = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
            foreach (var (cls, ar) in motionRefs) if (!own.ContainsKey(cls) && ModAnimations.Load(ar) is { } ba) own[cls] = ba;
            return own.Count == 0 ? Switches.None : new([], 0, [], own);
        }
        refs ??= [];
        var files = refs.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rules = files.SelectMany(ModMeshes.PropRules).Distinct().ToList();
        // (a form's rules hold the whole animation, whatever it is: its wings stay on)
        if (formRules != null) rules.AddRange(formRules.Select(r => r with { StartPoint = "power_on_start", StartOffset = 0, EndPoint = null, EndOffset = 0 }));
        if (formFiles != null) files.AddRange(formFiles.Where(f => !files.Contains(f, StringComparer.OrdinalIgnoreCase)));
        var extra = new List<BaseProp>();
        var extraSeqs = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rules.Where(r => r.Show && !have.Any(h => PropRig.Fills(h, r.Target))))
            foreach (string f in files)
            {
                var found = ModMeshes.Attachments(f).Where(x => PropRig.Fills(new PropRig.Prop(null!, x.Bone) { Slots = x.Slots, Class = x.Class }, r.Target)).ToList();
                if (found.Count == 0) continue;
                var inPkg = ModMeshes.List([(Path.GetFileName(f), f)], anyPackage: true);
                foreach (var x in found)
                    if (!extra.Any(e => e.W.Class.Equals(x.Class, StringComparison.OrdinalIgnoreCase)) && inPkg.FirstOrDefault(m => m.Name.Equals(x.Mesh, StringComparison.OrdinalIgnoreCase)) is { } mr)
                        try
                        {
                            if (ModMeshes.Load(mr, cooked, out _) is not { } lm) continue;
                            var w = new PropRig.Prop(mr, x.Bone) { Slots = x.Slots, OnDemand = true, Class = x.Class, Offset = ModMeshes.Rotator(x.OffsetRotation.Pitch, x.OffsetRotation.Yaw, x.OffsetRotation.Roll) };
                            // an animated one (a borrowed ride's vehicle) plays its own animation of this name, from the power's
                            // package: posed in its rest pose the bike sat ahead of and below its rider (Kurt, 2026-10-20)
                            if (x.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase))
                            {
                                var prefs = ModAnimations.For(mr, lm.Bones, [(Path.GetFileName(f), f)], cooked, minBones: 1);
                                extra.Add(new(w, lm, new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents) { InPlace = false }, prefs) { Sockets = PropRig.SocketsOf(mr) });
                                if (anim != null && prefs.FirstOrDefault(a => a.Name.Equals(anim, StringComparison.OrdinalIgnoreCase)) is { } pr && ModAnimations.Load(pr) is { } pba) extraSeqs[x.Class] = pba;
                            }
                            else extra.Add(new(w, lm, null, null) { Sockets = PropRig.SocketsOf(mr) });
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                break;
            }
        // the contact time: the power's AnimationContactTimePercent (else 0.4) of the animation
        float pct = 0.4f, lift = 0;
        if (db != null)
        {
            var byClass = Fx.PowerIndex.PrototypesByClass(db);
            var protos = refs.SelectMany(r => byClass.TryGetValue(r.Class, out var l) ? l : []).ToList();
            if (protos.FirstOrDefault() is string proto) pct = Fx.PowerEffects.ContactPercentOf(db, proto);
            if (protos.Any(p => PowerBorrow.Field(db, p, "HighFlying", 'B') is 1) && PowerBorrow.Field(db, "Globals/Globals.defaults", "HighFlyingHeight", 'D') is double hf) lift = (float)hf;
        }
        var seqs = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cls, ar) in motionRefs) if (!seqs.ContainsKey(cls) && ModAnimations.Load(ar) is { } ba) seqs[cls] = ba;
        foreach (var (cls, ba) in extraSeqs) seqs.TryAdd(cls, ba);
        return new(rules, pct * seconds, extra, seqs) { Lift = lift };
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
