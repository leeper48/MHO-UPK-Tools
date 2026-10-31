using System.Numerics;
using MhoExtendedModManager.Model.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The gizmo on an In This Animation adjustment (Gizmo: This Animation Only), the same in the Model, Animations and Powers tabs
/// (Kurt, 2026-10-30): Move along the world's axes as the sliders are; Turn about them around the bone the prop hangs on, as
/// PropHold bakes it; the change shown at once as an offset on the prop.
/// </summary>
static class AnimFitGizmo
{
    /// <summary>The adjustment a drag gives, from <paramref name="s"/> (as the drag began) and the prop's bone frame then
    /// (<paramref name="frame"/>), and the offset that shows it on the prop until the animation is built again.</summary>
    /// <param name="free">A drag on the centre dot: the world move, instead of along <paramref name="axis"/>.</param>
    public static (PropAnimFit Now, Matrix4x4 Offset) Drag(PropAnimFit s, Matrix4x4 frame, int axis, float amount, bool turn, Vector3? free, bool fine)
    {
        var w = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        static float R(float v) => MathF.Round(v);
        PropAnimFit now;
        if (turn)
        {
            float step = fine ? 1 : 5;
            float a = MathF.Round(amount / step) * step * MathF.PI / 180;
            static Matrix4x4 TurnOf(float x, float y, float z) => new PropAnimFit { TurnX = x, TurnY = y, TurnZ = z }.Turn();
            var want = s.Turn() * Matrix4x4.CreateFromAxisAngle(w, a);
            var (tx, ty, tz) = ModelPage.SolveTurn(want, s.TurnX, s.TurnY, s.TurnZ, TurnOf);
            now = new PropAnimFit { Slot = s.Slot, X = s.X, Y = s.Y, Z = s.Z, TurnX = R(tx), TurnY = R(ty), TurnZ = R(tz) };
        }
        else
        {
            var mv = free ?? w * amount;
            now = new PropAnimFit { Slot = s.Slot, X = Math.Clamp(R(s.X + mv.X), -60, 60), Y = Math.Clamp(R(s.Y + mv.Y), -60, 60), Z = Math.Clamp(R(s.Z + mv.Z), -60, 60), TurnX = s.TurnX, TurnY = s.TurnY, TurnZ = s.TurnZ };
        }
        // the change in the world (about the bone's place) put in the prop's own frame: drawn v·O·B, wanted v·B·Δ
        var o = frame.Translation;
        Matrix4x4 Place(PropAnimFit f) => Matrix4x4.CreateTranslation(-o) * f.Turn() * Matrix4x4.CreateTranslation(o + f.Move);
        var offset = Matrix4x4.Identity;
        if (Matrix4x4.Invert(Place(s), out var undo) && Matrix4x4.Invert(frame, out var inv)) offset = frame * (undo * Place(now)) * inv;
        return (now, offset);
    }
}
