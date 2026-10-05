using System.Numerics;

namespace MhoExtendedModManager.Model;

static partial class Retarget
{
    sealed partial class Job
    {
        /// <summary>
        /// Long hair (Gamora S02, 0.6.11-0.6.15). MFF hair is nearly all on the head bone, so the whole mass turns with it and
        /// went into her shoulders when she turned her head (Kurt). Two passes:
        /// <list type="number">
        /// <item><see cref="Fall"/> (while the mesh is built): head / neck weight of a vertex below the base of the neck and out
        /// from it (hair, not neck skin) moves to the chest (g_spine03), more the lower it hangs: none at the neck base, all at
        /// shoulder height; none within 4 % of the height of the neck's axis, fully from 8 %. Over a shoulder it follows that
        /// side's collarbone instead, by how far out it hangs (0.6.13: at loginscreen_fidget01 frame 74 the shoulders come up
        /// and the coat collar went through hair that followed only the chest).</item>
        /// <item><see cref="RideOnBody"/> (after): hair resting on the body rides on what's under it (0.6.14, Kurt's manual
        /// technique: at fidget frame 73 the coat's lapel came up through the hair ends, since the coat there also follows the
        /// arm): each such hair vertex takes, for the part that leaves the head, the weights of the nearest vertex that isn't on
        /// the head (coat, body) within 10 % of the height.</item>
        /// </list>
        /// </summary>
        sealed class Hair
        {
            readonly Job j;
            readonly bool on;
            readonly int head = -1, neckBone = -1, chest = -1, lClav = -1, rClav = -1;
            readonly Vector3 neck;
            readonly float drop = 1f, shoulderOut = 1f;
            readonly List<(int Section, int V, float T, float Away, Dictionary<int, float> Acc)> rec = new();

            public Hair(Job job)
            {
                j = job;
                var sk = j.sk; var pos = j.pos;
                int hh = sk.Find("g_head"), hn = sk.Find("g_neck"), hc = sk.Find("g_spine03"), hs = sk.Find("g_l_shoulder");
                if (!j.opt.HairFall || hh < 0 || hn < 0 || hc < 0 || hs < 0 || pos[hn] == null || pos[hs] == null) return;
                float d = Vector3.Dot(pos[hn]!.Value - pos[hs]!.Value, sk.Up);
                int lc = sk.Find("g_l_clavical"), rc = sk.Find("g_r_clavical");
                float sOut = MathF.Abs(Vector3.Dot(pos[hs]!.Value - pos[hn]!.Value, sk.Left));
                if (d <= 0.005f * sk.Height) return;
                (on, head, neckBone, chest, neck, drop, lClav, rClav, shoulderOut) = (true, hh, hn, hc, pos[hn]!.Value, d, lc >= 0 ? lc : hc, rc >= 0 ? rc : hc, sOut);
            }

            /// <summary>Pass 1 for one vertex (<paramref name="p"/> in MHO space): moves its head / neck weight in <paramref name="acc"/>
            /// and records it for pass 2.</summary>
            public void Fall(Dictionary<int, float> acc, Vector3 p, int section, int v, bool hairBone = false)
            {
                if (!on) return;
                var sk = j.sk;
                var off = p - neck;
                float z = Vector3.Dot(off, sk.Up);
                float d = (off - z * sk.Up).Length();
                float t = Math.Clamp(-z / drop, 0, 1) * Math.Clamp((d - 0.04f * sk.Height) / (0.04f * sk.Height), 0, 1);
                t = t * t * (3 - 2 * t);
                float away = Math.Clamp((d - 0.04f * sk.Height) / (0.04f * sk.Height), 0, 1);
                // skin the MFF rig puts on hair bones is hair however close it hangs to the neck (0.10.22; America's hair
                // right behind her neck counted as neck skin, so the hood came through it when she ran)
                if (hairBone) away = 1;
                if (away > 0 && z < Reach() * sk.Height && (acc.GetValueOrDefault(head) > 0 || acc.GetValueOrDefault(neckBone) > 0))
                    rec.Add((section, v, t, away, new Dictionary<int, float>(acc)));
                if (t <= 0) return;
                foreach (int hb in new[] { head, neckBone })
                    if (acc.TryGetValue(hb, out float hw) && hw > 0)
                    {
                        float lat = Vector3.Dot(off, sk.Left);
                        float c = Math.Clamp((MathF.Abs(lat) - 0.04f * sk.Height) / MathF.Max(1e-3f, shoulderOut - 0.04f * sk.Height), 0, 1);
                        acc[hb] = hw * (1 - t);
                        Add(acc, chest, hw * t * (1 - c));
                        Add(acc, lat > 0 ? lClav : rClav, hw * t * c);
                    }
            }

