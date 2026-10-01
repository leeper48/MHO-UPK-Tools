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
}
