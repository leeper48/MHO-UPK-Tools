using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The manager's look (modelled on MHModManager's WPF window): dark surfaces, a purple accent for the main actions,
/// coloured category badges (P packages, T textures, S strings, A audio), mod cards in the list, flat tabs.
/// Sizes are in 96-dpi units and scaled by the control's DPI.
/// </summary>
static class Ui
{
    public static readonly Color Back = Color.FromArgb(30, 30, 32);
    public static readonly Color Bar = Color.FromArgb(36, 36, 40);
    public static readonly Color Card = Color.FromArgb(36, 36, 40);
    public static readonly Color CardHover = Color.FromArgb(44, 44, 50);
    public static readonly Color CardSelected = Color.FromArgb(48, 50, 86);
    public static readonly Color Line = Color.FromArgb(52, 52, 58);
    public static readonly Color Text = Color.FromArgb(232, 232, 236);
    public static readonly Color Subtle = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(108, 99, 255);
    public static readonly Color AccentHover = Color.FromArgb(128, 120, 255);
    public static readonly Color Enabled = Color.FromArgb(76, 190, 110);
    public static readonly Color Warn = Color.FromArgb(235, 90, 80);
    public static readonly Color Packages = Color.FromArgb(235, 150, 60);
    public static readonly Color Textures = Color.FromArgb(125, 130, 255);
    public static readonly Color Strings = Color.FromArgb(80, 195, 120);
    public static readonly Color Audio = Color.FromArgb(230, 110, 175);
    /// <summary>Window background: a vertical gradient from Kurt's navy (sampled from his reference image) to the dark grey.</summary>
    public static readonly Color GradientTop = Color.FromArgb(10, 35, 74);
    public static readonly Color GradientBottom = Color.FromArgb(30, 30, 32);
    /// <summary>Translucent dark overlay for the top / bottom bars on the gradient.</summary>
    public static readonly Color BarOverlay = Color.FromArgb(90, 0, 0, 0);
    /// <summary>Text on a filled badge / pill.</summary>
    public static readonly Color OnColor = Color.FromArgb(18, 18, 22);

