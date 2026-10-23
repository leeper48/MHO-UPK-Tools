using MhoPackageModifier;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's powers as the Mods tab and the editor's Powers tab use them: the power buttons, effects and props
/// are the shared 3D preview's (Preview3D); here the mod's packages it reads, a character's own effects and an NPC's power
/// (the Powers tab), and the Powers tab's colours.</summary>
sealed partial class StorePreview
{
    /// <summary>
    /// A power's condition material swaps on the shown model (Kurt, 2026-10-12: The Will of Doom's force field; Hulk's revive):
    /// the conditions its prototype applies, each condition package's ConditionFXMaterialSwaps for this costume, drawn in place
    /// of the sections' own materials (the slots a swap keeps stay); the mod's own condition packages first.
    /// </summary>
    Func<Fx.GameData, string, ModelView.Look?[]?> SwapsFor(string cooked, MeshRef r)
    {
        var own = (mod == null ? [] : ModPackages(mod, skip: true)).GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
        string cls = Model.ConditionSwaps.ClassOf(r.Package);
        return (db, power) => PowerPreview.Swaps(db, power, cooked, own, cls, null, r);
    }

    /// <summary>The editor's Powers tab: a power's colour to show before it's saved (by prototype path; null = the game's).</summary>
    public Func<string, PowerColor?>? ColorFor { get => colorFor; set { colorFor = value; if (p3 != null) p3.ColorFor = value; } }
    Func<string, PowerColor?>? colorFor;
    /// <summary>The power buttons and the Power FXs toggle under the preview (off in the editor's Powers tab: its list picks the power).</summary>
    public bool PowerStrip { get => powerStrip; set { powerStrip = value; if (p3 != null) p3.ShowPowerBlock = value; } }
    bool powerStrip = true;
    /// <summary>The editor's Powers tab: effects play even with Power FXs off in the main preview (they're what's being coloured).</summary>
    public bool ForceEffects { get => forceEffects; set { forceEffects = value; if (p3 != null) p3.ForceEffects = value; } }
    bool forceEffects;
    /// <summary>Show the 3D model whatever the mod's preview choice is (the editor's Powers page).</summary>
    public bool Always3D { get; set; }
    /// <summary>The editor's Powers tab: the mod's packages the preview leaves out (the recoloured ones it built before, so a
    /// colour being changed isn't shown on top of the old one).</summary>
    public Func<string, bool>? SkipModFile { get; set; }

    /// <summary>Packages the editor has built but not saved yet (a borrowed power look: Kurt, 2026-10-10), file → path: they
    /// take the place of the saved mod's (or join them) wherever the preview reads the mod's packages.</summary>
    public Func<IReadOnlyDictionary<string, string>>? OverrideFiles { get; set; }

