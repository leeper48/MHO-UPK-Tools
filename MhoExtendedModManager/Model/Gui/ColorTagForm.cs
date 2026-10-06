using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Model.Gui;

/// <summary>
/// The Materials tab's Tag Colors window (Kurt, 2026-10-04): a material's color groups as swatches with their share, each
/// tagged Metal / Skin / Leather / Cloth (or Not Set), beside the color map with the pointed-at group lit up and the rest
/// dimmed, so it's clear where each group is. OK returns the tags (group color → tag).
/// </summary>
sealed class ColorTagForm : Form
{
    List<ColorTags.Group> groups = [];
    readonly int w, h;
    readonly byte[] bgra;
    int[] assign = [];
    readonly ZoomCanvas map = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 24), Cursor = Cursors.Cross };
    readonly List<float> reach = [];
    readonly List<LightSlider> reachSliders = [];
    readonly TableLayoutPanel list;
    readonly List<DropDown> pickers = [];
    readonly List<Control[]> rows = [];
    /// <summary>Colors given a group of their own (the saved tags' and double-clicked ones).</summary>
    readonly List<Color> seeds = [];
    int shown = -2, picked = -1;
    // the eyedropper (Kurt, 2026-10-04: make your own swatch): on, a click on the picture makes that color a group of its own
    Button dropper = null!;
    readonly PictureBox dropSwatch = new() { Margin = new Padding(8, 3, 6, 3), Visible = false };
    readonly Label dropHex = new() { AutoSize = true, Margin = new Padding(0, 7, 0, 0), Tag = "subtle" };
    bool dropping;
    readonly float s;

    /// <summary>The tags chosen (group color hex → tag), set when OK closes the window.</summary>
    public List<(string Color, string Tag)> Result { get; private set; } = [];

    public ColorTagForm(string material, string colorFile, IReadOnlyList<(string Color, string Tag)> current)
    {
        var (ww, hh, argb) = NormalMapGen.LoadArgb(colorFile);
        var full = new byte[ww * hh * 4];
        for (int k = 0; k < ww * hh; k++) { full[4 * k] = (byte)argb[k]; full[4 * k + 1] = (byte)(argb[k] >> 8); full[4 * k + 2] = (byte)(argb[k] >> 16); full[4 * k + 3] = (byte)(argb[k] >> 24); }
        (w, h, bgra) = ColorTags.Small(ww, hh, full);   // the copy the build finds its groups on too
        seeds.AddRange(ColorTags.Seeds(current));

        Text = $"Tag Colors: {material}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 500 * s)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var intro = new Label
        {
            Text = "What is each color group made of? The spec map is made from your tags: Metal shines most and reflects, Skin is soft with the skin mask on, Cloth is dull, Leather in between (values from Angela's own map); Glow lights up in its own color (the glow map) and is dull otherwise. Point at a group to see where it is. Click the picture to find a color's group; double-click (or the Eyedropper) to give that color a group of its own (small details such as eye whites). Reach: how far a group takes in neighboring shades. Mouse wheel zooms, right-drag moves the picture.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 10), MaximumSize = new Size((int)(760 * s), 0),
        };
        t.Controls.Add(intro, 0, 0); t.SetColumnSpan(intro, 2);
        list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoScroll = true, Margin = new Padding(0, 0, 10, 0) };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44 * s)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56 * s)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190 * s));
        list.MouseLeave += (_, _) => Show(picked);
        Modern.DarkScrollbars(list);   // (more groups than fit: a dark scrollbar, as elsewhere)
        // up and down only (the vertical bar's width made the table spill sideways): room kept for it, no sideways bar
        list.Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0);
        list.AutoScroll = false; list.HorizontalScroll.Maximum = 0; list.HorizontalScroll.Visible = false; list.HorizontalScroll.Enabled = false; list.AutoScroll = true;
        t.Controls.Add(list, 0, 1);
        t.Controls.Add(map, 1, 1);
        Ui.Tip(map, "Click a color to find its group (its row lights up and scrolls into view); double-click to give that color a group of its own, then tag it. Mouse wheel: zoom at the cursor; right-drag: move; middle-click: fit.");
        map.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || PixelAt(e.Location) is not int i) return;
            if (dropping) { SetDropping(false); SplitOff(i); }
            else if (assign[i] >= 0) Pick(assign[i]);
        };
        map.MouseDoubleClick += (_, e) => { if (!dropping && e.Button == MouseButtons.Left && PixelAt(e.Location) is int i) SplitOff(i); };
        map.MouseMove += (_, e) => { if (dropping) ShowDropColor(PixelAt(e.Location)); };
        dropper = Ui.FlatButton("Eyedropper", () => SetDropping(!dropping), tip: "Make your own swatch: turn it on, then click a color on the picture; that exact color becomes a group of its own (small details such as eye whites), ready to tag. Lit while on; Esc turns it off. Double-clicking the picture does the same.");
        var dropRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 10, 0, 0) };
        dropRow.Controls.AddRange([dropper, dropSwatch, dropHex]);
        t.Controls.Add(dropRow, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Clear All", () => { foreach (var p in pickers) p.SelectedIndex = 0; }, tip: "Sets every group back to Not Set (the Soft recipe's shine)."),
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, tip: "Close without changing the tags (Esc)."),
            Ui.AccentButton("OK", Accept, tip: "Keep these tags: the spec map is made from them (Enter)."),
        ]);
        t.Controls.Add(buttons, 1, 2);
        Controls.Add(t);
        Rebuild(current);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && dropping) { SetDropping(false); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode == Keys.Enter) Accept();
        };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1040, 680);
        Show(-1);
    }

    /// <summary>The groups found again (with the seeds) and the list refilled, keeping the tags chosen so far.</summary>
    void Rebuild(IReadOnlyList<(string Color, string Tag)> tags)
    {
        groups = ColorTags.Groups(w, h, bgra, seeds: seeds);
        reach.Clear(); reach.AddRange(ColorTags.Reaches(groups, tags));
        assign = ColorTags.Assign(w, h, bgra, groups, reach);
        list.SuspendLayout();
        foreach (var c in list.Controls.Cast<Control>().ToList()) c.Dispose();   // (rows and headings; disposed while still parented: removes them)
        rows.Clear(); pickers.Clear(); reachSliders.Clear();
        list.RowCount = groups.Count + 2;
        // column headings
        list.Controls.Add(new Label { Text = "TAG", AutoSize = true, Tag = "subtle", Font = Ui.Bold(7.5f), Margin = new Padding(0, 0, 0, 2) }, 2, 0);
        list.Controls.Add(new Label { Text = "REACH", AutoSize = true, Tag = "subtle", Font = Ui.Bold(7.5f), Margin = new Padding(6, 0, 0, 2) }, 3, 0);
        var tagged = ColorTags.TagsFor(groups, tags);
        for (int g = 0; g < groups.Count; g++)
        {
            int gi = g;
            // the swatch as an image (the dark theme repaints a panel's background)
            var swImg = new Bitmap((int)(36 * s), (int)(24 * s));
            using (var gr = Graphics.FromImage(swImg)) { gr.Clear(groups[g].Center); gr.DrawRectangle(Pens.Gray, 0, 0, swImg.Width - 1, swImg.Height - 1); }
            var sw = new PictureBox { Image = swImg, Size = swImg.Size, Margin = new Padding(0, 3, 6, 3), Cursor = Cursors.Hand };
            bool own = seeds.Any(c => c.R == groups[g].Center.R && c.G == groups[g].Center.G && c.B == groups[g].Center.B);
            var share = new Label { Text = groups[g].Share < 0.005f ? "<1 %" : $"{groups[g].Share:P0}", AutoSize = true, Margin = new Padding(0, 7, 6, 0), Tag = "subtle" };
            var dd = new DropDown { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 2, (int)(4 * s), 2) };   // (fills its column's width: no sideways scrolling)
            dd.Items.AddRange(ColorTags.Tags.Select(x => (object)ColorTags.Label(x)));
            dd.SelectedIndex = Math.Max(0, Array.IndexOf(ColorTags.Tags, ColorTags.Name(tagged[g])));
            var rs = new LightSlider { Label = "", Min = 0.25f, Max = 3f, Step = 0.05f, Value = reach[g], Home = () => 1f, Mark = 1f, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = (int)(26 * s), Margin = new Padding(6, 4, 0, 2) };
            rs.ValueChanged += () => { reach[gi] = rs.Value; Reassign(); };
            Ui.Tip(rs, "How far this group reaches: right takes in more of the neighboring shades (they leave their own groups), left keeps it to its closest shades. 100 % = as found; double-click: back to 100 %. Kept with the tag.");
            reachSliders.Add(rs);
            pickers.Add(dd);
            foreach (Control c in new Control[] { sw, share, dd, rs }) c.MouseEnter += (_, _) => Show(gi);
            Ui.Tip(sw, $"{ColorTags.Hex(groups[g].Center)}: {groups[g].Share:P1} of the map{(own ? " (a color of its own: tag it to keep it)" : "")}. Point at it to see where it is.");
            list.Controls.Add(sw, 0, g + 1); list.Controls.Add(share, 1, g + 1); list.Controls.Add(dd, 2, g + 1); list.Controls.Add(rs, 3, g + 1);
            rows.Add([sw, share, dd, rs]);
        }
        // a last empty row takes the spare height (else the last row stretched)
        list.RowStyles.Clear();
        for (int r = 0; r < groups.Count + 1; r++) list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        list.ResumeLayout();
        if (IsHandleCreated) { Theme.ApplyTree(list, Palette.Dark); Ui.Restyle(list); }
        picked = -1; shown = -2;
    }

    void SetDropping(bool on)
    {
        dropping = on;
        Ui.Lit(dropper, on);
        map.Cursor = Cursors.Cross;
        dropSwatch.Visible = on;
        dropHex.Text = on ? "Click a color on the picture" : "";
        if (on) Show(-1);
    }

    /// <summary>The eyedropper's live swatch: the color under the cursor and its hex.</summary>
    void ShowDropColor(int? texel)
    {
        if (texel is not int i || bgra[4 * i + 3] < 128) { dropHex.Text = "Click a color on the picture"; return; }
        var c = Color.FromArgb(bgra[4 * i + 2], bgra[4 * i + 1], bgra[4 * i]);
        var img = new Bitmap((int)(36 * s), (int)(24 * s));
        using (var gr = Graphics.FromImage(img)) { gr.Clear(c); gr.DrawRectangle(Pens.Gray, 0, 0, img.Width - 1, img.Height - 1); }
        var old = dropSwatch.Image; dropSwatch.Image = img; dropSwatch.Size = img.Size; old?.Dispose();
        dropHex.Text = $"{ColorTags.Hex(c)}  (in group {assign[i] + 1})";
    }

    /// <summary>The texel under a point of the picture (as zoomed and moved); null outside it.</summary>
    int? PixelAt(Point p)
    {
        var (x, y) = map.ToImage(p);
        return x >= 0 && y >= 0 && x < w && y < h ? y * w + x : null;
    }

    /// <summary>A reach moved: every texel's group again, the shares and the picture.</summary>
    void Reassign()
    {
        assign = ColorTags.Assign(w, h, bgra, groups, reach);
        var counts = new int[groups.Count]; int total = 0;
        foreach (int a in assign) if (a >= 0) { counts[a]++; total++; }
        for (int g = 0; g < rows.Count; g++)
        {
            float sh = total > 0 ? counts[g] / (float)total : 0;
            rows[g][1].Text = sh < 0.005f && counts[g] > 0 ? "<1 %" : $"{sh:P0}";
        }
        int now = shown; shown = -2; Show(now);
    }

    /// <summary>Click: the group's row lit and scrolled into view, its area lit on the picture.</summary>
    void Pick(int g)
    {
        if (g < 0 || g >= rows.Count) return;
        if (picked >= 0 && picked < rows.Count) rows[picked][1].ForeColor = Ui.Subtle;
        picked = g;
        rows[g][1].ForeColor = Ui.Accent;
        list.ScrollControlIntoView(rows[g][2]);
        pickers[g].Focus();
        shown = -2; Show(g);
    }

    /// <summary>Double-click: the color under the cursor becomes a group of its own (kept once tagged), and is picked.</summary>
    void SplitOff(int texel)
    {
        var c = Color.FromArgb(bgra[4 * texel + 2], bgra[4 * texel + 1], bgra[4 * texel]);
        if (bgra[4 * texel + 3] < 128) return;
        var keep = CurrentTags();
        if (!seeds.Any(x => x.R == c.R && x.G == c.G && x.B == c.B)) seeds.Add(c);
        Rebuild(keep);
        int g = groups.FindIndex(x => x.Center.R == c.R && x.Center.G == c.G && x.Center.B == c.B);
        if (g >= 0) Pick(g);
    }

    List<(string Color, string Tag)> CurrentTags() =>
        [.. Enumerable.Range(0, groups.Count).Where(g => pickers[g].SelectedIndex > 0).Select(g => (ColorTags.Hex(groups[g].Center), ColorTags.Compose(ColorTags.Tags[pickers[g].SelectedIndex], reach[g])))];

    void Accept()
    {
        Result = CurrentTags();
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>The color map with group <paramref name="g"/> as it is and the rest dimmed (-1: all as they are).</summary>
    void Show(int g)
    {
        if (g == shown) return;
        shown = g;
        var px = new byte[bgra.Length];
        for (int i = 0; i < assign.Length; i++)
        {
            bool lit = g < 0 || assign[i] == g;
            for (int c = 0; c < 3; c++) px[4 * i + c] = lit ? bgra[4 * i + c] : (byte)(bgra[4 * i + c] / 6);
            px[4 * i + 3] = 255;
        }
        var bmp = ImagePixels.ToBitmap(w, h, px);
        var old = map.Image; map.Image = bmp; old?.Dispose();
    }

    /// <summary>Test: group <paramref name="g"/>'s reach set (as its slider does); the number of texels in it after.</summary>
    internal int TestReach(int g, float r) { reach[g] = r; Reassign(); return assign.Count(a => a == g); }

    /// <summary>Test: the tags OK would return.</summary>
    internal List<(string Color, string Tag)> TestTags(int g, string tag) { pickers[g].SelectedIndex = Array.IndexOf(ColorTags.Tags, tag); return CurrentTags(); }

    /// <summary>The picture, zoomable (wheel, at the cursor) and movable (right-drag); texels drawn sharp when zoomed in.</summary>
    sealed class ZoomCanvas : Panel
    {
        Image? image;
        float zoom = 1, cx = -1, cy = -1;   // the image point at the view's center
        Point? dragFrom;
        public ZoomCanvas() { DoubleBuffered = true; ResizeRedraw = true; }
        public Image? Image { get => image; set { image = value; if (cx < 0 && value != null) { cx = value.Width / 2f; cy = value.Height / 2f; } Invalidate(); } }
        float Scale => image == null ? 1 : Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height) * zoom;

        public (int X, int Y) ToImage(Point p)
        {
            float k = Scale;
            return ((int)MathF.Floor((p.X - ClientSize.Width / 2f) / k + cx), (int)MathF.Floor((p.Y - ClientSize.Height / 2f) / k + cy));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (image == null) return;
            float k = Scale;
            e.Graphics.InterpolationMode = k > 1.5f ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            e.Graphics.DrawImage(image, ClientSize.Width / 2f - cx * k, ClientSize.Height / 2f - cy * k, image.Width * k, image.Height * k);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (image == null) return;
            var (bx, by) = (( e.X - ClientSize.Width / 2f) / Scale + cx, (e.Y - ClientSize.Height / 2f) / Scale + cy);
            zoom = Math.Clamp(zoom * MathF.Pow(1.25f, e.Delta / 120f), 1f, 40f);
            // the point under the cursor stays under it
            cx = bx - (e.X - ClientSize.Width / 2f) / Scale; cy = by - (e.Y - ClientSize.Height / 2f) / Scale;
            Clamp(); Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { dragFrom = e.Location; Cursor = Cursors.SizeAll; }
            else if (e.Button == MouseButtons.Middle && image != null) { zoom = 1; cx = image.Width / 2f; cy = image.Height / 2f; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragFrom is Point d)
            {
                float k = Scale;
                cx -= (e.X - d.X) / k; cy -= (e.Y - d.Y) / k; dragFrom = e.Location;
                Clamp(); Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Right) { dragFrom = null; Cursor = Cursors.Cross; } }

        void Clamp() { if (image == null) return; cx = Math.Clamp(cx, 0, image.Width); cy = Math.Clamp(cy, 0, image.Height); }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Focus(); }   // (the wheel goes to the focused control)
    }

    /// <summary>Test: double-click on texel (x, y) of the map's copy; the groups after it.</summary>
    internal List<ColorTags.Group> TestSplitOff(int x, int y) { SplitOff(y * w + x); return groups; }
}
