using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Numerics;
using AnimExportCli.Animation;
using AnimExportCli.Meshes;
using MhoExtendedModManager;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The window's 3D preview: the retargeted MFF model on the base hero's skeleton, in the Mod Manager's 3D view (vendored
/// ModelView), posed by the hero's own animations (MEMM's ModAnimations / MeshAnimator, in the game's AnimSet order).
/// Laid out and behaving as the Mod Manager's preview (0.13.0, Kurt: "all the items learned about the viewer from MEMM"):
/// the playback bar under the view (animation, ▶, Loop, Frame), then Look ▾ (Spec / Reflect / Glow / Bloom / Props /
/// Power FXs / Bones, the Light / Glow / Lens sliders), Compare, Reset View, the Head / Bust / Full Body framing icons and
/// full screen (⛶ / F11), then the hero's power buttons (PreviewPanel.Powers.cs: a click shows that power's animations
/// and plays its effects; the hero's props follow the powers). Smaller frames while playing (ModelView.Moving). The look
/// and playback switches are remembered (Settings.Preview). The model side is PreviewPanel.Model.cs.
/// </summary>
sealed partial class PreviewPanel : UserControl
{
    // The 3D preview itself (shared with the Mods tab's preview: Preview3D); this panel adds the Model tab's own: Compare,
    // Single Animation ▾ (edits), the bones overlay, the weight paint and the prepared models (PreviewPanel.Model.cs).
    readonly Preview3D p3;
    /// <summary>The target's props left out by attachment class (the Parts tab's target props), until Save writes them.</summary>
    public Func<string, bool>? HiddenProps { get => p3.HiddenProps; set => p3.HiddenProps = value; }
    /// <summary>Shows the props again after HiddenProps' answers changed.</summary>
    public void RefreshProps() => p3.RebuildRig();
    /// <summary>The move / turn gizmo (the Model tab: an added prop's fit): what to draw, asked at every paint.</summary>
    public Func<ModelView.TransformGizmo?>? GizmoSource { get => view.GizmoSource; set { view.GizmoSource = value; view.Invalidate(true); } }
    public event Action<int, float>? GizmoMoved { add => view.GizmoMoved += value; remove => view.GizmoMoved -= value; }
    public event Action<int, float>? GizmoTurned { add => view.GizmoTurned += value; remove => view.GizmoTurned -= value; }
    public event Action? GizmoReleased { add => view.GizmoReleased += value; remove => view.GizmoReleased -= value; }
    public (System.Numerics.Matrix4x4 Frame, float Size)? PropBase(Func<string, bool> match) => p3.PropBase(match);
    public (System.Numerics.Vector3 Mid, int Count)? PropMid(Func<string, bool> match) => p3.PropMid(match);
    public void SetPropOffset(Func<string, bool> match, System.Numerics.Matrix4x4 offset) => p3.SetPropOffset(match, offset);
    internal bool TestGizmoDrag(int handle, int dx, int dy, bool release = true) => view.TestGizmoDrag(handle, dx, dy, release);
    internal void TestGizmoRelease() => view.TestGizmoRelease();
    internal bool TestGizmoMouse(int handle, int dx, int dy, int steps, bool release) => view.TestGizmoMouse(handle, dx, dy, steps, release);
    internal void TestGizmoMouseUp() => view.TestGizmoMouseUp();
    internal (System.Numerics.Vector3 Mid, System.Numerics.Vector3 Dir)? PropShownForTest(Func<string, bool> match) => p3.PropShownForTest(match);
    internal int GizmoLongestArrowForTest() => view.GizmoLongestArrow();
    readonly Button compare, edits;
    ModelView view => p3.View;
    MeshAnimator? animator => p3.Animator;
    ModMeshes.Loaded? shownLoaded => p3.Mesh;

    MeshAnimator? mffAnimator, stockAnimator;
    Prepared? shown;
    bool comparing, targetOnly;

