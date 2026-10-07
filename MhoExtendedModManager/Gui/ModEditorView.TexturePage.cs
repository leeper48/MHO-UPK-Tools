using System.Text.RegularExpressions;
using MhoPackageModifier;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>The Editor's texture tabs (Icons, Store Images, Achievements, Other Packages).</summary>
sealed partial class ModEditorView
{
    /// <summary>
    /// One texture view's replacements: stock textures (searchable, with preview) ← .dds files (with preview and size check).
    /// Views 0 icons, 1 achievement icons, 2 store images (MHModManager's three packages), 3 "more icon packages": any other
    /// stock ICO__ package (Silver Surfer's, HD, character select, …), picked from a list and saved in the manifest's
    /// ExtraIconReplacements extension (MHModManager ignores it; see ExtraIconReplacement).
    /// </summary>
    sealed class TexturePage : UserControl
    {
        readonly ModEditorView f;
        readonly int view;
        readonly TextBox search = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
        readonly NameList names = new() { Dock = DockStyle.Fill, Font = Ui.Regular(9.5f) };
        readonly DropDown packagePick = new() { Dock = DockStyle.Fill };
        readonly DataGridView rows;
        readonly PictureBox stockPic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24) };
        readonly PictureBox newPic = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(22, 22, 24) };
        readonly Label stockInfo = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 0) }, newInfo = new() { AutoSize = true, Tag = "subtle", Padding = new Padding(0, 4, 0, 0) };
        List<TexEntry> all = [];
        bool loaded;
        // Costume filter from the Packages tab (Kurt: open on the costume of the selected package when there is one).
        CostumeFilter? costume;
        string? dismissed;   // the costume the user chose Show All for
        readonly Label costumeLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.Regular(8.75f) };
        readonly Button showAll;
        readonly FlowLayoutPanel costumeRow = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4), Visible = false };   // Show All goes under a long label (it was cut off)
        int thumbRequest;
        bool Extra => view == 3;

        /// <summary>This view's replacements (package file, texture, .dds source).</summary>
        IEnumerable<(string File, string Texture, string Source)> ViewRows() => Extra
            ? f.draft.Extra.Select(x => (x.Package, x.Texture, x.Source))
            : f.draft.Textures[view].Select(r => (Applier.IconPackages[view].File, r.Texture, r.Source));

        public TexturePage(ModEditorView f, int view)
        {
            this.f = f; this.view = view; Dock = DockStyle.Fill;
            float s = f.S;
            rows = Ui.Grid(s, true, ("Texture", 0), ("Replacement .DDS", 240), ("Check", 330));
            rows.Tag = "keepselection";

            var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = (int)(360 * s), ColumnCount = 1, RowCount = 4, Padding = new Padding(0, 0, 8, 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(new Label { Text = Extra ? "STOCK TEXTURES  ·  pick an icon package" : $"STOCK TEXTURES  ·  {Applier.IconPackages[view].File}", AutoSize = true, MaximumSize = new Size((int)(350 * s), 0), Tag = "subtle", Font = Ui.Bold(8.5f), Padding = new Padding(0, 8, 0, 4) }, 0, 0);
            if (Extra) { packagePick.Margin = new Padding(0, 0, 0, 6); left.Controls.Add(packagePick, 0, 1); }
            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.Controls.Add(new Label { Text = "Find", AutoSize = true, Anchor = AnchorStyles.Left, Tag = "subtle", Padding = new Padding(0, 0, 4, 0) }, 0, 0);
            searchRow.Controls.Add(search, 1, 0);
            var searchBlock = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            searchBlock.Controls.Add(searchRow, 0, 0);
            showAll = Ui.FlatButton("Show All", () => { dismissed = costume?.Label; costume = null; costumeRow.Visible = false; Filter(); }, tip: "Show every texture again, not only the selected package's costume.");
            showAll.Padding = new Padding(4, 0, 4, 0); showAll.Font = Ui.Regular(8.5f);
            costumeRow.MaximumSize = new Size((int)(348 * s), 0);   // the column's width, so the row wraps instead of running off
            costumeRow.Controls.AddRange([costumeLabel, showAll]);
            searchBlock.Controls.Add(costumeRow, 0, 1);
            left.Controls.Add(searchBlock, 0, 2);
            left.Controls.Add(names, 0, 3);

            var previews = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 8, 0, 4), Margin = new Padding(0) };
            previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); previews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            previews.Controls.Add(Ui.CardPanel("ORIGINAL", stockPic, stockInfo), 0, 0);
            previews.Controls.Add(Ui.CardPanel("REPLACEMENT", newPic, newInfo), 1, 0);

            // Right: hint, buttons, then the replacements table over the two previews (55 / 45).
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            string hintText = "Pick the stock texture on the left (search, then click), then load its replacement (Load .DDS/.PNG: a .DDS in DXT1, or DXT5 for soft alpha, or a .PNG / .JPG converted to match the original) or make one from a character (Create from 3D). Double-click a name to load straight away.";
            if (Extra) hintText += "  These packages are an extension: the old MHModManager installs the mod but skips these images.";
            var hint = new Label { Text = hintText, AutoSize = true, Tag = "subtle", Padding = new Padding(2, 8, 2, 2), Dock = DockStyle.Fill };
            right.Controls.Add(hint, 0, 0);
            var tools = Toolbar(Ui.AccentButton("Load .DDS/.PNG", ChooseDds, tip: "Load the replacement for the texture selected on the left, or a new file for the replacement selected in the table (double-click a row does the same): a .DDS, or a .PNG / .JPG converted to match the original."),
                Ui.AccentButton("Create from 3D", CreateFrom3D, tip: "Make the replacement for the selected texture from a character in 3D: frame it, take a snapshot at the texture's size (store images, hero portraits, costume icons)."),
                Ui.FlatButton("Remove Replacement", RemoveRow, tip: "Take the selected replacements out of the mod."), Ui.FlatButton("Export Original", SaveOriginal, tip: "Save (as .DDS or .PNG) the game's original of the selected texture, as a starting point for your replacement."));
            tools.Dock = DockStyle.Fill;
            right.Controls.Add(tools, 0, 1);
            right.Controls.Add(rows, 0, 2);
            right.Controls.Add(previews, 0, 3);
            right.Resize += (_, _) => hint.MaximumSize = new Size(Math.Max(100, right.Width - 10), 0);
            // Two views of the selected texture (Kurt: the 3D creator in the Editor tab, not a popup): its replacement,
            // or Create from 3D (built the first time it's opened).
            sides.Add("Replacements", right);
            sides.Add("Create from 3D", creatorHost);
            sides.SelectedChanged += i => { if (i == 1) ShowCreator(); };
            Controls.Add(sides); Controls.Add(left);

            // Click a preview to look closer (Kurt: 1:1, full screen, zoom).
            // Export names say what each is: the game's original, or the replacement .DDS decoded (to compare with a snapshot's PNG).
            static string First(string info) => info.Split("  ·  ")[0].Trim();
            ImageViewerForm.Attach(stockPic, () => (stockPic.Image, "Original: " + stockInfo.Text, First(stockInfo.Text) + "_original"));
            ImageViewerForm.Attach(newPic, () => (newPic.Image, "Replacement: " + newInfo.Text, Path.GetFileNameWithoutExtension(newInfo.Text.Split("  ·  ").Select(x => x.Trim()).FirstOrDefault(x => x.EndsWith(".dds", StringComparison.OrdinalIgnoreCase), First(newInfo.Text))) + "_dds"));
            search.TextChanged += (_, _) => Filter();
            MhoPackageModifier.Gui.SearchBox.AddClear(search);
            names.SelectedIndexChanged += (_, _) => { if (names.SelectedItem is TexEntry e) { ShowStock(e.File, e.Name); if (sides.SelectedIndex == 1) ShowCreator(); } };
            names.DoubleClick += (_, _) => ChooseDds();
            // A replacement row: selecting it clears the left's pick, so Load / Export act on it; double-click loads a new file.
            rows.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && rows.Rows[e.RowIndex].Tag is (string, string, string)) { names.ClearSelected(); ChooseDds(); } };
            rows.SelectionChanged += (_, _) => { if (rows.SelectedRows.Count == 1 && rows.SelectedRows[0].Tag is (string file, string t, string src)) { if (rows.Focused) names.ClearSelected(); ShowStock(file, t); ShowNew(src); if (sides.SelectedIndex == 1) ShowCreator(); } };
            packagePick.SelectedIndexChanged += async (_, _) => await LoadNames();
            VisibleChanged += (_, _) => { if (Visible && loaded) ApplyCostume(); };
            VisibleChanged += async (_, _) =>
            {
                if (!Visible || loaded) return;
                loaded = true;
                if (f.catalog == null || f.game == null) { names.Items.Add("(game folder not set)"); return; }
                if (Extra)
                {
                    foreach (string p in IconCapture.ExtraPackages(f.game)) packagePick.Items.Add(p);
                    // Start on the package of the first existing replacement, else Silver Surfer's (the reason this exists), else the first.
                    string? first = f.draft.Extra.Select(x => x.Package).FirstOrDefault() ?? packagePick.Items.Cast<string>().FirstOrDefault(p => p.Contains("SilverSurfer", StringComparison.OrdinalIgnoreCase));
                    packagePick.SelectedItem = first != null && packagePick.Items.Contains(first) ? first : packagePick.Items.Count > 0 ? packagePick.Items[0] : null;
                }
                else await LoadNames();
                RefreshRows();
                if (rows.Rows.Count > 0) { rows.ClearSelection(); rows.Rows[0].Selected = true; }   // shows both previews
            };
            RefreshRows();
        }

        string? CurrentPackage => Extra ? packagePick.SelectedItem as string : Applier.IconPackages[view].File;

        readonly FlatTabs sides = new() { Dock = DockStyle.Fill };
        readonly Panel creatorHost = new() { Dock = DockStyle.Fill };
        IconCreatorView? creator;
        string? creatorTarget;

        /// <summary>The 3D creator on the texture selected on the left (its size, the original for the overlay, its setup).</summary>
        void ShowCreator()
        {
            if (creator == null)
            {
                creator = new IconCreatorView(() => f.draft.Packages.Where(p => File.Exists(p.Source)).Select(p => (p.File, p.Source)), f.game?.Cooked);
                creator.Use += (tex, png, keepSize) => { if ((all.FirstOrDefault(x => x.Name.Equals(tex, StringComparison.OrdinalIgnoreCase)) ?? Selected()) is TexEntry te) { UseFile(te, png, keepSize); sides.Select(0); } };
                creatorHost.Controls.Add(creator);
                Theme.ApplyTree(creator, Palette.Dark);
                Ui.Restyle(creator);
            }
            if (Selected() is not TexEntry e) return;
            if (creatorTarget == e.File + "|" + e.Name) { creator.Activated(); return; }
            if (f.catalog?.Size(e.File, e.Name) is not { } size) { Dialog.Show(this, "The original's size isn't known: set the game folder first (the snapshot is made at the original's size).", "Textures"); return; }
            creatorTarget = e.File + "|" + e.Name;
            var p = f.catalog.Preview(e.File, e.Name);
            creator.SetTarget(f.editing?.FolderName ?? "new:" + f.draft.Name, e.Name, size.W, size.H, p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null);
        }

        async Task LoadNames()
        {
            if (f.catalog == null || CurrentPackage is not string pkg) return;
            names.Items.Clear(); names.Items.Add("Loading…");
            var t = await Task.Run(() => f.catalog.EntriesFor(pkg));
            all = t ?? [];
            if (t == null) { names.Items.Clear(); names.Items.Add("(no verified original of this package)"); return; }
            ApplyCostume();
        }

        /// <summary>Filters to the Packages tab's costume when this page has textures for it; otherwise shows everything.</summary>
        void ApplyCostume()
        {
            var c = f.SelectedCostume();
            if (c != null && (c.Label == dismissed || !all.Any(e => c.Matches(e.Name)))) c = null;
            costume = c;
            costumeRow.Visible = c != null;
            if (c != null) { costumeLabel.Text = $"Showing {c.Label} (from the selected package)"; costumeLabel.ForeColor = Ui.TagCharacter; }
            Filter();
        }

        void Filter()
        {
            string q = search.Text.Trim();
            names.BeginUpdate(); names.Items.Clear();
            foreach (var e in all.Where(e => (q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) && (costume == null || costume.Matches(e.Name))).Take(5000)) names.Items.Add(e);
            names.EndUpdate();
        }

        async void ShowStock(string file, string texture)
        {
            if (f.catalog == null) return;
            var p = await Task.Run(() => f.catalog.Preview(file, texture));
            stockPic.Image = p is { } x ? TextureDecode.ToBitmap(x.Bgra, x.W, x.H) : null;
            stockInfo.Text = p is { } y ? $"{texture}  ·  {y.W}×{y.H} {y.Format.Replace("pf_", "").Replace("PF_", "").ToUpperInvariant()}" : $"{texture}  ·  no preview";
        }

        void ShowNew(string path)
        {
            var d = TextureDecode.ReadDds(path, out string note);
            byte[]? bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
            newPic.Image = bgra != null ? TextureDecode.ToBitmap(bgra, d!.Value.W, d.Value.H) : null;
            newInfo.Text = d is { } z ? $"{Path.GetFileName(path)}  ·  {z.W}×{z.H} {z.Format.Replace("PF_", "").ToUpperInvariant()}" : $"{Path.GetFileName(path)}  ·  {note}";
        }

        /// <summary>DXT1/DXT5, sizes divisible by 4 (what Apply can write); a size other than the original's is only a warning.</summary>
        (string Text, bool Ok) Check(string file, string texture, string dds)
        {
            if (!File.Exists(dds)) return ("file missing", false);
            var img = TextureImport.ParseDds(File.ReadAllBytes(dds), out string? err);
            if (img == null) return ("can't be used: " + err, false);
            var stock = f.catalog?.Size(file, texture);
            string size = $"{img.Width}×{img.Height} {img.FourCC}, {img.Levels.Count} mip(s)";
            return stock is { } s && (s.W != img.Width || s.H != img.Height) ? ($"{size}; original is {s.W}×{s.H} (may show scaled)", false) : (size + "  ✓", true);
        }

        string? convertNote;

        void ChooseDds()
        {
            // The texture selected on the left, else the replacement selected on the right (Kurt: loading a new file for
            // an existing replacement was refused).
            if (Selected() is not TexEntry e) { Dialog.Show(this, "Select the stock texture to replace on the left, or a replacement on the right to change it.", "Textures"); return; }
            using var d = new OpenFileDialog { Title = $"Replacement for {e.Name}", Filter = "Textures and images (*.dds;*.png;*.jpg;*.jpeg;*.bmp)|*.dds;*.png;*.jpg;*.jpeg;*.bmp|DDS textures (*.dds)|*.dds|Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            UseFile(e, d.FileName);
        }

        /// <summary>
        /// Create from 3D (Kurt): the creator tab on the selected texture, with the mod's packages as the characters to
        /// choose from; its snapshot (a PNG at the original's size) goes in like a chosen PNG.
        /// </summary>
        /// <summary>The texture to make: the one selected on the left, else the replacement row selected on the right.</summary>
        TexEntry? Selected() =>
            names.SelectedItem as TexEntry
            ?? (rows.SelectedRows.Count == 1 && rows.SelectedRows[0].Tag is (string file, string t, string _) ? all.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? new TexEntry(file, t) : null);

        void CreateFrom3D()
        {
            if (Selected() is not TexEntry) { Dialog.Show(this, "Select the stock texture to make first (search on the left).", "Textures"); return; }
            sides.Select(1);
        }

        /// <summary>A replacement for a stock texture: a .dds as it is, an image converted to match the original.</summary>
        void UseFile(TexEntry e, string chosen, bool keepSize = false)
        {
            if (!chosen.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                // An image: made into a .dds like the original (size, DXT1 / DXT5); the mod gets the .dds.
                if (f.catalog == null) { Dialog.Show(this, "Set the game folder first: the original texture's size and format are needed to convert an image.", "Textures"); return; }
                // named per texture too: one image used for two textures of different sizes (an icon and a portrait) made two
                // conversions of one name, and the second overwrote the first before Save (2026-10-07)
                string stem = ModInstaller.Sanitise(Path.GetFileNameWithoutExtension(chosen));
                string outDds = Path.Combine(Settings.Home, "converted", (stem.Equals(e.Name, StringComparison.OrdinalIgnoreCase) ? stem : $"{stem}_{ModInstaller.Sanitise(e.Name)}") + ".dds");
                try { convertNote = $"{Path.GetFileName(chosen)}: " + f.catalog.ImageToDds(e.File, e.Name, chosen, outDds, keepSize); chosen = outDds; }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or System.Runtime.InteropServices.ExternalException) { Dialog.Show(this, $"{Path.GetFileName(chosen)} can't be converted: {ex.Message}", "Textures"); return; }
            }
            else convertNote = null;
            var (check, _) = Check(e.File, e.Name, chosen);
            if (check.StartsWith("can't")) { Dialog.Show(this, $"{Path.GetFileName(chosen)} {check}\n\nSave it as DXT1 (no or 1-bit alpha) or DXT5 (soft alpha), or choose a PNG and it's converted.", "Textures"); return; }
            if (Extra)
            {
                f.draft.Extra.RemoveAll(x => x.Package.Equals(e.File, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                f.draft.Extra.Add((e.File, e.Name, chosen));
            }
            else
            {
                var list = f.draft.Textures[view];
                list.RemoveAll(r => r.Texture.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
                list.Add((e.Name, chosen));
            }
            RefreshRows();
            ShowNew(chosen);
            if (convertNote != null) newInfo.Text = convertNote + "  ·  " + newInfo.Text;
        }

        /// <summary>The selected stock texture as .dds (a starting point for its replacement).</summary>
        void SaveOriginal()
        {
            if (f.catalog == null || Selected() is not TexEntry e) { Dialog.Show(this, "Select a stock texture on the left, or a replacement on the right.", "Textures"); return; }
            using var d = new SaveFileDialog { Title = $"Export Original {e.Name}", Filter = "DDS texture (*.dds)|*.dds|PNG image (*.png)|*.png", FileName = e.Name + ".dds" };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            string? why = f.catalog.ExportImage(e.File, e.Name, d.FileName);
            if (why != null) Dialog.Show(this, "Not saved: " + why, "Textures");
        }

        void RemoveRow()
        {
            foreach (DataGridViewRow r in rows.SelectedRows)
                if (r.Tag is (string file, string t, string _))
                {
                    if (Extra) f.draft.Extra.RemoveAll(x => x.Package == file && x.Texture == t);
                    else f.draft.Textures[view].RemoveAll(x => x.Texture == t);
                }
            RefreshRows();
        }

        void RefreshRows()
        {
            rows.Rows.Clear();
            var paths = new List<(string Path, DataGridViewRow Row)>();
            foreach (var (file, t, src) in ViewRows())
            {
                var (check, ok) = Check(file, t, src);
                var row = rows.Rows[rows.Rows.Add(null, Extra ? $"{t}   · {Path.GetFileNameWithoutExtension(file)}" : t, Path.GetFileName(src), check)];
                row.Tag = (file, t, src);
                row.Cells["Check"].Style.ForeColor = ok ? Ui.Enabled : Ui.Packages;
                paths.Add((src, row));
            }
            // Thumbnails of the replacements, in the background.
            int req = ++thumbRequest;
            int size = (int)(96 * f.S);
            Task.Run(() => paths.Select(p => Thumb(p.Path, size)).ToList()).ContinueWith(task =>
            {
                if (task.IsFaulted || req != thumbRequest || rows.IsDisposed) return;
                for (int i = 0; i < paths.Count; i++) if (task.Result[i] is Image img && i < rows.Rows.Count) rows.Rows[i].Cells[0].Value = img;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    static Image? Thumb(string path, int size) => Ui.DdsThumb(path, size);
}
