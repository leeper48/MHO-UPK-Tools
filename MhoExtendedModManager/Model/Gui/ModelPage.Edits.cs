using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>The Model tab's FBX edits from Blender (kept per source and base package).</summary>
sealed partial class ModelPage
{
    // --- edits from Blender (0.16.0, Kurt) --------------------------------------------------------------------------------------
    /// <summary>The FBX edits of the shown source on the shown base package (kept in its edits folder, edits.txt, like the bone
    /// map: they're there again next time).</summary>
    AnimEdits edits = new();
    string? editsKey;

    /// <summary>The FBX edits of this source on this base hero (an FBX source's too, Kurt 2026-10-04: Open in Blender was grayed
    /// out for them).</summary>
    string? EditsFolder() => (model?.Folder ?? (sourceFbx != null ? ImportBuild.SourceName(sourceFbx) : null)) is string key && ChosenPackage is CharacterList.Item pkg
        ? Path.Combine(host.WorkFolder, "edits", $"{key} on {Path.GetFileNameWithoutExtension(pkg.Key)}") : null;

    AnimEdits EnsureEdits()
    {
        string? folder = EditsFolder();
        if (folder == editsKey) return edits;
        editsKey = folder;
        string file = folder != null ? Path.Combine(folder, "edits.txt") : "";
        edits = folder != null && File.Exists(file) ? AnimEdits.Parse(File.ReadAllText(file)).Resolved(folder) : new AnimEdits();
        return edits;
    }

