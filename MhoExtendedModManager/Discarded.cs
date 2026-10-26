using System.Text.Json;
using System.Text.Json.Serialization;

namespace MhoExtendedModManager;

/// <summary>
/// Discarded mod copies (Kurt, 2026-10-26: instead of the Recycle Bin, so a snapshot restore can bring files back at once).
/// A mod folder that's replaced (an edit, Make Unique, Fix Mirror Tables, an update, a bundle) or removed is moved here, not
/// recycled: data\library\discarded\&lt;time&gt;_&lt;folder&gt;\ (the files as they were, moved: same drive, instant) and
/// &lt;same&gt;.json (an <see cref="Entry"/>: the mod's folder name, name, version and <see cref="Snapshots.Fingerprint"/>).
/// <see cref="Snapshots.PlanFiles"/> finds a snapshot's copy of a mod here by folder + fingerprint.
///
/// Cleaned at a normal start after <see cref="Settings.DiscardDays"/> days (0: only by hand, Settings → Snapshots); copies a
/// saved snapshot would bring back are only deleted after a warning (<see cref="Needed"/>).
/// </summary>
static class Discarded
{
    public sealed class Entry
    {
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Version { get; set; }
        public string Fingerprint { get; set; } = "";
        public int Files { get; set; }
        public long Bytes { get; set; }
        public DateTime Time { get; set; }
        public string Reason { get; set; } = "";
        /// <summary>Kept past the cleanup by the user's answer until then.</summary>
        public DateTime? KeepUntil { get; set; }
        [JsonIgnore] public string Dir { get; set; } = "";
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root(string libraryData) => Path.Combine(libraryData, "discarded");

    /// <summary>
    /// Moves <paramref name="folder"/> (the files of the mod <paramref name="modFolder"/>, maybe under a temporary name) into
    /// the discarded copies. If it can't be moved (a file in use), it goes to the Recycle Bin as before.
    /// </summary>
    public static void Keep(string libraryData, string folder, string modFolder, string reason)
    {
        if (!Directory.Exists(folder)) return;
        var (fp, files, bytes) = Snapshots.Fingerprint(folder);
        string name = modFolder; string? version = null;
        try { var man = ModManifest.Load(Path.Combine(folder, "manifest.json")); if (!string.IsNullOrWhiteSpace(man.Name)) name = man.Name; version = man.Version; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { }
        try
        {
            string root = Root(libraryData);
            Directory.CreateDirectory(root);
            string stem = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{modFolder}"), dir = stem;
            for (int i = 2; Directory.Exists(dir) || File.Exists(dir + ".json"); i++) dir = $"{stem}_{i}";
            Directory.Move(folder, dir);
            var e = new Entry { Folder = modFolder, Name = name, Version = version, Fingerprint = fp, Files = files, Bytes = bytes, Time = DateTime.UtcNow, Reason = reason };
            File.WriteAllText(dir + ".json", JsonSerializer.Serialize(e, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(folder))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(folder, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }

    /// <summary>Every discarded copy, newest first.</summary>
    public static List<Entry> List(string libraryData)
    {
        var list = new List<Entry>();
        string root = Root(libraryData);
        if (!Directory.Exists(root)) return list;
        foreach (string f in Directory.EnumerateFiles(root, "*.json"))
            try
            {
                string dir = f[..^5];
                if (!Directory.Exists(dir)) continue;
                if (JsonSerializer.Deserialize<Entry>(File.ReadAllText(f)) is { } e) { e.Dir = dir; list.Add(e); }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return [.. list.OrderByDescending(e => e.Time)];
    }

    /// <summary>The copy of the mod <paramref name="folder"/> with exactly these files, if one was kept.</summary>
    public static Entry? Find(IEnumerable<Entry> entries, string folder, string fingerprint) => fingerprint.Length == 0 ? null
        : entries.FirstOrDefault(e => e.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) && e.Fingerprint == fingerprint);

    /// <summary>Moves a copy back into the library as the mod <see cref="Entry.Folder"/> (that folder must be free).</summary>
    public static void PutBack(string libraryData, Entry e)
    {
        Directory.Move(e.Dir, Path.Combine(libraryData, "mods", e.Folder));
        try { File.Delete(e.Dir + ".json"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes copies for good. Returns the bytes freed.</summary>
    public static long Delete(IEnumerable<Entry> entries)
    {
        long freed = 0;
        foreach (var e in entries)
            try
            {
                if (Directory.Exists(e.Dir)) Directory.Delete(e.Dir, true);
                File.Delete(e.Dir + ".json");
                freed += e.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return freed;
    }

    /// <summary>The copies past <paramref name="days"/> days (and past a Keep the user chose); none with 0 days.</summary>
    public static List<Entry> Due(string libraryData, int days)
    {
        if (days <= 0) return [];
        var now = DateTime.UtcNow;
        return List(libraryData).Where(e => e.Time < now.AddDays(-days) && (e.KeepUntil == null || e.KeepUntil < now)).ToList();
    }

    /// <summary>Of <paramref name="entries"/>, the ones a saved snapshot would bring back (its copy of that mod, which the
    /// library no longer has), each with those snapshots.</summary>
    public static Dictionary<Entry, List<Snapshots.Snapshot>> Needed(ModLibrary lib, IEnumerable<Entry> entries, List<Snapshots.Snapshot> snaps)
    {
        var have = lib.Mods.ToDictionary(m => m.FolderName, m => Snapshots.Fingerprint(m.Folder).Hash, StringComparer.OrdinalIgnoreCase);
        var needed = new Dictionary<Entry, List<Snapshots.Snapshot>>();
        foreach (var e in entries)
        {
            if (have.TryGetValue(e.Folder, out var fp) && fp == e.Fingerprint) continue;
            var by = snaps.Where(s => s.Mods.Any(m => m.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase) && m.Fingerprint == e.Fingerprint)).ToList();
            if (by.Count > 0) needed[e] = by;
        }
        return needed;
    }

    /// <summary>Kept past the cleanup for another <paramref name="days"/> days (the user's answer to the warning).</summary>
    public static void Postpone(IEnumerable<Entry> entries, int days)
    {
        foreach (var e in entries)
            try { e.KeepUntil = DateTime.UtcNow.AddDays(Math.Max(1, days)); File.WriteAllText(e.Dir + ".json", JsonSerializer.Serialize(e, Json)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The warning text for deleting copies snapshots need (empty when none is needed).</summary>
    public static string Warning(Dictionary<Entry, List<Snapshots.Snapshot>> needed)
    {
        if (needed.Count == 0) return "";
        var snaps = needed.Values.SelectMany(x => x).Distinct().OrderBy(s => s.Taken).ToList();
        return $"SNAPSHOTS THAT COULDN'T BRING THESE BACK ({snaps.Count})\n"
            + $"  • {Snapshots.Label(snaps[0])}" + (snaps.Count > 1 ? $" to {Snapshots.Label(snaps[^1])}" : "") + "\n\n"
            + $"MODS WHOSE OLDER FILES GO ({needed.Count})\n"
            + string.Join("\n", needed.Keys.Take(12).Select(e => $"  • {e.Name}{(e.Version is { Length: > 0 } v ? " v" + v.TrimStart('v', 'V') : "")}: {e.Reason} ({e.Time.ToLocalTime():yyyy-MM-dd})"))
            + (needed.Count > 12 ? $"\n  • And {needed.Count - 12} more" : "")
            + "\n\nRestoring those snapshots would still put their mods' state back, but these mods would keep the files they have now.";
    }
}
