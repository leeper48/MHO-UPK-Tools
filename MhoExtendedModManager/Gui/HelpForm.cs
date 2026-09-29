using System.Diagnostics;
using System.Net;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The manual (Manual\manual.html next to the exe; Kurt: like MPM's) in its own window, opened at a section: F1, the Help
/// button, Settings → Help. As MPM's HelpForm: the command reference is filled in from Program.Commands so it always
/// matches the app, the page is written to %TEMP% (a file URL keeps #section links working) with the dark theme, and
/// zoomed by the screen's scale times the user's A− / A+ size (the built-in browser ignores display scaling). Search
/// uses the page's own script (mhoSearch / mhoNext).
/// </summary>
sealed class HelpForm : Form
{
    static HelpForm? open;
    readonly WebBrowser browser = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = true, AllowWebBrowserDrop = false };
    readonly Settings settings;
    readonly TextBox search = new() { Width = 260, PlaceholderText = "Search the manual (Enter = next)" };
    readonly Label found = new() { AutoSize = true, Padding = new Padding(6, 8, 0, 0) };
    string page, searched = "";

    public static void Show(Form owner, Settings settings, string anchor = "contents")
    {
        string? file = Render(Zoom(owner.DeviceDpi, settings.ManualTextSize));
        if (file == null) { Dialog.Show(owner, "The manual (Manual\\manual.html) isn't next to the app. Copy the whole app folder, not only the .exe.", "Help", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (open == null || open.IsDisposed) { open = new HelpForm(file, settings) { Icon = owner.Icon }; open.Show(owner); }
        open.Go(anchor);
        open.Activate();
    }

    static int Zoom(int dpi, int textSize) => (int)Math.Round(dpi / 96.0 * textSize);

    /// <summary>The manual with the version and command reference filled in, written to %TEMP%; null if it isn't there.</summary>
    internal static string? Render(int zoomPercent = 100, string fileName = "MHO_Ext_ModManager_manual.html")
    {
        string src = Path.Combine(AppContext.BaseDirectory, "Manual", "manual.html");
        if (!File.Exists(src)) return null;
        string html = File.ReadAllText(src)
            .Replace("<!--COMMANDS-->", CommandTable())
            .Replace("<!--VERSION-->", Program.Version)
            .Replace("<body>", "<body class=\"dark\">")
            .Replace("</head>", zoomPercent == 100 ? "</head>" : $"<style>body {{ zoom: {zoomPercent}%; }}</style></head>");
        string file = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(file, html);
        return file;
    }

    static string CommandTable()
    {
        var sb = new System.Text.StringBuilder("<table><tr><th>Command</th><th>What it does</th></tr>");
        foreach (var c in Program.Commands)
            sb.Append("<tr><td><code>").Append(WebUtility.HtmlEncode(c.Syntax)).Append("</code></td><td>").Append(WebUtility.HtmlEncode(c.Summary)).Append("</td></tr>");
        return sb.Append("</table>").ToString();
    }

    HelpForm(string file, Settings settings)
    {
        page = file; this.settings = settings;
        Text = "MHO Extended Mod Manager: Manual";
        Ui.DarkFrame(this);
        Font = Ui.Regular(9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
        bar.Controls.Add(Ui.FlatButton("Contents", () => Go("contents"), "Back to the list of sections."));
        bar.Controls.Add(Ui.FlatButton("Back", () => browser.GoBack(), "The page you were on before."));
        bar.Controls.Add(Ui.FlatButton("A−", () => ChangeSize(-10), "Smaller text (remembered)."));
        bar.Controls.Add(Ui.FlatButton("A+", () => ChangeSize(+10), "Larger text (remembered)."));
        // A real browser scales for the screen itself: that copy gets no zoom.
        bar.Controls.Add(Ui.FlatButton("Open in Web Browser", () => { if (Render(100, "MHO_Ext_ModManager_manual_browser.html") is string f) Process.Start(new ProcessStartInfo { FileName = f, UseShellExecute = true }); }, "Open the manual in your default web browser."));
        bar.Controls.Add(new Label { Text = "   Search:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        search.Margin = new Padding(3, 6, 3, 3);
        bar.Controls.Add(search);
        bar.Controls.Add(Ui.FlatButton("▲", () => Find(-1), "Previous match (Shift+Enter)."));
        bar.Controls.Add(Ui.FlatButton("▼", () => Find(+1), "Next match (Enter)."));
        bar.Controls.Add(found);
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(e.Shift ? -1 : +1); } };
        search.TextChanged += (_, _) => { searched = ""; found.Text = ""; };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; search.Focus(); search.SelectAll(); }
            else if (e.KeyCode == Keys.Escape) Close();
        };
        browser.DocumentCompleted += (_, _) => { searched = ""; found.Text = ""; };
        Controls.Add(browser);
        Controls.Add(bar);
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        Ui.FitToScreen(this, 1100, 900);
    }

    void Go(string anchor) => browser.Navigate(new Uri(page).AbsoluteUri + "#" + anchor);

    void Find(int dir)
    {
        string term = search.Text.Trim();
        if (browser.Document == null) return;
        try
        {
            if (term != searched)
            {
                searched = term;
                object? n = browser.Document.InvokeScript("mhoSearch", [term]);
                if (term.Length == 0) { found.Text = ""; return; }
                if (Convert.ToInt32(n) == 0) { found.Text = "No Matches"; return; }
            }
            found.Text = Ui.TitleCase(browser.Document.InvokeScript("mhoNext", [dir])?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or System.Runtime.InteropServices.COMException) { found.Text = "Search Isn't Available"; }
    }

    void ChangeSize(int step)
    {
        settings.ManualTextSize = Math.Clamp(settings.ManualTextSize + step, 50, 250);
        settings.Save();
        string anchor = browser.Url?.Fragment.TrimStart('#') is { Length: > 0 } f ? f : "contents";
        if (Render(Zoom(DeviceDpi, settings.ManualTextSize)) is string file) { page = file; browser.Navigate("about:blank"); Go(anchor); }
    }
}
