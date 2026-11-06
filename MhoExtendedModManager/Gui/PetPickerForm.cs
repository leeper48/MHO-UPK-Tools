using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// Move to Another Costume → Pet (Kurt, 2026-11-06: "a swap to Pet option just like Hero and Team-Up"): the game's vanity
/// pets with their store images, then the choices for the new mod: Full Size, the pet's name in game, and turning the new
/// mod on. Create Mod (Enter) or Cancel (Esc); the work (PetMove.CreateMod) runs in MainForm after the window closes.
/// </summary>
sealed class PetPickerForm : Form
{
    public PetMove.Pet? Chosen { get; private set; }
    public bool FullSize => fullSize.Checked;
    public bool TurnOn => swap.Checked;
    /// <summary>The pet's name in game ("" = the pet's own).</summary>
    public string NameChoice => setName.Checked ? nameBox.Text.Trim() : "";

    readonly FlowLayoutPanel tiles = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TextBox filter = new() { Width = 220 };
    readonly CheckBox fullSize, swap, setName;
    readonly TextBox nameBox;
    readonly Label pickLabel = new() { AutoSize = true, Font = Ui.Bold(10f), Margin = new Padding(0, 6, 0, 4) };
    readonly Label nameNote = new() { AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
    readonly Label others = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    readonly Button create;
    readonly List<(PetMove.Pet Pet, Button Tile)> list = [];
    readonly StockCatalog? catalog;
    readonly string gameRoot;
    readonly IReadOnlyList<Mod> mods;
    readonly Mod mod;
    readonly float s;

    public PetPickerForm(Mod mod, string sourceTitle, string suggestedName, IReadOnlyList<PetMove.Pet> pets, IReadOnlyList<Mod> mods, string gameRoot, StockCatalog? catalog, bool fromPet = false)
    {
        this.mod = mod; this.mods = mods; this.gameRoot = gameRoot; this.catalog = catalog;
        Text = fromPet ? "Move to Another Pet" : "Move to a Pet";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Ui.DarkFrame(this);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        Font = Ui.Regular(9.5f);
        Padding = new Padding(12);
        s = MhoExtendedModManager.Gui.Ui.Dpi(DeviceDpi);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        head.Controls.Add(new Label { Text = $"Move \"{mod.Name}\" to {(fromPet ? "another" : "a")} pet:", AutoSize = true, Font = Ui.Bold(12f), Margin = new Padding(0, 2, 16, 0) });
        head.Controls.Add(new Label { Text = "Filter", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 6, 6, 0) });
        head.Controls.Add(filter);
        Ui.Tip(filter, "Show only pets whose name has these letters.");
        root.Controls.Add(head, 0, 0);
        root.Controls.Add(tiles, 0, 1);

        // The choices for the new mod.
        var opts = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        opts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pickLabel.Text = "Pick a pet";
        opts.Controls.Add(pickLabel, 0, 0); opts.SetColumnSpan(pickLabel, 2);
        // From another pet (Kurt, 2026-11-06): ticked = the size the source pet has now (a full-size hero stays full size).
        fullSize = new CheckBox { Text = fromPet ? $"Same Size as {sourceTitle}" : "Full Size", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 8, 0) };
        Ui.Tip(fullSize, fromPet ? $"The model at the size it has as {sourceTitle}. Unticked, it takes the new pet's own size."
            : $"The pet at {sourceTitle}'s own size. Unticked, it keeps the pet's size (most pets are shrunk to half or less).");
        opts.Controls.Add(fullSize, 0, 1); opts.SetColumnSpan(fullSize, 2);
        setName = new CheckBox { Text = "Name in Game", AutoSize = true, Checked = suggestedName.Length > 0, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        Ui.Tip(setName, "The pet's name in the inventory and tooltips. Unticked, it keeps the pet's own name.");
        nameBox = new TextBox { Text = suggestedName, Dock = DockStyle.Fill, Enabled = setName.Checked, Margin = new Padding(0, 4, 0, 0) };
        setName.CheckedChanged += (_, _) => { nameBox.Enabled = setName.Checked; if (setName.Checked) nameBox.Focus(); };
        opts.Controls.Add(setName, 0, 2); opts.Controls.Add(nameBox, 1, 2);
        nameNote.Tag = "subtle"; nameNote.MaximumSize = new Size((int)(640 * s), 0);
        opts.Controls.Add(nameNote, 1, 3);
        others.MaximumSize = new Size((int)(760 * s), 0);
        opts.Controls.Add(others, 0, 4); opts.SetColumnSpan(others, 2);
        swap = new CheckBox { Text = "Turn the new mod on", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
        Ui.Tip(swap, $"The pet shows after Apply Changes. \"{mod.Name}\" stays as it is: the costume and the pet can both be on.");
        opts.Controls.Add(swap, 0, 5); opts.SetColumnSpan(swap, 2);
        root.Controls.Add(opts, 0, 2);

        var bar = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var cancel = Ui.FlatButton("Cancel", () => { DialogResult = DialogResult.Cancel; }, "Change nothing (Esc).");
        create = Ui.AccentButton("Create Mod", () => { if (Chosen != null) DialogResult = DialogResult.OK; }, "Add the pet with this costume's model as a new mod at the top of the list; the original isn't changed (Enter).");
        create.Enabled = false;
        bar.Controls.Add(cancel); bar.Controls.Add(create);
        bar.Controls.Add(new Label { Text = fromPet ? "The model, its idle, run and fidgets, and its icon and store image move; the pet's item stays." : "The model, its idle, run and fidgets, and its icon and portrait move; the pet's item stays.", AutoSize = true, Tag = "subtle", Margin = new Padding(0, 8, 12, 0) });
        root.Controls.Add(bar, 0, 3);
        Controls.Add(root);
        AcceptButton = create; CancelButton = cancel;

        foreach (var p in pets)
        {
            var tile = new Button { Text = p.Name, Width = (int)(150 * s), Height = (int)(150 * s), TextImageRelation = TextImageRelation.ImageAboveText, ImageAlign = ContentAlignment.TopCenter, TextAlign = ContentAlignment.BottomCenter, Margin = new Padding(3), AutoEllipsis = true, Tag = "tile", FlatStyle = FlatStyle.Flat };
            tile.FlatAppearance.BorderColor = Ui.Line;
            var pet = p;
            tile.Click += (_, _) => Pick(pet);
            tile.DoubleClick += (_, _) => { Pick(pet); DialogResult = DialogResult.OK; };
            int n = mods.Count(m => m != mod && m.Manifest.UpkReplacements.Contains(p.File, StringComparer.OrdinalIgnoreCase));
            Ui.Tip(tile, $"{p.Name} ({p.File})" + (n > 0 ? $": {n} other mod(s) change this pet" : ""));
            list.Add((p, tile));
        }
        tiles.Controls.AddRange([.. list.Select(x => (Control)x.Tile)]);
        MhoPackageModifier.Gui.SearchBox.AddClear(filter);
        filter.TextChanged += (_, _) =>
        {
            tiles.SuspendLayout();
            foreach (var (p, t) in list) t.Visible = p.Name.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            tiles.ResumeLayout();
        };

        Theme.Apply(this, Palette.Dark); Modern.Modernize(this);
        Ui.RestyleButtons(this);
        foreach (var (_, t) in list) { t.BackColor = Ui.Card; t.ForeColor = Ui.Text; }
        Ui.FitToScreen(this, 1000, 760);
        Shown += (_, _) => Task.Run(() =>
        {
            foreach (var (p, t) in list.ToList())
            {
                var img = Picture(p.Store ?? p.Icon, (int)(140 * s), (int)(106 * s));
                if (img == null || IsDisposed) continue;
                try { BeginInvoke(() => { if (!t.IsDisposed) { t.Image = img; t.Invalidate(); } }); } catch (InvalidOperationException) { return; }
            }
        });
    }

    /// <summary>The pet tiles (--pet-picker-snapshot clicks one).</summary>
    public static IEnumerable<(PetMove.Pet Pet, Button Tile)> Tiles(PetPickerForm f) => f.list;

    public void Pick(PetMove.Pet p)
    {
        Chosen = p;
        foreach (var (q, t) in list) { t.FlatAppearance.BorderColor = q == p ? Ui.Accent : Ui.Line; t.FlatAppearance.BorderSize = q == p ? 2 : 1; }
        pickLabel.Text = $"Pet: {p.Name}";
        if (list.FirstOrDefault(x => x.Pet == p).Tile is { } shown) tiles.ScrollControlIntoView(shown);
        create.Enabled = true;
        var on = mods.Where(m => m != mod && m.Manifest.UpkReplacements.Contains(p.File, StringComparer.OrdinalIgnoreCase)).ToList();
        others.Text = on.Count == 0 ? "" : $"Other mods for {p.Name} (the one higher in the list wins): " + string.Join(", ", on.Select(m => $"\"{m.Name}\"" + (m.Enabled ? " (on)" : "")));
        others.ForeColor = Ui.OverrideAmber;
        // Whether anything else in the game shows the pet's name (as the Move window's Name in Game says).
        if (p.NameId == 0) { nameNote.Text = "This pet has no name of its own to change."; setName.Enabled = false; return; }
        setName.Enabled = true;
        var uses = StringUsage.Load(gameRoot)?.For(p.NameId) ?? [];
        var rest = uses.Where(u => !u.Prototype.Replace("\\", "/").Contains("/Items/Pets/", StringComparison.OrdinalIgnoreCase)).Select(StringUsage.Describe).Distinct().ToList();
        nameNote.Text = rest.Count == 0 ? $"Only this pet shows this name (the game's: \"{p.Name}\")."
            : $"The game also shows this name for {string.Join(", ", rest.Take(4))}{(rest.Count > 4 ? $" and {rest.Count - 4} more" : "")}: a new name changes theirs too.";
        nameNote.ForeColor = rest.Count == 0 ? Ui.Subtle : Ui.OverrideAmber;
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
}
