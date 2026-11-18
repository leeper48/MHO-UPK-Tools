using System.Diagnostics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Find My Mods on Nexus (Kurt): every unlinked mod with its likely Nexus pages (NexusMatch), the best one in a drop-down
/// with the others; confident matches are ticked in advance, the rest wait for the user. Link Selected links only the
/// ticked rows. Open on Nexus shows the chosen page to check it first.
/// </summary>
sealed class NexusScanForm : Form
{
    readonly DataGridView grid;
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" };
    readonly List<(Mod Mod, List<NexusMatch.Candidate> Candidates)> rows = [];
    /// <summary>The links the user confirmed: mod → Nexus mod ID.</summary>
    public List<(Mod Mod, int ModId, string Version)> Confirmed { get; } = [];
    /// <summary>Pages the user unticked, mod folder -> Nexus mod IDs, kept in state.json (NexusNotThese) so the next scan
    /// doesn't tick them again (Kurt, 2026-11-18). Filled on Link Selected and on Cancel.</summary>
    public Dictionary<string, HashSet<int>> NotThese { get; } = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<int>> before = new(StringComparer.OrdinalIgnoreCase);

    public NexusScanForm(IEnumerable<Mod> mods, List<NexusMatch.NexusMod> nexus, Dictionary<string, List<int>>? notThese = null)
    {
        foreach (var (k, v) in notThese ?? []) before[k] = [.. v];
        bool PreTick(Mod m, List<NexusMatch.Candidate> c) => NexusMatch.Confident(c) && !WasUnticked(m, c[0]);
        Text = "Find My Mods on Nexus";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Find My Mods on Nexus", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        var hint = new Label
        {
            Text = $"Your mods that aren't linked yet, with their likely pages among the {nexus.Count} Marvel Heroes Omega mods on Nexus. Strong matches are ticked; " +
                   "check the others (pick another match in the drop-down, or Open on Nexus to look), tick the right ones, then Link Selected. Linked mods get update notices.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8),
        };
        t.Controls.Add(hint, 0, 1);

        grid = Ui.Grid(s, false, ("Your Mod", 0), ("Author", 140), ("Nexus Page", 0), ("Match", 80));
        grid.ReadOnly = false;
        var tick = new DataGridViewCheckBoxColumn { Name = "link", HeaderText = "Link", Width = (int)(50 * s), FlatStyle = FlatStyle.Flat };
        grid.Columns.Insert(0, tick);
        // The page choice opens the app's own list (DropList), not a system drop-down.
        var pick = new DataGridViewTextBoxColumn { Name = "page", HeaderText = "Nexus Page", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
        int pageAt = grid.Columns["Nexus Page"]!.Index;
        grid.Columns.RemoveAt(pageAt);
        grid.Columns.Insert(pageAt, pick);
        foreach (DataGridViewColumn c in grid.Columns) c.ReadOnly = c.Name is not "link";
        Ui.StyleGrid(grid);
        pick.DefaultCellStyle.BackColor = Ui.Card; pick.DefaultCellStyle.ForeColor = Ui.Text;

        foreach (var m in mods.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var c = NexusMatch.Candidates(m, nexus);
            rows.Add((m, c));
        }
        // Confident first, then the ones to choose, then those without a match.
        foreach (var (m, c) in rows.OrderBy(r => PreTick(r.Mod, r.Candidates) ? 0 : r.Candidates.Count > 0 ? 1 : 2))
        {
            int i = grid.Rows.Add();
            var row = grid.Rows[i];
            row.Tag = (m, c);
            row.Cells["Your Mod"].Value = m.Name;
            row.Cells["Author"].Value = m.Manifest.Author ?? "";
            var cell = row.Cells["page"];
            if (c.Count == 0) { cell.Value = "(no match found)"; row.Cells["link"].ReadOnly = true; row.DefaultCellStyle.ForeColor = Ui.Subtle; }
            else
            {
                cell.Value = Label(c[0]);
                row.Cells["link"].Value = PreTick(m, c);
            }
            row.Cells["Match"].Value = c.Count > 0 ? MatchText(m, c[0]) : "";
            row.Cells["Match"].Style.ForeColor = c.Count > 0 && PreTick(m, c) ? Ui.Enabled : Ui.Subtle;
        }
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "page") return;
            var row = grid.Rows[e.RowIndex];
            if (row.Tag is (Mod m0, List<NexusMatch.Candidate> cs) && cs.FirstOrDefault(x => Label(x) == row.Cells["page"].Value as string) is { } chosen)
            {
                row.Cells["Match"].Value = MatchText(m0, chosen);
                row.Cells["link"].Value = true;   // choosing a page means "this one"
            }
            Count();
        };
        // A row with more than one likely page: the cell shows ▾ and a click opens the choices.
        grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "page" && grid.Rows[e.RowIndex].Tag is (Mod, List<NexusMatch.Candidate> { Count: > 1 }))
            { e.Value = e.Value + "  ▾"; e.FormattingApplied = true; }
        };
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "page" || grid.Rows[e.RowIndex].Tag is not (Mod, List<NexusMatch.Candidate> { Count: > 1 } cs)) return;
            var row = grid.Rows[e.RowIndex];
            var labels = cs.Select(Label).ToList();
            var rect = grid.RectangleToScreen(grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false));
            DropList.Show(grid, rect, labels, labels.IndexOf(row.Cells["page"].Value as string ?? ""), i => row.Cells["page"].Value = labels[i]);
        };
        // A page unticked in an earlier scan says so where the pointer is.
        grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "Match" && grid.Rows[e.RowIndex].Tag is (Mod m, List<NexusMatch.Candidate>)
                && Chosen(grid.Rows[e.RowIndex]) is { } c && WasUnticked(m, c))
                e.ToolTipText = "You unticked this page in an earlier scan, so it isn't ticked now. Tick it to link it after all.";
        };
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.DataError += (_, e) => e.ThrowException = false;
        t.Controls.Add(grid, 0, 2);

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(status, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange([
            Ui.FlatButton("Open on Nexus", OpenSelected, tip: "Open the chosen Nexus page of the selected row, to check it's the right mod."),
            Ui.FlatButton("Cancel", () => { Remember(); DialogResult = DialogResult.Cancel; }, tip: "Link nothing (Esc). Strong matches you unticked are remembered: the next scan doesn't tick them."),
            Ui.AccentButton("Link Selected", LinkSelected, tip: "Link the ticked mods to the chosen Nexus pages; they then get update notices."),
        ]);
        bar.Controls.Add(buttons, 1, 0);
        t.Controls.Add(bar, 0, 3);
        Controls.Add(t);
        CancelButton = null;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Remember(); DialogResult = DialogResult.Cancel; } };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1100, 720);
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        Count();
    }

    static string Label(NexusMatch.Candidate c) =>
        $"#{c.Mod.ModId}  {c.Mod.Name}  (by {(c.Mod.Author.Length > 0 ? c.Mod.Author : c.Mod.Uploader)}, v{c.Mod.Version.TrimStart('v', 'V')})";

    bool WasUnticked(Mod m, NexusMatch.Candidate c) => before.TryGetValue(m.FolderName, out var r) && r.Contains(c.Mod.ModId);

    string MatchText(Mod m, NexusMatch.Candidate c) => $"{c.Score:P0}" + (WasUnticked(m, c) ? "  · Unticked" : "");

    /// <summary>
    /// What to remember: a strong match's page (one the scan ticks by itself) that is unticked now, plus the pages unticked
    /// in an earlier scan; a page ticked now is forgotten. Mods not in this scan keep theirs.
    /// </summary>
    void Remember()
    {
        grid.EndEdit();
        NotThese.Clear();
        foreach (var (k, v) in before) NotThese[k] = [.. v];
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is not (Mod m, List<NexusMatch.Candidate> cs) || cs.Count == 0 || Chosen(row) is not { } chosen) continue;
            var set = NotThese.TryGetValue(m.FolderName, out var x) ? x : NotThese[m.FolderName] = [];
            if (row.Cells["link"].Value is true) set.Remove(chosen.Mod.ModId);
            else if (NexusMatch.Confident(cs) && chosen == cs[0]) set.Add(chosen.Mod.ModId);
            if (set.Count == 0) NotThese.Remove(m.FolderName);
        }
    }

    // --nexus-scan-test hooks
    internal List<(Mod Mod, bool Ticked)> TestRows() =>
        grid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is (Mod, List<NexusMatch.Candidate>)).Select(r => (((Mod, List<NexusMatch.Candidate>))r.Tag!).Item1 is var m ? (m, r.Cells["link"].Value is true) : default).ToList();
    void TestSet(Mod m, bool on) { foreach (DataGridViewRow r in grid.Rows) if (r.Tag is (Mod x, List<NexusMatch.Candidate>) && x == m) r.Cells["link"].Value = on; }
    internal void TestUntick(Mod m) => TestSet(m, false);
    internal void TestTick(Mod m) => TestSet(m, true);
    internal void TestRemember() => Remember();

    /// <summary>True when the remembered pages differ from what the scan started with.</summary>
    public bool NotTheseChanged =>
        NotThese.Count != before.Count || NotThese.Any(kv => !before.TryGetValue(kv.Key, out var b) || !b.SetEquals(kv.Value));

    NexusMatch.Candidate? Chosen(DataGridViewRow row) =>
        row.Tag is (Mod, List<NexusMatch.Candidate> cs) ? cs.FirstOrDefault(x => Label(x) == row.Cells["page"].Value as string) : null;

    void Count()
    {
        int ticked = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Cells["link"].Value is true);
        int withMatch = rows.Count(r => r.Candidates.Count > 0);
        status.Text = $"{rows.Count} Unlinked Mod(s)  ·  {withMatch} With a Likely Page  ·  {ticked} Ticked";
    }

    void OpenSelected()
    {
        if (grid.CurrentRow is not DataGridViewRow row || Chosen(row) is not { } c) { status.Text = "Select a Row With a Match First"; return; }
        Process.Start(new ProcessStartInfo(Nexus.SiteMods + c.Mod.ModId) { UseShellExecute = true });
    }

    void LinkSelected()
    {
        grid.EndEdit();
        Remember();
        foreach (DataGridViewRow row in grid.Rows)
            if (row.Cells["link"].Value is true && row.Tag is (Mod m, List<NexusMatch.Candidate>) && Chosen(row) is { } c)
                Confirmed.Add((m, c.Mod.ModId, c.Mod.Version));
        if (Confirmed.Count == 0) { status.Text = "Tick the Mods to Link First"; return; }
        DialogResult = DialogResult.OK;
    }
}
