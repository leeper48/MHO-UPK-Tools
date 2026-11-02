using AnimExportCli.Animation;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A 3D preview's props, one way for the Mods tab's preview (StorePreview, also the Editor's Powers tab) and the Model tab's
/// (PreviewPanel): the character's own props (PowerPreview.BaseProps), what the playing power does to them (its weapon slot
/// switches, the props it brings from its own package, the animated props' own animations: PowerPreview.SwitchesFor), and
/// the rig built from them. The preview says what it shows through the callbacks (phase 3 of the preview merge, step 1).
/// </summary>
sealed class PreviewProps
{
    readonly PropRig rig;
    readonly Control owner;

    /// <summary>The view, the character's animator and its mesh as shown now (null: nothing in 3D).</summary>
    public required Func<(ModelView View, MeshAnimator Animator, ModMeshes.Loaded Mesh)?> Shown { get; init; }
    /// <summary>The animation playing (null: rest pose) and its length in seconds.</summary>
    public required Func<BoneAnimation?> Playing { get; init; }
    public required Func<float> Seconds { get; init; }
    /// <summary>Draws the current pose (effects, props, the character).</summary>
    public required Action ShowPose { get; init; }
    /// <summary>The character as drawn (the Model tab's weight paint); null: as loaded.</summary>
    public Func<ModMeshes.Loaded, ModMeshes.Loaded>? Display { get; init; }
    /// <summary>Props left out by their attachment class (the Model tab: the target's props switched off before Save).</summary>
    public Func<string, bool>? Hidden { get; set; }
    /// <summary>The character class shown (a power's components can exclude it: EntityExcluded).</summary>
    public Func<string?>? Entity { get; set; }
    /// <summary>A prop's mesh shown in place of the game's (the Model tab's prop target: the model being fitted), by mesh; null
    /// keeps the game's. A replaced prop always shows (not only while a power shows it).</summary>
    public Func<MeshRef, ModMeshes.Loaded?>? Replace { get; set; }
    /// <summary>The replaced prop is the model being edited (the Model tab's prop target): it shows with Props off and when its
    /// power doesn't show it, and the weight colours (ModelView.Tint) are its. Else (the body target holding its prop as last
    /// fitted) the prop shows as the game's would.</summary>
    public bool ReplacedIsTarget { get; set; }
    /// <summary>The replacement as last put in the rig (its sections' place in the combined mesh: PropRig.SectionStart).</summary>
    public ModMeshes.Loaded? Replaced { get; private set; }
    /// <summary>More meshes for the rig after the props (the Mods tab's power actors).</summary>
    public Action<PropRig>? AddMore { get; init; }

    List<PowerPreview.BaseProp> baseProps = [];
    List<PowerPreview.BaseProp> powerProps = [];
    Dictionary<string, BoneAnimation> seqs = new(StringComparer.OrdinalIgnoreCase);
    List<ModMeshes.PropRule> rules = [];
    float contact;
    int loadRequest, switchRequest;

    public PreviewProps(PropRig rig, Control owner) { this.rig = rig; this.owner = owner; }

    float lift, rise = 600, fall = 600;
    int phase;

    /// <summary>
    /// Where the floor is under the character <paramref name="seconds"/> into the playing animation (the view's GroundZ): a
    /// high-flying travel power lifts the hero by Globals' HighFlyingHeight, moving at the hero's flyingZSpeed on the way up
    /// (from the start animation's first frame) and landingZSpeed on the way down (from the end's), as the game's files set
    /// them (PowerPreview.FlyingSpeeds; Kurt, 2026-11-02: a constant floor at full height showed Hawkeye's start far too
    /// high). The loop is at full height; 0 for anything else. Constant speeds are a reading of the property names, to be
    /// checked against the game.
    /// </summary>
    public float GroundAt(double seconds)
    {
        if (lift <= 0) return 0;
        float t = (float)Math.Max(0, seconds);
        return -(phase > 0 ? Math.Min(lift, rise * t) : phase < 0 ? Math.Max(0, lift - fall * t) : lift);
    }

    /// <summary>The playing power's lift (HighFlyingHeight) and the hero's speeds (null: not a high-flying power).</summary>
    public (float Lift, float Rise, float Fall)? Flight => lift > 0 ? (lift, rise, fall) : null;

    /// <summary>
    /// The character's props (none when <paramref name="on"/> is false), loaded in the background and held on their bones;
    /// then <paramref name="switches"/> reads the playing power's switches again: read before the props, the animated props
    /// found no animation (Blade's bike showed without its ride, turned 90°, or not at all; Kurt, 2026-10-03). The camera stays.
    /// </summary>
    public void Load(MeshRef main, IReadOnlyList<MeshRef> all, string? cooked, IEnumerable<(string File, string Path)> modPackages, bool on, Action switches)
    {
        if (Shown() is not { } now) return;
        int req = ++loadRequest;
        var replace = ReplacedIsTarget ? Replace : null;   // (a replaced prop shows with Props off too: the model being fitted, alone in the hand)
        if (!on && replace == null && rig.Count == 0 && baseProps.Count == 0) return;
        var meshes = all.ToList();
        var pkgs = modPackages.ToList();
        Task.Run(() => on ? PowerPreview.BaseProps(main, meshes, cooked, pkgs)
                : replace != null ? [.. PowerPreview.BaseProps(main, meshes, cooked, pkgs).Where(b => replace(b.W.Ref) != null)] : [])
            .ContinueWith(t =>
            {
                if (owner.IsDisposed || req != loadRequest || t.Status != TaskStatus.RanToCompletion) return;
                if (Shown() is not { } then || then.Animator != now.Animator || then.Mesh != now.Mesh) return;
                baseProps = t.Result;
                Rebuild();
                switches();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The character with its props, the power's and anything more (actors), as one mesh; the view and the pose
    /// kept (framed, and the saved view measured, by the character alone).</summary>
    public void Rebuild()
    {
        if (Shown() is not { } s) return;
        rig.Clear();
        // (a prop rigged to the character's skeleton is held at the root: its bind pose is already in place)
        rig.SetParentAnimation(Playing());
        Replaced = null;
        var shownProps = (Hidden == null ? baseProps : [.. baseProps.Where(b => !Hidden(b.W.Class))]).Select(b =>
        {
            if (Replace?.Invoke(b.W.Ref) is not { } rep) return b;
            Replaced = rep with { MeshTransform = b.Mesh.MeshTransform };
            return ReplacedIsTarget ? b with { W = b.W with { OnDemand = false }, Mesh = Replaced } : b with { Mesh = Replaced };
        }).ToList();
        PowerPreview.AddProps(rig, s.Animator, shownProps, powerProps);
        AddMore?.Invoke(rig);
        rig.SetRules(rules, contact, Seconds());
        rig.SetMotions(seqs);
        var keep = s.View.ViewState;
        s.View.ShowMesh(rig.Combine(Display?.Invoke(s.Mesh) ?? s.Mesh), s.Mesh.Positions.Length);
        s.View.TintStart = ReplacedIsTarget && Replaced != null ? Math.Max(0, rig.VertexStart(Replaced, s.Mesh.Positions.Length)) : 0;
        s.View.ViewState = keep;
        ShowPose();
    }

    /// <summary>
    /// The props for <paramref name="anim"/> (Kurt: Punisher's sawed-off shotgun, not his pistols): the power playing it
    /// switches weapon slots, and may bring a prop the hero doesn't have (a thrown car), read in the background; with no
    /// animation, no hero or Props off, the rest pose shows what's always held.
    /// </summary>
    public void LoadSwitches(bool on, string? cooked, string? hero, string? anim, IReadOnlyList<string> modFiles,
        IReadOnlyList<ModMeshes.PropRule>? formRules = null, IReadOnlyList<string>? formFiles = null)
    {
        int req = ++switchRequest;
        if (!on || cooked == null || hero == null)
        {
            lift = 0;
            if (Shown() is { } sh0) sh0.View.GroundZ = 0;
            if (rules.Count > 0 || powerProps.Count > 0) { rules = []; bool had = powerProps.Count > 0; powerProps = []; if (had) Rebuild(); else { rig.SetRules(rules, 0, 0); ShowPose(); } }
            return;
        }
        var have = baseProps.Select(x => x.W).ToList();
        var motionRefs = rig.MotionRefs(anim);
        var dbTask = PowerPreview.GameDb(cooked);
        var idxTask = PowerPreview.PowerIndex(hero, cooked, modFiles);
        float secs = Seconds();
        string? entity = Entity?.Invoke();
        // the hero's base package (the mod's copy first): its rise / fall speeds for a high-flying power
        string baseFile = $"UC__MarvelPlayer_{hero}_SF.upk";
        string basePath = modFiles.FirstOrDefault(f => Path.GetFileName(f).Equals(baseFile, StringComparison.OrdinalIgnoreCase)) ?? StockFiles.For(cooked, baseFile);
        Task.WhenAll(idxTask, dbTask).ContinueWith(done => (S: idxTask.Status != TaskStatus.RanToCompletion ? PowerPreview.Switches.None
                : PowerPreview.SwitchesFor(dbTask.Status == TaskStatus.RanToCompletion ? dbTask.Result : null, idxTask.Result, anim, cooked, have, motionRefs, secs, formRules, formFiles, entity),
                Speeds: PowerPreview.FlyingSpeeds(basePath, hero)))
            .ContinueWith(t0 =>
            {
                if (owner.IsDisposed || req != switchRequest || t0.Status != TaskStatus.RanToCompletion) return;
                var t = Task.FromResult(t0.Result.S);
                (rules, contact) = (t.Result.Rules, t.Result.Contact);
                // the floor under a high-flying power (GroundAt, drawn by the preview's ShowPose): rising, held, falling
                lift = t.Result.Lift;
                (rise, fall) = t0.Result.Speeds;
                phase = anim != null && anim.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ? 1 : anim != null && anim.EndsWith("_end", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
                seqs = t.Result.Seqs;
                bool rebuild = powerProps.Count > 0 || t.Result.Extra.Count > 0;
                powerProps = t.Result.Extra;
                if (rebuild) Rebuild();
                else { rig.SetRules(rules, contact, Seconds()); rig.SetMotions(seqs); ShowPose(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
