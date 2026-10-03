using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MhoExtendedModManager;

/// <summary>
/// Where things are: the game root (the folder holding UnrealEngine3 and Data) and the mod library.
/// Everything the manager keeps lives in a "data" folder next to the exe (portable, like MHModManager):
/// data\settings.json, data\library (unless moved: Settings… → Move library), data\history (undo snapshots).
/// Not AppData: Claude's shell sees a private AppData (it runs inside the Claude app's package), so the two never
/// matched; a folder next to the exe is the same for everyone. Versions up to 0.7.0 used AppData; the first start of
/// a later version offers to move that library here (MoveFromAppData).
/// The game root is found from Steam on first run, or asked for once.
/// MHO_EXTMM_HOME (tests): the data folder is that folder instead.
/// </summary>
sealed class Settings
{
    public string? GameRoot { get; set; }
    /// <summary>A folder holding the game's stock packages (a clean CookedPCConsole copy, Kurt's H:\\CookedPCConsole - Original
    /// Backup): read only, each file checked against the stock checksums before use. Null when not set.</summary>
    public string? CleanGameFiles { get; set; }
    /// <summary>The Model tab (MFF models onto a mod's package): the MFF rip folder (Models\Models, Texture2D; read only), the
    /// Blender it opens exports in (null = found), and whether its add-on offer was turned off.</summary>
    public string? MffFolder { get; set; }
    public string? BlenderPath { get; set; }
    public bool SkipBlenderAddonOffer { get; set; }
    /// <summary>Null = the default location. May also be MHModManager's folder (read-only there until migrated).</summary>
    public string? Library { get; set; }
    /// <summary>The mod list's sort (priority, name, author, tag, enabled) and grouping (none, tag, author).</summary>
    public string ListSort { get; set; } = "priority";
    /// <summary>The mod list's width as a fraction of the window (null: 30%), kept between starts (a user's request).</summary>
    public float? ListWidth { get; set; }
    /// <summary>The main window's monitor, size and position (a user's request: it always opened maximized on the main
    /// monitor): its normal bounds (x, y, width, height) and whether it was maximized, restored at start while that spot is
    /// on a connected monitor. <see cref="RememberWindow"/> off = the old behavior (maximized on the main monitor).</summary>
    public bool RememberWindow { get; set; } = true;
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; } = true;
    /// <summary>Settings → Download Counts: the total seen last time and when (to show the change since).</summary>
    public int? LastDownloadTotal { get; set; }
    public DateTime? LastDownloadCheck { get; set; }
    public string ListGroup { get; set; } = "none";
    /// <summary>Updates: look for a new version at start and every hour while open; a version the user chose to skip.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Features not released yet (Kurt, 2026-10-01: the editor's Powers tab). Off by default; set to true in
    /// settings.json by hand to see them. No menu item on purpose.</summary>
    public bool PreviewFeatures { get; set; }
    /// <summary>The user's name as a mod author: filled in on every new mod (Settings → Your Author Name; Kurt, 2026-10-03).
    /// Before, a new mod took the author of the most recently changed mod folder, often someone else's.</summary>
    public string? AuthorName { get; set; }
    /// <summary>The last "what's new" notice shown at start (MainForm.WhatsNew), so each is shown once.</summary>
    public string? WhatsNewSeen { get; set; }
    /// <summary>The manual's text size in percent (A− / A+ in the Help window).</summary>
    public int ManualTextSize { get; set; } = 100;
    /// <summary>The user was asked once whether the app may look for updates at start (nothing is sent without that).</summary>
    public bool UpdateCheckAsked { get; set; }
    /// <summary>Nexus Mods: check the linked mods (public data, no account) at start. Off unless the user turns it on.
    /// (Settings from 0.23–0.26 may hold a NexusApiKey: it's no longer read, and the next save drops it.)</summary>
    public bool NexusCheckAtStart { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkipVersion { get; set; }
    /// <summary>The newest release a check found (the top bar's update alert shows it until it's installed or skipped).</summary>
    public string? LatestKnownVersion { get; set; }

    static string? TestHome => Environment.GetEnvironmentVariable("MHO_EXTMM_HOME");
    /// <summary>The data folder: next to the exe (or MHO_EXTMM_HOME).</summary>
    public static string Home => TestHome ?? Path.Combine(AppContext.BaseDirectory, "data");
    static string SettingsFile => Path.Combine(Home, "settings.json");
    public static string DefaultLibrary => Path.Combine(Home, "library");
    public static string HistoryFolder => Path.Combine(Home, "history");

    /// <summary>Null if the data folder can be written, else why not (e.g. the exe is under Program Files).</summary>
    public static string? CheckWritable()
    {
        try
        {
            Directory.CreateDirectory(Home);
            string probe = Path.Combine(Home, ".write_test");
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"{Home}: {ex.Message}"; }
    }

    // Where versions up to 0.7.0 kept things.
    static string LegacySettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MhoExtendedModManager", "settings.json");
    static string LegacyLibrary => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MhoExtendedModManager", "library");

    /// <summary>An AppData library from an earlier version, when this data folder has no settings yet (else null).</summary>
    public static string? FindAppDataLibrary()
    {
        if (TestHome != null || File.Exists(SettingsFile)) return null;
        Settings? old = null;
        try { if (File.Exists(LegacySettingsFile)) old = JsonSerializer.Deserialize<Settings>(File.ReadAllText(LegacySettingsFile)); }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        string lib = string.IsNullOrEmpty(old?.Library) ? LegacyLibrary : old.Library;
        return File.Exists(Path.Combine(lib, "state.json")) ? lib : null;
    }

    /// <summary>
    /// Moves an earlier version's AppData library into data\library (a library the user moved elsewhere stays there)
    /// and carries the game folder over. Returns the new settings.
    /// </summary>
    /// <param name="legacySettings">Test hook: the old settings file (default: the AppData one).</param>
    /// <param name="legacyDefault">Test hook: the old default library location (default: the AppData one).</param>
    public static Settings MoveFromAppData(string oldLibrary, string? legacySettings = null, string? legacyDefault = null)
    {
        legacySettings ??= LegacySettingsFile; legacyDefault ??= LegacyLibrary;
        Settings? old = null;
        try { if (File.Exists(legacySettings)) old = JsonSerializer.Deserialize<Settings>(File.ReadAllText(legacySettings)); }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        var s = new Settings { GameRoot = old?.GameRoot };
        if (Path.GetFullPath(oldLibrary).Equals(Path.GetFullPath(legacyDefault), StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetPathRoot(Path.GetFullPath(oldLibrary))!.Equals(Path.GetPathRoot(Path.GetFullPath(DefaultLibrary)), StringComparison.OrdinalIgnoreCase) && !Directory.Exists(DefaultLibrary))
            {
                Directory.CreateDirectory(Home);
                Directory.Move(oldLibrary, DefaultLibrary);          // same drive: instant
            }
            else ModInstaller.MoveLibrary(oldLibrary, DefaultLibrary);   // copy, verify, delete
        }
        else s.Library = oldLibrary;
        s.Save();
        return s;
    }

    [System.Text.Json.Serialization.JsonIgnore] public string LibraryPath => string.IsNullOrEmpty(Library) ? DefaultLibrary : Library;

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new();
            string text = File.ReadAllText(SettingsFile);
            var s = JsonSerializer.Deserialize<Settings>(text) ?? new();
            // 0.23–0.26 kept a personal Nexus API key here (encrypted). Nexus doesn't allow apps to use personal keys, so
            // the app no longer reads one: rewrite the file at once so the old key isn't left on disk.
            if (text.Contains("\"NexusApiKey\"", StringComparison.Ordinal)) try { s.Save(); } catch (IOException) { }
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The library's data folder (holds mods\ and state.json): the folder itself, or its data\ subfolder.</summary>
    public static string? LibraryData(string? library)
    {
        if (string.IsNullOrEmpty(library)) return null;
        if (Directory.Exists(Path.Combine(library, "data", "mods"))) return Path.Combine(library, "data");
        return Directory.Exists(Path.Combine(library, "mods")) ? library : null;
    }

    /// <summary>The game root: set here, or else the one MHModManager saved in data\config.txt.</summary>
    public string? ResolvedGameRoot(string? dataFolder)
    {
        if (!string.IsNullOrEmpty(GameRoot)) return GameRoot;
        string? cfg = dataFolder == null ? null : Path.Combine(dataFolder, "config.txt");
        return cfg != null && File.Exists(cfg) ? File.ReadAllText(cfg).Trim() : null;
    }

    /// <summary>Set up = a game folder that exists and a library with a state file (first run otherwise).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSetUp =>
        GameRoot != null && IsGameRoot(GameRoot) && Settings.LibraryData(LibraryPath) is string d && File.Exists(Path.Combine(d, "state.json"));

    /// <summary>The path with each folder name as it is on disk (Steam's library list is lower case: "g:\\program files (x86)\\…").</summary>
    public static string TrueCase(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            if (!dir.Exists) return path;
            var parts = new Stack<string>();
            while (dir.Parent != null)
            {
                parts.Push(dir.Parent.GetDirectories(dir.Name).FirstOrDefault()?.Name ?? dir.Name);
                dir = dir.Parent;
            }
            return Path.Combine([dir.Name.ToUpperInvariant(), .. parts]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return path; }
    }

    public static string Cooked(string gameRoot) => Path.Combine(gameRoot, "UnrealEngine3", "MarvelGame", "CookedPCConsole");
    public static bool IsGameRoot(string root) => Directory.Exists(Cooked(root));

    /// <summary>
    /// Marvel Heroes installs Steam knows about: its install folder from the registry, then every library in
    /// steamapps\libraryfolders.vdf, each checked for steamapps\common\Marvel Heroes*.
    /// </summary>
    public static List<string> FindGame()
    {
        var steam = new List<string>();
        foreach (var (hive, key, value) in new[] { (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"), (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"), (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath") })
            try { using var k = hive.OpenSubKey(key); if (k?.GetValue(value) is string s) steam.Add(Path.GetFullPath(s.Replace('/', '\\'))); }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        steam.Add(@"C:\Program Files (x86)\Steam");
        var libraries = new List<string>();
        foreach (string root in steam.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
        }
        var found = new List<string>();
        foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string common = Path.Combine(lib, "steamapps", "common");
            if (!Directory.Exists(common)) continue;
            foreach (string dir in Directory.GetDirectories(common, "Marvel Heroes*"))
                if (IsGameRoot(dir) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase)) found.Add(TrueCase(dir));
        }
        return found;
    }

    /// <summary>MHModManager installs in the usual places (Desktop, Documents, Downloads, OneDrive, C:\Games …), a few levels deep.</summary>
    public static List<string> FindOldManager(TimeSpan budget)
    {
        var found = new List<string>();
        var until = DateTime.UtcNow + budget;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "Desktop", "Documents", "Downloads", "OneDrive" }.Select(d => Path.Combine(home, d)).Append(@"C:\Games").Append(@"C:\Tools").Where(Directory.Exists);
        void Walk(string dir, int depth)
        {
            if (DateTime.UtcNow > until || depth > 5) return;
            try
            {
                if (File.Exists(Path.Combine(dir, "MHModManager.exe")) && Directory.Exists(Path.Combine(dir, "data", "mods"))) { found.Add(dir); return; }
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    var attr = File.GetAttributes(d);
                    if ((attr & FileAttributes.System) != 0) continue;   // not ReparsePoint: OneDrive folders carry it (depth limit stops junction loops)
                    Walk(d, depth + 1);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (string r in roots) Walk(r, 0);
        // Most recently used first (its state.json changes whenever mods are turned on or off).
        return found.OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "data", "state.json"))).ToList();
    }
}
