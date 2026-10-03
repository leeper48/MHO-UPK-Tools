using System.Runtime.InteropServices;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The color picker of the Powers tab's color replacement (Kurt, 2026-10-03: a color wheel and an eyedropper driving the
/// hex value): a popup in the app's dark style (no system dialog) with a hue / saturation wheel, a Brightness slider, the
/// color as hex, and an eyedropper: press it and drag onto any color on screen (the 3D view, another window), release to
/// take it. Every change goes to <c>changed</c> at once (the row's hex box, so the preview follows).
/// </summary>
sealed class ColorPickerPopup : ToolStripDropDown
{
    readonly ColorWheel wheel;
    readonly LightSlider bright;
    readonly ColorSwatch preview;
    readonly Label hex;
    readonly Action<Color> changed;
    readonly Color initial;
    readonly ColorSwatch[] recents = new ColorSwatch[5];
    Color current;
    bool setting;

    ColorPickerPopup(Control owner, Rectangle anchor, Color initial, Color? original, Action<Color> changed)
    {
        this.changed = changed; this.initial = initial; current = initial;
        float s = owner.DeviceDpi / 96f;
        AutoSize = false; Padding = Padding.Empty; Margin = Padding.Empty; DropShadowEnabled = true;
        BackColor = Modern.MenuBack;
        int w = (int)(250 * s);
        var panel = new TableLayoutPanel { BackColor = Modern.MenuBack, ColumnCount = 1, Padding = new Padding((int)(10 * s)), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        wheel = new ColorWheel { Size = new Size(w - (int)(20 * s), w - (int)(20 * s)), Margin = new Padding(0, 0, 0, (int)(6 * s)) };
        bright = new LightSlider { Label = "Brightness", Min = 0, Max = 1, Step = 0.01f, Mark = 1, Format = v => $"{v * 100:0} %", Home = () => 1, Width = w - (int)(20 * s), Height = (int)(30 * s) };
        preview = new ColorSwatch { Size = new Size((int)(34 * s), (int)(28 * s)), Margin = new Padding(0, 2, (int)(6 * s), 0), Cursor = Cursors.Default };
        hex = new Label { AutoSize = true, ForeColor = Ui.Text, Font = Ui.Regular(10f), Margin = new Padding(0, (int)(7 * s), (int)(10 * s), 0) };
        var dropper = Ui.FlatButton("Eyedropper", () => { }, "Press here and drag onto any color on the screen (the 3D view, a picture in another window): release to take that color.");
        Eyedropper.Attach(dropper, c => { Set(c); changed(c); });
        var done = Ui.AccentButton("Done", Close, "Close the color picker (Esc too); the color is kept.");
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, (int)(4 * s), 0, 0) };
        row.Controls.AddRange([preview, hex, dropper, done]);
        // Under the wheel: the five most recent colors (click one to use it) and Reset (the game's own color).
        var recentRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, (int)(6 * s)) };
        recentRow.Controls.Add(new Label { Text = "Recent", AutoSize = true, Tag = "subtle", Margin = new Padding(0, (int)(7 * s), (int)(8 * s), 0) });
        for (int i = 0; i < 5; i++)
        {
            int k = i;
            var sw = new ColorSwatch { Size = new Size((int)(26 * s), (int)(24 * s)), Margin = new Padding(0, (int)(2 * s), (int)(4 * s), 0), Number = 0 };
            sw.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || k >= PreviewViews.RecentColors.Count || ColorMap.FromHex(PreviewViews.RecentColors[k]) is not { } v) return;
                var c = Color.FromArgb((int)MathF.Round(v.X * 255), (int)MathF.Round(v.Y * 255), (int)MathF.Round(v.Z * 255));
                Set(c); changed(c);
            };
            recents[k] = sw;
            recentRow.Controls.Add(sw);
        }
        ShowRecents();
        var reset = Ui.FlatButton("Reset", () => { if (original is { } o) { Set(o); changed(o); } },
            "Back to the power's own color (the game's): this color then isn't changed.");
        reset.Enabled = original != null;
        reset.Margin = new Padding((int)(6 * s), 0, 0, 0);
        recentRow.Controls.Add(reset);
        panel.Controls.Add(wheel); panel.Controls.Add(recentRow); panel.Controls.Add(bright); panel.Controls.Add(row);
        Closed += (_, _) => { if (current.ToArgb() != this.initial.ToArgb()) PreviewViews.AddRecentColor($"#{current.R:X2}{current.G:X2}{current.B:X2}"); };
        Ui.Tip(wheel, "Click or drag: the color's hue (around) and how strong it is (toward the edge).");
        Ui.Tip(bright, "How bright the color is. A darker color also makes that part of the power dimmer.");
        Theme.ApplyTree(panel, Palette.Dark);
        Ui.RestyleButtons(panel);
        // The theme gives some controls a background of their own: everything but the buttons takes the popup's.
        static IEnumerable<Control> All(Control c) => c.Controls.Cast<Control>().SelectMany(x => All(x).Prepend(x));
        foreach (Control c in All(panel).Prepend(panel).Where(c => c is not Button)) c.BackColor = Modern.MenuBack;
        wheel.Changed += Changed;
        bright.ValueChanged += Changed;
        Set(initial);
        panel.PerformLayout();
        var size = panel.GetPreferredSize(Size.Empty);
        panel.Size = size;
        Items.Add(new ToolStripControlHost(panel) { AutoSize = false, Size = size, Margin = Padding.Empty, Padding = Padding.Empty });
        Size = size;
        var screen = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Left, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        bool up = anchor.Bottom + size.Height > screen.Bottom && anchor.Top - size.Height >= screen.Top;
        Location = new Point(x, up ? anchor.Top - size.Height - 2 : anchor.Bottom + 2);
    }

    void Set(Color c)
    {
        setting = true;
        var (h, sat, v) = Hsv(c);
        wheel.Hue = h; wheel.Saturation = sat; wheel.Value = v; wheel.Invalidate();
        bright.Value = v;
        setting = false;
        Show(c);
    }

    void Show(Color c) { current = c; preview.Swatch = c; hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }

    void ShowRecents()
    {
        var list = PreviewViews.RecentColors;
        for (int k = 0; k < 5; k++)
        {
            var v = k < list.Count ? ColorMap.FromHex(list[k]) : null;
            recents[k].Swatch = v is { } c ? Color.FromArgb((int)MathF.Round(c.X * 255), (int)MathF.Round(c.Y * 255), (int)MathF.Round(c.Z * 255)) : null;
            recents[k].Cursor = v == null ? Cursors.Default : Cursors.Hand;
            Ui.Tip(recents[k], v == null ? "An empty slot: colors you pick show up here (the five most recent, on this PC)." : $"{list[k]}: a color you used recently. Click: use it.");
        }
    }

    void Changed()
    {
        if (setting) return;
        wheel.Value = bright.Value; wheel.Invalidate();
        var c = FromHsv(wheel.Hue, wheel.Saturation, bright.Value);
        Show(c);
        changed(c);
    }

    /// <summary>Opens the picker under <paramref name="anchor"/> (screen coordinates).</summary>
    /// <param name="original">What Reset goes back to (the power's own color); null: no Reset.</param>
    public static ColorPickerPopup Open(Control owner, Rectangle anchor, Color initial, Color? original, Action<Color> changed)
    {
        var p = new ColorPickerPopup(owner, anchor, initial, original, changed);
        p.Closed += (_, _) => p.BeginInvoke(p.Dispose);
        p.Show(p.Location);
        return p;
    }

    public static (float H, float S, float V) Hsv(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        float h = d < 1e-6f ? 0 : mx == r ? 60 * ((g - b) / d % 6) : mx == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, mx <= 0 ? 0 : d / mx, mx);
    }

    public static Color FromHsv(float h, float s, float v)
    {
        h = (h % 360 + 360) % 360;
        float c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        static int B(float f) => Math.Clamp((int)MathF.Round(f * 255), 0, 255);
        return Color.FromArgb(B(r + m), B(g + m), B(b + m));
    }

    /// <summary>For --color-picker-snapshot: the popup built (not shown) at <paramref name="initial"/> and drawn to a PNG.</summary>
    internal static void Snapshot(string png, Color initial)
    {
        using var owner = new Panel();
        var p = new ColorPickerPopup(owner, new Rectangle(-4000, -4000, 30, 30), initial, Color.Orange, _ => { });
        var panel = ((ToolStripControlHost)p.Items[0]).Control;
        // (a panel never on screen draws no children: shown in an off-screen window for the picture)
        using var f = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000), ClientSize = panel.Size, BackColor = Modern.MenuBack };
        f.Controls.Add(panel);
        f.Show();
        Application.DoEvents();
        using var b = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(b, new Rectangle(0, 0, panel.Width, panel.Height));
        b.Save(png);
        f.Close();
        p.Dispose();
    }
}

