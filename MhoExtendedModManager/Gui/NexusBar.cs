using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The Nexus strip above the mod list (Kurt: Nexus status and actions next to the list, not hidden in Settings): a status
/// icon coloured by state, what the state is ("Not Connected", "Checked 12 Min Ago · 23 Linked", "3 Updates"), and the
/// actions beside it. The icon and text are one clickable part (<see cref="StatusClicked"/>).
/// </summary>
sealed class NexusStatus : Control
{
    public enum State { NotConnected, NothingLinked, NotChecked, Stale, UpToDate, Updates, Busy }
    State state = State.NotConnected;
    string line1 = "", line2 = "";
    bool hover;
    readonly Font boldFont = Ui.Bold(9f), smallFont = Ui.Regular(8.25f), letterFont = Ui.Heavy(9.5f);
    public event Action? StatusClicked;

    public NexusStatus()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Margin = new Padding(0);
    }

    float S => MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);

    public void Set(State s, string first, string second)
    {
        if (s == state && first == line1 && second == line2) return;
        state = s; line1 = first; line2 = second;
        Invalidate();
    }

    public State Current => state;

    /// <summary>The state's colour: grey not connected, amber needs attention, green fine / updates, accent while busy.</summary>
    public static Color ColorOf(State s) => s switch
    {
        State.NotConnected => Ui.Subtle,
        State.NothingLinked or State.NotChecked or State.Stale => Color.FromArgb(242, 190, 60),
        State.Busy => Ui.Accent,
        _ => Ui.Enabled,
    };

    public override Size GetPreferredSize(Size proposedSize) => new(0, (int)(40 * S));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Ui.PaintGradient(g, this, ClientRectangle);
        var card = new Rectangle(0, (int)(2 * S), Width - 1, Height - (int)(4 * S));
        using (var path = Ui.Round(card, 4 * S))
        using (var fill = new SolidBrush(hover ? Ui.CardHover : Ui.Card))
            g.FillPath(fill, path);
        // Icon: a disc in the state's colour with an N (or ↑ when there are updates, … while busy).
        int d = (int)(24 * S);
        var disc = new Rectangle(card.X + (int)(8 * S), card.Y + (card.Height - d) / 2, d, d);
        var c = ColorOf(state);
        using (var b = new SolidBrush(c)) g.FillEllipse(b, disc);
        string glyph = state switch { State.Updates => "↑", State.Busy => "…", _ => "N" };
        TextRenderer.DrawText(g, glyph, letterFont, disc, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int x = disc.Right + (int)(8 * S);
        var t1 = new Rectangle(x, card.Y + (int)(3 * S), card.Right - x - (int)(6 * S), card.Height / 2 - (int)(2 * S));
        var t2 = new Rectangle(x, card.Y + card.Height / 2, card.Right - x - (int)(6 * S), card.Height / 2 - (int)(3 * S));
        const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, line1, boldFont, t1, state is State.Updates ? Ui.Enabled : Ui.Text, f);
        TextRenderer.DrawText(g, line2, smallFont, t2, Ui.Subtle, f);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) StatusClicked?.Invoke(); }

    /// <summary>"Just Now", "12 Min Ago", "3 Hours Ago", "2 Days Ago".</summary>
    public static string Ago(DateTime t)
    {
        var s = DateTime.Now - t;
        if (s.TotalMinutes < 1) return "Just Now";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} Min Ago";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours} Hour{((int)s.TotalHours == 1 ? "" : "s")} Ago";
        return $"{(int)s.TotalDays} Day{((int)s.TotalDays == 1 ? "" : "s")} Ago";
    }
}