            /// <summary>How far above the neck base hair may rest on the body, as a share of the height (0.10.22; America
            /// Chavez's hood rises above the neck base and came up through her hair while running: 0.03 before).</summary>
            /// <summary>Contact: full within <see cref="ContactFull"/>, none from <see cref="ContactNone"/> (shares of the height;
            /// 0.015 / 0.05 before 0.10.22; MFF_HAIRCONTACTFULL / MFF_HAIRCONTACTNONE).</summary>
            static float ContactFull() => Env("MFF_HAIRCONTACTFULL", 0.015f);
            static float ContactNone() => Env("MFF_HAIRCONTACTNONE", 0.05f);
            static float Env(string n, float d) => float.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : d;
            static float Reach() => float.TryParse(Environment.GetEnvironmentVariable("MFF_HAIRREACH"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0.07f;

            /// <summary>Pass 2: the recorded hair vertices take the weights of the nearest coat / body vertex, by the larger of
            /// how far below the neck they hang and how close they rest on it (Kurt, 0.6.15: "have the clavicle affect the point
            /// where it meets more": full within 1.5 % of the height, none from 5 %).</summary>
            public void RideOnBody()
            {
                if (rec.Count == 0) return;
                var sk = j.sk; var r = j.r;
                var headSet = new HashSet<int>();
                for (int i = 0; i < sk.Bones.Count; i++)
                    for (int k = i, g = 0; k >= 0 && g < 64; k = sk.Bones[k].ParentIndex == k ? -1 : sk.Bones[k].ParentIndex, g++)
                        if (k == neckBone) { headSet.Add(i); break; }
                // limbs: forearms / hands and legs (0.11.7, Kurt: Scream's mane on Carnage tore into sheets that swung with the
                // claws). Hair hanging beside a forearm isn't carried by it; that skin takes hair only by contact. The upper arm
                // still carries hanging hair (0.11.8: with the whole arm excluded, Black Cat's hair over her shoulders clipped:
                // the regression set's idle_combat 0 → 3.6 %).
                // Legs never carry hair (0.11.8, Kurt: sharp points at Scream's knees on Medusa): her strand tips hang beside the
                // knees, rode the leg while the strand above rode the chest, and the strand tore when the leg moved.
                var limbSet = new HashSet<int>(); var legSet = new HashSet<int>();
                for (int i = 0; i < sk.Bones.Count; i++)
                    for (int k = i, g = 0; k >= 0 && g < 64; k = sk.Bones[k].ParentIndex == k ? -1 : sk.Bones[k].ParentIndex, g++)
                        if (System.Text.RegularExpressions.Regex.Match(sk.Bones[k].Name, "^g_[lr]_(elbow|lwrarm|hip|uprleg|thigh)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } lm)
                        { limbSet.Add(i); if (lm.Groups[1].Value.ToLowerInvariant() is "hip" or "uprleg" or "thigh") legSet.Add(i); break; }
                bool limbs = j.opt.HairOffLimbs;
                var hairKey = rec.Select(h => (h.Section, h.V)).ToHashSet();
                var body = new List<(Vector3 P, (int, float)[] W, bool Limb)>();
                for (int si = 0; si < r.Sections.Count; si++)
                    for (int v = 0; v < r.Sections[si].Pos.Length; v++)
                    {
                        var wv = r.Sections[si].Weights[v];
                        if (hairKey.Contains((si, v)) || wv.Length == 0 || headSet.Contains(wv.MaxBy(x => x.Item2).Item1)) continue;
                        r.BodyVerts.Add((si, v));   // the clip check still counts legs
                        if (j.opt.HairOffLegs && legSet.Contains(wv.MaxBy(x => x.Item2).Item1)) continue;
                        body.Add((r.Sections[si].Pos[v], wv, limbs && limbSet.Contains(wv.MaxBy(x => x.Item2).Item1)));
                    }
                float reach = 0.10f * sk.Height; int rode = 0;
                foreach (var h in rec)
                {
                    var p = r.Sections[h.Section].Pos[h.V];
                    int best = -1, bestTorso = -1; float bd = reach * reach, bt = reach * reach;
                    for (int k = 0; k < body.Count; k++)
                    {
                        float dd = Vector3.DistanceSquared(p, body[k].P);
                        if (dd < bd) { bd = dd; best = k; }
                        if (!body[k].Limb && dd < bt) { bt = dd; bestTorso = k; }
                    }
                    r.HairVerts.Add((h.Section, h.V));
                    // hair standing far out from the body (Scream's mane, 0.11.7) stays on the head: hanging moves it onto the
                    // body fully within 4 % of the height of the nearest body skin, not at all from 8 %
                    float near = 1;
                    if (j.opt.HairNearBody)
                    {
                        float dist = best < 0 ? float.MaxValue : MathF.Sqrt(bd);
                        near = Math.Clamp((0.08f * sk.Height - dist) / (0.04f * sk.Height), 0, 1);
                        near = near * near * (3 - 2 * near);
                    }
                    float hang = h.T * near;
                    if (hang < h.T && best < 0) { r.Sections[h.Section].Weights[h.V] = Top4(h.Acc); continue; }
                    if (best < 0) continue;
                    float contact = !j.opt.HairContact ? 0
                        : Math.Clamp((ContactNone() * sk.Height - MathF.Sqrt(bd)) / ((ContactNone() - ContactFull()) * sk.Height), 0, 1);
                    contact = contact * contact * (3 - 2 * contact) * h.Away;
                    // hanging (not touching) rides only on the torso; nearest is a limb and no contact → the torso's nearest
                    // (none within reach: the chest / collarbone split of pass 1 stays)
                    if (body[best].Limb && contact < hang) best = bestTorso;
                    float te = MathF.Max(hang, contact);
                    if (best < 0 || te <= 0)
                    {
                        if (hang < h.T) r.Sections[h.Section].Weights[h.V] = Top4(h.Acc);   // undo pass 1's chest share
                        continue;
                    }
                    var acc = new Dictionary<int, float>(h.Acc);
                    foreach (int hb in new[] { head, neckBone })
                        if (acc.TryGetValue(hb, out float hw) && hw > 0)
                        {
                            acc[hb] = hw * (1 - te);
                            foreach (var (bb, bw) in body[best].W) Add(acc, bb, hw * te * bw);
                        }
                    r.Sections[h.Section].Weights[h.V] = Top4(acc);
                    rode++;
                }
                r.Notes.Add($"hair: {rec.Count} vertices hang below the neck; {rode} ride on the nearest coat / body vertex, the rest on the chest / collarbones");
            }
        }
    }
}
