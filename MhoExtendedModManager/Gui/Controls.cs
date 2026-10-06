using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>A split container that shows the window gradient behind its panels, with a faint divider line.</summary>
sealed class GradientSplit : SplitContainer
{
    public GradientSplit() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Ui.PaintGradient(e.Graphics, this, ClientRectangle);
        var r = SplitterRectangle;
        using var pen = new Pen(Color.FromArgb(55, 255, 255, 255));
        if (Orientation == Orientation.Vertical) e.Graphics.DrawLine(pen, r.X + r.Width / 2, r.Top, r.X + r.Width / 2, r.Bottom);
        else e.Graphics.DrawLine(pen, r.Left, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
    }
}

/// <summary>A table whose empty area shows the window gradient (rows stay solid surfaces).</summary>
sealed class GradientGrid : DataGridView
{
    // A tooltip still holding a disposed table asks for its handle when the window deactivates (ToolTip.HideAllToolTips),
    // which threw ObjectDisposedException; a disposed table now has no handle to give instead.
    protected override void CreateHandle() { if (IsDisposed || Disposing) return; base.CreateHandle(); }

    protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds) =>
        Ui.PaintGradient(graphics, this, clipBounds);
}

/// <summary>A plain list of names with roomier rows (stock texture names).</summary>
sealed class NameList : ListBox
{
    public NameList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Math.Min(255, (int)(26 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi))); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { using var g = Graphics.FromHdc(m.WParam); Ui.PaintGradient(g, this, ClientRectangle); m.Result = 1; return; }
        base.WndProc(ref m);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        if (sel) { using var b = new SolidBrush(Ui.CardSelected); e.Graphics.FillRectangle(b, e.Bounds); }
        else Ui.PaintGradient(e.Graphics, this, e.Bounds);
        string text = GetItemText(Items[e.Index]) ?? "";
        var r = new Rectangle(e.Bounds.X + (int)(8 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi)), e.Bounds.Y, e.Bounds.Width - (int)(12 * MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi)), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, r, text.StartsWith('(') || text == "Loading…" ? Ui.Subtle : Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Flat tabs (text with an accent underline) over switching pages, as in MHModManager.</summary>
sealed class FlatTabs : UserControl
{
    readonly FlowLayoutPanel strip = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(4, 2, 4, 0) };
    readonly Panel body = new() { Dock = DockStyle.Fill };
    readonly List<(Label Tab, Control Page)> tabs = [];
    public int SelectedIndex { get; private set; } = -1;
    public int Count => tabs.Count;
    public string TitleAt(int i) => tabs[i].Tab.Text;
    public void SetTitle(int i, string title) => tabs[i].Tab.Text = title;
    public Control PageAt(int i) => tabs[i].Page;
    /// <summary>Tab font size (the main window's top-level tabs are larger).</summary>
    public float TabPoints { get; set; } = 9.75f;
    public event Action<int>? SelectedChanged;

    public FlatTabs()
    {
        Controls.Add(body); Controls.Add(strip);
        strip.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(60, 255, 255, 255)); e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1); };
    }

    /// <summary>
    /// Removes and disposes every tab and page. A removed control that isn't disposed keeps its window, and once the
    /// garbage collector takes the .NET side, the next message to that window kills the process ("callback was made on a
    /// garbage collected delegate": Kurt's padlock-click crash in 0.17.0, reproduced by --ui-selftest).
    /// </summary>
    public void Clear()
    {
        var old = strip.Controls.Cast<Control>().Concat(body.Controls.Cast<Control>()).ToList();
        // Disposed while still in the window (a control removes itself as it goes): a table's own cell tooltip listens to
        // the window's Deactivate, and one taken out of the window first was left behind there; switching windows then
        // reached the disposed table ("Cannot access a disposed object: GradientGrid", a user, 2026-09-30).
        foreach (var c in old) c.Dispose();
        strip.Controls.Clear(); body.Controls.Clear(); tabs.Clear(); SelectedIndex = -1;
    }

    public void Add(string title, Control page)
    {
        var tab = new Label { Text = title, AutoSize = true, Padding = new Padding(10, 6, 10, 8), Cursor = Cursors.Hand, Font = TabPoints > 10 ? Ui.Bold(TabPoints) : Ui.Regular(TabPoints), ForeColor = Ui.Subtle, BackColor = Color.Transparent };
        int index = tabs.Count;
        tab.Click += (_, _) => Select(index);
        tab.Paint += (_, e) =>
        {
            if (index != SelectedIndex) return;
            using var b = new SolidBrush(Ui.Accent);
            float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
            e.Graphics.FillRectangle(b, 6 * s, tab.Height - 3 * s, tab.Width - 12 * s, 2.5f * s);
        };
        page.Dock = DockStyle.Fill; page.Visible = false;
        strip.Controls.Add(tab); body.Controls.Add(page);
        tabs.Add((tab, page));
        if (SelectedIndex < 0) Select(0);
    }

    public void Select(int i)
    {
        if (i < 0 || i >= tabs.Count) return;
        SelectedIndex = i;
        for (int k = 0; k < tabs.Count; k++)
        {
            tabs[k].Page.Visible = k == i;
            tabs[k].Tab.ForeColor = k == i ? Ui.Text : Ui.Subtle;
            tabs[k].Tab.Invalidate();
        }
        SelectedChanged?.Invoke(i);
    }
}

