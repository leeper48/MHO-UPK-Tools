using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MhoPackageModifier;

/// <summary>
/// Update check and self-update from the GitHub releases of leeper48/MHO-UPK-Tools. A release is a tag vX.Y.Z with an
/// asset MHO_Package_Modifier_vX.Y.Z.zip and its checksum MHO_Package_Modifier_vX.Y.Z.zip.sha256 (release.bat makes both).
/// Updating downloads the zip, refuses it unless its SHA-256 matches, unpacks it, renames the running exe aside (Windows
/// allows renaming a file in use, not overwriting it), copies the release's files over the app folder (only those: the
/// user's exports and anything else there stay) and restarts; files set aside are deleted on the next start. If any copy
/// fails, the files set aside are put back.
/// </summary>
static class Updater
{
    public const string Repo = "leeper48/MHO-UPK-Tools";
    public const string AssetPrefix = "MHO_Package_Modifier_v";

    public sealed record Release(Version Version, string Tag, string Notes, string PageUrl, string? ZipUrl, string? ShaUrl, string? ZipName);

    static readonly HttpClient http = CreateClient();
    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"MHO-Package-Modifier/{Current}");
        return c;
    }

    public static Version Current
    {
        get
        {
            string v = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
            return Version.TryParse(v, out var ver) ? ver : new Version(0, 0, 0);
        }
    }

    /// <summary>The newest release on GitHub, or null (none yet, or offline: the reason in note).</summary>
    public static async Task<(Release? Release, string Note)> LatestAsync()
    {
        try
        {
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if ((int)resp.StatusCode == 404) return (null, "no releases published yet");
            if (!resp.IsSuccessStatusCode) return (null, $"GitHub answered {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return (null, $"release tag '{tag}' isn't a version");
            string? zipUrl = null, shaUrl = null, zipName = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "", url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { zipUrl = url; zipName = name; }
            }
            foreach (var a in root.GetProperty("assets").EnumerateArray())
                if (zipName != null && (a.GetProperty("name").GetString() ?? "").Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = a.GetProperty("browser_download_url").GetString();
            return (new Release(version, tag, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", root.GetProperty("html_url").GetString() ?? "", zipUrl, shaUrl, zipName), "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, $"couldn't reach GitHub ({ex.Message})");
        }
    }

    /// <summary>Downloads, verifies and installs a release over the app folder. Returns null on success, else why not.</summary>
    public static async Task<string?> InstallAsync(Release r, Action<string> log)
    {
        if (r.ZipUrl == null || r.ShaUrl == null) return "the release has no zip and checksum to install from; download it from the release page instead";
        string appDir = AppContext.BaseDirectory, exe = Environment.ProcessPath ?? Path.Combine(appDir, "MHO_UPK_Mod.exe");
        string probe = Path.Combine(appDir, $".write_test_{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, ""); File.Delete(probe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"the app folder ({appDir}) isn't writable; move the app to a folder of your own, or update by hand"; }

        string work = Path.Combine(Path.GetTempPath(), $"MhoPackageModifier_update_{r.Version}");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, r.ZipName!);
            log($"Downloading {r.ZipName} ...");
            await using (var s = await http.GetStreamAsync(r.ZipUrl)) await using (var f = File.Create(zip)) await s.CopyToAsync(f);
            string expected = (await http.GetStringAsync(r.ShaUrl)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            return await InstallZipAsync(zip, expected, work, log);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"update failed: {ex.Message}";
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Installs a release zip that's already on disk, if its SHA-256 is <paramref name="expected"/>. Null on success.</summary>
    static async Task<string?> InstallZipAsync(string zip, string expected, string work, Action<string> log)
    {
        string appDir = AppContext.BaseDirectory;
        {
            string actual;
            await using (var f = File.OpenRead(zip)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return $"the download's checksum doesn't match the release's (got {actual[..12]}..., expected {expected[..Math.Min(12, expected.Length)]}...): not installed";
            log("Checksum OK. Unpacking ...");
            string unpacked = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, unpacked);
            // The zip holds the app folder's files, at its root or in one folder.
            string src = File.Exists(Path.Combine(unpacked, "MHO_UPK_Mod.exe")) ? unpacked
                : Directory.GetDirectories(unpacked).FirstOrDefault(d => File.Exists(Path.Combine(d, "MHO_UPK_Mod.exe"))) ?? "";
            if (src.Length == 0) return "the release zip has no MHO_UPK_Mod.exe";

            // Files in use (the running exe, assimp.dll once loaded) can be renamed but not overwritten: those are moved
            // aside to <name>.old first. If anything fails, every file set aside goes back, so the app still starts.
            var setAside = new List<string>();
            try
            {
                foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(src, f), dst = Path.Combine(appDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    try { File.Copy(f, dst, overwrite: true); }
                    catch (IOException) when (File.Exists(dst))
                    {
                        if (File.Exists(dst + ".old")) File.Delete(dst + ".old");
                        File.Move(dst, dst + ".old");
                        setAside.Add(dst);
                        File.Copy(f, dst);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (string dst in setAside)
                {
                    try { if (File.Exists(dst)) File.Delete(dst); File.Move(dst + ".old", dst); }
                    catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
                }
                return $"copying the new files failed ({ex.Message}); the files in use were put back";
            }
            log("Installed.");
            return null;
        }
    }

    /// <summary>--install-zip: a release zip downloaded by hand, checked against the .sha256 next to it, installed over this app.</summary>
    public static int InstallZipCli(string zip)
    {
        if (!File.Exists(zip) || !File.Exists(zip + ".sha256")) { Console.WriteLine($"Need the zip and its checksum file next to it: {zip}(.sha256)"); return 2; }
        string expected = File.ReadAllText(zip + ".sha256").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        string work = Path.Combine(Path.GetTempPath(), $"MhoPackageModifier_install_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(work);
            string? err = InstallZipAsync(Path.GetFullPath(zip), expected, work, Console.WriteLine).GetAwaiter().GetResult();
            Console.WriteLine(err == null ? "Installed. Start the app again to use the new version." : $"Not installed: {err}");
            return err == null ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Console.WriteLine($"Not installed: {ex.Message}"); return 1; }
        finally { try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Starts the app again (after an update) and returns; the caller exits.</summary>
    public static void Restart()
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MHO_UPK_Mod.exe");
        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
    }

    /// <summary>At start: the files the last update set aside (*.old in the app folder) are no longer in use, so they can go.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (string old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old"))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- CLI

    public static int CheckCli()
    {
        var (r, note) = LatestAsync().GetAwaiter().GetResult();
        if (r == null) { Console.WriteLine($"Update check: {note}."); return 1; }
        Console.WriteLine(r.Version > Current ? $"Update available: v{r.Version} (you have v{Current}). {r.PageUrl}\n  Install it with --update, or the Update button in the app." : $"Up to date: v{Current} (latest release v{r.Version}).");
        return 0;
    }

    public static int UpdateCli()
    {
        var (r, note) = LatestAsync().GetAwaiter().GetResult();
        if (r == null) { Console.WriteLine($"Update check: {note}."); return 1; }
        if (r.Version <= Current) { Console.WriteLine($"Up to date: v{Current}."); return 0; }
        string? err = InstallAsync(r, Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine(err == null ? $"Updated to v{r.Version}. Start the app again to use it." : $"Not updated: {err}");
        return err == null ? 0 : 1;
    }
}
