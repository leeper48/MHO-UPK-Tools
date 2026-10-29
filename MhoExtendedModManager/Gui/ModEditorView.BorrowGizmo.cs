using System.Numerics;
using MhoExtendedModManager.Model.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The move / turn gizmo on a borrowed power's prop in the Powers tab (Kurt, 2026-10-28: as the Model tab has it, with Reset Fit
/// and a Gizmo toggle beside the Prop Fit sliders): colored arrows drag the prop along the world's X / Y / Z, rings turn it
/// around them. The Powers preview plays the power on the character, so the prop is fitted in the pose it's used in. A drag
/// moves the sliders (Move X / Y / Z are already along the world's axes here; Turn X / Y / Z found by ModelPage.SolveTurn) and
/// shows the prop there at once (an offset from the fit the preview was built with); letting go puts the fit in, as letting go
/// of a slider does (the borrow is built again).
/// </summary>
sealed partial class ModEditorView
{
    Button? borrowGizmoButton;
    Preview3D? gizmoHooked;
    PropFit? borrowGizmoBuilt;
    (float X, float Y, float Z, float TX, float TY, float TZ)? borrowGizmoStart;
    Matrix4x4? borrowGizmoFrame;

    /// <summary>The Reset Fit and Gizmo buttons, for the Prop Fit column.</summary>
    Control BorrowFitButtons()
    {
        float sc = Ui.Dpi(DeviceDpi);
        var reset = Ui.FlatButton("Reset Fit", ResetBorrowFit, "Puts the prop back as the borrow brings it: its own size, no move, its usual turn.");
        borrowGizmoButton = Ui.FlatButton("Gizmo", ToggleBorrowGizmo, "Shows or hides the move / turn gizmo on the prop in the preview: drag a colored arrow to move it along X (red), Y (green) or Z (blue), a ring to turn it around that axis (5° steps, Shift: 1°). Letting go puts the fit in.");
        Icons.Make(reset, "Reset Fit", Icons.Reset, sc);
        Icons.Make(borrowGizmoButton, "Gizmo", Icons.Gizmo, sc);
        Ui.Lit(borrowGizmoButton, Model.Settings.Current.Preview.Gizmo);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0), BackColor = Color.Transparent };
        row.Controls.AddRange([reset, borrowGizmoButton]);
        return row;
    }

    void ToggleBorrowGizmo()
    {
        bool on = !Model.Settings.Current.Preview.Gizmo;
        Model.Settings.Current.Preview.Gizmo = on;   // (one setting with the Model tab's gizmo)
        Model.Settings.Current.Save();
        if (borrowGizmoButton != null) Ui.Lit(borrowGizmoButton, on);
        HookBorrowGizmo();
        gizmoHooked?.View.Invalidate();
    }

    void ResetBorrowFit()
    {
        if (propPick.SelectedItem is not PowerBorrow.SpawnedProp sp || pSize == null || pX == null || pY == null || pZ == null || pTX == null || pTY == null || pTZ == null) return;
        bool held = HeldAs(sp.Class) != null;
        vShowing = true;
        try
        {
            pSize.Value = held ? 1 : sp.Auto;
            pX.Value = pY.Value = pZ.Value = 0;
            pTX.Value = pTY.Value = 0;
            pTZ.Value = held ? PowerBorrow.DefaultTurnZ(sp.Class) : sp.TurnZAuto;
        }
        finally { vShowing = false; }
        PropFitChanged();
    }

    /// <summary>The gizmo hooked to the Powers preview's 3D view (made when it's first shown).</summary>
    void HookBorrowGizmo()
    {
        if (powerPreview?.Viewer3D is not { } p3 || ReferenceEquals(p3, gizmoHooked)) return;
        gizmoHooked = p3;
        p3.View.GizmoSource = BorrowGizmoNow;
        p3.View.GizmoMoved += (axis, t) => BorrowGizmoDrag(axis, t, turn: false);
        p3.View.GizmoTurned += (axis, deg) => BorrowGizmoDrag(axis, deg, turn: true);
        p3.View.GizmoReleased += () =>
        {
            if (borrowGizmoStart == null) return;
            borrowGizmoStart = null; borrowGizmoBuilt = null; borrowGizmoFrame = null;
            PropFitChanged();   // (the fit into the borrow, which is built again)
        };
    }

    /// <summary>The prop picked in Prop Fit, its fit as the borrow (or the costume's held prop) has it, and its class.</summary>
    (PowerBorrow.SpawnedProp Sp, PropFit Fit)? BorrowGizmoTarget()
    {
        if (!propFit.Visible || propPick.SelectedItem is not PowerBorrow.SpawnedProp sp || powerList.SelectedItem is not Fx.PowerList.Power p || BorrowOf(p.Prototype) is not { } e) return null;
        if (sp.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase)) return null;   // (its fit isn't a held prop's)
        var fit = HeldAs(sp.Class)?.Fit ?? e.Props?.FirstOrDefault(f => f.Class.Equals(sp.Class, StringComparison.OrdinalIgnoreCase)) ?? new PropFit { Class = sp.Class, TurnZ = sp.TurnZAuto };
        return (sp, fit);
    }

    bool BorrowGizmoMatch(string cls) => propPick.SelectedItem is PowerBorrow.SpawnedProp sp && PowerBorrow.SameClass(cls, sp.Class);

    /// <summary>The fit as the sliders have it (Move along the world's axes → the prop's own, as PropFitChanged does).</summary>
    PropFit SliderBorrowFit(PropFit was)
    {
        var own = Vector3.TransformNormal(new Vector3(pX!.Value, pY!.Value, pZ!.Value), Matrix4x4.Transpose(fitAxes));
        return new PropFit { Class = was.Class, X = own.X, Y = own.Y, Z = own.Z, TurnX = pTX!.Value, TurnY = pTY!.Value, TurnZ = pTZ!.Value, Scale = was.Scale, PinPivot = was.PinPivot };
    }

    ModelView.TransformGizmo? BorrowGizmoNow()
    {
        if (!Model.Settings.Current.Preview.Gizmo || pX == null || BorrowGizmoTarget() is not var (_, fit) || gizmoHooked?.PropBase(BorrowGizmoMatch) is not var (b, size)) return null;
        var now = SliderBorrowFit(fit);
        var center = Vector3.Transform(new Vector3(now.X, now.Y, now.Z), b);
        Vector3[] world = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        return new ModelView.TransformGizmo(center, world, world, Math.Clamp(size * 0.6f, 8, 60));
    }

    void BorrowGizmoDrag(int axis, float amount, bool turn)
    {
        if (BorrowGizmoTarget() is not var (sp, fit) || gizmoHooked is not { } p3 || pX == null || pY == null || pZ == null || pTX == null || pTY == null || pTZ == null) return;
        if (borrowGizmoStart == null && p3.PropFrame(sp.Class) is { } pf)
        {
            // the sliders' axes as the prop is drawn now (they were filled with no turn when the preview hadn't drawn it yet),
            // the sliders put in those terms, so a drag along world X changes Move X only
            var own = Vector3.TransformNormal(new Vector3(pX.Value, pY.Value, pZ.Value), Matrix4x4.Transpose(fitAxes));
            fitAxes = Turn(pf);
            var world = Vector3.TransformNormal(own, fitAxes);
            vShowing = true;
            try { pX.Value = world.X; pY.Value = world.Y; pZ.Value = world.Z; }
            finally { vShowing = false; }
        }
        borrowGizmoStart ??= (pX.Value, pY.Value, pZ.Value, pTX.Value, pTY.Value, pTZ.Value);
        borrowGizmoBuilt ??= SliderBorrowFit(fit);
        var s = borrowGizmoStart.Value;
        var w = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        vShowing = true;
        try
        {
            if (turn)
            {
                // a turn around the world's axis through the gizmo's centre: in the bone's frame B·Q·B⁻¹ after the prop's own turn
                var b = borrowGizmoFrame ??= p3.PropBase(BorrowGizmoMatch)?.Frame ?? Matrix4x4.Identity;
                var lin = b with { M41 = 0, M42 = 0, M43 = 0 };
                Matrix4x4.Invert(lin, out var toBone);
                float step = ModifierKeys.HasFlag(Keys.Shift) ? 1 : 5;
                float a = MathF.Round(amount / step) * step * MathF.PI / 180;
                var want = ModelPage.Turn(s.TX, s.TY, s.TZ) * (lin * Matrix4x4.CreateFromAxisAngle(w, a) * toBone);
                var (tx, ty, tz) = ModelPage.SolveTurn(want, s.TX, s.TY, s.TZ, ModelPage.Turn);
                pTX.Value = tx; pTY.Value = ty; pTZ.Value = tz;
            }
            else
            {
                // Move X / Y / Z are along the world's axes already
                static float R(float v) => MathF.Round(v * 10) / 10;
                pX.Value = Math.Clamp(R(s.X + w.X * amount), pX.Min, pX.Max);
                pY.Value = Math.Clamp(R(s.Y + w.Y * amount), pY.Min, pY.Max);
                pZ.Value = Math.Clamp(R(s.Z + w.Z * amount), pZ.Min, pZ.Max);
            }
        }
        finally { vShowing = false; }
        // shown at once: from the fit the preview was built with to this one, in the prop's own space
        if (Matrix4x4.Invert(PowerBorrow.FitMatrix(borrowGizmoBuilt, sp.Class), out var undo))
            p3.SetPropOffset(BorrowGizmoMatch, undo * PowerBorrow.FitMatrix(SliderBorrowFit(fit), sp.Class));
    }
}
