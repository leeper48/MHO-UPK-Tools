using System.Diagnostics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Which file on a Nexus page a mod is, when the page has several side by side (a variant; a user's Rogue #300, 2026-09-28):
/// one row per file name (its newest version, upload date and description). The choice becomes NexusLink.File, and updates
/// then come only from files of that name.
/// </summary>
sealed class NexusFileForm : Form
{
    readonly DataGridView grid;
    public Nexus.NexusFile? Chosen { get; private set; }

    public NexusFileForm(Mod m, Nexus.ModInfo info, string? current, string? why = null)
    {
        Text = "Choose the Nexus File";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = DeviceDpi / 96f;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Choose the Nexus File", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        var hint = new Label
        {
            Text = (why != null ? why + "\n\n" : "") +
                   $"The Nexus page \"{(info.Name.Length > 0 ? info.Name : "#" + info.ModId)}\" has more than one file. Which one is \"{m.Name}\"? " +
                   "Its updates then come only from that file.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8),
        };
        t.Controls.Add(hint, 0, 1);

        grid = Ui.Grid(s, false, ("File", 0), ("Version", 80), ("Uploaded", 100), ("Description", 0));
        Ui.StyleGrid(grid);
        grid.MultiSelect = false;
        foreach (var f in Nexus.Lines(info))
        {
            int i = grid.Rows.Add(f.Name, f.Version.TrimStart('v', 'V'), DateTimeOffset.FromUnixTimeSeconds(f.Uploaded).LocalDateTime.ToString("yyyy-MM-dd"),
                f.Description.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "");
            grid.Rows[i].Tag = f;
            grid.Rows[i].Cells[3].ToolTipText = f.Description.Trim();
        }
        grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Choose(); };
        t.Controls.Add(grid, 0, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Open on Nexus", () => Process.Start(new ProcessStartInfo(Nexus.SiteMods + info.ModId + "?tab=files") { UseShellExecute = true }), tip: "Open the page's Files tab, to see the files and their descriptions."),
            Ui.FlatButton("Cancel", () => DialogResult = DialogResult.Cancel, tip: "Change nothing (Esc)."),
            Ui.AccentButton("Choose", Choose, tip: "This mod is the selected file: its updates come from that file (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) { e.Handled = true; Choose(); }
        };
        Theme.Apply(this, Palette.Dark);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 900, 360);
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        Shown += (_, _) =>
        {
            // Preselect the current file (or the best guess); else nothing, so a choice is made on purpose.
            grid.ClearSelection();
            foreach (DataGridViewRow r in grid.Rows)
                if (current != null && r.Tag is Nexus.NexusFile f && Nexus.SameLine(f.Name, current)) { r.Selected = true; grid.CurrentCell = r.Cells[0]; }
        };
    }

    void Choose()
    {
        if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not Nexus.NexusFile f) return;
        Chosen = f;
        DialogResult = DialogResult.OK;
    }
}
