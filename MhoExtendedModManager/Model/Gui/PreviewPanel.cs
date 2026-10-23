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
        p3 = new Preview3D(P, edits, compare) { Dock = DockStyle.Fill };
        p3.TransformAnimation = Transform;
        p3.IsOverridden = n => IsOverridden?.Invoke(n) == true;
        p3.DisplayMesh = Display;
        p3.MeshShown += MeasureBones;
        p3.ExtraLook.Add(new("Bones", "The skeleton over the model; the Bone Map tab's selected row in orange.", () => showBones, () => ShowBones = !showBones));
        p3.ExtraLook.Add(new("Weights", "Weight paint: the model coloured by how much the Bone Map tab's selected bones move it (blue 0 → green → red 1), as Blender's weight painting.", () => showWeights, () => ShowWeights = !showWeights));
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
            if (e.Button != MouseButtons.Left || (ModifierKeys & Keys.Control) == 0) return;
            // the Materials tab (Kurt, 2026-10-06): Ctrl+click picks the material under the mouse
            if (PickMaterials)
            {
                int sec = view.SectionAt(e.Location);
                if (shown != null && sec >= 0 && sec < shown.SectionNames.Count && shown.SectionNames[sec].Length > 0) MaterialPicked?.Invoke(shown.SectionNames[sec]);
                return;
            }
            if (PickBone(e.Location, view.Controls[0].ClientSize) is string bone) BonePicked?.Invoke(bone);
        };
        Ui.Tip(view.Controls[0], "Drag to turn, right-drag to move, wheel to zoom (Shift: finer), double-click to frame. Ctrl+click a bone to pick it in the Bone Map tab; on the Materials tab, Ctrl+click the model to pick that material.");
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
        bool samePackage = shown != null && shown.PackagePath.Equals(p.PackagePath, StringComparison.OrdinalIgnoreCase);
        shown = p;
        mffAnimator = new MeshAnimator(p.Bones, p.Mesh.Positions, p.Mesh.Normals, p.Mesh.Influences, p.Mesh.Tangents) { Shift = new Vector3(0, 0, feetShift ?? p.Feet) };
        FeetAutoChanged?.Invoke(p.Feet);
        stockAnimator = p.Stock is { } st ? new MeshAnimator(st.Bones.ToList(), st.Positions, st.Normals, st.Influences, st.Tangents) : null;
        if (stockAnimator == null && comparing) { comparing = false; Ui.Lit(compare, false); }
        compare.Enabled = stockAnimator != null;
        // no source: the target's own model, Compare on and locked; a source picked after it starts on the source again
        if (p.TargetOnly) { comparing = true; Ui.Lit(compare, true); compare.Enabled = false; }
        else if (targetOnly) { comparing = false; Ui.Lit(compare, false); }
        targetOnly = p.TargetOnly;
        view.ModelScale = ShownScale;
        Ui.Tip(compare, p.TargetOnly ? "No source is picked: this is the target's own model. Pick a source on the left to put a model on it; Compare then switches between the two."
            : "Show the target's own in-game mesh instead, in the same animation, frame and view, to compare (click again for the source's model).");
        var ctx = new PreviewContext(Hero(), Cooked, p.MainRef, p.PackageMeshes, [], () => TargetClass?.Invoke() ?? MhoExtendedModManager.Model.ConditionSwaps.ClassOf(p.PackagePath), SwapsFor);
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
            if (mffAnimator != null && shown != null) { mffAnimator.Shift = new Vector3(0, 0, value ?? shown.Feet); p3.SwapCharacter(comparing ? shown.Stock! : shown.Mesh, comparing ? stockAnimator! : mffAnimator); }
        }
    }
    /// <summary>A model was shown: its automatic Feet amount.</summary>
    public event Action<float>? FeetAutoChanged;

    float size = 1;
    /// <summary>The Model tab's Size: the model drawn that much larger or smaller (Compare shows the hero at its own size).</summary>
    public float Size { get => size; set { size = value; view.ModelScale = ShownScale; view.Invalidate(); } }
    /// <summary>The model at Size; Compare's hero at the game's size, except with no source (the target alone is what's sized).</summary>
    float ShownScale => comparing && !targetOnly ? 1 : size;

    void ToggleCompare()
    {
        if (shown == null || stockAnimator == null || mffAnimator == null) return;
        comparing = !comparing; Ui.Lit(compare, comparing);
        view.ModelScale = ShownScale;
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
        return shown != null && sec >= 0 && sec < shown.SectionNames.Count ? shown.SectionNames[sec] : null;
    }

    /// <summary>A bone was Ctrl+clicked in the view (its MHO name).</summary>
    public event Action<string>? BonePicked;

    /// <summary>
    /// The bone under a point of the view: the nearest joint dot within 12 px, else the nearest drawn segment within 8 px
    /// (a segment is moved by the bone it starts from: the shin picks the knee). Null when nothing is that close.
    /// </summary>
    string? PickBone(Point at, Size size)
    {
        if (animator == null || frameBones == null || Project(size) is not { } pt) return null;
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

    /// <summary>Where each bone of the pose last made is on the view (null: behind the camera); null without a model.</summary>
    PointF?[]? Project(Size size)
    {
        if (animator == null || frameBones == null || size.Width < 8 || size.Height < 8) return null;
        var vs = view.ViewState;
        float fov = 2 * MathF.Atan(12f / view.FocalLength);
        float lensScale = MathF.Tan(ModelView.ReferenceFov / 2) / MathF.Tan(fov / 2);
        float yaw = vs[0], pitch = vs[1], distance = vs[2] * frameRadius * lensScale;
        var target = frameCenter + new Vector3(vs[3], vs[4], vs[5]) * frameRadius;
        var eye = target + distance * new Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch));
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var u = Vector3.Cross(r, f);
        float focal = size.Height / 2f / MathF.Tan(fov / 2), cx = size.Width / 2f, cy = size.Height / 2f, near = distance * 0.01f;
        int n = frameBones.Count;
        var pt = new PointF?[n];
        for (int i = 0; i < n; i++)
        {
            var e = animator.BonePosition(i);
            var d = new Vector3(e.X, -e.Y, e.Z) - eye;
            float z = Vector3.Dot(d, f);
            if (z < near) continue;
            pt[i] = new PointF(cx + Vector3.Dot(d, r) * focal / z, cy - Vector3.Dot(d, u) * focal / z);
        }
        return pt;
    }

    void PaintBones(Graphics g, Size size)
    {
        if (!showBones || frameBones == null || Project(size) is not { } pt) return;
        int n = frameBones.Count;
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
