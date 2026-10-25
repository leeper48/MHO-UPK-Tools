namespace MhoExtendedModManager.Gui;

/// <summary>
/// The main window's Make Unique (Kurt, 2026-10-11; users: a modded Jessica Jones team-up and Scream on Carnage showed the
/// stock model near other players): a mod whose changed model another stock package also carries gets a yellow "OVERLAP"
/// warning pill (checked in the background after each load); the pill or the card's Make Unique gives the mod's copy paths of its
/// own (UniqueNames), saved like an editor save. Apply then puts it in the game.
/// </summary>
sealed partial class MainForm
{
    int sharedCheck;

    /// <summary>Works out which mods are exposed (the mods' and the stock character packages are read once; cached per mod).</summary>
    void CheckShared()
    {
        if (lib == null || game == null) return;
        var mods = lib.Mods.ToList();
        string cooked = game.Cooked, root = game.Root;
        int run = ++sharedCheck;
        Task.Run(() =>
        {
            foreach (var m in mods)
            {
                if (run != sharedCheck) return;
                try { UniqueNames.Plans(m, cooked); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                try { PowerUsage.Check(m, root); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                try { CrashCheck.Check(m, cooked); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        }).ContinueWith(_ =>
        {
            if (IsDisposed || run != sharedCheck) return;
            // grouped or filtered by overlap: the list is made again now that it's known
            BeginInvoke(() => { if (settings.ListGroup is "overlap" or "crash" || filter.Text.Contains("is:overlap", StringComparison.OrdinalIgnoreCase) || filter.Text.Contains("is:crash", StringComparison.OrdinalIgnoreCase)) FillList(Selected?.FolderName); else list.Invalidate(); });
        });
    }

    /// <summary>The OVERLAP pill's reasons: a shared model (Make Unique) and / or enemy-only power packages.</summary>
    static string? OverlapReason(Mod m) => (UniqueNames.Reason(m), PowerUsage.Reason(m)) switch
    {
        (null, null) => null,
        (string a, null) => a,
        (null, string b) => b,
        (string a, string b) => a + "\n\n" + b,
    };

    /// <summary>
    /// The CRASH RISK pill's click (Kurt, 2026-10-12: added bones for a loincloth crashed on one hero and not another): every
    /// finding of CrashCheck, and Make Unique when another copy of a model is what makes it a risk (the mod's copy then gets a
    /// path of its own, and its own animation table).
    /// </summary>
    async void CrashClicked(Mod m)
    {
        if (CrashDialog(m, readOnly) is not var (text, labels)) return;
        int pick = Dialog.Choose(this, text, "Crash Risk", [.. labels]);
        string? chosen = pick >= 0 && pick < labels.Count ? labels[pick] : null;
        if (chosen == "Fix Mirror Tables") FixMirrorTables(m);
        else if (chosen == "Make Unique") await MakeUnique(m);
    }

    /// <summary>The CRASH RISK window's text and buttons (the findings, then what can be done), or null with no findings.</summary>
    internal static (string Text, List<string> Labels)? CrashDialog(Mod m, bool readOnly)
    {
        if (CrashCheck.Details(m) is not string details) return null;
        bool unique = !readOnly && UniqueNames.Reason(m, evenKept: true) != null;
        bool mirror = !readOnly && CrashCheck.HasMirrorIssue(m);
        var advice = new List<string>();
        if (mirror) advice.Add("  • Fix Mirror Tables: Gives every bone of these models a mirror entry (an added left bone mirrors its right partner, others themselves).");
        if (unique) advice.Add("  • Make Unique: Gives this mod's models paths of their own, so no other copy shares their animation table."
            + (m.Manifest.KeepShared == true ? " Its author keeps its paths shared on purpose (Keep Shared Paths): After Make Unique, characters that shared them (a summoner's copy of an agent, a team-up) show the game's look instead of this mod's." : ""));
        if (advice.Count == 0) advice.Add("Let the mod's creator know: Fewer added bones, or a model path of its own, avoids it.");
        else advice.Add("\nOnly this mod's own package files change (saved like an edit; the old folder goes to the Recycle Bin). It's a good idea to let the mod's creator know too, so they can update it officially.");
        var labels = new List<string>();
        if (mirror) labels.Add("Fix Mirror Tables");
        if (unique) labels.Add("Make Unique");
        labels.Add("Close");
        return (details + "\n\nWHAT YOU CAN DO\n" + string.Join("\n", advice), labels);
    }

    /// <summary>The mod's mirror tables made one entry per bone (MirrorTables), saved like an edit.</summary>
    async void FixMirrorTables(Mod m)
    {
        if (readOnly || lib == null) return;
        var l = lib;
        var log = new List<string>();
        string? error = null;
        UseWaitCursor = true;
        status.Text = $"Fixing the Mirror Tables of \"{m.Name}\"…";
        var done = await Task.Run(() => MirrorTables.Apply(l, m, log, out error));
        UseWaitCursor = false;
        if (done == null) { Dialog.Show(this, error ?? "The mirror tables couldn't be fixed.", "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        note = $"Mirror tables fixed in \"{m.Name}\" ({string.Join(", ", done)}){(m.Enabled ? ": Apply Changes puts them in the game" : "")}";
        Reload();
        SelectMod(m.FolderName);
    }

    /// <summary>The SHARED pill's reason: the mod keeps its paths shared on purpose (Keep Shared Paths) and other files carry
    /// them; null otherwise.</summary>
    static string? KeptReason(Mod m) => m.Manifest.KeepShared == true && UniqueNames.Reason(m, evenKept: true) is string why
        ? "Shared on purpose (Keep Shared Paths). " + why : null;

    /// <summary>The SHARED pill's click: what sharing means for this mod, and Make Unique after all.</summary>
    async void KeptClicked(Mod m)
    {
        if (KeptReason(m) is not string why) return;
        var (lose, _) = UniqueNames.Impact(m);
        string text = $"\"{m.Name}\" keeps its paths shared with other game files on purpose: Keep Shared Paths is ticked (in the Editor, or by Undo Make Unique)."
            + (lose.Count > 0 ? "\n\nSHARED WITH\n" + string.Join("\n", lose.Take(10).Select(x => $"  • {x}")) + (lose.Count > 10 ? $"\n  • And {lose.Count - 10} more" : "") : "")
            + "\n\nWHAT THAT MEANS"
            + "\n  • Those characters can show this mod's look."
            + "\n  • But the game shows whichever copy loads first: Near other players or in hubs, the game's look can show instead of this mod's."
            + "\n\nMake Unique gives the mod paths of its own instead (Undo Make Unique can put them back).";
        int pick = Dialog.Choose(this, text, "Shared Paths", readOnly ? new[] { "Close" } : new[] { "Make Unique", "Keep Shared" });
        if (readOnly || pick != 0) return;
        await MakeUnique(m);
    }

    /// <summary>The pill's click: Make Unique for a shared model, then the enemy-only power packages.</summary>
    async void OverlapClicked(Mod m)
    {
        if (UniqueNames.Reason(m) != null && !await MakeUnique(m)) return;
        if (lib?.Mods.FirstOrDefault(x => x.FolderName.Equals(m.FolderName, StringComparison.OrdinalIgnoreCase)) is { } now && PowerUsage.Cached(now).Count > 0) RemoveEnemyPowers(now);
    }

    /// <summary>Removes the mod's enemy-only power packages, in place or in a copy (the original kept, to switch back).</summary>
    async void RemoveEnemyPowers(Mod m)
    {
        if (readOnly || lib == null) return;
        var uses = PowerUsage.Cached(m);
        if (uses.Count == 0) return;
        int pick = Dialog.Choose(this, PowerUsage.Reason(m) + "\n\nTake " + (uses.Count == 1 ? "it" : "them") + " out of the mod? Then Apply Changes puts the game's own back, and those enemies look as they should again. Remove in a Copy keeps this mod as it is, so you can switch back.",
            "Enemy Power", "Remove from This Mod", "Remove in a Copy", "Leave It");
        if (pick is not (0 or 1)) return;
        var l = lib;
        string? copy = pick == 1 ? $"{m.Name} (No Enemy Powers)" : null;
        var files = uses.Select(u => u.File).ToList();
        string? error = null;
        UseWaitCursor = true;
        string? saved = await Task.Run(() => ModPackages.Remove(l, m, files, copy, out error));
        UseWaitCursor = false;
        if (saved == null) { Dialog.Show(this, error ?? "The packages couldn't be taken out.", "Not Changed", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        note = copy != null ? $"\"{copy}\" made without {string.Join(", ", files)} (turn it on and \"{m.Name}\" off, then Apply)" : $"{string.Join(", ", files)} taken out of \"{m.Name}\"{(m.Enabled ? ": Apply Changes puts the game's own back" : "")}";
        Reload();
        SelectMod(saved);
    }

    /// <summary>Make Unique, asked first; true when it was made unique.</summary>
    /// <summary>Undo Make Unique (Kurt, 2026-10-14): the packages back to their shared paths, after saying what that changes.</summary>
    void UndoUnique(Mod m)
    {
        if (readOnly || lib == null) return;
        var files = UniqueNames.Undoable(m, out var changed);
        if (files.Count == 0)
        {
            Dialog.Show(this, $"\"{m.Name}\" was changed after Make Unique (a Model build, a power color or an update), so its copy from before can't be put back without losing that:\n\n" + string.Join("\n", changed.Select(f => $"  • {f}"))
                + "\n\nTo share the paths again: Reinstall the mod's original file, then tick Keep Shared Paths in the Editor before saving.", "Can't Undo Make Unique", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string text = $"Put \"{m.Name}\" back as it was before Make Unique?\n\nGOES BACK\n" + string.Join("\n", files.Select(f => $"  • {f.File}"))
            + (changed.Count > 0 ? "\n\nSTAYS AS IT IS (changed since by a Model build, a color or an update)\n" + string.Join("\n", changed.Select(f => $"  • {f}")) : "")
            + "\n\nWHAT IT CHANGES"
            + "\n  • The mod's models share their paths with the game's other copies again."
            + "\n  • Characters that carry the same model in other files (illusions, afterimages, summons, team-ups, NPCs) can show the mod's look."
            + "\n  • But the game shows whichever copy loads first: Near other players or in hubs, the game's look can show instead of the mod's."
            + "\n  • If the mod added bones (a cape, a dress, wings), sharing with copies that have fewer bones can crash the game: Its CRASH RISK warning comes back if so."
            + "\n\nKeep Shared Paths gets ticked, so saving in the Editor won't rename them again, and the card shows a SHARED pill. Then Apply Changes.";
        if (Dialog.Show(this, text, "Undo Make Unique", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var done = UniqueNames.Undo(lib, m, out string? error);
        if (done == null) { Dialog.Show(this, error ?? "It couldn't be undone.", "Not Undone", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        note = $"\"{m.Name}\": Make Unique undone ({done.Count} package(s)){(m.Enabled ? ": Apply Changes puts it in the game" : "")}";
        string folder = m.FolderName;
        Reload();
        SelectMod(folder);
    }

    /// <summary>The Make Unique window's text: what's shared, what changes, how it works, when to choose it (Kurt,
    /// 2026-10-14: sections and bullets, not one block).</summary>
    internal static string MakeUniqueText(Mod m)
    {
        var sb = new System.Text.StringBuilder();
        string List(List<string> l, int n) => string.Join(", ", l.Take(n)) + (l.Count > n ? $" and {l.Count - n} more" : "");
        sb.AppendLine($"\"{m.Name}\" changes models that other game files also carry. The game shows whichever copy loads first, so near other players, summons, team-ups or hub NPCs, the game's look can show instead of this mod's.");
        if (m.Manifest.KeepShared == true)
        {
            sb.AppendLine();
            sb.AppendLine("Note: This mod shares these paths on purpose (Keep Shared Paths is ticked).");
        }
        var items = UniqueNames.Items(m);
        if (items.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SHARED WITH OTHER FILES");
            foreach (var (path, others) in items) sb.AppendLine($"  • {path}: Also in {List(others, 4)}");
        }
        var (lose, keep) = UniqueNames.Impact(m);
        sb.AppendLine();
        sb.AppendLine("WHAT IT CHANGES");
        if (lose.Count > 0) sb.AppendLine($"  • These show the game's look afterwards: {List(lose, 6)}");
        if (keep.Count > 0) sb.AppendLine($"  • These stay shared and keep this mod's look (the hero's own illusions, afterimages and summons): {List(keep, 6)}");
        sb.AppendLine("  • Only this mod's own package files change. The folder is saved like an edit (the old one goes to the Recycle Bin), and streamed textures keep their path. The mod still installs in MHModManager.");
        sb.AppendLine("  • Right-click → Undo Make Unique puts it back.");
        sb.AppendLine();
        sb.AppendLine("WHICH TO CHOOSE");
        sb.AppendLine("  • Make it unique: When this mod's look should stay its own near other players and NPCs.");
        sb.AppendLine("  • Leave it shared (Keep Shared Paths in the Editor): When its look should reach the characters above.");
        sb.AppendLine();
        sb.AppendLine("Either way, it's a good idea to let the mod's creator know. Make it unique now?");
        return sb.ToString().TrimEnd();
    }

    async Task<bool> MakeUnique(Mod m)
    {
        if (readOnly || lib == null || game == null) return false;
        string? why = UniqueNames.Reason(m, evenKept: true);
        if (why == null) { Dialog.Show(this, $"\"{m.Name}\" doesn't change a model that another stock package also carries.", "Nothing to Make Unique"); return false; }
        if (Dialog.Show(this, MakeUniqueText(m), "Make Unique", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        var l = lib; string cooked = game.Cooked;
        UseWaitCursor = true;
        status.Text = $"Making \"{m.Name}\" Unique…";
        var log = new List<string>();
        string? error = null;
        var done = await Task.Run(() => UniqueNames.Apply(l, m, cooked, log, out error));
        UseWaitCursor = false;
        if (done == null) { Dialog.Show(this, error ?? "It couldn't be made unique.", "Not Made Unique", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        note = $"\"{m.Name}\" made unique ({done.Count} package(s)){(m.Enabled ? ": Apply Changes puts it in the game" : "")}";
        string folder = m.FolderName;
        Reload();
        SelectMod(folder);
        return true;
    }
}
