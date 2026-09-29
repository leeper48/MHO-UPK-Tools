using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Apply Changes in one window, in the app's style (Kurt: the confirmation and the result were two plain popups).
/// It shows the plan and asks; Apply writes (the window can't be closed meanwhile); then the same window says
/// "Success", or "Not Applied" with the explanation (the log of what was done and where it stopped).
/// </summary>
sealed class ApplyForm : Form
{
    readonly Label heading = new() { AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 8) };
    readonly TextBox body = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false };
    readonly Label footer = new() { AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 0, 0) };
    readonly Button apply, cancel, close;
    Button? headingBtn;
    readonly TableLayoutPanel layout;
    readonly FlowLayoutPanel buttonBar;
    readonly Func<Task<(bool Ok, string Log)>>? run;
    bool running;

    /// <param name="plan">The plan as text (what will be written).</param>
    /// <param name="run">Writes the plan; null when there's nothing to do.</param>
    public ApplyForm(string plan, Func<Task<(bool Ok, string Log)>>? run)
    {
        this.run = run;
        Text = "Apply Changes";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        FormBorderStyle = FormBorderStyle.Sizable; MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        var t = layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(heading, 0, 0);
        t.Controls.Add(body, 0, 1);
        t.Controls.Add(footer, 0, 2);
        var buttons = buttonBar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        apply = Ui.AccentButton("Apply", Run, tip: "Write these changes to the game (Enter).");
        cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, tip: "Change nothing (Esc).");
        close = Ui.AccentButton("Close", () => { DialogResult = DialogResult.OK; }, tip: "Close (Enter).");
        buttons.Controls.AddRange([close, apply, cancel]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);

        body.Text = Crlf(plan);
        if (run == null)
        {
            // One way out: the heading is the button (Kurt).
            heading.Visible = false;
            headingBtn = Ui.HeadingButton("Nothing to Apply", Ui.Accent, () => { DialogResult = DialogResult.OK; });
            headingBtn.Margin = new Padding(0, 0, 0, 10);
            t.Controls.Add(headingBtn, 0, 0);
            footer.Text = "The game already matches your list.";
            apply.Visible = cancel.Visible = close.Visible = false;
            buttons.Visible = false;
        }
        else
        {
            heading.Text = "Apply These Changes to the Game?";
            footer.Text = "Each file is built from its verified original, checked, and can be undone.";
            close.Visible = false;
        }
        FormClosing += (_, e) => { if (running) e.Cancel = true; };   // not while files are being written
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        footer.ForeColor = Ui.Subtle;
        Ui.FitToScreen(this, 640, 460);
        AcceptButton = run == null ? headingBtn : apply;   // Enter confirms
        CancelButton = run == null ? headingBtn : cancel;
        if (headingBtn != null) { headingBtn.BackColor = Ui.Accent; headingBtn.ForeColor = Color.White; }
        Shown += (_, _) => { body.SelectionLength = 0; ActiveControl = run == null ? headingBtn : apply; };
    }

    /// <summary>--apply-snapshot: the window asking and after an error (made-up plan), as PNGs (a success just closes it).</summary>
    public static void Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        const string plan = "3 file(s) to change:\n  UC__MarvelPlayer_Storm_Classic_SF.upk: Storm Classic Costume Visual Update's copy\n  ICO__MarvelUIIcons_SF.upk: icons rebuilt from stock with 12 replacement(s)\n  eng.all_7FFFFFFFFFFFFFFF.string: 2 string(s) from Jeff (Pet)\n351 file(s) already right.";
        foreach (var (name, ok) in new[] { ("apply_ask", (bool?)null), ("apply_error", false) })
        {
            using var f = new ApplyForm(plan, () => Task.FromResult((ok ?? true, "UC__MarvelPlayer_Storm_Classic_SF.upk: Storm Classic Costume Visual Update's copy\n  no clean original (the live file isn't stock and no backup matches); stopping.\nStopped after 0 of 3.")));
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                if (ok != null) { f.Run(); await Task.Delay(400); }
                await Task.Delay(300);
                using var b = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(Path.Combine(dir, name + ".png"));
                f.Close();
            });
            f.ShowDialog();
        }
    }

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();

    async void Run()
    {
        if (run == null) return;
        running = true;
        apply.Enabled = cancel.Enabled = false;
        heading.Text = "Applying…";
        footer.Text = "Writing and checking each file. Please wait.";
        UseWaitCursor = true;
        (bool Ok, string Log) result;
        try { result = await run(); }
        catch (Exception ex) { result = (false, ex.Message); }
        UseWaitCursor = false;
        running = false;
        // Success: no popup (Kurt): the window closes and the status line shows the game matches the list again.
        if (result.Ok) { DialogResult = DialogResult.OK; return; }
        // The result's word is the only button (Kurt: no Close; "Success" continues, Enter too).
        buttonBar.Visible = false;
        heading.Visible = false;
        var done = Ui.HeadingButton(result.Ok ? "Success" : "Not Applied", result.Ok ? Ui.Enabled : Ui.Warn, () => { DialogResult = DialogResult.OK; }, result.Ok ? 16f : 13f);
        if (result.Ok)
        {
            // Success: just that, in the middle of a small window.
            body.Visible = false;
            footer.Visible = false;
            done.Anchor = AnchorStyles.None;
            layout.Controls.Add(done, 0, 1);   // the stretching row: centred up and down
            var centre = new Point(Left + Width / 2, Top + Height / 2);
            Ui.FitToScreen(this, 340, 170);
            Location = new Point(centre.X - Width / 2, centre.Y - Height / 2);
        }
        else
        {
            done.Margin = new Padding(0, 0, 0, 10);
            layout.Controls.Add(done, 0, 0);
            body.Text = Crlf(result.Log);
            footer.Text = "Files written before the stop were verified and can be undone; nothing after it was changed.";
        }
        done.BackColor = result.Ok ? Ui.Enabled : Ui.Warn; done.ForeColor = Ui.OnColor;
        AcceptButton = CancelButton = done;
        ActiveControl = done;
    }
}
