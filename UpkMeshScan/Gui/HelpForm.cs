using System.Diagnostics;

namespace UpkMeshScan.Gui;

/// <summary>
/// The manual (Help/manual.html next to the exe) in its own window, opened at a section. The command reference is
/// filled in from CommandCatalog, so it always matches the tool; the page is written to %TEMP% with the chosen theme
/// and shown there (a file URL keeps #section links working), or opened in the default browser.
/// </summary>
sealed class HelpForm : Form
{
    static HelpForm? open;
    readonly WebBrowser browser = new() { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true, IsWebBrowserContextMenuEnabled = true, AllowWebBrowserDrop = false };
    readonly string page;

    public static void Show(Form owner, string anchor, bool dark)
    {
        string? file = Render(dark);
        if (file == null) { MessageBox.Show(owner, "The manual (Help\\manual.html) isn't next to the app. Copy the whole app folder, not only the .exe.", "Help"); return; }
        if (open == null || open.IsDisposed) { open = new HelpForm(file); open.Show(owner); }
        open.Go(anchor);
        open.Activate();
    }

    internal static string? Render(bool dark)
    {
        string src = Path.Combine(AppContext.BaseDirectory, "Help", "manual.html");
        if (!File.Exists(src)) return null;
        string html = File.ReadAllText(src)
            .Replace("<!--COMMANDS-->", CommandCatalog.HtmlReference())
            .Replace("<!--VERSION-->", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "")
            .Replace("<body>", dark ? "<body class=\"dark\">" : "<body>");
        string file = Path.Combine(Path.GetTempPath(), "MHO_Package_Modifier_manual.html");
        File.WriteAllText(file, html);
        return file;
    }

    HelpForm(string file)
    {
        page = file;
        Text = "MHO Package Modifier: manual";
        Width = 1100; Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (_, _) => a(); bar.Controls.Add(b); return b; }
        B("Contents", () => Go("contents"));
        B("Back", () => browser.GoBack());
        B("Open in web browser", () => Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true }));
        Controls.Add(browser);
        Controls.Add(bar);
    }

    void Go(string anchor) => browser.Navigate(new Uri(page).AbsoluteUri + "#" + anchor);
}
