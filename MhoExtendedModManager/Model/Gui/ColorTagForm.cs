using MhoExtendedModManager.Gui;
using MhoPackageModifier.Gui;

namespace MhoMffImporter.Gui;

/// <summary>
/// The Materials tab's Tag Colors window (Kurt, 2026-10-04): a material's color groups as swatches with their share, each
/// tagged Metal / Skin / Leather / Cloth (or Not Set), beside the color map with the pointed-at group lit up and the rest
/// dimmed, so it's clear where each group is. OK returns the tags (group color → tag).
/// </summary>
sealed class ColorTagForm : Form
{
    readonly List<ColorTags.Group> groups;
    readonly int w, h;
    readonly byte[] bgra;
    readonly int[] assign;
    readonly PictureBox map = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 20, 24) };
    readonly List<DropDown> pickers = [];
    int shown = -2;

    /// <summary>The tags chosen (group color hex → tag), set when OK closes the window.</summary>
    public List<(string Color, string Tag)> Result { get; private set; } = [];

    public ColorTagForm(string material, string colorFile, IReadOnlyList<(string Color, string Tag)> current)
    {
        var (ww, hh, argb) = NormalMapGen.LoadArgb(colorFile);
        var full = new byte[ww * hh * 4];
        for (int k = 0; k < ww * hh; k++) { full[4 * k] = (byte)argb[k]; full[4 * k + 1] = (byte)(argb[k] >> 8); full[4 * k + 2] = (byte)(argb[k] >> 16); full[4 * k + 3] = (byte)(argb[k] >> 24); }
        (w, h, bgra) = ColorTags.Small(ww, hh, full);   // the copy the build finds its groups on too
        groups = ColorTags.Groups(w, h, bgra);
        assign = ColorTags.Assign(w, h, bgra, groups);

        Text = $"Tag Colors: {material}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = DeviceDpi / 96f;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300 * s)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var intro = new Label
        {
            Text = "What is each color group made of? The spec map is made from your tags: Metal shines most and reflects, Skin is soft with the skin mask on, Cloth is dull, Leather in between (values from Angela's own map). Point at a group to see where it is.",
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 10), MaximumSize = new Size((int)(760 * s), 0),
        };
        t.Controls.Add(intro, 0, 0); t.SetColumnSpan(intro, 2);
        var list = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Margin = new Padding(0, 0, 10, 0) };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44 * s)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56 * s)); list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var tagged = ColorTags.TagsFor(groups, current);
        for (int g = 0; g < groups.Count; g++)
        {
            int gi = g;
            // the swatch as an image (the dark theme repaints a panel's background)
            var swImg = new Bitmap((int)(36 * s), (int)(24 * s));
            using (var gr = Graphics.FromImage(swImg)) { gr.Clear(groups[g].Center); gr.DrawRectangle(Pens.Gray, 0, 0, swImg.Width - 1, swImg.Height - 1); }
            var sw = new PictureBox { Image = swImg, Size = swImg.Size, Margin = new Padding(0, 3, 6, 3), Cursor = Cursors.Hand };
            var share = new Label { Text = $"{groups[g].Share:P0}", AutoSize = true, Margin = new Padding(0, 7, 6, 0), Tag = "subtle" };
            var dd = new DropDown { Width = (int)(150 * s), Margin = new Padding(0, 2, 0, 2) };
            dd.Items.AddRange(ColorTags.Tags.Select(x => (object)ColorTags.Label(x)));
            string? tag = tagged[g];
            dd.SelectedIndex = Math.Max(0, Array.IndexOf(ColorTags.Tags, tag ?? ""));
            pickers.Add(dd);
            foreach (Control c in new Control[] { sw, share, dd }) c.MouseEnter += (_, _) => Show(gi);
            Ui.Tip(sw, $"{ColorTags.Hex(groups[g].Center)}: {groups[g].Share:P1} of the map. Point at it to see where it is.");
            list.Controls.Add(sw, 0, g); list.Controls.Add(share, 1, g); list.Controls.Add(dd, 2, g);
        }
        list.MouseLeave += (_, _) => Show(-1);
        t.Controls.Add(list, 0, 1);
        t.Controls.Add(map, 1, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Ui.FlatButton("Clear All", () => { foreach (var p in pickers) p.SelectedIndex = 0; }, tip: "Sets every group back to Not Set (the Soft recipe's shine)."),
            Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, tip: "Close without changing the tags (Esc)."),
            Ui.AccentButton("OK", Accept, tip: "Keep these tags: the spec map is made from them (Enter)."),
        ]);
        t.Controls.Add(buttons, 0, 2); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } else if (e.KeyCode == Keys.Enter) Accept(); };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 860, 640);
        Show(-1);
    }

    void Accept()
    {
        Result = [];
        for (int g = 0; g < groups.Count; g++)
            if (pickers[g].SelectedIndex > 0) Result.Add((ColorTags.Hex(groups[g].Center), ColorTags.Tags[pickers[g].SelectedIndex]));
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
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(px, 0, d.Scan0, px.Length);
        bmp.UnlockBits(d);
        var old = map.Image; map.Image = bmp; old?.Dispose();
    }
}
