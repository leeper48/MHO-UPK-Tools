using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Apply: make the game match the library's state.
/// <list type="bullet">
/// <item>Packages: every package any mod replaces gets the highest-priority enabled mod's copy, or else its verified
/// stock original.</item>
/// <item>Icon textures: the three UI icon packages are rebuilt from their verified stock originals with each texture's
/// winning replacement (MPM's TextureImport.ReplaceMany: mips inline, no cache; the same form MHModManager writes,
/// checked byte for byte 2026-09-27). With no replacements a package goes back to stock.</item>
/// <item>Strings: each .string file is rebuilt from its original with each ID's winning replacement (StringFile; the
/// same rule and bytes as MHModManager). No .bak goes next to these: the originals are kept in the library.</item>
/// <item>Sounds: each .pck a sound pack patches is rebuilt from its original with the enabled packs' patches (Akpk.Build;
/// byte-identical to MHModManager's own library on both of the Miles Morales pack's files). Originals kept in the library.</item>
/// </list>
/// Every game-folder write goes through MHO Package Modifier's MeshImport.WriteLive (verified temp file, swap, undo
/// history; CLAUDE.md rule 1). A missing .bak is created from the verified original, not from a possibly modded live
/// file.
/// </summary>
static class Applier
{
    /// <summary>The UI icon packages and the manifest list that targets each.</summary>
    public static readonly (string File, string Label, Func<ModManifest, List<TextureReplacement>> List)[] IconPackages =
    [
        ("ICO__MarvelUIIcons_SF.upk", "icon", m => m.Replacements),
        ("ICO__MarvelUIIcons_Achievements_SF.upk", "achievement icon", m => m.AchievementReplacements),
        ("ICO__MarvelUIIcons_Store_SF.upk", "store image", m => m.StoreReplacements),
    ];

    /// <param name="File">A package name in CookedPCConsole, or for strings a path under Loco (&lt;lang&gt;.all\&lt;file&gt;.string).</param>
    /// <param name="Source">File to copy in (a mod's package or an original); null when <paramref name="Built"/> holds the bytes.</param>
    public enum Kind { Package, Strings, Sound, Tfc }

    public sealed record Step(string File, string What, string? Source, byte[]? Built, uint Crc, Func<byte[], List<string>>? Verify = null, Kind Type = Kind.Package)
    {
        public bool Strings => Type == Kind.Strings;
    }

    public sealed record Plan(List<Step> Steps, List<string> Problems, int UpToDate, List<string> NotHandled);

