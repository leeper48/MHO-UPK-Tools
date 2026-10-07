using System.Text.Json;
using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The importer's half of the Blender roundtrip (0.16.3, Kurt: Ctrl+S in Blender sends the work back). Open in Blender links
/// the work (this model on this base hero) to its export folder (blender.txt in the edits folder: the folder and the last
/// sync applied; not part of undo). The scene Blender opens writes what changed on every save into from_blender\ and counts
/// up from_blender\sync.json (<see cref="BlenderLaunch"/>); a watcher on that file applies each new sync here as FBX edits
/// (the mesh, the replaced animations: the same as Single Animation ▾ → Import, one undo step), also when the work is opened again
/// after Blender saved while the app was closed or on another model.
/// </summary>
sealed partial class ModelPage
{
    FileSystemWatcher? blenderWatch, paintWatch;
    string? blenderWatchKey;
    readonly System.Windows.Forms.Timer blenderDelay = new() { Interval = 700 };
    bool blenderDelayHooked;

    (string? Folder, int Applied) BlenderLink()
    {
        if (EditsFolder() is not string f || !File.Exists(Path.Combine(f, "blender.txt"))) return (null, 0);
        var lines = File.ReadAllLines(Path.Combine(f, "blender.txt"));
        return (lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null, lines.Length > 1 && int.TryParse(lines[1], out int n) ? n : 0);
    }

    void SaveBlenderLink(string folder, int applied)
    {
        if (EditsFolder() is not string f) return;
        Protected.CheckWrite(f);
        Directory.CreateDirectory(f);
        File.WriteAllLines(Path.Combine(f, "blender.txt"), [folder, applied.ToString()]);
    }

    /// <summary>Open in Blender made <paramref name="folder"/>: this work now listens to it (syncs counted from 0).</summary>
    void LinkBlender(string folder)
    {
        SaveBlenderLink(folder, 0);
        blenderWatchKey = null;
        WatchBlender();
    }

