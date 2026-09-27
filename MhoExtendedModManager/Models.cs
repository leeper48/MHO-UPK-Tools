using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// The mod formats of MHModManager 1.0.1 (the existing manager by a former modder), read as it writes them so its
/// mod library works unchanged: data\mods\&lt;folder&gt;\manifest.json, &lt;lang&gt;.json string files, data\state.json.
/// </summary>
enum ModType { Texture = 0, String = 1, Upk = 2, Audio = 3, Mixed = 4 }

sealed class TextureReplacement
{
    public string TextureName { get; set; } = "";
    public string DdsFileName { get; set; } = "";
}

/// <summary>manifest.json. Texture lists target the three UI icon packages: ICO__MarvelUIIcons(_Achievements|_Store)_SF.upk.</summary>
sealed class ModManifest
{
    public string Name { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public List<TextureReplacement> Replacements { get; set; } = [];
    public List<TextureReplacement> AchievementReplacements { get; set; } = [];
    public List<TextureReplacement> StoreReplacements { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<string> UpkReplacements { get; set; } = [];
    public List<string> AudioPacks { get; set; } = [];
    // MHModManager leaves false flags and a zero count out of its manifests; so do we (re-saving a mod gives the same file).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool HasTextures { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool HasStrings { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool HasUpkReplacements { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool HasAudio { get; set; }
    public ModType Type { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public int TextureReplacementCount { get; set; }

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // & and ' as they are, like MHModManager's files

    public static ModManifest Load(string path) =>
        JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty manifest");
}

/// <summary>data\state.json. ModOrder is top = highest priority (the old manager: "Higher-priority mods (top of list) will win").</summary>
sealed class ModState
{
    public List<string> EnabledMods { get; set; } = [];
    public List<string> ModOrder { get; set; } = [];
    public string ActiveLanguage { get; set; } = "eng";

    public static ModState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ModState>(File.ReadAllText(path), ModManifest.Json) ?? new() : new();
}

/// <summary>
/// One string replacement from &lt;lang&gt;.json: { "eng.all_7FFF….string": { "&lt;id&gt;": { "String": "…", "FlagsProduced": 0, "Variants": [] } } }.
/// </summary>
/// <param name="Variants">Null when the JSON's Variants list is empty: the original's variants are kept (as MHModManager does).</param>
sealed record StringReplacement(string Language, string File, ulong Id, string Text, ushort FlagsProduced = 0, IReadOnlyList<StringFile.Variant>? Variants = null);

/// <summary>One installed mod: its folder under data\mods, manifest, and priority (0 = top).</summary>
sealed class Mod
{
    public required string Folder { get; init; }
    public required string FolderName { get; init; }
    public ModManifest Manifest { get; init; } = new();
    public string? LoadError { get; init; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public List<StringReplacement> Strings { get; } = [];

    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? FolderName : Manifest.Name;

    /// <summary>Everything this mod needs from its folder that isn't there.</summary>
    public IEnumerable<string> MissingFiles()
    {
        foreach (var r in Manifest.Replacements.Concat(Manifest.AchievementReplacements).Concat(Manifest.StoreReplacements))
            if (!File.Exists(Path.Combine(Folder, r.DdsFileName))) yield return r.DdsFileName;
        foreach (string f in Manifest.UpkReplacements.Concat(Manifest.AudioPacks))
            if (!File.Exists(Path.Combine(Folder, f))) yield return f;
        foreach (string l in Manifest.Languages)
            if (!File.Exists(Path.Combine(Folder, l + ".json"))) yield return l + ".json";
    }

    /// <summary>Short category list, e.g. "packages 6, textures 8, strings 1".</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Manifest.UpkReplacements.Count > 0) parts.Add($"packages {Manifest.UpkReplacements.Count}");
        int tex = Manifest.Replacements.Count + Manifest.AchievementReplacements.Count + Manifest.StoreReplacements.Count;
        if (tex > 0) parts.Add($"textures {tex}");
        if (Strings.Count > 0) parts.Add($"strings {Strings.Count}");
        if (Manifest.AudioPacks.Count > 0) parts.Add($"sound packs {Manifest.AudioPacks.Count}");
        return string.Join(", ", parts);
    }
}
