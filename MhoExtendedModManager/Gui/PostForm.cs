using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Create Post: the mod's Nexus description (BBCode) and Discord message (Markdown), filled in from the mod or loaded from
/// the post saved in it, editable; its images (the mod's own store image / costume icons / portraits, and screenshots the
/// user adds). Save to Mod keeps text and images in the mod's Post\ folder (Kurt), and Export ZIP writes them out next to
/// the zip. In the style of the other popups (Dialog / Apply).
/// </summary>
sealed class PostForm : Form
{
    readonly FlatTabs tabs = new() { Dock = DockStyle.Fill };
    readonly TextBox nexus = Box(), discord = Box();
    readonly Label counter = new() { AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle" };
    readonly FlowLayoutPanel strip = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Margin = new Padding(0) };
    readonly PostWriter.Source source;
    readonly string modFolder, modName;
    readonly Action<string?, string?, List<string>> save;
    readonly List<string> images;
    readonly string tempImages = Path.Combine(Path.GetTempPath(), "mhoextmm_post_" + Guid.NewGuid().ToString("N")[..8]);
    string? selectedImage;
    bool dirty;

    static TextBox Box() => new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, AcceptsReturn = true, Font = Ui.Regular(9.5f) };

    /// <param name="modFolder">Where the source's .dds names resolve ("" for a draft, whose names are full paths).</param>
    /// <param name="saved">The post saved in the mod (null texts: none saved).</param>
    /// <param name="save">Keeps the post: into the mod's folder, or into the draft (saved with the mod).</param>
    /// <param name="keptWithDraft">True for a mod being created or edited (the post is saved with it).</param>
    public PostForm(PostWriter.Source source, string modFolder, (string? Nexus, string? Discord, List<string> Images) saved,
                    Action<string?, string?, List<string>> save, bool keptWithDraft = false)
    {
        this.source = source; this.modFolder = modFolder; this.save = save;
        images = [.. saved.Images];
        modName = string.IsNullOrWhiteSpace(source.Manifest.Name) ? "Mod" : source.Manifest.Name;
        Text = "Create Post";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(14);
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, (int)(118 * s))); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = Ui.TitleCase($"Create Post: \"{modName}\""), AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        var hint = new Label
        {
            Text = (saved.Nexus != null || saved.Discord != null ? "The post saved in the mod. " : "Filled in from the mod. ") +
                   "Edit anything, then Copy it into Nexus (the description, in BBCode) or Discord. " +
                   (keptWithDraft ? "Save to Mod keeps it with the mod when you save the mod." : "Save to Mod keeps it in the mod; Export ZIP writes it out next to the zip."),
            AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 6),
        };
        t.Controls.Add(hint, 0, 1);
        Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);   // wraps
        tabs.Add("Nexus", nexus);
        tabs.Add("Discord", discord);
        t.Controls.Add(tabs, 0, 2);

        // Images: the post's pictures, in the order they'll be uploaded.
        var imgHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4) };
        imgHead.Controls.Add(new Label { Text = "IMAGES", AutoSize = true, Font = Ui.Bold(8.5f), Tag = "subtle", Margin = new Padding(0, 6, 8, 0) });
        imgHead.Controls.Add(Ui.FlatButton("Add Screenshots", AddScreenshots, tip: "Add pictures (.PNG, .JPG) to the post, e.g. in-game screenshots of the mod."));
        imgHead.Controls.Add(Ui.FlatButton("Add Mod Images", AddModImages, tip: "Add the mod's own store image, costume icons and hero portraits as .PNG."));
        imgHead.Controls.Add(Ui.FlatButton("Remove", RemoveImage, tip: "Remove the selected picture from the post (click a picture to select it)."));
        t.Controls.Add(imgHead, 0, 3);
        var stripCard = new Panel { Dock = DockStyle.Fill, Tag = "card", Padding = new Padding(6) };
        stripCard.Controls.Add(strip);
        t.Controls.Add(stripCard, 0, 4);

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(counter, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right };
        var copy = Ui.AccentButton("Copy", Copy, tip: "Copy the text of the tab you're on, ready to paste.");
        var keep = Ui.AccentButton("Save to Mod", SaveToMod, tip: keptWithDraft ? "Keep this post (text and images) with the mod; it's written when you save the mod." : "Keep this post (text and images) in the mod's folder. Export ZIP writes it out next to the zip.");
        var export = Ui.FlatButton("Export Images", ExportImages, tip: "Save the post's pictures to a folder, to upload them.");
        var reset = Ui.FlatButton("Start Over", Fill, tip: "Fill both posts in again from the mod (your edits here are dropped).");
        var close = Ui.FlatButton("Close", CloseAsked, tip: "Close (asks first if the post has changes that aren't saved to the mod).");
        buttons.Controls.AddRange([reset, export, close, keep, copy]);
        bar.Controls.Add(buttons, 1, 0);
        t.Controls.Add(bar, 0, 5);
        Controls.Add(t);

        discord.TextChanged += (_, _) => { Count(); dirty = true; };
        nexus.TextChanged += (_, _) => { Count(); dirty = true; };
        tabs.SelectedChanged += _ => Count();
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) CloseAsked(); };
        FormClosed += (_, _) => { try { if (Directory.Exists(tempImages)) Directory.Delete(tempImages, true); } catch (IOException) { } };
        nexus.Text = Crlf(saved.Nexus ?? PostWriter.Nexus(source));
        discord.Text = Crlf(saved.Discord ?? PostWriter.Discord(source));
        dirty = false;
        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.Restyle(this);
        Ui.FitToScreen(this, 1000, 800);
        hint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - Padding.Horizontal - 10), 0);
        MinimumSize = new Size((int)(820 * s), (int)(600 * s));
        ShowImages();
        Shown += (_, _) => { nexus.SelectionLength = 0; discord.SelectionLength = 0; ActiveControl = copy; Count(); };
    }

    public void SelectTabForSnapshot(int i) { tabs.Select(i); Count(); }

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    void Fill()
    {
        nexus.Text = Crlf(PostWriter.Nexus(source));
        discord.Text = Crlf(PostWriter.Discord(source));
        Count();
    }

    /// <summary>Discord's limit, counted as Discord counts (line breaks are one character).</summary>
    void Count()
    {
        if (tabs.SelectedIndex == 1)
        {
            int n = discord.Text.Replace("\r\n", "\n").Length;
            counter.Text = $"{n:N0} / {PostWriter.DiscordLimit:N0} Characters" + (n > PostWriter.DiscordLimit ? "  ·  Too Long for Discord" : "");
            counter.ForeColor = n > PostWriter.DiscordLimit ? Ui.Warn : Ui.Subtle;
        }
        else { counter.Text = $"{nexus.Text.Replace("\r\n", "\n").Length:N0} Characters"; counter.ForeColor = Ui.Subtle; }
    }

    void Status(string text, Color c) { counter.Text = text; counter.ForeColor = c; }

    void Copy()
    {
        var box = tabs.SelectedIndex == 1 ? discord : nexus;
        Clipboard.SetText(box.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        Status((tabs.SelectedIndex == 1 ? "Discord" : "Nexus") + " Post Copied", Ui.Enabled);
    }

    // ---- images

    void ShowImages()
    {
        foreach (Control c in strip.Controls.Cast<Control>().ToList()) { strip.Controls.Remove(c); c.Dispose(); }
        float s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);
        int h = (int)(92 * s);
        foreach (string f in images)
        {
            Image? img = null;
            try { using var src = Image.FromFile(f); img = new Bitmap(src, Math.Max(1, src.Width * h / Math.Max(1, src.Height)), h); } catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException) { }
            var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Width = img?.Width ?? h, Height = h, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(2), Cursor = Cursors.Hand, Tag = f,
                                       BackColor = f == selectedImage ? Ui.Accent : Color.FromArgb(22, 22, 24) };
            pic.Click += (_, _) => { selectedImage = f; ShowImages(); };
            Ui.Tips.SetToolTip(pic, Path.GetFileName(f) + "  ·  click to select");
            strip.Controls.Add(pic);
        }
        if (images.Count == 0) strip.Controls.Add(new Label { Text = "No images yet: Add Screenshots or Add Mod Images.", AutoSize = true, Tag = "subtle", ForeColor = Ui.Subtle, Margin = new Padding(4, 8, 0, 0) });
    }

    void AddScreenshots()
    {
        using var d = new OpenFileDialog { Title = "Add Screenshots", Filter = "Images (*.PNG;*.JPG;*.JPEG;*.BMP;*.GIF)|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string f in d.FileNames) if (!images.Contains(f, StringComparer.OrdinalIgnoreCase)) images.Add(f);
        dirty = true;
        ShowImages();
    }

    void AddModImages()
    {
        List<string> made;
        try { made = PostWriter.SaveImages(source, modFolder, tempImages); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Dialog.Show(this, ex.Message, "Images Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (made.Count == 0) { Dialog.Show(this, "This mod has no store image, costume icon or hero portrait.", "No Mod Images"); return; }
        foreach (string f in made) if (!images.Any(i => Path.GetFileName(i).Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))) images.Add(f);
        dirty = true;
        ShowImages();
    }

    void RemoveImage()
    {
        if (selectedImage == null) { Status("Click a Picture to Select It First", Ui.Subtle); return; }
        images.Remove(selectedImage);
        selectedImage = null;
        dirty = true;
        ShowImages();
    }

    void ExportImages()
    {
        if (images.Count == 0) { Dialog.Show(this, "The post has no images yet: Add Screenshots or Add Mod Images first.", "No Images"); return; }
        using var d = new FolderBrowserDialog { Description = $"Folder for the images of \"{modName}\"", UseDescriptionForTitle = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        string folder = Path.Combine(d.SelectedPath, ModInstaller.Sanitise(modName) + " images");
        Directory.CreateDirectory(folder);
        foreach (string f in images) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)), true);
        System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        Status($"Exported {images.Count} Image(s)", Ui.Enabled);
    }

    // ---- keeping it

    void SaveToMod()
    {
        try { save(nexus.Text.Replace("\r\n", "\n"), discord.Text.Replace("\r\n", "\n"), [.. images]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Dialog.Show(this, ex.Message, "Post Not Saved", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        dirty = false;
        Status("Post Saved to the Mod", Ui.Enabled);
    }

    void CloseAsked()
    {
        if (dirty)
        {
            var a = Dialog.Show(this, "The post has changes that aren't saved to the mod. Save them?", "Save the Post?", MessageBoxButtons.YesNoCancel);
            if (a == DialogResult.Cancel) return;
            if (a == DialogResult.Yes) { SaveToMod(); if (dirty) return; }
        }
        DialogResult = DialogResult.OK;
    }
}
