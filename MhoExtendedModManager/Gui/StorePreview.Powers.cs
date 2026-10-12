using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's power effects, power buttons and props (what the character holds, and what each power shows).</summary>
sealed partial class StorePreview
{
    void ClearEffects() => Effects.Clear();

    /// <summary>The game data (Calligraphy.sip), read once in the background for every power lookup.</summary>
    static Task<Fx.GameData?> GameDb(string cooked) => PowerPreview.GameDb(cooked);   // shared with the Model tab

    /// <summary>
    /// The effects of the power the playing animation belongs to (Powers on): the hero's power packages name it
    /// (PowerIndex, the mod's copies first), the game data gives the power with what it sets off (PowerEffects.For),
    /// then they play from the animation's time (PreviewEffects, shared with the Model tab). Nothing is added to the mod.
    /// </summary>
    void LoadEffects()
    {
        if (ownFxPackage != null) { LoadOwnEffects(); return; }
        ClearEffects();
        if (!(PreviewViews.Powers || ForceEffects) || playing == null || animator == null || mod == null || !MeshOk || CookedFolder is not string cooked || animBox == null) { ShowPose(); Invalidate(); return; }
        int ai = animBox.SelectedIndex - 1;
        if (ai < 0 || ai >= anims.Count) return;
        string anim = anims[ai].Name;
        var r = meshes[meshIndex];
        // The hero whose animations these are: UC__MarvelPlayer_<Hero>_… (a moved costume plays its target hero's).
        string? hero = HeroOf.Package(r.Package, cooked);
        if (hero == null) return;
        var modFiles = ModPackages(mod, skip: true).Select(x => x.Path).ToList();
        string? entity = Model.ConditionSwaps.ClassOf(r.Package);
        Effects.Load(cooked, true, db => db == null ? null : PowerPreview.ForAnimation(db, hero, cooked, modFiles, anim, entity, r.File, r.Name), SwapsFor(cooked, r));
    }

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
    public Func<string, PowerColor?>? ColorFor { get => colorFor; set { colorFor = value; if (effects != null) effects.ColorFor = value; } }
    Func<string, PowerColor?>? colorFor;
    /// <summary>The power buttons and the Power FXs toggle under the preview (off in the editor's Powers tab: its list picks the power).</summary>
    public bool PowerStrip { get; set; } = true;
    /// <summary>The editor's Powers tab: effects play even with Power FXs off in the main preview (they're what's being coloured).</summary>
    public bool ForceEffects { get; set; }
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
        var over = OverrideFiles?.Invoke() ?? new Dictionary<string, string>();
        var list = m.Manifest.UpkReplacements.Where(f => !skip || SkipModFile?.Invoke(f) != true)
            .Select(f => (f, over.TryGetValue(f, out var o) ? o : Path.Combine(m.Folder, f))).ToList();
        foreach (var (f, p) in over) if (!list.Any(x => x.f.Equals(f, StringComparison.OrdinalIgnoreCase))) list.Add((f, p));
        return list;
    }

    /// <summary>The overrides changed: the model, its animations and the effects read again.</summary>
    public void ReloadOverrides()
    {
        if (mod is not { } m) return;
        modStamp = "";
        mod = null;
        Mod = m;
    }
    /// <summary>The colour of the power playing changed (Powers tab): shown at once.</summary>
    public void RefreshPowerColor() => effects?.RefreshColor();

    /// <summary>Shows a power of the hero (as its power button): its animations only, the first one picked; paused unless
    /// <paramref name="play"/> (Kurt: the editor's Powers list doesn't start playing; ▶ does).</summary>
    // --- a character's own always-on effects (the editor's Powers tab, "<Name>: Own Effects"; Kurt, 2026-10-05: see the
    // Sinister clones' red glow before the game) ------------------------------------------------------------------------------
    string? ownFxPackage, ownProto, agentPower, agentAnim;
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
    public void PlayOwn(string packageFile, string proto)
    {
        ownFxPackage = packageFile; ownProto = proto; agentPower = null; agentAnim = null;
        powerFilter = null;
        // the character's own model (its mesh component's: the clones' packages also hold the cryopod they come out of)
        int k = meshes.FindIndex(r => r.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase));
        if (k >= 0 && Model.MhoSkeleton.ComponentMesh(meshes[k].File) is string body)
        {
            int kb = meshes.FindIndex(r => r.Package.Equals(packageFile, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(body, StringComparison.OrdinalIgnoreCase));
            if (kb >= 0) k = kb;
        }
        if (k >= 0 && k != meshIndex) { meshIndex = k; ownPending = true; LoadMesh(); return; }
        PlayOwnAnimation();
    }

    /// <summary>An NPC's or enemy's power (Fx.AgentPowers) on its character: the first of its animations the model has,
    /// playing with the power's effects (as a hero's power plays).</summary>
    public void PlayAgentPower(string packageFile, string proto, IReadOnlyList<string> animations)
    {
        string keep = proto;
        PlayOwn(packageFile, proto);
        agentPower = keep; ownProto = keep;
        agentAnim = animations.FirstOrDefault(a => allAnims.Any(x => x.Name.Equals(a, StringComparison.OrdinalIgnoreCase))) ?? animations.FirstOrDefault();
        if (!ownPending && agentAnim != null) PlayAnimation(agentAnim, true);
    }

    /// <summary>The idle (else the first animation) playing, which loads the own effects (LoadEffects).</summary>
    void PlayOwnAnimation()
    {
        if (agentAnim != null && allAnims.Any(x => x.Name.Equals(agentAnim, StringComparison.OrdinalIgnoreCase)) && PlayAnimation(agentAnim, true)) return;
        if (allAnims.Count == 0 || animBox == null) { LoadOwnEffects(); return; }
        int i = allAnims.FindIndex(a => a.Name.Equals("idle", StringComparison.OrdinalIgnoreCase));
        if (i < 0) i = allAnims.FindIndex(a => a.Name.Contains("idle", StringComparison.OrdinalIgnoreCase));
        if (!PlayAnimation(allAnims[Math.Max(0, i)].Name, true)) LoadOwnEffects();
    }

    /// <summary>After the model's animations loaded (LoadMesh): the own effects' idle, if they were waiting for it.</summary>
    void OwnAfterAnimations() { if (ownPending) { ownPending = false; PlayOwnAnimation(); } }

    void LoadOwnEffects()
    {
        ClearEffects();
        if (ownFxPackage is not string pkgFile || animator == null || mod == null || !MeshOk || CookedFolder is not string cooked) return;
        var r = meshes[meshIndex];
        var modFiles = ModPackages(mod, skip: true).Select(x => x.Path).ToList();
        string proto = ownProto ?? "";
        string? power = agentPower;
        string? ownHero = HeroOf.Package(r.Package, cooked);
        string? entity = Model.ConditionSwaps.ClassOf(r.Package);
        var phase = power != null && playing != null ? Fx.PowerEffects.Player.PhaseOf(playing.Name) : _ => true;
        // an NPC's or enemy's power (its effects with what it sets off), else the package's own always-on effects; the caption
        // and the colour by the entry the Powers tab lists (proto)
        Effects.Load(cooked, power != null, db =>
        {
            var fx = power != null && db != null ? PowerPreview.ForPower(db, power, ownHero, cooked, modFiles, entity, r.File, r.Name).Effects : Fx.PowerEffects.Own(new Fx.FxGame(cooked, modFiles), pkgFile);
            return new PowerPreview.Loaded(fx, proto, Fx.FxSockets.Of(r.File, r.Name), phase);
        }, power != null ? SwapsFor(cooked, r) : null, "Reading Effects…",
            l => power != null ? (l with { Power = power }).Label : l.Effects.Effects.Count == 0 ? "No Effects of Its Own" : $"Own Effects · {l.Effects.Effects.Count} Effect{(l.Effects.Effects.Count == 1 ? "" : "s")}");
    }

    public bool PlayPower(string prototype, bool play = false)
    {
        ownFxPackage = null; ownProto = null; agentPower = null; agentAnim = null;
        int k = heroPowers.FindIndex(p => p.Prototype.Equals(prototype, StringComparison.OrdinalIgnoreCase));
        if (k < 0) return false;
        // The frame slider keeps its place (Kurt, 2026-10-03: compare powers at the same moment): the next power's
        // animation opens at the same fraction of its length.
        if (playing != null && playSeconds > 0)
            keepFraction = Math.Clamp((PreviewViews.Loop ? playTime % playSeconds : Math.Min(playTime, playSeconds)) / playSeconds, 0, 1);
        if (powerFilter == heroPowers[k].Prototype) PowerClicked(k);   // (filtering already: back to all, then again)
        PowerClicked(k);
        if (!play) autoPlay = false;
        return true;
    }

    /// <summary>The hero's powers as the power buttons show them (empty until loaded).</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => heroPowers;
    /// <summary>The hero's powers with those that have effects but no animation (procs, passives): the editor's colors.</summary>
    public IReadOnlyList<Fx.PowerList.Power> AllHeroPowers => allHeroPowers;
    List<Fx.PowerList.Power> allHeroPowers = [];
    public event Action? HeroPowersLoaded;

    static string SplitWords(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>The effects moved on to <paramref name="seconds"/> (from where they were; started over when time went back: a loop).</summary>
    void FxAdvance(double seconds, bool ended) => effects?.Advance(seconds, ended);

    /// <summary>The effects as they are at <paramref name="seconds"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double seconds) => effects?.Replay(seconds);

    /// <summary>
    /// The props the game attaches to the shown character (PropRig.Attached: what its attachment classes name, from the
    /// mod's packages or its hero's base package), loaded in the background and shown with it; none when the Props toggle is
    /// off. Shared with the Model tab (PreviewProps).
    /// </summary>
    void LoadProps()
    {
        if (viewer == null || animator == null || shownLoaded == null || !MeshOk) return;
        Props.Load(meshes[meshIndex], meshes, CookedFolder, mod == null ? [] : ModPackages(mod), PreviewViews.Props, LoadPropSwitches);
    }

    PreviewProps? props;
    /// <summary>The character's props and the power's (shared with the Model tab); the power's animated actors join the rig.</summary>
    PreviewProps Props => props ??= new PreviewProps(rig, this)
    {
        Shown = () => viewer != null && animator != null && shownLoaded != null ? (viewer, animator, shownLoaded) : null,
        Playing = () => playing,
        Seconds = () => playSeconds,
        ShowPose = ShowPose,
        AddMore = r => effects?.AddActors(r),
    };

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept.</summary>
    void RebuildRig() => Props.Rebuild();

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
        heroPowers = []; allHeroPowers = []; powerRects.Clear(); hoverPower = -1; powerFilter = null; powerScroll = 0; powersLoaded = false;
        int req = ++heroPowersRequest;
        if (mod == null || !MeshOk || CookedFolder is not string cooked) { powersLoaded = true; return; }
        string? hero = HeroOfMesh();
        if (hero == null) { powersLoaded = true; return; }
        var modFiles = ModPackages(mod).Select(x => x.Path).ToList();
        var names = allAnims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            using var busy = Busy.Begin("3D view: listing the powers");
            if (dbt.Result is not { } db) return (Play: new List<Fx.PowerList.Power>(), All: new List<Fx.PowerList.Power>());
            return PowerPreview.Powers(db, hero, cooked, modFiles, names);   // shared: the buttons the Model tab shows
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != heroPowersRequest || t.Status != TaskStatus.RanToCompletion) return;
            heroPowers = t.Result.Play;
            allHeroPowers = t.Result.All;
            HeroPowersLoaded?.Invoke();
            powersLoaded = true;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The hero of the shown mesh's package (UC__MarvelPlayer_<Hero>_…), else null.</summary>
    string? HeroOfMesh()
    {
        if (!MeshOk) return null;
        return HeroOf.Package(meshes[meshIndex].Package, CookedFolder);
    }

    /// <summary>
    /// The animation a power button plays (Kurt, 2026-10-02: the loop of a start / loop / end power, not its start): one
    /// ending in "_loop" (absattack_channeledbeam_loop, movement_ridebike_loop); else the plain one beside a "_start"
    /// (movement_run_flying beside movement_run_flying_start); else the first.
    /// </summary>
    public static int PreferredAnim(IReadOnlyList<string> names) => PowerPreview.PreferredAnim(names);

    /// <summary>A power button clicked: its animations only (the loop of a start / loop / end power plays, else its first), or all again when it was filtering.</summary>
    void PowerClicked(int k)
    {
        if (animBox == null || k < 0 || k >= heroPowers.Count) return;
        var pw = heroPowers[k];
        if (pw.Animations.Count == 0 && MeshOk)
        {
            // effects only (no animation of its own): played over the idle, as an NPC's power is; again = back to all
            if (powerFilter == pw.Prototype) { powerFilter = null; ownFxPackage = null; ownProto = null; agentPower = null; anims = allAnims; FillAnims(); LoadEffects(); Invalidate(); return; }
            powerFilter = pw.Prototype;
            anims = allAnims; FillAnims();
            string keepProto = pw.Prototype;
            PlayOwn(meshes[meshIndex].Package, keepProto);
            agentPower = keepProto; ownProto = keepProto; powerFilter = keepProto;
            LoadOwnEffects();
            Invalidate();
            return;
        }
        if (agentPower != null && ownFxPackage != null) { ownFxPackage = null; ownProto = null; agentPower = null; }   // an effect-only power was playing
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
            if (anims.Count > 0) { autoPlay = true; animBox.SelectedIndex = PreferredAnim(anims.Select(a => a.Name).ToList()) + 1; }
        }
        Invalidate();
    }

    /// <summary>
    /// The props for the animation picked (Kurt: Punisher's sawed-off shotgun, not his pistols): the power playing it
    /// switches weapon slots (PreviewProps.LoadSwitches); the rest pose shows what's always held.
    /// </summary>
    void LoadPropSwitches()
    {
        string? cooked = CookedFolder;
        string? hero = mod != null && MeshOk && animBox != null ? HeroOfMesh() : null;
        int ai = animBox == null ? -1 : animBox.SelectedIndex - 1;
        string? anim = playing != null && ai >= 0 && ai < anims.Count ? anims[ai].Name : null;
        Props.LoadSwitches(PreviewViews.Props, cooked, hero, anim, mod == null ? [] : ModPackages(mod).Select(x => x.Path).ToList());
    }
}
