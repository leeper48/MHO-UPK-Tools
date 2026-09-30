using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AnimExportCli.UI;

/// <summary>
/// A clear button (×) for filter and search boxes (Kurt, 2026-09-30: every filter field in every app gets one). A small
/// × drawn inside the box at the right, shown while there's text: a click clears the box and keeps the focus there; Esc
/// clears it too. The box's text stops short of the × (EM_SETMARGINS). A copy of MHO Package Modifier's Gui/SearchBox.cs (this app
/// doesn't reference it); keep the two alike.
/// </summary>
static class SearchBox
{
    /// <summary>How the × gets its tooltip (an app with its own styled tooltips sets this); null = a plain ToolTip.</summary>
    public static Action<Control, string>? Tip;
    static ToolTip? plainTips;

    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    const int EM_SETMARGINS = 0xD3, EC_RIGHTMARGIN = 2;

    /// <summary>Adds the × to <paramref name="box"/> (once; a no-op on a multi-line or read-only box).</summary>
    public static void AddClear(TextBox box)
    {
        if (box.Multiline || box.ReadOnly || box.Controls.OfType<ClearButton>().Any()) return;
        var x = new ClearButton(box);
        box.Controls.Add(x);
        const string tip = "Clear (Esc)";
        if (Tip != null) Tip(x, tip); else (plainTips ??= new ToolTip()).SetToolTip(x, tip);
        box.TextChanged += (_, _) => x.Visible = box.TextLength > 0;
        // Esc clears while there's text (claimed before a window's Cancel / Esc-to-close sees it; an empty box lets it through).
        box.PreviewKeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && box.TextLength > 0) e.IsInputKey = true; };
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && box.TextLength > 0) { box.Clear(); e.Handled = e.SuppressKeyPress = true; } };
        box.Resize += (_, _) => x.Place();
        box.HandleCreated += (_, _) => x.Place();
        box.BackColorChanged += (_, _) => x.Invalidate();
        x.Visible = box.TextLength > 0;
        x.Place();
    }

    sealed class ClearButton : Control
    {
        readonly TextBox box;
        bool hot;

        public ClearButton(TextBox box)
        {
            this.box = box;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        /// <summary>At the box's right edge, as tall as its text area, square; the box's text keeps clear of it.</summary>
        public void Place()
        {
            int h = Math.Max(8, box.ClientSize.Height);
            Bounds = new Rectangle(box.ClientSize.Width - h, 0, h, h);
            if (box.IsHandleCreated) SendMessage(box.Handle, EM_SETMARGINS, EC_RIGHTMARGIN, (IntPtr)(h << 16));
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            box.Clear();
            box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(box.BackColor)) g.FillRectangle(back, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(Width, Height), c = s / 2f, r = s * 0.2f;
            // The text colour, dimmed until the mouse is over it.
            var fore = box.ForeColor;
            var col = hot ? fore : Color.FromArgb(150, fore);
            if (hot)
            {
                using var ring = new SolidBrush(Color.FromArgb(40, fore));
                g.FillEllipse(ring, c - s * 0.36f, c - s * 0.36f, s * 0.72f, s * 0.72f);
            }
            using var pen = new Pen(col, Math.Max(1.4f, s / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, c - r, c - r, c + r, c + r);
            g.DrawLine(pen, c + r, c - r, c - r, c + r);
        }
    }
}