    /// <summary>The mod's packages as the preview reads them: its own files, the editor's overrides in their place;
    /// <paramref name="skip"/> leaves out SkipModFile's.</summary>
    List<(string File, string Path)> ModPackages(Mod m, bool skip = false)
    {
        var over = (OverrideFiles?.Invoke() ?? new Dictionary<string, string>()).Where(kv => File.Exists(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);   // (a build deleted after Save: the saved file)
        var list = m.Manifest.UpkReplacements.Where(f => !skip || SkipModFile?.Invoke(f) != true)
            .Select(f => (f, over.TryGetValue(f, out var o) ? o : Path.Combine(m.Folder, f))).ToList();
        foreach (var (f, p) in over) if (!list.Any(x => x.f.Equals(f, StringComparison.OrdinalIgnoreCase))) list.Add((f, p));
        return list;
    }

    /// <summary>The overrides changed: the model, its animations and the effects read again.</summary>
    /// <summary>
    /// The mod after a save in the editor (Save and Keep Editing): the same folder holds the files the view shows already
    /// (its overrides were the unsaved builds), so the model, pose and camera stay; a renamed folder loads again.
    /// </summary>
    public void Rebind(Mod m)
    {
        if (mod == null || !mod.Folder.Equals(m.Folder, StringComparison.OrdinalIgnoreCase)) { Mod = m; return; }
        // a model shown from an unsaved work copy (an added prop, a borrowed look): what was saved may differ, so read again
        // (Kurt, 2026-10-23: after Save and Keep Editing the hammer was gone from the view, though the saved mod has it)
        if (meshes.Any(r => !Path.GetFullPath(r.File).StartsWith(Path.GetFullPath(m.Folder), StringComparison.OrdinalIgnoreCase))) { mod = m; ReloadOverrides(); return; }
        mod = m; modStamp = FilesStamp(m);
    }

    public void ReloadOverrides()
    {
        if (mod is not { } m) return;
        SaveAnim();   // (the animation and frame showing come back after the reload, not the last one saved: Kurt, 2026-10-23)
        // this view's own animation and frame, kept here (Kurt, 2026-10-23: a Prop Fit slider on Thor's idle jumped to the
        // travel power's run: the Powers tab's preview reloads too and saved its own animation under the same model's key);
        // one played from outside the model's list (the Animations tab's unsaved change, from the donor) is played again as is
        keepAnim = p3 == null || shownMesh == null ? null : (m.FolderName, shownMesh, p3.CurrentOutsideRef, p3.CurrentAnimation, p3.Time, p3.IsPlaying);
        ownPending = ownFxPackage != null;   // (a character's own effects come back once the model and its animations are in)
        modStamp = "";
        mod = null;
        Mod = m;
    }

    /// <summary>The colour of the power playing changed (Powers tab): shown at once.</summary>
    public void RefreshPowerColor() => p3?.RefreshPowerColor();

    // --- a character's own always-on effects (the editor's Powers tab, "<Name>: Own Effects"; Kurt, 2026-10-05: see the
    // Sinister clones' red glow before the game) ------------------------------------------------------------------------------
    string? ownFxPackage, ownProto, agentPower, agentAnim, ownPart;
    (string Anim, float Frame)? ownKeep;
    bool ownPending, overridesOff;

    /// <summary>The Powers tab: show the model's own materials where its component swaps them (a hologram at Opacity 0).</summary>
    public bool OverridesOff
    {
        get => overridesOff;
        set { if (overridesOff == value) return; overridesOff = value; if (meshIndex >= 0 && meshIndex < meshes.Count) { ownPending = ownFxPackage != null; LoadMesh(); } }
    }

    /// <summary>Shows a character package's own effects (PowerEffects.Own) on its model, playing its idle (or first animation);
    /// <paramref name="proto"/> is the color entry ("own:&lt;file&gt;"). A package without a model of its own shows them on the
    /// model shown.</summary>
    public void PlayOwn(string packageFile, string proto, string? part = null, (string Anim, float Frame)? keep = null)
    {
        bool reloading = ownPending;   // (a reload is under way: what's asked plays once the animations are in)
        ownFxPackage = packageFile; ownProto = proto; agentPower = null; agentAnim = null; ownPart = part; ownKeep = keep;
        if (reloading) return;
        // the character's own model (its mesh component's: the clones' packages also hold the cryopod they come out of)
        int k = meshes.FindIndex(r => r.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase));
        if (k >= 0 && Model.MhoSkeleton.ComponentMesh(meshes[k].File) is string body)
        {
            int kb = meshes.FindIndex(r => r.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(body, StringComparison.OrdinalIgnoreCase));
            if (kb >= 0) k = kb;
        }
        if (k >= 0 && k != meshIndex) { meshIndex = k; ownPending = true; LoadMesh(); return; }
        PlayOwnAnimation();
        // one part (the Parts list's ▶): its effects now, even when the idle was playing already (it only loads them as it starts)
        if (part != null) LoadOwnEffects();
    }