    /// <summary>Edits from Blender (0.16.0): the imported animation for a name (null = the game's), whether a name has one,
    /// and the Single Animation ▾ menu (filled by the main window).</summary>
    public Func<AnimRef, BoneAnimation?, BoneAnimation?>? Override;
    public Func<string, bool>? IsOverridden;
    public Action<ContextMenuStrip>? FillEditsMenu;
    /// <summary>The animation picked in the drop-down (null = rest pose).</summary>
    public string? CurrentAnimation => p3.CurrentAnimation;
    /// <summary>Every animation of the shown model (not only the power filter's).</summary>
    public IReadOnlyList<string> AnimationNames => p3.AllAnimations.Select(a => a.Name).ToList();
    /// <summary>The animations as the preview plays them (the game's: a later set's wins), by name, for an export.</summary>
    public Dictionary<string, MhoAnimRef> AnimationsForExport()
    {
        var d = new Dictionary<string, MhoAnimRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in p3.AllAnimations) d.TryAdd(a.Name, new MhoAnimRef(a.File, a.Name, a.SequenceExport, a.TrackBoneNames, a.TranslationBones));
        return d;
    }

    /// <summary>The animation playing (tests).</summary>
    public string? CurrentAnimationName => p3.CurrentAnimation;
    /// <summary>Where the animation playing comes from (tests): its package file and the set it's in.</summary>
    public string? CurrentAnimationSource => p3.CurrentRef is { } r ? $"{Path.GetFileName(r.File)} #{r.SequenceExport} {SetOf(r)}" : null;
    static string SetOf(AnimRef r)
    {
        try { var pk = MhoPackageModifier.Package.Open(r.File); var ex = pk.Exports[r.SequenceExport]; return ex.OuterIndex > 0 ? pk.Exports[ex.OuterIndex - 1].ObjectName : ""; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or IndexOutOfRangeException) { return ""; }
    }
    /// <summary>The animations Full Export writes: the hero's and the costume's own, not the shared ones every hero plays
    /// (blink, interactions: sets in Startup / MarvelGame).</summary>
    public int OwnAnimationCount => p3.AllAnimations.Count(a => !a.Package.StartsWith("startup", StringComparison.OrdinalIgnoreCase) && !a.Package.StartsWith("marvelgame", StringComparison.OrdinalIgnoreCase));

    static Settings.PreviewPrefs P => Settings.Current.Preview;
    static void SaveP() => Settings.Current.Save();
    float S => MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
    static string? Cooked => Settings.Current.CookedFolder;

    /// <summary>The Model tab: the target costume's class (marvelplayer_drdoom_classic: a costume shown with its hero's model
    /// still has its own effects), and the build's slot order for a list of material names (MaterialOverrides.Ordered).</summary>
    public Func<string?>? TargetClass { get; set; }
    public Func<IReadOnlyList<string>, List<string>>? SlotOrder { get; set; }

    public PreviewPanel()
    {
        BackColor = Color.Transparent;
        edits = Ui.FlatButton("Single Animation ▾", ShowEditsMenu, "The animation picked here and Blender: export just it (Export FBX: … Only, Open … in Blender: the model and this one animation, quick), bring edits back (an FBX's animation in place of it, and / or its mesh with its weights in place of the model's; kept in the edits folder, built into the mod) and revert them. The buttons on the right export all the animations.");
        compare = Ui.FlatButton("Compare", ToggleCompare, "Show the base hero's own in-game mesh instead, in the same animation, frame and view, to compare (click again for the MFF model).");
        edits.AutoSize = true; edits.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // (no warning about sets the costume's list doesn't name: the Model tab shows its own build, which the editor gives the
        // borrowed powers' sets afterwards)
        p3 = new Preview3D(P, edits, compare) { Dock = DockStyle.Fill, WarnUnlisted = false };
        p3.TransformAnimation = Transform;
        p3.IsOverridden = n => IsOverridden?.Invoke(n) == true;
        p3.DisplayMesh = Display;
        p3.MeshShown += MeasureBones;
        p3.ExtraLook.Add(new("Bones", "The skeleton over the model; the Bone Map tab's selected row in orange. Key: B.", () => showBones, () => ShowBones = !showBones));
        p3.ExtraLook.Add(new("Weights", "Weight paint: the model coloured by how much the Bone Map tab's selected bones move it (blue 0 → green → red 1), as Blender's weight painting. Key: W (off → over the model at 50 % → the weights alone).", () => showWeights, () => ShowWeights = !showWeights));
        showBones = P.Bones;
        // a narrow window: the menu's caption shortens so the animation drop-down keeps at least 160 px (0.16.5)
        var bar = p3.Bar;
        bar.SizeChanged += (_, _) =>
        {
            if (!string.IsNullOrEmpty(edits.AccessibleName)) return;   // (an icon now: nothing to shorten)
            int others = bar.Controls.OfType<Control>().Where(c => c != edits && c is Button).Sum(c => c.Width) + (int)(11 * 6 * S);
            string want = bar.Width - others - TextRenderer.MeasureText("Single Animation ▾", edits.Font).Width - (int)(30 * S) >= (int)(160 * S) ? "Single Animation ▾" : "Single ▾";
            if (edits.Text != want) edits.Text = want;
        };
        Controls.Add(p3);
        view.ShowMessage("Pick a character and a base hero");
        // the skeleton overlay, painted after the view's own frame (its canvas is the view's only child)
        view.Controls[0].Paint += (_, e) => PaintBones(e.Graphics, view.Controls[0].ClientSize);
        // Ctrl+click picks the bone under the mouse (0.13.3, Kurt): the Bone Map tab selects its row
        view.Controls[0].MouseDown += (_, e) =>
        {
            // painting: Ctrl alone paints the other way, so a bone is picked with Ctrl+Shift (Kurt, 2026-10-25: Ctrl strokes
            // picked other bones); both changeable (Settings → Keyboard Shortcuts)
            if (!Shortcuts.Click(painting ? "paint.pickbone" : "model.pickbone", e.Button, ModifierKeys)) return;
            // the Materials tab (Kurt, 2026-10-06): Ctrl+click picks the material under the mouse
            if (PickMaterials)
            {
                int sec = view.SectionAt(e.Location);
                if (SectionName(sec) is { Length: > 0 } picked) MaterialPicked?.Invoke(picked);
                return;
            }
            if (PickBone(e.Location, view.Controls[0].ClientSize) is string bone) BonePicked?.Invoke(bone);
        };
        Ui.Tip(view.Controls[0], "Drag to turn, right-drag to move, wheel to zoom (Shift: finer), double-click to frame. Ctrl+click a bone to pick it in the Bone Map tab; on the Materials tab, Ctrl+click the model to pick that material.");
        WirePaint();
    }

    /// <summary>An animation as the Model tab plays it: an FBX in its place (0.16.0, the import's own tracks), and a borrowed
    /// cape's tracks made for it unless the FBX animates those bones (the base hero's own mesh ignores them in Compare).</summary>
    BoneAnimation? Transform(AnimRef ar, BoneAnimation? anim)
    {
        BoneAnimation? imported = Override?.Invoke(ar, anim);
        if (imported != null) anim = imported;
        if (anim != null && shown != null)
            foreach (var r in shown.Rigs)
                if (imported == null || !shown.Bones.Where(b => r.Bones.IsMatch(b.Name)).All(b => imported.Tracks.ContainsKey(b.Name))) anim = r.Apply(anim, shown.Bones);
        return anim;
    }

    /// <summary>The hero of the base package (UC__MarvelPlayer_&lt;Hero&gt;_…), else null (team-ups).</summary>
    string? Hero()
    {
        if (shown == null) return null;
        var parts = Path.GetFileNameWithoutExtension(shown.PackagePath).Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[2] : null;
    }

    /// <summary>
    /// A power's condition material swaps on the shown model (Kurt, 2026-10-12: Doctor Doom's force field, Hulk's revive): each
    /// section's slot is its material's place in the build's slot order (the target's own model: its section's material index),
    /// and a swap for the target costume draws its material on the slots it doesn't keep.
    /// </summary>
    Func<Fx.GameData, string, ModelView.Look?[]?>? SwapsFor(string cooked)
    {
        if (shown is not { } sh) return null;
        string cls = TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(sh.PackagePath);
        List<int>? slots = null;
        if (!sh.TargetOnly && sh.SectionNames.Count > 0)
        {
            var distinct = sh.SectionNames.Distinct().ToList();
            var order = SlotOrder?.Invoke(distinct) ?? distinct;
            slots = sh.SectionNames.Select(n => order.IndexOf(n)).ToList();
        }
        var main = sh.MainRef;
        return (db, power) => PowerPreview.Swaps(db, power, cooked, new Dictionary<string, string>(), cls, slots, main);
    }

    /// <summary>Test: the camera (ModelView.ViewState).</summary>
    internal float[] ViewForTest { get => view.ViewState; set => view.ViewState = value; }

    /// <summary>One map on its own instead of the lit model (the Materials tab's Preview Shows; ModelView.MapView).</summary>
    public ModelView.MapView ShowMap { get => view.ShowMap; set => view.ShowMap = value; }

    /// <summary>Test: the view as shown, <paramref name="w"/>×<paramref name="h"/>.</summary>
    internal Bitmap? SnapshotForTest(int w, int h) => view.Snapshot(w, h, 1);

    /// <summary>Test: Compare lit and locked (the target alone).</summary>
    internal (bool Lit, bool Enabled, bool TargetOnly) CompareForTest => (comparing, compare.Enabled, targetOnly);
    /// <summary>The Variations tab's Preview This (Kurt, 2026-10-26): the shown model drawn with another color map on these
    /// sections, in this view only (the next Show draws the package's own again).</summary>
    internal void SetSectionDiffuse(IReadOnlyCollection<int> sections, byte[] bgra, int w, int h)
    {
        if (shown == null) return;
        var map = new ModelView.Map(bgra, w, h);
        var looks = shown.Mesh.Looks;
        for (int i = 0; i < looks.Length; i++)
            if (sections.Contains(i) && looks[i] is { } lk)
            {
                bool specFollows = ReferenceEquals(lk.SpecColor, lk.Diffuse);
                lk.Diffuse = map;
                if (specFollows) lk.SpecColor = map;
            }
        view.RedrawNow();
    }

    /// <summary>Test: the shown model's section looks.</summary>
    internal IEnumerable<ModelView.Look?> LooksForTest => shown?.Mesh.Looks ?? [];

    float[]? heldView;
    bool heldPlaying;
    float heldAt;

    public void ShowMessage(string text)
    {
        // a rebuild shows "Loading…" between two models: what was shown is held for the next Show (Kurt, 2026-10-04: Smooth
        // Weights re-framed the camera and stopped playback; the camera and the place in the animation are kept now)
        if (animator != null) { heldView = view.ViewState; heldPlaying = p3.IsPlaying; heldAt = p3.Fraction; }
        frameBones = null;
        mffAnimator = stockAnimator = null; shown = null;
        p3.ShowMessage(text);
    }

    // --- showing a model -----------------------------------------------------------------------------------------------------
    /// <summary>Shows a prepared model (UI thread), keeping the animation picked before when the new list has it.</summary>
    float shownExtent;
    /// <summary>Test: the shown model's largest side and its bounds' center.</summary>
    internal float ExtentValueForTest => shown == null ? 0 : Extent(shown.Mesh.Positions);   // (the model: a held prop's own size)
    internal string ExtentForTest => shown == null ? "none" : $"{shownExtent:0.##} units, center {Center(shown.Mesh.Positions)}";
    static Vector3 Center(IReadOnlyList<Vector3> pos) { Vector3 lo = new(float.MaxValue), hi = new(float.MinValue); foreach (var v in pos) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); } return (lo + hi) / 2; }

    /// <summary>A model's largest bounding-box side.</summary>
    static float Extent(IReadOnlyList<Vector3> pos)
    {
        if (pos.Count == 0) return 0;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var v in pos) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
        var d = hi - lo;
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
    }

    public void Show(Prepared p)
    {
        // What the reload keeps (0.10.17, Kurt): the animation by name, where in it (a share of its length), playing or
        // paused, and the camera; the frame only when the new list has that animation.
        string? keep = p3.CurrentAnimation;
        bool live = animator != null;
        float keepAt = live ? p3.Fraction : heldAt;
        bool wasPlaying = live ? p3.IsPlaying : heldPlaying;
        float[]? keepView = live ? view.ViewState : heldView;
        heldView = null; heldPlaying = false; heldAt = 0;
        // a model of quite another size (a 46-unit hammer after a body: Kurt's prop showed as a dot) is framed afresh
        float extent = Extent((p.Holder?.Body ?? p.Mesh).Positions);
        if (shownExtent > 0 && extent > 0 && (extent / shownExtent > 3 || shownExtent / extent > 3)) keepView = null;
        shownExtent = extent;
        bool samePackage = shown != null && shown.PackagePath.Equals(p.PackagePath, StringComparison.OrdinalIgnoreCase);
        shown = p;
        mffAnimator = new MeshAnimator(p.Bones, p.Mesh.Positions, p.Mesh.Normals, p.Mesh.Influences, p.Mesh.Tangents) { Shift = new Vector3(0, 0, feetShift ?? p.Feet) };
        FeetAutoChanged?.Invoke(p.Feet);
        if (!p.TargetOnly) LeanAutoChanged?.Invoke(p.LeanAuto);
        stockAnimator = p.Stock is { } st ? new MeshAnimator(st.Bones.ToList(), st.Positions, st.Normals, st.Influences, st.Tangents) : null;
        if (stockAnimator == null && comparing) { comparing = false; Ui.Lit(compare, false); }
        compare.Enabled = stockAnimator != null;
        // no source: the target's own model, Compare on and locked; a source picked after it starts on the source again
        if (p.TargetOnly) { comparing = true; Ui.Lit(compare, true); compare.Enabled = false; }
        else if (targetOnly) { comparing = false; Ui.Lit(compare, false); }
        targetOnly = p.TargetOnly;
        view.ModelScale = ShownScale; p3.ModelScaleChanged();
        Ui.Tip(compare, p.TargetOnly ? "No source is picked: this is the target's own model. Pick a source on the left to put a model on it; Compare then switches between the two."
            : "Show the target's own in-game mesh instead, in the same animation, frame and view, to compare (click again for the source's model).");
        // each target's model as last fitted, so the body and its prop show together whichever is the target (Kurt, 2026-10-27)
        string pkgFile = Path.GetFileName(p.PackagePath);
        if (p.TargetOnly) { if (p.Holder is { } th) propShown.Remove(pkgFile + "|" + th.PropMesh); else bodyShown.Remove(pkgFile); }
        else if (p.Holder is { } sh) propShown[pkgFile + "|" + sh.PropMesh] = p.Mesh;
        else bodyShown[pkgFile] = (p, feetShift ?? p.Feet);
        // a prop target (Kurt, 2026-10-26): shown in its character's hand, posed by the character's animations (the body as
        // last fitted this session, else the mod's copy's or the game's); Compare shows the game's prop there instead. Paint and
        // Weights work on the prop in the hand.
        if (p.Holder is { } h0)
        {
            var h = h0; var hBones = h0.Body.Bones.ToList(); float hFeet = 0;
            if (bodyShown.TryGetValue(pkgFile, out var fitted) && fitted.P.MainRef is { } fm)
            // (its props from the package as it is now, h0: the body was shown before a prop's bone or fit changed; Kurt,
            // 2026-10-27: the hammer moved to the left hand only after stepping to the axe)
            { h = new HeldBy(fitted.P.Mesh, fitted.P.Animations, h0.Main, h0.Meshes, h0.PropMesh); hBones = fitted.P.Bones; hFeet = fitted.Feet; _ = fm; }
            holderAnimator = new MeshAnimator(hBones, h.Body.Positions, h.Body.Normals, h.Body.Influences, h.Body.Tangents) { Shift = new Vector3(0, 0, hFeet) };
            // (the character at its Size, as the body target shows it: Kurt, 2026-10-28, stepping to the prop shrank it to 100 %)
            view.ModelScale = size; p3.ModelScaleChanged();
            p3.ReplaceProp = HeldProp;
            p3.ReplacedIsTarget = true;
            var hctx = new PreviewContext(Hero(), Cooked, h.Main, h.Meshes, PreviewPanel.ModsNow, () => TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(p.PackagePath), SwapsFor);
            p3.ShowCharacter(h.Body, holderAnimator, h.Animations, hctx, keep, keepAt, wasPlaying, keepView, samePackage);
            p3.RebuildRig();   // (the props already read: the new model in the hand at once; a fresh read follows)
            Ui.Tip(view, p.Note + $" · held by {h.Main.Name}");
            ShowCount++;
            return;
        }
        holderAnimator = null;
        p3.ReplaceProp = BodyProp;   // (a body target: its props as last fitted)
        p3.ReplacedIsTarget = false;
        var ctx = new PreviewContext(Hero(), Cooked, p.MainRef, p.PackageMeshes, PreviewPanel.ModsNow, () => TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(p.PackagePath), SwapsFor)
        { PropsFrom = comparing || p.PropsFrom is not { } pf ? null : (pf.Main, pf.Meshes) };   // (the mod's added props too)
        p3.ShowCharacter(comparing ? p.Stock! : p.Mesh, (comparing ? stockAnimator : mffAnimator)!, p.Animations, ctx, keep, keepAt, wasPlaying, keepView, samePackage);
        Ui.Tip(view, p.Note);
        ShowCount++;
    }

    /// <summary>The animation reloaded (an edit imported or reverted), at the same place in it.</summary>
    public void ReloadAnimation() => p3.ReloadAnimation();

    void ShowEditsMenu()
    {
        var m = new ContextMenuStrip();
        FillEditsMenu?.Invoke(m);
        if (m.Items.Count == 0) return;
        Ui.ShowUnder(m, edits);
    }

    /// <summary>The animation and where in it (checks): "name @ 0.35".</summary>
    public string PlaybackState => p3.PlaybackState;
    /// <summary>How many models have been shown (checks wait for a reload).</summary>
    public int ShowCount { get; private set; }

    /// <summary>A model is shown (checks wait for it).</summary>
    public bool Ready => p3.Ready;

    /// <summary>Picks the first animation whose name contains <paramref name="part"/> and poses it at a fraction of its length (checks).</summary>
    public void PickAnimation(string part, float at) => p3.PickAnimation(part, at);

    /// <summary>P / Esc (the main window's keys) and full screen, as the 3D preview does them.</summary>
    public bool TogglePlayback() => p3.TogglePlayback();
    public bool ToggleQuad() => p3.ToggleQuad();
    public bool PausePlayback() => p3.PausePlayback();
    public void ToggleFull() => p3.ToggleFull();
    public bool IsFull => p3.IsFull;

    /// <summary>The hero's powers shown as buttons (checks).</summary>
    public IReadOnlyList<Fx.PowerList.Power> HeroPowers => p3.HeroPowers;
    /// <summary>The power line under the view (checks).</summary>
    public string EffectsNote => p3.EffectsNote;
    /// <summary>The material swap drawn now (checks): "n of m section(s)", else "none".</summary>
    public string SwapNote => p3.SwapNote;
    /// <summary>The view's picture (checks).</summary>
    public Bitmap ViewShot() => p3.ViewShot();
    /// <summary>Clicks a power button by name (checks).</summary>
    public bool ClickPower(string name) => p3.ClickPower(name);
    /// <summary>Tests: an animation picked by name.</summary>
    public bool PlayAnimationForTest(string name) => p3.PlayAnimation(name, false);

    public void CompareOn() { if (!comparing) ToggleCompare(); }

    /// <summary>Swaps between the MFF model and the base hero's own mesh, keeping the animation, frame and camera.</summary>
    float? feetShift;
    /// <summary>The Model tab's Feet slider: the model moved by this (units, + up); null: the automatic amount (Prepared.Feet),
    /// as Build moves it.</summary>
    public float? FeetShift
    {
        get => feetShift;
        set
        {
            feetShift = value;
            if (holderAnimator == null && shown is { TargetOnly: false } fs) bodyShown[Path.GetFileName(fs.PackagePath)] = (fs, value ?? fs.Feet);
            if (mffAnimator != null && shown != null && holderAnimator == null) { mffAnimator.Shift = new Vector3(0, 0, value ?? shown.Feet); p3.SwapCharacter(comparing ? shown.Stock! : shown.Mesh, comparing ? stockAnimator! : mffAnimator); }
        }
    }
    /// <summary>A model was shown: its automatic Lean.</summary>
    public event Action<float>? LeanAutoChanged;
    /// <summary>A model was shown: its automatic Feet amount.</summary>
    public event Action<float>? FeetAutoChanged;

    float size = 1;
    /// <summary>The Model tab's Size: the model drawn that much larger or smaller (Compare shows the hero at its own size).</summary>
    public float Size { get => size; set { size = value; view.ModelScale = ShownScale; p3.ModelScaleChanged(); view.Invalidate(); } }
    /// <summary>The model at Size; Compare's hero at the game's size, except with no source (the target alone is what's sized).</summary>
    float ShownScale => comparing && !targetOnly ? 1 : size;

    /// <summary>Each package's body and each prop (package | mesh) as last shown fitted to a source this session, with the body's
    /// Feet: the other one's target shows it (the body holding the fitted prop, the prop held by the fitted body).</summary>
    readonly Dictionary<string, (Prepared P, float Feet)> bodyShown = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ModMeshes.Loaded> propShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model shown in the holder's hand in place of the game's prop (none while comparing), with the weight colours
    /// when Weights is on (it's what's painted).</summary>
    ModMeshes.Loaded? HeldProp(MeshRef r)
    {
        // (an added prop's model is drawn from its fitted copy: <model>_x100_fit12345)
        if (shown?.Holder is not { } h || !(r.Name.Equals(h.PropMesh, StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(r.Name, "^" + System.Text.RegularExpressions.Regex.Escape(h.PropMesh) + @"_x\d+(_fit\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) return null;
        if (comparing) { view.Tint = null; return null; }
        var d = Display(shown.Mesh);
        return shown.AddedFit is { } m ? Fitted(d, m) : d;
    }

    /// <summary>A model moved, turned and sized by <paramref name="m"/> (an added prop's fit), as its package draws it.</summary>
    static ModMeshes.Loaded Fitted(ModMeshes.Loaded l, System.Numerics.Matrix4x4 m)
    {
        var rot = m; rot.Translation = default;
        var n = l.Normals.Select(v => { var t = System.Numerics.Vector3.TransformNormal(v, rot); return t.LengthSquared() > 0 ? System.Numerics.Vector3.Normalize(t) : t; }).ToArray();
        var tg = l.Tangents.Select(v => { var t = System.Numerics.Vector3.TransformNormal(new(v.X, v.Y, v.Z), rot); if (t.LengthSquared() > 0) t = System.Numerics.Vector3.Normalize(t); return new System.Numerics.Vector4(t, v.W); }).ToArray();
        return l with { Positions = [.. l.Positions.Select(v => System.Numerics.Vector3.Transform(v, m))], Normals = n, Tangents = tg };
    }

    /// <summary>A body target's prop as last fitted this session, else as the Model tab built it into the mod (none while
    /// comparing, or with no source: the package's own as it is).</summary>
    ModMeshes.Loaded? BodyProp(MeshRef r)
    {
        if (shown == null || comparing || shown.TargetOnly) return null;
        return propShown.GetValueOrDefault(Path.GetFileName(shown.PackagePath) + "|" + r.Name) ?? shown.BuiltProps?.GetValueOrDefault(r.Name);
    }

    /// <summary>The weights changed in place: re-skinned, the colours refreshed (a held prop target's too).</summary>
    void RefreshSkin()
    {
        if (holderAnimator != null && shown != null && !comparing && p3.ReplacedVertexStart is int at && at >= 0)
            view.SetUv(at, Display(shown.Mesh).Uv, redraw: false);
        p3.RefreshSkin();
    }
    /// <summary>The character holding a prop target (null: the model is shown on its own).</summary>
    MeshAnimator? holderAnimator;
    /// <summary>Test: the character a prop target is shown held by, and whether the model is in its hand now.</summary>
    internal (string? Holder, bool InHand) HeldForTest => (holderAnimator != null ? shown?.Holder?.Main.Name : null, p3.ReplacedSectionStart >= 0);

    /// <summary>Test: where the painted model's vertices start in the view (-1: nothing to paint).</summary>
    internal int PaintStartForTest => PaintReady(out _, out int s) ? s : -1;
    /// <summary>Test: a body target holds a prop as last fitted (or built).</summary>
    /// <summary>Test: the props the preview holds (their attachment classes).</summary>
    internal string PropClassesForTest => string.Join(", ", p3.PropClassesForTest);
    internal string? PropBoneForTest(string cls) => p3.PropBoneForTest(cls);
    internal bool HoldsFittedPropForTest => holderAnimator == null && p3.ReplacedVertexStart >= 0;

    /// <summary>Test: Compare clicked.</summary>
    internal void CompareForTestClick() => ToggleCompare();

    void ToggleCompare()
    {
        if (shown?.Holder != null && holderAnimator != null)
        {
            comparing = !comparing; Ui.Lit(compare, comparing);
            p3.RebuildRig();   // (the game's prop in the hand, or the model)
            return;
        }
        if (shown == null || stockAnimator == null || mffAnimator == null) return;
        comparing = !comparing; Ui.Lit(compare, comparing);
        view.ModelScale = ShownScale; p3.ModelScaleChanged();
        p3.SwapCharacter(comparing ? shown.Stock! : shown.Mesh, comparing ? stockAnimator : mffAnimator);
    }

    // --- skeleton overlay (0.12.0, Kurt: the skeleton over the model, the active bones highlighted) -----------------------------
    bool showBones;
    HashSet<string> hot = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<MeshBone>? frameBones;
    /// <summary>The bones drawn: those carrying skin and the bones above them (no IK targets, root lines to the floor).</summary>
    bool[] frameDrawn = [];
    Vector3 frameCenter; float frameRadius = 1;

    /// <summary>The bones to draw in orange (the Bone Map tab's pick); empty = none.</summary>
    public void Highlight(IEnumerable<string> bones)
    {
        var next = new HashSet<string>(bones, StringComparer.OrdinalIgnoreCase);
        bool changed = !next.SetEquals(hot);
        hot = next;
        if (showWeights && changed) RefreshDisplay();
        if (showBones) view.Controls[0].Invalidate();
    }

    /// <summary>Whether the skeleton is drawn (Look ▾ → Bones; remembered).</summary>
    public bool ShowBones { get => showBones; set { showBones = value; P.Bones = value; SaveP(); view.Controls[0].Invalidate(); TogglesChanged?.Invoke(); } }

    /// <summary>Shows a character mesh and keeps what the view frames it by (its bounds, display space: Y mirrored), so the
    /// overlay can use the same camera; props come back through RebuildRig.</summary>
    void MeasureBones(ModMeshes.Loaded mesh)
    {
        frameBones = mesh.Bones;
        frameDrawn = new bool[mesh.Bones.Count];
        foreach (var inf in mesh.Influences)
            for (int k = 0; k < inf.Bones.Count; k++)
                if (inf.Weights[k] > 0 && inf.Bones[k] >= 0 && inf.Bones[k] < frameDrawn.Length)
                    for (int b = inf.Bones[k], g = 0; b >= 0 && b < frameDrawn.Length && !frameDrawn[b] && g < 256; g++)
                    { frameDrawn[b] = true; int pb = mesh.Bones[b].ParentIndex; b = pb == b ? -1 : pb; }
        if (mesh.Positions.Length == 0) return;
        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        foreach (var q in mesh.Positions) { var d = new Vector3(q.X, -q.Y, q.Z); lo = Vector3.Min(lo, d); hi = Vector3.Max(hi, d); }
        frameCenter = (lo + hi) / 2; frameRadius = Math.Max(1f, (hi - lo).Length() / 2);
    }

    /// <summary>
    /// Draws the posed skeleton over the frame: a line from each bone to its parent, a dot at each joint; the highlighted
    /// bones thicker in orange. The camera is ModelView's own, rebuilt from its public state (ViewState: yaw, pitch,
    /// distance / radius / lens scale, target offset / radius; FocalLength) and the bounds it framed the mesh by (not
    /// exposed; the vendored file stays unchanged).
    /// </summary>
    /// <summary>Check: a Ctrl+click where <paramref name="bone"/>'s joint is drawn (or, with <paramref name="segment"/>, halfway
    /// along the segment from it to its first drawn child); returns what was picked (and raises BonePicked).</summary>
    public string? TestPick(string bone, bool segment)
    {
        var size = view.Controls[0].ClientSize;
        if (frameBones == null || Project(size) is not { } pt) return null;
        int i = -1;
        for (int k = 0; k < frameBones.Count; k++) if (frameBones[k].Name.Equals(bone, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0 || pt[i] is not PointF a) return null;
        var at = a;
        if (segment)
            for (int c = 0; c < frameBones.Count; c++)
                if (frameBones[c].ParentIndex == i && c < frameDrawn.Length && frameDrawn[c] && pt[c] is PointF b) { at = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); break; }
        string? picked = PickBone(Point.Round(at), size);
        if (picked != null) BonePicked?.Invoke(picked);
        return picked;
    }

    /// <summary>Ctrl+click picks a material (the Materials tab is showing) instead of a bone.</summary>
    public bool PickMaterials { get; set; }
    /// <summary>A material was Ctrl+clicked in the view (its name, as the Materials tab lists it).</summary>
    public event Action<string>? MaterialPicked;
    /// <summary>Test: the material a Ctrl+click at the view's middle picks (null: background).</summary>
    public string? TestMaterialAtCenter()
    {
        var c = view.Controls[0].ClientSize;
        int sec = view.SectionAt(new Point(c.Width / 2, c.Height / 2));
        return SectionName(sec);
    }

    /// <summary>The material of a section of the shown mesh (a prop held by its character: the prop's own sections, after the
    /// character's and the props before it), else null.</summary>
    string? SectionName(int sec)
    {
        if (shown == null || sec < 0) return null;
        if (holderAnimator != null)
        {
            if (comparing) return null;
            int start = p3.ReplacedSectionStart;
            if (start < 0) return null;
            sec -= start;
        }
        return sec < shown.SectionNames.Count ? shown.SectionNames[sec] : null;
    }

    /// <summary>A bone was Ctrl+clicked in the view (its MHO name).</summary>
    public event Action<string>? BonePicked;

    /// <summary>
    /// The bone under a point of the view: the nearest joint dot within 12 px, else the nearest drawn segment within 8 px
    /// (a segment is moved by the bone it starts from: the shin picks the knee). Null when nothing is that close.
    /// </summary>
    string? PickBone(Point at, Size size)
    {
        if (animator == null || frameBones == null || Project(size, at) is not { } pt) return null;
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        int n = frameBones.Count, joint = -1, seg = -1;
        float dj = 12 * s, ds = 8 * s;
        bool Drawn(int i) => i < frameDrawn.Length && frameDrawn[i] && frameBones[i].ParentIndex != i;
        // a helper "_offset" bone sits on its real bone's joint: the real one wins a tie
        float Penalty(int i) => frameBones[i].Name.EndsWith("_offset", StringComparison.OrdinalIgnoreCase) ? 3 * s : 0;
        for (int i = 0; i < n; i++)
            if (Drawn(i) && pt[i] is PointF a && Dist(at, a) + Penalty(i) is float d && d < dj) { dj = d; joint = i; }
        for (int i = 0; i < n; i++)
        {
            int p = frameBones[i].ParentIndex;
            if (!Drawn(i) || p < 0 || p == i || p >= n || !Drawn(p) || pt[i] is not PointF a || pt[p] is not PointF b) continue;
            float d = SegmentDist(at, b, a) + Penalty(p);
            if (d < ds) { ds = d; seg = p; }
        }
        // a dot wins when it's very close or nearer than any line (in a small view the next joint is only a few pixels away)
        int best = joint >= 0 && (dj <= 6 * s || seg < 0 || dj <= ds) ? joint : seg;
        return best >= 0 ? frameBones[best].Name : null;

        static float Dist(Point p, PointF q) => MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        static float SegmentDist(Point p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            float t = len < 1e-6f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return MathF.Sqrt(x * x + y * y);
        }
    }

    /// <summary>Where each bone of the pose last made is on the view (null: behind the camera); null without a model. In the
    /// quad view: on the pane under <paramref name="at"/> (else the first).</summary>
    PointF?[]? Project(Size size, Point? at = null)
    {
        if (animator == null || frameBones == null || size.Width < 8 || size.Height < 8) return null;
        var panes = view.Panes();
        if (panes.Count == 0) return null;
        var pane = at is Point p && panes.FirstOrDefault(x => x.Area.Contains(p)) is { Project: not null } hit ? hit : panes[0];
        return ProjectWith(pane.Project);
    }

    PointF?[] ProjectWith(Func<Vector3, PointF?> project)
    {
        int n = frameBones!.Count;
        var pt = new PointF?[n];
        for (int i = 0; i < n; i++) pt[i] = project(animator!.BonePosition(i));
        return pt;
    }

    void PaintBones(Graphics g, Size size)
    {
        if (!showBones || frameBones == null || animator == null) return;
        foreach (var (area, project) in view.Panes())
        {
            g.SetClip(area);
            DrawBones(g, ProjectWith(project));
            g.ResetClip();
        }
    }

    void DrawBones(Graphics g, PointF?[] pt)
    {
        int n = frameBones!.Count;
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var cold = new Pen(Color.FromArgb(170, 120, 200, 255), 1.2f * s);
        using var warm = new Pen(Color.FromArgb(255, 255, 150, 40), 3f * s);
        using var coldDot = new SolidBrush(Color.FromArgb(190, 160, 215, 255));
        using var warmDot = new SolidBrush(Color.FromArgb(255, 255, 170, 60));
        // a highlighted bone: its dot, and the segments it moves (to its children) and into it, in orange
        bool Hot(int i) => i >= 0 && i < n && hot.Contains(frameBones[i].Name);
        for (int pass = 0; pass < 2; pass++)   // the highlighted bones last, on top
            for (int i = 0; i < n; i++)
            {
                int p = frameBones[i].ParentIndex;
                bool rootParent = p < 0 || p == i || p >= n || frameBones[p].ParentIndex == p;   // no line up from the root at the floor
                bool lineHot = Hot(i) || Hot(p);
                if (pt[i] is not PointF a || (!Hot(i) && i < frameDrawn.Length && !frameDrawn[i])) continue;
                if (lineHot == (pass == 1) && !rootParent && (p >= frameDrawn.Length || frameDrawn[p] || Hot(p)) && pt[p] is PointF b) g.DrawLine(lineHot ? warm : cold, b, a);
                if (Hot(i) != (pass == 1) || (frameBones[i].ParentIndex == i && !Hot(i))) continue;
                float rad = (Hot(i) ? 3.5f : 2f) * s;
                g.FillEllipse(Hot(i) ? warmDot : coldDot, a.X - rad, a.Y - rad, rad * 2, rad * 2);
            }
        g.SmoothingMode = mode;
    }


}
