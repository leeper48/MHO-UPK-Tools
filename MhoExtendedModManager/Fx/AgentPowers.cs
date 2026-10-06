namespace MhoExtendedModManager.Fx;

/// <summary>
/// An NPC's or enemy's own powers (Kurt, 2026-10-05: the Sinister clones' attacks in the Powers tab, like a hero's). The game
/// data names a character by its Unreal class (UnrealClass MarvelAgent_CloneWolverine on Entity\Characters\Mobs\
/// MrSinisterBattle\CloneWolverine and the event versions); its powers are named by that prototype and what it points at (its
/// Brain, the AI profile listing the attacks it uses, equipped passives …): the prototypes under Powers\ reached that way.
/// Each is a power like a hero's: its class and what it sets off (PowerClosure) are its packages.
/// </summary>
static class AgentPowers
{
    static Dictionary<string, List<string>>? byClass;
    static readonly object gate = new();
    static readonly Dictionary<string, List<string>> powersCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character prototypes (Entity\…) whose UnrealClass is <paramref name="cls"/> (case doesn't matter).</summary>
    public static List<string> PrototypesOf(GameData db, string cls)
    {
        lock (gate)
        {
            if (byClass == null)
            {
                var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, e) in db.Prototypes)
                {
                    if (!e.Path.StartsWith(@"Entity\", StringComparison.OrdinalIgnoreCase)) continue;
                    Calligraphy.Data data;
                    try { data = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
                    foreach (var g in data.Groups)
                        foreach (var f in g.Simple)
                            if (f.Type == 'A' && db.FieldName(g.Blueprint, f.Id) == "UnrealClass" && db.Assets.TryGetValue(f.Value.Raw, out var a))
                            {
                                if (!d.TryGetValue(a.Asset.Name, out var list)) d[a.Asset.Name] = list = [];
                                list.Add(e.Path);
                            }
                }
                byClass = d;
            }
            return byClass.TryGetValue(cls, out var hit) ? hit : [];
        }
    }

    /// <summary>The power prototypes the character's prototypes name, directly or through what they point at (AI profiles,
    /// behaviors, other character data), up to three steps away; not blueprints, defaults or talents.</summary>
    public static List<string> PowersOf(GameData db, string cls)
    {
        lock (powersCache) if (powersCache.TryGetValue(cls, out var hit)) return hit;
        var found = new List<string>();
        var seen = new HashSet<ulong>();
        var queue = new Queue<(ulong Id, int Depth)>();
        foreach (string p in PrototypesOf(db, cls)) if (db.Find(p) is { } e && seen.Add(e.Id)) queue.Enqueue((e.Id, 0));
        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            Calligraphy.Data data;
            try { data = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
            foreach (ulong r in PowerClosure.Refs(db, data))
            {
                if (r == 0 || !seen.Add(r) || !db.Prototypes.TryGetValue(r, out var re)) continue;
                string path = re.Path;
                if (path.Contains(@"\Blueprints\", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase)
                    || path.Contains(@"\Talents\", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.StartsWith(@"Powers\", StringComparison.OrdinalIgnoreCase)) { found.Add(path); continue; }
                if (depth < 3 && (path.StartsWith(@"AI\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"Entity\Characters\", StringComparison.OrdinalIgnoreCase))
                    && !path.StartsWith(@"Entity\Characters\Avatars", StringComparison.OrdinalIgnoreCase))
                    queue.Enqueue((r, depth + 1));
            }
        }
        var list = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (powersCache) powersCache[cls] = list;
        return list;
    }

    /// <summary>The character's powers and everything they set off (prototype paths with '/'): what "its own" means when a
    /// power's packages are weighed (PowerRecolor.PackagesOf).</summary>
    public static HashSet<string> Mine(GameData db, string cls)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in PowersOf(db, cls))
        {
            set.Add(p.Replace('\\', '/'));
            foreach (var a in PowerClosure.Of(db, p)) set.Add(a.Prototype.Replace('\\', '/'));
        }
        return set;
    }

    /// <summary>The character's powers for the editor's Powers tab: name (the game's, else from the prototype), icon, the
    /// animations its own class plays; only those with something of their own to recolor. <paramref name="label"/> starts
    /// each name ("Clone Wolverine").</summary>
    public static List<PowerList.Power> List(GameData db, string cls, string cooked, string label)
    {
        string gameRoot = Path.GetFullPath(Path.Combine(cooked, "..", "..", ".."));
        var strings = PowerList.English(gameRoot);
        var result = new List<PowerList.Power>();
        foreach (string proto in PowersOf(db, cls))
        {
            if (PowerRecolor.PackagesOfAgent(db, proto, cls, cooked).Count == 0) continue;
            bool hasName = PowerList.Field(db, proto, "DisplayName", 'S') is { Found: true } dn && strings.TryGetValue(dn.Raw, out var t) && t.Length > 0;
            string name = hasName ? strings[PowerList.Field(db, proto, "DisplayName", 'S').Raw]
                : System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(proto), "(?<=[a-z])(?=[A-Z])", " ");
            name = System.Text.RegularExpressions.Regex.Replace(name, "#/?powerkeyword#", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string? icon = PowerList.Field(db, proto, "IconPath", 'A') is { Found: true } ip && db.Assets.TryGetValue(ip.Raw, out var a) ? a.Asset.Name : null;
            var anims = PowerEffects.UnrealClassOf(db, proto) is string pc && File.Exists(Path.Combine(cooked, $"UC__{pc}_SF.upk"))
                ? PowerIndex.AnimationsIn(StockFiles.For(cooked, $"UC__{pc}_SF.upk")) : [];
            result.Add(new PowerList.Power(proto, $"{label}: {name}", icon, anims) { HasName = true });
        }
        return [.. result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
