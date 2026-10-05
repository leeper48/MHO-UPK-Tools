using System.Diagnostics;
using MhoPackageModifier.Gui;
using Microsoft.VisualBasic.FileIO;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Settings ▾ → Model → Clean Up Exports and Rigs (Kurt, 2026-10-04): the Model tab's folders outside the mods, with sizes:
/// data\model\fbx (Export FBX / Open in Blender) and data\model\rigs (models without an armature, rigged in Blender). Ticked
/// ones go to the Recycle Bin. Nothing a mod holds is listed: a mod keeps its own copy of what it needs (its Model folder).
/// </summary>
sealed class ModelCleanUpForm : Form
{
    readonly DataGridView grid;
    readonly Label summary = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8) };

    /// <summary>The folders listed: (kind, root).</summary>
    static IEnumerable<(string Kind, string Root)> Roots() =>
        [("Export", Path.Combine(MhoExtendedModManager.Model.Settings.Home, "fbx")), ("Rig", Path.Combine(MhoExtendedModManager.Model.Settings.Home, "rigs"))];

    public ModelCleanUpForm()
    {
        Text = "Clean Up Exports and Rigs";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = DeviceDpi / 96f;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Clean Up Exports and Rigs", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        t.Controls.Add(summary, 0, 1);
        grid = Ui.Grid(s, false, ("Kind", 70), ("Name", 0), ("Size", 80), ("Last Changed", 130));
        grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "use", HeaderText = "", Width = (int)(32 * s) });
        grid.ReadOnly = false;
        foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name != "use";
        grid.Columns[3].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, _) => UpdateSummary();
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenRow(e.RowIndex); };
        Ui.StyleGrid(grid);
        t.Controls.Add(grid, 0, 2);
        var note = new Label
        {
            Text = "Exports are the folders Export FBX and Open in Blender write. Rigs are models without an armature, rigged in Blender for a base hero; a mod built from one keeps its own copy, so deleting the rig here loses only work never built into a mod. A rig still open in Blender can't send its next Ctrl+S once its folder is gone. Deleted folders go to the Recycle Bin.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 0, 0),
        };
        t.Controls.Add(note, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Tick All", () => { foreach (DataGridViewRow r in grid.Rows) r.Cells[0].Value = true; UpdateSummary(); }, tip: "Tick every folder in the list."),
            Ui.FlatButton("Open Folder", () => { if (grid.CurrentCell != null) OpenRow(grid.CurrentCell.RowIndex); }, tip: "Show the selected folder in Explorer (or double-click a row)."),
            Ui.FlatButton("Delete Ticked", DeleteTicked, tip: "Send the ticked folders to the Recycle Bin (asks first)."),
            Ui.AccentButton("Close", Close, tip: "Close this window (Esc)."),
        ]);
        t.Controls.Add(buttons, 0, 4);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 760, 520);
        void Wrap() => note.MaximumSize = summary.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        Resize += (_, _) => Wrap();
        Wrap();
        Fill();
    }

    static long SizeOf(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    static string Mb(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    void Fill()
    {
        grid.Rows.Clear();
        foreach (var (kind, root) in Roots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.GetDirectories(root).OrderByDescending(Directory.GetLastWriteTime))
            {
                long size = SizeOf(d);
                var last = Directory.EnumerateFiles(d, "*", System.IO.SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty(Directory.GetLastWriteTime(d)).Max();
                int i = grid.Rows.Add(false, kind, Path.GetFileName(d), Mb(size), last.ToString("yyyy-MM-dd HH:mm"));
                grid.Rows[i].Tag = (d, size);
            }
        }
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var rows = grid.Rows.Cast<DataGridViewRow>().ToList();
        long all = rows.Sum(r => ((string, long))r.Tag! is var (_, n) ? n : 0);
        var ticked = rows.Where(r => r.Cells[0].Value is true).ToList();
        long sel = ticked.Sum(r => ((string, long))r.Tag! is var (_, n) ? n : 0);
        summary.Text = rows.Count == 0 ? "Nothing to clean up: no exports or rigs in data\\model."
            : $"{rows.Count} folder(s), {Mb(all)} in data\\model{(ticked.Count > 0 ? $" · {ticked.Count} ticked, {Mb(sel)}" : "")}.";
    }

    void OpenRow(int row)
    {
        if (row < 0 || row >= grid.Rows.Count || grid.Rows[row].Tag is not (string dir, long _) || !Directory.Exists(dir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    void DeleteTicked()
    {
        var ticked = grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => ((string Dir, long Size))r.Tag!).ToList();
        if (ticked.Count == 0) { Dialog.Show(this, "Tick the folders to delete first.", "Nothing Ticked"); return; }
        if (Dialog.Show(this, $"Send {ticked.Count} folder(s), {Mb(ticked.Sum(x => x.Size))}, to the Recycle Bin?\n\n" + string.Join("\n", ticked.Take(12).Select(x => Path.GetFileName(x.Dir))) + (ticked.Count > 12 ? "\n…" : ""),
                "Delete Ticked Folders", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        var roots = Roots().Select(r => Path.GetFullPath(r.Root) + Path.DirectorySeparatorChar).ToList();
        var failed = new List<string>();
        foreach (var (dir, _) in ticked)
        {
            // only folders directly in the two roots (never anything else)
            string full = Path.GetFullPath(dir);
            if (!roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && !full[r.Length..].Contains(Path.DirectorySeparatorChar))) continue;
            try { FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { failed.Add($"{Path.GetFileName(dir)}: {ex.Message}"); }
        }
        Fill();
        if (failed.Count > 0) Dialog.Show(this, "Some folders weren't deleted:\n\n" + string.Join("\n", failed), "Not Deleted", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