    void SaveEdits()
    {
        if (EditsFolder() is not string folder) return;
        Protected.CheckWrite(folder);
        string file = Path.Combine(folder, "edits.txt");
        if (!edits.Any) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, edits.Relative(folder).Serialize());   // relative: the folder travels with the mod
    }

    void FillEditsMenu(ContextMenuStrip m)
    {
        EnsureEdits();
        if (!HasSource || ChosenPackage == null) { m.Items.Add(new ToolStripMenuItem("Pick a Source and a Base Hero First") { Enabled = false }); return; }
        string? anim = preview.CurrentAnimation;
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Import FBX for \"{anim}\"" : "Import FBX (Pick an Animation to Replace One)", null, (_, _) => ImportEditFbx(anim)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Export FBX: \"{anim}\" Only" : "Export FBX: Pick an Animation First", null, (_, _) => ExportFbx(false, anim)) { Enabled = anim != null && !building, ToolTipText = "The model and just this animation (the right panel's Export FBX: all of them)." });
        m.Items.Add(new ToolStripMenuItem(anim != null ? $"Open \"{anim}\" in Blender" : "Open in Blender: Pick an Animation First", null, (_, _) => ExportFbx(true, anim)) { Enabled = anim != null && !building, ToolTipText = "The model and just this animation in a new Blender scene; Ctrl+S there sends your changes back (the right panel's Open in Blender: all of them)." });
        m.Items.Add(new ToolStripSeparator());
        if (anim != null && edits.Anims.ContainsKey(anim)) m.Items.Add(new ToolStripMenuItem($"Back to the Game's \"{anim}\"", null, (_, _) => { edits.Anims.Remove(anim); EditsChanged($"\"{anim}\" is the game's again"); }));
        if (edits.ModelFbx != null) m.Items.Add(new ToolStripMenuItem("Back to the Retargeted Mesh", null, (_, _) => { edits.ModelFbx = null; EditsChanged("the mesh is the retarget's again"); }));
        if (edits.Anims.Count > 0)
        {
            var list = new ToolStripMenuItem($"Replaced Animations ({edits.Anims.Count})");
            foreach (var (name, file) in edits.Anims) list.DropDownItems.Add(new ToolStripMenuItem($"{name}  ←  {Path.GetFileName(file)}") { Enabled = false });
            m.Items.Add(list);
        }
        if (edits.Any)
        {
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Revert All FBX Edits", null, (_, _) => { if (Dialog.Show(this, "Revert every FBX edit of this model on this base hero (the mesh and the replaced animations)? Undo brings them back.", "Revert All FBX Edits", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return; edits = new AnimEdits(); EditsChanged("every FBX edit reverted"); }));
            m.Items.Add(new ToolStripMenuItem("Open the Edits Folder", null, (_, _) => { if (EditsFolder() is string f && Directory.Exists(f)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{f}\"") { UseShellExecute = false }); }));
        }
    }

    void EditsChanged(string what)
    {
        SaveEdits();
        Log("FBX edits: " + what + ".");
        preview.ReloadAnimation();
        SchedulePreview();   // the mesh may have changed; also records the undo step
    }

    /// <summary>
    /// An FBX from Blender for the animation <paramref name="anim"/> (null: the mesh only): its clip replaces that animation
    /// and / or its skinned mesh replaces the model's, as asked. Clip bones are checked against the base hero's skeleton.
    /// </summary>
    void ImportEditFbx(string? anim)
    {
        if (EditsFolder() is not string folder || ChosenPackage is not CharacterList.Item pkg) return;
        string exported = Path.Combine(Settings.Home, "fbx", $"{model!.Folder} on {Path.GetFileNameWithoutExtension(pkg.Key)}");
        string start = Directory.Exists(Path.Combine(exported, "anims")) && anim != null ? Path.Combine(exported, "anims") : Directory.Exists(exported) ? exported : Settings.Home;
        using var dlg = new OpenFileDialog { Title = anim != null ? $"FBX for {anim}" : "FBX with the edited mesh", Filter = "FBX (*.fbx)|*.fbx", InitialDirectory = start };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AnimEdits.Contents c;
        try { c = AnimEdits.Inspect(dlg.FileName); }
        catch (Exception ex) { Dialog.Show(this, ex.Message, "FBX Not Read", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        bool hasClip = c.Clips.Count > 0 && anim != null, hasMesh = c.SkinnedMeshes > 0;
        if (!hasClip && !hasMesh)
        {
            Dialog.Show(this, anim == null && c.Clips.Count > 0 ? "This FBX has an animation but no skinned mesh. Pick the animation to replace in the drop-down first." : "This FBX has neither an animation nor a skinned mesh.", "Nothing to Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        bool useClip = hasClip, useMesh = hasMesh;
        if (hasClip && hasMesh)
        {
            int pick = Dialog.Choose(this, $"{Path.GetFileName(dlg.FileName)} has an animation ({c.Clips[0].Name}, {c.Clips[0].Frames:0} frames) and a skinned mesh ({c.SkinnedMeshes} part(s)).\n\nThe animation replaces \"{anim}\". The mesh, with its weights (weight painting from Blender), replaces the model's for every animation. Take the mesh only if you changed it: an FBX exported from here carries the mesh too.",
                "Import FBX", "Animation and Mesh", "Animation Only", "Mesh Only", "Cancel");
            if (pick == 3) return;
            useClip = pick != 2; useMesh = pick != 1;
        }
        else if (hasMesh && Dialog.Show(this, $"{Path.GetFileName(dlg.FileName)} has a skinned mesh ({c.SkinnedMeshes} part(s)){(anim != null ? " and no animation" : "")}. Use it, with its weights, in place of the model's mesh?", "Import FBX", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        try
        {
            var notes = new List<string>();
            if (useClip)
            {
                var clip = AnimExportCli.Fbx.FbxAnimationImporter.Read(dlg.FileName);
                int known = clip.Tracks.Keys.Count(k => mhoBones.Contains(k, StringComparer.OrdinalIgnoreCase));
                if (known < Math.Max(4, clip.Tracks.Count / 2))
                {
                    Dialog.Show(this, $"Only {known} of the animation's {clip.Tracks.Count} bones are bones of {pkg.Title}'s skeleton. Export from here (Export FBX) and edit that file, so the bone names stay the game's.", "Animation Not Imported", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                edits.Anims[anim!] = AnimEdits.Keep(dlg.FileName, folder, anim!);
                notes.Add($"\"{anim}\" ← {Path.GetFileName(dlg.FileName)} ({known} of {clip.Tracks.Count} bones known)");
            }
            if (useMesh)
            {
                edits.ModelFbx = AnimEdits.Keep(dlg.FileName, folder, useClip ? anim! : "model");
                notes.Add($"the mesh ← {Path.GetFileName(dlg.FileName)}");
            }
            EditsChanged(string.Join("; ", notes) + $" (kept in {folder})");
        }
        catch (Exception ex) when (ex is AnimExportCli.Fbx.AnimationImportException or IOException or UnauthorizedAccessException) { Dialog.Show(this, ex.Message, "FBX Not Imported", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void ResetMap()
    {
        if (MapPath() is string p && File.Exists(p)) { File.Delete(p); Log("Bone map back to automatic."); }
        SchedulePreview();
    }

    static string UniqueDir(string dir)
    {
        if (!Directory.Exists(dir)) return dir;
        for (int i = 2; ; i++) if (!Directory.Exists($"{dir} ({i})")) return $"{dir} ({i})";
    }

    void OpenFolder()
    {
        if (lastZip == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{lastZip}\"") { UseShellExecute = false });
    }
}
