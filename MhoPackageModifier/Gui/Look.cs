using System.Drawing.Drawing2D;

namespace MhoPackageModifier.Gui;

/// <summary>
/// The dark look shared with MHO Extended Mod Manager (its Gui/Ui.cs): a navy-to-grey window gradient that the panels sit
/// on transparently, translucent top and bottom bars, cards with small uppercase captions, flat tabs with an accent
/// underline, a purple accent for the main actions and quiet flat buttons for the rest. Applied after Theme.Apply, on the
/// main window only; Theme and its palettes are left as they are (the Mod Manager builds on them). Light mode keeps the
/// plain Theme look.
/// </summary>
static class Look
{
    public static readonly Color Back = Color.FromArgb(30, 30, 32);
    public static readonly Color Bar = Color.FromArgb(36, 36, 40);
    public static readonly Color Card = Color.FromArgb(36, 36, 40);
    public static readonly Color CardHover = Color.FromArgb(44, 44, 50);
    public static readonly Color CardSelected = Color.FromArgb(48, 50, 86);
    public static readonly Color Line = Color.FromArgb(52, 52, 58);
    public static readonly Color Text = Color.FromArgb(232, 232, 236);
    public static readonly Color Subtle = Color.FromArgb(150, 150, 160);
    public static readonly Color Field = Color.FromArgb(40, 40, 46);
    public static readonly Color Accent = Color.FromArgb(108, 99, 255);
    public static readonly Color AccentHover = Color.FromArgb(128, 120, 255);
    public static readonly Color GradientTop = Color.FromArgb(10, 35, 74);
    public static readonly Color GradientBottom = Color.FromArgb(30, 30, 32);
    public static readonly Color BarOverlay = Color.FromArgb(90, 0, 0, 0);

    /// <summary>The Theme palette for this look (lists, grids, fields, headers and selection in the Mod Manager's colours).</summary>
    public static readonly Palette Palette = new(
        Back: Back, Panel: Card, Field: Field, Text: Text, Subtle: Subtle, Border: Line,
        Button: Bar, ButtonHover: CardHover, Header: Bar, Selection: CardSelected, SelectionText: Color.White,
        Changed: Color.FromArgb(92, 74, 24), IsDark: true);

    /// <summary>Captions of the main "do it" buttons, drawn in the accent colour.</summary>
    static readonly HashSet<string> AccentButtons = new(StringComparer.Ordinal)
    {
        "Apply to game file(s)…", "Import into game file…", "Write to game…", "Build and write to game…", "Copy into game file…",
        "Remove from game file…", "Export for Blender", "Export for baking", "Run",
    };

    public static Font Regular(float pt = 9.75f) => new("Segoe UI", pt);
    public static Font Bold(float pt = 10f) => new("Segoe UI Semibold", pt);

    /// <summary>
    /// Fills <paramref name="area"/> (in <paramref name="c"/>'s coordinates) with the window gradient, aligned to the whole
    /// form, so every control that paints it continues the same gradient seamlessly.
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
    /// After Theme.Apply with <see cref="Palette"/>: containers and labels transparent on the gradient, cards in the card
    /// colour, captions and hints subtle, accent / flat buttons, <paramref name="bars"/> as translucent strips.
    /// </summary>
    public static void Restyle(Control root, params Control[] bars)
    {
        void Walk(Control c)
        {
            bool inCard = InCard(c);
            switch (c)
            {
                case Panel { Tag: "card" } or TableLayoutPanel { Tag: "card" }:
                    c.BackColor = Card;
                    break;
                case Button { Tag: "cardtitle" } t:
                    // A card's title as a button: the card colour, left-aligned bold text, a lighter card on hover.
                    t.UseVisualStyleBackColor = false; t.FlatStyle = FlatStyle.Flat;
                    t.BackColor = Card; t.ForeColor = Text; t.TextAlign = ContentAlignment.MiddleLeft;
                    t.FlatAppearance.BorderColor = Card; t.FlatAppearance.MouseOverBackColor = CardHover; t.FlatAppearance.MouseDownBackColor = CardSelected;
                    break;
                case Button b:
                    bool accent = AccentButtons.Contains(b.Text) || b.Tag is "accent";
                    b.UseVisualStyleBackColor = false;
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = accent ? Accent : Bar;
                    b.ForeColor = accent ? Color.White : Text;
                    b.FlatAppearance.BorderColor = accent ? Accent : Line;
                    b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : CardHover;
                    b.FlatAppearance.MouseDownBackColor = accent ? AccentHover : CardSelected;
                    break;
                case Label l:
                    l.BackColor = inCard ? Card : Color.Transparent;
                    if (l.Tag is "hint" or "caption") l.ForeColor = Subtle;
                    break;
                case ListBox lb:
                    lb.BackColor = Back; lb.BorderStyle = BorderStyle.None;
                    break;
                case ListView lv:
                    lv.BackColor = Back;
                    break;
                case CheckBox or RadioButton:
                    c.BackColor = inCard ? Card : Color.Transparent;
                    break;
                case Panel or TableLayoutPanel or FlowLayoutPanel or SplitterPanel or TabPage or UserControl:
                    c.BackColor = inCard ? Card : Color.Transparent;
                    break;
            }
            foreach (Control k in c.Controls) Walk(k);
        }
        Walk(root);
        foreach (var bar in bars)
        {
            bar.BackColor = BarOverlay;
            foreach (Control c in bar.Controls)
                if (c is Label or FlowLayoutPanel or TableLayoutPanel or CheckBox) { c.BackColor = Color.Transparent; foreach (Control k in c.Controls) if (k is Label or CheckBox) k.BackColor = Color.Transparent; }
        }
    }

