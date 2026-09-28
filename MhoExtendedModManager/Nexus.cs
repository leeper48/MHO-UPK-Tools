using System.Text.Json;
using System.Text.RegularExpressions;

namespace MhoExtendedModManager;

/// <summary>
/// Nexus Mods: which installed mods have a newer version on Nexus (Kurt, 2026-09-27). Everything comes from Nexus's
/// public v2 GraphQL API: no API key, no account (2026-09-28: Nexus's review doesn't allow apps to ask users for their
/// personal API key; a registered OAuth app would be needed for that, and downloads through the API need Premium). So
/// an update opens the mod's Files page and the app picks the downloaded file up from the Downloads folder.
///
/// A mod is linked to its Nexus page by (in this order) the file name Nexus gives downloads ("Name-176-1-2-1690000000.zip":
/// mod 176, version 1.2, uploaded at 1690000000, which also identifies the file), the mod's own manifest (NexusModId, set
/// by its author), Find My Mods, or by hand (paste the page). The link lives in state.json (NexusLinks); the last fetched
/// info in data\nexus_cache.json.
///
/// MHO_EXTMM_NEXUS_API (tests): a local folder standing in for the API: graphql_mods.json (the game's mods: modId, name,
/// version, author, uploader, updatedAt, status) and graphql_files.json ({ "modId": [ files ] }).
/// </summary>
static class Nexus
{
    public const string Game = "marvelheroesomega";
    public const int GameId = 7310;
    public const string SiteMods = "https://www.nexusmods.com/" + Game + "/mods/";
    const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";
    static string? Fake => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_API");

    public class NexusException(string message) : Exception(message);

    public sealed record NexusFile(long FileId, string Name, string Version, string Category, long Uploaded, string FileName, string Description = "");
    public sealed record ModInfo(int ModId, string Name, string Version, long Updated, bool Available, List<NexusFile> Files, List<long[]> Updates);

    // ---- requests