/// <summary>The selected mod's header: name and version, "by author · N modification(s)", badges and the Enabled pill.</summary>
sealed class DetailsHeader : Control
{
    public Mod? Mod { get; set; }
    public bool Conflicted { get; set; }
    public event Action? PillClicked;
    /// <summary>The "Conflict" badge clicked (opens the Conflicts tab).</summary>
    public event Action? ConflictClicked;
    Rectangle conflictRect;
    /// <summary>A tag chip or "+ Tag" clicked, at a screen point (opens the tags menu).</summary>
    public event Action<Point>? TagsClicked;
    /// <summary>The single costume the mod is for ("Age of Ultron Movie"), or null (then no costume control).</summary>
    public string? CostumeTitle { get; set; }
    /// <summary>The costume control clicked, at a screen point (opens the Move to Another Costume menu).</summary>
    public event Action<Point>? CostumeClicked;
    Rectangle pill, tagArea, costumeRect;
    readonly Font chipFont = Ui.Heavy(8f);
    readonly Font title = Ui.Bold(14f), version = Ui.Regular(9.5f), by = Ui.Regular(9.5f), badge = Ui.Heavy(8.25f), pillFont = Ui.Heavy(8.5f);

    public DetailsHeader()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Default;
    }

    float S => MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = (int)(64 * S); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.PaintGradient(g, this, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        if (Mod is not Mod m) { TextRenderer.DrawText(g, "Select a Mod", title, new Point((int)(12 * S), (int)(14 * S)), Ui.Subtle); return; }
        int x = (int)(12 * S), right = Width - (int)(12 * S);

        // Right side: the Enabled / Disabled pill, then the badges.
        string state = m.Enabled ? "Enabled" : "Disabled";
        var ps = TextRenderer.MeasureText(g, state, pillFont);
        // Level with the name, so the Costume pill below keeps a gap (Kurt: the badges hit it).
        pill = new Rectangle(right - ps.Width - (int)(8 * S), (int)(8 * S), ps.Width + (int)(8 * S), (int)(21 * S));
        var pc = m.Enabled ? Ui.Enabled : Ui.Subtle;
        using (var path = Ui.Round(pill, 4 * S))
        {
            using var f = new SolidBrush(pc); g.FillPath(f, path);   // solid pill, dark text
        }
        TextRenderer.DrawText(g, state, pillFont, pill, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var badges = Ui.Badges(m);
        int left = Ui.DrawBadges(g, badges, pill.Left - (int)(8 * S), pill.Top + pill.Height / 2, badge, S * 1.15f);
        conflictRect = Rectangle.Empty;
        if (Conflicted)
        {
            int r0 = left - (int)(6 * S);
            int l0 = Ui.DrawBadges(g, [("Conflict", Ui.Warn)], r0, pill.Top + pill.Height / 2, badge, S * 1.15f);
            conflictRect = new Rectangle(l0, pill.Top, r0 - l0, pill.Height);
            left = l0;
        }

        // Left: name, version, by-line.
        var ts = TextRenderer.MeasureText(g, m.Name, title, Size.Empty, TextFormatFlags.NoPrefix);
        int maxName = left - x - (int)(80 * S);
        Ui.DrawModName(g, m, title, new Rectangle(x, (int)(8 * S), Math.Min(ts.Width, maxName), ts.Height), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(m.Manifest.Version))
            TextRenderer.DrawText(g, m.Manifest.Version, version, new Point(x + Math.Min(ts.Width, maxName) + (int)(4 * S), (int)(8 * S) + ts.Height - TextRenderer.MeasureText(m.Manifest.Version, version).Height - (int)(2 * S)), Ui.Subtle, TextFormatFlags.NoPrefix);
        string line = (string.IsNullOrEmpty(m.Manifest.Author) ? "" : $"by {m.Manifest.Author}   ") + Ui.CountText(m) + (m.Enabled ? "" : "   ·   Turned Off");
        var byPos = new Point(x, (int)(8 * S) + ts.Height + (int)(2 * S));
        TextRenderer.DrawText(g, line, by, byPos, Ui.Subtle, TextFormatFlags.NoPrefix);

        // Right of the by-line: the mod's costume, a drop-down (Kurt: show the source costume, and choices to move it).
        costumeRect = Rectangle.Empty;
        int tagsRight = right;
        if (!string.IsNullOrEmpty(CostumeTitle))
        {
            string ct = "Costume: " + CostumeTitle + "  ▾";
            var cs = TextRenderer.MeasureText(g, ct, pillFont, Size.Empty, TextFormatFlags.NoPrefix);
            var bs = TextRenderer.MeasureText(g, "X", by);
            costumeRect = new Rectangle(right - cs.Width - (int)(12 * S), byPos.Y + bs.Height / 2 - (int)(11 * S), cs.Width + (int)(12 * S), (int)(22 * S));
            if (costumeRect.Top < pill.Bottom + (int)(5 * S)) costumeRect.Y = pill.Bottom + (int)(5 * S);
            using (var path = Ui.Round(costumeRect, 4 * S))
            {
                using var fill = new SolidBrush(Color.FromArgb(40, Ui.Accent)); g.FillPath(fill, path);
                using var pen = new Pen(Ui.Accent); g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, ct, pillFont, costumeRect, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            tagsRight = costumeRect.Left - (int)(8 * S);
        }

        // The mod's tags, then "+ Tag" (both open the tags menu).
        var ls = TextRenderer.MeasureText(g, line, by, Size.Empty, TextFormatFlags.NoPrefix);
        int cx = x + ls.Width + (int)(12 * S), mid = byPos.Y + ls.Height / 2;
        tagArea = Rectangle.Empty;
        for (int i = 0; i <= m.Tags.Count; i++)
        {
            bool add = i == m.Tags.Count;
            string tag = add ? "+ Tag" : m.Tags[i];
            if (cx + Ui.ChipWidth(g, tag, chipFont, S) > tagsRight) break;
            var r = Ui.DrawChip(g, tag, chipFont, cx, mid, S, outline: add, soft: !add && m.KindOf(tag) == Mod.TagKind.Auto, mod: m);
            tagArea = tagArea.IsEmpty ? r : Rectangle.Union(tagArea, r);
            cx = r.Right + (int)(4 * S);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hand = (pill.Contains(e.Location) || tagArea.Contains(e.Location) || conflictRect.Contains(e.Location) || costumeRect.Contains(e.Location)) && Mod != null;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
        string? tip = costumeRect.Contains(e.Location) ? "The costume this mod is for. Click to move it to another costume of the same hero (as a new mod; this one isn't changed)." : null;
        if (tip != shownTip) { shownTip = tip; if (tip == null) Ui.Tips.Hide(this); else Ui.Tips.Show(tip, this, e.X + 12, e.Y + 18, 8000); }
    }
    string? shownTip;
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (shownTip != null) { shownTip = null; Ui.Tips.Hide(this); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Mod == null) return;
        if (pill.Contains(e.Location)) PillClicked?.Invoke();
        else if (conflictRect.Contains(e.Location)) ConflictClicked?.Invoke();
        else if (tagArea.Contains(e.Location)) TagsClicked?.Invoke(PointToScreen(new Point(e.X, tagArea.Bottom)));
        else if (costumeRect.Contains(e.Location)) CostumeClicked?.Invoke(PointToScreen(new Point(costumeRect.Left, costumeRect.Bottom)));
    }
}

/// <summary>
/// The 3D view's light brightness (Kurt): a flat slider in the app's colours, 50–200 %, labelled "Light", with the value.
/// Drag or use the mouse wheel; double-click goes back to Home (the mod's own level, else 100 %). ValueChanged fires while it moves, Committed once it's set.
/// </summary>
sealed class LightSlider : Control
{
    float value = 1;
    bool dragging;
    public event Action? ValueChanged, Committed;
    /// <summary>What a double-click goes back to (default 1).</summary>
    public Func<float>? Home { get; set; }
    /// <summary>Range and step (the Light slider: 0.5–2 in 0.05); the icon creator's frame slider uses its own.</summary>
    public float Min { get; set; } = 0.5f;
    public float Max { get; set; } = 2f;
    public float Step { get; set; } = 0.05f;
    /// <summary>The label on the left and how the value is written on the right.</summary>
    public string Label { get; set; } = "Light";
    public Func<float, string> Format { get; set; } = v => $"{v * 100:0} %";
    readonly Font labelFont = Ui.Regular(8.5f);
    /// <summary>A tick at this value (the Light slider's 100 %), or null.</summary>
    public float? Mark { get; set; } = 1f;

    public LightSlider()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    // Keyboard (Kurt: click once, then step with the arrow keys, Shift for faster): ← → ↑ ↓ one step, Shift five,
    // Home / End the ends. The steps while a key is held are one change; Committed fires when it's let go.
    /// <summary>What one arrow key moves when it isn't <see cref="Step"/> (the Model tab's frame slider, 0–1: one frame).</summary>
    public float ArrowStep { get; set; }
    float KeyStep => ArrowStep > 0 ? ArrowStep : Step > 0 ? Step : (Max - Min) / 100f;
    bool keyChanged;
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        float step = KeyStep * (e.Shift ? 5 : 1), was = value;
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value = value - step; break;
            case Keys.Right: case Keys.Up: Value = value + step; break;
            case Keys.Home: Value = Min; break;
            case Keys.End: Value = Max; break;
            default: return;
        }
        e.Handled = true;
        if (Math.Abs(was - value) > 1e-6) keyChanged = true;
    }
    /// <summary>Tests: a key pressed and let go, as the keyboard does.</summary>
    internal void TestKey(Keys key) { OnKeyDown(new KeyEventArgs(key)); OnKeyUp(new KeyEventArgs(key)); }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (keyChanged) { keyChanged = false; Committed?.Invoke(); } }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (keyChanged) { keyChanged = false; Committed?.Invoke(); } Invalidate(); }

    public float Value
    {
        get => value;
        set { float v = Math.Clamp(Step > 0 ? MathF.Round(value / Step) * Step : value, Min, Max); if (Math.Abs(v - this.value) < 1e-4) return; this.value = v; Invalidate(); ValueChanged?.Invoke(); }
    }

    float S => MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
    Rectangle Track
    {
        get
        {
            // Room for the label (measured: "Light", "Frame", "Original") and the value on the right, measured at the widest
            // it gets (Kurt: the frame count "122 / 122" was cut off in the fixed 52 px).
            int left = Math.Max((int)(44 * S), TextRenderer.MeasureText(Label, labelFont).Width + (int)(8 * S));
            int widest = Math.Max(TextRenderer.MeasureText(Format(Max), labelFont).Width, TextRenderer.MeasureText(Format(value), labelFont).Width);
            int right = Math.Max((int)(52 * S), widest + (int)(10 * S));
            return new Rectangle(left, Height / 2 - (int)(2 * S), Math.Max(10, Width - left - right), Math.Max(3, (int)(4 * S)));
        }
    }

    float ValueAt(int x) { var t = Track; return Min + (Max - Min) * Math.Clamp((x - t.X) / (float)t.Width, 0, 1); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var t = Track;
        float k = Max > Min ? (value - Min) / (Max - Min) : 0;
        int tx = t.X + (int)(k * t.Width);
        using var font = Ui.Regular(8.5f);
        TextRenderer.DrawText(g, Label, font, new Rectangle(0, 0, t.X - (int)(8 * S), Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using (var back = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) using (var p = Ui.Round(t, t.Height / 2f)) g.FillPath(back, p);
        var done = new Rectangle(t.X, t.Y, Math.Max(1, tx - t.X), t.Height);
        using (var acc = new SolidBrush(Ui.Accent)) using (var p = Ui.Round(done, t.Height / 2f)) g.FillPath(acc, p);
        if (Mark is float mark && Max > Min)
        {
            int one = t.X + (int)((mark - Min) / (Max - Min) * t.Width);   // e.g. the 100 % mark
            using var pen = new Pen(Color.FromArgb(140, 255, 255, 255), Math.Max(1f, S));
            g.DrawLine(pen, one, t.Y - (int)(3 * S), one, t.Bottom + (int)(3 * S));
        }
        float r = 6 * S;
        using (var thumb = new SolidBrush(Ui.Text)) g.FillEllipse(thumb, tx - r, Height / 2f - r, 2 * r, 2 * r);
        if (Focused) using (var ring = new Pen(Ui.Accent, Math.Max(1.5f, 2 * S))) g.DrawEllipse(ring, tx - r - 2 * S, Height / 2f - r - 2 * S, 2 * r + 4 * S, 2 * r + 4 * S);   // selected: the arrow keys move it
        TextRenderer.DrawText(g, Enabled ? Format(value) : "–", font, new Rectangle(t.Right + (int)(6 * S), 0, Width - t.Right - (int)(6 * S), Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();   // then the arrow keys step it
        // A press on the thumb only selects it (no jump); elsewhere on the track it goes there, as before.
        var t = Track;
        int tx = t.X + (int)((Max > Min ? (value - Min) / (Max - Min) : 0) * t.Width);
        dragging = true; Capture = true;
        if (Math.Abs(e.X - tx) > 7 * S) Value = ValueAt(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Value = ValueAt(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (!dragging) return; dragging = false; Capture = false; Committed?.Invoke(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Value += e.Delta > 0 ? Math.Max(Step, (Max - Min) / 40) : -Math.Max(Step, (Max - Min) / 40); Committed?.Invoke(); }
    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); Value = Home?.Invoke() ?? 1; Committed?.Invoke(); }
}
