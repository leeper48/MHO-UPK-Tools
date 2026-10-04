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
            case "--model-build":
            {
                // --model-build <mff model> <package name or file> <out folder> [--map bonemap.json]: the importer's --encode-mff
                // (MFF_HAIR, MFF_CAPE, MFF_ANIM_FBX, MFF_MODEL_FBX … as there; MHO_MFF_SOURCE = the MFF folder, else Settings)
                if (rest.Count < 4) { Console.WriteLine("--model-build <mff model> <package name or file> <out folder> [--map bonemap.json]"); return 1; }
                int mi = rest.IndexOf("--map");
                string? map = mi > 0 && mi + 1 < rest.Count ? rest[mi + 1] : null;
                MhoMffImporter.Settings.Reset();
                try
                {
                    string package = MhoMffImporter.BasePackage.Resolve(rest[2], true);
                    var result = MhoMffImporter.ImportBuild.Run(rest[1], package, rest[3], MhoMffImporter.ImportOptions.FromEnvironment(null, map, null), Console.WriteLine);
                    return result == null ? 1 : 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
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
                MhoMffImporter.Settings.Change(s => s.MffFolder = probe);
                MhoMffImporter.Settings.Reset();
                MhoMffImporter.Settings.Current.BlenderPath = @"C:\BlenderTest\blender.exe"; MhoMffImporter.Settings.Current.Save();
                MhoMffImporter.Settings.App!.Save();   // the window's copy saved, as on exit
                var after = Settings.Load();
                bool ok1 = after.MffFolder == probe, ok2 = after.BlenderPath == @"C:\BlenderTest\blender.exe";
                Console.WriteLine((ok1 ? "PASS" : "FAIL") + " the MFF folder survives the window's save on exit: " + after.MffFolder);
                Console.WriteLine((ok2 ? "PASS" : "FAIL") + " the Blender choice survives it too: " + after.BlenderPath);
                // the old way (0.37.113–0.37.115): the file written on its own, then the window's copy saved on exit
                var window = MhoMffImporter.Settings.App!;
                var fresh = Settings.Load(); fresh.MffFolder = probe + "_old"; fresh.Save();
                window.Save();
                bool lost = Settings.Load().MffFolder != probe + "_old";
                Console.WriteLine((lost ? "PASS" : "FAIL") + " (the old way loses it, as Kurt saw: " + Settings.Load().MffFolder + ")");
                MhoMffImporter.Settings.Change(s => { s.MffFolder = keep; s.BlenderPath = keepBlender; });   // back as it was
                return ok1 && ok2 && lost ? 0 : 1;
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
                MhoMffImporter.Settings.Reset();
                try
                {
                    string package = MhoMffImporter.BasePackage.Resolve(rest[2], true);
                    var sk = MhoMffImporter.MhoSkeleton.Load(package, null);
                    string rigged = MhoMffImporter.AutoRig.Rig(rest[1], sk, package, Path.Combine(rest[3], "rig"), Console.WriteLine);
                    Console.WriteLine("rigged: " + rigged);
                    var o = MhoMffImporter.ImportOptions.FromEnvironment(null, null, null) with { SourceFbx = rigged };
                    return MhoMffImporter.ImportBuild.Run(rigged, package, rest[3], o, Console.WriteLine) == null ? 1 : 0;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
            }
            case "--color-tags-snapshot":
            {
                // --color-tags-snapshot <color.png> <out.png>: the Tag Colors window rendered off screen
                if (rest.Count < 3) { Console.WriteLine("--color-tags-snapshot <color.png> <out.png>"); return 1; }
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                using var f = new MhoMffImporter.Gui.ColorTagForm("material1", rest[1], [("#775027", "metal")]);
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
                var groups = MhoMffImporter.ColorTags.Groups(W, H, col);
                var assign = MhoMffImporter.ColorTags.Assign(W, H, col, groups);
                var tags = new List<(string, string)>();
                for (int g = 0; g < groups.Count; g++)
                {
                    int n = 0, metal = 0, skin = 0;
                    for (int i = 0; i < assign.Length; i++) if (assign[i] == g) { n++; if (A[4 * i + 2] > 64) metal++; else if (B[4 * i + 2] > 128) skin++; }
                    string tag = metal * 2 > n ? "metal" : skin * 2 > n ? "skin" : "cloth";
                    tags.Add((MhoMffImporter.ColorTags.Hex(groups[g].Center), tag));
                    Console.WriteLine($"  group {g}: {MhoMffImporter.ColorTags.Hex(groups[g].Center)} {groups[g].Share:P0} → {tag} (metal {metal * 100 / Math.Max(1, n)} %, skin {skin * 100 / Math.Max(1, n)} %)");
                }
                var packed = MhoMffImporter.ColorTags.MakePacked(W, H, col, tags);
                var soft = MhoMffImporter.SpecMapGen.Make(W, H, col, "soft");
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
            case "--skeleton-guess":
            {
                // --skeleton-guess <fbx | mff model> … [--mff-sample N] (read only): the shape guess (SkeletonProfile.Guess) forced
                // on rigs whose names are known (Mixamo, MFF Biped), each pick scored against the name: right, wrong, missed.
                MhoMffImporter.Settings.Reset();
                var items = rest.Skip(1).Where(a => !a.StartsWith("--")).ToList();
                int si = rest.IndexOf("--mff-sample");
                if (si > 0 && si + 1 < rest.Count && int.TryParse(rest[si + 1], out int sample))
                {
                    items.Remove(rest[si + 1]);
                    var all = MhoMffImporter.Source.AllModelFolders().ToList();
                    for (int k = 0; k < sample && all.Count > 0; k++) items.Add(all[(int)((long)k * all.Count / sample)]);
                }
                if (items.Count == 0) { Console.WriteLine("--skeleton-guess <fbx | mff model> … [--mff-sample N]"); return 1; }
                var biped = new HashSet<string>(MhoMffImporter.Retarget.DefaultMap().Select(x => x.Mff), StringComparer.OrdinalIgnoreCase);
                int files = 0, guessed = 0, right = 0, wrong = 0, missed = 0;
                foreach (var item in items)
                {
                    string file;
                    try { file = MhoMffImporter.Source.ResolveModelFile(item); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { Console.WriteLine($"{item}: {ex.Message}"); continue; }
                    files++;
                    Assimp.Scene? scene;
                    try { scene = MhoMffImporter.SkeletonProfile.Open(file); }
                    catch (Assimp.AssimpException ex) { Console.WriteLine($"{Path.GetFileName(file)}: {ex.Message}"); continue; }
                    if (scene == null) continue;
                    // a renamed test copy names its bones' real names beside it (<file>.names.txt: new name, tab, old name)
                    var realName = File.Exists(file + ".names.txt")
                        ? File.ReadAllLines(file + ".names.txt").Select(l => l.Split('\t')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>();
                    string? Truth(string n) { n = realName.GetValueOrDefault(n, n); return MhoMffImporter.SkeletonProfile.MixamoName(n) ?? (biped.Contains(n) ? n : null); }
                    var known = MhoMffImporter.SkeletonProfile.Names(scene).Distinct().Where(n => Truth(n) != null).ToList();
                    Console.WriteLine($"{Path.GetFileName(file)}: found as {MhoMffImporter.SkeletonProfile.Find(MhoMffImporter.SkeletonProfile.Open(file)!, out _)?.Family ?? "(MFF / MHO / none)"}");
                    var r = MhoMffImporter.SkeletonProfile.Guess(scene, out string? why);
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
