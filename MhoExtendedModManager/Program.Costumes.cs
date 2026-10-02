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
