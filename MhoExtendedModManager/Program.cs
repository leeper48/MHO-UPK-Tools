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
        ("--check-update", "--check-update", "Look for a newer version (GitHub releases of leeper48/MHO-UPK-Tools, tag extmm-v<version>)."),
        ("--update", "--update", "Download, verify (SHA-256) and install a newer version over this one (data\\ is never touched); restart afterwards."),
        ("--make-checksums", "--make-checksums <clean CookedPCConsole> <out.json> [--compare <list.json>]", "Make the stock checksum list (CRC-32 of every .upk) from a clean copy of the game's packages; checks each has the stock traits (date, compressed) and compares with another list. Reads the folder only."),
        ("--material-probe", "--material-probe <mod | package.upk> [png folder]", "Each section's material for a mod's meshes (parent, switches, parameters, maps); with a folder, the maps as PNG and the spec map's channels one by one. Changes nothing."),
        ("--costume-move", "--costume-move <mod> [target costume] [--from <package>] [--build <folder>] [--create | --update-copy]", "Plan moving a costume mod onto another costume of the same hero: the package renames, icons and costume name that would move. Without a target, lists the hero's costumes. --build writes the moved package(s) to a folder (not the game's), verified; --create adds the moved costume as a new disabled mod; --update-copy rebuilds an existing moved copy in place. Changes nothing in the game."),
        ("--manual", "--manual <out.html>", "Save the manual (what Help / F1 shows) as one .html file."),
        ("--mesh-bones", "--mesh-bones <package.upk> ...", "List each skeletal mesh in the packages with its bone names. Changes nothing."),
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
        if (args.Length == 3 && args[0].Equals("--post-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check: Create Post for a library mod (read only), both tabs as PNGs.
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            string? d = Settings.LibraryData(Settings.Load().LibraryPath);
            if (d == null || ModLibrary.Load(d).Find(args[2]) is not Mod pm) return 1;
            Directory.CreateDirectory(args[1]);
            using var f = new Gui.PostForm(PostWriter.From(pm), pm.Folder, ModPost.Read(pm.Folder), (_, _, _) => { });   // snapshot only: nothing saved
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                foreach (int tab in new[] { 0, 1 })
                {
                    f.SelectTabForSnapshot(tab);
                    await Task.Delay(400);
                    using var b = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                    b.Save(Path.Combine(args[1], tab == 0 ? "post_nexus.png" : "post_discord.png"));
                }
                f.Close();
            });
            f.ShowDialog();
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--manual", StringComparison.OrdinalIgnoreCase))
        {
            // The manual as the Help window shows it (version and command reference filled in), saved as one .html.
            if (Gui.HelpForm.Render() is not string m) { Console.WriteLine("Manual/manual.html isn't next to the app"); return 1; }
            File.Copy(m, args[1], overwrite: true);
            Console.WriteLine("saved " + args[1]);
            return 0;
        }
        if (args.Length == 4 && args[0].Equals("--move-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Test: the Move to Another Costume window for a mod and target, as a PNG (the plan only; nothing is made).
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Gui.Ui.UseDarkTheme();
            var st = Settings.Load();
            var mlib = ModLibrary.Load(Settings.LibraryData(st.LibraryPath)!);
            string? mgr = st.ResolvedGameRoot(mlib.DataFolder);
            var mm = mlib.Find(args[1]);
            var all = mgr == null ? null : Costume.All(mgr);
            if (mm == null || mgr == null || all == null || CostumeMove.Single(mm, all) is not { } one) { Console.WriteLine("no single-costume mod / game data"); return 1; }
            var mgame = new GameState(mgr, mlib.DataFolder);
            // A target given as Hero/Costume (e.g. Storm/Modern) is another hero's: the cross-hero window.
            bool crossHero = args[2].Contains('/');
            var tgt = crossHero
                ? all.FirstOrDefault(c => c.Short.Replace(".prototype", "", StringComparison.OrdinalIgnoreCase).Equals(args[2], StringComparison.OrdinalIgnoreCase))
                : CostumeMove.Targets(one.Costume, all, mgame.Cooked).FirstOrDefault(t => t.Title.Equals(args[2], StringComparison.OrdinalIgnoreCase) || t.Class.EndsWith("_" + args[2], StringComparison.OrdinalIgnoreCase));
            if (tgt == null) { Console.WriteLine("no such target"); return 1; }
            var mcat = new StockCatalog(mlib, mgame);
            var plan = CostumeMove.Make(mm, one.File, one.Costume, tgt, all, mgame.Cooked, mcat);
            using var f = new Gui.MoveCostumeForm(mm, plan, mcat, mlib.Mods.Where(x => x.Manifest.UpkReplacements.Contains(tgt.Package, StringComparer.OrdinalIgnoreCase)).ToList(), crossHero)
                { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                await Task.Delay(400);
                using var b = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(args[3]);
                f.Close();
            });
            f.ShowDialog();
            return 0;
        }
        if (args.Length == 4 && args[0].Equals("--hero-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Test: the Another Hero picker for a mod, with a hero's costumes shown, as a PNG. --hero-snapshot <mod> <hero> <out.png>
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Gui.Ui.UseDarkTheme();
            var st = Settings.Load();
            var hlib = ModLibrary.Load(Settings.LibraryData(st.LibraryPath)!);
            string? hgr = st.ResolvedGameRoot(hlib.DataFolder);
            var hm = hlib.Find(args[1]);
            var all = hgr == null ? null : Costume.All(hgr);
            if (hm == null || hgr == null || all == null || CostumeMove.Single(hm, all) is not { } one) { Console.WriteLine("no single-costume mod / game data"); return 1; }
            var hgame = new GameState(hgr, hlib.DataFolder);
            using var f = new Gui.HeroPickerForm(hm, one.Costume, all, hgame.Cooked, new StockCatalog(hlib, hgame)) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                await Task.Delay(300);
                foreach (var b in Gui.HeroPickerForm.Tiles(f)) if (b.Text.Equals(args[2], StringComparison.OrdinalIgnoreCase)) { b.PerformClick(); break; }
                await Task.Delay(3000);
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args[3]);
                f.Close();
            });
            f.ShowDialog();
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--dialog-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Gui.Dialog.Snapshot(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--apply-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Gui.ApplyForm.Snapshot(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--nexus-scan-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check: Find My Mods on Nexus for the library's unlinked mods (the real list, or MHO_EXTMM_NEXUS_API's), to a PNG.
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var st = Settings.Load();
            var scanLib = ModLibrary.Load(st.LibraryPath);
            var all = NexusMatch.AllMods().GetAwaiter().GetResult();
            using var f = new Gui.NexusScanForm(scanLib.Mods.Where(m => m.NexusModId == null), all);
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                await Task.Delay(500);
                using var b = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(args[1]);
                f.Close();
            });
            f.ShowDialog();
            return 0;
        }
        if (args.Length >= 4 && args[0].Equals("--anim-render", StringComparison.OrdinalIgnoreCase))
        {
            // Test: a mod's first mesh posed at a few points of an animation, rendered by the 3D view to one PNG
            // (the window opens off-screen). --anim-render <mod> <animation name part> <out.png>
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var st = Settings.Load();
            var rlib = ModLibrary.Load(Settings.LibraryData(st.LibraryPath)!);
            var rm = rlib.Find(args[1]);
            string? rgr = st.ResolvedGameRoot(rlib.DataFolder);
            string? rc = rgr != null && Settings.IsGameRoot(rgr) ? Settings.Cooked(rgr) : null;
            // Optional 5th argument: which mesh (e.g. a cross-hero move's copied mesh), else the first.
            var mr = rm == null ? null : ModMeshes.List(rm).FirstOrDefault(x => args.Length < 5 || x.Name.Equals(args[4], StringComparison.OrdinalIgnoreCase));
            var ld = mr == null ? null : ModMeshes.Load(mr, rc, out _);
            if (rm == null || mr == null || ld == null) { Console.WriteLine("no mesh"); return 1; }
            var ar = ModAnimations.For(mr, ld.Bones, rm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(rm.Folder, f))), rc).FirstOrDefault(a => a.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase));
            var ba = ar == null ? null : ModAnimations.Load(ar);
            Console.WriteLine(ar == null ? "no such animation: rest pose" : $"{ar.Name} ({ar.Package})");
            var anim8 = new MeshAnimator(ld.Bones, ld.Positions, ld.Normals, ld.Influences, ld.Tangents);
            float frames = ba == null ? 0 : MeshAnimator.Span(ba).Frames;
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(420, 560), ShowInTaskbar = false };
            var v = new Gui.ModelView { Dock = DockStyle.Fill, Background = Gui.Ui.Card };
            f.Controls.Add(v);
            var shots = new List<Bitmap>();
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                v.ShowMesh(ld);
                foreach (float at in new[] { 0f, 0.33f, 0.66f, 1f })
                {
                    anim8.Pose(ba, frames * at);
                    v.UpdateGeometry(anim8);
                    await Task.Delay(200);
                    var b = new Bitmap(v.Width, v.Height); v.DrawToBitmap(b, new Rectangle(0, 0, v.Width, v.Height)); shots.Add(b);
                    Console.WriteLine($"  frame at {at:0.00}: {v.LastFrameMs:0} ms");
                }
                using var sheet = new Bitmap(shots.Sum(s => s.Width), shots.Max(s => s.Height));
                using (var g = Graphics.FromImage(sheet)) { int x = 0; foreach (var s in shots) { g.DrawImage(s, x, 0); x += s.Width; s.Dispose(); } }
                sheet.Save(args[3]);
                f.Close();
            });
            Application.Run(f);
            return 0;
        }
        if (args.Length >= 4 && args[0].Equals("--icon-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Test: the icon creator for a mod's texture (window off-screen), snapshot taken and saved with the window.
            // --icon-snapshot <dir> <mod> <texture> [animation part]
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var st = Settings.Load();
            var ilib = ModLibrary.Load(Settings.LibraryData(st.LibraryPath)!);
            var im = ilib.Find(args[2]);
            string? igr = st.ResolvedGameRoot(ilib.DataFolder);
            var igame = igr != null && Settings.IsGameRoot(igr) ? new GameState(igr, ilib.DataFolder) : null;
            var icat = igame != null ? new StockCatalog(ilib, igame) : null;
            string file = args[3].StartsWith("store_", StringComparison.OrdinalIgnoreCase) ? Applier.IconPackages[2].File : Applier.IconPackages[0].File;
            if (im == null || icat?.Size(file, args[3]) is not { } isz) { Console.WriteLine("no such mod, or the texture's size isn't known"); return 1; }
            Directory.CreateDirectory(args[1]);
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), Size = new Size(1300, 760), ShowInTaskbar = false, Font = Gui.Ui.Regular(9.5f) };
            var cv = new Gui.IconCreatorView(() => im.Manifest.UpkReplacements.Select(u => (u, Path.Combine(im.Folder, u))), igame!.Cooked);
            f.Controls.Add(cv);
            MhoPackageModifier.Gui.Theme.Apply(f, MhoPackageModifier.Gui.Palette.Dark);
            Gui.Ui.Restyle(f);
            var orig = icat.Preview(file, args[3]);
            string report = "";
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                PreviewViews.ForgetIcon("selftest:" + im.FolderName + "|" + args[3]);   // start from the kind's framing
                cv.SetTarget("selftest:" + im.FolderName, args[3], isz.W, isz.H, orig is { } o ? MhoPackageModifier.TextureDecode.ToBitmap(o.Bgra, o.W, o.H) : null);
                report = await cv.SelfTest(args[1], args.Length > 4 ? args[4] : null);
                f.Close();
            });
            Application.Run(f);
            Console.WriteLine(report);
            return report.StartsWith("ok") ? 0 : 1;
        }
        if (args.Length == 3 && args[0].Equals("--viewer-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check: the image viewer (off-screen) on a picture, fitted and at 8×, as PNGs. --viewer-snapshot <image> <dir>
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Directory.CreateDirectory(args[2]);
            using var img = Image.FromFile(args[1]);
            using var v = new Gui.ImageViewerForm(img, Path.GetFileName(args[1])) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000) };
            v.Shown += (_, _) => v.BeginInvoke(async () =>
            {
                await Task.Delay(300);
                void Shot(string n) { using var b = new Bitmap(v.Width, v.Height); v.DrawToBitmap(b, new Rectangle(0, 0, v.Width, v.Height)); b.Save(Path.Combine(args[2], n)); }
                Shot("viewer_fit.png");
                v.TestZoom(8); await Task.Delay(200); Shot("viewer_8x.png");
                v.Close();
            });
            Application.Run(v);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--update-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Layout check: the update window for a made-up release, rendered to a PNG.
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var r = new Updater.Release(new Version(9, 9, 9), "extmm-v9.9.9", "MHO Extended Mod Manager 9.9.9",
                string.Join("\n", "What's new", "- Tags travel with mods", "- Update in place", "- PNG export and import"), Updater.ReleasesPage, "", "", 0);
            using var f = new Gui.UpdateForm(r);
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                await Task.Delay(500);
                using var b = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(args[1]);
                f.Close();
            });
            Application.Run(f);
            return 0;
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
        if (args.Length == 2 && args[0].Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase))
        {
            // Test hook (needs MHO_EXTMM_HOME on a scratch library: it changes tags and on/off, then undoes them).
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) { Console.WriteLine("--ui-selftest needs MHO_EXTMM_HOME (a scratch library)."); return 1; }
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.UiSelfTest(args[1]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--tooltip-audit", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var main = new Gui.MainForm();
            int code = 0;
            main.Shown += (_, _) => main.BeginInvoke(async () => { code = await main.TooltipAudit(args[1]); main.Close(); });
            Application.Run(main);
            return code;
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
                var answer = Gui.Dialog.Show($"Your mod library is in AppData:\n{oldLib}\n\nThis version keeps everything next to the program instead:\n{Settings.Home}\n\nMove the library there now? (Yes is recommended. No starts the first-run setup; your AppData library is left alone.)",
                    "MHO Extended Mod Manager", MessageBoxButtons.YesNoCancel);
                if (answer == DialogResult.Cancel) return 0;
                if (answer == DialogResult.Yes)
                    try { Settings.MoveFromAppData(oldLib); Gui.Dialog.Show($"Moved. Your mods are now in\n{Settings.DefaultLibrary}", "MHO Extended Mod Manager"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Gui.Dialog.Show("The move didn't finish, and nothing was changed: " + ex.Message, "MHO Extended Mod Manager"); return 1; }
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
        if (args.Length == 3 && args[0].Equals("--preview-selftest", StringComparison.OrdinalIgnoreCase))
        {
            // The 3D view's controls, driven as a user would (a scratch library: MHO_EXTMM_HOME). --preview-selftest <dir> <mod>
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            var form = new Gui.MainForm();
            form.Shown += (_, _) => form.BeginInvoke(async () => { await form.PreviewSelfTest(args[1], args[2]); form.Close(); });
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
        Console.WriteLine($"library: {data} ({lib.Mods.Count} mods, {lib.Mods.Count(m => m.Enabled)} enabled)");

        switch (rest[0].ToLowerInvariant())
        {
            case "--test-images":
            {
                // Test: stock image → .png / .dds, .png → .dds (same size and format as the original), and a 2× image scaled.
                string? tgr = settings.ResolvedGameRoot(data);
                if (rest.Count < 2 || tgr == null || !Directory.Exists(Settings.Cooked(tgr))) { Console.WriteLine("--test-images <scratch folder> (needs the game folder)"); return 1; }
                var game = new GameState(tgr, data);
                string dir = rest[1];
                if (Path.GetFullPath(dir).Contains(@"\CookedPCConsole", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Refusing the game folder."); return 1; }
                Directory.CreateDirectory(dir);
                var cat = new StockCatalog(lib, game);
                int fails = 0;
                void Check(string what, bool ok) { if (!ok) fails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                foreach (var (pk, tex) in new[] { (Applier.IconPackages[2].File, "store_storm_classicblack"), (Applier.IconPackages[0].File, "costumestorm_classic") })
                {
                    var size = cat.Size(pk, tex);
                    var prev = cat.Preview(pk, tex);
                    if (size == null || prev == null) { Check($"{tex}: in the originals", false); continue; }
                    string png = Path.Combine(dir, tex + ".png"), dds = Path.Combine(dir, tex + ".dds"), back = Path.Combine(dir, tex + "_from_png.dds");
                    Check($"{tex}: saved as .png", cat.ExportImage(pk, tex, png) == null && File.Exists(png));
                    Check($"{tex}: saved as .dds", cat.ExportImage(pk, tex, dds) == null && File.Exists(dds));
                    string note = cat.ImageToDds(pk, tex, png, back);
                    var img = MhoPackageModifier.TextureImport.ParseDds(File.ReadAllBytes(back), out string? err);
                    bool same = img != null && img.Width == size.Value.W && img.Height == size.Value.H && size.Value.Format.Contains(img.FourCC, StringComparison.OrdinalIgnoreCase);
                    Check($"{tex}: .png → .dds {img?.Width}×{img?.Height} {img?.FourCC} like the original ({size.Value.W}×{size.Value.H} {size.Value.Format}): {note}", same);
                    // Pixels against the original (largest stored mip; the original may keep only a smaller one in the package).
                    var d = MhoPackageModifier.TextureDecode.ReadDds(back, out _);
                    var bgra = d is { } x ? MhoPackageModifier.TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
                    if (bgra != null && prev.Value.W == d!.Value.W && prev.Value.H == d.Value.H)
                    {
                        double se = 0; int n = 0;
                        for (int i = 0; i < bgra.Length; i += 4) if (prev.Value.Bgra[i + 3] > 128) for (int c = 0; c < 3; c++) { double e = bgra[i + c] - prev.Value.Bgra[i + c]; se += e * e; n++; }
                        double psnr = n == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / Math.Max(1e-9, se / n));
                        Check($"{tex}: round trip PSNR {psnr:0.0} dB (opaque pixels)", psnr > 30);
                    }
                    // A 2× image: scaled back to the original's size.
                    using (var big = new System.Drawing.Bitmap(png))
                    using (var twice = new System.Drawing.Bitmap(big, big.Width * 2, big.Height * 2)) twice.Save(Path.Combine(dir, tex + "_2x.png"));
                    string note2 = cat.ImageToDds(pk, tex, Path.Combine(dir, tex + "_2x.png"), Path.Combine(dir, tex + "_2x.dds"));
                    var img2 = MhoPackageModifier.TextureImport.ParseDds(File.ReadAllBytes(Path.Combine(dir, tex + "_2x.dds")), out _);
                    Check($"{tex}: 2× image scaled to the original's size ({note2})", img2 != null && img2.Width == size.Value.W && img2.Height == size.Value.H);
                }
                // The editor's costume filter against the real icon and store lists.
                var icons = cat.EntriesFor(Applier.IconPackages[0].File) ?? [];
                var stores = cat.EntriesFor(Applier.IconPackages[2].File) ?? [];
                foreach (string pkgName in new[] { "UC__MarvelPlayer_Storm_Classic_SF.upk", "UC__MarvelPlayer_Beast_Astonishing_SF.upk", "UC__MarvelPlayer_DoctorStrange_Classic_SF.upk",
                                                    "UC__MarvelPlayer_Spiderman_CivilWarMovie_SF.upk", "UC__MarvelPlayer_CaptainAmerica_Avengers_SF.upk", "UC__MarvelPlayer_Storm_SF.upk" })
                {
                    var cf = Gui.ModEditorView.CostumeFilter.FromPackage(pkgName)!;
                    var ic = icons.Where(e => cf.Matches(e.Name)).Select(e => e.Name).ToList();
                    var st = stores.Where(e => cf.Matches(e.Name)).Select(e => e.Name).ToList();
                    Console.WriteLine($"  {cf.Label,-28} icons {ic.Count,3}: {string.Join(", ", ic.Take(6))}{(ic.Count > 6 ? " …" : "")}");
                    Console.WriteLine($"  {"",-28} store {st.Count,3}: {string.Join(", ", st.Take(6))}{(st.Count > 6 ? " …" : "")}");
                }
                Console.WriteLine(fails == 0 ? "All image checks passed." : $"{fails} image check(s) FAILED.");
                return fails == 0 ? 0 : 1;
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
            case "--menu-place-test":
            {
                // The Settings menu on a two-monitor desktop (a user's: it opened on the other screen). No windows.
                var main = new Rectangle(0, 0, 1920, 1040);   // main monitor's work area; the second starts at x 1920
                var menuSize = new Size(300, 420);
                var cases = new (string What, Rectangle Button, Point Want)[]
                {
                    ("Settings at the right edge: right-aligned, on the main monitor", new Rectangle(1800, 40, 110, 30), new Point(1610, 70)),
                    ("room to the right: under the button's left edge", new Rectangle(100, 40, 110, 30), new Point(100, 70)),
                    ("near the bottom: above the button", new Rectangle(100, 900, 110, 30), new Point(100, 480)),
                };
                int bad = 0;
                foreach (var (what, b, want) in cases)
                {
                    var got = Gui.Ui.PlaceUnder(b, menuSize, main);
                    bool ok = got == want && got.X + menuSize.Width <= main.Right;
                    if (!ok) bad++;
                    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}: {got.X},{got.Y}");
                }
                return bad == 0 ? 0 : 1;
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
                var xsrc = CostumeMove.Single(xm, xall);
                if (xsrc is not { } one) { Console.WriteLine("not a single-costume mod"); return 1; }
                var xt = xall.FirstOrDefault(c => c.Short.Replace(".prototype", "", StringComparison.OrdinalIgnoreCase).Equals(rest[2], StringComparison.OrdinalIgnoreCase) || c.Class.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                if (xt == null) { Console.WriteLine($"no costume '{rest[2]}' (use Hero/Costume as in the prototype, e.g. Storm/ClassicBlack)"); return 1; }
                var xgame = new GameState(xgr, data);
                string? xstock = new Originals(lib.DataFolder, xgame).Find(xt.Package);
                if (xstock == null) { Console.WriteLine($"no stock copy of {xt.Package}"); return 1; }
                string modPkg = Path.Combine(xm.Folder, one.File);
                // The mod's main mesh: the one its costume's component uses (initialskeletalmesh), else its first.
                var mp = MhoPackageModifier.Package.Open(modPkg);
                string meshName = ModMeshes.List(xm).Where(r => r.Package.Equals(one.File, StringComparison.OrdinalIgnoreCase)).Select(r => r.Name).FirstOrDefault() ?? "";
                string heroBase = "UC__MarvelPlayer_" + one.Costume.Class.Split('_')[1] + "_SF.upk";
                string? baseHero = File.Exists(Path.Combine(xm.Folder, heroBase)) ? Path.Combine(xm.Folder, heroBase) : File.Exists(Path.Combine(xgame.Cooked, heroBase)) ? Path.Combine(xgame.Cooked, heroBase) : null;
                Console.WriteLine($"{one.Costume.Short} ({meshName}) → {xt.Short} ({xt.Class}); target stock {xstock}; source hero base {baseHero ?? "none"}");
                var xlog = new List<string>();
                try
                {
                    byte[] built = CrossMove.Build(modPkg, meshName, xstock, xt.Class, baseHero, xlog, sounds: false, sourceClass: one.Costume.Class);
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
            case "--mesh-copy-test":
            {
                // Test (phase 1): copy a skeletal mesh into another package (renamed), write it to a scratch file, read it back.
                // --mesh-copy-test <source.upk> <mesh> <target.upk> <out.upk>   (never the game folder)
                if (rest.Count < 5) { Console.WriteLine("--mesh-copy-test <source.upk> <mesh> <target.upk> <out.upk>"); return 1; }
                var probs = MeshCopy.Test(rest[1], rest[2], rest[3], rest[4]);
                Console.WriteLine(probs.Count == 0 ? $"PASS: {rest[2]} copied and read back identical (bones, geometry, material paths)" : "FAIL: " + string.Join("; ", probs));
                return probs.Count == 0 ? 0 : 1;
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
                            string stem = Path.Combine(outDir, $"s{sec}_{k}");
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
                        foreach (var s in sets.Take(8)) Console.WriteLine($"  {ap.GetExportName(s.ExportIndex)}: {s.Sequences.Count} sequences, {s.TrackBoneNames.Count} bones");
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
