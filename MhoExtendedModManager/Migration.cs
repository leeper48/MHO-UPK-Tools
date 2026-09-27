namespace MhoExtendedModManager;

/// <summary>
/// Moves a user from MHModManager to this manager by copying (the old folder is left exactly as it was):
/// mods\ and state.json (same formats), the stock checksum list, the game root, the old package backups that
/// verify as stock (into originals\), and the icon / string / sound backups (into legacy\, for the later phases).
/// </summary>
static class Migration
{
    public static int Run(string oldFolder, string newLibrary, Settings settings)
    {
        string? oldData = Settings.LibraryData(oldFolder);
        if (oldData == null) { Console.WriteLine($"No MHModManager library in {oldFolder} (expected data\\mods)."); return 1; }
        string newMods = Path.Combine(newLibrary, "mods");
        if (Directory.Exists(newMods) && Directory.EnumerateFileSystemEntries(newMods).Any())
        { Console.WriteLine($"{newLibrary} already has mods; migration only fills an empty library. Nothing copied."); return 1; }
        if (Path.GetFullPath(newLibrary).StartsWith(Path.GetFullPath(oldFolder), StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("The new library can't be inside the old manager's folder."); return 1; }

        string? gameRoot = settings.ResolvedGameRoot(oldData);
        if (gameRoot == null || !Directory.Exists(Settings.Cooked(gameRoot))) { Console.WriteLine("Game folder not found (old config.txt or --game)."); return 1; }
        var game = new GameState(gameRoot, oldData);
        if (!game.HasStockList) { Console.WriteLine("No upk_checksums.json next to the old manager; can't verify originals."); return 1; }

        Console.WriteLine($"Migrating {oldFolder}\n       to {newLibrary}");
        Directory.CreateDirectory(newLibrary);
        int files = CopyTree(Path.Combine(oldData, "mods"), newMods, game);
        Console.WriteLine($"  mods: {Directory.GetDirectories(newMods).Length} mods, {files} files copied and verified");
        foreach (string f in new[] { Path.Combine(oldData, "state.json"), Path.Combine(oldFolder, "upk_checksums.json") })
            if (File.Exists(f))
            {
                string to = Path.Combine(newLibrary, Path.GetFileName(f));
                if (File.Exists(to)) File.Delete(to);   // an empty library's own state / list: replaced by the old manager's
                CopyVerified(f, to, game);
                Console.WriteLine($"  {Path.GetFileName(f)}");
            }

        // Old package backups: keep only verified stock copies as originals.
        var originals = new Originals(newLibrary, game);
        string oldBackups = Path.Combine(oldData, "upk_backups");
        int ok = 0; var rejected = new List<string>();
        if (Directory.Exists(oldBackups))
            foreach (string f in Directory.GetFiles(oldBackups, "*.upk"))
            {
                if (originals.Ensure(Path.GetFileName(f), out _, oldBackups) != null) ok++;
                else rejected.Add(Path.GetFileName(f));
            }
        // The icon packages: MHModManager kept their backups in data\ as ICO__….upk.bak.
        foreach (string f in Applier.IconPackages.Select(p => p.File))
            if (originals.Ensure(f, out _, oldData) != null) ok++;
            else rejected.Add(f);
        Console.WriteLine($"  originals: {ok} verified stock package(s)" + (rejected.Count > 0 ? $"; {rejected.Count} old backup(s) aren't stock and were left out: {string.Join(", ", rejected)}" : ""));

        // Icon / string / sound backups, as they are, for the texture, string and audio phases.
        string legacy = Path.Combine(newLibrary, "legacy");
        int legacyFiles = 0;
        foreach (string f in Directory.GetFiles(oldData, "*.bak")) { CopyVerified(f, Path.Combine(legacy, Path.GetFileName(f)), game); legacyFiles++; }
        foreach (string d in new[] { "string_backups", "sound_backups" })
            if (Directory.Exists(Path.Combine(oldData, d))) legacyFiles += CopyTree(Path.Combine(oldData, d), Path.Combine(legacy, d), game);
        Console.WriteLine($"  legacy backups (icons, strings, sounds): {legacyFiles} file(s)");

        // Icon changes in the game that no mod accounts for: kept as a mod, or Apply's rebuild from stock would drop them.
        var lib = ModLibrary.Load(newLibrary);
        IconCapture.Run(lib, new GameState(gameRoot, newLibrary), new Originals(newLibrary, new GameState(gameRoot, newLibrary)), null);
        File.WriteAllText(Path.Combine(newLibrary, "migrated.txt"), $"Migrated from {oldFolder} on {DateTime.Now:yyyy-MM-dd HH:mm}\r\n");
        settings.Library = newLibrary.Equals(Settings.DefaultLibrary, StringComparison.OrdinalIgnoreCase) ? null : newLibrary;
        settings.GameRoot ??= gameRoot;
        settings.Save();
        Console.WriteLine("Done. The old manager's folder is unchanged; stop using it now, or the two will undo each other's changes.");
        return 0;
    }

    static int CopyTree(string from, string to, GameState game)
    {
        int n = 0;
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            CopyVerified(f, Path.Combine(to, Path.GetRelativePath(from, f)), game);
            n++;
        }
        return n;
    }

    static void CopyVerified(string from, string to, GameState game)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: false);
        if (new FileInfo(from).Length != new FileInfo(to).Length || game.Crc(from) != game.Crc(to))
            throw new IOException($"Copy of {from} didn't verify.");
    }
}
