using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>Self-tests and checks run from the command line (scratch libraries where they write). Null when <paramref name="cmd"/> isn't one of them.</summary>
    static int? TestCommand(string cmd, List<string> rest, Settings settings, string data, ModLibrary lib)
    {
        switch (cmd)
        {
            case "--strip-test":
            {
                // --strip-test <package.upk> <out dir> <costume class> <prop class> <slot,...>: copies without the added prop,
                // without the animation swaps, and without both (finding which part of a costume crashes other players' clients)
                if (rest.Count < 6) { Console.WriteLine("--strip-test <package.upk> <out dir> <costume class> <prop class> <slot,...>"); return 1; }
                byte[] src = File.ReadAllBytes(rest[1]);
                Directory.CreateDirectory(rest[2]);
                var slog = new List<string>();
                byte[] noProp = PowerBorrow.RemoveProp(src, rest[4], slog);
                byte[]? NoSwap(byte[] b) => AnimSwap.Unswap(MhoPackageModifier.Package.FromBytes(b), rest[3], rest[5].Split(','), slog);
                byte[] noSwap = NoSwap(src) ?? throw new InvalidDataException("no swaps undone");
                byte[] neither = NoSwap(noProp) ?? throw new InvalidDataException("no swaps undone (no prop)");
                string name = Path.GetFileName(rest[1]);
                foreach (var (dir, b) in new[] { ("noprop", noProp), ("noswap", noSwap), ("neither", neither) })
                {
                    Directory.CreateDirectory(Path.Combine(rest[2], dir));
                    File.WriteAllBytes(Path.Combine(rest[2], dir, name), b);
                    Console.WriteLine($"{dir}: {b.Length:N0} bytes");
                }
                slog.ForEach(x => Console.WriteLine("  " + x));
                return 0;
            }
            case "--held-test":
            {
                // --held-test <package.upk> <out.upk> <prop class> <bone>: a copy with the prop held on that bone (SetHeldBones)
                if (rest.Count < 5) { Console.WriteLine("--held-test <package.upk> <out.upk> <prop class> <bone>"); return 1; }
                var hlog = new List<string>();
                File.WriteAllBytes(rest[2], PowerBorrow.SetHeldBones(File.ReadAllBytes(rest[1]), rest[3], [rest[4]], hlog));
                hlog.ForEach(x => Console.WriteLine("  " + x));
                return 0;
            }
            case "--snapshot-take":
            {
                // --snapshot-take [name]: a snapshot of the library now (Settings → Snapshots), kept until deleted. Only reads the mods.
                var sgame = settings.ResolvedGameRoot(data) is string sr && Settings.IsGameRoot(sr) ? new GameState(sr, data) : null;
                string? file = Snapshots.Take(lib, sgame, "manual", rest.Count > 1 ? string.Join(" ", rest.Skip(1)) : "");
                Console.WriteLine(file != null ? $"snapshot: {file} ({new FileInfo(file).Length:N0} bytes, {lib.Mods.Count} mods)" : "the snapshot couldn't be saved");
                return file != null ? 0 : 1;
            }
            case "--snapshot-test":
            {
                // --snapshot-test <scratch folder> [mod ...]: Snapshots and bundles on a throwaway library made in the scratch folder
                // from copies of the library's smallest mods (or those named): take, change, compare, restore, bundle, restore the
                // bundle. Nothing outside the scratch folder changes. Exit 0 when every check passes.
                if (rest.Count < 2) { Console.WriteLine("--snapshot-test <scratch folder> [mod ...]"); return 1; }
                return SnapshotTest(lib, rest[1], rest.Skip(2).ToList());
            }
            case "--history-test":
            {
                // Test (scratch folder only): a recorded write keeps no copy, the app still knows its own write, and
                // DeleteSnapshots removes an earlier version's copies but keeps the records. --history-test <scratch dir>
                if (rest.Count < 2) { Console.WriteLine("--history-test <scratch dir>"); return 1; }
                string dir = Path.GetFullPath(rest[1]);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                string? keepRoot = MhoPackageModifier.History.RootOverride;
                MhoPackageModifier.History.RootOverride = Path.Combine(dir, "history");
                int fails = 0;
                void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}"); if (!ok) fails++; }
                try
                {
                    string live = Path.Combine(dir, "Test_SF.upk");
                    File.WriteAllBytes(live, [1, 2, 3]);
                    byte[] next = [4, 5, 6, 7];
                    MhoPackageModifier.History.Record(live, next);
                    File.WriteAllBytes(live, next);
                    var folder = Directory.GetDirectories(Path.Combine(dir, "history")).Single();
                    Check("a write leaves no copy of the previous version", !Directory.EnumerateFiles(folder, "*.upk").Any());
                    Check("the record (history.txt) is written", File.Exists(Path.Combine(folder, "history.txt")));
                    Check("the app still knows the file as its own write", MhoPackageModifier.History.IsLastWritten(live));
                    File.WriteAllBytes(live, [9, 9]);
                    Check("a change by something else is still noticed", !MhoPackageModifier.History.IsLastWritten(live));
                    File.WriteAllBytes(live, next);
                    // an earlier version's copies, and a file that isn't one
                    File.WriteAllBytes(Path.Combine(folder, "00001.upk"), new byte[1000]);
                    File.WriteAllBytes(Path.Combine(folder, "00002.upk"), new byte[500]);
                    File.WriteAllBytes(Path.Combine(folder, "notes.upk"), [1]);
                    var (n, bytes) = MhoPackageModifier.History.DeleteSnapshots();
                    Check($"cleanup removes the numbered copies ({n} files, {bytes} bytes)", n == 2 && bytes == 1500 && !File.Exists(Path.Combine(folder, "00001.upk")));
                    Check("cleanup keeps the record and other files", File.Exists(Path.Combine(folder, "history.txt")) && File.Exists(Path.Combine(folder, "notes.upk")));
                    Check("after cleanup the app still knows its own write", MhoPackageModifier.History.IsLastWritten(live));
                }
                finally { MhoPackageModifier.History.RootOverride = keepRoot; }
                Console.WriteLine(fails == 0 ? "all passed" : $"{fails} failed");
                return fails == 0 ? 0 : 1;
            }
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
            case "--blueprint-check":
            {
                // Read-only self-check of the Calligraphy blueprint reader: which base types carry a subtype id.
                string? bgr = settings.ResolvedGameRoot(data);
                if (bgr == null) { Console.WriteLine("game folder not set"); return 1; }
                foreach (string set in new[] { "ACPR", "ACPRT" })
                {
                    var (exact, off, failed, examples) = StringUsage.CheckBlueprints(bgr, set);
                    Console.WriteLine($"subtype for {set}: {exact} blueprints read exactly to the end, {off} not, {failed} failed");
                    foreach (string e in examples) Console.WriteLine("  " + e);
                }
                return 0;
            }
            case "--dropdown-test":
            {
                // Self-test of the app's drop-down list (nothing is shown on screen); writes droplist.png into <dir>.
                if (rest.Count < 2) { Console.WriteLine("--dropdown-test <dir>"); return 1; }
                Directory.CreateDirectory(rest[1]);
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Gui.Ui.UseDarkTheme();
                var dp = Gui.DropList.Test(rest[1]);
                Console.WriteLine(dp.Count == 0 ? "PASS: list built, current item marked, search narrows it, Enter picks" : "FAIL: " + string.Join("; ", dp));
                return dp.Count == 0 ? 0 : 1;
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
            case "--power-borrow-test":
            {
                // --power-borrow-test <mod> <target power .prototype> <donor power .prototype> [--keep-timing] [--color]
                // (scratch MHO_EXTMM_HOME only: it saves the mod three times): a borrow on (with a color on the same power),
                // saved; saved again unchanged (nothing stacks); taken off, saved (the mod as it was)
                string? bhome = Environment.GetEnvironmentVariable("MHO_EXTMM_HOME");
                if (bhome == null || Path.GetFullPath(bhome).Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder (a copy of a library), never the real one."); return 2; }
                var bm = rest.Count > 3 ? lib.Find(rest[1]) : null;
                string? bgr2 = settings.ResolvedGameRoot(data);
                if (bm == null || bgr2 == null) { Console.WriteLine("--power-borrow-test <mod> <target power> <donor power> [--keep-timing] [--color]"); return 1; }
                var bgame = new GameState(bgr2, data);
                Fx.GameData? bdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(bgr2, "Data", "Game", "Calligraphy.sip")));
                string tp = rest[2].Replace('/', '\\'), dp = rest[3].Replace('/', '\\');
                bool keep = rest.Contains("--keep-timing"), color = rest.Contains("--color");
                int bfails = 0;
                void BCheck(string what, bool ok) { if (!ok) bfails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                string model = Path.Combine(lib.DataFolder, "model-work-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(model);
                var blog = new List<string>();
                int start = bm.Manifest.UpkReplacements.Count;
                string? Save(Mod m, Action<ModDraft> change, out ModLibrary l2)
                {
                    var d = ModDraft.From(m);
                    change(d);
                    string work = Path.Combine(lib.DataFolder, "power-borrow-" + Guid.NewGuid().ToString("N")[..8]);
                    var built = PowerBorrow.Rebuild(d, m, lib, bgame, bdb!, work, model, blog);
                    string? cw = PowerColorBuild.Apply(d, m, lib, bgame, ref bdb, blog, () => model, built.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
                    string? saved = ModWriter.Save(lib, d, m, out string? err);
                    foreach (string? w in new[] { work, cw }) if (w != null) try { Directory.Delete(w, true); } catch (IOException) { }
                    if (err != null) Console.WriteLine("    save: " + err);
                    l2 = ModLibrary.Load(data);
                    return saved;
                }
                // the target power package's state: borrowed components listed / beams drawing the donor's; recolor groups
                (bool Borrowed, int Recolor) State(Mod m)
                {
                    var e = m.Manifest.PowerBorrows?.FirstOrDefault();
                    string? f = e?.Packages.FirstOrDefault(x => x.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) || x.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase));
                    if (f == null || !File.Exists(Path.Combine(m.Folder, f))) return (false, 0);
                    var pk = MhoPackageModifier.Package.Open(Path.Combine(m.Folder, f));
                    bool b = keep ? pk.Exports.Any(x => x.ObjectName.StartsWith("vfx_torch", StringComparison.OrdinalIgnoreCase) || x.ObjectName.Contains("borrowed_", StringComparison.OrdinalIgnoreCase) || pk.PathOf(x).Contains("humantorch", StringComparison.OrdinalIgnoreCase))
                                  : pk.Exports.Any(x => x.ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase));
                    return (b, pk.Exports.Count(x => x.ObjectName.EndsWith("_recolor_fx", StringComparison.OrdinalIgnoreCase)));
                }
                // 1. on
                string? s1 = Save(bm, d =>
                {
                    d.PowerBorrows.RemoveAll(x => x.Power.Equals(tp, StringComparison.OrdinalIgnoreCase));
                    d.PowerBorrows.Add(new PowerBorrowEntry { Power = tp, Name = Path.GetFileNameWithoutExtension(tp), Donor = dp, DonorName = Path.GetFileNameWithoutExtension(dp), KeepTiming = keep,
                        // --pin <prop class>: that prop's Pivot on the Hand
                        Props = rest.IndexOf("--pin") is int pi && pi >= 0 && pi + 1 < rest.Count ? [new PropFit { Class = rest[pi + 1], PinPivot = true }] : null });
                    if (color) d.PowerColors.Add(new PowerColorEntry { Power = tp, Name = Path.GetFileNameWithoutExtension(tp), Hue = 120 });
                }, out var l1);
                blog.ForEach(x => Console.WriteLine("    " + x)); blog.Clear();
                var m1 = l1.Find(rest[1])!;
                var e1 = m1.Manifest.PowerBorrows?.FirstOrDefault();
                var st1 = State(m1);
                BCheck($"borrow saved ({e1?.Packages.Count} package(s): {string.Join(", ", e1?.Packages ?? [])})", s1 != null && e1 != null && e1.Packages.Count > 0);
                BCheck("every built package in the mod", e1 != null && e1.Packages.All(f => m1.Manifest.UpkReplacements.Contains(f, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(m1.Folder, f))));
                BCheck($"the power package holds the borrowed look{(color ? $" and the color ({st1.Recolor} recolor group(s))" : "")}", st1.Borrowed && (!color || st1.Recolor > 0));
                if (rest.Contains("--keep")) { try { Directory.Delete(model, true); } catch (IOException) { } Console.WriteLine(bfails == 0 ? "PASS (borrow kept)" : $"{bfails} FAILED"); return bfails == 0 ? 0 : 1; }
                // 2. saved again, unchanged
                string? s2 = Save(m1, _ => { }, out var l2b);
                blog.Clear();
                var m2 = l2b.Find(rest[1])!;
                var st2 = State(m2);
                BCheck($"saved again unchanged: still borrowed, {st2.Recolor} recolor group(s) (was {st1.Recolor})", s2 != null && st2.Borrowed && st2.Recolor == st1.Recolor);
                // 3. off
                string? s3 = Save(m2, d => { d.PowerBorrows.Clear(); foreach (var c in d.PowerColors) { c.Hue = 0; c.Saturation = 1; c.Brightness = 1; } }, out var l3);
                blog.ForEach(x => Console.WriteLine("    " + x));
                var m3 = l3.Find(rest[1])!;
                BCheck($"borrow off: no PowerBorrows, packages back ({m3.Manifest.UpkReplacements.Count} = {start})", s3 != null && m3.Manifest.PowerBorrows == null && m3.Manifest.UpkReplacements.Count == start);
                try { Directory.Delete(model, true); } catch (IOException) { }
                Console.WriteLine(bfails == 0 ? "PASS" : $"{bfails} FAILED");
                return bfails == 0 ? 0 : 1;
            }
            case "--power-color-test":
            {
                // Test (a scratch library only: MHO_EXTMM_HOME): a power color saved into a mod (PowerColorBuild.Apply + ModWriter.Save),
                // read back, then taken off again. --power-color-test <mod> <power name>
                string? home = Environment.GetEnvironmentVariable("MHO_EXTMM_HOME");
                if (home == null || Path.GetFullPath(home).StartsWith(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")), StringComparison.OrdinalIgnoreCase)
                    || Path.GetFullPath(home).Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder (a copy of a library), never the real one."); return 2; }
                var pm = rest.Count > 2 ? lib.Find(rest[1]) : null;
                string? pgr = settings.ResolvedGameRoot(data);
                if (pm == null || pgr == null) { Console.WriteLine("--power-color-test <mod> <power name>"); return 1; }
                var pgame = new GameState(pgr, data);
                Fx.GameData? pdb = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(pgr, "Data", "Game", "Calligraphy.sip")));
                string hero = pm.Manifest.UpkReplacements.Select(f => f.Split('_', StringSplitOptions.RemoveEmptyEntries)).First(x => x.Length >= 4 && x[1].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase))[2];
                // (pkg:<file>: a whole package added by hand, the Powers tab's Add Package)
                // (char:<base package>: a hero's own parts, the Powers tab's Character entry)
                var power = rest[2].StartsWith(PowerColorBuild.PackagePrefix, StringComparison.OrdinalIgnoreCase) || PowerColorBuild.IsCharacterParts(rest[2]) ? new Fx.PowerList.Power(rest[2], rest[2][(rest[2].IndexOf(':') + 1)..], null, [])
                    : Fx.PowerList.For(pdb, hero, pgame.Cooked, [], effectOnly: true).FirstOrDefault(x => x.Name.Equals(rest[2], StringComparison.OrdinalIgnoreCase));
                int pfails = 0;
                void PCheck(string what, bool ok) { if (!ok) pfails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                PCheck($"power '{rest[2]}' of {hero} found", power != null);
                if (power == null) return 1;
                int before = pm.Manifest.UpkReplacements.Count;
                // 1. a colour on
                var d = ModDraft.From(pm);
                if (rest.Contains("--all"))
                    foreach (var ap in Fx.PowerList.For(pdb, hero, pgame.Cooked, [], effectOnly: true)) d.PowerColors.Add(new PowerColorEntry { Power = ap.Prototype, Name = ap.Name, Hue = 120 });
                else d.PowerColors.Add(new PowerColorEntry { Power = power.Prototype, Name = power.Name, Hue = 120 });
                var pclock = System.Diagnostics.Stopwatch.StartNew();
                var plog = new List<string>();
                string? work = PowerColorBuild.Apply(d, pm, lib, pgame, ref pdb, plog);
                string? saved = ModWriter.Save(lib, d, pm, out string? perr);
                if (work != null) try { Directory.Delete(work, true); } catch (IOException) { }
                plog.ForEach(l => Console.WriteLine("    " + l));
                PCheck($"saved in {pclock.Elapsed.TotalSeconds:0.0} s" + (perr != null ? ": " + perr : ""), saved != null);
                var lib2 = ModLibrary.Load(data);
                var m2 = lib2.Find(rest[1])!;
                if (rest.Contains("--all"))
                {
                    var all2 = m2.Manifest.PowerColors ?? [];
                    var pk = all2.SelectMany(x => x.Packages).ToList();
                    PCheck($"all powers coloured: {all2.Count} entries, {pk.Count} packages, none twice", all2.Count > 0 && pk.Count == pk.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    PCheck("every package in the mod folder", pk.All(f => File.Exists(Path.Combine(m2.Folder, f)) && m2.Manifest.UpkReplacements.Contains(f, StringComparer.OrdinalIgnoreCase)));
                }
                var entry = m2.Manifest.PowerColors?.FirstOrDefault();
                PCheck($"manifest keeps the colour ({entry?.Name} hue {entry?.Hue}, {entry?.Packages.Count} package(s): {string.Join(", ", entry?.Packages ?? [])})", entry is { Hue: 120 } && entry.Packages.Count > 0);
                PCheck("its packages are the mod's packages", entry != null && entry.Packages.All(f => m2.Manifest.UpkReplacements.Contains(f, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(m2.Folder, f))));
                PCheck("each holds the recoloured group", entry != null && entry.Packages.All(f => MhoPackageModifier.Package.Open(Path.Combine(m2.Folder, f)).Exports.Any(e => e.ObjectName.EndsWith("_recolor_fx", StringComparison.OrdinalIgnoreCase))));
                int colored = m2.Manifest.PowerColors?.Sum(x => x.Packages.Count) ?? 0;
                PCheck($"the mod's other packages kept ({before} + {colored} = {m2.Manifest.UpkReplacements.Count})", m2.Manifest.UpkReplacements.Count == before + colored);
                if (rest.Contains("--keep")) { Console.WriteLine(pfails == 0 ? "PASS (colour kept)" : $"{pfails} FAILED"); return pfails == 0 ? 0 : 1; }
                // 2. back to the game's colours
                var d2 = ModDraft.From(m2);
                foreach (var e in d2.PowerColors) { e.Hue = 0; e.Saturation = 1; e.Brightness = 1; }
                string? work2 = PowerColorBuild.Apply(d2, m2, lib2, pgame, ref pdb, plog);
                string? saved2 = ModWriter.Save(lib2, d2, m2, out perr);
                if (work2 != null) try { Directory.Delete(work2, true); } catch (IOException) { }
                var m3 = ModLibrary.Load(data).Find(rest[1])!;
                PCheck("colour off: saved", saved2 != null);
                PCheck("colour off: no PowerColors in the manifest", m3.Manifest.PowerColors == null);
                PCheck($"colour off: its packages gone ({m3.Manifest.UpkReplacements.Count} = {before})", m3.Manifest.UpkReplacements.Count == before && entry != null && entry.Packages.All(f => !File.Exists(Path.Combine(m3.Folder, f))));
                Console.WriteLine(pfails == 0 ? "PASS" : $"{pfails} FAILED");
                return pfails == 0 ? 0 : 1;
            }
            case "--own-color-test":
            {
                // Test (scratch library only): an NPC / enemy package's own effects recolored (Powers tab → "<Name>: Own Effects",
                // Kurt 2026-10-05: the Sinister clones' red glow): its reddest color → blue, saved; the model kept; saved again
                // = the same bytes (no recolor of a recolor); back to the game's colors = the package as it was.
                // --own-color-test <mod> <package file>
                string? ohome = Environment.GetEnvironmentVariable("MHO_EXTMM_HOME");
                if (ohome == null || Path.GetFullPath(ohome).Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder (a copy of a library), never the real one."); return 2; }
                var om = rest.Count > 2 ? lib.Find(rest[1]) : null;
                string? ogr = settings.ResolvedGameRoot(data);
                if (om == null || ogr == null) { Console.WriteLine("--own-color-test <mod> <package file>"); return 1; }
                string ofile = rest[2], opath = Path.Combine(om.Folder, ofile);
                var ogame = new GameState(ogr, data);
                Fx.GameData? odb = null;
                int ofails = 0;
                void OCheck(string what, bool ok) { if (!ok) ofails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                byte[] original = File.ReadAllBytes(opath);
                static byte[]? MeshBytes(string f) { var p = MhoPackageModifier.Package.Open(f); string? n = MhoExtendedModManager.Model.MhoSkeleton.ComponentMesh(f); var e = p.Exports.FirstOrDefault(x => x.ObjectName.Equals(n, StringComparison.OrdinalIgnoreCase) && p.ClassOf(x).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)); return e == null ? null : p.ReadExportBytes(e).ToArray(); }
                byte[]? mesh0 = MeshBytes(opath);
                var pal = PowerRecolor.Palette([opath], ogame.Cooked);
                Console.WriteLine("  colors: " + string.Join(" ", pal.Select(x => $"{ColorMap.Hex(x.Tint)} {x.Share:P0}")));
                var red = pal.Where(x => x.Tint.X > 0.8f && x.Tint.Y < 0.5f && x.Tint.Z < 0.5f).OrderByDescending(x => x.Weight).FirstOrDefault();
                OCheck($"a red among its colors ({(red != null ? ColorMap.Hex(red.Tint) : "none")})", red != null);
                if (red == null) return 1;
                string Power = PowerColorBuild.OwnPrefix + ofile;
                var od = ModDraft.From(om);
                od.PowerColors.Add(new PowerColorEntry { Power = Power, Name = "Own Effects", Maps = [new ColorMapEntry { From = ColorMap.Hex(red.Tint), To = "#2060FF", Tolerance = 0.3f }] });
                var olog = new List<string>();
                PowerColorBuild.Apply(od, om, lib, ogame, ref odb, olog);
                string? osaved = ModWriter.Save(lib, od, om, out string? oerr);
                olog.ForEach(l => Console.WriteLine("    " + l));
                OCheck("saved" + (oerr != null ? ": " + oerr : ""), osaved != null);
                var ol2 = ModLibrary.Load(data); var om2 = ol2.Find(rest[1])!;
                string after = Path.Combine(om2.Folder, ofile);
                byte[] first = File.ReadAllBytes(after);
                OCheck("the package changed", !first.AsSpan().SequenceEqual(original));
                OCheck("its model is kept (the same mesh bytes)", mesh0 != null && MeshBytes(after) is { } m1 && m1.AsSpan().SequenceEqual(mesh0));
                string kb = Path.Combine(om2.Folder, ModelWork.Folder, "color_base", ofile);
                OCheck("the package before the recolor is kept with the mod (Model color_base)", File.Exists(kb) && File.ReadAllBytes(kb).AsSpan().SequenceEqual(original));
                var pal2 = PowerRecolor.Palette([after], ogame.Cooked);
                Console.WriteLine("  colors now: " + string.Join(" ", pal2.Select(x => $"{ColorMap.Hex(x.Tint)} {x.Share:P0}")));
                float RedShare(List<PowerRecolor.Swatch> p) => p.Where(x => x.Tint.X > 0.8f && x.Tint.Y < 0.5f && x.Tint.Z < 0.5f).Sum(x => x.Share);
                OCheck($"less red ({RedShare(pal):P0} → {RedShare(pal2):P0})", RedShare(pal2) < RedShare(pal));
                // saved again with the same colors: the same bytes
                var od2 = ModDraft.From(om2);
                PowerColorBuild.Apply(od2, om2, ol2, ogame, ref odb, olog);
                ModWriter.Save(ol2, od2, om2, out oerr);
                var om3 = ModLibrary.Load(data).Find(rest[1])!;
                OCheck("saved again: the same package (not a recolor of the recolor)", File.ReadAllBytes(Path.Combine(om3.Folder, ofile)).AsSpan().SequenceEqual(first));
                // the game's colors again
                var od3 = ModDraft.From(om3);
                foreach (var e in od3.PowerColors) { e.Hue = 0; e.Saturation = 1; e.Brightness = 1; e.Maps = null; }
                PowerColorBuild.Apply(od3, om3, ModLibrary.Load(data), ogame, ref odb, olog);
                ModWriter.Save(ModLibrary.Load(data), od3, om3, out oerr);
                var om4 = ModLibrary.Load(data).Find(rest[1])!;
                OCheck("back to the game's colors: the package as it was, still in the mod", File.Exists(Path.Combine(om4.Folder, ofile)) && File.ReadAllBytes(Path.Combine(om4.Folder, ofile)).AsSpan().SequenceEqual(original));
                OCheck("no PowerColors left in the manifest", om4.Manifest.PowerColors == null);
                Console.WriteLine(ofails == 0 ? "PASS" : $"{ofails} FAILED");
                return ofails == 0 ? 0 : 1;
            }
            case "--opacity-test":
            {
                // Test (read only on the source; writes <out.upk>): a package recolored at an opacity (PowerRecolor.Build), then
                // every particle system read back: how many emitters still draw. 0 % must leave none.
                // --opacity-test <package.upk> <opacity 0-1> <out.upk>
                string? xgr = settings.ResolvedGameRoot(data);
                if (rest.Count < 4 || xgr == null || !float.TryParse(rest[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float xop)) { Console.WriteLine("--opacity-test <package.upk> <opacity 0-1> <out.upk>"); return 1; }
                string xcooked = Settings.Cooked(xgr);
                static int Drawing(string f)
                {
                    var p = MhoPackageModifier.Package.Open(f); var t = new Fx.FxTables(p); var fx = new Fx.FxPkg(Path.GetFileName(f), p.Body, t);
                    int n = 0;
                    for (int i = 0; i < t.Exports.Count; i++)
                        if (t.ClassOf(t.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase) && Fx.ParticleData.Read(fx, i) is { } d) n += d.Emitters.Count;
                    return n;
                }
                int before = Drawing(rest[1]);
                var xb = PowerRecolor.Build(rest[1], new PowerColor(0) { Opacity = xop }, xcooked, l => Console.WriteLine("    " + l));
                if (xb == null) { Console.WriteLine("nothing built"); return 1; }
                File.WriteAllBytes(rest[3], xb);
                int after = Drawing(rest[3]);
                bool ok = xop < PowerColor.Invisible ? after == 0 && before > 0 : after == before;
                Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} emitters that draw: {before} → {after}");
                return ok ? 0 : 1;
            }
            case "--agent-color-test":
            {
                // Test (scratch library only): an NPC's / enemy's power colored (Powers tab: its powers under its Own Effects),
                // saved: the packages are the power's (PackagesOfAgent), the character's own package untouched; then off again.
                // --agent-color-test <mod> <character package file> <power name part>
                string? ahome = Environment.GetEnvironmentVariable("MHO_EXTMM_HOME");
                if (ahome == null || Path.GetFullPath(ahome).Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Set MHO_EXTMM_HOME to a scratch folder."); return 2; }
                var am = rest.Count > 3 ? lib.Find(rest[1]) : null;
                string? agr3 = settings.ResolvedGameRoot(data);
                if (am == null || agr3 == null) { Console.WriteLine("--agent-color-test <mod> <character package> <power name part>"); return 1; }
                var ag = new GameState(agr3, data);
                Fx.GameData? adb3 = new Fx.GameData(Fx.SipArchive.Load(Path.Combine(agr3, "Data", "Game", "Calligraphy.sip")));
                string acls = PowerColorBuild.ClassOf(rest[2]);
                var apw = Fx.AgentPowers.List(adb3, acls, ag.Cooked, "x").FirstOrDefault(x => x.Name.Contains(rest[3], StringComparison.OrdinalIgnoreCase));
                int afails = 0;
                void ACheck(string what, bool ok) { if (!ok) afails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                ACheck($"power '{rest[3]}' of {acls}: {apw?.Prototype}", apw != null);
                if (apw == null) return 1;
                byte[] ownBefore = File.ReadAllBytes(Path.Combine(am.Folder, rest[2]));
                int countBefore = am.Manifest.UpkReplacements.Count;
                var ad = ModDraft.From(am);
                ad.PowerColors.Add(new PowerColorEntry { Power = apw.Prototype, Name = apw.Name, Owner = rest[2], Hue = 120 });
                var alog = new List<string>();
                PowerColorBuild.Apply(ad, am, lib, ag, ref adb3, alog);
                string? asaved = ModWriter.Save(lib, ad, am, out string? aerr);
                alog.ForEach(l => Console.WriteLine("    " + l));
                ACheck("saved" + (aerr != null ? ": " + aerr : ""), asaved != null);
                var am2 = ModLibrary.Load(data).Find(rest[1])!;
                var want = PowerRecolor.PackagesOfAgent(adb3, apw.Prototype, acls, ag.Cooked);
                var entry = am2.Manifest.PowerColors?.FirstOrDefault(e => e.Power == apw.Prototype);
                ACheck($"its packages in the mod: {string.Join(", ", entry?.Packages ?? [])}", entry != null && entry.Packages.Count > 0 && entry.Packages.All(f => want.Contains(f) && File.Exists(Path.Combine(am2.Folder, f))));
                ACheck("the owner is kept with the color", entry?.Owner == rest[2]);
                ACheck("the character's own package untouched", File.ReadAllBytes(Path.Combine(am2.Folder, rest[2])).AsSpan().SequenceEqual(ownBefore));
                var ad2 = ModDraft.From(am2);
                foreach (var e in ad2.PowerColors) { e.Hue = 0; e.Saturation = 1; e.Brightness = 1; e.Maps = null; }
                PowerColorBuild.Apply(ad2, am2, ModLibrary.Load(data), ag, ref adb3, alog);
                ModWriter.Save(ModLibrary.Load(data), ad2, am2, out aerr);
                var am3 = ModLibrary.Load(data).Find(rest[1])!;
                ACheck($"color off: its packages gone ({am3.Manifest.UpkReplacements.Count} = {countBefore})", am3.Manifest.UpkReplacements.Count == countBefore && am3.Manifest.PowerColors == null);
                Console.WriteLine(afails == 0 ? "PASS" : $"{afails} FAILED");
                return afails == 0 ? 0 : 1;
            }
            case "--fit-icon-test":
            {
                // Test (scratch folder): a costume image of the wrong size is fitted for a costume move (CostumeMove.FitIcon):
                // a stock store image made 512×700 (another shape), fitted back to the target's 300×420, its format kept.
                string? fgr = settings.ResolvedGameRoot(data);
                if (rest.Count < 2 || fgr == null) { Console.WriteLine("--fit-icon-test <scratch folder>"); return 1; }
                string fdir = rest[1];
                if (Path.GetFullPath(fdir).Contains(@"\CookedPCConsole", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Refusing the game folder."); return 1; }
                Directory.CreateDirectory(fdir);
                var fcat = new StockCatalog(lib, new GameState(fgr, data));
                const string pkg = "ICO__MarvelUIIcons_Store_SF.upk", tex = "store_storm_classicblack";
                int ffails = 0;
                void FCheck(string what, bool ok) { if (!ok) ffails++; Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); }
                var target = fcat.Size(pkg, tex);
                FCheck($"target size known ({target?.W}×{target?.H} {target?.Format})", target != null);
                string png = Path.Combine(fdir, "stock.png");
                FCheck("stock image exported", fcat.ExportImage(pkg, tex, png) == null && File.Exists(png));
                // the wrong size: 512×700 (wider shape than 300:420)
                string bigPng = Path.Combine(fdir, "big.png"), bigDds = Path.Combine(fdir, "LunaStore.dds");
                using (var src = new System.Drawing.Bitmap(png))
                using (var big = new System.Drawing.Bitmap(512, 700))
                {
                    using (var g = System.Drawing.Graphics.FromImage(big)) g.DrawImage(src, 0, 0, 512, 700);
                    big.Save(bigPng, System.Drawing.Imaging.ImageFormat.Png);
                }
                fcat.ImageToDds(pkg, tex, bigPng, bigDds, keepSize: true);
                var made = MhoPackageModifier.TextureDecode.ReadDds(bigDds, out _);
                FCheck($"wrong-size image made ({made?.W}×{made?.H})", made is { W: 512, H: 700 });
                var icon = new CostumeMove.IconMove("Store Image", pkg, tex, tex, "LunaStore.dds", (512, 700), target);
                var resized = new List<string>();
                string fitted = CostumeMove.FitIcon(icon, fdir, Path.Combine(fdir, "work"), fcat, resized);
                var o = MhoPackageModifier.TextureDecode.ReadDds(fitted, out _);
                FCheck($"fitted to the target's size ({o?.W}×{o?.H})", target is { } tt && o is { } oo && oo.W == tt.W && oo.H == tt.H);
                FCheck($"in the target's format ({o?.Format} for {target?.Format})", o is { } o2 && target is { } t2 && t2.Format.Contains(o2.Format.Replace("PF_", ""), StringComparison.OrdinalIgnoreCase));
                FCheck("reported: " + string.Join(" | ", resized), resized.Count == 1 && resized[0].Contains("512×700") && resized[0].Contains("300×420"));
                // the right size is used as it is
                var same = new CostumeMove.IconMove("Store Image", pkg, tex, tex, "LunaStore.dds", (target!.Value.W, target.Value.H), target);
                var none = new List<string>();
                FCheck("an image of the right size is used unchanged", CostumeMove.FitIcon(same, fdir, Path.Combine(fdir, "work"), fcat, none) == Path.Combine(fdir, "LunaStore.dds") && none.Count == 0);
                if (o is { } shown)
                {
                    var bgra = MhoPackageModifier.TextureDecode.ToBgra(shown.Format, shown.W, shown.H, shown.Data, out _);
                    if (bgra != null) using (var bmp = MhoPackageModifier.TextureDecode.ToBitmap(bgra, shown.W, shown.H)) bmp.Save(Path.Combine(fdir, "fitted.png"));
                }
                Console.WriteLine(ffails == 0 ? "PASS" : $"{ffails} FAILED");
                return ffails == 0 ? 0 : 1;
            }
            case "--picture-test":
            {
                // Test (scratch library only, MHO_EXTMM_HOME): custom card / preview pictures on this PC, carried by Export,
                // stripped by a legacy export, used after install, and saved by the editor into Pictures\.
                if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") is not { Length: > 0 } phome || phome.Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase))
                { Console.WriteLine("refused: needs MHO_EXTMM_HOME on a scratch library"); return 1; }
                var pm = rest.Count > 1 ? lib.Find(rest[1]) : null;
                if (pm == null) { Console.WriteLine("--picture-test <mod>"); return 1; }
                int fails = 0;
                void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
                string work = Path.Combine(Path.GetTempPath(), "mho_picture_test"); if (Directory.Exists(work)) Directory.Delete(work, true); Directory.CreateDirectory(work);
                string png = Path.Combine(work, "my card.png");
                using (var bmp = new System.Drawing.Bitmap(64, 80)) { using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.OrangeRed); bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png); }
                pm.LocalCard = ModPictures.KeepLocal(lib.DataFolder, pm.FolderName, png, "card");
                pm.LocalPreview = ModPictures.KeepLocal(lib.DataFolder, pm.FolderName, png, "preview");
                Check("your card picture shows on the card", pm.CostumeIconFile() is string cf && cf.EndsWith("card.png", StringComparison.OrdinalIgnoreCase));
                Check("and decodes as a thumbnail", pm.CostumeIconFile() is string cf2 && Gui.Ui.DdsThumb(cf2, 40) is { Width: > 0 });
                Check("your preview picture is offered first", PreviewImages.For(pm, null).FirstOrDefault()?.Key == pm.LocalPreview);
                string zip = Path.Combine(work, "full.zip");
                ModInstaller.Export(pm, zip, previewPick: pm.LocalPreview, cardPick: pm.LocalCard);
                using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    var man = System.Text.Json.JsonSerializer.Deserialize<ModManifest>(new StreamReader(z.GetEntry("manifest.json")!.Open()).ReadToEnd(), ModManifest.Json)!;
                    Check("export carries the pictures (Pictures/card.png, Pictures/preview.png)", z.GetEntry("Pictures/card.png") != null && z.GetEntry("Pictures/preview.png") != null);
                    Check("and the manifest names them", man.CardPicture == "file:Pictures/card.png" && man.PreviewImage == "file:Pictures/preview.png");
                }
                string legacyZip = Path.Combine(work, "legacy.zip");
                ModInstaller.Export(pm, legacyZip, legacy: true, previewPick: pm.LocalPreview, cardPick: pm.LocalCard);
                using (var z = System.IO.Compression.ZipFile.OpenRead(legacyZip))
                {
                    string json = new StreamReader(z.GetEntry("manifest.json")!.Open()).ReadToEnd();
                    Check("a legacy export leaves them out", !json.Contains("CardPicture") && z.GetEntry("Pictures/card.png") == null);
                }
                // Install the full export under another name and check its card uses the carried picture.
                string unpacked = Path.Combine(work, "unpacked"); System.IO.Compression.ZipFile.ExtractToDirectory(zip, unpacked);
                var inst = new Mod { Folder = unpacked, FolderName = "unpacked", Manifest = ModManifest.Load(Path.Combine(unpacked, "manifest.json")) };
                Check("installed: its card uses the author's picture", inst.CostumeIconFile() is string icf && icf.Replace('\\', '/').EndsWith("Pictures/card.png", StringComparison.OrdinalIgnoreCase));
                Check("installed: its preview offers the author's picture", PreviewImages.For(inst, null).Any(c => c.Key == "file:Pictures/preview.png"));
                // The editor: a custom picture chosen for the card is saved into the mod's Pictures\.
                var dr = ModDraft.From(pm);
                dr.Pictures.Add(("Pictures/editor card.png", png)); dr.CardPicture = "file:Pictures/editor card.png";
                string? saved = ModWriter.Save(lib, dr, pm, out string? perr);
                Check("editor save keeps the custom card picture" + (perr != null ? ": " + perr : ""), saved != null && File.Exists(Path.Combine(lib.DataFolder, "mods", saved, "Pictures", "editor card.png"))
                    && ModManifest.Load(Path.Combine(lib.DataFolder, "mods", saved, "manifest.json")).CardPicture == "file:Pictures/editor card.png");
                Directory.Delete(work, true);
                Console.WriteLine(fails == 0 ? "PASS" : $"{fails} FAILED");
                return fails == 0 ? 0 : 1;
            }
            case "--tip-width-test":
            {
                // Test: the app's tooltip tells Windows its wrap width (long tips were placed by a one-line measure, far left)
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
                var l = new Label { Text = "x" };
                f.Controls.Add(l);
                int w = 0;
                f.Shown += (_, _) => { Gui.Ui.Tip(l, new string('w', 2000)); w = Gui.Ui.TipMaxWidthForTest(); f.Close(); };   // (on the UI thread, in its message loop, as the app)
                Application.Run(f);
                Console.WriteLine($"{(w > 0 && w < 1000 ? "PASS" : "FAIL")} native max tip width {w} px");
                return w > 0 && w < 1000 ? 0 : 1;
            }
            case "--tip-dialog-test":
            {
                // ON SCREEN, a few seconds, moves the mouse: a main window, then a modal dialog with a tipped button over it; the
                // mouse on the button; is the tip shown, and on top? (Kurt: the Tag Colors window's buttons showed no tooltips)
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Gui.Ui.UseDarkTheme();
                var back = Cursor.Position;
                var area = Screen.FromPoint(back).WorkingArea;
                var main = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(area.Left + 100, area.Top + 100), Size = new Size(900, 600), ShowInTaskbar = false };
                var mainBtn = Gui.Ui.FlatButton("Main", () => { }, "The main window's button.");
                main.Controls.Add(mainBtn);
                string result = "";
                main.Shown += (_, _) => main.BeginInvoke(() =>
                {
                    var dlg = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(area.Left + 300, area.Top + 250), Size = new Size(600, 300), ShowInTaskbar = false, TopMost = true };   // (on top: another window there takes the mouse)
                    var b = Gui.Ui.FlatButton("Cancel", () => { }, "Close without changing anything (Esc).");
                    b.Location = new Point(200, 100); dlg.Controls.Add(b);
                    Gui.Ui.Restyle(dlg);
                    dlg.Shown += async (_, _) =>
                    {
                        var p = b.PointToScreen(new Point(b.Width / 2, b.Height / 2));
                        Cursor.Position = new Point(p.X - 3, p.Y); await Task.Delay(100); Cursor.Position = p;
                        for (int k = 0; k < 3; k++) { TipNative.mouse_event(1, 0, 0, 0, IntPtr.Zero); await Task.Delay(50); }
                        Rectangle tip = Rectangle.Empty; IntPtr tipH = IntPtr.Zero;
                        for (int i = 0; i < 40 && tip.IsEmpty; i++)
                        {
                            await Task.Delay(100);
                            TipNative.EnumThreadWindows(TipNative.GetCurrentThreadId(), (h, _) =>
                            {
                                var cls = new System.Text.StringBuilder(64); TipNative.GetClassName(h, cls, 64);
                                if (cls.ToString().Contains("tooltips_class32") && TipNative.IsWindowVisible(h) && TipNative.GetWindowRect(h, out var r) && r.Right - r.Left > 50)
                                { tip = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); tipH = h; return false; }
                                return true;
                            }, IntPtr.Zero);
                        }
                        result = $"title '{b.AccessibleName}', tip text '{Gui.Ui.Tips.GetToolTip(b).Replace("\u0001", "[T]").Replace("\n", " / ")}', shown {tip}";
                        Cursor.Position = back;
                        dlg.Close();
                    };
                    dlg.ShowDialog(main);
                    main.Close();
                });
                Application.Run(main);
                Console.WriteLine(result);
                foreach (var l in Gui.Ui.TipDebugLog) Console.WriteLine("  " + l);
                return result.Contains("shown {X=0,Y=0,Width=0,Height=0}") ? 1 : 0;
            }
            case "--tip-place-test":
            {
                // ON SCREEN, a few seconds, moves the mouse: a window with a drop-down and a button (long tips); the mouse is put
                // on each and the shown tooltip window's rectangle is printed against the cursor (Kurt: tips far left of the mouse)
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Gui.Ui.UseDarkTheme();
                foreach (var sc in Screen.AllScreens) Console.WriteLine($"screen {sc.DeviceName} bounds {sc.Bounds} work {sc.WorkingArea}{(sc.Primary ? " primary" : "")}");
                var back = Cursor.Position;
                var f = new Form { StartPosition = FormStartPosition.Manual, Size = new Size(900, 300), ShowInTaskbar = false, TopMost = true };
                var area = Screen.FromPoint(back).WorkingArea;
                f.Location = new Point(area.Left + area.Width / 2 - 100, area.Top + area.Height / 2);
                var dd = new Gui.DropDown { Location = new Point(500, 40), Width = 200 };
                dd.Items.Add("No Added Cape"); dd.SelectedIndex = 0;
                var bt = new Button { Text = "Button", Location = new Point(500, 120), Size = new Size(150, 40) };
                var off = Gui.Ui.FlatButton("Off", () => { }); off.Location = new Point(500, 200); off.Size = new Size(150, 40); off.Enabled = false;
                f.Controls.Add(dd); f.Controls.Add(bt); f.Controls.Add(off);
                string longTip = string.Join(" ", Enumerable.Repeat("A long tooltip text to wrap.", 20));
                f.Shown += async (_, _) =>
                {
                    Gui.Ui.Tip(dd, longTip); Gui.Ui.Tip(bt, longTip); Gui.Ui.TipTitled(off, "Disabled Button", longTip);
                    f.Activate();
                    foreach (var c in new Control[] { dd, bt, off })
                    {
                        var p = c.PointToScreen(new Point(c.Width / 2, c.Height / 2));
                        Cursor.Position = new Point(p.X - 3, p.Y); await Task.Delay(100); Cursor.Position = p;
                        for (int k = 0; k < 3; k++) { TipNative.mouse_event(1, 0, 0, 0, IntPtr.Zero); await Task.Delay(50); }   // real input (MOUSEEVENTF_MOVE)
                        Rectangle tipRect = Rectangle.Empty;
                        for (int i = 0; i < 40 && tipRect.IsEmpty; i++) { await Task.Delay(100); tipRect = TipWindowRect(); }
                        Console.WriteLine($"{c.GetType().Name}: cursor {Cursor.Position}, tip {tipRect}, form at {f.Bounds}, dpi {f.DeviceDpi}");
                        if (c == off && !tipRect.IsEmpty && rest.Count > 1)
                        {
                            using var shot = new Bitmap(tipRect.Width, tipRect.Height);
                            using (var sg = Graphics.FromImage(shot)) sg.CopyFromScreen(tipRect.Location, Point.Empty, tipRect.Size);
                            shot.Save(rest[1]);
                        }
                        Cursor.Position = new Point(f.Left + 20, f.Bottom - 20); await Task.Delay(600);
                    }
                    Cursor.Position = back;
                    f.Close();
                };
                static Rectangle TipWindowRect()
                {
                    Rectangle found = Rectangle.Empty;
                    TipNative.EnumThreadWindows(TipNative.GetCurrentThreadId(), (h, _) =>
                    {
                        var cls = new System.Text.StringBuilder(64);
                        TipNative.GetClassName(h, cls, 64);
                        if (cls.ToString().Contains("tooltips_class32") && TipNative.IsWindowVisible(h) && TipNative.GetWindowRect(h, out var r) && r.Right - r.Left > 50)
                        { found = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); return false; }
                        return true;
                    }, IntPtr.Zero);
                    return found;
                }
                Application.Run(f);
                return 0;
            }
            case "--grid-drag-test":
            {
                // (off screen) a table whose first column stretches; a drag on the divider after the second column, sent as
                // window messages: the second column must grow by the drag and the first keep its width (Kurt, 2026-10-06)
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(700, 300), ShowInTaskbar = false };
                var g = Gui.Ui.Grid(1f, false, ("Name", 0), ("Size", 90), ("State", 140));
                g.Dock = DockStyle.Fill;
                f.Controls.Add(g);
                f.Show(); Application.DoEvents();
                var cols = g.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).ToList();
                int w0 = cols[0].Width, w1 = cols[1].Width;
                var rect = g.GetColumnDisplayRectangle(cols[1].Index, false);
                int x = rect.Right - 1, y = g.ColumnHeadersHeight / 2;
                static IntPtr L(int px, int py) => (IntPtr)((py << 16) | (px & 0xFFFF));
                const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MK_LBUTTON = 1;
                // a press on the divider (the table's own drag needs the real mouse: its resize is played as it does it, Width + 40)
                typeof(Control).GetMethod("OnMouseDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(g, [new MouseEventArgs(MouseButtons.Left, 1, x, y, 0)]);
                cols[1].Width += 40; Application.DoEvents();
                int a0 = cols[0].Width, a1 = cols[1].Width;
                bool ok = Math.Abs(a0 - w0) <= 2 && a1 - w1 >= 30;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} drag +40 on the divider after Size: Name {w0} -> {a0}, Size {w1} -> {a1}, State {cols[2].Width}");
                return ok ? 0 : 1;
            }
            case "--searchbox-test":
            {
                // Test: the clear button (×) on a filter box, in an off-screen window: shown only with text, a click and Esc
                // clear, the text stops short of it; the framed box rendered to <dir>\searchbox.png.
                if (rest.Count < 2) { Console.WriteLine("--searchbox-test <dir>"); return 1; }
                Directory.CreateDirectory(rest[1]);
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Gui.Ui.UseDarkTheme();
                int sfail = 0;
                void SCheck(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) sfail++; }
                using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-4000, -4000), Size = new System.Drawing.Size(840, 160), ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, BackColor = Gui.Ui.Back };
                var box = new TextBox { Location = new System.Drawing.Point(24, 40), Width = 720, Font = Gui.Ui.Regular(9.5f) };
                form.Controls.Add(box);
                MhoPackageModifier.Gui.Theme.Apply(form, MhoPackageModifier.Gui.Palette.Dark); Gui.Modern.Modernize(form);
                MhoPackageModifier.Gui.SearchBox.AddClear(box);
                form.Show(); Application.DoEvents();
                var x = box.Controls.Cast<Control>().FirstOrDefault();
                SCheck("an empty box shows no ×", x != null && !x.Visible);
                box.Text = "storm classic"; Application.DoEvents();
                SCheck("with text, the × shows", x!.Visible);
                const int EM_GETMARGINS = 0xD4;
                int margins = (int)SearchMessage(box.Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero);
                SCheck($"the text stops short of the × (right margin {margins >> 16} px, × {x.Width} px)", (margins >> 16) >= x.Width);
                using (var bmp = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                { form.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.ClientSize)); bmp.Save(Path.Combine(rest[1], "searchbox.png")); }
                x.GetType().GetMethod("OnMouseClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(x, [new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)]);
                Application.DoEvents();
                SCheck("a click on the × clears the box and hides the ×", box.TextLength == 0 && !x.Visible);
                box.Text = "vision"; box.Focus(); Application.DoEvents();
                const int WM_KEYDOWN = 0x100;
                SearchMessage(box.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero); Application.DoEvents();
                SCheck("Esc clears it", box.TextLength == 0);
                form.Close();
                Console.WriteLine(sfail == 0 ? "PASS" : $"{sfail} FAILED");
                return sfail == 0 ? 0 : 1;
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
                    foreach (string f in Directory.GetFiles(m.Folder)) { Directory.CreateDirectory(Path.Combine(scratch, "mods", m.FolderName)); File.Copy(f, Path.Combine(scratch, "mods", m.FolderName, Path.GetFileName(f)), overwrite: true); }
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
        }
        return null;
    }

    /// <summary>--snapshot-test: snapshots and bundles on a throwaway library (copies of a few small mods in the scratch folder).</summary>
    static int SnapshotTest(ModLibrary real, string scratch, List<string> named)
    {
        int fails = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        string dir = Path.GetFullPath(scratch), data = Path.Combine(dir, "library"), mods = Path.Combine(data, "mods");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(mods);
        var pick = (named.Count > 0 ? real.Mods.Where(m => named.Contains(m.Name, StringComparer.OrdinalIgnoreCase)) : real.Mods.OrderBy(m => Snapshots.Fingerprint(m.Folder).Bytes).Take(3)).ToList();
        if (pick.Count < 3) { Console.WriteLine("needs 3 mods"); return 1; }
        static void CopyDir(string from, string to)
        {
            foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                string d = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                File.Copy(f, d, true);
            }
        }
        foreach (var m in pick) CopyDir(m.Folder, Path.Combine(mods, m.FolderName));
        var (a, bm, c) = (pick[0].FolderName, pick[1].FolderName, pick[2].FolderName);
        File.WriteAllText(Path.Combine(data, "state.json"), System.Text.Json.JsonSerializer.Serialize(new ModState { ModOrder = [a, bm, c], EnabledMods = [a] }));
        var lib = ModLibrary.Load(data);
        Console.WriteLine($"library: {string.Join(", ", lib.Mods.OrderBy(m => m.Priority).Select(m => m.Name))}");

        // 1. a snapshot, a change, another snapshot, the report
        var snapA = Snapshots.Now(lib, null, "manual", "A");
        Snapshots.Save(snapA, Path.Combine(dir, "a.json.gz"));
        var back = Snapshots.Load(Path.Combine(dir, "a.json.gz"));
        long size = new FileInfo(Path.Combine(dir, "a.json.gz")).Length;
        Check(back != null && back.Mods.Count == 3 && back.Mods.Count(m => m.Enabled) == 1 && size < 50_000, $"a snapshot saves and reads back ({size:N0} bytes for 3 mods)");
        Thread.Sleep(50);
        ModHistory.Add(data, a, "Fix Mirror Tables (Crash Risk)", "test");
        foreach (var m in lib.Mods) { if (m.FolderName == bm) m.Enabled = true; m.Priority = m.FolderName == c ? -1 : m.Priority; }
        lib.SaveState();
        lib = ModLibrary.Load(data);
        var snapB = Snapshots.Now(lib, null, "manual", "B");
        string report = Snapshots.Compare(snapA, snapB);
        Console.WriteLine("  report:\n    " + report.Replace("\n", "\n    "));
        Check(report.Contains("Turned on") && report.Contains(pick[1].Name), "the report lists the mod turned on");
        Check(report.Contains("Moved in the priority order") && report.Contains(pick[2].Name), "the report lists the mod moved");
        Check(report.Contains("Fix Mirror Tables (Crash Risk)"), "the report lists the Crash Risk fix done in between");
        Check(Snapshots.Compare(snapB, Snapshots.Now(lib, null)).Contains("Nothing changed"), "nothing changed from the last snapshot to now");

        // 2. restore the first
        Snapshots.Restore(lib, snapA);
        lib.SaveState();
        lib = ModLibrary.Load(data);
        var order = lib.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).ToList();
        Check(order.SequenceEqual([a, bm, c]) && lib.Mods.Where(m => m.Enabled).Select(m => m.FolderName).SequenceEqual([a]), $"restoring the first puts its order and on/off back ({string.Join(", ", order)})");

        // 3. a bundle: made, one mod deleted, restored
        string zip = Path.Combine(dir, "bundle.zip");
        int files = Snapshots.MakeBundle(lib, null, lib.Mods, zip);
        Check(File.Exists(zip) && files > 0 && Snapshots.BundleInfo(zip)?.Mods.Count == 3, $"a bundle is made ({files} files, {new FileInfo(zip).Length:N0} bytes)");
        string gone = Path.Combine(mods, bm);
        var hashes = Directory.EnumerateFiles(gone, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(gone, f), f => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f))));
        Directory.Delete(gone, true);
        foreach (var m in ModLibrary.Load(data).Mods) m.Enabled = false;
        var lib2 = ModLibrary.Load(data);
        var log = Snapshots.RestoreBundle(lib2, zip, replace: false);
        Console.WriteLine("  " + string.Join("\n  ", log));
        var lib3 = ModLibrary.Load(data);
        bool same = Directory.Exists(gone) && hashes.All(kv => File.Exists(Path.Combine(gone, kv.Key)) && Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(Path.Combine(gone, kv.Key)))) == kv.Value);
        Check(same && lib3.Mods.Count == 3, "restoring the bundle brings the deleted mod back, every file identical");
        Check(log.Count(l => l.Contains("already the same")) == 2, "the mods still there count as the same (no needless replace)");
        Snapshots.Restore(lib3, Snapshots.BundleInfo(zip)!);
        lib3.SaveState();
        var lib4 = ModLibrary.Load(data);
        Check(lib4.Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).SequenceEqual([a, bm, c]) && lib4.Mods.Where(m => m.Enabled).Select(m => m.FolderName).SequenceEqual([a]), "the bundle's state goes back too");

        // 4. discarded copies: an edit (Keep Shared Paths changed, as a fix would) and a removal, then a restore puts both back
        var snapC = Snapshots.Now(lib4, null, "manual", "C");
        Thread.Sleep(2100);   // (fingerprint times are to 2 seconds)
        var ma = lib4.Mods.First(m => m.FolderName == a);
        var draft = ModDraft.From(ma);
        draft.KeepShared = !draft.KeepShared;
        Check(ModWriter.Save(lib4, draft, ma, out string? err, "test edit") != null, $"an edit saves ({err})");
        var lib5 = ModLibrary.Load(data);
        Check(ModInstaller.Remove(lib5.Mods.First(m => m.FolderName == c), lib5, null) == null, "a turned-off mod is removed");
        lib5 = ModLibrary.Load(data);
        var kept = Discarded.List(data);
        Check(kept.Count == 2 && kept.Any(e => e.Folder == a && e.Reason == "test edit") && kept.Any(e => e.Folder == c && e.Reason == "Removed"), $"both old folders are kept as discarded copies ({kept.Count})");
        var fplan = Snapshots.PlanFiles(lib5, snapC);
        Check(fplan.Back.Count == 2 && fplan.Cant.Count == 0, $"the restore plan puts both back ({fplan.Back.Count} back, {fplan.Cant.Count} can't)");
        Console.WriteLine("  " + string.Join("\n  ", Snapshots.RestoreFiles(lib5, fplan)));
        var lib6 = ModLibrary.Load(data);
        bool fpSame(ModLibrary l, string folder) => l.Mods.FirstOrDefault(m => m.FolderName == folder) is { } x && Snapshots.Fingerprint(x.Folder).Hash == snapC.Mods.First(i => i.Folder == folder).Fingerprint;
        Check(fpSame(lib6, a) && fpSame(lib6, c), "both mods have their files from then again (same fingerprints)");
        Check(Discarded.List(data).Count(e => e.Folder == a) == 1 && Discarded.List(data).Count(e => e.Folder == c) == 0, "the edited copy is now the discarded one; the removed one left the discarded copies");
        Snapshots.Restore(lib6, snapC); lib6.SaveState();
        Check(ModLibrary.Load(data).Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).SequenceEqual([a, bm, c]), "the state goes back with the files");

        // 5. the cleanup's warning, and Keep Shared Paths without a copy
        Discarded.Delete(Discarded.List(data));
        Thread.Sleep(2100);
        var lib7 = ModLibrary.Load(data);
        var ma7 = lib7.Mods.First(m => m.FolderName == a);
        var d7 = ModDraft.From(ma7); d7.KeepShared = !d7.KeepShared;
        ModWriter.Save(lib7, d7, ma7, out _, "test edit 2");
        lib7 = ModLibrary.Load(data);
        var needed = Discarded.Needed(lib7, Discarded.List(data), [snapC]);
        Check(needed.Count == 1 && Discarded.Warning(needed).Contains("test edit 2"), "the cleanup warns: a snapshot needs that copy");
        Check(Discarded.Due(data, 30).Count == 0 && Discarded.Due(data, 0).Count == 0, "a new copy isn't due (30 days), nor with Until Cleaned Up");
        Discarded.Delete(Discarded.List(data));
        var splan = Snapshots.PlanFiles(lib7, snapC);
        Check(splan.Shared.Count == 1 && splan.Back.Count == 0, "without its copy, only Keep Shared Paths goes back");
        Snapshots.RestoreFiles(lib7, splan);
        Check(ModLibrary.Load(data).Mods.First(m => m.FolderName == a).Manifest.KeepShared == true == snapC.Mods.First(i => i.Folder == a).KeepShared, "Keep Shared Paths is as it was then");
        Console.WriteLine(fails == 0 ? "all checks passed" : $"{fails} check(s) failed");
        return fails == 0 ? 0 : 1;
    }
}

static class TipNative
{
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumThreadWindows(uint thread, EnumProc proc, IntPtr l);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
