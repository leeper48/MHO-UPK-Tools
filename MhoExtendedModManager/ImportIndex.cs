using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Which objects the game's packages import from one another, by path (a user, 2026-10-14: Nova's recolored Pulsar showed
/// black planes in game). The Richard Rider versions of Nova's Pulsar effects (UC__MarvelEntity_Hotspot_NovaPulsarHotspot_RR_SF
/// and others) carry no materials of their own: they import mat_nova_pulsarhotspot_elec and the rest by path from the base
/// effect packages. The power recolor moved those materials to a group of their own, the imports found nothing, and the
/// effects drew black (in-game tests: nothing moved = fine, only textures moved = fine, materials moved = black). An object
/// another package imports must keep its path. Read only; built from every UC__ package's import table once per folder
/// version and kept in data\imported_paths.json.
/// </summary>
static class ImportIndex
{
    static readonly string[] Classes = ["material", "materialinstanceconstant", "materialinstancetimevarying", "texture2d", "particlesystem", "staticmesh", "skeletalmesh", "physicsasset", "animset"];

    sealed class Saved
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, List<string>> Paths { get; set; } = [];
    }

    static readonly object gate = new();
    static (string Folder, Dictionary<string, List<string>> Paths)? index;

    /// <summary>The full path of an import (its outer chain of imports), as an export's path reads.</summary>
    public static string PathOf(Package p, ImportEntry im)
    {
        var parts = new List<string> { im.ObjectName };
        int outer = im.OuterIndex, guard = 0;
        while (outer != 0 && guard++ < 64)
        {
            if (outer < 0) { var o = p.Imports[-outer - 1]; parts.Add(o.ObjectName); outer = o.OuterIndex; }
            else { var o = p.Exports[outer - 1]; parts.Add(o.ObjectName); outer = o.OuterIndex; }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>"path|class" (lower case) → the game packages that import it. A minute or two cold, then from the cache.</summary>
    public static Dictionary<string, List<string>> Imported(string folder)
    {
        lock (gate)
        {
            if (index is { } ix && ix.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) return ix.Paths;
            var infos = new DirectoryInfo(folder).EnumerateFiles("UC__*_SF.upk")
                .Where(f => !f.Name.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
            string stamp = $"{Path.GetFullPath(folder).ToLowerInvariant()}|{infos.Count}|{infos.Sum(f => f.Length)}|{(infos.Count == 0 ? 0 : infos.Max(f => f.LastWriteTimeUtc.Ticks))}|v1";
            string cache = Path.Combine(Settings.Home, "imported_paths.json");
            try
            {
                if (File.Exists(cache) && System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(cache)) is { } sv && sv.Stamp == stamp)
                {
                    var loaded = new Dictionary<string, List<string>>(sv.Paths, StringComparer.OrdinalIgnoreCase);
                    index = (folder, loaded);
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(infos, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, fi =>
            {
                try
                {
                    var p = Package.Open(fi.FullName);
                    foreach (var im in p.Imports)
                    {
                        string c = im.ClassName.ToLowerInvariant();
                        if (!Classes.Contains(c)) continue;
                        seen.GetOrAdd($"{PathOf(p, im)}|{c}", _ => []).Add(fi.Name);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            });
            var paths = seen.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            index = (folder, paths);
            try { File.WriteAllText(cache, System.Text.Json.JsonSerializer.Serialize(new Saved { Stamp = stamp, Paths = paths })); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return paths;
        }
    }

    static (string Folder, Dictionary<string, string> Paths)? exported;

    /// <summary>
    /// "path|class" (lower case) → one game package that exports it, for textures and materials (2026-10-15: a dark power's
    /// additive effects take their textures from the shared effect packages, vfx_shared_textures and the like, which a power
    /// package imports). Built with every UC__ package's export table once per folder version, kept in
    /// data\exported_paths.json.
    /// </summary>
    public static Dictionary<string, string> Exported(string folder)
    {
        lock (gate)
        {
            if (exported is { } ex && ex.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) return ex.Paths;
            var infos = new DirectoryInfo(folder).EnumerateFiles("UC__*_SF.upk")
                .Where(f => !f.Name.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
            string stamp = $"{Path.GetFullPath(folder).ToLowerInvariant()}|{infos.Count}|{infos.Sum(f => f.Length)}|{(infos.Count == 0 ? 0 : infos.Max(f => f.LastWriteTimeUtc.Ticks))}|e1";
            string cache = Path.Combine(Settings.Home, "exported_paths.json");
            try
            {
                if (File.Exists(cache) && System.Text.Json.JsonSerializer.Deserialize<SavedOne>(File.ReadAllText(cache)) is { } sv && sv.Stamp == stamp)
                {
                    var loaded = new Dictionary<string, string>(sv.Paths, StringComparer.OrdinalIgnoreCase);
                    exported = (folder, loaded);
                    return loaded;
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            string[] wanted = ["texture2d", "material", "materialinstanceconstant"];
            var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Parallel.ForEach(infos.OrderBy(f => f.Length), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, fi =>
            {
                try
                {
                    var p = Package.Open(fi.FullName);
                    foreach (var e in p.Exports)
                    {
                        string c = p.ClassOf(e).ToLowerInvariant();
                        if (wanted.Contains(c)) seen.TryAdd($"{p.PathOf(e)}|{c}", fi.Name);
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or PackageFormatException) { }
            });
            var paths = new Dictionary<string, string>(seen, StringComparer.OrdinalIgnoreCase);
            exported = (folder, paths);
            try { File.WriteAllText(cache, System.Text.Json.JsonSerializer.Serialize(new SavedOne { Stamp = stamp, Paths = paths })); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return paths;
        }
    }

    sealed class SavedOne
    {
        public string Stamp { get; set; } = "";
        public Dictionary<string, string> Paths { get; set; } = [];
    }

    /// <summary>The game packages other than <paramref name="file"/> that import this export's path (empty: none).</summary>
    public static List<string> ImportersOf(string folder, string path, string cls, string file) =>
        Imported(folder).TryGetValue($"{path}|{cls.ToLowerInvariant()}", out var list) ? [.. list.Where(f => !f.Equals(file, StringComparison.OrdinalIgnoreCase))] : [];
}