    /// <summary>Watches the shown work's Blender folder (call when the work may have changed); applies a sync that came while
    /// it wasn't shown.</summary>
    void WatchBlender()
    {
        string? key = EditsFolder();
        if (key == blenderWatchKey) return;
        blenderWatchKey = key;
        blenderWatch?.Dispose(); blenderWatch = null;
        paintWatch?.Dispose(); paintWatch = null;
        if (!blenderDelayHooked) { blenderDelay.Tick += (_, _) => { blenderDelay.Stop(); ApplyBlenderSync(); }; blenderDelayHooked = true; }
        var (folder, _) = BlenderLink();
        if (folder == null) return;
        // the link travels with the mod (Model\edits): on another PC, or after the export folder was deleted, there's nothing to watch
        if (!Directory.Exists(folder)) { Log($"Blender: the linked export folder isn't here any more ({folder}); Open in Blender again to work in Blender."); return; }
        string from = Path.Combine(folder, "from_blender");
        try
        {
            Directory.CreateDirectory(from);
            blenderWatch = new FileSystemWatcher(from, "sync.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => Later(() => { blenderDelay.Stop(); blenderDelay.Start(); });
            blenderWatch.Changed += on; blenderWatch.Created += on;
            blenderWatch.Renamed += (_, _) => Later(() => { blenderDelay.Stop(); blenderDelay.Start(); });
            blenderWatch.EnableRaisingEvents = true;
            // textures painted in Blender and saved to the export folder (Texture Paint; Image → Save, or Ctrl+S in a scene
            // that saves painted images)
            paintWatch?.Dispose();
            paintWatch = new FileSystemWatcher(folder, "*.png") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            paintWatch.Changed += on; paintWatch.Created += on;
            paintWatch.Renamed += (_, _) => Later(() => { blenderDelay.Stop(); blenderDelay.Start(); });
            paintWatch.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Log($"Blender: can't watch {from}: {ex.Message}"); return; }
        ApplyBlenderSync();
    }

    /// <summary>On the UI thread, unless the tab has gone (the editor closed while Blender kept saving).</summary>
    void Later(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>The watcher stops with the tab (the editor closes); a later save in Blender is applied when the mod's Model tab
    /// opens again (its last applied sync is in the mod's Model folder).</summary>
    void StopBlenderWatch()
    {
        blenderWatch?.Dispose(); blenderWatch = null;
        paintWatch?.Dispose(); paintWatch = null;
        StopImageWatches();
        blenderDelay.Stop(); blenderDelay.Dispose();
        rigWatch?.Dispose(); rigWatch = null;
        rigDelay.Stop(); rigDelay.Dispose();
    }

    /// <summary>The export's texture names (FbxExport: &lt;material&gt; + suffix) and the map each one is.</summary>
    static readonly (string Suffix, string Kind)[] PaintKinds =
        [("", "Color"), ("_n", "Normal"), ("_sp", "Spec"), ("_mhospec", "MHO Spec"), ("_speccolor", "Spec Color"), ("_glow", "Glow"), ("_alpha", "Alpha")];

    /// <summary>
    /// Textures painted in Blender (Kurt, 2026-10-06: Texture Paint on She-Hulk's horns): an export folder texture saved after
    /// the export (newer than its model.fbx; the export copies keep their own dates or are written before it) and not taken
    /// in yet becomes that material's map override (the Materials tab's Replace File: copied into the mod's Model folder,
    /// Back to Automatic undoes it). What was taken in is kept in the edits folder (painted.txt: file, SHA-1).
    /// </summary>
    void ApplyPaintedTextures(string folder)
    {
        if (EditsFolder() is not string editsFolder || OverridesPath() is not string ovPath) return;
        string modelFbx = Path.Combine(folder, "model.fbx");
        if (!File.Exists(modelFbx)) return;
        DateTime exported = File.GetLastWriteTimeUtc(modelFbx).AddSeconds(5);
        string record = Path.Combine(editsFolder, "painted.txt");
        var taken = File.Exists(record) ? File.ReadAllLines(record).Select(l => l.Split('	')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var (material, _) in shownMaterials)
            foreach (var (suffix, kind) in PaintKinds)
            {
                string file = Path.Combine(folder, FbxExport.SafeName(material) + suffix + ".png");
                if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) <= exported) continue;
                byte[] bytes;
                try { bytes = File.ReadAllBytes(file); } catch (IOException) { continue; }   // Blender still writing: the next change event comes
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes));
                string key = Path.GetFileName(file);
                if (taken.TryGetValue(key, out var was) && was == hash) continue;
                string dir = Path.Combine(Path.GetDirectoryName(ovPath)!, Path.GetFileNameWithoutExtension(ovPath));
                Protected.CheckWrite(dir);
                Directory.CreateDirectory(dir);
                // a new name per paint: pictures are cached by file
                string name = $"{FbxExport.SafeName(material)}_{kind.ToLowerInvariant().Replace(" ", "")}_painted_{hash[..8].ToLowerInvariant()}.png";
                File.WriteAllBytes(Path.Combine(dir, name), bytes);
                string rel = Path.Combine(Path.GetFileName(dir), name);
                ChangeOverrides((_, e) =>
                {
                    switch (kind) { case "Color": e.Color = rel; break; case "Normal": e.Normal = rel; e.FlipGreen = false; break; case "Spec": e.Spec = rel; break; case "MHO Spec": e.SpecMho = rel; break; case "Spec Color": e.SpecColor = rel; break; case "Glow": e.Glow = rel; e.GlowOff = false; break; default: e.Alpha = rel; break; }
                }, material, $"{material}'s {kind.ToLowerInvariant()} map painted in Blender ({key}, saved {File.GetLastWriteTime(file):HH:mm:ss}) is in now.");
                taken[key] = hash;
                changed = true;
            }
        if (!changed) return;
        Protected.CheckWrite(editsFolder);
        Directory.CreateDirectory(editsFolder);
        File.WriteAllLines(record, taken.Select(kv => kv.Key + "	" + kv.Value));
        FillMaterials();
    }

    sealed record SyncFile(int Version, string? Model, Dictionary<string, string> Anims, List<string>? Last, string? Time);

    /// <summary>A new sync from Blender (a higher version than the last applied) into the FBX edits.</summary>
    void ApplyBlenderSync()
    {
        if (!HasSource || ChosenPackage == null || EditsFolder() is not string editsFolder) return;
        var (folder, applied) = BlenderLink();
        if (folder == null) return;
        ApplyPaintedTextures(folder);
        string from = Path.Combine(folder, "from_blender"), file = Path.Combine(from, "sync.json");
        if (!File.Exists(file)) return;
        SyncFile? sync = null;
        for (int i = 0; i < 5 && sync == null; i++)
        {
            try { sync = JsonSerializer.Deserialize<SyncFile>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (Exception ex) when (ex is IOException or JsonException) { Thread.Sleep(150); }
        }
        if (sync == null || sync.Version <= applied) return;
        EnsureEdits();
        var notes = new List<string>(); var problems = new List<string>();
        var names = preview.AnimationNames;
        foreach (var (name, rel) in sync.Anims ?? new())
        {
            string path = Path.Combine(from, rel.Replace('/', Path.DirectorySeparatorChar));
            // the NLA track is the export's file name: the game's name, or one Export FBX made safe for a file
            string? anim = names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault(n => FbxExport.SafeName(n).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (anim == null) { problems.Add($"{name}: no animation of that name"); continue; }
            if (!File.Exists(path)) { problems.Add($"{name}: {rel} isn't there"); continue; }
            var clip = AnimExportCli.Fbx.FbxAnimationImporter.Read(path);
            int known = clip.Tracks.Keys.Count(k => mhoBones.Contains(k, StringComparer.OrdinalIgnoreCase));
            if (known < Math.Max(4, clip.Tracks.Count / 2)) { problems.Add($"{name}: only {known} of {clip.Tracks.Count} bones are the skeleton's"); continue; }
            string kept = AnimEdits.Keep(path, editsFolder, anim);
            if (edits.Anims.TryGetValue(anim, out var was) && was == kept) continue;
            edits.Anims[anim] = kept;
            notes.Add(anim);
        }
        if (sync.Model is string m && File.Exists(Path.Combine(from, m)))
        {
            string kept = AnimEdits.Keep(Path.Combine(from, m), editsFolder, "blender_model");
            bool bones = sync.Last?.Contains("bones") == true, mesh = sync.Last == null || sync.Last.Contains("the mesh");
            if (edits.ModelFbx != kept) { edits.ModelFbx = kept; notes.Insert(0, bones && !mesh ? "the bones" : bones ? "the mesh and bones" : "the mesh"); }
        }
        SaveBlenderLink(folder, sync.Version);
        foreach (var p in problems) Log("Blender: " + p);
        if (notes.Count > 0) EditsChanged($"Blender sent {string.Join(", ", notes)} (save {sync.Version}{(sync.Time != null ? ", " + sync.Time : "")})");
        else if (problems.Count == 0) Log($"Blender: save {sync.Version} had nothing new.");
    }
}
