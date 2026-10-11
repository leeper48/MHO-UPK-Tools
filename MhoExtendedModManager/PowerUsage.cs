using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// Who uses a power package (Kurt, 2026-10-11: his Captain Carter over Taskmaster replaced UC__PowerTaskmaster_ShieldToss_SF,
/// and the Taskmaster boss threw her shield: that package's class, PowerTaskmaster_ShieldToss, belongs to one power only,
/// Powers\EnemyPowers\Boss\Taskmaster\TaskmasterShieldToss). Every power prototype's Unreal classes (PowerUnrealClass, and
/// PowerArt in its overrides: a costume's own look, ShieldBounce_Udon) from the game data, so a mod's power package whose
/// class no hero power uses, but an enemy's does, is flagged: it changes enemies only. Built once per Calligraphy.sip and
/// kept in data\power_users.json.
/// </summary>
static class PowerUsage
{
    public sealed record Use(string File, List<string> Users);

    sealed class Cache
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, List<string>> Map { get; set; } = [];
    }

    static readonly object gate = new();
    static (string Stamp, Dictionary<string, List<string>> Map)? index;
    static readonly System.Text.RegularExpressions.Regex ClassLine = new(@"= (\S+)\s+\[PowerUnrealClass\]");

    /// <summary>Power class (lower case) → the power prototypes using it. Slow the first time (every power prototype is read):
    /// call off the UI thread.</summary>
    public static Dictionary<string, List<string>> Index(string gameRoot)
    {
        string sip = Path.Combine(gameRoot, "Data", "Game", "Calligraphy.sip");
        var fi = new FileInfo(sip);
        if (!fi.Exists) return [];
        string stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        lock (gate)
        {
            if (index is { } ix && ix.Stamp == stamp) return ix.Map;
            string file = Path.Combine(Settings.Home, "power_users.json");
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(file)) is { } c && c.Stamp == stamp)
                {
                    var m = new Dictionary<string, List<string>>(c.Map, StringComparer.OrdinalIgnoreCase);
                    index = (stamp, m);
                    return m;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            var db = new Fx.GameData(Fx.SipArchive.Load(sip));
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in db.Prototypes.Values)
            {
                if (!e.Path.StartsWith(@"Powers\", StringComparison.OrdinalIgnoreCase)) continue;
                IEnumerable<string> lines;
                try { lines = db.Dump(db.Prototype(e.Id).Data, 200).ToList(); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { continue; }
                foreach (string l in lines)
                    if (ClassLine.Match(l) is { Success: true } mt)
                    {
                        string cls = mt.Groups[1].Value.ToLowerInvariant();
                        if (!map.TryGetValue(cls, out var list)) map[cls] = list = [];
                        if (!list.Contains(e.Path, StringComparer.OrdinalIgnoreCase)) list.Add(e.Path);
                    }
            }
            index = (stamp, map);
            try { File.WriteAllText(file, JsonSerializer.Serialize(new Cache { Stamp = stamp, Map = map })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return map;
        }
    }

    /// <summary>An enemy's or boss's power (Powers\EnemyPowers\, 3,119 of them). Every other power folder is something players
    /// use: heroes, team-ups, items, stolen powers (Rogue), PvP, emotes, missions.</summary>
    static bool Enemy(string power) => power.StartsWith(@"Powers\EnemyPowers\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The mod's power packages (UC__Power…_SF) whose class only enemy powers use.</summary>
    public static List<Use> EnemyOnly(Mod m, Dictionary<string, List<string>> map)
    {
        var found = new List<Use>();
        foreach (string f in m.Manifest.UpkReplacements)
        {
            if (!f.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase) || !f.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase)) continue;
            string cls = f[4..^7];
            if (!map.TryGetValue(cls, out var users) || users.Count == 0 || !users.All(Enemy)) continue;
            found.Add(new(f, users));
        }
        return found;
    }

    static readonly Dictionary<string, List<Use>> perMod = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Works out a mod's enemy-only packages (the index must be ready: call off the UI thread).</summary>
    public static List<Use> Check(Mod m, string gameRoot)
    {
        var uses = EnemyOnly(m, Index(gameRoot));
        lock (perMod) perMod[m.Folder] = uses;
        return uses;
    }

    /// <summary>From the cache only (empty until <see cref="Check"/> ran for the mod).</summary>
    public static List<Use> Cached(Mod m) { lock (perMod) return perMod.TryGetValue(m.Folder, out var u) ? u : []; }

    public static string? Reason(Mod m)
    {
        var uses = Cached(m);
        if (uses.Count == 0) return null;
        static string Short(string p) => Path.GetFileNameWithoutExtension(p);
        return "Enemy power: " + string.Join("; ", uses.Select(u => $"{u.File} is used only by {string.Join(", ", u.Users.Take(3).Select(Short))}{(u.Users.Count > 3 ? " …" : "")}"))
            + ". No hero power uses it, so replacing it changes how those enemies look, not this hero.";
    }
}

static class ModPackages
{
    /// <summary>
    /// The mod without <paramref name="files"/> (Kurt, 2026-10-11: enemy-only power packages): saved in place, or with
    /// <paramref name="copyName"/> as a new mod (the original kept as it is, to switch back). Returns the saved folder, or
    /// null with <paramref name="error"/>.
    /// </summary>
    public static string? Remove(ModLibrary lib, Mod m, IReadOnlyCollection<string> files, string? copyName, out string? error)
    {
        var d = ModDraft.From(m);
        int before = d.Packages.Count;
        d.Packages.RemoveAll(p => files.Contains(p.File, StringComparer.OrdinalIgnoreCase));
        if (d.Packages.Count == before) { error = "none of those packages is in the mod"; return null; }
        if (copyName != null) d.Name = copyName;
        return ModWriter.Save(lib, d, copyName != null ? null : m, out error);
    }
}
