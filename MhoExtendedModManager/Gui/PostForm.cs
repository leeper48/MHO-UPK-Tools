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
    readonly StockCatalog? catalog;   // the game's store images for Make Visual Update Image
    readonly string tempImages = Path.Combine(Path.GetTempPath(), "mhoextmm_post_" + Guid.NewGuid().ToString("N")[..8]);
    string? selectedImage;
    bool dirty;

    static TextBox Box() => new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, AcceptsReturn = true, Font = Ui.Regular(9.5f) };

    /// <param name="modFolder">Where the source's .dds names resolve ("" for a draft, whose names are full paths).</param>
    /// <param name="saved">The post saved in the mod (null texts: none saved).</param>
    /// <param name="save">Keeps the post: into the mod's folder, or into the draft (saved with the mod).</param>
    /// <param name="keptWithDraft">True for a mod being created or edited (the post is saved with it).</param>
    public PostForm(PostWriter.Source source, string modFolder, (string? Nexus, string? Discord, List<string> Images) saved,
                    Action<string?, string?, List<string>> save, bool keptWithDraft = false, StockCatalog? catalog = null)
    {
        this.catalog = catalog;
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
        // each box on a page of its own (Kurt, 2026-11-17: the Discord tab showed nothing): the tabs show and hide their page,
        // and a box's rounded frame (Modern.Modernize) only follows its own parent
        static Panel PageOf(TextBox box) { var page = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0) }; page.Controls.Add(box); return page; }
        SpellCheck.Attach(nexus); SpellCheck.Attach(discord);
        tabs.Add("Nexus", PageOf(nexus));
        tabs.Add("Discord", PageOf(discord));
        t.Controls.Add(tabs, 0, 2);

        // Images: the post's pictures, in the order they'll be uploaded.
        var imgHead = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4) };
        imgHead.Controls.Add(new Label { Text = "IMAGES", AutoSize = true, Font = Ui.Bold(8.5f), Tag = "subtle", Margin = new Padding(0, 6, 8, 0) });
        imgHead.Controls.Add(Ui.FlatButton("Add Screenshots", AddScreenshots, tip: "Add pictures (.PNG, .JPG) to the post, e.g. in-game screenshots of the mod."));
        imgHead.Controls.Add(Ui.FlatButton("Add Mod Images", AddModImages, tip: "Add the mod's own store image, costume icons and hero portraits as .PNG."));
        imgHead.Controls.Add(Ui.FlatButton("Make Showcase Image", MakeShowcase, tip: "A 1920 x 1080 picture for the Nexus page: the Marvel Heroes Omega backdrop with the mod's store image on the left and room on the right for a comic cover (pick one, or drop it in with your image editor)."));
        imgHead.Controls.Add(Ui.FlatButton("Make Visual Update Image", MakeVisualUpdate, tip: "A 1920 x 1080 picture for the Nexus page: the game's store image under LAUNCH and the mod's under VISUAL UPDATE (pick others if you like), with room on the right for a comic cover."));
        imgHead.Controls.Add(Ui.FlatButton("Open in Image Editor", () => EditImage(selectedImage), tip: "Open the selected picture in your image editor (Settings ▾ → Model → Choose Image Editor). Each save there comes back into the post."));
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
        editDelay.Tick += (_, _) => { editDelay.Stop(); if (!IsDisposed) { dirty = true; ShowImages(); Status($"Edit Taken In {DateTime.Now:HH:mm:ss}", Ui.Enabled); } };
        FormClosed += (_, _) => { watcher?.Dispose(); editDelay.Dispose(); };
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
            Ui.ForgetOnDispose(pic);
            strip.Controls.Add(pic);
        }
        if (images.Count == 0) strip.Controls.Add(new Label { Text = "No images yet: Add Screenshots, Add Mod Images, Make Showcase Image or Make Visual Update Image.", AutoSize = true, Tag = "subtle", ForeColor = Ui.Subtle, Margin = new Padding(4, 8, 0, 0) });
    }

    void AddScreenshots()
    {
        using var d = new OpenFileDialog { Title = "Add Screenshots", Filter = "Images (*.PNG;*.JPG;*.JPEG;*.BMP;*.WEBP;*.GIF)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (string pick in d.FileNames)
        {
            string f;
            try { f = ImageFiles.Usable(pick); }   // (a .webp: its PNG copy)
            catch (IOException ex) { Dialog.Show(this, ex.Message, "Picture Not Read", MessageBoxButtons.OK, MessageBoxIcon.Error); continue; }
            if (!images.Contains(f, StringComparer.OrdinalIgnoreCase)) images.Add(f);
        }
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
        PostWriter.AddBanner(folder);   // the Marvel Heroes Omega banner for the top of the page (Kurt)
        System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        Status($"Exported {images.Count} Image(s)", Ui.Enabled);
    }

    // ---- showcase image and the image editor

    void MakeShowcase()
    {
        Directory.CreateDirectory(tempImages);
        string file = NewFile(PostWriter.ShowcaseSuffix);
        string? made;
        try { made = PostWriter.Showcase(source, modFolder, file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { Dialog.Show(this, ex.Message, "Showcase Image Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (made == null) { Dialog.Show(this, "The showcase image is made from the mod's store image, and this mod has none.\n\nAdd one on the Editor's Icons → Store Images tab.", "No Store Image"); return; }
        Added(made, "The showcase image is made: the mod's store image on the left, room on the right for a comic cover.", "Showcase Image");
    }

    /// <summary>A new file in the post's own folder whose name ends in the picture kind's suffix.</summary>
    string NewFile(string suffix)
    {
        Directory.CreateDirectory(tempImages);
        string stem = ModInstaller.Sanitise(modName) + suffix;
        string file = Path.Combine(tempImages, stem + ".png");
        // (a number goes before the suffix, so the name still ends in it)
        for (int i = 2; File.Exists(file) || images.Contains(file, StringComparer.OrdinalIgnoreCase); i++) file = Path.Combine(tempImages, $"{ModInstaller.Sanitise(modName)} ({i}){suffix}.png");
        return file;
    }

    void MakeVisualUpdate()
    {
        string? made;
        try
        {
            using var f = new VisualUpdateForm(source, modFolder, catalog, NewFile(PostWriter.VisualUpdateSuffix));
            if (f.ShowDialog(this) != DialogResult.OK) return;
            made = f.Made;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { Dialog.Show(this, ex.Message, "Visual Update Image Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (made != null) Added(made, "The visual update image is made: the two looks side by side, room on the right for a comic cover.", "Visual Update Image");
    }

    /// <summary>A made picture into the post (first), then the cover question.</summary>
    void Added(string made, string text, string caption)
    {
        images.Insert(0, made); selectedImage = made; dirty = true;
        ShowImages();
        int a = Dialog.Choose(this, text + "\n\nAdd the cover now?", caption, "Pick a Cover Image", "Open in Image Editor", "Later");
        if (a == 0)
        {
            using var d = new OpenFileDialog { Title = "Comic Cover", Filter = "Images (*.PNG;*.JPG;*.JPEG;*.BMP;*.WEBP;*.GIF)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { PostWriter.PutCover(made, ImageFiles.Usable(d.FileName)); }
            catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException or System.Runtime.InteropServices.ExternalException)
            { Dialog.Show(this, $"That picture couldn't be read ({ex.Message}). Try a .PNG or .JPG, or drop it in with your image editor.", "Cover Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            ShowImages();
            Status("Cover Added: Open in Image Editor to Touch It Up", Ui.Enabled);
        }
        else if (a == 1) EditImage(made);
    }

    FileSystemWatcher? watcher;
    string? editFile;
    readonly System.Windows.Forms.Timer editDelay = new() { Interval = 600 };

    /// <summary>Opens a post picture in the image editor; each save there shows in the strip. A picture that isn't the post's
    /// own copy (a screenshot from elsewhere) is copied first, so the original file isn't changed.</summary>
    void EditImage(string? file)
    {
        if (file == null || !File.Exists(file)) { Status("Click a Picture to Select It First", Ui.Subtle); return; }
        string? exe = MhoExtendedModManager.Model.ImageEditor.Find();
        if (exe == null)
        {
            Dialog.Show(this, "No image editor was found (GIMP, Photoshop, Corel PHOTO-PAINT, Affinity Photo, Krita or Paint.NET under Program Files).\n\nTo pick your editor's program: Settings ▾ → Model → Choose Image Editor → Another Program.", "No Image Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!Path.GetFullPath(file).StartsWith(Path.GetFullPath(tempImages) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(tempImages);
            string copy = Path.Combine(tempImages, Path.GetFileName(file));
            for (int i = 2; File.Exists(copy); i++) copy = Path.Combine(tempImages, $"{Path.GetFileNameWithoutExtension(file)} ({i}){Path.GetExtension(file)}");
            File.Copy(file, copy);
            int at = images.IndexOf(file);
            if (at >= 0) images[at] = copy; else images.Add(copy);
            file = copy; selectedImage = copy; dirty = true;
            ShowImages();
        }
        watcher?.Dispose();
        editFile = file;
        watcher = new FileSystemWatcher(tempImages, "*.*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
        void Changed(object? _, FileSystemEventArgs e)
        {
            if (!string.Equals(e.FullPath, editFile, StringComparison.OrdinalIgnoreCase)) return;
            try { BeginInvoke(() => { editDelay.Stop(); editDelay.Start(); }); } catch (InvalidOperationException) { }
        }
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Renamed += (o, e) => Changed(o, e);
        string name = MhoExtendedModManager.Model.ImageEditor.Describe(exe);
        if (MhoExtendedModManager.Model.ImageEditor.Open(exe, file) is string failed) { Status($"{name} Didn't Start: {failed}", Ui.Subtle); return; }
        bool gimp = name.StartsWith("GIMP", StringComparison.OrdinalIgnoreCase);
        Status($"Editing in {name}: Save There Over the Same File{(gimp ? " (GIMP: Ctrl+E)" : "")}", Ui.Subtle);
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
