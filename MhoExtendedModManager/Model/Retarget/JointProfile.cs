using System.Numerics;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Game Joint Blend (2026-10-25; users: biceps, triceps and thighs warp in motion, the seat creases). An MFF model's skin
/// hands over from body to limb in a narrow band (Red She-Hulk's seat: all on the leg up to the hip joint, all on the pelvis
/// one band above); the game's models spread it over a wide band that starts well below the joint (She-Hulk Lawyer's seat:
/// 0.82, 0.55, 0.41, 0.23 on the leg). Measured with --fit-distort / --fit-heat: in idle_combat the fitted hips stretched 9
/// to 11 times, the game's 3. Here each joint's skin takes the target's own stock model's share between limb and body, by
/// place along the limb (0 at the joint, 1 at the next one, negative above) and by quarter around it; each vertex keeps its
/// own split among the limb's bones and among the body's. Only skin wholly on that limb and the body next to it (not a hand
/// hanging beside the hip, not hair). The middle strip between the legs follows the stock middle strip, both legs together.
/// </summary>
static class JointProfile
{
    const float Lo = -0.5f, Hi = 0.6f, Step = 0.05f;
    /// <summary>How far from the joint (in limb lengths) its skin counts: without it the shoulder's band ran across the torso
    /// (the arm hangs down) and gave waist skin half its weight on the upper arm. MHO_JOINT_RADIUS overrides (test).</summary>
    static float Radius => float.TryParse(Environment.GetEnvironmentVariable("MHO_JOINT_RADIUS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : 0.6f;
    static readonly int Bins = (int)MathF.Round((Hi - Lo) / Step);

    /// <summary>A joint: its bone, the next joint down the limb, the limb's regions and the body's (see FitDistort.Region).</summary>
    sealed record Joint(string Name, string Bone, string Next, string[] Limb, string[] Body);

    static IEnumerable<Joint> Joints()
    {
        foreach (string s in new[] { "l", "r" })
        {
            string S = s.ToUpperInvariant() + " ";
            // (the other thigh counts as body: the crotch carries both legs, and skipped by both sides it was never blended,
            // 2026-10-25: Red She-Hulk's crotch stayed at 9.3 times stretched with the blend on)
            string O = (s == "l" ? "R" : "L") + " ";
            yield return new("hip", $"g_{s}_hip", $"g_{s}_knee", [S + "thigh", S + "calf", S + "foot"], ["pelvis", "chest", O + "thigh"]);
            yield return new("knee", $"g_{s}_knee", $"g_{s}_ankle", [S + "calf", S + "foot"], [S + "thigh"]);
            yield return new("shoulder", $"g_{s}_shoulder", $"g_{s}_elbow", [S + "upper arm", S + "forearm", S + "hand"], [S + "clavicle", "chest"]);
            yield return new("elbow", $"g_{s}_elbow", $"g_{s}_wrist", [S + "forearm", S + "hand"], [S + "upper arm"]);
        }
    }

    /// <summary>One model's view of a joint: the joint, the limb's direction and length, a frame around it, the bone sets.</summary>
    sealed class Frame
    {
        public Vector3 At, Down, Ref1, Ref2; public float Len, Side;
        public HashSet<int> Limb = [], Body = [];
        public int Bone;

        public static Frame? Of(Joint j, IReadOnlyList<string> names, Func<int, Vector3> pos)
        {
            int B(string n) { for (int k = 0; k < names.Count; k++) if (names[k].Equals(n, StringComparison.OrdinalIgnoreCase)) return k; return -1; }
            int a = B(j.Bone), b = B(j.Next);
            if (a < 0 || b < 0) return null;
            var at = pos(a); var d = pos(b) - at; float len = d.Length();
            if (len < 1e-3f) return null;
            var down = d / len;
            var r1 = Vector3.UnitX - Vector3.Dot(Vector3.UnitX, down) * down;
            if (r1.LengthSquared() < 1e-4f) r1 = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, down) * down;
            r1 = Vector3.Normalize(r1);
            var f = new Frame { At = at, Down = down, Len = len, Ref1 = r1, Ref2 = Vector3.Cross(down, r1), Bone = a, Side = at.Y };
            for (int k = 0; k < names.Count; k++)
            {
                string rg = FitDistort.Region(names[k]);
                if (j.Limb.Contains(rg)) f.Limb.Add(k); else if (j.Body.Contains(rg)) f.Body.Add(k);
            }
            return f;
        }

        public float T(Vector3 p) => Vector3.Dot(p - At, Down) / Len;
        public bool Near(Vector3 p) => (p - At).Length() <= Radius * Len;
        public int Quarter(Vector3 p)
        {
            var q = p - At; q -= Vector3.Dot(q, Down) * Down;
            float ang = MathF.Atan2(Vector3.Dot(q, Ref2), Vector3.Dot(q, Ref1)) + MathF.PI;
            return Math.Clamp((int)(ang / (MathF.PI / 2)), 0, 3);
        }
        /// <summary>On this joint's side, not in the middle strip (within 15 % of the hip's sideways offset of the centre).</summary>
        public bool OnSide(Vector3 p, float middle) => MathF.Sign(p.Y) == MathF.Sign(Side) && MathF.Abs(p.Y) >= middle;

        public bool Eligible((int Bone, float Weight)[] w)
        {
            bool limb = false;
            foreach (var (b, x) in w) { if (x < 0.01f) continue; if (Limb.Contains(b)) limb = true; else if (!Body.Contains(b)) return false; }
            return limb || w.Any(x => x.Weight >= 0.01f && Body.Contains(x.Bone));
        }

        public float Share((int Bone, float Weight)[] w) => w.Where(x => Limb.Contains(x.Bone)).Sum(x => x.Weight);
    }

    /// <summary>The stock profile: limb share per (quarter, bin), empty bins from the nearest filled one (same quarter, else any).</summary>
    static float[,]? Profile(Frame f, IReadOnlyList<Vector3> pos, (int, float)[][] w, Func<Vector3, bool> where)
    {
        var sum = new float[4, Bins]; var cnt = new int[4, Bins];
        for (int v = 0; v < pos.Count; v++)
        {
            var p = pos[v]; float t = f.T(p);
            if (t < Lo || t >= Hi || !f.Near(p) || !where(p) || !f.Eligible(w[v])) continue;
            int b = Math.Min(Bins - 1, (int)((t - Lo) / Step)), q = f.Quarter(p);
            sum[q, b] += f.Share(w[v]); cnt[q, b]++;
        }
        if (cnt.Cast<int>().Sum() < 20) return null;
        var prof = new float[4, Bins];
        for (int q = 0; q < 4; q++)
            for (int b = 0; b < Bins; b++)
            {
                int qq = -1, bb = -1;
                for (int d = 0; d < Bins && bb < 0; d++)
                    foreach (int c in new[] { q, (q + 1) % 4, (q + 3) % 4, (q + 2) % 4 })
                    {
                        if (b - d >= 0 && cnt[c, b - d] > 0) { qq = c; bb = b - d; break; }
                        if (b + d < Bins && cnt[c, b + d] > 0) { qq = c; bb = b + d; break; }
                    }
                prof[q, b] = sum[qq, bb] / cnt[qq, bb];
            }
        return prof;
    }

    static float Want(float[,] prof, Frame f, Vector3 p)
    {
        float bf = (f.T(p) - Lo) / Step - 0.5f;
        int b0 = Math.Clamp((int)MathF.Floor(bf), 0, Bins - 1), b1 = Math.Min(Bins - 1, b0 + 1); float u = Math.Clamp(bf - b0, 0, 1);
        int q = f.Quarter(p);
        return prof[q, b0] * (1 - u) + prof[q, b1] * u;
    }

    /// <summary>A vertex's weights with <paramref name="want"/> on the limb's bones (in their own proportions) and the rest on
    /// the body's; a side that had nothing takes its share on <paramref name="limbBone"/> / <paramref name="bodyBone"/>.</summary>
    static (int, float)[] Reshare((int Bone, float Weight)[] w, HashSet<int> limb, float want, int limbBone, int bodyBone)
    {
        float leg = w.Where(x => limb.Contains(x.Bone)).Sum(x => x.Weight), other = 1 - leg;
        var acc = new Dictionary<int, float>();
        foreach (var (b, x) in w)
        {
            float nx = limb.Contains(b) ? (leg > 1e-4f ? x / leg * want : 0) : (other > 1e-4f ? x / other * (1 - want) : 0);
            acc[b] = acc.GetValueOrDefault(b) + nx;
        }
        if (leg <= 1e-4f && want > 0) acc[limbBone] = acc.GetValueOrDefault(limbBone) + want;
        if (other <= 1e-4f && want < 1) acc[bodyBone] = acc.GetValueOrDefault(bodyBone) + 1 - want;
        var top = acc.Where(x => x.Value > 1e-4f).OrderByDescending(x => x.Value).Take(4).ToList();
        float s = top.Sum(x => x.Value);
        return [.. top.Select(x => (x.Key, x.Value / s))];
    }

    public static void Apply(Retargeted r, MhoSkeleton sk)
    {
        var lod = sk.Mesh.HighestDetail;
        if (lod == null || lod.Positions.Count == 0) { r.Notes.Add("game joint blend: no stock model to take it from"); return; }
        var sw = MhoAnim.Weights(lod);
        var names = sk.Bones.Select(b => b.Name).ToList();
        int lh = names.FindIndex(n => n.Equals("g_l_hip", StringComparison.OrdinalIgnoreCase));
        float sMid = lh >= 0 ? MathF.Abs(sk.Pos(lh).Y) * 0.15f : 0, fMid = lh >= 0 ? MathF.Abs(r.Bones[lh].Position.Y) * 0.15f : 0;
        var done = new List<string>();
        // MHO_JOINT_SKIP=hip,knee,shoulder,elbow,mid (tests): those parts left out
        var skip = (Environment.GetEnvironmentVariable("MHO_JOINT_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var j in Joints())
        {
            if (skip.Contains(j.Name)) continue;
            var sf = Frame.Of(j, names, sk.Pos); var ff = Frame.Of(j, names, k => r.Bones[k].Position);
            if (sf == null || ff == null) continue;
            var prof = Profile(sf, lod.Positions, sw, p => sf.OnSide(p, sMid));
            if (prof == null) continue;
            int bodyBone = sf.Body.Count > 0 ? (j.Name == "hip" ? names.FindIndex(n => n.Equals("g_pelvis", StringComparison.OrdinalIgnoreCase)) : sf.Body.OrderBy(b => Vector3.DistanceSquared(sk.Pos(b), sf.At)).First()) : -1;
            if (bodyBone < 0) continue;
            int n = 0;
            foreach (var s in r.Sections)
                for (int v = 0; v < s.Pos.Length; v++)
                {
                    var p = s.Pos[v]; float t = ff.T(p);
                    if (t < Lo || t >= Hi || !ff.Near(p) || !ff.OnSide(p, fMid) || !ff.Eligible(s.Weights[v])) continue;
                    s.Weights[v] = Reshare(s.Weights[v], ff.Limb, Want(prof, ff, p), ff.Bone, bodyBone);
                    n++;
                }
            if (n > 0) done.Add($"{j.Bone} {n:N0}");
        }
        // the middle strip between the legs: both legs together
        var legs = Joints().Where(j => j.Name == "hip").Select(j => (S: Frame.Of(j, names, sk.Pos), F: Frame.Of(j, names, k => r.Bones[k].Position))).ToList();
        if (legs.All(x => x.S != null && x.F != null) && legs.Count == 2 && !skip.Contains("mid"))
        {
            Frame Mid(Frame a, Frame b) => new() { At = (a.At + b.At) / 2, Down = Vector3.Normalize(a.Down + b.Down), Len = (a.Len + b.Len) / 2, Ref1 = a.Ref1, Ref2 = a.Ref2, Side = 0, Bone = a.Bone, Limb = [.. a.Limb, .. b.Limb], Body = a.Body };
            var sm = Mid(legs[0].S!, legs[1].S!); var fm = Mid(legs[0].F!, legs[1].F!);
            var prof = Profile(sm, lod.Positions, sw, p => MathF.Abs(p.Y) < sMid);
            int pelvis = names.FindIndex(x => x.Equals("g_pelvis", StringComparison.OrdinalIgnoreCase));
            if (prof != null && pelvis >= 0)
            {
                int n = 0;
                foreach (var s in r.Sections)
                    for (int v = 0; v < s.Pos.Length; v++)
                    {
                        var p = s.Pos[v]; float t = fm.T(p);
                        if (t < Lo || t >= Hi || !fm.Near(p) || MathF.Abs(p.Y) >= fMid || !fm.Eligible(s.Weights[v])) continue;
                        // a vertex with no leg weight takes the leg on its side
                        int leg = p.Y * legs[0].F!.Side >= 0 ? legs[0].F!.Bone : legs[1].F!.Bone;
                        s.Weights[v] = Reshare(s.Weights[v], fm.Limb, Want(prof, fm, p), leg, pelvis);
                        n++;
                    }
                if (n > 0) done.Add($"between the legs {n:N0}");
            }
        }
        r.Notes.Add(done.Count > 0 ? $"game joint blend: skin around the joints shared as {sk.Name}'s own model shares it ({string.Join(", ", done)})" : "game joint blend: no joint matched");
    }
}
