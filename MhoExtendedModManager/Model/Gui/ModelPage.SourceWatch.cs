namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// A Single Model source saved again outside the app (a user, 2026-11-18: weights fixed in Blender and exported over the same
/// FBX showed only after a Bone Map change forced a rebuild): the file is watched while it's the source, and each save reads it
/// again with the tab's choices (parts, rigs, size …) kept. Reload (F5) does the same by hand, and rebuilds the preview for any
/// other source.
/// </summary>
sealed partial class ModelPage
{
    FileSystemWatcher? sourceWatch;
    string? sourceWatchFile;
    readonly System.Windows.Forms.Timer sourceDelay = new() { Interval = 900 };
    bool sourceDelayHooked;

    /// <summary>Watches the picked Single Model's file (call after a source loads; null: nothing to watch).</summary>
    void WatchSource()
    {
        string? file = chosenKey?.StartsWith("fbx:", StringComparison.OrdinalIgnoreCase) == true ? chosenKey[4..] : null;
        if (string.Equals(file, sourceWatchFile, StringComparison.OrdinalIgnoreCase)) return;
        sourceWatch?.Dispose(); sourceWatch = null; sourceWatchFile = file;
        if (!sourceDelayHooked) { sourceDelay.Tick += (_, _) => { sourceDelay.Stop(); ReloadSource(changed: true); }; sourceDelayHooked = true; }
        if (file == null || Path.GetDirectoryName(file) is not string dir || !Directory.Exists(dir)) return;
        try
        {
            sourceWatch = new FileSystemWatcher(dir, Path.GetFileName(file)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => Later(() => { sourceDelay.Stop(); sourceDelay.Start(); });
            sourceWatch.Changed += on; sourceWatch.Created += on;
            sourceWatch.Renamed += (_, _) => Later(() => { sourceDelay.Stop(); sourceDelay.Start(); });   // (a program that saves to a temporary file, then renames it)
            sourceWatch.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { Log($"Source: can't watch {file} for changes: {ex.Message}"); }
    }

    /// <summary>Reads the source again from disk, the tab's choices kept (a Single Model, or a From the Game source), else rebuilds
    /// the preview. <paramref name="changed"/>: the watched file was saved (said so in the log).</summary>
    void ReloadSource(bool changed = false)
    {
        if (IsDisposed || !loaded) return;
        if (chosenKey is string key && sourceFbx is string fbx && File.Exists(chosenKey.StartsWith("fbx:", StringComparison.OrdinalIgnoreCase) ? key[4..] : fbx))
        {
            Log(changed ? $"{Path.GetFileName(sourceWatchFile)} was saved: reading it again (your parts, rigs and size are kept)."
                        : $"Reload: reading {Path.GetFileName(key.StartsWith("fbx:", StringComparison.OrdinalIgnoreCase) ? key[4..] : fbx)} again (your parts, rigs and size are kept).");
            pendingState = CurrentState();
            if (key.StartsWith(GameSrc, StringComparison.OrdinalIgnoreCase)) GameChosen(key);   // (written again if its package changed)
            else FbxChosen(key, key.StartsWith("fbx:", StringComparison.OrdinalIgnoreCase) ? null : fbx);
            return;
        }
        if (changed) return;
        Log("Reload: the preview is built again.");
        SchedulePreview();
    }
}
