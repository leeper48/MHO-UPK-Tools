using System.Numerics;
using AnimExportCli.Animation;

namespace MhoExtendedModManager.Model;

/// <summary>
/// How much a fit stretches or squashes the skin, region by region (2026-10-25; users: biceps, triceps and thighs warp, the
/// ankles crease with Lean). Read only. Per triangle: the in-plane deformation (its two singular values s1 ≥ s2) from one
/// shape to another, after the fit's uniform scale. Rest: the fitted mesh against the MFF model itself (the swing alone is
/// rigid, so anything here comes from blending across joints). Animated: each mesh posed against its own bind pose, the fitted
/// model beside the game's model in the same animations, so the game's own share of runtime deformation is visible.
/// Regions come from the triangle's MHO bones; a triangle with 25 % or more on a second region counts to the joint between them.
/// </summary>
static class FitDistort
{
    public sealed record Stat(string Region, int Count, float AreaLog, float AreaAbs, float Stretch95, float Aniso95);

    /// <summary>The region an MHO bone belongs to.</summary>
    public static string Region(string bone)
    {
        string n = bone.ToLowerInvariant();
        string side = n.StartsWith("g_l_") ? "L " : n.StartsWith("g_r_") ? "R " : "";
        string part =
            n.Contains("biceptwist") || n.Contains("shoulder") || n.Contains("uprarm") ? "upper arm" :
            n.Contains("clavical") ? "clavicle" :
            n.Contains("elbow") || n.Contains("forarm") ? "forearm" :
            n.Contains("wrist") || n.Contains("thumb") || n.Contains("index") || n.Contains("birdy") || n.Contains("ring") || n.Contains("pinky") ? "hand" :
            n.Contains("thightwist") || n.Contains("_hip") || n.Contains("uprleg") ? "thigh" :
            n.Contains("knee") ? "calf" :
            n.Contains("ankle") || n.Contains("ball") || n.Contains("toe") ? "foot" :
            n.Contains("spine") ? "chest" :
            n.Contains("pelvis") ? "pelvis" :
            n.Contains("neck") || n.Contains("head") || n.Contains("jaw") || n.Contains("eye") ? "head" : "other";
        return part is "chest" or "pelvis" or "head" or "other" ? part : side + part;
    }

    /// <summary>Singular values of the map taking triangle a onto triangle b (in their own planes); null for a degenerate one.</summary>
    public static (float S1, float S2)? Deform(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2)
    {
        static (float, float, float, float)? Flat(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 e1 = p1 - p0, e2 = p2 - p0, n = Vector3.Cross(e1, e2);
            if (e1.LengthSquared() < 1e-12f || n.LengthSquared() < 1e-12f) return null;
            var u = Vector3.Normalize(e1); var v = Vector3.Normalize(Vector3.Cross(n, u));
            return (Vector3.Dot(e1, u), Vector3.Dot(e2, u), Vector3.Dot(e1, v), Vector3.Dot(e2, v));
        }
        if (Flat(a0, a1, a2) is not var (a, b, c, d) || Flat(b0, b1, b2) is not var (p, q, r, s)) return null;
        float det = a * d - b * c;
        if (MathF.Abs(det) < 1e-12f) return null;
        // M = F · E⁻¹ (2 × 2)
        float i00 = d / det, i01 = -b / det, i10 = -c / det, i11 = a / det;
        float m00 = p * i00 + q * i10, m01 = p * i01 + q * i11, m10 = r * i00 + s * i10, m11 = r * i01 + s * i11;
        float e = (m00 + m11) / 2, f = (m00 - m11) / 2, g = (m10 + m01) / 2, h = (m10 - m01) / 2;
        float qq = MathF.Sqrt(e * e + h * h), rr = MathF.Sqrt(f * f + g * g);
        return (qq + rr, MathF.Abs(qq - rr));
    }

