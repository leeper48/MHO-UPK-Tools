using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// Snapshots and bundles (Kurt, 2026-10-26: users hit an Array.h:575 crash that came and went; a record of what changed makes
/// it findable, and a way back to a point in time).
///
/// A snapshot is the library's state at one moment, a few tens of KB: every mod's on/off, priority, lock, tags and note (the
/// whole state.json), and per mod its name, version, install date, a content fingerprint (its files' names, sizes and dates:
/// no file is read), Make Unique / Keep Shared Paths, the OVERLAP and CRASH RISK pills as last checked, and its history
/// (installs, updates, Crash Risk fixes: <see cref="ModHistory"/>); plus the game files this app last wrote (the ledger).
/// Taken at every launch (kept), before every Apply (the last 30 kept) and by hand (kept), in data\snapshots\*.json.gz.
///
/// <see cref="Compare"/> is the audit report between two snapshots (or one and now). <see cref="Restore"/> puts back the
/// state of the mods still installed (on/off, order, locks, tags, notes); <see cref="PlanFiles"/> / <see cref="RestoreFiles"/>
/// put mods' files back from the discarded copies (<see cref="Discarded"/>: edits, Make Unique, Fix Mirror Tables, updates,
/// removals since), else undo Make Unique or set Keep Shared Paths back. Older files no copy was kept of: a bundle.
///
/// A bundle is a .zip of mods (all, or the turned-on ones) with a snapshot: the whole set, to go back to a clean set or move
/// to another PC. Restoring one installs the mods the library lacks (and, when asked, replaces the ones that differ), then
/// puts the bundle's state back.
/// </summary>
static class Snapshots
{
    public sealed class ModInfo
    {
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Author { get; set; }
        public string? Version { get; set; }
        public bool Enabled { get; set; }
        public int Priority { get; set; }
        public DateTime? Installed { get; set; }
        public string Fingerprint { get; set; } = "";
        public int Files { get; set; }
        public long Bytes { get; set; }
        public bool MadeUnique { get; set; }
        public bool KeepShared { get; set; }
        /// <summary>The pills as last checked (null: not checked yet when the snapshot was taken).</summary>
        public bool? Overlap { get; set; }
        public bool? CrashRisk { get; set; }
        public List<ModHistory.Entry>? History { get; set; }
    }

    public sealed class Snapshot
    {
        public DateTime Taken { get; set; }
        /// <summary>"launch", "apply" or "manual".</summary>
        public string Kind { get; set; } = "manual";
        public string Name { get; set; } = "";
        public string App { get; set; } = "";
        public string? GameRoot { get; set; }
        public ModState State { get; set; } = new();
        public List<ModInfo> Mods { get; set; } = [];
        public Dictionary<string, uint>? GameFiles { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string File { get; set; } = "";
    }

