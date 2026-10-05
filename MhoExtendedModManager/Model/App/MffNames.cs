using System.Text.Json;

namespace MhoExtendedModManager.Model;

/// <summary>
/// Who each MFF model folder is (the character and uniform name from the Future Fight Wiki's uniform order, matched by the
/// index tool, 2026-09-30; embedded as StockData\mff_names.json). A folder that isn't listed shows its folder name.
/// </summary>
static class MffNames
{
    public sealed record Entry(string? Kind, string? Character, int? Uniform, string? UniformName, string? Ver);

    static Dictionary<string, Entry>? all;

    public static IReadOnlyDictionary<string, Entry> All => all ??= Load();

    static Dictionary<string, Entry> Load()
    {
        using var s = typeof(MffNames).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.mff_names.json")
            ?? throw new InvalidOperationException("mff_names.json is not embedded");
        using var doc = JsonDocument.Parse(s);
        var d = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.GetProperty("models").EnumerateObject())
        {
            var v = p.Value;
            string? S(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            int? I(string n) => v.TryGetProperty(n, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null;
            d[p.Name] = new Entry(S("kind"), S("character"), I("uniform"), S("uniformName"), S("ver"));
        }
        return d;
    }

    /// <summary>"Gamora", "Guardians of the Galaxy 2 (S02)" for hero_gamora01_S02; the folder name when unknown.</summary>
    public static (string Title, string Detail) Describe(string folder)
    {
        if (!All.TryGetValue(folder, out var e) || e.Character == null) return (folder, "");
        string slot = e.Uniform is int u and > 0 ? $"S{u:00}" : "Base";
        return (e.Character, e.UniformName is { Length: > 0 } n ? $"{n} ({slot})" : slot);
    }

    /// <summary>
    /// A character (0.13.2, Kurt: Characters Only instead of Heroes Only, so villains show and stray props don't): a hero or
    /// a named villain (sv_), or any other model with a 3ds Max Biped character rig (bosses, enemies, NPCs, person-like
    /// summons). Out: props (abn_ cocoons, barriers), and models without a Biped rig (drones, boxes, serpents; the list from
    /// --survey, StockData\mff_rigs.json). A folder the list doesn't know is judged by its kind.
    /// </summary>
    public static bool IsCharacter(string folder)
    {
        string kind = All.TryGetValue(folder, out var e) && e.Kind != null ? e.Kind : folder.Split('_')[0].ToLowerInvariant();
        if (kind is "hero" or "sv") return true;
        if (kind == "abn") return false;
        return !NotBiped.Contains(folder);
    }

    static HashSet<string>? notBiped;
    static HashSet<string> NotBiped => notBiped ??= LoadRigs();

    static HashSet<string> LoadRigs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var s = typeof(MffNames).Assembly.GetManifestResourceStream("MhoMffImporter.StockData.mff_rigs.json");
        if (s == null) return set;
        using var doc = JsonDocument.Parse(s);
        foreach (var f in doc.RootElement.GetProperty("notBiped").EnumerateArray()) if (f.GetString() is string n) set.Add(n);
        return set;
    }

    public static bool IsHero(string folder) => All.TryGetValue(folder, out var e) ? e.Kind == "hero" : folder.StartsWith("hero_", StringComparison.OrdinalIgnoreCase);
}
