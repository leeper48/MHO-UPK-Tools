using System.Text;

namespace UpkMeshScan.Gui;

/// <summary>Small layout helpers shared by the tabs: labelled rows, hints, headings, file pickers, number boxes.</summary>
sealed partial class MainForm
{
    /// <summary>A three-column form: label, field (stretches), optional buttons.</summary>
    sealed class FieldGrid : TableLayoutPanel
    {
        public FieldGrid()
        {
            ColumnCount = 3; Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(4);
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        public void Row(string label, Control field, Control? extra = null)
        {
            int r = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(Lbl(label), 0, r);
            field.Dock = field is TextBox or ComboBox ? DockStyle.Fill : field.Dock;
            Controls.Add(field, 1, r);
            if (extra != null) Controls.Add(extra, 2, r);
        }

        public void Full(Control c)
        {
            int r = RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(c, 0, r);
            SetColumnSpan(c, 3);
        }
    }

    /// <summary>Explanatory text in the subtle colour, wrapped at the given width.</summary>
    static Label Hint(string text, int width = 1150) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Tag = "hint", Margin = new Padding(3, 2, 3, 6) };

    static Label Heading(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), Tag = "heading", Margin = new Padding(3, 8, 3, 2) };

    /// <summary>A group box that sizes to its content (a FieldGrid or flow inside).</summary>
    static GroupBox Section(string title, Control content)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 8), Margin = new Padding(3, 6, 3, 6) };
        content.Dock = DockStyle.Top;
        g.Controls.Add(content);
        return g;
    }

    /// <summary>A scrollable page that stacks its sections top to bottom (first argument at the top).</summary>
    static TabPage Stacked(string title, params Control[] parts)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(6) };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var p in parts) { stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.Dock = DockStyle.Fill; stack.Controls.Add(p); }
        page.Controls.Add(stack);
        return page;
    }

    /// <summary>Browse button for a file box: open (or save) dialog, remembering the box's folder.</summary>
    Button FileButton(TextBox box, string filter, bool save = false) => Btn(save ? "Save as…" : "Browse…", () =>
    {
        string start = box.Text.Length > 0 ? box.Text : exportFolder.Text;
        FileDialog d = save ? new SaveFileDialog { Filter = filter, OverwritePrompt = false } : new OpenFileDialog { Filter = filter };
        using (d)
        {
            try { d.InitialDirectory = File.Exists(start) ? Path.GetDirectoryName(start) : Directory.Exists(start) ? start : ""; d.FileName = File.Exists(start) ? Path.GetFileName(start) : ""; } catch (ArgumentException) { }
            if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName;
        }
    });

    const string UpkFilter = "UE3 packages (*.upk;*.umap)|*.upk;*.umap|All files|*.*";
    const string FbxFilter = "FBX (*.fbx)|*.fbx|All files|*.*";

    static NumericUpDown Num(decimal value, decimal min, decimal max, int decimals = 0, decimal step = 1) =>
        new() { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Value = value, Width = 130 };

    /// <summary>Columns as wide as their content or header (fixed pixel widths don't follow Windows display scaling); the last one fills.</summary>
    static void FitColumns(ListView lv)
    {
        if (lv.Columns.Count == 0) return;
        for (int i = 0; i < lv.Columns.Count - 1; i++)
        {
            lv.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.HeaderSize);
            int header = lv.Columns[i].Width;
            if (lv.Items.Count > 0) { lv.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent); lv.Columns[i].Width = Math.Max(lv.Columns[i].Width + 12, header); }
            if (lv.ClientSize.Width > 200) lv.Columns[i].Width = Math.Min(lv.Columns[i].Width, lv.ClientSize.Width * 2 / 5);
        }
        FillLastColumn(lv);
    }

    static string F(decimal v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Splits a command line into arguments ("quoted parts" stay together, quotes removed).</summary>
    static string[] SplitArgs(string line)
    {
        var args = new List<string>(); var cur = new StringBuilder(); bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (any || cur.Length > 0) args.Add(cur.ToString()); cur.Clear(); any = false; continue; }
            cur.Append(c);
        }
        if (any || cur.Length > 0) args.Add(cur.ToString());
        return [.. args];
    }

    static string Quote(string s) => s.Length == 0 || s.Any(char.IsWhiteSpace) ? $"\"{s}\"" : s;

    /// <summary>--gui-snapshot: each tab rendered to a PNG (a layout check without clicking through the app).</summary>
    public void Snapshot(string dir)
    {
        Directory.CreateDirectory(dir);
        if (HelpForm.Render(dark: false) is string manual) File.Copy(manual, Path.Combine(dir, "manual.html"), overwrite: true);
        for (int i = 0; i < tabs.TabPages.Count; i++)
        {
            tabs.SelectedIndex = i;
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"{i:D2}_{tabs.TabPages[i].Text}.png"));
            // A long page is also rendered whole (scroll content), to see what's below the fold.
            if (tabs.TabPages[i].AutoScroll && tabs.TabPages[i].Controls.Count > 0 && tabs.TabPages[i].Controls[0].Height > tabs.TabPages[i].ClientSize.Height)
            {
                var c = tabs.TabPages[i].Controls[0];
                using var full = new Bitmap(c.Width, c.Height);
                c.DrawToBitmap(full, new Rectangle(0, 0, c.Width, c.Height));
                full.Save(Path.Combine(dir, $"{i:D2}_{tabs.TabPages[i].Text}_full.png"));
            }
        }
    }

    /// <summary>Runs a command through the same entry point as the command line (Program.Run), in the background.</summary>
    void RunCommand(string title, string[] args, bool writes, Action? after = null)
    {
        Log("> MHO_UPK_Mod " + string.Join(' ', args.Select(Quote)));
        Run(title, () => Program.Run(args, ""), after, writes);
    }
}
