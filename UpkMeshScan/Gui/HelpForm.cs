using System.Diagnostics;

namespace UpkMeshScan.Gui;

/// <summary>
/// The manual (Help/manual.html next to the exe) in its own window, opened at a section. The command reference is
/// filled in from CommandCatalog, so it always matches the tool; the page is written to %TEMP% with the chosen theme
/// and shown there (a file URL keeps #section links working), or opened in the default browser. The built-in browser
/// (IE engine) ignores Windows display scaling, so the page is zoomed by the screen's scale times the user's A-/A+ size.
/// </summary>
sealed class HelpForm : Form
{
    static HelpForm? open;
    readonly WebBrowser browser = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = true, AllowWebBrowserDrop = false };
    string page;
    readonly bool dark;
    readonly int dpi;
    int textSize;                                                          // the user's size in percent (A- / A+)
    readonly Action<int> saveTextSize;
    readonly TextBox search = new() { Width = 260, PlaceholderText = "Search the manual (Enter = next)" };
    readonly Label found = new() { AutoSize = true, Padding = new Padding(6, 8, 0, 0) };
    string searched = "";

    public static void Show(Form owner, string anchor, bool dark, int textSize, Action<int> saveTextSize)
    {
        string? file = Render(dark, Zoom(owner.DeviceDpi, textSize));
        if (file == null) { MessageBox.Show(owner, "The manual (Help\\manual.html) isn't next to the app. Copy the whole app folder, not only the .exe.", "Help"); return; }
        if (open == null || open.IsDisposed || open.dark != dark) { open?.Close(); open = new HelpForm(file, dark, owner.DeviceDpi, textSize, saveTextSize) { Icon = owner.Icon }; open.Show(owner); }
        open.Go(anchor);
        open.Activate();
    }

    static int Zoom(int dpi, int textSize) => (int)Math.Round(dpi / 96.0 * textSize);

    internal static string? Render(bool dark, int zoomPercent = 100, string fileName = "MHO_Package_Modifier_manual.html")
    {
        string src = Path.Combine(AppContext.BaseDirectory, "Help", "manual.html");
        if (!File.Exists(src)) return null;
        string html = File.ReadAllText(src)
            .Replace("<!--COMMANDS-->", CommandCatalog.HtmlReference())
            .Replace("<!--VERSION-->", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "")
            .Replace("<body>", dark ? "<body class=\"dark\">" : "<body>")
            .Replace("</head>", zoomPercent == 100 ? "</head>" : $"<style>body {{ zoom: {zoomPercent}%; }}</style></head>");
        string file = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(file, html);
        return file;
    }

    HelpForm(string file, bool dark, int dpi, int textSize, Action<int> saveTextSize)
    {
        page = file; this.dark = dark; this.dpi = dpi; this.textSize = textSize; this.saveTextSize = saveTextSize;
        Text = "MHO Package Modifier: manual";
        Width = 1100; Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => a(); bar.Controls.Add(b); return b; }
        B("Contents", () => Go("contents"));
        B("Back", () => browser.GoBack());
        B("A−  smaller text", () => ChangeSize(-10));
        B("A+  larger text", () => ChangeSize(+10));
        // A real browser scales for the screen itself: that copy gets no zoom.
        B("Open in web browser", () => { if (Render(dark, 100, "MHO_Package_Modifier_manual_browser.html") is string f) Process.Start(new ProcessStartInfo { FileName = f, UseShellExecute = true }); });
        bar.Controls.Add(new Label { Text = "   Search:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        search.Margin = new Padding(3, 6, 3, 3);
        bar.Controls.Add(search);
        B("▲", () => Find(-1));
        B("▼", () => Find(+1));
        bar.Controls.Add(found);
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(e.Shift ? -1 : +1); } };
        search.TextChanged += (_, _) => { searched = ""; found.Text = ""; };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; search.Focus(); search.SelectAll(); } };
        browser.DocumentCompleted += (_, _) => { searched = ""; found.Text = ""; };
        Controls.Add(browser);
        Controls.Add(bar);
    }

    void Go(string anchor) => browser.Navigate(new Uri(page).AbsoluteUri + "#" + anchor);

    /// <summary>Marks every match of the search text in the page (the page's own script), then moves to the next or previous one.</summary>
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
                if (Convert.ToInt32(n) == 0) { found.Text = "no matches"; return; }
            }
            found.Text = browser.Document.InvokeScript("mhoNext", [dir])?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or System.Runtime.InteropServices.COMException) { found.Text = "search isn't available"; }
    }

    /// <summary>Text size in 10% steps (50-250%), remembered; the page reopens at the section being read.</summary>
    void ChangeSize(int step)
    {
        textSize = Math.Clamp(textSize + step, 50, 250);
        saveTextSize(textSize);
        string anchor = browser.Url?.Fragment.TrimStart('#') is { Length: > 0 } f ? f : "contents";
        if (Render(dark, Zoom(dpi, textSize)) is string file) { page = file; browser.Navigate("about:blank"); Go(anchor); }
    }
}
