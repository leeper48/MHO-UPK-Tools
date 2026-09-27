using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MhoPackageModifier.Gui;

/// <summary>
/// A texture viewer: the whole image or zoomed (fit, 25–800%), with its alpha over a checkerboard, or one channel at a
/// time (colour, alpha, R, G, B), the pixel under the mouse, Save as PNG, and Pop out (the same image in its own
/// window). Lives on the Textures tab, where it shows the texture clicked in the list (the largest mip available,
/// including from the .tfc caches) or an image file chosen for import.
/// </summary>
sealed class TextureViewer : UserControl
{
    byte[]? bgra;
    int w, h;
    string name = "", info = "";
    Bitmap? view;
    float zoom = 1f;
    bool fit = true;
    readonly Panel canvas = new DoubleBufferedPanel { Dock = DockStyle.Fill, AutoScroll = true };
    readonly ComboBox show = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly ComboBox zoomBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly Label pixel = new() { AutoSize = true, Padding = new Padding(8, 7, 0, 0) };
    readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), Tag = "hint" };
    readonly Button save = new() { Text = "Save as PNG…", AutoSize = true }, popOut = new() { Text = "Pop out", AutoSize = true };
    string message = "Click a texture in the list to see it here.";

    public bool HasImage => bgra != null;

    /// <summary>Called for Pop out (the owner opens the window, themed like the app).</summary>
    public Action<string, byte[], int, int, string>? PopOut { get; set; }

    sealed class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

    public TextureViewer()
    {
        show.Items.AddRange(["Colour with alpha (checkerboard)", "Colour only", "Alpha only", "Red", "Green", "Blue"]);
        show.SelectedIndex = 0;
        show.SelectedIndexChanged += (_, _) => Rebuild();
        zoomBox.Items.AddRange(["Fit", "25%", "50%", "100%", "200%", "400%", "800%"]);
        zoomBox.SelectedIndex = 0;
        zoomBox.SelectedIndexChanged += (_, _) =>
        {
            fit = zoomBox.SelectedIndex == 0;
            if (!fit) zoom = float.Parse(((string)zoomBox.SelectedItem!).TrimEnd('%')) / 100f;
            LayoutCanvas();
        };
        save.Click += (_, _) => SavePng();
        popOut.Click += (_, _) => { if (bgra != null) PopOut?.Invoke(name, bgra, w, h, info); };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2), WrapContents = true };
        Label L(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(6, 7, 0, 0) };
        bar.Controls.AddRange([L("Show:"), show, L("Zoom:"), zoomBox, save, popOut, pixel]);

        canvas.Paint += (_, e) => Draw(e.Graphics);
        canvas.Resize += (_, _) => { if (fit) canvas.Invalidate(); };
        canvas.MouseMove += (_, e) => ShowPixel(e.Location);
        canvas.MouseWheel += (_, e) =>
        {
            if ((ModifierKeys & Keys.Control) == 0) return;
            zoomBox.SelectedIndex = Math.Clamp(zoomBox.SelectedIndex + (e.Delta > 0 ? 1 : -1), 1, zoomBox.Items.Count - 1);
        };
        Controls.Add(canvas);
        Controls.Add(status);
        Controls.Add(bar);
        UpdateButtons();
    }

    /// <summary>Shows an image (BGRA pixels); the view and zoom chosen stay as they are.</summary>
    public void ShowImage(string name, byte[] bgra, int w, int h, string info)
    {
        this.name = name; this.bgra = bgra; this.w = w; this.h = h; this.info = info;
        status.Text = $"{name}   {w} x {h}   {info}";
        Rebuild();
    }

    /// <summary>No image: a line of text in the middle instead (e.g. why a texture can't be shown).</summary>
    public void ShowMessage(string text)
    {
        bgra = null; message = text; status.Text = "";
        view?.Dispose(); view = null;
        UpdateButtons();
        LayoutCanvas();
    }

    void UpdateButtons() { save.Enabled = popOut.Enabled = bgra != null; pixel.Text = ""; }

    /// <summary>The bitmap for the chosen view: colour with alpha, one channel as grey, or colour made opaque.</summary>
    void Rebuild()
    {
        UpdateButtons();
        if (bgra == null) { LayoutCanvas(); return; }
        var v = (byte[])bgra.Clone();
        int mode = show.SelectedIndex;
        if (mode > 0)
            for (int i = 0; i < v.Length; i += 4)
            {
                switch (mode)
                {
                    case 1: v[i + 3] = 255; break;
                    case 2: v[i] = v[i + 1] = v[i + 2] = bgra[i + 3]; v[i + 3] = 255; break;
                    case 3: v[i] = v[i + 1] = v[i + 2] = bgra[i + 2]; v[i + 3] = 255; break;
                    case 4: v[i] = v[i + 1] = v[i + 2] = bgra[i + 1]; v[i + 3] = 255; break;
                    case 5: v[i] = v[i + 1] = v[i + 2] = bgra[i]; v[i + 3] = 255; break;
                }
            }
        view?.Dispose();
        view = TextureDecode.ToBitmap(v, w, h);
        LayoutCanvas();
    }

    float Scale() => fit ? Math.Min((canvas.ClientSize.Width - 8f) / w, (canvas.ClientSize.Height - 8f) / h) : zoom;

    void LayoutCanvas()
    {
        canvas.AutoScrollMinSize = fit || bgra == null ? Size.Empty : new Size((int)(w * zoom) + 8, (int)(h * zoom) + 8);
        canvas.Invalidate();
    }

    Rectangle ImageRect()
    {
        float s = Math.Max(0.01f, Scale());
        int dw = (int)(w * s), dh = (int)(h * s);
        int x = fit ? Math.Max(4, (canvas.ClientSize.Width - dw) / 2) : 4 + canvas.AutoScrollPosition.X;
        int y = fit ? Math.Max(4, (canvas.ClientSize.Height - dh) / 2) : 4 + canvas.AutoScrollPosition.Y;
        return new Rectangle(x, y, dw, dh);
    }

    void Draw(Graphics g)
    {
        if (view == null)
        {
            TextRenderer.DrawText(g, message, Font, canvas.ClientRectangle, Theme.Current.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }
        var r = ImageRect();
        if (show.SelectedIndex == 0)
        {
            // Checkerboard under the image, so transparency shows.
            using var light = new SolidBrush(Color.FromArgb(200, 200, 200)); using var dark = new SolidBrush(Color.FromArgb(150, 150, 150));
            const int c = 12;
            g.SetClip(r);
            for (int y = r.Top; y < r.Bottom; y += c)
                for (int x = r.Left; x < r.Right; x += c)
                    g.FillRectangle((((x - r.Left) / c + (y - r.Top) / c) & 1) == 0 ? light : dark, x, y, c, c);
            g.ResetClip();
        }
        g.InterpolationMode = Scale() >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(view, r);
    }

    void ShowPixel(Point p)
    {
        if (bgra == null) return;
        var r = ImageRect();
        if (!r.Contains(p)) { pixel.Text = ""; return; }
        int x = Math.Clamp((int)((p.X - r.Left) / (float)r.Width * w), 0, w - 1), y = Math.Clamp((int)((p.Y - r.Top) / (float)r.Height * h), 0, h - 1);
        int i = (y * w + x) * 4;
        pixel.Text = $"pixel {x}, {y}:  R {bgra[i + 2]}  G {bgra[i + 1]}  B {bgra[i]}  A {bgra[i + 3]}";
    }

    void SavePng()
    {
        if (bgra == null) return;
        using var d = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = name + ".png", OverwritePrompt = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        using var full = TextureDecode.ToBitmap(bgra, w, h);
        full.Save(d.FileName, ImageFormat.Png);
    }

    protected override void Dispose(bool disposing) { if (disposing) view?.Dispose(); base.Dispose(disposing); }
}

/// <summary>A texture in its own window (Pop out from the Textures tab's viewer).</summary>
sealed class TexturePreviewForm : Form
{
    public static Form Open(Form owner, Palette palette, string name, byte[] bgra, int w, int h, string info)
    {
        var f = new TexturePreviewForm { Text = $"Texture: {name}", Width = 1000, Height = 900, StartPosition = FormStartPosition.CenterParent, Icon = owner.Icon };
        var viewer = new TextureViewer { Dock = DockStyle.Fill };
        f.Controls.Add(viewer);
        viewer.PopOut = (n, px, ww, hh, i) => Open(owner, palette, n, px, ww, hh, i);
        viewer.ShowImage(name, bgra, w, h, info);
        Theme.Apply(f, palette);                                          // before showing (owner-drawn lists), and again for the title bar
        f.HandleCreated += (_, _) => Theme.Apply(f, palette);
        f.Show(owner);
        return f;
    }
}
