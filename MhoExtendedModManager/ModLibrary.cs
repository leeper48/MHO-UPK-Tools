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
        var top = new HashSet<string>(lib.State.LockedTop ?? [], StringComparer.OrdinalIgnoreCase);
        var bottom = new HashSet<string>(lib.State.LockedBottom ?? [], StringComparer.OrdinalIgnoreCase);
        var tags = new Dictionary<string, List<string>>(lib.State.Tags ?? [], StringComparer.OrdinalIgnoreCase);
        var hidden = new Dictionary<string, List<string>>(lib.State.HiddenTags ?? [], StringComparer.OrdinalIgnoreCase);
        var notes = new Dictionary<string, string>(lib.State.Notes ?? [], StringComparer.OrdinalIgnoreCase);
        var nexus = new Dictionary<string, NexusLink>(lib.State.NexusLinks ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            m.Enabled = enabled.Contains(m.FolderName);
            m.UserTags = tags.TryGetValue(m.FolderName, out var t) ? t.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
            m.HiddenTags = hidden.TryGetValue(m.FolderName, out var h) ? h.ToList() : [];
            m.LocalNote = notes.TryGetValue(m.FolderName, out var n) ? n : null;
            m.NexusLink = nexus.TryGetValue(m.FolderName, out var nl) ? nl : null;
            m.Lock = top.Contains(m.FolderName) ? ModLock.Top : bottom.Contains(m.FolderName) ? ModLock.Bottom : ModLock.None;
        }
        lib.Mods.AddRange(mods);
        lib.Normalize();
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
        foreach (var r in m.Manifest.Extra) yield return $"extra:{r.Package.ToLowerInvariant()}/{r.TextureName.ToLowerInvariant()}";
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
        Normalize();
        State.ModOrder = Mods.OrderBy(m => m.Priority).Select(m => m.FolderName).ToList();
        State.EnabledMods = Mods.Where(m => m.Enabled).OrderBy(m => m.Priority).Select(m => m.FolderName).ToList();
        List<string>? Locked(ModLock l) { var x = Mods.Where(m => m.Lock == l).OrderBy(m => m.Priority).Select(m => m.FolderName).ToList(); return x.Count > 0 ? x : null; }
        State.LockedTop = Locked(ModLock.Top);
        State.LockedBottom = Locked(ModLock.Bottom);
        var tagged = Mods.Where(m => m.UserTags.Count > 0).OrderBy(m => m.Priority).ToList();
        State.Tags = tagged.Count > 0 ? tagged.ToDictionary(m => m.FolderName, m => m.UserTags.ToList()) : null;
        var hid = Mods.Where(m => m.HiddenTags.Count > 0).OrderBy(m => m.Priority).ToList();
        State.HiddenTags = hid.Count > 0 ? hid.ToDictionary(m => m.FolderName, m => m.HiddenTags.ToList()) : null;
        var noted = Mods.Where(m => m.LocalNote != null).OrderBy(m => m.Priority).ToList();
        State.Notes = noted.Count > 0 ? noted.ToDictionary(m => m.FolderName, m => m.LocalNote!) : null;
        var linked = Mods.Where(m => m.NexusLink != null).OrderBy(m => m.Priority).ToList();
        State.NexusLinks = linked.Count > 0 ? linked.ToDictionary(m => m.FolderName, m => m.NexusLink!) : null;
        string path = Path.Combine(DataFolder, "state.json"), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, ModManifest.Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Locked mods first: top-locked, then the rest, then bottom-locked, each in its current order; priorities renumbered.
    /// Whatever put a mod elsewhere (a new mod written at the top, a capture appended at the bottom, a mod unlocked from
    /// the middle of a locked run) lands at the edge of the unlocked range, so a lock always holds.
    /// </summary>
    public void Normalize()
    {
        var sorted = Mods.OrderBy(m => m.Lock == ModLock.Top ? 0 : m.Lock == ModLock.None ? 1 : 2).ThenBy(m => m.Priority).ToList();
        Mods.Clear(); Mods.AddRange(sorted);
        for (int i = 0; i < Mods.Count; i++) Mods[i].Priority = i;
    }

    int TopLocked => Mods.Count(m => m.Lock == ModLock.Top);
    int BottomLocked => Mods.Count(m => m.Lock == ModLock.Bottom);

    /// <summary>The lock a click on this mod's padlock would set: Top if it's at the top or right under the top-locked run,
    /// Bottom likewise at the bottom; None if it's in the middle (or already locked).</summary>
    public ModLock CanLock(Mod m) =>
        m.Lock != ModLock.None ? ModLock.None
        : m.Priority == TopLocked ? ModLock.Top
        : m.Priority == Mods.Count - 1 - BottomLocked ? ModLock.Bottom
        : ModLock.None;

    /// <summary>Locks the mod where it can be locked, or unlocks a locked one. False if it can't be locked where it is.</summary>
    public bool ToggleLock(Mod m)
    {
        if (m.Lock != ModLock.None) m.Lock = ModLock.None;
        else if (CanLock(m) is var l && l != ModLock.None) m.Lock = l;
        else return false;
        Normalize();
        return true;
    }

    /// <summary>Moves a mod one place up (-1) or down (+1) within the unlocked range. False for a locked mod.</summary>
    public bool Move(Mod m, int delta)
    {
        if (m.Lock != ModLock.None) return false;
        int to = Math.Clamp(m.Priority + delta, TopLocked, Mods.Count - 1 - BottomLocked);
        if (to == m.Priority) return true;
        var other = Mods.First(x => x.Priority == to);
        (other.Priority, m.Priority) = (m.Priority, to);
        Mods.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return true;
    }

    /// <summary>Moves a mod to the top (-1) or bottom (+1) of the unlocked range. False for a locked mod.</summary>
    public bool MoveToEnd(Mod m, int direction)
    {
        if (m.Lock != ModLock.None) return false;
        Mods.Remove(m);
        Mods.Insert(direction < 0 ? TopLocked : Mods.Count - BottomLocked, m);
        for (int i = 0; i < Mods.Count; i++) Mods[i].Priority = i;
        return true;
    }

    /// <summary>Every tag in use, sorted.</summary>
    public List<string> AllTags() => Mods.SelectMany(m => m.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A tag as typed, cleaned up, and spelled like an existing tag that differs only in case.</summary>
    public string? CleanTag(string? text)
    {
        string t = string.Join(' ', (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimStart('#');
        if (t.Length == 0) return null;
        if (t.Length > 30) t = t[..30];
        return AllTags().FirstOrDefault(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? t;
    }

    public static bool HasTag(Mod m, string tag) => m.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Puts a tag on a mod: un-hides an automatic / mod tag, else adds it to the user's tags.</summary>
    public static void AddTag(Mod m, string tag)
    {
        m.HiddenTags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (!HasTag(m, tag)) m.UserTags.Add(tag);
    }

    /// <summary>Takes a tag off a mod: removes the user's tag, and hides an automatic / mod tag on this PC.</summary>
    public static void RemoveTag(Mod m, string tag)
    {
        m.UserTags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (HasTag(m, tag)) m.HiddenTags.Add(tag);
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
