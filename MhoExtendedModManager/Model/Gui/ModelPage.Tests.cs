using System.Diagnostics;

namespace MhoExtendedModManager.Model.Gui;

sealed partial class ModelPage
{
    /// <summary>Test (--model-tab-test): the tab's own controls driven as a user would: the MFF character, one of the mod's
    /// packages, Build into Mod. The built file, or null when nothing was built into the draft.</summary>
    internal async Task<string?> TestBuild(string mff, string package, Action<string> say)
    {
        if (!await TestPick(mff, package, say)) return null;
        Build();
        for (int i = 0; i < 6000 && building; i++) await Task.Delay(100);
        say("log:\n  " + log.Text.Replace("\n", "\n  ").TrimEnd());
        return built.TryGetValue(package, out string? path) && File.Exists(path) ? path : null;
    }

    internal bool TestHasRig => unrigged != null;

    /// <summary>Test: the live rig folder (under data/model/rigs) of the shown unrigged source on the chosen hero; else null.</summary>
    internal string? TestLiveRig => unrigged != null && ChosenPackage is CharacterList.Item p ? AutoRig.Live(unrigged, p.Key) : null;

    /// <summary>Test: Blender's Ctrl+S on the rig (rigged.fbx written to a temporary file, then moved over it, as the save hook
    /// does) must reach the tab: the log says so and the preview reloads.</summary>
    internal async Task<bool> TestRigWatch(Action<string> say)
    {
        if (unrigged == null || ChosenPackage is not CharacterList.Item pkg) return false;
        string folder = AutoRig.Live(unrigged, pkg.Key), rigged = AutoRig.RiggedFbx(folder), tmp = Path.Combine(folder, "rigged.tmp.fbx");
        var rows = mapGrid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Cells[1].Value + " | " + r.Cells[3].Value).ToList();
        int fingers = rows.Count(r => System.Text.RegularExpressions.Regex.IsMatch(r, @"^g_[lr]_(thumb|index|birdy|ring|pinky)\d \|.*vertices"));
        say($"{(rows.Count > 20 && fingers >= 20 ? "PASS" : "FAIL")} the Bone Map lists the FBX's bones ({rows.Count} rows, {fingers} weighted finger bones; e.g. {rows.FirstOrDefault()})");
        if (rows.Count <= 20 || fingers < 20) return false;

        // the model's own normal map came over; an override file, then Undo
        string? normalRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Normal | "));
        bool own = normalRow?.Contains("The model's own") == true;
        say($"{(own ? "PASS" : "FAIL")} the model's own normal map is used ({normalRow})");
        string ovFile = Path.Combine(Path.GetDirectoryName(unrigged!)!, "override_normal_test.png");
        int pm = previewId, sm = preview.ShowCount;
        TestUseMapFile("Normal", ovFile);
        for (int i = 0; i < 300 && preview.ShowCount == sm; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && !undoButton.Enabled; i++) await Task.Delay(100);
        string? afterRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Normal | "));
        bool overridden = afterRow?.Contains("Your file: ") == true && OverridesFile() is string of && File.Exists(Path.Combine(Path.GetDirectoryName(of)!, Path.GetFileNameWithoutExtension(of), Directory.GetFiles(Path.Combine(Path.GetDirectoryName(of)!, Path.GetFileNameWithoutExtension(of))).Select(Path.GetFileName).First()!));
        say($"{(overridden ? "PASS" : "FAIL")} Use a File puts an override in, copied into the Model folder ({afterRow})");
        int su = preview.ShowCount;
        Undo();
        for (int i = 0; i < 300 && preview.ShowCount == su; i++) await Task.Delay(100);
        string? undoneRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Normal | "));
        bool backOwn = undoneRow?.Contains("The model's own") == true;
        say($"{(backOwn ? "PASS" : "FAIL")} Undo takes the override back ({undoneRow})");
        await Task.Delay(1500);
        if (!(own && overridden && backOwn)) return false;

