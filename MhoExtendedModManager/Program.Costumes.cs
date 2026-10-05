using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>Commands for costume moves (same hero, another hero) and voices. Null when <paramref name="cmd"/> isn't one of them.</summary>
    static int? CostumeCommand(string cmd, List<string> rest, Settings settings, string data, ModLibrary lib)
    {
        switch (cmd)
        {
            case "--costume-move":
            {
                // Read-only (phase 1): what moving a costume mod onto another costume of the same hero would do.
                var cm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                string? cgr = settings.ResolvedGameRoot(data);
                if (cm == null || cgr == null || !Settings.IsGameRoot(cgr)) { Console.WriteLine("--costume-move <mod> [target costume] [--from <package>]  (needs the game folder)"); return 1; }
                var costumes = Costume.All(cgr);
                if (costumes == null) { Console.WriteLine("the game's costume data (Calligraphy.sip) can't be read"); return 1; }
                string cooked = Settings.Cooked(cgr);
                int fromAt = rest.IndexOf("--from");
                string? from = fromAt >= 0 && fromAt + 1 < rest.Count ? rest[fromAt + 1] : null;
                var sources = CostumeMove.SourceCostumes(cm, costumes);
                Console.WriteLine($"{costumes.Count} costumes in the game data; this mod's costume packages: " + (sources.Count == 0 ? "none" : string.Join(", ", sources.Select(x => $"{x.File} ({x.Costume.Short})"))));
                var src = from != null ? sources.FirstOrDefault(x => x.File.Contains(from, StringComparison.OrdinalIgnoreCase)) : sources.FirstOrDefault();
                if (src.Costume == null) { Console.WriteLine("no costume package to move" + (sources.Count > 1 ? " (use --from)" : "")); return 1; }
                if (sources.Count > 1 && from == null) Console.WriteLine($"moving {src.File} (the first; --from picks another)");
                var targets = CostumeMove.Targets(src.Costume, costumes, cooked);
                string? want = rest.Count > 2 && !rest[2].StartsWith("--") ? rest[2] : null;
                var tgt = want == null ? null : targets.FirstOrDefault(t => t.Class.Equals(want, StringComparison.OrdinalIgnoreCase) || t.Short.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase)
                    || t.Package.Equals(want, StringComparison.OrdinalIgnoreCase) || t.Class.EndsWith("_" + want, StringComparison.OrdinalIgnoreCase));
                if (tgt == null)
                {
                    Console.WriteLine($"{src.Costume.Short} ({src.Costume.Class}); the hero's other costumes:");
                    foreach (var t in targets) Console.WriteLine($"  {t.Short,-34} {t.Class}{(t.IsDefault ? "   (default)" : "")}");
                    if (costumes.FirstOrDefault(c => c.IsDefault && c.Hero == src.Costume.Hero && CostumeMove.IsBase(c)) is { } bd)
                        Console.WriteLine($"  {bd.Short,-34} {bd.Class}   (default; in the hero's main package: can't be a target)");
                    return want == null ? 0 : 1;
                }
                var mcat = new StockCatalog(lib, new GameState(cgr, data));
                var plan = CostumeMove.Make(cm, src.File, src.Costume, tgt, costumes, cooked, mcat);
                Console.Write(CostumeMove.Report(plan));
                int buildAt = rest.IndexOf("--build");
                if (buildAt >= 0)
                {
                    // Phase 2 test: the moved package(s) into a folder of your choice (refuses the game folder), verified.
                    string outDir = buildAt + 1 < rest.Count ? Path.GetFullPath(rest[buildAt + 1]) : "";
                    if (outDir.Length == 0 || outDir.StartsWith(Path.GetFullPath(cgr), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("--build <folder outside the game folder>"); return 1; }
                    if (plan.Problems.Count > 0) { Console.WriteLine("not built: fix the problems above first"); return 1; }
                    foreach (string w in CostumeMove.Build(plan, outDir, new Originals(lib.DataFolder, new GameState(cgr, data)))) Console.WriteLine($"built and verified: {w} ({new FileInfo(w).Length:N0} bytes)");
                }
                if (rest.Contains("--update-copy"))
                {
                    // Rebuild the existing moved copy ("<mod> (on <target>)") in place: same folder, place and on/off.
                    var copy = lib.Mods.FirstOrDefault(x => x.Name.Equals(CostumeMove.NewName(cm, tgt), StringComparison.OrdinalIgnoreCase));
                    if (copy == null) { Console.WriteLine($"no mod named \"{CostumeMove.NewName(cm, tgt)}\" (use --create)"); return 1; }
                    string? done = CostumeMove.CreateMod(lib, cm, plan, new Originals(lib.DataFolder, new GameState(cgr, data)), out string? uerr, copy);
                    Console.WriteLine(done != null ? $"updated mod folder '{done}' (run Apply Changes if it's on)" : "not updated: " + uerr);
                    return done != null ? 0 : 1;
                }
                if (rest.Contains("--create"))
                {
                    // Phase 3: a new disabled mod in the library (the original is untouched).
                    string? made = CostumeMove.CreateMod(lib, cm, plan, new Originals(lib.DataFolder, new GameState(cgr, data)), out string? err);
                    Console.WriteLine(made != null ? $"created mod folder '{made}' (disabled, top of the list)" : "not created: " + err);
                    return made != null ? 0 : 1;
                }
                return 0;
            }
            case "--cross-move":
            {
                // Test (cross-hero move, phase 2): the target costume's stock package with the mod's main mesh in it, written to a
                // folder (never the game's). --cross-move <mod> <Hero/Costume, e.g. Storm/ClassicBlack> --build <folder>
                var xm = rest.Count > 2 ? lib.Find(rest[1]) : null;
                string? xgr = settings.ResolvedGameRoot(data);
                int xb = rest.IndexOf("--build");
                if (xm == null || xgr == null || !Settings.IsGameRoot(xgr) || xb < 0 || xb + 1 >= rest.Count) { Console.WriteLine("--cross-move <mod> <Hero/Costume> --build <folder>"); return 1; }
                string xout = Path.GetFullPath(rest[xb + 1]);
                if (xout.StartsWith(Path.GetFullPath(xgr), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("not into the game folder"); return 1; }
                var xall = Costume.All(xgr)!;
                var xsrc = CostumeMove.Single(xm, xall, allowBase: true);
                if (xsrc is not { } one) { Console.WriteLine("not a single-costume mod"); return 1; }
                var xt = xall.FirstOrDefault(c => c.Short.Replace(".prototype", "", StringComparison.OrdinalIgnoreCase).Equals(rest[2], StringComparison.OrdinalIgnoreCase) || c.Class.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (xt == null) { Console.WriteLine($"no costume '{rest[2]}' (use Hero/Costume as in the prototype, e.g. Storm/ClassicBlack)"); return 1; }
                var xgame = new GameState(xgr, data);
                string? xstock = new Originals(lib.DataFolder, xgame).Find(xt.Package);
                if (xstock == null) { Console.WriteLine($"no stock copy of {xt.Package}"); return 1; }
                string modPkg = Path.Combine(xm.Folder, one.File);
                // The mod's main mesh: the one its costume's component uses (initialskeletalmesh), else its first.
                var mp = MhoPackageModifier.Package.Open(modPkg);
                string meshName = CrossMove.SourceMeshName(xm, one.File, one.Costume.Class);
                string heroBase = "UC__MarvelPlayer_" + one.Costume.Class.Split('_')[1] + "_SF.upk";
                string? baseHero = File.Exists(Path.Combine(xm.Folder, heroBase)) ? Path.Combine(xm.Folder, heroBase) : File.Exists(Path.Combine(xgame.Cooked, heroBase)) ? Path.Combine(xgame.Cooked, heroBase) : null;
                Console.WriteLine($"{one.Costume.Short} ({meshName}) → {xt.Short} ({xt.Class}); target stock {xstock}; source hero base {baseHero ?? "none"}");
                var xlog = new List<string>();
                try
                {
                    byte[] built = CrossMove.Build(modPkg, meshName, xstock, xt.Class, baseHero, xlog, sounds: false, sourceClass: one.Costume.Class, cooked: xgame.Cooked);
                    foreach (var l in xlog) Console.WriteLine("  " + l);
                    Directory.CreateDirectory(xout);
                    string f = Path.Combine(xout, xt.Package);
                    File.WriteAllBytes(f, built);
                    Console.WriteLine($"built and verified: {f} ({built.Length:N0} bytes)");
                    if (rest.Contains("--create") || rest.Contains("--update-copy"))
                    {
                        // The moved costume as a mod: package, icons, costume text, sound packs (--update-copy rebuilds the existing one).
                        var existing = lib.Mods.FirstOrDefault(x => x.Name.Equals(CrossMove.NewName(xm, xt), StringComparison.OrdinalIgnoreCase));
                        if (rest.Contains("--update-copy") && existing == null) { Console.WriteLine($"no mod named \"{CrossMove.NewName(xm, xt)}\""); return 1; }
                        var clog = new List<string>();
                        string? made = CrossMove.CreateMod(lib, xm, one.File, one.Costume, xt, xall, xgame, new StockCatalog(lib, xgame), clog, out string? merr, rest.Contains("--update-copy") ? existing : null);
                        foreach (var l in clog.Where(l => l.StartsWith("icons") || l.StartsWith("sound"))) Console.WriteLine("  " + l);
                        Console.WriteLine(made != null ? $"{(existing != null && rest.Contains("--update-copy") ? "updated" : "created")} mod folder '{made}'" : "not created: " + merr);
                    }
                    return 0;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException)
                {
                    foreach (var l in xlog) Console.WriteLine("  " + l);
                    Console.WriteLine("FAILED: " + ex.Message);
                    return 1;
                }
            }
            case "--costume-anims":
            {
                // Read-only: the animations a costume plays, as the game finds them (the mesh component's AnimSets list, the
                // last set with a name wins). --costume-anims <mod | package.upk> [--all]  (--all lists every animation)
                string? agr = settings.ResolvedGameRoot(data);
                string? acooked = agr != null && Settings.IsGameRoot(agr) ? Settings.Cooked(agr) : null;
                var targets = new List<(string Path, string File, Mod? Mod)>();
                bool folder = rest.Count > 1 && Directory.Exists(rest[1]);
                if (folder) targets.AddRange(Directory.EnumerateFiles(rest[1], "UC__MarvelPlayer_*.upk").Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => (f, Path.GetFileName(f), (Mod?)null)));
                else if (rest.Count > 1 && rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase) && File.Exists(rest[1])) targets.Add((rest[1], Path.GetFileName(rest[1]), null));
                else if (rest.Count > 1 && lib.Find(rest[1]) is { } am)
                    targets.AddRange(am.Manifest.UpkReplacements.Where(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)).Select(f => (Path.Combine(am.Folder, f), f, (Mod?)am)));
                if (targets.Count == 0) { Console.WriteLine("--costume-anims <mod | package.upk> [--all]"); return 1; }
                int read = 0, none = 0, setsAll = 0, missingSets = 0, overriding = 0;
                foreach (var (tp, tf, tm) in targets)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ca = CostumeAnims.Read(tp, tf, CostumeAnims.FilesFor(tm, acooked));
                    if (folder)
                    {
                        // A summary per folder: only packages with a set that can't be found are named.
                        if (ca == null) { none++; continue; }
                        read++; setsAll += ca.Sets.Count; overriding += ca.Anims.Count(a => a.Overrides.Count > 0 && a.From.Kind == CostumeAnims.Source.Costume) > 0 ? 1 : 0;
                        foreach (var s in ca.Sets.Where(s => !s.Found)) { missingSets++; Console.WriteLine($"  {tf}: set {s.Order + 1} {s.Path} not found"); }
                        continue;
                    }
                    if (ca == null) { Console.WriteLine($"{tf}: no character mesh component with an AnimSets list"); continue; }
                    Console.WriteLine($"{tf} ({ca.Class}): {ca.Sets.Count} set(s), {ca.Anims.Count} animation(s) ({sw.ElapsedMilliseconds} ms)");
                    foreach (var s in ca.Sets)
                        Console.WriteLine($"  {s.Order + 1}. {s.PackageName}: {s.Path} [{s.Label}{(s.FromMod ? ", this mod's copy" : "")}] " +
                            (s.Found ? $"{s.Sequences.Count} sequence(s), {s.TrackBoneNames.Count} bones, {(s.TranslationBones == null ? "positions for all bones" : $"rotation only ({s.TranslationBones.Count} bones with positions)")}" : "NOT FOUND"));
                    foreach (var a in ca.Anims.Where(a => rest.Contains("--all") || a.Overrides.Count > 0))
                        Console.WriteLine($"    {a.Name}: set {a.From.Order + 1}{(a.Overrides.Count > 0 ? $", overrides set {string.Join(", ", a.Overrides.Select(o => o.Order + 1))}" : "")}");
                }
                if (folder) Console.WriteLine($"{read} package(s) read ({none} without a character component list); {setsAll} sets, {missingSets} not found; {overriding} with their own set overriding animations");
                return 0;
            }
            case "--who-references":
            {
                // Read-only: the exports of a package whose data holds a reference to an export (its index + 1 as an int32 at any
                // byte position: candidates, to be checked by hand). --who-references <package.upk> <export name or path end>
                if (rest.Count < 3) { Console.WriteLine("--who-references <package.upk> <export name or path end>"); return 1; }
                var wp = MhoPackageModifier.Package.Open(rest[1]);
                int target = Array.FindIndex(wp.Exports, e => wp.PathOf(e).EndsWith(rest[2], StringComparison.OrdinalIgnoreCase));
                if (target < 0) { Console.WriteLine("no export " + rest[2]); return 1; }
                int refVal = target + 1;
                string tpath = wp.PathOf(wp.Exports[target]);
                Console.WriteLine($"{tpath} is export {refVal}");
                for (int i = 0; i < wp.Exports.Length; i++)
                {
                    var e = wp.Exports[i];
                    if (wp.PathOf(e).StartsWith(tpath + ".", StringComparison.OrdinalIgnoreCase)) continue;   // its own children
                    byte[] d;
                    try { d = wp.ReadExportBytes(e); } catch (Exception) { continue; }
                    var at = new List<int>();
                    for (int k = 0; k + 4 <= d.Length; k++) if (BitConverter.ToInt32(d, k) == refVal) at.Add(k);
                    if (at.Count > 0) Console.WriteLine($"  {wp.PathOf(e)} ({wp.ClassOf(e)}): at byte {string.Join(", ", at.Take(6))}");
                }
                return 0;
            }
            case "--alias-swap":
            {
                // The alternate-set test (2026-10-04, Jean Grey's Phoenix animations): builds (never into the game) a costume
                // package whose own alias list points <alias> at a set of donor animations, reads it back, and with --zip makes
                // an installable test mod. --alias-swap <costume.upk> <hero base.upk> <alias> <slot>=<donor.upk>:<animation> [...]
                //   --build <dir> [--zip <mod name>] [--whole]   (packages by path or by file name in the game folder)
                // --whole (2026-10-05, after the first test: the form's set is used alone): the hero's whole alternate set, with
                // the donors' animations fitted to its bones (AnimSwap.FitTracks) in place of its own
                string? agr2 = settings.ResolvedGameRoot(data);
                string? acook = agr2 != null && Settings.IsGameRoot(agr2) ? Settings.Cooked(agr2) : null;
                int abAt = rest.IndexOf("--build"), azAt = rest.IndexOf("--zip");
                string? aout = abAt >= 0 && abAt + 1 < rest.Count ? rest[abAt + 1] : null;
                if (rest.Count < 5 || aout == null) { Console.WriteLine("--alias-swap <costume.upk> <hero base.upk> <alias> <slot>=<donor.upk>:<animation> [...] --build <dir> [--zip <mod name>]"); return 1; }
                string? Find(string f) => File.Exists(f) ? f : acook != null && File.Exists(Path.Combine(acook, f)) ? Path.Combine(acook, f) : null;
                string? cpath = Find(rest[1]), hpath = Find(rest[2]);
                if (cpath == null || hpath == null) { Console.WriteLine("costume or hero package not found"); return 1; }
                string cfile = Path.GetFileName(cpath), afile = Path.GetFileName(hpath), aliasName = rest[3];
                string cClass = "marvelplayer_" + cfile["UC__MarvelPlayer_".Length..^"_SF.upk".Length].ToLowerInvariant();
                string hClass = "marvelplayer_" + afile["UC__MarvelPlayer_".Length..^"_SF.upk".Length].ToLowerInvariant();
                var aswaps = new List<AnimSwap.Swap>();
                var adonors = new Dictionary<string, CostumeAnims.Anim>(StringComparer.OrdinalIgnoreCase);
                foreach (string arg in rest.Skip(4).Where(x => x.Contains('=') && x.Contains(':') && !x.StartsWith("--")))
                {
                    string slot = arg[..arg.IndexOf('=')], dfile = arg[(arg.IndexOf('=') + 1)..arg.LastIndexOf(':')], dname = arg[(arg.LastIndexOf(':') + 1)..];
                    string? dpath = Find(dfile.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? dfile : dfile + ".upk");
                    if (dpath == null) { Console.WriteLine($"no donor package {dfile}"); return 1; }
                    // <animation>, or <set>/<animation> for a set the game's list doesn't pick (a control test from another set)
                    string? dsetName = dname.Contains('/') ? dname[..dname.IndexOf('/')] : null;
                    if (dsetName != null) dname = dname[(dname.IndexOf('/') + 1)..];
                    var dread = CostumeAnims.Read(dpath, Path.GetFileName(dpath), CostumeAnims.FilesFor(null, acook));
                    var da = dread?.Anims.Concat(dread.Anims.SelectMany(x => x.Overrides.Select(o => x with { From = o, Export = o.Sequences.FirstOrDefault(q => q.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase), (Name: "", Export: -1)).Export }))).FirstOrDefault(x => x.Name.Equals(dname, StringComparison.OrdinalIgnoreCase) && (dsetName == null || x.From.Path.EndsWith("." + dsetName, StringComparison.OrdinalIgnoreCase)));
                    if ((da == null || da.Export < 0) && dsetName != null)
                    {
                        // a set no list names (a form's set: a control test copies one of its own animations back)
                        var dpk0 = AnimExportCli.Packages.Package.Open(dpath);
                        var mp0 = MhoPackageModifier.Package.Open(dpath);
                        foreach (var si in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(dpk0))
                        {
                            string sp = mp0.PathOf(mp0.Exports[si.ExportIndex]);
                            if (!sp.EndsWith("." + dsetName, StringComparison.OrdinalIgnoreCase)) continue;
                            int sq = si.Sequences.Where(r => r.IsExport).Select(r => r.ExportIndex).FirstOrDefault(i => AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(dpk0, i).Equals(dname, StringComparison.OrdinalIgnoreCase), -1);
                            if (sq < 0) continue;
                            var set0 = new CostumeAnims.Set(0, sp, Path.GetFileNameWithoutExtension(dpath), dpath, si.ExportIndex, CostumeAnims.Source.Other, false, si.TrackBoneNames,
                                si.RotationOnly ? new HashSet<string>(si.TranslationBones ?? [], StringComparer.OrdinalIgnoreCase) : null, []);
                            da = new CostumeAnims.Anim(dname, set0, sq, []);
                            break;
                        }
                    }
                    if (da == null || da.From.File == null || da.Export < 0) { Console.WriteLine($"{dfile} has no animation '{dname}'"); return 1; }
                    aswaps.Add(new AnimSwap.Swap(slot, da.From.File, da.From.Export, da.Export));
                    adonors[slot] = da;
                    Console.WriteLine($"{aliasName} · {slot} ← {da.From.PackageName}.upk · {da.From.Path} · {da.Name}");
                }
                if (aswaps.Count == 0) { Console.WriteLine("no swaps given (slot=donor.upk:animation)"); return 1; }
                var alog = new List<string>();
                byte[] abuilt;
                bool awhole = rest.Contains("--whole");
                try { abuilt = AnimSwap.BuildAlias(cpath, cClass, hpath, hClass, aliasName, aswaps, alog, awhole); }
                catch (InvalidDataException ex) { foreach (string l in alog) Console.WriteLine("  " + l); Console.WriteLine("can't: " + ex.Message); return 1; }
                foreach (string l in alog) Console.WriteLine("  " + l);
                Directory.CreateDirectory(aout);
                string aoutPath = Path.Combine(aout, cfile);
                if (Path.GetFullPath(aoutPath).Equals(Path.GetFullPath(cpath), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("the build folder can't be the package's own"); return 1; }
                File.WriteAllBytes(aoutPath, abuilt);
                Console.WriteLine($"built: {aoutPath} ({abuilt.Length:N0} bytes)");
                // read back: the costume's own alias list, and the aliased set's animations equal the donors'
                var outPkg = MhoPackageModifier.Package.Open(aoutPath);
                var outAliases = CostumeAnims.Aliases(outPkg, cClass);
                Console.WriteLine("  the costume's alias list: " + string.Join(", ", outAliases.Select(x => $"{x.Alias} → {x.SetPath}")));
                var aset = outAliases.FirstOrDefault(x => x.Alias.Equals(aliasName, StringComparison.OrdinalIgnoreCase));
                var ap = AnimExportCli.Packages.Package.Open(aoutPath);
                var setInfo = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).FirstOrDefault(x => outPkg.PathOf(outPkg.Exports[x.ExportIndex]).Equals(aset.SetPath ?? "", StringComparison.OrdinalIgnoreCase));
                int abad = setInfo == null ? 1 : 0;
                if (setInfo == null) Console.WriteLine("  FAIL the alias doesn't point at an animation set in the package");
                else foreach (var (slot, da) in adonors)
                {
                    var seq = setInfo.Sequences.Where(r => r.IsExport).Select(r => r.ExportIndex).FirstOrDefault(i => AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, i).Equals(slot, StringComparison.OrdinalIgnoreCase), -1);
                    if (seq < 0) { abad++; Console.WriteLine($"  FAIL {slot}: not in the aliased set"); continue; }
                    if (awhole)
                    {
                        // every bone both sets have: the donor's keys exactly; the others hold one key
                        var dpk = AnimExportCli.Packages.Package.Open(da.Ref.File);
                        var dsets = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(dpk).ToList();
                        var dset = dsets.First(i => i.ExportIndex == da.From.Export);   // the set the game finds it in (the swap's)
                        var lists = dsets.Where(i => i.Sequences.Any(r => r.IsExport && r.ExportIndex == da.Ref.SequenceExport)).ToList();
                        if (lists.Count != 1 || lists[0].ExportIndex != dset.ExportIndex || lists.Any(i => !i.TrackBoneNames.SequenceEqual(dset.TrackBoneNames)))
                            Console.WriteLine($"  note {slot}: listed by {lists.Count} set(s) (#{string.Join(", #", lists.Select(i => i.ExportIndex))}), the swap's set #{dset.ExportIndex}; bone order the same in all: {lists.All(i => i.TrackBoneNames.SequenceEqual(dset.TrackBoneNames))}");
                        var dx = AnimExportCli.Animation.AnimObjectReader.TryRead(dpk, da.Ref.SequenceExport, dset.TrackBoneNames);
                        var oy = AnimExportCli.Animation.AnimObjectReader.TryRead(ap, seq, setInfo.TrackBoneNames);
                        int shared = 0, differ = 0, single = 0;
                        if (dx != null && oy != null)
                            foreach (var (bone, t) in oy.Tracks)
                            {
                                if (dx.Tracks.TryGetValue(bone, out var dt))
                                {
                                    shared++;
                                    if (!t.PositionKeys.SequenceEqual(dt.PositionKeys) || !t.RotationKeys.SequenceEqual(dt.RotationKeys))
                                    {
                                        if (differ++ == 0) Console.WriteLine($"  first difference {bone}: pos {t.PositionKeys.Count} vs {dt.PositionKeys.Count} keys, rot {t.RotationKeys.Count} vs {dt.RotationKeys.Count}; first rot {(t.RotationKeys.Count > 0 ? t.RotationKeys[0].ToString() : "-")} vs {(dt.RotationKeys.Count > 0 ? dt.RotationKeys[0].ToString() : "-")}; first pos {(t.PositionKeys.Count > 0 ? t.PositionKeys[0].ToString() : "-")} vs {(dt.PositionKeys.Count > 0 ? dt.PositionKeys[0].ToString() : "-")}");
                                    }
                                }
                                else if (t.RotationKeys.Count == 1) single++;
                                else differ++;
                            }
                        bool wok = dx != null && oy != null && differ == 0 && shared > 0;
                        if (!wok) abad++;
                        int setSeqs = setInfo.Sequences.Count(r => r.IsExport);
                        Console.WriteLine($"  {(wok ? "ok  " : "FAIL")} {slot}: {shared} bone(s) with the donor's keys{(differ > 0 ? $", {differ} DIFFERENT" : "")}, {single} held on one key; the set lists {setSeqs} animations");
                        continue;
                    }
                    var mine = new AnimRef(Path.GetFileName(aoutPath), aoutPath, slot, seq, setInfo.TrackBoneNames) { TranslationBones = setInfo.RotationOnly ? new HashSet<string>(setInfo.TranslationBones ?? [], StringComparer.OrdinalIgnoreCase) : null };
                    var x = ModAnimations.Load(da.Ref); var y = ModAnimations.Load(mine);
                    bool same = x != null && y != null && x.Tracks.Count == y.Tracks.Count && x.Tracks.All(kv => y.Tracks.TryGetValue(kv.Key, out var t)
                        && t.PositionKeys.SequenceEqual(kv.Value.PositionKeys) && t.RotationKeys.SequenceEqual(kv.Value.RotationKeys));
                    if (!same) abad++;
                    Console.WriteLine($"  {(same ? "ok  " : "FAIL")} {slot}: {y?.Tracks.Count ?? 0} tracks {(same ? "identical to the donor's" : "differ from the donor's")}");
                }
                if (awhole && setInfo != null)
                {
                    // the set's other animations: the hero's own, decoded identically
                    var hpk = AnimExportCli.Packages.Package.Open(hpath);
                    var hInfo = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(hpk).FirstOrDefault(i => i.TrackBoneNames.SequenceEqual(setInfo.TrackBoneNames) && i.Sequences.Count == setInfo.Sequences.Count);
                    int same = 0, other = 0;
                    if (hInfo == null) { abad++; Console.WriteLine("  FAIL the hero's set to compare with wasn't found"); }
                    else foreach (int i in setInfo.Sequences.Where(r => r.IsExport).Select(r => r.ExportIndex))
                    {
                        string nm = AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, i);
                        if (adonors.ContainsKey(nm)) continue;
                        int hi = hInfo.Sequences.Where(r => r.IsExport).Select(r => r.ExportIndex).FirstOrDefault(j => AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(hpk, j).Equals(nm, StringComparison.OrdinalIgnoreCase), -1);
                        var a1 = AnimExportCli.Animation.AnimObjectReader.TryRead(ap, i, setInfo.TrackBoneNames);
                        var a2 = hi < 0 ? null : AnimExportCli.Animation.AnimObjectReader.TryRead(hpk, hi, hInfo.TrackBoneNames);
                        bool eq = (a1 == null && a2 == null && hi >= 0) || (a1 != null && a2 != null && a1.Tracks.Count == a2.Tracks.Count && a1.Tracks.All(kv => a2.Tracks.TryGetValue(kv.Key, out var t2) && t2.PositionKeys.SequenceEqual(kv.Value.PositionKeys) && t2.RotationKeys.SequenceEqual(kv.Value.RotationKeys)));
                        if (eq) same++; else { other++; Console.WriteLine($"  FAIL {nm}: not the hero's"); }
                    }
                    if (other > 0) abad++;
                    Console.WriteLine($"  {(other == 0 ? "ok  " : "FAIL")} the set's other animations: {same} identical to the hero's{(other > 0 ? $", {other} not" : "")}");
                }
                Console.WriteLine(abad == 0 ? "PASS" : $"{abad} problem(s)");
                if (abad == 0 && azAt >= 0 && azAt + 1 < rest.Count)
                {
                    string modDir = Path.Combine(aout, "mod");
                    string zip = MhoExtendedModManager.Model.ModOut.Write(modDir, rest[azAt + 1], "MHO Extended Mod Manager", "test", [aoutPath],
                        $"Test of the alternate animation set '{aliasName}' on {cfile}: " + string.Join(", ", adonors.Select(kv => $"{kv.Key} from {kv.Value.From.PackageName}")) + ".");
                    Console.WriteLine("test mod: " + zip);
                }
                return abad == 0 ? 0 : 1;
            }
            case "--unlisted-animsets":
            {
                // Read-only (2026-10-04, a user: Jean Grey's Phoenix power animations weren't in the Animations tab): every hero base
                // package's AnimSets that its own AnimSets list doesn't name (reached only through forms, talent powers or other
                // classes, so the Animations tab doesn't list them), and every form class package (_Transform_ and the like) with
                // its sets. --unlisted-animsets <CookedPCConsole folder>
                if (rest.Count < 2 || !Directory.Exists(rest[1])) { Console.WriteLine("--unlisted-animsets <CookedPCConsole folder>"); return 1; }
                string uc = rest[1];
                bool Skip(string f) => f.Contains("bak", StringComparison.OrdinalIgnoreCase) || f.Contains("copy", StringComparison.OrdinalIgnoreCase);
                var bases = Directory.EnumerateFiles(uc, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                    .Where(f => !Skip(f) && f["UC__MarvelPlayer_".Length..^"_SF.upk".Length].IndexOf('_') < 0).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                int heroes = 0, unlistedSets = 0, unlistedSeqs = 0, aliasedSets = 0, aliasedSeqs = 0;
                foreach (var f in bases)
                {
                    string path = Path.Combine(uc, f);
                    try
                    {
                        var ap = AnimExportCli.Packages.Package.Open(path);
                        var sets = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).Select(x => (Name: ap.GetExportName(x.ExportIndex), Seqs: x.Sequences.Count)).ToList();
                        if (sets.Count == 0) continue;
                        var ca = CostumeAnims.Read(path, f, CostumeAnims.FilesFor(null, uc));
                        var listed = new HashSet<string>((ca?.Sets ?? []).Select(x => x.Path.Split('.').Last()), StringComparer.OrdinalIgnoreCase);
                        var un = sets.Where(x => !listed.Contains(x.Name)).ToList();
                        // the class's alternate sets by alias (AnimationSetAliases): what powers / forms switch to
                        var mpm = MhoPackageModifier.Package.Open(path);
                        string cls = "marvelplayer_" + f["UC__MarvelPlayer_".Length..^"_SF.upk".Length];
                        var aliases = CostumeAnims.Aliases(mpm, cls);
                        if (un.Count == 0 && aliases.Count == 0) continue;
                        heroes++; unlistedSets += un.Count; unlistedSeqs += un.Sum(x => x.Seqs);
                        string AliasOf(string set) => aliases.FirstOrDefault(a => a.SetPath.Split('.').Last().Equals(set, StringComparison.OrdinalIgnoreCase)).Alias ?? "";
                        var byAlias = un.Where(x => AliasOf(x.Name).Length > 0).ToList();
                        var other = un.Where(x => AliasOf(x.Name).Length == 0).ToList();
                        aliasedSets += byAlias.Count; aliasedSeqs += byAlias.Sum(x => x.Seqs);
                        Console.WriteLine($"{f}: ALIASES {(aliases.Count == 0 ? "none" : string.Join(", ", aliases.Select(a => $"{a.Alias} → {a.SetPath.Split('.').Last()}{(sets.FirstOrDefault(x => x.Name.Equals(a.SetPath.Split('.').Last(), StringComparison.OrdinalIgnoreCase)) is var ss && ss.Name != null ? $" ({ss.Seqs})" : "")}")))}"
                            + (other.Count > 0 ? $"; other unlisted (props, vehicles …): {string.Join(", ", other.Select(x => $"{x.Name} ({x.Seqs})"))}" : ""));
                    }
                    catch (Exception ex) { Console.WriteLine($"{f}: {ex.GetType().Name}: {ex.Message}"); }
                }
                Console.WriteLine($"{heroes} hero(es); {unlistedSets} unlisted set(s) ({unlistedSeqs} animations), of them {aliasedSets} alternate sets by alias ({aliasedSeqs} animations)");
                // form classes: player packages named for a form, not a costume
                var forms = Directory.EnumerateFiles(uc, "UC__MarvelPlayer_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                    .Where(f => !Skip(f) && System.Text.RegularExpressions.Regex.IsMatch(f, "_(Transform|Form|Mode|Stance|Phoenix|Hulk|Rage|Mech|Armor)_", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && f.Contains("Transform", StringComparison.OrdinalIgnoreCase)).ToList();
                Console.WriteLine($"form packages (_Transform_): {forms.Count}");
                foreach (var f in forms)
                {
                    try
                    {
                        var ap = AnimExportCli.Packages.Package.Open(Path.Combine(uc, f));
                        var sets = AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).Select(x => $"{ap.GetExportName(x.ExportIndex)} ({x.Sequences.Count})").ToList();
                        Console.WriteLine($"  {f}: {(sets.Count == 0 ? "no AnimSets of its own" : string.Join(", ", sets))}");
                    }
                    catch (Exception ex) { Console.WriteLine($"  {f}: {ex.GetType().Name}: {ex.Message}"); }
                }
                return 0;
            }
            case "--anim-swap":
            {
                // Builds (never into the mod or the game) a costume package with other characters' animations in place of
                // its own, then reads it back: each slot must come from the new set and decode to the donor's keys exactly.
                // --anim-swap <mod | package.upk> <slot>=<donor package or mod>:<donor animation> [...] --build <dir> [--package <file>]
                string? sgr2 = settings.ResolvedGameRoot(data);
                string? scooked = sgr2 != null && Settings.IsGameRoot(sgr2) ? Settings.Cooked(sgr2) : null;
                int bAt = rest.IndexOf("--build"), pAt = rest.IndexOf("--package");
                string? outDir = bAt >= 0 && bAt + 1 < rest.Count ? rest[bAt + 1] : null;
                if (rest.Count < 3 || outDir == null || scooked == null) { Console.WriteLine("--anim-swap <mod | package.upk> <slot>=<donor package>:<animation> [...] --build <dir> [--package <file>]  (needs the game folder)"); return 1; }
                Mod? sm = rest[1].EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? null : lib.Find(rest[1]);
                string spath, sfile;
                if (sm == null) { spath = rest[1]; sfile = Path.GetFileName(rest[1]); }
                else
                {
                    sfile = pAt >= 0 && pAt + 1 < rest.Count ? rest[pAt + 1] : sm.Manifest.UpkReplacements.FirstOrDefault(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) ?? "";
                    spath = Path.Combine(sm.Folder, sfile);
                }
                if (!File.Exists(spath)) { Console.WriteLine($"no package {spath}"); return 1; }
                var files = CostumeAnims.FilesFor(sm, scooked);
                var mine = CostumeAnims.Read(spath, sfile, files);
                if (mine == null) { Console.WriteLine($"{sfile}: no character mesh component"); return 1; }
                var swaps = new List<AnimSwap.Swap>();
                var donorAnims = new Dictionary<string, CostumeAnims.Anim>(StringComparer.OrdinalIgnoreCase);
                foreach (string arg in rest.Skip(2).Where(a => a.Contains('=') && a.Contains(':')))
                {
                    string slot = arg[..arg.IndexOf('=')], donor = arg[(arg.IndexOf('=') + 1)..arg.LastIndexOf(':')], dname = arg[(arg.LastIndexOf(':') + 1)..];
                    if (!mine.Anims.Any(a => a.Name.Equals(slot, StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"  note: {sfile} has no animation '{slot}' yet; it's added");
                    // The donor: a mod (its first character package) or a game package by file name.
                    Mod? dm = lib.Find(donor);
                    string dfile = dm?.Manifest.UpkReplacements.FirstOrDefault(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) ?? (donor.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? donor : donor + ".upk");
                    string? dpath = dm != null ? Path.Combine(dm.Folder, dfile) : files(dfile)?.Path;
                    if (dpath == null || !File.Exists(dpath)) { Console.WriteLine($"no donor package {donor}"); return 1; }
                    var da = CostumeAnims.Read(dpath, dfile, CostumeAnims.FilesFor(dm, scooked))?.Anims.FirstOrDefault(a => a.Name.Equals(dname, StringComparison.OrdinalIgnoreCase));
                    if (da == null || da.From.File == null) { Console.WriteLine($"{dfile} has no animation '{dname}'"); return 1; }
                    swaps.Add(new AnimSwap.Swap(slot, da.From.File, da.From.Export, da.Export));
                    donorAnims[slot] = da;
                    Console.WriteLine($"{slot} ← {da.From.PackageName}.upk · {da.From.Path} · {da.Name}");
                }
                if (swaps.Count == 0) { Console.WriteLine("no swaps given (slot=donor:animation)"); return 1; }
                var slog = new List<string>();
                byte[] built;
                try { built = AnimSwap.Build(spath, mine.Class, swaps, slog, mine.Inherited ? [.. mine.Sets.Select(s => s.Path)] : null); }
                catch (InvalidDataException ex) { foreach (string l in slog) Console.WriteLine("  " + l); Console.WriteLine("can't: " + ex.Message); return 1; }
                foreach (string l in slog) Console.WriteLine("  " + l);
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, sfile);
                if (Path.GetFullPath(outPath).Equals(Path.GetFullPath(spath), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("the build folder can't be the package's own"); return 1; }
                File.WriteAllBytes(outPath, built);
                Console.WriteLine($"built: {outPath} ({built.Length:N0} bytes)");
                // Read back as the game would find the animations.
                var after = CostumeAnims.Read(outPath, sfile, name => name.Equals(Path.GetFileNameWithoutExtension(sfile), StringComparison.OrdinalIgnoreCase) ? (outPath, true) : files(name));
                int bad = 0;
                foreach (var (slot, da) in donorAnims)
                {
                    var a = after?.Anims.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
                    if (a == null || a.From.Kind != CostumeAnims.Source.Costume || !a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase)) { bad++; Console.WriteLine($"  FAIL {slot}: comes from {(a == null ? "nowhere" : a.From.Path)}"); continue; }
                    var x = ModAnimations.Load(da.Ref); var y = ModAnimations.Load(a.Ref);
                    bool same = x != null && y != null && x.Tracks.Count == y.Tracks.Count && x.Tracks.All(kv => y.Tracks.TryGetValue(kv.Key, out var t)
                        && t.PositionKeys.SequenceEqual(kv.Value.PositionKeys) && t.RotationKeys.SequenceEqual(kv.Value.RotationKeys));
                    if (!same) bad++;
                    Console.WriteLine($"  {(same ? "ok  " : "FAIL")} {slot}: from {a.From.Path} (set {a.From.Order + 1} of {after!.Sets.Count}), {y?.Tracks.Count ?? 0} tracks {(same ? "identical to the donor's" : "differ from the donor's")}");
                }
                Console.WriteLine(bad == 0 ? "PASS" : $"{bad} problem(s)");
                // --create <name>: a new mod (top of the list, turned off) made from the costume's mod with this package in;
                // the mod itself isn't changed. For testing in game before the editor does it.
                int cAt = rest.IndexOf("--create");
                if (bad == 0 && cAt >= 0 && cAt + 1 < rest.Count && sm != null)
                {
                    var d = ModDraft.From(sm);
                    d.Name = rest[cAt + 1];
                    d.Packages = [.. d.Packages.Select(x => x.File.Equals(sfile, StringComparison.OrdinalIgnoreCase) ? (x.File, outPath) : x)];
                    d.Notes = (d.Notes.Length > 0 ? d.Notes + " " : "") + "Animations: " + string.Join(", ", donorAnims.Select(kv => $"{kv.Key} from {kv.Value.From.PackageName}")) + ".";
                    string? made = ModWriter.Save(lib, d, null, out string? cerr);
                    Console.WriteLine(made != null ? $"created mod '{d.Name}' ({made}), at the top of the list, turned off" : "can't create the mod: " + cerr);
                    if (made == null) return 1;
                }
                return bad == 0 ? 0 : 1;
            }
            case "--bank-check":
            {
                // Read-only: every bank of every .pck (or one) parsed; prints the ones that fail.
                string? bgr = settings.ResolvedGameRoot(data);
                if (bgr == null) return 1;
                foreach (string pck in rest.Count > 1 ? [rest[1]] : Directory.EnumerateFiles(Settings.Cooked(bgr), "*.pck"))
                    foreach (string l in Akpk.CheckBanks(pck)) Console.WriteLine($"{Path.GetFileName(pck)}: {l}");
                return 0;
            }
            case "--sound-codecs":
            {
                // Read-only: which codecs the game's sound banks use (Sound objects' plugin IDs), over every .pck.
                string? cgr2 = settings.ResolvedGameRoot(data);
                if (cgr2 == null) return 1;
                var all = new Dictionary<uint, (int Count, HashSet<byte> St, HashSet<string> Files)>();
                foreach (string pck in rest.Count > 1 && rest[1].EndsWith(".pck", StringComparison.OrdinalIgnoreCase) ? [rest[1]] : Directory.EnumerateFiles(Settings.Cooked(cgr2), "*.pck"))
                    foreach (var (id, (n, st)) in Akpk.Codecs(pck))
                    {
                        if (!all.TryGetValue(id, out var v)) v = (0, [], []);
                        v.St.UnionWith(st); v.Files.Add(Path.GetFileName(pck));
                        all[id] = (v.Count + n, v.St, v.Files);
                    }
                foreach (var (id, v) in all.OrderByDescending(x => x.Value.Count))
                    Console.WriteLine($"0x{id:X8}: {v.Count} sound(s), stream types {string.Join(",", v.St)}, in {v.Files.Count} file(s): {string.Join(", ", v.Files.Take(4))}");
                return 0;
            }
            case "--voice-shift-pack":
            {
                // In-game test of shifted voice lines as PCM (Kurt, 2026-10-02): a new mod from <mod> whose voice set plays, for
                // the lines whose event has one of <event parts>, copies of those events (named …_mhoshift) added by a sound
                // pack with the shifted audio as PCM. --voice-shift-pack <mod> <part,part> <semitones> <formant> --create <name>
                int cAt = rest.IndexOf("--create");
                var vm = rest.Count > 4 ? lib.Find(rest[1]) : null;
                string? vgr = settings.ResolvedGameRoot(data);
                if (vm == null || vgr == null || cAt < 0 || cAt + 1 >= rest.Count) { Console.WriteLine("--voice-shift-pack <mod> <event part[,part]> <semitones> <formant> --create <name>"); return 1; }
                string cooked2 = Settings.Cooked(vgr);
                float st = float.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture), fm = float.Parse(rest[4], System.Globalization.CultureInfo.InvariantCulture);
                // --legacy: what MHModManager 1.0.1 can apply too. Its patches clone the sound of a named event in a named bank,
                // codec included, so the lines go into the bank of a stock PCM sound (Sentinel's death, wwisedefaultbank_
                // sentinelsfx in SFX_InitialDownloadChunk_INT.pck) cloned from it, and the costume's new events require that
                // bank (its AkBank copied into the package).
                bool legacy = rest.Contains("--legacy");
                // --vorbis: the lines as Wwise Vorbis (WwiseVorbisWrite, q0.4: the setup the game's own voice files use), cloned
                // from the line's own event in its own bank (named by its AkBank): what MHModManager 1.0.1 applies as well.
                bool vorbis = rest.Contains("--vorbis");
                const string donorEvent = "play_sfx_pwr_sentinel_death", donorBank = "wwisedefaultbank_sentinelsfx", donorPck = "SFX_InitialDownloadChunk_INT.pck", donorPkg = "UC__MarvelAgent_Sentinel_SF.upk";
                int wAt = rest.IndexOf("--warmth");
                float wm = wAt >= 0 && wAt + 1 < rest.Count ? float.Parse(rest[wAt + 1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                string[] parts = rest[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
                string work = Path.Combine(lib.DataFolder, "voice-shift-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(work);
                var draftV = ModDraft.From(vm);
                var patches = new List<object>(); var wems = new Dictionary<string, byte[]>();
                var packsV = vm.Manifest.AudioPacks.Select(p => Path.Combine(vm.Folder, p)).ToList();
                foreach (string file in vm.Manifest.UpkReplacements)
                {
                    string src = Path.Combine(vm.Folder, file);
                    var lines = VoiceSet.Read(file, src, vm.Manifest.VoiceOff ?? []).Where(l => !l.Off && parts.Any(pt => l.Event.Contains(pt, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (lines.Count == 0) continue;
                    string cur = src;
                    var changes = new List<(int, string?)>();
                    int bankRef = 0;
                    if (legacy)
                    {
                        // The donor bank's AkBank into the package (kept at its path: the same object as the Sentinel's).
                        var spk = MhoPackageModifier.Package.Open(StockFiles.For(cooked2, donorPkg));
                        int bi = Array.FindIndex(spk.Exports, e => e.ObjectName.Equals(donorBank, StringComparison.OrdinalIgnoreCase) && spk.ClassOf(e).Equals("AkBank", StringComparison.OrdinalIgnoreCase));
                        var dst = MhoPackageModifier.Package.Open(cur);
                        var bc = CrossMove.Quiet(() => MhoPackageModifier.ExportCopy.Copy(spk, bi, dst, []), out string bsaid) ?? throw new InvalidDataException("AkBank copy: " + bsaid);
                        cur = Path.Combine(work, Guid.NewGuid().ToString("N")[..6] + "_" + file);
                        File.WriteAllBytes(cur, bc.Output);
                        bankRef = bc.RootRef;
                        Console.WriteLine($"AkBank {spk.PathOf(spk.Exports[bi])} copied in (#{bankRef})");
                    }
                    foreach (var l in lines)
                    {
                        string leaf = l.Event[(l.Event.LastIndexOf('.') + 1)..], group = l.Event[..l.Event.LastIndexOf('.')], nleaf = leaf + "_mhoshift";
                        // The event's copy in the package, under the new name (its bank reference kept).
                        var pkgV = MhoPackageModifier.Package.Open(cur);
                        int ei = Array.FindIndex(pkgV.Exports, e => pkgV.PathOf(e).Equals(l.Event, StringComparison.OrdinalIgnoreCase));
                        if (ei < 0) { Console.WriteLine($"no AkEvent {l.Event} in {file}"); return 1; }
                        if (!pkgV.Exports.Any(e => e.ObjectName.Equals(nleaf, StringComparison.OrdinalIgnoreCase)))
                        {
                            var cp = CrossMove.Quiet(() => MhoPackageModifier.ExportCopy.Copy(pkgV, ei, pkgV, [], nleaf), out string said) ?? throw new InvalidDataException("event copy: " + said);
                            byte[] outBytes = cp.Output;
                            if (legacy)
                            {
                                // The copy requires the donor bank instead of the voice's own.
                                var np = MhoPackageModifier.Package.FromBytes(outBytes);
                                byte[] ed = np.ReadExportBytes(np.Exports[cp.RootRef - 1]).ToArray();
                                var rb = MhoPackageModifier.TagWalker.Walk(np, ed, 4)?.FirstOrDefault(t => t.Name.Equals("RequiredBank", StringComparison.OrdinalIgnoreCase) && t.Size == 4)
                                         ?? throw new InvalidDataException("the event has no RequiredBank");
                                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(ed.AsSpan(rb.ValueAt), bankRef);
                                outBytes = MhoPackageModifier.PackageRebuilder.Rebuild(np, new Dictionary<int, Func<long, byte[]>> { [cp.RootRef - 1] = _ => ed }, [], out _);
                            }
                            cur = Path.Combine(work, Guid.NewGuid().ToString("N")[..6] + "_" + file);
                            File.WriteAllBytes(cur, outBytes);
                        }
                        changes.Add((l.Offset, group + "." + nleaf));
                        // The shifted audio as PCM, and where the original event lives.
                        byte[] wav = VoiceAudio.ToWav(VoiceAudio.Wem(l.Event, packsV, cooked2).Wem);
                        byte[] shiftedWav = VoiceShift.Shift(wav, st, fm, VoiceShift.Mode.Natural, wm);
                        byte[] wem = vorbis ? WwiseVorbisWrite.ToWem(VorbisEncode.Encode(shiftedWav, 0.4f), WavPcm.Read(shiftedWav).Channels[0].Length) : VoiceShift.PcmWem(shiftedWav);
                        // The bank by its AkBank's name (its ID is the name's hash): what the old manager looks banks up by.
                        string? bankName = null;
                        if (vorbis)
                        {
                            byte[] ed = pkgV.ReadExportBytes(pkgV.Exports[ei]).ToArray();
                            if (MhoPackageModifier.TagWalker.Walk(pkgV, ed, 4)?.FirstOrDefault(t => t.Name.Equals("RequiredBank", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } rbt)
                                bankName = pkgV.RefName(BitConverter.ToInt32(ed, rbt.ValueAt));
                        }
                        var where = legacy ? (Pck: donorPck, Bank: SoundPack.Fnv(donorBank))
                                    : Akpk.FindEvent(cooked2, SoundPack.Fnv(leaf), group.Replace("vo", "", StringComparison.OrdinalIgnoreCase))
                                    ?? throw new InvalidDataException($"{leaf} isn't in the game's sound files");
                        string wemName = nleaf + ".wem";
                        wems[wemName] = wem;
                        patches.Add(new Dictionary<string, string>
                        {
                            ["type"] = "new_event", ["original_event_name"] = legacy ? donorEvent : leaf, ["event_name"] = nleaf, ["event_hash"] = $"0x{SoundPack.Fnv(nleaf):X8}",
                            ["action_id"] = $"0x{SoundPack.Fnv(nleaf + "_action"):X8}", ["sound_id"] = $"0x{SoundPack.Fnv(nleaf + "_sound"):X8}", ["source_id"] = $"0x{SoundPack.Fnv(nleaf + "_source"):X8}",
                            ["wem_file"] = wemName, ["bank_name"] = legacy ? donorBank : bankName ?? $"0x{where.Bank:X8}", ["pck_file"] = where.Pck,
                        });
                        if (bankName != null && SoundPack.Fnv(bankName) != where.Bank) throw new InvalidDataException($"the event's bank {bankName} hashes to {SoundPack.Fnv(bankName):X8}, not the bank it's in ({where.Bank:X8})");
                        Console.WriteLine($"{l.Situation} · {leaf} → {nleaf}: {wem.Length:N0} bytes {(vorbis ? "Wwise Vorbis" : "PCM")}; bank {bankName ?? $"0x{where.Bank:X8}"} in {where.Pck}");
                    }
                    string outPkg = Path.Combine(work, file);
                    File.WriteAllBytes(outPkg, VoiceSet.Write(cur, changes) ?? throw new InvalidDataException("voice set not written"));
                    int k = draftV.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                    draftV.Packages[k] = (file, outPkg);
                }
                if (patches.Count == 0) { Console.WriteLine("no matching voice lines"); return 1; }
                string packPath = Path.Combine(work, "VoiceShiftTest.mhsfx");
                using (var z = System.IO.Compression.ZipFile.Open(packPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var s2 = new StreamWriter(z.CreateEntry("mod.json").Open()))
                        s2.Write(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = "Voice Shift Test", ["patches"] = patches }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    foreach (var (n, b) in wems) using (var e = z.CreateEntry(n).Open()) e.Write(b);
                }
                draftV.SoundPacks.Add(packPath);
                draftV.Name = rest[cAt + 1];
                draftV.Notes = $"Voice shift test{(legacy ? " (legacy: Sentinel PCM donor)" : vorbis ? " (Wwise Vorbis)" : "")}: {string.Join(", ", parts)} at pitch {st:+0.#;-0.#;0}, formant {fm:+0.#;-0.#;0}, warmth {wm:+0.#;-0.#;0} dB (Natural), as PCM.";
                string? made = ModWriter.Save(lib, draftV, null, out string? verr);
                Console.WriteLine(made != null ? $"created mod '{draftV.Name}', at the top of the list, turned off" : "can't create: " + verr);
                try { Directory.Delete(work, true); } catch (IOException) { }
                return made != null ? 0 : 1;
            }
            case "--bank-names":
            {
                // Read-only: which names in the game's packages hash to the given bank IDs (wwisedefaultbank_… names).
                // --bank-names <hex id> [hex id ...]
                string? bgr = settings.ResolvedGameRoot(data);
                if (bgr == null || rest.Count < 2) return 1;
                var want = rest.Skip(1).Where(h => !h.StartsWith("--")).Select(h => Convert.ToUInt32(h.Replace("0x", ""), 16)).ToHashSet();
                var found = new Dictionary<uint, HashSet<string>>();
                foreach (string f in Directory.EnumerateFiles(Settings.Cooked(bgr), "*.upk"))
                {
                    if (Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    MhoPackageModifier.Package pk;
                    try { pk = MhoPackageModifier.Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { continue; }
                    foreach (string n in rest.Contains("--any") ? pk.Names : pk.Names.Where(n => n.StartsWith("wwisedefaultbank", StringComparison.OrdinalIgnoreCase)))
                        if (want.Contains(SoundPack.Fnv(n))) { if (!found.TryGetValue(SoundPack.Fnv(n), out var set)) found[SoundPack.Fnv(n)] = set = []; if (set.Count < 6) set.Add($"{n} ({Path.GetFileName(f)})"); }
                }
                foreach (uint id in want) Console.WriteLine($"0x{id:X8}: {(found.TryGetValue(id, out var s2) ? string.Join("; ", s2) : "no name found")}");
                return 0;
            }
            case "--vorbis-test":
            {
                // Read-only: a mod's voice line, shifted, Vorbis-encoded (OggVorbisEncoder) and decoded back (NVorbis): sizes,
                // length and SNR. --vorbis-test <mod> <event part> <semitones> <formant> <warmth> [quality] [--out <dir>]
                var vm = rest.Count > 5 ? lib.Find(rest[1]) : null;
                string? vgr = settings.ResolvedGameRoot(data);
                if (vm == null || vgr == null) { Console.WriteLine("--vorbis-test <mod> <event part> <semitones> <formant> <warmth> [quality] [--out <dir>]"); return 1; }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var line = vm.Manifest.UpkReplacements.SelectMany(f => VoiceSet.Read(f, Path.Combine(vm.Folder, f), vm.Manifest.VoiceOff ?? []))
                    .FirstOrDefault(l => !l.Off && l.Event.Contains(rest[2], StringComparison.OrdinalIgnoreCase));
                if (line == null) { Console.WriteLine($"no voice line with '{rest[2]}'"); return 1; }
                var (wem, from) = VoiceAudio.Wem(line.Event, vm.Manifest.AudioPacks.Select(p => Path.Combine(vm.Folder, p)).ToList(), Settings.Cooked(vgr));
                byte[] wav = VoiceShift.Shift(VoiceAudio.ToWav(wem), float.Parse(rest[3], inv), float.Parse(rest[4], inv), VoiceShift.Mode.Natural, float.Parse(rest[5], inv));
                float q = rest.Count > 6 && float.TryParse(rest[6], System.Globalization.NumberStyles.Float, inv, out float qq) ? qq : 0.5f;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var enc = VorbisEncode.Encode(wav, q);
                long ms = sw.ElapsedMilliseconds;
                var (dec, drate) = VorbisEncode.Decode(enc.Ogg);
                var (src, srate) = WavPcm.Read(wav);
                var (snr, shift) = VorbisEncode.Snr(src[0], dec[0]);
                Console.WriteLine($"{line.Event} ({from}): game .wem {wem.Length:N0} bytes, shifted WAV {wav.Length:N0}");
                Console.WriteLine($"encoded q{q}: {enc.Ogg.Length:N0} bytes Ogg ({enc.Ogg.Length * 8.0 / (enc.Samples / (double)enc.Rate) / 1000:0} kbit/s), {enc.Audio.Count} audio packets, setup {enc.Setup.Length:N0} bytes, in {ms} ms");
                Console.WriteLine($"decoded: {dec[0].Length} samples at {drate} Hz (source {src[0].Length} at {srate} Hz), {dec.Length} channel(s); SNR {snr:0.0} dB (aligned at {shift})");
                // Phase 2's first question: are the encoder's codebooks in Wwise's library (the game's runtime has only those)?
                var (stripped, _) = WwiseVorbisWrite.StripSetup(enc.Setup, enc.Channels);
                Console.WriteLine($"stripped setup {stripped.Length} bytes, hash {WwiseVorbisWrite.SetupHash(stripped):X8}; blocksizes {enc.Info[28] & 15}/{enc.Info[28] >> 4}");
                // Phase 2: the .wem the game's way, read back by the ww2ogg port and decoded: the same samples as the Ogg?
                try
                {
                    byte[] wemOut = WwiseVorbisWrite.ToWem(enc, src[0].Length);
                    var (back, _) = VorbisEncode.Decode(WwiseVorbis.ToOgg(wemOut, positions: true));
                    int same = Enumerable.Range(0, Math.Min(back[0].Length, dec[0].Length)).Count(i => back[0][i] == dec[0][i]);
                    Console.WriteLine($"wem: {wemOut.Length:N0} bytes; read back {back[0].Length} samples, {same} identical to the Ogg's decode ({dec[0].Length})");
                    if (rest.IndexOf("--out") is int oo && oo >= 0 && oo + 1 < rest.Count) File.WriteAllBytes(Path.Combine(rest[oo + 1], line.Event[(line.Event.LastIndexOf('.') + 1)..] + $"_q{q}.wem"), wemOut);
                }
                catch (InvalidDataException ex) { Console.WriteLine("wem: " + ex.Message); }
                var books = WwiseVorbisWrite.MatchCodebooks(enc.Setup);
                Console.WriteLine($"codebooks: {books.Count}, {books.Count(b => b.Id >= 0)} found in Wwise's library ({WwiseVorbisWrite.LibraryCount} entries): " + string.Join(" ", books.Select(b => b.Id >= 0 ? b.Id.ToString() : "MISSING")));
                int oAt = rest.IndexOf("--out");
                if (oAt >= 0 && oAt + 1 < rest.Count)
                {
                    Directory.CreateDirectory(rest[oAt + 1]);
                    string stem = Path.Combine(rest[oAt + 1], line.Event[(line.Event.LastIndexOf('.') + 1)..] + $"_q{q}");
                    File.WriteAllBytes(stem + ".ogg", enc.Ogg);
                    File.WriteAllBytes(stem + "_shifted.wav", wav);
                    Console.WriteLine($"wrote {stem}.ogg and _shifted.wav");
                }
                return 0;
            }
            case "--voice-shift-mod":
            {
                // A whole costume voice shifted (VoiceShiftBuild), as a new mod made from <mod> (top of the list, turned off).
                // --voice-shift-mod <mod> <pitch> <formant> <warmth> --create <name>
                int cAt = rest.IndexOf("--create");
                var vm = rest.Count > 4 ? lib.Find(rest[1]) : null;
                string? vgr = settings.ResolvedGameRoot(data);
                if (vm == null || vgr == null || cAt < 0 || cAt + 1 >= rest.Count) { Console.WriteLine("--voice-shift-mod <mod> <pitch> <formant> <warmth> --create <name>"); return 1; }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var draftS = ModDraft.From(vm);
                string work = Path.Combine(lib.DataFolder, "voice-shift-" + Guid.NewGuid().ToString("N")[..8]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int shiftedPkgs = 0;
                foreach (string file in vm.Manifest.UpkReplacements)
                {
                    string src = Path.Combine(vm.Folder, file);
                    if (VoiceSet.Read(file, src, vm.Manifest.VoiceOff ?? []).Count == 0) continue;
                    var entry = new VoiceShiftEntry { Package = file, Pitch = float.Parse(rest[2], inv), Formant = float.Parse(rest[3], inv), Warmth = float.Parse(rest[4], inv) };
                    var packs = draftS.SoundPacks.Where(p => !Path.GetFileName(p).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase)).ToList();
                    var (pkgOut, pack, log) = VoiceShiftBuild.Build(src, file, vm.Manifest.VoiceOff ?? [], packs, Settings.Cooked(vgr), entry, Path.Combine(work, file));
                    foreach (string l in log.Take(8)) Console.WriteLine("  " + l);
                    if (log.Count > 8) Console.WriteLine($"  … {log.Count - 8} more");
                    int k = draftS.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                    draftS.Packages[k] = (file, pkgOut);
                    draftS.SoundPacks.RemoveAll(p => Path.GetFileName(p).Equals(VoiceShiftBuild.PackName(file), StringComparison.OrdinalIgnoreCase));
                    draftS.SoundPacks.Add(pack);
                    draftS.VoiceShifts.RemoveAll(v => v.Package.Equals(file, StringComparison.OrdinalIgnoreCase));
                    draftS.VoiceShifts.Add(entry);
                    shiftedPkgs++;
                }
                if (shiftedPkgs == 0) { Console.WriteLine("no package with a voice set"); return 1; }
                draftS.Name = rest[cAt + 1];
                string? made = ModWriter.Save(lib, draftS, null, out string? err);
                Console.WriteLine(made != null ? $"created '{draftS.Name}' in {sw.Elapsed.TotalSeconds:0.0} s" : "can't create: " + err);
                try { Directory.Delete(work, true); } catch (IOException) { }
                return made != null ? 0 : 1;
            }
            case "--wem-setups":
            {
                // Read-only: every Wwise Vorbis stream / embedded file in the game's .pck files, grouped by setup hash, sample
                // rate, channels and block sizes, with the decoder allocation sizes each declares (are they one per setup?).
                string? wgr = settings.ResolvedGameRoot(data);
                if (wgr == null) return 1;
                var groups = new Dictionary<(uint Hash, int Rate, int Ch, int Bs, uint A, uint A64), (int N, HashSet<string> Files)>();
                foreach (string pck in Directory.EnumerateFiles(Settings.Cooked(wgr), "*.pck"))
                {
                    using var f = File.OpenRead(pck);
                    Akpk a;
                    try { a = Akpk.Read(f); } catch (InvalidDataException) { continue; }
                    foreach (var e in a.Streams)
                    {
                        if (e.Size < 0x60) continue;
                        byte[] w = Akpk.ReadData(f, e);
                        if (BitConverter.ToUInt16(w, 0x14) != 0xFFFF || BitConverter.ToUInt32(w, 0x10) != 0x42) continue;
                        var key = (BitConverter.ToUInt32(w, 0x2C + 0x24), (int)BitConverter.ToUInt32(w, 0x18), (int)BitConverter.ToUInt16(w, 0x16), w[0x2C + 0x28] * 16 + w[0x2C + 0x29], BitConverter.ToUInt32(w, 0x2C + 0x1C), BitConverter.ToUInt32(w, 0x2C + 0x20));
                        if (!groups.TryGetValue(key, out var g)) g = (0, []);
                        g.Files.Add(Path.GetFileName(pck));
                        groups[key] = (g.N + 1, g.Files);
                    }
                }
                foreach (var (k, g) in groups.OrderByDescending(x => x.Value.N).Take(40))
                    Console.WriteLine($"{k.Hash:X8} {k.Rate,6} Hz {k.Ch}ch bs {k.Bs >> 4}/{k.Bs & 15} alloc {k.A:X}/{k.A64:X}: {g.N} streams in {g.Files.Count} file(s), e.g. {string.Join(", ", g.Files.Take(3))}");
                Console.WriteLine($"{groups.Count} distinct; setups with more than one alloc pair: {groups.GroupBy(x => (x.Key.Hash, x.Key.Rate, x.Key.Ch)).Count(x => x.Select(y => (y.Key.A, y.Key.A64)).Distinct().Count() > 1)}");
                return 0;
            }
            case "--wem-fields":
            {
                // Read-only: the vorb fields of a .pck's Wwise Vorbis streams next to what can be measured (data size, packets,
                // largest / average packet, setup size, samples), to work out the undocumented ones. --wem-fields <pck> [count]
                if (rest.Count < 2) return 1;
                int want = rest.Count > 2 ? int.Parse(rest[2]) : 12, shown = 0;
                using var f = File.OpenRead(rest[1]);
                var pk = Akpk.Read(f);
                Console.WriteLine("samples  sig  @08      @0C        @18        @1C      @20      uid       bs  | data  setup pkts max  avg  sum(pkt+2)");
                foreach (var e in pk.Streams)
                {
                    byte[] w = Akpk.ReadData(f, e);
                    if (w.Length < 0x60 || BitConverter.ToUInt16(w, 0x14) != 0xFFFF || BitConverter.ToUInt32(w, 0x10) != 0x42) continue;
                    int v = 0x2C;
                    uint U(int o) => BitConverter.ToUInt32(w, v + o);
                    int dataAt = 0x5E; uint dataSize = BitConverter.ToUInt32(w, 0x5A);
                    if (System.Text.Encoding.ASCII.GetString(w, 0x56, 4) != "data") continue;
                    uint setupOff = U(0x10), audioOff = U(0x14);
                    int setupSize = BitConverter.ToUInt16(w, dataAt + (int)setupOff);
                    int at = dataAt + (int)audioOff, end = dataAt + (int)dataSize, n = 0, max = 0; long sum = 0;
                    while (at + 2 <= end) { int sz = BitConverter.ToUInt16(w, at); n++; max = Math.Max(max, sz); sum += sz + 2; at += 2 + sz; }
                    if (rest.Contains("--hash") && shown == 0)
                    {
                        byte[] setup = w.AsSpan(dataAt + (int)setupOff + 2, setupSize).ToArray();
                        uint Fnv1(byte[] b) { uint h = 2166136261; foreach (byte c in b) { h *= 16777619; h ^= c; } return h; }
                        uint Fnv1a(byte[] b) { uint h = 2166136261; foreach (byte c in b) { h ^= c; h *= 16777619; } return h; }
                        uint Crc(byte[] b) => System.IO.Hashing.Crc32.HashToUInt32(b);
                        byte[] withSize = w.AsSpan(dataAt + (int)setupOff, setupSize + 2).ToArray();
                        Console.WriteLine($"uid {U(0x24):X8}: fnv1 {Fnv1(setup):X8} fnv1a {Fnv1a(setup):X8} crc {Crc(setup):X8}; with size: fnv1 {Fnv1(withSize):X8} fnv1a {Fnv1a(withSize):X8} crc {Crc(withSize):X8}");
                        // Codebook ids only (10 bits each after the 8-bit count)
                        var bi = new WwiseVorbis.BitIn(setup, 0); int nb = (int)bi.Read(8) + 1; var ids = new List<byte>();
                        for (int k = 0; k < nb; k++) { uint id = bi.Read(10); ids.AddRange(BitConverter.GetBytes((ushort)id)); }
                        Console.WriteLine($"  codebook ids ({nb}): fnv1 {Fnv1(ids.ToArray()):X8} fnv1a {Fnv1a(ids.ToArray()):X8} crc {Crc(ids.ToArray()):X8}");
                    }
                    WwiseVorbis.ToOgg(w, positions: true);
                    long blocks = WwiseVorbis.LastBlockTotal;
                    Console.Write($"[blocks {blocks}, -samples {blocks - U(0)}, extra {U(0x18) >> 16}] ");
                    Console.WriteLine($"{U(0),7} {U(4),4:X} {U(8),8:X} {U(0x0C),10:X} {U(0x18),10:X} {U(0x1C),8:X} {U(0x20),8:X} {U(0x24):X8} {w[v + 0x28]}/{w[v + 0x29]} | {dataSize,5} {setupSize,5} {n,4} {max,4:X} {(n > 0 ? (sum - 2L * n) / n : 0),4:X} {sum,5}");
                    if (++shown >= want) break;
                }
                return 0;
            }
            case "--voice-shift-test":
            {
                // Self-test of the pitch shifter on a synthetic tone (no files).
                var res = VoiceShift.Test();
                foreach (string l in res) Console.WriteLine(l);
                return res.All(l => l.StartsWith("ok")) ? 0 : 1;
            }
            case "--voice-shift":
            {
                // Read-only: a mod's voice line (event name part), shifted, as a WAV to listen to.
                // --voice-shift <mod> <event part> <semitones> <formant semitones> <tape|natural> <out.wav>
                var vm = rest.Count > 6 ? lib.Find(rest[1]) : null;
                string? vgr = settings.ResolvedGameRoot(data);
                if (vm == null || vgr == null) { Console.WriteLine("--voice-shift <mod> <event part> <semitones> <formant semitones> <tape|natural> <out.wav>"); return 1; }
                var line = vm.Manifest.UpkReplacements.SelectMany(f => VoiceSet.Read(f, Path.Combine(vm.Folder, f), vm.Manifest.VoiceOff ?? []))
                    .FirstOrDefault(l => !l.Off && l.Event.Contains(rest[2], StringComparison.OrdinalIgnoreCase));
                if (line == null) { Console.WriteLine($"no voice line with '{rest[2]}'"); return 1; }
                var packs = vm.Manifest.AudioPacks.Select(p => Path.Combine(vm.Folder, p)).ToList();
                byte[] wav = VoiceAudio.ToWav(VoiceAudio.Wem(line.Event, packs, Settings.Cooked(vgr)).Wem);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int wAt2 = rest.IndexOf("--warmth");
                byte[] outWav = VoiceShift.Shift(wav, float.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture), float.Parse(rest[4], System.Globalization.CultureInfo.InvariantCulture),
                    rest[5].StartsWith("t", StringComparison.OrdinalIgnoreCase) ? VoiceShift.Mode.Tape : VoiceShift.Mode.Natural,
                    wAt2 >= 0 && wAt2 + 1 < rest.Count ? float.Parse(rest[wAt2 + 1], System.Globalization.CultureInfo.InvariantCulture) : 0);
                File.WriteAllBytes(rest[6], outWav);
                Console.WriteLine($"{line.Event}: {wav.Length:N0} → {outWav.Length:N0} bytes in {sw.ElapsedMilliseconds} ms → {rest[6]}");
                return 0;
            }
            case "--voice-lines":
            {
                // Read-only: a mod's voice lines as the editor's Voice tab lists them (with its manifest's turned-off lines).
                var vm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (vm == null) { Console.WriteLine("--voice-lines <mod>"); return 1; }
                var off = vm.Manifest.VoiceOff ?? [];
                var all = vm.Manifest.UpkReplacements.SelectMany(f => VoiceSet.Read(f, Path.Combine(vm.Folder, f), off)).ToList();
                foreach (var l in all.Where(l => rest.Contains("--all") || l.Off))
                    Console.WriteLine($"  {(l.Missing ? "missing" : l.Off ? "off    " : "on     ")}  {l.Situation}{(l.Detail.Length > 0 ? " · " + l.Detail : "")}  {l.Event}");
                Console.WriteLine($"{all.Count} line(s): {all.Count(l => !l.Off)} on, {all.Count(l => l.Off && !l.Missing)} turned off, {all.Count(l => l.Missing)} not in the moved voice (off)");
                return 0;
            }
            case "--voice-test":
            {
                // Test (scratch copies only): three lines off, read back as off; back on, the package byte-identical to before.
                var tm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                string? tf = tm?.Manifest.UpkReplacements.FirstOrDefault(f => VoiceSet.Read(f, Path.Combine(tm.Folder, f), []).Count > 0);
                if (tm == null || tf == null) { Console.WriteLine("--voice-test <mod with a voice set>"); return 1; }
                string src = Path.Combine(tm.Folder, tf), tmp = Path.Combine(Path.GetTempPath(), "voicetest_" + tf);
                var lines = VoiceSet.Read(tf, src, []);
                var pick = lines.Take(3).ToList();
                File.WriteAllBytes(tmp, VoiceSet.Write(src, [.. pick.Select(l => (l.Offset, (string?)null))])!);
                var off = pick.Select(l => new VoiceOffEntry { Package = tf, Offset = l.Offset, Event = l.Event }).ToList();
                var after = VoiceSet.Read(tf, tmp, off);
                bool offOk = pick.All(p => after.Any(a => a.Offset == p.Offset && a.Off && a.Event == p.Event)) && after.Count == lines.Count;
                byte[] back = VoiceSet.Write(tmp, [.. pick.Select(l => (l.Offset, (string?)l.Event))])!;
                var orig = MhoPackageModifier.Package.Open(src); var b2 = MhoPackageModifier.Package.FromBytes(back);
                int vi = VoiceSet.Find(orig);
                bool same = orig.ReadExportBytes(orig.Exports[vi]).SequenceEqual(b2.ReadExportBytes(b2.Exports[vi]));
                Console.WriteLine($"{(offOk ? "ok  " : "FAIL")} 3 lines off: read back off with their events ({after.Count(a => a.Off)} off of {after.Count})");
                Console.WriteLine($"{(same ? "ok  " : "FAIL")} back on: the voice set is byte-identical to the original");
                File.Delete(tmp);
                return offOk && same ? 0 : 1;
            }
            case "--voice-sources":
            case "--voice-copy":
            {
                // Read-only: the stock voice sets (--voice-sources), or a costume package with one copied in, written to <out.upk>
                // (--voice-copy <costume.upk> <voice title or file> <out.upk>; never into the game folder).
                var vst = Settings.Load();
                string? vgr = vst.ResolvedGameRoot(lib.DataFolder);
                if (vgr == null || !Settings.IsGameRoot(vgr)) { Console.WriteLine("game folder not set"); return 1; }
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var srcs = VoiceSet.Sources(Settings.Cooked(vgr));
                if (rest[0] == "--voice-sources")
                {
                    foreach (var s in srcs) Console.WriteLine($"{s.Title,-36} {Path.GetFileName(s.File)}");
                    Console.WriteLine($"{srcs.Count} voice sets ({sw2.ElapsedMilliseconds} ms)");
                    // --voice-sources <hero>…: where each hero's voice comes from in the game (the Voice tab's hint).
                    StockFiles.Init(new GameState(vgr, lib.DataFolder), vst.CleanGameFiles, Path.Combine(lib.DataFolder, "originals"));
                    foreach (string h in rest.Skip(1))
                        Console.WriteLine(VoiceSet.HeroVoice(vgr, Settings.Cooked(vgr), h) is { } hv ? $"{h}: {hv.CostumeName} ({hv.CostumeFile}) plays {hv.Voice.Title} ({Path.GetFileName(hv.Voice.File)})" : $"{h}: none found");
                    return 0;
                }
                if (rest.Count < 4) { Console.WriteLine("--voice-copy <costume.upk> <voice title or file> <out.upk>"); return 1; }
                if (Path.GetFullPath(rest[3]).StartsWith(Path.GetFullPath(Settings.Cooked(vgr)), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("refused: that's the game folder"); return 1; }
                var src = srcs.FirstOrDefault(s => s.Title.Equals(rest[2], StringComparison.OrdinalIgnoreCase) || Path.GetFileName(s.File).Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (src == null) { Console.WriteLine("no voice set " + rest[2]); return 1; }
                var vlog = new List<string>();
                File.WriteAllBytes(rest[3], VoiceSet.Replace(rest[1], src.File, vlog));
                foreach (string l in vlog) Console.WriteLine(l);
                var got = VoiceSet.Read(Path.GetFileName(rest[3]), rest[3], []);
                Console.WriteLine($"wrote {rest[3]}: {got.Count} line(s)");
                return got.Count > 0 ? 0 : 1;
            }
            case "--voice-use-test":
            {
                // Test (scratch library only, MHO_EXTMM_HOME): the editor's Use Another Voice on a mod's costume package, saved
                // through ModWriter like the editor does, then read back (lines) and every line's audio found and decoded.
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") is not { Length: > 0 } home || home.Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine("refused: needs MHO_EXTMM_HOME on a scratch library"); return 1; }
                var um = rest.Count > 2 ? lib.Find(rest[1]) : null;
                if (um == null) { Console.WriteLine("--voice-use-test <mod> <voice title>"); return 1; }
                var ust = Settings.Load();
                string ucooked = Settings.Cooked(ust.ResolvedGameRoot(lib.DataFolder)!);
                var uv = VoiceSet.Sources(ucooked).FirstOrDefault(s => s.Title.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                string? ufile = um.Manifest.UpkReplacements.FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(f, @"^UC__MarvelPlayer_[A-Za-z0-9]+_[A-Za-z0-9_]+_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (uv == null || ufile == null) { Console.WriteLine("no such voice, or the mod has no costume package"); return 1; }
                int before = VoiceSet.Read(ufile, Path.Combine(um.Folder, ufile), []).Count;
                var ulog = new List<string>();
                string uwork = Path.Combine(lib.DataFolder, "voice-work-test");
                Directory.CreateDirectory(uwork);
                string upath = Path.Combine(uwork, ufile);
                File.WriteAllBytes(upath, VoiceSet.Replace(Path.Combine(um.Folder, ufile), uv.File, ulog));
                var ud = ModDraft.From(um);
                int uk = ud.Packages.FindIndex(p => p.File.Equals(ufile, StringComparison.OrdinalIgnoreCase));
                ud.Packages[uk] = (ufile, upath);
                string? usaved = ModWriter.Save(lib, ud, um, out string? uerr);
                Directory.Delete(uwork, true);
                if (usaved == null) { Console.WriteLine("FAIL save: " + uerr); return 1; }
                var ulines = VoiceSet.Read(ufile, Path.Combine(lib.DataFolder, "mods", usaved, ufile), []);
                Console.WriteLine($"{(ulines.Count > 0 ? "ok  " : "FAIL")} {ufile}: {before} line(s) before, {ulines.Count} after ({uv.Title})");
                int uok = 0;
                foreach (var l in ulines.DistinctBy(l => l.Event))
                    try { VoiceAudio.ToWav(VoiceAudio.Wem(l.Event, [], ucooked).Wem); uok++; }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException) { Console.WriteLine($"     no sound: {l.Event}: {ex.Message}"); }
                int utotal = ulines.DistinctBy(l => l.Event).Count();
                Console.WriteLine($"{(uok == utotal ? "ok  " : "FAIL")} {uok} of {utotal} events play");
                return ulines.Count > 0 && uok == utotal ? 0 : 1;
            }
            case "--wem-to-ogg":
            {
                // Test: Wwise Vorbis → Ogg (WwiseVorbis, the ww2ogg port); compare with ww2ogg.exe's output.
                if (rest.Count < 3) { Console.WriteLine("--wem-to-ogg <in.wem> <out.ogg>"); return 1; }
                File.WriteAllBytes(rest[2], WwiseVorbis.ToOgg(File.ReadAllBytes(rest[1])));
                Console.WriteLine($"wrote {rest[2]}");
                return 0;
            }
            case "--voice-audio":
            {
                // Read-only: finds, converts and decodes the audio of every line of a mod's voice set (the Voice tab's ▶);
                // with an event name part, saves that line as .wem / .ogg / .wav into <out dir>.
                var am = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (am == null) { Console.WriteLine("--voice-audio <mod> [<event part> <out dir>]"); return 1; }
                var ast = Settings.Load();
                string? agr = ast.ResolvedGameRoot(lib.DataFolder);
                string acooked = agr != null && Settings.IsGameRoot(agr) ? Settings.Cooked(agr) : "";
                var apacks = am.Manifest.AudioPacks.Select(f => Path.Combine(am.Folder, f)).ToList();
                var alines = am.Manifest.UpkReplacements.SelectMany(f => VoiceSet.Read(f, Path.Combine(am.Folder, f), am.Manifest.VoiceOff ?? [])).ToList();
                if (rest.Count > 3)
                {
                    var one = alines.FirstOrDefault(l => l.Event.Contains(rest[2], StringComparison.OrdinalIgnoreCase));
                    if (one == null) { Console.WriteLine("no line's event has " + rest[2]); return 1; }
                    var (wem, from) = VoiceAudio.Wem(one.Event, apacks, acooked);
                    Directory.CreateDirectory(rest[3]);
                    string stem = Path.Combine(rest[3], one.Event[(one.Event.LastIndexOf('.') + 1)..]);
                    File.WriteAllBytes(stem + ".wem", wem);
                    File.WriteAllBytes(stem + ".ogg", WwiseVorbis.ToOgg(wem));
                    File.WriteAllBytes(stem + ".wav", VoiceAudio.ToWav(wem));
                    Console.WriteLine($"{one.Event}: {from} → {stem}.wem / .ogg / .wav");
                    return 0;
                }
                int ok = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var l in alines.DistinctBy(l => l.Event))
                {
                    try
                    {
                        var (wem, from) = VoiceAudio.Wem(l.Event, apacks, acooked);
                        byte[] wav = VoiceAudio.ToWav(wem);
                        ok++;
                        Console.WriteLine($"ok   {l.Event}: {from}, {wav.Length:N0} bytes WAV");
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException) { Console.WriteLine($"FAIL {l.Event}: {ex.Message}"); }
                }
                int total = alines.DistinctBy(l => l.Event).Count();
                Console.WriteLine($"{ok} of {total} events play ({sw.ElapsedMilliseconds} ms)");
                return ok == total ? 0 : 1;
            }
            case "--voice":
            {
                // Read-only: the voice set lines of a mod's costume packages (the editor's Voice tab).
                var vm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (vm == null) { Console.WriteLine("--voice <mod>"); return 1; }
                foreach (string f in vm.Manifest.UpkReplacements)
                {
                    var lines = VoiceSet.Read(f, Path.Combine(vm.Folder, f), vm.Manifest.VoiceOff ?? []);
                    if (lines.Count == 0) continue;
                    Console.WriteLine($"{f}: {lines.Count} line(s), {lines.Count(l => l.Off)} off");
                    foreach (var l in lines) Console.WriteLine($"  {(l.Off ? "[off] " : "")}{l.Situation,-28} {l.Detail,-40} {l.Event}");
                }
                return 0;
            }
        }
        return null;
    }
}
