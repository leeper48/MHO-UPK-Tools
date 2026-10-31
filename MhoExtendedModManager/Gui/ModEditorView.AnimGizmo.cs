using System.Numerics;
using MhoExtendedModManager.Model.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The move / turn gizmo in the Animations tab's preview (Kurt, 2026-10-30: the fit and In This Animation in all three tabs,
/// each with the gizmo on one or the other): Gizmo: All Animations works on the prop's fit (the Move sliders along the world's
/// axes, as the Powers tab's), Gizmo: This Animation Only on the selected animation's adjustment (AnimFitGizmo).
/// </summary>
sealed partial class ModEditorView
{
    Button? animGizmoButton;
    /// <summary>Pivot on the Hand's icon button (beside the gizmo, as in the Powers tab).</summary>
    Button? propPinButton;
    Preview3D? animGizmoHooked;
    PropFit? animFitBuilt;
    (float X, float Y, float Z, float TX, float TY, float TZ)? animFitStart;
    PropAnimFit? animAdjustStart;
    Matrix4x4? animGizmoFrame;

    /// <summary>Reset Fit, Gizmo: All Animations and Pivot on the Hand under the fit's sliders (laid out as the Powers tab's,
    /// Kurt 2026-10-30).</summary>
    Control AnimGizmoButtons()
    {
        var reset = Ui.FlatButton("Reset Fit", ResetAnimPropFit, "Puts the prop's fit back: its own size, no move, its usual turn.");
        Icons.Make(reset, "Reset Fit", Icons.Reset, S);
        animGizmoButton = Ui.FlatButton("Gizmo: All Animations", () => PickAnimGizmo(thisAnimation: false), "The move / turn gizmo in the preview on the prop's fit in every animation (these sliders): drag a colored arrow to move it along X (red), Y (green) or Z (blue), the white dot to move it in the view, a ring to turn it around that axis (5° steps, Shift: 1°). Letting go puts the fit in. Lit when it's on; click again to hide the gizmo. Gizmo: This Animation Only (In This Animation) is the other choice.");
        Icons.Make(animGizmoButton, "Gizmo: All Animations", Icons.Gizmo, S);
        LightAnimGizmos();
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, (int)(4 * S), 0, 0), BackColor = Color.Transparent };
        row.Controls.AddRange([reset, animGizmoButton]);
        if (propPinButton != null) row.Controls.Add(propPinButton);
        animFitButtons = row;
        return row;
    }

    Control? animFitButtons;

    void ResetAnimPropFit()
    {
        if (propList.SelectedItem is not AddedPropEntry e || apSize == null || apX == null || apY == null || apZ == null || apTX == null || apTY == null || apTZ == null) return;
        propShowing = true;
        try
        {
            apSize.Value = 1;
            apX.Value = apY.Value = apZ.Value = 0;
            apTX.Value = apTY.Value = 0;
            apTZ.Value = PowerBorrow.DefaultTurnZ(e.Class);
        }
        finally { propShowing = false; }
        PropFitCommitted();
    }

    void PickAnimGizmo(bool thisAnimation)
    {
        bool on = Model.Settings.Current.Preview.Gizmo, here = animBlock?.GizmoHere == true;
        bool show = !(on && here == thisAnimation);
        Model.Settings.Current.Preview.Gizmo = show;   // (one show / hide setting with the other tabs' gizmos)
        Model.Settings.Current.Save();
        if (animBlock != null) animBlock.GizmoHere = show && thisAnimation;
        LightAnimGizmos();
        HookAnimGizmo();
        animGizmoHooked?.View.Invalidate();
    }

    void LightAnimGizmos()
    {
        bool on = Model.Settings.Current.Preview.Gizmo, here = animBlock?.GizmoHere == true;
        if (animGizmoButton != null) Ui.Lit(animGizmoButton, on && !here);
        animBlock?.LightGizmo(on && here);
    }

    /// <summary>The gizmo hooked to the Animations preview's 3D view (made when it first shows a model).</summary>
    void HookAnimGizmo()
    {
        LightAnimGizmos();
        if (animPreview?.Viewer3D is not { } p3 || ReferenceEquals(p3, animGizmoHooked)) return;
        animGizmoHooked = p3;
        p3.View.GizmoSource = AnimGizmoNow;
        p3.View.GizmoMoved += (axis, t) => AnimTabGizmoDrag(axis, t, turn: false);
        p3.View.GizmoTurned += (axis, deg) => AnimTabGizmoDrag(axis, deg, turn: true);
        p3.View.GizmoPlaneMoved += v => AnimTabGizmoDrag(0, 0, turn: false, free: v);
        p3.View.GizmoReleased += () =>
        {
            animGizmoFrame = null;
            if (animAdjustStart != null) { animAdjustStart = null; animBlock?.Commit(); return; }
            if (animFitStart == null) return;
            animFitStart = null; animFitBuilt = null;
            PropFitCommitted();   // (the fit in, as letting go of a slider does)
        };
    }

    AddedPropEntry? AnimGizmoProp() => propList.SelectedItem is AddedPropEntry e && !e.Off && !e.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase) ? e : null;
    bool AnimGizmoMatch(string cls) => propList.SelectedItem is AddedPropEntry e && PowerBorrow.SameClass(cls, e.Class);

    /// <summary>Where the Animations preview holds the prop (its bone, without the gizmo's offset), by the gizmo's class match.</summary>
    Matrix4x4? AnimHeldFrame(string cls) => animPreview?.Viewer3D?.PropBase(c => PowerBorrow.SameClass(c, cls))?.Frame;

    /// <summary>The fit as the sliders have it (Move along the world's axes → the prop's own).</summary>
    PropFit SliderAnimFit(AddedPropEntry e)
    {
        var own = Vector3.TransformNormal(new Vector3(apX!.Value, apY!.Value, apZ!.Value), Matrix4x4.Transpose(apAxes));
        return new PropFit { Class = e.Fit.Class, X = own.X, Y = own.Y, Z = own.Z, TurnX = apTX!.Value, TurnY = apTY!.Value, TurnZ = apTZ!.Value, Scale = e.Fit.Scale, PinPivot = e.Fit.PinPivot };
    }

    ModelView.TransformGizmo? AnimGizmoNow()
    {
        if (!Model.Settings.Current.Preview.Gizmo || apX == null || AnimGizmoProp() is not { } e || animGizmoHooked?.PropBase(AnimGizmoMatch) is not var (b, size)) return null;
        if (animBlock is { GizmoHere: true, Target: null }) return null;   // (This Animation Only with no animation to adjust)
        var now = SliderAnimFit(e);
        var center = Vector3.Transform(new Vector3(now.X, now.Y, now.Z), b);
        Vector3[] world = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        return new ModelView.TransformGizmo(center, world, world, Math.Clamp(size * 0.6f, 8, 60));
    }

    void AnimTabGizmoDrag(int axis, float amount, bool turn, Vector3? free = null)
    {
        if (AnimGizmoProp() is not { } e || animGizmoHooked is not { } p3 || apX == null || apY == null || apZ == null || apTX == null || apTY == null || apTZ == null) return;
        // this animation's adjustment
        if (animBlock is { GizmoHere: true } blk && blk.Target != null)
        {
            animAdjustStart ??= blk.Current;
            var fb = animGizmoFrame ??= p3.PropBase(AnimGizmoMatch)?.Frame ?? Matrix4x4.Identity;
            var (an, off) = AnimFitGizmo.Drag(animAdjustStart, fb, axis, amount, turn, free, ModifierKeys.HasFlag(Keys.Shift));
            blk.SetLive(an);
            p3.SetPropOffset(AnimGizmoMatch, off);
            return;
        }
        // the fit in every animation
        if (animFitStart == null && AnimHeldFrame(e.Class) is { } pf)
        {
            // the sliders' axes as the prop is held now (this animation's own adjustment taken out), the sliders in those terms
            var own = Vector3.TransformNormal(new Vector3(apX.Value, apY.Value, apZ.Value), Matrix4x4.Transpose(apAxes));
            apAxes = Turn(WithoutAnimFit(e, pf));
            var world0 = Vector3.TransformNormal(own, apAxes);
            propShowing = true;
            try { apX.Value = world0.X; apY.Value = world0.Y; apZ.Value = world0.Z; }
            finally { propShowing = false; }
        }
        animFitStart ??= (apX.Value, apY.Value, apZ.Value, apTX.Value, apTY.Value, apTZ.Value);
        animFitBuilt ??= SliderAnimFit(e);
        var s = animFitStart.Value;
        var w = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        propShowing = true;
        try
        {
            if (turn)
            {
                var b = animGizmoFrame ??= p3.PropBase(AnimGizmoMatch)?.Frame ?? Matrix4x4.Identity;
                var lin = b with { M41 = 0, M42 = 0, M43 = 0 };
                Matrix4x4.Invert(lin, out var toBone);
                float step = ModifierKeys.HasFlag(Keys.Shift) ? 1 : 5;
                float a = MathF.Round(amount / step) * step * MathF.PI / 180;
                var want = ModelPage.Turn(s.TX, s.TY, s.TZ) * (lin * Matrix4x4.CreateFromAxisAngle(w, a) * toBone);
                var (tx, ty, tz) = ModelPage.SolveTurn(want, s.TX, s.TY, s.TZ, ModelPage.Turn);
                apTX.Value = tx; apTY.Value = ty; apTZ.Value = tz;
            }
            else
            {
                static float R(float v) => MathF.Round(v * 10) / 10;
                var mv = free ?? w * amount;
                apX.Value = Math.Clamp(R(s.X + mv.X), apX.Min, apX.Max);
                apY.Value = Math.Clamp(R(s.Y + mv.Y), apY.Min, apY.Max);
                apZ.Value = Math.Clamp(R(s.Z + mv.Z), apZ.Min, apZ.Max);
            }
        }
        finally { propShowing = false; }
        // shown at once: from the fit the preview was built with to this one, in the prop's own space
        if (Matrix4x4.Invert(PowerBorrow.FitMatrix(animFitBuilt, e.Class), out var undo))
            p3.SetPropOffset(AnimGizmoMatch, undo * PowerBorrow.FitMatrix(SliderAnimFit(e), e.Class));
    }
}
