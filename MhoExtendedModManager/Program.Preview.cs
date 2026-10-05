using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>Read-only commands about the 3D preview: meshes, materials, animations, props and power effects. Null when <paramref name="cmd"/> isn't one of them.</summary>
    static int? PreviewCommand(string cmd, List<string> rest, Settings settings, string data, ModLibrary lib)
    {
        switch (cmd)
        {
            case "--icon-formats":
            {
                // Read-only: how many textures of each format the game's icon packages hold (originals), with examples.
                string? fgr = settings.ResolvedGameRoot(data);
                if (fgr == null || !Settings.IsGameRoot(fgr)) { Console.WriteLine("game folder not set"); return 1; }
                var fgame = new GameState(fgr, data);
                var fcat = new StockCatalog(lib, fgame);
                if (rest.Count > 2)
                {
                    // --icon-formats <package> <name part>: each matching texture's size and format
                    var hits = fcat.EntriesFor(rest[1]) ?? [];
                    foreach (var e in hits.Where(e => e.Name.Contains(rest[2], StringComparison.OrdinalIgnoreCase)).Take(40)) Console.WriteLine($"  {e.Name}: {fcat.Size(rest[1], e.Name)}");
                    return 0;
                }
                foreach (string pk in Applier.IconPackages.Select(p => p.File).Concat(IconCapture.ExtraPackages(fgame)))
                {
                    var list = fcat.EntriesFor(pk);
                    if (list == null) { Console.WriteLine($"{pk}: no original"); continue; }
                    var by = list.Select(e => (e.Name, S: fcat.Size(pk, e.Name))).GroupBy(x => x.S?.Format ?? "?").OrderByDescending(g => g.Count());
                    Console.WriteLine($"{pk}: " + string.Join(", ", by.Select(g => $"{g.Key} {g.Count()}" + (g.Key.Contains("DXT") ? "" : $" (e.g. {string.Join(", ", g.Take(3).Select(x => x.Name))})"))));
                }
                return 0;
            }
            case "--convert-image":
            {
                // Test: an image converted like a replacement chosen in the editor (size and DXT format of the original).
                // --convert-image <icons|store> <texture> <image> <out.dds>
                if (rest.Count < 5) { Console.WriteLine("--convert-image <icons|store> <texture> <image> <out.dds>"); return 1; }
                string? cgr2 = settings.ResolvedGameRoot(data);
                if (cgr2 == null || !Settings.IsGameRoot(cgr2)) { Console.WriteLine("game folder not set"); return 1; }
                var ccat = new StockCatalog(lib, new GameState(cgr2, data));
                string cpk = rest[1].Equals("store", StringComparison.OrdinalIgnoreCase) ? Applier.IconPackages[2].File : Applier.IconPackages[0].File;
                Console.WriteLine($"original: {ccat.Size(cpk, rest[2])}");
                Console.WriteLine(ccat.ImageToDds(cpk, rest[2], rest[3], rest[4], keepSize: rest.Contains("--keep-size")));
                return 0;
            }
            case "--mesh-tail":
            {
                // Read-only (cross-hero move, phase 1): the bytes after a skeletal mesh's LODs, read as UE3's NameIndexMap
                // (count, then name + int per bone) where they fit, the rest shown raw.
                foreach (string f in rest.Skip(1))
                {
                    AnimExportCli.Packages.Package ap;
                    try { ap = AnimExportCli.Packages.Package.Open(f); } catch (Exception ex) { Console.WriteLine($"{f}: {ex.Message}"); continue; }
                    foreach (int i in ap.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName))
                    {
                        string why = "";
                        var sm = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i, e => why = e);
                        byte[] d = ap.GetExportData(i).ToArray();
                        if (sm == null) { Console.WriteLine($"{Path.GetFileName(f)} | {ap.GetExportName(i)} | unread: {why}"); continue; }
                        int p = sm.LodsEnd, tail = d.Length - p;
                        string map = "";
                        if (tail >= 4)
                        {
                            int n = BitConverter.ToInt32(d, p);
                            if (n == sm.Bones.Count && p + 4 + n * 12 <= d.Length) { map = $"namemap {n}"; p += 4 + n * 12; }
                            else map = $"first int {n} (bones {sm.Bones.Count})";
                        }
                        int left = d.Length - p;
                        string hex = string.Join(" ", d.Skip(p).Take(96).Select(x => x.ToString("x2")));
                        Console.WriteLine($"{Path.GetFileName(f)} | {sm.Name} | size {d.Length} lods {sm.Lods.Count} tail {tail} | {map} | left {left} | {hex}");
                    }
                }
                return 0;
            }
            case "--mod-props":
            {
                // Read-only: for each of a mod's meshes, the props the 3D preview shows with it (PropRig.Attached) and their bones.
                var pm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (pm == null) { Console.WriteLine("--mod-props <mod>"); return 1; }
                var all = ModMeshes.List(pm);
                foreach (var r in all)
                {
                    string? pgr2 = settings.ResolvedGameRoot(data);
                    var props = PropRig.Attached(r, all, pgr2 != null && Settings.IsGameRoot(pgr2) ? Settings.Cooked(pgr2) : null);
                    Console.WriteLine($"{r.Name} ({r.Package}): {(props.Count == 0 ? "no props" : string.Join(", ", props.Select(p => $"{p.Ref.Name} on {p.Bone ?? "the right hand"}{(p.OnDemand ? " (on demand)" : "")} [{string.Join(" ", p.Slots)}]")))}");
                    if (props.Count == 0 || Environment.GetEnvironmentVariable("MHO_PROPS_CHECK") != "1") continue;
                    // Check: load, combine and pose as the preview does; report sizes and anything not finite.
                    string? pc = settings.ResolvedGameRoot(data) is string pgr && Settings.IsGameRoot(pgr) ? Settings.Cooked(pgr) : null;
                    var main = ModMeshes.Load(r, pc, out string w1);
                    if (main == null) { Console.WriteLine("   main: " + w1); continue; }
                    var an = new MeshAnimator(main.Bones, main.Positions, main.Normals, main.Influences, main.Tangents);
                    var rg = new PropRig();
                    foreach (var (pr, bone) in props)
                    {
                        var pmsh = ModMeshes.Load(pr, pc, out string w2);
                        if (pmsh == null) { Console.WriteLine($"   {pr.Name}: {w2}"); continue; }
                        int bi = PropRig.BoneFor(an, bone);
                        an.Pose(null, 0);
                        var mat = an.BoneMatrix(bi);
                        var lo = pmsh.Positions.Aggregate(Vector3.Min); var hi = pmsh.Positions.Aggregate(Vector3.Max);
                        int badP = pmsh.Positions.Count(v => !float.IsFinite(v.X + v.Y + v.Z)), badN = pmsh.Normals.Count(v => !float.IsFinite(v.X + v.Y + v.Z) || v.LengthSquared() < 1e-12f);
                        Console.WriteLine($"   {pr.Name}: {pmsh.Positions.Length} verts, {pmsh.Indices.Length / 3} tris, sections {pmsh.Looks.Length}, box {lo} .. {hi}; bone #{bi} at {mat.Translation}; bad positions {badP}, zero/bad normals {badN}; bones {pmsh.Bones.Count}");
                        rg.Add(pmsh, bi);
                    }
                    var comb = rg.Combine(main);
                    Console.WriteLine($"   character {main.Positions.Length} verts / {main.Looks.Length} sections; combined {comb.Positions.Length} verts, {comb.Looks.Length} sections, max index {comb.Indices.Max()}, max section {comb.TriangleSection.Max()}");

                }
                return 0;
            }
            case "--fx-dump":
            {
                // Read-only: every particle system's emitters (the MHO Hero Creator's --fx-dump, same output, so the two
                // readers can be compared line for line). --fx-dump <package.upk> [system name part]
                if (rest.Count < 2) { Console.WriteLine("--fx-dump <package.upk> [system name part]"); return 1; }
                var pk = Fx.FxPkg.Open(rest[1]);
                var rnd = new Random(1);
                for (int i = 0; i < pk.T.Exports.Count; i++)
                {
                    if (!pk.T.ClassOf(pk.T.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rest.Count > 2 && !pk.T.PathOf(i + 1).Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    var ps = Fx.ParticleData.Read(pk, i)!;
                    Console.WriteLine($"{ps.Name}: {ps.Emitters.Count} emitter(s)");
                    foreach (var em in ps.Emitters)
                    {
                        var rq = em.Required;
                        int mat = rq.Ref("Material");
                        Console.WriteLine($"  {em.Name} [{em.Kind}] material {(mat == 0 ? "none" : pk.T.PathOf(mat))} · {rq.Enum("ScreenAlignment", "psa_square")} · sub-images {rq.Int("SubImages_Horizontal", 1)}×{rq.Int("SubImages_Vertical", 1)}"
                            + $" · duration {rq.Float("EmitterDuration", 1)}s loops {rq.Int("EmitterLoops", 0)} delay {rq.Float("EmitterDelay", 0)} · local {rq.Bool("bUseLocalSpace", false)}");
                        if (em.Spawn is { } sp)
                        {
                            var rate = sp.Dist("Rate", 1); var bursts = sp.Prop("BurstList");
                            Console.WriteLine($"      spawn: rate {(rate?.IsSet == true ? rate.F(0, rnd).ToString("0.##") + " (" + rate.Describe() + ")" : "none")}{(bursts != null ? $", bursts {bursts.Size} bytes" : "")}");
                        }
                        foreach (var m in em.Modules)
                        {
                            var ds = m.Props.Where(x => x.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) && x.Value.Contains("rawdistribution", StringComparison.OrdinalIgnoreCase)).ToList();
                            var parts = ds.Select(x =>
                            {
                                int dim = x.Value.Contains("vector", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                                var d = m.Dist(x.Name, dim)!;
                                string V(float t) { var v = d.Eval(t, rnd); return dim == 1 ? v[0].ToString("0.##") : $"({v[0]:0.##},{v[1]:0.##},{v[2]:0.##})"; }
                                return $"{x.Name} {V(0)} → {V(0.5f)} → {V(1)} [{d.Describe()}]";
                            });
                            Console.WriteLine($"      {m.Class}: {string.Join("; ", parts)}");
                        }
                    }
                }
                return 0;
            }
            case "--fx-sim":
            {
                // Read-only: plays a particle system off screen and prints the live particles per emitter every 8 frames (the
                // MHO Hero Creator's --fx-sim, same output). --fx-sim <package.upk> <system name part> [seconds]
                if (rest.Count < 3) { Console.WriteLine("--fx-sim <package.upk> <system name part> [seconds]"); return 1; }
                var pk = Fx.FxPkg.Open(rest[1]);
                int ex = Enumerable.Range(0, pk.T.Exports.Count).FirstOrDefault(i => pk.T.ClassOf(pk.T.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase) && pk.T.PathOf(i + 1).Contains(rest[2], StringComparison.OrdinalIgnoreCase), -1);
                if (ex < 0) { Console.WriteLine("No particle system " + rest[2]); return 1; }
                var sim = new Fx.ParticleSim(Fx.ParticleData.Read(pk, ex)!);
                float secs = rest.Count > 3 ? float.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture) : 3;
                Console.WriteLine(sim.Data.Name);
                for (float t = 0; t < secs; t += 1f / 30)
                {
                    sim.Step(1f / 30);
                    if ((int)(t * 30) % 8 != 0) continue;
                    var sprites = sim.Sprites().ToList();
                    string sample = sprites.Count == 0 ? "" : $"  e.g. {sprites[^1].Emitter.Name}: at ({sprites[^1].Sprite.Position.X:0},{sprites[^1].Sprite.Position.Y:0},{sprites[^1].Sprite.Position.Z:0}) size {sprites[^1].Sprite.Size.X:0}×{sprites[^1].Sprite.Size.Y:0} colour ({sprites[^1].Sprite.Color.X:0.#},{sprites[^1].Sprite.Color.Y:0.#},{sprites[^1].Sprite.Color.Z:0.#}) alpha {sprites[^1].Sprite.Color.W:0.##} image {sprites[^1].Sprite.Image}";
                    Console.WriteLine($"  t={sim.Age:0.00}s  {string.Join(" ", sim.Counts().Select(c => c.Count))}  (total {sprites.Count}){sample}");
                }
                return 0;
            }
            case "--power-anims":
            {
                // Read-only: a hero's animations and the powers that play them (the power-effects index).
                // --power-anims <hero, e.g. Thor or DoctorStrange> [animation name part]
                if (rest.Count < 2) { Console.WriteLine("--power-anims <hero> [animation name part]"); return 1; }
                string? pgr = settings.ResolvedGameRoot(data);
                string? pcook = pgr != null && Settings.IsGameRoot(pgr) ? Settings.Cooked(pgr) : null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var idx = Fx.PowerIndex.For(rest[1], pcook, []);
                Console.WriteLine($"{idx.Count} animation(s) in {rest[1]}'s power packages ({sw.ElapsedMilliseconds} ms)");
                foreach (var (anim, list) in idx.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    if (rest.Count < 3 || anim.Contains(rest[2], StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"  {anim}: {string.Join(", ", list.Select(x => $"{x.Class} ({Path.GetFileName(x.File)})"))}");
                return 0;
            }
            case "--remove-property":
            {
                // Test: a copy of a package with one top-level property removed from one export (component: properties at 16,
                // else at 4), written outside the game folder. --remove-property <in.upk> <export path end> <property> <out.upk>
                if (rest.Count < 5) { Console.WriteLine("--remove-property <in.upk> <export path end> <property> <out.upk>"); return 1; }
                string rgr2 = settings.ResolvedGameRoot(data) ?? "";
                string rout2 = Path.GetFullPath(rest[4]);
                if (rgr2.Length > 0 && rout2.StartsWith(Path.GetFullPath(rgr2), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("not into the game folder"); return 1; }
                var rp = MhoPackageModifier.Package.Open(rest[1]);
                int ri = Array.FindIndex(rp.Exports, e => rp.PathOf(e).EndsWith(rest[2], StringComparison.OrdinalIgnoreCase));
                if (ri < 0) { Console.WriteLine($"{rest[2]}: no such export"); return 1; }
                byte[] rd = rp.ReadExportBytes(rp.Exports[ri]);
                var tags = MhoPackageModifier.TagWalker.Walk(rp, rd, 16) ?? MhoPackageModifier.TagWalker.Walk(rp, rd, 4);
                var tag = tags?.FirstOrDefault(t => t.Name.Equals(rest[3], StringComparison.OrdinalIgnoreCase));
                if (tag == null) { Console.WriteLine($"{rest[3]}: not a property of {rp.PathOf(rp.Exports[ri])}"); return 1; }
                byte[] nd = [.. rd.AsSpan(0, tag.Start), .. rd.AsSpan(tag.End)];
                byte[] built = MhoPackageModifier.PackageRebuilder.Rebuild(rp, new Dictionary<int, Func<long, byte[]>> { [ri] = _ => nd }, [], out _);
                var back = MhoPackageModifier.Package.FromBytes(built);
                var btags = MhoPackageModifier.TagWalker.Walk(back, back.ReadExportBytes(back.Exports[ri]), tag.Start == 16 || (tags![0].Start == 16) ? 16 : 4);
                if (btags == null || btags.Any(t => t.Name.Equals(rest[3], StringComparison.OrdinalIgnoreCase))) { Console.WriteLine("didn't read back right; nothing written"); return 1; }
                File.WriteAllBytes(rout2, built);
                Console.WriteLine($"{rp.PathOf(rp.Exports[ri])}: {rest[3]} removed ({rd.Length} → {nd.Length} bytes); written {rout2}, reads back ({btags.Count} properties left)");
                return 0;
            }
            case "--mesh-namemap":
            {
                // Read-only: a skeletal mesh's bone list against its NameIndexMap (after the LODs: count, then name + index per
                // bone), the table the game uses to find a bone (and so a socket) by name.
                if (rest.Count < 3) { Console.WriteLine("--mesh-namemap <package.upk> <mesh name>"); return 1; }
                var mp = MhoPackageModifier.Package.Open(rest[1]);
                var ap = AnimExportCli.Packages.Package.Read(mp.RawFile);
                int mi = ap.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).FirstOrDefault(i => ap.GetExportName(i).Equals(rest[2], StringComparison.OrdinalIgnoreCase), -1);
                if (mi < 0) { Console.WriteLine($"{rest[2]}: no skeletal mesh by that name"); return 1; }
                var mesh = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, mi, e => Console.WriteLine("  " + e));
                if (mesh == null) return 1;
                byte[] md = ap.GetExportData(mi).ToArray();
                int at = mesh.LodsEnd, count = BitConverter.ToInt32(md, at);
                Console.WriteLine($"{mesh.Name}: {mesh.Bones.Count} bones, name map {count} entries");
                int wrong = 0;
                for (int k = 0; k < count; k++)
                {
                    int p = at + 4 + 12 * k, ni = BitConverter.ToInt32(md, p), nn = BitConverter.ToInt32(md, p + 4), ix = BitConverter.ToInt32(md, p + 8);
                    string name = ni >= 0 && ni < mp.Names.Length ? (nn > 0 ? $"{mp.Names[ni]}_{nn - 1}" : mp.Names[ni]) : $"#{ni}";
                    string atIx = ix >= 0 && ix < mesh.Bones.Count ? mesh.Bones[ix].Name : "(out of range)";
                    bool ok = atIx.Equals(name, StringComparison.OrdinalIgnoreCase);
                    if (!ok) wrong++;
                    if (!ok || rest.Contains("--all")) Console.WriteLine($"  [{k}] {name} → bone {ix} {atIx}{(ok ? "" : "   MISMATCH")}");
                }
                var mapped = Enumerable.Range(0, count).Select(k => BitConverter.ToInt32(md, at + 12 + 12 * k)).ToHashSet();
                var unmapped = Enumerable.Range(0, mesh.Bones.Count).Where(b => !mapped.Contains(b)).Select(b => mesh.Bones[b].Name).ToList();
                Console.WriteLine($"{wrong} mismatch(es); bones not in the map: {(unmapped.Count == 0 ? "none" : string.Join(", ", unmapped))}");
                return 0;
            }
            case "--set-ref":
            {
                // Test: a copy of a package with one object property of one export pointed at another export (by path end),
                // written outside the game folder. --set-ref <in.upk> <export path end> <property> <target path end> <out.upk>
                if (rest.Count < 6) { Console.WriteLine("--set-ref <in.upk> <export path end> <property> <target export path end> <out.upk>"); return 1; }
                string sgr = settings.ResolvedGameRoot(data) ?? "";
                string sout = Path.GetFullPath(rest[5]);
                if (sgr.Length > 0 && sout.StartsWith(Path.GetFullPath(sgr), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("not into the game folder"); return 1; }
                var sp = MhoPackageModifier.Package.Open(rest[1]);
                int si = Array.FindIndex(sp.Exports, e => sp.PathOf(e).EndsWith(rest[2], StringComparison.OrdinalIgnoreCase));
                int ti = Array.FindIndex(sp.Exports, e => sp.PathOf(e).EndsWith(rest[4], StringComparison.OrdinalIgnoreCase));
                if (si < 0 || ti < 0) { Console.WriteLine($"{(si < 0 ? rest[2] : rest[4])}: no such export"); return 1; }
                byte[] sd = sp.ReadExportBytes(sp.Exports[si]);
                var stags = MhoPackageModifier.TagWalker.Walk(sp, sd, 16) ?? MhoPackageModifier.TagWalker.Walk(sp, sd, 4);
                var stag = stags?.FirstOrDefault(t => t.Name.Equals(rest[3], StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
                if (stag == null) { Console.WriteLine($"{rest[3]}: not an object property of {sp.PathOf(sp.Exports[si])}"); return 1; }
                int was = BitConverter.ToInt32(sd, stag.ValueAt);
                BitConverter.GetBytes(ti + 1).CopyTo(sd, stag.ValueAt);
                byte[] sbuilt = MhoPackageModifier.PackageRebuilder.Rebuild(sp, new Dictionary<int, Func<long, byte[]>> { [si] = _ => sd }, [], out _);
                var sback = MhoPackageModifier.Package.FromBytes(sbuilt);
                if (BitConverter.ToInt32(sback.ReadExportBytes(sback.Exports[si]), stag.ValueAt) != ti + 1) { Console.WriteLine("didn't read back right; nothing written"); return 1; }
                File.WriteAllBytes(sout, sbuilt);
                Console.WriteLine($"{sp.PathOf(sp.Exports[si])}.{rest[3]}: {(was > 0 ? sp.PathOf(sp.Exports[was - 1]) : was.ToString())} → {sp.PathOf(sp.Exports[ti])}; written {sout}");
                return 0;
            }
            case "--mesh-sockets":
            {
                // Read-only: each skeletal mesh in a package with its sockets (name → bone): where powers attach effects.
                if (rest.Count < 2) { Console.WriteLine("--mesh-sockets <package.upk>"); return 1; }
                string sp = Path.GetFullPath(rest[1]);
                // --detail: each socket's local place and its rest-pose direction in model space (+X of the socket).
                bool detail = rest.Contains("--detail");
                foreach (var mr in ModMeshes.List([(Path.GetFileName(sp), sp)], anyPackage: true))
                {
                    var socks = Fx.FxSockets.Of(sp, mr.Name);
                    Console.WriteLine($"{mr.Name}: {socks.Count} socket(s)");
                    var model = new Dictionary<string, System.Numerics.Matrix4x4>(StringComparer.OrdinalIgnoreCase);
                    if (detail)
                    {
                        var mp = MhoPackageModifier.Package.Open(sp);
                        var ap = AnimExportCli.Packages.Package.Read(mp.RawFile);
                        int mi = Enumerable.Range(0, mp.Exports.Length).FirstOrDefault(i => mp.PathOf(mp.Exports[i]).Split('.')[^1].Equals(mr.Name, StringComparison.OrdinalIgnoreCase)
                            && AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i) != null, -1);
                        var sm = mi >= 0 ? AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, mi) : null;
                        var mats = new System.Numerics.Matrix4x4[sm?.Bones.Count ?? 0];
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var b = sm!.Bones[i];
                            var local = System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(b.Orientation)) * System.Numerics.Matrix4x4.CreateTranslation(b.Position);
                            mats[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? local * mats[b.ParentIndex] : local;
                            model[b.Name] = mats[i];
                        }
                    }
                    static string V(System.Numerics.Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";
                    foreach (var (n, s) in socks.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!detail) { Console.WriteLine($"  {n} on {s.Bone}"); continue; }
                        string where = "";
                        if (model.TryGetValue(s.Bone, out var bm))
                        {
                            var w = s.Local * bm;
                            var bx = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(bm.M11, bm.M12, bm.M13));
                            where = $" | bone X {V(bx)} | socket at {V(w.Translation)} X {V(System.Numerics.Vector3.Normalize(new(w.M11, w.M12, w.M13)))}";
                        }
                        Console.WriteLine($"  {n} on {s.Bone}: local {V(s.Local.Translation)} X {V(new(s.Local.M11, s.Local.M12, s.Local.M13))}{where}");
                    }
                }
                return 0;
            }
            case "--export-diff":
            {
                // Read-only: the exports whose data differ between two packages (same export order), with where they differ.
                if (rest.Count < 3) { Console.WriteLine("--export-diff <a.upk> <b.upk>"); return 1; }
                var pa = MhoPackageModifier.Package.Open(rest[1]); var pb = MhoPackageModifier.Package.Open(rest[2]);
                int n = Math.Min(pa.Exports.Length, pb.Exports.Length), diffs = 0;
                for (int i = 0; i < n; i++)
                {
                    byte[] a = pa.ReadExportBytes(pa.Exports[i]), b = pb.ReadExportBytes(pb.Exports[i]);
                    string pathA = pa.PathOf(pa.Exports[i]), pathB = pb.PathOf(pb.Exports[i]);
                    if (a.AsSpan().SequenceEqual(b) && pathA == pathB) continue;
                    diffs++;
                    var at = Enumerable.Range(0, Math.Min(a.Length, b.Length)).Where(k => a[k] != b[k]).ToList();
                    Console.WriteLine($"#{i + 1} {pa.ClassOf(pa.Exports[i])} {pathA}{(pathA != pathB ? " → " + pathB : "")}: {a.Length} / {b.Length} bytes, {at.Count} differ" +
                        (at.Count > 0 ? $" (0x{at[0]:X}…0x{at[^1]:X})" : ""));
                }
                if (pa.Exports.Length != pb.Exports.Length) Console.WriteLine($"export counts differ: {pa.Exports.Length} / {pb.Exports.Length}");
                Console.WriteLine($"{diffs} export(s) differ of {n}.");
                return 0;
            }
            case "--proto-search":
            {
                // Read-only: every prototype (whose path contains [path part]) with a field line containing <text>, e.g. which
                // game data names an AnimSet. --proto-search <text> [path part]
                if (rest.Count < 2) { Console.WriteLine("--proto-search <text> [path part]"); return 1; }
                string? sgr = settings.ResolvedGameRoot(data);
                if (sgr == null || !Settings.IsGameRoot(sgr)) { Console.WriteLine("game folder not found"); return 1; }
                var sdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(sgr, "Data", "Game", "Calligraphy.sip")));
                string needle = rest[1], part = rest.Count > 2 ? rest[2] : "";
                int hits = 0;
                foreach (var (id, entry) in sdb.Prototypes)
                {
                    string path = sdb.Name(id);
                    if (part.Length > 0 && !path.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                    List<string> lines;
                    try { lines = sdb.Dump(sdb.Prototype(id).Data, 200).ToList(); } catch (Exception) { continue; }
                    var m = lines.Where(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (m.Count == 0) continue;
                    hits++;
                    Console.WriteLine(path);
                    foreach (var l in m.Take(8)) Console.WriteLine("    " + l.Trim());
                }
                Console.WriteLine($"{hits} prototype(s)");
                return 0;
            }
            case "--proto":
            {
                // Read-only: a prototype's fields as the game data has them (its own, parents not merged).
                if (rest.Count < 2) { Console.WriteLine("--proto <prototype path, e.g. Powers\\Player\\Thor\\GroundSmash.prototype>"); return 1; }
                string? pgr2 = settings.ResolvedGameRoot(data);
                if (pgr2 == null || !Settings.IsGameRoot(pgr2)) { Console.WriteLine("game folder not found"); return 1; }
                var pdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(pgr2, "Data", "Game", "Calligraphy.sip")));
                if (pdb.Find(rest[1]) is not { } pe) { Console.WriteLine($"{rest[1]}: not found"); return 1; }
                foreach (string l in pdb.Dump(pdb.Prototype(pe.Id).Data, 200)) Console.WriteLine(l);
                return 0;
            }
            case "--power-fx":
            {
                // Read-only: what the 3D preview plays for a power class (its own; the MHO Hero Creator's --power-fx summary).
                // --power-fx <power class, e.g. powerthor_shockwave> [hero, e.g. Thor] [mod for its own package copies]
                if (rest.Count < 2) { Console.WriteLine("--power-fx <power class> [hero] [mod]"); return 1; }
                string? fgr = settings.ResolvedGameRoot(data);
                string? fcook = fgr != null && Settings.IsGameRoot(fgr) ? Settings.Cooked(fgr) : null;
                if (fcook == null) { Console.WriteLine("game folder not found"); return 1; }
                var fmod = rest.Count > 3 ? lib.Find(rest[3]) : null;
                var game = new Fx.FxGame(fcook, fmod == null ? [] : fmod.Manifest.UpkReplacements.Select(f => Path.Combine(fmod.Folder, f)));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // A prototype path (Powers\...\X.prototype): the power with what it sets off (the game data); else a class.
                Fx.PowerEffects fx;
                if (rest[1].EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                {
                    var db = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(fgr!, "Data", "Game", "Calligraphy.sip")));
                    Console.WriteLine($"(game data read in {sw.ElapsedMilliseconds} ms)"); sw.Restart();
                    fx = Fx.PowerEffects.For(game, db, rest[1], rest.Count > 2 ? rest[2] : null);
                }
                else fx = Fx.PowerEffects.ForClass(game, rest[1], rest.Count > 2 ? rest[2] : null);
                Console.WriteLine($"{rest[1]}: particles {string.Join(", ", fx.Effects.Where(e => e.BeamTarget == null).GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}; beams {fx.Effects.Count(e => e.BeamTarget != null)}; decals {fx.Decals.Count} ({fx.Decals.Count(d => d.Tex != null)} with a texture); weapon slots {string.Join(" ", fx.Slots.Select(x => (x.Show != null ? "+" + x.Show : "") + (x.Hide != null ? " -" + x.Hide : "")))}; thrown {fx.ThrownSlot ?? "-"}; mesh emitters {fx.Meshes.Count}; contact {fx.ContactPercent:0.##}{(fx.Returning ? ", returning" : "")}  ({sw.ElapsedMilliseconds} ms)");
                foreach (var e in fx.Effects)
                    Console.WriteLine($"  {e.Kind,-10} {e.Name}: {e.System.Name} ({e.System.Emitters.Count} emitters, {e.System.Emitters.Count(em => fx.Looks.TryGetValue(em, out var lk) && lk.Tex != null)} textured) at {(e.AtTarget ? "the target" : string.Join("/", e.Sockets.DefaultIfEmpty("root")))}, {e.Point}+{e.Offset:0.##}{(e.BeamTarget != null ? " beam to " + e.BeamTarget : "")}{(e.TriggeredBy != null ? " · by " + e.TriggeredBy : "")} · in {(e.System.Emitters.Count > 0 ? e.System.Emitters[0].Required.P.Name : "?")}");
                foreach (var n in fx.Notes.Distinct().Take(6)) Console.WriteLine("    note: " + n);
                return 0;
            }
            case "--anim-power":
            {
                // Read-only: the power an animation of a hero belongs to, and what the 3D preview would play for it.
                // --anim-power <hero, e.g. Thor> <animation name>
                if (rest.Count < 3) { Console.WriteLine("--anim-power <hero> <animation>"); return 1; }
                string? agr2 = settings.ResolvedGameRoot(data);
                string? acook = agr2 != null && Settings.IsGameRoot(agr2) ? Settings.Cooked(agr2) : null;
                if (acook == null) { Console.WriteLine("game folder not found"); return 1; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var db = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(agr2!, "Data", "Game", "Calligraphy.sip")));
                var byClass = Fx.PowerIndex.PrototypesByClass(db);
                Console.WriteLine($"game data and {byClass.Count} power classes in {sw.ElapsedMilliseconds} ms"); sw.Restart();
                var idx = Fx.PowerIndex.For(rest[1], acook, []);
                if (!idx.TryGetValue(rest[2], out var powers)) { Console.WriteLine($"{rest[2]}: no power of {rest[1]} plays it"); return 0; }
                var game = new Fx.FxGame(acook, []);
                foreach (var pw in powers)
                {
                    var protos = byClass.TryGetValue(pw.Class, out var l) ? l : [];
                    Console.WriteLine($"{rest[2]} → {pw.Class} → {(protos.Count == 0 ? "no prototype" : string.Join(", ", protos))}");
                    foreach (var proto in protos.Take(2))
                    {
                        var fx = Fx.PowerEffects.For(game, db, proto, rest[1]);
                        Console.WriteLine($"  {Path.GetFileNameWithoutExtension(proto)}: {fx.Effects.Count} particle effects ({fx.Effects.Count(e => e.TriggeredBy != null)} triggered), {fx.Decals.Count} decals, {fx.Meshes.Count} mesh emitters, contact {fx.ContactPercent:0.##}  ({sw.ElapsedMilliseconds} ms)");
                    }
                }
                return 0;
            }
            case "--props-audit":
            {
                // Read-only: every hero's base package (or the heroes given): its props (attachment classes) and what its
                // powers do with them, and its power buttons. Lists only problems: an on-demand prop no power brings out, a
                // power switch that names no prop, a prop whose mesh isn't found, a power button without a name or icon.
                string? agr = settings.ResolvedGameRoot(data);
                string? acook = agr != null && Settings.IsGameRoot(agr) ? Settings.Cooked(agr) : null;
                if (acook == null) { Console.WriteLine("game folder not found"); return 1; }
                var adb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(agr!, "Data", "Game", "Calligraphy.sip")));
                var heroes = rest.Count > 1 ? rest.Skip(1).ToList()
                    : Directory.EnumerateFiles(acook, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension)
                        .Select(n => n!.Split('_', StringSplitOptions.RemoveEmptyEntries)).Where(q => q.Length == 4).Select(q => q[2]).Order(StringComparer.OrdinalIgnoreCase).ToList();
                int clean = 0;
                foreach (string hero in heroes)
                {
                    var issues = new List<string>();
                    string basePath = Path.Combine(acook, $"UC__MarvelPlayer_{hero}_SF.upk");
                    var classes = File.Exists(basePath) ? ModMeshes.AttachmentClasses(basePath) ?? [] : [];
                    var atts = File.Exists(basePath) ? ModMeshes.Attachments(basePath) : [];
                    var meshNames = File.Exists(basePath) ? ModMeshes.List([(Path.GetFileName(basePath), basePath)]).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
                    var listed = atts.Where(x => classes.Contains(x.Class, StringComparer.OrdinalIgnoreCase)).ToList();
                    foreach (string c in classes.Where(c => !atts.Any(x => x.Class.Equals(c, StringComparison.OrdinalIgnoreCase)))) issues.Add($"class {c}: no attachment default in the base package");
                    foreach (var x in listed.Where(x => !meshNames.Contains(x.Mesh))) issues.Add($"prop {x.Mesh} ({x.Class}): mesh not in the base package");
                    var idx = Fx.PowerIndex.For(hero, acook, [], adb);
                    var allRules = idx.Values.SelectMany(v => v).Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase)
                        .SelectMany(f => ModMeshes.PropRules(f).Select(w => (Rule: w, File: Path.GetFileNameWithoutExtension(f)))).ToList();
                    bool Hits(ModMeshes.Attachment x, string target) => target.Equals("class:" + x.Class, StringComparison.OrdinalIgnoreCase) || x.Slots.Contains(target, StringComparer.OrdinalIgnoreCase)
                        || target.Equals("bothhands", StringComparison.OrdinalIgnoreCase) && (x.Slots.Contains("lefthand", StringComparer.OrdinalIgnoreCase) || x.Slots.Contains("righthand", StringComparer.OrdinalIgnoreCase));
                    foreach (var x in listed.Where(x => x.OnDemand))
                        if (!allRules.Any(w => w.Rule.Show && Hits(x, w.Rule.Target)))
                            issues.Add($"prop {x.Mesh} ({x.Class}, slots {string.Join(" ", x.Slots)}): on demand, no power shows it");
                    foreach (var w in allRules.Where(w => w.Rule.Show).DistinctBy(w => w.Rule.Target, StringComparer.OrdinalIgnoreCase))
                        if (!listed.Any(x => Hits(x, w.Rule.Target)) && !ModMeshes.Attachments(Path.Combine(acook, w.File + ".upk")).Any(x => Hits(x, w.Rule.Target)))
                            issues.Add($"show {w.Rule.Target} in {w.File}: no prop fills it");
                    var buttons = Fx.PowerList.For(adb, hero, acook, []);
                    foreach (var pw in buttons)
                    {
                        if (!pw.HasName) issues.Add($"power button {Path.GetFileNameWithoutExtension(pw.Prototype)}: no name");
                        if (pw.Icon == null) issues.Add($"power button {pw.Name}: no icon");
                        else if (Fx.PowerList.Icon(pw.Icon, acook) == null) issues.Add($"power button {pw.Name}: icon {pw.Icon} not found");
                    }
                    if (Fx.PowerList.TravelPowerOf(adb, hero) is string tp && !buttons.Any(b => Path.GetFileNameWithoutExtension(b.Prototype).Equals(Path.GetFileNameWithoutExtension(tp), StringComparison.OrdinalIgnoreCase)))
                        issues.Add($"travel power {Path.GetFileNameWithoutExtension(tp)}: no button");
                    if (idx.Count == 0) issues.Add("no power animations found");
                    if (issues.Count == 0) { clean++; Console.WriteLine($"{hero}: ok ({listed.Count} props, {buttons.Count} power buttons)"); continue; }
                    Console.WriteLine($"{hero}: {issues.Count} issue(s) ({listed.Count} props, {buttons.Count} power buttons)");
                    foreach (string i in issues) Console.WriteLine("    " + i);
                }
                Console.WriteLine($"{clean} of {heroes.Count} heroes clean");
                return 0;
            }
            case "--attach-census":
            {
                // Read-only: every export whose class mentions "attach" in the game's power and hero packages (or the files
                // given): counts per class and per property name, with one example each. Finds every way the game shows,
                // hides or switches a prop, instead of meeting them one hero at a time.
                string? cgr = settings.ResolvedGameRoot(data);
                string? ccook = cgr != null && Settings.IsGameRoot(cgr) ? Settings.Cooked(cgr) : null;
                if (ccook == null) { Console.WriteLine("game folder not found"); return 1; }
                var files = rest.Count > 1 ? rest.Skip(1).ToList()
                    : Directory.EnumerateFiles(ccook, "UC__*.upk").Where(f => { string n = Path.GetFileName(f); return !n.Contains("bak", StringComparison.OrdinalIgnoreCase) && !n.Contains("copy", StringComparison.OrdinalIgnoreCase) && (n.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) || n.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)); }).ToList();
                var classes = new Dictionary<string, (int Count, string Example)>(StringComparer.OrdinalIgnoreCase);
                var propsByClass = new Dictionary<string, Dictionary<string, (int Count, string Example)>>(StringComparer.OrdinalIgnoreCase);
                int done = 0, failed = 0;
                foreach (string f in files)
                {
                    try
                    {
                        var pk = MhoPackageModifier.Package.Open(f);
                        for (int i = 0; i < pk.Exports.Length; i++)
                        {
                            var e = pk.Exports[i];
                            string cls = pk.ClassOf(e);
                            if (!cls.Contains("attach", StringComparison.OrdinalIgnoreCase) || cls.Equals("Class", StringComparison.OrdinalIgnoreCase)) continue;
                            if (cls.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) cls = "marvelattachment*";
                            string where = Path.GetFileName(f) + " : " + e.ObjectName;
                            classes[cls] = classes.TryGetValue(cls, out var c) ? (c.Count + 1, c.Example) : (1, where);
                            var d = pk.ReadExportBytes(e);
                            // Components (power fx, anim notifies are objects too): try properties at 16, else 4.
                            var tags = MhoPackageModifier.TagWalker.Walk(pk, d, 16) ?? MhoPackageModifier.TagWalker.Walk(pk, d, 4);
                            if (tags == null) continue;
                            if (!propsByClass.TryGetValue(cls, out var pm)) propsByClass[cls] = pm = new(StringComparer.OrdinalIgnoreCase);
                            foreach (var t in tags)
                            {
                                string val = t.Type switch
                                {
                                    "NameProperty" => MhoPackageModifier.TagWalker.NameAt(pk, d, t.ValueAt),
                                    "ByteProperty" when t.Size == 8 => MhoPackageModifier.TagWalker.NameAt(pk, d, t.ValueAt),
                                    "ObjectProperty" => BitConverter.ToInt32(d, t.ValueAt) is int r && r != 0 ? (r > 0 ? pk.Exports[r - 1].ObjectName : pk.RefName(r)) : "none",
                                    "BoolProperty" => d[t.ValueAt - 1] != 0 ? "true" : "false",
                                    "FloatProperty" => BitConverter.ToSingle(d, t.ValueAt).ToString("0.###"),
                                    "IntProperty" => BitConverter.ToInt32(d, t.ValueAt).ToString(),
                                    "ArrayProperty" => $"[{BitConverter.ToInt32(d, t.ValueAt)}]",
                                    _ => t.Type,
                                };
                                string ex = $"{val}   ({where})";
                                pm[t.Name] = pm.TryGetValue(t.Name, out var q) ? (q.Count + 1, q.Example) : (1, ex);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or ArgumentException or IndexOutOfRangeException) { failed++; }
                    if (++done % 500 == 0) Console.Error.WriteLine($"  {done} / {files.Count}");
                }
                Console.WriteLine($"{files.Count} packages read ({failed} failed)");
                foreach (var (cls, (count, ex)) in classes.OrderByDescending(x => x.Value.Count))
                {
                    Console.WriteLine($"{cls}: {count}   e.g. {ex}");
                    if (propsByClass.TryGetValue(cls, out var pm) && cls != "marvelattachment*")
                        foreach (var (pn, (pc, pe)) in pm.OrderByDescending(x => x.Value.Count)) Console.WriteLine($"    {pn}: {pc}   e.g. {pe}");
                }
                return 0;
            }
            case "--power-packages":
            {
                // Read-only: for each power button of a hero, every class the power sets off (PowerClosure) with its prototype,
                // whether it has a package, and whether the power customizer recolours it (PowerRecolor.PackagesOf).
                // --power-packages <hero> [power name part]
                if (rest.Count < 2) { Console.WriteLine("--power-packages <hero> [power name part]"); return 1; }
                string? qgr = settings.ResolvedGameRoot(data);
                if (qgr == null || !Settings.IsGameRoot(qgr)) { Console.WriteLine("game folder not found"); return 1; }
                string qcook = Settings.Cooked(qgr);
                var qdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(qgr, "Data", "Game", "Calligraphy.sip")));
                foreach (var pw in Fx.PowerList.For(qdb, rest[1], qcook, [], effectOnly: true).Where(x => rest.Count < 3 || x.Name.Contains(rest[2], StringComparison.OrdinalIgnoreCase)))
                {
                    var used = PowerRecolor.PackagesOf(qdb, pw.Prototype, rest[1], qcook).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    Console.WriteLine($"{pw.Name} ({Path.GetFileNameWithoutExtension(pw.Prototype)}): {used.Count} package(s)");
                    if (PowerRecolor.SharedWith(qdb, pw.Prototype, rest[1], qcook) is { Count: > 0 } sh) Console.WriteLine("    also changes: " + string.Join(", ", sh));
                    var classes = new List<(string Class, string Proto)>();
                    if (Fx.PowerEffects.UnrealClassOf(qdb, pw.Prototype) is string own) classes.Add((own, pw.Prototype));
                    foreach (var art in Fx.PowerClosure.Of(qdb, pw.Prototype)) if (!string.IsNullOrEmpty(art.Class)) classes.Add((art.Class, art.Prototype));
                    foreach (var (cls, proto) in classes.DistinctBy(x => x.Class, StringComparer.OrdinalIgnoreCase))
                    {
                        string f = $"UC__{cls}_SF.upk";
                        string state = used.Contains(f) ? "RECOLORED" : File.Exists(Path.Combine(qcook, f)) ? "left (generic: 6+ owners)" : "no package";
                        Console.WriteLine($"    {state,-26} {cls}   <- {proto.Replace('\\', '/')}");
                        if (Environment.GetEnvironmentVariable("MHO_POWER_USERS") == "1" && !used.Contains(f) && File.Exists(Path.Combine(qcook, f)))
                            foreach (string u in PowerRecolor.UsersOf(qdb, cls, rest[1], qcook)) Console.WriteLine("        used by " + u);
                    }
                }
                return 0;
            }
            case "--power-recolor":
            {
                // Build (to a file outside the game folder): a power's stock package recoloured (PowerRecolor).
                // --power-recolor <UC__Power….upk> <hue degrees> <saturation> <brightness> <out.upk>
                if (rest.Count < 6) { Console.WriteLine("--power-recolor <UC__Power….upk> <hue> <saturation> <brightness> <out.upk>"); return 1; }
                string? rgr = settings.ResolvedGameRoot(data);
                if (rgr == null || !Settings.IsGameRoot(rgr)) { Console.WriteLine("game folder not found"); return 1; }
                string rout = Path.GetFullPath(rest[5]);
                if (rout.StartsWith(Path.GetFullPath(rgr), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("not into the game folder"); return 1; }
                var rgame = new GameState(rgr, data);
                string pkgFile = Path.GetFileName(rest[1]).EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(rest[1]) : Path.GetFileName(rest[1]) + ".upk";
                string? stock = new Originals(lib.DataFolder, rgame).Find(pkgFile);
                if (stock == null) { Console.WriteLine($"no verified stock copy of {pkgFile}"); return 1; }
                var color = new PowerColor(float.Parse(rest[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(rest[4], System.Globalization.CultureInfo.InvariantCulture))
                {
                    // --map FROM=TO[@tolerance] (repeatable): single colors replaced, hex.
                    Maps = [.. rest.Select((x, k) => (x, k)).Where(x => x.x == "--map" && x.k + 1 < rest.Count).Select(x => rest[x.k + 1].Split('=', '@'))
                        .Select(m => ColorMap.FromHex(m[0]) is { } f && m.Length > 1 && ColorMap.FromHex(m[1]) is { } t
                            ? new ColorMap(f, t, m.Length > 2 ? float.Parse(m[2], System.Globalization.CultureInfo.InvariantCulture) : 0.3f) : throw new ArgumentException("--map FROM=TO[@tolerance], hex colors"))],
                };
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[]? built = PowerRecolor.Build(stock, color, Settings.Cooked(rgr), l => Console.WriteLine("  " + l));
                    if (built == null) { Console.WriteLine("nothing to recolor in it; no file written"); return 0; }
                    File.WriteAllBytes(rout, built);
                    Console.WriteLine($"built and verified: {rout} ({built.Length:N0} bytes, {sw.ElapsedMilliseconds} ms) from {stock}");
                    return 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or ArgumentException) { Console.WriteLine("not built: " + ex.Message); return 1; }
            }
            case "--power-palette":
            {
                // Read-only: the colors a power uses (PowerRecolor.Palette over its stock packages), for the Powers tab's
                // single-color replacement. --power-palette <hero> <power name or prototype part>
                if (rest.Count < 2 || rest.Count < 3 && !rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("--power-palette <hero> <power name or prototype part>"); return 1; }
                string? pgr = settings.ResolvedGameRoot(data);
                if (pgr == null || !Settings.IsGameRoot(pgr)) { Console.WriteLine("game folder not found"); return 1; }
                string pcook = Settings.Cooked(pgr);
                var pgame = new GameState(pgr, data);
                StockFiles.Init(pgame, settings.CleanGameFiles, Path.Combine(data, "originals"));
                if (rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
                {
                    // --power-palette <file.upk> … : the colors of these package files (a built recolor, to check it).
                    foreach (var c in PowerRecolor.Palette(rest.Skip(1).Where(x => x.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)), pcook))
                        Console.WriteLine($"  {ColorMap.Hex(c.Tint)}  {c.Share * 100,5:0.0} %  hue {PowerRecolor.Hue(c.Tint),3:0}  {c.Sources}");
                    return 0;
                }
                var pdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(pgr, "Data", "Game", "Calligraphy.sip")));
                var powers = Fx.PowerList.For(pdb, rest[1], pcook, [], effectOnly: true);
                var pw = powers.FirstOrDefault(x => x.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase))
                         ?? powers.FirstOrDefault(x => x.Name.Contains(rest[2], StringComparison.OrdinalIgnoreCase) || x.Prototype.Contains(rest[2], StringComparison.OrdinalIgnoreCase));
                if (pw == null) { Console.WriteLine($"no power like \"{rest[2]}\"; {rest[1]}'s: {string.Join(", ", powers.Select(x => x.Name))}"); return 1; }
                var originals = new Originals(data, pgame);
                var files = PowerRecolor.PackagesOf(pdb, pw.Prototype, rest[1], pcook).Select(f => originals.Find(f) ?? StockFiles.For(pcook, f)).ToList();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var pal = PowerRecolor.Palette(files, pcook);
                Console.WriteLine($"{pw.Name} ({pw.Prototype}): {files.Count} package(s), {pal.Count} color(s), {sw.ElapsedMilliseconds} ms");
                foreach (var c in pal)
                    Console.WriteLine($"  {ColorMap.Hex(c.Tint)}  {c.Share * 100,5:0.0} %  hue {PowerRecolor.Hue(c.Tint),3:0}  {c.Sources}");
                return 0;
            }
            case "--actor-props":
            {
                // Read-only: every animated actor (PowerFxAnimatedActor / EntityFxAnimatedActor) in the UC__ packages: which
                // properties they set, how often, with example values. --actor-props [package name part]
                string? agr = settings.ResolvedGameRoot(data);
                string? acook2 = agr != null && Settings.IsGameRoot(agr) ? Settings.Cooked(agr) : null;
                if (acook2 == null) { Console.WriteLine("game folder not found"); return 1; }
                var counts = new SortedDictionary<string, (int N, List<string> Ex)>(StringComparer.OrdinalIgnoreCase);
                int actorsSeen = 0;
                foreach (string f in Directory.EnumerateFiles(acook2, "UC__*_SF.upk"))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rest.Count > 1 && !fn.Contains(rest[1], StringComparison.OrdinalIgnoreCase)) continue;
                    MhoPackageModifier.Package pk;
                    try { pk = MhoPackageModifier.Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
                    var t = new Fx.FxTables(pk);
                    for (int i = 0; i < pk.Exports.Length; i++)
                    {
                        string c = pk.ClassOf(pk.Exports[i]);
                        if (!(c.StartsWith("PowerFxAnimatedActor", StringComparison.OrdinalIgnoreCase) || c.StartsWith("EntityFxAnimatedActor", StringComparison.OrdinalIgnoreCase)) || c.EndsWith("MaterialParameter", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!pk.PathOf(pk.Exports[i]).Contains("default__", StringComparison.OrdinalIgnoreCase)) continue;
                        actorsSeen++;
                        foreach (var pr in Fx.FxProps.Find(pk.Body, t, t.Exports[i])?.Props ?? [])
                        {
                            counts.TryGetValue(pr.Name, out var v);
                            var ex = v.Ex ?? [];
                            string val = pr.Value ?? (pr.Size == 4 && pr.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) ? BitConverter.ToSingle(pk.Body, pr.ValueAt).ToString("0.##") : pr.Type);
                            if (ex.Count < 4 && !ex.Contains(val)) ex.Add(val);
                            counts[pr.Name] = (v.N + 1, ex);
                        }
                    }
                }
                Console.WriteLine($"{actorsSeen} animated actor defaults");
                foreach (var (n, (k, ex)) in counts.OrderByDescending(x => x.Value.N)) Console.WriteLine($"  {n,-34} {k,4}  e.g. {string.Join(" | ", ex)}");
                return 0;
            }
            case "--singlekey-census":
            {
                // Read-only: in the given packages' AnimSets, the single-key rotation tracks of posed bones (not the root or
                // a *_offset bone) where "closer to the bind pose" picks the mirrored (conjugate) reading over the stored one,
                // and by how much (degrees between them). --singlekey-census <file.upk> …
                int tracks = 0, flipped = 0, storedWins = 0, mirroredWins = 0;
                var byBone = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var examples = new List<string>();
                foreach (string path in rest.Skip(1))
                {
                    var ap = AnimExportCli.Packages.Package.Open(path);
                    var meshIdx = ap.FindExportsOfClass("skeletalmesh").ToList();
                    foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                    {
                        // The rest pose: the package's first mesh with these bones.
                        var mesh = meshIdx.Select(mi => { try { return AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, mi, _ => { }); } catch { return null; } })
                            .FirstOrDefault(m => m != null && set.TrackBoneNames.Count(n => m.Bones.Any(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) > set.TrackBoneNames.Count / 2);
                        if (mesh == null) continue;
                        // Every multi-key rotation of each bone in the set (not ambiguous): what the bone's poses look like.
                        var poses = new Dictionary<string, List<System.Numerics.Quaternion>>(StringComparer.OrdinalIgnoreCase);
                        var decodedSeqs = set.Sequences.Where(q => q.IsExport).Select(q => (q, A: AnimExportCli.Animation.AnimObjectReader.TryRead(ap, q.ExportIndex, set.TrackBoneNames))).ToList();
                        foreach (var (_, A) in decodedSeqs)
                            if (A != null)
                                foreach (var (bn, tr) in A.Tracks)
                                    if (tr.RotationKeys.Count > 1)
                                    {
                                        if (!poses.TryGetValue(bn, out var pl)) poses[bn] = pl = [];
                                        for (int k = 0; k < tr.RotationKeys.Count; k += 4) pl.Add(System.Numerics.Quaternion.Normalize(tr.RotationKeys[k].Rotation));
                                    }
                        foreach (var seq in set.Sequences.Where(q => q.IsExport))
                        {
                            var anim = AnimExportCli.Animation.AnimObjectReader.TryRead(ap, seq.ExportIndex, set.TrackBoneNames);
                            if (anim == null) continue;
                            string an = AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex);
                            foreach (var (bone, t) in anim.Tracks)
                            {
                                if (t.RotationKeys.Count != 1 || bone.EndsWith("_offset", StringComparison.OrdinalIgnoreCase)) continue;
                                var mb = mesh.Bones.FirstOrDefault(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase));
                                if (mb == null || mb.ParentIndex < 0 || bone.Equals("root", StringComparison.OrdinalIgnoreCase) || ReferenceEquals(mesh.Bones[0], mb)) continue;
                                tracks++;
                                var d = t.RotationKeys[0].Rotation; var rest0 = System.Numerics.Quaternion.Normalize(mb.Orientation);
                                var c = new System.Numerics.Quaternion(-d.X, -d.Y, -d.Z, d.W);
                                if (MathF.Abs(System.Numerics.Quaternion.Dot(c, rest0)) <= MathF.Abs(System.Numerics.Quaternion.Dot(d, rest0))) continue;
                                float deg = 2 * MathF.Acos(Math.Clamp(MathF.Abs(System.Numerics.Quaternion.Dot(System.Numerics.Quaternion.Normalize(d), System.Numerics.Quaternion.Normalize(c))), 0, 1)) * 180 / MathF.PI;
                                if (deg < 2) continue;   // the two readings are the same rotation, near enough
                                flipped++;
                                // Which reading looks like the bone's multi-key poses (smallest angle to any of them)?
                                if (poses.TryGetValue(bone, out var known) && known.Count > 0)
                                {
                                    float Near(System.Numerics.Quaternion q) => known.Min(k => 2 * MathF.Acos(Math.Clamp(MathF.Abs(System.Numerics.Quaternion.Dot(System.Numerics.Quaternion.Normalize(q), k)), 0, 1)));
                                    float nd = Near(d), nc = Near(c);
                                    if (nd < nc) storedWins++; else if (nc < nd) mirroredWins++;
                                }
                                byBone[bone] = byBone.GetValueOrDefault(bone) + 1;
                                if (examples.Count < 12) examples.Add($"{Path.GetFileName(path)} {an} {bone}: {deg:0}° apart");
                            }
                        }
                    }
                }
                Console.WriteLine($"{tracks} single-key tracks of posed bones; the bind-pose rule picks the mirrored reading (2°+ apart) on {flipped}");
                Console.WriteLine($"  of those, closer to the same bone's multi-key poses: the stored reading {storedWins}, the mirrored one {mirroredWins}");
                foreach (var (b, n) in byBone.OrderByDescending(x => x.Value).Take(15)) Console.WriteLine($"  {b}: {n}");
                foreach (var e in examples) Console.WriteLine("  e.g. " + e);
                return 0;
            }
            case "--attach-offsets":
            {
                // Read-only: every attachment class default (marvelattachment*) in the UC__ packages that sets an offset
                // property (OffsetRotation, OffsetTranslation …): class, package, values; and which classes extend it.
                // --attach-offsets [package name part]
                string? ogr = settings.ResolvedGameRoot(data);
                string? ocook = ogr != null && Settings.IsGameRoot(ogr) ? Settings.Cooked(ogr) : null;
                if (ocook == null) { Console.WriteLine("game folder not found"); return 1; }
                int seen = 0;
                foreach (string f in Directory.EnumerateFiles(ocook, rest.Count > 1 && !rest[1].StartsWith("UC__", StringComparison.OrdinalIgnoreCase) ? "*" + rest[1] + "*.upk" : "UC__*_SF.upk"))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rest.Count > 1 && !fn.Contains(rest[1], StringComparison.OrdinalIgnoreCase)) continue;
                    MhoPackageModifier.Package pk;
                    try { pk = MhoPackageModifier.Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
                    for (int i = 0; i < pk.Exports.Length; i++)
                    {
                        var e = pk.Exports[i];
                        if (!e.ObjectName.StartsWith("default__marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
                        seen++;
                        byte[] d = pk.ReadExportBytes(e);
                        if (MhoPackageModifier.TagWalker.Walk(pk, d, 4) is not { } tags) continue;
                        var offs = tags.Where(t => t.Name.Contains("offset", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("rotation", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("scale", StringComparison.OrdinalIgnoreCase)).ToList();
                        if (offs.Count == 0) continue;
                        string vals = string.Join("; ", offs.Select(t => t.Size == 12 && t.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)
                            ? (t.Name.Contains("rotation", StringComparison.OrdinalIgnoreCase)
                                ? $"{t.Name} pitch {BitConverter.ToInt32(d, t.ValueAt)} yaw {BitConverter.ToInt32(d, t.ValueAt + 4)} roll {BitConverter.ToInt32(d, t.ValueAt + 8)}"
                                : $"{t.Name} {BitConverter.ToSingle(d, t.ValueAt):0.##},{BitConverter.ToSingle(d, t.ValueAt + 4):0.##},{BitConverter.ToSingle(d, t.ValueAt + 8):0.##}")
                            : t.Size == 4 && t.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) ? $"{t.Name} {BitConverter.ToSingle(d, t.ValueAt):0.##}" : $"{t.Name} ({t.Type} {t.Size})"));
                        Console.WriteLine($"{fn}: {e.ObjectName[9..]}: {vals}");
                    }
                }
                Console.WriteLine($"{seen} attachment defaults read");
                return 0;
            }
            case "--mesh-origin":
            {
                // Read-only: each skeletal mesh's Origin and RotOrigin (native data) in the given packages.
                foreach (string path in rest.Skip(1))
                {
                    var ap = AnimExportCli.Packages.Package.Open(path);
                    foreach (int i in ap.FindExportsOfClass("skeletalmesh"))
                        if (AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i, _ => { }) is { } sm)
                            Console.WriteLine($"{Path.GetFileName(path)} | {sm.Name}: origin {sm.Origin.X:0.##},{sm.Origin.Y:0.##},{sm.Origin.Z:0.##}; rot origin pitch {sm.RotOrigin.Pitch} yaw {sm.RotOrigin.Yaw} roll {sm.RotOrigin.Roll}; root bone {sm.Bones[0].Name}");
                }
                return 0;
            }
            case "--fx-census":
            {
                // Read-only: per hero, the powers whose effects include summoned entities' models or animated actors (the
                // parts the 3D preview doesn't show yet). --fx-census [hero …] (none: every hero)
                string? cgr = settings.ResolvedGameRoot(data);
                string? ccook = cgr != null && Settings.IsGameRoot(cgr) ? Settings.Cooked(cgr) : null;
                if (ccook == null) { Console.WriteLine("game folder not found"); return 1; }
                var cdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(cgr!, "Data", "Game", "Calligraphy.sip")));
                var cgame = new Fx.FxGame(ccook, []);
                var heroes = rest.Count > 1 ? rest.Skip(1).ToList()
                    : Directory.EnumerateFiles(ccook, "UC__MarvelPlayer_*_SF.upk").Select(f => System.Text.RegularExpressions.Regex.Match(Path.GetFileName(f), @"^UC__MarvelPlayer_([A-Za-z0-9]+)_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
                int summons = 0, actors = 0, powersWith = 0;
                foreach (string h in heroes)
                {
                    List<Fx.PowerList.Power> list;
                    try { list = Fx.PowerList.For(cdb, h, ccook, []); } catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException) { continue; }
                    foreach (var pw in list)
                    {
                        Fx.PowerEffects pfx;
                        try { pfx = Fx.PowerEffects.For(cgame, cdb, pw.Prototype, h); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException) { continue; }
                        var notes = pfx.Notes.Distinct().Where(n => n.Contains("summoned") || n.Contains("animated actor")).ToList();
                        if (notes.Count == 0 && pfx.Actors.Count == 0) continue;
                        powersWith++;
                        summons += notes.Count(n => n.Contains("summoned")); actors += pfx.Actors.Count;
                        Console.WriteLine($"{h} · {pw.Name}: " + string.Join("; ", pfx.Actors.Take(6).Select(x => $"actor {x.Name}{(x.TriggeredBy != null ? " BY " + x.TriggeredBy : "")} ({Path.GetFileName(x.MeshFile)} #{x.MeshExport}, {x.AnimName ?? "no anim"}{(x.SetFile == null ? ", no AnimSet" : "")}, {x.Point}+{x.Offset:0.##}{(x.AtTarget ? ", at target" : "")}{(x.Shift != System.Numerics.Vector3.Zero ? $", shift {x.Shift.X:0},{x.Shift.Y:0},{x.Shift.Z:0}" : "")}{(x.Scale != 1 ? $", scale {x.Scale:0.##}" : "")})").Concat(notes.Take(3))));
                    }
                }
                Console.WriteLine($"{powersWith} power(s) of {heroes.Count} hero(es): {summons} summoned model(s), {actors} animated actor(s)");
                return 0;
            }
            case "--hero-powers":
            {
                // Read-only: a hero's powers as the 3D preview's power buttons list them (name, icon, animations).
                if (rest.Count < 2) { Console.WriteLine("--hero-powers <hero>"); return 1; }
                string? hgr = settings.ResolvedGameRoot(data);
                string? hcook = hgr != null && Settings.IsGameRoot(hgr) ? Settings.Cooked(hgr) : null;
                if (hcook == null) { Console.WriteLine("game folder not found"); return 1; }
                var db = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(hgr!, "Data", "Game", "Calligraphy.sip")));
                foreach (var pw in Fx.PowerList.For(db, rest[1], hcook, [], effectOnly: true))
                {
                    var ic = pw.Icon == null ? null : Fx.PowerList.Icon(pw.Icon, hcook);
                    Console.WriteLine($"  {pw.Name} ({Path.GetFileNameWithoutExtension(pw.Prototype)}): icon {pw.Icon ?? "none"}{(ic == null ? "" : $" {ic.Width}×{ic.Height}")}; {(pw.Animations.Count == 0 ? "(no animation: effects only)" : string.Join(", ", pw.Animations))}");
                    // MHO_POWER_FIELDS=1: every asset (A) field of the power, to see which icons it names.
                    if (Environment.GetEnvironmentVariable("MHO_POWER_FIELDS") == "1" && db.Find(pw.Prototype) is { } pe)
                        foreach (var grp in db.Prototype(pe.Id).Data.Groups)
                            foreach (var fl in grp.Simple)
                                if (fl.Type == 'A')
                                    Console.WriteLine($"      {db.FieldName(grp.Blueprint, fl.Id)} = {(db.Assets.TryGetValue(fl.Value.Raw, out var asx) ? asx.Asset.Name : fl.Value.Raw.ToString())}");
                    // MHO_POWER_ICON_DIR: each icon as <power>.png too, to look at.
                    if (ic != null && Environment.GetEnvironmentVariable("MHO_POWER_ICON_DIR") is string pid) { Directory.CreateDirectory(pid); ic.Save(Path.Combine(pid, string.Concat(pw.Name.Where(char.IsLetterOrDigit)) + ".png")); }
                }
                return 0;
            }
            case "--socket-track":
            {
                // Read-only: a mesh posed by an animation at a few frames, with sockets' places (and the direction from the
                // first to each other, as a power's beam would go). --socket-track <mod | package.upk> <animation> <socket> [socket ...]
                if (rest.Count < 4) { Console.WriteLine("--socket-track <mod | package.upk> <animation> <socket> [socket ...]"); return 1; }
                string? tgr = settings.ResolvedGameRoot(data);
                string? tcooked = tgr != null && Settings.IsGameRoot(tgr) ? Settings.Cooked(tgr) : null;
                var tm = rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? null : lib.Find(rest[1]);
                var refs = tm != null ? ModMeshes.List(tm) : ModMeshes.List([(Path.GetFileName(rest[1]), rest[1])], anyPackage: true);
                var mr = refs.FirstOrDefault();
                if (mr == null) { Console.WriteLine("no mesh"); return 1; }
                var loaded = ModMeshes.Load(mr, tcooked, out string why);
                if (loaded == null) { Console.WriteLine("can't load: " + why); return 1; }
                var pk = tm != null ? tm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(tm.Folder, f))).ToList() : [(Path.GetFileName(rest[1]), rest[1])];
                var anim = ModAnimations.For(mr, loaded.Bones, pk, tcooked).FirstOrDefault(a => a.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                var ba = anim == null ? null : ModAnimations.Load(anim);
                if (ba == null) { Console.WriteLine($"no animation {rest[2]}"); return 1; }
                var socks = Fx.FxSockets.Of(mr.File, mr.Name);
                var animr = new MeshAnimator(loaded.Bones, loaded.Positions, loaded.Normals, loaded.Influences);
                var (frames, secs) = MeshAnimator.Span(ba);
                Console.WriteLine($"{mr.Name} ({mr.Package}): {rest[2]} from {anim!.Package}, {frames:0} frames");
                static string V(System.Numerics.Vector3 v) => $"({v.X,7:0.0}, {v.Y,7:0.0}, {v.Z,7:0.0})";
                foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f })
                {
                    animr.Pose(ba, t * frames);
                    var places = new List<(string, System.Numerics.Vector3)>();
                    foreach (string sn in rest.Skip(3).Where(x => !x.StartsWith("--")))
                    {
                        if (!socks.TryGetValue(sn, out var so)) { Console.WriteLine($"  no socket {sn}"); continue; }
                        int bi = animr.BoneIndex(so.Bone);
                        var w = so.Local * animr.BoneMatrix(bi);
                        places.Add((sn, w.Translation));
                        if (rest.Contains("--axes")) Console.WriteLine($"    {sn}: X {V(System.Numerics.Vector3.Normalize(new(w.M11, w.M12, w.M13)))} Y {V(System.Numerics.Vector3.Normalize(new(w.M21, w.M22, w.M23)))} Z {V(System.Numerics.Vector3.Normalize(new(w.M31, w.M32, w.M33)))}");
                    }
                    if (places.Count == 0) break;
                    var from = places[0].Item2;
                    Console.WriteLine($"  frame {t * frames,5:0}: {places[0].Item1} at {V(from)}" + string.Concat(places.Skip(1).Select(p => $"; {p.Item1} dir {V(System.Numerics.Vector3.Normalize(p.Item2 - from))}")));
                }
                return 0;
            }
            case "--anim-census":
            {
                // Read-only: for every UC__MarvelPlayer_* package in a folder (no bak / copy), its AnimSet exports and each
                // class default's initialskeletalmesh AnimSets list in order. --anim-census <CookedPCConsole> [name part]
                if (rest.Count < 2) { Console.WriteLine("--anim-census <CookedPCConsole> [name part]"); return 1; }
                string part = rest.Count > 2 ? rest[2] : "";
                int withSets = 0, withList = 0, total = 0;
                foreach (string f in Directory.EnumerateFiles(rest[1], "UC__MarvelPlayer_*.upk").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (part.Length > 0 && !fn.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                    MhoPackageModifier.Package pp;
                    try { pp = MhoPackageModifier.Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
                    total++;
                    var sets = Enumerable.Range(0, pp.Exports.Length).Where(i => pp.ClassOf(pp.Exports[i]).Equals("AnimSet", StringComparison.OrdinalIgnoreCase))
                        .Select(i => $"{pp.PathOf(pp.Exports[i])} ({Enumerable.Range(0, pp.Exports.Length).Count(j => pp.Exports[j].OuterIndex == i + 1 && pp.ClassOf(pp.Exports[j]).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase))} seq)").ToList();
                    var lists = new List<string>();
                    for (int i = 0; i < pp.Exports.Length; i++)
                    {
                        string path = pp.PathOf(pp.Exports[i]);
                        if (!path.StartsWith("marvelgamecontent.default__", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase)) continue;
                        byte[] d = pp.ReadExportBytes(pp.Exports[i]).ToArray();
                        var t = MhoPackageModifier.TagWalker.Walk(pp, d, 16)?.FirstOrDefault(x => x.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase));
                        if (t == null) continue;
                        int n = BitConverter.ToInt32(d, t.ValueAt);
                        var refs = Enumerable.Range(0, Math.Max(0, n)).Select(k => BitConverter.ToInt32(d, t.ValueAt + 4 + 4 * k))
                            .Select(r => r > 0 ? pp.PathOf(pp.Exports[r - 1]) + " (export)" : r < 0 ? pp.RefName(r) : "none");
                        lists.Add($"{path.Split('.')[1]}: [{string.Join(", ", refs)}]");
                    }
                    if (sets.Count > 0) withSets++;
                    if (lists.Count > 0) withList++;
                    if (sets.Count == 0 && lists.Count == 0) continue;
                    Console.WriteLine(fn);
                    foreach (string s in sets) Console.WriteLine($"  set   {s}");
                    foreach (string l in lists) Console.WriteLine($"  list  {l}");
                }
                Console.WriteLine($"{total} packages; {withSets} with AnimSets; {withList} with an AnimSets list on a mesh component");
                return 0;
            }
            case "--props-under":
            {
                // Read-only: every export whose path starts with a prefix, with its simple properties (names, numbers, flags,
                // object references by path). --props-under <package.upk> <path prefix>
                if (rest.Count < 3) { Console.WriteLine("--props-under <package.upk> <path prefix>"); return 1; }
                var pp = MhoPackageModifier.Package.Open(rest[1]);
                for (int i = 0; i < pp.Exports.Length; i++)
                {
                    string path = pp.PathOf(pp.Exports[i]);
                    if (!path.StartsWith(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] d = pp.ReadExportBytes(pp.Exports[i]).ToArray();
                    string cls = pp.ClassOf(pp.Exports[i]);
                    var tags = MhoPackageModifier.TagWalker.Walk(pp, d, 4) ?? MhoPackageModifier.TagWalker.Walk(pp, d, 16);
                    var parts = new List<string>();
                    foreach (var t in tags ?? [])
                    {
                        string v = t.Type.ToLowerInvariant() switch
                        {
                            "nameproperty" when t.Size == 8 => MhoPackageModifier.TagWalker.NameAt(pp, d, t.ValueAt),
                            "floatproperty" => BitConverter.ToSingle(d, t.ValueAt).ToString("0.###"),
                            "intproperty" => BitConverter.ToInt32(d, t.ValueAt).ToString(),
                            "boolproperty" => d[t.ValueAt - 1] != 0 ? "true" : "false",
                            "byteproperty" when t.Size == 8 => MhoPackageModifier.TagWalker.NameAt(pp, d, t.ValueAt),
                            "objectproperty" => BitConverter.ToInt32(d, t.ValueAt) is int r && r != 0 ? (r > 0 ? pp.PathOf(pp.Exports[r - 1]) : pp.RefName(r)) : "none",
                            "arrayproperty" when BitConverter.ToInt32(d, t.ValueAt) is int n && n > 0 && t.Size == 4 + 8 * n && t.Name.EndsWith("name", StringComparison.OrdinalIgnoreCase)
                                => "[" + string.Join(", ", Enumerable.Range(0, n).Select(k => MhoPackageModifier.TagWalker.NameAt(pp, d, t.ValueAt + 4 + 8 * k))) + "]",
                            _ => $"[{t.Type} {t.Size}]",
                        };
                        parts.Add($"{t.Name}={v}");
                    }
                    Console.WriteLine($"{path} ({cls}): {string.Join("; ", parts)}");
                }
                return 0;
            }
            case "--mesh-copy-test":
            {
                // Test (phase 1): copy a skeletal mesh into another package (renamed), write it to a scratch file, read it back.
                // --mesh-copy-test <source.upk> <mesh> <target.upk> <out.upk>   (never the game folder)
                if (rest.Count < 5) { Console.WriteLine("--mesh-copy-test <source.upk> <mesh> <target.upk> <out.upk>"); return 1; }
                var probs = MeshCopy.Test(rest[1], rest[2], rest[3], rest[4]);
                Console.WriteLine(probs.Count == 0 ? $"PASS: {rest[2]} copied and read back identical (bones, geometry, material paths)" : "FAIL: " + string.Join("; ", probs));
                return probs.Count == 0 ? 0 : 1;
            }
            case "--mesh-bones":
            {
                // Read-only: every skeletal mesh in the given packages with its bone names (skeleton comparisons).
                foreach (string f in rest.Skip(1))
                    foreach (var mr in ModMeshes.List([(Path.GetFileName(f), f)]))
                    {
                        var ld = ModMeshes.Load(mr, null, out string why);
                        Console.WriteLine(ld == null ? $"{mr.Package} | {mr.Name}: {why}" : $"{mr.Package} | {mr.Name} | {ld.Bones.Count} | " + string.Join(",", ld.Bones.Select(b => b.Name)));
                    }
                return 0;
            }
            case "--material-probe":
            {
                // Read-only: each section's material (parent, switches on, parameters, maps) for a mod's meshes; with an
                // output folder, every map as PNG plus the packed spec map's channels one by one (to see what each holds).
                var pm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                bool upk = rest.Count > 1 && rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase) && File.Exists(rest[1]);
                if (pm == null && !upk) { Console.WriteLine("--material-probe <mod | package.upk> [png folder]"); return 1; }
                string? pgr = settings.ResolvedGameRoot(data);
                string? pc = pgr != null && Settings.IsGameRoot(pgr) ? Settings.Cooked(pgr) : null;
                string? outDir = rest.Count > 2 ? rest[2] : null;
                if (outDir != null) Directory.CreateDirectory(outDir);
                foreach (var mr in upk ? ModMeshes.List([(Path.GetFileName(rest[1]), rest[1])]) : ModMeshes.List(pm!))
                {
                    Console.WriteLine($"{mr.Package} | {mr.Name}");
                    var mpk = MhoPackageModifier.Package.Open(mr.File);
                    foreach (var (sec, mat) in ModMeshes.SectionMaterials(mr))
                    {
                        var mi = ModMaterials.Read(mpk, mat);
                        if (mi == null) { Console.WriteLine($"  section {sec}: material {(mat < 0 ? mpk.RefName(mat) + " (imported)" : "none")}"); continue; }
                        Console.WriteLine($"  section {sec}: {mi.Name}  (parent {mi.Parent})");
                        Console.WriteLine("    switches on: " + string.Join(", ", mi.Switches.Where(x => x.Value).Select(x => x.Key)));
                        foreach (var (k, v) in mi.Scalars) Console.WriteLine($"    scalar {k} = {v:0.###}");
                        foreach (var (k, v) in mi.Vectors) Console.WriteLine($"    vector {k} = ({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###})");
                        foreach (var (k, ti) in mi.Textures)
                        {
                            var mip = MhoPackageModifier.TextureExport.ReadBestMip(mpk, ti, out string note, pc);
                            Console.WriteLine($"    texture {k} = {mpk.Exports[ti].ObjectName}  {(mip == null ? note : $"{mip.Format} {mip.Width}x{mip.Height}")}");
                            if (outDir == null || mip == null || MhoPackageModifier.TextureDecode.ToBgra(mip.Format, mip.Width, mip.Height, mip.Pixels, out _) is not byte[] px) continue;
                            string stem = Path.Combine(outDir, $"{mr.Name}_s{sec}_{k}");   // (per mesh: a mod with two meshes overwrote the first's maps)
                            SavePng(px, mip.Width, mip.Height, stem + ".png", -1);
                            if (k.Contains("spec", StringComparison.OrdinalIgnoreCase) || k.Contains("mask", StringComparison.OrdinalIgnoreCase))
                                foreach (var (ch, off) in new[] { ("R", 2), ("G", 1), ("B", 0), ("A", 3) }) SavePng(px, mip.Width, mip.Height, $"{stem}_{ch}.png", off);
                        }
                    }
                }
                return 0;
                static void SavePng(byte[] bgra, int w, int h, string path, int channel)
                {
                    using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                    var buf = new byte[w * h * 4];
                    for (int i = 0; i < w * h; i++)
                        if (channel < 0) { buf[i * 4] = bgra[i * 4]; buf[i * 4 + 1] = bgra[i * 4 + 1]; buf[i * 4 + 2] = bgra[i * 4 + 2]; buf[i * 4 + 3] = 255; }
                        else { byte v = bgra[i * 4 + channel]; buf[i * 4] = buf[i * 4 + 1] = buf[i * 4 + 2] = v; buf[i * 4 + 3] = 255; }
                    System.Runtime.InteropServices.Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
                    bmp.UnlockBits(bd);
                    bmp.Save(path);
                }
            }
            case "--anim-mesh-probe":
            {
                // Read-only: animations for a mod's first mesh, and a few posed at mid-length (how far vertices move).
                var am = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (am == null) { Console.WriteLine("--anim-mesh-probe <mod> [animation name part...]"); return 1; }
                string? agr = settings.ResolvedGameRoot(data);
                string? ac = agr != null && Settings.IsGameRoot(agr) ? Settings.Cooked(agr) : null;
                var mr = ModMeshes.List(am).FirstOrDefault();
                if (mr == null) { Console.WriteLine("no meshes"); return 1; }
                var ld = ModMeshes.Load(mr, ac, out string awhy);
                if (ld == null) { Console.WriteLine("mesh: " + awhy); return 1; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var anims = ModAnimations.For(mr, ld.Bones, am.Manifest.UpkReplacements.Select(f => (f, Path.Combine(am.Folder, f))), ac);
                Console.WriteLine($"{mr.Name}: {ld.Bones.Count} bones, {anims.Count} animation(s) ({sw.ElapsedMilliseconds} ms) from {string.Join(", ", anims.Select(a => a.Package).Distinct())}");
                Console.WriteLine("  first: " + string.Join(", ", anims.Take(12).Select(a => a.Name)));
                var anim8 = new MeshAnimator(ld.Bones, ld.Positions, ld.Normals, ld.Influences);
                anim8.Pose(null, 0);
                float restErr = ld.Positions.Select((p, i) => Vector3.Distance(p, anim8.Positions[i])).Max();
                Console.WriteLine($"  rest pose through the skinning: max vertex error {restErr:0.000} units");
                var wanted = rest.Skip(2).ToList();
                foreach (var a in anims.Where(a => wanted.Count == 0 ? true : wanted.Any(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(4))
                {
                    var ba = ModAnimations.Load(a);
                    if (ba == null) { Console.WriteLine($"  {a.Name}: can't be read"); continue; }
                    var (frames, secs) = MeshAnimator.Span(ba);
                    int covered = ld.Bones.Count(b => ba.Tracks.ContainsKey(b.Name));
                    anim8.Pose(ba, frames / 2);
                    var d = ld.Positions.Select((p, i) => Vector3.Distance(p, anim8.Positions[i])).ToList();
                    Vector3 lo = anim8.Positions.Aggregate(Vector3.Min), hi = anim8.Positions.Aggregate(Vector3.Max);
                    Console.WriteLine($"  {a.Name}: {frames:0} frames, {secs:0.00} s, tracks for {covered} of {ld.Bones.Count} bones; mid-frame moves vertices avg {d.Average():0.0} / max {d.Max():0.0}; size {hi.X - lo.X:0} x {hi.Y - lo.Y:0} x {hi.Z - lo.Z:0}");
                }
                return 0;
            }
            case "--foot-check":
            {
                // Read-only: how high a mod's mesh stands: the lowest vertex and the pelvis, at rest and over animations (default idle, run).
                // MHO_FOOT_MESH = part of a mesh name (a cross-hero move keeps the target's stock mesh first); MHO_FOOT_CHAIN=1 prints the pelvis's parents.
                var fm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (fm == null) { Console.WriteLine("--foot-check <mod> [animation name part...]"); return 1; }
                string? fgr = settings.ResolvedGameRoot(data);
                string? fc = fgr != null && Settings.IsGameRoot(fgr) ? Settings.Cooked(fgr) : null;
                var fr = (Environment.GetEnvironmentVariable("MHO_FOOT_MESH") is { Length: > 0 } fmn ? ModMeshes.List(fm).FirstOrDefault(r => r.Name.Contains(fmn, StringComparison.OrdinalIgnoreCase)) : null) ?? ModMeshes.List(fm).FirstOrDefault();
                if (fr == null) { Console.WriteLine("no meshes"); return 1; }
                var fl = ModMeshes.Load(fr, fc, out string fwhy);
                if (fl == null) { Console.WriteLine("mesh: " + fwhy); return 1; }
                var fa = new MeshAnimator(fl.Bones, fl.Positions, fl.Normals, fl.Influences);
                int pelvis = fa.BoneIndex("g_pelvis"), froot = 0;
                void Show(string what)
                {
                    float lo = fa.Positions.Min(p => p.Z), hi = fa.Positions.Max(p => p.Z);
                    Console.WriteLine($"  {what,-34} lowest {lo,8:0.0}  top {hi,7:0.0}  pelvis z {(pelvis >= 0 ? fa.BonePosition(pelvis).Z : float.NaN),7:0.0}  root z {fa.BonePosition(froot).Z,6:0.0}");
                }
                Console.WriteLine($"{fr.Name} in {fr.Package}: {fl.Bones.Count} bones, root {fl.Bones[0].Name}");
                fa.Pose(null, 0); Show("rest pose");
                var fanims = ModAnimations.For(fr, fl.Bones, fm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(fm.Folder, f))), fc);
                var fw = rest.Skip(2).ToList(); if (fw.Count == 0) fw = ["idle", "run"];
                foreach (var a in fanims.Where(a => fw.Any(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(6))
                {
                    var ba = ModAnimations.Load(a); if (ba == null) continue;
                    var tb = ModAnimations.TranslationBones(ba);
                    fa.Pose(ba, 0); Show($"{a.Name} f0 ({a.Package})");
                    var (fN, _) = MeshAnimator.Span(ba); float minLo = float.MaxValue, maxLo = float.MinValue;
                    for (float f = 0; f <= fN; f += Math.Max(1, fN / 40)) { fa.Pose(ba, f); float lo = fa.Positions.Min(p => p.Z); minLo = Math.Min(minLo, lo); maxLo = Math.Max(maxLo, lo); }
                    Console.WriteLine($"      over the animation the lowest point runs {minLo:0.0} .. {maxLo:0.0}");
                    if (pelvis >= 0 && Environment.GetEnvironmentVariable("MHO_FOOT_CHAIN") == "1")
                        for (int c = pelvis, guard = 0; c >= 0 && guard++ < 64; c = c == 0 ? -1 : fl.Bones[c].ParentIndex)
                        {
                            var cb = fl.Bones[c]; ba.Tracks.TryGetValue(cb.Name, out var ct);
                            Console.WriteLine($"      chain {cb.Name,-20} rest pos {cb.Position}  track pos {(ct != null && ct.PositionKeys.Count > 0 ? ct.PositionKeys[0].Position.ToString() : "-")} ({ct?.PositionKeys.Count ?? 0} keys)  translation bone: {(tb == null || tb.Contains(cb.Name) ? "yes" : "no")}");
                        }
                    if (pelvis >= 0 && ba.Tracks.TryGetValue("g_pelvis", out var pt) && pt.PositionKeys.Count > 0)
                        Console.WriteLine($"      g_pelvis track position {pt.PositionKeys[0].Position}  (mesh rest {fl.Bones[pelvis].Position}); positions applied: {(tb == null ? "all" : tb.Contains("g_pelvis") ? "yes" : "no")}");
                }
                return 0;
            }
            case "--anim-probe":
            {
                // Read-only: the AnimSets in packages (paths), with their sequence and bone counts.
                foreach (string path in rest.Skip(1))
                {
                    try
                    {
                        var ap = AnimExportCli.Packages.Package.Open(path);
                        var sets = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).ToList();
                        Console.WriteLine($"{Path.GetFileName(path)}: {sets.Count} AnimSet(s), {ap.FindExportsOfClass("skeletalmesh").Count()} skeletal mesh(es)");
                        foreach (var s in sets.Take(8)) Console.WriteLine($"  {ap.GetExportName(s.ExportIndex)}: {s.Sequences.Count} sequences, {s.TrackBoneNames.Count} bones; " + (s.RotationOnly ? $"rotation-only, positions for {s.TranslationBones?.Count ?? 0}: {string.Join(" ", s.TranslationBones ?? [])}" : "positions for every bone"));
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
                }
                return 0;
            }
            case "--mesh-probe":
            {
                // Read-only: the mod's skeletal meshes and whether each loads with textures (the preview's 3D view).
                var pm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (pm == null) { Console.WriteLine("--mesh-probe <mod>"); return 1; }
                string? pgr = settings.ResolvedGameRoot(data);
                string? cooked = pgr != null && Settings.IsGameRoot(pgr) ? Settings.Cooked(pgr) : null;
                var meshes = ModMeshes.List(pm);
                Console.WriteLine($"{pm.Name}: {meshes.Count} skeletal mesh(es)");
                foreach (var r in meshes)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var loaded = ModMeshes.Load(r, cooked, out string why);
                    Console.WriteLine($"  {r.Package} | {r.Name}: {(loaded == null ? "can't be read: " + why : loaded.Info)}  ({sw.ElapsedMilliseconds} ms)");
                    if (loaded != null && rest.Contains("--bones"))
                    {
                        // Which bones the mesh has, and where its vertices are (for props: does it share the character's skeleton?).
                        var used = new HashSet<int>(loaded.Influences.SelectMany(i => i.Bones ?? []));
                        Vector3 lo = loaded.Positions.Aggregate(Vector3.Min), hi = loaded.Positions.Aggregate(Vector3.Max);
                        Console.WriteLine($"    {loaded.Bones.Count} bones, {used.Count} weighted; bounds {lo.X:0},{lo.Y:0},{lo.Z:0} .. {hi.X:0},{hi.Y:0},{hi.Z:0}");
                        Console.WriteLine("    weighted: " + string.Join(", ", used.OrderBy(i => i).Take(12).Select(i => loaded.Bones[i].Name)));
                        Console.WriteLine("    first bones: " + string.Join(", ", loaded.Bones.Take(8).Select(b => b.Name)));
                        Console.WriteLine("    unweighted: " + string.Join(", ", loaded.Bones.Select((b, i) => (b, i)).Where(x => !used.Contains(x.i)).Select(x => x.b.Name)));
                    }
                }
                return 0;
            }
        }
        return null;
    }
}
