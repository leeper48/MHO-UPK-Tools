using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Save's build steps that need no window: the Editor's Save and the mod list's Resave (ModResave) run the same code.
/// Moved here unchanged from ModEditorView (MakeDraftUnique, RepairVoices), 2026-11-03.
/// </summary>
static class SaveSteps
{
    /// <summary>
    /// Every Save makes the mod's character packages unique (Kurt, 2026-10-11: mods made here never get the OVERLAP warning):
    /// a changed model or material instance that other stock packages also carry gets paths of its own (UniqueNames), so a
    /// stock copy loaded first can't show instead (a Model tab build into Jessica's team-up is jessica_jones.jessicajones_tv,
    /// like her agent's and NPC's). A package already unique, or one that ships every copy, is left as it is. The work folder.
    /// </summary>
    public static string? MakeUnique(ModDraft draft, ModLibrary lib, GameState? game, List<string> log)
    {
        if (game == null || draft.KeepShared) return null;   // (Overview → Keep Shared Paths)
        var files = draft.Packages.Where(p => UniqueNames.Covers(p.File) && File.Exists(p.Source)).ToList();
        if (files.Count == 0) return null;
        var shared = UniqueNames.Shared(UniqueNames.Folder(game.Cooked));
        var shipped = draft.Packages.Select(p => p.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string tag = UniqueNames.Tag(draft.Name), work = Path.Combine(lib.DataFolder, "make-unique-" + Guid.NewGuid().ToString("N")[..8]);
        bool any = false;
        foreach (var (file, source) in files)
        {
            string src = source;
            var own = UniqueNames.OwnFiles(file, game.Cooked);
            // groups an earlier Make Unique renamed that today's rules keep shared go back first (0.50.19)
            var backNotes = new List<string>();
            if (UniqueNames.Reshare(src, StockFiles.For(game.Cooked, file), file, shared, shipped, own, draft.Name, backNotes, game.Cooked) is byte[] reshared)
            {
                Directory.CreateDirectory(work);
                string back = Path.Combine(work, "reshared_" + file);
                File.WriteAllBytes(back, reshared);
                log.AddRange(backNotes.Select(n => $"unique: {file}: {n}"));
                int kb = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.Packages[kb] = (file, back);
                src = back;
                any = true;
            }
            string? Shipped(string f) => draft.Packages.FirstOrDefault(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source;
            var plan = UniqueNames.PlanFor(src, StockFiles.For(game.Cooked, file), file, shared, shipped, tag, own, game.Cooked, Shipped);
            // the mod's own copies of other packages still holding another model there get the mod's (a user's Bishop: the
            // mod's recolored Future Bomb kept stock Cable's X-Men model, and showed instead on TAHITI, 2026-11-09)
            if (plan.Syncs.Count > 0)
            {
                string pre = src;
                var syncNotes = new List<string>();
                foreach (var (sf, bytes) in UniqueNames.SyncCopies([plan], f => f.Equals(file, StringComparison.OrdinalIgnoreCase) ? pre : Shipped(f), syncNotes))
                {
                    Directory.CreateDirectory(work);
                    string sdst = Path.Combine(work, "synced_" + sf);
                    File.WriteAllBytes(sdst, bytes);
                    int sk = draft.Packages.FindIndex(p => p.File.Equals(sf, StringComparison.OrdinalIgnoreCase));
                    if (sk >= 0) draft.BeforeUnique[sf] = (draft.Packages[sk].Source, UniqueNames.Sha(bytes));   // (Undo Make Unique puts it back too)
                    if (sk >= 0) draft.Packages[sk] = (sf, sdst);
                    any = true;
                }
                log.AddRange(syncNotes.Select(n => "unique: " + n));
            }
            string final = src;
            if (plan.Changes)
            {
                Directory.CreateDirectory(work);
                string dst = Path.Combine(work, file);
                byte[] built = UniqueNames.Build(src, plan);
                File.WriteAllBytes(dst, built);
                draft.BeforeUnique[file] = (src, UniqueNames.Sha(built));   // (Make Unique's undo copy: the package before this rename)
                int k = draft.Packages.FindIndex(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase));
                draft.Packages[k] = (file, dst);
                log.AddRange(plan.Notes.Select(n => $"unique: {file}: {n}"));
                final = dst;
                any = true;
            }
            // the summons kept showing the mod's model (Jean's illusions): their materials and scale fitted to it (0.50.22)
            if (!file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)) continue;
            // (every model of it the hero's own summons share, the ones the mod ships already too: Make Unique leaves those out)
            var kept = SummonLooks.SharedWith(final, file, shared, own);
            if (kept.Count == 0) continue;
            var fixNotes = new List<string>();
            var fixes = SummonLooks.Fix(file, final, StockFiles.For(game.Cooked, file), kept, game.Cooked,
                f => draft.Packages.FirstOrDefault(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase)).Source, draft.Packages.Select(p => p.Source), fixNotes);
            log.AddRange(fixNotes.Select(n => $"summons: {n}"));
            foreach (var (sumFile, bytes) in fixes)
            {
                Directory.CreateDirectory(work);
                string dst = Path.Combine(work, sumFile);
                File.WriteAllBytes(dst, bytes);
                int k = draft.Packages.FindIndex(p => p.File.Equals(sumFile, StringComparison.OrdinalIgnoreCase));
                if (k >= 0) draft.Packages[k] = (sumFile, dst); else draft.Packages.Add((sumFile, dst));
                any = true;
            }
        }
        return any ? work : null;
    }

    /// <summary>Each character package's spare voice components put right (VoiceSet.RepairSpares). The work folder, or null.</summary>
    public static string? RepairVoices(ModDraft draft, ModLibrary lib, List<string> log)
    {
        string? dir = null;
        for (int i = 0; i < draft.Packages.Count; i++)
        {
            var (file, source) = draft.Packages[i];
            if (!(file.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase) || file.StartsWith("UC__MarvelTeamUp_", StringComparison.OrdinalIgnoreCase)) || !File.Exists(source)) continue;
            byte[] was = File.ReadAllBytes(source);
            var said = new List<string>();
            byte[] now = VoiceSet.RepairSpares(was, said);
            if (ReferenceEquals(now, was)) continue;
            dir ??= Path.Combine(lib.DataFolder, "voicefix-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            string outFile = Path.Combine(dir, file);
            File.WriteAllBytes(outFile, now);
            draft.Packages[i] = (file, outFile);
            log.Add($"{file}:"); log.AddRange(said.Select(l => "  " + l));
        }
        return dir;
    }
}
