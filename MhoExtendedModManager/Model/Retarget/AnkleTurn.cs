using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The Model tab's Ankle slider (Kurt, 2026-11-20: a fitted model's toes still pointed up in some animations): each foot of
/// the fitted model (g_l_ankle / g_r_ankle and what hangs under them) turned about its ankle, around the side-to-side axis,
/// in degrees (+ toes down, − toes up). The skeleton stays as it is: each vertex moves by its bones' share of the turn, so the
/// skin above the ankle blends into it and the turn holds in every animation.
/// </summary>
static class AnkleTurn
{
    public static string? Apply(Retargeted r, float degrees)
    {
        if (MathF.Abs(degrees) < 0.01f) return null;
        int B(string n) => r.Bones.FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        int la = B("g_l_ankle"), ra = B("g_r_ankle");
        if (la < 0 || ra < 0) return null;
        bool Under(int i, int a) { for (int j = i, guard = 0; j >= 0 && guard < 512; j = r.Bones[j].Parent, guard++) if (j == a) return true; return false; }
        // (MHO frame: facing +X, left +Y, up +Z; a turn about +Y takes +X down: toes down for +)
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180);
        var foot = new int[r.Bones.Count];   // the ankle a bone turns about (-1: none)
        for (int i = 0; i < foot.Length; i++) foot[i] = Under(i, la) ? la : Under(i, ra) ? ra : -1;
        Vector3 Bone(int b, Vector3 p, bool normal)
        {
            if (b < 0 || b >= foot.Length || foot[b] < 0) return p;
            if (normal) return Vector3.Transform(p, q);
            var pivot = r.Bones[foot[b]].Position;
            return pivot + Vector3.Transform(p - pivot, q);
        }
        int moved = 0;
        foreach (var s in r.Sections)
            for (int v = 0; v < s.Pos.Length; v++)
            {
                if (!s.Weights[v].Any(x => x.Bone >= 0 && x.Bone < foot.Length && foot[x.Bone] >= 0)) continue;
                Vector3 p = Vector3.Zero, n = Vector3.Zero; float total = 0;
                foreach (var (b, w) in s.Weights[v]) { p += w * Bone(b, s.Pos[v], false); n += w * Bone(b, s.Normal[v], true); total += w; }
                if (total <= 0) continue;
                s.Pos[v] = p / total;
                if (n.LengthSquared() > 1e-12f) s.Normal[v] = Vector3.Normalize(n);
                moved++;
            }
        return $"ankle: the feet turned {degrees:+0.#;-0.#} degrees at the ankles (+ toes down), {moved} vertices";
    }
}
