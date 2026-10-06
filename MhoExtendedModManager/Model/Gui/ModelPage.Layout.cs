using MhoExtendedModManager.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Model tab's layout (Kurt, 2026-10-06: a user's screen was too small; panels collapsible and resizable): dividers to drag
/// between Source, Preview and Target, between Target's list and its Parts / Bone Map / Materials tabs, and above the log;
/// a click on a heading (SOURCE, TARGET, MATERIAL, SIZE IN GAME, LOG) folds that panel away or back. Kept in the Model tab's
/// settings (model\settings.json: Layout, Folded).
/// </summary>
sealed partial class ModelPage
{
    TableLayoutPanel layoutRoot = null!, layoutBody = null!, layoutRight = null!;
    DragBar barSource = null!, barPreview = null!, barTarget = null!, barLog = null!;

    static Dictionary<string, float> LayoutPrefs => Settings.Current.Layout;
    static float Pref(string key, float fallback) => LayoutPrefs.TryGetValue(key, out float v) && v > 0 ? v : fallback;
    static void SaveLayout() => Settings.Current.Save();
    bool Folded(string key) => Settings.Current.Folded.Contains(key);

    /// <summary>The column widths (percent) and the right column's split, from the settings.</summary>
    void ApplyLayout()
    {
        float s = Ui.Dpi(DeviceDpi);
        layoutBody.ColumnStyles[0] = Folded("source") ? new ColumnStyle(SizeType.Absolute, 110 * s) : new ColumnStyle(SizeType.Percent, Pref("col0", 27));
        layoutBody.ColumnStyles[2] = new ColumnStyle(SizeType.Percent, Pref("col1", 40));
        layoutBody.ColumnStyles[4] = new ColumnStyle(SizeType.Percent, Pref("col2", 33));
        barSource.Visible = !Folded("source");
        layoutRight.RowStyles[0] = Folded("target") ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Percent, Pref("target", 55));
        layoutRight.RowStyles[2] = new RowStyle(SizeType.Percent, Pref("tabs", 45));
        barTarget.Visible = !Folded("target");
        layoutRoot.RowStyles[3] = Folded("log") ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, Pref("log", 90) * s);
        barLog.Visible = !Folded("log");
    }

    /// <summary>A divider between two percent columns (or rows) of a table: dragging moves the share between them.</summary>
    DragBar Divider(TableLayoutPanel table, bool vertical, int before, int after, string keyBefore, string keyAfter)
    {
        var bar = new DragBar(vertical) { Margin = new Padding(0) };
        bar.Dragged += delta =>
        {
            var styles = vertical ? table.ColumnStyles.Cast<TableLayoutStyle>().ToList() : table.RowStyles.Cast<TableLayoutStyle>().ToList();
            if (styles[before].SizeType != SizeType.Percent || styles[after].SizeType != SizeType.Percent) return;
            // the pixels both share, to percent
            int[] sizes = vertical ? table.GetColumnWidths() : table.GetRowHeights();
            float px = sizes[before] + sizes[after], pct = Width(styles[before]) + Width(styles[after]);
            if (px < 20) return;
            float minPx = 80 * Ui.Dpi(DeviceDpi);
            float newBefore = Math.Clamp(sizes[before] + delta, minPx, px - minPx);
            float a = pct * newBefore / px, b = pct - a;
            Set(styles[before], a); Set(styles[after], b);
            LayoutPrefs[keyBefore] = a; LayoutPrefs[keyAfter] = b;
        };
        bar.Released += SaveLayout;
        return bar;

        static float Width(TableLayoutStyle st) => st is ColumnStyle c ? c.Width : ((RowStyle)st).Height;
        static void Set(TableLayoutStyle st, float v) { if (st is ColumnStyle c) c.Width = v; else ((RowStyle)st).Height = v; }
    }

    /// <summary>The divider above the log: drags its height (pixels at 96 DPI in the settings).</summary>
    DragBar LogDivider()
    {
        var bar = new DragBar(false) { Margin = new Padding(0) };
        bar.Dragged += delta =>
        {
            float s = Ui.Dpi(DeviceDpi);
            if (layoutRoot.RowStyles[3].SizeType != SizeType.Absolute) return;
            float h = Math.Clamp(layoutRoot.RowStyles[3].Height - delta, 40 * s, Math.Max(60 * s, layoutRoot.Height * 0.6f));
            layoutRoot.RowStyles[3].Height = h;
            LayoutPrefs["log"] = h / s;
        };
        bar.Released += SaveLayout;
        return bar;
    }

    /// <summary>A panel made by Column() folds away with a click on its heading (▾ open, ▸ folded); <paramref name="key"/> keeps it.</summary>
    void Foldable(Control column, string key)
    {
        if (column is not TableLayoutPanel t || t.GetControlFromPosition(0, 0) is not Label label) return;
        string caption = label.Text;
        label.Cursor = Cursors.Hand;
        void Show()
        {
            bool folded = Folded(key);
            label.Text = (folded ? "▸ " : "▾ ") + caption;
            foreach (Control c in t.Controls) if (c != label) c.Visible = !folded;
        }
        label.Click += (_, _) =>
        {
            if (!Settings.Current.Folded.Remove(key)) Settings.Current.Folded.Add(key);
            SaveLayout();
            Show();
            ApplyLayout();
        };
        Ui.Tip(label, $"Click to fold {caption.ToLowerInvariant()} away or show it again; drag the dividers between the panels to resize them.");
        Show();
    }
}

/// <summary>A thin bar to drag (a divider between panels): a line on hover, the resize cursor, the moved pixels reported.</summary>
sealed class DragBar : Control
{
    readonly bool vertical;
    bool hover, down;
    Point start;
    /// <summary>The mouse moved by this many pixels (across the bar) since the last report, while held.</summary>
    public event Action<int>? Dragged;
    public event Action? Released;

    public DragBar(bool vertical)
    {
        this.vertical = vertical;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = Color.Transparent;
        Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS;
        Dock = DockStyle.Fill;
        Ui.Tip(this, "Drag to resize the panels on both sides.");
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; start = Cursor.Position; Capture = true; } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (down)
        {
            var now = Cursor.Position;
            int d = vertical ? now.X - start.X : now.Y - start.Y;
            if (d != 0) { start = now; Dragged?.Invoke(d); }
        }
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { if (down) { down = false; Capture = false; Released?.Invoke(); } base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(hover || down ? Ui.Accent : Color.FromArgb(40, 255, 255, 255), hover || down ? 2 : 1);
        if (vertical) e.Graphics.DrawLine(pen, Width / 2, 6, Width / 2, Height - 6);
        else e.Graphics.DrawLine(pen, 6, Height / 2, Width - 6, Height / 2);
    }
}
