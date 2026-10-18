namespace MhoExtendedModManager.Gui;

/// <summary>
/// The editor's Powers tab: its entries as icon tiles in rows under the 3D view (Kurt, 2026-10-17: like the power buttons
/// under the Mods tab's and the Model tab's previews; the list beside the view took a third of the page). A ListBox-like
/// API (Items, SelectedIndex, SelectedItem, SelectedIndexChanged, BeginUpdate / EndUpdate), so the tab's code and tests read
/// it as they read the list. The host draws each tile (DrawTile); a hover names it (TipFor); a gap starts a new group
/// (StartsGroup: the character's own effects, parts and added packages after the hero's powers). Arrow keys step.
/// </summary>
sealed class PowerBar : Control
{
    public List<object> Items { get; } = [];
    public event EventHandler? SelectedIndexChanged;
    /// <summary>Draws a tile: graphics, its rectangle, the item, selected, under the mouse.</summary>
    public Action<Graphics, Rectangle, object, bool, bool>? DrawTile;
    public Func<object, string>? TipFor;
    public Func<int, bool>? StartsGroup;
    /// <summary>The tile's side and the space between tiles, unscaled.</summary>
    public int Side { get; set; } = 40;
    public int Gap { get; set; } = 4;
    int selected = -1, hover = -1, updating;
    readonly List<Rectangle> rects = [];
    float S => Ui.Dpi(DeviceDpi);

    public PowerBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = true;
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            if (value < -1 || value >= Items.Count) value = -1;
            if (value == selected) return;
            selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public object? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null;

    /// <summary>The items are about to be replaced: the selection goes (as a ListBox's on Clear), without an event.</summary>
    public void BeginUpdate() { updating++; selected = -1; }
    public void EndUpdate() { if (--updating > 0) return; updating = 0; hover = -1; Relayout(); }

    int TileSide => (int)(Side * S);
    int TileGap => Math.Max(1, (int)(Gap * S));

    /// <summary>Lays the tiles out in rows for <paramref name="width"/>; returns the height they take.</summary>
    int Arrange(int width, List<Rectangle>? into)
    {
        int s = TileSide, gap = TileGap, x = 0, y = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            int before = i > 0 && StartsGroup?.Invoke(i) == true ? s / 2 : 0;
            if (x > 0 && x + before + s > width) { x = 0; y += s + gap; before = 0; }
            x += before;
            into?.Add(new Rectangle(x, y, s, s));
            x += s + gap;
        }
        return Items.Count == 0 ? 0 : y + s + Padding.Vertical;
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Math.Max(1, Width);
        return new Size(w, Arrange(w - Padding.Horizontal, null));
    }

    void Relayout()
    {
        if (updating > 0) return;
        rects.Clear();
        int h = Arrange(Math.Max(1, ClientSize.Width - Padding.Horizontal), rects);
        if (Height != h) Height = h;   // (docked at the bottom of its column: the view above takes the rest)
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (rects.Count != Items.Count || rects.Count == 0 || Width != lastWidth) { lastWidth = Width; Relayout(); } }
    int lastWidth = -1;
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Relayout(); }

    Rectangle Tile(int i) => i < rects.Count ? new Rectangle(rects[i].X + Padding.Left, rects[i].Y + Padding.Top, rects[i].Width, rects[i].Height) : Rectangle.Empty;

    int At(Point p)
    {
        for (int i = 0; i < rects.Count && i < Items.Count; i++) if (Tile(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (DrawTile == null) return;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        for (int i = 0; i < rects.Count && i < Items.Count; i++)
        {
            var r = Tile(i);
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            DrawTile(e.Graphics, r, Items[i], i == selected, i == hover);
        }
        if (Focused && selected >= 0 && selected < rects.Count)
        {
            using var pen = new Pen(Ui.Text, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            e.Graphics.DrawRectangle(pen, Rectangle.Inflate(Tile(selected), 1, 1));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int k = At(e.Location);
        if (k == hover) return;
        hover = k;
        Invalidate();
        Ui.Tips.SetToolTip(this, k >= 0 && TipFor != null ? TipFor(Items[k]) : "");
        Ui.ForgetOnDispose(this);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Left && At(e.Location) is int k and >= 0) SelectedIndex = k;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Items.Count == 0) return;
        int k = selected;
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Up: k = Math.Max(0, k - 1); break;
            case Keys.Right: case Keys.Down: k = Math.Min(Items.Count - 1, k + 1); break;
            case Keys.Home: k = 0; break;
            case Keys.End: k = Items.Count - 1; break;
            default: return;
        }
        SelectedIndex = k;
        e.Handled = true;
    }
}
