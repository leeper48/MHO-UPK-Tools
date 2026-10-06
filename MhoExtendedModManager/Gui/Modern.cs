using System.Collections.ObjectModel;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The app's own inputs (Kurt, 2026-09-29: no system-style drop-downs, text boxes or check boxes; everything like the
/// header's Costume ▾): <see cref="DropDown"/> (an accent pill that opens a dark list), <see cref="Field"/> (a rounded
/// frame around a borderless TextBox) and hand-drawn check boxes / radio buttons. <see cref="Modernize"/> converts a
/// window's text boxes and toggles after its theme is applied (Ui.Restyle and every themed window call it; idempotent).
/// </summary>
static class Modern
{
    public static readonly Color FieldBack = Color.FromArgb(40, 40, 46);
    public static readonly Color FieldBorder = Color.FromArgb(72, 72, 84);
    public static readonly Color MenuBack = Color.FromArgb(38, 38, 44);
    public static readonly Color MenuHover = Color.FromArgb(58, 58, 80);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Dark scrollbars on a control (Windows' dark Explorer theme), now or when its window is made.</summary>
    public static void DarkScrollbars(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        else c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    }

    /// <summary>What the parent shows behind <paramref name="c"/>: its colour, or the window gradient when it's see-through.</summary>
    public static void PaintBehind(Control c, Graphics g)
    {
        var r = c.ClientRectangle;
        var back = c.Parent?.BackColor ?? Ui.Back;
        if (back.A < 255) Ui.PaintGradient(g, c, r);
        if (back.A > 0) { using var b = new SolidBrush(back); g.FillRectangle(b, r); }
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// The window's text boxes framed (<see cref="Field"/>) and check boxes / radio buttons hand-drawn. Read-only multi-line
    /// boxes (logs, message bodies) aren't framed, and grids' own editors aren't touched.
    /// </summary>
    public static void Modernize(Control root)
    {
        var boxes = new List<TextBox>();
        void Walk(Control c)
        {
            if (c is TextBox tb && tb.Parent is not Field && tb.Parent is not DataGridView && !(tb.ReadOnly && tb.Multiline)) boxes.Add(tb);
            else if (c is CheckBox or RadioButton) Toggle((ButtonBase)c);
            foreach (Control k in c.Controls) Walk(k);
        }
        Walk(root);
        foreach (var tb in boxes) Field.Wrap(tb);
    }

    static readonly ConditionalWeakTable<ButtonBase, object> toggles = [];

    /// <summary>A check box or radio button drawn in the app's style (rounded box / circle, accent when on).</summary>
    public static void Toggle(ButtonBase b)
    {
        if (toggles.TryGetValue(b, out _)) return;
        toggles.Add(b, new object());
        // Our box is a little bigger than Windows' glyph: give auto-sized ones the room so their text isn't cut.
        if (b.AutoSize) b.Padding = new Padding(b.Padding.Left, b.Padding.Top, b.Padding.Right + (int)(8 * MhoExtendedModManager.Gui.Ui.Dpi(b.DeviceDpi)), b.Padding.Bottom);
        if (b is CheckBox cb) cb.CheckStateChanged += (_, _) => b.Invalidate();
        if (b is RadioButton rb) rb.CheckedChanged += (_, _) => b.Invalidate();
        b.MouseEnter += (_, _) => b.Invalidate();
        b.MouseLeave += (_, _) => b.Invalidate();
        b.EnabledChanged += (_, _) => b.Invalidate();
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var r = b.ClientRectangle;
            if (r.Width < 4 || r.Height < 4) return;
            PaintBehind(b, g);
            float s = MhoExtendedModManager.Gui.Ui.Dpi(b.DeviceDpi);
            float box = 14 * s;
            bool on = b is CheckBox c ? c.CheckState != CheckState.Unchecked : ((RadioButton)b).Checked;
            bool mixed = b is CheckBox c2 && c2.CheckState == CheckState.Indeterminate;
            bool hot = b.Enabled && b.ClientRectangle.Contains(b.PointToClient(Cursor.Position));
            var boxRect = new RectangleF(1 * s, (r.Height - box) / 2f, box, box);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = !b.Enabled ? Color.FromArgb(50, 50, 56) : on ? Ui.Accent : FieldBack;
            var border = !b.Enabled ? FieldBorder : on ? Ui.Accent : hot ? Ui.AccentHover : Color.FromArgb(110, 110, 124);
            using (var path = b is RadioButton ? Ellipse(boxRect) : Round(boxRect, 3 * s))
            {
                using (var f = new SolidBrush(b is RadioButton && on ? FieldBack : fill)) g.FillPath(f, path);
                using var pen = new Pen(border, 1.2f * s); g.DrawPath(pen, path);
            }
            if (on && b is RadioButton)
            {
                var dot = RectangleF.Inflate(boxRect, -3.5f * s, -3.5f * s);
                using var f = new SolidBrush(b.Enabled ? Ui.Accent : Ui.DisabledText); g.FillEllipse(f, dot);
            }
            else if (on)
            {
                using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                if (mixed) g.DrawLine(pen, boxRect.X + box * 0.25f, boxRect.Y + box / 2, boxRect.Right - box * 0.25f, boxRect.Y + box / 2);
                else g.DrawLines(pen, [new PointF(boxRect.X + box * 0.22f, boxRect.Y + box * 0.52f), new PointF(boxRect.X + box * 0.43f, boxRect.Y + box * 0.73f), new PointF(boxRect.X + box * 0.8f, boxRect.Y + box * 0.3f)]);
            }
            g.SmoothingMode = SmoothingMode.None;
            var text = new Rectangle((int)(boxRect.Right + 6 * s), 0, Math.Max(1, r.Width - (int)(boxRect.Right + 6 * s)), r.Height);
            TextRenderer.DrawText(g, b.Text, b.Font, text, b.Enabled ? (b.ForeColor.IsEmpty ? Ui.Text : b.ForeColor) : Ui.DisabledText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | (b.AutoSize ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak));
        };
    }

    static GraphicsPath Ellipse(RectangleF r) { var p = new GraphicsPath(); p.AddEllipse(r); return p; }
}

/// <summary>A rounded frame around a borderless TextBox (the box keeps its own events and text); accent border when focused.</summary>
sealed class Field : Panel
{
    public TextBox Box { get; }
    bool focused;

