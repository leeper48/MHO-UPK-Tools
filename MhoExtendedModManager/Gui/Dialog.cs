using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Every popup in the app, in the Apply Changes window's format (Kurt: all popups like that one): a Title Case heading,
/// the message (wrapped text for short ones, a scrolling box for long ones such as logs), the app's buttons, Enter for
/// the main button and Esc for Cancel / No. Show() takes the same arguments as MessageBox.Show, so calls just switch.
/// </summary>
static class Dialog
{
    public enum Tone { Normal, Good, Bad }

    public static DialogResult Show(string text, string caption) => Show(null, text, caption);
    public static DialogResult Show(IWin32Window? owner, string text) => Show(owner, text, "MHO Extended Mod Manager");
    public static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Show(owner, text, caption, buttons, icon, defaultButton, null);

    public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon = MessageBoxIcon.None) =>
        Show(null, text, caption, buttons, icon);

    /// <summary>A log or report (install, capture, migrate): a heading, the text in a scrolling box, Close.</summary>
    public static void ShowLog(IWin32Window? owner, string heading, string text, Tone tone = Tone.Normal) =>
        Show(owner, text, heading, MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, tone, log: true);

    static DialogResult Show(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton, Tone? tone, bool log = false)
    {
        // Tests (MHO_EXTMM_TEST_DIALOGS = a file): written there instead of shown, answered with the default button, so a
        // test never stops on a window (one blocked on Kurt's screen 2026-10-02).
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_TEST_DIALOGS") is { Length: > 0 } testLog)
        {
            File.AppendAllText(testLog, $"[{caption}] {text}{Environment.NewLine}");
            return buttons switch
            {
                MessageBoxButtons.OK => DialogResult.OK,
                MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel => defaultButton == MessageBoxDefaultButton.Button1 ? DialogResult.Yes : DialogResult.No,
                _ => defaultButton == MessageBoxDefaultButton.Button1 ? DialogResult.OK : DialogResult.Cancel,
            };
        }
        using var f = new DialogForm(text, caption, buttons, icon, defaultButton,
            tone ?? (icon is MessageBoxIcon.Error or MessageBoxIcon.Warning ? Tone.Bad : Tone.Normal), log);
        return owner != null ? f.ShowDialog(owner) : f.ShowDialog();
    }

    /// <summary>--dialog-snapshot: a question, a log (Install) and an error, as PNGs (layout check).</summary>
    public static void Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        var samples = new (string Name, Func<DialogForm> Make)[]
        {
            ("dialog_question", () => new DialogForm("\"Storm Classic Costume Visual Update\" is installed already (version 4 by Wlzzer).\n\nReplace it with version 5 by Wlzzer?\n\nIt keeps its place in the list, on/off, lock, tags and note. The old files go to the Recycle Bin.",
                "Update Mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1, Tone.Normal, false)),
            ("dialog_log", () => new DialogForm("Storm Classic.zip:\nInstalled 'Storm Classic Costume Visual Update' by Wlzzer, version 5 (disabled, top of the list).\n\nNew mods are added at the top of the list, turned off: tick one, then Apply Changes.",
                "Installed", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Good, true)),
            ("dialog_error", () => new DialogForm("The folder is in use by another program.", "Export Failed", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Tone.Bad, false)),
            ("dialog_changelog", () => new DialogForm(File.Exists(Path.Combine(AppContext.BaseDirectory, "CHANGELOG.txt")) ? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CHANGELOG.txt")) : "(no CHANGELOG.txt)",
                "Changelog (You Have " + Program.Version + ")", MessageBoxButtons.OK, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Normal, true)),
        };
        // A menu as the app draws them (dark renderer): normal, checked, disabled, separator, submenu.
        using (var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f) })
        {
            menu.Items.Add("Change Game Folder");
            menu.Items.Add(new ToolStripMenuItem("Check for Updates at Start") { Checked = true });
            menu.Items.Add(new ToolStripMenuItem("Sign In with Nexus") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            var sub = new ToolStripMenuItem("Nexus");
            sub.DropDownItems.Add("Open the Nexus Page");
            menu.Items.Add(sub);
            menu.Show(new Point(-3000, -3000));
            Application.DoEvents();
            using var b = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(b, new Rectangle(0, 0, menu.Width, menu.Height));
            b.Save(Path.Combine(dir, "menu.png"));
            menu.Close();
        }
        foreach (var (name, make) in samples)
        {
            using var f = make();
            f.Shown += (_, _) => f.BeginInvoke(async () =>
            {
                await Task.Delay(400);
                using var b = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(Path.Combine(dir, name + ".png"));
                f.Close();
            });
            f.ShowDialog();
        }
    }

    sealed class DialogForm : Form
    {
        public DialogForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, Tone tone, bool log)
        {
            text = text.Replace("\r\n", "\n").Trim();
            bool longText = log || text.Length > 420 || text.Count(c => c == '\n') > 7;
            Text = caption;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Ui.DarkFrame(this);
            FormBorderStyle = longText ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = Ui.Regular(9.5f);
            Padding = new Padding(14);
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // One button only (OK / Close): the heading is the button (Kurt: "Success" continues; no separate Close).
            bool single = buttons is not (MessageBoxButtons.OKCancel or MessageBoxButtons.YesNo or MessageBoxButtons.YesNoCancel or MessageBoxButtons.RetryCancel or MessageBoxButtons.AbortRetryIgnore);
            var toneColor = tone switch { Tone.Good => Ui.Enabled, Tone.Bad => Ui.Warn, _ => Ui.Accent };
            Control heading = single
                ? Ui.HeadingButton(Ui.TitleCase(caption), toneColor, () => { DialogResult = DialogResult.OK; })
                : new Label { Text = Ui.TitleCase(caption), AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 8), ForeColor = tone switch { Tone.Good => Ui.Enabled, Tone.Bad => Ui.Warn, _ => Ui.Text } };
            if (single) { heading.Margin = new Padding(0, 0, 0, 10); if (!log && !(text.Length > 420 || text.Count(c => c == '\n') > 7)) heading.Anchor = AnchorStyles.None; }
            t.Controls.Add(heading, 0, 0);
            float s = DeviceDpi / 96f;
            if (longText)
                t.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false, Text = text.Replace("\n", "\r\n") }, 0, 1);
            else
                t.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size((int)(470 * s), 0), Margin = new Padding(0, 0, 0, 4), Anchor = single ? AnchorStyles.None : AnchorStyles.Left | AnchorStyles.Top, TextAlign = single ? ContentAlignment.TopCenter : ContentAlignment.TopLeft }, 0, 1);

            var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
            (string Label, DialogResult Result)[] choices = buttons switch
            {
                MessageBoxButtons.OKCancel => [("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel)],
                MessageBoxButtons.YesNo => [("Yes", DialogResult.Yes), ("No", DialogResult.No)],
                MessageBoxButtons.YesNoCancel => [("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel)],
                MessageBoxButtons.RetryCancel => [("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel)],
                _ => [(log ? "Close" : "OK", DialogResult.OK)],
            };
            int main = defaultButton switch { MessageBoxDefaultButton.Button2 => 1, MessageBoxDefaultButton.Button3 => 2, _ => 0 };
            if (main >= choices.Length) main = 0;
            var made = new List<Button>();
            for (int i = 0; i < choices.Length; i++)
            {
                var (label, result) = choices[i];
                bool isCancel = result is DialogResult.Cancel or DialogResult.No;
                string tip = i == main ? $"{label} (Enter)" : isCancel ? $"{label} (Esc)" : label;
                var b = i == main ? Ui.AccentButton(label, () => { DialogResult = result; }, tip) : Ui.FlatButton(label, () => { DialogResult = result; }, tip);
                made.Add(b);
            }
            for (int i = made.Count - 1; i >= 0; i--) bar.Controls.Add(made[i]);   // right to left: first choice on the left
            if (!single) t.Controls.Add(bar, 0, 2);
            Controls.Add(t);

            if (single) { made.Clear(); made.Add((Button)heading); main = 0; }
            AcceptButton = made[main];
            int cancelAt = single ? -1 : Array.FindIndex(choices, c => c.Result is DialogResult.Cancel or DialogResult.No);
            CancelButton = made[cancelAt >= 0 ? cancelAt : 0];
            Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
            Ui.RestyleButtons(this);
            if (single) { var hb = (Button)heading; hb.BackColor = toneColor; hb.ForeColor = toneColor == Ui.Accent ? Color.White : Ui.OnColor; hb.FlatAppearance.BorderColor = toneColor; }
            else heading.ForeColor = tone switch { Tone.Good => Ui.Enabled, Tone.Bad => Ui.Warn, _ => Ui.Text };
            if (longText) Ui.FitToScreen(this, 640, 460);
            else
            {
                // Short message: the window fits its content (heading, wrapped text, buttons).
                var pref = t.GetPreferredSize(new Size((int)(500 * s), 0));
                ClientSize = new Size(Math.Max(pref.Width, (int)(380 * s)) + Padding.Horizontal, pref.Height + Padding.Vertical);
            }
            Shown += (_, _) => { ActiveControl = made[main]; foreach (var tb in Controls.OfType<TableLayoutPanel>().SelectMany(x => x.Controls.OfType<TextBox>())) tb.SelectionLength = 0; };
        }
    }
}
