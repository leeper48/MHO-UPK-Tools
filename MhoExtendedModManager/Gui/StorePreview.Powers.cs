using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's power effects, power buttons and props (what the character holds, and what each power shows).</summary>
sealed partial class StorePreview
{
    void ClearEffects() { fxRequest++; fxPlayer = null; fxNote = ""; if (viewer != null) { viewer.Effects = []; viewer.EffectTris = []; } }

    /// <summary>The game data (Calligraphy.sip), read once in the background for every power lookup.</summary>
    Task<Fx.GameData?> GameDb(string cooked)
    {
        if (fxDb != null) return fxDb;
        string sip = Path.GetFullPath(Path.Combine(cooked, "..", "..", "..", "Data", "Game", "Calligraphy.sip"));
        return fxDb = Task.Run(() => { try { return File.Exists(sip) ? new Fx.GameData(Fx.SipArchive.Load(sip)) : null; } catch (Exception ex) when (ex is IOException or InvalidDataException) { return (Fx.GameData?)null; } });
    }

    /// <summary>
    /// The effects of the power the playing animation belongs to (Powers on): the hero's power packages name it
    /// (PowerIndex, the mod's copies first), the game data gives the power with what it sets off (PowerEffects.For),
    /// then they play from the animation's time. Read in the background; nothing is added to the mod.
    /// </summary>
    void LoadEffects()
    {
        ClearEffects();
        if (!(PreviewViews.Powers || ForceEffects) || playing == null || animator == null || mod == null || !MeshOk || CookedFolder is not string cooked || animBox == null) { ShowPose(); Invalidate(); return; }
        int ai = animBox.SelectedIndex - 1;
        if (ai < 0 || ai >= anims.Count) return;
        string anim = anims[ai].Name;
        var r = meshes[meshIndex];
        // The hero whose animations these are: UC__MarvelPlayer_<Hero>_… (a moved costume plays its target hero's).
        var parts = r.Package.Split('_', StringSplitOptions.RemoveEmptyEntries);
        string? hero = parts.Length >= 2 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3 ? parts[2] : null;
        if (hero == null) return;
        var modFiles = mod.Manifest.UpkReplacements.Where(f => SkipModFile?.Invoke(f) != true).Select(f => Path.Combine(mod.Folder, f)).ToList();
        int req = fxRequest;
        var a = animator; var l = shownLoaded;
        string meshFile = r.File, meshName = r.Name;
        fxNote = "Reading Powers…"; Invalidate();
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            var db = dbt.Result;
            if (db == null) return ((Fx.PowerEffects?)null, "", (Dictionary<string, (string, System.Numerics.Matrix4x4)>?)null, "");
            var idx = Fx.PowerIndex.For(hero, cooked, modFiles, db);
            if (!idx.TryGetValue(anim, out var powers) || powers.Count == 0) return (null, "", null, "");
            var byClass = Fx.PowerIndex.PrototypesByClass(db);
            var proto = powers.SelectMany(p => byClass.TryGetValue(p.Class, out var list) ? list : []).FirstOrDefault();
            if (proto == null) return (null, "", null, "");
            var fx = Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, proto, hero);
            return (fx, Path.GetFileNameWithoutExtension(proto), Fx.FxSockets.Of(meshFile, meshName), proto);
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != fxRequest || animator != a || playing == null || viewer == null) return;
            var (fx, power, sockets, proto) = t.Status == TaskStatus.RanToCompletion ? t.Result : (null, "", null, "");
            if (fx == null) { fxNote = ""; Invalidate(); return; }
            fxSockets = sockets ?? new();
            var phase = Fx.PowerEffects.Player.PhaseOf(anim);
            // The target of effects at the world position: the ground 250 units in front (characters face +X).
            float ground = l == null || l.Positions.Length == 0 ? 0 : l.Positions.Min(v => v.Z);
            fxPlayer = new Fx.PowerEffects.Player(fx, Socket, new System.Numerics.Vector3(250, 0, ground), phase) { AnimSeconds = Math.Max(0.1f, playSeconds), Color = ColorFor?.Invoke(proto) };
            fxPower = proto;
            viewer.EffectStrength = PreviewViews.FxPower;   // the effects' opacity / glow (default 15 %)
            int n = fx.Effects.Count(e => phase(e));
            fxNote = $"{Ui.TitleCase(SplitWords(power))} · {n} Effect{(n == 1 ? "" : "s")}";
            FxReplay(PreviewViews.Loop && playSeconds > 0 ? playTime % playSeconds : playTime);
            animator.Pose(playing, playSeconds > 0 ? (float)((PreviewViews.Loop ? playTime % playSeconds : playTime) / playSeconds * playFrames) : 0);
            ShowPose();
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The editor's Powers tab: a power's colour to show before it's saved (by prototype path; null = the game's).</summary>
    public Func<string, PowerColor?>? ColorFor { get; set; }
    /// <summary>The power buttons and the Power FXs toggle under the preview (off in the editor's Powers tab: its list picks the power).</summary>
    public bool PowerStrip { get; set; } = true;
    /// <summary>The editor's Powers tab: effects play even with Power FXs off in the main preview (they're what's being coloured).</summary>
    public bool ForceEffects { get; set; }
    /// <summary>The editor's Powers tab: the mod's packages the preview leaves out (the recoloured ones it built before, so a
    /// colour being changed isn't shown on top of the old one).</summary>
    public Func<string, bool>? SkipModFile { get; set; }
    string fxPower = "";

    /// <summary>The colour of the power playing changed (Powers tab): shown at once.</summary>
    public void RefreshPowerColor()
    {
        if (fxPlayer == null) return;
        fxPlayer.Color = ColorFor?.Invoke(fxPower);
        ShowPose();
    }

    /// <summary>Shows a power of the hero (as its power button): its animations only, the first one picked; paused unless
    /// <paramref name="play"/> (Kurt: the editor's Powers list doesn't start playing; ▶ does).</summary>
    public bool PlayPower(string prototype, bool play = false)
    {
        int k = heroPowers.FindIndex(p => p.Prototype.Equals(prototype, StringComparison.OrdinalIgnoreCase));
        if (k < 0) return false;
        if (powerFilter == heroPowers[k].Prototype) PowerClicked(k);   // (filtering already: back to all, then again)
        PowerClicked(k);
        if (!play) autoPlay = false;
        return true;
    }

    /// <summary>The hero's powers as the power buttons show them (empty until loaded).</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => heroPowers;
    public event Action? HeroPowersLoaded;

    static string SplitWords(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>A socket's place in the pose last made: the mesh's socket on its bone, else a bone of that name, else null.</summary>
    System.Numerics.Matrix4x4? Socket(string name)
    {
        if (animator == null) return null;
        if (fxSockets.TryGetValue(name, out var sk) && animator.BoneIndex(sk.Bone) is int b && b >= 0) return sk.Local * animator.BoneMatrix(b);
        int bi = animator.BoneIndex(name);
        return bi >= 0 ? animator.BoneMatrix(bi) : null;
    }

    /// <summary>The effects moved on to <paramref name="seconds"/> (from where they were; started over when time went back: a loop).</summary>
    void FxAdvance(double seconds, bool ended)
    {
        if (fxPlayer == null) return;
        if (seconds < fxTime - 1e-6) { fxPlayer.Reset(); fxTime = 0; }
        float dt = (float)(seconds - fxTime);
        if (dt > 0 || ended) fxPlayer.Step(Math.Max(0, dt), ended);
        fxTime = seconds;
    }

    /// <summary>The effects as they are at <paramref name="seconds"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double seconds)
    {
        if (fxPlayer == null || playing == null || animator == null) return;
        fxPlayer.Reset(); fxTime = 0;
        for (double t = 0; t + 1e-6 < seconds; t += 1.0 / 30)
        {
            animator.Pose(playing, playSeconds > 0 ? (float)(t / playSeconds * playFrames) : 0);
            fxPlayer.Step(1f / 30, false);
            fxTime = t + 1.0 / 30;
        }
    }

    /// <summary>
    /// The props the game attaches to the shown character (PropRig.Attached: other meshes of the mod's packages that a
    /// marvelattachment names), loaded in the background and shown with it; none when the Props toggle is off. The view
    /// (camera) stays as it was; the character's framing isn't changed by a prop.
    /// </summary>
    void LoadProps()
    {
        if (viewer == null || animator == null || shownLoaded == null || !MeshOk) return;
        var main = meshes[meshIndex];
        var l = shownLoaded;
        var a = animator;
        int req = request;
        bool on = PreviewViews.Props;
        var all = meshes.ToList();
        string? cooked = CookedFolder;
        if (!on && rig.Count == 0) return;
        // (in the background: the hero's base package may be read for its props)
        var modPkgs = mod == null ? [] : mod.Manifest.UpkReplacements.Select(f => (f, Path.Combine(mod.Folder, f))).ToList();
        Task.Run(() => (on ? PropRig.Attached(main, all, cooked) : []).Select(w =>
        {
            try
            {
                var lm = ModMeshes.Load(w.Ref, cooked, out _);
                // An animated prop gets its own animator and the animations made for its skeleton.
                if (lm != null && w.UseParentAnim)
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: (List<AnimRef>?)null);
                if (lm != null && w.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase))
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: ModAnimations.For(w.Ref, lm.Bones, modPkgs, cooked, minBones: 1));
                return (W: w, Mesh: lm, A: (MeshAnimator?)null, R: (List<AnimRef>?)null);
            }
            catch { return (W: w, Mesh: (ModMeshes.Loaded?)null, A: (MeshAnimator?)null, R: (List<AnimRef>?)null); }
        }).ToList())
            .ContinueWith(t =>
            {
                if (IsDisposed || req != request || viewer == null || animator != a || shownLoaded != l) return;
                baseProps = [.. t.Result.Where(x => x.Mesh != null).Select(x => (x.W, x.Mesh!, x.A, x.R))];
                RebuildRig();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The character's own props (LoadProps) and the ones the playing power brings from its own package (LoadPropSwitches:
    // Jean Grey's, Luke Cage's and Magneto's thrown cars are defined in their power packages).
    List<(PropRig.Prop W, ModMeshes.Loaded M, MeshAnimator? A, List<AnimRef>? R)> baseProps = [];
    List<(PropRig.Prop W, ModMeshes.Loaded M)> powerProps = [];
    Dictionary<string, AnimExportCli.Animation.BoneAnimation> propSeqs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept.</summary>
    void RebuildRig()
    {
        if (viewer == null || animator == null || shownLoaded == null) return;
        rig.Clear();
        // (a prop rigged to the character's skeleton is held at the root: its bind pose is already in place)
        rig.SetParentAnimation(playing);
        foreach (var (w, m, an, refs) in baseProps) rig.Add(m, w.UseParentAnim ? -1 : PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class, an, refs, w.UseParentAnim);
        foreach (var (w, m) in powerProps) rig.Add(m, PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class);
        rig.SetRules(propRules, propContact, playSeconds);
        rig.SetMotions(propSeqs);
        var keep = viewer.ViewState;
        viewer.ShowMesh(rig.Combine(shownLoaded), shownLoaded.Positions.Length);   // framed (and the saved view measured) by the character alone
        viewer.ViewState = keep;
        ShowPose();
    }

    /// <summary>One power button: its icon on a card; accent border while one of its animations plays, tinted while it filters.</summary>
    void DrawPower(Graphics g, Rectangle r, int k)
    {
        var pw = heroPowers[k];
        powerRects.Add((r, k));
        string? current = animBox != null && animBox.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? anims[animBox.SelectedIndex - 1].Name : null;
        bool playingIt = current != null && pw.Animations.Contains(current, StringComparer.OrdinalIgnoreCase);
        bool filtering = powerFilter == pw.Prototype;
        using var path = Ui.Round(r, 4 * S);
        using (var fill = new SolidBrush(filtering ? Color.FromArgb(70, Ui.Accent) : k == hoverPower ? Ui.CardHover : Ui.Card)) g.FillPath(fill, path);
        if (pw.Icon != null && CookedFolder is string cooked && Fx.PowerList.Icon(pw.Icon, cooked) is { } img)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int inset = (int)(4 * S);
            g.DrawImage(img, Rectangle.Inflate(r, -inset, -inset));
        }
        else TextRenderer.DrawText(g, string.Concat(pw.Name.Split(' ').Where(w => w.Length > 0).Take(2).Select(w => w[0])), Ui.Heavy(11f), r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var pen = new Pen(playingIt || filtering ? Ui.Accent : Ui.Line, playingIt || filtering ? Math.Max(2f, 2f * S) : 1f);
        g.DrawPath(pen, path);
    }

    /// <summary>The hero's powers for the power buttons (in the background; their icons decoded there too).</summary>
    void LoadHeroPowers()
    {
        heroPowers = []; powerRects.Clear(); hoverPower = -1; powerFilter = null; powerScroll = 0; powersLoaded = false;
        int req = ++heroPowersRequest;
        if (mod == null || !MeshOk || CookedFolder is not string cooked) { powersLoaded = true; return; }
        string? hero = HeroOfMesh();
        if (hero == null) { powersLoaded = true; return; }
        var modFiles = mod.Manifest.UpkReplacements.Select(f => Path.Combine(mod.Folder, f)).ToList();
        var names = allAnims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            if (dbt.Result is not { } db) return new List<Fx.PowerList.Power>();
            var list = Fx.PowerList.For(db, hero, cooked, modFiles).Where(p => p.Animations.Any(names.Contains)).ToList();
            foreach (var p in list) if (p.Icon != null) Fx.PowerList.Icon(p.Icon, cooked);   // decoded here, off the UI thread
            return list;
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != heroPowersRequest || t.Status != TaskStatus.RanToCompletion) return;
            heroPowers = t.Result;
            HeroPowersLoaded?.Invoke();
            powersLoaded = true;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The hero of the shown mesh's package (UC__MarvelPlayer_<Hero>_…), else null.</summary>
    string? HeroOfMesh()
    {
        if (!MeshOk) return null;
        var parts = meshes[meshIndex].Package.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[2] : null;
    }

    /// <summary>A power button clicked: its animations only (its first plays), or all again when it was filtering.</summary>
    void PowerClicked(int k)
    {
        if (animBox == null || k < 0 || k >= heroPowers.Count) return;
        var pw = heroPowers[k];
        string? current = animBox.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? anims[animBox.SelectedIndex - 1].Name : null;
        if (powerFilter == pw.Prototype)
        {
            powerFilter = null;
            anims = allAnims;
            FillAnims();
            int at = current == null ? -1 : anims.FindIndex(a => a.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) { fillingAnims = true; animBox.SelectedIndex = at + 1; fillingAnims = false; }   // the playing one stays
        }
        else
        {
            powerFilter = pw.Prototype;
            anims = [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))];
            FillAnims();
            if (anims.Count > 0) { autoPlay = true; animBox.SelectedIndex = 1; }
        }
        Invalidate();
    }

    List<ModMeshes.PropRule> propRules = [];
    float propContact;
    readonly Dictionary<string, Task<Dictionary<string, List<Fx.PowerIndex.PowerRef>>>> powerIndexCache = new(StringComparer.OrdinalIgnoreCase);
    int propSwitchRequest;

    /// <summary>
    /// The props for the animation picked (Kurt: Punisher's sawed-off shotgun, not his pistols): the power playing it
    /// (PowerIndex) switches weapon slots (its power package's PowerFxMeshAttachment), read in the background; the
    /// rest pose shows what's always held.
    /// </summary>
    void LoadPropSwitches()
    {
        int req = ++propSwitchRequest;
        if (!PreviewViews.Props || mod == null || !MeshOk || CookedFolder is not string cooked || animBox == null || HeroOfMesh() is not string hero)
        {
            if (propRules.Count > 0 || powerProps.Count > 0) { propRules = []; bool had = powerProps.Count > 0; powerProps = []; if (had) RebuildRig(); else { rig.SetRules(propRules, 0, 0); ShowPose(); } }
            return;
        }
        var have = baseProps.Select(x => x.W).ToList();
        int ai0 = animBox.SelectedIndex - 1;
        var motionRefs = rig.MotionRefs(playing != null && ai0 >= 0 && ai0 < anims.Count ? anims[ai0].Name : null);
        int ai = animBox.SelectedIndex - 1;
        string? anim = playing != null && ai >= 0 && ai < anims.Count ? anims[ai].Name : null;
        var modFiles = mod.Manifest.UpkReplacements.Select(f => Path.Combine(mod.Folder, f)).ToList();
        string key = hero + "|" + mod.Folder;
        var dbTask = GameDb(cooked);
        if (!powerIndexCache.TryGetValue(key, out var idxTask))
            powerIndexCache[key] = idxTask = dbTask.ContinueWith(t => t.Result is { } pdb ? Fx.PowerIndex.For(hero, cooked, modFiles, pdb) : Fx.PowerIndex.For(hero, cooked, modFiles));
        float seconds = playSeconds;
        Task.WhenAll(idxTask, dbTask).ContinueWith(done =>
            {
                if (anim == null || idxTask.Status != TaskStatus.RanToCompletion || !idxTask.Result.TryGetValue(anim, out var refs)) return (Rules: new List<ModMeshes.PropRule>(), Contact: 0f, Extra: new List<(PropRig.Prop, ModMeshes.Loaded)>(), Seqs: new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase));
                var files = refs.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rules = files.SelectMany(ModMeshes.PropRules).Distinct().ToList();
                // A prop the power shows that the character doesn't have: the power package's own attachment (a thrown car).
                var extra = new List<(PropRig.Prop, ModMeshes.Loaded)>();
                foreach (var r in rules.Where(r => r.Show && !have.Any(h => PropRig.Fills(h, r.Target))))
                    foreach (string f in files)
                    {
                        var found = ModMeshes.Attachments(f).Where(x => PropRig.Fills(new PropRig.Prop(null!, x.Bone) { Slots = x.Slots, Class = x.Class }, r.Target)).ToList();
                        if (found.Count == 0) continue;
                        var inPkg = ModMeshes.List([(Path.GetFileName(f), f)], anyPackage: true);
                        foreach (var x in found)
                            if (!extra.Any(e => e.Item1.Class.Equals(x.Class, StringComparison.OrdinalIgnoreCase)) && inPkg.FirstOrDefault(m => m.Name.Equals(x.Mesh, StringComparison.OrdinalIgnoreCase)) is { } mr)
                                try { if (ModMeshes.Load(mr, cooked, out _) is { } lm) extra.Add((new PropRig.Prop(mr, x.Bone) { Slots = x.Slots, OnDemand = true, Class = x.Class }, lm)); }
                                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                        break;
                    }
                // The contact time: the power's AnimationContactTimePercent (else 0.4) of the animation.
                float pct = 0.4f;
                if (dbTask.Status == TaskStatus.RanToCompletion && dbTask.Result is { } db)
                {
                    var byClass = Fx.PowerIndex.PrototypesByClass(db);
                    if (refs.SelectMany(r => byClass.TryGetValue(r.Class, out var l) ? l : []).FirstOrDefault() is string proto) pct = Fx.PowerEffects.ContactPercentOf(db, proto);
                }
                // The animated props' own animations of this name.
                var seqs = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
                foreach (var (cls, ar) in motionRefs) if (!seqs.ContainsKey(cls) && ModAnimations.Load(ar) is { } ba) seqs[cls] = ba;
                return (Rules: rules, Contact: pct * seconds, Extra: extra, Seqs: seqs);
            })
            .ContinueWith(t =>
            {
                if (IsDisposed || req != propSwitchRequest || t.Status != TaskStatus.RanToCompletion) return;
                (propRules, propContact) = (t.Result.Rules, t.Result.Contact);
                propSeqs = t.Result.Seqs;
                bool rebuild = powerProps.Count > 0 || t.Result.Extra.Count > 0;
                powerProps = t.Result.Extra;
                if (rebuild) RebuildRig();
                else { rig.SetRules(propRules, propContact, playSeconds); rig.SetMotions(propSeqs); ShowPose(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
