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
            case "--add-prop":
            {
                // --add-prop <costume.upk> <from.upk> <prop class> <out.upk> [size movex movey movez turnx turny turnz]: the prop added
                // (PowerBorrow.AddProp), applied twice (the second must change nothing), written to <out> (never into the game)
                if (rest.Count < 5) { Console.WriteLine("--add-prop <costume.upk> <from.upk> <prop class> <out.upk> [size mx my mz tx ty tz]"); return 1; }
                float F(int i, float dflt) => rest.Count > i ? float.Parse(rest[i], System.Globalization.CultureInfo.InvariantCulture) : dflt;
                var ent = new AddedPropEntry { Package = Path.GetFileName(rest[1]), From = Path.GetFileName(rest[2]), Class = rest[3],
                    Fit = new PropFit { Class = rest[3], Scale = rest.Count > 5 ? F(5, 1) : null, X = F(6, 0), Y = F(7, 0), Z = F(8, 0), TurnX = F(9, 0), TurnY = F(10, 0), TurnZ = F(11, 0), PinPivot = Environment.GetEnvironmentVariable("MHO_PIN") == "1" } };
                var alog = new List<string>();
                byte[] once = PowerBorrow.AddProp(File.ReadAllBytes(rest[1]), ent, rest[2], alog);
                byte[] twice = PowerBorrow.AddProp(once, ent, rest[2], alog);
                alog.ForEach(Console.WriteLine);
                var a1 = ModMeshes.AttachmentClasses(WriteTemp(once)); var a2 = ModMeshes.AttachmentClasses(WriteTemp(twice));
                Console.WriteLine($"props once: {string.Join(", ", a1 ?? [])}; again: {string.Join(", ", a2 ?? [])} ({once.Length:N0} / {twice.Length:N0} bytes)");
                MhoExtendedModManager.Model.Protected.CheckWrite(rest[4]);
                File.WriteAllBytes(rest[4], twice);
                return 0;
                static string WriteTemp(byte[] b) { string t = Path.Combine(Path.GetTempPath(), "mhoextmm_addprop_" + Guid.NewGuid().ToString("N")[..8] + ".upk"); File.WriteAllBytes(t, b); return t; }
            }
            case "--remove-prop":
            {
                // --remove-prop <costume.upk> <prop class> <out.upk>: PowerBorrow.RemoveProp, written to <out> (never into the game)
                if (rest.Count < 4) { Console.WriteLine("--remove-prop <costume.upk> <prop class> <out.upk>"); return 1; }
                var rlog = new List<string>();
                byte[] rb = PowerBorrow.RemoveProp(File.ReadAllBytes(rest[1]), rest[2], rlog);
                rlog.ForEach(Console.WriteLine);
                MhoExtendedModManager.Model.Protected.CheckWrite(rest[3]);
                File.WriteAllBytes(rest[3], rb);
                return 0;
            }
            case "--attach-classes":
            {
                // Read-only: each package's player class default's mAttachmentClasses (the props it always has), or "none".
                foreach (string f in rest.Skip(1)) Console.WriteLine($"{Path.GetFileName(f)}: {(ModMeshes.AttachmentClasses(f) is { } l ? string.Join(", ", l) : "no list of its own")}");
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
                // --mod <name>: the mod's packages first (a borrowed power's animations)
                int pmAt = rest.IndexOf("--mod");
                var pmod = pmAt >= 0 && pmAt + 1 < rest.Count ? lib.Find(rest[pmAt + 1]) : null;
                if (pmAt >= 0) rest.RemoveRange(pmAt, Math.Min(2, rest.Count - pmAt));
                var idx = Fx.PowerIndex.For(rest[1], pcook, pmod == null ? [] : pmod.Manifest.UpkReplacements.Select(f => Path.Combine(pmod.Folder, f)));
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
            case "--normal-check":
            {
                // Read-only: every normal-map slot (a texture parameter named *normal*) of the materials in a mod's packages (or a
                // .upk / folder), with its texture's sRGB, compression and unpack settings. The game's own normal maps have sRGB
                // off, UnpackMin -1 and TC_NormalMap; one read as sRGB shades wrong and shows seams. --normal-check <mod | path | all>
                if (rest.Count < 2) { Console.WriteLine("--normal-check <mod | package.upk | folder | all>"); return 1; }
                var files = new List<string>();
                if (rest[1].Equals("all", StringComparison.OrdinalIgnoreCase)) foreach (var mm in lib.Mods) files.AddRange(Directory.GetFiles(mm.Folder, "*.upk"));
                else if (lib.Find(rest[1]) is { } fm) files.AddRange(Directory.GetFiles(fm.Folder, "*.upk"));
                else if (Directory.Exists(rest[1])) files.AddRange(Directory.GetFiles(rest[1], "*.upk"));
                else if (File.Exists(rest[1])) files.Add(rest[1]);
                else { Console.WriteLine("no such mod or file: " + rest[1]); return 1; }
                int good = 0, bad = 0;
                foreach (string f in files.Where(x => !Path.GetFileName(x).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(x).Contains("copy", StringComparison.OrdinalIgnoreCase)))
                {
                    MhoPackageModifier.Package pkg;
                    try { pkg = MhoPackageModifier.Package.Open(f); } catch (Exception ex) { Console.WriteLine($"{f}: {ex.Message}"); continue; }
                    var seen = new HashSet<int>();
                    for (int i = 0; i < pkg.Exports.Length; i++)
                    {
                        if (!pkg.ClassOf(pkg.Exports[i]).Equals("MaterialInstanceConstant", StringComparison.OrdinalIgnoreCase)) continue;
                        List<MhoPackageModifier.MaterialTexture> texs;
                        try { texs = MhoPackageModifier.TextureExport.MaterialTextures(pkg, i + 1, []); } catch (Exception) { continue; }
                        foreach (var t in texs.Where(t => t.Parameter.Contains("normal", StringComparison.OrdinalIgnoreCase) && seen.Add(t.ExportIndex)))
                        {
                            var props = MhoPackageModifier.PropertyEdit.ReadProperties(pkg, t.ExportIndex) ?? [];
                            string Val(string n) => props.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value ?? "(default)";
                            string srgb = props.FirstOrDefault(x => x.Name.Equals("srgb", StringComparison.OrdinalIgnoreCase)) is { } sp ? sp.Value : "(default: true)";
                            bool ok = srgb.Contains("false", StringComparison.OrdinalIgnoreCase);
                            if (ok) good++; else bad++;
                            Console.WriteLine($"{(ok ? "ok " : "BAD")}  {Path.GetFileName(f)}  {pkg.Exports[i].ObjectName}.{t.Parameter} -> {pkg.PathOf(pkg.Exports[t.ExportIndex])}: srgb {srgb}, compression {Val("compressionsettings")}, unpackmin {Val("unpackmin")}, lodgroup {Val("lodgroup")}");
                        }
                    }
                }
                Console.WriteLine($"{good} normal map(s) with sRGB off, {bad} read as sRGB");
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
            case "--fx-component":
            {
                // --fx-component <package> <component path part> (read only): a power / projectile effect component's properties
                if (rest.Count < 3) { Console.WriteLine("--fx-component <package> <path part>"); return 1; }
                var cp = MhoPackageModifier.Package.Open(rest[1]);
                for (int i = 0; i < cp.Exports.Length; i++)
                {
                    string path = cp.PathOf(cp.Exports[i]);
                    if (!path.Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] b = cp.ReadExportBytes(cp.Exports[i]).ToArray();
                    Console.WriteLine($"{path} ({cp.ClassOf(cp.Exports[i])})");
                    foreach (var t in MhoPackageModifier.TagWalker.Walk(cp, b, 16) ?? [])
                    {
                        string v = t.Type.ToLowerInvariant() switch
                        {
                            "nameproperty" or "byteproperty" => MhoPackageModifier.TagWalker.NameAt(cp, b, t.ValueAt) ?? "?",
                            "boolproperty" => b[t.ValueAt - 1].ToString(),
                            "floatproperty" => BitConverter.ToSingle(b, t.ValueAt).ToString("0.###"),
                            "intproperty" => BitConverter.ToInt32(b, t.ValueAt).ToString(),
                            "objectproperty" => BitConverter.ToInt32(b, t.ValueAt) is int r && r > 0 ? cp.PathOf(cp.Exports[r - 1]) : r.ToString(),
                            _ => $"({t.Size} bytes)",
                        };
                        Console.WriteLine($"    {t.Name} {t.Type} = {v}");
                    }
                }
                return 0;
            }
            case "--anim-lengths":
            {
                // --anim-lengths <package> [name part] (read only): every AnimSequence's name, length (seconds), frames and play rate
                if (rest.Count < 2) { Console.WriteLine("--anim-lengths <package> [name part]"); return 1; }
                var lp = AnimExportCli.Packages.Package.Open(rest[1]);
                string part = rest.Count > 2 ? rest[2] : "";
                foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(lp))
                    foreach (var seq in set.Sequences.Where(q => q.IsExport))
                    {
                        string n = AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(lp, seq.ExportIndex);
                        if (part.Length > 0 && !n.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                        var pr = lp.TryReadProperties(seq.ExportIndex);
                        Console.WriteLine($"{lp.GetExportName(set.ExportIndex)}	{n}	{pr?.GetFloat("SequenceLength"):0.###} s	{pr?.GetInt("NumFrames")} frames	rate {(pr != null && pr.Find("RateScale") != null ? pr.GetFloat("RateScale") : 1):0.###}");
                    }
                return 0;
            }
            case "--power-borrow-census":
            {
                // --power-borrow-census <report.txt> [--hero <target hero>] [--donor <donor hero>] [--per-hero N] [--keep-timing | --both]
                //   [--threads N] [--seed S]   (read only: builds in memory from the stock copies, writes only the report)
                // Borrows built for many pairs (each hero power with an animation as a target; donors: every power of --donor,
                // else one random power of another hero), sorted into built / built with notes / failed (Kurt, 2026-10-10:
                // three bugs showed only on new pairs). In-game problems (a freeze) aren't seen here; the report picks pairs to play.
                if (rest.Count < 2) { Console.WriteLine("--power-borrow-census <report.txt> [--hero H] [--donor H] [--per-hero N] [--keep-timing | --both] [--threads N] [--seed S]"); return 1; }
                string? cgr = settings.ResolvedGameRoot(data);
                string? ccook = cgr != null && Settings.IsGameRoot(cgr) ? Settings.Cooked(cgr) : null;
                if (ccook == null) { Console.WriteLine("game folder not found"); return 1; }
                string? Opt(string name) { int i = rest.IndexOf(name); return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null; }
                string? onlyHero = Opt("--hero"), onlyDonor = Opt("--donor");
                int perHero = int.TryParse(Opt("--per-hero"), out int ph) ? ph : int.MaxValue;
                int threads = int.TryParse(Opt("--threads"), out int th0) ? th0 : Math.Max(1, Environment.ProcessorCount / 2);
                var rng = new Random(int.TryParse(Opt("--seed"), out int seed) ? seed : 1);
                var modes = rest.Contains("--both") ? new[] { false, true } : rest.Contains("--keep-timing") ? new[] { true } : new[] { false };
                var cdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(cgr!, "Data", "Game", "Calligraphy.sip")));
                _ = cdb.Assets;   // built once, before the threads
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var heroes = Directory.EnumerateFiles(ccook, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                    .Where(f => f.Split('_').Length == 5 && !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Split('_')[3]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
                var powers = new Dictionary<string, List<Fx.PowerList.Power>>(StringComparer.OrdinalIgnoreCase);
                foreach (string h in heroes)
                {
                    try { var l = Fx.PowerList.For(cdb, h, ccook, []).Where(p => p.Animations.Count > 0).ToList(); if (l.Count > 0) powers[h] = l; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException) { Console.WriteLine($"  {h}: powers not read ({ex.Message})"); }
                }
                Console.WriteLine($"{powers.Count} heroes, {powers.Values.Sum(l => l.Count)} powers with an animation ({sw.Elapsed.TotalSeconds:0} s)");
                var pairs = new List<(string TH, Fx.PowerList.Power T, string DH, Fx.PowerList.Power D, bool Keep)>();
                foreach (var (h, list) in powers.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (onlyHero != null && !h.Equals(onlyHero, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var t in list.Take(perHero))
                    {
                        var donors = new List<(string, Fx.PowerList.Power)>();
                        if (onlyDonor != null) { if (powers.TryGetValue(onlyDonor, out var dl)) donors.AddRange(dl.Select(d => (onlyDonor, d))); }
                        else
                        {
                            var others = powers.Keys.Where(k => !k.Equals(h, StringComparison.OrdinalIgnoreCase)).ToList();
                            string dh = others[rng.Next(others.Count)];
                            donors.Add((dh, powers[dh][rng.Next(powers[dh].Count)]));
                        }
                        foreach (var (dh, d) in donors) foreach (bool keep in modes) pairs.Add((h, t, dh, d, keep));
                    }
                }
                Console.WriteLine($"{pairs.Count} pair(s), {threads} thread(s)");
                var results = new (string Status, string Detail, List<string> Notes, int Packages, int Costumes, int Anims)[pairs.Count];
                var fxLocal = new ThreadLocal<Fx.FxGame>(() => new Fx.FxGame(ccook, []));
                int doneCount = 0;
                Parallel.For(0, pairs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
                {
                    var (thName, t, dhName, d, keep) = pairs[i];
                    var log = new List<string>();
                    try
                    {
                        var plan = PowerBorrow.Plan(cdb, fxLocal.Value!, t.Prototype, d.Prototype, log);
                        var tp = plan.FirstOrDefault(x => x.Role == "power") ?? throw new InvalidDataException("no power pair in the plan");
                        string th = PowerBorrow.HeroPackage(ccook, thName, t.Prototype, tp.TargetClass, []) ?? throw new InvalidDataException("target hero package not found");
                        string dh = PowerBorrow.HeroPackage(ccook, dhName, d.Prototype, tp.DonorClass, []) ?? throw new InvalidDataException("donor hero package not found");
                        var built = PowerBorrow.Build(plan, StockFiles.For(ccook, $"UC__MarvelPlayer_{th}_SF.upk"), $"MarvelPlayer_{th}",
                            StockFiles.For(ccook, $"UC__MarvelPlayer_{dh}_SF.upk"), log, true, keep, f => StockFiles.For(ccook, f), ccook);
                        foreach (var (f, bytes) in built) _ = MhoPackageModifier.Package.FromBytes(bytes);   // every package reads back
                        var notes = log.Where(l => l.Contains("left out") || l.Contains("not found") || l.Contains("nothing of the donor's") || l.Contains("plays no animation")).Select(l => l.Trim()).ToList();
                        results[i] = (notes.Count > 0 ? "notes" : "ok", "", notes, built.Count, log.Count(l => l.Contains("own animation list")), log.Count(l => l.StartsWith("anims: ") && l.Contains(" s")));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or KeyNotFoundException or MhoPackageModifier.PackageFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or NullReferenceException)
                    {
                        results[i] = ("failed", $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}", [], 0, 0, 0);
                    }
                    int n = Interlocked.Increment(ref doneCount);
                    if (n % 25 == 0) Console.WriteLine($"  {n} of {pairs.Count} ({sw.Elapsed.TotalSeconds:0} s)");
                });
                // the report
                var rep = new List<string> { $"Power borrow census, {DateTime.Now:yyyy-MM-dd HH:mm}, {pairs.Count} pair(s) in {sw.Elapsed.TotalMinutes:0.0} min", "" };
                string Pair(int i) { var p = pairs[i]; return $"{p.TH}: {p.T.Name} ← {p.DH}: {p.D.Name} ({(p.Keep ? "keep timing" : "whole power")})  [{p.T.Prototype} ← {p.D.Prototype}]"; }
                int ok = results.Count(r => r.Status == "ok"), nt = results.Count(r => r.Status == "notes"), fl = results.Count(r => r.Status == "failed");
                rep.Add($"Built: {ok}   Built with notes: {nt}   Failed: {fl}");
                rep.Add("");
                // failures and notes grouped by their message with names taken out
                static string Shape(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"[A-Za-z0-9_]*[0-9_][A-Za-z0-9_.]*", "…");
                rep.Add("FAILED, by reason");
                foreach (var g in Enumerable.Range(0, pairs.Count).Where(i => results[i].Status == "failed").GroupBy(i => Shape(results[i].Detail)).OrderByDescending(g => g.Count()))
                {
                    rep.Add($"  {g.Count()} × {g.Key}");
                    foreach (int i in g.Take(3)) rep.Add($"      {Pair(i)}\n        {results[i].Detail}");
                }
                rep.Add("");
                rep.Add("NOTES, by kind");
                foreach (var g in Enumerable.Range(0, pairs.Count).SelectMany(i => results[i].Notes.Select(n => (i, n))).GroupBy(x => Shape(x.n)).OrderByDescending(g => g.Count()))
                {
                    rep.Add($"  {g.Count()} × {g.Key}");
                    foreach (var (i, n) in g.Take(2)) rep.Add($"      {Pair(i)}\n        {n}");
                }
                rep.Add("");
                // pairs worth playing: one or two of each kind the build can't judge
                rep.Add("TO PLAY IN GAME (picked from the built ones)");
                void Pick(string what, Func<int, bool> when, int n = 2)
                {
                    var hit = Enumerable.Range(0, pairs.Count).Where(i => results[i].Status != "failed" && when(i)).Take(n).ToList();
                    rep.Add($"  {what}: {(hit.Count == 0 ? "none in this run" : "")}");
                    foreach (int i in hit) rep.Add($"      {Pair(i)}");
                }
                Pick("whole power, costumes with their own animation list changed", i => !pairs[i].Keep && results[i].Costumes > 0);
                Pick("whole power, a combo target (several animations)", i => !pairs[i].Keep && pairs[i].T.Animations.Count > 2);
                Pick("whole power, a combo donor onto a single-animation power", i => !pairs[i].Keep && pairs[i].D.Animations.Count > 2 && pairs[i].T.Animations.Count == 1);
                Pick("whole power, no animation borrowed (the donor plays none)", i => !pairs[i].Keep && results[i].Anims == 0);
                Pick("keep timing", i => pairs[i].Keep);
                Pick("built with notes (something left out)", i => results[i].Status == "notes");
                rep.Add("");
                rep.Add("EVERY PAIR");
                for (int i = 0; i < pairs.Count; i++)
                    rep.Add($"  {results[i].Status,-7} {Pair(i)}{(results[i].Status == "failed" ? "\n          " + results[i].Detail : $"  ({results[i].Packages} package(s), {results[i].Costumes} costume list(s), {results[i].Anims} animation(s))")}");
                File.WriteAllLines(rest[1], rep);
                Console.WriteLine($"Built: {ok}   Built with notes: {nt}   Failed: {fl}   ({sw.Elapsed.TotalMinutes:0.0} min) → {rest[1]}");
                return 0;
            }
            case "--class-survey":
            {
                // Read-only: every compiled script class (Class export) in the stock packages matching a pattern, parsed with MPM's
                // ClassData: how many read to their end, and how many carry bytecode, children (properties / functions), a
                // function map or interfaces (what copying a class must handle). --class-survey [file pattern, default UC__Power*_SF.upk]
                string? sgr = settings.ResolvedGameRoot(data);
                if (sgr == null || !Settings.IsGameRoot(sgr)) { Console.WriteLine("game folder not found"); return 1; }
                string folder = UniqueNames.Folder(Settings.Cooked(sgr));
                string pattern = rest.Count > 1 ? rest[1] : "UC__Power*_SF.upk";
                var files = Directory.GetFiles(folder, pattern).Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
                int classes = 0, ok = 0, script = 0, children = 0, funcs = 0, ifaces = 0;
                var fails = new Dictionary<string, int>(); var scriptEx = new List<string>(); var childEx = new List<string>();
                var bag = new System.Collections.Concurrent.ConcurrentBag<(string Name, MhoPackageModifier.ClassData? C, string? Err)>();
                var funcBag = new System.Collections.Concurrent.ConcurrentBag<(string Name, int Size)>();
                var fieldBag = new System.Collections.Concurrent.ConcurrentBag<(string Class, string Name, string? Err, int Tokens)>();
                Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, f =>
                {
                    try
                    {
                        var pk = MhoPackageModifier.Package.Open(f);
                        MhoPackageModifier.ScriptCode.UseNoneName(Array.FindIndex(pk.Names, n => n.Equals("None", StringComparison.OrdinalIgnoreCase)));
                        foreach (var e in pk.Exports)
                        {
                            // a script class's fields: functions (with their code) and properties
                            string fc = pk.ClassOf(e);
                            if (fc.Equals("Function", StringComparison.OrdinalIgnoreCase) || fc.EndsWith("Property", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] fd = pk.ReadExportBytes(e).ToArray();
                                string? ferr;
                                var fr = fc.Equals("Function", StringComparison.OrdinalIgnoreCase) ? MhoPackageModifier.ScriptCode.Function(fd, out ferr) : MhoPackageModifier.ScriptCode.Property(fd, fc, out ferr);
                                fieldBag.Add((fc, Path.GetFileName(f) + " " + pk.PathOf(e), fr == null ? ferr : null, fr?.Tokens ?? 0));
                            }
                            if (!pk.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)) continue;
                            var c = MhoPackageModifier.ClassData.Parse(pk.ReadExportBytes(e).ToArray(), out string? err);
                            bag.Add((Path.GetFileName(f) + " " + e.ObjectName, c, err));
                            if (c is { Children: not 0 })
                            {
                                // its functions: name, size and a hash of the bytes (do copies in other packages match?)
                                string cp = pk.PathOf(e) + ".";
                                var fn = pk.Exports.Where(x => pk.ClassOf(x).Equals("Function", StringComparison.OrdinalIgnoreCase) && pk.PathOf(x).StartsWith(cp, StringComparison.OrdinalIgnoreCase)).ToList();
                                foreach (var fx in fn) funcBag.Add((e.ObjectName + "." + fx.ObjectName, fx.SerialSize));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                });
                foreach (var (name, c, err) in bag)
                {
                    classes++;
                    if (c == null) { string k = (err ?? "?").Split(" (at")[0]; fails[k] = fails.GetValueOrDefault(k) + 1; if (fails[k] <= 2) Console.WriteLine("  fail: " + name + ": " + err); continue; }
                    ok++;
                    if (c.ScriptStorage > 0) { script++; if (scriptEx.Count < 5) scriptEx.Add($"{name} ({c.ScriptStorage} bytes)"); }
                    if (c.Children != 0) { children++; if (childEx.Count < 5) childEx.Add(name); }
                    if (c.Functions > 0) funcs++;
                    if (c.Interfaces > 0) ifaces++;
                }
                Console.WriteLine($"{files.Count} packages, {classes} classes: {ok} read to their end; bytecode {script}, children {children}, functions {funcs}, interfaces {ifaces}");
                foreach (var (k, n) in fails.OrderByDescending(x => x.Value)) Console.WriteLine($"  {n} × {k}");
                Console.WriteLine("  with bytecode: " + string.Join("; ", scriptEx));
                Console.WriteLine("  with children: " + string.Join("; ", childEx));
                foreach (var g in fieldBag.GroupBy(x => x.Class, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  {g.Key}: {g.Count()}, {g.Count(x => x.Err == null)} read to their end{(g.Key.Equals("Function", StringComparison.OrdinalIgnoreCase) ? $" ({g.Sum(x => x.Tokens)} code tokens)" : "")}");
                foreach (var g in fieldBag.Where(x => x.Err != null).GroupBy(x => x.Err!.Split(" (at")[0]).OrderByDescending(g => g.Count()).Take(8))
                    Console.WriteLine($"    {g.Count()} × {g.Key}: e.g. {g.First().Name}: {g.First().Err}");
                foreach (var g in funcBag.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  function {g.Key}: in {g.Count()} packages, sizes {string.Join("/", g.Select(x => x.Size).Distinct())}");
                return 0;
            }
            case "--borrow-check":
            {
                // Read-only: what the borrow window says for a pair: its suggestion, what Keep This Power's Timing would change,
                // and enemy powers sharing the target's classes. --borrow-check <target power prototype> <donor power prototype>
                if (rest.Count < 3) { Console.WriteLine("--borrow-check <target power> <donor power>"); return 1; }
                string? kgr = settings.ResolvedGameRoot(data);
                if (kgr == null || !Settings.IsGameRoot(kgr)) { Console.WriteLine("game folder not found"); return 1; }
                var kdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(kgr, "Data", "Game", "Calligraphy.sip")));
                var kg = new Fx.FxGame(Settings.Cooked(kgr), []);
                var (keep, why) = PowerBorrow.Suggest(kdb, kg, rest[1], rest[2]);
                Console.WriteLine($"suggested: {(keep ? "keep this power's timing" : "whole power")}: {why}");
                Console.WriteLine($"keep timing changes: {PowerBorrow.KeepTimingTakes(kdb, kg, rest[1], rest[2]) ?? "nothing"}");
                var en = PowerBorrow.EnemyUsers(kdb, kg, kgr, rest[1], rest[2], 20);
                Console.WriteLine($"enemy powers sharing the target's classes: {(en.Count == 0 ? "none" : string.Join(", ", en))}");
                var tk = PowerBorrow.KindOf(kdb, kg, rest[1]); var dk = PowerBorrow.KindOf(kdb, kg, rest[2]);
                Console.WriteLine($"kinds: target {tk?.Label ?? "?"}, donor {dk?.Label ?? "?"}");
                Console.WriteLine($"warning: {PowerBorrow.Mismatch(tk, dk, Path.GetFileNameWithoutExtension(rest[1]), Path.GetFileNameWithoutExtension(rest[2])) ?? "none (built alike)"}");
                return 0;
            }
            case "--power-borrow":
            {
                // --power-borrow <out dir> <target pkg>|<target default>=<donor pkg>|<donor default> ... [--anims <hero pkg> <hero class> <donor hero pkg>]
                // (writes only into <out dir>; the game's packages are read from the stock copies): each target default's
                // components become copies of the donor's, and with --anims the donor's power animations go into the hero's package
                if (rest.Count < 3) { Console.WriteLine("--power-borrow <out dir> <target pkg>|<target default>=<donor pkg>|<donor default> ... [--anims <hero pkg> <hero class> <donor hero pkg>]"); return 1; }
                string? bgr = settings.ResolvedGameRoot(data);
                string? bcook = bgr != null && Settings.IsGameRoot(bgr) ? Settings.Cooked(bgr) : null;
                if (bcook == null) { Console.WriteLine("game folder not found"); return 1; }
                string outDir = rest[1];
                Directory.CreateDirectory(outDir);
                string Stock(string f) => File.Exists(f) ? f : StockFiles.For(bcook, f.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? f : f + ".upk");
                // the automatic form: two power prototypes (Powers\Player\Iceman\RapidFire.prototype …); --no-fit keeps the donor's speed
                if (rest[2].EndsWith(".prototype", StringComparison.OrdinalIgnoreCase) && rest.Count > 3)
                {
                    var plog = new List<string>();
                    try
                    {
                        var pdb2 = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(bgr!, "Data", "Game", "Calligraphy.sip")));
                        var pgame = new Fx.FxGame(bcook, []);
                        string tp = rest[2].Replace('/', '\\'), dp = rest[3].Replace('/', '\\');
                        var plan = PowerBorrow.Plan(pdb2, pgame, tp, dp, plog);
                        var ppw = plan.FirstOrDefault(x => x.Role == "power");
                        string th = PowerBorrow.HeroPackage(bcook, null, tp, ppw?.TargetClass, ppw == null ? [] : PowerBorrow.AnimNames(ppw.TargetFile, $"marvelgamecontent.default__{ppw.TargetClass}".ToLowerInvariant()))
                            ?? throw new InvalidDataException("the target power's hero package wasn't found");
                        string dh = PowerBorrow.HeroPackage(bcook, null, dp, ppw?.DonorClass, ppw == null ? [] : PowerBorrow.AnimNames(ppw.DonorFile, $"marvelgamecontent.default__{ppw.DonorClass}".ToLowerInvariant()))
                            ?? throw new InvalidDataException("the donor power's hero package wasn't found");
                        plog.Add($"hero packages: UC__MarvelPlayer_{th}_SF ← UC__MarvelPlayer_{dh}_SF");
                        var pspawn = rest.Contains("--keep-timing") ? [] : PowerBorrow.ConditionAttachments(pdb2, pgame, dp, Stock($"UC__MarvelPlayer_{dh}_SF.upk"), plog);
                        // --rider <forward>,<up>,<lean degrees> (Vehicle Fit), --vehicle-scale <n>
                        int ri = rest.IndexOf("--rider"), vi = rest.IndexOf("--vehicle-scale");
                        var rf = ri >= 0 && ri + 1 < rest.Count ? rest[ri + 1].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
                        var prider = rf is { Length: 3 } ? new AnimSwap.RiderFit(rf[0], rf[1], rf[2]) : null;
                        float? pvs = vi >= 0 && vi + 1 < rest.Count ? float.Parse(rest[vi + 1], System.Globalization.CultureInfo.InvariantCulture) : null;
                        var built = PowerBorrow.Build(plan, Stock($"UC__MarvelPlayer_{th}_SF.upk"), $"MarvelPlayer_{th}", Stock($"UC__MarvelPlayer_{dh}_SF.upk"), plog, fit: !rest.Contains("--no-fit"), keepTiming: rest.Contains("--keep-timing"), cooked: bcook, spawn: pspawn,
                            vehicleScale: pvs, rider: prider is { IsNone: false } && pspawn.Any(x => x.Vehicle) ? prider : null);
                        plog.ForEach(Console.WriteLine);
                        foreach (var (f, bytes) in built)
                        {
                            var st = MhoPackageModifier.Package.Open(Stock(f)); var nw = MhoPackageModifier.Package.FromBytes(bytes);
                            bool guid = File.ReadAllBytes(Stock(f)).AsSpan(st.GenerationsAt - 16, 16).SequenceEqual(bytes.AsSpan(nw.GenerationsAt - 16, 16));
                            File.WriteAllBytes(Path.Combine(outDir, f), bytes);
                            Console.WriteLine($"wrote {Path.Combine(outDir, f)} ({bytes.Length:N0} bytes; GUID {(guid ? "the stock package's" : "CHANGED")})");
                        }
                        return 0;
                    }
                    catch (InvalidDataException ex) { plog.ForEach(Console.WriteLine); Console.WriteLine("ERROR: " + ex.Message); return 1; }
                }
                int ai = rest.IndexOf("--anims");
                var pairs = rest.Skip(2).Take((ai < 0 ? rest.Count : ai) - 2).ToList();
                var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var donorAnims = new List<string>();
                var blog = new List<string>();
                try
                {
                    foreach (string pr in pairs)
                    {
                        var sides = pr.Split('=');
                        var t = sides[0].Split('|'); var dn = sides[1].Split('|');
                        string tFile = Path.GetFileName(Stock(t[0]));
                        byte[] cur = done.TryGetValue(tFile, out var b) ? b : File.ReadAllBytes(Stock(t[0]));
                        done[tFile] = PowerBorrow.Graft(cur, t[1], Stock(dn[0]), dn[1], blog);
                        donorAnims.AddRange(PowerBorrow.AnimNames(Stock(dn[0]), dn[1]));
                    }
                    if (ai > 0 && rest.Count > ai + 3 && donorAnims.Count > 0)
                    {
                        string heroPkg = Stock(rest[ai + 1]), heroClass = rest[ai + 2], donorHero = Stock(rest[ai + 3]);
                        var ap = AnimExportCli.Packages.Package.Open(donorHero);
                        var swaps = new List<AnimSwap.Swap>();
                        foreach (string an in donorAnims.Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            AnimSwap.Swap? found = null;
                            foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                                foreach (var seq in set.Sequences.Where(q => q.IsExport))
                                    if (found == null && AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex).Equals(an, StringComparison.OrdinalIgnoreCase))
                                        found = new AnimSwap.Swap(an, donorHero, set.ExportIndex, seq.ExportIndex);
                            if (found != null) swaps.Add(found); else blog.Add($"anims: {an} not found in {Path.GetFileName(donorHero)}");
                        }
                        if (swaps.Count > 0)
                        {
                            done[Path.GetFileName(heroPkg)] = AnimSwap.Build(heroPkg, heroClass, swaps, blog);
                            blog.Add($"anims: {string.Join(", ", swaps.Select(x => x.Slot))} added to {Path.GetFileName(heroPkg)} ({heroClass})");
                        }
                    }
                }
                catch (InvalidDataException ex) { blog.ForEach(Console.WriteLine); Console.WriteLine("ERROR: " + ex.Message); return 1; }
                blog.ForEach(Console.WriteLine);
                foreach (var (f, bytes) in done)
                {
                    // the GUID stays the stock package's (the game checks it)
                    var st = MhoPackageModifier.Package.Open(Stock(f)); var nw = MhoPackageModifier.Package.FromBytes(bytes);
                    bool guid = File.ReadAllBytes(Stock(f)).AsSpan(st.GenerationsAt - 16, 16).SequenceEqual(bytes.AsSpan(nw.GenerationsAt - 16, 16));
                    File.WriteAllBytes(Path.Combine(outDir, f), bytes);
                    Console.WriteLine($"wrote {Path.Combine(outDir, f)} ({bytes.Length:N0} bytes; GUID {(guid ? "the stock package's" : "CHANGED")})");
                }
                return 0;
            }
            case "--costume-props":
            {
                // Read only, or a test into <out folder> (Kurt, 2026-10-17: the target's props in the Model tab): a character
                // package's props (PowerParts.CostumeProps); with an out folder, all of them off, read back, then on again,
                // compared with the package's own exports. --costume-props <package.upk> [out folder]
                if (rest.Count < 2) { Console.WriteLine("--costume-props <package.upk> [out folder]"); return 1; }
                string cpf = Path.GetFileName(rest[1]);
                var cps = PowerParts.CostumeProps(cpf, rest[1]);
                foreach (var c in cps) Console.WriteLine($"  {c.Label}: {c.Part} · {c.Key} = {(c.Original.Length == 0 ? "(none)" : c.Original)}");
                if (rest.Count < 3 || cps.Count == 0) return 0;
                string cdir = Path.GetFullPath(rest[2]);
                if (cdir.StartsWith(Path.GetFullPath(Settings.Cooked(settings.ResolvedGameRoot(data) ?? "x:\none")), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: the game folder"); return 1; }
                Directory.CreateDirectory(cdir);
                var entries = cps.Select(c => new PartOffEntry { Package = cpf, Part = c.Part, Switch = c.Key, Original = c.Original }).ToList();
                var clog = new List<string>();
                string offFile = Path.Combine(cdir, "off_" + cpf), onFile = Path.Combine(cdir, "on_" + cpf);
                File.WriteAllBytes(offFile, PowerParts.Apply(rest[1], [.. entries.Select(e => (e, true))], clog));
                var after = PowerParts.CostumeProps(cpf, offFile);
                int fails = 0;
                foreach (var c in cps) { bool ok = after.Any(a => a.Part == c.Part && a.Original.Length == 0); if (!ok) fails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {c.Label} off"); }
                File.WriteAllBytes(onFile, PowerParts.Apply(offFile, [.. entries.Select(e => (e, false))], clog));
                var p0 = MhoPackageModifier.Package.Open(rest[1]); var p2 = MhoPackageModifier.Package.Open(onFile);
                int diff = Enumerable.Range(0, p0.Exports.Length).Count(i => !p0.ReadExportBytes(p0.Exports[i]).AsSpan().SequenceEqual(p2.ReadExportBytes(p2.Exports[i])));
                Console.WriteLine($"  {(diff == 0 ? "ok  " : "FAIL")} back on: {diff} export(s) differ from the package's own");
                if (diff > 0) fails++;
                clog.ForEach(l => Console.WriteLine("    " + l));
                Console.WriteLine(fails == 0 ? "PASS" : $"{fails} FAILED");
                return fails == 0 ? 0 : 1;
            }
            case "--power-parts":
            {
                // A power package's parts (a user, 2026-10-17: Invisible Woman's long-run hover): the class default's
                // components list (animation, effects, sounds) with each sound's events. With <out.upk> --off <regex>: a copy
                // without the parts whose name matches (taken off the list; their data stays). Never writes the game folder.
                if (rest.Count < 2) { Console.WriteLine("--power-parts <package.upk> [<out.upk> --off <regex>]"); return 1; }
                var pp = MhoPackageModifier.Package.Open(rest[1]);
                int def = -1; MhoPackageModifier.TagWalker? tags = null; byte[] dd = [];
                for (int i = 0; i < pp.Exports.Length && def < 0; i++)
                {
                    if (!pp.Exports[i].ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase) || pp.Exports[i].OuterIndex == 0 && !pp.PathOf(pp.Exports[i]).StartsWith("marvelgamecontent.", StringComparison.OrdinalIgnoreCase)) continue;
                    dd = pp.ReadExportBytes(pp.Exports[i]).ToArray();
                    tags = MhoPackageModifier.TagWalker.Walk(pp, dd, 4);
                    if (tags?.Any(t => t.Name.Equals("components", StringComparison.OrdinalIgnoreCase)) == true) def = i;
                }
                if (def < 0 || tags == null) { Console.WriteLine("no class default with a components list"); return 1; }
                var ct = tags.First(t => t.Name.Equals("components", StringComparison.OrdinalIgnoreCase));
                int cn = BitConverter.ToInt32(dd, ct.ValueAt);
                var refs = Enumerable.Range(0, cn).Select(k => BitConverter.ToInt32(dd, ct.ValueAt + 4 + 4 * k)).ToList();
                Console.WriteLine($"{pp.PathOf(pp.Exports[def])}: {cn} part(s)");
                foreach (int r in refs)
                {
                    if (r <= 0 || r > pp.Exports.Length) { Console.WriteLine($"  {pp.RefName(r)}"); continue; }
                    var pe = pp.Exports[r - 1];
                    string extra = "";
                    if (pp.ClassOf(pe).Equals("PowerFXSound", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] sd = pp.ReadExportBytes(pe).ToArray();
                        if (MhoPackageModifier.TagWalker.Walk(pp, sd, 8) is { } st)
                            extra = "  " + string.Join(", ", st.Where(t => t.Name.EndsWith("event", StringComparison.OrdinalIgnoreCase) && t.Size == 4)
                                .Select(t => $"{t.Name} = {pp.RefName(BitConverter.ToInt32(sd, t.ValueAt))}"));
                    }
                    Console.WriteLine($"  {pe.ObjectName,-16} {pp.ClassOf(pe),-36}{extra}");
                }
                int offAt = rest.IndexOf("--off");
                if (rest.Count < 3 || offAt < 0 || offAt + 1 >= rest.Count) return 0;
                string outPath = Path.GetFullPath(rest[2]);
                var vst = Settings.Load();
                if (vst.ResolvedGameRoot(lib.DataFolder) is string pgr && outPath.StartsWith(Path.GetFullPath(Settings.Cooked(pgr)), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: that's the game folder"); return 1; }
                var rx = new System.Text.RegularExpressions.Regex(rest[offAt + 1], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var keep = refs.Where(r => r <= 0 || r > pp.Exports.Length || !rx.IsMatch(pp.Exports[r - 1].ObjectName)).ToList();
                Console.WriteLine($"off: {string.Join(", ", refs.Except(keep).Select(r => pp.Exports[r - 1].ObjectName))}");
                byte[] head = dd.AsSpan(0, ct.ValueAt).ToArray();
                BitConverter.GetBytes(4 + 4 * keep.Count).CopyTo(head, ct.Start + 16);   // the array's size
                byte[] newData = [.. head, .. BitConverter.GetBytes(keep.Count), .. keep.SelectMany(BitConverter.GetBytes), .. dd.AsSpan(ct.End)];
                byte[] built = MhoPackageModifier.PackageRebuilder.Rebuild(pp, new Dictionary<int, Func<long, byte[]>> { [def] = _ => newData }, [], out var written);
                var probs = MhoPackageModifier.PackageRebuilder.Verify(pp, built, [def], [], written);
                var back = MhoPackageModifier.Package.FromBytes(built);
                var bt = MhoPackageModifier.TagWalker.Walk(back, back.ReadExportBytes(back.Exports[def]).ToArray(), 4);
                if (bt == null || BitConverter.ToInt32(back.ReadExportBytes(back.Exports[def]).ToArray(), bt.First(t => t.Name.Equals("components", StringComparison.OrdinalIgnoreCase)).ValueAt) != keep.Count) probs.Add("the components list doesn't read back");
                if (probs.Count > 0) { Console.WriteLine("verify: FAIL " + string.Join("; ", probs)); return 1; }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, built);
                Console.WriteLine($"wrote {outPath}: {keep.Count} of {cn} part(s) kept, verified");
                return 0;
            }
            case "--parts-test":
            {
                // Self-test (scratch libraries only, MHO_EXTMM_HOME): the Parts list's Save path (PowerParts.Build + ModWriter):
                // a new mod with Invisible Woman's Can Transition to Hover and a Levitating Disc effect off, then the setting on
                // again (her base package leaves the mod), then the effect on and another off (its game value comes back).
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("scratch libraries only (MHO_EXTMM_HOME)"); return 2; }
                string? tgr = settings.ResolvedGameRoot(data);
                if (tgr == null) { Console.WriteLine("game folder not set"); return 1; }
                var tgs = new GameState(tgr, data);
                StockFiles.Init(tgs, settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string tck = Settings.Cooked(tgr), tb = "UC__MarvelPlayer_InvisibleWoman_SF.upk", tp = "UC__PowerFlyingMovement_InvisibleWoman_SF.upk";
                var bsw = PowerParts.List(tb, StockFiles.For(tck, tb)); var psw = PowerParts.List(tp, StockFiles.For(tck, tp));
                var hover = bsw.First(s => s.Key == "cantransitiontohover"); var trail = psw.First(s => s.PartName == "flytrailvfx"); var palm = psw.First(s => s.PartName == "palmglow");
                int fails = 0;
                void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
                PartOffEntry Off(PowerParts.Switch s) => new() { Package = s.Package, Part = s.Part, Switch = s.Key, Original = s.Original };
                string? Val(Mod m, string f, PowerParts.Switch s) => File.Exists(Path.Combine(m.Folder, f)) ? PowerParts.List(f, Path.Combine(m.Folder, f)).FirstOrDefault(x => x.Part == s.Part && x.Key == s.Key)?.Original : null;
                Check(hover.Original == "true" && trail.Original.Length > 0 && palm.Original.Length > 0, $"stock values read (hover {hover.Original}, trail {trail.What}, palm {palm.What})");
                var tlog = new List<string>();
                string tname = "Parts Test " + Guid.NewGuid().ToString("N")[..6];
                var d1 = new ModDraft { Name = tname, Author = "Test" };
                d1.PartsOff.Add(Off(hover)); d1.PartsOff.Add(Off(trail));
                PowerParts.Build(d1, null, lib, tgs, tlog);
                string? s1 = ModWriter.Save(lib, d1, null, out string? e1);
                Check(s1 != null, "saved: " + (e1 ?? s1));
                var m1 = ModLibrary.Load(data).Find(tname)!;
                Check(m1.Manifest.UpkReplacements.Count == 2 && m1.Manifest.PartsOff?.Count == 2 && m1.Manifest.PartsOff.All(x => x.Added), $"both packages in the mod, 2 entries, added ({string.Join(", ", m1.Manifest.UpkReplacements)})");
                Check(Val(m1, tb, hover) == "" && Val(m1, tp, trail) == "" && Val(m1, tp, palm) == palm.Original, "hover off, trail off, palm glow as the game's");
                // MHO_PARTS_COMPARE=<a package>: her base package as this saves it, compared with that one (test E, confirmed in game)
                if (Environment.GetEnvironmentVariable("MHO_PARTS_COMPARE") is { Length: > 0 } cmpFile)
                    Check(File.ReadAllBytes(Path.Combine(m1.Folder, tb)).AsSpan().SequenceEqual(File.ReadAllBytes(cmpFile)), "her base package byte-identical to " + Path.GetFileName(Path.GetDirectoryName(cmpFile)));
                var d2 = ModDraft.From(m1);
                d2.PartsOff.RemoveAll(x => x.Switch == "cantransitiontohover");
                PowerParts.Build(d2, m1, lib, tgs, tlog);
                string? s2 = ModWriter.Save(lib, d2, m1, out string? e2);
                var m2 = ModLibrary.Load(data).Find(tname)!;
                Check(s2 != null && !m2.Manifest.UpkReplacements.Contains(tb, StringComparer.OrdinalIgnoreCase) && m2.Manifest.UpkReplacements.Contains(tp, StringComparer.OrdinalIgnoreCase), "setting on again: her base package left the mod, the power's stays " + (e2 ?? ""));
                var d3 = ModDraft.From(m2);
                d3.PartsOff.Clear(); d3.PartsOff.Add(Off(palm));
                PowerParts.Build(d3, m2, lib, tgs, tlog);
                string? s3 = ModWriter.Save(lib, d3, m2, out string? e3);
                var m3 = ModLibrary.Load(data).Find(tname)!;
                Check(s3 != null && Val(m3, tp, trail) == trail.Original && Val(m3, tp, palm) == "", $"trail back to {trail.What}, palm glow off " + (e3 ?? ""));
                foreach (string l in tlog) Console.WriteLine("  " + l);
                Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
                return fails == 0 ? 0 : 1;
            }
            case "--soft-census":
            {
                // Read only (2026-10-17): over the game's power packages (UC__Power*: optional name filter), how many plain sprite
                // emitters the preview rounds off (Map.SoftEdged), by material. --soft-census [package name part]
                string? sgr = settings.ResolvedGameRoot(data);
                if (sgr == null) { Console.WriteLine("game folder not set"); return 1; }
                string sck = Settings.Cooked(sgr);
                var files = Directory.EnumerateFiles(sck, "UC__Power*.upk").Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase)
                    && (rest.Count < 2 || Path.GetFileName(f).Contains(rest[1], StringComparison.OrdinalIgnoreCase))).ToList();
                var ssw = System.Diagnostics.Stopwatch.StartNew();
                var (spr, soft, mats) = Fx.PowerEffects.SoftCensus(new Fx.FxGame(sck, []), files, m => { Console.WriteLine(m); Console.Out.Flush(); });
                Console.WriteLine($"{files.Count} packages: {spr:N0} plain sprite emitters, {soft:N0} rounded off ({(spr == 0 ? 0 : 100.0 * soft / spr):0.0} %), {mats.Count} materials ({ssw.Elapsed.TotalSeconds:0} s)");
                foreach (var (k, v) in mats.OrderByDescending(x => x.Value.Emitters).Take(40))
                    Console.WriteLine($"  {v.Emitters,5}  {(v.Additive ? "additive" : "blended "),-9} {v.Texture,-34} {k}");
                return 0;
            }
            case "--ghost-slots":
            {
                // Read only: a hero package's dodge ghosts, where each sits and its automatic pose (DodgeGhosts.Analyze, as the
                // Powers tab reads them; kept in data\cache). --ghost-slots <hero package file>
                if (rest.Count < 2) { Console.WriteLine("--ghost-slots <hero package file>"); return 1; }
                string? ggr2 = settings.ResolvedGameRoot(data);
                if (ggr2 == null) { Console.WriteLine("game folder not set"); return 1; }
                StockFiles.Init(new GameState(ggr2, data), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                if (rest.Contains("--bounds"))
                {
                    // the stock ghosts' bounds beside the model's rest pose (where the meshes are kept)
                    string gstock = StockFiles.For(Settings.Cooked(ggr2), rest[1]);
                    var gpk = MhoPackageModifier.Package.Open(gstock);
                    string B(IEnumerable<Vector3> ps) { var l = ps.ToList(); var lo = l.Aggregate(Vector3.Min); var hi = l.Aggregate(Vector3.Max); return $"X {lo.X:0} … {hi.X:0}, Y {lo.Y:0} … {hi.Y:0}, Z {lo.Z:0} … {hi.Z:0}"; }
                    var gm = DodgeGhosts.CharacterModel(rest[1], gstock)!;
                    var gl = ModMeshes.Load(gm, null, out _, componentMaterials: false)!;
                    Console.WriteLine($"model {gm.Name} rest pose: {B(gl.Positions)}");
                    foreach (int gi in DodgeGhosts.GhostExports(gpk, rest[1], gstock))
                    {
                        var sm = MhoPackageModifier.StaticMesh.Read(gpk, gpk.Exports[gi]);
                        Console.WriteLine($"{gpk.Exports[gi].ObjectName}: {sm.Positions.Length} vertices, {B(sm.Positions)}");
                    }
                    return 0;
                }
                var gsw = System.Diagnostics.Stopwatch.StartNew();
                var gslots = DodgeGhosts.Analyze(rest[1], Settings.Cooked(ggr2), Console.WriteLine);
                foreach (var g in gslots) Console.WriteLine($"  {g.Name}: {(g.Anim.Length == 0 ? "rest pose" : $"{g.Anim} frame {g.Frame:0.#}")}, scale {g.Place.Scale:0.00}, error {g.Place.ErrorShare * 100:0.0} %");
                Console.WriteLine($"({gsw.Elapsed.TotalSeconds:0} s)");
                return 0;
            }
            case "--ghost-poses":
            {
                // Test: DodgeGhosts.Build as the Powers tab runs it, the model the package's own, with poses by hand
                // (ghost number=animation@frame; others automatic). --ghost-poses <package.upk> <out.upk> [1=anim@frame ...]
                if (rest.Count < 3) { Console.WriteLine("--ghost-poses <package.upk> <out.upk> [1=anim@frame ...]"); return 1; }
                string? pgr3 = settings.ResolvedGameRoot(data);
                if (pgr3 == null) { Console.WriteLine("game folder not set"); return 1; }
                StockFiles.Init(new GameState(pgr3, data), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string pck = Settings.Cooked(pgr3), ppath = rest[1], pfile = Path.GetFileName(ppath), pout = Path.GetFullPath(rest[2]);
                if (pout.StartsWith(Path.GetFullPath(pck), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: that's the game folder"); return 1; }
                var pslots = DodgeGhosts.Analyze(pfile, pck, Console.WriteLine);
                var ppicks = new Dictionary<string, (string, float)>(StringComparer.OrdinalIgnoreCase);
                foreach (string a in rest.Skip(3))
                {
                    var mm = System.Text.RegularExpressions.Regex.Match(a, @"^(\d+)=(.+)@([\d.]+)$");
                    if (mm.Success) ppicks[pslots[int.Parse(mm.Groups[1].Value) - 1].Name] = (mm.Groups[2].Value, float.Parse(mm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
                var pmodel = DodgeGhosts.CharacterModel(pfile, ppath)!;
                var plog = new List<string>();
                // (--leans: forward, back, left, right in turn)
                var pleans = rest.Contains("--leans") ? pslots.Select((s, i) => (s.Name, DodgeGhosts.Leans[i % 4])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.OrdinalIgnoreCase) : null;
                byte[] pbytes = DodgeGhosts.Build(ppath, pfile, pmodel, [(pfile, ppath)], pck, pslots, ppicks, plog, keepEffectSize: rest.Contains("--keep-size"), leans: pleans);
                plog.ForEach(Console.WriteLine);
                Directory.CreateDirectory(Path.GetDirectoryName(pout)!);
                File.WriteAllBytes(pout, pbytes);
                Console.WriteLine("wrote " + pout);
                return 0;
            }
            case "--ghost-build":
            {
                // Test (2026-10-17, Kurt: Captain Carter on Taskmaster): a hero package's dodge ghosts rebuilt from that package's
                // own character model (the mod's), posed at the frame that fits each stock ghost best (animations matching
                // <anim regex>), full body, in the character's own space. The ghost meshes get new names (…_mod: the boss's
                // package carries the stock ones at the same paths). --ghost-build <package.upk> <out.upk> [anim regex]
                if (rest.Count < 3) { Console.WriteLine("--ghost-build <package.upk> <out.upk> [anim regex]"); return 1; }
                string? bgr = settings.ResolvedGameRoot(data);
                if (bgr == null) { Console.WriteLine("game folder not set"); return 1; }
                StockFiles.Init(new GameState(bgr, data), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string bck = Settings.Cooked(bgr), bpath = rest[1], bfile = Path.GetFileName(bpath), bout = Path.GetFullPath(rest[2]);
                if (bout.StartsWith(Path.GetFullPath(bck), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: that's the game folder"); return 1; }
                var brx = new System.Text.RegularExpressions.Regex(rest.Count > 3 ? rest[3] : "idle|run|dodge|tumble|flip|kick|blink|dash", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var bmrs = ModMeshes.List([(bfile, bpath)]);
                string? bbody = Model.MhoSkeleton.ComponentMesh(bpath);
                var bmr = bmrs.FirstOrDefault(m => m.Name.Equals(bbody, StringComparison.OrdinalIgnoreCase)) ?? bmrs.FirstOrDefault();
                if (bmr == null) { Console.WriteLine("no character model"); return 1; }
                var bld = ModMeshes.Load(bmr, null, out string bwhy, componentMaterials: false);
                if (bld == null) { Console.WriteLine("model not read: " + bwhy); return 1; }
                Console.WriteLine($"model {bmr.Name}: {bld.Positions.Length:N0} vertices, {bld.Indices.Length / 3:N0} triangles");
                if (bld.Positions.Length > 65535) { Console.WriteLine("too many vertices for a static mesh (65,535)"); return 1; }
                var bp = MhoPackageModifier.Package.Open(bpath);
                var bsw = PowerParts.List(bfile, bpath).FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase));
                if (bsw == null || bsw.Original.Length == 0) { Console.WriteLine("no dodge effect in this package"); return 1; }
                var ghostIdx = new List<int>();
                foreach (var e in bp.Exports)
                {
                    if (!bp.ClassOf(e).Equals("ParticleModuleTypeDataMesh", StringComparison.OrdinalIgnoreCase) || !bp.PathOf(e).StartsWith(bsw.Original + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] d = bp.ReadExportBytes(e).ToArray();
                    if (MhoPackageModifier.TagWalker.Walk(bp, d, 4)?.FirstOrDefault(t => t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase)) is not { } mt) continue;
                    int r = BitConverter.ToInt32(d, mt.ValueAt);
                    if (r > 0 && !bp.PathOf(bp.Exports[r - 1]).Contains("vfx_shared", StringComparison.OrdinalIgnoreCase) && !ghostIdx.Contains(r - 1)) ghostIdx.Add(r - 1);
                }
                if (ghostIdx.Count == 0) { Console.WriteLine("no ghost meshes of its own"); return 1; }
                // the poses to try
                var banimator = new MeshAnimator(bld.Bones, bld.Positions, bld.Normals, bld.Influences);
                var bposes = new List<(string Anim, float Frame, Vector3[] P, Vector3[] N, GhostFit.Model M)> { ("(rest pose)", 0, bld.Positions, bld.Normals, new GhostFit.Model(bld.Positions)) };
                foreach (var a in ModAnimations.For(bmr, bld.Bones, [], bck).Where(a => brx.IsMatch(a.Name)))
                {
                    var ba = ModAnimations.Load(a);
                    if (ba == null) continue;
                    var (frames, _) = MeshAnimator.Span(ba);
                    for (float fr = 0, st = Math.Max(1f, frames / 6f); fr <= frames; fr += st)
                    {
                        banimator.Pose(ba, fr);
                        var pp = (Vector3[])banimator.Positions.Clone();
                        bposes.Add((a.Name, fr, pp, (Vector3[])banimator.Normals.Clone(), new GhostFit.Model(pp)));
                    }
                }
                Console.WriteLine($"{ghostIdx.Count} ghost(s), {bposes.Count} pose(s) to try");
                // Where each stock ghost sits is found with the game's own model (Kurt, 2026-10-17: fitting the mod's full body
                // onto Taskmaster's partial ghosts placed Captain Carter at twice her size): the stock model posed the same way,
                // fitted to the stock ghost; the mod's model in that pose then goes where that fit puts it.
                var stockPoses = new Dictionary<(string, float), GhostFit.Model>();
                string bstock = StockFiles.For(bck, bfile);
                if (File.Exists(bstock) && !Path.GetFullPath(bstock).Equals(Path.GetFullPath(bpath), StringComparison.OrdinalIgnoreCase))
                {
                    var smrs = ModMeshes.List([(bfile, bstock)]);
                    string? sbody = Model.MhoSkeleton.ComponentMesh(bstock);
                    var smr = smrs.FirstOrDefault(m => m.Name.Equals(sbody, StringComparison.OrdinalIgnoreCase)) ?? smrs.FirstOrDefault();
                    var sld = smr == null ? null : ModMeshes.Load(smr, null, out _, componentMaterials: false);
                    if (sld != null)
                    {
                        stockPoses[("(rest pose)", 0)] = new GhostFit.Model(sld.Positions);
                        var sanim = new MeshAnimator(sld.Bones, sld.Positions, sld.Normals, sld.Influences);
                        foreach (var a in ModAnimations.For(smr!, sld.Bones, [], bck).Where(a => brx.IsMatch(a.Name)))
                        {
                            var sa = ModAnimations.Load(a);
                            if (sa == null) continue;
                            var (frames, _) = MeshAnimator.Span(sa);
                            for (float fr = 0, st = Math.Max(1f, frames / 6f); fr <= frames; fr += st)
                            {
                                sanim.Pose(sa, fr);
                                stockPoses[(a.Name, fr)] = new GhostFit.Model((Vector3[])sanim.Positions.Clone());
                            }
                        }
                        Console.WriteLine($"placed by the game's model {smr!.Name}: {stockPoses.Count} pose(s)");
                    }
                }
                var replace = new Dictionary<int, Func<long, byte[]>>();
                var renames = new Dictionary<int, string>();
                var used = new HashSet<(string, float)>();
                foreach (int gi in ghostIdx)
                {
                    var stock = MhoPackageModifier.StaticMesh.Read(bp, bp.Exports[gi]);
                    // the pose that fits the stock ghost best (each pose used once), placed where the stock ghost is: its scale, turn
                    // and offset (Kurt, 2026-10-17: placed as posed, in the character's own space, two of Taskmaster's ghosts, kept
                    // 1.5× and turned in their meshes, floated above the head)
                    var (pick, fit) = bposes.Where(x => !used.Contains((x.Anim, x.Frame)) && (stockPoses.Count == 0 || stockPoses.ContainsKey((x.Anim, x.Frame))))
                        .Select(x => (x, F: GhostFit.Best(stockPoses.Count > 0 ? stockPoses[(x.Anim, x.Frame)] : x.M, stock.Positions, 8))).OrderBy(x => x.F.ErrorShare).First();
                    used.Add((pick.Anim, pick.Frame));
                    var placedP = pick.P.Select(v => fit.Scale * Vector3.Transform(v, fit.Turn) + fit.Offset).ToArray();
                    var placedN = pick.N.Select(n => Vector3.Transform(n, fit.Turn)).ToArray();
                    var built0 = MhoPackageModifier.StaticMeshBuilder.BuildGeometry(stock, placedP, placedN, bld.Indices, stock.Sections[0].MaterialRef, stock.Sections[0].MaterialName, bld.Uv);
                    // vertex colours as the stock ghost's body has them (B, G, R, A = 0, 0, 0, 255; its cut edge fades with alpha 0)
                    var cols = new byte[pick.P.Length * 4];
                    for (int v = 0; v < pick.P.Length; v++) cols[v * 4 + 3] = 255;
                    var built = stock.HasVertexColors ? new MhoPackageModifier.BuiltMesh { Positions = built0.Positions, Normals = built0.Normals, TexCoords = built0.TexCoords, TangentX = built0.TangentX, TangentZ = built0.TangentZ,
                        Indices = built0.Indices, Adjacency = built0.Adjacency, Sections = built0.Sections, BoundsOrigin = built0.BoundsOrigin, BoundsExtent = built0.BoundsExtent, BoundsRadius = built0.BoundsRadius, ColorsBgra = cols } : built0;
                    replace[gi] = off => MhoPackageModifier.StaticMeshBuilder.Serialize(stock, built, off, dropBodySetup: true);
                    renames[gi] = bp.Exports[gi].ObjectName + "_mod";
                    Console.WriteLine($"  {bp.Exports[gi].ObjectName} ({stock.Positions.Length:N0} vertices) → {renames[gi]}: {pick.Anim} frame {pick.Frame:0.#}, {pick.P.Length:N0} vertices, placed at scale {fit.Scale:0.00}, error {fit.ErrorShare * 100:0.0} %");
                }
                var addNames = renames.Values.Where(n => !bp.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                byte[] bbuilt = MhoPackageModifier.PackageRebuilder.Rebuild(bp, replace, [], out var bwritten, addNames, null, renames, null);
                var bprobs = MhoPackageModifier.PackageRebuilder.Verify(bp, bbuilt, replace.Keys.ToList(), [], bwritten, addNames, null, renames, null);
                var bback = MhoPackageModifier.Package.FromBytes(bbuilt);
                foreach (int gi in ghostIdx)
                {
                    var rb = MhoPackageModifier.StaticMesh.Read(bback, bback.Exports[gi]);
                    if (rb.Positions.Length != bld.Positions.Length) bprobs.Add($"{bback.Exports[gi].ObjectName} reads back with {rb.Positions.Length} vertices");
                    if (!bback.Exports[gi].ObjectName.Equals(renames[gi], StringComparison.OrdinalIgnoreCase)) bprobs.Add($"export {gi + 1} isn't named {renames[gi]}");
                }
                if (bprobs.Count > 0) { Console.WriteLine("verify: FAIL " + string.Join("; ", bprobs.Take(6))); return 1; }
                Directory.CreateDirectory(Path.GetDirectoryName(bout)!);
                File.WriteAllBytes(bout, bbuilt);
                Console.WriteLine($"wrote {bout} ({bbuilt.Length:N0} bytes), verified: ghosts read back with {bld.Positions.Length:N0} vertices each");
                return 0;
            }
            case "--ghost-fit":
            {
                // Read only (2026-10-17): is each dodge ghost the hero's model posed at a frame of an animation? For every ghost
                // mesh of the hero's dodge effect, the best frame of the animations matching <anim regex> (default: dodge-like
                // ones) by GhostFit (scale, turn, offset). --ghost-fit <Hero> [anim regex]
                if (rest.Count < 2) { Console.WriteLine("--ghost-fit <Hero> [anim regex]"); return 1; }
                string? ggr = settings.ResolvedGameRoot(data);
                if (ggr == null) { Console.WriteLine("game folder not set"); return 1; }
                StockFiles.Init(new GameState(ggr, data), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string gck = Settings.Cooked(ggr), gfile = $"UC__MarvelPlayer_{rest[1]}_SF.upk", gpath = StockFiles.For(gck, gfile);
                var animRx = new System.Text.RegularExpressions.Regex(rest.Count > 2 ? rest[2] : "dodge|evade|roll|dash|flip|blink|teleport|counter", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var mrs = ModMeshes.List([(gfile, gpath)]);
                string? body = Model.MhoSkeleton.ComponentMesh(gpath);
                var mr = mrs.FirstOrDefault(m => m.Name.Equals(body, StringComparison.OrdinalIgnoreCase)) ?? mrs.FirstOrDefault();
                if (mr == null) { Console.WriteLine("no character model"); return 1; }
                var ld = ModMeshes.Load(mr, null, out string why, componentMaterials: false);
                if (ld == null) { Console.WriteLine("model not read: " + why); return 1; }
                var anims = ModAnimations.For(mr, ld.Bones, [], gck).Where(a => animRx.IsMatch(a.Name)).ToList();
                Console.WriteLine($"{mr.Name}: {ld.Positions.Length:N0} vertices, {ld.Bones.Count} bones; {anims.Count} animation(s) match: {string.Join(", ", anims.Select(a => a.Name).Take(12))}");
                // the ghost meshes
                var gp = MhoPackageModifier.Package.Open(gpath);
                var sw = PowerParts.List(gfile, gpath).FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase));
                if (sw == null || sw.Original.Length == 0) { Console.WriteLine("no dodge effect"); return 1; }
                var ghosts = new List<(string Name, Vector3[] P)>();
                foreach (var e in gp.Exports)
                {
                    if (!gp.ClassOf(e).Equals("ParticleModuleTypeDataMesh", StringComparison.OrdinalIgnoreCase) || !gp.PathOf(e).StartsWith(sw.Original + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] d = gp.ReadExportBytes(e).ToArray();
                    if (MhoPackageModifier.TagWalker.Walk(gp, d, 4)?.FirstOrDefault(t => t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase)) is not { } mt) continue;
                    int r = BitConverter.ToInt32(d, mt.ValueAt);
                    if (r <= 0 || gp.PathOf(gp.Exports[r - 1]).Contains("vfx_shared", StringComparison.OrdinalIgnoreCase) || ghosts.Any(x => x.Name == gp.Exports[r - 1].ObjectName)) continue;
                    var sm = MhoPackageModifier.StaticMesh.Read(gp, gp.Exports[r - 1]);
                    ghosts.Add((gp.Exports[r - 1].ObjectName, sm.Positions));
                }
                var gsw = System.Diagnostics.Stopwatch.StartNew();
                var animator = new MeshAnimator(ld.Bones, ld.Positions, ld.Normals, ld.Influences);
                // rest pose too, for comparison
                var poses = new List<(string Anim, float Frame, GhostFit.Model P)> { ("(rest pose)", 0, new GhostFit.Model(ld.Positions)) };
                foreach (var a in anims)
                {
                    var ba = ModAnimations.Load(a);
                    if (ba == null) continue;
                    var (frames, _) = MeshAnimator.Span(ba);
                    for (float fr = 0, fstep = Math.Max(1f, frames / (float.TryParse(Environment.GetEnvironmentVariable("MHO_GHOST_FRAMES"), out float gfn) ? gfn : 24f)); fr <= frames; fr += fstep)
                    {
                        animator.Pose(ba, fr);
                        poses.Add((a.Name, fr, new GhostFit.Model((Vector3[])animator.Positions.Clone())));
                    }
                }
                Console.WriteLine($"{ghosts.Count} ghost(s), {poses.Count} pose(s) to try");
                foreach (var (gname, gpts) in ghosts)
                {
                    // the turn from the rest pose (all 24 starts), then every pose from that turn; the best three refined
                    var restFit = GhostFit.Best(poses[0].P, gpts, 20);
                    var results = poses.Select(p => (p.Anim, p.Frame, p.P, F: GhostFit.Best(p.P, gpts, 8, restFit.Start))).OrderBy(x => x.F.ErrorShare).ToList();
                    var refined = results.Take(3).Select(x => (x.Anim, x.Frame, F: GhostFit.Best(x.P, gpts, 30))).OrderBy(x => x.F.ErrorShare).ToList();
                    var top = refined[0]; var e = top.F.Turn;
                    Console.WriteLine($"  {gname} ({gpts.Length} vertices): best {top.Anim} frame {top.Frame:0.#}: error {top.F.Error:0.00} units ({top.F.ErrorShare * 100:0.0} % of its size), scale {top.F.Scale:0.000}, turn ({e.X:0.00} {e.Y:0.00} {e.Z:0.00} {e.W:0.00}); next: {string.Join(", ", results.Where(x => x.Anim != top.Anim || x.Frame != top.Frame).Take(3).Select(x => $"{x.Anim} {x.Frame:0.#} {x.F.ErrorShare * 100:0.0} %"))}; rest pose {restFit.ErrorShare * 100:0.0} %");
                    Console.Out.Flush();
                    // MHO_GHOST_DUMP=<dir>: the ghost's points and the best pose's, fitted onto it (x y z per line), to look at
                    if (Environment.GetEnvironmentVariable("MHO_GHOST_DUMP") is { Length: > 0 } dump)
                    {
                        Directory.CreateDirectory(dump);
                        var bp = poses.First(x => x.Anim == top.Anim && x.Frame == top.Frame).P;
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        File.WriteAllLines(Path.Combine(dump, gname + "_ghost.xyz"), gpts.Select(v => string.Format(inv, "{0} {1} {2}", v.X, v.Y, v.Z)));
                        File.WriteAllLines(Path.Combine(dump, gname + "_model.xyz"), GhostFit.Apply(bp, top.F).Select(v => string.Format(inv, "{0} {1} {2}", v.X, v.Y, v.Z)));
                    }
                }
                Console.WriteLine($"({gsw.Elapsed.TotalSeconds:0.0} s)");
                return 0;
            }
            case "--dodge-census":
            {
                // Read only (2026-10-17, Kurt: replace the dodge ghost with the mod's model): every hero base package's
                // dodgevfx part → its particle system → mesh emitters → the static meshes they emit (vertices, size, package).
                string? dgr = settings.ResolvedGameRoot(data);
                if (dgr == null) { Console.WriteLine("game folder not set"); return 1; }
                StockFiles.Init(new GameState(dgr, data), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string dck = Settings.Cooked(dgr);
                var rx = new System.Text.RegularExpressions.Regex(@"^UC__MarvelPlayer_([A-Za-z0-9]+)_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int heroes = 0, withMesh = 0;
                foreach (string f in Directory.EnumerateFiles(dck, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileName).Where(f => rx.IsMatch(f!)).Order()!)
                {
                    if (f!.Contains("Audio", StringComparison.OrdinalIgnoreCase)) continue;
                    var sw = PowerParts.List(f, StockFiles.For(dck, f)).FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase));
                    heroes++;
                    if (sw == null || sw.Original.Length == 0) { Console.WriteLine($"{rx.Match(f).Groups[1].Value,-18} no dodge effect of its own"); continue; }
                    var pk = MhoPackageModifier.Package.Open(StockFiles.For(dck, f));
                    string sys = sw.Original;
                    // the system's mesh emitters: particlemoduletypedatamesh exports under it, their 'mesh'
                    var meshes = new List<string>();
                    for (int i = 0; i < pk.Exports.Length; i++)
                    {
                        var e = pk.Exports[i];
                        if (!pk.ClassOf(e).Equals("ParticleModuleTypeDataMesh", StringComparison.OrdinalIgnoreCase) || !pk.PathOf(e).StartsWith(sys + ".", StringComparison.OrdinalIgnoreCase)) continue;
                        byte[] d = pk.ReadExportBytes(e).ToArray();
                        if (MhoPackageModifier.TagWalker.Walk(pk, d, 4) is { } w && w.FirstOrDefault(t => t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase)) is { } mt)
                        {
                            int r = BitConverter.ToInt32(d, mt.ValueAt);
                            meshes.Add(r > 0 ? pk.PathOf(pk.Exports[r - 1]) + $" ({pk.Exports[r - 1].SerialSize:N0} B)" : "import " + pk.RefName(r));
                        }
                    }
                    var own = meshes.Where(m => !m.StartsWith("import") && !m.Contains("vfx_shared", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                    if (own.Count > 0) withMesh++;
                    Console.WriteLine($"{rx.Match(f).Groups[1].Value,-18} {sys[(sys.LastIndexOf('.') + 1)..],-40} {meshes.Count} mesh emitter(s); own meshes: {(own.Count == 0 ? "none" : string.Join(", ", own.Select(m => m[(m.LastIndexOf('.') + 1)..])))}");
                }
                Console.WriteLine($"{heroes} heroes, {withMesh} with a dodge ghost mesh of their own");
                return 0;
            }
            case "--parts":
            {
                // Read only: what the Powers tab's Parts list offers for these packages (PowerParts.List).
                foreach (string f in rest.Skip(1))
                {
                    var sw = PowerParts.List(Path.GetFileName(f), f);
                    Console.WriteLine($"{Path.GetFileName(f)}: {sw.Count} switch(es)");
                    foreach (var s in sw) Console.WriteLine($"  {s.Kind,-10} {s.Label,-28} {s.What,-48} {(s.Original.Length > 0 ? "on" : "off in the game")}  [{s.PartName}]");
                }
                return 0;
            }
            case "--power-null":
            {
                // Test (2026-10-17, Invisible Woman's hover): a power or condition package with parts switched off from inside.
                // Taking parts off the class default's components list did nothing in game: the Class export's own map
                // (component name → template) is what the game instances. Here, in every part whose name matches <part regex>,
                // object properties matching <property regex> are set to none and array properties are emptied.
                // --power-null <package.upk> <out.upk> <part regex> <property regex>. Never writes the game folder.
                if (rest.Count < 5) { Console.WriteLine("--power-null <package.upk> <out.upk> <part regex> <property regex>"); return 1; }
                var np = MhoPackageModifier.Package.Open(rest[1]);
                string nout = Path.GetFullPath(rest[2]);
                if (Settings.Load().ResolvedGameRoot(lib.DataFolder) is string ngr && nout.StartsWith(Path.GetFullPath(Settings.Cooked(ngr)), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: that's the game folder"); return 1; }
                var partRx = new System.Text.RegularExpressions.Regex(rest[3], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var propRx = new System.Text.RegularExpressions.Regex("^(" + rest[4] + ")$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var replace = new Dictionary<int, Func<long, byte[]>>();
                for (int i = 0; i < np.Exports.Length; i++)
                {
                    var e = np.Exports[i];
                    string path = np.PathOf(e);
                    // a part: a component under a class default (marvelgamecontent.default__<class>.<part>)
                    if (e.OuterIndex <= 0 || !np.Exports[e.OuterIndex - 1].ObjectName.StartsWith("default__", StringComparison.OrdinalIgnoreCase) || !partRx.IsMatch(e.ObjectName)) continue;
                    byte[] d = np.ReadExportBytes(e).ToArray();
                    // (components start their properties at 16 or 8; the start whose walk finds a matching property)
                    var walk = new[] { 16, 8 }.Select(st => MhoPackageModifier.TagWalker.Walk(np, d, st)).FirstOrDefault(w => w != null && w.Any(t => propRx.IsMatch(t.Name)));
                    if (walk == null) continue;
                    var hits = walk.Where(t => propRx.IsMatch(t.Name)).ToList();
                    if (hits.Count == 0) continue;
                    var outB = new List<byte>(); int at = 0;
                    foreach (var t in hits.OrderBy(t => t.Start))
                    {
                        string ty = t.Type.ToLowerInvariant();
                        if (ty == "objectproperty" && t.Size == 4)
                        {
                            outB.AddRange(d.AsSpan(at, t.ValueAt - at).ToArray()); outB.AddRange(BitConverter.GetBytes(0)); at = t.ValueAt + 4;
                            Console.WriteLine($"  {e.ObjectName}.{t.Name} = {np.RefName(BitConverter.ToInt32(d, t.ValueAt))} → none");
                        }
                        else if (ty == "boolproperty" && t.Size == 0 && t.Start + 24 < d.Length)
                        {
                            // a true/false setting: its value is the byte after the 24-byte tag (Invisible Woman's
                            // animcomplexcomponent.cantransitiontohover: running turns into her hover)
                            outB.AddRange(d.AsSpan(at, t.Start + 24 - at).ToArray()); outB.Add(0); at = t.Start + 25;
                            Console.WriteLine($"  {e.ObjectName}.{t.Name} = {(d[t.Start + 24] != 0 ? "true" : "false")} → false");
                        }
                        else if (ty == "arrayproperty")
                        {
                            byte[] head = d.AsSpan(at, t.ValueAt - at).ToArray();
                            BitConverter.GetBytes(4).CopyTo(head, t.Start + 16 - at);   // the array's size: just the count
                            outB.AddRange(head); outB.AddRange(BitConverter.GetBytes(0)); at = t.End;
                            Console.WriteLine($"  {e.ObjectName}.{t.Name}: {BitConverter.ToInt32(d, t.ValueAt)} entries → empty");
                        }
                    }
                    outB.AddRange(d.AsSpan(at).ToArray());
                    byte[] nd = [.. outB];
                    replace[i] = _ => nd;
                }
                if (replace.Count == 0) { Console.WriteLine("nothing matched"); return 1; }
                byte[] nbuilt = MhoPackageModifier.PackageRebuilder.Rebuild(np, replace, [], out var nwritten);
                var nprobs = MhoPackageModifier.PackageRebuilder.Verify(np, nbuilt, replace.Keys.ToList(), [], nwritten);
                var nback = MhoPackageModifier.Package.FromBytes(nbuilt);
                foreach (int i in replace.Keys)
                {
                    byte[] bd = nback.ReadExportBytes(nback.Exports[i]).ToArray();
                    var bw = new[] { 16, 8 }.Select(st => MhoPackageModifier.TagWalker.Walk(nback, bd, st)).FirstOrDefault(w => w != null && w.Any(t => propRx.IsMatch(t.Name)));
                    if (bw == null) { nprobs.Add($"{nback.Exports[i].ObjectName}: properties don't read back"); continue; }
                    foreach (var t in bw.Where(t => propRx.IsMatch(t.Name)))
                        if (t.Type.Equals("boolproperty", StringComparison.OrdinalIgnoreCase) ? bd[t.Start + 24] != 0 : BitConverter.ToInt32(bd, t.ValueAt) != 0) nprobs.Add($"{nback.Exports[i].ObjectName}.{t.Name} isn't empty");
                }
                if (nprobs.Count > 0) { Console.WriteLine("verify: FAIL " + string.Join("; ", nprobs)); return 1; }
                Directory.CreateDirectory(Path.GetDirectoryName(nout)!);
                File.WriteAllBytes(nout, nbuilt);
                Console.WriteLine($"wrote {nout}: {replace.Count} part(s) changed, verified");
                return 0;
            }
            case "--hero-forms":
            {
                // Read-only: a hero's forms (a talent whose condition adds an animation alias: Jean Grey's Unleash the Phoenix),
                // the animation set each alias names, the powers each swaps, and mapped powers no talent uses. --hero-forms <hero>
                if (rest.Count < 2) { Console.WriteLine("--hero-forms <hero, e.g. JeanGrey>"); return 1; }
                string? hgr = settings.ResolvedGameRoot(data);
                if (hgr == null || !Settings.IsGameRoot(hgr)) { Console.WriteLine("game folder not found"); return 1; }
                string hcook = Settings.Cooked(hgr), hero = rest[1];
                var hdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(hgr, "Data", "Game", "Calligraphy.sip")));
                foreach (var m in Fx.HeroForms.Mappings(hdb, hero))
                    Console.WriteLine($"talent {m.Spec}: {m.Mapped.Count} swap(s): {string.Join(", ", m.Mapped.Select(kv => Path.GetFileNameWithoutExtension(kv.Key) + " → " + Path.GetFileNameWithoutExtension(kv.Value)))}");
                var forms = Fx.HeroForms.For(hdb, hero, hcook, [], new Dictionary<string, string>());
                string basePkg = StockFiles.For(hcook, $"UC__MarvelPlayer_{hero}_SF.upk");
                foreach (var f in forms)
                {
                    var set = Fx.HeroForms.AnimSetFor(f.Alias, [basePkg]);
                    string setName = set is { } st ? MhoPackageModifier.Package.Open(st.Path) is var sp ? sp.PathOf(sp.Exports[st.Export]) : "?" : "not found";
                    Console.WriteLine($"form {f.Spec} ({f.Name}): alias {f.Alias} → {setName}; condition packages {string.Join(", ", f.ConditionFiles.Select(Path.GetFileName))}");
                    if (set is { } fs) Console.WriteLine($"    its animations: {string.Join(", ", ModAnimations.FromSet(fs.Path, fs.Export).Select(x => x.Name))}");
                }
                foreach (var t in Fx.HeroForms.Transforms(hdb, hero))
                {
                    Console.WriteLine($"transform {t.Mode}: on with {t.Spec}; model {t.Model} ({t.ModelFor.Count} costume version(s)); abilities {string.Join(", ", t.Abilities.Select(Path.GetFileNameWithoutExtension))}");
                    string tf = $"UC__{t.Model}_SF.upk", tp = StockFiles.For(hcook, tf);
                    var refs = File.Exists(tp) ? ModMeshes.List([(tf, tp)]) : [];
                    var mr = refs.FirstOrDefault(r => Model.MhoSkeleton.ComponentMesh(r.File) is string cm && r.Name.Equals(cm, StringComparison.OrdinalIgnoreCase)) ?? refs.FirstOrDefault();
                    if (mr != null && ModMeshes.Load(mr, hcook, out _) is { } ml)
                        Console.WriteLine($"    {mr.Name}: animations {string.Join(", ", ModAnimations.For(mr, ml.Bones, [], hcook).Select(x => x.Name))}");
                    else Console.WriteLine($"    {tf}: no model found");
                }
                var used = Fx.HeroForms.Mappings(hdb, hero).SelectMany(m => m.Mapped.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var unused = Fx.HeroForms.AllMappedTargets(hdb, hero).Where(t => !used.Contains(t)).ToList();
                Console.WriteLine($"mapped powers no talent uses: {(unused.Count == 0 ? "none" : string.Join(", ", unused.Select(Path.GetFileNameWithoutExtension)))}");
                return 0;
            }
            case "--hero-files":
            {
                // Read-only: the packages Make Unique counts as a hero's own copies (PowerRecolor.HeroFiles: what its powers use or
                // summon), optionally only those whose name contains a part. --hero-files <hero> [name part]
                if (rest.Count < 2) { Console.WriteLine("--hero-files <hero> [name part]"); return 1; }
                string? hgr = settings.ResolvedGameRoot(data);
                if (hgr == null || !Settings.IsGameRoot(hgr)) { Console.WriteLine("game folder not found"); return 1; }
                var hdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(hgr, "Data", "Game", "Calligraphy.sip")));
                var files = (rest.Contains("--unique") ? UniqueNames.OwnFiles($"UC__MarvelPlayer_{rest[1]}_SF.upk", Settings.Cooked(hgr)) : PowerRecolor.HeroFiles(hdb, rest[1], Settings.Cooked(hgr))).Order(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string f in files.Where(f => rest.Count < 3 || rest[2] == "--unique" || f.Contains(rest[2], StringComparison.OrdinalIgnoreCase))) Console.WriteLine(f);
                Console.WriteLine($"{files.Count} package(s)");
                return 0;
            }
            case "--summon-census":
            {
                // Read-only: every hero's powers that summon a character, with what the 3D preview shows for them (the summon's
                // class, model, animation and own materials; the costume variant picked for the hero's default costume).
                // --summon-census [hero ...]
                string? cgr = settings.ResolvedGameRoot(data);
                if (cgr == null || !Settings.IsGameRoot(cgr)) { Console.WriteLine("game folder not found"); return 1; }
                string ccook = Settings.Cooked(cgr);
                var cdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(cgr, "Data", "Game", "Calligraphy.sip")));
                var heroes = rest.Count > 1 ? rest.Skip(1).ToList()
                    : Directory.GetFiles(ccook, "UC__MarvelPlayer_*_SF.upk").Select(f => Path.GetFileNameWithoutExtension(f)["UC__MarvelPlayer_".Length..^3])
                        .Where(h => !h.Contains('_') && !h.Contains("Mech", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
                int found = 0;
                // MHO_CENSUS_OUT=<file>: also a table of the summons' packages (package, hero, summon, power): the Model tab's
                // Summons list (Model\StockData\model_summons.txt)
                var table = Environment.GetEnvironmentVariable("MHO_CENSUS_OUT") is string cto ? new SortedSet<string>(StringComparer.OrdinalIgnoreCase) : null;
                Fx.PowerEffects.AllCostumeSummons = table != null;
                foreach (string hero in heroes)
                {
                    List<string>? heroFiles = null;   // (the hero's power and projectile packages, for copies of a summon's model)
                    List<Fx.PowerList.Power> powers;
                    try { powers = Fx.PowerList.For(cdb, hero, ccook, [], effectOnly: true); } catch (Exception ex) { Console.WriteLine($"{hero}: {ex.Message}"); continue; }
                    foreach (var pw in powers)
                    {
                        Fx.PowerEffects fx;
                        try { fx = Fx.PowerEffects.For(new Fx.FxGame(ccook, []), cdb, pw.Prototype, hero); } catch (Exception ex) { Console.WriteLine($"{hero} · {pw.Name}: {ex.Message}"); continue; }
                        // (with MHO_CENSUS_OUT, the animated actors too: models a power plays with no summon, Cyclops' Strike Force)
                        var sums = fx.Actors.Where(x => x.Kind == "summon" || table != null && x.SetFile != null).GroupBy(x => $"{x.MeshFile}|{x.MeshExport}").Select(g => g.First()).ToList();
                        var missing = fx.Notes.Where(n => n.Contains("summoned character")).ToList();
                        if (sums.Count == 0 && missing.Count == 0) continue;
                        found++;
                        Console.WriteLine($"{hero} · {pw.Name} ({Path.GetFileNameWithoutExtension(pw.Prototype)}):");
                        foreach (var sm in sums)
                        {
                            var mref = new MeshRef(Path.GetFileName(sm.MeshFile), sm.MeshFile, sm.Name, sm.MeshExport);
                            var lm = ModMeshes.Load(mref, ccook, out _);
                            static bool Real(AnimRef a) => !a.Name.StartsWith("temp", StringComparison.OrdinalIgnoreCase) && !a.Name.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
                            var anims = sm.SetFile != null ? ModAnimations.FromSet(sm.SetFile, sm.SetExport).Where(Real).ToList() : [];
                            if (anims.Count == 0 && lm != null) anims = ModAnimations.For(mref, lm.Bones, [], ccook).Where(Real).ToList();
                            if (lm != null && table != null)
                            {
                                // the model's path, and every other package of the hero's powers and summons with a copy under that
                                // path (Squirrelpocalypse's squirrel is in its projectile's package and in the power's: the game
                                // shows the copy it loads first, so the Model tab builds them together)
                                var mpk = MhoPackageModifier.Package.Open(sm.MeshFile);
                                string meshPath = mpk.PathOf(mpk.Exports[sm.MeshExport]);
                                string meshName = mpk.Exports[sm.MeshExport].ObjectName;
                                table.Add($"{Path.GetFileName(sm.MeshFile)}\t{hero}\t{sm.Name}\t{pw.Name}\t{sm.Kind}\t{meshName}\t{lm.Bones.Count}\t{pw.Icon}\t{meshPath}");
                                heroFiles ??= Directory.GetFiles(ccook, $"UC__Power{hero}_*_SF.upk").Concat(Directory.GetFiles(ccook, $"UC__MarvelProjectile_*{hero}*_SF.upk"))
                                    .Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
                                foreach (string other in heroFiles.Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(sm.MeshFile), StringComparison.OrdinalIgnoreCase)))
                                {
                                    try
                                    {
                                        var op = MhoPackageModifier.Package.Open(other);
                                        for (int k = 0; k < op.Exports.Length; k++)
                                            if (op.Exports[k].ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && op.ClassOf(op.Exports[k]).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)
                                                && op.PathOf(op.Exports[k]).Equals(meshPath, StringComparison.OrdinalIgnoreCase))
                                            { table.Add($"{Path.GetFileName(other)}\t{hero}\t{sm.Name}\t{pw.Name}\tcopy\t{meshName}\t{lm.Bones.Count}\t{pw.Icon}\t{meshPath}"); break; }
                                    }
                                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                                }
                            }
                            Console.WriteLine($"    {(sm.Kind != "summon" ? "(" + sm.Kind + " actor) " : "")}{sm.Name}: model {(lm != null ? $"{lm.Positions.Length} vertices" : "NOT LOADED")}, {anims.Count} animations{(anims.Count > 0 ? " (" + string.Join(" ", anims.Take(4).Select(x => x.Name)) + ")" : "")}{(sm.Materials is { Count: > 0 } m ? $", {m.Count} own material(s)" : "")}{(sm.TriggeredBy != null ? " · by " + sm.TriggeredBy : "")}");
                            if (Environment.GetEnvironmentVariable("MHO_CENSUS_DETAIL") == "1")
                            {
                                Console.WriteLine($"      mesh {Path.GetFileName(sm.MeshFile)}#{sm.MeshExport}, {lm?.Looks.Length ?? 0} section look(s)");
                                foreach (var mt in sm.Materials ?? []) Console.WriteLine($"      material {(mt is { } x ? Path.GetFileName(x.File) + "#" + x.Export + " " + MhoPackageModifier.Package.Open(x.File) switch { var pk => new Fx.FxTables(pk).PathOf(x.Export + 1) } : "none")}");
                            }
                        }
                        foreach (var n in missing) Console.WriteLine("    " + n);
                    }
                }
                if (table != null) File.WriteAllLines(Environment.GetEnvironmentVariable("MHO_CENSUS_OUT")!, table.Prepend("# Summoned characters' packages (--summon-census with MHO_CENSUS_OUT, on the stock packages): file, hero, summon or actor, power, kind (summon, power, entity), mesh, bones, power icon, mesh path; kind copy = the same model under the same path in another package"));
                Console.WriteLine($"{found} power(s) with summons");
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
                Console.WriteLine($"{rest[1]}: particles {string.Join(", ", fx.Effects.Where(e => e.BeamTarget == null).GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}"))}; beams {fx.Effects.Count(e => e.BeamTarget != null)}; decals {fx.Decals.Count} ({fx.Decals.Count(d => d.Tex != null)} with a texture); weapon slots {string.Join(" ", fx.Slots.Select(x => (x.Show != null ? "+" + x.Show : "") + (x.Hide != null ? " -" + x.Hide : "")))}; thrown {fx.ThrownSlot ?? "-"}; animation aliases {(fx.AnimAliases.Count > 0 ? string.Join(" ", fx.AnimAliases.Select(x => x.Alias + (x.By != null ? "(" + x.By + ")" : ""))) : "-")}; mesh emitters {fx.Meshes.Count}; contact {fx.ContactPercent:0.##}{(fx.Returning ? ", returning" : "")}  ({sw.ElapsedMilliseconds} ms)");
                foreach (var e in fx.Effects)
                    Console.WriteLine($"  {e.Kind,-10}{(e.OnHit ? " [on the one hit]" : "")} {e.Name}: {e.System.Name} ({e.System.Emitters.Count} emitters, {e.System.Emitters.Count(em => fx.Looks.TryGetValue(em, out var lk) && lk.Tex != null)} textured) at {(e.AtTarget ? "the target" : string.Join("/", e.Sockets.DefaultIfEmpty("root")))}, {e.Point}+{e.Offset:0.##}{(e.BeamTarget != null ? " beam to " + e.BeamTarget : "")}{(e.TriggeredBy != null ? " · by " + e.TriggeredBy : "")} · in {(e.System.Emitters.Count > 0 ? e.System.Emitters[0].Required.P.Name : "?")}");
                foreach (var ac in fx.Actors) Console.WriteLine($"  actor {ac.Kind} {ac.Name}: {Path.GetFileName(ac.MeshFile)}#{ac.MeshExport}{(ac.TriggeredBy != null ? " · by " + ac.TriggeredBy : "")}{(ac.Materials is { Count: > 0 } am ? $" · {am.Count} own material(s)" : "")}");
                foreach (var n in fx.Notes.Distinct().Take(6)) Console.WriteLine("    note: " + n);
                return 0;
            }
            case "--swap-material":
            {
                // Test (writes only <out.upk>; 2026-10-15, dark power colors): every object property that names <from> (a
                // material, by path) names <to> instead. --swap-material <in.upk> <from path> <to path> <out.upk>
                if (rest.Count < 5) { Console.WriteLine("--swap-material <in.upk> <from path> <to path> <out.upk>"); return 1; }
                var sp = MhoPackageModifier.Package.Open(rest[1]);
                var st = new Fx.FxTables(sp);
                int Ref(string path)
                {
                    for (int k = 0; k < st.Exports.Count; k++) if (st.PathOf(k + 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return k + 1;
                    for (int k = 0; k < st.Imports.Count; k++) if (st.PathOf(-k - 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return -k - 1;
                    return 0;
                }
                int from = Ref(rest[2]), to = Ref(rest[3]);
                if (from == 0 || to == 0) { Console.WriteLine($"not found: {(from == 0 ? rest[2] : rest[3])}"); return 1; }
                var swaps = new Dictionary<int, Func<long, byte[]>>();
                int n = 0;
                for (int k = 0; k < st.Exports.Count; k++)
                {
                    var e = st.Exports[k];
                    var props = Fx.FxProps.Find(sp.Body, st, e)?.Props;
                    if (props == null) continue;
                    byte[]? d = null;
                    foreach (var pr in props.Where(x => x.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && x.Size == 4))
                        if (BitConverter.ToInt32(sp.Body, pr.ValueAt) == from)
                        {
                            d ??= sp.ReadExportBytes(sp.Exports[k]).ToArray();
                            BitConverter.GetBytes(to).CopyTo(d, pr.ValueAt - e.SerialOffset);
                            n++;
                            Console.WriteLine($"  {st.PathOf(k + 1)}.{pr.Name}");
                        }
                    if (d != null) { var dd = d; swaps[k] = _ => dd; }
                }
                File.WriteAllBytes(rest[4], MhoPackageModifier.PackageRebuilder.Rebuild(sp, swaps, [], out _));
                Console.WriteLine($"{n} reference(s) swapped -> {rest[4]}");
                return n > 0 ? 0 : 1;
            }
            case "--material-census":
            {
                // Read-only: every base material in the game's packages (blend mode, lighting, particle usage, parameters, how
                // many packages and instances use it), and the darkening candidates for dark power colors. --material-census <out.tsv>
                string? mgr = settings.ResolvedGameRoot(data);
                string? mcook = mgr != null && Settings.IsGameRoot(mgr) ? Settings.Cooked(mgr) : null;
                if (mcook == null || rest.Count < 2) { Console.WriteLine("--material-census <out.tsv> (needs the game folder)"); return 1; }
                return Fx.MaterialCensus.Run(UniqueNames.Folder(mcook), rest[1], rest.Count > 2 ? rest[2] : null);
            }
            case "--own-homes":
            {
                // Read-only: for every hero / costume package of the game, where its always-on effects are (PowerColorBuild.OwnHome:
                // itself, its hero's package, or none). --own-homes
                string? hgr = settings.ResolvedGameRoot(data);
                if (hgr == null || !Settings.IsGameRoot(hgr)) { Console.WriteLine("game folder not found"); return 1; }
                StockFiles.Init(new GameState(hgr, lib.DataFolder), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                string hcook = Settings.Cooked(hgr);
                int own = 0, parent = 0, none = 0;
                foreach (string f in Directory.EnumerateFiles(hcook, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                    .Where(f => PowerColorBuild.IsHero(f) && !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                {
                    string? path = StockFiles.For(hcook, f);
                    string? home = path == null ? null : PowerColorBuild.OwnHome(f, path);
                    if (home == null) { none++; continue; }
                    if (home.Equals(f, StringComparison.OrdinalIgnoreCase)) { own++; Console.WriteLine($"own     {f}"); }
                    else { parent++; Console.WriteLine($"parent  {f} → {home}"); }
                }
                Console.WriteLine($"{own} with always-on effects of their own, {parent} taking them from their hero's package, {none} without");
                return 0;
            }
            case "--mirror-fix-mod":
            {
                // Test (a scratch MHO_EXTMM_HOME only): a mod's mirror tables fixed and saved as the CRASH RISK dialog does.
                if (rest.Count < 2) { Console.WriteLine("--mirror-fix-mod <mod> (scratch MHO_EXTMM_HOME)"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") is not string home || home.Replace('\\', '/').Contains("publish/data", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("scratch MHO_EXTMM_HOME only"); return 1; }
                if (lib.Find(rest[1]) is not { } fm) { Console.WriteLine("no such mod"); return 1; }
                var flog = new List<string>();
                var fdone = MirrorTables.Apply(lib, fm, flog, out string? ferr);
                flog.ForEach(x => Console.WriteLine("  " + x));
                Console.WriteLine(fdone == null ? "not changed: " + ferr : "saved: " + string.Join(", ", fdone));
                return fdone == null ? 1 : 0;
            }
            case "--mirror-fix":
            {
                // A package's skeletal meshes with their mirror tables one entry per bone (MirrorTables.Fix), written to <out>
                // (never into the game folder). --mirror-fix <package.upk> <out.upk>
                if (rest.Count < 3) { Console.WriteLine("--mirror-fix <package.upk> <out.upk>"); return 1; }
                Model.Protected.CheckWrite(rest[2]);
                var fixedPkg = MirrorTables.Fix(rest[1]);
                if (fixedPkg == null) { Console.WriteLine("nothing to fix"); return 0; }
                fixedPkg.Value.Changes.ForEach(c => Console.WriteLine("  " + c));
                File.WriteAllBytes(rest[2], fixedPkg.Value.Bytes);
                Console.WriteLine("wrote " + rest[2]);
                return 0;
            }
            case "--crash-check":
            {
                // Read-only: the crash checks (CrashCheck) on a mod, or every mod with "all": added bones, other copies of a
                // model with fewer bones, mirror tables, sockets. --crash-check <mod | all | package.upk ...>
                if (rest.Count < 2) { Console.WriteLine("--crash-check <mod | all>"); return 1; }
                string? cgr = settings.ResolvedGameRoot(data);
                string? ccook = cgr != null && Settings.IsGameRoot(cgr) ? Settings.Cooked(cgr) : null;
                if (ccook == null) { Console.WriteLine("game folder not found"); return 1; }
                StockFiles.Init(new GameState(cgr!, lib.DataFolder), settings.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                if (rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase))
                {
                    // package files (a build, a made-unique copy): checked as one mod's packages
                    foreach (var i in CrashCheck.For(rest.Skip(1).Select(f => (Path.GetFileName(f), f)).ToList(), ccook))
                        Console.WriteLine($"  {(i.Crash ? "CRASH RISK" : "warning   ")} {i.Package} | {i.Mesh}: {i.Text}");
                    Console.WriteLine("checked");
                    return 0;
                }
                var which = rest[1].Equals("all", StringComparison.OrdinalIgnoreCase) ? lib.Mods.ToList() : lib.Find(rest[1]) is { } one ? [one] : new List<Mod>();
                if (which.Count == 0) { Console.WriteLine("no such mod"); return 1; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int risky = 0, warned = 0;
                foreach (var m in which)
                {
                    var issues = CrashCheck.Check(m, ccook);
                    if (issues.Count == 0) continue;
                    if (issues.Any(i => i.Crash)) risky++; else warned++;
                    Console.WriteLine($"== {m.Name}");
                    foreach (var i in issues.OrderByDescending(i => i.Crash)) Console.WriteLine($"  {(i.Crash ? "CRASH RISK" : "warning   ")} {i.Package} | {i.Mesh}: {i.Text}");
                }
                Console.WriteLine($"{which.Count} mod(s) checked in {sw.Elapsed.TotalSeconds:0.0} s: {risky} with a crash risk, {warned} with warnings only");
                return 0;
            }
            case "--fx-placement-census":
            {
                // Read-only test: every hero's power buttons, each effect with where the preview plays it (kind, on the one
                // hit, sockets / root / the target). Run twice (MHO_FX_OLDPLACE=1 for the rules before) and compare.
                // --fx-placement-census <out.txt> [hero …]
                if (rest.Count < 2) { Console.WriteLine("--fx-placement-census <out.txt> [hero …]"); return 1; }
                string? pgr = settings.ResolvedGameRoot(data);
                string? pcook = pgr != null && Settings.IsGameRoot(pgr) ? Settings.Cooked(pgr) : null;
                if (pcook == null) { Console.WriteLine("game folder not found"); return 1; }
                var db = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(pgr!, "Data", "Game", "Calligraphy.sip")));
                var game = new Fx.FxGame(pcook, []);
                var heroes = rest.Count > 2 ? rest.Skip(2).ToList()
                    : Directory.EnumerateFiles(pcook, "UC__MarvelPlayer_*_SF.upk").Select(f => Path.GetFileNameWithoutExtension(f)[17..^3]).Where(h => h.All(char.IsLetter)).ToList();
                var lines = new List<string>();
                foreach (string hero in heroes)
                    foreach (var pw in Fx.PowerList.For(db, hero, pcook, [], effectOnly: true))
                    {
                        lines.Add($"== {hero} {pw.Name} ({Path.GetFileNameWithoutExtension(pw.Prototype)})");
                        try
                        {
                            var fx = Fx.PowerEffects.For(game, db, pw.Prototype, hero);
                            foreach (var e in fx.Effects)
                                lines.Add($"  {e.Kind} {(e.OnHit ? "[hit] " : "")}{e.Name} {e.System.Name} at {(e.OnHit ? "hit" : e.AtTarget ? "target" : string.Join("/", e.Sockets.DefaultIfEmpty("root")))}{(e.TriggeredBy != null ? " by " + e.TriggeredBy : "")}");
                        }
                        catch (Exception ex) { lines.Add("  error: " + ex.Message); }
                    }
                File.WriteAllLines(rest[1], lines);
                Console.WriteLine($"{heroes.Count} heroes, {lines.Count(l => l.StartsWith("=="))} powers, {lines.Count(l => !l.StartsWith("=="))} effects → {rest[1]}");
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
                    foreach (string fp in PowerRecolor.Family(qdb, pw.Prototype, rest[1]))   // (with the hero's same-named powers)
                    {
                        if (Fx.PowerEffects.UnrealClassOf(qdb, fp) is string own) classes.Add((own, fp));
                        foreach (var art in Fx.PowerClosure.Of(qdb, fp)) if (!string.IsNullOrEmpty(art.Class)) classes.Add((art.Class, art.Prototype));
                    }
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
                    // --own: only the package's always-on effects (a hero's: Ghost Rider's flaming skull); --opacity N (0-1)
                    if (rest.IndexOf("--opacity") is int oi and > 0 && oi + 1 < rest.Count) color = color with { Opacity = float.Parse(rest[oi + 1], System.Globalization.CultureInfo.InvariantCulture) };
                    byte[]? built = PowerRecolor.Build(stock, color, Settings.Cooked(rgr), l => Console.WriteLine("  " + l), onlyOwnSystems: rest.Contains("--own"));
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
                var boneWins = new Dictionary<string, (int Stored, int Mirrored)>(StringComparer.OrdinalIgnoreCase);
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
                                    var bw = boneWins.GetValueOrDefault(bone);
                                    boneWins[bone] = nd < nc ? (bw.Stored + 1, bw.Mirrored) : nc < nd ? (bw.Stored, bw.Mirrored + 1) : bw;
                                }
                                byBone[bone] = byBone.GetValueOrDefault(bone) + 1;
                                if (examples.Count < 12) examples.Add($"{Path.GetFileName(path)} {an} {bone}: {deg:0}° apart");
                            }
                        }
                    }
                }
                Console.WriteLine($"{tracks} single-key tracks of posed bones; the bind-pose rule picks the mirrored reading (2°+ apart) on {flipped}");
                Console.WriteLine($"  of those, closer to the same bone's multi-key poses: the stored reading {storedWins}, the mirrored one {mirroredWins}");
                foreach (var (b, n) in byBone.OrderByDescending(x => x.Value).Take(400))
                    Console.WriteLine($"  {b}: {n}" + (boneWins.TryGetValue(b, out var w) ? $" (closer to its multi-key poses: stored {w.Stored}, mirrored {w.Mirrored})" : " (no multi-key poses)"));
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
            case "--fx-hit-census":
            {
                // Read-only: every hero's powers (the 3D preview's power buttons), each effect the preview would put on the hero at
                // socket_hit or socket_head without playing it on the one hit (candidates for a wrong place), and the beams with
                // their ends. --fx-hit-census [hero ...]
                string? cgr = settings.ResolvedGameRoot(data);
                string? ccook = cgr != null && Settings.IsGameRoot(cgr) ? Settings.Cooked(cgr) : null;
                if (ccook == null) { Console.WriteLine("game folder not found"); return 1; }
                var cdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(cgr!, "Data", "Game", "Calligraphy.sip")));
                var cheroes = rest.Count > 1 ? rest.Skip(1).ToList()
                    : Directory.EnumerateFiles(ccook, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileNameWithoutExtension)
                        .Select(n => n!.Split('_', StringSplitOptions.RemoveEmptyEntries)).Where(q => q.Length == 4).Select(q => q[2]).Order(StringComparer.OrdinalIgnoreCase).ToList();
                var cg = new Fx.FxGame(ccook, []);
                int powers = 0, onHit = 0, left = 0, beams = 0, leftOut = 0;
                foreach (string hero in cheroes)
                {
                    List<Fx.PowerList.Power> list;
                    try { list = Fx.PowerList.For(cdb, hero, ccook, [], effectOnly: true); } catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
                    foreach (var pw in list)
                    {
                        powers++;
                        Fx.PowerEffects fx;
                        try { fx = Fx.PowerEffects.For(cg, cdb, pw.Prototype, hero); } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { continue; }
                        onHit += fx.Effects.Count(e => e.OnHit);
                        if (fx.Effects.Count == 0 && pw.Animations.Count > 0)
                            Console.WriteLine($"{hero} · {pw.Name}: NO EFFECTS ({Path.GetFileNameWithoutExtension(pw.Prototype)}; {string.Join(" | ", fx.Notes.Take(3))})");
                        leftOut += fx.Notes.Count(n => n.Contains("its own animation"));
                        foreach (var e in fx.Effects)
                        {
                            if (e.BeamTarget != null) { beams++; Console.WriteLine($"{hero} · {pw.Name}: beam {e.Name} {string.Join("/", e.Sockets)} → {e.BeamTarget}"); }
                            if (e.OnHit || e.AtTarget) continue;
                            if (!e.Sockets.Any(n => n.Equals("socket_hit", StringComparison.OrdinalIgnoreCase))) continue;
                            left++;
                            string lin = e.TriggeredBy != null && Fx.PowerClosure.Of(cdb, pw.Prototype).FirstOrDefault(a => Path.GetFileNameWithoutExtension(a.Prototype).Equals(e.TriggeredBy, StringComparison.OrdinalIgnoreCase)) is { } art
                                && cdb.Find(art.Prototype) is { } ape ? Fx.PowerClosure.Lineage(cdb, ape.Id) : "";
                            Console.WriteLine($"{hero} · {pw.Name}: ON THE HERO {e.Kind} {e.Name} {e.System.Name} at {string.Join("/", e.Sockets)} ({e.Point}){(e.TriggeredBy != null ? " by " + e.TriggeredBy : "")} [{lin}]");
                        }
                    }
                }
                Console.WriteLine($"{cheroes.Count} heroes, {powers} powers: {onHit} effects play on the one hit, {left} at socket_hit stay on the hero, {beams} beams, {leftOut} other powers left out");
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
            case "--target-census":
            {
                // Read-only (2026-10-05): the NPC and enemy / boss packages whose character (the mesh its component shows,
                // MhoSkeleton's choice) has the humanoid skeleton the Model tab fits models to: g_pelvis, g_head, both hips
                // and at least 90 % of the 81 core bones Storm has. Writes "file<TAB>core<TAB>bones<TAB>mesh" lines.
                // --target-census <CookedPCConsole> <out.txt>
                if (rest.Count < 3) { Console.WriteLine("--target-census <CookedPCConsole> <out.txt>"); return 1; }
                var refSk = MhoExtendedModManager.Model.MhoSkeleton.Load(Path.Combine(rest[1], "UC__MarvelPlayer_Storm_SF.upk"));
                var core = refSk.Bones.Select(b => b.Name).Where(n => n.StartsWith("g_") && !n.Contains("cape") && !n.Contains("hair") && !n.Contains("wing")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var lines = new List<string>();
                int seen = 0;
                foreach (string pattern in new[] { "UC__MarvelNPC_*_SF.upk", "UC__MarvelAgent_*_SF.upk" })
                    foreach (string f in Directory.EnumerateFiles(rest[1], pattern).OrderBy(x => x))
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                        seen++;
                        MhoExtendedModManager.Model.MhoSkeleton sk;
                        try { sk = MhoExtendedModManager.Model.MhoSkeleton.Load(f); } catch (Exception) { continue; }
                        var names = sk.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        int have = names.Count(core.Contains);
                        bool ok = have * 10 >= core.Count * 9 && new[] { "g_pelvis", "g_head", "g_l_hip", "g_r_hip" }.All(names.Contains);
                        if (ok) lines.Add($"{fn}	{have}	{sk.Bones.Count}	{sk.Mesh.Name}");
                    }
                File.WriteAllLines(rest[2], lines);
                Console.WriteLine($"{seen} packages, {lines.Count} with the humanoid skeleton → {rest[2]}");
                return 0;
            }
            case "--skeleton-census":
            {
                // Read-only (2026-10-05, Model tab targets beyond player characters): each matching package's skeletal meshes,
                // how many of the 80 humanoid core bones (the ones Storm and Thor share) each has. --skeleton-census
                // <CookedPCConsole> <file pattern> [reference package with the humanoid skeleton]
                if (rest.Count < 3) { Console.WriteLine("--skeleton-census <CookedPCConsole> <file pattern> [reference.upk]"); return 1; }
                string refPkg = rest.Count > 3 ? rest[3] : Path.Combine(rest[1], "UC__MarvelPlayer_Storm_SF.upk");
                var refMesh = ModMeshes.List([(Path.GetFileName(refPkg), refPkg)], anyPackage: true).Select(m => ModMeshes.Load(m, null, out _)).FirstOrDefault(x => x != null);
                if (refMesh == null) { Console.WriteLine("no reference skeleton"); return 1; }
                var core = refMesh.Bones.Select(b => b.Name).Where(n => n.StartsWith("g_") && !n.Contains("cape") && !n.Contains("hair") && !n.Contains("wing")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var buckets = new SortedDictionary<int, int>();
                int files = 0, meshes = 0;
                foreach (string f in Directory.EnumerateFiles(rest[1], rest[2]).OrderBy(x => x))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    files++;
                    List<MeshRef> list;
                    try { list = ModMeshes.List([(fn, f)], anyPackage: true).ToList(); } catch (Exception) { continue; }
                    foreach (var mr in list.Take(1))
                    {
                        ModMeshes.Loaded? ld;
                        try { ld = ModMeshes.Load(mr, null, out _); } catch (Exception) { continue; }
                        if (ld == null) continue;
                        meshes++;
                        int have = ld.Bones.Count(b => core.Contains(b.Name));
                        int pct = have * 100 / core.Count / 10 * 10;
                        buckets[pct] = buckets.GetValueOrDefault(pct) + 1;
                        Console.WriteLine($"{fn} | {mr.Name} | {ld.Bones.Count} bones | {have} of {core.Count} core");
                    }
                }
                Console.WriteLine($"{files} packages, {meshes} with a skeletal mesh; core bones present (percent bucket: count):");
                foreach (var kv in buckets.Reverse()) Console.WriteLine($"  {kv.Key,3}%+  {kv.Value}");
                return 0;
            }
            case "--agent-powers":
            {
                // Read-only: an NPC's / enemy's powers (Fx.AgentPowers): its prototypes, the powers they name, and per power
                // the packages a recolor writes. --agent-powers <class, e.g. MarvelAgent_CloneWolverine>
                string? agr = settings.ResolvedGameRoot(data);
                if (rest.Count < 2 || agr == null) { Console.WriteLine("--agent-powers <class>"); return 1; }
                string acook = Settings.Cooked(agr);
                var adb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(agr, "Data", "Game", "Calligraphy.sip")));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (string p in Fx.AgentPowers.PrototypesOf(adb, rest[1])) Console.WriteLine("  character: " + p);
                Console.WriteLine($"  ({clock.ElapsedMilliseconds} ms)");
                foreach (var pw in Fx.AgentPowers.List(adb, rest[1], acook, "x"))
                    Console.WriteLine($"  {pw.Name[3..]} | {pw.Prototype} | anims {string.Join(",", pw.Animations.Take(4))} | packages {string.Join(", ", PowerRecolor.PackagesOfAgent(adb, pw.Prototype, rest[1], acook))}");
                Console.WriteLine($"  all powers named: {Fx.AgentPowers.PowersOf(adb, rest[1]).Count} ({clock.ElapsedMilliseconds} ms)");
                return 0;
            }
            case "--own-fx":
            {
                // Read-only: a character package's own always-on effects (PowerEffects.Own): each effect, its sockets and emitters.
                // --own-fx <package.upk>
                string? ogr2 = settings.ResolvedGameRoot(data);
                if (rest.Count < 2 || ogr2 == null) { Console.WriteLine("--own-fx <package.upk>"); return 1; }
                var ofx = Fx.PowerEffects.Own(new Fx.FxGame(Settings.Cooked(ogr2), [rest[1]]), Path.GetFileName(rest[1]));
                foreach (var e in ofx.Effects) Console.WriteLine($"  {e.Name}: {e.System.Emitters.Count} emitter(s) at [{string.Join(", ", e.Sockets)}]");
                foreach (var n in ofx.Notes) Console.WriteLine("  note: " + n);
                Console.WriteLine($"{ofx.Effects.Count} effect(s)");
                return 0;
            }
            case "--parent-package":
            {
                // Read-only: the NPC / enemy package a package's class extends (PowerColorBuild.ParentPackage). --parent-package <package.upk> [...]
                string? ppr = settings.ResolvedGameRoot(data);
                if (ppr == null) { Console.WriteLine("game folder not found"); return 1; }
                foreach (string f in rest.Skip(1)) Console.WriteLine($"{Path.GetFileName(f)} → {PowerColorBuild.ParentPackage(f, Settings.Cooked(ppr)) ?? "(none)"}");
                return 0;
            }
            case "--fx-emitters":
            {
                // Read-only: each particle system in packages and how many of its emitters draw (an enabled LOD level).
                // --fx-emitters <package.upk> [...]
                foreach (string f in rest.Skip(1))
                {
                    var fp = MhoPackageModifier.Package.Open(f); var ft = new Fx.FxTables(fp); var ff = new Fx.FxPkg(Path.GetFileName(f), fp.Body, ft);
                    int sys = 0, draw = 0;
                    for (int i = 0; i < ft.Exports.Count; i++)
                    {
                        if (!ft.ClassOf(ft.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) continue;
                        sys++;
                        int n = Fx.ParticleData.Read(ff, i)?.Emitters.Count ?? 0; draw += n;
                        Console.WriteLine($"  {n,3}  {ft.PathOf(i + 1)}");
                    }
                    Console.WriteLine($"{Path.GetFileName(f)}: {sys} particle system(s), {draw} emitter(s) that draw");
                }
                return 0;
            }
            case "--imports":
            {
                // Read-only: a package's imports (-index, class, path). --imports <package.upk> [filter]
                if (rest.Count < 2) { Console.WriteLine("--imports <package.upk> [filter]"); return 1; }
                var ip = MhoPackageModifier.Package.Open(rest[1]);
                for (int i = 0; i < ip.Imports.Length; i++)
                {
                    string path = ip.RefName(-1 - i);
                    for (int o = ip.Imports[i].OuterIndex; o != 0;) { if (o < 0) { path = ip.Imports[-o - 1].ObjectName + "." + path; o = ip.Imports[-o - 1].OuterIndex; } else { path = ip.PathOf(ip.Exports[o - 1]) + "." + path; break; } }
                    if (rest.Count > 2 && !path.Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    Console.WriteLine($"{-1 - i,6}  {ip.Imports[i].ClassName,-28} {path}");
                }
                return 0;
            }
            case "--tag-census":
            {
                // Read-only (2026-10-05, the scale slider: do stock characters scale their mesh component?): every export of the
                // packages matching a pattern that has a property of these names, with its value. Skips bak / copy files.
                // --tag-census <CookedPCConsole> <file pattern, e.g. UC__Marvel*_SF.upk> <property name> [more names]
                if (rest.Count < 4) { Console.WriteLine("--tag-census <CookedPCConsole> <file pattern> <property> [...]"); return 1; }
                var want = rest.Skip(3).ToHashSet(StringComparer.OrdinalIgnoreCase);
                int files = 0, hits = 0;
                var byValue = new Dictionary<string, int>();
                foreach (string f in Directory.EnumerateFiles(rest[1], rest[2]).OrderBy(x => x))
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    MhoPackageModifier.Package pk;
                    try { pk = MhoPackageModifier.Package.Open(f); } catch (Exception) { continue; }
                    files++;
                    for (int i = 0; i < pk.Exports.Length; i++)
                    {
                        string cls = pk.ClassOf(pk.Exports[i]);
                        if (cls.Equals("Class", StringComparison.OrdinalIgnoreCase) || cls.Contains("Texture", StringComparison.OrdinalIgnoreCase) || cls.Contains("Material", StringComparison.OrdinalIgnoreCase)) continue;
                        if (cls.Contains("AnimSequence", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("MHO_CENSUS_ANIMS") != "1") continue;
                        byte[] d;
                        try { d = pk.ReadExportBytes(pk.Exports[i]).ToArray(); } catch (Exception) { continue; }
                        foreach (int start in new[] { 4, 16, 8 })
                        {
                            MhoPackageModifier.TagWalker? tags;
                            try { tags = MhoPackageModifier.TagWalker.Walk(pk, d, start); } catch (Exception) { continue; }
                            if (tags == null) continue;
                            foreach (var t in tags.Where(t => want.Contains(t.Name)))
                            {
                                string v = t.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) ? BitConverter.ToSingle(d, t.ValueAt).ToString("0.###")
                                    : t.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) && t.Size >= 12 ? string.Join(" ", Enumerable.Range(0, 3).Select(k => BitConverter.ToSingle(d, t.End - t.Size + k * 4).ToString("0.###")))
                                    : $"[{t.Type} {t.Size}]";
                                hits++;
                                byValue[$"{t.Name}={v}"] = byValue.GetValueOrDefault($"{t.Name}={v}") + 1;
                                if (hits <= 400) Console.WriteLine($"{fn} | {pk.PathOf(pk.Exports[i])} ({cls}) | {t.Name} = {v}");
                            }
                            break;
                        }
                    }
                }
                Console.WriteLine($"{files} packages, {hits} hit(s)");
                foreach (var kv in byValue.OrderByDescending(kv => kv.Value).Take(40)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
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
            case "--bone-rest":
            {
                // Read-only: each skeletal mesh in a package with the rest pose (local rotation, position) of bones whose
                // name contains one of the given parts. --bone-rest <package.upk> <name part>...
                if (rest.Count < 3) { Console.WriteLine("--bone-rest <package.upk> <name part>..."); return 1; }
                foreach (var mr in ModMeshes.List([(Path.GetFileName(rest[1]), rest[1])]))
                {
                    var ld = ModMeshes.Load(mr, null, out _);
                    if (ld == null) continue;
                    Console.WriteLine($"{mr.Name}: {ld.Bones.Count} bones");
                    foreach (var b in ld.Bones.Where(b => rest.Skip(2).Any(w => b.Name.Contains(w, StringComparison.OrdinalIgnoreCase))))
                    {
                        var q = b.Orientation;
                        Console.WriteLine($"  {b.Name,-20} parent {(b.ParentIndex >= 0 && b.ParentIndex < ld.Bones.Count ? ld.Bones[b.ParentIndex].Name : "-"),-16} rot ({q.X:0.000}, {q.Y:0.000}, {q.Z:0.000}, {q.W:0.000})  pos ({b.Position.X:0.0}, {b.Position.Y:0.0}, {b.Position.Z:0.0})");
                    }
                }
                return 0;
            }
            case "--name-at":
            {
                // Read-only: a package's names by index (hex or decimal). --name-at <package.upk> <index>...
                if (rest.Count < 3) { Console.WriteLine("--name-at <package.upk> <index>..."); return 1; }
                var np = MhoPackageModifier.Package.Open(rest[1]);
                foreach (string ix in rest.Skip(2))
                {
                    int k = ix.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(ix[2..], 16) : int.Parse(ix);
                    Console.WriteLine($"{ix}: {(k >= 0 && k < np.Names.Length ? np.Names[k] : "?")}");
                }
                return 0;
            }
            case "--hold-turn":
            {
                // Read-only: the turn that keeps a held prop facing as in the donor's hand (PowerBorrow.HoldTurn).
                // --hold-turn <donor hero package> <target hero package> <bone>
                if (rest.Count < 4) { Console.WriteLine("--hold-turn <donor hero package> <target hero package> <bone>"); return 1; }
                if (PowerBorrow.HoldTurn(rest[1], rest[2], rest[3]) is not { } ht) { Console.WriteLine("a skeleton lacks the bone"); return 1; }
                var hq = System.Numerics.Quaternion.CreateFromRotationMatrix(ht);
                float ang = 2 * MathF.Acos(Math.Clamp(hq.W, -1, 1)) * 180 / MathF.PI, sn = MathF.Sqrt(Math.Max(1e-9f, 1 - hq.W * hq.W));
                Console.WriteLine($"turn {ang:0.0} degrees about ({hq.X / sn:0.00}, {hq.Y / sn:0.00}, {hq.Z / sn:0.00})");
                Console.WriteLine($"rows: ({ht.M11:0.00} {ht.M12:0.00} {ht.M13:0.00}) ({ht.M21:0.00} {ht.M22:0.00} {ht.M23:0.00}) ({ht.M31:0.00} {ht.M32:0.00} {ht.M33:0.00})");
                return 0;
            }
            case "--set-anim-tree":
            {
                // Test (writes the given file, never the game folder): a character component's AnimTreeTemplate set to a shared
                // tree in Startup.upk (Kurt, 2026-10-22: Cyclops' tilt on his bike may come from his pc_at_v5).
                // --set-anim-tree <package.upk> <component path> <tree outer> <tree name>
                if (rest.Count < 5) { Console.WriteLine("--set-anim-tree <package.upk> <component path> <tree outer> <tree name>"); return 1; }
                if (Settings.IsGameRoot(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rest[1])))) ?? "")) { Console.WriteLine("refused: a game file"); return 1; }
                var tp = MhoPackageModifier.Package.Open(rest[1]);
                int ci = Array.FindIndex(tp.Exports, e => tp.PathOf(e).Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (ci < 0) { Console.WriteLine("no " + rest[2]); return 1; }
                var tNames = new List<string>();
                var tImports = new List<MhoPackageModifier.NewImport>();
                int TName(string n)
                {
                    int i = Array.FindIndex(tp.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) return i;
                    if (!tNames.Contains(n, StringComparer.OrdinalIgnoreCase)) tNames.Add(n);
                    return tp.Names.Length + tNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                }
                int outerIdx = Array.FindIndex(tp.Imports, im => im.ObjectName.Equals(rest[3], StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase) && im.OuterIndex == 0);
                int outerRef;
                if (outerIdx >= 0) outerRef = -1 - outerIdx;
                else
                {
                    foreach (string n in new[] { "Core", "Package", rest[3] }) TName(n);
                    tImports.Add(new MhoPackageModifier.NewImport("Core", "Package", 0, rest[3]));
                    outerRef = -(tp.Imports.Length + tImports.Count);
                }
                int treeIdx = Array.FindIndex(tp.Imports, im => im.ObjectName.Equals(rest[4], StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("AnimTree", StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outerRef);
                int treeRef;
                if (treeIdx >= 0) treeRef = -1 - treeIdx;
                else
                {
                    foreach (string n in new[] { "Engine", "AnimTree", rest[4] }) TName(n);
                    tImports.Add(new MhoPackageModifier.NewImport("Engine", "AnimTree", outerRef, rest[4]));
                    treeRef = -(tp.Imports.Length + tImports.Count);
                }
                byte[] cd = tp.ReadExportBytes(tp.Exports[ci]).ToArray();
                var ctags = MhoPackageModifier.TagWalker.Walk(tp, cd, 16);
                var tt = ctags?.FirstOrDefault(t => t.Name.Equals("AnimTreeTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
                if (tt == null) { Console.WriteLine("the component has no AnimTreeTemplate of its own"); return 1; }
                { int was = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(tt.ValueAt)); Console.WriteLine("AnimTreeTemplate was " + (was < 0 ? tp.Imports[-was - 1].ObjectName : was.ToString())); }
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(cd.AsSpan(tt.ValueAt), treeRef);
                var trep = new Dictionary<int, Func<long, byte[]>> { [ci] = _ => cd };
                byte[] tout = MhoPackageModifier.PackageRebuilder.Rebuild(tp, trep, [], out var twritten, tNames, tImports);
                var tprob = MhoPackageModifier.PackageRebuilder.Verify(tp, tout, trep.Keys.ToList(), [], twritten, tNames, tImports);
                if (tprob.Count > 0) { Console.WriteLine("didn't verify: " + string.Join("; ", tprob.Take(3))); return 1; }
                File.WriteAllBytes(rest[1], tout);
                Console.WriteLine($"AnimTreeTemplate -> {rest[3]}.{rest[4]} ({tout.Length:N0} bytes)");
                return 0;
            }
            case "--script-refs":
            {
                // Read-only: what a compiled script function refers to (objects and names, in code order).
                // --script-refs <package.upk> <function path>
                if (rest.Count < 3) { Console.WriteLine("--script-refs <package.upk> <function path>"); return 1; }
                var sp = MhoPackageModifier.Package.Open(rest[1]);
                int fi = Array.FindIndex(sp.Exports, e => sp.PathOf(e).Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (fi < 0) { Console.WriteLine("no " + rest[2]); return 1; }
                byte[] fd = sp.ReadExportBytes(sp.Exports[fi]).ToArray();
                MhoPackageModifier.ScriptCode.UseNoneName(Array.FindIndex(sp.Names, n => n.Equals("None", StringComparison.OrdinalIgnoreCase)));
                var refs = MhoPackageModifier.ScriptCode.Function(fd, out string? ferr);
                if (refs == null) { Console.WriteLine("doesn't read: " + ferr); return 1; }
                var all = refs.Objects.Select(o => (o, true)).Concat(refs.Names.Select(n => (n, false))).OrderBy(x => x.Item1);
                foreach (var (at, obj) in all)
                {
                    int v = BitConverter.ToInt32(fd, at);
                    string what = obj ? (v == 0 ? "null" : v > 0 ? sp.PathOf(sp.Exports[v - 1]) : sp.RefName(v)) : (v >= 0 && v < sp.Names.Length ? "'" + sp.Names[v] + "'" : "?");
                    Console.WriteLine($"  {at,6}: {what}");
                }
                return 0;
            }
            case "--mesh-bones":
            {
                // Read-only: every skeletal mesh in the given packages with its bone names (skeleton comparisons).
                foreach (string f in rest.Skip(1))
                    foreach (var mr in ModMeshes.List([(Path.GetFileName(f), f)]))
                    {
                        var ld = ModMeshes.Load(mr, null, out string why);
                        Console.WriteLine(ld == null ? $"{mr.Package} | {mr.Name}: {why}" : $"{mr.Package} | {mr.Name} | {ld.Bones.Count} | " + string.Join(",", ld.Bones.Select(b => b.Name)));
                        if (ld != null && Environment.GetEnvironmentVariable("MHO_MESH_WEIGHTS") == "1")
                        {
                            // per bone: total weight and vertex count (which bones the skin follows)
                            var sum = new double[ld.Bones.Count]; var cnt = new int[ld.Bones.Count];
                            foreach (var inf in ld.Influences) for (int k = 0; k < inf.Bones.Count; k++) if (inf.Weights[k] > 0 && inf.Bones[k] < sum.Length) { sum[inf.Bones[k]] += inf.Weights[k]; cnt[inf.Bones[k]]++; }
                            for (int k = 0; k < sum.Length; k++) if (cnt[k] > 0) Console.WriteLine($"   {ld.Bones[k].Name,-28} {sum[k],9:0.0} {cnt[k],6}");
                        }
                    }
                return 0;
            }
            case "--weight-compare":
            {
                // Read-only (2026-10-06, Kamala's team-up thighs kinked): two skinned meshes on the same skeleton (stock, ours).
                // For each of ours vertices the nearest stock vertex (bind pose); per stock dominant bone (limbs only): how our
                // vertices there are weighted on average. --weight-compare <stock.upk> <ours.upk> [bone regex]
                if (rest.Count < 3) { Console.WriteLine("--weight-compare <stock.upk> <ours.upk> [bone regex]"); return 1; }
                ModMeshes.Loaded? L(string f) { var mr = ModMeshes.List([(Path.GetFileName(f), f)]).FirstOrDefault(); return mr == null ? null : ModMeshes.Load(mr, null, out _); }
                var sa = L(rest[1]); var oa = L(rest[2]);
                if (sa == null || oa == null) { Console.WriteLine("no skeletal mesh in one of them"); return 1; }
                var rx = new System.Text.RegularExpressions.Regex(rest.Count > 3 ? rest[3] : "(hip|knee|uprleg|thigh|lwrleg|shoulder|uprarm|bicep|elbow|lwrarm|clavical|pelvis|spine01)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string Bn(IReadOnlyList<AnimExportCli.Meshes.MeshBone> bs, int b) => b >= 0 && b < bs.Count ? bs[b].Name : "?";
                (int B, float W)[] Inf(ModMeshes.Loaded l, int v) => Enumerable.Range(0, l.Influences[v].Bones.Count).Where(k => l.Influences[v].Weights[k] > 0)
                    .Select(k => ((int)l.Influences[v].Bones[k], (float)l.Influences[v].Weights[k])).ToArray();
                var groups = new SortedDictionary<string, (int N, Dictionary<string, double> Ours, Dictionary<string, double> Stock)>();
                for (int v = 0; v < oa.Positions.Length; v++)
                {
                    int best = -1; float bd = float.MaxValue;
                    for (int s = 0; s < sa.Positions.Length; s++) { float d = System.Numerics.Vector3.DistanceSquared(sa.Positions[s], oa.Positions[v]); if (d < bd) { bd = d; best = s; } }
                    if (best < 0 || bd > 25) continue;
                    var si = Inf(sa, best);
                    if (si.Length == 0) continue;
                    string dom = Bn(sa.Bones, si.MaxBy(x => x.W).B);
                    if (!rx.IsMatch(dom)) continue;
                    if (!groups.TryGetValue(dom, out var g)) g = (0, new(), new());
                    g.N++;
                    foreach (var (b, w) in Inf(oa, v)) g.Ours[Bn(oa.Bones, b)] = g.Ours.GetValueOrDefault(Bn(oa.Bones, b)) + w;
                    foreach (var (b, w) in si) g.Stock[Bn(sa.Bones, b)] = g.Stock.GetValueOrDefault(Bn(sa.Bones, b)) + w;
                    groups[dom] = g;
                }
                string Top(Dictionary<string, double> d, int n) => string.Join(", ", d.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key} {x.Value / n:0.00}"));
                foreach (var (dom, g) in groups)
                    Console.WriteLine($"{dom} ({g.N} of ours)\n   stock: {Top(g.Stock, g.N)}\n   ours:  {Top(g.Ours, g.N)}");
                return 0;
            }
            case "--normal-map":
            {
                // Read-only (2026-10-07, a user: the generated normal maps are too strong): the normal map NormalMapGen makes from a
                // color map, with optional settings. --normal-map <color.png> <out.png> [strength fine broad fineBlur broadBlur]
                if (rest.Count < 3) { Console.WriteLine("--normal-map <color.png> <out.png> [strength fine broad fineBlur broadBlur]"); return 1; }
                var ns = new MhoExtendedModManager.Model.NormalMapSettings();
                float F(int i, float d) => rest.Count > i ? float.Parse(rest[i], System.Globalization.CultureInfo.InvariantCulture) : d;
                ns.Strength = F(3, ns.Strength); ns.Fine = F(4, ns.Fine); ns.Broad = F(5, ns.Broad); ns.FineBlur = F(6, ns.FineBlur); ns.BroadBlur = F(7, ns.BroadBlur);
                var (nw, nh, npx) = MhoExtendedModManager.Model.NormalMapGen.LoadArgb(rest[1]);
                MhoExtendedModManager.Model.NormalMapGen.SaveArgb(nw, nh, MhoExtendedModManager.Model.NormalMapGen.Make(nw, nh, npx, ns), rest[2]);
                Console.WriteLine($"{rest[2]}: {ns}");
                return 0;
            }
            case "--mesh-refs":
            {
                // --mesh-refs <CookedPCConsole> [name part] (read only): character packages (UC__Marvel*) with no skeletal mesh of
                // their own, and the mesh their class default's mesh component names (an import from another package)
                if (rest.Count < 2) { Console.WriteLine("--mesh-refs <CookedPCConsole> [name part]"); return 1; }
                string part = rest.Count > 2 ? rest[2] : "";
                int none = 0, total = 0;
                foreach (string f in Directory.EnumerateFiles(rest[1], "UC__Marvel*.upk").Order())
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Contains("bak", StringComparison.OrdinalIgnoreCase) || fn.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (part.Length > 0 && !fn.Contains(part, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var mp = MhoPackageModifier.Package.Open(f);
                        total++;
                        int meshes = mp.Exports.Count(e => mp.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
                        if (meshes > 0) continue;
                        string stem = Path.GetFileNameWithoutExtension(f)[4..];
                        if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
                        string compPath = $"marvelgamecontent.default__{stem.ToLowerInvariant()}.initialskeletalmesh";
                        int comp = Array.FindIndex(mp.Exports, e => mp.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
                        string refTo = "no component of its own";
                        if (comp >= 0)
                        {
                            byte[] d = mp.ReadExportBytes(mp.Exports[comp]).ToArray();
                            var t = MhoPackageModifier.TagWalker.Walk(mp, d, 16)?.FirstOrDefault(x => x.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
                            int r = t == null ? 0 : BitConverter.ToInt32(d, t.ValueAt);
                            if (r < 0)
                            {
                                var parts = new List<string>();
                                for (int guard = 0; r < 0 && guard < 16; guard++) { var im = mp.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex; }
                                refTo = "imports " + string.Join('.', parts);
                            }
                            else refTo = t == null ? "component sets no SkeletalMesh" : "SkeletalMesh = " + r;
                        }
                        none++;
                        Console.WriteLine($"{fn}	{refTo}");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or IndexOutOfRangeException) { }
                }
                Console.WriteLine($"{none} of {total} character packages have no skeletal mesh of their own");
                return 0;
            }
            case "--mesh-materials":
            {
                // Read-only (2026-10-07): a skeletal mesh's material slots (native list): each one's path, whether it's in the
                // package or imported, its class, the textures ModMaterials finds, and the textures under it (expressions).
                // --mesh-materials <upk> <mesh>
                if (rest.Count < 3) { Console.WriteLine("--mesh-materials <upk> <mesh>"); return 1; }
                var mmr = ModMeshes.List([(Path.GetFileName(rest[1]), rest[1])], anyPackage: true).FirstOrDefault(r => r.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (mmr == null) { Console.WriteLine("no such mesh"); return 1; }
                var mp = MhoPackageModifier.Package.Open(rest[1]);
                {
                    var apk = AnimExportCli.Packages.Package.Open(mmr.File);
                    if (AnimExportCli.Meshes.SkeletalMeshReader.TryRead(apk, mmr.Export, _ => { })?.HighestDetail is { } slod)
                        Console.WriteLine($"{ModMeshes.MaterialRefs(apk, mmr.Export).Count} material slot(s); sections use slots {string.Join(", ", slod.Sections.Select(s => s.MaterialIndex))}");
                }
                foreach (var (sec, mat) in ModMeshes.SectionMaterials(mmr))
                {
                    if (mat == 0) { Console.WriteLine($"section {sec}: none"); continue; }
                    if (mat < 0) { var im = mp.Imports[-mat - 1]; Console.WriteLine($"section {sec}: import {mp.RefName(mat)} ({im.ClassName}, outer {mp.RefName(im.OuterIndex)})"); continue; }
                    var ex = mp.Exports[mat - 1];
                    var info = ModMaterials.Read(mp, mat);
                    Console.WriteLine($"section {sec}: {mp.PathOf(ex)} ({mp.ClassOf(ex)}) parent {info?.Parent}; textures: {string.Join(", ", info?.Textures.Select(t => $"{t.Key}={mp.Exports[t.Value].ObjectName}") ?? [])}");
                    string prefix = mp.PathOf(ex) + ".";
                    foreach (var sub in mp.Exports.Where(x => mp.PathOf(x).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"    {mp.ClassOf(sub)} {sub.ObjectName}");
                }
                return 0;
            }
            case "--limb-profile":
            {
                // Read-only (2026-10-06): a skinned mesh's weights along a limb (bind pose): the vertices near the segment from one
                // bone to another, in tenths of its length, and their average weights. --limb-profile <upk> <from bone> <to bone>
                var lmr = ModMeshes.List([(Path.GetFileName(rest[1]), rest[1])]).FirstOrDefault();
                var lm = lmr == null ? null : ModMeshes.Load(lmr, null, out _);
                if (lm == null) { Console.WriteLine("no skeletal mesh"); return 1; }
                var anim = new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents) { InPlace = false };
                anim.Pose(null, 0);
                int ia = anim.BoneIndex(rest[2]), ib = anim.BoneIndex(rest[3]);
                if (ia < 0 || ib < 0) { Console.WriteLine("no such bone"); return 1; }
                var A = anim.BonePosition(ia); var B = anim.BonePosition(ib); var seg = B - A; float len = seg.Length();
                foreach (var bn in lm.Bones.Where(b => System.Text.RegularExpressions.Regex.IsMatch(b.Name, "forarm|lwrarm|elbow|wrist|uprarm|bicep|shoulder|hip|knee|thigh|uprleg|lwrleg")))
                {
                    int k = anim.BoneIndex(bn.Name); var bp = anim.BonePosition(k);
                    float bt = System.Numerics.Vector3.Dot(bp - A, seg) / (len * len);
                    if (bt > -0.3f && bt < 1.3f && (bp - (A + bt * seg)).Length() < 0.3f * len) Console.WriteLine($"  bone {bn.Name} at {bt:0.00}");
                }
                for (int bin = -2; bin < 12; bin++)
                {
                    var acc = new Dictionary<string, double>(); int n = 0;
                    for (int v = 0; v < lm.Positions.Length; v++)
                    {
                        float t = System.Numerics.Vector3.Dot(lm.Positions[v] - A, seg) / (len * len);
                        if (t < bin / 10f || t >= (bin + 1) / 10f || (lm.Positions[v] - (A + t * seg)).Length() > 0.45f * len) continue;
                        n++;
                        var inf = lm.Influences[v];
                        for (int q = 0; q < inf.Bones.Count; q++) if (inf.Weights[q] > 0) acc[lm.Bones[inf.Bones[q]].Name] = acc.GetValueOrDefault(lm.Bones[inf.Bones[q]].Name) + inf.Weights[q];
                    }
                    if (n > 0) Console.WriteLine($"{bin / 10f:0.0}..{(bin + 1) / 10f:0.0} ({n,4}): " + string.Join(", ", acc.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key} {x.Value / n:0.00}")));
                }
                return 0;
            }
            case "--mff-thigh-weights":
            {
                // Read-only (2026-10-06): an MFF model's own weights near the top of each thigh (between the hip joint and a third
                // of the way to the knee, within the thigh's reach): which of its bones carry them. --mff-thigh-weights <model>
                var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1]));
                int Fb(string n) => mm.Bones.FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                foreach (var bo in mm.Bones.Where(b => b.Deforms && (!b.Name.StartsWith("Bip001", StringComparison.OrdinalIgnoreCase) || b.Name.Contains("Twist") || b.Name.EndsWith(" 1"))))
                    Console.WriteLine($"  {bo.Name} parent {(bo.Parent >= 0 ? mm.Bones[bo.Parent].Name : "-")} at {bo.Position.X:0.0} {bo.Position.Y:0.0} {bo.Position.Z:0.0}");
                // MHO_BONES=<regex>: those bones instead, with parent and whether they deform and carry weight
                if (Environment.GetEnvironmentVariable("MHO_BONES") is string brx)
                {
                    var wsum = new double[mm.Bones.Count];
                    foreach (var s2 in mm.Parts.SelectMany(p2 => p2.Sections)) foreach (var ws in s2.Weights) foreach (var (bb, w) in ws) wsum[bb] += w;
                    for (int q = 0; q < mm.Bones.Count; q++)
                        if (System.Text.RegularExpressions.Regex.IsMatch(mm.Bones[q].Name, brx))
                            Console.WriteLine($"  [{q}] {mm.Bones[q].Name} parent {(mm.Bones[q].Parent >= 0 ? mm.Bones[mm.Bones[q].Parent].Name : "-")} at {mm.Bones[q].Position.X:0.0} {mm.Bones[q].Position.Y:0.0} {mm.Bones[q].Position.Z:0.0} deforms {mm.Bones[q].Deforms} weight {wsum[q]:0.0}");
                    return 0;
                }
                foreach (var bo in mm.Bones.Where(b => System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Pelvis|Thigh$|Calf$|UpperArm$|Clavicle$|Spine")))
                    Console.WriteLine($"  {bo.Name} at {bo.Position.X:0.0} {bo.Position.Y:0.0} {bo.Position.Z:0.0}");
                foreach (var (z0, z1) in new[] { (84f, 92f), (76f, 84f), (68f, 76f) })
                {
                    var acc = new Dictionary<string, double>(); int n = 0;
                    foreach (var pt in mm.Parts) foreach (var s in pt.Sections)
                        for (int v = 0; v < s.Pos.Length; v++)
                            if (s.Pos[v].Z >= z0 && s.Pos[v].Z < z1 && s.Pos[v].Y > 2)
                            { n++; foreach (var (bb, w) in s.Weights[v]) acc[pt.Name + ":" + mm.Bones[bb].Name] = acc.GetValueOrDefault(pt.Name + ":" + mm.Bones[bb].Name) + w; }
                    Console.WriteLine($"left z {z0}..{z1} ({n}): " + string.Join(", ", acc.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value / Math.Max(1, n):0.00}")));
                }
                foreach (string side in new[] { "L", "R" })
                {
                    int t = Fb($"Bip001 {side} Thigh"), c = Fb($"Bip001 {side} Calf");
                    if (t < 0 || c < 0) { Console.WriteLine("no Bip001 thigh / calf"); break; }
                    var A = mm.Bones[t].Position; var seg = mm.Bones[c].Position - A; float len = seg.Length();
                    foreach (var (lo, hi) in new[] { (-0.3f, -0.15f), (-0.15f, 0f), (0f, 0.15f), (0.15f, 0.35f), (0.35f, 0.6f) })
                    {
                        var acc = new Dictionary<string, double>(); int n = 0;
                        foreach (var s in mm.Parts.SelectMany(p => p.Sections))
                            for (int v = 0; v < s.Pos.Length; v++)
                            {
                                float u = System.Numerics.Vector3.Dot(s.Pos[v] - A, seg) / (len * len);
                                var perp = s.Pos[v] - (A + u * seg);
                                if (u < lo || u >= hi || perp.Length() > (Environment.GetEnvironmentVariable("MHO_R") is string rr ? float.Parse(rr) : 0.35f) * len) continue;
                                n++;
                                foreach (var (b, w) in s.Weights[v]) acc[mm.Bones[b].Name] = acc.GetValueOrDefault(mm.Bones[b].Name) + w;
                            }
                        Console.WriteLine($"{side} thigh {lo:0.00}..{hi:0.00} ({n}): " + string.Join(", ", acc.OrderByDescending(x => x.Value).Take(6).Select(x => $"{x.Key} {x.Value / Math.Max(1, n):0.00}")));
                    }
                }
                return 0;
            }
            case "--feet-gap":
            {
                // Read-only (2026-10-22, users: MFF models' feet above or into the floor): MFF models retargeted onto a package's
                // skeleton (no build); each one's pelvis height above its soles (bind pose) against the stock model's. The game's
                // player animations keep the pelvis at the stock height, so the difference is how far the soles end up off the
                // floor. --feet-gap <package name or file> [name filter] [max count]
                var fsk = MhoExtendedModManager.Model.MhoSkeleton.Load(MhoExtendedModManager.Model.BasePackage.Resolve(rest[1], true), null);
                int fp = fsk.Find("g_pelvis");
                var stockPos = fsk.Mesh.HighestDetail?.Positions ?? [];
                if (fp < 0 || stockPos.Count == 0) { Console.WriteLine("no g_pelvis or no stock mesh"); return 1; }
                float stockDrop = fsk.Pos(fp).Z - stockPos.Min(v => v.Z);
                Console.WriteLine($"stock: pelvis {fsk.Pos(fp).Z:0.0} above its soles by {stockDrop:0.0}");
                int max = rest.Count > 3 && int.TryParse(rest[3], out int mx2) ? mx2 : int.MaxValue, done2 = 0;
                foreach (string dir in MhoExtendedModManager.Model.Source.AllModelFolders())
                {
                    if (done2 >= max) break;
                    if (rest.Count > 2 && rest[2] != "*" && !Path.GetFileName(dir).Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(Path.GetFileName(dir)));
                        if (!mm.Bones.Any(b => b.Name.Equals("Bip001 Pelvis", StringComparison.OrdinalIgnoreCase))) continue;
                        var rr = MhoExtendedModManager.Model.Retarget.Run(mm, mm.Selected(null), fsk);
                        var pel = rr.Bones.FirstOrDefault(b => b.Name.Equals("g_pelvis", StringComparison.OrdinalIgnoreCase));
                        if (pel == null || rr.Sections.Count == 0) continue;
                        float floor = rr.Sections.SelectMany(x => x.Pos).Min(v => v.Z), drop = pel.Position.Z - floor;
                        // the ankles: the leg IK puts them where the stock ankles are; the soles hang below by the model's own
                        float Ank(IEnumerable<System.Numerics.Vector3> ps) => ps.Any() ? ps.Average(v => v.Z) : float.NaN;
                        float mAnk = Ank(rr.Bones.Where(b => b.Name is "g_l_ankle" or "g_r_ankle").Select(b => b.Position));
                        float sAnk = Ank(new[] { fsk.Find("g_l_ankle"), fsk.Find("g_r_ankle") }.Where(i => i >= 0).Select(fsk.Pos));
                        float sFloor = stockPos.Min(v => v.Z);
                        Console.WriteLine($"{Path.GetFileName(dir),-40} pelvis drop {drop,5:0.0} (stock {stockDrop:0.0})  ankle drop {mAnk - floor,5:0.0} (stock {sAnk - sFloor:0.0})  → by the ankles feet {(sAnk - sFloor) - (mAnk - floor),5:+0.0;-0.0} off the floor  scale {rr.Scale:0.0000} (MFF height {mm.SourceHeight * MhoExtendedModManager.Model.MffModel.UnitScale:0.0}, MHO {fsk.Height:0.0})");
                        done2++;
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(dir)} | error: {ex.Message}"); }
                }
                return 0;
            }
            case "--lean-posed":
            {
                // Read-only: an MFF model fitted as the Model tab's preview fits it, and the hero's own model, both posed in the
                // same animations: how far forward the head (and chest) sit over the ankles, from upright, at a few frames.
                // --lean-posed <mff model> <package> [animation …]
                if (rest.Count < 3) { Console.WriteLine("--lean-posed <mff model> <package> [animation …]"); return 1; }
                MhoExtendedModManager.Model.Settings.Reset();
                string lpkg = MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true);
                var lpm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1]));
                var prep = MhoExtendedModManager.Model.Gui.PreviewPanel.Prepare(lpm, null, lpkg, null, lean: Environment.GetEnvironmentVariable("MHO_LEAN") == "auto" ? float.NaN : float.TryParse(Environment.GetEnvironmentVariable("MHO_LEAN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lv) ? lv : 0);   // (MHO_LEAN=<degrees>: the Lean slider)
                if (prep.Stock == null) { Console.WriteLine("no stock model to compare"); return 1; }
                var want = rest.Count > 3 ? rest.Skip(3).ToList() : ["idle", "run", "idle_combat"];
                static float Deg(System.Numerics.Vector3 top, System.Numerics.Vector3 foot) => MathF.Atan2(top.X - foot.X, top.Z - foot.Z) * 180 / MathF.PI;
                foreach (string an in want)
                {
                    var ar = prep.Animations.FirstOrDefault(a => a.Name.Equals(an, StringComparison.OrdinalIgnoreCase));
                    if (ar == null || ModAnimations.Load(ar) is not { } ba) { Console.WriteLine($"{an}: not found"); continue; }
                    float frames = MeshAnimator.Span(ba).Frames;
                    foreach (var (label, l) in new[] { ("stock ", prep.Stock), ("fitted", prep.Mesh with { Bones = prep.Bones }) })
                    {
                        var animr = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
                        int B(string n) { for (int k = 0; k < l.Bones.Count; k++) if (l.Bones[k].Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return k; return -1; }
                        int head = B("g_head"), chest = B("g_spine03"), la = B("g_l_ankle"), ra = B("g_r_ankle");
                        var line = new List<string>();
                        for (float f = 0; f <= frames; f += Math.Max(1, frames / 4))
                        {
                            animr.Pose(ba, f);
                            var ank = (animr.BonePosition(la) + animr.BonePosition(ra)) / 2;
                            line.Add($"head {Deg(animr.BonePosition(head), ank),5:+0.0;-0.0} chest {Deg(animr.BonePosition(chest), ank),5:+0.0;-0.0}");
                        }
                        Console.WriteLine($"{an,-12} {label}: {string.Join(" | ", line)}");
                    }
                }
                return 0;
            }
            case "--fit-distort":
                // Read-only (2026-10-25): how much a fit stretches the skin per region, at rest and in animations (FitDistort)
                return MhoExtendedModManager.Model.FitDistort.Run(rest.Skip(1).ToList());
            case "--jaw-sweep":
            {
                // Read-only (2026-10-25): every MFF model retargeted onto a package's skeleton (no build); its jaw note.
                // --jaw-sweep <package name or file> [name filter]
                var jsk = MhoExtendedModManager.Model.MhoSkeleton.Load(MhoExtendedModManager.Model.BasePackage.Resolve(rest[1], true), null);
                foreach (string dir in MhoExtendedModManager.Model.Source.AllModelFolders())
                {
                    if (rest.Count > 2 && !Path.GetFileName(dir).Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(Path.GetFileName(dir)));
                        if (!mm.Bones.Any(b => b.Name.Equals("Bip001 Pelvis", StringComparison.OrdinalIgnoreCase))) continue;
                        var rr = MhoExtendedModManager.Model.Retarget.Run(mm, mm.Selected(null), jsk);
                        Console.WriteLine($"{Path.GetFileName(dir)} | {rr.Notes.FirstOrDefault(n => n.StartsWith("jaw:")) ?? "no jaw"}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(dir)} | error: {ex.Message}"); }
                }
                return 0;
            }
            case "--fit-heat":
                // Read-only (2026-10-25): the stretch heat map, fitted and game models at an animation's worst frame (FitHeat)
                return MhoExtendedModManager.Model.FitHeat.Run(rest.Skip(1).ToList());
            case "--lean-census":
            {
                // Read-only: every MFF model's pelvis-to-head angle from the nearest file axis (MffModel.UpTilt), as counts
                var tilts = new List<(string Name, float T)>();
                foreach (string dir in MhoExtendedModManager.Model.Source.AllModelFolders())
                    try { var cm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(Path.GetFileName(dir))); if (cm.FrameFrom == "bones") tilts.Add((Path.GetFileName(dir), cm.UpTilt)); }
                    catch (Exception) { }
                foreach (var (lo, hi) in new[] { (0f, 1f), (1f, 2f), (2f, 3f), (3f, 4f), (4f, 5f), (5f, 6f), (6f, 8f), (8f, 12f), (12f, 90f) })
                    Console.WriteLine($"{lo,4:0}-{hi,-3:0}°: {tilts.Count(t => t.T >= lo && t.T < hi)}");
                foreach (var t in tilts.OrderByDescending(t => t.T).Take(15)) Console.WriteLine($"  {t.Name} {t.T:0.0}°");
                return 0;
            }
            case "--lean":
            {
                // Read-only (2026-10-23, Kurt: MFF models lean forward about 5 degrees in the rest pose): an MFF model fitted to a
                // package's skeleton; how far forward (+X) its head, neck, chest and pelvis sit over the ankles, as angles from
                // upright, for the stock skeleton, the fitted bones and the fitted mesh (its top and bottom tenth).
                // --lean <mff model> <package>
                if (rest.Count < 3) { Console.WriteLine("--lean <mff model> <package>"); return 1; }
                var lsk = MhoExtendedModManager.Model.MhoSkeleton.Load(MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true), null);
                var lm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1]));
                Console.WriteLine($"frame from {lm.FrameFrom}: pelvis-to-head {lm.UpTilt:0.0}° from the nearest file axis ({(lm.UpTilt <= 5 ? "snapped to it" : "kept: the model is turned by it")})");
                var lr = MhoExtendedModManager.Model.Retarget.Run(lm, lm.Selected(null), lsk);
                string[] names = ["g_pelvis", "g_spine01", "g_spine03", "g_neck", "g_head"];
                static string Ang(System.Numerics.Vector3 top, System.Numerics.Vector3 foot) => $"{MathF.Atan2(top.X - foot.X, top.Z - foot.Z) * 180 / MathF.PI,6:+0.0;-0.0}° (side {MathF.Atan2(top.Y - foot.Y, top.Z - foot.Z) * 180 / MathF.PI:+0.0;-0.0})";
                System.Numerics.Vector3? Stock(string n) => lsk.Find(n) is int i and >= 0 ? lsk.Pos(i) : null;
                System.Numerics.Vector3? Fit(string n) => lr.Bones.FirstOrDefault(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Position;
                System.Numerics.Vector3 Mid(Func<string, System.Numerics.Vector3?> f) => ((f("g_l_ankle") ?? default) + (f("g_r_ankle") ?? default)) / 2;
                var sa = Mid(Stock); var fa = Mid(Fit);
                foreach (string n in names)
                    Console.WriteLine($"{n,-10} stock {(Stock(n) is { } sv ? Ang(sv, sa) : "-"),-26} fitted {(Fit(n) is { } fv ? Ang(fv, fa) : "-")}");
                var vs = lr.Sections.SelectMany(x => x.Pos).ToList();
                var sp = lsk.Mesh.HighestDetail?.Positions ?? [];
                (System.Numerics.Vector3 Top, System.Numerics.Vector3 Bottom) Ends(IReadOnlyList<System.Numerics.Vector3> p)
                {
                    float lo = p.Min(v => v.Z), hi = p.Max(v => v.Z), h = hi - lo;
                    var top = p.Where(v => v.Z > hi - h * 0.1f).ToList(); var bot = p.Where(v => v.Z < lo + h * 0.1f).ToList();
                    return (top.Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / top.Count, bot.Aggregate(System.Numerics.Vector3.Zero, (a, v) => a + v) / bot.Count);
                }
                // the MFF model itself (its normalized frame: Z up, facing +X), before the fit
                System.Numerics.Vector3? Src(string n) => lm.Bones.FirstOrDefault(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))?.Position;
                var srcFeet = ((Src("Bip001 L Foot") ?? default) + (Src("Bip001 R Foot") ?? default)) / 2;
                foreach (string n in new[] { "Bip001 Pelvis", "Bip001 Spine2", "Bip001 Neck", "Bip001 Head" })
                    if (Src(n) is { } v) Console.WriteLine($"{n,-14} source {Ang(v, srcFeet)}");
                foreach (var (mho, mff) in new[] { ("g_head", "Bip001 Head"), ("g_pelvis", "Bip001 Pelvis"), ("g_l_hip", "Bip001 L Thigh"), ("g_l_knee", "Bip001 L Calf"), ("g_l_ankle", "Bip001 L Foot"), ("g_l_ball", "Bip001 L Toe0") })
                    Console.WriteLine($"  {mho,-10} stock {Stock(mho)?.ToString("0.0") ?? "-",-28} fitted {Fit(mho)?.ToString("0.0") ?? "-",-28} source x scale {(Src(mff) is { } q ? (q * lr.Scale).ToString("0.0") : "-")}");
                foreach (var (from, to) in new[] { ("g_pelvis", "g_spine01"), ("g_spine01", "g_spine02"), ("g_spine02", "g_spine03"), ("g_spine03", "g_neck"), ("g_neck", "g_head"), ("g_l_hip", "g_l_knee"), ("g_l_knee", "g_l_ankle") })
                    if (Stock(from) is { } s0 && Stock(to) is { } s1 && Fit(from) is { } f0 && Fit(to) is { } f1)
                        Console.WriteLine($"  {from} → {to}: stock {Ang(s1, s0)} len {(s1 - s0).Length():0.0}   fitted {Ang(f1, f0)} len {(f1 - f0).Length():0.0}");
                Console.WriteLine("  MFF spine bones: " + string.Join(", ", lm.Bones.Where(x => x.Name.Contains("Spine", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Neck", StringComparison.OrdinalIgnoreCase) || x.Name.EndsWith("Pelvis")).Select(x => x.Name)));
                Console.WriteLine("  map: " + string.Join(", ", lr.Map.Where(kv => kv.Key.Contains("Spine", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Neck", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Pelvis") || kv.Key.Contains("Head")).Select(kv => kv.Key + " → " + kv.Value)));
                foreach (var nt in lr.Notes.Where(x => x.Contains("spine", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("  note: " + nt);
                var srcPos = lm.Parts.Where(x => x.DefaultOn).SelectMany(x => x.Sections).SelectMany(x => x.Pos).ToList();
                if (srcPos.Count > 0) { var e = Ends(srcPos); Console.WriteLine($"mesh       source {Ang(e.Top, e.Bottom)}"); }
                if (sp.Count > 0) { var e = Ends(sp); Console.WriteLine($"mesh       stock {Ang(e.Top, e.Bottom)}"); }
                if (vs.Count > 0) { var e = Ends(vs); Console.WriteLine($"mesh       fitted {Ang(e.Top, e.Bottom)}"); }
                return 0;
            }
            case "--helper-sweep":
            {
                // Read-only (2026-10-06): every MFF model in the MFF folder retargeted onto a package's skeleton (no build); the
                // helper bones each one hands to a limb. --helper-sweep <package name or file> [name filter]
                var hsk = MhoExtendedModManager.Model.MhoSkeleton.Load(MhoExtendedModManager.Model.BasePackage.Resolve(rest[1], true), null);
                foreach (string dir in MhoExtendedModManager.Model.Source.AllModelFolders())
                {
                    if (rest.Count > 2 && !Path.GetFileName(dir).Contains(rest[2], StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(Path.GetFileName(dir)));
                        foreach (var w in mm.Warnings.Where(w => w.StartsWith("loose hands"))) Console.WriteLine($"{Path.GetFileName(dir)} | {w}");
                        if (!mm.Bones.Any(b => b.Name.Equals("Bip001 Pelvis", StringComparison.OrdinalIgnoreCase))) continue;
                        var rr = MhoExtendedModManager.Model.Retarget.Run(mm, mm.Selected(null), hsk);
                        foreach (var n in rr.Notes.Where(n => (n.StartsWith("helper bone") && n.Contains("top of") || n.StartsWith("numbered twist")))) Console.WriteLine($"{Path.GetFileName(dir)} | {n}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(dir)} | error: {ex.Message}"); }
                }
                return 0;
            }
            case "--anim-tracks":
            {
                // Read-only (2026-10-23, a borrowed flight exported to Blender with only its root): one animation of a mod's
                // costume package as the preview finds it (the mod's packages), decoded by the preview and by the FBX export.
                // --anim-tracks <mod> <costume package file> <animation>
                var tm = rest.Count > 3 ? lib.Find(rest[1]) : null;
                if (tm == null) { Console.WriteLine("--anim-tracks <mod> <costume package file> <animation>"); return 1; }
                string? tgr = settings.ResolvedGameRoot(data);
                string? tck = tgr != null && Settings.IsGameRoot(tgr) ? Settings.Cooked(tgr) : null;
                var mods = tm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(tm.Folder, f))).ToList();
                string cp = Path.Combine(tm.Folder, rest[2]);
                var tmr = ModMeshes.List([(rest[2], cp)], anyPackage: true).FirstOrDefault();
                var tld = tmr == null ? null : ModMeshes.Load(tmr, tck, out _);
                if (tmr == null || tld == null) { Console.WriteLine("no mesh in " + rest[2]); return 1; }
                var tr = ModAnimations.For(tmr, tld.Bones, mods, tck).FirstOrDefault(x => x.Name.Equals(rest[3], StringComparison.OrdinalIgnoreCase));
                if (tr == null) { Console.WriteLine("no animation " + rest[3]); return 1; }
                Console.WriteLine($"{tr.Name}: {Path.GetFileName(tr.File)} #{tr.SequenceExport}, {tr.TrackBoneNames.Count} track names, positions for {tr.TranslationBones?.Count.ToString() ?? "all"}{(tr.Unlisted != null ? ", unlisted" : "")}");
                var pa = ModAnimations.Load(tr);
                var ea = MhoExtendedModManager.Model.MhoAnim.Load(new MhoExtendedModManager.Model.MhoAnimRef(tr.File, tr.Name, tr.SequenceExport, tr.TrackBoneNames, tr.TranslationBones));
                foreach (var (what, an) in new[] { ("preview", pa), ("export", ea) })
                {
                    if (an == null) { Console.WriteLine($"  {what}: can't be read"); continue; }
                    int moving = an.Tracks.Count(t => t.Value.RotationKeys.Count > 1 || t.Value.PositionKeys.Count > 1);
                    Console.WriteLine($"  {what}: {an.Tracks.Count} tracks, {moving} with more than one key, mesh bones covered {tld.Bones.Count(b => an.Tracks.ContainsKey(b.Name))} of {tld.Bones.Count}; e.g. {string.Join(", ", an.Tracks.Keys.Take(6))}");
                }
                return 0;
            }
            case "--anim-compare":
            {
                // Read-only: two AnimSequences of a package (export paths) bone by bone, as stored: the angle between their
                // rotations at the first and middle frame, key counts; the bones that differ most. --anim-compare <package> <a> <b>
                if (rest.Count < 4) { Console.WriteLine("--anim-compare <package> <sequence a> <sequence b>"); return 1; }
                var cp2 = AnimExportCli.Packages.Package.Open(rest[1]);
                var cm2 = MhoPackageModifier.Package.Open(rest[1]);
                AnimExportCli.Animation.BoneAnimation? Seq(string path)
                {
                    int e = Array.FindIndex(cm2.Exports, x => cm2.PathOf(x).Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (e < 0) return null;
                    var info = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(cp2).FirstOrDefault(x => x.ExportIndex == cm2.Exports[e].OuterIndex - 1);
                    return info == null ? null : AnimExportCli.Animation.AnimObjectReader.TryRead(cp2, e, info.TrackBoneNames);
                }
                var sa = Seq(rest[2]); var sb = Seq(rest[3]);
                if (sa == null || sb == null) { Console.WriteLine("one doesn't decode"); return 1; }
                double Ang(System.Numerics.Quaternion x, System.Numerics.Quaternion y) => 2 * Math.Acos(Math.Min(1, Math.Abs(System.Numerics.Quaternion.Dot(System.Numerics.Quaternion.Normalize(x), System.Numerics.Quaternion.Normalize(y))))) * 180 / Math.PI;
                var rows = new List<(string Bone, double First, double Mid, int Ka, int Kb)>();
                foreach (var (bn, ta) in sa.Tracks)
                {
                    if (!sb.Tracks.TryGetValue(bn, out var tb2) || ta.RotationKeys.Count == 0 || tb2.RotationKeys.Count == 0) continue;
                    var ra = ta.RotationKeys; var rb = tb2.RotationKeys;
                    rows.Add((bn, Ang(ra[0].Rotation, rb[0].Rotation), Ang(ra[ra.Count / 2].Rotation, rb[rb.Count / 2].Rotation), ra.Count, rb.Count));
                }
                var prow = new List<(string Bone, float First, float Mid)>();
                foreach (var (bn, ta) in sa.Tracks)
                {
                    if (!sb.Tracks.TryGetValue(bn, out var tb3) || ta.PositionKeys.Count == 0 || tb3.PositionKeys.Count == 0) continue;
                    var pa2 = ta.PositionKeys; var pb2 = tb3.PositionKeys;
                    prow.Add((bn, System.Numerics.Vector3.Distance(pa2[0].Position, pb2[0].Position), System.Numerics.Vector3.Distance(pa2[pa2.Count / 2].Position, pb2[pb2.Count / 2].Position)));
                }
                Console.WriteLine("  positions that differ most:");
                foreach (var r in prow.OrderByDescending(r => Math.Max(r.First, r.Mid)).Take(10))
                    Console.WriteLine($"    {r.Bone,-28} first {r.First,7:0.00}  mid {r.Mid,7:0.00}  ({sa.Tracks[r.Bone].PositionKeys[0].Position} vs {sb.Tracks[r.Bone].PositionKeys[0].Position})");
                Console.WriteLine("  rotations that differ most:");
                foreach (var r in rows.OrderByDescending(r => Math.Max(r.First, r.Mid)).Take(15))
                    Console.WriteLine($"  {r.Bone,-28} first {r.First,6:0.0}°  mid {r.Mid,6:0.0}°  keys {r.Ka} vs {r.Kb}");
                Console.WriteLine($"  {rows.Count} bones compared; {rows.Count(r => Math.Max(r.First, r.Mid) > 10)} differ by more than 10°");
                return 0;
            }
            case "--anim-keys":
            {
                // Read-only: a package's AnimSequence (by export path) decoded as stored (no single-key rule): each named
                // bone's key counts, first and middle rotation, first position. --anim-keys <package> <sequence path> <bone>...
                if (rest.Count < 4) { Console.WriteLine("--anim-keys <package> <sequence path> <bone>..."); return 1; }
                var kp = AnimExportCli.Packages.Package.Open(rest[1]);
                var mp = MhoPackageModifier.Package.Open(rest[1]);
                int ke = Array.FindIndex(mp.Exports, x => mp.PathOf(x).Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (ke < 0) { Console.WriteLine("no " + rest[2]); return 1; }
                int ks = mp.Exports[ke].OuterIndex - 1;
                var kinfo = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(kp).FirstOrDefault(x => x.ExportIndex == ks);
                if (kinfo == null) { Console.WriteLine("its set doesn't read"); return 1; }
                var ka = AnimExportCli.Animation.AnimObjectReader.TryRead(kp, ke, kinfo.TrackBoneNames);
                if (ka == null) { Console.WriteLine("doesn't decode"); return 1; }
                foreach (string bn in rest.Skip(3))
                {
                    if (!ka.Tracks.TryGetValue(bn, out var kt)) { Console.WriteLine($"  {bn}: no track"); continue; }
                    string Q(System.Numerics.Quaternion q) => $"({q.X:0.000} {q.Y:0.000} {q.Z:0.000} {q.W:0.000})";
                    Console.WriteLine($"  {bn}: {kt.PositionKeys.Count} pos / {kt.RotationKeys.Count} rot keys; rot first {Q(kt.RotationKeys[0].Rotation)} mid {Q(kt.RotationKeys[kt.RotationKeys.Count / 2].Rotation)}; pos first {kt.PositionKeys.FirstOrDefault().Position}");
                }
                return 0;
            }
            case "--bone-rel":
            {
                // Read-only (2026-10-23, Thor's hammer swinging by its strap in fidget02): a bone's place relative to another
                // through an animation of a costume package (the game's copy), every few frames: position (in the other
                // bone's frame) and the angle between them. --bone-rel <costume package file> <animation> <bone> <relative to>
                if (rest.Count < 5) { Console.WriteLine("--bone-rel <costume package file> <animation> <bone> <relative to>"); return 1; }
                string? ck = Settings.Load().GameRoot is string gr && Settings.IsGameRoot(gr) ? Settings.Cooked(gr) : null;
                if (ck == null) { Console.WriteLine("no game folder"); return 1; }
                string pf = File.Exists(rest[1]) ? Path.GetFullPath(rest[1]) : StockFiles.For(ck, rest[1]);   // (a path: that file, e.g. a mod's)
                var mr = ModMeshes.List([(Path.GetFileName(pf), pf)]).FirstOrDefault();
                var ml = mr == null ? null : ModMeshes.Load(mr, ck, out _);
                if (mr == null || ml == null) { Console.WriteLine("no skeletal mesh in " + rest[1]); return 1; }
                bool restPose = rest[2].Equals("rest", StringComparison.OrdinalIgnoreCase);   // (the bind pose)
                var ar = restPose ? null : ModAnimations.For(mr, ml.Bones, [(Path.GetFileName(pf), pf)], ck).FirstOrDefault(x => x.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (ar == null && !restPose) { Console.WriteLine("no animation " + rest[2]); return 1; }
                var ba = ar == null ? null : ModAnimations.Load(ar);
                var an = new MeshAnimator(ml.Bones, ml.Positions, ml.Normals, ml.Influences, ml.Tangents);
                int bi = an.BoneIndex(rest[3]), ri = an.BoneIndex(rest[4]);
                if (bi < 0 || ri < 0) { Console.WriteLine("bone not found"); return 1; }
                Console.WriteLine($"{mr.Name}: {rest[3]} (parent {(ml.Bones[bi].ParentIndex >= 0 ? ml.Bones[ml.Bones[bi].ParentIndex].Name : "-")}) relative to {rest[4]}; {(ar == null ? "the bind pose" : $"{ar.Name} from {ar.File}, {(ar.TranslationBones?.Contains(rest[3]) == true ? "positions taken" : "positions not taken")} for {rest[3]}")}");
                var (fr, sec) = ba == null ? (0f, 0f) : MeshAnimator.Span(ba);
                for (int f = 0; f <= (int)fr; f += Math.Max(1, (int)fr / 16))
                {
                    an.Pose(ba, f);
                    System.Numerics.Matrix4x4.Invert(an.BoneMatrix(ri), out var inv);
                    var rel = an.BoneMatrix(bi) * inv;
                    var q = System.Numerics.Quaternion.CreateFromRotationMatrix(rel);
                    double deg = 2 * Math.Acos(Math.Min(1, Math.Abs(q.W))) * 180 / Math.PI;
                    Console.WriteLine($"  frame {f,4}: at {rel.Translation.X,7:0.0} {rel.Translation.Y,7:0.0} {rel.Translation.Z,7:0.0}  ({rel.Translation.Length(),5:0.0} away)  turned {deg,5:0}°");
                }
                return 0;
            }
            case "--bone-frames":
            {
                // Read-only (2026-10-05, Storm's idle twisted on Jean's Phoenix set): the first skeletal mesh of two packages,
                // bone by bone: each shared bone's rest rotation (relative to its parent) and the angle between the two; the bones
                // only one has. --bone-frames <a.upk> <b.upk>
                if (rest.Count < 3) { Console.WriteLine("--bone-frames <a.upk> <b.upk>"); return 1; }
                AnimExportCli.Meshes.MeshBone[]? Bones(string f) { var mr = ModMeshes.List([(Path.GetFileName(f), f)]).FirstOrDefault(); return mr == null ? null : ModMeshes.Load(mr, null, out _)?.Bones.ToArray(); }
                var ba = Bones(rest[1]); var bb = Bones(rest[2]);
                if (ba == null || bb == null) { Console.WriteLine("no skeletal mesh in one of them"); return 1; }
                var byName = bb.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
                int shared = 0, off = 0;
                foreach (var x in ba)
                {
                    if (!byName.TryGetValue(x.Name, out var y)) continue;
                    shared++;
                    float dot = Math.Abs(System.Numerics.Quaternion.Dot(System.Numerics.Quaternion.Normalize(x.Orientation), System.Numerics.Quaternion.Normalize(y.Orientation)));
                    double deg = 2 * Math.Acos(Math.Min(1, dot)) * 180 / Math.PI;
                    string pa = x.ParentIndex >= 0 && x.ParentIndex < ba.Length ? ba[x.ParentIndex].Name : "-", pb = y.ParentIndex >= 0 && y.ParentIndex < bb.Length ? bb[y.ParentIndex].Name : "-";
                    if (deg > 5 || !pa.Equals(pb, StringComparison.OrdinalIgnoreCase)) { off++; Console.WriteLine($"  {x.Name,-24} {deg,6:0.0}°  parent {pa}{(pa.Equals(pb, StringComparison.OrdinalIgnoreCase) ? "" : " vs " + pb)}"); }
                }
                Console.WriteLine($"{shared} shared bones, {off} turned more than 5° (or with another parent)");
                Console.WriteLine("only in the first: " + string.Join(", ", ba.Where(x => !byName.ContainsKey(x.Name)).Select(x => x.Name)));
                var an = ba.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine("only in the second: " + string.Join(", ", bb.Where(x => !an.Contains(x.Name)).Select(x => x.Name)));
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
                        if (mi == null) { Console.WriteLine($"  section {sec}: material {(mat < 0 ? ModMeshes.ImportPath(mpk, mat) + " (imported)" : "none")}"); continue; }
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
                if (anims.Where(a => a.Unlisted != null).ToList() is { Count: > 0 } unl)
                    Console.WriteLine($"  not in the costume's list (the game won't play them): {unl.Count}: " + string.Join(", ", unl.Take(20).Select(a => $"{a.Name} ({a.Unlisted})")));
                var anim8 = new MeshAnimator(ld.Bones, ld.Positions, ld.Normals, ld.Influences);
                anim8.Pose(null, 0);
                float restErr = ld.Positions.Select((p, i) => Vector3.Distance(p, anim8.Positions[i])).Max();
                Console.WriteLine($"  rest pose through the skinning: max vertex error {restErr:0.000} units");
                var wanted = rest.Skip(2).ToList();
                foreach (var a in anims.Where(a => wanted.Count == 0 ? true : wanted.Any(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(wanted.Count == 0 ? 4 : 400))
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
                int pelvis = fa.BoneIndex(Environment.GetEnvironmentVariable("MHO_FOOT_BONE") is { Length: > 0 } fbn ? fbn : "g_pelvis"), froot = 0;
                void Show(string what)
                {
                    float lo = fa.Positions.Min(p => p.Z), hi = fa.Positions.Max(p => p.Z);
                    Console.WriteLine($"  {what,-34} lowest {lo,8:0.0}  top {hi,7:0.0}  pelvis z {(pelvis >= 0 ? fa.BonePosition(pelvis).Z : float.NaN),7:0.0}  root z {fa.BonePosition(froot).Z,6:0.0}");
                    // MHO_FOOT_DIR=1: each toe bone's three local axes in world space (left mirrored: the two should match)
                    if (Environment.GetEnvironmentVariable("MHO_FOOT_DIR") == "1")
                        foreach (string tb in new[] { "g_r_ball", "g_l_ball" })
                            if (fa.BoneIndex(tb) is int ti && ti >= 0)
                            {
                                var bm = fa.BoneMatrix(ti); float my = tb.StartsWith("g_l") ? -1 : 1;
                                string Ax(Vector3 v) { var w = Vector3.TransformNormal(v, bm); return $"({w.X,5:0.00},{w.Y * my,5:0.00},{w.Z,5:0.00})"; }
                                Console.WriteLine($"      {tb}: x {Ax(Vector3.UnitX)}  y {Ax(Vector3.UnitY)}  z {Ax(Vector3.UnitZ)}");
                            }
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
                    // MHO_FOOT_POS=<bone>: that bone's average place (X front, Y side, Z up) over the animation, against the root
                    if (Environment.GetEnvironmentVariable("MHO_FOOT_POS") is { Length: > 0 } pb2 && fa.BoneIndex(pb2) is int pi2 && pi2 >= 0)
                    {
                        Vector3 sum2 = default; int cnt2 = 0;
                        for (float f = 0; f <= fN; f += Math.Max(1, fN / 40)) { fa.Pose(ba, f); sum2 += fa.BonePosition(pi2) - fa.BonePosition(froot); cnt2++; }
                        var av = sum2 / Math.Max(1, cnt2);
                        Console.WriteLine($"      {pb2}: average place {av.X:0.0} front, {av.Y:0.0} side, {av.Z:0.0} up");
                        fa.Pose(ba, 0); var p0 = fa.BonePosition(pi2) - fa.BonePosition(froot); var r0 = fa.BonePosition(froot);
                        fa.Pose(ba, fN); var p1 = fa.BonePosition(pi2) - fa.BonePosition(froot); var r1 = fa.BonePosition(froot);
                        Console.WriteLine($"      {pb2}: first frame {p0.X:0.0} / {p0.Y:0.0} / {p0.Z + r0.Z:0.0} up, last frame {p1.X:0.0} / {p1.Y:0.0} / {p1.Z + r1.Z:0.0} up (with the root)");
                    }
                    // MHO_FOOT_LAT=<bone>: that bone's average sideways place (Y) over the animation, against the pelvis (a cape's drift)
                    if (Environment.GetEnvironmentVariable("MHO_FOOT_LAT") is { Length: > 0 } lb && fa.BoneIndex(lb) is int li && li >= 0 && pelvis >= 0)
                    {
                        float sum = 0; int cnt = 0;
                        for (float f = 0; f <= fN; f += Math.Max(1, fN / 40)) { fa.Pose(ba, f); sum += fa.BonePosition(li).Y - fa.BonePosition(pelvis).Y; cnt++; }
                        Console.WriteLine($"      {lb}: sideways {sum / Math.Max(1, cnt):0.0} from the pelvis on average");
                    }
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
            case "--anim-find":
            {
                // Read-only: the AnimSets in packages (paths) that hold a sequence of that name, with their bones.
                if (rest.Count < 3) { Console.WriteLine("--anim-find <sequence> <package.upk>..."); return 1; }
                foreach (string path in rest.Skip(2))
                {
                    try
                    {
                        var ap = AnimExportCli.Packages.Package.Open(path);
                        foreach (var s in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                            if (s.Sequences.Where(q => q.IsExport).Any(q => AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, q.ExportIndex).Equals(rest[1], StringComparison.OrdinalIgnoreCase)))
                                Console.WriteLine($"{Path.GetFileName(path)} {ap.GetExportName(s.ExportIndex)}: {s.Sequences.Count} sequences, {s.TrackBoneNames.Count} bones: {string.Join(" ", s.TrackBoneNames.Take(6))}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}"); }
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
