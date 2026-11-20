using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The app's image editor (Kurt, 2026-11-18: one editor everywhere, "an eye drop and brush ... the shift straight line, undo"),
/// grown from the icon maker's eraser (2026-11-17). Tools: Brush (B, the brush color at its opacity), Erase (E), Bring Back
/// (R, back toward what was there when the window opened), Clone (C: Ctrl + click sets the source, then the source follows
/// the brush), Smudge (S: drags colors along) and Eyedropper (I, or Alt / Ctrl + click with any tool). The right
/// button always brings back. Shift + click: a straight line from where the last stroke ended (as GIMP does). [ and ]: brush
/// size; Shift + [ / ]: opacity. Wheel zooms at the cursor (Shift: finer), + / − too; 0 fits, 1 is actual size; the middle
/// button or Space + drag moves the view; F11 full screen; Ctrl+Z / Ctrl+Y undo and redo a stroke. Each stroke's opacity holds
/// across the stroke (stamps don't add up).
/// Used three ways: the icon maker's layer (<see cref="Changed"/> after each stroke, Cancel takes every stroke back), a picture
/// to edit and keep (<see cref="Result"/> after Done: the Editor's icon tabs), and a PNG file as the "Built-In Editor" choice of
/// Choose External Image Editor (<see cref="OpenFile"/>: Save writes the file, so every Edit in External Image Editor takes it in as it would a
/// save in GIMP).
/// </summary>
sealed class ImageEditForm : Form, Ui.IOwnsTips
{
    public enum Tool { Brush, Erase, BringBack, Eyedropper, Clone, Smudge }

    readonly int w, h;
    readonly byte[] px, orig;
    readonly Stack<byte[]> undo = new(), redo = new();
    readonly Color? back;
    readonly Canvas canvas = new();
    readonly Label info = new() { AutoSize = true, Tag = "subtle" };
    readonly Button undoButton, redoButton;
    readonly Dictionary<Tool, Button> toolButtons = [];
    readonly ColorSwatch swatch;
    readonly LightSlider opacitySlider, softSlider;
    readonly string? file;
    Bitmap view;
    /// <summary>The brush's radius in the image's pixels, its color and opacity (remembered while the app runs).</summary>
    static float radius = 10;
    static Color color = Color.White;
    static float opacity = 1;
    /// <summary>The brush's soft edge (Kurt, 2026-11-18): 0 = hard (a one-pixel edge), 1 = soft from its middle out.</summary>
    static float softness = 0;
    Tool tool;
    /// <summary>The painting tool the eyedropper goes back to after a pick.</summary>
    Tool lastPaint;
    Point? last;
    Point mouse = new(-100, -100);
    bool restoring;
    /// <summary>This stroke: the pixels before it and each pixel's coverage so far (opacity holds across the stroke).</summary>
    byte[]? strokeBase;
    float[]? cover;
    /// <summary>The view: <see cref="zoom"/> × the fitting scale, the image's top-left at <see cref="offset"/> (canvas pixels).</summary>
    float zoom = 1;
    PointF offset;
    Point? panFrom;
    /// <summary>Where the last stroke ended, in the image's pixels: Shift + click draws a straight line from there.</summary>
    PointF? lastEnd;
    bool space;
    /// <summary>Clone (Kurt, 2026-11-18): the source point Ctrl + click set (image pixels), and the offset from the stroke to
    /// the source, fixed by the first stroke after the source is set (aligned, as GIMP's and Photoshop's default).</summary>
    PointF? cloneSource;
    PointF? cloneOffset;
    /// <summary>Smudge: the colors the brush carries (premultiplied, per brush pixel around its center), picked up as it moves.</summary>
    float[]? carried;
    int carriedSide;
    /// <summary>File mode: the pixels last written to the file.</summary>
    byte[]? saved;
    FormWindowState beforeFull; FormBorderStyle beforeBorder; bool full;
    /// <summary>A picture shown over the canvas to paint against (Kurt, 2026-11-20: the color map over a glow or spec map being
    /// painted), see-through at <see cref="overlayOpacity"/>, on or off (O). Only shown: never painted in or saved.</summary>
    readonly Bitmap? overlay;
    static bool overlayOn = true;
    static float overlayOpacity = 0.5f;
    readonly Button? overlayButton;

    /// <summary>The next editor's place (the viewer's Edit Here: the editor takes the viewer's window); used once.</summary>
    internal static (Rectangle Bounds, bool Maximized)? NextBounds;
    bool placed;

    /// <summary>The image after a stroke (a new bitmap each time; the receiver keeps it).</summary>
    public event Action<Bitmap>? Changed;

    /// <summary>The picture as edited (a new bitmap; the caller keeps it).</summary>
    public Bitmap Result => Model.ImagePixels.ToBitmap(w, h, px);

    /// <summary>True when the picture differs from the one the window opened with.</summary>
    public bool Edited => !px.AsSpan().SequenceEqual(orig);

    sealed class Canvas : Panel { public Canvas() { DoubleBuffered = true; ResizeRedraw = true; } }

