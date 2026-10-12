using System.Buffers.Binary;
using System.Text.Json;
using MhoPackageModifier;

namespace MhoExtendedModManager.Model;

/// <summary>
/// The game's ConditionFX material swaps that skip material slots by number (Kurt, 2026-10-12, from Krisan's Material Editor:
/// a model with another slot layout breaks them). 145 swaps in 74 condition packages; about 40 list slots to keep
/// (MeshMaterialIndiciesToExclude) for a costume (EntityRequired: its class name): Hulk's revive visual (one per costume:
/// the skin material on every slot but the pants, the pants material on the pants slot), Doctor Doom's force field (body /
/// cape), Vision's stealth, Super Skrull's rock arms, Rogue's stolen Colossus skin and heals. A build onto such a costume
/// gives its materials slots in its own order, so the Model tab shows the swaps and lets the order be changed. Read once
/// from the condition packages (the clean game files) and kept in data\model\condition_swaps.json.
/// </summary>
static class ConditionSwaps
{
    public sealed record Swap(string Package, string Name, string Replacement, List<int> Excluded, List<string> Required, List<string> Excludes)
    {
        /// <summary>The replacement material's export (1-based) in its package; 0 when it's an import.</summary>
        public int ReplacementRef { get; init; }
        /// <summary>Whether it applies to a character of class <paramref name="cls"/> (required, or none required and not excluded).</summary>
        public bool AppliesTo(string cls) => Required.Count > 0 ? Required.Contains(cls, StringComparer.OrdinalIgnoreCase) : !Excludes.Contains(cls, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every ConditionFXMaterialSwap in a package (those for all slots too).</summary>
    public static List<Swap> Read(Package p, string file)
    {
        var list = new List<Swap>();
        for (int i = 0; i < p.Exports.Length; i++)
        {
            if (!p.ClassOf(p.Exports[i]).Equals("ConditionFXMaterialSwap", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] b = p.ReadExportBytes(p.Exports[i]).ToArray();
            var tags = TagWalker.Walk(p, b, 16);
            if (tags == null) continue;
            int r = tags.FirstOrDefault(t => t.Name.Equals("ReplacementMaterial", StringComparison.OrdinalIgnoreCase)) is { Size: 4 } rt ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(rt.ValueAt)) : 0;
            list.Add(new(file, p.Exports[i].ObjectName, r > 0 ? p.PathOf(p.Exports[r - 1]) : r < 0 ? p.RefName(r) : "",
                Ints(b, tags.FirstOrDefault(t => t.Name.Equals("MeshMaterialIndiciesToExclude", StringComparison.OrdinalIgnoreCase))),
                Names(p, b, tags.FirstOrDefault(t => t.Name.Equals("EntityRequired", StringComparison.OrdinalIgnoreCase))),
                Names(p, b, tags.FirstOrDefault(t => t.Name.Equals("EntityExcluded", StringComparison.OrdinalIgnoreCase)))) { ReplacementRef = Math.Max(0, r) });
        }
        return list;
    }

    static readonly System.Text.RegularExpressions.Regex ConditionLine = new(@"= (\S+)\s+\[ConditionUnrealClass\]");

    /// <summary>The condition classes a power prototype applies (its ConditionUnrealClass fields: The Will of Doom →
    /// MarvelConditionEffect_ForceField_Player_DrDoom).</summary>
    public static List<string> ConditionsOf(Fx.GameData db, string power)
    {
        if (db.Find(power) is not { } e) return [];
        try { return db.Dump(db.Prototype(e.Id).Data, 200).Select(l => ConditionLine.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or KeyNotFoundException) { return []; }
    }

    sealed class Saved { public string Stamp { get; set; } = ""; public List<Swap> Swaps { get; set; } = []; }

    static readonly object gate = new();
    static (string Stamp, List<Swap> Swaps)? cache;

    /// <summary>Every slot-specific material swap in the game (call off the UI thread the first time: ~10 s).</summary>
    public static List<Swap> All(string folder)
    {
        lock (gate)
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("UC__MarvelConditionEffect_*_SF.upk")
                .Where(f => !f.Name.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
            string stamp = $"{Path.GetFullPath(folder).ToLowerInvariant()}|{files.Count}|{files.Sum(f => f.Length)}|v2";
            if (cache is { } c && c.Stamp == stamp) return c.Swaps;
            string file = Path.Combine(Settings.Home, "condition_swaps.json");
            try
            {
                if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is { } sv && sv.Stamp == stamp)
                { cache = (stamp, sv.Swaps); return sv.Swaps; }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            var found = new System.Collections.Concurrent.ConcurrentBag<Swap>();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, fi =>
            {
                try
                {
                    var p = Package.Open(fi.FullName);
                    foreach (var sw in Read(p, fi.Name)) if (sw.Excluded.Count > 0) found.Add(sw);   // every slot: the order doesn't matter
                }
                catch (Exception e) when (e is IOException or InvalidDataException or PackageFormatException) { }
            });
            var list = found.OrderBy(s => s.Package, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name).ToList();
            cache = (stamp, list);
            try { File.WriteAllText(file, JsonSerializer.Serialize(new Saved { Stamp = stamp, Swaps = list })); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return list;
        }
    }

    static List<int> Ints(byte[] b, TagWalker.Tag? t)
    {
        var list = new List<int>();
        if (t == null || t.Size < 4) return list;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
        for (int k = 0; k < n && 4 + 4 * k + 4 <= t.Size; k++) list.Add(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt + 4 + 4 * k)));
        return list;
    }

    static List<string> Names(Package p, byte[] b, TagWalker.Tag? t)
    {
        var list = new List<string>();
        if (t == null || t.Size < 4) return list;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
        for (int k = 0; k < n && 4 + 8 * k + 8 <= t.Size; k++)
        {
            int ni = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt + 4 + 8 * k));
            if (ni >= 0 && ni < p.Names.Length) list.Add(p.Names[ni]);
        }
        return list;
    }

    /// <summary>The swaps for a costume class (marvelplayer_hulk_christmas): those that require it.</summary>
    public static List<Swap> For(string folder, string costumeClass) =>
        All(folder).Where(s => s.Required.Contains(costumeClass, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>"UC__MarvelPlayer_Hulk_Christmas_SF.upk" → "marvelplayer_hulk_christmas".</summary>
    public static string ClassOf(string packageFile) => Path.GetFileNameWithoutExtension(packageFile) is var n && n.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) && n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)
        ? n[4..^3].ToLowerInvariant() : n.ToLowerInvariant();

    /// <summary>Plain words for one swap and a slot list ("slots 0, 1 keep their own material; the rest get hulk_christmaspants_mat_unhulk").</summary>
    public static string Describe(Swap s, IReadOnlyList<string> slots)
    {
        string kept = string.Join(", ", s.Excluded.Select(i => i < slots.Count ? $"{i} ({slots[i]})" : $"{i} (none)"));
        string package = s.Package.Replace("UC__MarvelConditionEffect_", "").Replace("_SF.upk", "");
        return $"{package}: slot(s) {kept} keep their own material; every other slot gets {s.Replacement.Split('.').Last()}";
    }
}