/// <summary>A hue (around) / saturation (out from the middle) wheel, drawn at the current brightness.</summary>
sealed class ColorWheel : Control
{
    public float Hue, Saturation, Value = 1;
    public event Action? Changed;
    Bitmap? disc;
    float discValue = -1;
    int discSize;

    public ColorWheel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Cursor = Cursors.Cross;
    }

    float Radius => Math.Min(Width, Height) / 2f - 3;
    PointF Center => new(Width / 2f, Height / 2f);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Modern.MenuBack);
        int size = (int)(Radius * 2);
        if (disc == null || discSize != size || Math.Abs(discValue - Value) > 0.004f)
        {
            disc?.Dispose();
            disc = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = disc.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new int[size * size];
            float r0 = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r0, dy = y + 0.5f - r0, d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d > r0) continue;
                    float h = MathF.Atan2(-dy, dx) * 180 / MathF.PI;
                    var c = ColorPickerPopup.FromHsv(h, Math.Min(1, d / r0), Value);
                    int a = d > r0 - 1 ? (int)(255 * (r0 - d)) : 255;   // a soft edge
                    px[y * size + x] = a << 24 | c.R << 16 | c.G << 8 | c.B;
                }
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            disc.UnlockBits(bd);
            discSize = size; discValue = Value;
        }
        var ctr = Center;
        g.DrawImage(disc, ctr.X - size / 2f, ctr.Y - size / 2f);
        float ang = Hue * MathF.PI / 180, rad = Saturation * Radius;
        var m = new PointF(ctr.X + MathF.Cos(ang) * rad, ctr.Y - MathF.Sin(ang) * rad);
        float s = DeviceDpi / 96f, k = 6 * s;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var p1 = new Pen(Color.Black, 3 * s)) g.DrawEllipse(p1, m.X - k, m.Y - k, 2 * k, 2 * k);
        using (var p2 = new Pen(Color.White, 1.5f * s)) g.DrawEllipse(p2, m.X - k, m.Y - k, 2 * k, 2 * k);
    }

    void Pick(Point p)
    {
        var ctr = Center;
        float dx = p.X - ctr.X, dy = p.Y - ctr.Y;
        Hue = (MathF.Atan2(-dy, dx) * 180 / MathF.PI + 360) % 360;
        Saturation = Math.Min(1, MathF.Sqrt(dx * dx + dy * dy) / Radius);
        Invalidate();
        Changed?.Invoke();
    }

    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Pick(e.Location); base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Pick(e.Location); base.OnMouseMove(e); }
    protected override void Dispose(bool disposing) { if (disposing) disc?.Dispose(); base.Dispose(disposing); }
}

