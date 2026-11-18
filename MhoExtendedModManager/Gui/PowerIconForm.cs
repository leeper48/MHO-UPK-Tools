using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MhoExtendedModManager.Model;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Make Icon From 3D View (Kurt, 2026-11-17: "generate power icons by using the 3D preview power animations and choose
/// whatever background color you want"): a snapshot of a 3D preview as it shows now (the power's animation and effects, the
/// frame and camera the user set there; the middle square, as tall as the view), over a background color, at the game's
/// large power-icon size (128 × 128; the power bar's 40 × 40 is made from it). The window stays open beside the editor, so
/// the view can be turned, zoomed and scrubbed between snapshots. Use as Icon hands the PNG to the host.
/// <para>
/// Kurt, 2026-11-17 (2): the icon opens in the image editor (GIMP …) either as the character alone on transparency, so
/// streaks and blurs can be painted and the background and effects still go under and over it here, or as the finished icon
/// with its background (then used as it comes back); every save there comes back. And the image effects the app has
/// elsewhere (Adjust Colors' hue / saturation / brightness and levels, the 3D view's bloom) plus an outline and a shadow.
/// </para>
/// <para>
/// Gear icons (Kurt, 2026-11-17: the Gear page): the same window for a gear slot. The game's gear icons are the piece alone on
/// transparency (40 × 40 in the inventory, 128 × 128 large), so Transparent is on by default there and the effects start at none.
/// </para>
/// </summary>
sealed class PowerIconForm : Form
{
    /// <summary>The game's large power icons (ICO__MarvelUIIcons_HD_SF) are 128 × 128; the bar's are 40 × 40.</summary>
    const int Size128 = 128;
    /// <summary>The snapshot (a size in pixels, square) and the animation playing, from the 3D view (or a picture: the manual's
    /// screenshot).</summary>
    readonly Func<int, Bitmap?> shoot;
    readonly Func<string?> playing;
    readonly string powerName;
    /// <summary>A gear icon (the Gear page), not a power's.</summary>
    readonly bool gear;
    readonly CheckBox clearBack = new() { Text = "Transparent (No Background)", AutoSize = true };
    readonly PictureBox big = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 32) };
    readonly PictureBox bar = new() { SizeMode = PictureBoxSizeMode.Normal, BackColor = Color.FromArgb(28, 28, 32) };
    readonly ColorSwatch colorSwatch = new();
    readonly ColorSwatch outlineSwatch = new();
    readonly CheckBox gradient = new() { Text = "Soft Light in the Middle", AutoSize = true };
    readonly CheckBox twoColor = new() { Text = "Two-Color Radial Gradient", AutoSize = true };
    readonly ColorSwatch edgeSwatch = new();
    LightSlider? gradSize;
    readonly Label status = new() { AutoSize = true, Tag = "subtle" };
    readonly Label editNote = new() { AutoSize = true, Tag = "subtle" };
    readonly List<(LightSlider Slider, float Home)> effectSliders = [];
    Button? backToSnap;
    /// <summary>The 3D view's snapshot as taken (Back to the Snapshot).</summary>
    Bitmap? snapshot;
    /// <summary>The framing over the snapshot (Kurt, 2026-11-17: pan and zoom in the preview): zoom 1 = the snapshot fills the
    /// icon; the pan in icon pixels. The snapshot is taken at 3× the icon's size, so zooming in stays sharp. An image editor's
    /// character-only save comes back framed already (zoom 1, no pan; the snapshot's framing is kept for Back to the Snapshot).</summary>
    float zoom = 1, panX, panY;
    (float Zoom, float X, float Y) snapFraming = (1, 0, 0);
    Point? dragFrom;
    /// <summary>The snapshot's size: 3× the icon, for zooming in.</summary>
    const int SnapSize = Size128 * 3;
    Bitmap? layer;   // the character and effects, transparent around them (the snapshot, or its edit from the image editor)
    /// <summary>The finished icon edited in the image editor with its background (used as it is), or null.</summary>
    Bitmap? flat;
    Bitmap? result;
    FileSystemWatcher? watcher;
    string? editFile;
    bool editAlpha;
    string editHash = "";
    readonly System.Windows.Forms.Timer editDelay = new() { Interval = 700 };
    readonly System.Windows.Forms.Timer redraw = new() { Interval = 40 };

    // the choices, kept while the app runs (the next icon starts from them)
    static Color backColor = Color.FromArgb(22, 32, 58);
    static bool soft = true;
    /// <summary>Two-color radial gradient (Kurt, 2026-11-17): the background color in the middle to the edge color outside,
    /// reaching the edge color at <see cref="gradientSize"/> of the way to the corners.</summary>
    static bool radial;
    static Color edgeColor = Color.FromArgb(4, 6, 14);
    static float gradientSize = 1f;
    static readonly ColorAdjust adjust = new();
    static float contrast, glow, outlineWidth, shadow;
    static Color outlineColor = Color.FromArgb(10, 10, 12);
    /// <summary>No background (the character on transparency): off for powers, on for gear (as the game's gear icons are).</summary>
    static bool transparentPower, transparentGear = true;
    bool Transparent { get => gear ? transparentGear : transparentPower; set { if (gear) transparentGear = value; else transparentPower = value; } }

    /// <summary>The PNG made (Use as Icon): the host puts it in as the power's icon.</summary>
    public event Action<string>? Use;

    /// <summary>
    /// An icon's makings (Kurt, 2026-11-17: "clicking on an already replaced icon brings the make gear icon panel back up to
    /// add glows, tweak colors"): the background, the effects and the framing, kept beside the character layer (a PNG) when the
    /// icon is used, so it opens again as it was.
    /// </summary>
    public sealed record Look(int Back, bool Soft, bool Radial, int Edge, float GradientSize, float Hue, float Saturation, float Brightness, float Gamma,
        float Contrast, float Glow, float Outline, int OutlineColor, float Shadow, bool Transparent, float Zoom, float PanX, float PanY);

    Look Now() => new(backColor.ToArgb(), soft, radial, edgeColor.ToArgb(), gradientSize, adjust.Hue, adjust.Saturation, adjust.Brightness, adjust.Gamma,
        contrast, glow, outlineWidth, outlineColor.ToArgb(), shadow, Transparent, zoom, panX, panY);

    void Take(Look l)
    {
        backColor = Color.FromArgb(l.Back); soft = l.Soft; radial = l.Radial; edgeColor = Color.FromArgb(l.Edge); gradientSize = l.GradientSize;
        adjust.Hue = l.Hue; adjust.Saturation = l.Saturation; adjust.Brightness = l.Brightness; adjust.Gamma = l.Gamma;
        contrast = l.Contrast; glow = l.Glow; outlineWidth = l.Outline; outlineColor = Color.FromArgb(l.OutlineColor); shadow = l.Shadow;
        Transparent = l.Transparent; zoom = l.Zoom; panX = l.PanX; panY = l.PanY;
    }

    /// <summary>The character layer and the look, to <paramref name="stem"/>.png / .json (after Use as Icon), so it can be opened again.</summary>
    public void SaveMakings(string stem)
    {
        if (layer == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        layer.Save(stem + ".png", ImageFormat.Png);
        File.WriteAllText(stem + ".json", System.Text.Json.JsonSerializer.Serialize(Now()));
    }

    /// <summary>A layer and look kept by <see cref="SaveMakings"/>, or null.</summary>
    public static (Bitmap Layer, Look? Look)? LoadMakings(string stem)
    {
        if (!File.Exists(stem + ".png")) return null;
        try
        {
            using var raw = ImagePixels.Open(stem + ".png");
            var bmp = new Bitmap(raw);
            Look? look = null;
            try { if (File.Exists(stem + ".json")) look = System.Text.Json.JsonSerializer.Deserialize<Look>(File.ReadAllText(stem + ".json")); }
            catch (System.Text.Json.JsonException) { }
            return (bmp, look);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return null; }
    }

    /// <summary>Tests: the character layer and the finished icon now.</summary>
    internal Bitmap? LayerForTest => layer;
    internal Bitmap? ResultForTest => result;
    internal Look LookForTest => Now();

    /// <summary>The character layer it opened with (an icon opened again), or null for a snapshot at once.</summary>
    readonly Bitmap? startLayer;

    public PowerIconForm(Preview3D view, string powerName, bool gear = false) : this(size => view.IconSnapshot(size, 2), () => view.CurrentAnimation, powerName, gear) { }

    /// <param name="startLayer">Open on this character layer (an icon made before, or a picture) instead of a snapshot; with
    /// <paramref name="startLook"/>, its background, effects and framing as they were.</param>
    public PowerIconForm(Func<int, Bitmap?> shoot, Func<string?> playing, string powerName, bool gear = false, Bitmap? startLayer = null, Look? startLook = null)
    {
        this.shoot = shoot; this.playing = playing;
        this.powerName = powerName;
        this.gear = gear;
        this.startLayer = startLayer;
        // gear: the effects start at none (Kurt: plain, as the game's gear icons); an icon opened again: as it was made
        if (startLook != null) Take(startLook);
        else if (gear) { adjust.Hue = 0; adjust.Saturation = 1; adjust.Brightness = 1; adjust.Gamma = 1; contrast = glow = outlineWidth = shadow = 0; }
        Text = gear ? $"Make Gear Icon From 3D View: {powerName}" : $"Make Icon From 3D View: {powerName}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int k = 0; k < 4; k++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = new Label
        {
            AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(760 * s), 0), Margin = new Padding(0, 0, 0, 10),
            Text = gear
                ? "Set up the 3D view first: turn, move and zoom the view onto the piece of gear (as in any 3D view; the mouse wheel zooms). The snapshot is the middle of the view (a square as tall as the view); zoom and drag in the icon below to frame it. This window stays open, so you can change the view and take another."
                : "Set up the 3D view first: play the power and pause on a good frame, then turn and zoom. The snapshot is the middle of the view (a square as tall as the view). This window stays open, so you can change the view and take another.",
        };
        t.Controls.Add(hint, 0, 0); t.SetColumnSpan(hint, 3);
        big.Size = new Size((int)(256 * s), (int)(256 * s)); big.Margin = new Padding(0, 0, 14, 0);
        t.Controls.Add(big, 0, 1);
        Ui.Tip(big, "The icon at twice its large size (128 × 128 in game, where the game shows a large one). Click: open it big to erase parts ([ ] brush size). Mouse wheel: zoom at the cursor (Shift: finer); drag: move the character in the icon.");
        big.Cursor = Cursors.SizeAll;
        big.MouseWheel += (_, e) => ZoomAt(e.Location, e.Delta > 0 ? ((ModifierKeys & Keys.Shift) != 0 ? 1.03f : 1.12f) : 1 / ((ModifierKeys & Keys.Shift) != 0 ? 1.03f : 1.12f));
        big.MouseEnter += (_, _) => big.Focus();
        // a click (no drag) opens the eraser; a drag moves the character
        Point? pressedAt = null; bool dragged = false;
        big.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragFrom = e.Location; pressedAt = e.Location; dragged = false; } };
        big.MouseUp += (_, e) =>
        {
            dragFrom = null;
            if (e.Button == MouseButtons.Left && pressedAt != null && !dragged) { pressedAt = null; BeginInvoke(OpenEraser); }
            pressedAt = null;
        };
        big.MouseMove += (_, e) =>
        {
            if (pressedAt is Point p0 && Math.Abs(e.X - p0.X) + Math.Abs(e.Y - p0.Y) > 4) dragged = true;
            if (dragFrom is not Point from || flat != null || !dragged) return;
            float k = big.Width / (float)Size128;
            panX += (e.X - from.X) / k; panY += (e.Y - from.Y) / k;
            dragFrom = e.Location;
            redraw.Stop(); redraw.Start();
        };
        var bigCol = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 14, 0) };
        big.Margin = new Padding(0);
        t.Controls.Remove(big);
        bigCol.Controls.Add(big);
        var resetFrame = Ui.FlatButton("Reset Framing", () => { zoom = 1; panX = panY = 0; Compose(); }, tip: "The whole snapshot in the icon again (no zoom, centered).");
        var erase = Ui.FlatButton("Edit Image", OpenEraser, tip: "Open the icon big in the built-in editor (a click on the icon does the same): Erase (E, where it starts), Brush (B), Bring Back (R, or the right button) and Eyedropper (I, or Alt + click); Shift + click draws a straight line; [ and ] change the brush size; Ctrl+Z undoes.");
        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        tools.Controls.AddRange([erase, resetFrame]);
        bigCol.Controls.Add(tools);
        t.Controls.Add(bigCol, 0, 1);

        // middle: the power bar size, the background, the image editor
        var side = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 18, 0) };
        Label Head(string text) => new() { Text = text, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(0, 0, 0, 4) };
        side.Controls.Add(Head(gear ? "IN THE INVENTORY" : "IN THE POWER BAR"));
        bar.Size = new Size(40, 40); bar.Margin = new Padding(0, 0, 0, 14);
        Ui.Tip(bar, gear ? "The icon at the inventory's own size (40 × 40 pixels)." : "The icon at the power bar's own size (40 × 40 pixels).");
        side.Controls.Add(bar);
        side.Controls.Add(Head("BACKGROUND"));
        clearBack.Checked = Transparent;
        clearBack.Margin = new Padding(0, 0, 0, 6);
        Ui.Tip(clearBack, "No background: the character (or piece of gear) alone on transparency, as the game's gear icons are. The colors below are used when it's off.");
        side.Controls.Add(clearBack);
        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        colorSwatch.Size = new Size((int)(28 * s), (int)(28 * s)); colorSwatch.Swatch = backColor; colorSwatch.Margin = new Padding(0, 0, 8, 0);
        colorSwatch.Click += (_, _) => PickColor();
        Ui.Tip(colorSwatch, "The background color: click to pick another.");
        colorRow.Controls.Add(colorSwatch);
        colorRow.Controls.Add(Ui.FlatButton("Pick Color", PickColor, tip: "Choose the icon's background color."));
        side.Controls.Add(colorRow);
        // quick colors: the game's own icon backgrounds are mostly dark, saturated tones
        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        foreach (var c in new[] { Color.FromArgb(22, 32, 58), Color.FromArgb(70, 12, 16), Color.FromArgb(18, 52, 28), Color.FromArgb(48, 20, 70), Color.FromArgb(70, 46, 8), Color.FromArgb(10, 10, 12), Color.FromArgb(200, 200, 205) })
        {
            var sw = new ColorSwatch { Swatch = c, Size = new Size((int)(20 * s), (int)(20 * s)), Margin = new Padding(0, 0, 4, 0) };
            sw.Click += (_, _) => SetColor(c);
            Ui.Tip(sw, $"Use this background color (#{c.R:X2}{c.G:X2}{c.B:X2}).");
            quick.Controls.Add(sw);
        }
        side.Controls.Add(quick);
        gradient.Checked = soft;
        gradient.CheckedChanged += (_, _) => { soft = gradient.Checked; Compose(); };
        Ui.Tip(gradient, "A lighter glow of the background color behind the middle, darker toward the corners (as many of the game's icons). Off: one flat color.");
        side.Controls.Add(gradient);
        twoColor.Checked = radial;
        twoColor.Margin = new Padding(0, 6, 0, 2);
        Ui.Tip(twoColor, "A radial gradient of two colors: the background color in the middle, the edge color toward the corners (instead of the soft light).");
        side.Controls.Add(twoColor);
        var edgeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        edgeSwatch.Size = new Size((int)(24 * s), (int)(24 * s)); edgeSwatch.Swatch = edgeColor; edgeSwatch.Margin = new Padding(0, 2, 8, 0);
        edgeSwatch.Click += (_, _) => PickEdge();
        Ui.Tip(edgeSwatch, "The gradient's edge color (the corners): click to pick another.");
        edgeRow.Controls.Add(edgeSwatch);
        edgeRow.Controls.Add(Ui.FlatButton("Edge Color", PickEdge, tip: "Choose the color the gradient reaches at the corners."));
        side.Controls.Add(edgeRow);
        gradSize = new LightSlider { Label = "Size", Min = 0.3f, Max = 2f, Step = 0.05f, Value = gradientSize, Home = () => 1, Mark = 1, Format = v => $"{v * 100:0} %", Width = (int)(250 * s), Height = (int)(28 * s), Margin = new Padding(0, 2, 0, 0) };
        gradSize.ValueChanged += () => { gradientSize = gradSize.Value; redraw.Stop(); redraw.Start(); };
        Ui.Tip(gradSize, "How far the middle color reaches: 100 % blends to the edge color at the corners, less makes a smaller spot, more keeps the middle color wider. Double-click: back to 100 %.");
        side.Controls.Add(gradSize);
        void RadialShown()
        {
            bool back = !Transparent;
            colorRow.Enabled = quick.Enabled = twoColor.Enabled = back;
            edgeRow.Enabled = gradSize.Enabled = back && radial; gradient.Enabled = back && !radial;
        }
        twoColor.CheckedChanged += (_, _) => { radial = twoColor.Checked; RadialShown(); Compose(); };
        clearBack.CheckedChanged += (_, _) => { Transparent = clearBack.Checked; RadialShown(); Compose(); };
        RadialShown();
        var editHead = Head("IMAGE EDITOR"); editHead.Margin = new Padding(0, 16, 0, 4);
        side.Controls.Add(editHead);
        string? exe = ImageEditor.Find();
        string editor = exe != null ? ImageEditor.Describe(exe) : "an Image Editor";
        var alphaBtn = Ui.FlatButton("Character Only (Transparent)", () => OpenInEditor(alpha: true),
            $"Opens the character and its effects alone, on transparency, in {editor} (Settings ▾ → Model → Choose Image Editor). Paint streaks, blurs or anything else there and save (PNG, same file; GIMP: Ctrl+E): each save comes back here, and the background and the effects on the right still go under and over it.");
        var flatBtn = Ui.FlatButton("Whole Icon (With Background)", () => OpenInEditor(alpha: false),
            $"Opens the finished icon, background and effects included, in {editor}. Each save (PNG, same file; GIMP: Ctrl+E) comes back here and is used as it is: the background and effects here no longer change it (Back to the Snapshot uses them again).");
        backToSnap = Ui.FlatButton("Back to the Snapshot", BackToSnapshot, "Stop using the image editor's version: the snapshot again, with the background and effects set here.");
        backToSnap.Enabled = false;
        foreach (var b in new[] { alphaBtn, flatBtn, backToSnap }) { b.Margin = new Padding(0, 0, 0, 4); side.Controls.Add(b); }
        editNote.MaximumSize = new Size((int)(250 * s), 0);
        side.Controls.Add(editNote);
        t.Controls.Add(side, 1, 1);

        // right: the effects
        var fx = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        fx.Controls.Add(Head("EFFECTS"));
        LightSlider Add(string label, float min, float max, float step, float now, float home, Func<float, string> format, Action<float> set, string tip)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Value = now, Home = () => home, Mark = home, Format = format, Width = (int)(280 * s), Height = (int)(28 * s), Margin = new Padding(0, 1, 0, 1) };
            sl.ValueChanged += () => { set(sl.Value); redraw.Stop(); redraw.Start(); };
            Ui.Tip(sl, tip + " Double-click: back to " + format(home) + "; arrow keys step (Shift: bigger steps).");
            effectSliders.Add((sl, home));
            fx.Controls.Add(sl);
            return sl;
        }
        Add("Hue", -180, 180, 1, adjust.Hue, 0, v => $"{v:+0;-0;0}°", v => adjust.Hue = v, "Turns the character's colors around the color wheel (as Adjust Colors and the Powers tab's Hue).");
        Add("Saturation", 0, 2, 0.01f, adjust.Saturation, 1, v => $"{v * 100:0} %", v => adjust.Saturation = v, "0 % is gray, 100 % as it is, above more vivid.");
        Add("Brightness", 0, 2, 0.01f, adjust.Brightness, 1, v => $"{v * 100:0} %", v => adjust.Brightness = v, "Darker or lighter.");
        Add("Contrast", 0, 100, 1, contrast, 0, v => $"{v:0}", v => contrast = v, "Deeper shadows and brighter highlights (the levels' black and white points moved in).");
        Add("Gamma", 0.3f, 3, 0.01f, adjust.Gamma, 1, v => $"{v:0.00}", v => adjust.Gamma = v, "The middle tones: above 1 lighter, below 1 darker; black and white stay.");
        Add("Glow", 0, 3, 0.05f, glow, 0, v => $"{v * 100:0} %", v => glow = v, "A soft halo around the brightest parts (the power's effects, lit edges), as the 3D view's Bloom: 0 % is none.");
        var outlineRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        fx.Controls.Add(outlineRow);
        var outline = new LightSlider { Label = "Outline", Min = 0, Max = 4, Step = 0.5f, Value = outlineWidth, Home = () => 0, Mark = 0, Format = v => $"{v:0.#} px", Width = (int)(244 * s), Height = (int)(28 * s), Margin = new Padding(0, 1, 6, 1) };
        outline.ValueChanged += () => { outlineWidth = outline.Value; redraw.Stop(); redraw.Start(); };
        Ui.Tip(outline, "A line of the outline color around the character, this many pixels wide (at 128 × 128): it sets the character off the background. Double-click: back to none.");
        effectSliders.Add((outline, 0));
        outlineSwatch.Size = new Size((int)(26 * s), (int)(26 * s)); outlineSwatch.Swatch = outlineColor; outlineSwatch.Margin = new Padding(0, 2, 0, 0);
        outlineSwatch.Click += (_, _) => { using var d = new ColorDialog { Color = outlineColor, FullOpen = true }; if (d.ShowDialog(this) == DialogResult.OK) { outlineColor = d.Color; outlineSwatch.Swatch = d.Color; Compose(); } };
        Ui.Tip(outlineSwatch, "The outline's color: click to pick another.");
        outlineRow.Controls.AddRange([outline, outlineSwatch]);
        Add("Shadow", 0, 1, 0.01f, shadow, 0, v => $"{v * 100:0} %", v => shadow = v, "A soft dark shadow behind the character, down and to the right, so it stands off the background: 0 % is none.");
        var reset = Ui.FlatButton("Reset Effects", ResetEffects, "Every effect back to none (the background stays).");
        reset.Margin = new Padding(0, 8, 0, 0);
        fx.Controls.Add(reset);
        t.Controls.Add(fx, 2, 1);

        status.Margin = new Padding(0, 10, 0, 0);
        t.Controls.Add(status, 0, 2); t.SetColumnSpan(status, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([
            Ui.AccentButton("Take Snapshot", Snap, tip: "Snapshot the 3D view as it shows now (F5). It replaces an image editor's version."),
            Ui.FlatButton("Save PNG", SavePng, tip: "Save the icon as a PNG file (128 × 128)."),
            Ui.AccentButton("Use as Icon", UseIt, tip: gear
                ? "Make this the gear slot's icon (the inventory's and the large one), converted to the game's sizes; it's put in the Icons tab (Enter)."
                : "Make this the power's icon (the power bar's and the large one), converted to the game's sizes; it's put in the Icons tab (Enter)."),
            Ui.FlatButton("Close", Close, tip: "Close this window (Esc)."),
        ]);
        t.Controls.Add(buttons, 0, 3); t.SetColumnSpan(buttons, 3);
        Controls.Add(t);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (Ui.TypingInSlider(this)) return;   // (Enter / Esc in a slider's typed value belong to that box: Kurt, 2026-11-17)
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { UseIt(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { Snap(); e.Handled = true; }
        };
        redraw.Tick += (_, _) => { redraw.Stop(); Compose(); };
        editDelay.Tick += (_, _) => { editDelay.Stop(); TakeEdit(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Shown += (_, _) =>
        {
            if (startLayer == null) { Snap(); return; }
            // opened again on an icon made before (or a picture): its layer, as it was framed
            snapshot = new Bitmap(startLayer); layer = new Bitmap(startLayer);
            snapFraming = (zoom, panX, panY);
            status.Text = "Opened as It Was Made: Change the Background or Effects, or Take a New Snapshot";
            Compose();
        };
    }

    /// <summary>The manual's screenshot: a two-color gradient, a little glow and an outline.</summary>
    internal static void ShowcaseLook()
    {
        radial = true; backColor = Color.FromArgb(48, 20, 70); edgeColor = Color.FromArgb(6, 4, 12); gradientSize = 1f;
        glow = 0.6f; outlineWidth = 1.5f; outlineColor = Color.FromArgb(10, 10, 12); shadow = 0.4f;
    }

    /// <summary>Beside the view, on its screen.</summary>
    public void PlaceBeside(Control anchor)
    {
        var r = anchor.RectangleToScreen(anchor.ClientRectangle);
        var area = Screen.FromControl(anchor).WorkingArea;
        int x = r.Right - Width - 20, y = r.Top + 20;
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    void PickColor()
    {
        using var d = new ColorDialog { Color = backColor, FullOpen = true };
        if (d.ShowDialog(this) == DialogResult.OK) SetColor(d.Color);
    }

    void SetColor(Color c) { backColor = c; colorSwatch.Swatch = c; Compose(); }

    void PickEdge()
    {
        using var d = new ColorDialog { Color = edgeColor, FullOpen = true };
        if (d.ShowDialog(this) == DialogResult.OK) { edgeColor = d.Color; edgeSwatch.Swatch = d.Color; Compose(); }
    }

    /// <summary>The two-color radial background: per pixel, the middle color eased into the edge color by the distance from the
    /// middle (smoothstep, so it has no hard ring).</summary>
    static Bitmap RadialBack()
    {
        const int N = Size128;
        var px = new byte[N * N * 4];
        float reach = MathF.Max(0.05f, gradientSize) * N * 0.7071f;   // (100 %: the corners)
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x + 0.5f - N / 2f, dy = y + 0.5f - N / 2f;
                float t = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / reach, 0, 1);
                t = t * t * (3 - 2 * t);
                int i = (y * N + x) * 4;
                px[i] = (byte)(backColor.B + (edgeColor.B - backColor.B) * t);
                px[i + 1] = (byte)(backColor.G + (edgeColor.G - backColor.G) * t);
                px[i + 2] = (byte)(backColor.R + (edgeColor.R - backColor.R) * t);
                px[i + 3] = 255;
            }
        return ImagePixels.ToBitmap(N, N, px);
    }

    /// <summary>
    /// The eraser (Kurt, 2026-11-17): the framing is put into the layer first (the layer at its own size, zoomed and moved as
    /// the icon shows it; Back to the Snapshot undoes that), then erased big; each stroke shows here at once.
    /// </summary>
    void OpenEraser()
    {
        if (layer == null) { status.Text = "Take a Snapshot First"; return; }
        if (flat != null) { status.Text = "The Image Editor's Whole Icon Is in Use: Back to the Snapshot to Erase"; return; }
        if (zoom != 1 || panX != 0 || panY != 0)
        {
            var baked = FramedAt(layer.Width);
            if (layer.Width == SnapSize) snapFraming = (zoom, panX, panY);
            layer.Dispose(); layer = baked;
            zoom = 1; panX = panY = 0;
        }
        using var f = new ImageEditForm(layer, Transparent ? null : backColor, "Edit: " + powerName, ImageEditForm.Tool.Erase);
        f.Changed += b => { layer?.Dispose(); layer = b; Compose(); };
        f.ShowDialog(this);
        if (backToSnap != null) backToSnap.Enabled = true;
        status.Text = f.DialogResult == DialogResult.OK ? $"Erased {DateTime.Now:HH:mm:ss}  ·  Back to the Snapshot Starts Over" : status.Text;
    }

    void ResetEffects()
    {
        foreach (var (sl, home) in effectSliders) sl.Value = home;
        adjust.Hue = 0; adjust.Saturation = 1; adjust.Brightness = 1; adjust.Gamma = 1;
        contrast = glow = outlineWidth = shadow = 0;
        Compose();
    }

    /// <summary>Wheel zoom about the point under the cursor (the preview's pixels).</summary>
    void ZoomAt(Point at, float f)
    {
        if (layer == null || flat != null) return;
        float k = big.Width / (float)Size128;
        float px = at.X / k - Size128 / 2f, py = at.Y / k - Size128 / 2f;
        float nz = Math.Clamp(zoom * f, 0.3f, 6f);
        f = nz / zoom; zoom = nz;
        panX = px - (px - panX) * f; panY = py - (py - panY) * f;
        redraw.Stop(); redraw.Start();
    }

    /// <summary>The character layer framed into the icon (zoom and pan over its middle).</summary>
    Bitmap Framed() => FramedAt(Size128);

    /// <summary>The framing at another size (<paramref name="n"/> square): the eraser keeps the layer's own detail.</summary>
    Bitmap FramedAt(int n)
    {
        var b = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        if (layer == null) return b;
        using var g = Graphics.FromImage(b);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = n / (float)Size128;
        float scale = n / (float)layer.Width * zoom, w = layer.Width * scale, h = layer.Height * scale;
        g.DrawImage(layer, new RectangleF(n / 2f + panX * k - w / 2, n / 2f + panY * k - h / 2, w, h));
        return b;
    }

    void Snap()
    {
        var shot = shoot(SnapSize);
        if (shot == null) { status.Text = "Nothing to Snapshot: the 3D View Shows No Model Yet"; return; }
        StopEditing();
        if (layer != null && layer.Width != SnapSize) (zoom, panX, panY) = snapFraming;   // (after an image editor's save: the snapshot's framing again)
        snapshot?.Dispose(); snapshot = shot;
        layer?.Dispose(); layer = new Bitmap(shot);
        flat?.Dispose(); flat = null;
        if (backToSnap != null) backToSnap.Enabled = false;
        status.Text = $"Snapshot Taken {DateTime.Now:HH:mm:ss}" + (playing() is string a ? $"  ·  {a}" : "");
        Compose();
    }

    // --- the image editor -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The icon as a PNG in the image editor: the character alone (the snapshot, or its last edit, without the effects here,
    /// so they aren't put on twice) or the finished icon. A watcher takes each save there back in.
    /// </summary>
    void OpenInEditor(bool alpha)
    {
        if (layer == null) { status.Text = "Take a Snapshot First"; return; }
        string? exe = ImageEditor.Find();
        if (exe == null)
        {
            Dialog.Show(this, "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET under Program Files).\n\nTo pick your editor's program: Settings ▾ → Model → Choose Image Editor → Another Program.", "No Image Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        StopEditing();
        string dir = Path.Combine(Settings.Home, "power-icons", "edit");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{ModInstaller.Sanitise(powerName)}_{(alpha ? "transparent" : "with_background")}.png");
        try
        {
            if (alpha) { using var fr = Framed(); fr.Save(file, ImageFormat.Png); }
            else (result ?? layer).Save(file, ImageFormat.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { status.Text = $"{Path.GetFileName(file)} Couldn't Be Written: Is It Still Open in the Editor?"; return; }
        editFile = file; editAlpha = alpha; editHash = HashOf(file);
        watcher = new FileSystemWatcher(dir, "*.*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(e.FullPath, editFile, StringComparison.OrdinalIgnoreCase)) return;
            try { BeginInvoke(() => { editDelay.Stop(); editDelay.Start(); }); } catch (InvalidOperationException) { }
        }
        watcher.Changed += Changed; watcher.Created += Changed;
        watcher.Renamed += (o, e) => Changed(o, e);
        if (ImageEditor.Open(exe, file) is string failed) { status.Text = $"{ImageEditor.Describe(exe)} Didn't Start: {failed}"; StopEditing(); return; }
        bool gimp = ImageEditor.Describe(exe).StartsWith("GIMP", StringComparison.OrdinalIgnoreCase);
        editNote.Text = $"Open in {ImageEditor.Describe(exe)} ({(alpha ? "character only" : "whole icon")}). Save there as PNG over the same file{(gimp ? " (GIMP: Ctrl+E)" : "")}: each save comes back here.";
        status.Text = "Editing in " + ImageEditor.Describe(exe) + ": Save There to Update";
    }

    /// <summary>A save in the image editor: the new image in (128 × 128; another size is scaled).</summary>
    void TakeEdit()
    {
        if (editFile == null || IsDisposed) return;
        string hash = HashOf(editFile);
        if (hash.Length == 0 || hash == editHash) return;
        Bitmap? img = null;
        for (int tries = 0; tries < 5 && img == null; tries++)
            try
            {
                using var raw = ImagePixels.Open(editFile);
                img = new Bitmap(Size128, Size128, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(img);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(raw, new Rectangle(0, 0, Size128, Size128));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or System.Runtime.InteropServices.ExternalException) { img?.Dispose(); img = null; Thread.Sleep(150); }
        if (img == null) { status.Text = "The Edited Image Couldn't Be Read (Saved as PNG?)"; return; }
        editHash = hash;
        if (editAlpha)
        {
            // (it comes back framed: the snapshot's framing kept for Back to the Snapshot)
            if (layer != null && layer.Width == SnapSize) snapFraming = (zoom, panX, panY);
            layer?.Dispose(); layer = img; flat?.Dispose(); flat = null;
            zoom = 1; panX = panY = 0;
        }
        else { flat?.Dispose(); flat = img; }
        if (backToSnap != null) backToSnap.Enabled = true;
        status.Text = $"Edit Taken In {DateTime.Now:HH:mm:ss}" + (editAlpha ? "  ·  Background and Effects Applied Under and Over It" : "  ·  Used as It Is (Background and Effects Not Applied)");
        Compose();
    }

    void BackToSnapshot()
    {
        StopEditing();
        flat?.Dispose(); flat = null;
        if (backToSnap != null) backToSnap.Enabled = false;
        if (snapshot == null) return;
        layer?.Dispose(); layer = new Bitmap(snapshot);
        (zoom, panX, panY) = snapFraming;
        status.Text = "The Snapshot Again";
        Compose();
    }

    void StopEditing()
    {
        editDelay.Stop();
        watcher?.Dispose(); watcher = null;
        editFile = null;
        editNote.Text = "";
    }

    static string HashOf(string file)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    // --- the icon ---------------------------------------------------------------------------------------------------------------

    /// <summary>The character with the effects, over the background (or the image editor's whole icon as it is).</summary>
    void Compose()
    {
        if (layer == null) return;
        Bitmap b;
        if (flat != null) b = new Bitmap(flat);
        else
        {
            b = new Bitmap(Size128, Size128, PixelFormat.Format32bppArgb);
            using var framed = Framed();
            using var fxLayer = Effects(framed);
            using var g = Graphics.FromImage(b);
            if (Transparent) g.Clear(Color.Transparent);
            else g.Clear(backColor);
            if (Transparent) { }
            else if (radial) { using var rb = RadialBack(); g.DrawImageUnscaled(rb, 0, 0); }
            else if (soft)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(-Size128 * 0.35f, -Size128 * 0.35f, Size128 * 1.7f, Size128 * 1.7f);
                using var pg = new PathGradientBrush(path) { CenterColor = Lighter(backColor, 0.45f), SurroundColors = [Darker(backColor, 0.55f)] };
                g.FillRectangle(pg, 0, 0, Size128, Size128);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImageUnscaled(fxLayer, 0, 0);
        }
        result?.Dispose(); result = b;
        var oldBig = big.Image; big.Image = new Bitmap(b); oldBig?.Dispose();
        var oldBar = bar.Image;
        var small = new Bitmap(40, 40);
        using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(b, new Rectangle(0, 0, 40, 40)); }
        bar.Image = small; oldBar?.Dispose();
    }

    /// <summary>
    /// The effects on the character layer (straight-alpha BGRA): colors and levels (ColorAdjust), then a glow from its brightest
    /// parts (added light, as the 3D view's Bloom), and under it an outline and a soft shadow.
    /// </summary>
    static Bitmap Effects(Bitmap src)
    {
        const int N = Size128;
        var px = ImageEditorPixels(src);
        var a = new ColorAdjust { Hue = adjust.Hue, Saturation = adjust.Saturation, Brightness = adjust.Brightness, Gamma = adjust.Gamma, InBlack = contrast, InWhite = 255 - contrast };
        if (!a.IsNone) a.Apply(px);
        // premultiplied floats
        var r = new float[N * N]; var gr = new float[N * N]; var bl = new float[N * N]; var al = new float[N * N];
        for (int i = 0; i < N * N; i++)
        {
            float k = px[i * 4 + 3] / 255f;
            al[i] = k; r[i] = px[i * 4 + 2] / 255f * k; gr[i] = px[i * 4 + 1] / 255f * k; bl[i] = px[i * 4] / 255f * k;
        }
        if (glow > 0.001f)
        {
            // the light past a threshold, blurred, added back (it can light the empty pixels around the character too)
            var gR = new float[N * N]; var gG = new float[N * N]; var gB = new float[N * N];
            for (int i = 0; i < N * N; i++)
            {
                float lum = 0.3f * r[i] + 0.59f * gr[i] + 0.11f * bl[i];
                float m = Math.Clamp((lum - 0.45f) / 0.55f, 0, 1);
                gR[i] = r[i] * m; gG[i] = gr[i] * m; gB[i] = bl[i] * m;
            }
            foreach (var ch in new[] { gR, gG, gB }) { Blur(ch, N, 4); Blur(ch, N, 4); }
            for (int i = 0; i < N * N; i++)
            {
                float addR = gR[i] * glow, addG = gG[i] * glow, addB = gB[i] * glow;
                r[i] += addR; gr[i] += addG; bl[i] += addB;
                al[i] = Math.Min(1, Math.Max(al[i], Math.Max(addR, Math.Max(addG, addB))));
            }
        }
        // under the character: the shadow, then the outline (each composited beneath what's there)
        void Under(float[] alpha, Color c)
        {
            float cr = c.R / 255f, cg = c.G / 255f, cb = c.B / 255f;
            for (int i = 0; i < N * N; i++)
            {
                float u = alpha[i] * (1 - al[i]);
                r[i] += cr * u; gr[i] += cg * u; bl[i] += cb * u; al[i] += u;
            }
        }
        if (outlineWidth > 0.01f)
        {
            // the alpha grown by the width (a round reach), soft-edged by its fraction
            var grown = new float[N * N];
            int reach = (int)MathF.Ceiling(outlineWidth);
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float best = 0;
                    for (int dy = -reach; dy <= reach; dy++)
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= N || yy >= N) continue;
                            float d = MathF.Sqrt(dx * dx + dy * dy), w = Math.Clamp(outlineWidth + 0.5f - d, 0, 1);
                            if (w > 0) best = Math.Max(best, al[yy * N + xx] * w);
                        }
                    grown[y * N + x] = best;
                }
            Under(grown, outlineColor);
        }
        if (shadow > 0.001f)
        {
            var sh = new float[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int sx = x - 3, sy = y - 3;
                    sh[y * N + x] = sx >= 0 && sy >= 0 ? al[sy * N + sx] : 0;
                }
            Blur(sh, N, 3); Blur(sh, N, 3);
            for (int i = 0; i < N * N; i++) sh[i] = Math.Min(1, sh[i] * shadow * 1.2f);
            Under(sh, Color.Black);
        }
        var o = new byte[N * N * 4];
        for (int i = 0; i < N * N; i++)
        {
            float k = Math.Clamp(al[i], 0, 1);
            o[i * 4 + 3] = (byte)MathF.Round(k * 255);
            if (k <= 0.0001f) continue;
            o[i * 4 + 2] = (byte)Math.Clamp(MathF.Round(r[i] / k * 255), 0, 255);
            o[i * 4 + 1] = (byte)Math.Clamp(MathF.Round(gr[i] / k * 255), 0, 255);
            o[i * 4] = (byte)Math.Clamp(MathF.Round(bl[i] / k * 255), 0, 255);
        }
        return ImagePixels.ToBitmap(N, N, o);
    }

    static byte[] ImageEditorPixels(Bitmap b)
    {
        var argb = ImagePixels.ReadArgb(b);
        var px = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++) { int c = argb[i]; px[4 * i] = (byte)c; px[4 * i + 1] = (byte)(c >> 8); px[4 * i + 2] = (byte)(c >> 16); px[4 * i + 3] = (byte)(c >> 24); }
        return px;
    }

    /// <summary>A box blur of radius <paramref name="rad"/> (rows, then columns), in place.</summary>
    static void Blur(float[] v, int n, int rad)
    {
        var tmp = new float[v.Length];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = 0; int c = 0;
                for (int k = -rad; k <= rad; k++) { int xx = x + k; if (xx < 0 || xx >= n) continue; s += v[y * n + xx]; c++; }
                tmp[y * n + x] = s / c;
            }
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = 0; int c = 0;
                for (int k = -rad; k <= rad; k++) { int yy = y + k; if (yy < 0 || yy >= n) continue; s += tmp[yy * n + x]; c++; }
                v[y * n + x] = s / c;
            }
    }

    static Color Lighter(Color c, float k) => Color.FromArgb(c.R + (int)((255 - c.R) * k), c.G + (int)((255 - c.G) * k), c.B + (int)((255 - c.B) * k));
    static Color Darker(Color c, float k) => Color.FromArgb((int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k)));

    string? WritePng()
    {
        if (result == null) return null;
        string png = Path.Combine(Settings.Home, "converted", $"{(gear ? "gearicon" : "powericon")}_{Guid.NewGuid().ToString("N")[..8]}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        result.Save(png, ImageFormat.Png);
        return png;
    }

    void SavePng()
    {
        if (result == null) return;
        using var d = new SaveFileDialog { Title = "Save Icon", Filter = "PNG image (*.png)|*.png", FileName = gear ? "gear_icon.png" : "power_icon.png" };
        if (d.ShowDialog(this) == DialogResult.OK) { result.Save(d.FileName, ImageFormat.Png); status.Text = "Saved " + Path.GetFileName(d.FileName); }
    }

    void UseIt()
    {
        if (WritePng() is not string png) { status.Text = "Take a Snapshot First"; return; }
        Use?.Invoke(png);
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopEditing();
            editDelay.Dispose(); redraw.Dispose();
            snapshot?.Dispose(); layer?.Dispose(); flat?.Dispose(); result?.Dispose(); big.Image?.Dispose(); bar.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