    /// <param name="background">The color behind see-through pixels (the icon's background); null = a checkerboard.</param>
    /// <param name="file">File mode: the PNG that Save writes.</param>
    /// <param name="overlay">A picture shown over the canvas to paint against (the material's color map); the editor keeps a copy.</param>
    public ImageEditForm(Bitmap image, Color? background, string title, Tool start = Tool.Brush, string? file = null, Bitmap? overlay = null)
    {
        if (overlay != null) this.overlay = new Bitmap(overlay);
        w = image.Width; h = image.Height;
        px = ToBgra(image); orig = (byte[])px.Clone();
        back = background; this.file = file;
        if (file != null) saved = (byte[])px.Clone();
        view = Model.ImagePixels.ToBitmap(w, h, px);
        tool = start; lastPaint = start == Tool.Eyedropper ? Tool.Brush : start;
        Text = title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = file != null; MaximizeBox = true; ShowInTaskbar = file != null; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(12);
        float s = Ui.Dpi(DeviceDpi);
        int side = (int)(640 * s);

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // tools
        // (one line each, no wrapping: a wrapping row in an auto-sized table re-laid itself out over and over, moving the
        // buttons under their tooltips, which flickered; Kurt, 2026-11-18. The window is kept wide enough for both rows.)
        var tools = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 0, 8) };
        void AddTool(Tool k, string name, string tip, Action<Graphics, RectangleF, Pen, Brush> paint)
        {
            var b = Ui.FlatButton(name, () => SetTool(k), tip: tip);
            Icons.Make(b, name, paint, s);
            toolButtons[k] = b; tools.Controls.Add(b);
        }
        AddTool(Tool.Brush, "Brush", "Paint in the brush color at its opacity (B).", Icons.PaintBrush);
        AddTool(Tool.Erase, "Erase", "Make pixels see-through (E).", Icons.Eraser);
        AddTool(Tool.BringBack, "Bring Back", "Paint back what was there when this window opened (R). The right button does this with any tool.", Icons.BringBack);
        AddTool(Tool.Clone, "Clone", "Paint a copy of another part of the picture (C): Ctrl + click where to copy from, then paint; the source follows the brush.", Icons.CloneStamp);
        AddTool(Tool.Smudge, "Smudge", "Push colors along as you drag, like a finger in wet paint (S). Opacity sets how strongly it smears.", Icons.Smudge);
        AddTool(Tool.Eyedropper, "Eyedropper", "Click the picture to take its color as the brush color (I). Alt + click (or Ctrl + click, except with Clone) does it with any tool.", Icons.Dropper);
        swatch = new ColorSwatch { Size = new Size((int)(34 * s), (int)(30 * s)), Margin = new Padding((int)(10 * s), 3, (int)(6 * s), 3), Swatch = color };
        Ui.Tip(swatch, "The brush color. Click: a color wheel, the hex value and a screen eyedropper (drag onto any color on screen).");
        swatch.Click += (_, _) =>
        {
            var r = swatch.RectangleToScreen(swatch.ClientRectangle);
            ColorPickerPopup.Open(swatch, r, color, null, c => { color = Color.FromArgb(255, c); swatch.Swatch = color; ShowInfo(); });
        };
        tools.Controls.Add(swatch);
        opacitySlider = new LightSlider { Label = "Opacity", Min = 0.05f, Max = 1, Step = 0.05f, Mark = null, Format = v => $"{v * 100:0} %", Home = () => 1, Width = (int)(220 * s), Height = (int)(30 * s), Value = opacity, Margin = new Padding(0, 3, (int)(10 * s), 3) };
        opacitySlider.ValueChanged += () => { opacity = opacitySlider.Value; ShowInfo(); };
        Ui.Tip(opacitySlider, "How strongly the Brush, Erase and Bring Back cover in one stroke (Shift + [ and Shift + ]). A stroke doesn't build up over itself; a new stroke adds to the last.");
        tools.Controls.Add(opacitySlider);
        softSlider = new LightSlider { Label = "Softness", Min = 0, Max = 1, Step = 0.05f, Mark = null, Format = v => $"{v * 100:0} %", Home = () => 0, Width = (int)(220 * s), Height = (int)(30 * s), Value = softness, Margin = new Padding(0, 3, (int)(10 * s), 3) };
        softSlider.ValueChanged += () => { softness = softSlider.Value; ShowInfo(); canvas.Invalidate(); };
        Ui.Tip(softSlider, "How soft the brush's edge is, for every tool: 0 % is a hard edge, 100 % fades from the middle out (Ctrl + [ and Ctrl + ]). The inner dashed ring shows where the fade starts.");
        tools.Controls.Add(softSlider);
        tools.Controls.Add(Ui.FlatButton("Smaller [", () => SetRadius(radius / 1.25f), tip: "A smaller brush ([ key)."));
        tools.Controls.Add(Ui.FlatButton("Bigger ]", () => SetRadius(radius * 1.25f), tip: "A bigger brush (] key)."));
        t.Controls.Add(tools, 0, 0);

        canvas.Dock = DockStyle.Fill;
        canvas.MinimumSize = new Size((int)(320 * s), (int)(240 * s));
        canvas.Cursor = Cursors.Cross;
        canvas.Paint += (_, e) => PaintCanvas(e.Graphics);
        canvas.Resize += (_, _) => { Clamp(); canvas.Invalidate(); };
        canvas.MouseDown += (_, e) =>
        {
            canvas.Focus();
            // moving the view: the middle button, or Space + left
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && space)) { panFrom = e.Location; canvas.Cursor = Cursors.SizeAll; return; }
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
            // the eyedropper: its tool, or Alt / Ctrl + click with any
            // Clone: Ctrl + click sets where it copies from
            if (e.Button == MouseButtons.Left && tool == Tool.Clone && (ModifierKeys & Keys.Control) != 0 && (ModifierKeys & Keys.Alt) == 0) { cloneSource = ToLayer(e.Location); cloneOffset = null; ShowInfo(); canvas.Invalidate(); return; }
            if (e.Button == MouseButtons.Left && (tool == Tool.Eyedropper || PickHeld)) { Pick(e.Location); return; }
            if (e.Button == MouseButtons.Left && tool == Tool.Clone && cloneSource == null) { ShowInfo("Ctrl + Click Where to Copy From First"); return; }
            Begin(e.Button == MouseButtons.Right);
            last = e.Location;
            if ((ModifierKeys & Keys.Shift) != 0 && lastEnd is PointF from) Stroke(ToCanvas(from), e.Location);
            else Stroke(e.Location, e.Location);
        };
        canvas.MouseMove += (_, e) =>
        {
            mouse = e.Location;
            if (panFrom is Point p0) { offset.X += e.X - p0.X; offset.Y += e.Y - p0.Y; panFrom = e.Location; Clamp(); canvas.Invalidate(); return; }
            if (last is Point from && (e.Button & (MouseButtons.Left | MouseButtons.Right)) != 0) { Stroke(from, e.Location); last = e.Location; }
            else { if (tool == Tool.Eyedropper || PickHeld) ShowInfo(); canvas.Invalidate(); }
        };
        canvas.MouseUp += (_, _) =>
        {
            if (panFrom != null) { panFrom = null; canvas.Cursor = space ? Cursors.SizeAll : Cursors.Cross; return; }
            if (last is Point end) { lastEnd = ToLayer(end); last = null; End(); }
        };
        canvas.MouseLeave += (_, _) => { mouse = new Point(-100, -100); canvas.Invalidate(); };
        canvas.MouseEnter += (_, _) => canvas.Focus();
        canvas.MouseWheel += (_, e) => ZoomAt(e.Location, e.Delta > 0 ? ((ModifierKeys & Keys.Shift) != 0 ? 1.05f : 1.25f) : 1 / ((ModifierKeys & Keys.Shift) != 0 ? 1.05f : 1.25f));
        // (no tooltip on the picture: it covered what you paint, Kurt 2026-11-18; the keys are on the Keys and Mouse button)
        t.Controls.Add(canvas, 0, 1);
        info.Margin = new Padding(0, 8, 0, 0);
        // (a fixed-size line: an auto-sized one changed size with every mouse move over the picture and re-laid the window out)
        info.AutoSize = false; info.AutoEllipsis = true; info.Dock = DockStyle.Fill; info.Height = TextRenderer.MeasureText("Ag", Font).Height + 4;
        t.Controls.Add(info, 0, 2);

        var keys = Ui.FlatButton("Keys and Mouse", () => { }, tip: "Left button: the tool (B Brush, E Erase, R Bring Back, C Clone, S Smudge, I Eyedropper). Right button: bring back what was there. Clone: Ctrl + click where to copy from. Alt + click (or Ctrl + click with other tools): take a color. Shift + click: a straight line from where the last stroke ended. [ and ]: brush size; Shift + [ / ]: opacity; Ctrl + [ / ]: softness. Mouse wheel or + / −: zoom at the cursor (Shift: finer); 0: fit; 1: actual size. Middle button, or Space + left: move the view. Ctrl+Z / Ctrl+Y: undo / redo a stroke. F11: full screen.");
        Icons.Make(keys, "Keys and Mouse", Icons.Help, s);
        undoButton = Ui.FlatButton("Undo", Undo, tip: "Take the last stroke back (Ctrl+Z).");
        Icons.Make(undoButton, "Undo", Icons.Undo, s);
        redoButton = Ui.FlatButton("Redo", Redo, tip: "Do the stroke again that Undo took back (Ctrl+Y).");
        Icons.Make(redoButton, "Redo", Icons.Redo, s);
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        if (this.overlay != null)
        {
            overlayButton = Ui.FlatButton("Color Overlay", ToggleOverlay, tip: "Show the material's color map over the picture, see-through, to paint against (O). It's only shown: it isn't painted in or saved. Lit when on.");
            var overlaySlider = new LightSlider { Label = "Overlay", Min = 0.05f, Max = 1, Step = 0.05f, Mark = null, Format = v => $"{v * 100:0} %", Home = () => 0.5f, Width = (int)(200 * s), Height = (int)(30 * s), Value = overlayOpacity, Margin = new Padding(0, 3, (int)(10 * s), 3) };
            overlaySlider.ValueChanged += () => { overlayOpacity = overlaySlider.Value; if (!overlayOn) ToggleOverlay(); canvas.Invalidate(); };
            Ui.Tip(overlaySlider, "How strongly the color map shows over the picture.");
            buttons.Controls.Add(overlayButton);
            buttons.Controls.Add(overlaySlider);
            Shown += (_, _) => Ui.Lit(overlayButton, overlayOn);
        }
        buttons.Controls.AddRange([
            keys, undoButton, redoButton,
            Ui.FlatButton("Fit", Fit, tip: "The whole picture in the window again (0 key)."),
            Ui.FlatButton("1:1", ActualSize, tip: "One picture pixel per screen pixel (1 key)."),
            Ui.FlatButton("Full Screen", ToggleFull, tip: "Fill the screen with the editor; again to come back (F11)."),
            Ui.FlatButton("Bring All Back", () => { Push(); Array.Copy(orig, px, px.Length); Refresh2(); Send(); }, tip: "Everything back as it was when this window opened (Undo takes that back too)."),
            Ui.FlatButton("Export", Export, tip: file != null ? "Save a copy as a PNG somewhere else (Ctrl+Shift+S)." : "Save the picture as a PNG (Ctrl+S)."),
        ]);
        if (file != null)
            buttons.Controls.AddRange([
                Ui.AccentButton("Save", () => Save(), tip: "Write the picture to its file (Ctrl+S): the window it came from takes it in, as a save in an image editor does."),
                Ui.FlatButton("Close", Close, tip: "Close the editor (Esc); asks first when there are changes not saved."),
            ]);
        else
            buttons.Controls.AddRange([
                Ui.AccentButton("Done", () => { DialogResult = DialogResult.OK; }, tip: "Keep the changes and close (Enter)."),
                Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, tip: "Take every stroke back and close (Esc)."),
            ]);
        t.Controls.Add(buttons, 0, 3);
        Controls.Add(t);

        // sized for the picture's shape (a 640 px canvas on its long side), on the window's screen
        float aspect = w / (float)h;
        int cw = aspect >= 1 ? side : (int)(side * aspect), ch = aspect >= 1 ? (int)(side / aspect) : side;
        cw = Math.Max(cw, (int)(560 * s)); ch = Math.Max(ch, (int)(320 * s));
        ClientSize = new Size(cw + Padding.Horizontal, ch + Padding.Vertical + (int)(150 * s));
        int rowW = Math.Max(tools.PreferredSize.Width, buttons.PreferredSize.Width) + Padding.Horizontal + (Width - ClientSize.Width) + (int)(8 * s);
        MinimumSize = new Size(Math.Max((int)(560 * s), rowW), (int)(480 * s));
        if (Width < MinimumSize.Width) Width = MinimumSize.Width;

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (ActiveControl is TextBox) return;
            bool shift = e.Shift, ctrl = e.Control;
            if (e.KeyCode == Keys.OemOpenBrackets) { if (ctrl) softSlider.Value = Math.Clamp(softness - 0.1f, 0, 1); else if (shift) SetOpacity(opacity - 0.1f); else SetRadius(radius / 1.25f); e.Handled = true; }
            else if (e.KeyCode == Keys.OemCloseBrackets) { if (ctrl) softSlider.Value = Math.Clamp(softness + 0.1f, 0, 1); else if (shift) SetOpacity(opacity + 0.1f); else SetRadius(radius * 1.25f); e.Handled = true; }
            else if (e.KeyCode == Keys.Z && ctrl && shift) { Redo(); e.Handled = true; }
            else if (e.KeyCode == Keys.Z && ctrl) { Undo(); e.Handled = true; }
            else if (e.KeyCode == Keys.Y && ctrl) { Redo(); e.Handled = true; }
            else if (e.KeyCode == Keys.S && ctrl) { if (file != null && !shift) Save(); else Export(); e.Handled = true; e.SuppressKeyPress = true; }
            else if (ctrl || e.Alt) return;
            else if (e.KeyCode == Keys.B) { SetTool(Tool.Brush); e.Handled = true; }
            else if (e.KeyCode == Keys.E) { SetTool(Tool.Erase); e.Handled = true; }
            else if (e.KeyCode == Keys.R) { SetTool(Tool.BringBack); e.Handled = true; }
            else if (e.KeyCode == Keys.I) { SetTool(Tool.Eyedropper); e.Handled = true; }
            else if (e.KeyCode == Keys.C) { SetTool(Tool.Clone); e.Handled = true; }
            else if (e.KeyCode == Keys.S) { SetTool(Tool.Smudge); e.Handled = true; }
            else if (e.KeyCode is Keys.D0 or Keys.NumPad0) { Fit(); e.Handled = true; }
            else if (e.KeyCode is Keys.D1 or Keys.NumPad1) { ActualSize(); e.Handled = true; }
            else if (e.KeyCode is Keys.Oemplus or Keys.Add) { ZoomAt(ViewCenter(), 1.25f); e.Handled = true; }
            else if (e.KeyCode is Keys.OemMinus or Keys.Subtract) { ZoomAt(ViewCenter(), 1 / 1.25f); e.Handled = true; }
            else if (e.KeyCode == Keys.F11) { ToggleFull(); e.Handled = true; }
            else if (e.KeyCode == Keys.O && overlay != null) { ToggleOverlay(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { if (!space) { space = true; canvas.Cursor = Cursors.SizeAll; } e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Enter && file == null) { DialogResult = DialogResult.OK; e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { if (full) ToggleFull(); else if (file != null) Close(); else DialogResult = DialogResult.Cancel; e.Handled = true; }
        };
        // (Shift shows the straight line to come; Alt / Ctrl the color under the cursor)
        KeyDown += (_, e) => { if (e.KeyCode is Keys.ShiftKey or Keys.Menu or Keys.ControlKey) { ShowInfo(); canvas.Invalidate(); } };
        KeyUp += (_, e) => { if (e.KeyCode is Keys.ShiftKey or Keys.Menu or Keys.ControlKey) { ShowInfo(); canvas.Invalidate(); } };
        KeyUp += (_, e) => { if (e.KeyCode == Keys.Space) { space = false; if (panFrom == null) canvas.Cursor = Cursors.Cross; } };
        FormClosing += (_, e) =>
        {
            // file mode: changes not saved are asked about
            if (file == null || saved == null || px.AsSpan().SequenceEqual(saved) || e.CloseReason != CloseReason.UserClosing) return;
            var a = Dialog.Show(this, $"Save the changes to {Path.GetFileName(file)}?", "Image Editor", MessageBoxButtons.YesNoCancel);
            if (a == DialogResult.Cancel) e.Cancel = true;
            else if (a == DialogResult.Yes && !Save()) e.Cancel = true;
        };
        FormClosed += (_, _) =>
        {
            // Cancel (layer and picture modes): everything as it was
            if (file == null && DialogResult != DialogResult.OK && Edited) { Array.Copy(orig, px, px.Length); Send(); }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        SetTool(tool);
        if (NextBounds is { } nb)
        {
            NextBounds = null; placed = true;
            StartPosition = FormStartPosition.Manual; Bounds = nb.Bounds;
            if (nb.Maximized) WindowState = FormWindowState.Maximized;
        }
    }

    /// <summary>"Built-In Editor" (Choose External Image Editor): <paramref name="png"/> in the editor, beside the active window. Save writes
    /// it (the callers watch the file, as they do for GIMP). Null = opened; else why not.</summary>
    public static string? OpenFile(string png, string? overlayFile = null)
    {
        Bitmap img;
        Bitmap? over = null;
        try { using var b = Model.ImagePixels.Open(png); img = new Bitmap(b); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return ex.Message; }
        if (overlayFile != null)
            try { using var ob = Model.ImagePixels.Open(overlayFile); over = new Bitmap(ob); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { over = null; }   // (no overlay: the map still opens)
        var f = new ImageEditForm(img, null, Path.GetFileName(png), Tool.Brush, png, over);
        img.Dispose(); over?.Dispose();
        f.FormClosed += (_, _) => f.BeginInvoke(f.Dispose);
        if (Form.ActiveForm is { } owner && !owner.IsDisposed) { if (!f.placed) f.StartPosition = FormStartPosition.CenterParent; f.Show(owner); }
        else { if (!f.placed) f.StartPosition = FormStartPosition.CenterScreen; f.Show(); }
        return null;
    }

    void ToggleOverlay()
    {
        overlayOn = !overlayOn;
        if (overlayButton != null) Ui.Lit(overlayButton, overlayOn);
        canvas.Invalidate();
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
    Point ViewCenter() => mouse.X > -50 ? mouse : new Point(canvas.Width / 2, canvas.Height / 2);

    /// <summary>Canvas pixels per image pixel when fitted.</summary>
    float FitScale => Math.Max(1e-3f, Math.Min(canvas.Width / (float)w, canvas.Height / (float)h));
    /// <summary>Canvas pixels per image pixel.</summary>
    float Scale => FitScale * zoom;

    void ZoomAt(Point at, float f)
    {
        float fit = FitScale;
        float nz = Math.Clamp(zoom * f, Math.Min(1, 1 / fit), Math.Max(16, 32 / fit));
        if (nz == zoom) return;
        SetZoom(at, nz);
    }

    void SetZoom(Point at, float nz)
    {
        // the image point under the cursor stays there
        float k0 = Scale;
        float lx = (at.X - offset.X) / k0, ly = (at.Y - offset.Y) / k0;
        zoom = nz;
        float k1 = Scale;
        offset = new PointF(at.X - lx * k1, at.Y - ly * k1);
        Clamp();
        ShowInfo();
        canvas.Invalidate();
    }

    void ActualSize() => SetZoom(ViewCenter(), 1 / FitScale);

    /// <summary>The view kept over the picture (no empty space past its edges when zoomed in).</summary>
    void Clamp()
    {
        float k = Scale, vw = w * k, vh = h * k;
        offset.X = vw <= canvas.Width ? (canvas.Width - vw) / 2 : Math.Clamp(offset.X, canvas.Width - vw, 0);
        offset.Y = vh <= canvas.Height ? (canvas.Height - vh) / 2 : Math.Clamp(offset.Y, canvas.Height - vh, 0);
    }

    void Fit() { zoom = 1; offset = PointF.Empty; Clamp(); ShowInfo(); canvas.Invalidate(); }

    void ToggleFull()
    {
        if (!full) { beforeFull = WindowState; beforeBorder = FormBorderStyle; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized; full = true; }
        else { FormBorderStyle = beforeBorder; WindowState = beforeFull; full = false; }
        Fit();
    }

    void SetRadius(float r) { radius = Math.Clamp(r, 0.5f, Math.Max(w, h) / 2f); ShowInfo(); canvas.Invalidate(); }

    void SetOpacity(float o) { opacitySlider.Value = Math.Clamp(o, 0.05f, 1); opacity = opacitySlider.Value; ShowInfo(); }

    void SetTool(Tool k)
    {
        tool = k;
        if (k != Tool.Eyedropper) lastPaint = k;
        foreach (var (key, b) in toolButtons) Ui.Lit(b, key == k);
        ShowInfo(); canvas.Invalidate();
    }

    static string Name(Tool k) => k switch { Tool.Brush => "Brush", Tool.Erase => "Erase", Tool.BringBack => "Bring Back", Tool.Clone => "Clone", Tool.Smudge => "Smudge", _ => "Eyedropper" };

    /// <summary>A modifier that makes a click take a color: Alt, or Ctrl (with Clone, Ctrl sets the source instead).</summary>
    bool PickHeld => (ModifierKeys & Keys.Alt) != 0 || ((ModifierKeys & Keys.Control) != 0 && tool != Tool.Clone);

    void ShowInfo(string? note = null)
    {
        string under = note != null ? "  ·  " + note : tool == Tool.Clone && cloneSource == null ? "  ·  Ctrl + Click Where to Copy From" : "";
        if (UnderMouse() is Color c && (tool == Tool.Eyedropper || PickHeld))
            under += $"  ·  Under the Cursor #{c.R:X2}{c.G:X2}{c.B:X2}{(c.A < 255 ? $" ({c.A * 100 / 255} % Opaque)" : "")}";
        info.Text = $"{Name(tool)}  ·  Brush {radius * 2:0.#} px  ·  Opacity {opacity * 100:0} %  ·  Softness {softness * 100:0} %  ·  Color #{color.R:X2}{color.G:X2}{color.B:X2}  ·  {w} × {h}  ·  Zoom {Scale * 100:0} %{under}"
            + (file != null && saved != null && !px.AsSpan().SequenceEqual(saved) ? "  ·  Not Saved" : "");
        if (undoButton != null) { undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0; }
    }

    Color? UnderMouse()
    {
        if (mouse.X < -50) return null;
        var l = ToLayer(mouse);
        int x = (int)MathF.Floor(l.X), y = (int)MathF.Floor(l.Y);
        if (x < 0 || y < 0 || x >= w || y >= h) return null;
        int at = (y * w + x) * 4;
        return Color.FromArgb(px[at + 3], px[at + 2], px[at + 1], px[at]);
    }

    /// <summary>The eyedropper: the color under <paramref name="at"/> becomes the brush color (see-through pixels are skipped),
    /// then back to the painting tool.</summary>
    void Pick(Point at)
    {
        mouse = at;
        if (UnderMouse() is not Color c || c.A == 0) return;
        color = Color.FromArgb(255, c); swatch.Swatch = color;
        if (tool == Tool.Eyedropper) SetTool(Tool.Brush);   // a color is picked to paint with
        ShowInfo();
    }

    /// <summary>Before a change: the pixels now on the undo stack; a new change ends the redo chain.</summary>
    void Push() { undo.Push((byte[])px.Clone()); redo.Clear(); ShowInfo(); }

    /// <summary>A stroke starts: the undo step, the pixels it paints over, no coverage yet.</summary>
    void Begin(bool bringBack)
    {
        Push();
        restoring = bringBack;
        strokeBase = undo.Peek();
        cover = new float[w * h];
        carried = null;
    }

    void End() { strokeBase = null; cover = null; carried = null; Send(); ShowInfo(); }

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

    /// <summary>The brush along a drag (stamps a third of the radius apart), in the image's pixels; soft by one pixel at the
    /// edge. Each pixel takes the stroke's highest coverage × opacity, painted over what was there before the stroke.</summary>
    void Stroke(Point a, Point b)
    {
        if (strokeBase == null || cover == null) return;
        Tool k = restoring ? Tool.BringBack : tool;
        float sc = Scale;
        float ax = (a.X - offset.X) / sc, ay = (a.Y - offset.Y) / sc, bx = (b.X - offset.X) / sc, by = (b.Y - offset.Y) / sc;
        float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        // the smudge needs closer stamps (each one drags the colors a little further)
        int steps = Math.Max(1, (int)MathF.Ceiling(len / Math.Max(0.5f, radius / (k == Tool.Smudge ? 8 : 3))));
        if (k == Tool.Clone && cloneSource is PointF csrc && cloneOffset == null) cloneOffset = new PointF(csrc.X - ax, csrc.Y - ay);
        if (k == Tool.Clone && cloneOffset == null) return;
        float cr = color.R, cg = color.G, cb = color.B;
        for (int i = 0; i <= steps; i++)
        {
            float cx = ax + (bx - ax) * i / steps, cy = ay + (by - ay) * i / steps;
            if (k == Tool.Smudge) { SmudgeStamp(cx, cy); continue; }
            int x0 = Math.Max(0, (int)(cx - radius - 1)), x1 = Math.Min(w - 1, (int)(cx + radius + 1));
            int y0 = Math.Max(0, (int)(cy - radius - 1)), y1 = Math.Min(h - 1, (int)(cy + radius + 1));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float c = Cover(d);
                    int p = y * w + x;
                    if (c <= cover[p]) continue;
                    cover[p] = c;
                    float o = c * opacity;
                    int at = p * 4;
                    float ba = strokeBase[at + 3] / 255f;
                    switch (k)
                    {
                        case Tool.Clone:
                        {
                            // toward the source pixel as it was before the stroke (premultiplied, as Bring Back)
                            int sx = (int)MathF.Floor(x + 0.5f + cloneOffset!.Value.X), sy = (int)MathF.Floor(y + 0.5f + cloneOffset.Value.Y);
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) break;
                            int sat = (sy * w + sx) * 4;
                            float sa = strokeBase[sat + 3] / 255f;
                            float na = ba + (sa - ba) * o;
                            if (na <= 0) { px[at + 3] = 0; break; }
                            for (int ch = 0; ch < 3; ch++)
                                px[at + ch] = (byte)Math.Clamp(MathF.Round((strokeBase[at + ch] * ba + (strokeBase[sat + ch] * sa - strokeBase[at + ch] * ba) * o) / na), 0, 255);
                            px[at + 3] = (byte)MathF.Round(na * 255);
                            break;
                        }
                        case Tool.Erase:
                            px[at] = strokeBase[at]; px[at + 1] = strokeBase[at + 1]; px[at + 2] = strokeBase[at + 2];
                            px[at + 3] = (byte)MathF.Round(strokeBase[at + 3] * (1 - o));
                            break;
                        case Tool.Brush:
                        {
                            // the color over what was there (straight alpha)
                            float oa = o + ba * (1 - o);
                            if (oa <= 0) break;
                            px[at] = (byte)MathF.Round((cb * o + strokeBase[at] * ba * (1 - o)) / oa);
                            px[at + 1] = (byte)MathF.Round((cg * o + strokeBase[at + 1] * ba * (1 - o)) / oa);
                            px[at + 2] = (byte)MathF.Round((cr * o + strokeBase[at + 2] * ba * (1 - o)) / oa);
                            px[at + 3] = (byte)MathF.Round(oa * 255);
                            break;
                        }
                        default:
                        {
                            // toward the original (premultiplied, so a see-through pixel's color doesn't bleed in)
                            float ga = orig[at + 3] / 255f;
                            float na = ba + (ga - ba) * o;
                            if (na <= 0) { px[at + 3] = 0; break; }
                            for (int ch = 0; ch < 3; ch++)
                                px[at + ch] = (byte)Math.Clamp(MathF.Round((strokeBase[at + ch] * ba + (orig[at + ch] * ga - strokeBase[at + ch] * ba) * o) / na), 0, 255);
                            px[at + 3] = (byte)MathF.Round(na * 255);
                            break;
                        }
                    }
                }
        }
        Refresh2();
    }

    /// <summary>
    /// One smudge stamp: each brush pixel moves toward the color the brush carries (by its coverage × opacity), and the brush
    /// picks up a little of what it passes over (a quarter per stamp), so colors are dragged along the stroke and fade out.
    /// The first stamp of a stroke loads the brush with what's under it.
    /// </summary>
    void SmudgeStamp(float cx, float cy)
    {
        int r = (int)MathF.Ceiling(radius) + 1, side = 2 * r + 1;
        int ox = (int)MathF.Round(cx), oy = (int)MathF.Round(cy);
        bool load = carried == null || carriedSide != side;
        if (load) { carried = new float[side * side * 4]; carriedSide = side; }
        for (int j = -r; j <= r; j++)
            for (int i = -r; i <= r; i++)
            {
                int x = ox + i, y = oy + j;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int at = (y * w + x) * 4, bi = ((j + r) * side + (i + r)) * 4;
                float a = px[at + 3] / 255f;
                float pb = px[at] * a, pg = px[at + 1] * a, pr = px[at + 2] * a;
                if (load) { carried![bi] = pb; carried[bi + 1] = pg; carried[bi + 2] = pr; carried[bi + 3] = a; continue; }
                float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                float c = Cover(d) * opacity;
                if (c <= 0) continue;
                float nb = pb + (carried![bi] - pb) * c, ng = pg + (carried[bi + 1] - pg) * c, nr = pr + (carried[bi + 2] - pr) * c, na = a + (carried[bi + 3] - a) * c;
                if (na <= 1e-4f) px[at + 3] = 0;
                else
                {
                    px[at] = (byte)Math.Clamp(MathF.Round(nb / na), 0, 255); px[at + 1] = (byte)Math.Clamp(MathF.Round(ng / na), 0, 255);
                    px[at + 2] = (byte)Math.Clamp(MathF.Round(nr / na), 0, 255); px[at + 3] = (byte)Math.Clamp(MathF.Round(na * 255), 0, 255);
                }
                // the brush takes up some of the new color
                carried[bi] += (nb - carried[bi]) * 0.25f; carried[bi + 1] += (ng - carried[bi + 1]) * 0.25f;
                carried[bi + 2] += (nr - carried[bi + 2]) * 0.25f; carried[bi + 3] += (na - carried[bi + 3]) * 0.25f;
            }
    }

    /// <summary>The brush's coverage at <paramref name="d"/> pixels from its middle: full inside the hard part, then a smooth
    /// fade to the edge (with no softness, the one-pixel edge it always had).</summary>
    static float Cover(float d)
    {
        float inner = radius * (1 - softness);
        if (d <= inner - 0.5f) return 1;
        float fade = Math.Max(1f, radius + 0.5f - (inner - 0.5f));
        float c = Math.Clamp((radius + 0.5f - d) / fade, 0, 1);
        return softness > 0 ? c * c * (3 - 2 * c) : c;
    }

    void Refresh2()
    {
        var old = view; view = Model.ImagePixels.ToBitmap(w, h, px); old.Dispose();
        canvas.Invalidate();
    }

    void Send() { if (Changed != null) Changed(Model.ImagePixels.ToBitmap(w, h, px)); }

    /// <summary>File mode: the picture written to its file. False when it couldn't be (said in a dialog).</summary>
    bool Save()
    {
        if (file == null) return false;
        try { Model.ImagePixels.Save(w, h, px, file); saved = (byte[])px.Clone(); ShowInfo(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Dialog.Show(this, $"{Path.GetFileName(file)} wasn't saved: {ex.Message}", "Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    void Export()
    {
        using var d = new SaveFileDialog { Title = "Export as PNG", Filter = "PNG image (*.png)|*.png", FileName = (file != null ? Path.GetFileNameWithoutExtension(file) + "_copy" : ModInstaller.Sanitise(Text)) + ".png" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { Model.ImagePixels.Save(w, h, px, d.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Dialog.Show(this, $"Not saved: {ex.Message}", "Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // --- tests ----------------------------------------------------------------------------------------------------------------
    internal Control CanvasForTest => canvas;
    /// <summary>Test (--icon-maker-test): an erase (or bring-back) stroke between two canvas points, ended as a mouse-up would.</summary>
    internal void StrokeForTest(Point a, Point b, bool bringBack, float brushRadius)
    {
        radius = brushRadius; var was = tool; tool = Tool.Erase;
        Begin(bringBack); Stroke(a, b); End(); tool = was;
    }
    /// <summary>Test: a brush stroke in <paramref name="c"/> at <paramref name="alpha"/> opacity.</summary>
    internal void PaintForTest(Point a, Point b, Color c, float alpha, float brushRadius)
    {
        radius = brushRadius; color = c; opacity = alpha; var was = tool; tool = Tool.Brush;
        Begin(false); Stroke(a, b); End(); tool = was; opacity = 1;
    }
    /// <summary>Test: the eyedropper at a canvas point; the brush color after.</summary>
    internal Color PickForTest(Point at) { var was = tool; tool = Tool.Eyedropper; Pick(at); tool = was; return color; }
    internal void UndoForTest() => Undo();
    /// <summary>Snapshot: the brush color, opacity and softness set as the controls would (they show it).</summary>
    internal void SettingsForTest(Color c, float o, float soft) { color = c; swatch.Swatch = c; opacitySlider.Value = o; opacity = o; softSlider.Value = soft; softness = soft; ShowInfo(); }
    /// <summary>Test / snapshot: the mouse over a canvas point (the brush ring drawn there).</summary>
    internal void HoverForTest(Point at) { mouse = at; SetTool(Tool.Brush); canvas.Invalidate(); }
    internal static float SoftnessForTest { get => softness; set => softness = value; }
    /// <summary>Test: Clone from <paramref name="source"/> (canvas), then a stroke from <paramref name="a"/> to <paramref name="b"/>.</summary>
    internal void CloneForTest(Point source, Point a, Point b, float brushRadius)
    {
        radius = brushRadius; opacity = 1; var was = tool; tool = Tool.Clone;
        cloneSource = ToLayer(source); cloneOffset = null;
        Begin(false); Stroke(a, b); End(); tool = was;
    }
    /// <summary>Test: a smudge stroke from <paramref name="a"/> to <paramref name="b"/>.</summary>
    internal void SmudgeForTest(Point a, Point b, float brushRadius, float strength)
    {
        radius = brushRadius; opacity = strength; var was = tool; tool = Tool.Smudge;
        Begin(false); Stroke(a, a); Stroke(a, b); End(); tool = was; opacity = 1;
    }
    /// <summary>Test: a click, then Shift + click elsewhere (a straight line between them), as the mouse would.</summary>
    internal void ShiftLineForTest(Point a, Point b, float brushRadius)
    {
        radius = brushRadius; var was = tool; tool = Tool.Erase;
        Begin(false); Stroke(a, a); lastEnd = ToLayer(a); End();
        Begin(false); Stroke(ToCanvas(lastEnd.Value), b); lastEnd = ToLayer(b); End();
        tool = was;
    }
    internal void RedoForTest() => Redo();
    internal void ZoomForTest(Point at, float f) => ZoomAt(at, f);
    /// <summary>Test: the canvas point over an image pixel (the view's zoom and position).</summary>
    internal Point CanvasPointForTest(float lx, float ly) => new((int)(offset.X + lx * Scale), (int)(offset.Y + ly * Scale));
    internal float ZoomNow => zoom;
    internal bool SaveForTest() => Save();

    void PaintCanvas(Graphics g)
    {
        if (offset == PointF.Empty && zoom == 1) Clamp();
        var r = new Rectangle(0, 0, canvas.Width, canvas.Height);
        using (var bg = new SolidBrush(Color.FromArgb(24, 24, 28))) g.FillRectangle(bg, r);
        float k = Scale;
        var area = new RectangleF(offset.X, offset.Y, w * k, h * k);
        if (back is Color c) { using var b = new SolidBrush(c); g.FillRectangle(b, area); }
        else
        {
            // transparency as a checkerboard (behind the picture only)
            int cell = Math.Max(8, Math.Min(canvas.Width, canvas.Height) / 40);
            using var dark = new SolidBrush(Color.FromArgb(40, 40, 46)); using var light = new SolidBrush(Color.FromArgb(62, 62, 70));
            var clip = Rectangle.Intersect(r, Rectangle.Round(area));
            g.SetClip(clip);
            for (int y = clip.Top - clip.Top % cell; y < clip.Bottom; y += cell)
                for (int x = clip.Left - clip.Left % cell; x < clip.Right; x += cell)
                    g.FillRectangle(((x / cell) + (y / cell)) % 2 == 0 ? dark : light, x, y, cell, cell);
            g.ResetClip();
        }
        g.InterpolationMode = k >= 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(view, area);
        // the reference picture over it (the color map), see-through
        if (overlay != null && overlayOn)
        {
            using var ia = new System.Drawing.Imaging.ImageAttributes();
            ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = overlayOpacity });
            g.DrawImage(overlay, Rectangle.Round(area), 0, 0, overlay.Width, overlay.Height, GraphicsUnit.Pixel, ia);
        }
        bool picking = tool == Tool.Eyedropper || PickHeld;
        // Clone: where it copies from (a cross; it follows the brush while painting)
        if (tool == Tool.Clone && cloneSource is PointF cs0)
        {
            var src = cloneOffset is PointF off && mouse.X > -50 ? (PointF)ToCanvas(new PointF(ToLayer(mouse).X + off.X, ToLayer(mouse).Y + off.Y)) : ToCanvas(cs0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var dark = new Pen(Color.FromArgb(180, 0, 0, 0), 3f); using var light = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f);
            foreach (var cp in new[] { dark, light }) { g.DrawLine(cp, src.X - 9, src.Y, src.X + 9, src.Y); g.DrawLine(cp, src.X, src.Y - 9, src.X, src.Y + 9); }
            float rs = radius * Scale;
            using var ring = new Pen(Color.FromArgb(150, 255, 255, 255), 1f) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(ring, src.X - rs, src.Y - rs, rs * 2, rs * 2);
        }
        // Shift held: the straight line a click would paint (from the last stroke's end)
        if (!picking && (ModifierKeys & Keys.Shift) != 0 && lastEnd is PointF le && last == null && mouse.X > -50)
        {
            var a = ToCanvas(le);
            using var line = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, radius * 2 * k)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLine(line, a, mouse);
            using var thin = new Pen(Color.FromArgb(220, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
            g.DrawLine(thin, a, mouse);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (mouse.X < -50) return;
        if (picking)
        {
            // the eyedropper: a ring in the color under the cursor
            var under = UnderMouse();
            using var ring = new Pen(under is Color u && u.A > 0 ? Color.FromArgb(255, u) : Color.Gray, 6f);
            g.DrawEllipse(ring, mouse.X - 14, mouse.Y - 14, 28, 28);
            using var edge = new Pen(Color.FromArgb(200, 0, 0, 0), 1f);
            g.DrawEllipse(edge, mouse.X - 17, mouse.Y - 17, 34, 34);
            return;
        }
        // the brush (filled lightly with its color for the Brush)
        float rr = radius * k;
        if (tool == Tool.Brush) { using var fill = new SolidBrush(Color.FromArgb((int)(60 * opacity), color)); g.FillEllipse(fill, mouse.X - rr, mouse.Y - rr, rr * 2, rr * 2); }
        using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f);
        using var pen2 = new Pen(Color.FromArgb(160, 0, 0, 0), 1f);
        g.DrawEllipse(pen2, mouse.X - rr - 1, mouse.Y - rr - 1, rr * 2 + 2, rr * 2 + 2);
        g.DrawEllipse(pen, mouse.X - rr, mouse.Y - rr, rr * 2, rr * 2);
        if (softness > 0.02f)
        {
            // where the soft edge starts
            float ri = rr * (1 - softness);
            using var inner = new Pen(Color.FromArgb(170, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash };
            if (ri > 1) g.DrawEllipse(inner, mouse.X - ri, mouse.Y - ri, ri * 2, ri * 2);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { view.Dispose(); overlay?.Dispose(); }
        base.Dispose(disposing);
    }
}