    /// <summary>One GraphQL request (public data, no key), identified by the app's name and version as Nexus asks.</summary>
    public static async Task<JsonElement> GraphQl(string query, object? variables = null)
    {
        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);
        h.DefaultRequestHeaders.Add("Application-Name", "MHO Extended Mod Manager");
        h.DefaultRequestHeaders.Add("Application-Version", Program.Version);
        var body = JsonSerializer.Serialize(new { query, variables });
        using var resp = await h.PostAsync(GraphQlUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        if ((int)resp.StatusCode == 429) throw new NexusException("Nexus's request limit is used up for now; try again later.");
        resp.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0 && !root.TryGetProperty("data", out _))
            throw new NexusException("Nexus: " + (errs[0].TryGetProperty("message", out var msg) ? msg.GetString() : "request failed"));
        return root.GetProperty("data");
    }

    /// <summary>The linked mods' Nexus info and files, in batches of 20 per request. Mods Nexus doesn't return
    /// (hidden, removed) come back as unavailable.</summary>
    public static async Task<Dictionary<int, ModInfo>> Mods(IEnumerable<int> modIds)
    {
        var ids = modIds.Distinct().ToList();
        var result = new Dictionary<int, ModInfo>();
        if (Fake is string fake)
        {
            var list = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fake, "graphql_mods.json"))).RootElement;
            string filesPath = Path.Combine(fake, "graphql_files.json");
            var files = File.Exists(filesPath) ? JsonDocument.Parse(await File.ReadAllTextAsync(filesPath)).RootElement : default;
            foreach (int id in ids)
            {
                var node = list.EnumerateArray().FirstOrDefault(n => n.GetProperty("modId").GetInt32() == id);
                var fl = files.ValueKind == JsonValueKind.Object && files.TryGetProperty(id.ToString(), out var f) ? f : default;
                result[id] = Info(id, node, fl);
            }
            return result;
        }
        foreach (var batch in ids.Chunk(20))
        {
            var sb = new System.Text.StringBuilder("{ legacyModsByDomain(ids:[");
            sb.Append(string.Join(",", batch.Select(i => $"{{gameDomain:\"{Game}\", modId:{i}}}")));
            sb.Append("]){ nodes { modId name version status updatedAt } } ");
            foreach (int i in batch) sb.Append($"f{i}: modFiles(modId:{i}, gameId:{GameId}){{ fileId name version category date uri description }} ");
            sb.Append('}');
            var data = await GraphQl(sb.ToString());
            var nodes = data.TryGetProperty("legacyModsByDomain", out var l) && l.TryGetProperty("nodes", out var n) ? n : default;
            foreach (int i in batch)
            {
                var node = nodes.ValueKind == JsonValueKind.Array ? nodes.EnumerateArray().FirstOrDefault(x => x.GetProperty("modId").GetInt32() == i) : default;
                result[i] = Info(i, node, data.TryGetProperty($"f{i}", out var fl) ? fl : default);
            }
        }
        return result;
    }

    // ---- downloads (signed in with Nexus, Premium only)

    static string V1 => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_V1") ?? "https://api.nexusmods.com/v1/";

    /// <summary>A download URL for a file, for a signed-in Premium member (the access token from NexusAuth).</summary>
    public static async Task<string> DownloadLink(string accessToken, int modId, long fileId)
    {
        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);
        h.DefaultRequestHeaders.Add("Application-Name", "MHO Extended Mod Manager");
        h.DefaultRequestHeaders.Add("Application-Version", Program.Version);
        h.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await h.GetAsync($"{V1}games/{Game}/mods/{modId}/files/{fileId}/download_link.json");
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new NexusAuth.SignedOutException("Nexus didn't accept the sign-in; sign in again.");
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new NexusException("Nexus only gives apps download links for Premium members.");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new NexusException("That file isn't on Nexus anymore.");
        if ((int)resp.StatusCode == 429) throw new NexusException("Nexus's request limit is used up for now; try again later.");
        resp.EnsureSuccessStatusCode();
        foreach (var x in JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.EnumerateArray())
            if (Str(x, "URI") is { Length: > 0 } u) return u;
        throw new NexusException("Nexus gave no download link.");
    }

    /// <summary>Downloads a file into <paramref name="folder"/> (as <paramref name="fileName"/>); returns the path.</summary>
    public static async Task<string> Download(string uri, string fileName, string folder, IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName));
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { File.Copy(uri, path, true); return path; }   // tests
        using var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);
        using var resp = await h.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(path + ".part"))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
                progress?.Report(total > 0 ? $"Downloading {fileName}: {done * 100 / total}%" : $"Downloading {fileName}: {done / 1048576} MB");
            }
        }
        File.Move(path + ".part", path, true);
        return path;
    }

    static ModInfo Info(int id, JsonElement node, JsonElement files)
    {
        var fs = new List<NexusFile>();
        if (files.ValueKind == JsonValueKind.Array)
            foreach (var x in files.EnumerateArray())
            {
                string uri = Str(x, "uri");
                fs.Add(new NexusFile(x.GetProperty("fileId").GetInt64(), Str(x, "name"), Str(x, "version"), Str(x, "category"),
                    x.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0, uri.Contains('.') ? Path.GetFileName(uri) : "", Str(x, "description")));
            }
        bool found = node.ValueKind == JsonValueKind.Object;
        long updated = found && DateTimeOffset.TryParse(Str(node, "updatedAt"), out var u) ? u.ToUnixTimeSeconds() : 0;
        bool available = found && (Str(node, "status") is "" or "published");
        return new ModInfo(id, found ? Str(node, "name") : "", found ? Str(node, "version") : "", updated, available, fs, []);
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.ToString() : "" : "";

    // ---- links

    static readonly Regex NexusFileName = new(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>[0-9][0-9a-zA-Z\-]*?)-(?<time>\d{9,11})(\s*\(\d+\))?\.(zip|7z|rar)$", RegexOptions.IgnoreCase);

    /// <summary>The Nexus mod, version and upload time in a downloaded file's name ("Storm Classic-176-1-2-1690000000.zip"), or null.</summary>
    public static (int ModId, string Version, long Uploaded)? FromFileName(string path)
    {
        var m = NexusFileName.Match(Path.GetFileName(path));
        return m.Success && int.TryParse(m.Groups["id"].Value, out int id) ? (id, m.Groups["ver"].Value.Replace('-', '.'), long.Parse(m.Groups["time"].Value)) : null;
    }

    /// <summary>The file a download is: the one uploaded at the time in its name (Nexus puts it there).</summary>
    public static NexusFile? FileUploadedAt(ModInfo info, long uploaded) => info.Files.FirstOrDefault(f => f.Uploaded == uploaded);

    /// <summary>A mod ID from a pasted Nexus page (…/marvelheroesomega/mods/176…) or a plain number.</summary>
    public static int? ParseModId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"/mods/(\d+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return int.TryParse(text.Trim(), out int id) && id > 0 ? id : null;
    }

    /// <summary>The files an update could install: not old, archived or deleted.</summary>
    public static List<NexusFile> Usable(ModInfo info) =>
        info.Files.Where(f => !f.Category.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase) && !f.Category.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase)
                           && !f.Category.Equals("DELETED", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The file an update would install: the newest main file (else the newest usable file). With <paramref name="line"/>
    /// (a file name on the page, NexusLink.File), only files of that name count: a page can carry several mods side by side,
    /// each updated under its own name (Rogue #300: "… Visual Update" and "… Visual Update (Variant)", both v5 main files;
    /// 2026-09-28 a user's Variant copy was updated to the default one). Null when none of that name is left.
    /// </summary>
    public static NexusFile? Latest(ModInfo info, string? line = null)
    {
        var usable = Usable(info);
        if (line != null) return usable.Where(f => SameLine(f.Name, line)).OrderByDescending(f => f.Uploaded).FirstOrDefault();
        return usable.Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Uploaded).FirstOrDefault()
            ?? usable.OrderByDescending(f => f.Uploaded).FirstOrDefault();
    }

    /// <summary>The newest usable file of each file name on the page (main files first), for choosing which one a mod is.</summary>
    public static List<NexusFile> Lines(ModInfo info)
    {
        var usable = Usable(info);
        var main = usable.Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).ToList();
        return (main.Count > 0 ? main : usable).GroupBy(f => LineKey(f.Name)).Select(g => g.OrderByDescending(f => f.Uploaded).First())
            .OrderByDescending(f => f.Uploaded).ToList();
    }

    static string LineKey(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static bool SameLine(string a, string b) => LineKey(a) == LineKey(b);

    /// <summary>
    /// Which file on the page a mod follows: the one chosen (NexusLink.File), else the installed file's name, else a
    /// guess from the mod's names when the page has several (a file whose own words, the ones the others don't have,
    /// such as "Variant", all appear in the mod's name). Null: the page has one file name, or it can't be told.
    /// </summary>
    public static string? LineFor(NexusLink link, ModInfo info, params string?[] modNames)
    {
        if (link.File is { Length: > 0 } chosen) return chosen;
        if (link.FileId is long fid && info.Files.FirstOrDefault(f => f.FileId == fid) is { } installed) return installed.Name;
        var lines = Lines(info);
        if (lines.Count < 2) return null;
        static HashSet<string> Words(string s) => [.. Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+").Where(w => w.Length > 0)];
        var sets = lines.Select(l => Words(l.Name)).ToList();
        var common = sets.Skip(1).Aggregate(new HashSet<string>(sets[0]), (a, b) => { a.IntersectWith(b); return a; });
        var mine = new HashSet<string>(modNames.Where(n => n != null).SelectMany(n => Words(n!)));
        var hits = lines.Where((l, i) => sets[i].Except(common).ToList() is { Count: > 0 } own && own.All(mine.Contains)).ToList();
        return hits.Count == 1 ? hits[0].Name : null;
    }

    /// <summary>True when the page has several file names and it isn't known which one the mod is (ask before updating).</summary>
    public static bool NeedsChoice(ModInfo info, string? line) => line == null && Lines(info).Count > 1;

    /// <summary>
    /// The newer version on Nexus for a linked mod, or null when it's up to date. By file when the installed file is
    /// known (a newer main file, or one the file-update chain leads to), else by version number, else by upload time
    /// after the install.
    /// </summary>
    public static string? UpdateFor(NexusLink link, ModInfo info, string? localVersion, DateTime? filesMade = null, string? line = null)
    {
        var latest = Latest(info, line);
        if (latest == null || !info.Available) return null;
        string latestVersion = latest.Version.Length > 0 ? latest.Version : info.Version;
        if (link.Ignore == latest.FileId) return null;
        if (link.FileId == null && !link.FromNexus)
        {
            // Linked by name: which Nexus file the user has is unknown. An update is a file uploaded after the link, or a
            // higher version uploaded after the user's copy was made (Miles Morales: Nexus's v2 from 2025 is older than the
            // user's v0.1 from 2026, a different release; Storm Classic v4 → v5.1 uploaded after it: a real update).
            long linkedAt = link.Installed is DateTime li ? new DateTimeOffset(li.ToUniversalTime()).ToUnixTimeSeconds() : long.MaxValue;
            if (latest.Uploaded > linkedAt) return latestVersion;
            long made = filesMade is DateTime fm && fm > DateTime.MinValue ? new DateTimeOffset(DateTime.SpecifyKind(fm, DateTimeKind.Utc)).ToUnixTimeSeconds() : long.MaxValue;
            return latest.Uploaded > made && VersionOf(link.Version ?? localVersion) is Version have && VersionOf(latestVersion) is Version theirs && theirs > have ? latestVersion : null;
        }
        if (link.FileId is long fid)
        {
            if (latest.FileId == fid) return null;
            var installed = info.Files.FirstOrDefault(f => f.FileId == fid);
            if (installed != null && line != null && !SameLine(installed.Name, line)) return latestVersion;   // another file on the page was chosen
            bool chained = info.Updates.Any(u => u[0] == fid);
            return chained || installed == null || latest.Uploaded > installed.Uploaded ? latestVersion : null;
        }
        string? mine = link.Version ?? localVersion;
        if (VersionOf(mine) is Version a && VersionOf(latestVersion) is Version b) return b > a ? latestVersion : null;
        if (mine != null && Norm(mine) == Norm(latestVersion)) return null;
        long installedAt = link.Installed is DateTime d ? new DateTimeOffset(d.ToUniversalTime()).ToUnixTimeSeconds() : 0;
        return latest.Uploaded > installedAt && installedAt > 0 ? latestVersion : mine == null ? null : latestVersion;
    }

    static string Norm(string v) => v.Trim().TrimStart('v', 'V').Replace('-', '.').ToLowerInvariant();
    static Version? VersionOf(string? v)
    {
        if (v == null) return null;
        var m = Regex.Match(Norm(v), @"^\d+(\.\d+){0,3}");
        if (!m.Success) return null;
        string s = m.Value.Contains('.') ? m.Value : m.Value + ".0";
        return Version.TryParse(s, out var r) ? r : null;
    }
}

/// <summary>A mod's link to its Nexus page (state.json, this PC): mod ID, and the installed file / version when known.</summary>
sealed class NexusLink
{
    public int ModId { get; set; }
    public long? FileId { get; set; }
    public string? Version { get; set; }
    public DateTime? Installed { get; set; }
    /// <summary>
    /// True when the link came from a Nexus download (its file name), so FileId / Version are Nexus's. False: linked
    /// by name (Find My Mods) or by hand; which Nexus file the user has is unknown, so only files uploaded after the link
    /// count as updates (Miles Morales, 2026-09-27: the mod's own v0.1 against Nexus's older, unrelated v2 read as an update).
    /// </summary>
    public bool FromNexus { get; set; }
    /// <summary>
    /// The Nexus file (its name on the page) this mod is, when the page has several side by side (a variant): updates come
    /// only from files of that name. Set from the installed file, or chosen by the user (Nexus → Choose the Nexus File…).
    /// </summary>
    public string? File { get; set; }
    /// <summary>A Nexus file the user chose to ignore (not an update for them, or not installable here).</summary>
    public long? Ignore { get; set; }
}

/// <summary>The last Nexus info fetched per mod ID (data\nexus_cache.json), so the list shows update badges at start.</summary>
sealed class NexusCache
{
    public DateTime? Checked { get; set; }
    public Dictionary<int, Nexus.ModInfo> Mods { get; set; } = [];

    static string PathFor(string home) => System.IO.Path.Combine(home, "nexus_cache.json");
    public static NexusCache Load(string home)
    {
        try { return File.Exists(PathFor(home)) ? JsonSerializer.Deserialize<NexusCache>(File.ReadAllText(PathFor(home))) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return new(); }
    }
    public void Save(string home) => File.WriteAllText(PathFor(home), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
