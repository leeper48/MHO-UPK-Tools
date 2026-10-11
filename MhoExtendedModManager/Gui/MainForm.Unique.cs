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
        string cooked = game.Cooked;
        int run = ++sharedCheck;
        Task.Run(() =>
        {
            foreach (var m in mods)
            {
                if (run != sharedCheck) return;
                try { UniqueNames.Plans(m, cooked); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        }).ContinueWith(_ => { if (!IsDisposed && run == sharedCheck) BeginInvoke(() => list.Invalidate()); });
    }

    async void MakeUnique(Mod m)
    {
        if (readOnly || lib == null || game == null) return;
        string? why = UniqueNames.Reason(m);
        if (why == null) { Dialog.Show(this, $"\"{m.Name}\" doesn't change a model that another stock package also carries.", "Nothing to Make Unique"); return; }
        if (Dialog.Show(this, why + $"\n\nMake Unique gives \"{m.Name}\" paths of its own for what it changed, so no stock copy can take its place. Only this mod's own package files change (the folder is saved like an edit; the old one goes to the Recycle Bin). Streamed textures keep their stock path. The mod stays installable in MHModManager.\n\nIt's highly recommended to make it unique, and/or to let the mod's creator know so they can update it officially. Shall I proceed?",
                "Make Unique", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var l = lib; string cooked = game.Cooked;
        UseWaitCursor = true;
        status.Text = $"Making \"{m.Name}\" Unique…";
        var log = new List<string>();
        string? error = null;
        var done = await Task.Run(() => UniqueNames.Apply(l, m, cooked, log, out error));
        UseWaitCursor = false;
        if (done == null) { Dialog.Show(this, error ?? "It couldn't be made unique.", "Not Made Unique", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        note = $"\"{m.Name}\" made unique ({done.Count} package(s)){(m.Enabled ? ": Apply Changes puts it in the game" : "")}";
        string folder = m.FolderName;
        Reload();
        SelectMod(folder);
    }
}
