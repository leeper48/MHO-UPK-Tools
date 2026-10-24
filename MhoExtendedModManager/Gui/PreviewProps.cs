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
    /// <summary>More meshes for the rig after the props (the Mods tab's power actors).</summary>
    public Action<PropRig>? AddMore { get; init; }

    List<PowerPreview.BaseProp> baseProps = [];
    List<PowerPreview.BaseProp> powerProps = [];
    Dictionary<string, BoneAnimation> seqs = new(StringComparer.OrdinalIgnoreCase);
    List<ModMeshes.PropRule> rules = [];
    float contact;
    int loadRequest, switchRequest;

    public PreviewProps(PropRig rig, Control owner) { this.rig = rig; this.owner = owner; }

    /// <summary>
    /// The character's props (none when <paramref name="on"/> is false), loaded in the background and held on their bones;
    /// then <paramref name="switches"/> reads the playing power's switches again: read before the props, the animated props
    /// found no animation (Blade's bike showed without its ride, turned 90°, or not at all; Kurt, 2026-10-03). The camera stays.
    /// </summary>
    public void Load(MeshRef main, IReadOnlyList<MeshRef> all, string? cooked, IEnumerable<(string File, string Path)> modPackages, bool on, Action switches)
    {
        if (Shown() is not { } now) return;
        int req = ++loadRequest;
        if (!on && rig.Count == 0 && baseProps.Count == 0) return;
        var meshes = all.ToList();
        var pkgs = modPackages.ToList();
        Task.Run(() => on ? PowerPreview.BaseProps(main, meshes, cooked, pkgs) : [])
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
        PowerPreview.AddProps(rig, s.Animator, Hidden == null ? baseProps : [.. baseProps.Where(b => !Hidden(b.W.Class))], powerProps);
        AddMore?.Invoke(rig);
        rig.SetRules(rules, contact, Seconds());
        rig.SetMotions(seqs);
        var keep = s.View.ViewState;
        s.View.ShowMesh(rig.Combine(Display?.Invoke(s.Mesh) ?? s.Mesh), s.Mesh.Positions.Length);
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
        Task.WhenAll(idxTask, dbTask).ContinueWith(done => idxTask.Status != TaskStatus.RanToCompletion ? PowerPreview.Switches.None
                : PowerPreview.SwitchesFor(dbTask.Status == TaskStatus.RanToCompletion ? dbTask.Result : null, idxTask.Result, anim, cooked, have, motionRefs, secs, formRules, formFiles, entity))
            .ContinueWith(t =>
            {
                if (owner.IsDisposed || req != switchRequest || t.Status != TaskStatus.RanToCompletion) return;
                (rules, contact) = (t.Result.Rules, t.Result.Contact);
                if (Shown() is { } sh) sh.View.GroundZ = -t.Result.Lift;
                seqs = t.Result.Seqs;
                bool rebuild = powerProps.Count > 0 || t.Result.Extra.Count > 0;
                powerProps = t.Result.Extra;
                if (rebuild) Rebuild();
                else { rig.SetRules(rules, contact, Seconds()); rig.SetMotions(seqs); ShowPose(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
