using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// Checks for a newer version and updates the app in place (Kurt, 2026-09-27: GitHub Releases, framework-dependent).
///
/// Releases live on the public repo leeper48/MHO-UPK-Tools, tagged "extmm-v&lt;version&gt;" (the repo holds other tools),
/// each with two assets: MHO_Ext_ModManager-&lt;version&gt;.zip (the app folder, no data\) and the same name + ".sha256"
/// (the zip's SHA-256 in hex). make_release.ps1 builds both. An update without a matching SHA-256 is refused.
///
/// Update: download to data\update\&lt;version&gt;\, verify the hash, unpack, check the new exe's version, then for every
/// file rename the current one to *.old (Windows lets a running exe / loaded dll be renamed) and copy the new one in.
/// Any failure puts the renamed files back. The *.old files are deleted on the next start. data\ is never touched.
///
/// MHO_EXTMM_UPDATE_FEED (tests): a local folder with releases.json (GitHub's release list format) whose asset
/// "browser_download_url"s are file paths.
/// </summary>
static class Updater
{
    public const string Repo = "leeper48/MHO-UPK-Tools";
    public const string TagPrefix = "extmm-v";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";

    public sealed record Release(Version Version, string Tag, string Name, string Notes, string PageUrl, string ZipUrl, string ShaUrl, long ZipSize);

    static string? Feed => Environment.GetEnvironmentVariable("MHO_EXTMM_UPDATE_FEED");

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MHO-Ext-ModManager/" + Program.Version);   // GitHub's API requires one
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current => Version.TryParse(Program.Version, out var v) ? v : new Version(0, 0);

    static async Task<string> GetText(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return await File.ReadAllTextAsync(url);
        using var h = Http();
        return await h.GetStringAsync(url);
    }

    /// <summary>The newest published release of this app (not drafts or pre-releases), or null if there is none.</summary>
    public static async Task<Release?> Latest()
    {
        string json = Feed is string f ? await File.ReadAllTextAsync(Path.Combine(f, "releases.json")) : await GetText($"https://api.github.com/repos/{Repo}/releases?per_page=50");
        using var doc = JsonDocument.Parse(json);
        Release? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean() || r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) || !Version.TryParse(tag[TagPrefix.Length..], out var ver)) continue;
            string? zip = null, sha = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "", url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zip = url; size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0; }
            }
            if (zip == null || sha == null) continue;
            if (best == null || ver > best.Version)
                best = new Release(ver, tag, r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                                   r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage, zip, sha, size);
        }
        return best;
    }

    /// <summary>One release's download counts (GitHub counts every download of each file; .sha256 files left out).</summary>
    public sealed record Downloads(string Tag, Version Version, DateTime Published, int Zip, int Setup)
    {
        public int Total => Zip + Setup;
    }

    /// <summary>
    /// Every published release of this app with its download counts (Kurt, 2026-09-30), newest first, from GitHub's public
    /// release list (no account needed). Counts are downloads, not people: the zip includes in-app updates.
    /// </summary>
    public static async Task<List<Downloads>> DownloadCounts()
    {
        var list = new List<Downloads>();
        for (int page = 1; page <= 20; page++)
        {
            string json = Feed is string f ? (page == 1 ? await File.ReadAllTextAsync(Path.Combine(f, "releases.json")) : "[]")
                : await GetText($"https://api.github.com/repos/{Repo}/releases?per_page=100&page={page}");
            using var doc = JsonDocument.Parse(json);
            int n = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                n++;
                if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                string tag = r.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) || !Version.TryParse(tag[TagPrefix.Length..], out var ver)) continue;
                int zip = 0, setup = 0;
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    int c = a.TryGetProperty("download_count", out var dc) ? dc.GetInt32() : 0;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip += c;
                    else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup += c;
                }
                var published = r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String && pa.TryGetDateTime(out var dt) ? dt.ToLocalTime() : DateTime.MinValue;
                list.Add(new Downloads(tag, ver, published, zip, setup));
            }
            if (n < 100) break;
        }
        return [.. list.OrderByDescending(x => x.Version)];
    }

    /// <summary>Downloads, verifies and installs a release over the running app. Returns null when done, else why not.</summary>
    public static async Task<string?> Install(Release r, Action<string> progress)
    {
        string appDir = AppContext.BaseDirectory;
        string work = Path.Combine(Settings.Home, "update", r.Version.ToString());
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, "release.zip"), files = Path.Combine(work, "files");

        progress($"Downloading version {r.Version}…");
        if (r.ZipUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var h = Http();
            using var s = await h.GetStreamAsync(r.ZipUrl);
            using var o = File.Create(zip);
            await s.CopyToAsync(o);
        }
        else File.Copy(r.ZipUrl, zip);

        string expected = (await GetText(r.ShaUrl)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        string actual;
        using (var fs = File.OpenRead(zip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (expected.Length != 64 || actual != expected) return $"the download doesn't match its published SHA-256 (expected {expected}, got {actual}); nothing was changed.";

        progress("Unpacking…");
        ZipFile.ExtractToDirectory(zip, files);
        // A zip holding one folder: use that folder.
        if (!File.Exists(Path.Combine(files, "MHO_Ext_ModManager.exe")) && Directory.GetDirectories(files) is [string only] && Directory.GetFiles(files).Length == 0) files = only;
        string newExe = Path.Combine(files, "MHO_Ext_ModManager.exe");
        if (!File.Exists(newExe)) return "the release has no MHO_Ext_ModManager.exe; nothing was changed.";
        string newVersion = (FileVersionInfo.GetVersionInfo(newExe).ProductVersion ?? "").Split('+')[0];
        if (!Version.TryParse(newVersion, out var nv) || nv != r.Version) return $"the release says {r.Version} but its program is {newVersion}; nothing was changed.";

        progress("Installing…");
        var swapped = new List<(string Target, string? Old)>();
        try
        {
            foreach (string src in Directory.GetFiles(files, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(files, src);
                if (rel.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // never the user's data
                string target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                swapped.Add((target, old));
                File.Copy(src, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put everything back as it was.
            foreach (var (target, old) in Enumerable.Reverse(swapped))
            {
                try { if (File.Exists(target)) File.Delete(target); if (old != null) File.Move(old, target); } catch (Exception) { }
            }
            return $"installing failed ({ex.Message}); the old version was put back.";
        }
        try { Directory.Delete(work, true); } catch (IOException) { }
        progress($"Updated to {r.Version}.");
        return null;
    }

    /// <summary>At start: removes the *.old files an update left (they were in use while it ran).</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (string f in Directory.GetFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
                if (!f.StartsWith(Path.Combine(AppContext.BaseDirectory, "data"), StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Starts the (new) exe and lets this process end.</summary>
    public static void Restart() => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "MHO_Ext_ModManager.exe")) { UseShellExecute = false });
}