    /// <summary>An NPC's or enemy's power (Fx.AgentPowers) on its character: the first of its animations the model has,
    /// playing with the power's effects (as a hero's power plays).</summary>
    public void PlayAgentPower(string packageFile, string proto, IReadOnlyList<string> animations)
    {
        string keep = proto;
        PlayOwn(packageFile, proto);
        agentPower = keep; ownProto = keep;
        agentAnim = animations.FirstOrDefault(a => AllAnimations.Any(x => x.Name.Equals(a, StringComparison.OrdinalIgnoreCase))) ?? animations.FirstOrDefault();
        if (!ownPending && agentAnim != null) PlayAnimation(agentAnim, true);
    }

    /// <summary>The idle (else the first animation) playing, which loads the own effects (Preview3D.CustomEffects).</summary>
    void PlayOwnAnimation()
    {
        var all = AllAnimations;
        // an animation and frame to stay on, paused (the Powers tab's Dodge Ghosts: Kurt, 2026-10-17, Use This Pose went back
        // to a playing idle)
        if (ownKeep is { } keep && all.Any(x => x.Name.Equals(keep.Anim, StringComparison.OrdinalIgnoreCase)))
        {
            ownKeep = null;
            if (PlayAnimation(keep.Anim, false) && p3 != null)
            {
                if (p3.Frames > 0) p3.Time = keep.Frame / p3.Frames * p3.Seconds;
                return;
            }
        }
        if (agentAnim != null && all.Any(x => x.Name.Equals(agentAnim, StringComparison.OrdinalIgnoreCase)) && PlayAnimation(agentAnim, true)) return;
        if (all.Count == 0 || p3 == null) { LoadOwnEffects(); return; }
        int i = all.ToList().FindIndex(a => a.Name.Equals("idle", StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = all.ToList().FindIndex(a => a.Name.Contains("idle", StringComparison.OrdinalIgnoreCase));
        if (!PlayAnimation(all[Math.Max(0, i)].Name, true)) LoadOwnEffects();
    }

    /// <summary>After the model's animations loaded (LoadMesh): the own effects' idle, if they were waiting for it.</summary>
    void OwnAfterAnimations() { if (ownPending) { ownPending = false; PlayOwnAnimation(); } }

    /// <summary>A character's own effects, or an NPC's power, through the shared effects player.</summary>
    void LoadOwnEffects()
    {
        if (p3 == null) return;
        var fx = p3.EffectsPlayer;
        if (ownFxPackage is not string pkgFile || p3.Animator == null || mod == null || !MeshOk || CookedFolder is not string cooked) { fx.Clear(); return; }
        var r = meshes[meshIndex];
        var modFiles = ModPackages(mod, skip: true).Select(x => x.Path).ToList();
        string proto = ownProto ?? "";
        string? power = agentPower;
        string? part = ownPart;
        string? ownHero = HeroOf.Package(r.Package, cooked);
        string? entity = Model.ConditionSwaps.ClassOf(r.Package);
        var playing = p3.Playing;
        var phase = power != null && playing != null ? Fx.PowerEffects.Player.PhaseOf(playing.Name) : _ => true;
        // an NPC's or enemy's power (its effects with what it sets off), else the package's own always-on effects; the caption
        // and the colour by the entry the Powers tab lists (proto)
        fx.Load(cooked, power != null, db =>
        {
            var e = power != null && db != null ? PowerPreview.ForPower(db, power, ownHero, cooked, modFiles, entity, r.File, r.Name).Effects : Fx.PowerEffects.Own(new Fx.FxGame(cooked, modFiles), pkgFile, part);
            return new PowerPreview.Loaded(e, proto, Fx.FxSockets.Of(r.File, r.Name), phase);
        }, power != null ? SwapsFor(cooked, r) : null, "Reading Effects…",
            l => power != null ? (l with { Power = power }).Label : part != null ? (l.Effects.Effects.Count == 0 ? $"{part}: Nothing to Show" : $"{part} · Looping")
                : l.Effects.Effects.Count == 0 ? "No Effects of Its Own" : $"Own Effects · {l.Effects.Effects.Count} Effect{(l.Effects.Effects.Count == 1 ? "" : "s")}");
    }

    /// <summary>Shows a power of the hero (as its power button): its animations only, the first one picked; paused unless
    /// <paramref name="play"/> (Kurt: the editor's Powers list doesn't start playing; ▶ does).</summary>
    public bool PlayPower(string prototype, bool play = false)
    {
        ownFxPackage = null; ownProto = null; agentPower = null; agentAnim = null; ownPart = null;
        if (p3 != null) p3.EffectsPlayer.OnlyEffect = null;
        return p3 != null && p3.PlayPower(prototype, play);
    }

    /// <summary>
    /// One effect of a power, alone (the Powers tab's Parts ▶ on a power's effect: Jean Grey's Phoenix flames, a condition's
    /// effect the power applies; Kurt, 2026-10-19: they said "Nothing to Show"): the power plays (in its form, if it has one)
    /// with only that effect.
    /// </summary>
    public void PlayPowerPart(string prototype, string part)
    {
        ownFxPackage = null; ownProto = null; agentPower = null; agentAnim = null; ownPart = null;
        if (p3 == null) return;
        p3.EffectsPlayer.OnlyEffect = part;
        if (p3.ShowsPower(prototype)) p3.ReloadEffects();
        else p3.PlayPower(prototype, true);
    }

    /// <summary>The hero's powers as the power buttons show them (empty until loaded).</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => p3?.HeroPowers ?? [];
    /// <summary>The hero's powers with those that have effects but no animation (procs, passives): the editor's colors.</summary>
    public IReadOnlyList<Fx.PowerList.Power> AllHeroPowers => p3?.AllHeroPowers ?? [];
    public event Action? HeroPowersLoaded;
    /// <summary>The hero's powers have been read (or there are none to read).</summary>
    public bool HeroPowersReady => p3?.PowersLoaded ?? false;

    /// <summary>The animation showing and its frame (the Powers tab's Use This Pose); null while none plays.</summary>
    /// <summary>Effect parts left out of the preview by name (the Powers tab's Parts switched off).</summary>
    public Func<string, bool>? HiddenEffect { get => hiddenEffect; set { hiddenEffect = value; if (p3 != null) p3.EffectsPlayer.HiddenEffect = value; } }
    Func<string, bool>? hiddenEffect;

    /// <summary>The effects again (a part switched on or off): a character's own, else the power's.</summary>
    public void ReloadEffects() { if (ownFxPackage != null) LoadOwnEffects(); else p3?.ReloadEffects(); }

    /// <summary>Shows <paramref name="anim"/> at <paramref name="frame"/>, paused (Save and Keep Editing puts the pose back).</summary>
    public bool HoldPose(string anim, float frame)
    {
        if (p3 == null || !PlayAnimation(anim, false)) return false;
        if (p3.Frames > 0) p3.Time = frame / p3.Frames * p3.Seconds;
        return true;
    }

    /// <summary>The animation is playing (not paused).</summary>
    public bool IsPlaying => p3?.IsPlaying == true;
    // (the Frame slider's place: dragged while paused it's ahead of the playback clock, which read frame 0; Kurt, 2026-10-17)
    public (string Anim, float Frame)? CurrentPose => p3?.Playing == null || p3.Seconds <= 0 ? null : (p3.CurrentAnimation ?? "", p3.FrameSlider.Value * p3.Frames);

    /// <summary>Debug (the Powers tab's test hook): the effects playing, with what draws each emitter.</summary>
    public string EffectsDebug() => p3?.EffectsPlayer.Player?.Describe() ?? "no effects playing";

    /// <summary>The hero of the shown mesh's package (UC__MarvelPlayer_&lt;Hero&gt;_…), else null.</summary>
    string? HeroOfMesh() => MeshOk ? HeroOf.Package(meshes[meshIndex].Package, CookedFolder) : null;
}
