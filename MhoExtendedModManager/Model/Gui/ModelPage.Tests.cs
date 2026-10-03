using System.Diagnostics;

namespace MhoMffImporter.Gui;

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

    async Task<bool> TestPick(string mff, string package, Action<string> say)
    {
        for (int i = 0; i < 100 && !loaded; i++) await Task.Delay(100);
        if (chosenKey != mff || model == null)
        {
            characterFilter.Text = mff;
            await Task.Delay(500);
            Reselect(characters, mff);
            for (int i = 0; i < 1200 && !(chosenKey == mff && model != null); i++) await Task.Delay(100);
        }
        if (model == null) { say("the MFF model didn't load: " + mff); return false; }
        say($"model {mff}: {parts.Rows.Count} parts");
        Reselect(packages, package);
        for (int i = 0; i < 600 && preview.AnimationNames.Count == 0; i++) await Task.Delay(100);
        say("package: " + ChosenPackage?.Key + " from " + StartLabel(package) + $", {preview.AnimationNames.Count} animations");
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
bpy.ops.wm.save_mainfile()
""");
        var (code2, out2) = await RunBlender(exe, $"-b \"{blend}\" -y --python \"{edit}\"", outDir);
        say($"edit + save: exit {code2}; " + string.Join(" | ", out2.Split('\n').Where(l => l.Contains("TEST EDIT") || l.Contains("MHO") || l.Contains("Error") || l.Contains("Traceback")).Take(8)));
        string sync = Path.Combine(outDir, "from_blender", "sync.json");
        say("sync.json: " + (File.Exists(sync) ? File.ReadAllText(sync).Replace("\n", " ") : "missing"));

        // the watcher (0.7 s after the file settles)
        for (int i = 0; i < 100 && !(EnsureEdits().Anims.TryGetValue(name, out var f) && File.Exists(f)); i++) await Task.Delay(100);
        say("log:\n  " + log.Text.Replace("\n", "\n  ").TrimEnd());
        if (!EnsureEdits().Anims.TryGetValue(name, out var kept) || !File.Exists(kept)) return null;
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
