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
            }
        }).ContinueWith(_ =>
        {
            if (IsDisposed || run != sharedCheck) return;
            // grouped or filtered by overlap: the list is made again now that it's known
            BeginInvoke(() => { if (settings.ListGroup == "overlap" || filter.Text.Contains("is:overlap", StringComparison.OrdinalIgnoreCase)) FillList(Selected?.FolderName); else list.Invalidate(); });
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
    async Task<bool> MakeUnique(Mod m)
    {
        if (readOnly || lib == null || game == null) return false;
        string? why = UniqueNames.Reason(m);
        if (why == null) { Dialog.Show(this, $"\"{m.Name}\" doesn't change a model that another stock package also carries.", "Nothing to Make Unique"); return false; }
        if (Dialog.Show(this, why + $"\n\nMake Unique gives \"{m.Name}\" paths of its own for what it changed, so no stock copy can take its place. Only this mod's own package files change (the folder is saved like an edit; the old one goes to the Recycle Bin). Streamed textures keep their stock path. The mod stays installable in MHModManager.\n\nIt's highly recommended to make it unique, and/or to let the mod's creator know so they can update it officially. Shall I proceed?",
                "Make Unique", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
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
