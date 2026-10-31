using System.Numerics;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The move / turn gizmo on an added prop, and on a game prop picked as the target (Kurt, 2026-10-28: Thor's hammer; its
/// sliders turn and size it around its grip, PropFit.Adjust, so the gizmo sits there) (Kurt, 2026-10-27: colored arrows to drag it along X / Y / Z, colored rings to turn it
/// around one axis; 2026-10-28: oriented to the world's X / Y / Z). It sits where the prop's fit puts it, its arrows and rings
/// along the game world's axes; a drag is turned into the fit's own terms (Move X / Y / Z in the bone's frame; Turn X / Y / Z,
/// the angles that give the turned prop, found by a few Newton steps from the angles before). The Prop Fit sliders follow and
/// the prop shows moved at once (an offset on the preview's rig, from the fit built into the package to the dragged one);
/// letting go puts the fit in, as letting go of a slider does.
/// </summary>
sealed partial class ModelPage
{
    PropAdjust? gizmoStart;
    Button gizmoButton = null!;

    void ToggleGizmo() => PickModelGizmo(thisAnimation: false);

    /// <summary>Gizmo: All Animations or Gizmo: This Animation Only clicked (Kurt, 2026-10-30: one or the other, as in the
    /// Powers tab): the one clicked is lit and the gizmo works on its sliders; clicking the lit one hides the gizmo.</summary>
    void PickModelGizmo(bool thisAnimation)
    {
        bool on = Settings.Current.Preview.Gizmo, here = modelAnimBlock?.GizmoHere == true;
        bool show = !(on && here == thisAnimation);
        Settings.Current.Preview.Gizmo = show;
        Settings.Current.Save();
        if (modelAnimBlock != null) modelAnimBlock.GizmoHere = show && thisAnimation;
        LightModelGizmos();
        preview.GizmoSource = GizmoNow;   // (repaints)
    }

    void LightModelGizmos()
    {
        bool on = Settings.Current.Preview.Gizmo, here = modelAnimBlock?.GizmoHere == true;
        if (gizmoButton != null) Ui.Lit(gizmoButton, on && !here);
        modelAnimBlock?.LightGizmo(on && here);
    }

    /// <summary>A drag on In This Animation's adjustment: its values as the drag began.</summary>
    PropAnimFit? animGizmoStart;
    MhoExtendedModManager.PropFit? gizmoBuilt;

    void InitGizmo()
    {
        preview.GizmoSource = GizmoNow;
        preview.GizmoMoved += (axis, t) => GizmoDrag(axis, t, turn: false);
        preview.GizmoTurned += (axis, deg) => GizmoDrag(axis, deg, turn: true);
        preview.GizmoPlaneMoved += v => GizmoDrag(0, 0, turn: false, free: v);
        preview.AnimationPicked += (_, _) => ShowModelAnimFit();
        preview.GizmoReleased += () =>
        {
            if (animGizmoStart != null)
            {
                animGizmoStart = null; gizmoFrame = null;
                modelAnimBlock?.Commit();   // (this animation's adjustment, built in)
                return;
            }
            if (gizmoStart == null) return;
            var was = gizmoStart; var now = CurrentPropFit();
            bool changed = was != now;
            gizmoStart = null; gizmoBuilt = null; gizmoFrame = null;
            Log($"Gizmo: {addedPick?.Name ?? "no prop picked"} let go: move {was.MoveX:0.#}, {was.MoveY:0.#}, {was.MoveZ:0.#} → {now.MoveX:0.#}, {now.MoveY:0.#}, {now.MoveZ:0.#}; turn {was.TurnX:0}°, {was.TurnY:0}°, {was.TurnZ:0}° → {now.TurnX:0}°, {now.TurnY:0}°, {now.TurnZ:0}°{(changed ? "; putting it in" : " (no change)")}.");
            if (!changed) return;
            if (addedPick != null) AddedFitCommitted();
            else PropFitChanged();   // (a game prop: kept with the tab's state, the preview made again with it)
        };
    }

    bool GizmoMatch(string cls) => addedPick is { } e ? PowerBorrow.SameClass(cls, e.Class) : heldClass != null && GamePropTarget && cls.Equals(heldClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>A game prop is the target (Thor's hammer): its Prop Fit sliders are the tab's, around its grip.</summary>
    bool GamePropTarget => addedPick == null && TargetIsProp();
    /// <summary>The game prop target's grip (its model's space), for the prop it was worked out for (package | model).</summary>
    (string Key, Vector3 Pivot)? gamePivot;
    int gamePivotRun;

    /// <summary>The game prop target's grip, worked out in the background the first time (null until then).</summary>
    Vector3? GamePivot()
    {
        if (PropFitKey() is not string key || ChosenPackage is not CharacterList.Item pkg) return null;
        if (gamePivot is var (k, p) && k == key) return p;
        int run = ++gamePivotRun;
        string start = StartPackageCore(pkg.Key);
        _ = Task.Run(() =>
        {
            try { return (Vector3?)PropFit.Pivot(MhoSkeleton.Load(start, null)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result is Vector3 v && run == gamePivotRun && !IsDisposed) BeginInvoke(() => { gamePivot = (key, v); preview.GizmoSource = GizmoNow; });
        });
        return null;
    }

    /// <summary>An added prop's model middle in its own (unfitted) space, per prop and model (Kurt, 2026-10-28: an MFF shield
    /// whose model sat far from its origin had the gizmo out in the air beside the character, and turned in wide arcs).</summary>
    (string Key, Vector3 Mid)? addedMid;

    Vector3? AddedMid(AddedPropEntry e)
    {
        if (preview.PropMid(GizmoMatch) is not var (mid, count)) return null;
        string key = $"{e.Package}|{e.Class}|{count}";
        if (addedMid is var (k, m) && k == key) return m;
        // the shown model is the one built with the prop's fit: back to its own space (the fit is the same for any later fit)
        if (gizmoStart != null || !Matrix4x4.Invert(PowerBorrow.FitMatrix(e.Fit, e.Class), out var unfit)) return null;
        var own = Vector3.Transform(mid, unfit);
        addedMid = (key, own);
        return own;
    }

    /// <summary>The fit as the sliders have it, as an added prop's fit (for FitMatrix).</summary>
    MhoExtendedModManager.PropFit SliderFit(AddedPropEntry e, PropAdjust a) => new()
    {
        Class = e.Class, X = a.MoveX, Y = a.MoveY, Z = a.MoveZ, TurnX = a.TurnX, TurnY = a.TurnY, TurnZ = a.TurnZ,
        Scale = Math.Abs(a.Scale - 1) > 0.005f ? a.Scale : null, PinPivot = e.Fit.PinPivot,
    };

    /// <summary>The gizmo for the added prop picked (a plain one: an animated prop's fit isn't baked into its model), else none.</summary>
    ModelView.TransformGizmo? GizmoNow()
    {
        if (!Settings.Current.Preview.Gizmo) return null;
        if (modelAnimBlock is { GizmoHere: true, Target: null }) return null;   // (This Animation Only with no animation to adjust)
        Vector3 at;
        var fit = CurrentPropFit();
        if (addedPick is { } e)
        {
            if (e.Off || e.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase)) return null;
            // on the model's middle, where the fit puts it (its origin can be far from the model)
            at = AddedMid(e) is Vector3 m ? Vector3.Transform(m, PowerBorrow.FitMatrix(SliderFit(e, fit), e.Class)) : new Vector3(fit.MoveX, fit.MoveY, fit.MoveZ);
        }
        else if (GamePropTarget && heldClass != null && !heldClass.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase) && GamePivot() is Vector3 pivot)
            at = pivot + new Vector3(fit.MoveX, fit.MoveY, fit.MoveZ);
        else return null;
        if (preview.PropBase(GizmoMatch) is not var (b, size)) return null;
        var center = Vector3.Transform(at, b);
        // the world's axes (the game's X, Y, Z: Z up)
        Vector3[] world = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        return new ModelView.TransformGizmo(center, world, world, Math.Clamp(size * 0.6f, 8, 60));
    }

    /// <summary>A gizmo drag: the slider moved from where the drag began, the prop shown there at once.</summary>
    /// <param name="free">A drag on the centre dot: the world move (any direction), instead of along <paramref name="axis"/>.</param>
    void GizmoDrag(int axis, float amount, bool turn, Vector3? free = null)
    {
        if (modelAnimBlock is { GizmoHere: true } blk && blk.Target != null && addedPick != null)
        {
            animGizmoStart ??= blk.Current;
            var fb = gizmoFrame ??= preview.PropBase(GizmoMatch)?.Frame ?? Matrix4x4.Identity;
            var (an, off) = MhoExtendedModManager.Gui.AnimFitGizmo.Drag(animGizmoStart, fb, axis, amount, turn, free, ModifierKeys.HasFlag(Keys.Shift));
            blk.SetLive(an);
            preview.SetPropOffset(GizmoMatch, off);
            return;
        }
        var e = addedPick;
        if (e == null && !GamePropTarget) return;
        gizmoStart ??= CurrentPropFit();
        if (e != null) gizmoBuilt ??= e.Fit;
        var pivot = e == null ? GamePivot() ?? Vector3.Zero : Vector3.Zero;
        var s = gizmoStart;
        PropAdjust now;
        var b = gizmoFrame ??= preview.PropBase(GizmoMatch)?.Frame ?? Matrix4x4.Identity;   // (the bone as the drag began)
        var lin = b with { M41 = 0, M42 = 0, M43 = 0 };
        Matrix4x4.Invert(lin, out var toBone);
        var w = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        if (turn)
        {
            // a turn around the world's axis, through the gizmo's centre: in the bone's frame Qb = B·Q·B⁻¹, the prop's turn
            // R·Qb (the centre is the fit's move, so it stays), then the Turn X / Y / Z giving that
            float step = ModifierKeys.HasFlag(Keys.Shift) ? 1 : 5;   // (snaps: 5°, Shift 1°)
            float a = MathF.Round(amount / step) * step * MathF.PI / 180;
            var q = Matrix4x4.CreateFromAxisAngle(w, a);
            Func<float, float, float, Matrix4x4> rot = e != null ? Turn : GameTurn;
            var want = rot(s.TurnX, s.TurnY, s.TurnZ) * (lin * q * toBone);
            var (tx, ty, tz) = SolveTurn(want, s.TurnX, s.TurnY, s.TurnZ, rot);
            now = s with { TurnX = tx, TurnY = ty, TurnZ = tz };
            // an added prop turns around its model's middle (the gizmo's centre), not its origin: the move makes up the difference
            if (e != null && addedMid is var (_, mid))
            {
                var d = Vector3.Transform(mid, PowerBorrow.FitMatrix(SliderFit(e, s), e.Class)) - Vector3.Transform(mid, PowerBorrow.FitMatrix(SliderFit(e, now), e.Class));
                float range = propMoveX.Max;
                static float R(float v) => MathF.Round(v * 10) / 10;
                now = now with { MoveX = Math.Clamp(R(s.MoveX + d.X), -range, range), MoveY = Math.Clamp(R(s.MoveY + d.Y), -range, range), MoveZ = Math.Clamp(R(s.MoveZ + d.Z), -range, range) };
            }
        }
        else
        {
            // along the world's axis → the fit's move, in the bone's frame
            var d = Vector3.TransformNormal(free ?? w * amount, toBone);
            float range = propMoveX.Max;
            static float R(float v) => MathF.Round(v * 10) / 10;
            now = s with { MoveX = Math.Clamp(R(s.MoveX + d.X), -range, range), MoveY = Math.Clamp(R(s.MoveY + d.Y), -range, range), MoveZ = Math.Clamp(R(s.MoveZ + d.Z), -range, range) };
        }
        SetPropFit(now, save: false);
        // shown at once: from the fit the preview was made with (an added prop: in its package) to this one, in the prop's own space
        if (e != null && Matrix4x4.Invert(PowerBorrow.FitMatrix(gizmoBuilt!, e.Class), out var undo))
            preview.SetPropOffset(GizmoMatch, undo * PowerBorrow.FitMatrix(SliderFit(e, now), e.Class));
        else if (e == null && Matrix4x4.Invert(PropFit.Matrix(s, pivot), out var undoGame))
            preview.SetPropOffset(GizmoMatch, undoGame * PropFit.Matrix(now, pivot));
        status.Text = turn ? $"Turn around world {"XYZ"[axis]}: {MathF.Round(amount / (ModifierKeys.HasFlag(Keys.Shift) ? 1 : 5)) * (ModifierKeys.HasFlag(Keys.Shift) ? 1 : 5):+0;-0;0}° (let go to put it in)"
                           : free is Vector3 fv ? $"Move: {fv.X:+0.0;-0.0;0}, {fv.Y:+0.0;-0.0;0}, {fv.Z:+0.0;-0.0;0} (let go to put it in)"
                           : $"Move along world {"XYZ"[axis]}: {amount:+0.0;-0.0;0} (let go to put it in)";
    }

    Matrix4x4? gizmoFrame;

    /// <summary>The fit's turn alone (as FitMatrix turns the model: pitch Y, yaw Z, roll X, in degrees).</summary>
    internal static Matrix4x4 Turn(float x, float y, float z)
    {
        const float d = MathF.PI / 180;
        return Fx.PowerEffects.Player.UeRotation(y * d, z * d, x * d);
    }

    /// <summary>A game prop's turn alone (as PropFit.Adjust turns it: X, then Y, then Z, in degrees).</summary>
    static Matrix4x4 GameTurn(float x, float y, float z)
    {
        const float d = MathF.PI / 180;
        return Matrix4x4.CreateRotationX(x * d) * Matrix4x4.CreateRotationY(y * d) * Matrix4x4.CreateRotationZ(z * d);
    }

    /// <summary>The Turn X / Y / Z (degrees, -180..180) whose turn (<paramref name="turnOf"/>) is <paramref name="want"/>:
    /// Gauss-Newton steps from the angles before (a drag changes them a little at a time, so it stays on the same branch).</summary>
    internal static (float X, float Y, float Z) SolveTurn(Matrix4x4 want, float x, float y, float z, Func<float, float, float, Matrix4x4> turnOf)
    {
        Func<float, float, float, Matrix4x4> Turn = turnOf;
        static float[] Res(Matrix4x4 m, Matrix4x4 w) => [m.M11 - w.M11, m.M12 - w.M12, m.M13 - w.M13, m.M21 - w.M21, m.M22 - w.M22, m.M23 - w.M23, m.M31 - w.M31, m.M32 - w.M32, m.M33 - w.M33];
        double[] e = [x, y, z];
        for (int it = 0; it < 30; it++)
        {
            var r = Res(Turn((float)e[0], (float)e[1], (float)e[2]), want);
            if (r.Sum(v => v * v) < 1e-10f) break;
            var jac = new float[3][];
            for (int k = 0; k < 3; k++)
            {
                double[] ep = [.. e]; ep[k] += 0.1;
                var rp = Res(Turn((float)ep[0], (float)ep[1], (float)ep[2]), want);
                jac[k] = [.. rp.Select((v, i) => (v - r[i]) / 0.1f)];
            }
            // (JᵀJ + λI) δ = -Jᵀr
            var a = new double[3, 3]; var g = new double[3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) { double sum = 0; for (int n = 0; n < 9; n++) sum += jac[i][n] * jac[j][n]; a[i, j] = sum + (i == j ? 1e-6 : 0); }
                double gs = 0; for (int n = 0; n < 9; n++) gs += jac[i][n] * r[n]; g[i] = -gs;
            }
            double det = a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1]) - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0]) + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);
            if (Math.Abs(det) < 1e-18) break;
            double D(int col) { var m = (double[,])a.Clone(); for (int i = 0; i < 3; i++) m[i, col] = g[i];
                return (m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0])) / det; }
            double dx = D(0), dy = D(1), dz = D(2);
            double big = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (big > 30) { dx *= 30 / big; dy *= 30 / big; dz *= 30 / big; }   // (a short step at a time)
            e[0] += dx; e[1] += dy; e[2] += dz;
        }
        static float Wrap(double d) { d %= 360; return (float)Math.Round(d > 180 ? d - 360 : d < -180 ? d + 360 : d, 1); }
        return (Wrap(e[0]), Wrap(e[1]), Wrap(e[2]));
    }

    /// <summary>Test hook: a drag on gizmo handle <paramref name="handle"/> (0-2 arrows, 3-5 rings) by so many pixels.</summary>
    internal bool TestGizmo(int handle, int dx, int dy) => preview.TestGizmoDrag(handle, dx, dy);

    /// <summary>Test: where the prop draws before a drag, mid-drag and after letting go (and the preview reading it again).</summary>
    internal async Task<string> GizmoLiveForTest(int handle, int dx, int dy)
    {
        var a = preview.PropShownForTest(GizmoMatch);
        var ga = GizmoNow()?.Center;
        bool grabbed = preview.TestGizmoMouse(handle, dx, dy, 8, release: false);   // (as the mouse: window messages)
        if (!grabbed) return "the press didn't take the handle";
        await Task.Delay(300);
        var b = preview.PropShownForTest(GizmoMatch);
        var gb = GizmoNow()?.Center;
        preview.TestGizmoMouseUp();
        await Task.Delay(6000);
        var c = preview.PropShownForTest(GizmoMatch);
        string F((Vector3 Mid, Vector3 Dir)? v) => v is { } x ? $"mid {x.Mid.X:0.#},{x.Mid.Y:0.#},{x.Mid.Z:0.#} dir {x.Dir.X:0.#},{x.Dir.Y:0.#},{x.Dir.Z:0.#}" : "none";
        string G(Vector3? v) => v is { } x ? $"{x.X:0.#},{x.Y:0.#},{x.Z:0.#}" : "none";
        return $"before {F(a)} | dragging {F(b)} | after {F(c)} | gizmo centre {G(ga)} → {G(gb)}";
    }
    internal bool GizmoShownForTest => GizmoNow() != null;
    /// <summary>Test: the gizmo's centre and the prop's whole placement (fit, then bone) as the sliders have it.</summary>
    internal (Vector3 Center, Matrix4x4 World)? GizmoStateForTest()
    {
        if (addedPick is not { } e || preview.PropBase(GizmoMatch) is not var (b, _)) return null;
        var fit = CurrentPropFit();
        return (Vector3.Transform(new Vector3(fit.MoveX, fit.MoveY, fit.MoveZ), b), PowerBorrow.FitMatrix(SliderFit(e, fit), e.Class) * b);
    }
    internal void ToggleGizmoForTest() => ToggleGizmo();
    internal int GizmoArrowForTest() => preview.GizmoLongestArrowForTest();
}
