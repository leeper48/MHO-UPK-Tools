namespace MhoExtendedModManager;

/// <summary>
/// What the data folder collects that nothing needs any more, cleared at a normal start (Kurt, 2026-10-07: a user's data
/// folder kept leftovers). Only at a normal start, which holds the single instance: no editor is open, no update or
/// download is running, so none of it can be in use. Kept: the library, its originals and mods, settings, the write
/// records (history), the Model tab's exports and rigs (the user's own Blender work: Settings → Model → Clean Up Exports
/// and Rigs), pictures.
/// </summary>
static class DataCleanup
{
    public sealed record Result(string What, int Files, long Bytes);

    /// <summary>Clears them (or with <paramref name="dryRun"/> only counts); one line per kind that had something.</summary>
    public static List<Result> Run(string? library, bool dryRun = false)
    {
        var done = new List<Result>();
        void Add(string what, (int Files, long Bytes) r) { if (r.Files > 0) done.Add(new Result(what, r.Files, r.Bytes)); }
        // Images converted for the editor (PNG → DDS, Create from 3D snapshots): an editor's draft points at them until Save
        // copies them into the mod; drafts don't outlive the app.
        Add("converted editor images (data\\converted)", Folder(Path.Combine(Settings.Home, "converted"), dryRun));
        // Nexus downloads: installed right after they arrive (an update in place); the archive isn't read again.
        Add("Nexus downloads (data\\downloads)", Folder(Path.Combine(Settings.Home, "downloads"), dryRun));
        // A self-update that stopped part way (a finished one removes its folder itself).
        Add("an unfinished app update (data\\update)", Folder(Path.Combine(Settings.Home, "update"), dryRun));
        // Thumbnails not shown for 30 days, and temp files of a killed run (made again when needed).
        if (!dryRun) Add("model thumbnails not used for 30 days (data\\model\\thumbs)", Model.Thumbs.Prune(30));
        // The editor's work folders a killed run left (library\model-work-…, power-colors-…, voice-…, anim-work-…, costume-move-…).
        if (library != null && Directory.Exists(library))
        {
            var dirs = Directory.GetDirectories(library).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d),
                "^(model-work|power-colors|voice-work|voice-edit|voice-shift|anim-work|costume-move)-[0-9a-f]{8}$")).ToList();
            long size = dirs.Sum(d => Size(d));
            int n = dryRun ? dirs.Count : ModelWork.SweepOrphans(library);
            if (n > 0) done.Add(new Result("editor work folders left by a closed or crashed run (library)", n, size));
        }
        return done;
    }

    /// <summary>Everything in <paramref name="dir"/> (files and subfolders), the folder itself kept.</summary>
    static (int, long) Folder(string dir, bool dryRun)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        int n = 0; long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                long len = new FileInfo(f).Length;
                if (!dryRun) File.Delete(f);
                n++; bytes += len;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (!dryRun)
            foreach (var d in Directory.GetDirectories(dir))
                try { Directory.Delete(d, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (n, bytes);
    }

    static long Size(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