/// <summary>
/// Press-and-drag eyedropper: while the button is held (Windows keeps the mouse captured by it), the color under the cursor
/// anywhere on screen is read live; releasing takes it. The screen is read in physical pixels (the thread switches to
/// per-monitor DPI awareness for the read), so it's right on every monitor whatever its scaling.
/// </summary>
static class Eyedropper
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    static readonly IntPtr PerMonitorV2 = new(-4);

    /// <summary>The color of the screen pixel under the mouse, or null when it can't be read.</summary>
    public static Color? UnderCursor()
    {
        IntPtr old = SetThreadDpiAwarenessContext(PerMonitorV2);
        try
        {
            if (!GetCursorPos(out var p)) return null;
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try
            {
                uint v = GetPixel(dc, p.X, p.Y);
                if (v == 0xFFFFFFFF) return null;   // CLR_INVALID
                return Color.FromArgb((int)(v & 255), (int)(v >> 8 & 255), (int)(v >> 16 & 255));
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    /// <summary>Makes <paramref name="button"/> an eyedropper: hold it and move to see colors (<paramref name="picked"/> on every
    /// move), release to keep the last one.</summary>
    public static void Attach(Control button, Action<Color> picked)
    {
        bool down = false;
        button.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return; down = true; button.Cursor = Cursors.Cross; };
        button.MouseMove += (_, _) => { if (down && !button.ClientRectangle.Contains(button.PointToClient(Cursor.Position)) && UnderCursor() is { } c) picked(c); };
        button.MouseUp += (_, _) =>
        {
            if (!down) return;
            down = false; button.Cursor = Cursors.Default;
            if (!button.ClientRectangle.Contains(button.PointToClient(Cursor.Position)) && UnderCursor() is { } c) picked(c);
        };
    }
}
