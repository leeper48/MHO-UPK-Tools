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

/// <summary>
/// Extension (this manager only): a texture in one of the other icon packages (ICO__SilverSurferIcons_SF, ICO__MarvelUIIcons_HD_SF,
/// …) that MHModManager's three lists can't target. Stored in the manifest's ExtraIconReplacements, a field MHModManager's
/// loader ignores (checked with its own code 2026-09-27), so such mods still install there, just without these images.
/// </summary>
sealed class ExtraIconReplacement
{
    public string Package { get; set; } = "";
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
    /// <summary>Extension; null (and left out of the file) when unused, so legacy manifests re-save byte-identical.</summary>
    public List<ExtraIconReplacement>? ExtraIconReplacements { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<ExtraIconReplacement> Extra => ExtraIconReplacements ?? [];
    /// <summary>Extension: the mod's own tags (set in the editor; they travel with the mod). Null when none.</summary>
    public List<string>? Tags { get; set; }
    /// <summary>Extension: the mod's note (shown under the store image; travels with the mod). Null when none.</summary>
    public string? Notes { get; set; }
    /// <summary>Extension: the mod's description and per-version changelog (newest first), for its Nexus / Discord posts.</summary>
    public string? Description { get; set; }
    public List<ChangelogEntry>? Changelog { get; set; }
    /// <summary>Extension: the mod's Nexus Mods page (mod ID on nexusmods.com/marvelheroesomega), set by its author.</summary>
    public int? NexusModId { get; set; }
    /// <summary>Extension: the picture the manager shows big for this mod, chosen by its author ("mod:&lt;texture&gt;" for one
    /// of its own images, "game:&lt;texture&gt;" for the game's original). Null: chosen automatically.</summary>
    public string? PreviewImage { get; set; }
    /// <summary>Extension: the picture for the mod's card in the list, chosen by its author (a TextureName the mod replaces;
    /// Kurt: a mod with many packages / images picks which one). Null: automatic. A user's own pick wins on their PC.</summary>
    public string? CardPicture { get; set; }
    /// <summary>Extension: the author's 3D camera per mesh ("mesh:&lt;package&gt;|&lt;mesh&gt;" → MeshViewer.ViewState), the view a
    /// user starts from (their own turning is kept on their PC and wins there). Null when none.</summary>
    public Dictionary<string, float[]>? PreviewViews { get; set; }
    /// <summary>Extension: the author's light level for the 3D preview (0.5–2), the one a user starts from (their own, kept on
    /// their PC, wins there). Null when not set.</summary>
    public float? PreviewLight { get; set; }
    /// <summary>Extension: voice lines turned off in the editor (so they can be turned on again). Null when none.</summary>
    public List<VoiceOffEntry>? VoiceOff { get; set; }
    /// <summary>Extension: power colours set in the editor (the packages they built are ordinary package replacements). Null when none.</summary>
    public List<PowerColorEntry>? PowerColors { get; set; }
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
    /// <summary>Extension (ignored by MHModManager): mods locked at the top / bottom of the order, each a run from its end.
    /// Null when there are none, so a state.json without locks stays in the old manager's form.</summary>
    public List<string>? LockedTop { get; set; }
    public List<string>? LockedBottom { get; set; }
    /// <summary>Extension: the user's tags per mod folder (for search, sort and group). Null when no mod has tags.</summary>
    public Dictionary<string, List<string>>? Tags { get; set; }
    /// <summary>Extension: automatic or mod tags the user took off a mod on this PC.</summary>
    public Dictionary<string, List<string>>? HiddenTags { get; set; }
    /// <summary>Extension: the user's own note per mod (replaces the mod's note on this PC).</summary>
    public Dictionary<string, string>? Notes { get; set; }
    /// <summary>Extension: each mod's link to its Nexus page and the installed file / version (this PC).</summary>
    public Dictionary<string, NexusLink>? NexusLinks { get; set; }
    /// <summary>Extension: the picture the user picked to show big for a mod (this PC; see ModManifest.PreviewImage).</summary>
    public Dictionary<string, string>? Previews { get; set; }
    /// <summary>Extension: the picture the user picked for a mod's card in the list (a TextureName the mod replaces; this PC).</summary>
    public Dictionary<string, string>? CardPictures { get; set; }

    /// <summary>A mod folder was renamed (editor): its lock and tags follow it.</summary>
    public void RenameMod(string from, string to)
    {
        foreach (var l in new[] { LockedTop, LockedBottom })
            if (l != null) for (int i = 0; i < l.Count; i++) if (l[i].Equals(from, StringComparison.OrdinalIgnoreCase)) l[i] = to;
        Move(Tags, from, to); Move(HiddenTags, from, to); Move(Notes, from, to); Move(NexusLinks, from, to); Move(Previews, from, to); Move(CardPictures, from, to);
    }

    static void Move<T>(Dictionary<string, T>? d, string from, string to)
    {
        if (d != null && d.Keys.FirstOrDefault(k => k.Equals(from, StringComparison.OrdinalIgnoreCase)) is string key) { var v = d[key]; d.Remove(key); d[to] = v; }
    }

    static void Drop<T>(Dictionary<string, T>? d, string name)
    {
        if (d != null && d.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) is string key) d.Remove(key);
    }

