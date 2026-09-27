using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MhoPackageModifier;

/// <summary>
/// Finds the game's CookedPCConsole folder for a first run: every Steam library (Steam's install path from the registry,
/// then steamapps\libraryfolders.vdf) checked for Marvel Heroes, plus the usual Program Files places.
/// </summary>
static class GameFolder
{
    static readonly string[] GameDirs = ["Marvel Heroes", "Marvel Heroes Omega", "Marvel Heroes 2016"];
    const string Cooked = @"UnrealEngine3\MarvelGame\CookedPCConsole";

    public static string? Detect()
    {
        var libraries = new List<string>();
        foreach (var (hive, key) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"), (Registry.LocalMachine, @"SOFTWARE\Valve\Steam") })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if ((k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) is string steam && steam.Length > 0)
                {
                    steam = steam.Replace('/', '\\');
                    libraries.Add(steam);
                    string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (pf.Length > 0) libraries.Add(Path.Combine(pf, "Steam"));
        foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string game in GameDirs)
            {
                string c = Path.Combine(lib, "steamapps", "common", game, Cooked);
                if (Directory.Exists(c)) return c;
            }
        return null;
    }
}
