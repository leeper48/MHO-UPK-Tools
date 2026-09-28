namespace MhoExtendedModManager;

/// <summary>Nexus checks for the library, without UI (the window and the tests call these). Public data only: no key.</summary>
static class NexusUpdates
{
    /// <summary>Fetches every linked mod's Nexus info into the cache. Returns how many were checked and the problems.</summary>
    public static async Task<(int Checked, List<string> Problems)> Check(ModLibrary lib, NexusCache cache)
    {
        var problems = new List<string>();
        var ids = lib.Mods.Select(m => m.NexusModId).OfType<int>().Distinct().ToList();
        try
        {
            foreach (var (id, info) in await Nexus.Mods(ids))
            {
                cache.Mods[id] = info;
                if (!info.Available) problems.Add($"mod {id}: not found on Nexus (hidden or removed)");
            }
        }
        catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { problems.Add(ex.Message); return (0, problems); }
        cache.Checked = DateTime.Now;
        return (ids.Count - problems.Count, problems);
    }

    /// <summary>The newer Nexus version for a mod from the cache, or null.</summary>
    public static string? UpdateFor(Mod m, NexusCache cache)
    {
        if (m.NexusModId is not int id || !cache.Mods.TryGetValue(id, out var info)) return null;
        return Nexus.UpdateFor(m.NexusLink ?? new NexusLink { ModId = id }, info, m.Manifest.Version, m.FilesMade);
    }

    /// <summary>
    /// Links newly installed mods to Nexus from the archive's Nexus file name. The upload time in the name identifies the
    /// exact file; its ID is looked up in the cache, or (when <paramref name="online"/>) fetched from the public API.
    /// </summary>
    public static async Task Link(ModLibrary lib, string archive, IEnumerable<string> installed, NexusCache? cache = null, bool online = false)
    {
        if (Nexus.FromFileName(archive) is not { } name) return;
        long? fileId = null;
        if (cache != null && !cache.Mods.ContainsKey(name.ModId) && online)
            try { foreach (var (id, info) in await Nexus.Mods([name.ModId])) cache.Mods[id] = info; }
            catch (Exception ex) when (ex is Nexus.NexusException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        if (cache != null && cache.Mods.TryGetValue(name.ModId, out var mi)) fileId = Nexus.FileUploadedAt(mi, name.Uploaded)?.FileId;
        foreach (string folder in installed)
            if (lib.Mods.FirstOrDefault(x => x.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase)) is Mod m)
                m.NexusLink = new NexusLink { ModId = name.ModId, FileId = fileId, Version = name.Version, Installed = DateTime.Now, FromNexus = true };
        lib.SaveState();
    }

    /// <summary>Signed in with Premium: downloads the newest file of a linked mod into data\downloads; returns its path and the file.</summary>
    public static async Task<(string Path, Nexus.NexusFile File)> DownloadLatest(Mod m, string accessToken, NexusCache cache, string home, IProgress<string>? progress)
    {
        if (m.NexusModId is not int id) throw new Nexus.NexusException("The mod isn't linked to a Nexus page.");
        if (!cache.Mods.ContainsKey(id)) foreach (var (i, info) in await Nexus.Mods([id])) cache.Mods[i] = info;
        var mi = cache.Mods[id];
        var latest = Nexus.Latest(mi) ?? throw new Nexus.NexusException("The Nexus page has no main file.");
        string uri = await Nexus.DownloadLink(accessToken, id, latest.FileId);
        string name = latest.FileName.Length > 0 ? latest.FileName : $"{(mi.Name.Length > 0 ? mi.Name : "mod")}-{id}-{latest.Version.Replace('.', '-')}-{latest.Uploaded}.zip";
        string path = await Nexus.Download(uri, name, Path.Combine(home, "downloads"), progress);
        return (path, latest);
    }

    /// <summary>After an update is installed: the link now points at that file / version.</summary>
    public static void Record(ModLibrary lib, string folder, int modId, long? fileId, string? version)
    {
        if (lib.Mods.FirstOrDefault(x => x.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase)) is not Mod m) return;
        m.NexusLink = new NexusLink { ModId = modId, FileId = fileId, Version = version, Installed = DateTime.Now, FromNexus = true };
        lib.SaveState();
    }
}