    /// <summary>A mod was removed: drop its lock and tags.</summary>
    public void ForgetMod(string name)
    {
        LockedTop?.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        LockedBottom?.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        Drop(Tags, name); Drop(HiddenTags, name); Drop(Notes, name); Drop(NexusLinks, name); Drop(Previews, name); Drop(CardPictures, name);
    }

    /// <summary>
    /// ModOrder with the locks applied: top-locked mods first, bottom-locked last, the rest between in their given order.
    /// Everything that adds a mod (install, New Mod, capture) calls this before writing state.json, so a new mod put
    /// at the top lands right under the top-locked run and one put at the bottom right above the bottom-locked run.
    /// </summary>
    public void ApplyLocks()
    {
        var top = new HashSet<string>(LockedTop ?? [], StringComparer.OrdinalIgnoreCase);
        var bottom = new HashSet<string>(LockedBottom ?? [], StringComparer.OrdinalIgnoreCase);
        if (top.Count + bottom.Count == 0) return;
        ModOrder = ModOrder.Where(top.Contains).Concat(ModOrder.Where(n => !top.Contains(n) && !bottom.Contains(n))).Concat(ModOrder.Where(bottom.Contains)).ToList();
    }

    public static ModState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<ModState>(File.ReadAllText(path), ModManifest.Json) ?? new() : new();
}

/// <summary>
/// One string replacement from &lt;lang&gt;.json: { "eng.all_7FFF….string": { "&lt;id&gt;": { "String": "…", "FlagsProduced": 0, "Variants": [] } } }.
/// </summary>
/// <param name="Variants">Null when the JSON's Variants list is empty: the original's variants are kept (as MHModManager does).</param>
sealed record StringReplacement(string Language, string File, ulong Id, string Text, ushort FlagsProduced = 0, IReadOnlyList<StringFile.Variant>? Variants = null);

/// <summary>One version's changes (manifest extension "Changelog").</summary>
sealed class ChangelogEntry
{
    public string Version { get; set; } = "";
    public string Changes { get; set; } = "";
}

/// <summary>A mod pinned to the top or bottom of the priority order (Kurt, 2026-09-27).</summary>
enum ModLock { None, Top, Bottom }

/// <summary>One installed mod: its folder under data\mods, manifest, and priority (0 = top).</summary>
sealed class Mod
{
    public required string Folder { get; init; }
    public required string FolderName { get; init; }
    public ModManifest Manifest { get; init; } = new();
    public string? LoadError { get; init; }
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public ModLock Lock { get; set; }
    /// <summary>The user's own tags (state.json, this PC only).</summary>
    public List<string> UserTags { get; set; } = [];
    /// <summary>Automatic or mod tags the user took off this mod (state.json).</summary>
    public List<string> HiddenTags { get; set; } = [];
    List<string>? auto;
    /// <summary>Tags worked out from the content (characters, teams, costume, powers …; AutoTags).</summary>
    public List<string> AutoTags => auto ??= MhoExtendedModManager.AutoTags.For(Manifest);
    public List<string> ModTags => Manifest.Tags ?? [];

