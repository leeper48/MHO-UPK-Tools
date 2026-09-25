using System.Runtime.InteropServices;

namespace UpkMeshScan.Gui;

/// <summary>Colours for one look (dark or light).</summary>
sealed record Palette(
    Color Back, Color Panel, Color Field, Color Text, Color Subtle, Color Border,
    Color Button, Color ButtonHover, Color Header, Color Selection, Color SelectionText, Color Changed, bool IsDark)
{
    public static readonly Palette Dark = new(
        Back: Color.FromArgb(30, 30, 30), Panel: Color.FromArgb(37, 37, 38), Field: Color.FromArgb(24, 24, 24),
        Text: Color.FromArgb(222, 222, 222), Subtle: Color.FromArgb(140, 140, 140), Border: Color.FromArgb(70, 70, 70),
        Button: Color.FromArgb(51, 51, 55), ButtonHover: Color.FromArgb(66, 66, 72), Header: Color.FromArgb(45, 45, 48),
        Selection: Color.FromArgb(38, 79, 120), SelectionText: Color.White, Changed: Color.FromArgb(92, 74, 24), IsDark: true);

    public static readonly Palette Light = new(
        Back: SystemColors.Control, Panel: SystemColors.Control, Field: SystemColors.Window,
        Text: SystemColors.ControlText, Subtle: SystemColors.GrayText, Border: SystemColors.ControlDark,
        Button: SystemColors.Control, ButtonHover: SystemColors.ControlLight, Header: SystemColors.Control,
        Selection: SystemColors.Highlight, SelectionText: SystemColors.HighlightText, Changed: Color.LightYellow, IsDark: false);
}

