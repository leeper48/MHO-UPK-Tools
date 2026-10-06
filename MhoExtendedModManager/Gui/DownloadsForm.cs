using System.Diagnostics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Settings → Download Counts (Kurt, 2026-09-30): how often each release of the app was downloaded from GitHub, read from
/// its public release list (no account). The total, the change since the last look (kept in settings), and a table of
/// releases (published, total, zip, Setup.exe). Counts are downloads, not people: the zip includes in-app updates.
/// </summary>
sealed class DownloadsForm : Form
{
    readonly Settings settings;
    readonly DataGridView grid;
    readonly Label heading = new() { Text = "Download Counts", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) };
    readonly Label summary = new() { Text = "Reading GitHub…", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 8) };

    public DownloadsForm(Settings settings)
    {
        this.settings = settings;
        Text = "Download Counts";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(heading, 0, 0);
        t.Controls.Add(summary, 0, 1);
        grid = Ui.Grid(s, false, ("Release", 0), ("Published", 110), ("Total", 70), ("Zip", 70), ("Setup.exe", 90));
        Ui.StyleGrid(grid);
        foreach (int c in new[] { 2, 3, 4 }) grid.Columns[c].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        t.Controls.Add(grid, 0, 2);
        var note = new Label
        {
            Text = "These count downloads, not people: the same person downloading twice counts twice, and the zip count includes updates made from inside the app. Setup.exe is closer to fresh installs. From GitHub's public release data.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 0, 0),
        };
        t.Controls.Add(note, 0, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Refresh", () => _ = Load(), tip: "Read the counts from GitHub again."),
            Ui.FlatButton("Open Releases Page", () => Process.Start(new ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true }), tip: "Open the app's releases on GitHub in your browser."),
            Ui.AccentButton("Close", Close, tip: "Close this window (Esc)."),
        ]);
        t.Controls.Add(buttons, 0, 4);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 640, 560);
        void Wrap() => note.MaximumSize = summary.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        Resize += (_, _) => Wrap();
        Wrap();
        Shown += (_, _) => _ = Load();
    }

    async Task Load()
    {
        heading.ForeColor = Ui.Text; heading.Text = "Download Counts"; summary.Text = "Reading GitHub…";
        List<Updater.Downloads> rows;
        try { rows = await Updater.DownloadCounts(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            if (IsDisposed) return;
            heading.ForeColor = Ui.Warn; heading.Text = "Couldn't Read GitHub";
            summary.Text = ex.Message;
            return;
        }
        if (IsDisposed) return;
        grid.Rows.Clear();
        foreach (var r in rows)
            grid.Rows.Add(r.Tag.Replace(Updater.TagPrefix, "v"), r.Published == DateTime.MinValue ? "" : r.Published.ToString("yyyy-MM-dd"), r.Total, r.Zip, r.Setup);
        grid.ClearSelection();
        int total = rows.Sum(r => r.Total), zip = rows.Sum(r => r.Zip), setup = rows.Sum(r => r.Setup);
        heading.Text = $"{total:N0} Downloads";
        string since = settings.LastDownloadTotal is int last && settings.LastDownloadCheck is DateTime when
            ? $"  ·  {(total - last >= 0 ? "+" : "")}{total - last:N0} since you last looked ({when:MMM d, h:mm tt})"
            : "";
        summary.Text = $"{rows.Count} releases  ·  zip {zip:N0}, Setup.exe {setup:N0}{since}";
        settings.LastDownloadTotal = total;
        settings.LastDownloadCheck = DateTime.Now;
        settings.Save();
    }
}
