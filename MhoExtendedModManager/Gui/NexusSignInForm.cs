using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// While signing in with Nexus (Kurt, 2026-10-25): what to do in the browser (sign in, allow, Continue), and a box for when the
/// browser can't reach the app ("127.0.0.1 refused to connect"): paste Nexus's "Having issues?" code or the address there.
/// Use Code hands the text to the sign-in (Pasted), Cancel stops it (Cancelled). Closed by the owner when the sign-in ends.
/// </summary>
sealed class NexusSignInForm : Form
{
    readonly TaskCompletionSource<string> pasted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource cancel = new();
    readonly TextBox code = new() { Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 0) };

    /// <summary>The text the user pasted (Use Code).</summary>
    public Task<string> Pasted => pasted.Task;
    public CancellationToken Cancelled => cancel.Token;

    public NexusSignInForm()
    {
        Text = "Signing In with Nexus";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = Ui.Regular(9.5f); Padding = new Padding(14);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, AutoSize = true };
        for (int k = 0; k < 4; k++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "Waiting for Nexus", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 6) }, 0, 0);
        t.Controls.Add(new Label
        {
            Text = "In your browser:" + Environment.NewLine +
                   "  • Sign in to Nexus Mods and allow MHO Extended Mod Manager." + Environment.NewLine +
                   "  • On \"Authorisation successful!\", press Continue." + Environment.NewLine + Environment.NewLine +
                   "If the browser then says the site can't be reached (127.0.0.1 refused to connect), or nothing happens here:" + Environment.NewLine +
                   "  • Copy the text under \"Having issues?\" on the Nexus page, or the whole address from the browser's address bar." + Environment.NewLine +
                   "  • Paste it below and click Use Code.",
            AutoSize = true, MaximumSize = new Size(560, 0),
        }, 0, 1);
        code.Width = 540;
        t.Controls.Add(code, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        var use = Ui.AccentButton("Use Code", () =>
        {
            if (NexusAuth.ParsePasted(code.Text) == null) { Refused("That doesn't look like Nexus's sign-in code. Paste the text under \"Having issues?\" on the Nexus page, or the whole 127.0.0.1 address from the browser's address bar."); return; }
            pasted.TrySetResult(code.Text);
        }, tip: "Finish signing in with the pasted code or address.");
        var stop = Ui.FlatButton("Cancel", () => { cancel.Cancel(); Close(); }, tip: "Stop signing in (Esc).");
        buttons.Controls.AddRange([stop, use]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { cancel.Cancel(); Close(); } };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) cancel.Cancel(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this); Ui.Restyle(this);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    /// <summary>The pasted text was refused: say so and let the user try again.</summary>
    public void Refused(string why) { code.SelectAll(); code.Focus(); Dialog.Show(this, why, "Not a Sign-In Code", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
}
