namespace MhoExtendedModManager;

/// <summary>
/// Which game packages aren't stock now, and where a stock copy of each is (Kurt, 2026-10-01: make sure .bak files are
/// truly originals, as MHModManager checked its backups against a clean copy). Read only: CRC against the stock list.
/// A .bak that isn't stock was taken from an already modded file; the app never uses it (it reads the kept original or
/// the clean folder instead) and never overwrites it (a .bak counts as the original for other tools).
/// </summary>
static class BackupCheck
{
    /// <summary>
    /// The .bak files that are the game's original (CRC = the stock checksum) but dated later (written before 0.37.69),
    /// and, unless <paramref name="dryRun"/>, gives each the game's date (2024-03-14). Only the date changes, never the
    /// content; a .bak that isn't the original keeps its date (the stock date would make it look like one). Each file is
    /// checked again just before its date is set.
    /// </summary>
    public static List<string> FixDates(GameState game, bool dryRun)
    {
        var done = new List<string>();
        foreach (string bk in Directory.EnumerateFiles(game.Cooked, "*.upk.bak").Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(bk), f = name[..^4];
            if (name.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
            if (File.GetLastWriteTime(bk).Date == GameState.StockDate || !game.MatchesStock(f, bk)) continue;
            if (!dryRun)
            {
                if (game.CrcOf(File.ReadAllBytes(bk)) != game.Crc(bk) || !game.MatchesStock(f, bk)) continue;
                MhoPackageModifier.MeshImport.SetStockDate(bk);
                if (File.GetLastWriteTime(bk).Date != GameState.StockDate) continue;   // couldn't be set (read only, in use)
            }
            done.Add(name);
        }
        return done;
    }

    public static (List<string> Lines, string Summary) Run(GameState game, Originals originals, string? clean, bool all = false)
    {
        var lines = new List<string>();
        int changed = 0, noSource = 0, badBak = 0, onlyClean = 0, lateBak = 0;
        // A .bak that is the original but dated after the stock date (written, not copied: before 0.37.69).
        foreach (string bk in Directory.EnumerateFiles(game.Cooked, "*.upk.bak"))
        {
            string f = Path.GetFileName(bk)[..^4];
            if (File.GetLastWriteTime(bk).Date != new DateTime(2024, 3, 14) && game.MatchesStock(f, bk)) lateBak++;
        }
        foreach (string f in Directory.EnumerateFiles(game.Cooked, "*.upk").Select(p => Path.GetFileName(p)!).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (f.Contains("copy", StringComparison.OrdinalIgnoreCase) || game.IsStock(f) != false) continue;
            changed++;
            string bak = Path.Combine(game.Cooked, f + ".bak");
            bool hasBak = File.Exists(bak), bakOk = hasBak && game.MatchesStock(f, bak);
            bool kept = originals.Get(f) != null;
            bool inClean = clean != null && File.Exists(Path.Combine(clean, f)) && game.MatchesStock(f, Path.Combine(clean, f));
            if (inClean && !kept && !bakOk) onlyClean++;
            if (!kept && !bakOk && !inClean) noSource++;
            if (hasBak && !bakOk) badBak++;
            if (all || !kept && !bakOk || hasBak && !bakOk)
                lines.Add($"{f}: changed · .bak {(hasBak ? bakOk ? "stock" : "NOT STOCK (taken from a modded file)" : "none")} · kept original {(kept ? "yes" : "no")} · clean folder {(clean == null ? "not set" : inClean ? "stock" : "missing")}");
        }
        string summary = $"{changed} game package(s) differ from stock; {badBak} with a .bak that isn't stock; {lateBak} .bak file(s) that are the original but dated later; {noSource} with no stock copy anywhere"
            + (clean != null ? $"; {onlyClean} with a stock copy only in the clean folder" : "; no clean folder set (Settings → Clean Game Files Folder)");
        return (lines, summary);
    }
}
