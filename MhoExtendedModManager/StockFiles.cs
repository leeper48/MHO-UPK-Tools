namespace MhoExtendedModManager;

/// <summary>
/// Where to READ a game package as the game ships it (Kurt, 2026-10-01: after power colors were applied, previews and the
/// power list read the recolored live files). The first copy whose CRC is the stock one: the kept original
/// (&lt;library&gt;\originals), the clean folder (Settings.CleanGameFiles: a clean CookedPCConsole copy, read only, never
/// written), the live file, its .bak; else the live file as it is (nothing better is known). Nothing here writes.
/// Initialised by the window and the command line (<see cref="Init"/>); without it, the live file.
/// </summary>
static class StockFiles
{
    static GameState? game;
    static string? clean, kept;
    static readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Init(GameState? g, string? cleanFolder, string? keptFolder)
    {
        game = g; clean = cleanFolder is { Length: > 0 } c && Directory.Exists(c) ? c : null; kept = keptFolder;
        lock (cache) cache.Clear();
    }

    /// <summary>The game's CookedPCConsole (null before <see cref="Init"/> with a game folder).</summary>
    public static string? Cooked => game?.Cooked;

    /// <summary>The clean folder in use (null when not set or missing).</summary>
    public static string? Clean => clean;

    /// <summary>The stock copy of <paramref name="file"/> (a package file name) to read, else the live file.</summary>
    public static string For(string cooked, string file)
    {
        string live = Path.Combine(cooked, file);
        if (game == null) return live;
        string stamp = File.Exists(live) ? File.GetLastWriteTimeUtc(live).Ticks.ToString() : "-";
        string key = file + "|" + stamp;
        lock (cache) if (cache.TryGetValue(key, out var hit)) return hit;
        string found = live;
        foreach (string? c in new[] { kept == null ? null : Path.Combine(kept, file), clean == null ? null : Path.Combine(clean, file), live, live + ".bak" })
            if (c != null && File.Exists(c) && game.MatchesStock(file, c)) { found = c; break; }
        lock (cache) cache[key] = found;
        return found;
    }

    /// <summary>The same for a full path in the game folder (other paths are returned as they are).</summary>
    public static string ForPath(string cooked, string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(Path.GetFullPath(cooked).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? For(cooked, Path.GetFileName(path)) : path;
}
