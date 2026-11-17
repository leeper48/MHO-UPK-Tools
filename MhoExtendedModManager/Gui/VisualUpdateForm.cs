using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Make Visual Update Image (Kurt, 2026-11-17): the Visual Update template with the game's store image under LAUNCH and the
/// mod's under VISUAL UPDATE by default; either can be another of the mod's or the game's images, or a file of the user's.
/// OK writes the PNG to <see cref="OutFile"/>.
/// </summary>
sealed class VisualUpdateForm : Form
{
    sealed record Choice(string Label, Func<Bitmap?> Load);

    readonly List<Choice> choices = [];
    readonly Dictionary<int, Image?> thumbs = [];
    readonly DropDown beforeBox = new() { Width = 420 }, afterBox = new() { Width = 420 };
    readonly PictureBox preview = new() { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 22, 24) };
    readonly string outFile;
    const string FileChoice = "Choose a File";

    public string? Made { get; private set; }

    public VisualUpdateForm(PostWriter.Source source, string modFolder, StockCatalog? catalog, string outFile)
    {
        this.outFile = outFile;
        Text = "Visual Update Image";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = Ui.Dpi(DeviceDpi);

        // the mod's store images, then the game's originals of them; the mod's portraits and costume icons after
        var m = source.Manifest;
        int firstMod = -1, firstGame = -1;
        foreach (var r in m.StoreReplacements)
        {
            string file = Path.Combine(modFolder, r.DdsFileName), tex = r.TextureName ?? "";
            if (firstMod < 0) firstMod = choices.Count;
            choices.Add(new($"{tex}  ·  From the Mod", () => PostWriter.Picture(file)));
            if (catalog != null)
            {
                if (firstGame < 0) firstGame = choices.Count;
                choices.Add(new($"{tex}  ·  The Game's Image", () =>
                {
                    lock (Ui.StockLock)
                        return catalog.Preview(Applier.IconPackages[2].File, tex) is { } p ? MhoPackageModifier.TextureDecode.ToBitmap(p.Bgra, p.W, p.H) : null;
                }));
            }
        }
        foreach (var r in m.Replacements.Where(r => (r.TextureName ?? "").StartsWith("herohor_", StringComparison.OrdinalIgnoreCase) || (r.TextureName ?? "").StartsWith("costume", StringComparison.OrdinalIgnoreCase)))
        {
            string file = Path.Combine(modFolder, r.DdsFileName);
            choices.Add(new($"{r.TextureName}  ·  From the Mod", () => PostWriter.Picture(file)));
        }

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label { Text = "Visual Update Image", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 8) };
        t.Controls.Add(title, 0, 0); t.SetColumnSpan(title, 2);
        t.Controls.Add(new Label { Text = "LAUNCH (before)", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Margin = new Padding(0, 0, 10, 6) }, 0, 1);
        t.Controls.Add(beforeBox, 1, 1);
        t.Controls.Add(new Label { Text = "VISUAL UPDATE (after)", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Margin = new Padding(0, 0, 10, 6) }, 0, 2);
        t.Controls.Add(afterBox, 1, 2);
        preview.Margin = new Padding(0, 8, 0, 8);
        t.Controls.Add(preview, 0, 3); t.SetColumnSpan(preview, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, tip: "Close without making the picture (Esc)."));
        var ok = Ui.AccentButton("Make Image", MakeIt, tip: "Make the picture as shown and add it to the mod's post (Enter).");
        buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, 4); t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        foreach (var box in new[] { beforeBox, afterBox })
        {
            box.Thumb = Thumb;
            Ui.Tip(box, "The picture for this panel: the mod's or the game's images, or Choose a File for one of your own (.PNG, .JPG, .WEBP …).");
            box.SelectedIndexChanged += (_, _) => Picked(box);
        }
        Fill();
        beforeBox.SelectedIndex = firstGame >= 0 ? firstGame : choices.Count > 0 ? 0 : -1;
        afterBox.SelectedIndex = firstMod >= 0 ? firstMod : choices.Count > 0 ? 0 : -1;
        AcceptButton = ok;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 900, 760);
        Compose();
        FormClosed += (_, _) => { foreach (var i in thumbs.Values) i?.Dispose(); preview.Image?.Dispose(); };
    }

    int lastBefore = -1, lastAfter = -1;

    void Fill()
    {
        foreach (var box in new[] { beforeBox, afterBox })
        {
            int keep = box.SelectedIndex;
            box.Items.Clear();
            foreach (var c in choices) box.Items.Add(c.Label);
            box.Items.Add(FileChoice);
            if (keep >= 0 && keep < choices.Count) box.SelectedIndex = keep;
        }
    }

    Image? Thumb(int i)
    {
        if (i < 0 || i >= choices.Count) return null;
        if (thumbs.TryGetValue(i, out var img)) return img;
        using var full = choices[i].Load();
        return thumbs[i] = full == null ? null : new Bitmap(full, Math.Max(1, full.Width * 64 / Math.Max(1, full.Height)), 64);
    }

    void Picked(DropDown box)
    {
        if (box.SelectedIndex == choices.Count)
        {
            // Choose a File: added to both lists, picked in this one
            using var d = new OpenFileDialog { Title = "A Picture for This Panel", Filter = "Images (*.PNG;*.JPG;*.JPEG;*.BMP;*.WEBP;*.GIF;*.DDS)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.dds" };
            int back = box == beforeBox ? lastBefore : lastAfter;
            if (d.ShowDialog(this) != DialogResult.OK) { box.SelectedIndex = back; return; }
            string file = d.FileName;
            choices.Add(new($"{Path.GetFileName(file)}  ·  Your File", () => PostWriter.Picture(file)));
            int other = box == beforeBox ? afterBox.SelectedIndex : beforeBox.SelectedIndex;
            Fill();
            (box == beforeBox ? afterBox : beforeBox).SelectedIndex = other;
            box.SelectedIndex = choices.Count - 1;
            return;
        }
        if (box == beforeBox) lastBefore = box.SelectedIndex; else lastAfter = box.SelectedIndex;
        Compose();
    }

    Bitmap? Build()
    {
        using var before = beforeBox.SelectedIndex >= 0 && beforeBox.SelectedIndex < choices.Count ? choices[beforeBox.SelectedIndex].Load() : null;
        using var after = afterBox.SelectedIndex >= 0 && afterBox.SelectedIndex < choices.Count ? choices[afterBox.SelectedIndex].Load() : null;
        return PostWriter.VisualUpdateImage(before, after);
    }

    void Compose()
    {
        var old = preview.Image;
        using (var full = Build()) preview.Image = full == null ? null : new Bitmap(full, 960, 540);
        old?.Dispose();
    }

    void MakeIt()
    {
        try
        {
            using var full = Build();
            if (full == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            full.Save(outFile, System.Drawing.Imaging.ImageFormat.Png);
            Made = outFile;
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Dialog.Show(this, ex.Message, "Picture Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>Tests: the picture with the default choices, without showing the window.</summary>
    public static string? MakeDefault(PostWriter.Source source, string modFolder, StockCatalog? catalog, string outFile)
    {
        using var f = new VisualUpdateForm(source, modFolder, catalog, outFile);
        f.MakeIt();
        return f.Made;
    }
}
