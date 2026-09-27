using System.Diagnostics;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>"Version X is available": the release notes, Update and restart / Release page / Skip this version / Later.</summary>
sealed class UpdateForm : Form
{
    readonly Updater.Release release;
    readonly Label progress = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 0, 0) };
    readonly Button update, page, skip, later;
    /// <summary>True when the new version is installed (the caller restarts).</summary>
    public bool Installed { get; private set; }
    public bool SkipThis { get; private set; }

    public UpdateForm(Updater.Release r)
    {
        release = r;
        Text = "Update Available";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = $"MHO Extended Mod Manager {r.Version} is available (you have {Program.Version}).", AutoSize = true, Font = Ui.Bold(11f), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        var notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                                  Text = (string.IsNullOrWhiteSpace(r.Notes) ? "(no release notes)" : r.Notes).Replace("\r\n", "\n").Replace("\n", "\r\n") };
        t.Controls.Add(notes, 0, 1);
        t.Controls.Add(progress, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        update = Ui.AccentButton("Update and Restart", InstallUpdate);
        later = Ui.FlatButton("Later", () => { DialogResult = DialogResult.Cancel; });
        skip = Ui.FlatButton("Skip This Version", () => { SkipThis = true; DialogResult = DialogResult.Cancel; });
        page = Ui.FlatButton("Release Page", () => Process.Start(new ProcessStartInfo(r.PageUrl) { UseShellExecute = true }));
        buttons.Controls.AddRange([update, later, skip, page]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);
        Theme.Apply(this, Palette.Dark);
        Ui.RestyleButtons(this);
        Ui.FitToScreen(this, 620, 440);
        notes.TabStop = false;
        Shown += (_, _) => { notes.SelectionLength = 0; ActiveControl = update; };   // not all the notes highlighted
    }

    async void InstallUpdate()
    {
        update.Enabled = page.Enabled = skip.Enabled = later.Enabled = false;
        UseWaitCursor = true;
        string? why;
        try { why = await Updater.Install(release, s => BeginInvoke(() => progress.Text = s)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        { why = ex.Message + " (nothing was changed)"; }
        UseWaitCursor = false;
        if (why != null)
        {
            progress.Text = "Not updated: " + why;
            later.Enabled = page.Enabled = skip.Enabled = true;
            return;
        }
        Installed = true;
        DialogResult = DialogResult.OK;
    }
}
