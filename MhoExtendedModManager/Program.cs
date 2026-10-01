using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

/// <summary>
/// MHO Extended Mod Manager: replicates MHModManager 1.0.1 (by a former modder) on top of MHO Package Modifier's
/// package code, with room for new features. Phase 1 is read-only: it reads MHModManager's mod library and state
/// and reports what is enabled, what conflicts and what is actually live in the game folder.
/// </summary>
static partial class Program
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SearchMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    public static readonly (string Flag, string Syntax, string Summary)[] Commands =
    [
        ("--list", "--list", "Every mod in the library: priority, enabled, author, version, contents."),
        ("--conflicts", "--conflicts", "What more than one enabled mod changes, and which mod wins (top of the order)."),
        ("--status", "--status", "For each enabled mod's packages: is its copy live? Plus modified game packages no enabled mod claims."),
        ("--check", "--check", "Mods with a broken manifest or files missing from their folder."),
        ("--migrate", "--migrate <old folder> [--to <lib>]", "Copy MHModManager's mods, order, verified stock backups and settings into a new library (default %LOCALAPPDATA%\\MhoExtendedModManager\\library). The old folder is left as it is."),
        ("--install", "--install <archive.zip|.7z|folder> [--replace]", "Add the mod(s) in an archive or folder to the library (top of the list, disabled). --replace updates a mod of the same name in place (keeps its place, on/off, lock, tags and note)."),
        ("--export", "--export <mod> <out.zip> [--legacy]", "Save a mod from the library as a .zip (installable here and in MHModManager; --legacy also leaves out the other-icon-package extension)."),
        ("--remove", "--remove <mod> [--dry-run]", "Remove a disabled mod from the library (to the Recycle Bin). Refused while its own files are still modded in the game (Apply first). --dry-run only says whether it could be removed."),
        ("--enable", "--enable <mod>", "Enable a mod (library state only; run --apply to change the game)."),
        ("--disable", "--disable <mod>", "Disable a mod (library state only; run --apply to change the game)."),
        ("--apply", "--apply [--dry-run [--out <dir>]]", "Make the game match the library: winners' packages in, icon packages rebuilt from stock with the winning textures, verified stock originals back for the rest. Writes through MPM's verified path with undo. --out saves what a dry run would write."),
        ("--capture-icons", "--capture-icons [mod name]", "Save icon changes in the game that no mod accounts for (other tools, deleted mods) as a new texture mod, so Apply keeps them."),
        ("--verify-strings", "--verify-strings", "Self-test: every .string file in the game and in the library's originals reads and writes back byte for byte."),
        ("--sound-container", "--sound-container <file.pck> <bank id hex> <object id hex>", "Diagnostic: the sound a sound-pack line would copy for that target (looks inside nested containers)."),
        ("--build-sound", "--build-sound <pack.mhsfx> <original.pck> <out.pck>", "Test: patch a copy of a sound package with one sound pack (never into the game folder)."),
        ("--add-original", "--add-original <file.tfc>", "Keep a clean copy of a texture cache (e.g. Icons.tfc) as its original: must carry the stock date (2024-03-14) and the game file's size. Apply then keeps the live one original."),
        ("--extract-texture", "--extract-texture <icons|achievements|store> <texture> <out.dds|out.png>", "Save a stock icon / achievement / store image (from the originals) as .dds, or as .png."),
        ("--extract-strings", "--extract-strings <lang> <out.json>", "Save every original string of a language (eng, deu, …) as .json in the mod format."),
        ("--compare-textures", "--compare-textures <a.upk> <b.upk> [<cache folder a> <cache folder b>]", "Every Texture2D in two packages: same size, format and best-mip pixels? (Checks a rebuild against another tool's.)"),
        ("--make-signing-key", "--make-signing-key <private key .pem>", "Make the release signing key pair: the private key to a file (never overwritten), the public key printed (it goes into ReleaseSigning.cs)."),
        ("--sign-file", "--sign-file <file> <private key .pem>", "Sign a release file: writes <file>.sig, checked against the key built into the app. Updates install only with a valid signature."),
        ("--check-update", "--check-update", "Look for a newer version (GitHub releases of leeper48/MHO-UPK-Tools, tag extmm-v<version>)."),
        ("--update", "--update", "Download, verify (SHA-256) and install a newer version over this one (data\\ is never touched); restart afterwards."),
        ("--make-checksums", "--make-checksums <clean CookedPCConsole> <out.json> [--compare <list.json>]", "Make the stock checksum list (CRC-32 of every .upk) from a clean copy of the game's packages; checks each has the stock traits (date, compressed) and compares with another list. Reads the folder only."),
        ("--material-probe", "--material-probe <mod | package.upk> [png folder]", "Each section's material for a mod's meshes (parent, switches, parameters, maps); with a folder, the maps as PNG and the spec map's channels one by one. Changes nothing."),
        ("--costume-move", "--costume-move <mod> [target costume] [--from <package>] [--build <folder>] [--create | --update-copy]", "Plan moving a costume mod onto another costume of the same hero: the package renames, icons and costume name that would move. Without a target, lists the hero's costumes. --build writes the moved package(s) to a folder (not the game's), verified; --create adds the moved costume as a new disabled mod; --update-copy rebuilds an existing moved copy in place. Changes nothing in the game."),
        ("--manual", "--manual <out.html>", "Save the manual (what Help / F1 shows) as one .html file."),
        ("--mesh-bones", "--mesh-bones <package.upk> ...", "List each skeletal mesh in the packages with its bone names. Changes nothing."),
        ("--foot-check", "--foot-check <mod> [animation name part...]", "How high a mod's mesh stands (lowest point, pelvis) at rest and over its animations: for feet below / above the ground after a move. Changes nothing."),
        ("--mod-props", "--mod-props <mod>", "For each of a mod's meshes, the props (weapons) the 3D preview shows with it and the bones they're held on. Changes nothing."),
        ("--fx-dump", "--fx-dump <package.upk> [system name part]", "Every particle system's emitters (kind, material, alignment, sub-images, timing, spawn, modules with values). Changes nothing."),
        ("--fx-sim", "--fx-sim <package.upk> <system name part> [seconds]", "Plays a particle system off screen and prints its live particles over time. Changes nothing."),
        ("--power-anims", "--power-anims <hero> [animation name part]", "A hero's animations and the powers that play them (from the hero's power packages). Changes nothing."),
        ("--mesh-sockets", "--mesh-sockets <package.upk>", "Each skeletal mesh in a package with its sockets (where powers attach their effects). Changes nothing."),
        ("--export-diff", "--export-diff <a.upk> <b.upk>", "The exports whose data or path differ between two packages (same export order), with where. Changes nothing."),
        ("--proto", "--proto <prototype path>", "A prototype's own fields as the game data has them (parents not merged). Changes nothing."),
        ("--power-fx", "--power-fx <power class> [hero] [mod]", "What the 3D preview plays for a power class: its particle effects, beams, decals, weapon slots, mesh emitters. Changes nothing."),
        ("--anim-power", "--anim-power <hero> <animation>", "The power a hero's animation belongs to, and what the 3D preview would play for it. Changes nothing."),
        ("--attach-census", "--attach-census [package.upk ...]", "Every property the game's power and hero packages use to show, hide or swap a character's props, counted, with an example of each. Changes nothing."),
        ("--props-audit", "--props-audit [hero ...]", "For every hero (or those given): props no power shows, power rules that name no prop, props without a mesh, power buttons without a name or icon. Changes nothing."),
        ("--power-recolor", "--power-recolor <UC__Power….upk> <hue> <saturation> <brightness> <out.upk>", "Build a power's stock package recolored (hue in degrees, saturation and brightness as factors) to a file outside the game folder."),
        ("--stock-path", "--stock-path <file.upk> ... [--clean <folder>]", "Where the app reads each game package as the game ships it (kept original, clean folder, live file or .bak, whichever is stock). Changes nothing."),
        ("--changed-files", "--changed-files", "Game packages changed by something other than this app (no mod names them, and Apply never wrote them). Changes nothing."),
        ("--keep-as-mod", "--keep-as-mod <mod name> <file.upk> ...", "Copies changed game files (see --changed-files) into a new mod, turned on and lowest priority, so Apply manages them. The game isn't changed."),
        ("--restore-original", "--restore-original <file.upk> ... [--dry-run]", "Puts the game's original back for changed game files (see --changed-files), as Apply would: .bak made from the original if missing, verified, undo history."),
        ("--fix-bak-dates", "--fix-bak-dates [--dry-run]", "Gives .bak files that are the game's original (checked against the stock checksums) but dated later the game's date, 2024-03-14. Only the date changes. A .bak that isn't the original is left alone."),
        ("--backup-check", "--backup-check [--clean <folder>] [--all]", "Which game packages aren't stock now, whether their .bak is truly stock, and where a stock copy is (kept originals, the clean folder). Changes nothing."),
        ("--hero-powers", "--hero-powers <hero>", "A hero's powers as the 3D preview's power buttons list them: name, icon, animations. Changes nothing."),
        ("--mesh-probe", "--mesh-probe <mod>", "List a mod's skeletal meshes and whether each loads with its textures (the preview's 3D view). Changes nothing."),
        ("--nexus-check", "--nexus-check", "Check the linked mods against Nexus (public data, no account) and list those with an update. Changes nothing."),
        ("--nexus-scan", "--nexus-scan", "List likely Nexus pages for every mod that isn't linked yet (what Find My Mods shows). Changes nothing."),
        ("--post", "--post <mod> [nexus|discord]", "Print the mod's release post: a Nexus description (BBCode) or a Discord message (Markdown)."),
        ("--auto-tags", "--auto-tags", "List every mod's automatic tags (characters, teams, costume, powers ...)."),
        ("--card-pictures", "--card-pictures", "Each mod's card picture: its own image (hero portrait, costume icon, store image, inventory icon), else the stock one it falls back to."),
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
            if (gui) Gui.Dialog.Show(msg, "MHO Extended Mod Manager"); else { AttachCliConsole(); Console.WriteLine(msg); }
            return 2;
        }
        Updater.CleanUp();   // the *.old files a self-update left behind
        Gui.Ui.UseDarkTheme();   // dark menus and title bars for every window (nothing to do without one)
        if (WindowCommand(args) is int windowResult) return windowResult;

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
        if (rest[0].Equals("--sound-container", StringComparison.OrdinalIgnoreCase) && rest.Count == 4)
        {
            Console.WriteLine(Akpk.ProbeTarget(rest[1], Convert.ToUInt32(rest[2], 16), Convert.ToUInt32(rest[3], 16)));
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
        if (rest[0].Equals("--test-nexus", StringComparison.OrdinalIgnoreCase)) return NexusTest.Run();
        if (rest[0].Equals("--make-checksums", StringComparison.OrdinalIgnoreCase) && rest.Count >= 3)
        {
            int ci = rest.FindIndex(x => x.Equals("--compare", StringComparison.OrdinalIgnoreCase));
            return ChecksumMaker.Run(rest[1], rest[2], ci > 0 && ci + 1 < rest.Count ? rest[ci + 1] : null);
        }
        if (rest[0].Equals("--make-signing-key", StringComparison.OrdinalIgnoreCase) && rest.Count >= 2)
        {
            // The release key pair (ReleaseSigning): the private key to a file (never overwritten); the public key printed.
            try { Console.WriteLine(ReleaseSigning.MakeKey(rest[1])); Console.WriteLine($"Private key written to {Path.GetFullPath(rest[1])}. Keep it safe and private; back it up."); return 0; }
            catch (IOException ex) { Console.WriteLine(ex.Message); return 1; }
        }
        if (rest[0].Equals("--sign-file", StringComparison.OrdinalIgnoreCase) && rest.Count >= 3)
        {
            // Signs a release file with the private key: <file>.sig beside it (ReleaseSigning).
            if (!File.Exists(rest[1]) || !File.Exists(rest[2])) { Console.WriteLine("--sign-file <file> <private key .pem>"); return 1; }
            Console.WriteLine($"{Path.GetFileName(rest[1])}.sig: {ReleaseSigning.Sign(rest[1], rest[2])}");
            return 0;
        }
        if (rest[0].Equals("--check-update", StringComparison.OrdinalIgnoreCase) || rest[0].Equals("--update", StringComparison.OrdinalIgnoreCase))
        {
            Updater.CleanUp();
            Updater.Release? r;
            try { r = Updater.Latest().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException or KeyNotFoundException or InvalidOperationException)
            { Console.WriteLine("Couldn't check: " + ex.Message); return 1; }
            if (r == null || r.Version <= Updater.Current) { Console.WriteLine($"Up to date ({Program.Version}); latest release: {r?.Version.ToString() ?? "none"}."); return 0; }
            Console.WriteLine($"Version {r.Version} is available ({r.PageUrl}).");
            if (rest[0].Equals("--check-update", StringComparison.OrdinalIgnoreCase)) return 0;
            string? why = Updater.Install(r, Console.WriteLine).GetAwaiter().GetResult();
            if (why != null) { Console.WriteLine("Not updated: " + why); return 1; }
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
        if (settings.ResolvedGameRoot(data) is string sgr && Settings.IsGameRoot(sgr))
            StockFiles.Init(new GameState(sgr, data), rest.IndexOf("--clean") is int cleanAt && cleanAt > 0 && cleanAt + 1 < rest.Count ? rest[cleanAt + 1] : settings.CleanGameFiles, Path.Combine(data, "originals"));
        Console.WriteLine($"library: {data} ({lib.Mods.Count} mods, {lib.Mods.Count(m => m.Enabled)} enabled)");

        string cmd = rest[0].ToLowerInvariant();
        if (PreviewCommand(cmd, rest, settings, data, lib) is int previewResult) return previewResult;
        if (CostumeCommand(cmd, rest, settings, data, lib) is int costumeResult) return costumeResult;
        if (TestCommand(cmd, rest, settings, data, lib) is int testResult) return testResult;
        switch (rest[0].ToLowerInvariant())
        {
            case "--stock-path":
            {
                // Read-only: where the app reads each named game package as the game ships it (StockFiles).
                string? sgr2 = settings.ResolvedGameRoot(data);
                if (sgr2 == null) { Console.WriteLine("game folder not found"); return 1; }
                foreach (string f in rest.Skip(1).Where(x => x.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"  {f}: {StockFiles.For(Settings.Cooked(sgr2), f)}");
                return 0;
            }
            case "--changed-files":
            case "--keep-as-mod":
            case "--restore-original":
            {
                // GameFiles: game packages changed outside this app; list them, keep some as a mod, or restore the originals.
                string? cgr = settings.ResolvedGameRoot(data);
                if (cgr == null || !Settings.IsGameRoot(cgr)) { Console.WriteLine("game folder not found"); return 1; }
                var cgame = new GameState(cgr, data);
                var corig = new Originals(lib.DataFolder, cgame);
                var changed = ApplyCheck.Unexpected(lib, cgame, corig);
                string ccmd = rest[0].ToLowerInvariant();
                if (ccmd == "--changed-files")
                {
                    foreach (var (f, clean) in changed) Console.WriteLine($"  {f}{(clean ? "" : "  (no clean original found)")}");
                    Console.WriteLine($"{changed.Count} game file(s) changed outside this app.");
                    return 0;
                }
                bool cdry = rest.Contains("--dry-run");
                var cargs = rest.Skip(1).Where(a => a != "--dry-run").ToList();
                string? kname = ccmd == "--keep-as-mod" && cargs.Count > 0 ? cargs[0] : null;
                var picked = (ccmd == "--keep-as-mod" ? cargs.Skip(1) : cargs).ToList();
                var unknown = picked.Where(f => !changed.Any(c => c.File.Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
                if (picked.Count == 0 || unknown.Count > 0 || ccmd == "--keep-as-mod" && kname == null)
                {
                    Console.WriteLine(ccmd == "--keep-as-mod" ? "--keep-as-mod <mod name> <file.upk> ..." : "--restore-original <file.upk> ... [--dry-run]");
                    if (unknown.Count > 0) Console.WriteLine("Not changed outside this app: " + string.Join(", ", unknown));
                    return 1;
                }
                if (ccmd == "--keep-as-mod")
                {
                    string? why = GameFiles.KeepAsMod(lib, cgame, picked, kname!);
                    Console.WriteLine(why ?? $"Kept {picked.Count} file(s) as the mod \"{kname}\" (turned on, lowest priority).");
                    return why == null ? 0 : 1;
                }
                if (cdry) { foreach (string f in picked) Console.WriteLine($"  {f}: would be restored from {corig.Find(f) ?? "(no clean original)"}"); return 0; }
                return GameFiles.Restore(cgame, corig, lib.DataFolder, picked) ? 0 : 1;
            }
            case "--fix-bak-dates":
            {
                // BackupCheck.FixDates: .bak files that are the original but dated later get the game's date (content unchanged).
                string? fgr = settings.ResolvedGameRoot(data);
                if (fgr == null || !Settings.IsGameRoot(fgr)) { Console.WriteLine("game folder not found"); return 1; }
                bool fdry = rest.Contains("--dry-run");
                var fixedBaks = BackupCheck.FixDates(new GameState(fgr, data), fdry);
                fixedBaks.ForEach(n => Console.WriteLine("  " + n));
                Console.WriteLine($"{fixedBaks.Count} .bak file(s) {(fdry ? "would get" : "got")} the game's date (2024-03-14); contents unchanged.");
                return 0;
            }
            case "--backup-check":
            {
                // Read-only: BackupCheck (--clean <folder> to use a clean folder other than the setting; --all: every changed package).
                string? bgr = settings.ResolvedGameRoot(data);
                if (bgr == null || !Settings.IsGameRoot(bgr)) { Console.WriteLine("game folder not found"); return 1; }
                var bgame = new GameState(bgr, data);
                int ci = rest.IndexOf("--clean");
                string? clean = ci > 0 && ci + 1 < rest.Count ? rest[ci + 1] : settings.CleanGameFiles;
                var (blines, bsum) = BackupCheck.Run(bgame, new Originals(lib.DataFolder, bgame), clean, rest.Contains("--all"));
                blines.ForEach(l => Console.WriteLine("  " + l));
                Console.WriteLine(bsum);
                return 0;
            }
            case "--post":
            {
                // --post <mod> [nexus|discord]: the mod's release post (what Create Post fills in).
                if (rest.Count < 2 || lib.Find(rest[1]) is not Mod pm) { Console.WriteLine("--post <mod> [nexus|discord]"); return 1; }
                var src = PostWriter.From(pm);
                if (rest.Count > 3 && rest[2].Equals("images", StringComparison.OrdinalIgnoreCase))
                {
                    var files = PostWriter.SaveImages(src, pm.Folder, rest[3]);
                    files.ForEach(Console.WriteLine);
                    Console.WriteLine($"{files.Count} image(s) saved.");
                    return files.Count > 0 ? 0 : 1;
                }
                bool discord = rest.Count > 2 && rest[2].Equals("discord", StringComparison.OrdinalIgnoreCase);
                string text = discord ? PostWriter.Discord(src) : PostWriter.Nexus(src);
                Console.WriteLine(text);
                Console.WriteLine($"--- {text.Length} characters");
                return 0;
            }
            case "--string-usage":
            {
                // --string-usage <text or id>: what uses the matching English strings (the editor's Used By column).
                string? gr = settings.ResolvedGameRoot(data);
                if (rest.Count < 2 || gr == null) { Console.WriteLine("--string-usage <text or id> (needs the game folder)"); return 1; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var usage = StringUsage.Load(gr);
                if (usage == null) { Console.WriteLine("Can't read Data\\Game\\Calligraphy.sip."); return 1; }
                Console.WriteLine($"{usage.Count:N0} string ids used by the game's data ({sw.Elapsed.TotalSeconds:0.0}s).");
                var cat = new StockCatalog(lib, new GameState(gr, data));
                string q = rest[1];
                foreach (var (file, id, text) in cat.Strings("eng").Where(x => x.Id.ToString() == q || x.Text.Equals(q, StringComparison.OrdinalIgnoreCase)).OrderBy(x => StringUsage.Rank(usage.For(x.Id))))
                {
                    Console.WriteLine($"{id}  \"{text}\"");
                    var u = usage.For(id);
                    if (u.Count == 0) Console.WriteLine("    (nothing in the game's data uses it)");
                    foreach (var x in u.Take(5)) Console.WriteLine("    " + StringUsage.Describe(x));
                    if (u.Count > 5) Console.WriteLine($"    … {u.Count - 5} more");
                }
                return 0;
            }
            case "--download-counts":
            {
                // Read-only: every release's GitHub download counts (downloads, not people; the zip includes in-app updates).
                var rows = Updater.DownloadCounts().GetAwaiter().GetResult();
                Console.WriteLine($"{"release",-16} {"published",-11} {"total",6} {"zip",5} {"setup",6}");
                foreach (var r in rows) Console.WriteLine($"{r.Tag,-16} {(r.Published == DateTime.MinValue ? "" : r.Published.ToString("yyyy-MM-dd")),-11} {r.Total,6} {r.Zip,5} {r.Setup,6}");
                Console.WriteLine($"all releases: {rows.Sum(r => r.Total)} (zip {rows.Sum(r => r.Zip)}, Setup.exe {rows.Sum(r => r.Setup)})");
                return 0;
            }
            case "--nexus-check":
            {
                // Read-only: checks the linked mods against Nexus's public data (no key) and prints what has an update;
                // the library's nexus_cache.json isn't written.
                var cache = new NexusCache();
                var (n, problems) = NexusUpdates.Check(lib, cache).GetAwaiter().GetResult();
                Console.WriteLine($"Checked {n} linked mod(s) on Nexus (public data, no key).");
                foreach (var p in problems) Console.WriteLine("  problem: " + p);
                foreach (var m in lib.Mods.Where(m => m.NexusModId != null).OrderBy(m => m.Priority))
                    if (NexusUpdates.UpdateFor(m, cache) is string v) Console.WriteLine($"  update: {m.Name} → v{v.TrimStart('v', 'V')} (#{m.NexusModId})");
                return 0;
            }
            case "--nexus-scan":
            {
                // --nexus-scan: likely Nexus pages for every unlinked mod (the Find My Mods on Nexus window), read only.
                var all = NexusMatch.AllMods().GetAwaiter().GetResult();
                Console.WriteLine($"{all.Count} mods on Nexus.");
                int sure = 0, some = 0, none = 0;
                foreach (var m in lib.Mods.Where(x => x.NexusModId == null))
                {
                    var c = NexusMatch.Candidates(m, all, 3);
                    if (c.Count == 0) { none++; Console.WriteLine($"  --  {m.Name}  (by {m.Manifest.Author ?? "?"}): no match"); continue; }
                    bool ok = NexusMatch.Confident(c); if (ok) sure++; else some++;
                    Console.WriteLine($"  {(ok ? "OK" : "? ")}  {m.Name}  (by {m.Manifest.Author ?? "?"})");
                    foreach (var x in c) Console.WriteLine($"        {x.Score:0.00}  #{x.Mod.ModId} {x.Mod.Name}  (by {x.Mod.Author}{(x.Mod.Uploader != x.Mod.Author && x.Mod.Uploader.Length > 0 ? " / " + x.Mod.Uploader : "")}, v{x.Mod.Version})");
                }
                Console.WriteLine($"{sure} confident, {some} to choose, {none} without a match.");
                return 0;
            }
            case "--auto-tags":
                foreach (var m in lib.Mods) Console.WriteLine($"{m.Name}: {string.Join(", ", m.AutoTags)}");
                return 0;
            case "--card-pictures":
            {
                // Each mod's card picture: its own image (herohor / costume / store / inventory), else the stock one.
                string? cgr = settings.ResolvedGameRoot(data);
                var cat = cgr != null && Directory.Exists(Settings.Cooked(cgr)) ? new StockCatalog(lib, new GameState(cgr, data)) : null;
                foreach (var m in lib.Mods)
                    Console.WriteLine($"{m.Name}: " + (m.CostumeIconFile() is string own ? "own " + Path.GetFileName(own) : "stock " + (cat?.DefaultIconFor(m) ?? "(none)")) +
                        (m.Manifest.StoreReplacements.Count > 0 ? "" : "  ·  store: stock " + (cat?.DefaultStoreFor(m) ?? "(none)")));
                return 0;
            }
            case "--list":
                return List(lib);
            case "--conflicts":
                return Conflicts(lib);
            case "--check":
                return Check(lib);
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
                bool replace = rest.Any(a => a.Equals("--replace", StringComparison.OrdinalIgnoreCase));
                try { done = ModInstaller.Install(rest[1], lib, log, replace ? (_, _) => true : null); }
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
                bool dry = rest.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
                string? why = ModInstaller.Remove(m, lib, gr != null && Settings.IsGameRoot(gr) ? new GameState(gr, data) : null, dry);
                Console.WriteLine(why ?? (dry ? $"{m.Name} can be removed (dry run: nothing changed)." : $"Removed {m.Name} (its folder is in the Recycle Bin)."));
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
                string? why = cat.ExportImage(Applier.IconPackages[k].File, rest[2], rest[3]);
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
            case "--apply":
            {
                if (IsOldManager(data)) { Console.WriteLine(OldManagerRefusal); return 1; }
                string? gr = settings.ResolvedGameRoot(data);
                if (gr == null || !Directory.Exists(Settings.Cooked(gr))) { Console.WriteLine("Game folder not found. Pass --game <Marvel Heroes folder>."); return 1; }
                var game = new GameState(gr, data);
                if (!game.HasStockList) { Console.WriteLine("No stock checksum list in the library; can't verify originals."); return 1; }
                var originals = new Originals(data, game);
                var plan = Applier.MakePlan(lib, game, originals);
                Console.Write(ApplyCheck.Text(ApplyCheck.Run(lib, game, originals, plan), game));
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
