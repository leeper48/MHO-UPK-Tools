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
        ("--export", "--export <mod> <out.zip>", "Save a mod from the library as a .zip (installable here and in MHModManager)."),
        ("--remove", "--remove <mod>", "Remove a disabled mod from the library (to the Recycle Bin). Apply first so none of it is left in the game."),
        ("--enable", "--enable <mod>", "Enable a mod (library state only; run --apply to change the game)."),
        ("--disable", "--disable <mod>", "Disable a mod (library state only; run --apply to change the game)."),
        ("--apply", "--apply [--dry-run [--out <dir>]]", "Make the game match the library: winners' packages in, icon packages rebuilt from stock with the winning textures, verified stock originals back for the rest. Writes through MPM's verified path with undo. --out saves what a dry run would write."),
        ("--capture-icons", "--capture-icons [mod name]", "Save icon changes in the game that no mod accounts for (other tools, deleted mods) as a new texture mod, so Apply keeps them."),
        ("--verify-strings", "--verify-strings", "Self-test: every .string file in the game and in the library's originals reads and writes back byte for byte."),
        ("--build-sound", "--build-sound <pack.mhsfx> <original.pck> <out.pck>", "Test: patch a copy of a sound package with one sound pack (never into the game folder)."),
        ("--compare-textures", "--compare-textures <a.upk> <b.upk>", "Every Texture2D in two packages: same size, format and best-mip pixels? (Checks a rebuild against another tool's.)"),
        ("--gui-snapshot", "--gui-snapshot <dir>", "Render the window to <dir>\\main.png (layout check)."),
    ];

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0].Equals("--first-run-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check of the setup window (with MHO_EXTMM_HOME set to a scratch folder, so nothing real is touched).
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var f = new Gui.FirstRunForm(Settings.Load());
            f.Shown += async (_, _) => { await Task.Delay(5000); Directory.CreateDirectory(args[1]); using var b = new Bitmap(f.Width, f.Height); f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height)); b.Save(Path.Combine(args[1], "first_run.png")); f.Close(); };
            Application.Run(f);
            return 0;
        }
        if (args.Length == 0 || (args.Length == 2 && args[0].Equals("--gui-snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // First run (no game folder or library yet): the setup window, then the mod list.
            var s = Settings.Load();
            if (!s.IsSetUp && args.Length == 0)
            {
                using var setup = new Gui.FirstRunForm(s);
                if (setup.ShowDialog() != DialogResult.OK) return 0;
            }
            var form = new Gui.MainForm();
            if (args.Length == 2) form.Shown += (_, _) => form.BeginInvoke(async () => { await form.Snapshot(args[1]); form.Close(); });
            Application.Run(form);
            return 0;
        }

        // CLI: attach to the launching console, UTF-8 without a BOM (as MHO Package Modifier does).
        if (AttachConsole(-1))
        {
            var utf8 = new UTF8Encoding(false);
            Console.OutputEncoding = utf8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
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
        if (rest[0].Equals("--compare-textures", StringComparison.OrdinalIgnoreCase) && rest.Count == 3) return TextureCompare.Run(rest[1], rest[2]);
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
                if (m == null) { Console.WriteLine("Usage: --export <mod> <out.zip> (no such mod?)"); return 1; }
                ModInstaller.Export(m, rest[2]);
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
            case "--capture-icons":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || !Directory.Exists(Settings.Cooked(gr))) { Console.WriteLine("Game folder not found. Pass --game <Marvel Heroes folder>."); return 1; }
                var game = new GameState(gr, data);
                return IconCapture.Run(lib, game, new Originals(data, game), rest.Count > 1 ? rest[1] : null);
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
