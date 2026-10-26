using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// What was done to each mod and when (Kurt, 2026-10-26: snapshots should show the Crash Risk fixes too): installs, updates,
/// Fix Mirror Tables, Make Unique (and why: Overlap or Crash Risk), Undo Make Unique, Keep Shared Paths, enemy-only packages
/// taken out. Kept in data\library\mod_history.json (folder name → entries, oldest first); every snapshot carries it, so a
/// comparison lists what was done between two points in time. Small: one line per action.
/// </summary>
static class ModHistory
{
    public sealed record Entry(DateTime Time, string Action, string? Detail, string? Name, string? Version);

    static readonly object gate = new();

    static string FileOf(string libraryData) => Path.Combine(libraryData, "mod_history.json");

    public static Dictionary<string, List<Entry>> Load(string libraryData)
    {
        lock (gate)
        {
            try
            {
                string p = FileOf(libraryData);
                if (File.Exists(p) && JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(File.ReadAllText(p)) is { } d)
                    return new Dictionary<string, List<Entry>>(d, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>One action on a mod (by its folder name), now.</summary>
    public static void Add(ModLibrary lib, Mod m, string action, string? detail = null) => Add(lib.DataFolder, m.FolderName, action, detail, m.Name, m.Manifest.Version);

    public static void Add(string libraryData, string folder, string action, string? detail = null, string? name = null, string? version = null)
    {
        lock (gate)
        {
            var all = Load(libraryData);
            if (!all.TryGetValue(folder, out var list)) all[folder] = list = [];
            list.Add(new Entry(DateTime.UtcNow, action, detail, name, version));
            if (list.Count > 200) list.RemoveRange(0, list.Count - 200);
            try
            {
                string p = FileOf(libraryData), tmp = p + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, p, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
