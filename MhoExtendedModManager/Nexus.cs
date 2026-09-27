using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MhoExtendedModManager;

/// <summary>
/// Nexus Mods: which installed mods have a newer version on Nexus, and updating them as far as Nexus allows (Kurt,
/// 2026-09-27). Uses the Nexus v1 API with the user's own personal API key (Nexus account → API keys), stored encrypted
/// for the Windows user (DPAPI). Reading mod and file info works for every account; the API only gives download links
/// to Premium members, so free members get the mod's Files page opened and the app picks the file up from Downloads.
///
/// A mod is linked to its Nexus page by (in this order) the file name Nexus gives downloads ("Name-176-1-2-1690000000.zip":
/// mod 176, version 1.2), the archive's MD5 (md5_search), the mod's own manifest (NexusModId, set by its author), or by
/// hand (paste the page). The link lives in state.json (NexusLinks); the last fetched info in data\nexus_cache.json.
///
/// MHO_EXTMM_NEXUS_API (tests): a local folder standing in for https://api.nexusmods.com/v1/ (same paths, .json files;
/// download_link URIs may be local file paths).
/// </summary>
static class Nexus
{
    public const string Game = "marvelheroesomega";
    public const string SiteMods = "https://www.nexusmods.com/" + Game + "/mods/";
    const string Api = "https://api.nexusmods.com/v1/";
    static string? Fake => Environment.GetEnvironmentVariable("MHO_EXTMM_NEXUS_API");

    public sealed class NexusException(string message) : Exception(message);

    public sealed record Account(string Name, bool Premium);
    public sealed record NexusFile(long FileId, string Name, string Version, string Category, long Uploaded, string FileName);
    public sealed record ModInfo(int ModId, string Name, string Version, long Updated, bool Available, List<NexusFile> Files, List<long[]> Updates);

    // ---- requests