    static bool InCard(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
            if (p.Tag is "card") return true;
        return false;
    }
}

/// <summary>
/// Tabs as in the Mod Manager: titles on the gradient, the selected one bright with an accent underline, no boxes. A
/// TabControl underneath (pages, SelectedTab and keyboard switching as before); the strip is drawn here and clicks on it
/// are handled here.
/// </summary>
sealed class FlatTabControl : TabControl
{
    public FlatTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor, true);
    }

    float S => DeviceDpi / 96f;
    int StripHeight => (int)(40 * S);
    Font TabFont => tabFont ??= Look.Bold(11f);
    Font? tabFont;

    List<Rectangle> TabRects()
    {
        var list = new List<Rectangle>();
        int x = (int)(6 * S);
        for (int i = 0; i < TabCount; i++)
        {
            int w = TextRenderer.MeasureText(TabPages[i].Text, TabFont).Width + (int)(18 * S);
            list.Add(new Rectangle(x, 0, w, StripHeight));
            x += w;
        }
        return list;
    }

    public override Rectangle DisplayRectangle => new(0, StripHeight + 1, Width, Math.Max(0, Height - StripHeight - 1));

    static bool Modern => Theme.Current == Look.Palette;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Modern) Look.PaintGradient(e.Graphics, this, ClientRectangle);
        else { using var b = new SolidBrush(Theme.Current.Back); e.Graphics.FillRectangle(b, ClientRectangle); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var rects = TabRects();
        for (int i = 0; i < rects.Count; i++)
        {
            bool sel = i == SelectedIndex;
            TextRenderer.DrawText(e.Graphics, TabPages[i].Text, TabFont, rects[i], sel ? Theme.Current.Text : Theme.Current.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (sel)
            {
                using var b = new SolidBrush(Look.Accent);
                e.Graphics.FillRectangle(b, rects[i].X + 6 * S, rects[i].Bottom - 3 * S, rects[i].Width - 12 * S, 2.5f * S);
            }
        }
        using var pen = new Pen(Modern ? Color.FromArgb(60, 255, 255, 255) : Theme.Current.Border);
        e.Graphics.DrawLine(pen, 0, StripHeight, Width, StripHeight);
    }

    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(new Rectangle(0, 0, Width, StripHeight + 1)); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = e.Y < StripHeight && TabRects().Any(r => r.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_LBUTTONDOWN = 0x201;
        if (m.Msg == WM_LBUTTONDOWN)
        {
            var p = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
            if (p.Y < StripHeight)
            {
                int i = TabRects().FindIndex(r => r.Contains(p));
                if (i >= 0 && Enabled) SelectedIndex = i;
                return;                                                        // the native strip isn't where ours is drawn
            }
        }
        base.WndProc(ref m);
    }
}

/// <summary>A split view whose background (and splitter) continue the window gradient, with a thin line as the splitter.</summary>
sealed class GradientSplit : SplitContainer
{
    public GradientSplit() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Theme.Current != Look.Palette)
        {
            using var back = new SolidBrush(Theme.Current.Back); e.Graphics.FillRectangle(back, ClientRectangle);
            using var b = new SolidBrush(Theme.Current.Border); e.Graphics.FillRectangle(b, SplitterRectangle);
            return;
        }
        Look.PaintGradient(e.Graphics, this, ClientRectangle);
        using var pen = new Pen(Look.Line);
        var r = SplitterRectangle;
        if (Orientation == Orientation.Vertical) e.Graphics.DrawLine(pen, r.X + r.Width / 2, r.Top, r.X + r.Width / 2, r.Bottom);
        else e.Graphics.DrawLine(pen, r.Left, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
    }
}