    public static string Folder => Path.Combine(Settings.Home, "snapshots");
    const int KeepApply = 30;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The library as it is now (pills from the last background check).</summary>
    public static Snapshot Now(ModLibrary lib, GameState? game, string kind = "manual", string name = "")
    {
        var history = ModHistory.Load(lib.DataFolder);
        var snap = new Snapshot
        {
            Taken = DateTime.UtcNow, Kind = kind, Name = name, App = Program.Version, GameRoot = game?.Root,
            State = JsonSerializer.Deserialize<ModState>(JsonSerializer.Serialize(lib.State)) ?? new ModState(),
        };
        foreach (var m in lib.Mods.OrderBy(m => m.Priority))
        {
            var (fp, files, bytes) = Fingerprint(m.Folder);
            history.TryGetValue(m.FolderName, out var h);
            snap.Mods.Add(new ModInfo
            {
                Folder = m.FolderName, Name = m.Name, Author = m.Manifest.Author, Version = m.Manifest.Version,
                Enabled = m.Enabled, Priority = m.Priority,
                Installed = h?.FirstOrDefault(x => x.Action is "Installed" or "Updated")?.Time ?? Created(m.Folder),
                Fingerprint = fp, Files = files, Bytes = bytes,
                MadeUnique = Directory.Exists(Path.Combine(m.Folder, UniqueNames.BeforeFolder)),
                KeepShared = m.Manifest.KeepShared == true,
                Overlap = UniqueNames.Checked(m) ? UniqueNames.Reason(m) != null || PowerUsage.Reason(m) != null : null,
                CrashRisk = CrashCheck.Cached(m) is { } issues ? issues.Any(i => i.Crash) : null,
                History = h,
            });
        }
        if (game != null)
            try { snap.GameFiles = Ledger.Load(lib.DataFolder, game).Files(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return snap;
    }

    static DateTime? Created(string folder) { try { return Directory.GetCreationTimeUtc(folder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }

    /// <summary>A mod's files by name, size and date (nothing is read): the same fingerprint = the same files.</summary>
    public static (string Hash, int Files, long Bytes) Fingerprint(string folder)
    {
        try
        {
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + UniqueNames.BeforeFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f)).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder();
            // (times to 2 seconds: a zip keeps no finer, so a mod restored from a bundle fingerprints as the same)
            foreach (var f in files) sb.Append(Path.GetRelativePath(folder, f.FullName).ToLowerInvariant()).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks / 20_000_000).Append('\n');
            return (Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16], files.Count, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ("", 0, 0); }
    }

    /// <summary>Takes and saves one (gzip JSON); the automatic ones before Apply beyond the last 30 are deleted. Its file.</summary>
    public static string? Take(ModLibrary lib, GameState? game, string kind, string name = "")
    {
        try
        {
            var snap = Now(lib, game, kind, name);
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, $"{snap.Taken.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{kind}.json.gz");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(Folder, $"{snap.Taken.ToLocalTime():yyyy-MM-dd_HH-mm-ss}_{kind}_{i}.json.gz");
            Save(snap, file);
            if (kind == "apply")
                foreach (var old in List().Where(s => s.Kind == "apply").Skip(KeepApply)) Delete(old);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(Snapshot snap, string file)
    {
        string tmp = file + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, snap, Json);
        File.Move(tmp, file, overwrite: true);
        snap.File = file;
    }

    public static Snapshot? Load(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var s = JsonSerializer.Deserialize<Snapshot>(gz);
            if (s != null) s.File = file;
            return s;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every saved snapshot, newest first.</summary>
    public static List<Snapshot> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.EnumerateFiles(Folder, "*.json.gz").Select(Load).OfType<Snapshot>().OrderByDescending(s => s.Taken).ToList();
    }

    public static void Delete(Snapshot s) { try { File.Delete(s.File); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    public static void Rename(Snapshot s, string name) { s.Name = name; Save(s, s.File); }

    public static string Label(Snapshot s) => $"{s.Taken.ToLocalTime():yyyy-MM-dd HH:mm}" + (s.Kind switch { "launch" => " · Launch", "apply" => " · Before Apply", _ => "" })
        + (s.Name.Length > 0 ? " · " + s.Name : "");

    // ---- the audit report -------------------------------------------------------------------------------------------------

    /// <summary>
    /// What changed from <paramref name="a"/> (older) to <paramref name="b"/> (newer, or now), in sections with bullets
    /// (the app's popup style): mods added and removed, turned on and off, moved, updated (version or files), made unique or
    /// shared, pills that appeared or went away, what was done to mods in between (Crash Risk fixes …), and game files.
    /// </summary>
    public static string Compare(Snapshot a, Snapshot b)
    {
        var sb = new StringBuilder();
        void Section(string title, List<string> lines)
        {
            if (lines.Count == 0) return;
            sb.AppendLine(title + $" ({lines.Count}):");
            foreach (var l in lines.Take(60)) sb.AppendLine("  • " + l);
            if (lines.Count > 60) sb.AppendLine($"  • … and {lines.Count - 60} more");
            sb.AppendLine();
        }
        var am = a.Mods.ToDictionary(m => m.Folder, StringComparer.OrdinalIgnoreCase);
        var bm = b.Mods.ToDictionary(m => m.Folder, StringComparer.OrdinalIgnoreCase);
        string N(ModInfo m) => $"\"{m.Name}\"";
        sb.AppendLine($"From: {Label(a)}");
        sb.AppendLine($"To: {(b.File.Length == 0 ? "Now" : Label(b))}");
        sb.AppendLine($"Mods: {a.Mods.Count} ({a.Mods.Count(m => m.Enabled)} on) → {b.Mods.Count} ({b.Mods.Count(m => m.Enabled)} on)");
        sb.AppendLine();

        Section("Installed since", bm.Values.Where(m => !am.ContainsKey(m.Folder)).Select(m => $"{N(m)}{(m.Version is { Length: > 0 } v ? " v" + v.TrimStart('v', 'V') : "")}{(m.Enabled ? " (on)" : "")}").ToList());
        Section("Removed since", am.Values.Where(m => !bm.ContainsKey(m.Folder)).Select(m => $"{N(m)}{(m.Enabled ? " (it was on)" : "")}").ToList());
        var both = bm.Values.Where(m => am.ContainsKey(m.Folder)).ToList();
        Section("Turned on", both.Where(m => m.Enabled && !am[m.Folder].Enabled).Select(N).ToList());
        Section("Turned off", both.Where(m => !m.Enabled && am[m.Folder].Enabled).Select(N).ToList());
        // moved: the place among the mods in both (an install or a removal moves no one)
        var ra = a.Mods.Where(m => bm.ContainsKey(m.Folder)).OrderBy(m => m.Priority).Select((m, i) => (m.Folder, i)).ToDictionary(x => x.Folder, x => x.i + 1, StringComparer.OrdinalIgnoreCase);
        var rb = b.Mods.Where(m => am.ContainsKey(m.Folder)).OrderBy(m => m.Priority).Select((m, i) => (m.Folder, i)).ToDictionary(x => x.Folder, x => x.i + 1, StringComparer.OrdinalIgnoreCase);
        // (only the mods that moved: the rest keep their order among each other, the longest such run, so one mod dragged
        // to the top is the one listed, not every mod it passed)
        var seq = both.OrderBy(m => rb[m.Folder]).ToList();
        var stay = KeptInOrder(seq.Select(m => ra[m.Folder]).ToList()).Select(i => seq[i].Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Section("Moved in the priority order", seq.Where(m => !stay.Contains(m.Folder)).Select(m => $"{N(m)}: #{ra[m.Folder]} → #{rb[m.Folder]}").ToList());
        Section("Updated or changed", both.Where(m => am[m.Folder].Fingerprint != m.Fingerprint || am[m.Folder].Version != m.Version).Select(m =>
        {
            var o = am[m.Folder];
            string what = o.Version != m.Version ? $"v{o.Version?.TrimStart('v', 'V')} → v{m.Version?.TrimStart('v', 'V')}" : "its files changed (an edit, a build, Make Unique or a fix)";
            return $"{N(m)}: {what}{(o.Files != m.Files ? $"; {o.Files} → {m.Files} files" : "")}";
        }).ToList());
        var shared = new List<string>();
        foreach (var m in both)
        {
            var o = am[m.Folder];
            if (o.MadeUnique != m.MadeUnique) shared.Add($"{N(m)}: {(m.MadeUnique ? "made unique" : "Make Unique undone")}");
            if (o.KeepShared != m.KeepShared) shared.Add($"{N(m)}: Keep Shared Paths {(m.KeepShared ? "on" : "off")}");
        }
        Section("Unique and shared paths", shared);
        var pills = new List<string>();
        foreach (var m in both)
        {
            var o = am[m.Folder];
            if (o.CrashRisk is bool oc && m.CrashRisk is bool nc && oc != nc) pills.Add($"{N(m)}: CRASH RISK {(nc ? "appeared" : "gone")}");
            if (o.Overlap is bool oo && m.Overlap is bool no && oo != no) pills.Add($"{N(m)}: OVERLAP {(no ? "appeared" : "gone")}");
        }
        foreach (var m in bm.Values.Where(m => m.Enabled && m.CrashRisk == true)) if (!pills.Any(p => p.StartsWith(N(m) + ": CRASH"))) pills.Add($"{N(m)} (on): CRASH RISK");
        Section("Warnings", pills);
        // what was done between the two times (from the mods' histories)
        var done = new List<(DateTime, string)>();
        foreach (var m in b.Mods.Concat(a.Mods.Where(x => !bm.ContainsKey(x.Folder))))
            foreach (var h in m.History ?? [])
                if (h.Time > a.Taken && h.Time <= b.Taken) done.Add((h.Time, $"{h.Time.ToLocalTime():yyyy-MM-dd HH:mm} {N(m)}: {h.Action}{(h.Detail is { Length: > 0 } d ? $" ({d})" : "")}"));
        Section("Done to mods in between", done.DistinctBy(x => x.Item2).OrderBy(x => x.Item1).Select(x => x.Item2).ToList());
        if (a.GameFiles != null && b.GameFiles != null)
        {
            var gf = new List<string>();
            foreach (var (f, c) in b.GameFiles) if (!a.GameFiles.TryGetValue(f, out uint oc)) gf.Add($"{f}: first written"); else if (oc != c) gf.Add($"{f}: written again");
            foreach (var f in a.GameFiles.Keys.Where(f => !b.GameFiles.ContainsKey(f))) gf.Add($"{f}: no longer managed");
            Section("Game files Apply wrote", gf.Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
        string text = sb.ToString().TrimEnd();
        return text.Split('\n').Length <= 4 ? text + "\n\nNothing changed." : text;
    }

    /// <summary>The indices of the longest increasing run in <paramref name="v"/> (the mods that kept their order).</summary>
    static List<int> KeptInOrder(List<int> v)
    {
        int n = v.Count;
        var len = new int[n]; var prev = new int[n];
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            len[i] = 1; prev[i] = -1;
            for (int j = 0; j < i; j++) if (v[j] < v[i] && len[j] + 1 > len[i]) { len[i] = len[j] + 1; prev[i] = j; }
            if (best < 0 || len[i] > len[best]) best = i;
        }
        var run = new List<int>();
        for (int i = best; i >= 0; i = prev[i]) run.Add(i);
        return run;
    }

    // ---- restore -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The snapshot's state on the mods still installed (on/off, order, locks, tags, notes; mods installed since are turned
    /// off and go first, as a fresh install does). Returns what can't be put back (removed since, or changed: a bundle or a
    /// reinstall brings those). The caller saves (one undo step).
    /// </summary>
    public static List<string> Restore(ModLibrary lib, Snapshot s)
    {
        var missing = CantRestore(lib, s);
        var info = s.Mods.ToDictionary(m => m.Folder, StringComparer.OrdinalIgnoreCase);
        // order: the mods installed since first (as a fresh install), then the snapshot's order
        var fresh = lib.Mods.Where(m => !info.ContainsKey(m.FolderName)).OrderBy(m => m.Priority).ToList();
        int p = 0;
        foreach (var m in fresh) { m.Priority = p++; m.Enabled = false; m.Lock = ModLock.None; }
        foreach (var i in s.Mods.OrderBy(x => x.Priority))
            if (lib.Mods.FirstOrDefault(m => m.FolderName.Equals(i.Folder, StringComparison.OrdinalIgnoreCase)) is { } m)
            {
                m.Priority = p++; m.Enabled = i.Enabled;
                m.Lock = s.State.LockedTop?.Contains(i.Folder, StringComparer.OrdinalIgnoreCase) == true ? ModLock.Top
                    : s.State.LockedBottom?.Contains(i.Folder, StringComparer.OrdinalIgnoreCase) == true ? ModLock.Bottom : ModLock.None;
                m.UserTags = s.State.Tags?.GetValueOrDefault(i.Folder) is { } t ? [.. t] : [];
                m.HiddenTags = s.State.HiddenTags?.GetValueOrDefault(i.Folder) is { } ht ? [.. ht] : [];
                m.LocalNote = s.State.Notes?.GetValueOrDefault(i.Folder);
            }
        return missing;
    }

    /// <summary>What restoring <paramref name="s"/> can't bring back (see <see cref="PlanFiles"/>).</summary>
    public static List<string> CantRestore(ModLibrary lib, Snapshot s) => PlanFiles(lib, s).Cant;

    /// <summary>What a restore does to mods' files (Kurt, 2026-10-26: Make Unique, Fix Mirror Tables and Keep Shared Paths go
    /// back too): <see cref="Back"/> = the snapshot's copy of the mod, kept with the discarded copies (an edit, a fix, an
    /// update or a removal since); else <see cref="Ununique"/> = Undo Make Unique (its own copies from before it), or
    /// <see cref="Shared"/> = only Keep Shared Paths set back; <see cref="Cant"/> = the rest (state only).</summary>
    public sealed class FilePlan
    {
        public List<(ModInfo Info, Discarded.Entry Copy, Mod? Now)> Back = [];
        public List<(ModInfo Info, Mod Now)> Ununique = [];
        public List<(ModInfo Info, Mod Now)> Shared = [];
        public List<string> Cant = [];

        public int Count => Back.Count + Ununique.Count + Shared.Count;

        /// <summary>The preview's lines, one per mod.</summary>
        public List<string> Lines() => [
            .. Back.Select(b => $"\"{b.Info.Name}\": {(b.Now == null ? "put back" : "its files from then")} (kept {b.Copy.Time.ToLocalTime():yyyy-MM-dd HH:mm}, when: {b.Copy.Reason})"),
            .. Ununique.Select(u => $"\"{u.Info.Name}\": Make Unique undone{(u.Info.KeepShared ? "" : " (Keep Shared Paths off, as then)")}"),
            .. Shared.Select(x => $"\"{x.Info.Name}\": Keep Shared Paths {(x.Info.KeepShared ? "on" : "off")}"),
        ];
    }

    public static FilePlan PlanFiles(ModLibrary lib, Snapshot s)
    {
        var plan = new FilePlan();
        var copies = Discarded.List(lib.DataFolder);
        foreach (var i in s.Mods)
        {
            var now = lib.Mods.FirstOrDefault(m => m.FolderName.Equals(i.Folder, StringComparison.OrdinalIgnoreCase));
            if (now != null && (i.Fingerprint.Length == 0 || Fingerprint(now.Folder).Hash == i.Fingerprint)) continue;
            if (Discarded.Find(copies, i.Folder, i.Fingerprint) is { } copy) { plan.Back.Add((i, copy, now)); continue; }
            if (now == null) { plan.Cant.Add($"\"{i.Name}\" isn't installed any more{(i.Enabled ? " (it was on)" : "")} and its files weren't kept"); continue; }
            bool uniqueNow = Directory.Exists(Path.Combine(now.Folder, UniqueNames.BeforeFolder));
            if (!i.MadeUnique && uniqueNow && UniqueNames.Undoable(now, out _).Count > 0) { plan.Ununique.Add((i, now)); continue; }
            if (i.KeepShared != (now.Manifest.KeepShared == true)) plan.Shared.Add((i, now));
            plan.Cant.Add($"\"{now.Name}\" changed since (now {(now.Manifest.Version is { Length: > 0 } v ? "v" + v.TrimStart('v', 'V') : "another copy")}{(i.Version is { Length: > 0 } ov ? $", then v{ov.TrimStart('v', 'V')}" : "")}) and its files from then weren't kept: its state goes back, its files stay as they are now");
        }
        return plan;
    }

    /// <summary>Does <paramref name="plan"/>'s file changes (the mods' current files go to the discarded copies, so a
    /// later restore can bring them back again). Reload the library after. Returns a line per mod.</summary>
    public static List<string> RestoreFiles(ModLibrary lib, FilePlan plan)
    {
        var log = new List<string>();
        foreach (var (i, copy, now) in plan.Back)
            try
            {
                if (now != null) Discarded.Keep(lib.DataFolder, now.Folder, now.FolderName, "Replaced by restoring a snapshot");
                if (Directory.Exists(Path.Combine(lib.DataFolder, "mods", i.Folder))) { log.Add($"\"{i.Name}\": not put back (its folder couldn't be moved aside)"); continue; }
                Discarded.PutBack(lib.DataFolder, copy);
                ModHistory.Add(lib.DataFolder, i.Folder, "Files put back by a snapshot restore", $"from {copy.Time.ToLocalTime():yyyy-MM-dd HH:mm} ({copy.Reason})", i.Name, i.Version);
                log.Add($"\"{i.Name}\": files put back (from before: {copy.Reason})");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Add($"\"{i.Name}\": not put back ({ex.Message})"); }
        foreach (var (i, now) in plan.Ununique)
        {
            if (UniqueNames.Undo(lib, now, out string? error, i.KeepShared) is { } done)
            {
                ModHistory.Add(lib, now, "Undo Make Unique", $"by a snapshot restore; {done.Count} package(s)");
                log.Add($"\"{i.Name}\": Make Unique undone ({done.Count} package(s))");
            }
            else log.Add($"\"{i.Name}\": Make Unique not undone ({error})");
        }
        foreach (var (i, now) in plan.Shared)
        {
            var d = ModDraft.From(now);
            d.KeepShared = i.KeepShared;
            if (ModWriter.Save(lib, d, now, out string? error, "Keep Shared Paths set by a snapshot restore") != null) log.Add($"\"{i.Name}\": Keep Shared Paths {(i.KeepShared ? "on" : "off")}");
            else log.Add($"\"{i.Name}\": Keep Shared Paths not changed ({error})");
        }
        return log;
    }

    // ---- bundles -----------------------------------------------------------------------------------------------------------

    /// <summary>A bundle: the chosen mods' folders (mods\&lt;folder&gt;\…) and a snapshot (bundle.json) in one .zip. Returns the
    /// number of files written; <paramref name="progress"/> gets each mod's name.</summary>
    public static int MakeBundle(ModLibrary lib, GameState? game, IReadOnlyCollection<Mod> mods, string zip, Action<string>? progress = null)
    {
        var snap = Now(lib, game, "bundle", $"{mods.Count} mods");
        var names = mods.Select(m => m.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        snap.Mods = snap.Mods.Where(m => names.Contains(m.Folder)).ToList();
        string tmp = zip + ".tmp";
        int files = 0;
        using (var fs = File.Create(tmp))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("bundle.json", CompressionLevel.Optimal).Open()))
                w.Write(JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var m in mods.OrderBy(m => m.Priority))
            {
                progress?.Invoke(m.Name);
                foreach (string f in Directory.EnumerateFiles(m.Folder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(m.Folder, f).Replace('\\', '/');
                    var e = z.CreateEntry($"mods/{m.FolderName}/{rel}", CompressionLevel.Fastest);
                    e.LastWriteTime = File.GetLastWriteTime(f);
                    using var src = File.OpenRead(f);
                    using var dst = e.Open();
                    src.CopyTo(dst);
                    files++;
                }
            }
        }
        File.Move(tmp, zip, overwrite: true);
        return files;
    }

    /// <summary>A bundle's snapshot (bundle.json), without unpacking it.</summary>
    public static Snapshot? BundleInfo(string zip)
    {
        try
        {
            using var z = ZipFile.OpenRead(zip);
            if (z.GetEntry("bundle.json") is not { } e) return null;
            using var r = e.Open();
            var s = JsonSerializer.Deserialize<Snapshot>(r);
            if (s != null) s.File = zip;
            return s;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Restores a bundle: each of its mods the library lacks is unpacked into the library; one the library has with other
    /// files is replaced when <paramref name="replace"/> says so (the library's copy goes to the discarded copies), else kept.
    /// Entries are checked to stay inside their mod folder. Then the bundle's state is put back (the caller saves).
    /// Returns what happened, a line each.
    /// </summary>
    public static List<string> RestoreBundle(ModLibrary lib, string zip, bool replace, Action<string>? progress = null)
    {
        var log = new List<string>();
        var snap = BundleInfo(zip) ?? throw new InvalidDataException("not a bundle (no bundle.json)");
        string modsDir = Path.Combine(lib.DataFolder, "mods");
        using var z = ZipFile.OpenRead(zip);
        foreach (var m in snap.Mods)
        {
            if (!ModSafety.GameFileName(m.Folder)) { log.Add($"\"{m.Name}\": skipped (its folder name isn't safe)"); continue; }
            var have = lib.Mods.FirstOrDefault(x => x.FolderName.Equals(m.Folder, StringComparison.OrdinalIgnoreCase));
            if (have != null && Fingerprint(have.Folder).Hash == m.Fingerprint) { log.Add($"\"{m.Name}\": already the same"); continue; }
            if (have != null && !replace) { log.Add($"\"{m.Name}\": kept the library's copy (it differs from the bundle's)"); continue; }
            progress?.Invoke(m.Name);
            string target = Path.Combine(modsDir, m.Folder), fresh = target + ".bundle";
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            string prefix = $"mods/{m.Folder}/";
            int n = 0;
            foreach (var e in z.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0))
            {
                string rel = e.FullName[prefix.Length..];
                if (!ModSafety.InModFolder(rel)) throw new InvalidDataException($"{e.FullName}: a path outside its mod folder");
                string dest = Path.GetFullPath(Path.Combine(fresh, rel));
                if (!dest.StartsWith(Path.GetFullPath(fresh) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{e.FullName}: a path outside its mod folder");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                n++;
            }
            if (have != null) Discarded.Keep(lib.DataFolder, have.Folder, have.FolderName, "Replaced from a bundle");
            Directory.Move(fresh, target);
            ModHistory.Add(lib.DataFolder, m.Folder, have != null ? "Replaced from a bundle" : "Installed from a bundle", Path.GetFileName(zip), m.Name, m.Version);
            log.Add($"\"{m.Name}\": {(have != null ? "replaced" : "installed")} ({n} files)");
        }
        return log;
    }
}
