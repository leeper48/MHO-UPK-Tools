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

    /// <summary>A question with buttons of its own (first = the main one, Enter; last = Esc). Returns the index clicked.</summary>
    public static int Choose(IWin32Window? owner, string text, string caption, params string[] labels)
    {
        if (Environment.GetEnvironmentVariable("MHO_EXTMM_TEST_DIALOGS") is { Length: > 0 } testLog)
        {
            File.AppendAllText(testLog, $"[{caption}] {text} ({string.Join(" / ", labels)}){Environment.NewLine}");
            // MHO_EXTMM_TEST_CHOICE=<n>: that button instead of the last (Cancel)
            return int.TryParse(Environment.GetEnvironmentVariable("MHO_EXTMM_TEST_CHOICE"), out int pick) && pick >= 0 && pick < labels.Length ? pick : labels.Length - 1;
        }
        // Form.DialogResult accepts only the enum's own values: the buttons take these in order, the last one is Cancel (Esc).
        DialogResult[] results = [DialogResult.OK, DialogResult.Yes, DialogResult.Retry, DialogResult.Ignore, DialogResult.Abort];   // (not No: the dialog makes No the Esc button)
        if (labels.Length - 1 > results.Length) throw new ArgumentException("too many buttons", nameof(labels));
        var choices = labels.Select((l, i) => (l, i == labels.Length - 1 ? DialogResult.Cancel : results[i])).ToArray();
        using var f = new DialogForm(text, caption, MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Normal, false, choices);
        var r = owner != null ? f.ShowDialog(owner) : f.ShowDialog();
        int at = Array.IndexOf(results, r);
        return at >= 0 && at < labels.Length - 1 ? at : labels.Length - 1;
    }

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
            ("dialog_whatsnew", () => new DialogForm(WhatsNew.Notices[0].Text, "What's New in Version " + WhatsNew.Notices[0].Version, MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Normal, false,
                [("Open the Manual", DialogResult.OK), ("Close", DialogResult.Cancel)])),
            ("dialog_remove_target", () => new DialogForm("UC__MarvelPlayer_Storm_Classic_SF.upk (Storm) is one of the mod's own packages, with the model built onto it. Put the mod's own copy back (the model comes off), or take the package out of the mod?",
                "Remove a Target", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Normal, false,
                [("Restore the Mod's Copy", DialogResult.OK), ("Remove From the Mod", DialogResult.Yes), ("Cancel", DialogResult.Cancel)])),
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
        // Each button of a Choose notice clicked for real (a crash 2026-10-02: results outside DialogResult's values).
        var report = new List<string>();
        string[] labels = ["Open the Manual", "Older", "Close"];
        for (int k = 0; k < labels.Length; k++)
        {
            DialogResult[] results = [DialogResult.OK, DialogResult.Yes];
            var choices = labels.Select((l, i) => (l, i == labels.Length - 1 ? DialogResult.Cancel : results[i])).ToArray();
            using var f = new DialogForm("Click test.", "Choose", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button1, Tone.Normal, false, choices);
            int want = k;
            f.Shown += (_, _) => f.BeginInvoke(() =>
            {
                var buttons = All(f).OfType<Button>().Where(x => labels.Contains(x.Text)).ToList();
                var b = buttons.FirstOrDefault(x => x.Text == labels[want]);
                if (b == null) { report.Add($"{labels[want]}: button not found"); f.Close(); return; }
                try { b.PerformClick(); } catch (Exception ex) { report.Add($"{labels[want]}: {ex.GetType().Name}"); f.Close(); }
            });
            var r = f.ShowDialog();
            report.Add($"{labels[k]} → {r}");
        }
        File.WriteAllLines(Path.Combine(dir, "choose_clicks.txt"), report);
        static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
    }

    sealed class DialogForm : Form
    {
        /// <summary>
        /// Text with [link text](https://…) links, as a LinkLabel that opens them in the browser (only for the app's own
        /// notices, Dialog.Choose: never for text that may hold a mod's names). Only https links to discord.com, github.com or
        /// nexusmods.com open.
        /// </summary>
        static readonly Color NoticeYellow = Color.FromArgb(250, 210, 60);

        /// <summary>A notice's paragraphs (split at blank lines), top to bottom: a paragraph starting with [!] is a bold yellow
        /// warning (Kurt: "experimental" in yellow); the others may hold [text](https://…) links (LinkText).</summary>
        static Control Paragraphs(string text, float s)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            var paras = text.Split("\n\n");
            for (int i = 0; i < paras.Length; i++)
            {
                string para = paras[i].Trim('\n');
                Control c = para.StartsWith("[!]")
                    ? new Label { Text = para[3..].Trim(), AutoSize = true, MaximumSize = new Size((int)(470 * s), 0), ForeColor = NoticeYellow, Font = Ui.Bold(9.5f), Tag = "notice-yellow" }
                    : LinkText(para, s);
                c.Margin = new Padding(0, 0, 0, i < paras.Length - 1 ? (int)(12 * s) : 0);
                f.Controls.Add(c);
            }
            return f;
        }

        static LinkLabel LinkText(string text, float s)
        {
            var plain = new System.Text.StringBuilder();
            var links = new List<(int Start, int Length, string Url)>();
            int p = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\[([^\]]+)\]\((https://[^)\s]+)\)"))
            {
                plain.Append(text, p, m.Index - p);
                links.Add((plain.Length, m.Groups[1].Length, m.Groups[2].Value));
                plain.Append(m.Groups[1].Value);
                p = m.Index + m.Length;
            }
            plain.Append(text, p, text.Length - p);
            var l = new LinkLabel { Text = plain.ToString(), AutoSize = true, MaximumSize = new Size((int)(470 * s), 0), Margin = new Padding(0, 0, 0, 4),
                LinkColor = Ui.Accent, ActiveLinkColor = Ui.Accent, VisitedLinkColor = Ui.Accent, LinkBehavior = LinkBehavior.HoverUnderline };
            l.Links.Clear();
            foreach (var (start, length, url) in links) l.Links.Add(start, length, url);
            l.LinkClicked += (_, e) =>
            {
                if (e.Link?.LinkData is string url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
                    && (u.Host is "discord.com" or "github.com" or "www.nexusmods.com" or "nexusmods.com"))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
                    catch (System.ComponentModel.Win32Exception) { }
            };
            return l;
        }

        public DialogForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, Tone tone, bool log, (string Label, DialogResult Result)[]? custom = null)
        {
            text = text.Replace("\r\n", "\n").Trim();
            // A notice with its own buttons (Choose) stays compact up to a longer text: it's written to fit.
            bool longText = log || (custom == null ? text.Length > 420 || text.Count(c => c == '\n') > 7 : text.Length > 1400);
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
            float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
            if (longText)
                t.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, TabStop = false, Text = text.Replace("\n", "\r\n") }, 0, 1);
            else if (custom != null && (text.Contains("](https://") || text.Contains("[!]")))
                t.Controls.Add(Paragraphs(text, s), 0, 1);
            else
                t.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size((int)(470 * s), 0), Margin = new Padding(0, 0, 0, 4), Anchor = single ? AnchorStyles.None : AnchorStyles.Left | AnchorStyles.Top, TextAlign = single ? ContentAlignment.TopCenter : ContentAlignment.TopLeft }, 0, 1);

            var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 12, 0, 0) };
            (string Label, DialogResult Result)[] choices = custom ?? buttons switch
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
            // The theme sets every label's colour: a notice's warning gets its yellow back.
            static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
            foreach (var w in All(this).Where(c => c.Tag is "notice-yellow")) w.ForeColor = NoticeYellow;
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