        // no spec map of its own: generated (Soft); Next Recipe steps it, Undo takes it back
        string? specRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Spec | "));
        bool gen = specRow?.Contains("Generated from the color map: Soft") == true;
        int specIdx = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[1].Value == "Spec");
        matGrid.CurrentCell = matGrid.Rows[specIdx].Cells[0];
        MatSelectionChanged();
        bool recipeOn = matRecipe.Enabled;
        int sr = preview.ShowCount;
        matRecipe.Focus();   // (as a click: the focused button lost its state mid-refill and crashed the grid, 0.37.139)
        NextSpecRecipe();
        for (int i = 0; i < 300 && preview.ShowCount == sr; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && !undoButton.Enabled; i++) await Task.Delay(100);
        string? strongRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Spec | "));
        bool strong = strongRow?.Contains("Strong") == true;
        int su2 = preview.ShowCount;
        Undo();
        for (int i = 0; i < 300 && preview.ShowCount == su2; i++) await Task.Delay(100);
        bool softAgain = TestMaterialRows().FirstOrDefault(r => r.Contains(" | Spec | "))?.Contains("Soft") == true;
        say($"{(gen && recipeOn && strong && softAgain ? "PASS" : "FAIL")} a generated spec map (\"{specRow}\"), Next Recipe → \"{strongRow}\", Undo → Soft again: {softAgain}");
        await Task.Delay(1500);
        if (!(gen && recipeOn && strong && softAgain)) return false;

        // color group tags: an MHO spec map is made from them (the Metal template), Undo takes them off
        var (tm, ttex) = shownMaterials[0];
        var (cw, ch, cargb) = NormalMapGen.LoadArgb(ttex.Diffuse!);
        var cb = new byte[cw * ch * 4];
        for (int k = 0; k < cw * ch; k++) { cb[4 * k] = (byte)cargb[k]; cb[4 * k + 1] = (byte)(cargb[k] >> 8); cb[4 * k + 2] = (byte)(cargb[k] >> 16); cb[4 * k + 3] = (byte)(cargb[k] >> 24); }
        var smallMap = ColorTags.Small(cw, ch, cb);
        var grp = ColorTags.Groups(smallMap.W, smallMap.H, smallMap.Bgra);
        int st0 = preview.ShowCount;
        TestSetColorTags(tm, [(ColorTags.Hex(grp[0].Center), "cloth"), (ColorTags.Hex(grp[1].Center), "metal"), (ColorTags.Hex(grp[2].Center), "glow")]);
        for (int i = 0; i < 300 && preview.ShowCount == st0; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && !undoButton.Enabled; i++) await Task.Delay(100);
        string? mhoRow = TestMaterialRows().FirstOrDefault(r => r.Contains(" | MHO Spec | "));
        bool tagged = mhoRow?.Contains("Made from your color tags: 1 cloth, 1 metal, 1 glow") == true;
        string? tagGlowRow = TestMaterialRows().FirstOrDefault(r => r.StartsWith(tm + " | Glow | "));
        bool tagGlow = tagGlowRow?.Contains("From your color tags: 1 group(s) tagged Glow") == true && preview.LooksForTest.Any(l => l is { UseEmissive: true, EmissiveTex: not null });
        say($"{(tagGlow ? "PASS" : "FAIL")} a Glow tag makes the glow map (\"{tagGlowRow}\", in the preview)");
        tagged &= tagGlow;
        int st1 = preview.ShowCount;
        Undo();
        for (int i = 0; i < 300 && preview.ShowCount == st1; i++) await Task.Delay(100);
        bool untagged = TestMaterialRows().FirstOrDefault(r => r.Contains(" | MHO Spec | "))?.Contains("None") == true;
        say($"{(tagged && untagged ? "PASS" : "FAIL")} color tags make the MHO spec map (\"{mhoRow}\"), Undo takes them off: {untagged}");
        await Task.Delay(1500);
        if (!(tagged && untagged)) return false;

        // an MHO spec map in another layout (by its file name: v1), then Layout ▾ → Angela's; Undo twice → none
        string v1File = Path.Combine(Path.GetDirectoryName(unrigged!)!, "layout_test_specmultrimmaskreflection.png");
        File.Copy(ttex.Diffuse!, v1File, true);
        string? Mho() => TestMaterialRows().FirstOrDefault(r => r.StartsWith(tm + " | MHO Spec | "));
        async Task Step(Action a) { int s0 = preview.ShowCount; a(); for (int i = 0; i < 300 && preview.ShowCount == s0; i++) await Task.Delay(100); for (int i = 0; i < 300 && !undoButton.Enabled; i++) await Task.Delay(100); }
        int mhoIdx = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[1].Value == "MHO Spec");
        matGrid.CurrentCell = matGrid.Rows[mhoIdx].Cells[0];
        await Step(() => UseMapFile(v1File));
        string? v1Row = Mho();
        bool v1 = v1Row?.Contains("(v1), converted to Angela's") == true;
        MatSelectionChanged();
        bool layoutOn = matLayout.Enabled;
        await Step(() => TestSetSpecLayout(tm, "v2skin"));
        string? v2Row = Mho();
        bool v2 = v2Row?.Contains("Your file: ") == true && v2Row.Contains("converted") == false;
        await Step(Undo);
        bool backV1 = Mho()?.Contains("(v1), converted") == true;
        int su3 = preview.ShowCount;
        Undo();
        for (int i = 0; i < 300 && preview.ShowCount == su3; i++) await Task.Delay(100);
        bool none = Mho()?.Contains("None") == true;
        say($"{(v1 && layoutOn && v2 && backV1 && none ? "PASS" : "FAIL")} MHO spec layouts: \"{v1Row}\" (Layout ▾ on: {layoutOn}), set to Angela's → \"{v2Row}\", Undo → v1 again: {backV1}, Undo → none: {none}");
        await Task.Delay(1500);
        if (!(v1 && layoutOn && v2 && backV1 && none)) return false;

        // a glow map of the user's (the preview's Glow Map view shows it), No Glow turns it off; Undo twice → none again
        string? Glow() => TestMaterialRows().FirstOrDefault(r => r.StartsWith(tm + " | Glow | "));
        int glowIdx = matGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[0].Value == tm && (string)r.Cells[1].Value == "Glow");
        matGrid.CurrentCell = matGrid.Rows[glowIdx].Cells[0];
        string glowFile = Path.Combine(Path.GetDirectoryName(unrigged!)!, "glow_test.png");
        File.Copy(ttex.Diffuse!, glowFile, true);
        await Step(() => UseMapFile(glowFile));
        string? gRow = Glow();
        bool gMine = gRow?.Contains("Your file: ") == true;
        bool shownGlow = preview.LooksForTest.Any(l => l is { UseEmissive: true, EmissiveTex: not null });
        await Step(() => TestNoGlow(tm));
        string? offRow = Glow();
        bool gOff = offRow?.Contains("Off (No Glow)") == true && !preview.LooksForTest.Any(l => l is { UseEmissive: true });
        await Step(Undo);
        int su4 = preview.ShowCount;
        Undo();
        for (int i = 0; i < 300 && preview.ShowCount == su4; i++) await Task.Delay(100);
        bool gNone = Glow()?.Contains("None") == true;
        say($"{(gMine && shownGlow && gOff && gNone ? "PASS" : "FAIL")} glow: \"{gRow}\" (in the preview: {shownGlow}), No Glow → \"{offRow}\", Undo twice → none: {gNone}");
        await Task.Delay(1500);
        if (!(gMine && shownGlow && gOff && gNone)) return false;

        // double-click's image (a generated normal map counts) and Export Maps (named as the FBX import reads them)
        string? normalImg = TestMapImage("Normal"), specImg = TestMapImage("Spec");
        string exportDir = Path.Combine(Path.GetDirectoryName(unrigged!)!, "maps_export_test");
        if (Directory.Exists(exportDir)) Directory.Delete(exportDir, true);
        Directory.CreateDirectory(exportDir);
        int exported = await TestExportMaps(exportDir);
        string safe = FbxExport.SafeName(tm);
        bool namesOk = File.Exists(Path.Combine(exportDir, safe + ".png")) && File.Exists(Path.Combine(exportDir, safe + "_n.png")) && File.Exists(Path.Combine(exportDir, safe + "_sp.png"));
        bool viewOk = normalImg != null && File.Exists(normalImg) && specImg != null && File.Exists(specImg);
        say($"{(viewOk && exported >= 3 && namesOk ? "PASS" : "FAIL")} the map views (normal {Path.GetFileName(normalImg)}, spec {Path.GetFileName(specImg)}) and Export Maps ({exported} files: {string.Join(", ", Directory.GetFiles(exportDir).Select(Path.GetFileName).Take(6))})");
        if (!(viewOk && exported >= 3 && namesOk)) return false;

        // Smooth Weights on an FBX source (its map holds only smoothing), then Undo and Redo
        int elbow = mapGrid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (string)r.Cells[1].Value == "g_l_elbow");
        if (elbow < 0) { say("FAIL no g_l_elbow row"); return false; }
        mapGrid.CurrentCell = mapGrid.Rows[elbow].Cells[1];
        MapSelectionChanged();
        bool enabled = mapSmooth.Enabled;
        // a camera of the user's own: Smooth Weights must keep it (it re-framed the view before)
        var cam = preview.ViewForTest; cam[0] += 0.7f; cam[1] = 0.3f; cam[2] *= 0.6f; preview.ViewForTest = cam;
        cam = preview.ViewForTest;
        int Passes() => MapPath() is string m && File.Exists(m) ? BoneMapFile.Load(m).Smooth.FirstOrDefault(e => e.Bone == "g_l_elbow")?.Passes ?? 0 : 0;
        int was = Passes();
        int p0 = previewId, shows = preview.ShowCount;
        mapSmooth.Focus();
        SmoothSelected();
        for (int i = 0; i < 300 && previewId == p0; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && preview.ShowCount == shows; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && !undoButton.Enabled; i++) await Task.Delay(100);
        var after = preview.ViewForTest;
        bool sameCam = cam.Zip(after).All(x => MathF.Abs(x.First - x.Second) < 1e-3f);
        say($"{(sameCam ? "PASS" : "FAIL")} Smooth Weights keeps the camera (before {string.Join(" ", cam.Select(v => v.ToString("0.###")))}, after {string.Join(" ", after.Select(v => v.ToString("0.###")))})");
        if (!sameCam) return false;
        string? mp = MapPath();
        bool saved = Passes() == was + 1;
        say($"{(enabled && saved && undoButton.Enabled ? "PASS" : "FAIL")} Smooth Weights works on an FBX source (button enabled: {enabled}, saved in the map: {saved}, an undo step: {undoButton.Enabled})");
        Undo();
        for (int i = 0; i < 50 && Passes() != was; i++) await Task.Delay(100);
        bool undone = Passes() == was;
        await Task.Delay(1500);
        Redo();
        for (int i = 0; i < 50 && Passes() != was + 1; i++) await Task.Delay(100);
        bool redone = Passes() == was + 1;
        say($"{(undone && redone ? "PASS" : "FAIL")} Undo takes the smoothing off, Redo puts it back (undone: {undone}, redone: {redone})");
        await Task.Delay(1500);
        if (!(enabled && saved && undone && redone)) return false;
        int before = previewId;
        File.Copy(rigged, tmp, true);
        File.Move(tmp, rigged, true);
        for (int i = 0; i < 100 && !log.Text.Contains("Blender sent the rig"); i++) await Task.Delay(100);
        bool heard = log.Text.Contains("Blender sent the rig");
        for (int i = 0; i < 100 && previewId == before; i++) await Task.Delay(100);
        for (int i = 0; i < 300 && !status.Text.StartsWith("Rig from Blender loaded"); i++) await Task.Delay(100);
        bool shown = status.Text.StartsWith("Rig from Blender loaded") && log.Lines.LastOrDefault(l => l.Trim().Length > 0)?.StartsWith("Blender: the rig you saved") == true;
        say($"{(heard && previewId != before && shown ? "PASS" : "FAIL")} Blender's Ctrl+S on the rig reaches the tab (log: {heard}, preview reloaded: {previewId != before}, status line: \"{status.Text}\")");
        return heard && previewId != before && shown;
    }

    async Task<bool> TestPick(string mff, string package, Action<string> say)
    {
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        {
            // no source, a target picked: the target's own model, Compare lit and locked (after the tab's own restore of the last
            // source has finished: it loads in the background)
            for (int i = 0; i < 300 && preview.ShowCount == 0; i++) await Task.Delay(100);
            for (int last = -1, i = 0; i < 60 && last != preview.ShowCount; i++) { last = preview.ShowCount; await Task.Delay(1500); }
            chosenKey = null; model = null; sourceFbx = null; unrigged = null; parts.Rows.Clear(); characters.ClearSelected();   // (a source still selected wouldn't fire again)
            int sc = preview.ShowCount;
            Reselect(packages, package);
            SchedulePreview();
            for (int i = 0; i < 600 && preview.ShowCount == sc; i++) await Task.Delay(100);
            var c = preview.CompareForTest;
            bool ok = c.Lit && !c.Enabled && c.TargetOnly && preview.AnimationNames.Count > 0;
            say($"{(ok ? "PASS" : "FAIL")} with no source, the target's own model shows (Compare lit {c.Lit}, locked {!c.Enabled}, {preview.AnimationNames.Count} animations)");
            if (!ok) return false;
        }
        if (mff.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && File.Exists(mff))
        {
            // an FBX with another skeleton family (Mixamo …): Source → FBX Files, then the file, as a pick from the list
            string key = "fbx:" + Path.GetFullPath(mff);
            if (chosenKey != key || !HasSource)
            {
                if (!FbxMode) { sourceKind.SelectedIndex = 1; await Task.Delay(300); }
                FbxChosen(key);
                for (int i = 0; i < 1200 && !(chosenKey == key && HasSource); i++) await Task.Delay(100);
            }
            if (unrigged != null) say($"FBX {Path.GetFileName(mff)}: no armature (rigged in Blender per base hero), {parts.Rows.Count} mesh(es)");
            else if (model == null && sourceFbx == null) { say("the FBX didn't load as a model (no known skeleton family?): " + mff + "\n  " + log.Text.Replace("\n", "\n  ").TrimEnd()); return false; }
            else say($"FBX {Path.GetFileName(mff)}: {model?.Profile ?? "MHO bone names"} skeleton, {parts.Rows.Count} parts");
        }
        else if (chosenKey != mff || model == null)
        {
            if (FbxMode) { sourceKind.SelectedIndex = 0; await Task.Delay(300); }   // back to MFF characters (an FBX test left it on FBX)
            characterFilter.Text = mff;
            await Task.Delay(500);
            Reselect(characters, mff);
            for (int i = 0; i < 1200 && !(chosenKey == mff && model != null); i++) await Task.Delay(100);
        }
        if (!HasSource) { say($"the source didn't load: {mff} (picked: {chosenKey ?? "nothing"}, {characters.Items.Count} rows in the list)\n  " + log.Text.Replace("\n", "\n  ").TrimEnd()); return false; }
        say($"source {mff}: {parts.Rows.Count} parts");
        Reselect(packages, package);
        for (int i = 0; i < 1800 && preview.AnimationNames.Count == 0; i++) await Task.Delay(100);   // (an unrigged source is rigged in Blender first)
        await Task.Delay(1500);
        say($"cape / hair lists on {package}: {TestOwnRigs}");
        say("package: " + ChosenPackage?.Key + " from " + StartLabel(package) + $", {preview.AnimationNames.Count} animations");
        // MHO_TEST_MAPSHOTS=<png>: one strip of the preview in every Preview Shows view (a visual check of the map views)
        if (Environment.GetEnvironmentVariable("MHO_TEST_MAPSHOTS") is { Length: > 0 } shots)
        {
            await Task.Delay(1500);
            const int tw = 260, th = 420;
            using var strip = new Bitmap(tw * ShowMapChoices.Length, th + 24);
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(24, 24, 28));
                for (int k = 0; k < ShowMapChoices.Length; k++)
                {
                    preview.ShowMap = ShowMapChoices[k].Mode;
                    await Task.Delay(400);
                    using var b = preview.SnapshotForTest(tw, th);
                    if (b != null) g.DrawImage(b, k * tw, 24);
                    g.DrawString(ShowMapChoices[k].Label, SystemFonts.DefaultFont, Brushes.White, k * tw + 6, 5);
                }
            }
            preview.ShowMap = MhoExtendedModManager.Gui.ModelView.MapView.All;
            strip.Save(shots);
            say("map views: " + shots);
        }
        return ChosenPackage?.Key.Equals(package, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Test (--model-blender-test): the Blender round trip with the tab's own code and a real Blender, headless (no window):
    /// Single Animation → Export FBX of <paramref name="anim"/>, the scene built by the tab's open_in_blender.py
    /// (blender -b), then a second Blender opens model.blend, turns g_head in that animation and saves (Ctrl+S): the tab's
    /// watcher must take the sync in as an FBX edit of that animation. The edit (relative to the edits folder) or null.
    /// </summary>
    internal async Task<string?> TestBlender(string mff, string package, string anim, Action<string> say)
    {
        if (!await TestPick(mff, package, say)) return null;
        string? name = preview.AnimationNames.FirstOrDefault(n => n.Equals(anim, StringComparison.OrdinalIgnoreCase));
        if (name == null) { say($"no animation {anim} (first ones: {string.Join(", ", preview.AnimationNames.Take(8))})"); return null; }
        string? exe = BlenderLaunch.Find();
        if (exe == null) { say("no Blender"); return null; }
        say("Blender: " + BlenderLaunch.Describe(exe));

        lastZip = null;
        ExportFbx(false, name);
        await Task.Delay(200);
        for (int i = 0; i < 3000 && building; i++) await Task.Delay(100);
        if (lastZip == null || Path.GetDirectoryName(lastZip) is not string outDir) { say("the export failed:\n  " + log.Text.Replace("\n", "\n  ").TrimEnd()); return null; }
        string animFbx = Path.Combine(outDir, "anims", FbxExport.SafeName(name) + ".fbx");
        say($"exported: {outDir} (model.fbx {File.Exists(Path.Combine(outDir, "model.fbx"))}, {Path.GetFileName(animFbx)} {File.Exists(animFbx)})");

        // the scene, as Open in Blender makes it (here in the background: Open starts a window)
        LinkBlender(outDir);
        string script = BlenderLaunch.WriteScript(outDir);
        var (code1, out1) = await RunBlender(exe, $"-b --python \"{script}\"", outDir);
        say($"scene: exit {code1}; " + string.Join(" | ", out1.Split('\n').Where(l => l.Contains("MHO MFF Importer") || l.Contains("Error") || l.Contains("Traceback")).Take(6)));
        string blend = Path.Combine(outDir, "model.blend");
        if (!File.Exists(blend)) { say("no model.blend:\n" + out1); return null; }

        // Ctrl+S after an edit: g_head turned in that animation's action (-y: the file's sync script runs, as when
        // Blender is allowed to run the scene's scripts)
        string edit = Path.Combine(outDir, "test_edit.py");
        File.WriteAllText(edit, $$"""
import bpy, math
arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
track = next(t for t in arm.animation_data.nla_tracks if t.name == {{Py(FbxExport.SafeName(name))}})
act = track.strips[0].action
def curves(a):
    out = []
    for layer in getattr(a, "layers", []):
        for strip in layer.strips:
            for cb in getattr(strip, "channelbags", []):
                out.extend(cb.fcurves)
    if not out:
        try: out = list(a.fcurves)
        except Exception: pass
    return out
n = 0
for fc in curves(act):
    if fc.data_path == 'pose.bones["g_head"].rotation_quaternion' and fc.array_index == 3:
        for k in fc.keyframe_points:
            k.co[1] += 0.25; k.handle_left[1] += 0.25; k.handle_right[1] += 0.25; n += 1
print("TEST EDIT: keys changed", n)
# and a bone moved in Edit Mode (0.37.159: bone edits from this scene count): g_l_elbow 3 % of the model's size up
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="EDIT")
eb = arm.data.edit_bones["g_l_elbow"]
d = max(arm.dimensions) / max(arm.scale) * 0.03
for c in eb.children:
    c.use_connect = False
eb.head.z += d; eb.tail.z += d
bpy.ops.object.mode_set(mode="OBJECT")
print("TEST EDIT: g_l_elbow moved", d)
bpy.ops.wm.save_mainfile()
""");
        var (code2, out2) = await RunBlender(exe, $"-b \"{blend}\" -y --python \"{edit}\"", outDir);
        say($"edit + save: exit {code2}; " + string.Join(" | ", out2.Split('\n').Where(l => l.Contains("TEST EDIT") || l.Contains("MHO") || l.Contains("Error") || l.Contains("Traceback")).Take(8)));
        string sync = Path.Combine(outDir, "from_blender", "sync.json");
        say("sync.json: " + (File.Exists(sync) ? File.ReadAllText(sync).Replace("\n", " ") : "missing"));

        // the watcher (0.7 s after the file settles)
        // (this save's sync: an edit left from an earlier run doesn't count)
        for (int i = 0; i < 150 && !log.Text.Contains("Blender sent"); i++) await Task.Delay(100);
        say("log:\n  " + log.Text.Replace("\n", "\n  ").TrimEnd());
        if (!EnsureEdits().Anims.TryGetValue(name, out var kept) || !File.Exists(kept)) return null;
        // the moved bone came along in the model (and only it)
        if (EnsureEdits().ModelFbx is not string mfbx || !File.Exists(mfbx)) { say("FAIL: the moved bone sent no model"); return null; }
        var moved = FbxReimport.MovedBones(Path.Combine(outDir, "model.fbx"), mfbx);
        say("bones moved in the sent model: " + string.Join(", ", moved.Select(m => $"{m.Bone} {m.By:0.00}")));
        if (moved.Count != 1 || !moved[0].Bone.Equals("g_l_elbow", StringComparison.OrdinalIgnoreCase)) { say("FAIL: expected g_l_elbow alone"); return null; }
        return Path.GetRelativePath(EditsFolder()!, kept);
    }

    static string Py(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static async Task<(int Code, string Output)> RunBlender(string exe, string args, string dir)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { p.Kill(true); }
        return (p.HasExited ? p.ExitCode : -1, await o + await e);
    }
}
