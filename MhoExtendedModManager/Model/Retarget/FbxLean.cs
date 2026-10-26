using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Lean for an FBX source (Kurt, 2026-10-25: Lean did nothing on a From the Game source, only MFF models had it): the model
/// tipped forward (+) or back about the ankles, half of it at the hips, as the MFF fit does (Retarget's Lean), with the
/// automatic amount the same: the head over the ankles at the target's own angle. The skeleton stays as it is and each vertex
/// moves by its bones' share of the turn (as the game would skin it with the turned bones), so the lean holds in every
/// animation; the feet (the ankles and what hangs under them) don't move, the legs only take the ankles' part.
/// </summary>
static class FbxLean
{
    public static void Apply(Retargeted r, float lean, float hipShare = 0.5f)
    {
        int B(string n) => r.Bones.FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        int la = B("g_l_ankle"), ra = B("g_r_ankle"), lh = B("g_l_hip"), rh = B("g_r_hip"), hd = B("g_head");
        r.Lean = 0; r.LeanAuto = 0;
        if (la < 0 || ra < 0) return;
        bool Under(int i, int a) { for (int j = i, guard = 0; j >= 0 && guard < 512; j = r.Bones[j].Parent, guard++) if (j == a) return true; return false; }
        float hip = lh >= 0 && rh >= 0 ? hipShare : 0;
        Vector3 ankles = (r.Bones[la].Position + r.Bones[ra].Position) / 2;
        Vector3 hips = hip > 0 ? (r.Bones[lh].Position + r.Bones[rh].Position) / 2 : ankles;
        static Quaternion Q(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180);
        static Vector3 Rot(Vector3 p, Vector3 pivot, float degrees) => pivot + Vector3.Transform(p - pivot, Q(degrees));

        // automatic: the turn that puts the head over the ankles at the target's own angle (facing +X, up +Z)
        float auto = 0;
        var sk = r.Target;
        int sh = sk.Find("g_head"), sl = sk.Find("g_l_ankle"), sr = sk.Find("g_r_ankle");
        if (hd >= 0 && sh >= 0 && sl >= 0 && sr >= 0)
        {
            static float Angle(Vector3 top, Vector3 foot) => MathF.Atan2(top.X - foot.X, top.Z - foot.Z) * 180 / MathF.PI;
            float want = Angle(sk.Pos(sh), (sk.Pos(sl) + sk.Pos(sr)) / 2);
            Vector3 head = r.Bones[hd].Position;
            float Head(float t) { var hp = Rot(hips, ankles, t * (1 - hip)); return Angle(Rot(Rot(head, ankles, t * (1 - hip)), hp, t * hip), ankles); }
            for (int k = 0; k < 4; k++)
            {
                float f = Head(auto) - want, d = Head(auto + 0.5f) - Head(auto - 0.5f);
                if (MathF.Abs(d) < 1e-4f) break;
                auto -= f / d;
            }
            auto = MathF.Round(Math.Clamp(auto, -20, 20) * 2) / 2;
        }
        r.LeanAuto = auto;
        float use = float.IsNaN(lean) ? auto : lean;
        r.Lean = use;
        if (MathF.Abs(use) < 0.01f) return;

        // each bone's turn: none for the feet, the ankles' part for the legs, the whole lean above the hips
        float ankleTurn = use * (1 - hip), hipTurn = use * hip;
        Vector3 hipPivot = Rot(hips, ankles, ankleTurn);
        var moveA = new bool[r.Bones.Count]; var moveH = new bool[r.Bones.Count];
        for (int i = 0; i < r.Bones.Count; i++)
        {
            moveA[i] = !Under(i, la) && !Under(i, ra);
            moveH[i] = hip > 0 && !Under(i, lh) && !Under(i, rh);
        }
        Vector3 Bone(int b, Vector3 p, bool normal)
        {
            if (b < 0 || b >= moveA.Length || !moveA[b]) return p;
            p = normal ? Vector3.Transform(p, Q(ankleTurn)) : Rot(p, ankles, ankleTurn);
            if (moveH[b]) p = normal ? Vector3.Transform(p, Q(hipTurn)) : Rot(p, hipPivot, hipTurn);
            return p;
        }
        foreach (var s in r.Sections)
            for (int v = 0; v < s.Pos.Length; v++)
            {
                Vector3 p = Vector3.Zero, n = Vector3.Zero; float total = 0;
                foreach (var (b, w) in s.Weights[v]) { p += w * Bone(b, s.Pos[v], false); n += w * Bone(b, s.Normal[v], true); total += w; }
                if (total <= 0) continue;
                s.Pos[v] = p / total;
                if (n.LengthSquared() > 1e-12f) s.Normal[v] = Vector3.Normalize(n);
            }
        r.Notes.Add($"lean: {use:+0.#;-0.#} degrees{(float.IsNaN(lean) ? " (automatic)" : "")}, {ankleTurn:+0.#;-0.#} at the ankles" + (hip > 0 ? $" and {hipTurn:+0.#;-0.#} at the hips" : ""));
    }
}