    /// <summary>Every tag the mod shows: automatic, the mod's own and the user's, minus the ones the user took off.</summary>
    public List<string> Tags =>
        AutoTags.Concat(ModTags).Concat(UserTags).Where(t => !string.IsNullOrWhiteSpace(t) && !HiddenTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public enum TagKind { Auto, Mod, User }
    /// <summary>Where a tag comes from: the user (theirs), the mod (its author), or worked out automatically.</summary>
    public TagKind KindOf(string tag) =>
        UserTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? TagKind.User : ModTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ? TagKind.Mod : TagKind.Auto;

    /// <summary>The picture the user picked to show big (state.json); null = the mod's choice, else automatic.</summary>
    public string? LocalPreview { get; set; }

    /// <summary>The card picture the user picked (a TextureName the mod replaces; state.json); null = automatic.</summary>
    public string? LocalCard { get; set; }

    /// <summary>Every image the mod replaces that could be its card picture: (texture, .dds file), existing files only.</summary>
    public List<(string Texture, string File)> CardCandidates() =>
        [.. Manifest.Replacements.Select(x => (x.TextureName, x.DdsFileName))
            .Concat(Manifest.Extra.Select(x => (x.TextureName, x.DdsFileName)))
            .Concat(Manifest.StoreReplacements.Select(x => (x.TextureName, x.DdsFileName)))
            .Concat(Manifest.AchievementReplacements.Select(x => (x.TextureName, x.DdsFileName)))
            .Where(x => x.TextureName != null && x.DdsFileName != null && File.Exists(Path.Combine(Folder, x.DdsFileName)))
            .Select(x => (x.TextureName!, Path.Combine(Folder, x.DdsFileName!)))
            .Concat(ModPictures.Own(Folder))   // its own custom pictures ("file:Pictures/…")
            .DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The user's own note (state.json); null = the mod's note is shown.</summary>
    public string? LocalNote { get; set; }
    public string Note => LocalNote ?? Manifest.Notes ?? "";

    /// <summary>The link to this mod's Nexus page (state.json); failing that, the mod's own NexusModId.</summary>
    public NexusLink? NexusLink { get; set; }
    public int? NexusModId => NexusLink?.ModId ?? Manifest.NexusModId;

    DateTime? filesMade;
    /// <summary>
    /// When this copy of the mod was made: the newest write time of its files (not manifest.json, which the editor re-saves,
    /// nor Post\). Installs keep the archive's file times, so this is when the author made the version the user has.
    /// </summary>
    public DateTime FilesMade => filesMade ??= Directory.Exists(Folder)
        ? Directory.EnumerateFiles(Folder).Where(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
        : DateTime.MinValue;
    public List<StringReplacement> Strings { get; } = [];

    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? FolderName : Manifest.Name;

    /// <summary>The mod list's picture: the .dds of the first hero portrait the mod replaces (TextureName herohor…, in manifest
    /// order), else its first costume… icon, else its store image, else its first inventory_ image; null if it has none or the file is missing.</summary>
    public string? CostumeIconFile()
    {
        // The user's pick (Kurt: right-click → Card Picture), else the author's (manifest CardPicture), while the mod has that image.
        foreach (string? want in new[] { LocalCard, Manifest.CardPicture })
            if (ModPictures.Resolve(Folder, want) is string custom) return custom;
            else if (want != null && CardCandidates().FirstOrDefault(c => c.Texture.Equals(want, StringComparison.OrdinalIgnoreCase)) is { File: not null } pick) return pick.File;
        // The first herohor… portrait (69 of Kurt's 80 mods); failing that costume…, then the store image (pets have no
        // portrait: Jeff (Pet)'s store_petoldlace), then inventory_ (items: Kurt).
        var all = Manifest.Replacements.Select(x => (x.TextureName, x.DdsFileName))
            .Concat(Manifest.Extra.Select(x => (x.TextureName, x.DdsFileName)))
            .Concat(Manifest.StoreReplacements.Select(x => (x.TextureName, x.DdsFileName))).ToList();
        foreach (string prefix in new[] { "herohor", "costume", "store", "inventory_" })
        {
            var r = all.FirstOrDefault(x => x.TextureName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true && x.DdsFileName != null);
            if (r.DdsFileName != null && File.Exists(Path.Combine(Folder, r.DdsFileName))) return Path.Combine(Folder, r.DdsFileName);
        }
        return null;
    }

    /// <summary>Everything this mod needs from its folder that isn't there.</summary>
    public IEnumerable<string> MissingFiles()
    {
        foreach (var r in Manifest.Replacements.Concat(Manifest.AchievementReplacements).Concat(Manifest.StoreReplacements))
            if (!File.Exists(Path.Combine(Folder, r.DdsFileName))) yield return r.DdsFileName;
        foreach (var r in Manifest.Extra)
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
        int tex = Manifest.Replacements.Count + Manifest.AchievementReplacements.Count + Manifest.StoreReplacements.Count + Manifest.Extra.Count();
        if (tex > 0) parts.Add($"textures {tex}");
        if (Strings.Count > 0) parts.Add($"strings {Strings.Count}");
        if (Manifest.AudioPacks.Count > 0) parts.Add($"sound packs {Manifest.AudioPacks.Count}");
        return string.Join(", ", parts);
    }
}
