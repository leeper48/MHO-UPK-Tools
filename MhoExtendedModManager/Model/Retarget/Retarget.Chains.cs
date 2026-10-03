using System.Numerics;

namespace MhoMffImporter;

static partial class Retarget
{
    /// <summary>MHO bones whose extra MFF children turn with them when swung (weapons in the hand, hair on the head).</summary>
    static readonly System.Text.RegularExpressions.Regex RigidUnder = new(
        @"^g_(head|neck|[lr]_(wrist|palm|thumb\d|index\d|birdy\d|ring\d|pinky\d))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static readonly System.Text.RegularExpressions.Regex JawPart = new(@"(teeth|tooth|tongue|mouth)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex JawName = new(@"(jaw|mouth|chin|^bone_?m$|^bip001 m$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex BipedMouth = new(@"^bip001 m$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static readonly System.Text.RegularExpressions.Regex HairLike = new(@"(hair|bang|pony|tail|braid)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    sealed record ChainMatch(List<(int Mff, int Mho)> BoneMap, List<(int Mho, Vector3 Pos)> Positions, string Describe, float Fit)
    {
        /// <summary>A second (third …) MFF chain on a borrowed strand: its weights go there, the strand stays where the first put it.</summary>
        public bool Shared { get; init; }
    }

    /// <summary>MHO bones that are never cloth / hair chains (placed from the skeleton around them instead). Weapon and prop
    /// bones too (0.10.5): America Chavez's hoodie drawstring paired with Captain America Arctic's g_shieldanad_shield chain and
    /// flew to his hand with the shield in absattack_combo_01. Body bones too (0.10.6): a two-spine MFF rig leaves g_spine03
    /// unmapped, and Arachknight's front cape strip paired with g_spine03 → g_cape1 on Moon Knight.</summary>
    static readonly System.Text.RegularExpressions.Regex Structural = new(
        @"(_offset|ik|twist|_eye|topeyelid|jaw|palm|attach|breast|bind|forarm|uprarm|follow|thumb|index|birdy|ring\d|pinky|toe|ball|^root$"
        + @"|shield|weapon|sword|gun|pistol|rifle|hammer|axe|knife|dagger|staff|spear|_bow|quiver|throwable|prop"
        + @"|^g_(spine|neck|head|pelvis)|clavicle|shoulder)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches MFF extra chains (runs of weighted bones with no Biped mapping: cape strips, hair, ponytails) to the MHO
    /// hero's own extra chains (cape / hair bones), one to one: scored by how far apart they start plus how different their
    /// shapes are from their own starts (6 points along each; MFF posed, in MHO space; MHO at rest), same side of the body;
    /// the best pairs win first. (A plain point-to-point distance under 15 % of the height matched no cape on Storm: her
    /// MFF cape hangs straight down, the MHO one flares backward.) Inside a pair, each MFF bone's weights go to the MHO bone at the same point along the chain,
    /// and the MHO chain's bones move onto the MFF chain (its shape is kept, as the rest of the body's proportions are).
    /// </summary>
    static List<ChainMatch> MatchChains(MffModel m, Vector3[] newPos, Func<Vector3, Vector3> toMho, MhoSkeleton sk, HashSet<int> mhoMapped, Retargeted r,
                                        List<(string Mff, string Mho)>? forced, HashSet<string>? keptUnpaired = null)
    {
        // MFF chains: linear runs of unmapped bones that carry weights or lead to ones that do.
        var weighted = m.Bones.Select(b => b.Deforms).ToArray();
        var kidsM = Enumerable.Range(0, m.Bones.Count).ToLookup(i => m.Bones[i].Parent);
        bool Unmapped(int i) => !r.Map.ContainsKey(m.Bones[i].Name);
        bool Useful(int i) => weighted[i] || kidsM[i].Any(Useful);
        var mffChains = new List<List<int>>();
        for (int i = 0; i < m.Bones.Count; i++)
        {
            if (!Unmapped(i) || !Useful(i)) continue;
            int par = m.Bones[i].Parent;
            bool start = par < 0 || !Unmapped(par) || kidsM[par].Count(k => Unmapped(k) && Useful(k)) > 1;
            if (!start) continue;
            var chain = new List<int> { i };
            for (int cur = i; ;)
            {
                var next = kidsM[cur].Where(k => Unmapped(k) && Useful(k)).ToList();
                if (next.Count != 1) break;
                chain.Add(cur = next[0]);
            }
            mffChains.Add(chain);
        }
        // MHO chains: linear runs of non-structural, unmapped bones.
        var kidsH = Enumerable.Range(0, sk.Bones.Count).Where(i => sk.Bones[i].ParentIndex != i).ToLookup(i => sk.Bones[i].ParentIndex);
        bool Free(int i) => !mhoMapped.Contains(i) && !Structural.IsMatch(sk.Bones[i].Name);
        var mhoChains = new List<List<int>>();
        for (int i = 0; i < sk.Bones.Count; i++)
        {
            if (!Free(i)) continue;
            int par = sk.Bones[i].ParentIndex;
            bool start = par < 0 || par == i || !Free(par) || kidsH[par].Count(Free) > 1;
            if (!start) continue;
            var chain = new List<int> { i };
            for (int cur = i; ;)
            {
                var next = kidsH[cur].Where(Free).ToList();
                if (next.Count != 1) break;
                chain.Add(cur = next[0]);
            }
            mhoChains.Add(chain);
        }

        List<Vector3> MffPts(List<int> c) => c.Select(i => toMho(newPos[i])).ToList();
        List<Vector3> MhoPts(List<int> c) => c.Select(sk.Pos).ToList();
        // Score: how far apart the chains start (where they hang from) + how different their shapes are measured from their
        // own starts (a cape that hangs straight down on MFF but flares backward on MHO still pairs by where it hangs).
        // Never on opposite sides of the body; same region (head or body); roots within 35 % of the height, score under 50 %
        // (Storm's side strips start mid-thigh, MHO's cape chains at the shoulder: 29 units apart on a 91-unit body).
        float H = sk.Height, side = 0.05f * H;
        int Side(Vector3 p) => p.Y > side ? 1 : p.Y < -side ? -1 : 0;
        // Region: a chain hanging from the head / neck (hair, ponytail) only pairs with the MHO hero's head chains, and body
        // chains (capes, coat tails) with body chains (Storm's back cape strip went to her hair without this).
        var headM = new HashSet<string>(["g_head", "g_neck"], StringComparer.OrdinalIgnoreCase);
        bool MffHead(List<int> c)
        {
            for (int j = m.Bones[c[0]].Parent; j >= 0; j = m.Bones[j].Parent)
                if (r.Map.TryGetValue(m.Bones[j].Name, out var mho)) return headM.Contains(mho);
            return false;
        }
        bool MhoHead(List<int> c)
        {
            for (int j = sk.Bones[c[0]].ParentIndex; j >= 0 && sk.Bones[j].ParentIndex != j; j = sk.Bones[j].ParentIndex)
                if (mhoMapped.Contains(j)) return headM.Contains(sk.Bones[j].Name);
            return false;
        }
        float Fit(List<int> ca, List<int> cb)
        {
            var pa = MffPts(ca); var pb = MhoPts(cb);
            float shape = 0;
            for (int k = 0; k < 6; k++) shape += ((At(pa, k / 5f) - pa[0]) - (At(pb, k / 5f) - pb[0])).Length();
            return (pa[0] - pb[0]).Length() + 0.5f * shape / 6;
        }
        // a mouth / jaw bone is never a hair or cape chain (it stays on the head when the hero has no g_jaw): Scream's Bip001 M
        // paired with the borrowed hair root and her jaw swung with the mane (0.14.6). With a map file too, unless the file
        // pairs it itself: Kurt's Scream map leaves Bip001 M unpaired, and the borrowed pass then put it on g_hair1 (0.14.7)
        mffChains.RemoveAll(c => JawName.IsMatch(m.Bones[c[0]].Name)
            && (forced == null || !forced.Any(f => f.Mff.Equals(m.Bones[c[0]].Name, StringComparison.OrdinalIgnoreCase))));
        var cands = new List<(float Score, int A, int B)>();
        if (forced != null)
        {
            // From a bone map file: exactly its pairs. A chain named there is found among the chains, or walked from that
            // bone (single-child runs of unmapped / free bones) if the file starts it elsewhere.
            List<int> Walk(int start, Func<int, IEnumerable<int>> kids, Func<int, bool> ok)
            {
                var c = new List<int> { start };
                for (int cur = start; ;) { var n = kids(cur).Where(ok).ToList(); if (n.Count != 1) break; c.Add(cur = n[0]); }
                return c;
            }
            foreach (var (fm, fh) in forced)
            {
                int im = m.Bones.FindIndex(b => b.Name.Equals(fm, StringComparison.OrdinalIgnoreCase)), ih = sk.Find(fh);
                int a = mffChains.FindIndex(c => c[0] == im);
                if (a < 0) { mffChains.Add(Walk(im, i => kidsM[i], i => Unmapped(i) && Useful(i))); a = mffChains.Count - 1; }
                int b = mhoChains.FindIndex(c => c[0] == ih);
                if (b < 0) { mhoChains.Add(Walk(ih, i => kidsH[i], Free)); b = mhoChains.Count - 1; }
                cands.Add((Fit(mffChains[a], mhoChains[b]), a, b));
            }
        }
        else
        for (int a = 0; a < mffChains.Count; a++)
            for (int b = 0; b < mhoChains.Count; b++)
            {
                var pa = MffPts(mffChains[a]); var pb = MhoPts(mhoChains[b]);
                float la = Length(pa), lb = Length(pb);
                if (Environment.GetEnvironmentVariable("MFF_CHAINDEBUG") == "1")
                    Console.WriteLine($"  test {m.Bones[mffChains[a][0]].Name}@{pa[0]} len {la:0.0} side {Side(pa[0])} head {MffHead(mffChains[a])} → {sk.Bones[mhoChains[b][0]].Name}@{pb[0]} len {lb:0.0} side {Side(pb[0])} head {MhoHead(mhoChains[b])} root {(pa[0] - pb[0]).Length():0.0}");
                if ((la == 0) != (lb == 0)) continue;   // a single bone only pairs with a single bone
                // A lone bone pairs only with a lone MHO hair bone (bangs, ponytail); any other lone MFF bone (thigh helpers,
                // weapon sockets) stays with its parent: paired with MHO fingertips, Punisher's thigh skin followed his thumbs.
                if (la == 0 && !HairLike.IsMatch(sk.Bones[mhoChains[b][0]].Name)) continue;
                // ... and never with one the game moves by physics (0.10.21; Black Cat S01's fringe, a lone Bone100, paired with
                // her simulated g_bangs: in game the physics swung it over her face; the preview runs no physics). It stays
                // rigid on the head.
                if (la == 0 && sk.Simulated.Contains(sk.Bones[mhoChains[b][0]].Name)) continue;
                // ... and only on the same line across the body (within 3 % of the height): Gamora S02's side strands (4.5 units
                // out) paired with MHO's centre g_bangs / g_hair1, and the idle's bangs swing pulled a strand across her chin
                // (Kurt, 0.6.10). A lone strand without such a partner stays rigid on the head.
                if (la == 0 && MathF.Abs(pa[0].Y - pb[0].Y) > 0.03f * H) continue;
                // ... and on the same side front / back (0.10.21; Black Cat's fringe at the front of the head next went to g_hair1
                // at the back, 10.5 units off).
                if (la == 0 && MathF.Abs(pa[0].X - pb[0].X) > 0.05f * H) continue;
                if (la > 0 && (la / lb > 3f || lb / la > 3f)) continue;
                if (Side(pa[0]) * Side(pb[0]) < 0) continue;   // opposite sides
                // A chain down the middle (within 1.5 % of the height) pairs only with a middle MHO chain: Gamora S02's one back
                // hair chain went to MHO's left back strand (MHO has a left and a right one), and that strand's swing folded the
                // middle of her hair (Kurt, 0.6.12, loginscreen_fidget01 frame 74). Unpaired, it stays on the head.
                if (MathF.Abs(pa[0].Y) < 0.015f * H && MathF.Abs(pb[0].Y) >= 0.015f * H) continue;
                if (MffHead(mffChains[a]) != MhoHead(mhoChains[b])) continue;
                // A chain on the head pairs only with MHO hair (0.10.22; America Chavez's back hair went to Captain Marvel ANAD's
                // g_hat_off…g_hat3, which her run animates: the hair swung down into the hood). Unpaired it stays on the head.
                if (MffHead(mffChains[a]) && !HairLike.IsMatch(sk.Bones[mhoChains[b][0]].Name)) continue;
                float root = (pa[0] - pb[0]).Length();
                if (root > 0.35f * H) continue;
                float shape = 0;
                for (int k = 0; k < 6; k++) shape += ((At(pa, k / 5f) - pa[0]) - (At(pb, k / 5f) - pb[0])).Length();
                shape /= 6;
                float score = root + 0.5f * shape;
                if (Environment.GetEnvironmentVariable("MFF_CHAINDEBUG") == "1")
                    Console.WriteLine($"  cand {m.Bones[mffChains[a][0]].Name} → {sk.Bones[mhoChains[b][0]].Name}: root {root:0.0} shape {shape:0.0} score {score:0.0} (limit {0.5f * H:0.0})");
                if (score < 0.5f * H) cands.Add((score, a, b));
            }
        var usedA = new HashSet<int>(); var usedB = new HashSet<int>();
        var result = new List<ChainMatch>();
        foreach (var (score, a, b) in forced != null ? cands : cands.OrderBy(c => c.Score).ToList())
        {
            if (usedA.Contains(a)) continue;
            if (usedB.Contains(b))
            {
                // A map file may pair several chains with one MHO strand (Kurt's Scream map: three hair chains → g_hair1). Each
                // keeps that pair: the later ones share the strand, which stays where the first put it. Before 0.14.8 they were
                // dropped and the borrowed pass re-placed them, so a pick in the Bone Map bounced back.
                if (forced == null) continue;
                usedA.Add(a);
                var sa = mffChains[a]; var sb = mhoChains[b];
                var tsb = Params(MhoPts(sb)); var tsa = Params(MffPts(sa));
                var shared = sa.Select((mi, k) => (mi, sb[Enumerable.Range(0, sb.Count).MinBy(j => MathF.Abs(tsb[j] - tsa[k]))])).ToList();
                result.Add(new ChainMatch(shared, [(sb[0], sk.Pos(sb[0]))],
                    $"{m.Bones[sa[0]].Name}…{m.Bones[sa[^1]].Name} ({sa.Count}) → {sk.Bones[sb[0]].Name} (shared, map file), off by {score:0.0} units", score) { Shared = true });
                continue;
            }
            usedA.Add(a); usedB.Add(b);
            var ca = mffChains[a]; var cb = mhoChains[b];
            var pa = MffPts(ca); var pb = MhoPts(cb);
            var ta = Params(pa); var tb = Params(pb);
            var bones = ca.Select((mi, k) => (mi, cb[Enumerable.Range(0, cb.Count).MinBy(j => MathF.Abs(tb[j] - ta[k]))])).ToList();
            // MHO bones onto the MFF chain at their own relative place; past a single-bone MFF chain, keep MHO offsets.
            var ps = cb.Select((hi, j) => (hi, pa.Count > 1 ? At(pa, tb[j]) : pa[0] + (pb[j] - pb[0]))).ToList();
            result.Add(new ChainMatch(bones, ps,
                $"{m.Bones[ca[0]].Name}…{m.Bones[ca[^1]].Name} ({ca.Count}) → {sk.Bones[cb[0]].Name}…{sk.Bones[cb[^1]].Name} ({cb.Count}), off by {score:0.0} units{(forced != null ? " (map file)" : "")}", score));
        }
        // Many-to-one on borrowed bones (0.14.2, Kurt): a borrowed cape / hair has only a few strands (Hair 3: 5), while an MFF
        // model may have more (Storm S02 5, Black Widow S08 5, Scarlet Witch S07 31), or hair made of lone bones hanging off the
        // head (Scream: Bone_Hair_L_00…04). Left-over chains share the best-fitting borrowed strand (same rules as above, but a
        // strand may be used again; it stays where its first pair put it), and left-over lone hair bones go to the nearest
        // borrowed bone. Only borrowed bones: a hero's own hair still pairs one to one, as before.
        // With a bone map file too (Kurt's Scream map predates the borrowed hair: "0 chains ride Mega Hair"): the chains the
        // file leaves unpaired may ride borrowed bones; the first on an unused strand places it, as a normal pair does.
        if (sk.Borrowed.Count > 0)
        {
            bool Borrowed(List<int> c) => sk.Borrowed.Contains(sk.Bones[c[0]].Name);
            for (int a = 0; a < mffChains.Count; a++)
            {
                if (usedA.Contains(a) || !mffChains[a].Any(x => weighted[x])) continue;
                if (keptUnpaired?.Contains(m.Bones[mffChains[a][0]].Name) == true) continue;   // "(not paired)" picked in the map
                var ca = mffChains[a]; var pa = MffPts(ca); float la = Length(pa);
                bool head = MffHead(ca);
                int bestB = -1; float bestScore = 0.5f * H; int bestBone = -1;
                for (int b = 0; b < mhoChains.Count; b++)
                {
                    var cb = mhoChains[b];
                    if (!Borrowed(cb) || MhoHead(cb) != head) continue;
                    if (head && !HairLike.IsMatch(sk.Bones[cb[0]].Name)) continue;
                    var pb = MhoPts(cb);
                    if (la == 0)
                    {
                        // a lone bone: the nearest bone of any borrowed strand (within 15 % of the height)
                        for (int j = 0; j < cb.Count; j++)
                        {
                            float d = (pb[j] - pa[0]).Length();
                            if (d < bestScore && d < 0.15f * H) { bestScore = d; bestB = b; bestBone = cb[j]; }
                        }
                        continue;
                    }
                    if (Side(pa[0]) * Side(pb[0]) < 0) continue;
                    float lb = Length(pb);
                    if (lb == 0 || la / lb > 4f || lb / la > 4f) continue;
                    float root = (pa[0] - pb[0]).Length();
                    if (root > 0.35f * H) continue;
                    float shape = 0;
                    for (int k = 0; k < 6; k++) shape += ((At(pa, k / 5f) - pa[0]) - (At(pb, k / 5f) - pb[0])).Length();
                    float score = root + 0.5f * shape / 6;
                    if (score < bestScore) { bestScore = score; bestB = b; bestBone = -1; }
                }
                if (bestB < 0) continue;
                usedA.Add(a);
                var chainB = mhoChains[bestB];
                if (bestBone < 0 && !usedB.Contains(bestB))
                {
                    // the strand's first chain: placed on it, as in the one-to-one pass
                    usedB.Add(bestB);
                    var pb1 = MhoPts(chainB); var ta1 = Params(pa); var tb1 = Params(pb1);
                    var bones1 = ca.Select((mi, k) => (mi, chainB[Enumerable.Range(0, chainB.Count).MinBy(j => MathF.Abs(tb1[j] - ta1[k]))])).ToList();
                    var ps1 = chainB.Select((hi, j) => (hi, pa.Count > 1 ? At(pa, tb1[j]) : pa[0] + (pb1[j] - pb1[0]))).ToList();
                    result.Add(new ChainMatch(bones1, ps1, $"{m.Bones[ca[0]].Name}…{m.Bones[ca[^1]].Name} ({ca.Count}) → {sk.Bones[chainB[0]].Name}…{sk.Bones[chainB[^1]].Name} ({chainB.Count}, borrowed), off by {bestScore:0.0} units", bestScore));
                    continue;
                }
                List<(int, int)> map;
                if (bestBone >= 0) map = [(ca[0], bestBone)];
                else
                {
                    var tb2 = Params(MhoPts(chainB)); var ta2 = Params(pa);
                    map = ca.Select((mi, k) => (mi, chainB[Enumerable.Range(0, chainB.Count).MinBy(j => MathF.Abs(tb2[j] - ta2[k]))])).ToList();
                }
                int first = bestBone >= 0 ? bestBone : chainB[0];
                result.Add(new ChainMatch(map, [(first, sk.Pos(first))],
                    $"{m.Bones[ca[0]].Name}…{m.Bones[ca[^1]].Name} ({ca.Count}) → {sk.Bones[first].Name} (shared borrowed strand), off by {bestScore:0.0} units", bestScore) { Shared = true });
            }
        }
        var left = mffChains.Where((c, i) => !usedA.Contains(i) && c.Any(x => weighted[x])).Select(c => m.Bones[c[0]].Name).ToList();
        r.UnpairedChains.AddRange(left);
        if (left.Count > 0) r.Notes.Add($"{left.Count} MFF chain(s) {(forced != null ? "not paired in the map file" : "with no MHO chain near enough")} (weights stay on the nearest mapped parent): {string.Join(", ", left)}");
        return result;
    }

    static float Length(List<Vector3> p) { float l = 0; for (int i = 1; i < p.Count; i++) l += (p[i] - p[i - 1]).Length(); return l; }

    /// <summary>Each point's place along the chain (0 = first, 1 = last).</summary>
    static float[] Params(List<Vector3> p)
    {
        var t = new float[p.Count]; float total = Length(p), acc = 0;
        for (int i = 1; i < p.Count; i++) { acc += (p[i] - p[i - 1]).Length(); t[i] = total > 0 ? acc / total : 0; }
        return t;
    }

    /// <summary>The point at place t (0..1) along the chain.</summary>
    static Vector3 At(List<Vector3> p, float t)
    {
        if (p.Count == 1) return p[0];
        float total = Length(p), want = t * total, acc = 0;
        for (int i = 1; i < p.Count; i++)
        {
            float seg = (p[i] - p[i - 1]).Length();
            if (acc + seg >= want || i == p.Count - 1) return seg > 0 ? Vector3.Lerp(p[i - 1], p[i], Math.Clamp((want - acc) / seg, 0, 1)) : p[i];
            acc += seg;
        }
        return p[^1];
    }

    static void Add(Dictionary<int, float> acc, int bone, float w) { if (w > 0) acc[bone] = acc.GetValueOrDefault(bone) + w; }

    /// <summary>Where MHO's ball bone sits along its own foot: ankle→ball over ankle→toe tip, the tip being the furthest
    /// point of the vertices the ball bone drives most (0.7 when the mesh doesn't tell).</summary>
    static float BallRatio(MhoSkeleton sk, int ankle, int ball)
    {
        var lod = sk.Mesh.HighestDetail;
        var A = sk.Pos(ankle); var B = sk.Pos(ball);
        var dir = B - A; float ab = dir.Length();
        if (lod == null || ab < 1e-4f) return 0.7f;
        dir /= ab;
        float tip = 0;
        for (int v = 0; v < lod.Positions.Count; v++)
        {
            var inf = lod.Influences[v];
            if (inf.Bones == null || inf.Bones.Count == 0) continue;
            int dom = inf.Bones[inf.Weights.Select((w, k) => (w, k)).MaxBy(x => x.w).k];
            if (dom != ball) continue;
            tip = MathF.Max(tip, Vector3.Dot(lod.Positions[v] - A, dir));
        }
        return tip > ab ? ab / tip : 0.7f;
    }

    /// <summary>Shortest rotation taking unit vector a onto unit vector b.</summary>
    static Quaternion Arc(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1 + d));
    }
}