    Field(TextBox tb)
    {
        Box = tb;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        tb.GotFocus += (_, _) => { focused = true; Invalidate(); };
        tb.LostFocus += (_, _) => { focused = false; Invalidate(); };
        tb.EnabledChanged += (_, _) => { tb.BackColor = tb.Enabled ? Modern.FieldBack : Color.FromArgb(34, 34, 40); Invalidate(); };
        // A theme applied again (MPM's Theme gives text boxes a border): keep ours.
        tb.BorderStyleChanged += (_, _) => { if (tb.BorderStyle != BorderStyle.None) BeginInvoke(() => tb.BorderStyle = BorderStyle.None); };
        MouseDown += (_, _) => tb.Focus();
        Cursor = Cursors.IBeam;
        UseCueBanner(tb);
    }

    /// <summary>
    /// The box's hint drawn by Windows itself (EM_SETCUEBANNER) instead of .NET's PlaceholderText (2026-10-03, Kurt: the Model
    /// tab was slow before anything was loaded). Measured: an empty box showing a PlaceholderText kept the whole window
    /// repainting, about 3 times a second at ~220 ms each (the main window draws composited); with text in the box, or with
    /// the box hidden, it was idle. .NET draws that hint after the box's own paint; Windows' hint is part of it. Single-line
    /// boxes only (Windows has no hint for multi-line ones).
    /// </summary>
    static void UseCueBanner(TextBox tb)
    {
        if (tb.Multiline || string.IsNullOrEmpty(tb.PlaceholderText)) return;
        string cue = tb.PlaceholderText;
        tb.PlaceholderText = "";
        void Set() => SendMessage(tb.Handle, 0x1501, 1, cue);   // EM_SETCUEBANNER, shown while focused too (as before)
        if (tb.IsHandleCreated) Set();
        tb.HandleCreated += (_, _) => Set();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);

