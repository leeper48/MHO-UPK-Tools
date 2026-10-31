using System.Drawing;
using System.Text.RegularExpressions;
namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>
    /// The Model tab's engine from the command line (the MFF model importer, moved in 2026-10-03). Null when
    /// <paramref name="cmd"/> isn't one of these. Every one writes only into the folder it's given (never the game, the clean
    /// stock folder or the MFF source: the engine's Protected check).
    /// </summary>
    static int? ModelCommand(string cmd, List<string> rest)
    {
        switch (cmd)
        {
            case "--default-parts":
            {
                // --default-parts <models folder> (read only): each MFF model's parts ticked by default, one line per model
                // (MHO_NO_OFFSKELETON=1: without the off-skeleton rule, to compare)
                if (rest.Count < 2) { Console.WriteLine("--default-parts <models folder>"); return 1; }
                foreach (string dir in Directory.EnumerateDirectories(rest[1]).Order())
                {
                    string? fbx = Directory.EnumerateFiles(dir, "*.fbx").FirstOrDefault();
                    if (fbx == null) continue;
                    try
                    {
                        var mm = MhoExtendedModManager.Model.MffModel.Load(fbx);
                        Console.WriteLine($"{Path.GetFileName(dir)}	{string.Join(" | ", mm.Parts.Where(p => p.DefaultOn).Select(p => $"{p.Name} ({p.Verts}, {mm.SkeletonShare(p):P0})"))}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(dir)}	ERROR {ex.GetType().Name}"); }
                }
                return 0;
            }
            case "--weapon-bone-census":
            {
                // --weapon-bone-census <models folder> [max] (read only): every MFF model's prop parts split off on a weapon bone
                // (Bone_w, BoneW…, Bone_ultimate…): model, part, vertices, size (cm), and whether it was ticked before (body)
                if (rest.Count < 2) { Console.WriteLine("--weapon-bone-census <models folder> [max]"); return 1; }
                int max = rest.Count > 2 && int.TryParse(rest[2], out int mx) ? mx : int.MaxValue, seen = 0, hit = 0;
                var rx = new System.Text.RegularExpressions.Regex(@"· (bone_?w(_?\d+)?|bone_?ultimate\d*|bone_s_[lr])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (string dir in Directory.EnumerateDirectories(rest[1]).Order())
                {
                    if (seen++ >= max) break;
                    string? fbx = Directory.EnumerateFiles(dir, "*.fbx").FirstOrDefault();
                    if (fbx == null) continue;
                    try
                    {
                        var mm = MhoExtendedModManager.Model.MffModel.Load(fbx);
                        var split = mm.Parts.Where(p => rx.IsMatch(p.Name)).ToList();
                        if (split.Count == 0) continue;
                        hit++;
                        foreach (var p in split)
                        {
                            var pos = p.Sections.SelectMany(x => x.Pos).ToList();
                            var d = pos.Aggregate(System.Numerics.Vector3.Max) - pos.Aggregate(System.Numerics.Vector3.Min);
                            Console.WriteLine($"{Path.GetFileName(dir)}	{p.Name}	{p.Verts}	{MathF.Max(d.X, MathF.Max(d.Y, d.Z)):0}");
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"{Path.GetFileName(dir)}	ERROR {ex.GetType().Name}: {ex.Message}"); }
                }
                Console.WriteLine($"{hit} of {seen} models have parts on weapon bones");
                return 0;
            }
            case "--rig-graft-test":
            {
                // --rig-graft-test <target package> (read only): every borrowed rig kind and donor grafted onto the target's
                // skeleton: bones added and the motion library's size, or why not
                if (rest.Count < 2) { Console.WriteLine("--rig-graft-test <target package>"); return 1; }
                string target = MhoExtendedModManager.Model.BasePackage.Resolve(rest[1], true);
                var baseSk = MhoExtendedModManager.Model.MhoSkeleton.Load(target, null);
                foreach (var kind in Enum.GetValues<MhoExtendedModManager.Model.BorrowedRig.Kind>())
                    for (int n = 1; n <= MhoExtendedModManager.Model.BorrowedRig.Donors(kind).Length; n++)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var (sk, rig) = MhoExtendedModManager.Model.BorrowedRig.Graft(baseSk, kind, n, out string note);
                            Console.WriteLine($"{kind} {n} ({Path.GetFileNameWithoutExtension(MhoExtendedModManager.Model.BorrowedRig.Donors(kind)[n - 1])[17..^3]}): {(rig != null ? "OK" : "NOT USED")} {note} ({sw.ElapsedMilliseconds} ms)");
                        }
                        catch (Exception ex) { Console.WriteLine($"{kind} {n}: ERROR {ex.GetType().Name}: {ex.Message}"); }
                    }
                return 0;
            }
            case "--rig-census":
            {
                // --rig-census <CookedPCConsole> (read only): every hero base package's extra bone chains by kind (cape, hair, coat,
                // skirt, chain, whip, wing, tail …): bones, how many of its animations key them (hand-animated: borrowable like
                // the cape and hair), and how many the physics asset simulates instead
                if (rest.Count < 2) { Console.WriteLine("--rig-census <CookedPCConsole> [file pattern, default UC__MarvelPlayer_*_SF.upk] [kind]"); return 1; }
                string pattern = rest.Count > 2 ? rest[2] : "UC__MarvelPlayer_*_SF.upk";
                string? onlyKind = rest.Count > 3 ? rest[3] : null;
                var kinds = new (string Kind, System.Text.RegularExpressions.Regex Rx)[]
                {
                    ("cape", new(@"cape|cloak", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("hair", new(@"hair|bang|ponytail|braid", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("coat", new(@"coat|jacket|trench", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("skirt", new(@"skirt|dress|loin|robe|apron|tabard|kilt|cloth|sash", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("chain", new(@"chain", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("whip", new(@"whip|tentacle|tendril|lash|rope", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("wing", new(@"wing", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                    ("tail", new(@"tail", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
                };
                foreach (string f in Directory.EnumerateFiles(rest[1], pattern).Order())
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (stem.Contains("bak", StringComparison.OrdinalIgnoreCase) || stem.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rest.Count <= 2 && stem.Split('_').Length != 5) continue;   // the default: UC__MarvelPlayer_<Hero>_SF only
                    try
                    {
                        var sk = MhoExtendedModManager.Model.MhoSkeleton.Load(f, null);
                        var names = sk.Bones.Select(b => b.Name).ToList();
                        var found = kinds.Where(k => onlyKind == null || k.Kind.Equals(onlyKind, StringComparison.OrdinalIgnoreCase))
                            .Select(k => (k.Kind, Bones: names.Where(n => k.Rx.IsMatch(n) && !n.Contains("twist", StringComparison.OrdinalIgnoreCase)).ToList())).Where(x => x.Bones.Count > 0).ToList();
                        if (found.Count == 0) continue;
                        var anims = MhoExtendedModManager.Model.MhoAnim.For(f, names);
                        var loaded = anims.Select(MhoExtendedModManager.Model.MhoAnim.Load).Where(x => x != null).ToList();
                        foreach (var (kind, bones) in found)
                        {
                            int keyed = loaded.Count(an => bones.Any(b => an!.Tracks.TryGetValue(b, out var t) && t.RotationKeys.Count > 1));
                            int sim = bones.Count(b => sk.Simulated.Contains(b));
                            Console.WriteLine($"{stem}\t{kind}\t{bones.Count} bones\t{keyed}/{loaded.Count} anims keyed\t{sim} simulated\t{string.Join(" ", bones.Take(6))}");
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
                }
                return 0;
            }
            case "--mff-part-prop":
            {
                // --mff-part-prop <model> <part> <package.upk> <out dir> (reads the model and package; writes only <out dir>):
                // the part as the Parts list's Add as a Prop makes it: the FBX, the bone it's held on, and where it lies on that bone
                if (rest.Count < 5) { Console.WriteLine("--mff-part-prop <model> <part> <package.upk> <out dir>"); return 1; }
                MhoExtendedModManager.Model.Protected.CheckWrite(Path.Combine(rest[4], "x"));
                var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1]));
                var part = mm.Parts.FirstOrDefault(x => x.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (part == null) { Console.WriteLine("no such part; the parts: " + string.Join(", ", mm.Parts.Select(x => x.Name))); return 1; }
                // the part's own bones in MFF's rest pose: where each weight bone and its parents are, and the part's middle
                var pw = new Dictionary<int, float>();
                foreach (var sec in part.Sections) foreach (var vw in sec.Weights) foreach (var x in vw) pw[x.Bone] = pw.GetValueOrDefault(x.Bone) + x.Weight;
                var mid = part.Sections.SelectMany(x => x.Pos).Aggregate(System.Numerics.Vector3.Zero, (a2, v) => a2 + v) / part.Verts;
                Console.WriteLine($"part middle (MFF rest, cm): {mid.X:0.#} {mid.Y:0.#} {mid.Z:0.#}");
                foreach (var bi in pw.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key))
                    for (int c = bi, g = 0; c >= 0 && g < 4; c = mm.Bones[c].Parent, g++)
                    {
                        var bp = mm.Bones[c].Position;
                        Console.WriteLine($"  {mm.Bones[c].Name}: {bp.X:0.#} {bp.Y:0.#} {bp.Z:0.#} ({(bp - mid).Length():0.#} from the part's middle)");
                    }
                var body = MhoExtendedModManager.Model.MhoSkeleton.Load(rest[3], null, body: true);
                var res = MhoExtendedModManager.Model.MffPropExport.Write(mm, part, body, null, rest[4], Console.WriteLine);
                var raw = MhoExtendedModManager.Model.FbxReimport.Raw(res.Fbx);
                Console.WriteLine($"{res.Fbx}: {res.Vertices:N0} vertices on {res.Bone}; in the bone's frame (file axes) {raw.Min.X:0.#} {raw.Min.Y:0.#} {raw.Min.Z:0.#} .. {raw.Max.X:0.#} {raw.Max.Y:0.#} {raw.Max.Z:0.#} (size {(raw.Max - raw.Min).Length():0.#})");
                return 0;
            }
            case "--mff-parts":
            {
                // --mff-parts <model file or MFF folder name> (read only): each part as the importer sorts it (kind, ticked by
                // default), its size and place (normalized cm) and the bones carrying its weight, with each bone's parent chain
                if (rest.Count < 2) { Console.WriteLine("--mff-parts <model>"); return 1; }
                var mm = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1]));
                string Chain(int b) { var l = new List<string>(); for (int g = 0; b >= 0 && g < 6; g++) { l.Add(mm.Bones[b].Name); b = mm.Bones[b].Parent; } return string.Join(" < ", l); }
                foreach (var pt in mm.Parts)
                {
                    var pos = pt.Sections.SelectMany(x => x.Pos).ToList();
                    var lo = pos.Aggregate(System.Numerics.Vector3.Min); var hi = pos.Aggregate(System.Numerics.Vector3.Max);
                    var w = new Dictionary<int, float>(); float tot = 0;
                    foreach (var sec in pt.Sections) foreach (var vw in sec.Weights) foreach (var x in vw) { w[x.Bone] = w.GetValueOrDefault(x.Bone) + x.Weight; tot += x.Weight; }
                    Console.WriteLine($"{pt.Name}: {pt.Verts} verts, {(pt.IsProp ? "prop" : pt.IsAlternate ? "swap" : !pt.Weighted ? "unrigged" : "body")}, {(pt.DefaultOn ? "ticked" : "off")}, own material {pt.OwnMaterial}; box {lo.X:0} {lo.Y:0} {lo.Z:0} .. {hi.X:0} {hi.Y:0} {hi.Z:0}; materials {string.Join(", ", pt.Sections.Select(x => x.Material).Distinct())}");
                    foreach (var x in pt.Sections.DistinctBy(x => x.Material)) Console.WriteLine($"    texture {x.Material}: {x.Tex.Diffuse ?? "(none found)"}{(x.Tex.Guessed ? " (guessed)" : "")}");
                    foreach (var (b, v) in w.OrderByDescending(kv => kv.Value).Take(5)) Console.WriteLine($"    {v / tot:P0} {Chain(b)} (rig of {mm.RigSize(b)} bones)");
                    // vertex islands far from their bones (a floating piece): connected pieces, each with its dominant bone and gap
                    foreach (var sec in pt.Sections)
                    {
                        int n = sec.Pos.Length; var par = Enumerable.Range(0, n).ToArray();
                        int Find(int x) { while (par[x] != x) x = par[x] = par[par[x]]; return x; }
                        for (int t = 0; t + 2 < sec.Tris.Length; t += 3) { par[Find(sec.Tris[t])] = Find(sec.Tris[t + 1]); par[Find(sec.Tris[t + 1])] = Find(sec.Tris[t + 2]); }
                        foreach (var g in Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() >= 4))
                        {
                            var c = g.Select(v => sec.Pos[v]).Aggregate((p, q) => p + q) / g.Count();
                            var dom = g.SelectMany(v => sec.Weights[v]).GroupBy(x => x.Bone).OrderByDescending(x => x.Sum(y => y.Weight)).FirstOrDefault()?.Key ?? -1;
                            float gap = dom >= 0 ? g.Min(v => System.Numerics.Vector3.Distance(sec.Pos[v], mm.Bones[dom].Position)) : -1;
                            if (gap > 15) Console.WriteLine($"    island {g.Count()} verts at {c.X:0} {c.Y:0} {c.Z:0} on {(dom >= 0 ? Chain(dom) : "nothing")}, {gap:0} cm from that bone");
                        }
                    }
                }
                return 0;
            }
            case "--mff-layout":
                // read-only: where an MFF rip folder keeps its model folders and textures (Models\Models or Models, Texture2D …)
                foreach (string f in rest.Skip(1)) { var (m, t) = MhoExtendedModManager.Model.Source.Layout(f); Console.WriteLine($"{f}\n  models:   {m}\n  textures: {t}"); }
                return 0;
            case "--image-editors":
                // read-only: the image editors found installed, and the one the Materials tab uses
                foreach (var ed in MhoExtendedModManager.Model.ImageEditor.Installed()) Console.WriteLine($"{MhoExtendedModManager.Model.ImageEditor.Describe(ed)}: {ed}");
                Console.WriteLine("uses: " + (MhoExtendedModManager.Model.ImageEditor.Find() ?? "none"));
                return 0;
            case "--model-convert":
            {
                // --model-convert <model file>... (read-only on the files): each one as Single Model reads it: a .blend / XPS
                // turned into an FBX by Blender (data\model\converted), then what the Model tab sees in it
                foreach (string f in rest.Skip(1))
                {
                    Console.WriteLine($"== {f}");
                    try
                    {
                        string file = MhoExtendedModManager.Model.ModelConvert.NeedsBlender(f) ? MhoExtendedModManager.Model.ModelConvert.ToFbx(f, Console.WriteLine) : f;
                        if (file != f) Console.WriteLine($"  FBX: {file} ({new FileInfo(file).Length:N0} bytes; beside it: {string.Join(", ", Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName).Where(n => n != Path.GetFileName(file)).Take(8))})");
                        string? why;
                        string? family = MhoExtendedModManager.Model.SkeletonProfile.DetectFile(file, out why);
                        Console.WriteLine($"  MFF (Bip001): {MhoExtendedModManager.Model.SkeletonProfile.IsMff(file)}; skeleton family: {family ?? "none"}{(why != null ? " (" + why + ")" : "")}; armature: {MhoExtendedModManager.Model.AutoRig.HasArmature(file)}; MHO bones: {MhoExtendedModManager.Model.AutoRig.MhoBoneCount(file)}");
                        foreach (var (name, verts) in MhoExtendedModManager.Model.FbxReimport.Meshes(file).Take(12)) Console.WriteLine($"  mesh {name}: {verts:N0} vertices");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or Assimp.AssimpException) { Console.WriteLine("  ERROR: " + ex.Message); }
                }
                return 0;
            }
            case "--model-build":
            {
                // --model-build <mff model> <package name or file> <out folder> [--map bonemap.json]: the importer's --encode-mff
                // (MFF_HAIR, MFF_CAPE, MFF_ANIM_FBX, MFF_MODEL_FBX … as there; MHO_MFF_SOURCE = the MFF folder, else Settings)
                if (rest.Count < 4) { Console.WriteLine("--model-build <mff model> <package name or file> <out folder> [--map bonemap.json]"); return 1; }
                int mi = rest.IndexOf("--map");
                string? map = mi > 0 && mi + 1 < rest.Count ? rest[mi + 1] : null;
                MhoExtendedModManager.Model.Settings.Reset();
                try
                {
                    string package = MhoExtendedModManager.Model.InheritedMesh.Start(MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true), Console.WriteLine);   // (a costume showing its hero's model: a copy with it in)
                    // MHO_MODEL_MESH=<name>: the character in the package to build onto (the Model tab's Model ▾)
                    if (Environment.GetEnvironmentVariable("MHO_MODEL_MESH") is { Length: > 0 } pickMesh) MhoExtendedModManager.Model.MhoSkeleton.Choose(package, pickMesh);
                    var result = MhoExtendedModManager.Model.ImportBuild.Run(rest[1], package, rest[3], MhoExtendedModManager.Model.ImportOptions.FromEnvironment(null, map, null), Console.WriteLine);
                    return result == null ? 1 : 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            case "--mesh-aliases":
            {
                // Read-only census: every character package (players, team-ups; --all: every UC__ package with a character class)
                // whose class lists MeshAliases, with each alias's mesh, and the ones Build into Mod also builds onto.
                // --mesh-aliases [--all]
                var ast = Settings.Load(); string? agr = ast.ResolvedGameRoot(Settings.LibraryData(ast.LibraryPath));
                if (agr == null || !Settings.IsGameRoot(agr)) { Console.WriteLine("game folder not found"); return 1; }
                string acook = UniqueNames.Folder(Settings.Cooked(agr));
                var pkgs = Directory.GetFiles(acook, "UC__*_SF.upk").Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                    .Where(f => rest.Contains("--all") || Path.GetFileName(f).StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase).ToList();
                int with = 0;
                foreach (string f in pkgs)
                {
                    List<(string Alias, string Mesh, bool Here)> al;
                    try { al = Model.MhoSkeleton.RawAliases(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or ArgumentException or IndexOutOfRangeException) { continue; }
                    if (al.Count == 0) continue;
                    with++;
                    string? main = Model.MhoSkeleton.ComponentMesh(f);
                    var builds = main == null ? [] : Model.MhoSkeleton.Aliases(f, main);
                    Console.WriteLine($"{Path.GetFileName(f)} (model {main ?? "?"}): " + string.Join("; ", al.Select(a => $"{a.Alias} = {a.Mesh}{(a.Here ? "" : " [import]")}{(builds.Contains(a.Mesh.Split('.')[^1], StringComparer.OrdinalIgnoreCase) ? " [built too]" : "")}")));
                }
                Console.WriteLine($"{with} of {pkgs.Count} package(s) list mesh aliases");
                return 0;
            }
            case "--mesh-sizes":
            {
                // Read-only: every skeletal mesh in a package with its measured height and its head and pelvis bone heights
                // (Ant-Man's _small model beside the normal one). --mesh-sizes <package.upk>
                if (rest.Count < 2) { Console.WriteLine("--mesh-sizes <package.upk>"); return 1; }
                foreach (var (name, _) in Model.MhoSkeleton.List(rest[1]))
                    try
                    {
                        var sk = Model.MhoSkeleton.Load(rest[1], name);
                        int h = sk.Find("g_head"), pv = sk.Find("g_pelvis");
                        Console.WriteLine($"{name}: height {sk.Height:0.##}, up {sk.Up}, head {(h >= 0 ? sk.Pos(h).ToString() : "-")}, pelvis {(pv >= 0 ? sk.Pos(pv).ToString() : "-")}, {sk.Bones.Count} bones");
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { Console.WriteLine($"{name}: {ex.Message}"); }
                return 0;
            }
            case "--model-tab-test":
            {
                // --model-tab-test <mod> <mff model> <package file> (scratch MHO_EXTMM_HOME only: it saves the mod): the editor's
                // Model tab driven through its own controls; exit 0 when every check passes.
                if (rest.Count < 4) { Console.WriteLine("--model-tab-test <mod> <mff model> <package file>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library): this test saves the mod"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelTabTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-power-test":
            {
                // --model-power-test <mod> <power name part> <out.png> (scratch MHO_EXTMM_HOME): the Model tab's preview, a power clicked
                if (rest.Count < 4 || Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("--model-power-test <mod> <power> <out.png> (scratch MHO_EXTMM_HOME)"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    main.WindowState = FormWindowState.Normal; main.Bounds = new Rectangle(-6000, -6000, 1700, 1000);   // off screen, full size
                    try { code = await main.ModelPowerTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                return code;
            }
            case "--model-tab-timing":
            {
                if (rest.Count < 2 || Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("--model-tab-timing <mod> (scratch MHO_EXTMM_HOME)"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();   // as the app itself
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () => { try { code = await main.ModelTabTiming(rest[1], Console.WriteLine); } catch (Exception ex) { Console.WriteLine("ERROR: " + ex); } main.Close(); });
                Application.Run(main);
                return code;
            }
            case "--model-settings-test":
            {
                // --model-settings-test (scratch MHO_EXTMM_HOME): Settings → Model changes the main window's settings object, so
                // the window saving its copy on exit keeps them (Kurt: the MFF folder was forgotten after a restart).
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                var before = Settings.Load();
                string? keep = before.MffFolder, keepBlender = before.BlenderPath;
                using var main = new Gui.MainForm();   // not shown: it only has to hold its settings
                string probe = @"C:\MffFolderTest_" + Environment.TickCount64;
                MhoExtendedModManager.Model.Settings.Change(s => s.MffFolder = probe);
                MhoExtendedModManager.Model.Settings.Reset();
                MhoExtendedModManager.Model.Settings.Current.BlenderPath = @"C:\BlenderTest\blender.exe"; MhoExtendedModManager.Model.Settings.Current.Save();
                MhoExtendedModManager.Model.Settings.App!.Save();   // the window's copy saved, as on exit
                var after = Settings.Load();
                bool ok1 = after.MffFolder == probe, ok2 = after.BlenderPath == @"C:\BlenderTest\blender.exe";
                Console.WriteLine((ok1 ? "PASS" : "FAIL") + " the MFF folder survives the window's save on exit: " + after.MffFolder);
                Console.WriteLine((ok2 ? "PASS" : "FAIL") + " the Blender choice survives it too: " + after.BlenderPath);
                // the old way (0.37.113–0.37.115): the file written on its own, then the window's copy saved on exit
                var window = MhoExtendedModManager.Model.Settings.App!;
                var fresh = Settings.Load(); fresh.MffFolder = probe + "_old"; fresh.Save();
                window.Save();
                bool lost = Settings.Load().MffFolder != probe + "_old";
                Console.WriteLine((lost ? "PASS" : "FAIL") + " (the old way loses it, as Kurt saw: " + Settings.Load().MffFolder + ")");
                MhoExtendedModManager.Model.Settings.Change(s => { s.MffFolder = keep; s.BlenderPath = keepBlender; });   // back as it was
                return ok1 && ok2 && lost ? 0 : 1;
            }
            case "--effect-census":
            {
                // --effect-census <out.txt>: read only; every hero's effect models (static meshes in power, hotspot, projectile and
                // condition packages) with every package holding a copy (Model\StockData\effect_models.txt)
                if (rest.Count < 2) { Console.WriteLine("--effect-census <out.txt>"); return 1; }
                if (MhoExtendedModManager.Model.Settings.Current.CookedFolder is not string ecook) { Console.WriteLine("game folder not found"); return 1; }
                return MhoExtendedModManager.Model.EffectModels.Census(ecook, rest[1], Console.WriteLine);
            }
            case "--effect-check":
            {
                // --effect-check <scratch folder> [hero ...]: read only on the game; every effect model (of those heroes) exported
                // as the view does and built straight back into its first package: what can't be (and why), counted by reason.
                if (rest.Count < 2) { Console.WriteLine("--effect-check <scratch folder> [hero ...]"); return 1; }
                if (MhoExtendedModManager.Model.Settings.Current.CookedFolder is not string kcook) { Console.WriteLine("game folder not found"); return 1; }
                var want = rest.Skip(2).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var reasons = new Dictionary<string, int>();
                int ok = 0, n = 0;
                foreach (var e in MhoExtendedModManager.Model.EffectModels.All.Where(x => want.Count == 0 || want.Contains(x.Hero)).GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                {
                    n++;
                    string dir = Path.Combine(rest[1], $"{n:0000}");
                    try
                    {
                        string? pkg = MhoExtendedModManager.Model.EffectModels.StockPackage(e, kcook) ?? throw new InvalidDataException("no stock package");
                        string fbx = MhoExtendedModManager.Model.EffectModels.Export(pkg, e.Path, dir);
                        MhoExtendedModManager.Model.EffectModels.Build(pkg, e.Path, fbx, out _, out _);
                        ok++;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or Assimp.AssimpException or IndexOutOfRangeException or ArgumentException)
                    {
                        string why = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\d+", "N");
                        reasons[why] = reasons.GetValueOrDefault(why) + 1;
                        if (reasons[why] <= 2) Console.WriteLine($"  {e.Hero} {e.Path}: {ex.Message}");
                    }
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
                Console.WriteLine($"{ok} of {n} effect models build back");
                foreach (var (why, count) in reasons.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {count} x {why}");
                return 0;
            }
            case "--effect-roundtrip":
            {
                // --effect-roundtrip <mesh path> <scratch folder>: read only on the game; an effect model exported as the Effect
                // Models view does (model.fbx), opened in a headless Blender and saved unchanged (Ctrl+S's sync), then built back
                // into every package holding it; prints how far each vertex moved and the bounds against the stock mesh.
                if (rest.Count < 3) { Console.WriteLine("--effect-roundtrip <mesh path> <scratch folder>"); return 1; }
                if (MhoExtendedModManager.Model.Settings.Current.CookedFolder is not string rcook) { Console.WriteLine("game folder not found"); return 1; }
                var entry = MhoExtendedModManager.Model.EffectModels.All.FirstOrDefault(e => e.Path.Equals(rest[1], StringComparison.OrdinalIgnoreCase));
                if (entry == null) { Console.WriteLine("not in the effect model list: " + rest[1]); return 1; }
                string dir = rest[2];
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string? stockPkg = MhoExtendedModManager.Model.EffectModels.StockPackage(entry, rcook);
                if (stockPkg == null) { Console.WriteLine("no stock package"); return 1; }
                string fbx0 = MhoExtendedModManager.Model.EffectModels.Export(stockPkg, entry.Path, dir);
                Console.WriteLine($"exported {entry.Path} from {Path.GetFileName(stockPkg)} → {fbx0}");
                if (MhoExtendedModManager.Model.BlenderLaunch.OpenStatic(dir, background: true) is string why) { Console.WriteLine("Blender: " + why); return 1; }
                string blend = Path.Combine(dir, "model.blend");
                Console.WriteLine("scene: " + (File.Exists(blend) ? "model.blend saved" : "NO model.blend"));
                if (!File.Exists(blend)) return 1;
                // Ctrl+S, as a reopened scene does it (the reopen script turns the sync on, no auto-run)
                MhoExtendedModManager.Model.BlenderLaunch.Reopen(dir, scriptOnly: true);
                string save = Path.Combine(dir, "save.py");
                File.WriteAllText(save, "import bpy\nbpy.ops.wm.save_mainfile()\n");
                var psi = new System.Diagnostics.ProcessStartInfo(MhoExtendedModManager.Model.BlenderLaunch.Find()!) { UseShellExecute = false, WorkingDirectory = dir };
                foreach (var a in new[] { "-b", blend, "--python", Path.Combine(dir, "reopen_in_blender.py"), "--python", save }) psi.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(psi)!.WaitForExit(300_000);
                string sent = Path.Combine(dir, "from_blender", "model.fbx");
                Console.WriteLine("Ctrl+S sent: " + (File.Exists(sent) ? sent : "NOTHING"));
                if (!File.Exists(sent)) return 1;
                int bad = 0;
                foreach (string file in entry.Packages)
                {
                    string src = StockFiles.For(rcook, file);
                    var sp = MhoPackageModifier.Package.Open(src);
                    var stock = MhoPackageModifier.StaticMesh.Read(sp, sp.Exports[MhoExtendedModManager.Model.EffectModels.Find(sp, entry.Path)]);
                    try
                    {
                        byte[] bytes = MhoExtendedModManager.Model.EffectModels.Build(src, entry.Path, sent, out var built, out var notes);
                        float worst = 0;
                        foreach (var v in built.Positions) worst = Math.Max(worst, stock.Positions.Min(q => System.Numerics.Vector3.Distance(q, v)));
                        Console.WriteLine($"  {file}: {stock.Positions.Length} → {built.Positions.Length} vertices, worst vertex {worst:0.0000} from a stock one, bounds {built.BoundsOrigin} ± {built.BoundsExtent} (stock {stock.BoundsOrigin} ± {stock.BoundsExtent}); package {bytes.Length:N0} bytes, verified{(notes.Count > 0 ? "; " + string.Join("; ", notes) : "")}");
                        if (worst > 0.05f) bad++;
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or MhoPackageModifier.PackageFormatException) { Console.WriteLine($"  {file}: {ex.Message}"); bad++; }
                }
                Console.WriteLine(bad == 0 ? "round trip PASS" : $"round trip FAIL ({bad})");
                return bad == 0 ? 0 : 1;
            }
            case "--model-effects-test":
            {
                // --model-effects-test <mod> <hero> <mesh path> <fbx> (scratch MHO_EXTMM_HOME; the mod isn't saved): the Effect
                // Models view through its own code (ModelPage.TestEffects); exit 0 when every check passes.
                if (rest.Count < 5) { Console.WriteLine("--model-effects-test <mod> <hero> <mesh path> <fbx>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelEffectsTest(rest[1], rest[2], rest[3], rest[4], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-props-test":
            {
                // --model-props-test <mod> <costume package> <prop mesh> <other source package> (scratch MHO_EXTMM_HOME + MHO_EXTMM_TEST_DIALOGS
                // and MHO_EXTMM_TEST_CHOICE=0; the mod isn't saved): the Model tab's Props view (ModelPage.TestProps).
                if (rest.Count < 5) { Console.WriteLine("--model-props-test <mod> <costume package> <prop mesh> <other source package>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelPropsTest(rest[1], rest[2], rest[3], rest[4], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-lists-test":
            {
                // --model-lists-test <mod> (scratch MHO_EXTMM_HOME): the Model tab's Source and Target lists as they come up
                if (rest.Count < 2) { Console.WriteLine("--model-lists-test <mod>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelListsTest(rest[1], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "lists filled" : "LISTS EMPTY");
                return code;
            }
            case "--model-material-test":
            {
                // --model-material-test <mod> <target package> <source package> [shot.png] [modded-list.png] [filter] (scratch
                // MHO_EXTMM_HOME + MHO_EXTMM_TEST_DIALOGS; the mod isn't saved): Copy Setup From a Costume on the target's own
                // material, then the Source's Current Modded Game list (ModelPage.TestMaterialCopy; the manual's shots)
                if (rest.Count < 4) { Console.WriteLine("--model-material-test <mod> <target package> <source package> [shot.png] [modded-list.png] [filter]"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelMaterialTest(rest[1], rest[2], rest[3], rest.Count > 4 ? rest[4] : null, rest.Count > 5 ? rest[5] : null, rest.Count > 6 ? rest[6] : "", Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-partner-test":
            {
                // --model-partner-test <mod> <target package> <source package> (scratch MHO_EXTMM_HOME + MHO_EXTMM_TEST_DIALOGS;
                // the mod isn't saved): a build onto a power model offers its summon (ModelPage.TestPartners).
                if (rest.Count < 4) { Console.WriteLine("--model-partner-test <mod> <target package> <source package>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelPartnerTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-variations-test":
            {
                // --model-variations-test <mod> <target package> (scratch MHO_EXTMM_HOME; the mod isn't saved): the Model tab's
                // Variations tab (ModelPage.TestVariations); exit 0 when every check passes.
                if (rest.Count < 3) { Console.WriteLine("--model-variations-test <mod> <target package>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelVariationsTest(rest[1], rest[2], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-summon-test":
            {
                // --model-summon-test <mod> <creature summon package> <person-shaped summon package> (scratch MHO_EXTMM_HOME; the
                // mod isn't saved): the Model tab's summon steps (ModelPage.TestSummons); exit 0 when every check passes.
                if (rest.Count < 4) { Console.WriteLine("--model-summon-test <mod> <creature package> <person package>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library)"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelSummonTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-blender-test":
            {
                // --model-blender-test <mod> <mff model> <package file> <animation> (scratch MHO_EXTMM_HOME only; Blender runs
                // headless): the Model tab's Single Animation export, the scene its Open in Blender script builds, an edit
                // saved in Blender, the sync taken in, Save Changes; exit 0 when every check passes.
                if (rest.Count < 5) { Console.WriteLine("--model-blender-test <mod> <mff model> <package file> <animation>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library): this test saves the mod"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");   // no questions, no Explorer window
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelBlenderTest(rest[1], rest[2], rest[3], rest[4], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-paint-test":
            {
                // --model-paint-test <mod> <mff model> <package file> (scratch MHO_EXTMM_HOME only): weight painting in the Model tab
                if (rest.Count < 4) { Console.WriteLine("--model-paint-test <mod> <mff model> <package file>"); return 1; }
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("needs MHO_EXTMM_HOME (a scratch library): this test writes the mod's paint file"); return 1; }
                Environment.SetEnvironmentVariable("MFF_GUI_NOASK", "1");
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                int code = 1;
                var main = new Gui.MainForm();
                main.Shown += (_, _) => main.BeginInvoke(async () =>
                {
                    try { code = await main.ModelPaintTest(rest[1], rest[2], rest[3], Console.WriteLine); }
                    catch (Exception ex) { Console.WriteLine("ERROR: " + ex); }
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(code == 0 ? "all checks passed" : "FAILED");
                return code;
            }
            case "--model-housekeeping-test":
            {
                // --model-housekeeping-test <scratch folder> [snapshot.png]: the startup sweep deletes only leftover work folders;
                // Save keeps only the rigs of packages the tab built, and only their rig files; the clean-up window renders
                if (rest.Count < 2) { Console.WriteLine("--model-housekeeping-test <scratch folder> [snapshot.png]"); return 1; }
                string root = Path.GetFullPath(rest[1]);
                if (Directory.Exists(root)) Directory.Delete(root, true);
                int fails = 0;
                void Check(bool c, string what) { Console.WriteLine((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
                string lib = Path.Combine(root, "library");
                foreach (var d in new[] { "model-work-1234abcd", "model-work-notahex1", "mods" }) { Directory.CreateDirectory(Path.Combine(lib, d)); File.WriteAllText(Path.Combine(lib, d, "x.txt"), "x"); }
                int swept = ModelWork.SweepOrphans(lib);
                Check(swept == 1 && !Directory.Exists(Path.Combine(lib, "model-work-1234abcd")), "a leftover work folder (model-work-<8 hex>) is deleted");
                Check(Directory.Exists(Path.Combine(lib, "model-work-notahex1")) && Directory.Exists(Path.Combine(lib, "mods")), "nothing else in the library is touched");
                string work = Path.Combine(root, "work"), mod = Path.Combine(root, "mod", "Model");
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(work).FullName, "state.json"), "{ \"Built\": [ \"UC__MarvelPlayer_Beast_SF.upk\" ] }");
                string built = Path.Combine(work, "rigs", "Carter on UC__MarvelPlayer_Beast_SF"), other = Path.Combine(work, "rigs", "Carter on UC__MarvelTeamUp_Agent13_SF");
                foreach (var f in new[] { "rig.blend", "rig.blend1", "rigged.fbx", "rig.py", "save_hook.py", "tex.png" }) { Directory.CreateDirectory(built); File.WriteAllText(Path.Combine(built, f), f); }
                Directory.CreateDirectory(other); File.WriteAllText(Path.Combine(other, "rig.blend"), "x");
                ModelWork.CopyInto(work, mod);
                var kept = Directory.Exists(Path.Combine(mod, "rigs")) ? Directory.GetFiles(Path.Combine(mod, "rigs"), "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(Path.Combine(mod, "rigs"), f)).OrderBy(x => x).ToList() : [];
                Console.WriteLine("  kept: " + string.Join(", ", kept));
                Check(kept.SequenceEqual(new[] { "rig.blend", "rigged.fbx", "tex.png" }.Select(f => Path.Combine("Carter on UC__MarvelPlayer_Beast_SF", f))),
                    "Save keeps only the built hero's rig, and only rig.blend, rigged.fbx and its textures");
                Check(File.Exists(Path.Combine(mod, "state.json")), "the rest of the Model work is kept as before");
                if (rest.Count > 2)
                {
                    using var form = new Gui.ModelCleanUpForm();
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-4000, -4000);
                    form.Show(); Application.DoEvents();
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(rest[2]);
                    Console.WriteLine("  snapshot: " + rest[2]);
                }
                return fails == 0 ? 0 : 1;
            }
            case "--model-rig":
            {
                // --model-rig <fbx without an armature> <package name or file> <out folder>: AutoRig (fit, Blender's Automatic
                // Weights, headless) into the rig folder under out, then a build from the rigged FBX into out (its check render
                // shows the pose)
                //
                if (rest.Count < 4) { Console.WriteLine("--model-rig <fbx> <package name or file> <out folder>"); return 1; }
                MhoExtendedModManager.Model.Settings.Reset();
                try
                {
                    string package = MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true);
                    var sk = MhoExtendedModManager.Model.MhoSkeleton.Load(package, null);
                    string rigged = MhoExtendedModManager.Model.AutoRig.Rig(rest[1], sk, package, Path.Combine(rest[3], "rig"), Console.WriteLine);
                    Console.WriteLine("rigged: " + rigged);
                    var o = MhoExtendedModManager.Model.ImportOptions.FromEnvironment(null, null, null) with { SourceFbx = rigged };
                    return MhoExtendedModManager.Model.ImportBuild.Run(rigged, package, rest[3], o, Console.WriteLine) == null ? 1 : 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            case "--spec-channels-test":
            {
                // --spec-channels-test <rgba spec.png> <scratch folder> [window.png]: split into gray channels and combine back
                // (identical), then an edited (newer) channel file is what's found; optionally the From Channels window rendered
                if (rest.Count < 3) { Console.WriteLine("--spec-channels-test <rgba spec.png> <scratch folder> [window.png]"); return 1; }
                string dir = Path.GetFullPath(rest[2]);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                int fails = 0;
                void Check(bool c, string what) { Console.WriteLine((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
                string src = Path.Combine(dir, "m_mhospec.png");
                File.Copy(rest[1], src);
                MhoExtendedModManager.Model.SpecChannels.Split(src, dir, "m_mhospec");
                Check(MhoExtendedModManager.Model.SpecChannels.Suffix.All(x => File.Exists(Path.Combine(dir, "m_mhospec" + x + ".png"))) && File.Exists(Path.Combine(dir, "MHO spec maps - read me.txt")), "split: four gray channel files and the read-me");
                string back = MhoExtendedModManager.Model.SpecChannels.Combine(MhoExtendedModManager.Model.SpecChannels.Suffix.Select(x => (string?)Path.Combine(dir, "m_mhospec" + x + ".png")).ToList(), null, null, Path.Combine(dir, "back.png"));
                bool same;
                using (var a0 = new Bitmap(src)) using (var b0 = new Bitmap(back))
                {
                    same = a0.Width == b0.Width && a0.Height == b0.Height;
                    for (int y = 0; same && y < a0.Height; y += 3) for (int x = 0; same && x < a0.Width; x += 3) same = a0.GetPixel(x, y).ToArgb() == b0.GetPixel(x, y).ToArgb();
                }
                Check(same, "combined back: identical to the original (R, G, B and A)");
                File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(-5));
                Check(MhoExtendedModManager.Model.SpecChannels.Find(sfx => File.Exists(Path.Combine(dir, "m_mhospec" + sfx + ".png")) ? Path.Combine(dir, "m_mhospec" + sfx + ".png") : null) != src, "channel files newer than the combined map: they are what's used");
                File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(5));
                Check(MhoExtendedModManager.Model.SpecChannels.Find(sfx => File.Exists(Path.Combine(dir, "m_mhospec" + sfx + ".png")) ? Path.Combine(dir, "m_mhospec" + sfx + ".png") : null) == src, "the combined map newer: it is what's used");
                // layouts: a pixel R 10, G 20, B 30, A 40 read in each layout lands in Angela's channels (or the defaults)
                string px = Path.Combine(dir, "px.png");
                using (var pb = new Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) pb.SetPixel(x, y, Color.FromArgb(40, 10, 20, 30));
                    pb.Save(px, System.Drawing.Imaging.ImageFormat.Png);
                }
                byte[] given = [10, 20, 30, 40];
                foreach (var l in MhoExtendedModManager.Model.SpecLayouts.All)
                {
                    string conv = MhoExtendedModManager.Model.SpecLayouts.ToAngela(px, l);
                    using var cb = new Bitmap(conv);
                    using var cc = cb.Clone(new Rectangle(0, 0, 2, 2), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var d = cc.LockBits(new Rectangle(0, 0, 2, 2), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var raw = new byte[4]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, raw, 0, 4); cc.UnlockBits(d);
                    byte[] got = [raw[2], raw[1], raw[0], raw[3]];
                    byte[] want = [.. Enumerable.Range(0, 4).Select(c => l.FromChannel[c] >= 0 ? given[l.FromChannel[c]] : MhoExtendedModManager.Model.SpecChannels.Default[c])];
                    Check(got.SequenceEqual(want), $"layout {l.Id}: R G B A {string.Join(" ", got)} (want {string.Join(" ", want)}) · {MhoExtendedModManager.Model.SpecLayouts.Change(l)}");
                }
                // glow: the layout's glow channel × the color map (R 200 G 100 B 50) becomes the glow map
                string colPx = Path.Combine(dir, "col.png");
                using (var cbmp = new Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) cbmp.SetPixel(x, y, Color.FromArgb(255, 200, 100, 50));
                    cbmp.Save(colPx, System.Drawing.Imaging.ImageFormat.Png);
                }
                foreach (var l in MhoExtendedModManager.Model.SpecLayouts.All)
                {
                    string? gm = MhoExtendedModManager.Model.SpecLayouts.GlowMap(px, l, colPx);
                    if (l.GlowChannel < 0) { Check(gm == null, $"layout {l.Id}: no glow channel, no glow map"); continue; }
                    using var gb = new Bitmap(gm!);
                    var c0 = gb.GetPixel(0, 0);
                    int mask = given[l.GlowChannel];
                    bool okG = Math.Abs(c0.R - 200 * mask / 255) <= 1 && Math.Abs(c0.G - 100 * mask / 255) <= 1 && Math.Abs(c0.B - 50 * mask / 255) <= 1;
                    Check(okG, $"layout {l.Id}: glow map from {"RGBA"[l.GlowChannel]} ({mask}) × the color = {c0.R} {c0.G} {c0.B}");
                }
                Check(MhoExtendedModManager.Model.SpecLayouts.FromName(@"x\hero_specmultrimmaskreflection.png")?.Id == "v1" && MhoExtendedModManager.Model.SpecLayouts.FromName(@"x\m_specmult_specpow_reflectivity_emissive.png")?.Id == "v2emissive"
                    && MhoExtendedModManager.Model.SpecLayouts.FromName(@"x\m_emissivespecpowambient.png")?.Id == "v1ambient" && MhoExtendedModManager.Model.SpecLayouts.FromName(@"x\m_mhospec.png") == null, "layout from the file name");
                // DXT5 with refine fits colors only where alpha isn't 0: wrong for a packed spec map, whose alpha is reflectivity
                foreach (bool refine in new[] { true, false })
                {
                    var enc = MhoPackageModifier.TextureEncode.FromImage(src, "dxt5", 85, 1f, true, 0, refine);
                    var top = enc.Levels[0];
                    var dec = MhoPackageModifier.TextureDecode.ToBgra("PF_DXT5", top.W, top.H, top.Data, out _)!;
                    using var o = new Bitmap(src);
                    double err0 = 0, errAll = 0; int n0 = 0, n = 0;
                    for (int y = 0; y < top.H; y += 2)
                        for (int x = 0; x < top.W; x += 2)
                        {
                            var c = o.GetPixel(x, y); int i = (y * top.W + x) * 4;
                            double e = (Math.Abs(dec[i + 2] - c.R) + Math.Abs(dec[i + 1] - c.G) + Math.Abs(dec[i] - c.B)) / 3.0;
                            errAll += e; n++;
                            if (c.A == 0) { err0 += e; n0++; }
                        }
                    Console.WriteLine($"  DXT5 {(refine ? "refine" : "plain ")}: color error {errAll / n:0.0} overall, {err0 / Math.Max(1, n0):0.0} where reflectivity is 0");
                }
                if (rest.Count > 3)
                {
                    Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    using var f = new MhoExtendedModManager.Model.Gui.SpecChannelsForm("material1", src);
                    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                    f.Show(); Application.DoEvents();
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(rest[3]);
                    Console.WriteLine("window: " + rest[3]);
                }
                return fails == 0 ? 0 : 1;
            }
            case "--material-icons-snapshot":
            {
                // --material-icons-snapshot <out.png>: the Materials tab's icon buttons (normal, lit, disabled), rendered off screen
                if (rest.Count < 2) { Console.WriteLine("--material-icons-snapshot <out.png>"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Gui.Ui.UseDarkTheme();
                using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false, BackColor = Color.FromArgb(30, 32, 44) };
                float sc = MhoExtendedModManager.Gui.Ui.Dpi(f.DeviceDpi);
                var painters = new (string Name, Action<Graphics, RectangleF, Pen, Brush> Paint)[]
                {
                    ("Replace File", Gui.Icons.Folder), ("Back to Automatic", Gui.Icons.Reset),
                    ("OpenGL Normals", Gui.Icons.FlipVertical), ("No Glow", Gui.Icons.NoGlow),
                    ("Next Recipe", Gui.Icons.Next), ("Tag Colors", Gui.Icons.Tag),
                    ("From Channels", Gui.Icons.Channels), ("Layout", Gui.Icons.Layout),
                    ("Undo", Gui.Icons.Undo), ("Redo", Gui.Icons.Redo), ("Loop", Gui.Icons.Loop), ("Reset View", Gui.Icons.ResetView),
                    ("Build", Gui.Icons.Build), ("Full Export", Gui.Icons.WithMenu(Gui.Icons.Export)), ("Install", Gui.Icons.Install), ("New", Gui.Icons.Plus),
                    ("Cancel", Gui.Icons.Cancel), ("Save", Gui.Icons.Save), ("Remove", Gui.Icons.Trash), ("Edit", Gui.Icons.Pencil), ("Post", Gui.Icons.Post),
                    ("Help", Gui.Icons.Help), ("Settings", Gui.Icons.WithMenu(Gui.Icons.Gear)), ("Tags", Gui.Icons.WithMenu(Gui.Icons.Tag)), ("Look", Gui.Icons.Look), ("Export", Gui.Icons.Export), ("Updates", Gui.Icons.CheckUpdates), ("Find", Gui.Icons.Search), ("Browse", Gui.Icons.Globe),
                };
                var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, BackColor = f.BackColor };
                for (int row = 0; row < 3; row++)
                {
                    var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = f.BackColor };
                    foreach (var (name, paint) in painters)
                    {
                        var b = Gui.Ui.FlatButton(name, () => { }, "tip");
                        Gui.Icons.Make(b, name, paint, sc);
                        b.Margin = new Padding(0, 0, (int)(6 * sc), 0);
                        if (row == 1) Gui.Ui.Lit(b, true);
                        if (row == 2) b.Enabled = false;
                        line.Controls.Add(b);
                    }
                    flow.Controls.Add(line);
                }
                f.Controls.Add(flow);
                f.ClientSize = new Size((int)(1180 * sc), (int)(120 * sc));
                Gui.Ui.Restyle(f);
                f.Show(); Application.DoEvents();
                using var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(rest[1]);
                using (var tipBmp = Gui.Ui.RenderTipForTest(Gui.Ui.Titled("Tag Colors", "Tell it what each color group of the selected material is made of (Metal, Skin, Leather, Cloth, Glow)."), f.DeviceDpi))
                    tipBmp.Save(Path.ChangeExtension(rest[1], null) + "_tip.png");
                Console.WriteLine("snapshot: " + rest[1]);
                return 0;
            }
            case "--model-perf":
            {
                // --model-perf <mff model | fbx file> <package> [runs] (read only): the preview's preparation stage by stage, a few
                // times (the first run pays for loading), then a build into a temp folder; the performance pass's numbers
                if (rest.Count < 3) { Console.WriteLine("--model-perf <mff model | fbx file> <package> [runs]"); return 1; }
                int runs = rest.Count > 3 ? int.Parse(rest[3]) : 3;
                MhoExtendedModManager.Model.Settings.Reset();
                string pkg = MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true);
                bool fbx = rest[1].EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
                var total = System.Diagnostics.Stopwatch.StartNew();
                MhoExtendedModManager.Model.MffModel? model = null;
                if (!fbx) { model = MhoExtendedModManager.Model.MffModel.Load(MhoExtendedModManager.Model.Source.ResolveModelFile(rest[1])); Console.WriteLine($"MFF model loaded: {total.ElapsedMilliseconds} ms"); }
                for (int r = 1; r <= runs; r++)
                {
                    total.Restart();
                    var prep = fbx ? MhoExtendedModManager.Model.Gui.PreviewPanel.PrepareFbx(rest[1], null, pkg, null) : MhoExtendedModManager.Model.Gui.PreviewPanel.Prepare(model!, null, pkg, null, keepSourceSize: Environment.GetEnvironmentVariable("MFF_KEEPSIZE") == "1");
                    Console.WriteLine($"preview run {r}: {total.ElapsedMilliseconds} ms, feet {prep.Feet:+0.0;-0.0;0}  ({string.Join(", ", MhoExtendedModManager.Model.Gui.PreviewPanel.Timings.Select(t => $"{t.Stage} {t.Ms:0}"))})");
                }
                string outDir = Path.Combine(Path.GetTempPath(), "MHO_ExtMM_perf_build");
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
                total.Restart();
                var opts = MhoExtendedModManager.Model.ImportOptions.FromEnvironment(null, null, null) with { SourceFbx = fbx ? rest[1] : null };
                var lines = new List<(long Ms, string Line)>();
                var built = MhoExtendedModManager.Model.ImportBuild.Run(fbx ? "x" : rest[1], pkg, outDir, opts, l => lines.Add((total.ElapsedMilliseconds, l)));
                Console.WriteLine($"build: {total.ElapsedMilliseconds} ms ({(built == null ? "failed" : "ok")})");
                // where the build's time goes: the log lines with the longest gaps before them
                long prev = 0;
                foreach (var (ms, line) in lines.Select(x => x).ToList().Select(x => { var gap = x.Ms - prev; prev = x.Ms; return (gap, x.Line); }).OrderByDescending(x => x.gap).Take(8))
                    Console.WriteLine($"  {ms,6} ms before: {line[..Math.Min(110, line.Length)]}");
                try { Directory.Delete(outDir, true); } catch (IOException) { }
                return built == null ? 1 : 0;
            }
            case "--default-costumes":
            {
                // --default-costumes (read only): each hero's default costume (avatar StartingCostume) and its package: the
                // Model tab's lists mark those with a bullet
                var settings = MhoExtendedModManager.Settings.Load();
                foreach (var c in (Costume.All(settings.GameRoot!) ?? []).Where(c => c.IsDefault && !c.IsTeamUp && !c.Prototype.Contains("zTesting", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Short, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"{c.Short,-40} {c.Package}");
                return 0;
            }
            case "--game-source":
            {
                // --game-source <game package> [target package] (scratch MHO_EXTMM_HOME): the Model tab's From the Game source:
                // the package's character written as an FBX source, then prepared onto the target as the preview does
                if (rest.Count < 2) { Console.WriteLine("--game-source <game package> [target package]"); return 1; }
                MhoExtendedModManager.Model.Settings.Reset();
                string fbx = MhoExtendedModManager.Model.GameSource.Export(rest[1], Console.WriteLine);
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(fbx)!)) Console.WriteLine($"  {Path.GetFileName(f)}  {new FileInfo(f).Length:N0}");
                foreach (var (mn, mv) in MhoExtendedModManager.Model.FbxReimport.Meshes(fbx)) Console.WriteLine($"  mesh {mn}: {mv:N0} vertices{(MhoExtendedModManager.Model.FbxExport.IsPropPiece(mn) ? " (prop)" : "")}");
                if (rest.Count < 3) return 0;
                string pkg = MhoExtendedModManager.Model.BasePackage.Resolve(rest[2], true);
                var prep = MhoExtendedModManager.Model.Gui.PreviewPanel.PrepareFbx(fbx, null, pkg, null);
                Console.WriteLine($"prepared onto {rest[2]}: {prep.Mesh.Info}, feet {prep.Feet:+0.0;-0.0;0}");
                return 0;
            }
            case "--fbx-thumb":
            {
                // --fbx-thumb <fbx> <out.png> (scratch MHO_EXTMM_HOME): the source list's thumbnail of an FBX (its color map)
                if (rest.Count < 3) { Console.WriteLine("--fbx-thumb <fbx> <out.png>"); return 1; }
                MhoExtendedModManager.Model.Settings.Reset();
                Console.WriteLine("color map: " + (MhoExtendedModManager.Model.FbxReimport.FirstColorMap(rest[1]) ?? "none"));
                Image? img = null;
                for (int i = 0; i < 200 && (img = MhoExtendedModManager.Model.Thumbs.Fbx(rest[1])) == null && MhoExtendedModManager.Model.Thumbs.Pending > 0; i++) Thread.Sleep(50);
                img ??= MhoExtendedModManager.Model.Thumbs.Fbx(rest[1]);
                if (img == null) { Console.WriteLine("no thumbnail"); return 1; }
                img.Save(rest[2]);
                Console.WriteLine($"thumbnail {img.Width}x{img.Height}: {rest[2]}");
                return 0;
            }
            case "--color-tags-split-test":
            {
                // --color-tags-split-test <color.png> <x> <y>: the Tag Colors window (off screen) double-clicked on texel (x, y):
                // the color becomes a group of its own; tagged Glow, the build's glow map lights it (and not its old group)
                if (rest.Count < 4) { Console.WriteLine("--color-tags-split-test <color.png> <x> <y>"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Gui.Ui.UseDarkTheme();
                int px = int.Parse(rest[2]), py = int.Parse(rest[3]), fails = 0;
                void Check(bool c, string what) { Console.WriteLine((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
                using var f = new MhoExtendedModManager.Model.Gui.ColorTagForm("test", rest[1], []);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                f.Show(); Application.DoEvents();
                Color c0;
                using (var img = new Bitmap(rest[1])) c0 = img.GetPixel(px, py);
                var after = f.TestSplitOff(px, py);
                var own = after.FirstOrDefault(g => g.Center.R == c0.R && g.Center.G == c0.G && g.Center.B == c0.B);
                Check(own != null, $"texel ({px}, {py}) {MhoExtendedModManager.Model.ColorTags.Hex(c0)} is a group of its own: {(own != null ? $"{own.Share:P2} of the map" : "missing")}, {after.Count} groups");
                var tags = new List<(string, string)> { (MhoExtendedModManager.Model.ColorTags.Hex(c0), "glow") };
                string? glow = MhoExtendedModManager.Model.ColorTags.GlowFile(rest[1], tags);
                using (var gb = new Bitmap(glow!))
                {
                    int lit = 0;
                    for (int y = 0; y < gb.Height; y++) for (int x = 0; x < gb.Width; x++) { var p = gb.GetPixel(x, y); if (p.R + p.G + p.B > 0) lit++; }
                    var at = gb.GetPixel(px, py);
                    Check(at.R + at.G + at.B > 0 && lit < gb.Width * gb.Height / 50, $"tagged Glow, the build's glow map lights it ({at.R} {at.G} {at.B}) and {lit} texels in all");
                }
                // reach: the group takes in more shades at 2, and the build's glow map follows the saved reach
                int gi = after.FindIndex(g => g.Center.R == c0.R && g.Center.G == c0.G && g.Center.B == c0.B);
                int at1 = f.TestReach(gi, 1f), at2 = f.TestReach(gi, 2f);
                var saved = f.TestTags(gi, "glow");
                string savedTag = saved.First().Item2;
                Check(at2 > at1 && savedTag == "glow|2.00", $"reach 2: {at1} → {at2} texels in the window; saved as '{savedTag}'");
                string? glow2 = MhoExtendedModManager.Model.ColorTags.GlowFile(rest[1], saved);
                using (var gb = new Bitmap(glow2!))
                {
                    int lit2 = 0;
                    for (int y = 0; y < gb.Height; y++) for (int x = 0; x < gb.Width; x++) { var p = gb.GetPixel(x, y); if (p.R + p.G + p.B > 0) lit2++; }
                    Check(Math.Abs(lit2 - at2) <= at2 / 10 + 5, $"the build's glow map with that reach: {lit2} texels (window {at2})");
                }
                return fails == 0 ? 0 : 1;
            }
            case "--color-tags-snapshot":
            {
                // --color-tags-snapshot <color.png> <out.png>: the Tag Colors window rendered off screen
                if (rest.Count < 3) { Console.WriteLine("--color-tags-snapshot <color.png> <out.png>"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                using var f = new MhoExtendedModManager.Model.Gui.ColorTagForm("material1", rest[1], [("#775027", "metal")]);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000);
                f.Show(); Application.DoEvents();
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(rest[2]);
                Console.WriteLine("snapshot: " + rest[2]);
                return 0;
            }
            case "--color-tags-check":
            {
                // --color-tags-check <color.png> <packed R.png> <packed B.png> <packed A.png> (read only): the color groups of a
                // stock map, each tagged by its real map (metal where most of it reflects, skin where most is skin, else cloth),
                // and the packed shine ColorTags makes from those tags against the real one, Soft and a flat value
                if (rest.Count < 5) { Console.WriteLine("--color-tags-check <color.png> <R.png> <B.png> <A.png>"); return 1; }
                byte[] Load(string f, int w, int h)
                {
                    using var src = new Bitmap(f); using var b = new Bitmap(src, w, h);
                    var d = b.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var px = new byte[w * h * 4]; System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d); return px;
                }
                int W, H; using (var r0 = new Bitmap(rest[2])) { W = r0.Width; H = r0.Height; }
                var col = Load(rest[1], W, H); var R = Load(rest[2], W, H); var B = Load(rest[3], W, H); var A = Load(rest[4], W, H);
                var groups = MhoExtendedModManager.Model.ColorTags.Groups(W, H, col);
                var assign = MhoExtendedModManager.Model.ColorTags.Assign(W, H, col, groups);
                var tags = new List<(string, string)>();
                for (int g = 0; g < groups.Count; g++)
                {
                    int n = 0, metal = 0, skin = 0;
                    for (int i = 0; i < assign.Length; i++) if (assign[i] == g) { n++; if (A[4 * i + 2] > 64) metal++; else if (B[4 * i + 2] > 128) skin++; }
                    string tag = metal * 2 > n ? "metal" : skin * 2 > n ? "skin" : "cloth";
                    tags.Add((MhoExtendedModManager.Model.ColorTags.Hex(groups[g].Center), tag));
                    Console.WriteLine($"  group {g}: {MhoExtendedModManager.Model.ColorTags.Hex(groups[g].Center)} {groups[g].Share:P0} → {tag} (metal {metal * 100 / Math.Max(1, n)} %, skin {skin * 100 / Math.Max(1, n)} %)");
                }
                var packed = MhoExtendedModManager.Model.ColorTags.MakePacked(W, H, col, tags);
                var soft = MhoExtendedModManager.Model.SpecMapGen.Make(W, H, col, "soft");
                double eT = 0, eS = 0, eF = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0; int N = W * H;
                for (int i = 0; i < N; i++)
                {
                    double real = R[4 * i + 2], gen = packed[4 * i + 2];
                    eT += Math.Abs(gen - real); eS += Math.Abs(soft[i] - real); eF += Math.Abs(26 - real);
                    sx += gen; sy += real; sxx += gen * gen; syy += real * real; sxy += gen * real;
                }
                double corr = (N * sxy - sx * sy) / Math.Sqrt((N * sxx - sx * sx) * (N * syy - sy * sy));
                Console.WriteLine($"shine error: tagged {eT / N:0.0}, Soft {eS / N:0.0}, flat 26 {eF / N:0.0}; tagged correlation with the real shine {corr:0.00}");
                return 0;
            }
            case "--is-mff":
            {
                // Read-only: whether model files are MFF characters (Bip001 skeleton: Browse for an FBX reads them as MFF). --is-mff <file> ...
                foreach (string f in rest.Skip(1)) Console.WriteLine($"{(MhoExtendedModManager.Model.SkeletonProfile.IsMff(f) ? "MFF  " : "other")}  {f}");
                return 0;
            }
            case "--skeleton-guess":
            {
                // --skeleton-guess <fbx | mff model> … [--mff-sample N] (read only): the shape guess (SkeletonProfile.Guess) forced
                // on rigs whose names are known (Mixamo, MFF Biped), each pick scored against the name: right, wrong, missed.
                MhoExtendedModManager.Model.Settings.Reset();
                var items = rest.Skip(1).Where(a => !a.StartsWith("--")).ToList();
                int si = rest.IndexOf("--mff-sample");
                if (si > 0 && si + 1 < rest.Count && int.TryParse(rest[si + 1], out int sample))
                {
                    items.Remove(rest[si + 1]);
                    var all = MhoExtendedModManager.Model.Source.AllModelFolders().ToList();
                    for (int k = 0; k < sample && all.Count > 0; k++) items.Add(all[(int)((long)k * all.Count / sample)]);
                }
                if (items.Count == 0) { Console.WriteLine("--skeleton-guess <fbx | mff model> … [--mff-sample N]"); return 1; }
                var biped = new HashSet<string>(MhoExtendedModManager.Model.Retarget.DefaultMap().Select(x => x.Mff), StringComparer.OrdinalIgnoreCase);
                int files = 0, guessed = 0, right = 0, wrong = 0, missed = 0;
                foreach (var item in items)
                {
                    string file;
                    try { file = MhoExtendedModManager.Model.Source.ResolveModelFile(item); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { Console.WriteLine($"{item}: {ex.Message}"); continue; }
                    files++;
                    Assimp.Scene? scene;
                    try { scene = MhoExtendedModManager.Model.SkeletonProfile.Open(file); }
                    catch (Assimp.AssimpException ex) { Console.WriteLine($"{Path.GetFileName(file)}: {ex.Message}"); continue; }
                    if (scene == null) continue;
                    // a renamed test copy names its bones' real names beside it (<file>.names.txt: new name, tab, old name)
                    var realName = File.Exists(file + ".names.txt")
                        ? File.ReadAllLines(file + ".names.txt").Select(l => l.Split('\t')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>();
                    string? Truth(string n) { n = realName.GetValueOrDefault(n, n); return MhoExtendedModManager.Model.SkeletonProfile.MixamoName(n) ?? (biped.Contains(n) ? n : null); }
                    var known = MhoExtendedModManager.Model.SkeletonProfile.Names(scene).Distinct().Where(n => Truth(n) != null).ToList();
                    Console.WriteLine($"{Path.GetFileName(file)}: found as {MhoExtendedModManager.Model.SkeletonProfile.Find(MhoExtendedModManager.Model.SkeletonProfile.Open(file)!, out _)?.Family ?? "(MFF / MHO / none)"}");
                    var r = MhoExtendedModManager.Model.SkeletonProfile.Guess(scene, out string? why);
                    if (r == null) { Console.WriteLine($"{Path.GetFileName(file)}: NOT GUESSED: {why}"); continue; }
                    guessed++;
                    int ok = 0; var bad = new List<string>();
                    foreach (var (from, to) in r.Rename)
                    {
                        string? t = Truth(from);
                        if (t == null) continue;   // a bone the names don't pair (twists, extras): not scored
                        if (t.Equals(to, StringComparison.OrdinalIgnoreCase)) ok++; else bad.Add($"{from} → {to}");
                    }
                    // the scored set: the names the retarget pairs (Biped twists are left out of the guess on purpose)
                    var miss = known.Where(n => !r.Rename.ContainsKey(n) && !Regex.IsMatch(Truth(n)!, "Twist")).ToList();
                    right += ok; wrong += bad.Count; missed += miss.Count;
                    Console.WriteLine($"{Path.GetFileName(file)}: {ok} right, {bad.Count} wrong, {miss.Count} missed");
                    foreach (var b in bad) Console.WriteLine("    wrong: " + b);
                    if (miss.Count > 0) Console.WriteLine("    missed: " + string.Join(", ", miss));
                    foreach (var n in r.Notes.Skip(1)) Console.WriteLine("    " + n);
                }
                Console.WriteLine($"total: {files} file(s), {guessed} guessed; pairs {right} right, {wrong} wrong, {missed} missed");
                return wrong == 0 ? 0 : 1;
            }
            default: return null;
        }
    }
}