    /// <summary>
    /// Fills <paramref name="area"/> (in <paramref name="c"/>'s coordinates) with the window gradient, aligned to the whole
    /// form, so controls that draw their own background (card list, tables, headers) continue it seamlessly.
    /// </summary>
    public static void PaintGradient(Graphics g, Control c, Rectangle area)
    {
        var form = c.FindForm();
        if (form == null || form.ClientSize.Height <= 0) { using var b = new SolidBrush(GradientBottom); g.FillRectangle(b, area); return; }
        var origin = c == form ? Point.Empty : form.PointToClient(c.PointToScreen(Point.Empty));
        var rect = new Rectangle(-origin.X, -origin.Y, Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height));
        using var brush = new LinearGradientBrush(rect, GradientTop, GradientBottom, LinearGradientMode.Vertical);
        g.FillRectangle(brush, area);
    }

    /// <summary>
    /// A dark tooltip (owner-drawn: the system one is a pale yellow box). <paramref name="text"/> supplies the text for
    /// tips shown with Show() (the mod list); otherwise the SetToolTip text is used.
    /// </summary>
    public static ToolTip NewTips(Func<string?>? text = null)
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 20000, ShowAlways = true };
        tip.Popup += (_, e) =>
        {
            LimitTipWidth(tip);   // (for tips made elsewhere than Tip: from their second showing)
            string t = text?.Invoke() ?? (tip == Tips && shownText != null ? shownText : e.AssociatedControl != null ? tip.GetToolTip(e.AssociatedControl) ?? "" : "");
            e.ToolTipSize = MeasureTip(t, e.AssociatedControl?.DeviceDpi ?? 96);
        };
        tip.Draw += (_, e) => PaintTip(e.Graphics, e.Bounds, e.ToolTipText, e.AssociatedControl?.DeviceDpi ?? 96);
        return tip;
    }
    static readonly Font TipFont = Regular(9f);
    static readonly Font TipTitleFont = Bold(9f);
    const TextFormatFlags TipFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
    static (int Pad, int Max) TipMetrics(int dpi) { float sc = dpi / 96f; return ((int)(7 * sc), (int)(380 * sc)); }

    /// <summary>A tooltip's size: its title (bold) over its text, wrapped at 380 px (scaled).</summary>
    static Size MeasureTip(string t, int dpi)
    {
        var (pad, max) = TipMetrics(dpi);
        var (title, body) = SplitTitle(t);
        var size = body.Length > 0 ? TextRenderer.MeasureText(body, TipFont, new Size(max, 0), TipFlags) : Size.Empty;
        if (title != null)
        {
            var ts = TextRenderer.MeasureText(title, TipTitleFont, new Size(max, 0), TipFlags);
            size = new Size(Math.Max(size.Width, ts.Width), ts.Height + (body.Length > 0 ? size.Height + pad / 2 : 0));
        }
        return new Size(size.Width + 2 * pad, size.Height + 2 * pad);
    }

    /// <summary>Paints a tooltip: dark box, accent border, the title bold in the teal-cyan of the content tags (Kurt,
    /// 2026-10-04), then the text.</summary>
    static void PaintTip(Graphics g, Rectangle bounds, string t, int dpi)
    {
        var (pad, _) = TipMetrics(dpi);
        using (var bg = new SolidBrush(Color.FromArgb(34, 36, 46))) g.FillRectangle(bg, bounds);
        using (var pen = new Pen(Color.FromArgb(108, 99, 255))) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var inner = Rectangle.Inflate(bounds, -pad, -pad);
        var (title, body) = SplitTitle(t);
        if (title != null)
        {
            var ts = TextRenderer.MeasureText(title, TipTitleFont, new Size(inner.Width, 0), TipFlags);
            TextRenderer.DrawText(g, title, TipTitleFont, inner, TagContent, TipFlags);
            inner = Rectangle.FromLTRB(inner.Left, inner.Top + ts.Height + pad / 2, inner.Right, inner.Bottom);
        }
        if (body.Length > 0) TextRenderer.DrawText(g, body, TipFont, inner, Text, TipFlags);
    }

    /// <summary>Test: a tooltip rendered as it shows.</summary>
    internal static Bitmap RenderTipForTest(string t, int dpi)
    {
        var size = MeasureTip(t, dpi);
        var bmp = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(bmp);
        PaintTip(g, new Rectangle(Point.Empty, size), t, dpi);
        return bmp;
    }

    /// <summary>Marks a tooltip's first line as its title (TipTitled): drawn bold, in the teal-cyan.</summary>
    public const char TitleMark = '\u0001';

    /// <summary>A tooltip headed by <paramref name="title"/> (an icon button's name), then <paramref name="text"/>.</summary>
    public static T TipTitled<T>(T c, string title, string? text) where T : Control
    {
        LimitTipWidth(Tips);
        Tips.SetToolTip(c, Titled(title, text));
        return c;
    }

    /// <summary>The tooltip text for a title + text (for tooltips set by hand, such as Undo's).</summary>
    public static string Titled(string title, string? text) => TitleMark + title + (string.IsNullOrEmpty(text) ? "" : "\n" + text);

    static (string? Title, string Body) SplitTitle(string t)
    {
        if (t.Length == 0 || t[0] != TitleMark) return (null, t);
        int nl = t.IndexOf('\n');
        return nl < 0 ? (t[1..], "") : (t[1..nl], t[(nl + 1)..]);
    }

    /// <summary>The text a tip shown with Show() carries (a disabled control's: DisabledTips), read by Popup.</summary>
    static string? shownText;

    /// <summary>
    /// Tooltips for disabled controls (Kurt, 2026-10-04: a grayed-out button should still say what it does). Windows sends a
    /// disabled control no mouse messages (they go to its parent), so its ToolTip never fires: this filter watches mouse moves,
    /// finds the control under the cursor (disabled ones included) and shows its tip after the usual delay.
    /// </summary>
    internal static List<string> TipDebugLog => DisabledTips.DebugLog;

    sealed class DisabledTips : IMessageFilter
    {
        readonly System.Windows.Forms.Timer timer = new() { Interval = 450 };
        Control? over, shownOn;
        public DisabledTips() { timer.Tick += (_, _) => { timer.Stop(); Show(); }; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is 0x200 or 0x2A3)   // WM_MOUSEMOVE, WM_MOUSELEAVE
            {
                // over a modal window, the main window's shared tooltip is put away (Kurt: the 3D view's tip showed through
                // the Tag Colors window); back when the cursor leaves it
                var formUnder = Control.FromChildHandle(WindowFromPoint(Cursor.Position))?.FindForm();
                bool overModal = formUnder?.Modal == true;
                if (overModal == Tips.Active) Tips.Active = !overModal;
                var c = Under();
                if (Debug) DebugLog.Add($"{Environment.TickCount64 % 100000} msg {m.Msg:X} under {c?.AccessibleName ?? "-"} over {over?.AccessibleName ?? "-"}");
                if (c != over) { Hide(); over = c; timer.Stop(); if (c != null) timer.Start(); }
            }
            else if (m.Msg is 0x201 or 0x204 or 0x20A) Hide();   // a click or the wheel
            return false;
        }

        static Control? Under()
        {
            var pos = Cursor.Position;
            // the window under the cursor (not the active one: a window can be shown without being activated)
            var form = Control.FromChildHandle(WindowFromPoint(pos))?.FindForm() ?? Form.ActiveForm;
            if (form == null || !form.Bounds.Contains(pos)) return null;
            Control at = form;
            while (at.GetChildAtPoint(at.PointToClient(pos), GetChildAtPointSkip.Invisible) is Control child) at = child;
            // a disabled control, or any control of a modal window: the shared tooltip belongs to the main window, which a
            // modal window disables, so it never shows there (measured: --tip-dialog-test; Kurt: Tag Colors had no tooltips)
            return (!at.Enabled || form.Modal) && at.Parent != null && !string.IsNullOrEmpty(Tips.GetToolTip(at)) ? at : null;
        }

        // a modal window's own tooltip (the shared one, owned by the main window, stays hidden over it), freed with the window
        readonly Dictionary<Form, ToolTip> modalTips = new();
        ToolTip? shownBy;

        ToolTip TipsFor(Control c)
        {
            if (c.FindForm() is not { Modal: true } f) return Tips;
            if (!modalTips.TryGetValue(f, out var t))
            {
                modalTips[f] = t = NewTips(() => shownText);
                f.FormClosed += (_, _) => { if (modalTips.Remove(f, out var old)) old.Dispose(); };
            }
            return t;
        }

        static readonly bool Debug = Environment.GetEnvironmentVariable("MHO_TIPDEBUG") == "1";
        public static readonly List<string> DebugLog = [];

        void Show()
        {
            if (Debug) DebugLog.Add($"{Environment.TickCount64 % 100000} show over {over?.AccessibleName}, under now {Under()?.AccessibleName}");
            if (over == null || over.Parent == null || Under() != over) return;
            var parent = over.Parent;
            shownText = Tips.GetToolTip(over);
            shownBy = TipsFor(over);
            // (measured: Show's point is taken from the window's outer corner, not its client area: a form's title bar put the
            // tip over the cursor)
            var outer = parent.Parent == null ? parent.Bounds : parent.Parent.RectangleToScreen(parent.Bounds);
            var cur = Cursor.Position;
            shownBy.Show(shownText, parent, cur.X - outer.X, cur.Y - outer.Y + (int)(22 * parent.DeviceDpi / 96f), Tips.AutoPopDelay);
            shownOn = parent;
        }

        void Hide()
        {
            if (Debug && shownOn != null) DebugLog.Add($"{Environment.TickCount64 % 100000} hide ({new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name})");
            if (shownOn != null) { (shownBy ?? Tips).Hide(shownOn); shownOn = null; }
            shownText = null;
        }
    }

    /// <summary>
    /// Keeps a tooltip by the mouse (Kurt, 0.37.138: tips far left of the mouse). Measured with --tip-place-test on Kurt's
    /// three monitors: the second tip shown landed at x = 0 with the right y (cursor + 26), whichever control it was for, so
    /// something in the show path resets x. The tip's window is watched (TipPlacer) and a tip about to show on the cursor's
    /// line but not under it is moved to the cursor, kept on the cursor's monitor. The native tip also gets our wrap width.
    /// Done on the UI thread only (reading Handle makes the window on the calling thread).
    /// </summary>
    static void LimitTipWidth(ToolTip tip)
    {
        if (!Application.MessageLoop || !limited.Add(tip)) return;
        try
        {
            var prop = typeof(ToolTip).GetProperty("Handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop?.GetValue(tip) is IntPtr h && h != IntPtr.Zero)
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                SendMessage(h, 0x418 /* TTM_SETMAXTIPWIDTH */, IntPtr.Zero, (IntPtr)(int)(380 * g.DpiX / 96f));
                new TipPlacer().AssignHandle(h);
            }
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or InvalidOperationException) { }
    }

    sealed class TipPlacer : NativeWindow
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr Hwnd, After; public int X, Y, Cx, Cy; public uint Flags; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);   // (WinForms' own placement first)
            if (m.Msg != 0x46 /* WM_WINDOWPOSCHANGING */ || m.LParam == IntPtr.Zero) return;
            var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            const uint NoMove = 0x2, NoSize = 0x1, Show = 0x40;
            if ((wp.Flags & Show) == 0 && !IsWindowVisible(Handle)) return;
            GetWindowRect(Handle, out var r);
            int x = (wp.Flags & NoMove) != 0 ? r.Left : wp.X, y = (wp.Flags & NoMove) != 0 ? r.Top : wp.Y;
            int cx = (wp.Flags & NoSize) != 0 ? r.Right - r.Left : wp.Cx;
            var cur = Cursor.Position;
            if (cx <= 0 || (cur.X >= x - 8 && cur.X <= x + cx + 8) || Math.Abs(y - cur.Y) > 120) return;   // under the mouse, or placed elsewhere on purpose
            var wa = Screen.FromPoint(cur).WorkingArea;
            wp.X = Math.Max(wa.Left, Math.Min(cur.X, wa.Right - cx));
            wp.Y = y;
            wp.Flags &= ~NoMove;
            System.Runtime.InteropServices.Marshal.StructureToPtr(wp, m.LParam, false);
        }
    }

    static readonly HashSet<ToolTip> limited = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(Point p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Test: the native max width of the app's tooltip (TTM_GETMAXTIPWIDTH), 0 when it can't be read.</summary>
    internal static int TipMaxWidthForTest()
    {
        var prop = typeof(ToolTip).GetProperty("Handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return prop?.GetValue(Tips) is IntPtr h && h != IntPtr.Zero ? (int)SendMessage(h, 0x419, IntPtr.Zero, IntPtr.Zero) : 0;
    }

    // Tag colours by category (Kurt: all teams one colour, all characters another). Chosen apart from the badge
    // colours (orange packages, blue-violet textures, green strings, pink audio).
    public static readonly Color TagCharacter = Color.FromArgb(240, 200, 80);    // gold
    public static readonly Color TagTeam = Color.FromArgb(90, 175, 240);         // sky blue
    public static readonly Color TagContent = Color.FromArgb(60, 200, 190);      // teal: Costume, Power effects …
    public static readonly Color TagMod = Color.FromArgb(200, 170, 250);         // lavender: the mod's own tags
    public static readonly Color TagUser = Color.FromArgb(205, 205, 215);        // light grey: your tags

    /// <summary>A tag's colour: by what it names (character, team, content), else by who set it (the mod, or you).</summary>
    public static Color TagColor(string tag, Mod? m = null) => AutoTags.Classify(tag) switch
    {
        AutoTags.TagClass.Character => TagCharacter,
        AutoTags.TagClass.Team => TagTeam,
        AutoTags.TagClass.Content => TagContent,
        _ => m != null && m.KindOf(tag) == Mod.TagKind.Mod ? TagMod : TagUser,
    };

    /// <summary>"team, automatic", "character, user", "from the mod" … for tooltips.</summary>
    public static string TagDescription(Mod m, string tag)
    {
        string what = AutoTags.Classify(tag) switch { AutoTags.TagClass.Character => "character, ", AutoTags.TagClass.Team => "team, ", AutoTags.TagClass.Content => "content, ", _ => "" };
        return what + TagSource(m, tag);
    }

    /// <summary>
    /// Draws a tag chip at x, vertically centred on midY; returns its rectangle. Filled (tag colour, dark text) for the
    /// mod's own and the user's tags; <paramref name="soft"/> (tinted, coloured text and border) for automatic tags;
    /// <paramref name="outline"/> (subtle) for "+ Tag" / "+N".
    /// </summary>
    public static Rectangle DrawChip(Graphics g, string text, Font font, int x, int midY, float s, bool outline = false, bool soft = false, Mod? mod = null)
    {
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var r = new Rectangle(x, midY - (size.Height + (int)(3 * s)) / 2, size.Width + (int)(12 * s), size.Height + (int)(3 * s));
        var color = TagColor(text, mod);
        using (var path = Round(r, r.Height / 2f))
        {
            if (outline) { using var pen = new Pen(Subtle, Math.Max(1f, 1f * s)); g.DrawPath(pen, path); }
            else if (soft)
            {
                using var f = new SolidBrush(Color.FromArgb(45, color)); g.FillPath(f, path);
                using var pen = new Pen(Color.FromArgb(150, color), Math.Max(1f, 1f * s)); g.DrawPath(pen, path);
            }
            else { using var f = new SolidBrush(color); g.FillPath(f, path); }
        }
        TextRenderer.DrawText(g, text, font, r, outline ? Subtle : soft ? color : OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return r;
    }

    /// <summary>"automatic", "from the mod" or "user", for tooltips.</summary>
    public static string TagSource(Mod m, string tag) => m.KindOf(tag) switch { Mod.TagKind.User => "user", Mod.TagKind.Mod => "from the mod", _ => "automatic" };

    public static int ChipWidth(Graphics g, string text, Font font, float s) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)(12 * s);

    /// <summary>
    /// Opens a menu under a button, kept on the button's own monitor (a user with two monitors: Settings, at the window's
    /// right edge, opened its menu on the other screen; Windows only keeps menus inside the whole desktop). Aligned to the
    /// button's right edge when it doesn't fit, above the button when there's no room below; submenus open to the left there.
    /// </summary>
    public static void ShowUnder(ContextMenuStrip menu, Control button)
    {
        var area = Screen.FromControl(button).WorkingArea;
        ShowOnScreen(menu, PlaceUnder(button.RectangleToScreen(button.ClientRectangle), menu.GetPreferredSize(Size.Empty), area), area);
    }

    /// <summary>Where a menu of <paramref name="menu"/> size goes under a button (screen rectangle) on a monitor's work area.</summary>
    internal static Point PlaceUnder(Rectangle button, Size menu, Rectangle area)
    {
        int x = button.Left, y = button.Bottom;
        if (x + menu.Width > area.Right) x = button.Right - menu.Width;      // right-aligned to the button
        if (y + menu.Height > area.Bottom) y = button.Top - menu.Height;     // above it
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    /// <summary>A menu at a screen point (right-click), kept on that point's monitor.</summary>
    public static void ShowAt(ContextMenuStrip menu, Point screenPoint) => ShowOnScreen(menu, screenPoint, Screen.FromPoint(screenPoint).WorkingArea, flip: true);

    static void ShowOnScreen(ContextMenuStrip menu, Point p, Rectangle area, bool flip = false)
    {
        var size = menu.GetPreferredSize(Size.Empty);
        int x = p.X, y = p.Y;
        if (x + size.Width > area.Right) x = flip ? p.X - size.Width : area.Right - size.Width;
        if (y + size.Height > area.Bottom) y = flip ? p.Y - size.Height : area.Bottom - size.Height;
        x = Math.Max(area.Left, x); y = Math.Max(area.Top, y);
        // Near the right edge, submenus (Nexus, Tags …) open to the left, so they stay on this monitor too.
        bool left = x + size.Width + 260 * menu.DeviceDpi / 96 > area.Right;
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>()) if (item.HasDropDownItems) item.DropDownDirection = left ? ToolStripDropDownDirection.Left : ToolStripDropDownDirection.Right;
        menu.Show(new Point(x, y));
    }

    /// <summary>
    /// A notification's heading as its only button (Kurt: no separate Close; the word itself, e.g. "Success", continues;
    /// Enter too): large, bold, in the tone's colour (green success, red error, else the accent).
    /// </summary>
    public static Button HeadingButton(string text, Color color, Action action, float points = 13f)
    {
        var b = AccentButton(text, action, "Continue (Enter).");
        b.Font = Bold(points);
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(18, 6, 18, 6);
        b.BackColor = color; b.ForeColor = color == Accent ? Color.White : OnColor;
        b.FlatAppearance.BorderColor = color;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.25f);
        return b;
    }

    /// <summary>A one-line text prompt in the dark theme (with suggestions); null when cancelled or empty.</summary>
    public static string? Prompt(IWin32Window owner, string title, string label, string initial = "", IEnumerable<string>? suggestions = null, bool secret = false)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                                 StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Regular(9.5f), Padding = new Padding(12) };
        DarkFrame(f);
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = TitleCase(title), AutoSize = true, Font = Bold(12f), Margin = new Padding(0, 0, 0, 8) });   // as every popup
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size((int)(460 * f.DeviceDpi / 96f), 0) });
        var box = new TextBox { Text = initial, Width = (int)(320 * f.DeviceDpi / 96f), Margin = new Padding(0, 0, 0, 10), UseSystemPasswordChar = secret };
        if (suggestions != null)
        {
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend; box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            box.AutoCompleteCustomSource.AddRange(suggestions.ToArray());
        }
        t.Controls.Add(box);
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        var ok = AccentButton("OK", () => f.DialogResult = DialogResult.OK, "OK (Enter)"); var cancel = FlatButton("Cancel", () => f.DialogResult = DialogResult.Cancel, "Cancel (Esc)");
        ok.AccessibleDescription = cancel.AccessibleDescription = Icons.KeepText;   // (a prompt's answers keep their words)
        buttons.Controls.AddRange([cancel, ok]);
        t.Controls.Add(buttons);
        f.Controls.Add(t);
        f.AcceptButton = ok; f.CancelButton = cancel;
        MhoPackageModifier.Gui.Theme.Apply(f, MhoPackageModifier.Gui.Palette.Dark); Modern.Modernize(f);
        RestyleButtons(f);
        box.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    // Microsoft-style Title Case: articles, conjunctions and prepositions of four letters or fewer stay lower case,
    // except as the first word of the text or after ":" / "·" / "(" / "—".
    static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
    { "a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet", "as", "at", "by", "in", "of", "on", "per", "to", "via", "from", "into", "onto", "with", "over", "than", "like" };   // not "off" / "up": Turn Off, Set Up

    /// <summary>
    /// Title Case for labels and status lines (Kurt: every UI text in Title Case). Text in double quotes (mod and
    /// tag names) is left as it is, and so is the rest of each word (MHModManager, PC, .dds stay).
    /// </summary>
    public static string TitleCase(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool quoted = false, start = true;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"') { quoted = !quoted; sb.Append(c); i++; continue; }
            if (!quoted && char.IsLetter(c))
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '\'' || text[j] == '’')) j++;
                string word = text[i..j];
                bool afterDot = i > 0 && text[i - 1] == '.';   // .dds, .png, file extensions
                bool plural = word == "s" && i > 0 && text[i - 1] == '(';   // file(s), mod(s)
                bool minor = MinorWords.Contains(word) && !start;
                sb.Append(afterDot || minor || plural ? word : char.ToUpperInvariant(word[0]) + word[1..]);
                start = false;
                i = j;
                continue;
            }
            if (!quoted && (c == ':' || c == '·' || c == '(' || c == '—')) start = true;
            else if (!quoted && !char.IsWhiteSpace(c) && c != '-' && c != '●') start = start && (c == ' ');
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>The " (on Classic)" end of a moved costume mod's name (drawn in amber: our override, not part of the mod's
    /// name, Kurt), or null. Set by MainForm (it knows the costume each mod is for).</summary>
    public static Func<Mod, (string Suffix, bool OtherHero)?>? OverrideSuffix { get; set; }
    /// <summary>Kurt: amber for a costume moved to another hero, teal for one moved within its hero.</summary>
    public static readonly Color OverrideAmber = Color.FromArgb(242, 170, 60), OverrideTeal = TagContent;

    /// <summary>A mod's name in <paramref name="rect"/>: the name, then its override suffix in amber (the name is shortened first).</summary>
    public static void DrawModName(Graphics g, Mod m, Font font, Rectangle rect, Color color, TextFormatFlags flags)
    {
        var over = OverrideSuffix?.Invoke(m);
        string? suffix = over?.Suffix;
        if (suffix == null || !m.Name.EndsWith(suffix, StringComparison.Ordinal)) { TextRenderer.DrawText(g, m.Name, font, rect, color, flags); return; }
        string head = m.Name[..^suffix.Length];
        var nf = flags | TextFormatFlags.NoPadding;
        const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;   // no ellipsis: with Size.Empty it shortens to nothing
        int sw = TextRenderer.MeasureText(g, suffix, font, Size.Empty, measure).Width;
        int hw = Math.Min(TextRenderer.MeasureText(g, head, font, Size.Empty, measure).Width, Math.Max(0, rect.Width - sw));
        TextRenderer.DrawText(g, head, font, new Rectangle(rect.X, rect.Y, hw, rect.Height), color, nf | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, suffix, font, new Rectangle(rect.X + hw, rect.Y, Math.Max(0, rect.Width - hw), rect.Height), over!.Value.OtherHero ? OverrideAmber : OverrideTeal, nf);
    }

    /// <summary>A small preview of a .dds (null if it can't be read).</summary>
    /// <summary>
    /// A file's identity for picture caches: size, last write and creation time. A saved or updated mod copies its files
    /// (a copy keeps the source's write time), so the write time alone missed a replaced icon (Kurt: the preview's
    /// thumbnails kept the old images); the new copy's creation time changes. "" when the file can't be read.
    /// </summary>
    public static string FileStamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{fi.CreationTimeUtc.Ticks}" : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    public static Image? DdsThumb(string path, int size)
    {
        if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            // A custom picture (.png / .jpg / .bmp: ModPictures), read without locking the file.
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var src = Image.FromStream(ms);
            float k = Math.Min(1f, Math.Min((float)size / src.Width, (float)size / src.Height));
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }
        var d = TextureDecode.ReadDds(path, out _);
        var bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
        return bgra == null ? null : Thumb(bgra, d!.Value.W, d.Value.H, size);
    }

    /// <summary>A thumbnail (at most size × size) from decoded BGRA pixels.</summary>
    public static Image Thumb(byte[] bgra, int w, int h, int size)
    {
        using var full = TextureDecode.ToBitmap(bgra, w, h);
        float k = Math.Min(1f, Math.Min((float)size / full.Width, (float)size / full.Height));
        var bmp = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    public static Font Regular(float pt = 9.75f) => new("Segoe UI", pt);
    public static Font Bold(float pt = 10f) => new("Segoe UI Semibold", pt);
    /// <summary>True bold, for the letters on the small solid badges (Kurt: legibility).</summary>
    public static Font Heavy(float pt) => new("Segoe UI", pt, FontStyle.Bold);

    /// <summary>The mod's categories as badges: the full word when it has one kind of change (as MHModManager shows), letters otherwise.</summary>
    public static List<(string Text, Color Color)> Badges(Mod m)
    {
        var all = new List<(string Letter, string Word, Color Color)>();
        if (m.Manifest.UpkReplacements.Count > 0) all.Add(("P", "Packages", Packages));
        if (m.Manifest.Replacements.Count + m.Manifest.AchievementReplacements.Count + m.Manifest.StoreReplacements.Count + m.Manifest.Extra.Count() > 0) all.Add(("T", "Textures", Textures));
        if (m.Strings.Count > 0) all.Add(("S", "Strings", Strings));
        if (m.Manifest.AudioPacks.Count > 0) all.Add(("A", "Audio", Audio));
        return all.Count == 1 ? [(all[0].Word, all[0].Color)] : all.Select(a => (a.Letter, a.Color)).ToList();
    }

    public static int Modifications(Mod m) =>
        m.Manifest.UpkReplacements.Count + m.Manifest.Replacements.Count + m.Manifest.AchievementReplacements.Count + m.Manifest.StoreReplacements.Count + m.Manifest.Extra.Count() + m.Strings.Count + m.Manifest.AudioPacks.Count;

    public static string CountText(Mod m) =>
        Badges(m) is [("Packages", _)] ? $"{m.Manifest.UpkReplacements.Count} Package(s)" : $"{Modifications(m)} Modification(s)";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Draws badges right-aligned ending at <paramref name="right"/>, vertically centred on <paramref name="midY"/>; returns their left edge.</summary>
    public static int DrawBadges(Graphics g, List<(string Text, Color Color)> badges, int right, int midY, Font font, float scale, bool filled = true)
    {
        int x = right;
        for (int i = badges.Count - 1; i >= 0; i--)
        {
            var (text, color) = badges[i];
            var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int w = Math.Max((int)(18 * scale), size.Width + (int)(10 * scale)), h = (int)(18 * scale);
            var r = new Rectangle(x - w, midY - h / 2, w, h);
            using (var path = Round(r, 3 * scale))
            {
                // Solid colour with dark text (Kurt: inverted from the outlined look).
                if (filled) { using var b = new SolidBrush(color); g.FillPath(b, path); }
                else { using var pen = new Pen(color, Math.Max(1f, scale)); g.DrawPath(pen, path); }
            }
            TextRenderer.DrawText(g, text, font, r, filled ? OnColor : color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x = r.Left - (int)(4 * scale);
        }
        return x;
    }

    public static Button AccentButton(string text, Action onClick, string? tip = null) => Tip(Style(new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3) }, onClick, accent: true), tip);
    public static Button FlatButton(string text, Action onClick, string? tip = null) => Tip(Style(new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3) }, onClick, accent: false), tip);

    /// <summary>The app's one tooltip (dark). Every button gets a tip where it's made (Kurt: tooltips on all buttons);
    /// --tooltip-audit lists any that don't.</summary>
    public static readonly ToolTip Tips = NewTips();
    /// <summary>Stock images (the original icon packages) are decoded one at a time (the list's and the preview's pictures).</summary>
    public static readonly object StockLock = new();
    public static T Tip<T>(T c, string? text) where T : Control { if (!string.IsNullOrEmpty(text)) { LimitTipWidth(Tips); Tips.SetToolTip(c, text); } return c; }

    /// <summary>Buttons under a control that have no tooltip (on the shared tooltip or <paramref name="other"/>).</summary>
    public static List<string> MissingTips(Control root, params ToolTip[] other)
    {
        var missing = new List<string>();
        void Walk(Control c)
        {
            if (c is Button b && string.IsNullOrEmpty(Tips.GetToolTip(b)) && other.All(o => string.IsNullOrEmpty(o.GetToolTip(b)))) missing.Add(b.Text.Replace("&", ""));
            foreach (Control k in c.Controls) Walk(k);
        }
        Walk(root);
        return missing;
    }

    /// <summary>Draws a bar above (top) or below the button's ▲ / ▼, making it a "to the top / bottom" button that matches the
    /// weight of the plain arrows (the ⤒ ⤓ glyphs came out thin).</summary>
    public static void AddEndBar(Button b, bool top)
    {
        b.Paint += (_, e) =>
        {
            var size = TextRenderer.MeasureText(e.Graphics, b.Text, b.Font, Size.Empty, TextFormatFlags.NoPadding);
            float s = b.DeviceDpi / 96f;
            float w = size.Width * 0.9f, x = (b.Width - w) / 2f;
            float glyphTop = (b.Height - size.Height) / 2f + size.Height * 0.22f, glyphBottom = (b.Height + size.Height) / 2f - size.Height * 0.2f;
            float y = top ? glyphTop - 2.5f * s : glyphBottom + 1f * s;
            using var brush = new SolidBrush(b.Enabled ? b.ForeColor : Ui.DisabledText);
            e.Graphics.FillRectangle(brush, x, y, w, Math.Max(1.5f, 1.6f * s));
        };
    }

    /// <summary>Text of a disabled button (Kurt: medium grey, not the near-black Windows draws on our dark buttons).</summary>
    public static readonly Color DisabledText = Color.FromArgb(128, 128, 136);

    // ---- the dark theme for everything Windows draws itself (Kurt: every popup dark with light text)

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> darkFrames = new();

    /// <summary>
    /// A window's title bar and frame in the app's colours: Windows' dark mode (Windows 10 1809+), and on Windows 11 the
    /// caption colour itself (<paramref name="caption"/>, default the popups' background) with light text, since "show
    /// accent colour on title bars" otherwise paints them in the system accent (seen teal on Kurt's PC).
    /// </summary>
    public static void DarkFrame(Form f, Color? caption = null)
    {
        if (darkFrames.TryGetValue(f, out _)) return;
        darkFrames.Add(f, new object());
        void Apply()
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE (19 before 20H1)
            int border = ColorTranslator.ToWin32(Line), cap = ColorTranslator.ToWin32(caption ?? Back), text = ColorTranslator.ToWin32(Text);
            DwmSetWindowAttribute(f.Handle, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR (Windows 11)
            DwmSetWindowAttribute(f.Handle, 35, ref cap, sizeof(int));      // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(f.Handle, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
        }
        if (f.IsHandleCreated) Apply();
        f.HandleCreated += (_, _) => Apply();
    }

    /// <summary>
    /// At start: every menu (right-click, the ▾ buttons, submenus) drawn dark, and every window's title bar dark, also
    /// windows made elsewhere (MHO Package Modifier's): a new window gets it as soon as the app is idle.
    /// </summary>
    public static void UseDarkTheme()
    {
        // Filter / search boxes' clear button (×) uses the app's own dark tooltips.
        MhoPackageModifier.Gui.SearchBox.Tip = (c, t) => Tip(c, t);
        Application.AddMessageFilter(new DisabledTips());
        ToolStripManager.Renderer = new DarkMenuRenderer();
        Application.Idle += (_, _) => { foreach (Form f in Application.OpenForms) DarkFrame(f); };
    }

    /// <summary>Menus in the app's colours: dark background, light text, the hover row highlighted, accent check marks.</summary>
    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Menu = Color.FromArgb(38, 38, 44), Hover = Color.FromArgb(58, 58, 80), Border = Color.FromArgb(70, 70, 80);
            public override Color ToolStripDropDownBackground => Menu;
            public override Color ImageMarginGradientBegin => Menu;
            public override Color ImageMarginGradientMiddle => Menu;
            public override Color ImageMarginGradientEnd => Menu;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientMiddle => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color MenuStripGradientBegin => Menu;
            public override Color MenuStripGradientEnd => Menu;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Menu;
            public override Color CheckBackground => Menu;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
            public override Color ButtonSelectedBorder => Hover;
            public override Color ToolStripBorder => Border;
            public override Color ToolStripGradientBegin => Menu;
            public override Color ToolStripGradientMiddle => Menu;
            public override Color ToolStripGradientEnd => Menu;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // An item can carry its own colour in Tag (Move to Another Costume: the hero's default costume in green).
            e.TextColor = e.Item.Tag is Color own ? (e.Item.Enabled ? own : Color.FromArgb(150, own)) : e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? DisabledText : Subtle;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float s = Math.Max(1.6f, r.Height / 9f);
            using var pen = new Pen(Accent, s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, [new PointF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.76f), new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.26f)]);
        }
    }

    /// <summary>A toggle button's state: lit (accent) when on, flat when off (Loop, Spec / Reflect / Glow).</summary>
    public static void Lit(Button b, bool on)
    {
        b.Tag = on ? "accent" : "flat";
        b.BackColor = on ? Accent : Bar; b.ForeColor = on ? Color.White : Text;
        b.FlatAppearance.BorderColor = on ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = on ? AccentHover : CardHover;
        b.Invalidate();
    }

    static Button Style(Button b, Action onClick, bool accent)
    {
        Rounded(b);
        b.Click += (_, _) => onClick();
        b.Tag = accent ? "accent" : "flat";
        b.FlatStyle = FlatStyle.Flat;
        b.Font = Regular(9.5f);
        b.Margin = new Padding(4, 2, 4, 2);
        return b;
    }

    /// <summary>
    /// A table like MHModManager's: tall rows with a thin line between them, quiet headers, no grid lines or row headers.
    /// A width of 0 fills the remaining space; <paramref name="thumbs"/> adds an image column first. Widths are 96-dpi units.
    /// </summary>
    public static DataGridView Grid(float scale, bool thumbs, params (string Title, int Width)[] cols)
    {
        var g = new GradientGrid
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToOrderColumns = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, Font = Regular(9.5f), Dock = DockStyle.Fill,
        };
        g.RowTemplate.Height = (int)((thumbs ? 52 : 30) * scale);
        g.ColumnHeadersHeight = (int)(30 * scale);
        if (thumbs) g.Columns.Add(new DataGridViewImageColumn { Name = "thumb", HeaderText = "", Width = (int)(56 * scale), ImageLayout = DataGridViewImageCellLayout.Zoom, DefaultCellStyle = { NullValue = null, Padding = new Padding((int)(4 * scale)) } });
        foreach (var (t, w) in cols)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = t, Name = t, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (w == 0) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = (int)(w * scale);
            g.Columns.Add(c);
        }
        StyleGrid(g);
        g.DataBindingComplete += (_, _) => g.ClearSelection();
        g.VisibleChanged += (_, _) => { if (g.Visible && g.Tag is not "keepselection") g.ClearSelection(); };   // no row highlighted until clicked
        return g;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Back;
        g.GridColor = Line;
        g.DefaultCellStyle.BackColor = Back;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = CardSelected;
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.ColumnHeadersDefaultCellStyle.BackColor = Bar;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Subtle;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Bar;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.ColumnHeadersDefaultCellStyle.Font = Regular(9f);
        foreach (DataGridViewColumn c in g.Columns)
            if (!c.ReadOnly && c is DataGridViewTextBoxColumn) { c.DefaultCellStyle.BackColor = Color.FromArgb(40, 40, 46); }   // editable cells stand out
        if (styledGrids.TryGetValue(g, out _)) return;
        styledGrids.Add(g, new object());
        // Cell tooltips in the app's dark style (Kurt: the table's own were the white system box): a cell's own tip text, or
        // its whole text when the cell cuts it off. The table's built-in tooltip is off.
        g.ShowCellToolTips = false;
        string? cellTip = null;
        var cellTips = NewTips(() => cellTip);
        var cellTimer = new System.Windows.Forms.Timer { Interval = 450 };
        cellTimer.Tick += (_, _) =>
        {
            cellTimer.Stop();
            if (cellTip == null || g.IsDisposed || !g.IsHandleCreated) return;
            var p = g.PointToClient(Cursor.Position);
            float s = g.DeviceDpi / 96f;
            cellTips.Show(cellTip, g, p.X + (int)(14 * s), p.Y + (int)(20 * s), 20000);
        };
        g.CellMouseEnter += (_, e) =>
        {
            cellTimer.Stop(); cellTips.Hide(g); cellTip = null;
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= g.Rows.Count) return;
            var cell = g.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string? t = !string.IsNullOrEmpty(cell.ToolTipText) ? cell.ToolTipText : null;
            if (t == null && cell.FormattedValue is string v && v.Length > 0)
            {
                var font = cell.InheritedStyle.Font ?? g.Font;
                var pad = cell.InheritedStyle.Padding;
                int room = g.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false).Width - pad.Horizontal - 4;
                if (TextRenderer.MeasureText(v, font).Width > room) t = v;   // cut off: show it whole
            }
            cellTip = t;
            if (t != null) cellTimer.Start();
        };
        g.CellMouseLeave += (_, _) => { cellTimer.Stop(); cellTip = null; cellTips.Hide(g); };
        g.Disposed += (_, _) => { cellTimer.Dispose(); cellTips.Dispose(); };
        // Check boxes in cells drawn like the app's own (Modern.Toggle), not the system's.
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || g.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn || e.Graphics == null) return;
            e.PaintBackground(e.CellBounds, true);
            float s = g.DeviceDpi / 96f, box = 14 * s;
            var r = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - box) / 2f, e.CellBounds.Y + (e.CellBounds.Height - box) / 2f, box, box);
            bool on = e.Value is true || e.Value is CheckState.Checked;
            bool ro = g.ReadOnly || g.Columns[e.ColumnIndex].ReadOnly || g.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly;
            var gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Modern.Round(r, 3 * s))
            {
                using (var f = new SolidBrush(on ? (ro ? Color.FromArgb(90, Accent) : Accent) : Modern.FieldBack)) gr.FillPath(f, path);
                using var pen = new Pen(on ? Accent : ro ? Modern.FieldBorder : Color.FromArgb(110, 110, 124), 1.2f * s); gr.DrawPath(pen, path);
            }
            if (on)
            {
                using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                gr.DrawLines(pen, [new PointF(r.X + box * 0.22f, r.Y + box * 0.52f), new PointF(r.X + box * 0.43f, r.Y + box * 0.73f), new PointF(r.X + box * 0.8f, r.Y + box * 0.3f)]);
            }
            gr.SmoothingMode = SmoothingMode.None;
            e.Handled = true;
        };
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, object> styledGrids = [];

    /// <summary>
    /// After MPM's Theme.Apply: the manager's colours on top (dark panels, subtle labels, grids, card lists, accent / flat
    /// buttons, flat tabs). <paramref name="bars"/> get the bar colour (top / bottom strips).
    /// </summary>
    public static void Restyle(Control root, params Control[] bars)
    {
        void Walk(Control c)
        {
            if (c is Label { Tag: "subtle" } l) { l.ForeColor = Subtle; }
            else if (c is DataGridView dg) StyleGrid(dg);
            else if (c is ListBox lb) { lb.BackColor = Back; lb.BorderStyle = BorderStyle.None; }
            else if (c is TextBox { ReadOnly: false } tb) { tb.BackColor = Color.FromArgb(40, 40, 46); tb.ForeColor = Text; }
            else if (c is Panel { Tag: "card" } card) card.BackColor = Card;
            else if (c is Panel or TableLayoutPanel or FlowLayoutPanel or SplitterPanel or UserControl or TabPage) c.BackColor = c.Parent is Panel { Tag: "card" } ? Card : Color.Transparent;
            else if (c is GradientSplit gs) gs.Invalidate();
            if (c is Label lab) lab.BackColor = c.Parent is Panel { Tag: "card" } ? Card : Color.Transparent;
            foreach (Control k in c.Controls) Walk(k);
        }
        if (root is not Form) root.BackColor = Color.Transparent;
        Walk(root);
        foreach (var b in bars) { b.BackColor = BarOverlay; foreach (Control c in b.Controls) if (c is not Button and not TextBox) { c.BackColor = Color.Transparent; foreach (Control k in c.Controls) if (k is not Button and not TextBox) k.BackColor = Color.Transparent; } }
        RestyleButtons(root);
        Icons.Apply(root);   // (Kurt, 2026-10-04: almost every button an icon with a titled tooltip)
        Modern.Modernize(root);
        foreach (var t in All<FlatTabs>(root)) t.Select(Math.Max(0, t.SelectedIndex));
    }

    /// <summary>A dialog's size in 96-dpi units, scaled for the display and kept within 90% of its working area; centred on its owner.</summary>
    public static void FitToScreen(Form f, int width, int height)
    {
        float s = f.DeviceDpi / 96f;
        var area = Screen.FromControl(f.Owner ?? f).WorkingArea;
        f.Size = new Size(Math.Min((int)(width * s), (int)(area.Width * 0.9)), Math.Min((int)(height * s), (int)(area.Height * 0.9)));
        var o = f.Owner?.Bounds ?? area;
        f.Location = new Point(o.X + (o.Width - f.Width) / 2, o.Y + (o.Height - f.Height) / 2);
    }

    public static IEnumerable<T> All<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) yield return t;
            foreach (var k in All<T>(c)) yield return k;
        }
    }

    /// <summary>A dark rounded-looking panel with a subtle caption on top (e.g. an image preview).</summary>
    public static Panel CardPanel(string caption, Control content, Control? footer = null)
    {
        var p = new Panel { Dock = DockStyle.Fill, Tag = "card", Padding = new Padding(8), Margin = new Padding(4) };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        if (footer != null) { footer.Dock = DockStyle.Bottom; p.Controls.Add(footer); }
        p.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, AutoSize = true, Tag = "subtle", Font = Bold(8.5f), Padding = new Padding(0, 0, 0, 4) });
        return p;
    }

    // Buttons drawn with rounded corners (Kurt: every button), and their hover / pressed state.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, int[]> rounded = [];

    /// <summary>
    /// Draws a button with rounded corners (antialiased): the parent's background behind the corners, the fill for its
    /// state (normal, hover, pressed; the FlatAppearance colours), a 1 px border, and the text (grey when disabled).
    /// Paint handlers added later (e.g. AddEndBar's bar) draw on top.
    /// </summary>
    /// <summary>Buttons drawn with an icon instead of their text (the painter gets the button's area and text colour).</summary>
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, Action<Graphics, Rectangle, Color>> IconPainters = new();

    public static void Rounded(Button b)
    {
        if (rounded.TryGetValue(b, out _)) return;
        var st = new int[2];   // [0] hover, [1] pressed
        rounded.Add(b, st);
        b.MouseEnter += (_, _) => { st[0] = 1; b.Invalidate(); };
        b.MouseLeave += (_, _) => { st[0] = 0; st[1] = 0; b.Invalidate(); };
        b.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { st[1] = 1; b.Invalidate(); } };
        b.MouseUp += (_, _) => { st[1] = 0; b.Invalidate(); };
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var r = b.ClientRectangle;
            if (r.Width < 4 || r.Height < 4) return;   // nothing to draw (a button laid out at zero size crashed AddArc)
            // Behind the corners: what the parent shows there.
            var parentColor = b.Parent?.BackColor ?? Back;
            if (parentColor.A < 255) PaintGradient(g, b, r);
            if (parentColor.A > 0) { using var pb = new SolidBrush(parentColor); g.FillRectangle(pb, r); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = b.DeviceDpi / 96f;
            var box = new RectangleF(0.5f, 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            var fillColor = !b.Enabled ? b.BackColor : st[1] == 1 ? b.FlatAppearance.MouseDownBackColor : st[0] == 1 ? b.FlatAppearance.MouseOverBackColor : b.BackColor;
            if (fillColor.IsEmpty || fillColor.A == 0) fillColor = b.BackColor;
            using (var path = RoundF(box, 5 * s))
            {
                using (var f = new SolidBrush(fillColor)) g.FillPath(f, path);
                var border = b.FlatAppearance.BorderColor.IsEmpty ? Line : b.FlatAppearance.BorderColor;
                if (b.FlatAppearance.BorderSize > 0) { using var pen = new Pen(border, 1f); g.DrawPath(pen, path); }
            }
            if (IconPainters.TryGetValue(b, out var icon)) { icon(g, r, b.Enabled ? b.ForeColor : DisabledText); return; }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, b.Text, b.Font, r, b.Enabled ? b.ForeColor : DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }

    static GraphicsPath RoundF(RectangleF r, float radius)
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

    /// <summary>Re-applies accent / flat colours after MPM's theme (which colours every button the same), and rounds
    /// every button's corners (plain ones from other windows become flat buttons in the app's style).</summary>
    public static void RestyleButtons(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button pb && pb.Tag is not ("accent" or "flat") && pb.Tag == null)
            {
                pb.Tag = "flat"; pb.FlatStyle = FlatStyle.Flat;
                Rounded(pb);
            }
            if (c is Button d && d.Tag is "flat" && d.Text.TrimEnd().EndsWith('▾'))
            {
                // A button that opens a menu looks like the app's drop-downs (the header's Costume ▾): accent-tinted pill.
                static Color Mix(int a) => Color.FromArgb((Accent.R * a + Bar.R * (255 - a)) / 255, (Accent.G * a + Bar.G * (255 - a)) / 255, (Accent.B * a + Bar.B * (255 - a)) / 255);
                d.UseVisualStyleBackColor = false;
                d.BackColor = Mix(40); d.ForeColor = Text;
                d.FlatAppearance.BorderColor = Accent;
                d.FlatAppearance.MouseOverBackColor = Mix(62);
                d.FlatAppearance.MouseDownBackColor = Mix(80);
            }
            else if (c is Button b && b.Tag is "accent" or "flat")
            {
                bool accent = (string)b.Tag == "accent";
                b.UseVisualStyleBackColor = false;
                b.BackColor = accent ? Accent : Bar;
                b.ForeColor = accent ? Color.White : Text;
                b.FlatAppearance.BorderColor = accent ? Accent : Line;
                b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : CardHover;
                b.FlatAppearance.MouseDownBackColor = accent ? AccentHover : CardSelected;
            }
            RestyleButtons(c);
        }
    }
}