    /// <summary>Each triangle's region label (see the summary), from per-vertex influences.</summary>
    public static string[] Labels(int[] tris, Func<int, IEnumerable<(int Bone, float Weight)>> weights, Func<int, string> boneName)
    {
        var labels = new string[tris.Length / 3];
        var acc = new Dictionary<string, float>();
        for (int t = 0; t < labels.Length; t++)
        {
            acc.Clear();
            for (int k = 0; k < 3; k++)
                foreach (var (bone, w) in weights(tris[3 * t + k]))
                {
                    string rg = Region(boneName(bone));
                    acc[rg] = acc.GetValueOrDefault(rg) + w;
                }
            var top = acc.OrderByDescending(x => x.Value).Take(2).ToList();
            float tot = acc.Values.Sum();
            labels[t] = top.Count > 1 && top[1].Value >= 0.25f * tot && top[0].Key != top[1].Key
                ? string.Join(" ~ ", new[] { top[0].Key, top[1].Key }.Order()) : top.Count > 0 ? top[0].Key : "none";
        }
        return labels;
    }

    /// <summary>Per region: count, area-weighted mean log2 area ratio (+ bulges, − pinches) and its mean size, and the 95th
    /// percentile of stretch (max(s1, 1 / s2)) and of anisotropy (s1 / s2).</summary>
    public static List<Stat> Measure(int[] tris, string[] labels, IReadOnlyList<Vector3> from, IReadOnlyList<Vector3> to)
    {
        var groups = new Dictionary<string, List<(float Area, float Log, float Stretch, float Aniso)>>();
        for (int t = 0; t < labels.Length; t++)
        {
            int i0 = tris[3 * t], i1 = tris[3 * t + 1], i2 = tris[3 * t + 2];
            if (Deform(from[i0], from[i1], from[i2], to[i0], to[i1], to[i2]) is not var (s1, s2) || s2 < 1e-6f) continue;
            float area = Vector3.Cross(from[i1] - from[i0], from[i2] - from[i0]).Length() / 2;
            float lg = MathF.Log2(s1 * s2);
            if (!groups.TryGetValue(labels[t], out var list)) groups[labels[t]] = list = [];
            list.Add((area, lg, MathF.Max(s1, 1 / s2), s1 / s2));
        }
        var result = new List<Stat>();
        foreach (var (rg, list) in groups)
        {
            float wsum = Math.Max(1e-12f, list.Sum(x => x.Area));
            static float P95(IEnumerable<float> xs) { var a = xs.Order().ToArray(); return a.Length == 0 ? 0 : a[Math.Min(a.Length - 1, (int)(a.Length * 0.95f))]; }
            result.Add(new Stat(rg, list.Count, list.Sum(x => x.Area * x.Log) / wsum, list.Sum(x => x.Area * MathF.Abs(x.Log)) / wsum,
                P95(list.Select(x => x.Stretch)), P95(list.Select(x => x.Aniso))));
        }
        return result;
    }