    /// <param name="legacy">Extra folder to find stock originals in (the migrated MHModManager backups).</param>
    public static Plan MakePlan(ModLibrary lib, GameState game, Originals originals)
    {
        var steps = new List<Step>(); var problems = new List<string>(); int upToDate = 0;
        string legacy = Path.Combine(lib.DataFolder, "legacy");
        var winners = lib.PackageWinners();
        var ledger = Ledger.Load(lib.DataFolder, game);
        var icons = IconPackages.Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Also every package this app wrote before (its undo history) that no mod names now: a mod that dropped a package in
        // an edit or update, or a mod folder deleted by hand. It goes back to the original like a mod turned off; before
        // 0.37.72 it stayed changed for good (Kurt's Ms. Marvel power colors after Blue Marvel lost them).
        var named = lib.Mods.SelectMany(m => m.Manifest.UpkReplacements).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftOver = WrittenBefore(game).Where(f => !named.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managed = named.Concat(leftOver).Where(f => !icons.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (string file in managed)
        {
            string live = Path.Combine(game.Cooked, file);
            if (!File.Exists(live)) { if (winners.ContainsKey(file)) problems.Add($"{file}: not in the game folder, so it isn't replaced"); continue; }
            string? original = originals.Find(file);
            winners.TryGetValue(file, out var mod);
            string? source = mod != null ? Path.Combine(mod.Folder, file) : original;
            if (mod != null && !File.Exists(source)) { problems.Add($"{file}: missing from {mod.Name}'s folder"); continue; }
            if (source == null)
            {
                if (game.IsStock(file) != true) problems.Add($"{file}: should be stock again, but no clean original is available");
                continue;
            }
            uint want = game.Crc(source);
            // In step with the list: recorded as the state this app saw it in (Ledger).
            if (game.Crc(live) == want) { upToDate++; ledger.Set(file, want); continue; }
            // Putting the original back only over a file that's ours (Kurt, 2026-10-02: other programs install files too, e.g. a
            // freecam installer writing the zone packages Free Cam with Walls / Freecam Falloff Helper also name): the live file
            // is a library mod's copy of it, or what this app last wrote there (its undo history). Anything else was changed by
            // another program since: left as it is. Turning a mod off still restores its files (they are its copy).
            if (mod == null && !Ours(lib, game, ledger, file, live))
            {
                problems.Add($"{file}: changed since this app last wrote or checked it (another program, or files copied in by hand), so it isn't put back to the original");
                continue;
            }
            // Installing needs a clean original too, or the mod could never be taken off again.
            if (original == null) { problems.Add($"{file} ({mod!.Name}): no clean original available, so it isn't installed"); continue; }
            steps.Add(new Step(file, mod != null ? $"install from {mod.Name}" : leftOver.Contains(file) ? "restore stock original (written by this app before; no mod names it now)" : "restore stock original", source, null, want));
        }

        // Icon packages: the three MHModManager lists, plus every other icon package a mod names in ExtraIconReplacements.
        var targets = IconPackages.Select(p => (p.File, p.Label, Extra: false,
                (Func<ModManifest, IEnumerable<(string Tex, string Dds)>>)(man => p.List(man).Select(r => (r.TextureName, r.DdsFileName)))))
            .Concat(lib.Mods.SelectMany(m => m.Manifest.Extra.Select(r => r.Package)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !IconPackages.Any(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => (File: f, Label: "icon", Extra: true,
                    (Func<ModManifest, IEnumerable<(string Tex, string Dds)>>)(man => man.Extra.Where(r => r.Package.Equals(f, StringComparison.OrdinalIgnoreCase)).Select(r => (r.TextureName, r.DdsFileName))))))
            .ToList();
        foreach (var (file, label, extra, list) in targets)
        {
            string live = Path.Combine(game.Cooked, file);
            if (!File.Exists(live)) { if (extra) problems.Add($"{file}: not in the game folder"); continue; }
            // Each texture's winner: the highest-priority enabled mod that replaces it.
            var chosen = new Dictionary<string, (Mod Mod, string Dds)>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in lib.Mods.Where(m => m.Enabled).OrderBy(m => m.Priority))
                foreach (var (tex, dds) in list(m.Manifest))
                    chosen.TryAdd(tex, (m, dds));
            string? original = originals.Find(file, legacy);
            if (original == null)
            {
                if (chosen.Count > 0 || game.IsStock(file) != true) problems.Add($"{file}: no clean original available, so its {label}s aren't changed");
                continue;
            }
            // Rebuild from the original; sorted so the same state always gives the same bytes.
            var items = new List<TextureImport.Replacement>();
            foreach (var (tex, (m, ddsName)) in chosen.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                string dds = Path.Combine(m.Folder, ddsName);
                if (!File.Exists(dds)) { problems.Add($"{file}: {m.Name} is missing {ddsName}"); continue; }
                items.Add(new TextureImport.Replacement(tex, File.ReadAllBytes(dds), $"{m.Name}: {ddsName}"));
            }
            byte[]? built = null;
            Func<byte[], List<string>>? verify = null;
            uint want;
            if (chosen.Count == 0) want = game.Crc(original);
            else
            {
                List<string> buildProblems;
                try { built = TextureImport.ReplaceMany(Package.Open(original), items, out buildProblems, out var v); verify = v; }
                catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or IOException) { buildProblems = [ex.Message]; }
                if (built == null) { problems.AddRange(buildProblems.Select(p => $"{file}: {p}")); continue; }
                want = game.CrcOf(built);
            }
            if (game.Crc(live) == want) { upToDate++; continue; }
            // An extra package that some other tool changed: a rebuild from stock would drop those images (capture them first).
            if (extra)
            {
                var claimed = lib.Mods.SelectMany(m => list(m.Manifest)).Select(x => x.Tex).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var orphans = IconCapture.ChangedTextures(game, live, original, originals).Where(t => !claimed.Contains(t)).ToList();
                if (orphans.Count > 0) { problems.Add($"{file}: {orphans.Count} image(s) in it were changed outside any mod ({string.Join(", ", orphans.Take(5))}{(orphans.Count > 5 ? ", …" : "")}); use Capture icon changes first"); continue; }
            }
            int modCount = chosen.Values.Select(c => c.Mod).Distinct().Count();
            steps.Add(built == null
                ? new Step(file, $"restore stock original (no {label} replacements enabled)", original, null, want)
                : new Step(file, $"rebuild from stock with {items.Count} {label}(s) from {modCount} mod(s)", null, built, want, verify));
        }
        // Strings: every .string file of every language, rebuilt from its original with each ID's winner.
        if (Directory.Exists(game.Loco))
            foreach (string langDir in Directory.GetDirectories(game.Loco, "*.all").Order(StringComparer.OrdinalIgnoreCase))
                foreach (string liveFile in Directory.GetFiles(langDir, "*.string").Order(StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(liveFile), rel = Path.Combine(Path.GetFileName(langDir), name);
                    var chosen = new Dictionary<ulong, (Mod Mod, StringReplacement R)>();
                    foreach (var m in lib.Mods.Where(m => m.Enabled).OrderBy(m => m.Priority))
                        foreach (var r in m.Strings.Where(r => r.File.Equals(name, StringComparison.OrdinalIgnoreCase)))
                            chosen.TryAdd(r.Id, (m, r));
                    string? original = originals.FindString(rel, legacy);
                    if (original == null) { if (chosen.Count > 0) problems.Add($"{rel}: no original to build from, so its strings aren't changed"); continue; }
                    byte[] originalBytes = File.ReadAllBytes(original);
                    StringFile expected;
                    try
                    {
                        expected = StringFile.Parse(originalBytes);
                        foreach (var (id, (_, r)) in chosen)
                            expected.Entries[id] = new StringFile.Entry(r.FlagsProduced, r.Text,
                                r.Variants ?? (expected.Entries.TryGetValue(id, out var old) ? old.Variants : []));
                    }
                    catch (InvalidDataException ex) { problems.Add($"{rel}: original doesn't read ({ex.Message})"); continue; }
                    byte[] built = chosen.Count == 0 ? originalBytes : expected.Write();
                    uint crc = game.CrcOf(built);
                    if (game.Crc(liveFile) == crc) { upToDate++; continue; }
                    var exp = expected;
                    List<string> Verify(byte[] onDisk)
                    {
                        try { return StringFile.Differences(exp, StringFile.Parse(onDisk)); }
                        catch (InvalidDataException ex) { return [ex.Message]; }
                    }
                    int mods = chosen.Values.Select(c => c.Mod).Distinct().Count();
                    steps.Add(new Step(rel, chosen.Count == 0 ? "restore original strings" : $"rebuild from original with {chosen.Count} string(s) from {mods} mod(s)", null, built, crc, Verify, Kind.Strings));
                }

        // Sounds: every .pck any mod's sound pack patches, rebuilt from its original with the enabled packs (top of the order first).
        static string PackKey(string file)
        {
            try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
            catch (IOException) { return file; }
        }
        var loaded = new List<(Mod Mod, SoundPack Pack)>();
        foreach (var m in lib.Mods.OrderBy(m => m.Priority))
            foreach (string f in m.Manifest.AudioPacks)
            {
                string path = Path.Combine(m.Folder, f);
                if (!File.Exists(path)) { if (m.Enabled) problems.Add($"{m.Name}: {f} missing"); continue; }
                try { loaded.Add((m, SoundPack.Load(path))); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or FormatException) { problems.Add($"{m.Name}: {f} doesn't read ({ex.Message})"); }
            }
        foreach (string pck in loaded.SelectMany(l => l.Pack.Patches.Select(p => p.PckFile)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            string live = Path.Combine(game.Cooked, pck);
            // The same pack in two enabled mods (a costume mod and its moved copy) counts once: the higher mod's.
            var packs = loaded.Where(l => l.Mod.Enabled && l.Pack.Patches.Any(p => p.PckFile.Equals(pck, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(l => PackKey(l.Pack.File)).Select(g => g.First()).ToList();
            if (!File.Exists(live)) { if (packs.Count > 0) problems.Add($"{pck}: not in the game folder"); continue; }
            string? original = originals.FindSound(pck, legacy);
            if (original == null) { problems.Add($"{pck}: no original (it isn't stock-dated and there's no backup), so its sounds aren't changed"); continue; }
            if (packs.Count == 0)
            {
                uint orig = game.Crc(original);
                if (game.Crc(live) == orig) upToDate++;
                else steps.Add(new Step(pck, "restore original sounds", original, null, orig, null, Kind.Sound));
                continue;
            }
            var notes = new List<string>(); var buildProblems = new List<string>();
            byte[]? built = Akpk.Build(original, pck, packs.Select(x => x.Pack).ToList(), notes, buildProblems);
            problems.AddRange(buildProblems);
            problems.AddRange(notes.Select(n => $"{pck}: {n}"));
            if (built == null) continue;
            uint crc = game.CrcOf(built);
            if (game.Crc(live) == crc) { upToDate++; continue; }
            int events = packs.Sum(x => x.Pack.Patches.Count(p => p.PckFile.Equals(pck, StringComparison.OrdinalIgnoreCase)));
            steps.Add(new Step(pck, $"rebuild from original with {events} new sound event(s) from {packs.Count} pack(s)", null, built, crc, SoundVerify, Kind.Sound));
        }

        // Texture caches with a kept original (Icons.tfc): back to the original. Every icon replacement is stored inside
        // the icon packages, so nothing needs the cache modded; but only once every changed image in it belongs to an
        // enabled mod, or restoring would drop images nothing else provides (capture them first).
        string cooked = game.Cooked;
        if (originals.FindTfc("Icons") is string tfcOriginal && File.Exists(Path.Combine(cooked, "Icons.tfc")))
        {
            uint want = game.Crc(tfcOriginal);
            if (game.Crc(Path.Combine(cooked, "Icons.tfc")) == want) upToDate++;
            else
            {
                // Changed images: an enabled mod's (it's in the package anyway), a disabled mod's (a leftover: goes back to
                // stock, like a disabled mod's packages), or nobody's (would be lost: capture first).
                var enabledNames = lib.Mods.Where(m => m.Enabled).SelectMany(m => IconPackages.SelectMany(p => p.List(m.Manifest))).Select(r => r.TextureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var anyNames = lib.Mods.SelectMany(m => IconPackages.SelectMany(p => p.List(m.Manifest))).Select(r => r.TextureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var changed = TfcCheck.Changed(cooked, "Icons", tfcOriginal).Select(t => t[(t.LastIndexOf('.') + 1)..]).ToList();
                var orphans = changed.Where(n => !anyNames.Contains(n)).ToList();
                var leftovers = changed.Where(n => anyNames.Contains(n) && !enabledNames.Contains(n)).ToList();
                if (orphans.Count > 0) problems.Add($"Icons.tfc: {orphans.Count} changed image(s) in it belong to no mod ({string.Join(", ", orphans.Take(5))}{(orphans.Count > 5 ? ", …" : "")}); use Capture icon changes first, so restoring it loses nothing");
                else steps.Add(new Step("Icons.tfc", "restore the original texture cache (all icon changes are in the icon packages)" +
                    (leftovers.Count > 0 ? $"; back to stock, as their mods are off: {string.Join(", ", leftovers)}" : ""), tfcOriginal, null, want, null, Kind.Tfc));
            }
        }

        var notHandled = new List<string>();
        ledger.Save();
        return new Plan(steps, problems, upToDate, notHandled);
    }

    /// <summary>A written .pck reads back: header, both tables, every entry inside the file.</summary>
    static List<string> SoundVerify(byte[] onDisk)
    {
        try
        {
            using var ms = new MemoryStream(onDisk, false);
            var pk = Akpk.Read(ms);
            var bad = pk.Banks.Concat(pk.Streams).Where(e => (long)e.StartBlock * e.BlockSize + e.Size > onDisk.Length).Select(e => $"entry {e.Id:X8} runs past the end").Take(3).ToList();
            return bad;
        }
        catch (InvalidDataException ex) { return [ex.Message]; }
    }

    /// <summary>A skipped sound-pack line ("SFX_x.pck: Name: skipped (why)"), not a whole file.</summary>
    /// <summary>
    /// The game packages this app has written (its undo history, Settings.HistoryFolder: one folder per live file named
    /// "&lt;file&gt;_&lt;first 8 hex of SHA-256 of the lower-cased full path&gt;", as MPM's History names them), that are
    /// still in this game folder.
    /// </summary>
    public static List<string> WrittenBefore(GameState game)
    {
        var result = new List<string>();
        string root = Settings.HistoryFolder;
        if (!Directory.Exists(root)) return result;
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(dir);
            int us = name.LastIndexOf('_');
            if (us <= 0 || name.Length - us != 9) continue;
            string file = name[..us];
            if (!file.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)) continue;
            string live = Path.GetFullPath(Path.Combine(game.Cooked, file));
            string h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(live.ToLowerInvariant())))[..8];
            if (h.Equals(name[(us + 1)..], StringComparison.OrdinalIgnoreCase) && File.Exists(live)) result.Add(file);
        }
        return result;
    }

    /// <summary>Is the live game file one this app may put back to the original? Recorded in the ledger: only while it's
    /// still what was recorded (or what the undo history says this app wrote). Never recorded (e.g. a mod migrated from
    /// MHModManager, installed before this app saw it): a copy of it from a mod in the library, or this app's last write.</summary>
    public static bool Ours(ModLibrary lib, GameState game, Ledger ledger, string file, string live)
    {
        uint crc = game.Crc(live);
        // What this app last wrote or last saw in step with the list (Kurt, 2026-10-02: a reinstall of a mod's own files
        // by hand, or by another program, matched "a mod's copy" and was undone). A recorded file is ours only while it is
        // still that; the undo history covers a write the ledger missed.
        if (ledger.Get(file) is uint seen) return seen == crc || History.IsLastWritten(live);
        // Written by this app before (its undo history has the file): ours only if it's still what this app wrote.
        if (History.Counts(live) is var (u, r) && u + r > 0) return History.IsLastWritten(live);
        foreach (var m in lib.Mods)
            if (m.Manifest.UpkReplacements.Contains(file, StringComparer.OrdinalIgnoreCase) && Path.Combine(m.Folder, file) is var copy && File.Exists(copy) && game.Crc(copy) == crc)
                return true;
        return History.IsLastWritten(live);
    }

    /// <summary>Is <paramref name="path"/> a file directly in CookedPCConsole (or, for strings, in a &lt;lang&gt;.all folder
    /// directly under Loco)? Apply writes nowhere else.</summary>
    public static bool InGameFolder(GameState game, string path, bool strings)
    {
        string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full) ?? "";
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        if (!strings) return dir.Equals(Norm(game.Cooked), StringComparison.OrdinalIgnoreCase);
        return Path.GetFileName(dir).EndsWith(".all", StringComparison.OrdinalIgnoreCase)
            && (Path.GetDirectoryName(dir) ?? "").Equals(Norm(game.Loco), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSoundLine(string problem) => problem.Contains(": skipped (", StringComparison.Ordinal);

    public static void Print(Plan p)
    {
        foreach (var s in p.Steps) Console.WriteLine($"  {s.File}: {s.What}");
        // Two kinds of skipped items (a user's 7 were all sound-pack lines, not files): sound-pack lines that can't be
        // added (the rest of the pack is), and files that are left exactly as they are.
        var lines = p.Problems.Where(IsSoundLine).ToList();
        var files = p.Problems.Where(x => !IsSoundLine(x)).ToList();
        if (files.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Left as they are (nothing is written to these; the game keeps its current copy):");
            foreach (string x in files) Console.WriteLine($"  {x}");
            Console.WriteLine("  Usually the game's copy was already changed before (by a mod or another tool) and there's no clean");
            Console.WriteLine("  original to rebuild it from. Everything else still applies normally.");
        }
        if (lines.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Sound-pack lines left out (the rest of each pack is added and works):");
            foreach (string x in lines) Console.WriteLine($"  {x}");
            Console.WriteLine("  Each new line copies an existing sound event; these name one that isn't in that sound bank (for");
            Console.WriteLine("  example one added by another pack that isn't installed or turned on).");
        }
        if (p.Problems.Count > 0) Console.WriteLine();
        Console.WriteLine($"{p.Steps.Count} file(s) to change, {p.UpToDate} already right, {p.Problems.Count} skipped.");
        if (p.NotHandled.Count > 0)
            Console.WriteLine($"Not applied by this version: the sound packs of {p.NotHandled.Count} enabled mod(s); the game keeps whatever is there now.");
    }

    /// <summary>Writes the plan. Stops at the first failure (the files done so far stay done; each is its own undo step).</summary>
    public static bool Execute(Plan p, GameState game, Originals originals, string libraryData)
    {
        if (ZoneBuilds.GameRunning()) { Console.WriteLine("The game is running; close it first. Nothing written."); return false; }
        string legacy = Path.Combine(libraryData, "legacy");
        var ledger = Ledger.Load(libraryData, game);
        int done = 0;
        foreach (var s in p.Steps)
        {
            Console.WriteLine($"{s.File}: {s.What}");
            string live = s.Strings ? Path.Combine(game.Loco, s.File) : Path.Combine(game.Cooked, s.File);
            // Last line of defense (security audit): only files directly in CookedPCConsole, or in a <lang>.all folder for strings.
            if (!InGameFolder(game, live, s.Strings)) { Console.WriteLine($"  refused: {s.File} isn't a file in the game's {(s.Strings ? "language" : "package")} folder; stopping."); return false; }
            if (s.Type == Kind.Tfc)
            {
                if (originals.FindTfc(Path.GetFileNameWithoutExtension(s.File)) == null) { Console.WriteLine("  no original kept; stopping."); return false; }
            }
            else if (s.Type != Kind.Package)
            {
                // The original goes into the library, not next to the file (strings: a folder the game reads; sounds: 300+ MB).
                if ((s.Strings ? originals.EnsureString(s.File, legacy) : originals.EnsureSound(s.File, legacy)) == null) { Console.WriteLine("  no original to keep; stopping."); return false; }
            }
            else
            {
                string? original = originals.Ensure(s.File, out string? why, legacy);
                if (original == null) { Console.WriteLine($"  no clean original ({why}); stopping."); return false; }
                if (!MeshImport.CreateBak(live, File.ReadAllBytes(original), stockDated: true)) return false;
            }
            byte[] bytes = s.Built ?? File.ReadAllBytes(s.Source!);
            if (game.CrcOf(bytes) != s.Crc) { Console.WriteLine("  source changed since the plan was made; stopping."); return false; }
            History.Label = $"Ext Mod Manager: {s.What}";
            bool ok = MeshImport.WriteLive(live, bytes, onDisk =>
            {
                var problems = new List<string>();
                if (game.CrcOf(onDisk) != s.Crc) problems.Add("CRC differs from what was planned");
                if (s.Type == Kind.Package && (onDisk.Length < 4 || BitConverter.ToUInt32(onDisk, 0) != 0x9E2A83C1)) problems.Add("not a package (bad magic)");
                if (s.Verify != null) problems.AddRange(s.Verify(onDisk));
                return problems;
            }, bakBeside: s.Type == Kind.Package);
            if (!ok) { Console.WriteLine($"Stopped after {done} of {p.Steps.Count}."); return false; }
            // A package put back to the game's own (CRC is the stock one): the stock date again, so it doesn't look modified.
            if (s.Type == Kind.Package && game.MatchesStock(s.File, live)) MeshImport.SetStockDate(live);
            if (s.Type == Kind.Package) { ledger.Set(s.File, s.Crc); ledger.Save(); }   // what this app put there
            done++;
        }
        Console.WriteLine($"Applied: {done} file(s) written and verified.");
        return true;
    }
}

/// <summary>
/// The state this app last left or saw each managed game package in (CRC-32), per game folder: data\library\game_files.json.
/// Updated when Apply writes a package and whenever a package is found in step with the list. A package that no longer
/// matches its record was changed by something else (another program, a reinstall by hand), and Apply doesn't put it back.
/// </summary>
sealed class Ledger
{
    readonly string path;
    readonly Dictionary<string, uint> files;
    bool dirty;

    Ledger(string path, Dictionary<string, uint> files) { this.path = path; this.files = files; }

    public static Ledger Load(string libraryData, GameState game)
    {
        string p = Path.Combine(libraryData, "game_files.json");
        var all = new Dictionary<string, Dictionary<string, uint>>(StringComparer.OrdinalIgnoreCase);
        try { if (File.Exists(p)) all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, uint>>>(File.ReadAllText(p)) ?? all; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        all = new Dictionary<string, Dictionary<string, uint>>(all, StringComparer.OrdinalIgnoreCase);
        string key = Path.GetFullPath(game.Cooked).TrimEnd('\\').ToLowerInvariant();
        if (!all.TryGetValue(key, out var mine)) all[key] = mine = [];
        var ledger = new Ledger(p, new Dictionary<string, uint>(mine, StringComparer.OrdinalIgnoreCase)) { all = all, key = key };
        return ledger;
    }

    Dictionary<string, Dictionary<string, uint>> all = [];
    string key = "";

    public uint? Get(string file) => files.TryGetValue(file, out uint c) ? c : null;

    public void Set(string file, uint crc)
    {
        if (files.TryGetValue(file, out uint c) && c == crc) return;
        files[file] = crc; dirty = true;
    }

    public void Save()
    {
        if (!dirty) return;
        all[key] = files;
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
            dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
