using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Move to Another Costume → Another Hero (Kurt, 2026-09-29): pick a hero from a grid of their small portraits (the game's
/// herohor icons), then one of their costumes (store images; the default in green). Only costumes that can take a moved
/// model are listed (their own package, not the hero's base package). Returns the chosen costume.
/// </summary>
sealed class HeroPickerForm : Form
{
    public Costume? Chosen { get; private set; }

    readonly FlowLayoutPanel heroes = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly FlowLayoutPanel costumesPanel = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly Label costumeTitle = new() { AutoSize = true, Font = Ui.Bold(10f), Margin = new Padding(0, 0, 0, 6) };
    readonly TextBox filter = new() { Width = 220 };
    readonly List<(string Name, List<Costume> Costumes, Button Tile)> list = [];
    readonly StockCatalog? catalog;
    readonly float s;

    public HeroPickerForm(Mod mod, Costume source, List<Costume> all, string cooked, StockCatalog? catalog)
    {
        this.catalog = catalog;
        Text = "Move to Another Hero";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(12);
        s = DeviceDpi / 96f;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        head.Controls.Add(new Label { Text = $"Move \"{mod.Name}\" to:", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 2, 16, 0) });
        head.Controls.Add(new Label { Text = "Filter", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 6, 0) });
        head.Controls.Add(filter);
        Ui.Tip(filter, "Show only heroes whose name has these letters.");
        root.Controls.Add(head, 0, 0); root.SetColumnSpan(head, 2);
        root.Controls.Add(heroes, 0, 1);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        costumeTitle.Text = "Pick a hero";
        right.Controls.Add(costumeTitle, 0, 0);
        right.Controls.Add(costumesPanel, 0, 1);
        root.Controls.Add(right, 1, 1);
        var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, "Change nothing (Esc).");
        bar.Controls.Add(cancel);
        bar.Controls.Add(new Label { Text = "The model and voice move; the hero's own animations and powers stay.", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 12, 0) });
        root.Controls.Add(bar, 0, 2); root.SetColumnSpan(bar, 2);
        Controls.Add(root);
        CancelButton = cancel;

        // Heroes with a costume a model can move to (its own package in the game), other than the mod's own hero.
        foreach (var g in all.Where(c => c.Hero != null && c.Hero != source.Hero && !CostumeMove.IsBase(c) && File.Exists(Path.Combine(cooked, c.Package)))
                             .GroupBy(c => c.Hero!, StringComparer.OrdinalIgnoreCase))
        {
            string id = Path.GetFileNameWithoutExtension(g.Key.Replace('\\', '/').Split('/')[^1]);
            string name = AutoTags.DisplayName(id) ?? id;
            var costumes = g.GroupBy(c => c.Class, StringComparer.OrdinalIgnoreCase).Select(x => x.OrderByDescending(c => c.IsDefault).First())
                            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var tile = Tile(name, (int)(96 * s), (int)(92 * s));
            tile.Click += (_, _) => ShowHero(name, costumes);
            Ui.Tip(tile, $"{name}: {costumes.Count} costume(s) a model can move to.");
            list.Add((name, costumes, tile));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        heroes.Controls.AddRange([.. list.Select(x => (Control)x.Tile)]);
        MhoPackageModifier.Gui.SearchBox.AddClear(filter);
        filter.TextChanged += (_, _) =>
        {
            heroes.SuspendLayout();
            foreach (var (n, _, t) in list) t.Visible = n.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            heroes.ResumeLayout();
        };

        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        Ui.FitToScreen(this, 1000, 700);
        // Portraits in the background (the hero's default costume's herohor image).
        Shown += (_, _) => Task.Run(() =>
        {
            foreach (var (_, costumes, t) in list.ToList())
            {
                var c = costumes.FirstOrDefault(x => x.IsDefault) ?? costumes[0];
                var img = Picture(c.Portrait, (int)(88 * s), (int)(66 * s));
                if (img == null || IsDisposed) continue;
                try { BeginInvoke(() => { if (!t.IsDisposed) { t.Image = img; t.Invalidate(); } }); } catch (InvalidOperationException) { return; }
            }
        });
    }

    /// <summary>The hero tiles (--hero-snapshot clicks one).</summary>
    public static IEnumerable<Button> Tiles(HeroPickerForm f) => f.list.Select(x => x.Tile);

    /// <summary>A tile: picture above the name (flat, rounded like the app's buttons).</summary>
    Button Tile(string text, int w, int h)
    {
        var b = new Button { Text = text, Width = w, Height = h, TextImageRelation = TextImageRelation.ImageAboveText, ImageAlign = ContentAlignment.TopCenter, TextAlign = ContentAlignment.BottomCenter, Margin = new Padding(3), AutoEllipsis = true, Tag = "tile" };
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Ui.Line;
        b.BackColor = Ui.Card; b.ForeColor = Ui.Text;
        return b;
    }

    Image? Picture(string? iconPath, int w, int h)
    {
        if (catalog == null || Costume.IconTexture(iconPath) is not { } t) return null;
        try
        {
            lock (Ui.StockLock)
                if (catalog.Preview(t.Package, t.Texture) is { } pv)
                {
                    using var full = MhoPackageModifier.TextureDecode.ToBitmap(pv.Bgra, pv.W, pv.H);
                    float k = Math.Min((float)w / full.Width, (float)h / full.Height);
                    var bmp = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
                    using var g = Graphics.FromImage(bmp);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
                    return bmp;
                }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
        return null;
    }

    void ShowHero(string name, List<Costume> costumes)
    {
        costumeTitle.Text = $"{name}: Pick a Costume";
        foreach (var c in costumesPanel.Controls.Cast<Control>().ToList()) c.Dispose();   // (a dispose removes it from the collection)
        costumesPanel.Controls.Clear();
        foreach (var c in costumes)
        {
            var tile = Tile(c.Title + (c.IsDefault ? " (Default)" : ""), (int)(110 * s), (int)(170 * s));
            if (c.IsDefault) tile.ForeColor = Ui.Enabled;
            var chosen = c;
            tile.Click += (_, _) => { Chosen = chosen; DialogResult = DialogResult.OK; };
            Ui.Tip(tile, $"Move the model onto {name}'s {c.Title} costume ({c.Package}).");
            costumesPanel.Controls.Add(tile);
            Task.Run(() => Picture(chosen.Store, (int)(100 * s), (int)(140 * s))).ContinueWith(t =>
            {
                if (t.Result is { } img && !IsDisposed) BeginInvoke(() => { if (!tile.IsDisposed) tile.Image = img; });
            });
        }
        Theme.ApplyTree(costumesPanel, Palette.Dark);
        foreach (Button b in costumesPanel.Controls) { b.BackColor = Ui.Card; if (b.ForeColor != Ui.Enabled) b.ForeColor = Ui.Text; }
    }
}