    /// <summary>Puts <paramref name="tb"/> in a frame at its place (same parent position / table cell, dock, anchor, margin, size).</summary>
    public static void Wrap(TextBox tb)
    {
        var parent = tb.Parent;
        if (parent == null || tb.Parent is Field) return;
        float s = MhoExtendedModManager.Gui.Ui.Dpi(tb.DeviceDpi);
        var f = new Field(tb)
        {
            Dock = tb.Dock, Anchor = tb.Anchor, Margin = tb.Margin, MinimumSize = tb.MinimumSize,
            Padding = tb.Multiline ? new Padding((int)(8 * s), (int)(6 * s), (int)(3 * s), (int)(5 * s)) : new Padding((int)(8 * s), (int)(5 * s), (int)(6 * s), (int)(4 * s)),
        };
        int width = tb.Width;
        TableLayoutPanelCellPosition? cell = parent is TableLayoutPanel tl ? tl.GetCellPosition(tb) : null;
        int colSpan = parent is TableLayoutPanel t1 ? t1.GetColumnSpan(tb) : 1, rowSpan = parent is TableLayoutPanel t2 ? t2.GetRowSpan(tb) : 1;
        int index = parent.Controls.GetChildIndex(tb);
        parent.SuspendLayout();
        parent.Controls.Remove(tb);
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = tb.Enabled ? Modern.FieldBack : Color.FromArgb(34, 34, 40);
        tb.ForeColor = Ui.Text;
        tb.Margin = Padding.Empty;
        tb.Dock = DockStyle.Fill;
        tb.MinimumSize = Size.Empty;
        f.Controls.Add(tb);
        if (!tb.Multiline) f.Height = tb.PreferredHeight + f.Padding.Vertical;
        else f.Height = Math.Max(tb.Height, (int)(40 * s));
        f.Width = width + f.Padding.Horizontal - 2;
        if (cell is { } c && parent is TableLayoutPanel t)
        {
            t.Controls.Add(f, c.Column, c.Row);
            t.SetColumnSpan(f, colSpan); t.SetRowSpan(f, rowSpan);
        }
        else { parent.Controls.Add(f); parent.Controls.SetChildIndex(f, index); }
        f.Visible = tb.Visible || !parent.Visible;
        parent.ResumeLayout(true);
        Modern.DarkScrollbars(tb);
    }