    /// <summary>--fit-distort &lt;mff model&gt; &lt;package&gt; [animation …] (MHO_LEAN=&lt;degrees&gt;: the Lean slider).</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 2) { Console.WriteLine("--fit-distort <mff model> <package> [animation …]   (MHO_LEAN=<degrees>)"); return 1; }
        float lean = Environment.GetEnvironmentVariable("MHO_LEAN") == "auto" ? float.NaN : float.TryParse(Environment.GetEnvironmentVariable("MHO_LEAN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lv) ? lv : 0;   // (MHO_LEAN=auto: the automatic one)
        Settings.Reset();
        string pkg = BasePackage.Resolve(args[1], true);
        var model = MffModel.Load(Source.ResolveModelFile(args[0]));
        var sk = MhoSkeleton.Load(pkg, null);
        Console.WriteLine($"{args[0]} on {Path.GetFileName(pkg)}{(lean != 0 ? $", Lean {lean:+0.#;-0.#}°" : "")}");

        // rest: the fitted mesh against the MFF model (the fit's own reshaping)
        var picked = model.Selected(null);
        var mapFile = Environment.GetEnvironmentVariable("MHO_FD_MAP");   // (a bone map file, read only)
        var r = Retarget.Run(model, picked, sk, mapFile != null ? BoneMapFile.Load(mapFile) : null, RetargetOptions.FromEnvironment() with { Lean = lean });
        if (Environment.GetEnvironmentVariable("MHO_FD_NOTES") == "1")
        {
            foreach (var nt in r.Notes) Console.WriteLine("  note: " + nt);
            // the head: MFF bones under Bip001 Head (their skin and where it went), and the MHO head bones' weight
            int mh = model.Bones.FindIndex(b => b.Name.Equals("Bip001 Head", StringComparison.OrdinalIgnoreCase));
            bool Under(int i, int a) { for (int j = i, g = 0; j >= 0 && g < 512; j = model.Bones[j].Parent, g++) if (j == a) return true; return false; }
            var mffW = new float[model.Bones.Count];
            foreach (var sec in picked.SelectMany(p => p.Sections)) foreach (var inf in sec.Weights) foreach (var (b, w) in inf) mffW[b] += w;
            for (int i = 0; mh >= 0 && i < model.Bones.Count; i++)
                if (i != mh && (Under(i, mh) || model.Bones[i].Name.StartsWith("Bone5")) && mffW[i] > 0.5f)
                {
                    Vector3 c = Vector3.Zero; float cw = 0;
                    foreach (var sec in picked.SelectMany(p => p.Sections)) for (int v = 0; v < sec.Pos.Length; v++) foreach (var (b, w) in sec.Weights[v]) if (b == i) { c += sec.Pos[v] * w; cw += w; }
                    c /= Math.Max(cw, 1e-6f);
                    var hp = model.Bones[mh].Position;
                    Console.WriteLine($"  MFF {model.Bones[i].Name} (under {model.Bones[model.Bones[i].Parent].Name}): skin {mffW[i]:0} → {(r.Map.TryGetValue(model.Bones[i].Name, out var to) ? to : "(not mapped)")}; skin centre from the head joint {(c - hp) * r.Scale:0.0} (MHO units; +X front, +Z up)");
                }
            var mhoW = new Dictionary<string, float>();
            foreach (var sec in r.Sections) foreach (var inf in sec.Weights) foreach (var (b, w) in inf) if (Region(r.Bones[b].Name) == "head") mhoW[r.Bones[b].Name] = mhoW.GetValueOrDefault(r.Bones[b].Name) + w;
            Console.WriteLine("  MHO head bones' skin: " + string.Join(", ", mhoW.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:0}")) + "; target has " + string.Join(" ", sk.Bones.Select(b => b.Name).Where(n => n.Contains("jaw", StringComparison.OrdinalIgnoreCase) || n.Contains("mouth", StringComparison.OrdinalIgnoreCase) || n.Contains("lip", StringComparison.OrdinalIgnoreCase))));
            var placed = MhoAnim.TranslationBones(sk.Package, sk.Bones.Select(b => b.Name));
            foreach (string bn in new[] { "g_pelvis", "g_l_hip_offset", "g_l_hip", "g_l_thightwist", "g_l_knee", "g_spine01_offset", "g_spine01" })
            {
                int si = sk.Find(bn); var fb = r.Bones.FirstOrDefault(b => b.Name.Equals(bn, StringComparison.OrdinalIgnoreCase));
                if (si < 0 || fb == null) continue;
                Console.WriteLine($"  {bn,-18} parent {(fb.Parent >= 0 ? r.Bones[fb.Parent].Name : "-"),-18} stock {sk.Pos(si),-28:0.0} fitted {fb.Position,-28:0.0} {(placed.Contains(bn) ? "placed by animations" : "")}");
            }
        }
        var src = picked.SelectMany(p => p.Sections).ToList();
        if (src.Count != r.Sections.Count) Console.WriteLine($"(sections: {src.Count} in, {r.Sections.Count} out: compared in order)");
        var restStats = new List<(Stat S, int Sec)>();
        var allFrom = new List<Vector3>(); var allTo = new List<Vector3>(); var allTris = new List<int>(); var allLabels = new List<string>();
        for (int i = 0; i < Math.Min(src.Count, r.Sections.Count); i++)
        {
            var a = src[i]; var b = r.Sections[i];
            if (a.Pos.Length != b.Pos.Length) continue;
            int baseV = allFrom.Count;
            allFrom.AddRange(a.Pos.Select(p => new Vector3(p.X, -p.Y, p.Z) * r.Scale));
            allTo.AddRange(b.Pos);
            allTris.AddRange(b.Tris.Select(x => x + baseV));
            allLabels.AddRange(Labels(b.Tris, v => b.Weights[v], k => r.Bones[k].Name));
        }
        Print("REST (fitted vs the MFF model; rigid = 1.00 / 0 %)", Measure([.. allTris], [.. allLabels], allFrom, allTo), null);

        // animated: fitted and stock, each against its own bind pose
        var prep = Gui.PreviewPanel.Prepare(model, null, pkg, null, lean: lean);
        var fitted = prep.Mesh with { Bones = prep.Bones };
        fitted = Experiment(prep, fitted);
        // MHO_FD_HIPPROFILE=1: the share of the left leg's bones (hip, thigh twist, knee …) in the skin around the left hip, by
        // height from the knee to the waist, front and back half, for the fitted and the game's model
        if (Environment.GetEnvironmentVariable("MHO_FD_HIPPROFILE") == "1")
            foreach (var (label, l) in new[] { ("fitted", fitted), ("game", prep.Stock) })
            {
                if (l == null) continue;
                int B(string n) { for (int k = 0; k < l.Bones.Count; k++) if (l.Bones[k].Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return k; return -1; }
                var rest0 = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents); rest0.Pose(null, 0);
                var hip = rest0.BonePosition(B("g_l_hip")); var knee = rest0.BonePosition(B("g_l_knee")); var pel = rest0.BonePosition(B("g_pelvis"));
                var leg = new HashSet<int>(Enumerable.Range(0, l.Bones.Count).Where(k => l.Bones[k].Name.StartsWith("g_l_", StringComparison.OrdinalIgnoreCase) && Region(l.Bones[k].Name) is "L thigh" or "L calf" or "L foot"));
                float top = hip.Z + (hip.Z - knee.Z) * 0.35f, len = top - knee.Z;
                var bw = new Dictionary<string, float>();
                for (int v = 0; v < l.Positions.Length; v++)
                {
                    var p = l.Positions[v]; float tt = (p.Z - knee.Z) / Math.Max(1e-3f, hip.Z - knee.Z);
                    if (tt < 0.3f || tt > 1.5f || MathF.Sign(p.Y) != MathF.Sign(hip.Y)) continue;
                    var inf = l.Influences[v];
                    for (int k = 0; k < inf.Bones.Count; k++) { string bn = l.Bones[inf.Bones[k]].Name; bw[bn] = bw.GetValueOrDefault(bn) + inf.Weights[k]; }
                }
                Console.WriteLine($"\n{label}: bones around the left hip: " + string.Join(", ", bw.OrderByDescending(x => x.Value).Take(10).Select(x => $"{x.Key} {x.Value:0} ({Region(x.Key)})")));
                Console.WriteLine($"\n{label}: left leg's share by height (0 = knee, 1 = {0.35:0.##} thigh above the hip joint; hip joint at {(hip.Z - knee.Z) / len:0.00}), pelvis at {(pel.Z - knee.Z) / len:0.00}");
                foreach (bool back in new[] { false, true })
                {
                    var line = new List<string>();
                    for (int bin = 0; bin < 10; bin++)
                    {
                        float lo = knee.Z + len * bin / 10, hi = knee.Z + len * (bin + 1) / 10; float sum = 0; int cnt = 0;
                        for (int v = 0; v < l.Positions.Length; v++)
                        {
                            var p = l.Positions[v];
                            if (p.Z < lo || p.Z >= hi || MathF.Sign(p.Y) != MathF.Sign(hip.Y) || MathF.Abs(p.Y) < MathF.Abs(hip.Y) * 0.15f) continue;
                            if ((p.X < hip.X) != back) continue;
                            var inf = l.Influences[v]; float w = 0;
                            for (int k = 0; k < inf.Bones.Count; k++) if (leg.Contains(inf.Bones[k])) w += inf.Weights[k];
                            sum += w; cnt++;
                        }
                        line.Add(cnt == 0 ? "  -  " : $"{sum / cnt,5:0.00}");
                    }
                    Console.WriteLine($"  {(back ? "back " : "front")}: {string.Join(" ", line)}");
                }
            }
        var want = args.Count > 2 ? args.Skip(2).ToList() : ["idle", "idle_combat", "run"];
        foreach (string an in want)
        {
            var ar = prep.Animations.FirstOrDefault(x => x.Name.Equals(an, StringComparison.OrdinalIgnoreCase));
            if (ar == null || ModAnimations.Load(ar) is not { } ba) { Console.WriteLine($"{an}: not found"); continue; }
            var fs = Worst(fitted, ba, prep.Mesh.Influences); var ss = prep.Stock != null ? Worst(prep.Stock, ba) : null;
            Print($"ANIMATED {an} (each mesh against its own bind pose; worst of 8 frames)", fs, ss);
        }
        return 0;
    }

    /// <summary>The weight experiments (MHO_FD_WEIGHTS): "stock" = each vertex takes the nearest game-model vertex's weights;
    /// "smooth:N" = N Laplacian steps (factor 0.5) over the welded mesh, all bones at once.</summary>
    public static ModMeshes.Loaded Experiment(Gui.PreviewPanel.Prepared prep, ModMeshes.Loaded fitted)
    {
        if (Environment.GetEnvironmentVariable("MHO_FD_WEIGHTS") is not { Length: > 0 } exp) return fitted;
        Console.WriteLine($"(weights: {exp})");
        return fitted with { Influences = exp == "stock" && prep.Stock != null ? FromStock(fitted, prep.Stock) : exp.StartsWith("smooth:") ? Smooth(fitted, int.Parse(exp[7..])) : fitted.Influences };
    }

    static AnimExportCli.Meshes.VertexInfluence Inf(Dictionary<int, float> acc)
    {
        var top = acc.Where(x => x.Value > 1e-4f).OrderByDescending(x => x.Value).Take(4).ToList();
        float tot = Math.Max(1e-9f, top.Sum(x => x.Value));
        return new() { Bones = top.Select(x => x.Key).ToArray(), Weights = top.Select(x => x.Value / tot).ToArray() };
    }

    /// <summary>Experiment: each vertex takes the weights of the game model's nearest vertex (bones matched by name).</summary>
    static AnimExportCli.Meshes.VertexInfluence[] FromStock(ModMeshes.Loaded fit, ModMeshes.Loaded stock)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < fit.Bones.Count; k++) byName.TryAdd(fit.Bones[k].Name, k);
        var result = new AnimExportCli.Meshes.VertexInfluence[fit.Positions.Length];
        Parallel.For(0, result.Length, v =>
        {
            int best = 0; float bd = float.MaxValue;
            for (int s = 0; s < stock.Positions.Length; s++) { float d = Vector3.DistanceSquared(fit.Positions[v], stock.Positions[s]); if (d < bd) { bd = d; best = s; } }
            var acc = new Dictionary<int, float>();
            var si = stock.Influences[best];
            for (int k = 0; k < si.Bones.Count; k++)
                if (si.Bones[k] >= 0 && si.Bones[k] < stock.Bones.Count && byName.TryGetValue(stock.Bones[si.Bones[k]].Name, out int fb)) acc[fb] = acc.GetValueOrDefault(fb) + si.Weights[k];
            result[v] = acc.Count > 0 ? Inf(acc) : fit.Influences[v];
        });
        return result;
    }

    /// <summary>Experiment: N smoothing steps over the welded mesh (each vertex's weights halfway to its neighbours' average).</summary>
    static AnimExportCli.Meshes.VertexInfluence[] Smooth(ModMeshes.Loaded fit, int steps)
    {
        int n = fit.Positions.Length;
        var weld = new int[n]; var key = new Dictionary<(int, int, int), int>();
        for (int v = 0; v < n; v++) { var p = fit.Positions[v] * 1000; var k = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z)); if (!key.TryGetValue(k, out int g)) key[k] = g = key.Count; weld[v] = g; }
        int ng = key.Count;
        var nb = Enumerable.Range(0, ng).Select(_ => new HashSet<int>()).ToArray();
        for (int t = 0; t + 2 < fit.Indices.Length; t += 3)
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) if (a != b) nb[weld[fit.Indices[t + a]]].Add(weld[fit.Indices[t + b]]);
        var w = new Dictionary<int, float>[ng];
        for (int g = 0; g < ng; g++) w[g] = [];
        var cnt = new int[ng];
        for (int v = 0; v < n; v++)
        {
            var inf = fit.Influences[v]; cnt[weld[v]]++;
            for (int k = 0; k < inf.Bones.Count; k++) w[weld[v]][inf.Bones[k]] = w[weld[v]].GetValueOrDefault(inf.Bones[k]) + inf.Weights[k];
        }
        for (int g = 0; g < ng; g++) foreach (var k in w[g].Keys.ToList()) w[g][k] /= Math.Max(1, cnt[g]);
        for (int s = 0; s < steps; s++)
        {
            var next = new Dictionary<int, float>[ng];
            for (int g = 0; g < ng; g++)
            {
                var acc = new Dictionary<int, float>();
                foreach (var (k, x) in w[g]) acc[k] = 0.5f * x;
                if (nb[g].Count > 0)
                    foreach (int o in nb[g]) foreach (var (k, x) in w[o]) acc[k] = acc.GetValueOrDefault(k) + 0.5f * x / nb[g].Count;
                else foreach (var (k, x) in w[g]) acc[k] += 0.5f * x;
                next[g] = acc;
            }
            w = next;
        }
        return Enumerable.Range(0, n).Select(v => Inf(w[weld[v]])).ToArray();
    }

    /// <summary>A mesh posed at 8 frames of an animation: per region, the frame with the largest stretch.</summary>
    static List<Stat> Worst(ModMeshes.Loaded l, BoneAnimation ba, IReadOnlyList<AnimExportCli.Meshes.VertexInfluence>? labelBy = null)
    {
        var anim = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
        var lb = labelBy ?? l.Influences;
        var labels = Labels(l.Indices, v => lb[v].Bones.Zip(lb[v].Weights), k => k >= 0 && k < l.Bones.Count ? l.Bones[k].Name : "");
        float frames = MeshAnimator.Span(ba).Frames;
        var worst = new Dictionary<string, Stat>();
        for (int k = 0; k < 8; k++)
        {
            anim.Pose(ba, frames * k / 8f);
            foreach (var s in Measure(l.Indices, labels, l.Positions, anim.Positions))
                if (!worst.TryGetValue(s.Region, out var w) || s.Stretch95 > w.Stretch95) worst[s.Region] = s;
        }
        return [.. worst.Values];
    }

    static void Print(string title, List<Stat> stats, List<Stat>? stock)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine($"  {"region",-26} {"tris",6} {"area ±",8} {"|area|",7} {"stretch95",9} {"aniso95",8}{(stock != null ? $"   | game: {"stretch95",9} {"aniso95",8}" : "")}");
        foreach (var s in stats.Where(x => x.Count >= 20).OrderByDescending(x => x.Stretch95))
        {
            var g = stock?.FirstOrDefault(x => x.Region == s.Region);
            Console.WriteLine($"  {s.Region,-26} {s.Count,6} {Pct(s.AreaLog),8} {Pct(s.AreaAbs),7} {s.Stretch95,9:0.00} {s.Aniso95,8:0.00}" +
                (stock != null ? (g != null ? $"   |       {g.Stretch95,9:0.00} {g.Aniso95,8:0.00}" : "   |       -") : ""));
        }
        static string Pct(float log2) => $"{(MathF.Pow(2, log2) - 1) * 100:+0;-0}%";
    }
}
