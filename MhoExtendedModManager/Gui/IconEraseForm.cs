using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The icon maker's eraser (Kurt, 2026-11-17: "click on the image, enlarge and allow us to erase with brush sizes that adjust
/// via [ ] keys"): the character layer (on transparency, or the icon's background color) shown big. Left button erases, right
/// button brings back what was there when the window opened; [ and ] make the brush smaller or bigger; Ctrl+Z / Ctrl+Y undo
/// and redo a stroke. The mouse wheel zooms at the cursor; the middle button (or Space + left) moves the view when zoomed in.
/// Each stroke goes to the icon maker at once (<see cref="Changed"/>); Cancel takes every stroke back.
/// </summary>
sealed class IconEraseForm : Form
{
    readonly int w, h;
    readonly byte[] px, orig;
    readonly Stack<byte[]> undo = new(), redo = new();
    readonly Color? back;
    readonly Canvas canvas = new();
    readonly Label info = new() { AutoSize = true, Tag = "subtle" };
    readonly Button undoButton, redoButton;
    Bitmap view;
    /// <summary>The brush's radius in the layer's pixels (remembered while the app runs).</summary>
    static float radius = 10;
    Point? last;
    Point mouse = new(-100, -100);
    bool restoring;
    /// <summary>The view: <see cref="zoom"/> × the fitting scale, the layer's top-left at <see cref="offset"/> (canvas pixels).</summary>
    float zoom = 1;
    PointF offset;
    Point? panFrom;
    /// <summary>Where the last stroke ended, in the layer's pixels: Shift + click draws a straight line from there (as GIMP does).</summary>
    PointF? lastEnd;
    bool space;

    /// <summary>The layer after a stroke (a new bitmap each time; the receiver keeps it).</summary>
    public event Action<Bitmap>? Changed;

    sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }

    public IconEraseForm(Bitmap layer, Color? background, string title)
    {
        w = layer.Width; h = layer.Height;
        px = ToBgra(layer); orig = (byte[])px.Clone();
        back = background;
        view = Model.ImagePixels.ToBitmap(w, h, px);
        Text = "Erase: " + title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(12);
        float s = Ui.Dpi(DeviceDpi);
        int side = (int)(640 * s);
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 3 };
        canvas.Size = new Size(side, side);
        canvas.Cursor = Cursors.Cross;
        canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        canvas.MouseDown += (_, e) =>
        {
            canvas.Focus();
            // moving the view: the middle button, or Space + left
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && space)) { panFrom = e.Location; canvas.Cursor = Cursors.SizeAll; return; }
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            Push();
            restoring = e.Button == MouseButtons.Right;
            last = e.Location;
            // Shift + click: a straight line from the end of the last stroke (Kurt, 2026-11-17: "like GIMP does")
            if ((ModifierKeys & Keys.Shift) != 0 && lastEnd is PointF from) Stroke(ToCanvas(from), e.Location);
            else Stroke(e.Location, e.Location);
        };
        canvas.MouseMove += (_, e) =>
        {
            mouse = e.Location;
            if (panFrom is Point p0) { offset.X += e.X - p0.X; offset.Y += e.Y - p0.Y; panFrom = e.Location; Clamp(); canvas.Invalidate(); return; }
            if (last is Point from && (e.Button & (MouseButtons.Left | MouseButtons.Right)) != 0) { Stroke(from, e.Location); last = e.Location; }
            else canvas.Invalidate();
        };
        canvas.MouseUp += (_, _) =>
        {
            if (panFrom != null) { panFrom = null; canvas.Cursor = space ? Cursors.SizeAll : Cursors.Cross; return; }
            if (last is Point end) { lastEnd = ToLayer(end); last = null; Send(); }
        };
        canvas.MouseLeave += (_, _) => { mouse = new Point(-100, -100); canvas.Invalidate(); };
        canvas.MouseEnter += (_, _) => canvas.Focus();
        canvas.MouseWheel += (_, e) => ZoomAt(e.Location, e.Delta > 0 ? ((ModifierKeys & Keys.Shift) != 0 ? 1.05f : 1.25f) : 1 / ((ModifierKeys & Keys.Shift) != 0 ? 1.05f : 1.25f));
        Ui.Tip(canvas, "Left button: erase. Right button: bring back what was there. Shift + click: a straight line from where the last stroke ended. [ and ]: a smaller or bigger brush. Mouse wheel: zoom at the cursor (Shift: finer). Middle button, or Space + left: move the view. Ctrl+Z / Ctrl+Y: undo / redo a stroke.");
        t.Controls.Add(canvas, 0, 0);
        info.Margin = new Padding(0, 8, 0, 0);
        info.MaximumSize = new Size(side, 0);   // (wrapped to the picture's width: the window stays as wide as the picture)
        t.Controls.Add(info, 0, 1);
        undoButton = Ui.FlatButton("Undo", Undo, tip: "Take the last stroke back (Ctrl+Z).");
        Icons.Make(undoButton, "Undo", Icons.Undo, s);
        redoButton = Ui.FlatButton("Redo", Redo, tip: "Do the stroke again that Undo took back (Ctrl+Y).");
        Icons.Make(redoButton, "Redo", Icons.Redo, s);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            undoButton, redoButton,
            Ui.FlatButton("Smaller [", () => SetRadius(radius / 1.25f), tip: "A smaller brush ([ key)."),
            Ui.FlatButton("Bigger ]", () => SetRadius(radius * 1.25f), tip: "A bigger brush (] key)."),
            Ui.FlatButton("Fit", Fit, tip: "The whole picture in the window again (0 key)."),
            Ui.FlatButton("Bring All Back", () => { Push(); Array.Copy(orig, px, px.Length); Refresh2(); Send(); }, tip: "Everything back as it was when this window opened (Undo takes that back too)."),
            Ui.AccentButton("Done", () => { DialogResult = DialogResult.OK; }, tip: "Keep the erasing and close (Enter)."),
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, tip: "Take every stroke back and close (Esc)."),
        ]);
        t.Controls.Add(buttons, 0, 2);
        Controls.Add(t);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.OemOpenBrackets) { SetRadius(radius / 1.25f); e.Handled = true; }
            else if (e.KeyCode == Keys.OemCloseBrackets) { SetRadius(radius * 1.25f); e.Handled = true; }
            else if (e.KeyCode == Keys.Z && e.Control && e.Shift) { Redo(); e.Handled = true; }
            else if (e.KeyCode == Keys.Z && e.Control) { Undo(); e.Handled = true; }
            else if (e.KeyCode == Keys.Y && e.Control) { Redo(); e.Handled = true; }
            else if (e.KeyCode is Keys.D0 or Keys.NumPad0) { Fit(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { if (!space) { space = true; canvas.Cursor = Cursors.SizeAll; } e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; }
        };
        // (Shift shows the straight line to come)
        KeyDown += (_, e) => { if (e.KeyCode == Keys.ShiftKey) canvas.Invalidate(); };
        KeyUp += (_, e) => { if (e.KeyCode == Keys.ShiftKey) canvas.Invalidate(); };
        KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { space = false; if (panFrom == null) canvas.Cursor = Cursors.Cross; } };
        FormClosed += (_, _) =>
        {
            // Cancel: everything as it was
            if (DialogResult != DialogResult.OK && !px.AsSpan().SequenceEqual(orig)) { Array.Copy(orig, px, px.Length); Send(); }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        ShowInfo();
    }

    static byte[] ToBgra(Bitmap b)
    {
        var argb = Model.ImagePixels.ReadArgb(b);
        var o = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++) { int c = argb[i]; o[4 * i] = (byte)c; o[4 * i + 1] = (byte)(c >> 8); o[4 * i + 2] = (byte)(c >> 16); o[4 * i + 3] = (byte)(c >> 24); }
        return o;
    }

    PointF ToLayer(Point c) => new((c.X - offset.X) / Scale, (c.Y - offset.Y) / Scale);
    Point ToCanvas(PointF l) => new((int)MathF.Round(offset.X + l.X * Scale), (int)MathF.Round(offset.Y + l.Y * Scale));

    /// <summary>Canvas pixels per layer pixel.</summary>
    float Scale => canvas.Width / (float)Math.Max(w, h) * zoom;

    void ZoomAt(Point at, float f)
    {
        float nz = Math.Clamp(zoom * f, 1, 16);
        if (nz == zoom) return;
        // the layer point under the cursor stays there
        float k0 = Scale;
        float lx = (at.X - offset.X) / k0, ly = (at.Y - offset.Y) / k0;
        zoom = nz;
        float k1 = Scale;
        offset = new PointF(at.X - lx * k1, at.Y - ly * k1);
        Clamp();
        ShowInfo();
        canvas.Invalidate();
    }

    /// <summary>The view kept over the picture (no empty space past its edges when zoomed in).</summary>
    void Clamp()
    {
        float k = Scale, vw = w * k, vh = h * k;
        offset.X = vw <= canvas.Width ? (canvas.Width - vw) / 2 : Math.Clamp(offset.X, canvas.Width - vw, 0);
        offset.Y = vh <= canvas.Height ? (canvas.Height - vh) / 2 : Math.Clamp(offset.Y, canvas.Height - vh, 0);
    }

    void Fit() { zoom = 1; offset = PointF.Empty; Clamp(); ShowInfo(); canvas.Invalidate(); }

    void SetRadius(float r) { radius = Math.Clamp(r, 0.5f, Math.Max(w, h) / 2f); ShowInfo(); canvas.Invalidate(); }

    void ShowInfo()
    {
        info.Text = $"Brush {radius * 2:0.#} px (of {w} × {h})  ·  Zoom {zoom * 100:0} %  ·  [ ] Size  ·  Wheel: Zoom  ·  Middle or Space + Drag: Move  ·  Left: Erase  ·  Right: Bring Back";
        if (undoButton != null) { undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0; }
    }

    /// <summary>Before a change: the pixels now on the undo stack; a new change ends the redo chain.</summary>
    void Push() { undo.Push((byte[])px.Clone()); redo.Clear(); ShowInfo(); }

    void Undo()
    {
        if (undo.Count == 0) return;
        redo.Push((byte[])px.Clone());
        Array.Copy(undo.Pop(), px, px.Length);
        Refresh2(); Send(); ShowInfo();
    }

    void Redo()
    {
        if (redo.Count == 0) return;
        undo.Push((byte[])px.Clone());
        Array.Copy(redo.Pop(), px, px.Length);
        Refresh2(); Send(); ShowInfo();
    }

    /// <summary>The brush along a drag (stamps a third of the radius apart), in the layer's pixels; soft by one pixel at the edge.</summary>
    void Stroke(Point a, Point b)
    {
        float k = Scale;
        float ax = (a.X - offset.X) / k, ay = (a.Y - offset.Y) / k, bx = (b.X - offset.X) / k, by = (b.Y - offset.Y) / k;
        float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int steps = Math.Max(1, (int)MathF.Ceiling(len / Math.Max(0.5f, radius / 3)));
        for (int i = 0; i <= steps; i++)
        {
            float cx = ax + (bx - ax) * i / steps, cy = ay + (by - ay) * i / steps;
            int x0 = Math.Max(0, (int)(cx - radius - 1)), x1 = Math.Min(w - 1, (int)(cx + radius + 1));
            int y0 = Math.Max(0, (int)(cy - radius - 1)), y1 = Math.Min(h - 1, (int)(cy + radius + 1));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float cover = Math.Clamp(radius + 0.5f - d, 0, 1);
                    if (cover <= 0) continue;
                    int at = (y * w + x) * 4;
                    if (restoring)
                    {
                        // back toward what was there: colors as they were, alpha up to the original's
                        byte oa = orig[at + 3];
                        if (oa <= px[at + 3]) continue;
                        px[at] = orig[at]; px[at + 1] = orig[at + 1]; px[at + 2] = orig[at + 2];
                        px[at + 3] = (byte)Math.Max(px[at + 3], (int)MathF.Round(oa * cover));
                    }
                    else px[at + 3] = (byte)MathF.Round(px[at + 3] * (1 - cover));
                }
        }
        Refresh2();
    }

    void Refresh2()
    {
        var old = view; view = Model.ImagePixels.ToBitmap(w, h, px); old.Dispose();
        canvas.Invalidate();
    }

    void Send() => Changed?.Invoke(Model.ImagePixels.ToBitmap(w, h, px));

    /// <summary>Test (--icon-maker-test): a stroke between two canvas points, then sent as a mouse-up would.</summary>
    internal void StrokeForTest(Point a, Point b, bool bringBack, float brushRadius)
    {
        radius = brushRadius; Push(); restoring = bringBack;
        Stroke(a, b); Send();
    }

    internal void UndoForTest() => Undo();
    /// <summary>Test: a click, then Shift + click elsewhere (a straight line between them), as the mouse would.</summary>
    internal void ShiftLineForTest(Point a, Point b, float brushRadius)
    {
        radius = brushRadius; Push(); restoring = false;
        Stroke(a, a); lastEnd = ToLayer(a); Send();
        Push(); Stroke(ToCanvas(lastEnd.Value), b); lastEnd = ToLayer(b); Send();
    }
    internal void RedoForTest() => Redo();
    internal void ZoomForTest(Point at, float f) => ZoomAt(at, f);
    /// <summary>Test: the canvas point over a layer pixel (the view's zoom and position).</summary>
    internal Point CanvasPointForTest(float lx, float ly) => new((int)(offset.X + lx * Scale), (int)(offset.Y + ly * Scale));
    internal float ZoomNow => zoom;

    void PaintCanvas(Graphics g)
    {
        if (offset == PointF.Empty && zoom == 1) Clamp();
        var r = new Rectangle(0, 0, canvas.Width, canvas.Height);
        if (back is Color c) { using var b = new SolidBrush(c); g.FillRectangle(b, r); }
        else
        {
            // transparency as a checkerboard
            int cell = Math.Max(8, canvas.Width / 40);
            using var dark = new SolidBrush(Color.FromArgb(40, 40, 46)); using var light = new SolidBrush(Color.FromArgb(62, 62, 70));
            for (int y = 0; y < canvas.Height; y += cell)
                for (int x = 0; x < canvas.Width; x += cell)
                    g.FillRectangle(((x / cell) + (y / cell)) % 2 == 0 ? dark : light, x, y, cell, cell);
        }
        g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
        float k = Scale;
        g.DrawImage(view, new RectangleF(offset.X, offset.Y, w * k, h * k));
        // Shift held: the straight line a click would erase (from the last stroke's end)
        if ((ModifierKeys & Keys.Shift) != 0 && lastEnd is PointF le && last == null && mouse.X > -50)
        {
            var a = ToCanvas(le);
            using var line = new Pen(Color.FromArgb(200, 255, 255, 255), Math.Max(1f, radius * 2 * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            line.Color = Color.FromArgb(70, 255, 255, 255);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(line, a, mouse);
            using var thin = new Pen(Color.FromArgb(220, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
            g.DrawLine(thin, a, mouse);
        }
        // the brush
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float rr = radius * k;
        using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f);
        using var pen2 = new Pen(Color.FromArgb(160, 0, 0, 0), 1f);
        g.DrawEllipse(pen2, mouse.X - rr - 1, mouse.Y - rr - 1, rr * 2 + 2, rr * 2 + 2);
        g.DrawEllipse(pen, mouse.X - rr, mouse.Y - rr, rr * 2, rr * 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) view.Dispose();
        base.Dispose(disposing);
    }
}