    public override Size GetPreferredSize(Size proposed) =>
        Box.Multiline ? base.GetPreferredSize(proposed) : new Size(Width, Box.PreferredHeight + Padding.Vertical);

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Modern.PaintBehind(this, g);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Modern.Round(r, 5 * s);
        using (var b = new SolidBrush(Box.BackColor)) g.FillPath(b, path);
        using var pen = new Pen(!Box.Enabled ? Modern.FieldBorder : focused ? Ui.Accent : Modern.FieldBorder, focused ? 1.4f * s : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// The app's drop-down (Kurt: like the header's Costume ▾): an accent-tinted rounded pill with the chosen item and ▾;
/// a click (or Enter / Space / F4 / Alt+Down) opens a dark list (<see cref="DropList"/>), searchable when it's long;
/// Up / Down step through the items. Items, SelectedIndex, SelectedItem and SelectedIndexChanged work like a ComboBox's.
/// </summary>
sealed class DropDown : Control
{
    public sealed class ItemList(DropDown owner) : Collection<object>
    {
        public void AddRange(IEnumerable<object> items) { foreach (var i in items) Add(i); }
        protected override void ClearItems() { base.ClearItems(); owner.sel = -1; owner.Invalidate(); }
        protected override void InsertItem(int index, object item) { base.InsertItem(index, item); if (owner.sel >= index) owner.sel++; owner.Invalidate(); }
        protected override void RemoveItem(int index) { base.RemoveItem(index); if (owner.sel == index) owner.sel = -1; else if (owner.sel > index) owner.sel--; owner.Invalidate(); }
        protected override void SetItem(int index, object item) { base.SetItem(index, item); owner.Invalidate(); }
    }

    int sel = -1;
    bool hover, open;
    /// <summary>Its list is open (the 3D preview keeps its playback bar shown meanwhile).</summary>
    public bool IsOpen => open;
    public ItemList Items { get; }
    public event EventHandler? SelectedIndexChanged;
    /// <summary>Just before the list opens (fill or refresh the items here).</summary>
    public event EventHandler? Opening;
    /// <summary>An item's text (default: ToString()).</summary>
    public Func<object, string>? Format { get; set; }
    /// <summary>An item's own text colour (e.g. a default costume in green), by index.</summary>
    public Func<int, Color?>? ItemColor { get; set; }
    /// <summary>Shown when nothing is chosen.</summary>
    public string Placeholder { get; set; } = "";
    public int MaxDropDownItems { get; set; } = 18;
    /// <summary>False: a menu of choices that always shows <see cref="Placeholder"/> (e.g. "Found ▾" beside a text field).</summary>
    public bool ShowsSelection { get => showsSelection; set { showsSelection = value; FitHeight(); } }
    bool showsSelection = true;

    public DropDown()
    {
        Items = new ItemList(this);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor | ControlStyles.StandardClick, true);
        BackColor = Color.Transparent;
        ForeColor = Ui.Text;
        TabStop = true;
        Font = Ui.Regular(9.5f);
        Size = new Size(200, 28);
        FitHeight();
    }

    void FitHeight()
    {
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        Height = TextRenderer.MeasureText("Ag", Font).Height + (int)(10 * s);
        if (!ShowsSelection && Placeholder.Length > 0) Width = TextRenderer.MeasureText(Placeholder, Font).Width + (int)(40 * s);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitHeight(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); FitHeight(); }
    public override Size GetPreferredSize(Size proposed) => new(Width, Height);

    public void BeginUpdate() { }
    public void EndUpdate() { Invalidate(); }

    public int SelectedIndex
    {
        get => sel;
        set
        {
            if (value < -1 || value >= Items.Count) value = -1;
            if (value == sel) return;
            sel = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? SelectedItem
    {
        get => sel >= 0 && sel < Items.Count ? Items[sel] : null;
        set => SelectedIndex = value == null ? -1 : Items.IndexOf(value);
    }

    public string TextOf(object o) => Format?.Invoke(o) ?? o?.ToString() ?? "";

#pragma warning disable CS8765 // matches Control.Text's nullability loosely
    public override string Text { get => SelectedItem is { } o ? TextOf(o) : ""; set { } }
#pragma warning restore CS8765

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        Open();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        if (e.KeyCode is Keys.F4 || (e.Alt && e.KeyCode == Keys.Down) || e.KeyCode is Keys.Enter or Keys.Space) { Open(); e.Handled = true; }
        else if (e.KeyCode == Keys.Down) { if (sel < Items.Count - 1) SelectedIndex = sel + 1; e.Handled = true; }
        else if (e.KeyCode == Keys.Up) { if (sel > 0) SelectedIndex = sel - 1; e.Handled = true; }
    }

    /// <summary>Opens the list under the control.</summary>
    public void Open()
    {
        if (open || !Enabled) return;
        Opening?.Invoke(this, EventArgs.Empty);
        if (Items.Count == 0) return;
        open = true; Invalidate();
        var texts = Items.Select(TextOf).ToList();
        var list = DropList.Show(this, RectangleToScreen(ClientRectangle), texts, sel, i => { SelectedIndex = i; Focus(); }, ItemColor, MaxDropDownItems);
        list.Closed += (_, _) => { open = false; if (!IsDisposed) Invalidate(); };
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Modern.PaintBehind(this, g);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Modern.Round(r, 4 * s))
        {
            var fill = !Enabled ? Color.FromArgb(24, 150, 150, 160) : Color.FromArgb(open ? 80 : hover ? 62 : 40, Ui.Accent);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            var border = !Enabled ? Modern.FieldBorder : Focused || open ? Ui.AccentHover : Ui.Accent;
            using var pen = new Pen(border, Focused ? 1.4f * s : 1f); g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        int pad = (int)(10 * s), arrow = (int)(18 * s);
        var textRect = new Rectangle(pad, 0, Math.Max(1, Width - pad - arrow - (int)(4 * s)), Height);
        string text = ShowsSelection && SelectedItem is { } o ? TextOf(o) : Placeholder;
        var color = !Enabled ? Ui.DisabledText : !ShowsSelection ? ForeColor : SelectedItem == null ? Ui.Subtle : ItemColor?.Invoke(sel) ?? ForeColor;
        TextRenderer.DrawText(g, text, Font, textRect, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, "▾", Font, new Rectangle(Width - arrow - (int)(4 * s), 0, arrow, Height), Enabled ? Ui.Text : Ui.DisabledText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// The dark list a <see cref="DropDown"/> (or a table cell) opens: the items, the chosen one marked with an accent check,
/// the row under the mouse highlighted; with many items a search box on top (type to narrow, Down into the list, Enter
/// picks). Enter / click picks, Esc closes. Placed under its anchor, or above it when there's no room, on the anchor's screen.
/// </summary>
sealed class DropList : ToolStripDropDown
{
    readonly ListBox list;
    readonly TextBox? search;
    readonly List<string> texts;
    readonly Func<int, Color?>? color;
    readonly Action<int> pick;
    readonly int selected;
    List<int> shown;

    DropList(Control owner, Rectangle anchor, List<string> texts, int selected, Action<int> pick, Func<int, Color?>? color, int maxRows)
    {
        this.texts = texts; this.selected = selected; this.pick = pick; this.color = color;
        shown = [.. Enumerable.Range(0, texts.Count)];
        float s = MhoExtendedModManager.Gui.Ui.Dpi(owner.DeviceDpi);
        var font = Ui.Regular(9.5f);
        int row = TextRenderer.MeasureText("Ag", font).Height + (int)(8 * s);
        AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty; DropShadowEnabled = true;
        BackColor = Modern.MenuBack;
        var panel = new Panel { BackColor = Modern.MenuBack, Padding = new Padding(1), Margin = Padding.Empty };
        list = new ListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = row, BorderStyle = BorderStyle.None, IntegralHeight = false,
            BackColor = Modern.MenuBack, ForeColor = Ui.Text, Font = font, Dock = DockStyle.Fill,
        };
        Modern.DarkScrollbars(list);
        list.DrawItem += DrawRow;
        list.MouseMove += (_, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0 && i != list.SelectedIndex) list.SelectedIndex = i; };
        list.MouseClick += (_, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0) Pick(i); };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0) { Pick(list.SelectedIndex); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Up && list.SelectedIndex <= 0 && search != null) { search.Focus(); e.Handled = true; }
        };
        panel.Controls.Add(list);
        int searchH = 0;
        if (texts.Count > 12)
        {
            search = new TextBox { BorderStyle = BorderStyle.None, BackColor = Modern.FieldBack, ForeColor = Ui.Text, Font = font, PlaceholderText = "Type to search", Dock = DockStyle.Fill };
            var frame = new Panel { Dock = DockStyle.Top, BackColor = Modern.FieldBack, Padding = new Padding((int)(8 * s), (int)(5 * s), (int)(6 * s), (int)(4 * s)) };
            frame.Controls.Add(search);
            frame.Height = search.PreferredHeight + frame.Padding.Vertical;
            searchH = frame.Height;
            search.TextChanged += (_, _) => Filter();
            MhoPackageModifier.Gui.SearchBox.AddClear(search);
            search.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 0; e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { if (list.Items.Count > 0) Pick(Math.Max(0, list.SelectedIndex)); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape && search.TextLength == 0) { Close(); e.Handled = e.SuppressKeyPress = true; }   // with text, Esc clears it first (SearchBox)
            };
            panel.Controls.Add(frame);
        }
        foreach (int i in shown) list.Items.Add(texts[i]);
        if (selected >= 0 && selected < texts.Count) list.SelectedIndex = selected;

