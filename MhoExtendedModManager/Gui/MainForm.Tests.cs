using System.Diagnostics;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The main window's self-tests and snapshot commands (--ui-selftest, --model-tab-test, --gui-snapshot ...).</summary>
sealed partial class MainForm
{
    /// <summary>
    /// --ui-selftest (scratch library only): tags, on/off, "All", undo / redo back to the original state.json, filter,
    /// grouping, and a screenshot of a tooltip. Writes selftest.txt and PNGs to <paramref name="dir"/>.
    /// </summary>
    public async Task UiSelfTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new List<string>();
        int fails = 0;
        void Check(string what, bool ok) { if (!ok) fails++; log.Add($"{(ok ? "ok  " : "FAIL")} {what}"); }
        if (lib == null || lib.Mods.Count < 3) { File.WriteAllText(Path.Combine(dir, "selftest.txt"), "no library"); return; }
        settings.ListSort = "priority"; settings.ListGroup = "none"; filter.Text = "";
        string original = ReadState();
        string a = lib.Mods[0].FolderName, b = lib.Mods[1].FolderName, c = lib.Mods[2].FolderName;
        Mod M(string n) => lib!.Find(n)!;
        bool aOn = M(a).Enabled;

        // Real mouse clicks (window messages) on a padlock and a checkbox, as a user makes them.
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        void Click(Point p)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();   // a leaked window whose .NET side is gone fails here, not by chance
            var lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            SendMessage(list.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
            SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp); // WM_LBUTTONUP
            Application.DoEvents();
        }
        IntPtr listHandle = list.Handle;
        int topIndex = list.Items.Cast<object>().ToList().FindIndex(o => o is Mod);
        var lockBefore = M(a).Lock;   // the library may have it locked already
        Click(list.PartCentre(topIndex, padlock: true));
        await Task.Delay(300);
        Check($"padlock click {(lockBefore == ModLock.None ? "locks" : "unlocks")} the top mod", M(a).Lock == (lockBefore == ModLock.None ? ModLock.Top : ModLock.None));
        Click(list.PartCentre(topIndex, padlock: true));
        await Task.Delay(300);
        Check("second padlock click puts it back", M(a).Lock == lockBefore);
        Click(list.PartCentre(topIndex, padlock: false));
        await Task.Delay(300);
        Check("checkbox click toggles", M(a).Enabled != aOn);
        Click(list.PartCentre(topIndex, padlock: false));
        await Task.Delay(300);
        Check("checkbox click again restores", M(a).Enabled == aOn);
        Check("the list keeps its window through the clicks", list.Handle == listHandle);
        {
            var pt = list.PartCentre(topIndex, padlock: false); pt.X -= (int)(200 * DeviceDpi / 96f);
            var lp = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            SendMessage(list.Handle, 0x0201, (IntPtr)1, lp); SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp);
            SendMessage(list.Handle, 0x0203, (IntPtr)1, lp); SendMessage(list.Handle, 0x0202, IntPtr.Zero, lp);   // WM_LBUTTONDBLCLK
            Application.DoEvents();
            await Task.Delay(500);
            Check("double-click opens the Editor tab", editor != null && pages.SelectedIndex == 1);
            CloseEditor(); pages.Select(0);
            // The Editor tab itself with nothing open: the highlighted mod opens (Kurt).
            string? highlighted = Selected?.FolderName;
            pages.Select(1);
            for (int t = 0; t < 3000 && editor == null; t += 100) { await Task.Delay(100); Application.DoEvents(); }
            Check("the Editor tab opens the highlighted mod", editor != null && highlighted != null && editor.Editing?.FolderName == highlighted);
            CloseEditor(); pages.Select(0);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Check("the list keeps its window through the double-click", list.Handle == listHandle);
        }
        // Drag and drop (a user's request): press on the third card, move with the button held over the top half of the
        // first, let go: it becomes first (unless the first is locked there). Undo puts it back.
        if (M(a).Lock == ModLock.None && M(c).Lock == ModLock.None)
        {
            int ci = list.Items.Cast<object>().ToList().FindIndex(o => o is Mod mm && mm.FolderName == c);
            var from = list.GetItemRectangle(ci); var to = list.GetItemRectangle(topIndex);
            IntPtr L(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
            int x0 = from.X + (int)(140 * DeviceDpi / 96f);
            int y0 = from.Y + (int)(12 * DeviceDpi / 96f);   // the name row (the second row has tag chips)
            SendMessage(list.Handle, 0x0201, (IntPtr)1, L(x0, y0));   // down
            foreach (int y in new[] { y0 - 12, (from.Y + to.Y) / 2, to.Y + 4 })
                SendMessage(list.Handle, 0x0200, (IntPtr)1, L(x0, y));                        // WM_MOUSEMOVE, MK_LBUTTON
            SendMessage(list.Handle, 0x0202, IntPtr.Zero, L(x0, to.Y + 4));                  // up
            Application.DoEvents();
            await Task.Delay(300);
            bool dragged = lib.Mods[0].FolderName == c && M(c).Priority == 0;
            Check("drag the third mod onto the top: it becomes first", dragged);
            if (dragged) { Undo(); Check("undo the drag: back in third place", M(c).Priority == 2 && lib.Mods[0].FolderName == a); }
        }

        Change("tag a", () => { ModLibrary.AddTag(M(a), "Selftest-One"); return true; });
        Change("tag b", () => { ModLibrary.AddTag(M(b), "Selftest-One"); ModLibrary.AddTag(M(b), "Selftest-Two"); return true; });
        Change("tag c", () => { ModLibrary.AddTag(M(c), "selftest-two"); return true; });   // other case: same tag
        Check("tags saved and reloaded", M(a).UserTags.SequenceEqual(["Selftest-One"]) && M(b).UserTags.Count == 2 && M(c).UserTags.SequenceEqual(["selftest-two"]) && ReadState().Contains("\"Tags\""));
        Check("AllTags merges case", lib.AllTags().Count(t => t.StartsWith("selftest", StringComparison.OrdinalIgnoreCase)) == 2);
        Toggle(M(a));
        Check("toggle", M(a).Enabled != aOn);
        Undo();
        Check("undo toggle", M(a).Enabled == aOn);
        Redo();
        Check("redo toggle", M(a).Enabled != aOn);
        Undo();

        // Note: typed, saved as the user's (one undo step), undone.
        SelectMod(a);
        string noteBefore = M(a).Note;
        noteBox.Text = "Test note" + "\r\n" + "line two";
        SaveNote();
        Check("note saved as yours", M(a).LocalNote == "Test note" + "\n" + "line two" && ReadState().Contains("Test note"));
        Undo();
        Check("undo note", M(a).Note == noteBefore && noteBox.Text.Replace("\r\n", "\n") == noteBefore.Replace("\r\n", "\n"));
        // An automatic tag taken off one mod is hidden there, and comes back when ticked again.
        var withAuto = lib.Mods.FirstOrDefault(x => x.AutoTags.Count > 0);
        if (withAuto != null)
        {
            string autoTag = withAuto.AutoTags[0], wn = withAuto.FolderName;
            Change("hide auto tag", () => { ModLibrary.RemoveTag(M(wn), autoTag); return true; });
            Check($"automatic tag \"{autoTag}\" hidden", !ModLibrary.HasTag(M(wn), autoTag) && M(wn).HiddenTags.Contains(autoTag));
            Change("show auto tag", () => { ModLibrary.AddTag(M(wn), autoTag); return true; });
            Check("and shown again", ModLibrary.HasTag(M(wn), autoTag) && M(wn).HiddenTags.Count == 0 && !M(wn).UserTags.Contains(autoTag));
        }

        filter.Text = "tag:selftest-one";
        Check("filter tag:selftest-one shows 2", shown.Count == 2);
        filter.Text = "#selftest-two";
        Check("filter #selftest-two shows b and c", shown.Select(m => m.FolderName).Order().SequenceEqual(new[] { b, c }.Order()));
        bool bOn = M(b).Enabled, cOn = M(c).Enabled;
        SetAllVisible();
        Check("All: every mod in the list changed together", M(b).Enabled == M(c).Enabled && shown.All(m => m.Enabled == M(b).Enabled));
        Undo();
        Check("undo All", M(b).Enabled == bOn && M(c).Enabled == cOn);
        filter.Text = "";

        settings.ListGroup = "tag"; FillList(null);
        var groups = list.Items.OfType<ModGroup>().Select(g => g.Name).ToList();
        log.Add("groups: " + string.Join(", ", groups));
        Check("grouped by tag: test tags and automatic ones, Untagged last", groups.Contains("Selftest-One") && groups.Count >= 3 && groups[^1] == "Untagged");
        Check("priority buttons off while grouped", priorityButtons.All(x => !x.Enabled));
        await Task.Delay(1500);
        using (var bmp = new Bitmap(Width, Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(Path.Combine(dir, "grouped.png")); }
        var first = list.Items.OfType<ModGroup>().First();
        list.Items.OfType<ModGroup>().ToList();
        collapsed.Add(first.Key); FillList(null);
        Check("fold a group", list.Items.OfType<ModGroup>().First().Collapsed);
        collapsed.Clear();
        settings.ListGroup = "none"; FillList(null);

        // Tooltip: shown on the Apply button, captured from the screen.
        tips.Show("Sample tooltip: the dark tip the buttons and cards show." + Environment.NewLine + "Second line.", applyButton, 0, -(int)(60 * DeviceDpi / 96f), 5000);
        await Task.Delay(700);
        var at = applyButton.PointToScreen(new Point(-(int)(300 * DeviceDpi / 96f), -(int)(70 * DeviceDpi / 96f)));
        using (var shot = new Bitmap((int)(500 * DeviceDpi / 96f), (int)(80 * DeviceDpi / 96f)))
        {
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(at, Point.Empty, shot.Size);
            shot.Save(Path.Combine(dir, "tooltip.png"));
        }
        tips.Hide(applyButton);

        while (undo.Count > 0) Undo();
        Check("undo everything: state.json back byte for byte", ReadState() == original);
        Check("redo stack holds the undone steps", redo.Count >= 4);
        log.Add(fails == 0 ? "All UI checks passed." : fails + " FAILED");
        File.WriteAllLines(Path.Combine(dir, "selftest.txt"), log);
    }

    /// <summary>
    /// --tooltip-audit (Kurt: tooltips on all buttons, now and later): every button in the main window, a new-mod editor,
    /// Extract, Create Post, Apply, Update and the first-run window that has no tooltip. Writes the list; exit code 1 if any.
    /// </summary>
    public async Task<int> TooltipAudit(string outFile)
    {
        var found = new List<string>();
        void Check(string where, Control root) { foreach (string b in Ui.MissingTips(root)) found.Add($"{where}: {b}"); }
        await Task.Delay(1500);
        Check("Main window", this);
        OpenEditor(null); pages.Select(1); await Task.Delay(800);
        if (editor != null) { foreach (int i in Enumerable.Range(0, editor.TabCount)) { editor.SelectTab(i); await Task.Delay(150); } Check("Editor", editor); }
        CloseEditor();
        pages.Select(2); EnsureExtract(); await Task.Delay(800);
        Check("Extract", extractHost);
        pages.Select(0);
        if (lib?.Mods.FirstOrDefault() is Mod m)
            using (var p = new PostForm(PostWriter.From(m), m.Folder, ModPost.Read(m.Folder), (_, _, _) => { })) Check("Create Post", p);
        using (var a = new ApplyForm("plan", () => Task.FromResult((true, "")), checks: true, review: true)) Check("Apply", a);
        if (lib != null && game != null) using (var gf = new GameFilesForm(lib, game)) Check("Changed Game Files", gf);
        using (var u = new UpdateForm(new Updater.Release(new Version(9, 9, 9), "extmm-v9.9.9", "", "", Updater.ReleasesPage, "", "", 0))) Check("Update", u);
        using (var fr = new FirstRunForm(settings)) Check("First-run setup", fr);
        File.WriteAllLines(outFile, found.Count == 0 ? ["Every button has a tooltip."] : found);
        return found.Count == 0 ? 0 : 1;
    }

    /// <summary>--editor-snapshot: every sub-tab of the Editor tab as PNG (layout check), for a new mod or the named one.</summary>
    public async Task EditorSnapshot(string dir, string? modName)
    {
        Directory.CreateDirectory(dir);
        var m = modName == null ? null : lib?.Find(modName);
        OpenEditor(m);
        pages.Select(1);
        for (int i = 0; i < editor!.TabCount; i++)
        {
            editor.SelectTab(i);
            await Task.Delay(editor.TabTitle(i) is "Packages" or "Store Images" ? 6000 : editor.TabTitle(i) is "Animations" or "Powers" ? 9000 : 1500);   // icon names / previews / 3D models load in the background
            using var b = new Bitmap(Width, Height);
            DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
            b.Save(Path.Combine(dir, $"editor_{i}_{editor.TabTitle(i).Replace(' ', '_')}.png"));
        }
        CloseEditor();
    }

    /// <summary>
    /// --model-tab-test (scratch library only): the editor on <paramref name="modName"/>, its Model tab, an MFF character built
    /// onto one of the mod's packages, Save Changes; then the saved mod must have that package changed and a Model folder.
    /// </summary>
    public async Task<int> ModelTabTest(string modName, string mff, string package, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        string pkgFile = Path.Combine(m.Folder, package);
        OpenEditor(m);
        pages.Select(1);
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == "Model", -1);
        if (tab < 0) { say("no Model tab (PreviewFeatures off?)"); return 1; }
        editor.SelectTab(tab);
        for (int i = 0; i < 50 && editor.ModelPageForTest == null; i++) await Task.Delay(100);
        if (editor.ModelPageForTest is not { } page) { say("the Model tab didn't open"); return 1; }
        string? builtFile = await page.TestBuild(mff, package, say);
        say("built into the draft: " + (builtFile != null));
        if (builtFile == null) { CloseEditor(); return 1; }
        if (page.TestHasRig && !await page.TestRigWatch(say)) { CloseEditor(); return 1; }
        string? liveRig = page.TestLiveRig;
        bool imageEdit = mff == "-" || Environment.GetEnvironmentVariable("MHO_TEST_EDITOR") != "1" || await page.TestImageEdit(say);   // (no source: the package-texture test ran instead)
        byte[] builtBytes = File.ReadAllBytes(builtFile);   // the work folder goes with the editor
        await editor.SaveForTest();
        await Task.Delay(500);
        var saved = lib?.Find(modName);
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        if (mff != "-") Check(page.TestUnbuilt, "the Model tab tells built and unbuilt changes apart");
        Check(imageEdit, "the image editor's save is taken in (MHO_TEST_EDITOR)");
        Check(saved != null, "the mod is still in the library");
        if (saved != null)
        {
            string after = Path.Combine(saved.Folder, package);
            Check(File.Exists(after) && File.ReadAllBytes(after).AsSpan().SequenceEqual(builtBytes), "its package is the build, byte for byte");
            Check(File.Exists(Path.Combine(saved.Folder, ModelWork.Folder, "state.json")), "Model/state.json is kept with the mod");
            Check(!Directory.Exists(Path.Combine(saved.Folder, ModelWork.Folder, "builds")), "no builds folder in the mod");
            Check(saved.Manifest.UpkReplacements.Contains(package, StringComparer.OrdinalIgnoreCase), "the manifest still lists the package");
            if (float.TryParse(Environment.GetEnvironmentVariable("MHO_TEST_SIZE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ts) && File.Exists(after))
            {
                // the Size slider: the costume's mesh component has the Scale (the game's × the slider)
                var sp = MhoPackageModifier.Package.Open(after);
                string stem = Path.GetFileNameWithoutExtension(package)[4..^3].ToLowerInvariant();
                int ci = Array.FindIndex(sp.Exports, e => sp.PathOf(e).Equals($"marvelgamecontent.default__{stem}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
                float? got = null;
                if (ci >= 0) { byte[] cd = sp.ReadExportBytes(sp.Exports[ci]).ToArray(); if (MhoPackageModifier.TagWalker.Walk(sp, cd, 16)?.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase)) is { } st) got = BitConverter.ToSingle(cd, st.ValueAt); }
                Check(got != null && Math.Abs(got.Value / ts - Math.Round(got.Value / ts, 3)) < 1e-3, $"the costume's mesh component has Scale {got?.ToString("0.###") ?? "none"} (size {ts * 100:0} %)");
                // Match Steps to Size: the movement animations the costume plays now come from its own set, at 1 / size
                var plays = CostumeAnims.Read(after, package, CostumeAnims.FilesFor(null, MhoExtendedModManager.Model.Settings.Current.CookedFolder));
                var moves = plays?.Anims.Where(x => MhoExtendedModManager.Model.StepRate.IsMovement(x.Name)).ToList() ?? [];
                int timed = 0;
                foreach (var mv in moves.Where(x => x.From.File != null && Path.GetFullPath(x.From.File).Equals(Path.GetFullPath(after), StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] md = sp.ReadExportBytes(sp.Exports[mv.Export]).ToArray();
                    if (MhoPackageModifier.TagWalker.Walk(sp, md, 4)?.FirstOrDefault(t => t.Name.Equals("RateScale", StringComparison.OrdinalIgnoreCase)) is { } rt && Math.Abs(BitConverter.ToSingle(md, rt.ValueAt) - 1 / ts) < 1e-4) timed++;
                }
                if (Math.Abs(ts - 1) > 1e-4) Check(moves.Count > 0 && timed == moves.Count, $"{timed} of the {moves.Count} movement animations it plays are its own, at {100 / ts:0} % speed");
            }
        }
        Check(!Directory.EnumerateDirectories(lib!.DataFolder, "model-work-*").Any(), "the editor's work folder is gone");
        if (liveRig != null && saved != null)
        {
            // a model without an armature: the mod keeps its rig for the hero built onto (rig files only); the live rig, which an
            // open Blender works on, outlives the editor
            string rigs = Path.Combine(saved.Folder, ModelWork.Folder, "rigs");
            var files = Directory.Exists(rigs) ? Directory.GetFiles(rigs, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(rigs, f)).ToList() : [];
            say("  kept in the mod: " + string.Join(", ", files));
            Check(files.Count > 0 && files.All(f => f.Contains(" on " + Path.GetFileNameWithoutExtension(package)) && MhoExtendedModManager.Model.AutoRig.IsRigFile(Path.GetFileName(f)))
                && files.Any(f => f.EndsWith("rigged.fbx")) && files.Any(f => f.EndsWith("rig.blend")), "the mod keeps the built hero's rig, rig files only");
            Check(File.Exists(Path.Combine(liveRig, "rigged.fbx")) && File.Exists(Path.Combine(liveRig, "rig.blend")), "the live rig (data/model/rigs) is still there for Blender");
        }
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// --model-tab-timing (scratch library only): the editor's tab named by MHO_TIMING_TAB (default Model) shown for a few
    /// seconds, then how often the UI thread got a turn (a 10 ms timer). An idle window gets about 64 a second; the Model tab
    /// got 5 while an empty filter box showed .NET's placeholder text (0.37.116 fix: Field.UseCueBanner). Fails under 40.
    /// </summary>
    public async Task<int> ModelTabTiming(string modName, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        OpenEditor(m);
        pages.Select(1);
        string want = Environment.GetEnvironmentVariable("MHO_TIMING_TAB") ?? "Model";
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == want, -1);
        if (tab < 0) { say("no tab " + want); return 1; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = 0, worst = 0, ticks = 0;
        using var probe = new System.Windows.Forms.Timer { Interval = 10 };
        probe.Tick += (_, _) => { long now = sw.ElapsedMilliseconds; worst = Math.Max(worst, now - last); last = now; ticks++; };
        probe.Start();
        editor.SelectTab(tab);
        await Task.Delay(4000);   // loading, thumbnails
        int fails = 0;
        for (int s = 1; s <= 3; s++)
        {
            worst = 0; ticks = 0;
            await Task.Delay(1000);
            say($"{want}, idle second {s}: {ticks} turns, longest gap {worst} ms");
            if (ticks < 40) fails++;
        }
        CloseEditor();
        say(fails == 0 ? "PASS the window stays responsive" : "FAIL the window is busy while idle");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// --model-blender-test (scratch library only): the Model tab's Blender round trip (see ModelPage.TestBlender), then Save
    /// Changes: the edit must be in the mod's Model folder with a relative path.
    /// </summary>
    public async Task<int> ModelBlenderTest(string modName, string mff, string package, string anim, Action<string> say)
    {
        if (lib?.Find(modName) is not Mod m) { say("no mod " + modName); return 1; }
        OpenEditor(m);
        pages.Select(1);
        int tab = Enumerable.Range(0, editor!.TabCount).FirstOrDefault(i => editor.TabTitle(i) == "Model", -1);
        if (tab < 0) { say("no Model tab (PreviewFeatures off?)"); return 1; }
        editor.SelectTab(tab);
        for (int i = 0; i < 50 && editor.ModelPageForTest == null; i++) await Task.Delay(100);
        if (editor.ModelPageForTest is not { } page) { say("the Model tab didn't open"); return 1; }
        string? rel = await page.TestBlender(mff, package, anim, say);
        int fails = 0;
        void Check(bool c, string what) { say((c ? "PASS " : "FAIL ") + what); if (!c) fails++; }
        Check(rel != null, "the save in Blender came back as an FBX edit of " + anim);
        if (rel == null) { CloseEditor(); return 1; }
        await editor.SaveForTest();
        await Task.Delay(500);
        var saved = lib?.Find(modName);
        // this source's edits (a library used by other tests holds other sources' edits too)
        string source = Path.GetFileNameWithoutExtension(mff);
        string? editsTxt = saved == null ? null : Directory.EnumerateFiles(Path.Combine(saved.Folder, ModelWork.Folder, "edits"), "edits.txt", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)!).StartsWith(source + " on ", StringComparison.OrdinalIgnoreCase));
        Check(editsTxt != null, "the mod's Model folder has the edits");
        if (editsTxt != null)
        {
            string text = File.ReadAllText(editsTxt);
            say("edits.txt: " + text.Replace("\n", " | "));
            Check(text.Contains($"anim:{anim}=", StringComparison.OrdinalIgnoreCase) && !text.Contains(':' + "\\"), "the edit names its FBX relative to the edits folder");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(editsTxt)!, rel)), "the edited FBX is in the mod");
            Check(File.Exists(Path.Combine(Path.GetDirectoryName(editsTxt)!, "blender.txt")), "the Blender link is kept (blender.txt)");
        }
        return fails == 0 ? 0 : 1;
    }

    /// <summary>--editor-save-test: opens a mod in the Editor, changes nothing, saves (use with MHO_EXTMM_HOME on a scratch library).</summary>
    public async Task EditorSaveTest(string modName)
    {
        var m = lib?.Find(modName);
        if (m == null) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), "no such mod"); return; }
        OpenEditor(m);
        pages.Select(1);
        var ed = editor!;
        await ed.SearchStringsForTest("Vision");
        string check = ed.StringsCheck();
        using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_strings.png")); }
        string? saved = await ed.SaveForTest();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mhoextmm_editor_test.txt"), (saved ?? "not saved") + Environment.NewLine + check);
    }

    /// <summary>--powers-shot (scratch libraries only): the Powers tab's preview on a power at a frame, as a PNG.</summary>
    public async Task PowersShot(string png, string modName, string power, double fraction)
    {
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            if (await editor!.PowersShot(power, fraction) is { } c)
                using (var b = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height)); b.Save(png); }
        }
        catch (Exception ex) { File.WriteAllText(png + ".txt", ex.ToString()); }
    }

    /// <summary>--keep-frame-test (scratch libraries only): the Powers tab keeps the frame slider's place across powers.</summary>
    public async Task KeepFrameTest(string dir, string modName, string a, string b)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.KeepFrameTest(a, b));
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>--power-map-test (scratch libraries only): a color replaced through the Powers tab, saved, and checked on
    /// disk (the manifest's PowerColors.Maps, the built packages and their colors).</summary>
    public async Task PowerMapTest(string dir, string modName, string power, string toHex)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.PowerMapTest(power, toHex));
            await Task.Delay(1500);
            using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(dir, "powers_tab.png")); }
            string? saved = await editor.SaveForTest();
            lines.Add($"{(saved != null ? "PASS" : "FAIL")} saved");
            m = lib!.Find(saved ?? modName)!;
            var pc = m.Manifest.PowerColors ?? [];
            var entry = pc.FirstOrDefault(x => x.Maps is { Count: > 0 });
            lines.Add($"{(entry != null && entry.Packages.Count > 0 ? "PASS" : "FAIL")} manifest: {entry?.Name}: maps {string.Join(", ", entry?.Maps?.Select(x => x.From + "→" + x.To) ?? [])}; packages {string.Join(", ", entry?.Packages ?? [])}");
            if (entry != null && game != null)
                foreach (string f in entry.Packages)
                {
                    var pal = PowerRecolor.Palette([Path.Combine(m.Folder, f)], game.Cooked);
                    lines.Add($"  {f}: {string.Join(" ", pal.Take(8).Select(x => ColorMap.Hex(x.Tint) + $" {x.Share * 100:0}%"))}");
                }
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>--anim-find-test (scratch libraries only): the Animations tab's Find, then a real click on the first match.</summary>
    public async Task AnimFindTest(string dir, string modName, string find)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            OpenEditor(m); pages.Select(1);
            lines.AddRange(await editor!.FindClickForTest(find));
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>
    /// --voice-shift-tab-test (scratch libraries only): Shift This Voice through the Editor, saved and checked on disk (the
    /// manifest's VoiceShifts, the pack, the voice set at …_mhoshift events), then Remove Shift, saved, and the costume package
    /// compared with the one before. Results in &lt;dir&gt;\result.txt.
    /// </summary>
    public async Task VoiceShiftTabTest(string dir, string modName, float pitch, float formant, float warmth)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        void Check(bool ok, string what) => Note((ok ? "PASS " : "FAIL ") + what);
        void Note(string l) { lines.Add(l); File.AppendAllText(Path.Combine(dir, "progress.txt"), l + Environment.NewLine); }
        try
        {
            var m = lib?.Find(modName) ?? throw new InvalidOperationException("no such mod");
            var before = m.Manifest.UpkReplacements.Where(f => f.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(m.Folder, f)));
            OpenEditor(m); pages.Select(1);
            if (await editor!.HeroVoiceForTest() is { } hint) Note("hint: " + hint);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await editor!.ShiftForTest(pitch, formant, warmth);
            Note($"shift: {sw.Elapsed.TotalSeconds:F1} s, note \"{editor.ShiftNote}\"");
            string? saved = await editor.SaveForTest();
            Check(saved != null, "saved after the shift");
            m = lib!.Find(saved ?? modName)!;
            var vs = m.Manifest.VoiceShifts ?? [];
            Check(vs.Count > 0 && vs.All(v => Math.Abs(v.Pitch - pitch) < 0.01f), $"manifest VoiceShifts ({vs.Count})");
            foreach (var v in vs)
            {
                string pack = VoiceShiftBuild.PackName(v.Package);
                Check(m.Manifest.AudioPacks.Any(a => a.Equals(pack, StringComparison.OrdinalIgnoreCase)) && File.Exists(Path.Combine(m.Folder, pack)), "pack " + pack);
                var set = VoiceSet.Read(v.Package, Path.Combine(m.Folder, v.Package), m.Manifest.VoiceOff ?? []);
                int shifted = set.Count(l => l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase));
                Check(shifted > 0, $"{v.Package}: {shifted} of {set.Count} voice set entries shifted");
            }
            Note("reopening");
            OpenEditor(m); pages.Select(1);
            Note("remove shift");
            editor!.RemoveShiftForTest();
            saved = await editor.SaveForTest();
            Check(saved != null, "saved after Remove Shift");
            m = lib!.Find(saved ?? modName)!;
            Check((m.Manifest.VoiceShifts?.Count ?? 0) == 0, "no VoiceShifts left");
            Check(!m.Manifest.AudioPacks.Any(a => a.StartsWith("VoiceShift_", StringComparison.OrdinalIgnoreCase)), "no shift pack left");
            foreach (var (f, b) in before)
            {
                var set = VoiceSet.Read(f, Path.Combine(m.Folder, f), m.Manifest.VoiceOff ?? []);
                Check(!set.Any(l => l.Event.EndsWith(VoiceShiftBuild.Suffix, StringComparison.OrdinalIgnoreCase)), f + ": voice set back at the original events");
                lines.Add($"{f}: {(File.ReadAllBytes(Path.Combine(m.Folder, f)).AsSpan().SequenceEqual(b) ? "identical to before" : "differs from before (the shifted copies' events stay in the package)")}");
            }
        }
        catch (Exception ex) { lines.Add("FAIL " + ex); }
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>
    /// --anim-tab-test (scratch libraries only): the Animations tab on a mod: the picker as PNG, a change through the tab
    /// (as Use This Animation), saved and checked on disk (the slot from a swapped-in set, the manifest's AnimSwaps), then
    /// Back to the Original, saved and checked again. Results in &lt;dir&gt;\result.txt.
    /// </summary>
    public async Task AnimTabTest(string dir, string modName, string slot, string donor, string anim)
    {
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        void Shot(string name) { using var b = new Bitmap(Width, Height); DrawToBitmap(b, new Rectangle(0, 0, Width, Height)); b.Save(Path.Combine(dir, name)); }
        async Task<ModEditorView?> Open(string name)
        {
            var m = lib == null ? null : ModLibrary.Load(lib.DataFolder).Find(name);
            if (m == null) return null;
            OpenEditor(m);
            pages.Select(1);
            var ed = editor!;
            for (int i = 0; i < ed.TabCount; i++) if (ed.TabTitle(i) == "Animations") ed.SelectTab(i);
            await Task.Delay(4000);
            return ed;
        }
        (bool Swapped, int Entries, string From) Check(string name)
        {
            var m = ModLibrary.Load(lib!.DataFolder).Find(name);
            if (m == null) return (false, -1, "no mod");
            string file = m.Manifest.UpkReplacements.First(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase));
            var ca = CostumeAnims.Read(Path.Combine(m.Folder, file), file, CostumeAnims.FilesFor(m, settings.ResolvedGameRoot(lib.DataFolder) is string r ? Settings.Cooked(r) : null));
            var a = ca?.Anims.FirstOrDefault(x => x.Name.Equals(slot, StringComparison.OrdinalIgnoreCase));
            return (a != null && a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase), m.Manifest.AnimSwaps?.Count ?? 0, a?.From.Path ?? "none");
        }
        var ed = await Open(modName);
        if (ed == null) { File.WriteAllText(Path.Combine(dir, "result.txt"), "no such mod"); return; }
        await ed.AnimPickerSnapshot(slot, donor, Path.Combine(dir, "picker.png"));
        lines.Add("change: " + await ed.AnimTestChange(slot, donor, anim));
        await Task.Delay(2500);
        Shot("tab_changed.png");
        string? saved = await ed.SaveForTest();
        await Task.Delay(1500);
        var c1 = Check(saved ?? modName);
        lines.Add($"saved '{saved}': {slot} swapped={c1.Swapped} from {c1.From}; AnimSwaps entries {c1.Entries}");
        var ed2 = await Open(saved ?? modName);
        if (ed2 == null) { lines.Add("can't reopen"); File.WriteAllLines(Path.Combine(dir, "result.txt"), lines); return; }
        Shot("tab_reopened.png");
        lines.Add("back: " + await ed2.AnimTestBack(slot));
        string? saved2 = await ed2.SaveForTest();
        await Task.Delay(1500);
        var c2 = Check(saved2 ?? modName);
        lines.Add($"saved '{saved2}': {slot} swapped={c2.Swapped} from {c2.From}; AnimSwaps entries {c2.Entries}");
        bool ok = c1.Swapped && c1.Entries == 1 && !c2.Swapped && c2.Entries == 0;
        // Copy From a Character: Storm's emotes of the same names at once.
        var ed3 = await Open(saved2 ?? modName);
        if (ed3 != null)
        {
            await ed3.AnimPickerSnapshot("*", "Storm", Path.Combine(dir, "picker_many.png"));
            lines.Add("copy many: " + await ed3.AnimTestCopyMany("Storm", "Emote"));
            Shot("tab_many.png");
            string? saved3 = await ed3.SaveForTest();
            await Task.Delay(1500);
            var m3 = ModLibrary.Load(lib!.DataFolder).Find(saved3 ?? modName);
            string file3 = m3!.Manifest.UpkReplacements.First(f => f.StartsWith("UC__", StringComparison.OrdinalIgnoreCase));
            var ca3 = CostumeAnims.Read(Path.Combine(m3.Folder, file3), file3, CostumeAnims.FilesFor(m3, settings.ResolvedGameRoot(lib.DataFolder) is string r3 ? Settings.Cooked(r3) : null));
            int swapped = ca3?.Anims.Count(a => a.From.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase)) ?? 0, entries = m3.Manifest.AnimSwaps?.Count ?? 0;
            lines.Add($"saved: {swapped} animations from a swapped-in set; AnimSwaps entries {entries}");
            ok &= swapped > 1 && swapped == entries;
        }
        lines.Add(ok ? "PASS" : "FAIL");
        File.WriteAllLines(Path.Combine(dir, "result.txt"), lines);
    }

    /// <summary>--extract-snapshot: the Extract tab on the store images, with one selected (layout check).</summary>
    public async Task ExtractSnapshot(string dir, string texture = "store_vision_classic")
    {
        Directory.CreateDirectory(dir);
        pages.Select(2);
        await Task.Delay(2000);
        if (extract != null) await extract.ShowForSnapshot(2, texture);
        using var b = new Bitmap(Width, Height);
        DrawToBitmap(b, new Rectangle(0, 0, Width, Height));
        b.Save(Path.Combine(dir, "extract.png"));
    }

    /// <summary>--gui-snapshot: waits for the package check and thumbnails, then saves the window as PNG (and each details tab).</summary>
    /// <summary>--preview-selftest: the 3D view's controls on one mod (use a scratch library: picks are saved).</summary>
    public async Task PreviewSelfTest(string dir, string modName)
    {
        Directory.CreateDirectory(dir);
        if (lib?.Find(modName) is not Mod pick) { File.WriteAllText(Path.Combine(dir, "preview_selftest.txt"), "FAIL no such mod"); return; }
        SelectMod(pick.FolderName);
        await Task.Delay(1500);
        var log = await storePreview.SelfTest();
        // Kurt: the view is kept when leaving the mod and coming back (camera, animation and its frame).
        if (await storePreview.PoseForTest() is { } posed && lib?.Mods.FirstOrDefault(x => x.FolderName != pick.FolderName) is Mod other)
        {
            float lightWas = PreviewViews.Light(pick), otherWas = PreviewViews.Light(other);
            PreviewViews.SetLight(pick, 1.4f); PreviewViews.SetLight(other, 1f);
            SelectMod(other.FolderName);
            await Task.Delay(1500);
            float? otherShown = storePreview.ShownLight;
            SelectMod(pick.FolderName);
            (string? Anim, double Time, float[] View)? back = null;
            for (int t = 0; t < 15000; t += 100) { await Task.Delay(100); back = storePreview.Shown3D(); if (back?.Anim == posed.Anim) break; }
            bool ok = back is { } b && b.Anim == posed.Anim && Math.Abs(b.Time - posed.Time) < 1e-3 && b.View.Zip(posed.View).All(p => Math.Abs(p.First - p.Second) < 1e-4);
            log.Add($"{(ok ? "ok  " : "FAIL")} another mod and back: same animation ({posed.Anim}), frame ({posed.Time:0.00} s) and camera" +
                (ok ? "" : $" (got {back?.Anim ?? "none"}, {back?.Time:0.00} s)"));
            bool lightOk = (otherShown == null || Math.Abs(otherShown.Value - 1f) < 1e-4) && storePreview.ShownLight is float mine && Math.Abs(mine - 1.4f) < 1e-4;
            log.Add($"{(lightOk ? "ok  " : "FAIL")} the light is per mod (the other mod at 100 %, this one back at 140 %)");
            PreviewViews.SetLight(pick, lightWas); PreviewViews.SetLight(other, otherWas);
        }
        else log.Add("FAIL couldn't pose the 3D view, or no other mod to switch to");
        log.Add(log.Any(l => l.StartsWith("FAIL")) ? $"{log.Count(l => l.StartsWith("FAIL"))} FAILED" : "All preview checks passed.");
        File.WriteAllLines(Path.Combine(dir, "preview_selftest.txt"), log);
    }

    public async Task Snapshot(string dir, string? modName = null)
    {
        Directory.CreateDirectory(dir);
        if (modName != null && lib?.Find(modName) is Mod pick) { SelectMod(pick.FolderName); list.TopIndex = Math.Max(0, list.SelectedIndex - 5); }
        foreach (var t in new[] { loading, pending }) if (t != null) { try { await t; } catch { } }
        // card pictures (stock ones open the icons package) decode in the background (MHO_EXTMM_SNAP_WAIT: ms, the manual's shots)
        await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("MHO_EXTMM_SNAP_WAIT"), out int wait) ? wait : 5000);
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        bmp.Save(Path.Combine(dir, "main.png"));
        // Each details tab too.
        for (int i = 1; i < 8; i++)
        {
            int before = tabs.SelectedIndex; tabs.Select(i); if (tabs.SelectedIndex != i) break;
            await Task.Delay(1200);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"main_tab{i}.png"));
        }
    }
}