    static async Task<JsonElement> Get(string apiKey, string path)
    {
        if (Fake is string f)
        {
            string file = Path.Combine(f, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) throw new NexusException("Not found on Nexus (it may be hidden or deleted).");
            if (apiKey != "test-key" && apiKey != "test-premium") throw new NexusException("Nexus didn't accept the API key.");
            return JsonDocument.Parse(await File.ReadAllTextAsync(file)).RootElement.Clone();
        }
        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.Add("apikey", apiKey);
        h.DefaultRequestHeaders.Add("Application-Name", "MHO Extended Mod Manager");
        h.DefaultRequestHeaders.Add("Application-Version", Program.Version);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);
        using var resp = await h.GetAsync(Api + path);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new NexusException("Nexus didn't accept the API key (Settings → Nexus Account…).");
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new NexusException("Not found on Nexus (it may be hidden or deleted).");
        if ((int)resp.StatusCode == 429) throw new NexusException("Nexus's request limit for today is used up; try again later.");
        if (resp.StatusCode == HttpStatusCode.Forbidden) throw new NexusException("Nexus refused this (downloads through the app need Premium).");
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public static async Task<Account> Validate(string apiKey)
    {
        var j = await Get(apiKey, "users/validate.json");
        return new Account(j.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?", j.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True);
    }

    public static async Task<ModInfo> Mod(string apiKey, int modId)
    {
        var m = await Get(apiKey, $"games/{Game}/mods/{modId}.json");
        var fs = await Get(apiKey, $"games/{Game}/mods/{modId}/files.json");
        var files = new List<NexusFile>();
        if (fs.TryGetProperty("files", out var arr))
            foreach (var x in arr.EnumerateArray())
                files.Add(new NexusFile(x.GetProperty("file_id").GetInt64(), Str(x, "name"), Str(x, "version"), Str(x, "category_name"),
                    x.TryGetProperty("uploaded_timestamp", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : 0, Str(x, "file_name")));
        var updates = new List<long[]>();
        if (fs.TryGetProperty("file_updates", out var up))
            foreach (var x in up.EnumerateArray())
                updates.Add([x.GetProperty("old_file_id").GetInt64(), x.GetProperty("new_file_id").GetInt64()]);
        return new ModInfo(modId, Str(m, "name"), Str(m, "version"), m.TryGetProperty("updated_timestamp", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
                           !m.TryGetProperty("available", out var a) || a.ValueKind != JsonValueKind.False, files, updates);
    }

    /// <summary>The Nexus mod and file an archive is, by its MD5; null if Nexus doesn't know it.</summary>
    public static async Task<(int ModId, long FileId, string Version)?> Md5(string apiKey, string archive)
    {
        string md5;
        using (var s = File.OpenRead(archive)) md5 = Convert.ToHexString(await System.Security.Cryptography.MD5.HashDataAsync(s)).ToLowerInvariant();
        JsonElement j;
        try { j = await Get(apiKey, $"games/{Game}/mods/md5_search/{md5}.json"); }
        catch (NexusException) { return null; }
        foreach (var x in j.EnumerateArray())
        {
            var mod = x.GetProperty("mod"); var fd = x.GetProperty("file_details");
            if (!Str(mod, "domain_name").Equals(Game, StringComparison.OrdinalIgnoreCase) && mod.TryGetProperty("domain_name", out _)) continue;
            return (mod.GetProperty("mod_id").GetInt32(), fd.GetProperty("file_id").GetInt64(), Str(fd, "version"));
        }
        return null;
    }

    /// <summary>Premium only: a download URL for a file.</summary>
    public static async Task<string> DownloadLink(string apiKey, int modId, long fileId)
    {
        var j = await Get(apiKey, $"games/{Game}/mods/{modId}/files/{fileId}/download_link.json");
        foreach (var x in j.EnumerateArray()) if (Str(x, "URI") is { Length: > 0 } u) return u;
        throw new NexusException("Nexus gave no download link.");
    }

    /// <summary>Downloads a file (Premium link) into <paramref name="folder"/>; returns the path.</summary>
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
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path + ".part");
        var buf = new byte[1 << 20];
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
            progress?.Report(total > 0 ? $"Downloading {fileName}: {done * 100 / total}%" : $"Downloading {fileName}: {done / 1048576} MB");
        }
        dst.Close();
        File.Move(path + ".part", path, true);
        return path;
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.ToString() : "";

    // ---- links

    static readonly Regex NexusFileName = new(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>[0-9][0-9a-zA-Z\-]*?)-(?<time>\d{9,11})(\s*\(\d+\))?\.(zip|7z|rar)$", RegexOptions.IgnoreCase);

    /// <summary>The Nexus mod and version in a downloaded file's name ("Storm Classic-176-1-2-1690000000.zip"), or null.</summary>
    public static (int ModId, string Version)? FromFileName(string path)
    {
        var m = NexusFileName.Match(Path.GetFileName(path));
        return m.Success && int.TryParse(m.Groups["id"].Value, out int id) ? (id, m.Groups["ver"].Value.Replace('-', '.')) : null;
    }

    /// <summary>A mod ID from a pasted Nexus page (…/marvelheroesomega/mods/176…) or a plain number.</summary>
    public static int? ParseModId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"/mods/(\d+)");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return int.TryParse(text.Trim(), out int id) && id > 0 ? id : null;
    }

    /// <summary>The file an update would install: the newest main file (else the newest file that isn't old or archived).</summary>
    public static NexusFile? Latest(ModInfo info)
    {
        var usable = info.Files.Where(f => !f.Category.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase) && !f.Category.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase)
                                        && !f.Category.Equals("DELETED", StringComparison.OrdinalIgnoreCase)).ToList();
        return usable.Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Uploaded).FirstOrDefault()
            ?? usable.OrderByDescending(f => f.Uploaded).FirstOrDefault();
    }

    /// <summary>
    /// The newer version on Nexus for a linked mod, or null when it's up to date. By file when the installed file is
    /// known (a newer main file, or one the file-update chain leads to), else by version number, else by upload time
    /// after the install.
    /// </summary>
    public static string? UpdateFor(NexusLink link, ModInfo info, string? localVersion, DateTime? filesMade = null)
    {
        var latest = Latest(info);
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

    // ---- the API key, encrypted for this Windows user (DPAPI)

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    static byte[]? Crypt(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, "MHO Extended Mod Manager", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output)
                              : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
            if (!ok) return null;
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }

    public static string? Protect(string? key) => string.IsNullOrWhiteSpace(key) ? null : Crypt(System.Text.Encoding.UTF8.GetBytes(key.Trim()), true) is byte[] b ? Convert.ToBase64String(b) : null;
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Crypt(Convert.FromBase64String(stored), false) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : null; }
        catch (FormatException) { return null; }
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
    /// True when the link came from a Nexus download (its file name / MD5), so FileId / Version are Nexus's. False: linked
    /// by name (Find My Mods) or by hand; which Nexus file the user has is unknown, so only files uploaded after the link
    /// count as updates (Miles Morales, 2026-09-27: the mod's own v0.1 against Nexus's older, unrelated v2 read as an update).
    /// </summary>
    public bool FromNexus { get; set; }
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
