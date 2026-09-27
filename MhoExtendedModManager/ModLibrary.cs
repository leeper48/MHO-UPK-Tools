using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// A mod library in MHModManager's layout: &lt;library&gt;\mods\&lt;folder&gt;\…, &lt;library&gt;\state.json. Read-only (phase 1).
/// </summary>
sealed class ModLibrary
{
    public string DataFolder { get; }
    public ModState State { get; }
    public List<Mod> Mods { get; } = [];

    ModLibrary(string dataFolder, ModState state) { DataFolder = dataFolder; State = state; }

    public static ModLibrary Load(string dataFolder)
    {
        var lib = new ModLibrary(dataFolder, ModState.Load(Path.Combine(dataFolder, "state.json")));
        string modsDir = Path.Combine(dataFolder, "mods");
        var mods = new List<Mod>();
        if (Directory.Exists(modsDir))
            foreach (string dir in Directory.GetDirectories(modsDir))
                mods.Add(LoadMod(dir));

        // Priority: position in ModOrder (top wins); mods not listed there go after, by name.
        var order = lib.State.ModOrder.Select((n, i) => (n, i)).GroupBy(x => x.n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var enabled = new HashSet<string>(lib.State.EnabledMods, StringComparer.OrdinalIgnoreCase);
        mods = mods.OrderBy(m => order.TryGetValue(m.FolderName, out int i) ? i : int.MaxValue).ThenBy(m => m.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < mods.Count; i++) { mods[i].Priority = i; mods[i].Enabled = enabled.Contains(mods[i].FolderName); }
        lib.Mods.AddRange(mods);
        return lib;
    }

    static Mod LoadMod(string dir)
    {
        string name = Path.GetFileName(dir), manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) return new Mod { Folder = dir, FolderName = name, LoadError = "no manifest.json" };
        Mod mod;
        try { mod = new Mod { Folder = dir, FolderName = name, Manifest = ModManifest.Load(manifest) }; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { return new Mod { Folder = dir, FolderName = name, LoadError = "manifest.json: " + ex.Message }; }
        foreach (string lang in mod.Manifest.Languages)
        {
            string f = Path.Combine(dir, lang + ".json");
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var file in doc.RootElement.EnumerateObject())
                    foreach (var entry in file.Value.EnumerateObject())
                        if (ulong.TryParse(entry.Name, out ulong id))
                            mod.Strings.Add(ParseString(lang, file.Name, id, entry.Value));
            }
            catch (JsonException) { }   // reported through MissingFiles/Check as unreadable below
        }
        return mod;
    }

    /// <summary>One entry of a mod's &lt;lang&gt;.json: String, FlagsProduced, Variants (FlagsConsumed, FlagsProduced, String).</summary>
    static StringReplacement ParseString(string lang, string file, ulong id, JsonElement v)
    {
        static ushort U16(JsonElement e, string n) => e.TryGetProperty(n, out var x) && x.TryGetUInt16(out ushort u) ? u : (ushort)0;
        static ulong U64(JsonElement e, string n) => e.TryGetProperty(n, out var x) && x.TryGetUInt64(out ulong u) ? u : 0;
        static string Str(JsonElement e) => e.TryGetProperty("String", out var s) ? s.GetString() ?? "" : "";
        List<StringFile.Variant>? variants = null;
        if (v.TryGetProperty("Variants", out var vs) && vs.ValueKind == JsonValueKind.Array && vs.GetArrayLength() > 0)
            variants = vs.EnumerateArray().Select(x => new StringFile.Variant(U64(x, "FlagsConsumed"), U16(x, "FlagsProduced"), Str(x))).ToList();
        return new StringReplacement(lang, file, id, Str(v), U16(v, "FlagsProduced"), variants);
    }

    /// <summary>
    /// What each mod changes, as keys shared between mods: package:&lt;file&gt;, icon|achievement|store:&lt;texture&gt;,
    /// string:&lt;lang&gt;/&lt;file&gt;/&lt;id&gt;. Sound packs add new events under their own names, so they claim nothing.
    /// </summary>
    public static IEnumerable<string> Claims(Mod m)
    {
        foreach (string f in m.Manifest.UpkReplacements) yield return "package:" + f.ToLowerInvariant();
        foreach (var r in m.Manifest.Replacements) yield return "icon:" + r.TextureName.ToLowerInvariant();
        foreach (var r in m.Manifest.AchievementReplacements) yield return "achievement:" + r.TextureName.ToLowerInvariant();
        foreach (var r in m.Manifest.StoreReplacements) yield return "store:" + r.TextureName.ToLowerInvariant();
        foreach (var s in m.Strings) yield return $"string:{s.Language}/{s.File.ToLowerInvariant()}/{s.Id}";
    }

    /// <summary>Claims made by more than one enabled mod, with the mods in priority order (the first one wins).</summary>
    public List<(string Claim, List<Mod> Mods)> Conflicts()
    {
        var byClaim = new Dictionary<string, List<Mod>>();
        foreach (var m in Mods.Where(m => m.Enabled))
            foreach (string c in Claims(m).Distinct())
                (byClaim.TryGetValue(c, out var l) ? l : byClaim[c] = []).Add(m);
        return byClaim.Where(kv => kv.Value.Count > 1).Select(kv => (kv.Key, kv.Value.OrderBy(m => m.Priority).ToList())).OrderBy(x => x.Key).ToList();
    }

    /// <summary>Writes state.json in MHModManager's format: enabled mods, then the full order (top = highest priority).</summary>
    public void SaveState()
    {
        State.ModOrder = Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).ToList();
        State.EnabledMods = Mods.Where(m => m.Enabled).OrderBy(m => m.Priority).Select(m => m.FolderName).ToList();
        string path = Path.Combine(DataFolder, "state.json"), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, ModManifest.Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Moves a mod one place up (-1) or down (+1) in the priority order.</summary>
    public void Move(Mod m, int delta)
    {
        int to = m.Priority + delta;
        if (to < 0 || to >= Mods.Count) return;
        var other = Mods.First(x => x.Priority == to);
        (other.Priority, m.Priority) = (m.Priority, to);
        Mods.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    public Mod? Find(string name) =>
        Mods.FirstOrDefault(m => m.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        Mods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The enabled mod whose copy of each package should be live (highest priority).</summary>
    public Dictionary<string, Mod> PackageWinners()
    {
        var win = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mods.Where(m => m.Enabled).OrderBy(m => m.Priority))
            foreach (string f in m.Manifest.UpkReplacements)
                win.TryAdd(f, m);
        return win;
    }
}
