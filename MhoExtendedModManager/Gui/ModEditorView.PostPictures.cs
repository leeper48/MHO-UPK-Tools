namespace MhoExtendedModManager.Gui;

/// <summary>The kinds of Nexus picture the Details page makes (each kept in the post's images under its own name suffix).</summary>
enum PostPictureKind { Showcase, VisualUpdate }

/// <summary>
/// The Details page's Nexus pictures (Kurt, 2026-11-17): the showcase image (PostWriter.Showcase) and the Visual Update image
/// (VisualUpdateForm), kept in draft.PostImages (saved with the post); a cover picked or dropped in with the image editor.
/// </summary>
sealed partial class ModEditorView
{
    sealed class PostPictureUi
    {
        public PictureBox? Pic;
        public Label? Note;
        public ImageEditWatch? Edit;
    }

    readonly Dictionary<PostPictureKind, PostPictureUi> postPics = [];

    static string Suffix(PostPictureKind k) => k == PostPictureKind.Showcase ? PostWriter.ShowcaseSuffix : PostWriter.VisualUpdateSuffix;
    static string KindName(PostPictureKind k) => k == PostPictureKind.Showcase ? "Showcase Image" : "Visual Update Image";

    /// <summary>The post's picture of this kind ("… showcase.png" / "… visual update.png"), if there is one.</summary>
    string? PostPictureFile(PostPictureKind k) => draft.PostImages.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).EndsWith(Suffix(k), StringComparison.OrdinalIgnoreCase));

    Control PostPictureRow(PostPictureKind kind)
    {
        var ui = postPics[kind] = new PostPictureUi();
        string name = KindName(kind);
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var pic = ui.Pic = new PictureBox { Size = new Size((int)(240 * S), (int)(135 * S)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24), Margin = new Padding(0, 0, (int)(10 * S), 0), Cursor = Cursors.Hand };
        // a click opens the app's picture viewer (fit, 1:1, zoom, full screen), as the other pictures do (Kurt)
        ImageViewerForm.Attach(pic, () =>
        {
            if (PostPictureFile(kind) is not string f || !File.Exists(f)) return (null, "", "");
            try { return (ImageFiles.Load(f), name, Path.GetFileNameWithoutExtension(f)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { return (null, "", ""); }
        });
        pic.Paint += (_, e) =>
        {
            // an empty frame until there's one (the theme leaves the box transparent)
            if (pic.Image != null) return;
            var r = new Rectangle(0, 0, pic.Width - 1, pic.Height - 1);
            using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) e.Graphics.FillRectangle(b, r);
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) e.Graphics.DrawRectangle(pen, r);
            TextRenderer.DrawText(e.Graphics, $"No {name} Yet", Font, r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        row.Controls.Add(pic, 0, 0);
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        col.Controls.Add(Ui.FlatButton($"Make {name}", () => MakePostPicture(kind), tip: kind == PostPictureKind.Showcase
            ? "A 1920 x 1080 picture for the Nexus page: the Marvel Heroes Omega backdrop with the mod's store image on the left and room on the right for a comic cover. It goes with the mod's post (Create Post), saved with the mod."
            : "A 1920 x 1080 picture for the Nexus page: the game's store image under LAUNCH and the mod's under VISUAL UPDATE (pick others if you like), with room on the right for a comic cover. It goes with the mod's post, saved with the mod."));
        col.Controls.Add(Ui.FlatButton("Pick a Cover Image", () => PickPostCover(kind), tip: $"Put a comic cover picture (.PNG, .JPG, .WEBP …) into the {name.ToLowerInvariant()}'s cover slot."));
        col.Controls.Add(Ui.FlatButton("Open in Image Editor", () => EditPostPicture(kind), tip: $"Open the {name.ToLowerInvariant()} in your image editor (Settings ▾ → Model → Choose Image Editor) to drop in a cover or touch it up. Each save there comes back here."));
        col.Controls.Add(Ui.FlatButton("Remove", () => RemovePostPicture(kind), tip: $"Take the {name.ToLowerInvariant()} out of the mod's post."));
        ui.Note = new Label { AutoSize = true, Tag = "subtle", MaximumSize = new Size((int)(320 * S), 0), Margin = new Padding(0, (int)(4 * S), 0, 0) };
        col.Controls.Add(ui.Note);
        row.Controls.Add(col, 1, 0);
        Disposed += (_, _) => ui.Edit?.Dispose();
        ShowPostPicture(kind);
        return row;
    }

    void ShowPostPictures() { foreach (var k in postPics.Keys) ShowPostPicture(k); }

    void ShowPostPicture(PostPictureKind kind, string? note = null)
    {
        if (!postPics.TryGetValue(kind, out var ui) || ui.Pic == null) return;
        var old = ui.Pic.Image; ui.Pic.Image = null; old?.Dispose();
        string? f = PostPictureFile(kind);
        if (f != null && File.Exists(f))
            try { using var full = ImageFiles.Load(f); ui.Pic.Image = new Bitmap(full, ui.Pic.Width, ui.Pic.Width * full.Height / Math.Max(1, full.Width)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { }
        ui.Pic.Invalidate();
        if (ui.Note != null) ui.Note.Text = note ?? (f == null ? $"None yet: Make {KindName(kind)} (needs a store image)." : "In the mod's post: Create Post shows it with the other images.");
    }

    /// <summary>A copy of the picture to work on (never the one in the mod's folder: that changes only on Save).</summary>
    string WorkCopy(PostPictureKind kind, string? from)
    {
        string dir = Path.Combine(Settings.Home, "post-edit");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{ModInstaller.Sanitise(string.IsNullOrWhiteSpace(nameBox.Text) ? "Mod" : nameBox.Text.Trim())}{Suffix(kind)}.png");
        if (from != null && !from.Equals(file, StringComparison.OrdinalIgnoreCase)) File.Copy(from, file, true);
        return file;
    }

    void PutPostPicture(PostPictureKind kind, string file)
    {
        int at = draft.PostImages.FindIndex(f => Path.GetFileNameWithoutExtension(f).EndsWith(Suffix(kind), StringComparison.OrdinalIgnoreCase));
        if (at >= 0) draft.PostImages[at] = file;
        else draft.PostImages.Insert(kind == PostPictureKind.Showcase || PostPictureFile(PostPictureKind.Showcase) == null ? 0 : 1, file);   // (showcase first, then the visual update)
        draft.PostImages = [.. draft.PostImages];   // (a new list: the unsaved-changes check sees it)
    }

    void MakePostPicture(PostPictureKind kind)
    {
        Collect();
        string name = KindName(kind);
        if (PostPictureFile(kind) != null && Dialog.Show(this, $"Make a new {name.ToLowerInvariant()}? The one the post has now is replaced.", name, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var ui = postPics[kind];
        ui.Edit?.Dispose(); ui.Edit = null;
        string? made;
        try
        {
            if (kind == PostPictureKind.Showcase) made = PostWriter.Showcase(PostWriter.From(draft), "", WorkCopy(kind, null));
            else
            {
                using var f = new VisualUpdateForm(PostWriter.From(draft), "", catalog, WorkCopy(kind, null));
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                made = f.Made;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        { Dialog.Show(this, ex.Message, $"{name} Not Made", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (made == null) { Dialog.Show(this, $"The {name.ToLowerInvariant()} is made from the mod's store image, and this mod has none.\n\nAdd one on Icons → Store Images.", "No Store Image"); return; }
        PutPostPicture(kind, made);
        ShowPostPicture(kind, "Made. Pick a Cover Image, or Open in Image Editor to drop one in. Save Changes keeps it with the mod.");
    }

    void PickPostCover(PostPictureKind kind)
    {
        if (PostPictureFile(kind) is not string f || !File.Exists(f)) { ShowPostPicture(kind, $"Make {KindName(kind)} first."); return; }
        using var d = new OpenFileDialog { Title = "Comic Cover", Filter = "Images (*.PNG;*.JPG;*.JPEG;*.BMP;*.WEBP;*.GIF)|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif" };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { string work = WorkCopy(kind, f); PostWriter.PutCover(work, ImageFiles.Usable(d.FileName)); PutPostPicture(kind, work); }
        catch (Exception ex) when (ex is IOException or OutOfMemoryException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { Dialog.Show(this, $"That picture couldn't be used ({ex.Message}).", "Cover Not Added", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        ShowPostPicture(kind, "Cover added. Open in Image Editor to touch it up.");
    }

    void EditPostPicture(PostPictureKind kind)
    {
        if (PostPictureFile(kind) is not string f || !File.Exists(f)) { ShowPostPicture(kind, $"Make {KindName(kind)} first."); return; }
        string work;
        try { work = WorkCopy(kind, f); }
        catch (IOException ex) { ShowPostPicture(kind, ex.Message); return; }
        PutPostPicture(kind, work);
        var ui = postPics[kind];
        ui.Edit?.Dispose();
        ui.Edit = ImageEditWatch.Start(this, work, () => { draft.PostImages = [.. draft.PostImages]; ShowPostPicture(kind, $"Edit taken in at {DateTime.Now:HH:mm:ss}."); }, out string note);
        ShowPostPicture(kind, note);
    }

    void RemovePostPicture(PostPictureKind kind)
    {
        if (PostPictureFile(kind) is not string f) return;
        var ui = postPics[kind];
        ui.Edit?.Dispose(); ui.Edit = null;
        draft.PostImages = [.. draft.PostImages.Where(x => x != f)];
        ShowPostPicture(kind);
    }
}
