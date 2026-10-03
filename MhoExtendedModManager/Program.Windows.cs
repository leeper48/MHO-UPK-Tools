using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace MhoExtendedModManager;

static partial class Program
{
    /// <summary>The commands that open a window (the app itself, snapshots, on-screen self-tests), as Main had them, in
    /// order. Null when <paramref name="args"/> isn't one of them.</summary>
    static int? WindowCommand(string[] args)
    {
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
            using var f = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = Environment.GetEnvironmentVariable("MHO_RENDER_SIZE") is string rs && rs.Split('x') is [var rw, var rh] ? new Size(int.Parse(rw), int.Parse(rh)) : new Size(420, 560), ShowInTaskbar = false };   // MHO_RENDER_SIZE=WxH: time frames at the preview's size
            var v = new Gui.ModelView { Dock = DockStyle.Fill, Background = Gui.Ui.Card };
            f.Controls.Add(v);
            var shots = new List<Bitmap>();
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                // MHO_RENDER_PROPS=1: with the props the preview shows (PropRig), as it shows them.
                var rrig = new PropRig();
                PropRig? renderRig = null;
                if (Environment.GetEnvironmentVariable("MHO_RENDER_PROPS") == "1")
                {
                    AppDomain.CurrentDomain.FirstChanceException += (_, e) => Console.WriteLine("  exception: " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                    var rpk = rm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(rm.Folder, f))).ToList();
                    foreach (var w in PropRig.Attached(mr, ModMeshes.List(rm), rc))
                        if (ModMeshes.Load(w.Ref, rc, out _) is { } pm)
                        {
                            bool animated = !w.UseParentAnim && w.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase);
                            var pa = animated ? ModAnimations.For(w.Ref, pm.Bones, rpk, rc, minBones: 1) : null;
                            if (w.UseParentAnim) rrig.SetParentAnimation(ba);
                            rrig.Add(pm, w.UseParentAnim ? -1 : PropRig.BoneFor(anim8, w.Bone), w.Slots, w.OnDemand && Environment.GetEnvironmentVariable("MHO_RENDER_ALLPROPS") != "1", w.Class, animated || w.UseParentAnim ? new MeshAnimator(pm.Bones, pm.Positions, pm.Normals, pm.Influences, pm.Tangents) { InPlace = !animated } : null, pa, w.UseParentAnim, w.Offset);
                            Console.WriteLine($"  prop {w.Ref.Name} on {w.Bone}{(w.OnDemand ? " (on demand)" : "")}{(animated ? $", animated ({pa!.Count} animations)" : "")}");
                            if (Environment.GetEnvironmentVariable("MHO_PROP_ROOT") == "1" && pm.Bones.Count > 0) Console.WriteLine($"    rest root {pm.Bones[0].Name} {pm.Bones[0].Position}");
                            if (Environment.GetEnvironmentVariable("MHO_PROP_BONES") == "1" && w.UseParentAnim && ba != null)
                            {
                                var pt = new MeshAnimator(pm.Bones, pm.Positions, pm.Normals, pm.Influences, pm.Tangents);
                                float mid = MeshAnimator.Span(ba).Frames / 2;
                                pt.Pose(ba, mid); anim8.Pose(ba, mid);
                                foreach (string bn in new[] { "g_l_wrist", "g_r_wrist", "g_pelvis" })
                                    Console.WriteLine($"    {bn}: prop {pt.BonePosition(pt.BoneIndex(bn))}  character {anim8.BonePosition(anim8.BoneIndex(bn))}");
                                var wsum = new Dictionary<string, float>();
                                foreach (var inf in pm.Influences) for (int k = 0; k < inf.Bones.Count; k++) { string bn = inf.Bones[k] >= 0 && inf.Bones[k] < pm.Bones.Count ? pm.Bones[inf.Bones[k]].Name : "#" + inf.Bones[k]; wsum[bn] = wsum.GetValueOrDefault(bn) + inf.Weights[k]; }
                                Console.WriteLine("    weights: " + string.Join(", ", wsum.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value:0}")));
                                pt.Pose(null, 0); anim8.Pose(null, 0);
                                Console.WriteLine($"    rest g_l_wrist: prop {pt.BonePosition(pt.BoneIndex("g_l_wrist"))}  character {anim8.BonePosition(anim8.BoneIndex("g_l_wrist"))}");
                            }
                            if (Environment.GetEnvironmentVariable("MHO_PROP_BONES") == "1") Console.WriteLine("    bones: " + string.Join(" ", pm.Bones.Select(x => x.Name.StartsWith("g_bow") ? $"{x.Name}<{(x.ParentIndex >= 0 && x.ParentIndex < pm.Bones.Count ? pm.Bones[x.ParentIndex].Name : "-")}" : x.Name)) + (ba != null ? $"   (tracks for {pm.Bones.Count(x => ba.Tracks.ContainsKey(x.Name))})" : ""));
                        }
                    if (ar != null)
                    {
                        var seqs = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (cls, pref) in rrig.MotionRefs(ar.Name)) if (ModAnimations.Load(pref) is { } pba)
                        {
                            seqs[cls] = pba;
                            // MHO_PROP_ROOT=1: the prop skeleton's root, rest position vs its animation's (what playing in place drops).
                            if (Environment.GetEnvironmentVariable("MHO_PROP_ROOT") == "1" && pba.Tracks.FirstOrDefault() is var (rn, rt) && rt != null && rt.PositionKeys.Count > 0)
                                Console.WriteLine($"  {cls}: root track {rn}: first key {rt.PositionKeys[0].Position}, {rt.PositionKeys.Count} key(s); rotation {(rt.RotationKeys.Count > 0 ? rt.RotationKeys[0].Rotation.ToString() : "-")} ({rt.RotationKeys.Count} key(s)); tracks: {string.Join(" ", pba.Tracks.Keys.Take(3))}");
                        }
                        rrig.SetMotions(seqs);
                        Console.WriteLine("  prop animations: " + (seqs.Count == 0 ? "none" : string.Join(", ", seqs.Keys)));
                    }
                    // The playing power's weapon-slot switches, as the preview applies them.
                    // (with the game data, as the preview: travel powers' animations are found through it)
                    Fx.GameData? rdb = null;
                    try { rdb = new Fx.GameData(Fx.SipArchive.Load(Path.GetFullPath(Path.Combine(rc ?? ".", "..", "..", "..", "Data", "Game", "Calligraphy.sip")))); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
                    if (ar != null && rc != null && mr.Package.Split('_', StringSplitOptions.RemoveEmptyEntries) is [_, _, var rhero, ..]
                        && (rdb != null ? Fx.PowerIndex.For(rhero, rc, rm.Manifest.UpkReplacements.Select(f => Path.Combine(rm.Folder, f)), rdb) : Fx.PowerIndex.For(rhero, rc, rm.Manifest.UpkReplacements.Select(f => Path.Combine(rm.Folder, f)))).TryGetValue(ar.Name, out var prefs))
                    {
                        var sw = prefs.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(ModMeshes.PropRules).Distinct().ToList();
                        // As the preview (LoadPropSwitches): a rule's target the character's props don't fill brings the power
                        // package's own attachment, added as a plain prop.
                        var have = PropRig.Attached(mr, ModMeshes.List(rm), rc);
                        foreach (var r in sw.Where(r => r.Show && !have.Any(h => PropRig.Fills(h, r.Target))))
                            foreach (string pf in prefs.Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase))
                                foreach (var x in ModMeshes.Attachments(pf).Where(x => PropRig.Fills(new PropRig.Prop(null!, x.Bone) { Slots = x.Slots, Class = x.Class }, r.Target)))
                                    Console.WriteLine($"  rule {r.Target}: not on the character; the power's own {x.Class} ({x.Mesh} on {x.Bone}) from {Path.GetFileName(pf)}");
                        float rsecs = ba == null ? 0 : MeshAnimator.Span(ba).Seconds;
                        rrig.SetRules(sw, 0.4f * rsecs, rsecs);
                        renderRig = rrig;
                        Console.WriteLine("  prop rules: " + (sw.Count == 0 ? "none" : string.Join(", ", sw.Select(x => $"{(x.Show ? "show" : "hide")} {x.Target} @{x.StartPoint}+{x.StartOffset:0.##}{(x.EndPoint != null ? $" to {x.EndPoint}+{x.EndOffset:0.##}" : "")}"))));
                    }
                    v.ShowMesh(rrig.Combine(ld), ld.Positions.Length);
                    // MHO_GRIP=1: how far each hand is from the bike's grip on that side (the ride's handlebars), mid-animation.
                    if (Environment.GetEnvironmentVariable("MHO_GRIP") == "1" && ba != null)
                        foreach (var w in PropRig.Attached(mr, ModMeshes.List(rm), rc).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Class, "bike|motorcycle|cycle", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                            if (ModMeshes.Load(w.Ref, rc, out _) is { } bm)
                            {
                                var bseq = ModAnimations.For(w.Ref, bm.Bones, rm.Manifest.UpkReplacements.Select(f => (f, Path.Combine(rm.Folder, f))), rc, minBones: 1).FirstOrDefault(x => x.Name.Equals(ar!.Name, StringComparison.OrdinalIgnoreCase)) is { } br ? ModAnimations.Load(br) : null;
                                var bb = new MeshAnimator(bm.Bones, bm.Positions, bm.Normals, bm.Influences, bm.Tangents) { InPlace = false };
                                float mid = frames / 2;
                                anim8.Pose(ba, mid); bb.Pose(bseq, bseq == null ? 0 : MeshAnimator.Span(bseq).Frames / 2);
                                var hold = bm.MeshTransform * w.Offset * anim8.BoneMatrix(PropRig.BoneFor(anim8, w.Bone));
                                // MHO_GRIP_YAW=<degrees>: the vehicle turned by that much on its bone (testing an attachment's OffsetRotation).
                                if (float.TryParse(Environment.GetEnvironmentVariable("MHO_GRIP_YAW"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gyaw))
                                    hold = Fx.PowerEffects.Player.UeRotation(0, gyaw * MathF.PI / 180, 0) * hold;
                                Console.WriteLine($"  vehicle {w.Ref.Name} ({w.Class}) on {w.Bone}: bones " + string.Join(" ", bm.Bones.Select(x => x.Name)));
                                string? G(string side) => bm.Bones.Select(x => x.Name).FirstOrDefault(n => System.Text.RegularExpressions.Regex.IsMatch(n, $"(^|_){side}_(grip|handle|handlebar)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                                foreach (var (hand, grip) in new[] { ("g_l_palm", G("l") ?? "g_chopper_l_grip"), ("g_r_palm", G("r") ?? "g_chopper_r_grip") })
                                {
                                    var hp = anim8.BonePosition(anim8.BoneIndex(hand));
                                    var gp = System.Numerics.Vector3.Transform(bb.BonePosition(bb.BoneIndex(grip)), hold);
                                    Console.WriteLine($"  grip ({Environment.GetEnvironmentVariable("MHO_SINGLEKEY") ?? "auto"}): {hand} {hp.X:0.0},{hp.Y:0.0},{hp.Z:0.0}  {grip} {gp.X:0.0},{gp.Y:0.0},{gp.Z:0.0}  distance {System.Numerics.Vector3.Distance(hp, gp):0.0}");
                                }
                            }
                }
                else v.ShowMesh(ld);
                // MHO_RENDER_POWERS=1: the power's effects too (as the preview plays them), stepped at 30 fps to each frame shown.
                Fx.PowerEffects.Player? fxp = null; double fxt = 0; float secs = ba == null ? 0 : MeshAnimator.Span(ba).Seconds;
                if (Environment.GetEnvironmentVariable("MHO_RENDER_POWERS") == "1" && ar != null && rc != null)
                {
                    var parts = mr.Package.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    string hero = parts[2];
                    string sip = Path.GetFullPath(Path.Combine(rc, "..", "..", "..", "Data", "Game", "Calligraphy.sip"));
                    var db = new Fx.GameData(Fx.SipArchive.Load(sip));
                    var modFiles = rm.Manifest.UpkReplacements.Select(f => Path.Combine(rm.Folder, f)).ToList();
                    // MHO_RENDER_EXTRA=<file.upk;…>: packages read before the game's (a recoloured power package to look at).
                    if (Environment.GetEnvironmentVariable("MHO_RENDER_EXTRA") is string extra) modFiles.InsertRange(0, extra.Split(';', StringSplitOptions.RemoveEmptyEntries));
                    var idx = Fx.PowerIndex.For(hero, rc, modFiles, db);
                    if (idx.TryGetValue(ar.Name, out var pw) && pw.SelectMany(p => Fx.PowerIndex.PrototypesByClass(db).TryGetValue(p.Class, out var l) ? l : []).FirstOrDefault() is string proto)
                    {
                        string pcls = pw.First(p => Fx.PowerIndex.PrototypesByClass(db).TryGetValue(p.Class, out var l2) && l2.Contains(proto)).Class;
                        var pfx = Fx.PowerEffects.For(new Fx.FxGame(rc, modFiles), db, proto, hero);
                        var socks = Fx.FxSockets.Of(mr.File, mr.Name);
                        System.Numerics.Matrix4x4? Sock(string n) => socks.TryGetValue(n, out var sk) && anim8.BoneIndex(sk.Bone) is int b && b >= 0 ? sk.Local * anim8.BoneMatrix(b) : anim8.BoneIndex(n) is int bi && bi >= 0 ? anim8.BoneMatrix(bi) : null;
                        fxp = new Fx.PowerEffects.Player(pfx, Sock, new System.Numerics.Vector3(250, 0, ld.Positions.Min(q => q.Z)), Fx.PowerEffects.Player.PhaseFor(ar.Name, idx.Where(kv => kv.Value.Any(p => p.Class == pcls)).Select(kv => kv.Key))) { AnimSeconds = Math.Max(0.1f, secs) };
                        Console.WriteLine($"  power {Path.GetFileNameWithoutExtension(proto)}: {pfx.Effects.Count} effects, {pfx.Decals.Count} decals, {pfx.Meshes.Count} mesh emitters");
                        // Its animated actors, as the preview adds them to its rig.
                        var tgt = new System.Numerics.Vector3(250, 0, ld.Positions.Min(q => q.Z));
                        foreach (var sa in pfx.Actors)
                        {
                            var am = ModMeshes.Load(new MeshRef(Path.GetFileName(sa.MeshFile), sa.MeshFile, sa.Name, sa.MeshExport), rc, out string why);
                            if (am == null) { Console.WriteLine($"  actor {sa.Name}: no model ({why})"); continue; }
                            var aseq = sa.AnimName != null && ModAnimations.Named(sa.SetFile, sa.SetExport, sa.MeshFile, sa.AnimName) is { } aref ? ModAnimations.Load(aref) : null;
                            float own = aseq == null ? 0 : MeshAnimator.Span(aseq).Seconds;
                            if (fxp.ActorWindow(sa, own) is not { } win) continue;
                            var place = System.Numerics.Matrix4x4.CreateScale(sa.Scale) * Fx.PowerEffects.Player.UeRotation(sa.Turn.X, sa.Turn.Y, sa.Turn.Z)
                                * System.Numerics.Matrix4x4.CreateTranslation(sa.Shift + (sa.AtTarget ? tgt : System.Numerics.Vector3.Zero));
                            rrig.AddActor(am, new MeshAnimator(am.Bones, am.Positions, am.Normals, am.Influences, am.Tangents) { InPlace = false }, aseq, win.Start, win.End, place);
                            renderRig = rrig;
                            Console.WriteLine($"  actor {sa.Name}{(sa.TriggeredBy != null ? " (by " + sa.TriggeredBy + ")" : "")} [{sa.Point}+{sa.Offset:0.##}{(sa.EndPoint != null ? " to " + sa.EndPoint + "+" + sa.EndOffset.ToString("0.##") : "")}, {sa.Kind}]: {am.Positions.Length} vertices, {(aseq == null ? "no animation" : $"{sa.AnimName} {own:0.00} s")}, shown {win.Start:0.00}–{win.End:0.00} s");
                        }
                        if (pfx.Actors.Count > 0) v.ShowMesh(rrig.Combine(ld), ld.Positions.Length);
                        v.ZoomOut(1.6f);
                        v.EffectStrength = PreviewViews.FxPower;
                    }
                    else Console.WriteLine("  no power plays " + ar.Name);
                }
                v.Moving = Environment.GetEnvironmentVariable("MHO_RENDER_MOVING") == "1";   // timed as during playback
                foreach (float at in new[] { 0f, 0.33f, 0.66f, 1f })
                {
                    if (fxp != null)
                        for (; fxt + 1e-6 < secs * at; fxt += 1.0 / 30) { anim8.Pose(ba, (float)(fxt / secs * frames)); fxp.Step(1f / 30, false); }
                    anim8.Pose(ba, frames * at);
                    renderRig?.At(secs * at);
                    if (fxp != null) { v.Effects = fxp.Quads(); v.EffectTris = fxp.Tris(); Console.WriteLine($"    {v.Effects.Count} sprites, {v.EffectTris.Count} triangles"); }
                    rrig.Update(v, anim8);
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
        if (args.Length >= 2 && args[0].Equals("--color-picker-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // --color-picker-snapshot <out.png> [#RRGGBB]: the Powers tab's color picker drawn off-screen.
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Gui.Ui.UseDarkTheme();
            var c = args.Length > 2 && ColorMap.FromHex(args[2]) is { } v ? Color.FromArgb((int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255)) : Color.FromArgb(32, 255, 64);
            Gui.ColorPickerPopup.Snapshot(args[1], c);
            Console.WriteLine($"eyedropper read under the mouse: {(Gui.Eyedropper.UnderCursor() is { } u ? $"#{u.R:X2}{u.G:X2}{u.B:X2} at {Cursor.Position}" : "nothing")}");
            return 0;
        }
        if (args.Length == 5 && args[0].Equals("--powers-shot", StringComparison.OrdinalIgnoreCase))
        {
            // --powers-shot <out.png> <mod> <power name> <frame fraction 0-1> (scratch libraries only)
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.PowersShot(args[1], args[2], args[3], double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 5 && args[0].Equals("--keep-frame-test", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;   // scratch libraries only
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.KeepFrameTest(args[1], args[2], args[3], args[4]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 5 && args[0].Equals("--power-map-test", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;   // scratch libraries only
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.PowerMapTest(args[1], args[2], args[3], args[4]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 4 && args[0].Equals("--anim-find-test", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;   // scratch libraries only
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.AnimFindTest(args[1], args[2], args[3]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 6 && args[0].Equals("--anim-tab-test", StringComparison.OrdinalIgnoreCase))
        {
            // --anim-tab-test <dir> <mod> <slot> <donor title> <animation>  (scratch libraries only)
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.AnimTabTest(args[1], args[2], args[3], args[4], args[5]); main.Close(); });
            Application.Run(main);
            return 0;
        }
        if (args.Length == 6 && args[0].Equals("--voice-shift-tab-test", StringComparison.OrdinalIgnoreCase))
        {
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") == null) return 2;   // scratch libraries only
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            var main = new Gui.MainForm();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            main.Shown += (_, _) => main.BeginInvoke(async () => { await main.VoiceShiftTabTest(args[1], args[2], float.Parse(args[3], ci), float.Parse(args[4], ci), float.Parse(args[5], ci)); main.Close(); });
            Application.Run(main);
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
            bool wasSetUp = s.IsSetUp;
            if (!s.IsSetUp && args.Length == 0)
            {
                using var setup = new Gui.FirstRunForm(s);
                if (setup.ShowDialog() != DialogResult.OK) return 0;
            }
            var form = new Gui.MainForm { ShowsWhatsNew = wasSetUp && args.Length == 0 };
            if (args.Length >= 2) form.Shown += (_, _) => form.BeginInvoke(async () => { await form.Snapshot(args[1], args.Length == 3 ? args[2] : null); form.Close(); });
            Application.Run(form);
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--gamefiles-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Test: Changed Game Files rendered off-screen to a PNG (reads only; nothing is kept or restored).
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Gui.Ui.UseDarkTheme();
            var gs = Settings.Load();
            string? gd = Settings.LibraryData(gs.LibraryPath);
            if (gd == null || gs.ResolvedGameRoot(gd) is not string ggr) { Console.WriteLine("no library or game folder"); return 1; }
            var glib = ModLibrary.Load(gd);
            var ggame = new GameState(ggr, gd);
            StockFiles.Init(ggame, gs.CleanGameFiles, Path.Combine(gd, "originals"));
            using var f = new Gui.GameFilesForm(glib, ggame) { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-6000, -6000) };
            f.Shown += async (_, _) =>
            {
                await Task.Delay(300);
                using var bmp = new System.Drawing.Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, f.Size));
                bmp.Save(args[1]);
                f.Close();
            };
            f.ShowDialog();
            return 0;
        }
        if (args.Length == 2 && args[0].Equals("--downloads-snapshot", StringComparison.OrdinalIgnoreCase))
        {
            // Test: Settings → Download Counts rendered off-screen to a PNG (use a scratch MHO_EXTMM_HOME: it saves the total seen).
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Gui.Ui.UseDarkTheme();
            using var f = new Gui.DownloadsForm(Settings.Load()) { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-6000, -6000) };
            f.Shown += async (_, _) =>
            {
                for (int i = 0; i < 100 && !f.Controls[0].Controls[0].Text.Contains("Downloads", StringComparison.Ordinal) && !f.Controls[0].Controls[0].Text.Contains("Couldn't"); i++) await Task.Delay(100);
                using var bmp = new System.Drawing.Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, f.Size));
                bmp.Save(args[1]);
                f.Close();
            };
            Application.Run(f);
            return 0;
        }
        if (args.Length == 1 && args[0].Equals("--window-place-test", StringComparison.OrdinalIgnoreCase))
        {
            // Test (scratch MHO_EXTMM_HOME only): the main window's remembered monitor / size / position, built but never shown.
            AttachCliConsole();
            if (Environment.GetEnvironmentVariable("MHO_EXTMM_HOME") is not { Length: > 0 } wh || wh.Contains(@"\publish\data", StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine("refused: needs MHO_EXTMM_HOME on a scratch folder"); return 1; }
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            int wfail = 0;
            void WCheck(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) wfail++; }
            var second = Screen.AllScreens.FirstOrDefault(s => !s.Primary);
            var st = Settings.Load();
            if (second != null)
            {
                var a = second.WorkingArea;
                st.RememberWindow = true; st.WindowBounds = [a.X + 50, a.Y + 40, Math.Min(1200, a.Width - 100), Math.Min(700, a.Height - 80)]; st.WindowMaximized = false; st.Save();
                using (var f = new Gui.MainForm())
                    WCheck($"a window left on {second.DeviceName} ({a}) opens there, same size, not maximized: {f.Bounds}", f.StartPosition == FormStartPosition.Manual && f.WindowState == FormWindowState.Normal && a.Contains(f.Bounds) && f.Bounds.X == a.X + 50);
                st.WindowMaximized = true; st.Save();
                using (var f = new Gui.MainForm())
                    WCheck("left maximized there: it opens maximized on that monitor", f.WindowState == FormWindowState.Maximized && Screen.FromRectangle(f.Bounds).DeviceName == second.DeviceName);
                var p1 = Screen.PrimaryScreen!.WorkingArea;
                var moved = Gui.MainForm.OnScreenOf(new System.Drawing.Rectangle(p1.X + 100, p1.Y + 80, 1400, 850), second);
                WCheck($"maximized on {second.DeviceName} with its restored size still on the main monitor (the user's case): saved on {second.DeviceName}: {moved}", Rectangle.Intersect(a, moved) == moved);
            }
            else Console.WriteLine("(one monitor only: the second-monitor checks are skipped)");
            st.WindowBounds = [60000, 60000, 1200, 700]; st.Save();
            using (var f = new Gui.MainForm())
                WCheck("a spot on a monitor that's gone: maximized on the main monitor, as before", f.StartPosition == FormStartPosition.CenterScreen && f.WindowState == FormWindowState.Maximized);
            st.RememberWindow = false; st.WindowBounds = [0, 0, 1200, 700]; st.Save();
            using (var f = new Gui.MainForm())
                WCheck("Remember Window Position off: maximized on the main monitor", f.StartPosition == FormStartPosition.CenterScreen && f.WindowState == FormWindowState.Maximized);
            st.RememberWindow = true; st.WindowBounds = [10, 10, 1200, 700]; st.Save();
            using (var f = new Gui.MainForm())
            {
                f.WindowState = FormWindowState.Normal; f.Bounds = new System.Drawing.Rectangle(-40000, -40000, 1200, 700);
                typeof(Gui.MainForm).GetMethod("SaveWindowPlace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(f, null);
            }
            WCheck("an off-screen window (a test's) doesn't overwrite the saved place", Settings.Load().WindowBounds is [10, 10, 1200, 700]);
            Console.WriteLine(wfail == 0 ? "PASS" : $"{wfail} FAILED");
            return wfail == 0 ? 0 : 1;
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
        return null;
    }
}
