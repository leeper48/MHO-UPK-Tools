using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>A group header in the mod list (grouped by tag or author); click to fold it.</summary>
sealed class ModGroup(string key, string name, int count, bool collapsed)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public bool Collapsed { get; } = collapsed;
}

/// <summary>
/// The mod list as cards: costume icon, name, author and tag chips, category badges, a checkbox (enable), a padlock,
/// status and change count. Items are Mods, or ModGroup headers when the list is grouped. Hovering a part of a card
/// shows what it does (dark tooltip, after a short pause).
/// </summary>
sealed class ModListBox : ListBox
{
    public HashSet<Mod> Conflicted { get; set; } = [];
    /// <summary>Conflicting mods that lose at least one change to a higher mod (red stripe); the others only win (amber).</summary>
    public HashSet<Mod> Overridden { get; set; } = [];
    /// <summary>What each conflicting mod overrides / is overridden by (for the tooltip).</summary>
    public Func<Mod, string?>? ConflictText { get; set; }
    /// <summary>The selected mod's conflict partners: outlined in the list (a user: show which mods conflict).</summary>
    public HashSet<Mod> Partners { get; set; } = [];
    /// <summary>Drag and drop is allowed (the plain priority view).</summary>
    public Func<bool>? CanReorder { get; set; }
    /// <summary>A card dropped next to another: (moved mod, target mod, below the target).</summary>
    public event Action<Mod, Mod, bool>? Dropped;
    Mod? dragMod; Point dragFrom; bool dragging, leftDown; int dropItem = -1; bool dropBelow;
    readonly System.Windows.Forms.Timer scrollTimer = new() { Interval = 60 };
    public event Action<Mod>? CheckClicked;
    // Multi-select (a user, 2026-09-30: Shift to turn several on / off, drag them as a group, move them with the priority
    // buttons). The ListBox's own SelectedIndex stays the one card whose details show; the marked cards are kept by folder
    // name, so they survive the reload after a change (turn them on, then off again).
    readonly HashSet<string> marked = new(StringComparer.OrdinalIgnoreCase);
    int anchor = -1;
    bool clearOnUp;
    /// <summary>A checkbox of a marked card (or Space) with several marked: (the marked mods in list order, the new state).</summary>
    public event Action<List<Mod>, bool>? CheckManyClicked;
    /// <summary>Several marked cards dropped next to another: (the marked mods in list order, target, below the target).</summary>
    public event Action<List<Mod>, Mod, bool>? DroppedMany;
    /// <summary>The marking changed (how many are marked).</summary>
    public event Action<int>? MarksChanged;
    /// <summary>The marked mods shown in the list, in list order (empty unless more than one is marked).</summary>
    public List<Mod> MarkedMods => marked.Count > 1 ? Items.OfType<Mod>().Where(m => marked.Contains(m.FolderName)).ToList() : [];
    bool IsMarked(Mod m) => marked.Contains(m.FolderName);
    /// <summary>Back to a single selection (the card whose details show).</summary>
    public void ClearMarks()
    {
        int before = marked.Count;
        marked.Clear();
        if (SelectedItem is Mod m) marked.Add(m.FolderName);
        if (before > 1) { Invalidate(); MarksChanged?.Invoke(marked.Count); }
    }
    void MarkRange(int from, int to)
    {
        marked.Clear();
        for (int k = Math.Min(from, to); k <= Math.Max(from, to); k++)
            if (k >= 0 && k < Items.Count && Items[k] is Mod mk) marked.Add(mk.FolderName);
        Invalidate();
        MarksChanged?.Invoke(marked.Count);
    }
    /// <summary>Padlock clicked (a locked mod, or one that can be locked where it is).</summary>
    public event Action<Mod>? LockClicked;
    public event Action<ModGroup>? GroupClicked;
    public event Action<string>? TagClicked;
    /// <summary>Right-click on a card (already selected), at a screen point.</summary>
    public event Action<Mod, Point>? MenuRequested;
    /// <summary>The newer Nexus version for a mod (null: none), and a click on its ↑ badge.</summary>
    public Func<Mod, string?>? UpdateFor { get; set; }
    /// <summary>The single costume a mod is for ("Age of Ultron Movie"), shown on the card instead of the count; null = the count.</summary>
    public Func<Mod, string?>? CostumeLabel { get; set; }
    /// <summary>Stock pictures for mods without one of their own (StockCatalog.DefaultIconFor); null: none.</summary>
    public StockCatalog? Catalog { get; set; }
    public event Action<Mod>? UpdateClicked;
    /// <summary>What a padlock click would lock the mod to (ModLibrary.CanLock); None hides the padlock of an unlocked mod.</summary>
    public Func<Mod, ModLock>? CanLock { get; set; }
    int hover = -1;
    readonly Font nameFont = Ui.Bold(10f), smallFont = Ui.Regular(8.25f), badgeFont = Ui.Heavy(7.5f), initialFont = Ui.Bold(14f), chipFont = Ui.Heavy(7.25f), groupFont = Ui.Bold(8.75f);
    // Costume icons (first costume… replacement of each mod), decoded in the background. Key: .dds path + write time,
    // so an edited mod shows its new icon. A null value = no icon / unreadable / still loading.
    readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);
    // Where each card's badges and tag chips were drawn (item index), for the tooltips and chip clicks.
    readonly Dictionary<int, (Rectangle Badges, List<(Rectangle Rect, string Tag)> Chips)> regions = [];
    // Where each card's Update pill and Nexus mark were drawn (item index).
    readonly Dictionary<int, (Rectangle Pill, Rectangle Mark)> nexusParts = [];
    readonly Font pillFont = Ui.Heavy(7.5f), markFont = Ui.Heavy(6.5f);
    readonly ToolTip tips;
    readonly System.Windows.Forms.Timer tipTimer = new() { Interval = 450 };
    string? tipText;
    bool tipShown;

    public ModListBox()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        // The whole list is painted here, into one off-screen buffer (OnPaint): a native owner-drawn ListBox erases the
        // area first and then draws each card straight onto the screen, which flickered on every hover, tooltip and
        // scroll (a user, 2026-09-28).
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        tips = Ui.NewTips(() => tipText);
        // While dragging near the top or bottom edge, scroll.
        scrollTimer.Tick += (_, _) =>
        {
            if (!dragging) { scrollTimer.Stop(); return; }
            var p = PointToClient(MousePosition);
            int edge = (int)(28 * S);
            if (p.Y < edge && TopIndex > 0) TopIndex--;
            else if (p.Y > Height - edge && TopIndex < Items.Count - 1) TopIndex++;
            else return;
            UpdateDrop(p);
        };
        tipTimer.Tick += (_, _) =>
        {
            tipTimer.Stop();
            if (tipText == null || !IsHandleCreated) return;
            var p = PointToClient(MousePosition);
            tips.Show(tipText, this, p.X + (int)(14 * S), p.Y + (int)(20 * S), 15000);
            tipShown = true;
        };
    }

    float S => DeviceDpi / 96f;
    static readonly Color NexusOrange = Color.FromArgb(230, 140, 60);
    public static readonly Color ConflictAmber = Color.FromArgb(242, 170, 60);

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = Math.Min(255, (int)((e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is ModGroup ? 30 : 50) * S));
    }

    // Cards are laid out from the right edge (badges, checkbox): a width change must repaint every card, not just the
    // newly exposed strip, or the old badges stay behind (Kurt, 2026-09-27: "it repeats the status icons").
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    // No separate erase: OnPaint covers every pixel (erasing first is what made the list flash).
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { m.Result = 1; return; }   // WM_ERASEBKGND
        if (m.Msg is 0x0317 or 0x0318)   // WM_PRINT / WM_PRINTCLIENT (DrawToBitmap, snapshots): the same painting
        {
            // Painted into a bitmap and copied with GDI: the printing DC is offset to this window's place (window
            // origin), which GDI honours and GDI+ doesn't (the cards landed elsewhere in the picture).
            if (m.Msg == 0x0317) base.WndProc(ref m);   // the scroll bar
            using var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using (var bg = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(bg, ClientRectangle));
            IntPtr hbm = bmp.GetHbitmap(), mem = CreateCompatibleDC(m.WParam), old = SelectObject(mem, hbm);
            BitBlt(m.WParam, 0, 0, bmp.Width, bmp.Height, mem, 0, 0, 0x00CC0020);   // SRCCOPY
            SelectObject(mem, old); DeleteDC(mem); DeleteObject(hbm);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    /// <summary>The visible cards and, below the last one, the window gradient, all into the double buffer.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int bottom = 0;
        for (int i = Math.Max(0, TopIndex); i < Items.Count; i++)
        {
            var r = GetItemRectangle(i);
            if (r.Height <= 0 || r.Top >= ClientSize.Height) break;   // past the view (the list gives an empty rectangle there)
            bottom = r.Bottom;
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            var state = SelectedIndex == i || marked.Count > 1 && Items[i] is Mod mk && IsMarked(mk) ? DrawItemState.Selected : DrawItemState.None;
            OnDrawItem(new DrawItemEventArgs(g, Font, r, i, state));
        }
        if (bottom < ClientSize.Height) Ui.PaintGradient(g, this, new Rectangle(0, bottom, ClientSize.Width, ClientSize.Height - bottom));
    }

    Rectangle CheckRect(Rectangle b) { int s = (int)(16 * S); return new Rectangle(b.Right - s - (int)(10 * S), b.Top + (int)(9 * S), s, s); }
    // The padlock sits under the checkbox, on the second row.
    Rectangle LockRect(Rectangle b) { var c = CheckRect(b); return new Rectangle(c.X, b.Top + (int)(28 * S), c.Width, c.Height); }
    ModLock LockOffer(Mod m) => m.Lock != ModLock.None ? m.Lock : CanLock?.Invoke(m) ?? ModLock.None;

    /// <summary>A padlock: closed and gold when locked, open and grey when the mod can be locked here.</summary>
    static void DrawPadlock(Graphics g, Rectangle r, bool locked, float s)
    {
        var body = new RectangleF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.44f, r.Width * 0.72f, r.Height * 0.52f);
        float sw = r.Width * 0.44f, sx = r.X + (r.Width - sw) / 2 + (locked ? 0 : r.Width * 0.18f), sy = r.Y + r.Height * 0.06f - (locked ? 0 : r.Height * 0.08f);
        var color = locked ? Color.FromArgb(242, 190, 60) : Ui.Subtle;
        using (var pen = new Pen(color, Math.Max(1.5f, 1.8f * s)))
        {
            using var shackle = new GraphicsPath();
            shackle.AddLine(sx, body.Y, sx, sy + sw / 2);
            shackle.AddArc(sx, sy, sw, sw, 180, 180);
            shackle.AddLine(sx + sw, sy + sw / 2, sx + sw, body.Y - (locked ? 0 : r.Height * 0.1f));
            g.DrawPath(pen, shackle);
        }
        using (var path = Ui.Round(Rectangle.Round(body), 1.5f * s))
        {
            if (locked) { using var f = new SolidBrush(color); g.FillPath(f, path); }
            else { using var pen = new Pen(color, Math.Max(1f, 1.2f * s)); g.DrawPath(pen, path); }
        }
        float k = r.Width * 0.12f;
        using var hole = new SolidBrush(locked ? Ui.OnColor : color);
        g.FillEllipse(hole, body.X + body.Width / 2 - k / 2, body.Y + body.Height * 0.3f, k, k);
    }

    void DrawGroup(Graphics g, ModGroup grp, Rectangle b)
    {
        int x = b.X + (int)(8 * S), mid = b.Y + b.Height / 2 + (int)(2 * S);
        // Chevron: pointing right when folded, down when open.
        float c = 4.5f * S;
        using (var br = new SolidBrush(Ui.Subtle))
            g.FillPolygon(br, grp.Collapsed
                ? [new PointF(x, mid - c), new PointF(x + c * 1.3f, mid), new PointF(x, mid + c)]
                : [new PointF(x - c * 0.2f, mid - c * 0.6f), new PointF(x + c * 1.5f, mid - c * 0.6f), new PointF(x + c * 0.65f, mid + c * 0.7f)]);
        x += (int)(14 * S);
        var r = grp.Key.StartsWith("tag:") && grp.Name != "Untagged" ? Ui.DrawChip(g, grp.Name, chipFont, x, mid, S) : Rectangle.Empty;
        if (r.IsEmpty)
        {
            string label = grp.Name.ToUpperInvariant();
            var sz = TextRenderer.MeasureText(g, label, groupFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, label, groupFont, new Point(x, mid - sz.Height / 2), Ui.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            r = new Rectangle(x, mid - sz.Height / 2, sz.Width, sz.Height);
        }
        string count = grp.Count.ToString();
        var cs = TextRenderer.MeasureText(g, count, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, count, smallFont, new Point(r.Right + (int)(8 * S), mid - cs.Height / 2), Ui.Subtle, TextFormatFlags.NoPadding);
        using var pen = new Pen(Color.FromArgb(50, 255, 255, 255));
        int lx = r.Right + cs.Width + (int)(16 * S);
        if (lx < b.Right - (int)(8 * S)) g.DrawLine(pen, lx, mid, b.Right - (int)(8 * S), mid);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var b = e.Bounds;
        Ui.PaintGradient(g, this, b);
        if (Items[e.Index] is ModGroup grp) { DrawGroup(g, grp, b); return; }
        var m = (Mod)Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var card = new Rectangle(b.X + (int)(4 * S), b.Y + (int)(2 * S), b.Width - (int)(8 * S), b.Height - (int)(4 * S));
        using (var path = Ui.Round(card, 4 * S))
        using (var fill = new SolidBrush(sel ? Ui.CardSelected : e.Index == hover ? Ui.CardHover : Ui.Card))
            g.FillPath(fill, path);
        // Conflicts (enabled mods changing the same thing): a stripe on the left edge, red when part of this mod is
        // overridden by a higher one, amber when it only overrides others; the selected mod's partners are outlined.
        if (Conflicted.Contains(m))
        {
            var stripe = new Rectangle(card.X, card.Y + (int)(3 * S), Math.Max(3, (int)(4 * S)), card.Height - (int)(6 * S));
            using var sb = new SolidBrush(Overridden.Contains(m) ? Ui.Warn : ConflictAmber);
            using var sp = Ui.Round(stripe, 2 * S);
            g.FillPath(sb, sp);
        }
        if (Partners.Contains(m))
        {
            using var pen = new Pen(Overridden.Contains(m) ? Ui.Warn : ConflictAmber, Math.Max(1.5f, 1.6f * S));
            using var op = Ui.Round(Rectangle.Inflate(card, -1, -1), 4 * S);
            g.DrawPath(pen, op);
        }
        // Drag and drop: where the card will go.
        if (dragging && e.Index == dropItem)
        {
            int y = dropBelow ? card.Bottom + (int)(1 * S) : card.Top - (int)(1 * S);
            using var lp = new Pen(Ui.Accent, Math.Max(2f, 3f * S));
            g.DrawLine(lp, card.X, y, card.Right, y);
        }

        int pad = (int)(10 * S);
        var check = CheckRect(b);

        // Costume icon on the left (or the name's first letter on a plain tile when the mod has none).
        int iconSize = card.Height - (int)(8 * S);
        var iconRect = new Rectangle(card.X + (int)(4 * S), card.Y + (card.Height - iconSize) / 2, iconSize, iconSize);
        var icon = Icon(m);
        using (var path = Ui.Round(iconRect, 3 * S))
        {
            if (icon != null)
            {
                var old = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Max((float)iconRect.Width / icon.Width, (float)iconRect.Height / icon.Height);
                float w = icon.Width * k, h = icon.Height * k;
                g.DrawImage(icon, iconRect.X + (iconRect.Width - w) / 2, iconRect.Y + (iconRect.Height - h) / 2, w, h);
                g.Clip = old;
            }
            else
            {
                using var f = new SolidBrush(Color.FromArgb(40, 255, 255, 255)); g.FillPath(f, path);
                string initial = m.Name.TrimStart().Length > 0 ? m.Name.TrimStart()[..1].ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, initial, initialFont, iconRect, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
        // Nexus mark on the icon's corner: a green disc with ↑ when Nexus has a newer version, an orange N when linked.
        string? newer = UpdateFor?.Invoke(m);
        var mark = Rectangle.Empty;
        if (newer != null || m.NexusModId != null)
        {
            int ms = (int)((newer != null ? 17 : 14) * S);
            mark = new Rectangle(iconRect.Right - ms + (int)(3 * S), iconRect.Bottom - ms + (int)(3 * S), ms, ms);
            using (var ring = new SolidBrush(sel ? Ui.CardSelected : Ui.Card)) g.FillEllipse(ring, Rectangle.Inflate(mark, (int)(2 * S), (int)(2 * S)));
            using (var f = new SolidBrush(newer != null ? Ui.Enabled : NexusOrange)) g.FillEllipse(f, mark);
            if (newer != null)
            {
                float cx0 = mark.X + mark.Width / 2f, top = mark.Y + mark.Height * 0.22f, bot = mark.Bottom - mark.Height * 0.22f, w = mark.Width * 0.24f;
                using var p = new Pen(Ui.OnColor, Math.Max(1.5f, 1.8f * S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLine(p, cx0, bot, cx0, top);
                g.DrawLines(p, [new PointF(cx0 - w, top + w), new PointF(cx0, top), new PointF(cx0 + w, top + w)]);
            }
            else TextRenderer.DrawText(g, "N", markFont, mark, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        int textLeft = iconRect.Right + (int)(8 * S);
        // Checkbox: white box, accent fill with a tick when enabled.
        using (var path = Ui.Round(check, 2.5f * S))
        {
            if (m.Enabled) { using var f = new SolidBrush(Ui.Accent); g.FillPath(f, path); }
            using var pen = new Pen(m.Enabled ? Ui.Accent : Ui.Subtle, Math.Max(1f, 1.2f * S));
            g.DrawPath(pen, path);
        }
        if (m.Enabled)
        {
            using var tick = new Pen(Color.White, 2f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(tick, [new PointF(check.X + check.Width * 0.22f, check.Y + check.Height * 0.52f), new PointF(check.X + check.Width * 0.42f, check.Y + check.Height * 0.72f), new PointF(check.X + check.Width * 0.78f, check.Y + check.Height * 0.3f)]);
        }

        var lockOffer = LockOffer(m);
        if (lockOffer != ModLock.None) DrawPadlock(g, LockRect(b), m.Lock != ModLock.None, S);

        // Badges left of the checkbox; conflict / missing-files marker before them.
        var badges = Ui.Badges(m);
        bool broken = m.LoadError != null || m.MissingFiles().Any();
        if (Conflicted.Contains(m)) badges.Insert(0, ("!", Ui.Warn));
        int badgesLeft = Ui.DrawBadges(g, badges, check.Left - (int)(8 * S), check.Top + check.Height / 2, badgeFont, S);
        // A newer version on Nexus: a green "↑ Update" pill before the badges (click: update).
        var pill = Rectangle.Empty;
        if (newer != null)
        {
            const string label = "↑ UPDATE";
            var ts = TextRenderer.MeasureText(g, label, pillFont, Size.Empty, TextFormatFlags.NoPadding);
            int ph = (int)(16 * S), pw = ts.Width + (int)(12 * S);
            pill = new Rectangle(badgesLeft - pw - (int)(6 * S), check.Top + check.Height / 2 - ph / 2, pw, ph);
            using (var path = Ui.Round(pill, ph / 2f))
            using (var f = new SolidBrush(Ui.Enabled)) g.FillPath(f, path);
            TextRenderer.DrawText(g, label, pillFont, pill, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            badgesLeft = pill.Left;
        }
        nexusParts[e.Index] = (pill, mark);

        var nameRect = new Rectangle(textLeft, card.Y + (int)(5 * S), badgesLeft - textLeft, (int)(20 * S));
        Ui.DrawModName(g, m, nameFont, nameRect, m.Enabled ? Ui.Text : Color.FromArgb(200, 200, 205), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Second row: author and tag chips on the left, state and count on the right.
        var second = new Rectangle(textLeft, card.Y + (int)(25 * S), card.Right - pad - textLeft, (int)(16 * S));
        string state = broken ? "Missing Files" : m.Enabled ? "Enabled" : "Disabled";
        string count = "  ·  " + (CostumeLabel?.Invoke(m) ?? Ui.CountText(m));
        var countSize = TextRenderer.MeasureText(g, count, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        var stateSize = TextRenderer.MeasureText(g, state, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        int right = check.Left - (int)(8 * S);
        int stateLeft = right - countSize.Width - stateSize.Width;
        string author = m.Manifest.Author ?? "";
        int authorW = author.Length == 0 ? 0 : Math.Min(TextRenderer.MeasureText(g, author, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width, Math.Max(0, stateLeft - textLeft - (int)(8 * S)));
        TextRenderer.DrawText(g, author, smallFont, new Rectangle(second.X, second.Y, authorW + 1, second.Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var chips = new List<(Rectangle, string)>();
        int cx = textLeft + authorW + (authorW > 0 ? (int)(8 * S) : 0), mid = second.Y + second.Height / 2;
        int limit = stateLeft - (int)(8 * S);
        foreach (string tag in m.Tags)
        {
            int w = Ui.ChipWidth(g, tag, chipFont, S);
            if (cx + w > limit)
            {
                string more = $"+{m.Tags.Count - chips.Count}";
                if (cx + Ui.ChipWidth(g, more, chipFont, S) <= limit) Ui.DrawChip(g, more, chipFont, cx, mid, S, outline: true);
                break;
            }
            chips.Add((Ui.DrawChip(g, tag, chipFont, cx, mid, S, soft: m.KindOf(tag) == Mod.TagKind.Auto, mod: m), tag));
            cx += w + (int)(4 * S);
        }
        regions[e.Index] = (new Rectangle(badgesLeft, check.Top - (int)(2 * S), check.Left - badgesLeft, check.Height + (int)(4 * S)), chips);
        TextRenderer.DrawText(g, count, smallFont, new Point(right - countSize.Width, second.Y + (second.Height - countSize.Height) / 2), Ui.Subtle, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, state, smallFont, new Point(stateLeft, second.Y + (second.Height - stateSize.Height) / 2),
            broken ? Ui.Warn : m.Enabled ? Ui.Enabled : Ui.Subtle, TextFormatFlags.NoPadding);
    }

    /// <summary>The mod's costume icon if it's decoded; otherwise starts decoding it and repaints when it's ready.</summary>
    Image? Icon(Mod m)
    {
        string? file = m.CostumeIconFile();
        if (file == null) return StockIcon(m);
        string key;
        key = file + "|" + Ui.FileStamp(file);
        if (icons.TryGetValue(key, out var img)) return img;
        icons[key] = null;
        int size = (int)(64 * S);
        Task.Run(() => { try { return Ui.DdsThumb(file, size); } catch { return null; } }).ContinueWith(t =>
        {
            if (IsDisposed) { t.Result?.Dispose(); return; }
            icons[key] = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    /// <summary>The stock picture for a mod without one (decoded once per mod, in the background, one at a time).</summary>
    Image? StockIcon(Mod m)
    {
        if (Catalog is not { } cat) return null;
        string key = "stock|" + m.FolderName;
        if (icons.TryGetValue(key, out var img)) return img;
        icons[key] = null;
        int size = (int)(64 * S);
        Task.Run(() =>
        {
            try
            {
                lock (Ui.StockLock)
                    return cat.DefaultIconFor(m) is string tex && cat.Preview(Applier.IconPackages[0].File, tex) is { } p ? Ui.Thumb(p.Bgra, p.W, p.H, size) : null;
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (IsDisposed) { t.Result?.Dispose(); return; }
            icons[key] = t.Result;
            if (t.Result != null) Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    /// <summary>What the part of the list under the mouse does (the tooltip text), or null.</summary>
    string? TipAt(Point p)
    {
        int i = IndexFromPoint(p);
        if (i < 0 || i >= Items.Count || !GetItemRectangle(i).Contains(p)) return null;
        var b = GetItemRectangle(i);
        if (Items[i] is ModGroup grp) return $"{grp.Name}: {grp.Count} mod(s). Click to {(grp.Collapsed ? "open" : "fold")} the group.";
        var m = (Mod)Items[i];
        if (CheckRect(b).Contains(p)) return m.Enabled ? "On. Click to turn it off, then Apply Changes." : "Off. Click to turn it on, then Apply Changes.";
        if (LockRect(b).Contains(p))
            return m.Lock != ModLock.None ? $"Locked at the {(m.Lock == ModLock.Top ? "top" : "bottom")}: it stays in the locked run there (drag or the arrows reorder it among the other locked mods), and other mods can't pass it. Click to unlock."
                 : LockOffer(m) is var l && l != ModLock.None ? $"Click to lock it at the {(l == ModLock.Top ? "top" : "bottom")}: it keeps its place, and new or moved mods can't pass it."
                 : null;
        if (nexusParts.TryGetValue(i, out var np) && (np.Pill.Contains(p) || np.Mark.Contains(p)))
        {
            string have = (m.NexusLink?.Version ?? m.Manifest.Version ?? "?").TrimStart('v', 'V');
            if (UpdateFor?.Invoke(m) is string nv) return $"Update on Nexus: v{nv.TrimStart('v', 'V')} (you have v{have}). Click to update.";
            if (m.NexusModId is int id) return $"Linked to Nexus mod #{id} (you have v{have}). Right-click → Nexus for its page.";
        }
        if (regions.TryGetValue(i, out var r))
        {
            foreach (var (rect, tag) in r.Chips) if (rect.Contains(p)) return $"Tag \"{tag}\" ({Ui.TagDescription(m, tag)}). Click to show only mods with this tag.";
            if (r.Badges.Contains(p))
                return "Changes: " + m.Summary() + (Conflicted.Contains(m) ? ".\n! " + (ConflictText?.Invoke(m) ?? "Some of them are also changed by another enabled mod: the one higher in the list wins.") + "\nSelect it and open the Conflicts tab for the details." : ".");
        }
        return $"{m.Name}\n{m.Summary()}" + (Conflicted.Contains(m) ? "\n! " + (ConflictText?.Invoke(m) ?? "Conflicts with another enabled mod.") : "") +
               "\nDouble-click to edit. Right-click for tags and more." + (CanReorder?.Invoke() == true ? m.Lock == ModLock.None ? " Drag to move it in the order." : $" Drag to move it among the mods locked at the {(m.Lock == ModLock.Top ? "top" : "bottom")}." : "");
    }

    /// <summary>Test hook: the centre of item <paramref name="i"/>'s padlock or checkbox (client coordinates).</summary>
    public Point PartCentre(int i, bool padlock)
    {
        var r = padlock ? LockRect(GetItemRectangle(i)) : CheckRect(GetItemRectangle(i));
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        HideTip();
        int i = IndexFromPoint(e.Location);
        if (i < 0 || i >= Items.Count || !GetItemRectangle(i).Contains(e.Location)) { base.OnMouseDown(e); return; }
        var b = GetItemRectangle(i);
        if (Items[i] is ModGroup grp) { if (e.Button == MouseButtons.Left) GroupClicked?.Invoke(grp); return; }
        var m = (Mod)Items[i];
        if (e.Button == MouseButtons.Right)
        {
            SelectedIndex = i;
            MenuRequested?.Invoke(m, PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        if (CheckRect(b).Contains(e.Location))
        {
            if (marked.Count > 1 && IsMarked(m)) { CheckManyClicked?.Invoke(MarkedMods, !m.Enabled); return; }   // all marked follow this one
            SelectedIndex = i; anchor = i; ClearMarks(); CheckClicked?.Invoke(m); return;
        }
        if (LockRect(b).Contains(e.Location) && LockOffer(m) != ModLock.None) { SelectedIndex = i; LockClicked?.Invoke(m); return; }
        if (nexusParts.TryGetValue(i, out var np) && (np.Pill.Contains(e.Location) || np.Mark.Contains(e.Location)) && UpdateFor?.Invoke(m) != null)
        { SelectedIndex = i; UpdateClicked?.Invoke(m); return; }
        if (regions.TryGetValue(i, out var r))
        {
            foreach (var (rect, tag) in r.Chips)
                if (rect.Contains(e.Location)) { TagClicked?.Invoke(tag); return; }

        }
        // Shift: mark the range from the last plain click; Ctrl: add / remove this card. The clicked card's details show.
        var keys = ModifierKeys;
        if ((keys & Keys.Shift) != 0 && anchor >= 0 && anchor < Items.Count)
        {
            SelectedIndex = i; MarkRange(anchor, i); Focus(); return;
        }
        if ((keys & Keys.Control) != 0)
        {
            if (marked.Count == 0 && SelectedItem is Mod cur) marked.Add(cur.FolderName);
            if (!marked.Remove(m.FolderName)) marked.Add(m.FolderName);
            SelectedIndex = i; anchor = i; Invalidate(); MarksChanged?.Invoke(marked.Count); Focus(); return;
        }
        bool group = marked.Count > 1 && IsMarked(m);
        clearOnUp = group;                       // a plain click inside the marking: kept in case this becomes a group drag
        if (!group) { int was = marked.Count; marked.Clear(); marked.Add(m.FolderName); if (was > 1) { Invalidate(); MarksChanged?.Invoke(1); } }
        anchor = i;
        // A press on the card itself can become a drag (priority view, unlocked mods; a marked group when none is locked).
        // A locked mod drags within its locked run (Kurt); a marked group only when all share one region.
        dragMod = CanReorder?.Invoke() == true && (!group || MarkedMods.All(x => x.Lock == m.Lock)) ? m : null;
        dragFrom = e.Location;
        leftDown = true;   // our own record: MouseEventArgs.Button on a move reflects the hardware state, not the message
        base.OnMouseDown(e);
    }

    void UpdateDrop(Point p)
    {
        int i = IndexFromPoint(p);
        if (i < 0 || i >= Items.Count) i = p.Y < 0 ? TopIndex : Items.Count - 1;
        var r = GetItemRectangle(i);
        bool below = p.Y > r.Top + r.Height / 2;
        if (i == dropItem && below == dropBelow) return;
        (dropItem, dropBelow) = (i, below);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        leftDown = false;
        if (!dragging)
        {
            dragMod = null;
            if (clearOnUp) { clearOnUp = false; ClearMarks(); }   // a plain click on a marked card: just that one
            return;
        }
        clearOnUp = false;
        var (m, i, below) = (dragMod, dropItem, dropBelow);
        var group = MarkedMods;
        EndDrag();
        if (m == null || i < 0 || i >= Items.Count || Items[i] is not Mod target) return;
        if (group.Count > 1 && group.Contains(m)) { if (!group.Contains(target)) DroppedMany?.Invoke(group, target, below); }
        else if (target != m) Dropped?.Invoke(m, target, below);
    }

    void EndDrag()
    {
        dragging = false; dragMod = null; dropItem = -1; scrollTimer.Stop();
        Capture = false; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && dragging) { EndDrag(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && marked.Count > 1) { ClearMarks(); e.Handled = true; return; }
        base.OnKeyUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragMod != null && leftDown)
        {
            var ds = SystemInformation.DragSize;
            if (!dragging && (Math.Abs(e.X - dragFrom.X) > ds.Width || Math.Abs(e.Y - dragFrom.Y) > ds.Height))
            { dragging = true; HideTip(); tipText = null; Capture = true; Cursor = Cursors.SizeNS; scrollTimer.Start(); }
            if (dragging) { UpdateDrop(e.Location); return; }
        }
        else if (dragging) EndDrag();
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && (i >= Items.Count || !GetItemRectangle(i).Contains(e.Location))) i = -1;
        if (i != hover) { int old = hover; hover = i; if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old)); if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i)); }
        string? t = TipAt(e.Location);
        if (t != tipText) { HideTip(); tipText = t; if (t != null) tipTimer.Start(); }
    }

    void HideTip() { tipTimer.Stop(); if (tipShown) { tips.Hide(this); tipShown = false; } }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); HideTip(); tipText = null; if (hover >= 0 && hover < Items.Count) Invalidate(GetItemRectangle(hover)); hover = -1; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && SelectedItem is Mod m)
        {
            if (marked.Count > 1 && IsMarked(m)) CheckManyClicked?.Invoke(MarkedMods, !m.Enabled); else CheckClicked?.Invoke(m);
            e.Handled = true; return;
        }
        // Shift+Up / Down extends the marking from the anchor; plain arrows go back to one card. (Ctrl+arrows are the
        // window's priority keys, which move the whole marking.)
        if (e.KeyCode is Keys.Up or Keys.Down && !e.Control)
        {
            int step = e.KeyCode == Keys.Up ? -1 : 1, to = SelectedIndex + step;
            while (to >= 0 && to < Items.Count && Items[to] is not Mod) to += step;
            if (to >= 0 && to < Items.Count)
            {
                if (e.Shift) { if (anchor < 0) anchor = SelectedIndex; SelectedIndex = to; MarkRange(anchor, to); }
                else { SelectedIndex = to; anchor = to; ClearMarks(); }
            }
            e.Handled = true; return;
        }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing) { if (disposing) { tipTimer.Dispose(); tips.Dispose(); scrollTimer.Dispose(); } base.Dispose(disposing); }
}
