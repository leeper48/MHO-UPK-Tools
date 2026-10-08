using System.Drawing;
using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// Adjust Colors (Kurt, 2026-10-07, a user's request): a material's color map with hue / saturation / brightness and levels
/// (input black and white, gamma, output black and white), the map before and after side by side, updating as the sliders
/// move. OK keeps the values with the material (the file isn't changed: the preview, Build and Export FBX use an adjusted
/// copy, ColorAdjust.FileFor).
/// </summary>
sealed class ColorAdjustForm : Form
{
    readonly PictureBox before = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 32) };
    readonly PictureBox after = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 32) };
    readonly int w, h;
    readonly byte[] small;   // the map at preview size (BGRA)
    readonly ColorAdjust adj;
    readonly List<(LightSlider Slider, Func<float> Get, Action<float> Set, float Reset)> sliders = [];
    readonly System.Windows.Forms.Timer redraw = new() { Interval = 40 };

    /// <summary>The values chosen (null: as the map is), set when OK closes the window.</summary>
    public ColorAdjust? Result { get; private set; }

    public ColorAdjustForm(string material, string colorFile, ColorAdjust? current)
    {
        adj = current?.Copy() ?? new ColorAdjust();
        // a copy at most 512 pixels on its longer side: the sliders redraw it as they move
        using (var full = new Bitmap(colorFile))
        {
            float k = Math.Min(1f, 512f / Math.Max(full.Width, full.Height));
            w = Math.Max(1, (int)(full.Width * k)); h = Math.Max(1, (int)(full.Height * k));
            using var sm = new Bitmap(full, w, h);
            small = ImageToBgra(sm);
        }
        before.Image = ImagePixels.ToBitmap(w, h, (byte[])small.Clone());

        Text = $"Adjust Colors: {material}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330 * s));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = "BEFORE", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(2, 0, 0, 4) }, 0, 0);
        t.Controls.Add(new Label { Text = "AFTER", AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(2, 0, 0, 4) }, 1, 0);
        before.Margin = new Padding(0, 0, 6, 0); after.Margin = new Padding(0, 0, 10, 0);
        t.Controls.Add(before, 0, 1); t.Controls.Add(after, 1, 1);
        Ui.Tip(before, "The color map as it is (the file isn't changed).");
        Ui.Tip(after, "The color map as the preview, Build and Export FBX will use it.");

        var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Heading(string text) => side.Controls.Add(new Label { Text = text, AutoSize = true, Tag = "subtle", Font = Ui.Bold(8.5f), Margin = new Padding(2, 10, 0, 2) });
        void Add(string label, float min, float max, float step, Func<float> get, Action<float> set, float reset, Func<float, string> format, string tip)
        {
            var sl = new LightSlider { Label = label, Min = min, Max = max, Step = step, Value = get(), Home = () => reset, Mark = reset, Format = format,
                Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = (int)(30 * s), Margin = new Padding(0, 2, 0, 2) };
            sl.ValueChanged += () => { set(sl.Value); redraw.Stop(); redraw.Start(); };
            Ui.Tip(sl, tip + " Double-click: back to " + format(reset) + "; arrow keys step (Shift: bigger steps).");
            sliders.Add((sl, get, set, reset));
            side.Controls.Add(sl);
        }
        Heading("HUE, SATURATION, BRIGHTNESS");
        Add("Hue", -180, 180, 1, () => adj.Hue, v => adj.Hue = v, 0, v => $"{v:+0;-0;0}°", "Turns every color around the color wheel (as the Powers tab's Hue).");
        Add("Saturation", 0, 2, 0.01f, () => adj.Saturation, v => adj.Saturation = v, 1, v => $"{v * 100:0} %", "0 % is gray, 100 % as it is, above more vivid.");
        Add("Brightness", 0, 2, 0.01f, () => adj.Brightness, v => adj.Brightness = v, 1, v => $"{v * 100:0} %", "Darker or lighter, every color alike.");
        Heading("LEVELS");
        Add("Input Black", 0, 254, 1, () => adj.InBlack, v => adj.InBlack = v, 0, v => $"{v:0}", "Values at or below this become black: raise it for deeper shadows and more contrast.");
        Add("Input White", 1, 255, 1, () => adj.InWhite, v => adj.InWhite = v, 255, v => $"{v:0}", "Values at or above this become white: lower it for brighter highlights and more contrast.");
        Add("Gamma", 0.1f, 3, 0.01f, () => adj.Gamma, v => adj.Gamma = v, 1, v => $"{v:0.00}", "The middle tones: above 1 lighter, below 1 darker, black and white stay.");
        Add("Output Black", 0, 255, 1, () => adj.OutBlack, v => adj.OutBlack = v, 0, v => $"{v:0}", "The darkest value the map ends up with: raise it to soften the shadows (less contrast).");
        Add("Output White", 0, 255, 1, () => adj.OutWhite, v => adj.OutWhite = v, 255, v => $"{v:0}", "The brightest value the map ends up with: lower it to dim the highlights.");
        side.Controls.Add(new Label { Text = "Levels are applied first, then hue, saturation and brightness. Color tags and the glow guess are still made from the map before these changes.", AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(310 * s), 0), Margin = new Padding(2, 12, 0, 0) });
        t.Controls.Add(side, 2, 0); t.SetRowSpan(side, 2);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Reset", ResetAll, tip: "Every slider back to its start: the map as it is."),
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, tip: "Close without changing the material (Esc)."),
            Ui.AccentButton("OK", Accept, tip: "Keep these values with the material: the preview, Build and Export FBX use the adjusted map (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 2); t.SetColumnSpan(buttons, 3);
        Controls.Add(t);
        redraw.Tick += (_, _) => { redraw.Stop(); Redraw(); };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1100, 640);
        Redraw();
    }

    void ResetAll()
    {
        foreach (var (sl, _, set, reset) in sliders) { set(reset); sl.Value = reset; }
        Redraw();
    }

    void Redraw()
    {
        var px = (byte[])small.Clone();
        adj.Apply(px);
        var old = after.Image;
        after.Image = ImagePixels.ToBitmap(w, h, px);
        old?.Dispose();
    }

    void Accept()
    {
        Result = adj.IsNone ? null : adj.Copy();
        DialogResult = DialogResult.OK;
        Close();
    }

    static byte[] ImageToBgra(Bitmap b)
    {
        var argb = ImagePixels.ReadArgb(b);
        var px = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++) { int c = argb[i]; px[4 * i] = (byte)c; px[4 * i + 1] = (byte)(c >> 8); px[4 * i + 2] = (byte)(c >> 16); px[4 * i + 3] = (byte)(c >> 24); }
        return px;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { redraw.Dispose(); before.Image?.Dispose(); after.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
