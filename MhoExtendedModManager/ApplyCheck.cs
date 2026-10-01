namespace MhoExtendedModManager;

/// <summary>
/// The checks shown at the top of Apply Changes (Kurt, 2026-10-01: list anything unexpected, suggest the clean folder,
/// and how to get a clean copy from Steam). Read only. Two kinds of finding:
/// - files the plan can't do (no clean original to build from or put back: Applier's problems), and
/// - game packages changed by something this app doesn't manage (no mod in the library names them: another tool, a
///   deleted mod, a hand edit) and never written by Apply (those it puts back itself). Apply leaves them as they are.
/// Only packages not dated like the game's own (2024-03-14) are hashed, so the check stays quick; a changed file that kept
/// the stock date would go unnoticed here (Settings → Check Backups hashes everything).
/// </summary>
static class ApplyCheck
{
    public sealed record Result(List<string> NoOriginal, List<(string File, bool HasClean)> Unexpected, string? CleanFolder)
    {
        public bool Any => NoOriginal.Count > 0 || Unexpected.Count > 0;
        /// <summary>Some file has no clean copy anywhere: the clean-folder / Steam advice applies.</summary>
        public bool NeedsClean => NoOriginal.Count > 0 || Unexpected.Any(u => !u.HasClean);
    }

    public static Result Run(ModLibrary lib, GameState game, Originals originals, Applier.Plan plan)
    {
        var noOriginal = plan.Problems.Where(p => p.Contains("no clean original", StringComparison.OrdinalIgnoreCase)).ToList();
        return new Result(noOriginal, Unexpected(lib, game, originals), StockFiles.Clean);
    }

    /// <summary>Game packages changed by something this app doesn't manage, with whether a clean original exists.</summary>
    public static List<(string File, bool HasClean)> Unexpected(ModLibrary lib, GameState game, Originals originals)
    {
        var managed = lib.Mods.SelectMany(m => m.Manifest.UpkReplacements.Concat(m.Manifest.Extra.Select(r => r.Package)))
            .Concat(Applier.IconPackages.Select(p => p.File)).Concat(Applier.WrittenBefore(game)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpected = new List<(string File, bool HasClean)>();
        if (game.HasStockList)
            foreach (var f in game.ModifiedByDate().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (managed.Contains(f.Name) || !game.IsStockName(f.Name) || f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
                if (game.IsStock(f.Name) != false) continue;   // re-dated but the game's own
                unexpected.Add((f.Name, originals.Find(f.Name) != null));
            }
        return unexpected;
    }

    /// <summary>The checks as text for the Apply window (empty when there's nothing to say).</summary>
    public static string Text(Result r, GameState game)
    {
        if (!r.Any) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CHECKS");
        if (r.NoOriginal.Count > 0)
            sb.AppendLine($"• {r.NoOriginal.Count} file(s) can't be changed: there's no clean original of them (listed under \"Left as they are\" below).");
        if (r.Unexpected.Count > 0)
        {
            sb.AppendLine($"• {r.Unexpected.Count} game file(s) were changed by something other than this app (another tool, a mod that was deleted, a hand edit). No mod in your list names them, so Apply leaves them as they are:");
            foreach (var (f, clean) in r.Unexpected.Take(40))
                sb.AppendLine($"    {f}{(clean ? "" : "  (no clean copy found)")}");
            if (r.Unexpected.Count > 40) sb.AppendLine($"    … and {r.Unexpected.Count - 40} more (Settings → Check Backups lists them all)");
            sb.AppendLine("  If you didn't expect these, a mod that changed them may have been removed without turning it off first.");
        }
        if (r.NeedsClean)
        {
            sb.AppendLine();
            sb.AppendLine(r.CleanFolder == null
                ? "SUGGESTION: if you have a clean copy of the game's CookedPCConsole folder (one kept before modding), choose it in Settings → Clean Game Files Folder. It's only read; the app takes the game's original files from it."
                : $"Your clean game files folder ({r.CleanFolder}) has no clean copy of some of these files.");
            sb.AppendLine();
            sb.AppendLine("No clean copy? Steam can give you one:");
            sb.AppendLine("  1. Close the game. In Steam, right-click Marvel Heroes in your Library → Properties → Installed Files → Verify Integrity of Game Files. Steam puts every changed game file back to the game's own (this takes all mods out of the game folder, including ones made with other tools).");
            sb.AppendLine($"  2. Copy the folder {game.Cooked} to a place outside the game folder (about 6.5 GB), for example D:\\MHO Clean\\CookedPCConsole.");
            sb.AppendLine("  3. Here: Settings → Clean Game Files Folder → choose that copy.");
            sb.AppendLine("  4. Apply Changes again: your turned-on mods go back in, and every file now has a clean original.");
            sb.AppendLine("  Changes made outside this app aren't put back by step 4: keep them first if you want them (icons: Settings → Capture Icon Changes).");
        }
        sb.AppendLine();
        sb.AppendLine("PLAN");
        return sb.ToString();
    }
}
