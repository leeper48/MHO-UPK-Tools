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
