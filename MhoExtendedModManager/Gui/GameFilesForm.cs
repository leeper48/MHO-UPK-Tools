using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Changed Game Files (Kurt, 2026-10-01): the game packages changed by something this app doesn't manage
/// (ApplyCheck.Unexpected). Select rows, then Keep as a Mod (one new mod holding the selected files, turned on) or
/// Restore to Original (the clean originals written back, as Apply would). Anything not acted on stays as it is.
/// </summary>
sealed class GameFilesForm : Form
{
    readonly DataGridView grid;
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" };
    readonly Button keep, restore;
    ModLibrary lib;
    readonly GameState game;
    /// <summary>Something was kept or restored: the caller reloads the library.</summary>
    public bool Changed { get; private set; }

    public GameFilesForm(ModLibrary lib, GameState game)
    {
        this.lib = lib; this.game = game;
        Text = "Changed Game Files";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = DeviceDpi / 96f;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Changed Game Files", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        t.Controls.Add(new Label
        {
            Text = "Game files changed by something other than this app (another tool, a mod that was deleted, a hand edit). Select one or more, then " +
                   "Keep as a Mod (they go into one new mod in your list, turned on, so you can turn them off later) or Restore to Original (the game's own " +
                   "files go back). Files you don't act on stay as they are.",
            AutoSize = true, Dock = DockStyle.Fill, Tag = "subtle", Margin = new Padding(0, 0, 0, 8),
        }, 0, 1);
        grid = Ui.Grid(s, false, ("File", 0), ("Changed", 150), ("Size", 100), (".bak", 60), ("Clean Original", 120));
        grid.Columns[0].Name = "file";
        grid.SelectionChanged += (_, _) => Count();
        t.Controls.Add(grid, 0, 2);

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(status, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        keep = Ui.AccentButton("Keep as a Mod", Keep, tip: "Copy the selected files into one new mod in your list (turned on, lowest priority). Nothing in the game changes.");
        restore = Ui.FlatButton("Restore to Original", Restore, tip: "Put the game's own files back for the selected files (from the clean originals; a .bak is made first if there's none). The game must be closed.");
        buttons.Controls.AddRange([restore, keep, Ui.FlatButton("Close", () => { DialogResult = DialogResult.OK; }, tip: "Close; files you didn't act on stay as they are (Esc).")]);
        bar.Controls.Add(buttons, 1, 0);
        t.Controls.Add(bar, 0, 3);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.OK; };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1000, 640);
        Fill();
    }

    void Fill()
    {
        grid.Rows.Clear();
        foreach (var (f, clean) in ApplyCheck.Unexpected(lib, game, new Originals(lib.DataFolder, game)))
        {
            var fi = new FileInfo(Path.Combine(game.Cooked, f));
            int i = grid.Rows.Add(f, $"{fi.LastWriteTime:yyyy-MM-dd HH:mm}", $"{fi.Length / 1048576.0:0.0} MB", File.Exists(fi.FullName + ".bak") ? "Yes" : "", clean ? "Yes" : "None Found");
            if (!clean) grid.Rows[i].Cells[4].Style.ForeColor = Ui.Warn;
        }
        grid.ClearSelection();
        Count();
    }

    List<string> Selected() => grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (string)r.Cells["file"].Value).ToList();

    void Count()
    {
        int n = Selected().Count;
        status.Text = Ui.TitleCase($"{grid.Rows.Count} changed file(s) · {n} selected");
        keep.Enabled = restore.Enabled = n > 0;
    }

    void Keep()
    {
        var files = Selected();
        string suggest = files.Count == 1 ? Path.GetFileNameWithoutExtension(files[0]) + " (Captured)" : $"Captured game files {DateTime.Now:yyyy-MM-dd}";
        if (Ui.Prompt(this, "Keep as a Mod", $"Name of the new mod for {files.Count} file(s):", suggest) is not string name) return;
        string? why = GameFiles.KeepAsMod(lib, game, files, name.Trim());
        if (why != null) { Dialog.Show(this, why, "Not Kept", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Changed = true;
        lib = ModLibrary.Load(lib.DataFolder);
        Fill();
        status.Text = Ui.TitleCase($"Kept {files.Count} file(s) as \"{name.Trim()}\" (turned on)");
    }

    async void Restore()
    {
        var files = Selected();
        if (System.Diagnostics.Process.GetProcessesByName("MarvelHeroesOmega").Length > 0) { Dialog.Show(this, "Close the game first.", "The Game Is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (Dialog.Show(this, $"Put the game's own files back for {files.Count} file(s)? What's in them now is replaced (it stays in the undo history). Files without a .bak get one from the original first.",
                "Restore to Original", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        UseWaitCursor = true; Enabled = false;
        bool ok = false;
        var (g, l) = (game, lib);
        string log = await Task.Run(() => MainForm.CaptureOutput(() => ok = GameFiles.Restore(g, new Originals(l.DataFolder, g), l.DataFolder, files)));
        UseWaitCursor = false; Enabled = true;
        Changed = true;
        Fill();
        if (!ok) Dialog.ShowLog(this, "Not All Restored", log);
        else status.Text = Ui.TitleCase($"Restored {files.Count} file(s) to the game's originals");
    }
}
