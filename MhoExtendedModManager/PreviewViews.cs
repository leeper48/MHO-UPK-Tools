using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// The 3D preview's saved camera per mod and mesh (Kurt: rotate / zoom / pan remembered), and whether animations loop.
/// Kept in data\preview_views.json, not state.json: turning a model isn't a library change, so it stays out of Undo.
/// Views are relative to the mesh (MeshViewer.ViewState), so they still fit when the mesh changes.
/// </summary>
static class PreviewViews
{
    sealed class Data
    {
        public Dictionary<string, float[]> Views { get; set; } = [];
        public bool Loop { get; set; } = true;
    }

    static Data? data;
    static string FilePath => Path.Combine(Settings.Home, "preview_views.json");
    static Data D => data ??= Load();

    static Data Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    static void Save()
    {
        try { Directory.CreateDirectory(Settings.Home); File.WriteAllText(FilePath, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string Key(Mod m, MeshRef mesh) => m.FolderName + "|" + mesh.Key;

    /// <summary>This PC's saved views for a mod, by mesh key (for putting them into an exported copy).</summary>
    public static Dictionary<string, float[]> ForMod(Mod m) =>
        D.Views.Where(kv => kv.Key.StartsWith(m.FolderName + "|", StringComparison.OrdinalIgnoreCase))
               .ToDictionary(kv => kv.Key[(m.FolderName.Length + 1)..], kv => kv.Value);
    public static float[]? Get(string key) => D.Views.TryGetValue(key, out var v) ? v : null;

    /// <summary>Saves a view (null forgets it).</summary>
    public static void Set(string key, float[]? view)
    {
        if (view == null) { if (!D.Views.Remove(key)) return; }
        else D.Views[key] = view;
        Save();
    }

    public static bool Loop
    {
        get => D.Loop;
        set { if (D.Loop == value) return; D.Loop = value; Save(); }
    }
}
