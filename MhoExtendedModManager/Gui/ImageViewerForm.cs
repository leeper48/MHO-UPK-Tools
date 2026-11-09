using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// A picture looked at closely (Kurt: click an image for 1:1, full screen, zoom in and out): the editor's Original /
/// Replacement previews and the icon creator's snapshot. Fit or 1:1, zoom by the wheel or + / − (around the mouse),
/// sharp pixels from 2×, drag to pan, F11 or the button for full screen, a checkerboard where the image is see-through.
/// </summary>
sealed class ImageViewerForm : Form
{
    readonly Image image;
    /// <summary>What's drawn: the image, or one view of a packed map's channels (RGB without alpha, one channel as gray).</summary>
    Image shown;
    readonly List<(Button Button, int Mode)> channelButtons = [];
    FlowLayoutPanel? viewsBar;   // (not in Tag: Ui.Lit keeps its style there)
    readonly Canvas canvas;
    readonly Label info = new() { AutoSize = true, Tag = "subtle", Anchor = AnchorStyles.Left };
    float zoom = 1;
    PointF offset;              // image origin inside the canvas
    Point? dragFrom;
    bool fit = true;
    FormWindowState before; FormBorderStyle beforeBorder;
    readonly Button fullBtn;

    sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }

    readonly string exportName;

    /// <param name="exportName">The file name Export suggests (without extension).</param>
    /// <summary>Edit in Image Editor was clicked (the viewer closed for it): the caller opens the map in the editor.</summary>
    public bool EditRequested { get; private set; }

    /// <param name="editIn">The image editor's name: an "Edit in …" button that closes the viewer and asks the caller to open the
    /// picture there (<see cref="EditRequested"/>); null for none.</param>
    /// <param name="channels">A packed map's channel names (R, G, B, A; Kurt, 2026-11-08: a spec map's skin mask didn't show,
    /// its reflectivity alpha being 0 there drew it see-through): buttons to show RGB (alpha ignored, the start), RGBA, or
    /// one channel as gray. Null: the picture as it is.</param>
    public ImageViewerForm(Image source, string title, string exportName = "image", string? editIn = null, string[]? channels = null)
    {
        // (raw pixels: a GDI+ copy drops the color under alpha 0, which on a packed spec map is real data: skin, cloth)
        image = source is Bitmap sb ? RawCopy(sb) : new Bitmap(source);
        shown = image;
        this.exportName = exportName;
        Text = title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        Font = Ui.Regular(9.5f);
        canvas = new Canvas { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 18) };
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(10, 6, 10, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(info, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        fullBtn = Ui.FlatButton("Full Screen", ToggleFull, tip: "Fill the screen with the picture (F11; Esc to leave).");
        buttons.Controls.AddRange([
            Ui.FlatButton("Fit", () => { fit = true; Layout2(); }, tip: "Fit the whole picture in the window (0)."),
            Ui.FlatButton("1:1", () => ZoomTo(1, null), tip: "Show the picture at its real size, one image pixel per screen pixel (1)."),
            Ui.FlatButton("−", () => Step(-1, null), tip: "Zoom out by 25% (− or the mouse wheel)."),
            Ui.FlatButton("+", () => Step(1, null), tip: "Zoom in by 25% (+ or the mouse wheel)."),
            fullBtn,
            .. (editIn != null ? new[] { Ui.FlatButton("Edit in " + editIn, () => { EditRequested = true; Close(); }, tip: $"Close this and open the map in {editIn}; each save there comes back into the Model tab (E).") } : []),
            Ui.FlatButton("Export", Export, tip: "Save the picture as a PNG at its real size (what's shown here, whatever the zoom): e.g. a snapshot, or a replacement .DDS decoded, to compare them (Ctrl+S)."),
            Ui.FlatButton("Close", Close, tip: "Close the viewer (Esc)."),
        ]);
        bar.Controls.Add(buttons, 1, 0);
        if (channels is { Length: 4 })
        {
            var views = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 0) };
            void Add(string label, int mode, string tip)
            {
                var b = Ui.FlatButton(label, () => ShowChannel(mode), tip: tip);
                channelButtons.Add((b, mode)); views.Controls.Add(b);
            }
            Add("RGB", -1, "The map's red, green and blue as colors, its alpha left out (it's a channel of the map here, not transparency).");
            Add("RGBA", -2, "The map as an image editor shows it: its alpha (" + channels[3] + ") as see-through over a checkerboard.");
            for (int c = 0; c < 4; c++)
                if (channels[c] is { Length: > 0 } n && n != "—") Add($"{"RGBA"[c]}: {Ui.TitleCase(n)}", c, $"The {"RGBA"[c]} channel ({n}) alone, as gray: white = most, black = none.");
            viewsBar = views;
        }
        Controls.Add(canvas); Controls.Add(bar);
        if (viewsBar != null) Controls.Add(viewsBar);   // (after the canvas: a docked Fill must come first)
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1000, 780);

        canvas.Paint += (_, e) => PaintImage(e.Graphics);
        canvas.Resize += (_, _) => { if (fit) Layout2(); };
        canvas.MouseWheel += (_, e) => Step(e.Delta > 0 ? 1 : -1, e.Location);
        canvas.MouseDown += (_, e) => { dragFrom = e.Location; canvas.Cursor = Cursors.SizeAll; };
        canvas.MouseUp += (_, _) => { dragFrom = null; canvas.Cursor = Cursors.Default; };
        canvas.MouseMove += (_, e) =>
        {
            if (dragFrom is not Point from) return;
            offset = new PointF(offset.X + e.X - from.X, offset.Y + e.Y - from.Y); dragFrom = e.Location; fit = false;
            canvas.Invalidate();
        };
        canvas.DoubleClick += (_, _) => { if (fit) ZoomTo(1, null); else { fit = true; Layout2(); } };
        KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Escape: if (FormBorderStyle == FormBorderStyle.None) ToggleFull(); else Close(); break;
                case Keys.F11: ToggleFull(); break;
                case Keys.E when !e.Control && editIn != null: EditRequested = true; Close(); break;
                case Keys.S when e.Control: Export(); break;
                case Keys.Oemplus: case Keys.Add: Step(1, null); break;
                case Keys.OemMinus: case Keys.Subtract: Step(-1, null); break;
                case Keys.D1: case Keys.NumPad1: ZoomTo(1, null); break;
                case Keys.D0: case Keys.NumPad0: fit = true; Layout2(); break;
                default: return;
            }
            e.Handled = true;
        };
        Shown += (_, _) => Layout2();
        if (channels is { Length: 4 }) ShowChannel(-1);
    }

    /// <summary>Tests: the channel buttons.</summary>
    internal IEnumerable<Button> ChannelButtonsForTest => channelButtons.Select(x => x.Button);

    /// <summary>-2 the image as it is, -1 RGB opaque, 0-3 one channel (R G B A) as gray.</summary>
    void ShowChannel(int mode)
    {
        if (!ReferenceEquals(shown, image)) shown.Dispose();
        if (mode == -2) shown = image;
        else
        {
            var bmp = RawCopy((Bitmap)image);
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new int[bmp.Width * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            int shift = mode switch { 0 => 16, 1 => 8, 2 => 0, _ => 24 };
            for (int i = 0; i < px.Length; i++)
            {
                if (mode == -1) { px[i] |= unchecked((int)0xFF000000); continue; }
                int v = (int)(((uint)px[i] >> shift) & 0xFF);
                px[i] = unchecked((int)0xFF000000) | (v << 16) | (v << 8) | v;
            }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            shown = bmp;
        }
        foreach (var (b, m) in channelButtons) Ui.Lit(b, m == mode);
        canvas.Invalidate();
    }

    /// <summary>Fit: the whole picture, centred (never enlarged past 8×).</summary>
    void Layout2()
    {
        if (!fit) { canvas.Invalidate(); return; }
        var c = canvas.ClientSize;
        if (c.Width < 10 || c.Height < 10) return;
        zoom = Math.Min(8f, Math.Min((c.Width - 20) / (float)image.Width, (c.Height - 20) / (float)image.Height));
        offset = new PointF((c.Width - image.Width * zoom) / 2, (c.Height - image.Height * zoom) / 2);
        UpdateInfo();
        canvas.Invalidate();
    }

    /// <summary>One zoom step of 25 percentage points (Kurt): 25 %, 50 %, 75 %, 100 %, 125 % …; from a fitted size
    /// that isn't on the grid, the next mark in that direction.</summary>
    void Step(int dir, Point? at)
    {
        const float q = 0.25f;
        float next = dir > 0 ? MathF.Floor(zoom / q + 1e-3f) * q + q : MathF.Ceiling(zoom / q - 1e-3f) * q - q;
        ZoomTo(Math.Max(q, next), at);
    }

    /// <summary>Tests: a zoom level (centred).</summary>
    internal void TestZoom(float z) { fit = true; ZoomTo(z, null); }

    /// <summary>Zoom around a point (the mouse, else the canvas centre), keeping what's under it in place.</summary>
    void ZoomTo(float z, Point? at)
    {
        z = Math.Clamp(z, 0.25f, 64f);
        var p = at ?? new Point(canvas.ClientSize.Width / 2, canvas.ClientSize.Height / 2);
        float ix = (p.X - offset.X) / zoom, iy = (p.Y - offset.Y) / zoom;   // the image point under p
        if (at == null && fit) { ix = image.Width / 2f; iy = image.Height / 2f; }
        zoom = z; fit = false;
        offset = new PointF(p.X - ix * zoom, p.Y - iy * zoom);
        UpdateInfo();
        canvas.Invalidate();
    }

    void UpdateInfo() => info.Text = $"{image.Width}×{image.Height}  ·  {zoom * 100:0} %{(Math.Abs(zoom - 1) < 0.001f ? " (1:1)" : fit ? " (Fit)" : "")}  ·  wheel: zoom · drag: pan · double-click: 1:1 / fit";

    void ToggleFull()
    {
        if (FormBorderStyle != FormBorderStyle.None)
        {
            before = WindowState; beforeBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds;
            fullBtn.Text = "Leave Full Screen";
        }
        else
        {
            FormBorderStyle = beforeBorder; WindowState = before;
            Ui.FitToScreen(this, 1000, 780); CenterToScreen();
            fullBtn.Text = "Full Screen";
        }
        if (fit) Layout2(); else canvas.Invalidate();
    }

    void PaintImage(Graphics g)
    {
        var r = new RectangleF(offset.X, offset.Y, image.Width * zoom, image.Height * zoom);
        // Checkerboard under see-through parts (fixed 10 px squares).
        var vis = RectangleF.Intersect(r, canvas.ClientRectangle);
        if (vis.Width > 0 && vis.Height > 0)
        {
            using var a = new SolidBrush(Color.FromArgb(52, 52, 58)); using var b = new SolidBrush(Color.FromArgb(38, 38, 44));
            g.SetClip(vis);
            for (float y = r.Y; y < r.Bottom; y += 10)
            {
                if (y + 10 < vis.Top || y > vis.Bottom) continue;
                for (float x = r.X; x < r.Right; x += 10)
                {
                    if (x + 10 < vis.Left || x > vis.Right) continue;
                    g.FillRectangle((((int)((x - r.X) / 10) + (int)((y - r.Y) / 10)) % 2 == 0) ? a : b, x, y, 10, 10);
                }
            }
            g.ResetClip();
        }
        // Sharp pixels once enlarged (to judge a 40×40 icon); smooth when shrunk.
        g.InterpolationMode = zoom >= 2 ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(shown, r);
    }

    /// <summary>The picture as a PNG, pixel for pixel (Kurt: compare the 3D creator's PNG with the .DDS made from it).</summary>
    void Export()
    {
        using var d = new SaveFileDialog { Title = "Export Picture", Filter = "PNG image (*.png)|*.png", FileName = ModInstaller.Sanitise(exportName) + ".png" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { image.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png); info.Text = Ui.TitleCase($"Exported {Path.GetFileName(d.FileName)}"); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or UnauthorizedAccessException) { Dialog.Show(this, $"{Path.GetFileName(d.FileName)} can't be saved: {ex.Message}", "Export Picture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>A 32-bit ARGB copy pixel for pixel (LockBits, not drawn: drawing premultiplies and loses RGB under alpha 0).</summary>
    static Bitmap RawCopy(Bitmap src)
    {
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var copy = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var from = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var to = copy.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[src.Width * 4];
            for (int y = 0; y < src.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(from.Scan0 + y * from.Stride, row, 0, row.Length);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, to.Scan0 + y * to.Stride, row.Length);
            }
        }
        finally { src.UnlockBits(from); copy.UnlockBits(to); }
        return copy;
    }

    protected override void Dispose(bool disposing) { if (disposing) { if (!ReferenceEquals(shown, image)) shown.Dispose(); image.Dispose(); } base.Dispose(disposing); }

    /// <summary>Makes a picture box open its picture in the viewer on a click (hand cursor, tooltip).</summary>
    public static void Attach(PictureBox box, Func<(Image? Image, string Title, string ExportName)> what)
    {
        box.Cursor = Cursors.Hand;
        Ui.Tip(box, "Click to look closer: fit, 1:1, zoom, full screen and export as PNG.");
        box.Click += (_, _) =>
        {
            var (img, title, name) = what();
            if (img == null) return;
            using var v = new ImageViewerForm(img, title, name);
            v.ShowDialog(box.FindForm());
        };
    }
}