        int widest = texts.Count == 0 ? 0 : texts.Max(t => TextRenderer.MeasureText(t, font).Width);
        int minW = Math.Max(anchor.Width, (int)(140 * s));
        int width = Math.Clamp(widest + (int)(48 * s) + SystemInformation.VerticalScrollBarWidth, minW, Math.Max(minW, (int)(640 * s)));
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        int rows = Math.Min(texts.Count, Math.Max(4, maxRows));
        int height = rows * row + searchH + 2;
        int below = screen.Bottom - anchor.Bottom - 4, above = anchor.Top - screen.Top - 4;
        bool up = height > below && above > below;
        height = Math.Min(height, Math.Max(row * 3 + searchH, up ? above : below));
        panel.Size = new Size(width, height);
        var host = new ToolStripControlHost(panel) { AutoSize = false, Size = panel.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        Items.Add(host);
        Size = panel.Size;
        int x = Math.Clamp(anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        Location = new Point(x, up ? anchor.Top - height - 2 : anchor.Bottom + 2);
    }

    /// <summary>Opens a list at <paramref name="anchor"/> (screen coordinates); <paramref name="pick"/> gets the chosen item's index.</summary>
    public static DropList Show(Control owner, Rectangle anchor, List<string> texts, int selected, Action<int> pick, Func<int, Color?>? color = null, int maxRows = 18)
    {
        var d = new DropList(owner, anchor, texts, selected, pick, color, maxRows);
        d.Closed += (_, _) => d.BeginInvoke(d.Dispose);
        d.Show(d.Location);
        if (d.search != null) d.search.Focus(); else d.list.Focus();
        if (d.list.SelectedIndex >= 0) d.list.TopIndex = Math.Max(0, d.list.SelectedIndex - 3);
        return d;
    }

    /// <summary>
    /// Self-test (--dropdown-test): the list built but not shown (nothing appears on screen), rendered to a PNG, then a search
    /// typed and Enter pressed. Returns problems (empty = pass).
    /// </summary>
    internal static List<string> Test(string dir)
    {
        var problems = new List<string>();
        var texts = Enumerable.Range(1, 30).Select(i => i == 7 ? "Lady Deadpool" : $"Voice {i}").ToList();
        int picked = -1;
        using var owner = new Panel();
        var d = new DropList(owner, new Rectangle(-4000, -4000, 300, 30), texts, 3, i => picked = i, i => i == 3 ? Ui.Enabled : null, 18);
        var panel = (Panel)((ToolStripControlHost)d.Items[0]).Control;
        panel.CreateControl();
        // The rows through the list's own drawing (a never-shown owner-drawn list can't be printed), row 5 as if hovered.
        int rh = d.list.ItemHeight, w = panel.Width;
        using (var bmp = new Bitmap(w, rh * 10))
        using (var g = Graphics.FromImage(bmp))
        {
            for (int r = 0; r < 10; r++)
                d.DrawRow(null, new DrawItemEventArgs(g, d.list.Font, new Rectangle(0, r * rh, w, rh), r, r == 5 ? DrawItemState.Selected : DrawItemState.None));
            bmp.Save(Path.Combine(dir, "droplist.png"));
        }
        if (d.search == null) problems.Add("no search box on a 30-item list");
        if (d.list.Items.Count != 30) problems.Add($"list shows {d.list.Items.Count} of 30");
        if (d.list.SelectedIndex != 3) problems.Add($"current item not marked (selected {d.list.SelectedIndex})");
        if (d.search != null) d.search.Text = "dead";
        if (d.list.Items.Count != 1 || (string)d.list.Items[0] != "Lady Deadpool") problems.Add($"search 'dead' shows {d.list.Items.Count} item(s)");
        d.Pick(Math.Max(0, d.list.SelectedIndex));
        if (picked != 6) problems.Add($"Enter picked {picked}, expected 6 (Lady Deadpool)");
        d.Dispose();
        return problems;
    }

    void Filter()
    {
        string[] words = (search?.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        shown = [.. Enumerable.Range(0, texts.Count).Where(i => words.All(w => texts[i].Contains(w, StringComparison.OrdinalIgnoreCase)))];
        list.BeginUpdate();
        list.Items.Clear();
        foreach (int i in shown) list.Items.Add(texts[i]);
        int at = shown.IndexOf(selected);
        list.SelectedIndex = at >= 0 ? at : list.Items.Count > 0 ? 0 : -1;
        list.EndUpdate();
    }

    void Pick(int row)
    {
        if (row < 0 || row >= shown.Count) return;
        int i = shown[row];
        Close(ToolStripDropDownCloseReason.ItemClicked);
        pick(i);
    }

    void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= shown.Count) return;
        int i = shown[e.Index];
        var g = e.Graphics;
        bool hot = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(hot ? Modern.MenuHover : Modern.MenuBack)) g.FillRectangle(b, e.Bounds);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(list.DeviceDpi);
        int check = (int)(24 * s);
        if (i == selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = e.Bounds.X + 7 * s, cy = e.Bounds.Y + e.Bounds.Height / 2f, w = 10 * s;
            using var pen = new Pen(Ui.Accent, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [new PointF(cx, cy), new PointF(cx + w * 0.35f, cy + w * 0.35f), new PointF(cx + w, cy - w * 0.4f)]);
            g.SmoothingMode = SmoothingMode.None;
        }
        var fore = color?.Invoke(i) ?? Ui.Text;
        TextRenderer.DrawText(g, texts[i], list.Font, new Rectangle(e.Bounds.X + check, e.Bounds.Y, e.Bounds.Width - check - (int)(6 * s), e.Bounds.Height), fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}
