using AnimExportCli.Animation;
using MhoExtendedModManager;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>A dark two-line list (title, subtle detail line) in the Mod Manager's look.</summary>
sealed class CharacterList : ListBox
{
    /// <summary>A row; a group header (accordion) has a chevron and its first model's thumbnail; Indent 1 = under a header.</summary>
    public sealed record Item(string Key, string Title, string Detail, bool Header = false, int Indent = 0, bool Expanded = false, string? ThumbKey = null)
    { public override string ToString() => Title; }

    /// <summary>The row's thumbnail (null while it's being made: a dark tile).</summary>
    public Func<Item, Image?>? Thumb { get; set; }

    public CharacterList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
        // Drawn by us into one buffer (the Mod Manager's mod list, 0.33.2): the native owner-draw erases, then draws each row
        // on screen, which flickered (Kurt).
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Math.Min(255, (int)(50 * DeviceDpi / 96f)); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

    // No separate erase: OnPaint covers every pixel.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { m.Result = 1; return; }   // WM_ERASEBKGND
        if (m.Msg is 0x0317 or 0x0318)   // WM_PRINT / WM_PRINTCLIENT (DrawToBitmap): the same painting, copied with GDI (it honours the DC's offset)
        {
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
        if (m.Msg == 0x0115) Invalidate();   // WM_VSCROLL: repaint the rows we draw
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    /// <summary>The visible rows and, below the last one, the window gradient, all into the double buffer.</summary>
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
            OnDrawItem(new DrawItemEventArgs(g, Font, r, i, SelectedIndex == i ? DrawItemState.Selected : DrawItemState.None));
        }
        if (bottom < ClientSize.Height) Ui.PaintGradient(g, this, new Rectangle(0, bottom, ClientSize.Width, ClientSize.Height - bottom));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not Item it) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        if (sel) { using var b = new SolidBrush(Ui.CardSelected); e.Graphics.FillRectangle(b, e.Bounds); }
        else Ui.PaintGradient(e.Graphics, this, e.Bounds);
        float s = DeviceDpi / 96f;
        int x = e.Bounds.X + (int)(8 * s) + (int)(22 * s * it.Indent);
        if (it.Header)
        {
            // chevron: pointing right when shut, down when open
            float cx = x + 5 * s, cy = e.Bounds.Y + e.Bounds.Height / 2f, a = 4.5f * s;
            var pts = it.Expanded ? new[] { new PointF(cx - a, cy - a / 2), new PointF(cx + a, cy - a / 2), new PointF(cx, cy + a / 1.2f) }
                                  : new[] { new PointF(cx - a / 2, cy - a), new PointF(cx + a / 1.2f, cy), new PointF(cx - a / 2, cy + a) };
            var mode = e.Graphics.SmoothingMode;   // restored after: left on, the next rows' fills got soft edges (grey seams)
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var chev = new SolidBrush(Ui.Subtle)) e.Graphics.FillPolygon(chev, pts);
            e.Graphics.SmoothingMode = mode;
            x += (int)(16 * s);
        }
        if (Thumb != null)
        {
            // a square at the left: the thumbnail fitted in (kept aspect), on a dark tile while it's being made
            int side = e.Bounds.Height - (int)(6 * s);
            var box = new Rectangle(x - (int)(4 * s), e.Bounds.Y + (int)(3 * s), side, side);
            using (var tile = new SolidBrush(Color.FromArgb(24, 26, 34))) e.Graphics.FillRectangle(tile, box);
            if (Thumb(it) is Image img && img.Width > 0 && img.Height > 0)
            {
                float k = Math.Min((float)box.Width / img.Width, (float)box.Height / img.Height);
                int w = (int)(img.Width * k), h = (int)(img.Height * k);
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(img, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            x = box.Right + (int)(8 * s);
        }
        var r = new Rectangle(x, e.Bounds.Y + (int)(3 * s), e.Bounds.Right - x - (int)(4 * s), e.Bounds.Height / 2);
        using var bold = Ui.Bold(9.5f);
        TextRenderer.DrawText(e.Graphics, it.Title, bold, r, Ui.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        r.Offset(0, e.Bounds.Height / 2 - (int)(3 * s));
        using var small = Ui.Regular(8.5f);
        TextRenderer.DrawText(e.Graphics, it.Detail, small, r, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
