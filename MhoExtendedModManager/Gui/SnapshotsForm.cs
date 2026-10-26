using System.Diagnostics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Settings → Snapshots (Kurt, 2026-10-26): the saved snapshots (taken at launch, before each Apply and by hand: Snapshots),
/// newest first; take one, rename, delete, compare one with now or two with each other (the audit report), restore one
/// (state, and the mods' files from the discarded copies, after a preview), make or restore a bundle (the mods themselves
/// with a snapshot, one .zip), and the discarded copies' keep time and cleanup (Discarded).
/// </summary>
sealed class SnapshotsForm : Form
{
    readonly Func<(ModLibrary? Lib, GameState? Game)> current;
    readonly Action<Snapshots.Snapshot, string> restore;
    readonly Action reload;
    readonly Settings settings;
    readonly DataGridView grid;
    readonly Label summary = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8) };
    readonly DropDown bundleScope = new() { Width = 300 };
    readonly DropDown keepDays = new() { Width = 340 };
    readonly Label discardInfo = new() { AutoSize = true, Font = Ui.Bold(9.5f), Margin = new Padding(0, 9, 10, 0) };
    List<Snapshots.Snapshot> shown = [];

    static readonly string[] Scopes = ["ask", "all", "on"];
    static readonly int[] Days = [7, 30, 90, 0];

    /// <param name="restore">Puts a snapshot's state back on the library (one undo step) with a label.</param>
    public SnapshotsForm(Settings settings, Func<(ModLibrary? Lib, GameState? Game)> current, Action<Snapshots.Snapshot, string> restore, Action reload)
    {
        this.settings = settings; this.current = current; this.restore = restore; this.reload = reload;
        Text = "Snapshots";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        foreach (var _ in Enumerable.Range(0, 2)) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var _ in Enumerable.Range(0, 3)) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Snapshots", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        t.Controls.Add(summary, 0, 1);
        grid = Ui.Grid(s, false, ("Taken", 165), ("Kind", 120), ("Name", 0), ("Mods", 110), ("Size", 80));
        Ui.StyleGrid(grid);
        grid.MultiSelect = true;
        grid.Columns[3].DefaultCellStyle.Alignment = grid.Columns[4].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) CompareWithNow(); };
        t.Controls.Add(grid, 0, 2);

        var snapButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        // icons (Kurt, 2026-10-26): Restore beside Take Snapshot, the names head the tooltips
        Button IconOf(Button b, string name, Action<Graphics, RectangleF, Pen, Brush> paint) { Icons.Make(b, name, paint, s); b.Margin = new Padding(0, 0, 6, 0); return b; }
        Control Gap() => new Panel { Width = (int)(14 * s), Height = 1, Margin = new Padding(0) };
        snapButtons.Controls.AddRange([
            IconOf(Ui.AccentButton("Take Snapshot", TakeNamed, tip: "Save the library's state now, under a name of your own (kept until you delete it)."), "Take Snapshot", Icons.Camera),
            IconOf(Ui.FlatButton("Restore", Restore, tip: "Put the picked snapshot back: which mods are on, their order, locks, tags and notes, and the mods' files as they were then (from the discarded copies: undoes Make Unique, Fix Mirror Tables, edits, updates and removals since). It shows everything first. Then Apply Changes."), "Restore", Icons.History),
            Gap(),
            IconOf(Ui.FlatButton("Compare with Now", CompareWithNow, tip: "What changed from the picked snapshot to now: mods installed, removed, turned on or off, moved, updated, made unique, Crash Risk fixes and warnings, game files Apply wrote. Double-click a row does the same."), "Compare with Now", Icons.Split),
            IconOf(Ui.FlatButton("Compare Two", CompareTwo, tip: "Pick two snapshots (Ctrl+click) and see what changed from the older to the newer."), "Compare Two", Icons.Overlay),
            Gap(),
            IconOf(Ui.FlatButton("Rename", RenamePicked, tip: "Give the picked snapshot a name."), "Rename", Icons.Pencil),
            IconOf(Ui.FlatButton("Delete", DeletePicked, tip: "Delete the picked snapshots."), "Delete", Icons.Trash),
            IconOf(Ui.FlatButton("Open Folder", () => { Directory.CreateDirectory(Snapshots.Folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Snapshots.Folder}\"") { UseShellExecute = false }); }, tip: "Open the snapshots folder (data\\snapshots)."), "Open Folder", Icons.Folder),
        ]);
        t.Controls.Add(snapButtons, 0, 3);

        var bundleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        bundleScope.Items.AddRange(["Bundle: Ask Each Time", "Bundle: All Mods", "Bundle: Turned-On Mods"]);
        bundleScope.SelectedIndex = Math.Max(0, Array.IndexOf(Scopes, settings.BundleScope));
        bundleScope.SelectedIndexChanged += (_, _) => { settings.BundleScope = Scopes[Math.Max(0, bundleScope.SelectedIndex)]; settings.Save(); };
        Ui.Tip(bundleScope, "What Make Bundle puts in the bundle: ask each time, every installed mod, or only the turned-on ones.");
        bundleScope.Margin = new Padding(0, 4, 8, 0);
        bundleRow.Controls.AddRange([
            new Label { Text = "Bundles", AutoSize = true, Font = Ui.Bold(9.5f), Margin = new Padding(0, 9, 10, 0) },
            IconOf(Ui.FlatButton("Make Bundle", () => _ = MakeBundle(), tip: "Save the mods themselves (every file) with a snapshot of their state, in one .zip: to go back to a clean set later, or to move to another PC. Big: the size of the mods."), "Make Bundle", Icons.Export),
            IconOf(Ui.FlatButton("Restore Bundle", () => _ = RestoreBundle(), tip: "Install a bundle's mods (the ones the library lacks, and if you say so the ones that differ) and put its state back."), "Restore Bundle", Icons.Install),
            bundleScope,
        ]);
        t.Controls.Add(bundleRow, 0, 4);

        // the discarded copies (Discarded): how long they're kept, cleaned up by hand
        var discardRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        keepDays.Items.AddRange(["Keep Discarded: 7 Days", "Keep Discarded: 30 Days", "Keep Discarded: 90 Days", "Keep Discarded: Until Cleaned Up"]);
        keepDays.SelectedIndex = Array.IndexOf(Days, settings.DiscardDays) is int k && k >= 0 ? k : 1;
        keepDays.SelectedIndexChanged += (_, _) => { settings.DiscardDays = Days[Math.Max(0, keepDays.SelectedIndex)]; settings.Save(); };
        Ui.Tip(keepDays, "How long a replaced or removed mod's old files are kept (they're cleaned at a start after that). A snapshot restore brings mods' files back from them. Copies a snapshot still needs are only deleted after asking.");
        keepDays.Margin = new Padding(0, 4, 8, 0);
        Ui.Tip(discardInfo, "When a mod is edited, made unique, fixed, updated or removed, its old files are moved here instead of the Recycle Bin (data\\library\\discarded).");
        discardRow.Controls.AddRange([
            discardInfo, keepDays,
            IconOf(Ui.FlatButton("Clean Up Discarded", CleanUpDiscarded, tip: "Delete the discarded copies now. It names the snapshots that couldn't bring their mods' files back afterwards, and can keep those."), "Clean Up Discarded", Icons.Clear),
            IconOf(Ui.FlatButton("Open Discarded", () => { var (l, _) = current(); if (l == null) return; string r = Discarded.Root(l.DataFolder); Directory.CreateDirectory(r); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r}\"") { UseShellExecute = false }); }, tip: "Open the discarded copies' folder (data\\library\\discarded)."), "Open Discarded", Icons.Folder),
        ]);
        var closeButton = Ui.FlatButton("Close", Close, tip: "Close this window (Esc).");
        closeButton.Margin = new Padding(24, 0, 0, 0);
        discardRow.Controls.Add(closeButton);
        t.Controls.Add(discardRow, 0, 5);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 900, 620);
        void Wrap() => summary.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        Resize += (_, _) => Wrap();
        Wrap();
        Shown += (_, _) => Fill();
    }

    void Fill()
    {
        shown = Snapshots.List();
        grid.Rows.Clear();
        foreach (var x in shown)
        {
            string kind = x.Kind switch { "launch" => "Launch", "apply" => "Before Apply", "bundle" => "Bundle", _ => "Taken by Hand" };
            long size = File.Exists(x.File) ? new FileInfo(x.File).Length : 0;
            grid.Rows.Add(x.Taken.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), kind, x.Name, $"{x.Mods.Count(m => m.Enabled)} of {x.Mods.Count} on", $"{Math.Max(1, size / 1024):N0} KB");
        }
        grid.ClearSelection();
        long total = shown.Sum(x => File.Exists(x.File) ? new FileInfo(x.File).Length : 0);
        summary.Text = $"{shown.Count} snapshots, {Math.Max(1, total / 1024):N0} KB in all. One is taken at every launch and before every Apply (the last 30 of those are kept); the ones you take are kept until you delete them.";
        var (lib, _) = current();
        var copies = lib != null ? Discarded.List(lib.DataFolder) : [];
        discardInfo.Text = $"Discarded Copies: {copies.Count} ({copies.Sum(c => c.Bytes) / 1048576.0:N0} MB)";
    }

    List<Snapshots.Snapshot> Picked() => grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < shown.Count).OrderBy(i => i).Select(i => shown[i]).ToList();

    Snapshots.Snapshot? One()
    {
        var p = Picked();
        if (p.Count == 1) return p[0];
        Dialog.Show(this, p.Count == 0 ? "Pick a snapshot first." : "Pick just one snapshot.", "Snapshots");
        return null;
    }

    void TakeNamed()
    {
        var (lib, game) = current();
        if (lib == null) return;
        if (Ui.Prompt(this, "Take Snapshot", "A name for it (what you're about to try, say):", $"Snapshot {DateTime.Now:yyyy-MM-dd HH:mm}") is not string name) return;
        UseWaitCursor = true;
        Snapshots.Take(lib, game, "manual", name.Trim());
        UseWaitCursor = false;
        Fill();
    }

    void CompareWithNow()
    {
        var (lib, game) = current();
        if (lib == null || One() is not { } a) return;
        Dialog.ShowLog(this, "Changes Since " + Snapshots.Label(a), Snapshots.Compare(a, Snapshots.Now(lib, game)));
    }

    void CompareTwo()
    {
        var p = Picked();
        if (p.Count != 2) { Dialog.Show(this, "Pick two snapshots (Ctrl+click the second).", "Compare Two"); return; }
        var (older, newer) = p[0].Taken <= p[1].Taken ? (p[0], p[1]) : (p[1], p[0]);
        Dialog.ShowLog(this, "Changes Between Two Snapshots", Snapshots.Compare(older, newer));
    }

    async void Restore()
    {
        var (lib, game) = current();
        if (lib == null || One() is not { } snap) return;
        var now = Snapshots.Now(lib, game);
        var plan = Snapshots.PlanFiles(lib, snap);
        string text = "Restoring puts back which mods are on, their order, locks, tags and notes as they were" + (plan.Count > 0 ? ", and the mods' files listed below" : "") + ". Nothing in the game changes until Apply Changes.\n\n"
            + (plan.Count > 0 ? "FILES PUT BACK (the mods' files now are kept with the discarded copies)\n" + string.Join("\n", plan.Lines().Select(c => "  • " + c)) + "\n\n" : "")
            + "WHAT GOES BACK (from now to then)\n" + Snapshots.Compare(now, snap).Replace("From: ", "From now: ").Replace("To: ", "Back to: ")
            + (plan.Cant.Count > 0 ? "\n\nCAN'T BE PUT BACK\n" + string.Join("\n", plan.Cant.Select(c => "  • " + c)) + "\n  • A bundle or a reinstall brings back those mods' files." : "");
        if (Dialog.Choose(this, text, "Restore Snapshot", "Restore", "Cancel") != 0) return;
        Snapshots.Take(lib, game, "manual", "Before restoring " + Snapshots.Label(snap));
        var log = new List<string>();
        if (plan.Count > 0)
        {
            UseWaitCursor = true; Enabled = false;
            summary.Text = "Putting the mods' files back…";
            try { log = await Task.Run(() => Snapshots.RestoreFiles(lib, plan)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { log.Add("Stopped: " + ex.Message); }
            UseWaitCursor = false; Enabled = true;
            reload();
        }
        restore(snap, "restore the snapshot " + Snapshots.Label(snap));
        Fill();
        string done = "Restored. Apply Changes puts it in the game.\n\n"
            + (plan.Count > 0 ? "Undo takes back the on/off and order; to take back the files too, restore the snapshot \"Before restoring " + Snapshots.Label(snap) + "\"." : "Undo takes the restore back.");
        if (log.Count > 0) Dialog.ShowLog(this, "Snapshot Restored", done + "\n\nFILES\n" + string.Join("\n", log.Select(l => "  • " + l)));
        else Dialog.Show(this, done, "Snapshot Restored");
    }

    void CleanUpDiscarded()
    {
        var (lib, _) = current();
        if (lib == null) return;
        var all = Discarded.List(lib.DataFolder);
        if (all.Count == 0) { Dialog.Show(this, "There are no discarded copies.", "Clean Up Discarded"); return; }
        var needed = Discarded.Needed(lib, all, Snapshots.List());
        string text = $"Delete {all.Count} discarded copies ({all.Sum(c => c.Bytes) / 1048576.0:N0} MB) for good?"
            + (needed.Count > 0 ? "\n\n" + Discarded.Warning(needed) : "\n\nNo saved snapshot needs them.");
        string[] labels = needed.Count > 0 ? ["Delete All", "Keep the Ones Snapshots Need", "Cancel"] : ["Delete", "Cancel"];
        int pick = Dialog.Choose(this, text, "Clean Up Discarded", labels);
        if (pick < 0 || labels[Math.Min(pick, labels.Length - 1)] == "Cancel") return;
        var go = pick == 0 ? all : all.Where(e => !needed.ContainsKey(e)).ToList();
        long freed = Discarded.Delete(go);
        Fill();
        Dialog.Show(this, $"{go.Count} discarded copies deleted ({freed / 1048576.0:N0} MB).", "Cleaned Up");
    }

    void RenamePicked()
    {
        if (One() is not { } snap) return;
        if (Ui.Prompt(this, "Rename Snapshot", "Its name:", snap.Name) is not string name) return;
        Snapshots.Rename(snap, name.Trim());
        Fill();
    }

    void DeletePicked()
    {
        var p = Picked();
        if (p.Count == 0) { Dialog.Show(this, "Pick a snapshot first.", "Snapshots"); return; }
        if (Dialog.Show(this, $"Delete {p.Count} snapshot(s)? They can't be brought back.", "Delete Snapshots", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var x in p) Snapshots.Delete(x);
        Fill();
    }

    async Task MakeBundle()
    {
        var (lib, game) = current();
        if (lib == null) return;
        string scope = settings.BundleScope;
        if (scope == "ask")
        {
            int pick = Dialog.Choose(this, $"What goes in the bundle?\n\n  • All Installed Mods: {lib.Mods.Count} mods.\n  • Only Turned-On Mods: {lib.Mods.Count(m => m.Enabled)} mods.\n\n(The bundles setting in this window can skip this question.)", "Make Bundle",
                "All Installed Mods", "Only Turned-On Mods", "Cancel");
            if (pick is not (0 or 1)) return;
            scope = pick == 0 ? "all" : "on";
        }
        var mods = lib.Mods.Where(m => scope == "all" || m.Enabled).ToList();
        if (mods.Count == 0) { Dialog.Show(this, "There are no mods to put in it.", "Make Bundle"); return; }
        using var d = new SaveFileDialog { Title = "Make Bundle", Filter = "Bundle (*.zip)|*.zip", FileName = $"MEMM Bundle {DateTime.Now:yyyy-MM-dd HH-mm} ({mods.Count} mods).zip" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        long bytes = mods.Sum(m => Snapshots.Fingerprint(m.Folder).Bytes);
        summary.Text = $"Making the bundle: {mods.Count} mods, {bytes / 1048576.0:N0} MB…";
        UseWaitCursor = true; Enabled = false;
        string? error = null; int files = 0;
        try { files = await Task.Run(() => Snapshots.MakeBundle(lib, game, mods, d.FileName, name => BeginInvoke(() => summary.Text = $"Making the bundle: {name}…"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        UseWaitCursor = false; Enabled = true;
        Fill();
        if (error != null) { Dialog.Show(this, error, "Bundle Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        Dialog.Show(this, $"{mods.Count} mods ({files:N0} files, {new FileInfo(d.FileName).Length / 1048576.0:N0} MB) are in {d.FileName}.", "Bundle Made");
    }

    async Task RestoreBundle()
    {
        var (lib, game) = current();
        if (lib == null) return;
        using var d = new OpenFileDialog { Title = "Restore Bundle", Filter = "Bundle (*.zip)|*.zip" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        if (Snapshots.BundleInfo(d.FileName) is not { } b) { Dialog.Show(this, "That file isn't a bundle (it has no bundle.json).", "Not a Bundle", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        var missing = b.Mods.Where(m => !lib.Mods.Any(x => x.FolderName.Equals(m.Folder, StringComparison.OrdinalIgnoreCase))).ToList();
        var differ = b.Mods.Where(m => lib.Mods.FirstOrDefault(x => x.FolderName.Equals(m.Folder, StringComparison.OrdinalIgnoreCase)) is { } have && Snapshots.Fingerprint(have.Folder).Hash != m.Fingerprint).ToList();
        string text = $"The bundle from {b.Taken.ToLocalTime():yyyy-MM-dd HH:mm} has {b.Mods.Count} mods ({b.Mods.Count(m => m.Enabled)} on).\n\n"
            + $"NOT IN YOUR LIBRARY ({missing.Count})\n" + (missing.Count > 0 ? string.Join("\n", missing.Take(15).Select(m => $"  • {m.Name}")) + (missing.Count > 15 ? $"\n  • … and {missing.Count - 15} more" : "") : "  • None") + "\n\n"
            + $"DIFFERENT FROM YOURS ({differ.Count})\n" + (differ.Count > 0 ? string.Join("\n", differ.Take(15).Select(m => $"  • {m.Name}")) + (differ.Count > 15 ? $"\n  • … and {differ.Count - 15} more" : "") : "  • None") + "\n\n"
            + "Then the bundle's state goes back (which mods are on, their order, locks, tags, notes). Replaced mods go to the discarded copies. A snapshot is taken first.";
        var labels = differ.Count > 0 ? new[] { "Install and Replace", "Install Missing Only", "Cancel" } : new[] { "Restore", "Cancel" };
        int pick = Dialog.Choose(this, text, "Restore Bundle", labels);
        if (labels[Math.Clamp(pick, 0, labels.Length - 1)] == "Cancel" || pick < 0) return;
        bool replace = differ.Count > 0 && pick == 0;
        Snapshots.Take(lib, game, "manual", "Before restoring a bundle");
        UseWaitCursor = true; Enabled = false;
        List<string> log; string? error = null;
        try { log = await Task.Run(() => Snapshots.RestoreBundle(lib, d.FileName, replace, name => BeginInvoke(() => summary.Text = $"Restoring: {name}…"))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { log = []; error = ex.Message; }
        UseWaitCursor = false; Enabled = true;
        if (error != null) { Dialog.Show(this, error, "Bundle Not Restored", MessageBoxButtons.OK, MessageBoxIcon.Error); Fill(); return; }
        reload();
        restore(b, "restore the bundle " + Path.GetFileName(d.FileName));
        Fill();
        Dialog.ShowLog(this, "Bundle Restored", string.Join("\n", log) + "\n\nApply Changes puts it in the game.");
    }
}
