using System.Drawing.Drawing2D;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The 3D previews' view controls, made one way for the Mods tab's preview (StorePreview, also the Editor's Powers tab) and
/// the Model tab's (PreviewPanel): the framing and full screen buttons with their icons, the full screen window, and the
/// Look ▾ menu (Kurt, 2026-10-12: the previews should behave the same; phase 2 of the preview merge).
/// </summary>
static class PreviewControls
{
    // --- framing and full screen buttons ----------------------------------------------------------------------------------------

    /// <summary>The Head, Bust and Full Body buttons (icons of a figure cropped as each frames it).</summary>
    public static (Button Head, Button Bust, Button Full) FramingButtons(Action<Framing.Shot> frame, float s, string? saved = null)
    {
        string more = saved == null ? "" : " " + saved;
        var head = Ui.FlatButton("Head", () => frame(Framing.Shot.HeadShoulders), "Frame the head and shoulders." + more);
        var bust = Ui.FlatButton("Bust", () => frame(Framing.Shot.Bust), "Frame head and chest." + more);
        var full = Ui.FlatButton("Full Body", () => frame(Framing.Shot.Full), "Frame the whole character." + more);
        foreach (var (b, shot) in new[] { (head, Framing.Shot.HeadShoulders), (bust, Framing.Shot.Bust), (full, Framing.Shot.Full) })
        {
            b.AutoSize = false; b.Padding = new Padding(0); b.Size = new Size((int)(34 * s), (int)(30 * s));
            Ui.IconPainters.AddOrUpdate(b, (g, r, c) => PersonIcon(g, r, c, shot));
        }
        return (head, bust, full);
    }

    /// <summary>The ⛶ button (corners out; a cross while full screen).</summary>
    public static Button FullScreenButton(Action toggle, Func<bool> isFull, float s)
    {
        var b = Ui.FlatButton("⛶", toggle, "Full screen: the preview fills the screen (F11). Esc (after pausing), F11 or this button come back.");
        b.AutoSize = false; b.Padding = new Padding(0); b.Size = new Size((int)(34 * s), (int)(30 * s));
        Ui.IconPainters.AddOrUpdate(b, (g, r, c) => FullScreenIcon(g, r, c, isFull()));
        return b;
    }

    /// <summary>
    /// A preview's full screen: the control moves into a borderless window on its monitor and back. Esc pauses first, then
    /// comes back; F11 comes back; P plays or pauses; Alt+F4 comes back instead of closing.
    /// </summary>
    public sealed class FullScreen
    {
        Form? form;
        Control? homeParent;
        int homeIndex;
        public bool IsFull => form != null;
        /// <summary>Closes the full screen window (the preview is being disposed).</summary>
        public void Dispose() { form?.Dispose(); form = null; }

        /// <summary>Into or out of full screen. <paramref name="pause"/>: pauses playback, true if it was playing.</summary>
        public void Toggle(Control c, string title, Func<bool> pause, Action togglePlay, Control? focus, Padding padding, Action? changed = null)
        {
            if (form != null)
            {
                var f = form;
                form = null;
                if (homeParent != null && !homeParent.IsDisposed)
                {
                    c.Parent = homeParent;
                    homeParent.Controls.SetChildIndex(c, homeIndex);
                }
                f.Close(); f.Dispose();
                changed?.Invoke();
                c.FindForm()?.Activate();
                return;
            }
            if (c.Parent == null) return;
            var owner = c.FindForm();
            homeParent = c.Parent;
            homeIndex = homeParent.Controls.GetChildIndex(c);
            var nf = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Screen.FromControl(c).Bounds,
                ShowInTaskbar = false, Text = title, BackColor = Ui.GradientTop, KeyPreview = true, Padding = padding,
            };
            nf.KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Escape or Keys.F11) { e.Handled = true; if (e.KeyCode == Keys.F11 || !pause()) Toggle(c, title, pause, togglePlay, focus, padding, changed); }
                else if (e.KeyData == Keys.P) { e.Handled = true; togglePlay(); }
            };
            nf.FormClosing += (_, e) => { if (form == nf && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Toggle(c, title, pause, togglePlay, focus, padding, changed); } };
            form = nf;
            c.Parent = nf;
            nf.Show(owner);
            focus?.Focus();
            changed?.Invoke();
        }
    }

    // --- Look ▾ -----------------------------------------------------------------------------------------------------------------

    /// <summary>A Look ▾ toggle: its text, tooltip, state and what clicking it does.</summary>
    public sealed record LookItem(string Text, string Tip, Func<bool> Get, Action Toggle);

    /// <summary>The look toggles both previews have, in their order; each preview passes where it keeps them and what a
    /// change reloads (props, effects).</summary>
    public static List<LookItem> StandardLook(Func<bool> spec, Action toggleSpec, Func<bool> reflect, Action toggleReflect, Func<bool> glow, Action toggleGlow,
        Func<bool> bloom, Action toggleBloom, Func<bool> props, Action toggleProps, Func<bool> powers, Action togglePowers, Func<bool> ground, Action toggleGround) =>
    [
        new("Spec", "Specular highlights (shine) the materials set.", spec, toggleSpec),
        new("Reflect", "Reflections of the materials' own environment images (metal).", reflect, toggleReflect),
        new("Glow", "Glowing (emissive) parts.", glow, toggleGlow),
        new("Bloom", "A soft halo around the brightest parts (glow, the hottest highlights), as the game draws it.", bloom, toggleBloom),
        new("Props", "The weapons and props the game attaches to the character, held on their bones; they switch with the powers.", props, toggleProps),
        new("Power FXs", "The effects of the power an animation belongs to (lightning, shockwaves, trails …), read from the game's or the mod's power packages.", powers, togglePowers),
        new("Ground", "A ground plane at the character's feet, with a grid every 50 units: to see where feet and effects meet the floor (a little see-through; it hides what's below it).", ground, toggleGround),
    ];

    /// <summary>Look ▾: the toggles (the menu stays open while toggling, several at once) and the sliders.</summary>
    public static ContextMenuStrip LookMenu(IEnumerable<LookItem> items, IEnumerable<Control> sliders, float s, Action afterToggle)
    {
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        foreach (var x in items)
        {
            var it = new ToolStripMenuItem(x.Text) { ToolTipText = x.Tip };
            it.Click += (_, _) => { x.Toggle(); it.Checked = x.Get(); afterToggle(); };
            menu.Opening += (_, _) => it.Checked = x.Get();
            menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        foreach (var sl in sliders)
        {
            sl.Visible = true;
            menu.Items.Add(new ToolStripControlHost(sl) { AutoSize = false, Size = new Size((int)(260 * s), (int)(26 * s)), Margin = new Padding((int)(6 * s), 2, (int)(6 * s), 2) });
        }
        menu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        return menu;
    }

    // --- icons ------------------------------------------------------------------------------------------------------------------

    /// <summary>A framing button's icon: a figure cropped as the shot frames it (whole body, head and shoulders, head and chest).</summary>
    public static void PersonIcon(Graphics g, Rectangle r, Color c, Framing.Shot shot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.18f;
        var box = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        float w = box.Width, h = box.Height, cx = box.X + w / 2;
        var clip = g.Clip;
        g.SetClip(box);
        using var br = new SolidBrush(c);
        void Head(float cy, float rad) => g.FillEllipse(br, cx - rad, cy - rad, 2 * rad, 2 * rad);
        void Block(RectangleF b, float radius) { using var p = RoundRect(b, radius); g.FillPath(br, p); }
        switch (shot)
        {
            case Framing.Shot.Full:
                Head(box.Y + h * 0.12f, h * 0.12f);
                Block(new RectangleF(cx - w * 0.17f, box.Y + h * 0.27f, w * 0.34f, h * 0.36f), w * 0.08f);
                g.FillRectangle(br, cx - w * 0.15f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                g.FillRectangle(br, cx + w * 0.03f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                break;
            case Framing.Shot.HeadShoulders:
                Head(box.Y + h * 0.36f, h * 0.26f);
                Block(new RectangleF(cx - w * 0.5f, box.Y + h * 0.72f, w, h * 1.2f), w * 0.35f);
                break;
            default:   // bust
                Head(box.Y + h * 0.22f, h * 0.18f);
                Block(new RectangleF(cx - w * 0.36f, box.Y + h * 0.46f, w * 0.72f, h * 1.2f), w * 0.25f);
                break;
        }
        g.Clip = clip;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The full screen button's icon: four corners pointing out, or a cross while full screen (back).</summary>
    public static void FullScreenIcon(Graphics g, Rectangle r, Color c, bool full)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.27f, l = r.Width * 0.16f;
        var b = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        using var pen = new Pen(c, Math.Max(1.6f, r.Width / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (full) { g.DrawLine(pen, b.Left, b.Top, b.Right, b.Bottom); g.DrawLine(pen, b.Right, b.Top, b.Left, b.Bottom); return; }
        g.DrawLines(pen, new[] { new PointF(b.Left, b.Top + l), new PointF(b.Left, b.Top), new PointF(b.Left + l, b.Top) });
        g.DrawLines(pen, new[] { new PointF(b.Right - l, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Top + l) });
        g.DrawLines(pen, new[] { new PointF(b.Right, b.Bottom - l), new PointF(b.Right, b.Bottom), new PointF(b.Right - l, b.Bottom) });
        g.DrawLines(pen, new[] { new PointF(b.Left + l, b.Bottom), new PointF(b.Left, b.Bottom), new PointF(b.Left, b.Bottom - l) });
    }
}
