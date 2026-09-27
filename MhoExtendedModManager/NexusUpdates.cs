namespace MhoExtendedModManager;

/// <summary>Nexus checks and updates for the library, without UI (the window and the tests call these).</summary>
static class NexusUpdates
{
    /// <summary>Fetches every linked mod's Nexus info into the cache. Returns how many were checked and the problems.</summary>
    public static async Task<(int Checked, List<string> Problems)> Check(ModLibrary lib, string apiKey, NexusCache cache)
    {
        var problems = new List<string>();
        int n = 0;
        foreach (int id in lib.Mods.Select(m => m.NexusModId).OfType<int>().Distinct())
        {
            try { cache.Mods[id] = await Nexus.Mod(apiKey, id); n++; }
            catch (Nexus.NexusException ex) { problems.Add($"mod {id}: {ex.Message}"); if (ex.Message.Contains("API key") || ex.Message.Contains("limit")) break; }
            catch (HttpRequestException ex) { problems.Add(ex.Message); break; }
        }
        cache.Checked = DateTime.Now;
        return (n, problems);
    }

    /// <summary>The newer Nexus version for a mod from the cache, or null.</summary>
    public static string? UpdateFor(Mod m, NexusCache cache)
    {
        if (m.NexusModId is not int id || !cache.Mods.TryGetValue(id, out var info)) return null;
        return Nexus.UpdateFor(m.NexusLink ?? new NexusLink { ModId = id }, info, m.Manifest.Version, m.FilesMade);
    }

    /// <summary>Links newly installed mods to Nexus: from the archive's file name, else (with a key) its MD5.</summary>
    public static async Task Link(ModLibrary lib, string archive, IEnumerable<string> installed, string? apiKey)
    {
        var fromName = Nexus.FromFileName(archive);
        (int ModId, long FileId, string Version)? byMd5 = null;
        if (apiKey != null && File.Exists(archive))
            try { byMd5 = await Nexus.Md5(apiKey, archive); } catch (Exception ex) when (ex is IOException or HttpRequestException or Nexus.NexusException) { }
        if (fromName == null && byMd5 == null) return;
        foreach (string folder in installed)
            if (lib.Mods.FirstOrDefault(x => x.FolderName.Equals(folder, StringComparison.OrdinalIgnoreCase)) is Mod m)
                m.NexusLink = new NexusLink
                {
                    ModId = byMd5?.ModId ?? fromName!.Value.ModId,
                    FileId = byMd5?.FileId,
                    Version = byMd5?.Version is { Length: > 0 } v ? v : fromName?.Version,
                    Installed = DateTime.Now,
                    FromNexus = true,
                };
        lib.SaveState();
    }

    /// <summary>Premium: downloads the newest file of a linked mod into data\downloads; returns its path and the file.</summary>
    public static async Task<(string Path, Nexus.NexusFile File)> DownloadLatest(Mod m, string apiKey, NexusCache cache, string home, IProgress<string>? progress)
    {
        if (m.NexusModId is not int id) throw new Nexus.NexusException("The mod isn't linked to a Nexus page.");
        var info = cache.Mods.TryGetValue(id, out var c) ? c : cache.Mods[id] = await Nexus.Mod(apiKey, id);
        var latest = Nexus.Latest(info) ?? throw new Nexus.NexusException("The Nexus page has no main file.");
        string uri = await Nexus.DownloadLink(apiKey, id, latest.FileId);
        string path = await Nexus.Download(uri, latest.FileName.Length > 0 ? latest.FileName : $"{info.Name}-{id}.zip", Path.Combine(home, "downloads"), progress);
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