/// <summary>
/// Applies a Palette to a control tree. WinForms on .NET 8 has no dark mode of its own, so: colours on every
/// control, flat buttons, themed DataGridView and ListView headers (owner-drawn), a user-painted tab strip
/// (ThemedTabControl), and — where Windows supports it — the dark title bar and dark scrollbars.
/// Per-cell colours set elsewhere (colour swatches) are left alone.
/// </summary>
static class Theme
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>The palette last applied (owner-drawn parts read it when they paint).</summary>
    public static Palette Current { get; private set; } = Palette.Dark;

    public static void Apply(Form form, Palette p)
    {
        Current = p;
        int dark = p.IsDark ? 1 : 0;
        if (form.IsHandleCreated)
        {
            // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on current Windows, 19 on early Windows 10 builds.
            if (DwmSetWindowAttribute(form.Handle, 20, ref dark, 4) != 0) DwmSetWindowAttribute(form.Handle, 19, ref dark, 4);
        }
        form.BackColor = p.Back;
        form.ForeColor = p.Text;
        ApplyTree(form, p);
        form.Invalidate(true);
    }

    static void ApplyTree(Control c, Palette p)
    {
        switch (c)
        {
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = p.Button; b.ForeColor = p.Text;
                b.FlatAppearance.BorderColor = p.Border;
                b.FlatAppearance.MouseOverBackColor = p.ButtonHover;
                b.FlatAppearance.MouseDownBackColor = p.Selection;
                break;
            case TextBox t:
                t.BackColor = t.ReadOnly ? p.Panel : p.Field; t.ForeColor = p.Text; t.BorderStyle = BorderStyle.FixedSingle;
                Scrollbars(t, p);
                break;
            case ComboBox cb:
                cb.BackColor = p.Field; cb.ForeColor = p.Text; cb.FlatStyle = p.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                break;
            case CheckedListBox or ListBox:
                c.BackColor = p.Field; c.ForeColor = p.Text;
                ((ListBox)c).BorderStyle = BorderStyle.FixedSingle;
                Scrollbars(c, p);
                break;
            case ListView lv:
                lv.BackColor = p.Field; lv.ForeColor = p.Text;
                ThemeListView(lv, p);
                Scrollbars(lv, p);
                break;
            case DataGridView g:
                ThemeGrid(g, p);
                break;
            case ThemedTabControl tc:
                tc.Palette = p;
                tc.Invalidate();
                break;
            case TabPage tp:
                tp.BackColor = p.Back; tp.ForeColor = p.Text; tp.UseVisualStyleBackColor = false;
                break;
            case SplitContainer sc:
                sc.BackColor = p.Border;                   // the splitter bar
                sc.Panel1.BackColor = sc.Panel2.BackColor = p.Back;
                break;
            default:
                c.BackColor = c.Parent?.BackColor ?? p.Back;
                c.ForeColor = p.Text;
                break;
        }
        foreach (Control child in c.Controls) ApplyTree(child, p);
    }

    /// <summary>Dark scrollbars (and themed list headers) via the Explorer dark theme, when the handle exists.</summary>
    static void Scrollbars(Control c, Palette p)
    {
        void Set() => SetWindowTheme(c.Handle, p.IsDark ? "DarkMode_Explorer" : "Explorer", null);
        if (c.IsHandleCreated) Set();
        else c.HandleCreated += (_, _) => Set();
    }

    static readonly HashSet<ListView> ownerDrawn = new();

    static void ThemeListView(ListView lv, Palette p)
    {
        if (ownerDrawn.Add(lv))
        {
            // Column headers can't be coloured otherwise; items still draw themselves (DrawDefault).
            lv.OwnerDraw = true;
            // Items draw themselves, except selected rows: the stock selection colours are unreadable on dark.
            lv.DrawItem += (_, e) => { if (!e.Item.Selected) e.DrawDefault = true; };
            lv.DrawSubItem += (s, e) =>
            {
                if (e.Item is not { Selected: true } item) { e.DrawDefault = true; return; }
                var pal = Current;
                using var back = new SolidBrush(pal.Selection);
                e.Graphics.FillRectangle(back, e.Bounds);
                var align = ((ListView)s!).Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, item.Font, e.Bounds, pal.SelectionText,
                    align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
            };
            lv.DrawColumnHeader += (s, e) =>
            {
                var pal = Current;
                using var back = new SolidBrush(pal.Header);
                using var border = new Pen(pal.Border);
                e.Graphics.FillRectangle(back, e.Bounds);
                e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding
                            | (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font, e.Bounds, pal.Text, flags);
            };
        }
        lv.Invalidate();
    }

    static void ThemeGrid(DataGridView g, Palette p)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = p.Field;
        g.GridColor = p.Border;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.DefaultCellStyle.BackColor = p.Field;
        g.DefaultCellStyle.ForeColor = p.Text;
        g.DefaultCellStyle.SelectionBackColor = p.Selection;
        g.DefaultCellStyle.SelectionForeColor = p.SelectionText;
        g.ColumnHeadersDefaultCellStyle.BackColor = p.Header;
        g.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.Header;
        g.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Text;
        g.RowHeadersDefaultCellStyle.BackColor = p.Header;
        g.RowHeadersDefaultCellStyle.ForeColor = p.Text;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        foreach (DataGridViewColumn col in g.Columns)
            if (col is DataGridViewButtonColumn bc) { bc.FlatStyle = FlatStyle.Flat; bc.DefaultCellStyle.BackColor = p.Field; bc.DefaultCellStyle.ForeColor = p.Text; }
        foreach (Control child in g.Controls)
            if (child is ScrollBar sb) Scrollbars(sb, p);
    }
}

/// <summary>A TabControl that paints its own tab strip in the current Palette (the stock one ignores colours).</summary>
sealed class ThemedTabControl : TabControl
{
    public Palette Palette { get; set; } = Palette.Dark;

    public ThemedTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SizeMode = TabSizeMode.Normal;
        Padding = new Point(12, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Palette;
        e.Graphics.Clear(p.Back);
        using var border = new Pen(p.Border);
        if (TabCount > 0)
        {
            var page = DisplayRectangle;
            page.Inflate(1, 1);
            e.Graphics.DrawRectangle(border, page.X, page.Y, page.Width - 1, page.Height - 1);
        }
        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r = GetTabRect(i);
            bool selected = i == SelectedIndex;
            using var back = new SolidBrush(selected ? p.Back : p.Header);
            e.Graphics.FillRectangle(back, r);
            e.Graphics.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - (selected ? 0 : 1));
            if (selected)
            {
                using var accent = new Pen(p.IsDark ? Color.FromArgb(0, 122, 204) : SystemColors.Highlight, 2);
                e.Graphics.DrawLine(accent, r.Left + 1, r.Top + 1, r.Right - 2, r.Top + 1);
            }
            TextRenderer.DrawText(e.Graphics, TabPages[i].Text, Font, r, selected ? p.Text : p.Subtle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
