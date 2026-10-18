using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The preview's 3D view: the shared 3D preview (Preview3D, as the Model tab's), with what the Mods tab adds: the mod's
/// meshes, its light, lens and glow, the camera and the animation kept per mesh on this PC (PreviewViews), and pictures.</summary>
sealed partial class StorePreview
{
    Preview3D? p3;
    /// <summary>Holds the power block under the pictures strip (Preview3D.PlacePowerBlock).</summary>
    Panel? powerArea;

    /// <summary>The 3D preview, made the first time a mesh shows.</summary>
    Preview3D P3()
    {
        if (p3 != null) return p3;
        p3 = new Preview3D(new ModPrefs(this)) { Visible = false, BackColor = Ui.Card };
        p3.View.Background = Ui.Card; p3.View.BackColor = Ui.Card;
        p3.CanRun = () => { if (show3D && Visible) return true; SaveAnim(); return false; };   // hidden: hold the frame
        p3.LightHome = () => mod == null ? 1f : PreviewViews.AuthorLight(mod);
        p3.ResetViewAction = ResetView;
        p3.ColorFor = colorFor;
        p3.ForceEffects = forceEffects;
        p3.ShowPowerBlock = powerStrip;
        p3.CustomEffects = () => { if (ownFxPackage == null) return false; LoadOwnEffects(); return true; };
        p3.ViewChanged += () => { if (mod != null && MeshOk && show3D) PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), p3.View.ViewState); };
        p3.AnimationPicked += (name, user) =>
        {
            if (mod == null || !MeshOk) return;
            if (name != null && StartView(meshes[meshIndex]) == null) p3.View.ZoomOut(1.25f);   // room for reaching and lunging
            // A restored animation is shown as it was left, not a new pick (no undo step, the preview choice unchanged).
            if (user) Picked?.Invoke(mod, CurrentMeshKey());
            SaveAnim();
            Invalidate();
        };
        p3.PlaybackMoved += SaveAnim;
        p3.HeroPowersLoaded += () => { HeroPowersLoaded?.Invoke(); Invalidate(); };
        p3.CaptionChanged += Invalidate;
        powerArea = new Panel { BackColor = Color.Transparent, Visible = false };
        Controls.Add(p3);
        Controls.Add(powerArea);
        p3.PlacePowerBlock(powerArea);
        return p3;
    }

    /// <summary>The Mods tab's look switches: the toggles and Loop for every mod (PreviewViews), the light, lens and glow per mod.</summary>
    sealed class ModPrefs(StorePreview owner) : IPreviewPrefs
    {
        public bool Spec { get => PreviewViews.Spec; set => PreviewViews.Spec = value; }
        public bool Reflect { get => PreviewViews.Reflect; set => PreviewViews.Reflect = value; }
        public bool Glow { get => PreviewViews.Glow; set => PreviewViews.Glow = value; }
        public bool Bloom { get => PreviewViews.Bloom; set => PreviewViews.Bloom = value; }
        public bool Ground { get => PreviewViews.Ground; set => PreviewViews.Ground = value; }
        public bool Props { get => PreviewViews.Props; set => PreviewViews.Props = value; }
        public bool Powers { get => PreviewViews.Powers; set => PreviewViews.Powers = value; }
        public bool Loop { get => PreviewViews.Loop; set => PreviewViews.Loop = value; }
        public float Light { get => owner.mod == null ? 1 : PreviewViews.Light(owner.mod); set { if (owner.mod != null) PreviewViews.SetLight(owner.mod, value); } }
        public float Lens { get => owner.mod == null ? ModelView.DefaultFocalLength : PreviewViews.Lens(owner.mod); set { if (owner.mod != null) PreviewViews.SetLens(owner.mod, value); } }
        public float GlowStrength { get => owner.mod == null ? 1 : PreviewViews.GlowStrength(owner.mod); set { if (owner.mod != null) PreviewViews.SetGlowStrength(owner.mod, value); } }
        public void Save() { }   // (PreviewViews saves as it's set)
    }

    /// <summary>Shows meshes[meshIndex] in the 3D view (read in the background; the last few are kept).</summary>
    void LoadMesh()
    {
        if (mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        var view = P3();
        var r = meshes[meshIndex];
        view.Visible = true;
        SaveAnim();
        shownMesh = null;
        view.ShowMessage("Loading the 3D view…");
        view.ReloadPrefs();   // this mod's light, lens and glow
        Invalidate();
        string key = r.File + "|" + r.Export + "|" + Ui.FileStamp(r.File) + (overridesOff ? "|plain" : "");
        if (meshCache.TryGetValue(key, out var hit) && hit != null) { Show(hit); return; }
        int req = ++request;
        string? cooked = CookedFolder;
        bool plain = overridesOff;
        Task.Run(() => { using var busy = Busy.Begin("3D view: loading the model"); try { var l = ModMeshes.Load(r, cooked, out string why, componentMaterials: !plain); return (l, why); } catch (Exception ex) { return ((ModMeshes.Loaded?)null, ex.Message); } }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || p3 == null) return;
            var (loaded, why) = t.Result;
            if (meshCache.Count > 3) meshCache.Clear();   // with their mipmapped maps, a few are enough
            meshCache[key] = loaded;
            if (loaded == null) p3.ShowMessage($"{r.Name} can't be shown in 3D ({why}).");
            else Show(loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        void Show(ModMeshes.Loaded l)
        {
            if (p3 == null || mod == null) return;
            var animator = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
            string? cooked2 = CookedFolder;
            // The hero whose animations these are: UC__MarvelPlayer_<Hero>_… (a moved costume plays its target hero's).
            string? hero = HeroOf.Package(r.Package, cooked2);
            var ctx = new PreviewContext(hero, cooked2, r, meshes, ModPackages(mod, skip: true), () => Model.ConditionSwaps.ClassOf(r.Package), c => SwapsFor(c, r))
            { PropPackages = ModPackages(mod) };
            p3.FullTitle = $"Preview: {mod.Name}";
            p3.ShowCharacter(l, animator, [], ctx, null, 0, false, StartView(r), samePowers: false);
            shownMesh = r.Key;
            Invalidate();
            var m = mod;
            var pkgs = ModPackages(m);
            int req2 = request;
            Task.Run(() => { using var busy = Busy.Begin("3D view: reading the animations"); try { return ModAnimations.For(r, l.Bones, pkgs, cooked2); } catch { return []; } }).ContinueWith(t =>
            {
                if (IsDisposed || req2 != request || p3 == null || mod?.FolderName != m.FolderName || p3.Animator != animator) return;   // (a reload of the same mod is a new object)
                // This PC's last animation and frame for the mesh, else the pick's "@animation".
                var saved = PreviewViews.GetAnim(PreviewViews.Key(mod, r));
                string? name = saved?.Name ?? wantedAnim;
                double? at = saved?.Name != null && name != null && saved.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? saved.Time : null;
                p3.SetAnimations(t.Result, name, at, false);
                AnimationsLoaded?.Invoke();
                OwnAfterAnimations();
                Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>The view a mesh starts from: the one turned to on this PC, else the mod author's (manifest PreviewViews).</summary>
    float[]? StartView(MeshRef r) =>
        mod == null ? null : PreviewViews.Get(PreviewViews.Key(mod, r)) ?? (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var v) ? v : null);

    /// <summary>Back to the mod author's view when it has one, else the default framing; this PC's view is forgotten.</summary>
    void ResetView()
    {
        if (p3 == null || mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        var r = meshes[meshIndex];
        PreviewViews.Set(PreviewViews.Key(mod, r), null);
        if (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var author)) p3.View.ViewState = author;
        else p3.View.ResetView();
    }

    /// <summary>The shown mesh's animations (all of them, also while a power button filters the drop-down).</summary>
    public IReadOnlyList<AnimRef> AllAnimations => p3?.AllAnimations ?? [];
    /// <summary>The shown mesh's animations are loaded (AllAnimations).</summary>
    public event Action? AnimationsLoaded;

    /// <summary>Picks an animation by name (the editor's Animations tab): every animation back in the drop-down, that one
    /// selected and, with <paramref name="play"/>, playing. False when the shown mesh has no animation of that name.</summary>
    public bool PlayAnimation(string name, bool play = true) => p3 != null && p3.PlayAnimation(name, play);

    /// <summary>Plays an animation from anywhere on the shown model (the Animations tab's picker: try another character's
    /// before using it). It shows at the top of the drop-down until the mesh's own list is shown again.</summary>
    public bool PlayRef(AnimRef a, bool play = true) => p3 != null && p3.PlayRef(a, play);

    /// <summary>A picture shows: the 3D view is hidden but kept (mesh, animation paused where it was, camera).</summary>
    void Hide3D()
    {
        if (p3 == null) return;
        if (p3.IsFull) p3.ToggleFull();   // a picture (or another mod without 3D) shows: back from full screen
        if (p3.IsPlaying) resumeOnReveal = true;
        p3.Pause();
        SaveAnim();
        p3.Visible = false;
        if (powerArea != null) powerArea.Visible = false;
    }
    bool resumeOnReveal;

    /// <summary>Back to 3D on the mesh the view still holds: shown as it was left. False when it has to be loaded.</summary>
    bool Reveal3D()
    {
        if (p3 == null || !MeshOk || shownMesh != meshes[meshIndex].Key) return false;
        p3.Visible = true;
        if (resumeOnReveal && p3.Playing != null) p3.Play();   // it was playing: carry on
        resumeOnReveal = false;
        Invalidate();
        return true;
    }

    /// <summary>P (Kurt, 2026-10-04: the 3D views' play / pause key): plays or pauses the shown animation; false when the 3D
    /// view has no animation picked (P then does nothing).</summary>
    public bool TogglePlayback() => show3D && p3 != null && p3.TogglePlayback();

    /// <summary>Esc (Kurt): pauses a playing animation; false when nothing was playing (Esc then does its usual job).</summary>
    public bool PausePlayback() => show3D && p3 != null && p3.PausePlayback();

    /// <summary>Into or out of full screen (the 3D view; F11).</summary>
    public void ToggleFull() { if (p3 != null && (show3D || p3.IsFull)) p3.ToggleFull(); }

    /// <summary>Stores the shown mesh's animation and the frame it's on (preview_views.json), so it comes back as it was.</summary>
    void SaveAnim()
    {
        if (mod == null || shownMesh == null || p3 == null) return;
        if (p3.CurrentAnimation != null && p3.Playing == null) return;   // still loading
        double t = p3.Seconds <= 0 ? 0 : Math.Min(p3.Time, p3.Seconds);
        PreviewViews.SetAnim(PreviewViews.Key(mod, shownMesh), p3.CurrentAnimation, t);
    }
}
