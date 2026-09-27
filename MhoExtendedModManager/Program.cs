using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// MHO Extended Mod Manager: replicates MHModManager 1.0.1 (by a former modder) on top of MHO Package Modifier's
/// package code, with room for new features. Phase 1 is read-only: it reads MHModManager's mod library and state
/// and reports what is enabled, what conflicts and what is actually live in the game folder.
/// </summary>
static class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    public static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    public static readonly (string Flag, string Syntax, string Summary)[] Commands =
    [
        ("--list", "--list", "Every mod in the library: priority, enabled, author, version, contents."),
        ("--conflicts", "--conflicts", "What more than one enabled mod changes, and which mod wins (top of the order)."),
        ("--status", "--status", "For each enabled mod's packages: is its copy live? Plus modified game packages no enabled mod claims."),
        ("--check", "--check", "Mods with a broken manifest or files missing from their folder."),
        ("--migrate", "--migrate <old folder> [--to <lib>]", "Copy MHModManager's mods, order, verified stock backups and settings into a new library (default %LOCALAPPDATA%\\MhoExtendedModManager\\library). The old folder is left as it is."),
        ("--install", "--install <archive.zip|.7z|folder>", "Add the mod(s) in an archive or folder to the library (top of the list, disabled)."),
        ("--export", "--export <mod> <out.zip> [--legacy]", "Save a mod from the library as a .zip (installable here and in MHModManager; --legacy also leaves out the other-icon-package extension)."),
        ("--remove", "--remove <mod>", "Remove a disabled mod from the library (to the Recycle Bin). Apply first so none of it is left in the game."),
        ("--enable", "--enable <mod>", "Enable a mod (library state only; run --apply to change the game)."),
        ("--disable", "--disable <mod>", "Disable a mod (library state only; run --apply to change the game)."),
        ("--apply", "--apply [--dry-run [--out <dir>]]", "Make the game match the library: winners' packages in, icon packages rebuilt from stock with the winning textures, verified stock originals back for the rest. Writes through MPM's verified path with undo. --out saves what a dry run would write."),
        ("--capture-icons", "--capture-icons [mod name]", "Save icon changes in the game that no mod accounts for (other tools, deleted mods) as a new texture mod, so Apply keeps them."),
        ("--verify-strings", "--verify-strings", "Self-test: every .string file in the game and in the library's originals reads and writes back byte for byte."),
        ("--build-sound", "--build-sound <pack.mhsfx> <original.pck> <out.pck>", "Test: patch a copy of a sound package with one sound pack (never into the game folder)."),
        ("--add-original", "--add-original <file.tfc>", "Keep a clean copy of a texture cache (e.g. Icons.tfc) as its original: must carry the stock date (2024-03-14) and the game file's size. Apply then keeps the live one original."),
        ("--extract-texture", "--extract-texture <icons|achievements|store> <texture> <out.dds>", "Save a stock icon / achievement / store image (from the originals) as .dds."),
        ("--extract-strings", "--extract-strings <lang> <out.json>", "Save every original string of a language (eng, deu, …) as .json in the mod format."),
        ("--compare-textures", "--compare-textures <a.upk> <b.upk> [<cache folder a> <cache folder b>]", "Every Texture2D in two packages: same size, format and best-mip pixels? (Checks a rebuild against another tool's.)"),
        ("--gui-snapshot", "--gui-snapshot <dir>", "Render the window to <dir>\\main.png (layout check)."),
    ];

    [STAThread]
    static int Main(string[] args)
    {
        // Undo snapshots of the files Apply writes go to data\history next to the exe, not AppData.
        MhoPackageModifier.History.RootOverride = Settings.HistoryFolder;
        bool gui = args.Length == 0 || args[0].StartsWith("--gui", StringComparison.OrdinalIgnoreCase) || args[0].StartsWith("--editor", StringComparison.OrdinalIgnoreCase) || args[0].StartsWith("--first-run", StringComparison.OrdinalIgnoreCase);
        if (Settings.CheckWritable() is string notWritable)
        {
            string msg = $"MHO Extended Mod Manager keeps its settings and mods in a \"data\" folder next to the program, and can't write there:\n\n{notWritable}\n\nMove the program's folder somewhere you can write to (not Program Files), e.g. C:\\Games\\MHO Extended Mod Manager.";
            if (gui) MessageBox.Show(msg, "MHO Extended Mod Manager"); else { AttachCliConsole(); Console.WriteLine(msg); }
            return 2;
        }
        if (args.Length == 2 && args[0].Equals("--first-run-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check of the setup window (with MHO_EXTMM_HOME set to a scratch folder, so nothing real is touched).
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var f = new Gui.FirstRunForm(Settings.Load());
            f.Shown += async (_, _) => { await Task.Delay(5000); Directory.CreateDirectory(args[1]); using var b = new Bitmap(f.Width, f.Height); f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(Path.Combine(args[1], "first_run.png")); f.Close(); };
            Application.Run(f);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--editor-save-test", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;   // scratch libraries only
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.EditorSaveTest(args[1]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length is 2 or 3 && args[0].Equals("--extract-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.ExtractSnapshot(args[1], args.Length == 3 ? args[2] : "store_vision_classic"); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length >= 2 && args[0].Equals("--editor-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.EditorSnapshot(args[1], args.Length > 2 ? args[2] : null); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 0 || (args.Length is 2 or 3 && args[0].Equals("--gui-snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // A library from an earlier version (0.7.0 and before kept it in AppData): offer to move it next to the exe.
            if (args.Length == 0 && Settings.FindAppDataLibrary() is string oldLib)
            {
                var answer = MessageBox.Show($"Your mod library is in AppData:\n{oldLib}\n\nThis version keeps everything next to the program instead:\n{Settings.Home}\n\nMove the library there now? (Yes is recommended. No starts the first-run setup; your AppData library is left alone.)",
                    "MHO Extended Mod Manager", MessageBoxButtons.YesNoCancel);
                if (answer == DialogResult.Cancel) return 0;
                if (answer == DialogResult.Yes)
                    try { Settings.MoveFromAppData(oldLib); MessageBox.Show($"Moved. Your mods are now in\n{Settings.DefaultLibrary}", "MHO Extended Mod Manager"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show("The move didn't finish, and nothing was changed: " + ex.Message, "MHO Extended Mod Manager"); return 1; }
            }
            // First run (no game folder or library yet): the setup window, then the mod list.
            var s = Settings.Load();
            if (!s.IsSetUp && args.Length == 0)
            {
                using var setup = new Gui.FirstRunForm(s);
                if (setup.ShowDialog() != DialogResult.OK) return 0;
            }
            var form = new Gui.MainForm();
            if (args.Length >= 2) form.Shown += (_, _) => form.BeginInvoke(async () => { await form.Snapshot(args[1], args.Length == 3 ? args[2] : null); form.Close(); });
            Application.Run(form);
            return 0;
        }

        AttachCliConsole();
        Console.WriteLine($"MHO Extended Mod Manager v{Version}");

        var settings = Settings.Load();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--library", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) settings.Library = args[++i];
            else if (args[i].Equals("--game", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) settings.GameRoot = args[++i];
            else rest.Add(args[i]);
        }
        if (rest.Count == 0) { Usage(); return 1; }
        if (rest[0].Equals("--compare-textures", StringComparison.OrdinalIgnoreCase) && rest.Count is 3 or 5) return TextureCompare.Run(rest[1], rest[2], rest.Count == 5 ? rest[3] : null, rest.Count == 5 ? rest[4] : null);
        if (rest[0].Equals("--move-from-appdata-test", StringComparison.OrdinalIgnoreCase) && rest.Count == 4)
        {
            // Test of the AppData → data move with explicit old paths (scratch only: needs MHO_EXTMM_HOME).
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder."); return 2; }
            var s = Settings.MoveFromAppData(rest[1], rest[2], rest[3]);
            Console.WriteLine($"moved: library now {s.LibraryPath} (setting: {s.Library ?? "default"}), game {s.GameRoot}, set up: {s.IsSetUp}");
            return 0;
        }
        if (rest[0].Equals("--tfc-changes", StringComparison.OrdinalIgnoreCase) && rest.Count == 3)
        {
            // Diagnostic: textures whose cached mips differ between the live <cache>.tfc and a copy (read-only).
            string? gr0 = settings.ResolvedGameRoot(Settings.LibraryData(settings.LibraryPath));
            if (gr0 == null) { Console.WriteLine("Game folder not found."); return 1; }
            string cooked = Settings.Cooked(gr0), live = Path.Combine(cooked, rest[1] + ".tfc");
            using var fa = File.OpenRead(live); using var fb = File.OpenRead(rest[2]);
            var changed = new List<string>(); int checkedMips = 0;
            foreach (var e in MhoPackageModifier.TfcCache.All(cooked).Where(e => e.Cache.Equals(rest[1], StringComparison.OrdinalIgnoreCase)))
                foreach (var (mip, off, size) in e.Mips.Where(m => m.Size > 0))
                {
                    var x = new byte[size]; var y = new byte[size];
                    fa.Position = off; fa.ReadExactly(x); fb.Position = off; fb.ReadExactly(y);
                    checkedMips++;
                    if (!x.AsSpan().SequenceEqual(y)) { changed.Add($"{e.Path} (mip {mip})"); break; }
                }
            changed.ForEach(c => Console.WriteLine("  " + c));
            Console.WriteLine($"{changed.Count} texture(s) differ ({checkedMips} cached mips compared).");
            return 0;
        }
        if (rest[0].Equals("--build-sound", StringComparison.OrdinalIgnoreCase) && rest.Count == 4)
        {
            // Test command: patch a copy of a .pck with one sound pack, outside the game folder.
            var pack = SoundPack.Load(rest[1]);
            var notes = new List<string>(); var problems = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[]? built = Akpk.Build(rest[2], Path.GetFileName(rest[2]), [pack], notes, problems);
            notes.ForEach(n => Console.WriteLine("  note: " + n)); problems.ForEach(n => Console.WriteLine("  PROBLEM: " + n));
            if (built == null) return 1;
            if (Path.GetFullPath(rest[3]).Contains(@"\CookedPCConsole\", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Refusing to write into the game folder."); return 1; }
            File.WriteAllBytes(rest[3], built);
            Console.WriteLine($"Built {rest[3]}: {built.Length:N0} bytes in {sw.Elapsed.TotalSeconds:0.0}s");
            return 0;
        }

        if (rest[0].Equals("--test-locks", StringComparison.OrdinalIgnoreCase)) return LockTest.Run();

        if (rest[0].Equals("--migrate", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Count < 2) { Usage(); return 1; }
            int to = rest.FindIndex(a => a.Equals("--to", StringComparison.OrdinalIgnoreCase));
            string target = to > 0 && to + 1 < rest.Count ? rest[to + 1] : DefaultLibrary;
            try { return Migration.Run(rest[1], target, settings); }
            catch (IOException ex) { Console.WriteLine("Migration stopped: " + ex.Message); return 1; }
        }

        string? data = Settings.LibraryData(settings.LibraryPath);
        if (data == null) { Console.WriteLine("No mod library yet. Open the window once (first-run setup), or run --migrate <MHModManager folder>."); return 1; }
        var lib = ModLibrary.Load(data);
        Console.WriteLine($"library: {data} ({lib.Mods.Count} mods, {lib.Mods.Count(m => m.Enabled)} enabled)");

        switch (rest[0].ToLowerInvariant())
        {
            case "--list": return List(lib);
            case "--conflicts": return Conflicts(lib);
            case "--check": return Check(lib);
            case "--status":
                string? root = settings.ResolvedGameRoot(data);
                if (root == null || !Directory.Exists(Settings.Cooked(root))) { Console.WriteLine("Game folder not found. Pass --game <Marvel Heroes folder>."); return 1; }
                return Status(lib, new GameState(root, data));
            case "--install":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                if (rest.Count < 2) { Usage(); return 1; }
                var log = new List<string>();
                List<string> done;
                try { done = ModInstaller.Install(rest[1], lib, log); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { Console.WriteLine("Couldn't install it: " + ex.Message); return 1; }
                log.ForEach(Console.WriteLine);
                return done.Count > 0 ? 0 : 1;
            }
            case "--export":
            {
                var m = rest.Count > 2 ? lib.Find(rest[1]) : null;
                if (m == null) { Console.WriteLine("Usage: --export <mod> <out.zip> [--legacy] (no such mod?)"); return 1; }
                ModInstaller.Export(m, rest[2], rest.Any(a => a.Equals("--legacy", StringComparison.OrdinalIgnoreCase)));
                Console.WriteLine($"Exported {m.Name} to {rest[2]} ({new FileInfo(rest[2]).Length:N0} bytes).");
                return 0;
            }
            case "--remove":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                var m = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (m == null) { Console.WriteLine("No such mod."); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                string? why = ModInstaller.Remove(m, lib, gr != null && Settings.IsGameRoot(gr) ? new GameState(gr, data) : null);
                Console.WriteLine(why ?? $"Removed {m.Name} (its folder is in the Recycle Bin).");
                return why == null ? 0 : 1;
            }
            case "--enable" or "--disable":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                var m = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (m == null) { Console.WriteLine("No such mod."); return 1; }
                m.Enabled = rest[0].Equals("--enable", StringComparison.OrdinalIgnoreCase);
                lib.SaveState();
                Console.WriteLine($"{m.Name}: {(m.Enabled ? "enabled" : "disabled")}. Run --apply to update the game.");
                return 0;
            }
            case "--extract-texture" or "--extract-strings":
            {
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || !Settings.IsGameRoot(gr)) { Console.WriteLine("Game folder not found."); return 1; }
                var cat = new StockCatalog(lib, new GameState(gr, data));
                if (rest[0].Equals("--extract-strings", StringComparison.OrdinalIgnoreCase))
                {
                    if (rest.Count < 3) { Usage(); return 1; }
                    int n = cat.ExportStrings(rest[1], rest[2]);
                    Console.WriteLine($"Saved {n:N0} original {rest[1]} strings to {rest[2]}.");
                    return 0;
                }
                if (rest.Count < 4) { Usage(); return 1; }
                int k = rest[1].ToLowerInvariant() switch { "icons" => 0, "achievements" => 1, "store" => 2, _ => -1 };
                if (k < 0) { Console.WriteLine("Package kind: icons, achievements or store."); return 1; }
                string? why = cat.ExportDds(Applier.IconPackages[k].File, rest[2], rest[3]);
                Console.WriteLine(why == null ? $"Saved the stock {rest[2]} to {rest[3]}." : $"Not saved: {why}");
                return why == null ? 0 : 1;
            }
            case "--add-original":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || rest.Count < 2) { Usage(); return 1; }
                var g = new GameState(gr, data);
                string cache = Path.GetFileNameWithoutExtension(rest[1]).Split('.')[0];   // Icons.tfc.bak -> Icons
                string? why = new Originals(data, g).AddTfc(rest[1], cache);
                Console.WriteLine(why == null ? $"Kept {rest[1]} as the original {cache}.tfc (with a copy of the texture cache manifest)." : $"Not kept: {why}.");
                return why == null ? 0 : 1;
            }
            case "--capture-icons":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || !Directory.Exists(Settings.Cooked(gr))) { Console.WriteLine("Game folder not found. Pass --game <Marvel Heroes folder>."); return 1; }
                var game = new GameState(gr, data);
                return IconCapture.Run(lib, game, new Originals(data, game), rest.Count > 1 ? rest[1] : null);
            }
            case "--verify-writer":
            {
                // Self-test: every mod in the library re-saved by ModWriter into a scratch library (never this one), then compared.
                if (rest.Count < 2) { Console.WriteLine("Usage: --verify-writer <empty scratch folder>"); return 1; }
                string scratch = Path.GetFullPath(rest[1]);
                if (scratch.StartsWith(Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("The scratch folder can't be inside the library."); return 1; }
                ModInstaller.CreateEmptyLibrary(scratch);
                // A folder whose name differs from its mod's name (MHModManager made "Bucky …_3"), copied as is, for the edit pass.
                foreach (var m in lib.Mods.Where(m => m.FolderName != ModInstaller.Sanitise(m.Name)))
                    foreach (string f in Directory.GetFiles(m.Folder)) { Directory.CreateDirectory(Path.Combine(scratch, "mods", m.FolderName)); File.Copy(f, Path.Combine(scratch, "mods", m.FolderName, Path.GetFileName(f))); }
                int bad = 0, jsonSame = 0, jsonTotal = 0, manifestSame = 0, manifestTotal = 0; var manifestDiffs = new List<string>();
                foreach (var m in lib.Mods)
                {
                    var target = ModLibrary.Load(scratch);
                    string? name = ModWriter.Save(target, ModDraft.From(m), null, out string? err);
                    if (name == null) { Console.WriteLine($"  {m.Name}: {err}"); bad++; continue; }
                    var w = ModLibrary.Load(scratch).Find(name)!;
                    var diffs = new List<string>();
                    string Norm(ModManifest x) => System.Text.Json.JsonSerializer.Serialize(new { x.Name, x.Author, x.Version, R = x.Replacements.Select(r => r.TextureName), A = x.AchievementReplacements.Select(r => r.TextureName), S = x.StoreReplacements.Select(r => r.TextureName), x.UpkReplacements, x.AudioPacks, L = x.Languages.Order() });
                    if (Norm(m.Manifest) != Norm(w.Manifest)) diffs.Add("manifest differs");
                    // Each referenced file: same bytes as the original's.
                    for (int k = 0; k < Applier.IconPackages.Length; k++)
                    {
                        var a = Applier.IconPackages[k].List(m.Manifest); var b = Applier.IconPackages[k].List(w.Manifest);
                        for (int i = 0; i < a.Count; i++) if (!File.ReadAllBytes(Path.Combine(m.Folder, a[i].DdsFileName)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(w.Folder, b[i].DdsFileName)))) diffs.Add($"{a[i].TextureName} image differs");
                    }
                    foreach (string f in m.Manifest.UpkReplacements.Concat(m.Manifest.AudioPacks))
                        if (new FileInfo(Path.Combine(m.Folder, f)).Length != new FileInfo(Path.Combine(w.Folder, f)).Length) diffs.Add($"{f} differs");
                    if (!m.Strings.OrderBy(s => s.Id).Select(s => (s.Language, s.File.ToLowerInvariant(), s.Id, s.Text, s.FlagsProduced)).SequenceEqual(w.Strings.OrderBy(s => s.Id).Select(s => (s.Language, s.File.ToLowerInvariant(), s.Id, s.Text, s.FlagsProduced)))) diffs.Add("strings differ");
                    manifestTotal++;
                    if (File.ReadAllBytes(Path.Combine(m.Folder, "manifest.json")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(w.Folder, "manifest.json")))) manifestSame++;
                    else if (manifestDiffs.Count < 5) manifestDiffs.Add(m.Name);
                    foreach (string lang in m.Manifest.Languages)
                    {
                        jsonTotal++;
                        if (File.ReadAllBytes(Path.Combine(m.Folder, lang + ".json")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(w.Folder, lang + ".json")))) jsonSame++;
                    }
                    if (diffs.Count > 0) { bad++; Console.WriteLine($"  {m.Name}: {string.Join("; ", diffs.Take(3))}"); }
                }
                Console.WriteLine($"{lib.Mods.Count - bad} of {lib.Mods.Count} mods re-saved with the same content; {jsonSame} of {jsonTotal} string files and {manifestSame} of {manifestTotal} manifests byte-identical to the originals{(manifestDiffs.Count > 0 ? " (manifests differ: " + string.Join(", ", manifestDiffs) + ")" : "")}.");
                // Pass 2: every mod in the scratch library saved again as an unchanged edit of itself: same folder, same files.
                var sl = ModLibrary.Load(scratch); int editBad = 0;
                foreach (var m in sl.Mods.ToList())
                {
                    var before = Directory.GetFiles(m.Folder).Select(f => (Path.GetFileName(f), new FileInfo(f).Length)).OrderBy(x => x.Item1).ToList();
                    string? name = ModWriter.Save(ModLibrary.Load(scratch), ModDraft.From(ModLibrary.Load(scratch).Find(m.FolderName)!), ModLibrary.Load(scratch).Mods.First(x => x.FolderName == m.FolderName), out string? err);
                    var after = name == null ? [] : Directory.GetFiles(Path.Combine(scratch, "mods", name)).Select(f => (Path.GetFileName(f), new FileInfo(f).Length)).OrderBy(x => x.Item1).ToList();
                    if (name != m.FolderName || !before.SequenceEqual(after)) { editBad++; Console.WriteLine($"  edit of {m.FolderName}: {(name == null ? err : name != m.FolderName ? "moved to " + name : "files differ")}"); }
                }
                Console.WriteLine($"{sl.Mods.Count - editBad} of {sl.Mods.Count} unchanged edits kept their folder and files.");
                return bad == 0 && editBad == 0 ? 0 : 1;
            }
            case "--verify-strings":
            {
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null) { Console.WriteLine("Game folder not found."); return 1; }
                var files = new List<string>();
                var loco = new GameState(gr, data).Loco;
                if (Directory.Exists(loco)) files.AddRange(Directory.GetFiles(loco, "*.string", SearchOption.AllDirectories));
                foreach (string d in new[] { Path.Combine(data, "originals", "strings"), Path.Combine(data, "legacy", "string_backups") })
                    if (Directory.Exists(d)) files.AddRange(Directory.GetFiles(d, "*.string*", SearchOption.AllDirectories));
                int bad = 0;
                foreach (string f in files)
                {
                    byte[] b = File.ReadAllBytes(f);
                    string result;
                    try { var sf = StringFile.Parse(b); result = sf.Write().AsSpan().SequenceEqual(b) ? $"OK  ({sf.Entries.Count:N0} entries, {sf.Entries.Values.Sum(e => e.Variants.Count):N0} variants)" : "DIFFERS"; }
                    catch (InvalidDataException ex) { result = "ERROR " + ex.Message; }
                    if (!result.StartsWith("OK")) bad++;
                    Console.WriteLine($"  {result}  {f}");
                }
                Console.WriteLine(bad == 0 ? $"All {files.Count} string files round-trip byte for byte." : $"{bad} of {files.Count} failed.");
                return bad == 0 ? 0 : 1;
            }
            case "--apply":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || !Directory.Exists(Settings.Cooked(gr))) { Console.WriteLine("Game folder not found. Pass --game <Marvel Heroes folder>."); return 1; }
                var game = new GameState(gr, data);
                if (!game.HasStockList) { Console.WriteLine("No stock checksum list in the library; can't verify originals."); return 1; }
                var originals = new Originals(data, game);
                var plan = Applier.MakePlan(lib, game, originals);
                Applier.Print(plan);
                int outAt = rest.FindIndex(a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
                if (outAt > 0 && outAt + 1 < rest.Count)
                {
                    Directory.CreateDirectory(rest[outAt + 1]);
                    foreach (var s in plan.Steps)
                    {
                        string to = Path.Combine(rest[outAt + 1], s.File);
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.WriteAllBytes(to, s.Built ?? File.ReadAllBytes(s.Source!));
                    }
                    Console.WriteLine($"Saved {plan.Steps.Count} package(s) to {rest[outAt + 1]} (game folder untouched).");
                }
                if (rest.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) || plan.Steps.Count == 0) return 0;
                return Applier.Execute(plan, game, originals, data) ? 0 : 1;
            }
            default: Usage(); return 1;
        }
    }

    public static string DefaultLibrary => Settings.DefaultLibrary;

    /// <summary>CLI: attach to the launching console, UTF-8 without a BOM (as MHO Package Modifier does).</summary>
    static void AttachCliConsole()
    {
        if (!AttachConsole(-1)) return;
        var utf8 = new UTF8Encoding(false);
        Console.OutputEncoding = utf8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }

    /// <summary>The library is still MHModManager's own data folder (not migrated): this manager only reads it.</summary>
    public static bool IsOldManager(string data) => File.Exists(Path.Combine(Path.GetDirectoryName(data.TrimEnd('\\'))!, "MHModManager.exe"));

    public const string OldManagerRefusal = "This library is still MHModManager's own folder. Run --migrate first; this manager doesn't change the old one's files.";

    static void Usage()
    {
        Console.WriteLine("Usage: MHO_Ext_ModManager.exe <command> [--library <MHModManager folder>] [--game <Marvel Heroes folder>]");
        Console.WriteLine("No arguments opens the window. Only --apply writes to the game folder (verified, with undo; refused while the game runs).");
        foreach (var c in Commands) Console.WriteLine($"  {c.Syntax,-26} {c.Summary}");
    }

    static int List(ModLibrary lib)
    {
        foreach (var m in lib.Mods)
            Console.WriteLine($"{m.Priority + 1,4}  {(m.Enabled ? "on " : "off")}  {m.Name}  [{m.Manifest.Author}, v{m.Manifest.Version}]  {(m.LoadError ?? m.Summary())}");
        return 0;
    }

    static int Conflicts(ModLibrary lib)
    {
        var conflicts = lib.Conflicts();
        foreach (var (claim, mods) in conflicts)
            Console.WriteLine($"{claim}: {mods[0].Name} wins over {string.Join(", ", mods.Skip(1).Select(m => m.Name))}");
        Console.WriteLine($"{conflicts.Count} conflict(s) between enabled mods.");
        return 0;
    }

    static int Check(ModLibrary lib)
    {
        int bad = 0;
        foreach (var m in lib.Mods)
        {
            var missing = m.LoadError != null ? [m.LoadError] : m.MissingFiles().ToList();
            if (missing.Count == 0) continue;
            bad++;
            Console.WriteLine($"{m.Name}{(m.Enabled ? " (enabled)" : "")}: {string.Join(", ", missing)}");
        }
        Console.WriteLine(bad == 0 ? "All mods complete." : $"{bad} mod(s) with problems.");
        return bad == 0 ? 0 : 1;
    }

    static int Status(ModLibrary lib, GameState game)
    {
        Console.WriteLine($"game: {game.Cooked}{(game.HasStockList ? $" (stock list: {game.StockCount:N0} packages)" : " (no stock checksum list found)")}");
        var winners = lib.PackageWinners();
        var counts = new Dictionary<PackageState, int>();
        foreach (var (file, mod) in winners.OrderBy(w => w.Value.Priority).ThenBy(w => w.Key))
        {
            var s = game.Check(mod, file);
            counts[s] = counts.GetValueOrDefault(s) + 1;
            if (s != PackageState.Applied) Console.WriteLine($"  {file} ({mod.Name}): {GameState.Describe(s)}");
        }
        Console.WriteLine($"{winners.Count} package(s) from enabled mods: " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Value} {GameState.Describe(c.Key)}")));

        // Modified game packages no enabled mod accounts for (MHO Package Modifier edits, other tools, leftovers of disabled mods).
        // A newer date alone doesn't mean modified: MHModManager restores stock copies on disable, which re-dates them.
        var dated = game.ModifiedByDate().Where(f => !winners.ContainsKey(f.Name)).OrderBy(f => f.Name).ToList();
        var copies = game.HasStockList ? dated.Where(f => !game.IsStockName(f.Name)).ToList() : [];
        var restored = dated.Except(copies).Where(f => game.IsStock(f.Name) == true).ToList();
        var others = dated.Except(copies).Except(restored).ToList();
        Console.WriteLine($"{restored.Count} package(s) newer than stock but byte-identical to it (restored).");
        if (copies.Count > 0) Console.WriteLine($"{copies.Count} file(s) whose names aren't stock packages (copies, variants): {string.Join(", ", copies.Select(f => f.Name))}");
        Console.WriteLine($"{others.Count} other modified package(s) in the game folder (no enabled mod accounts for them):");
        foreach (var f in others)
        {
            var from = lib.Mods.Where(m => !m.Enabled && m.Manifest.UpkReplacements.Contains(f.Name, StringComparer.OrdinalIgnoreCase)).Select(m => m.Name).ToList();
            string bak = File.Exists(f.FullName + ".bak") ? ", has .bak" : "";
            Console.WriteLine($"  {f.Name}  {f.LastWriteTime:yyyy-MM-dd}{bak}{(from.Count > 0 ? "  (in disabled: " + string.Join(", ", from) + ")" : "")}");
        }
        return 0;
    }
}
