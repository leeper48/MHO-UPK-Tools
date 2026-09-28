using MhoPackageModifier;
using System.Drawing.Drawing2D;
using MhoPackageModifier.Gui;

namespace MhoExtendedModManager.Gui;

/// <summary>
/// The manager's look (modelled on MHModManager's WPF window): dark surfaces, a purple accent for the main actions,
/// coloured category badges (P packages, T textures, S strings, A audio), mod cards in the list, flat tabs.
/// Sizes are in 96-dpi units and scaled by the control's DPI.
/// </summary>
static class Ui
{
    public static readonly Color Back = Color.FromArgb(30, 30, 32);
    public static readonly Color Bar = Color.FromArgb(36, 36, 40);
    public static readonly Color Card = Color.FromArgb(36, 36, 40);
    public static readonly Color CardHover = Color.FromArgb(44, 44, 50);
    public static readonly Color CardSelected = Color.FromArgb(48, 50, 86);
    public static readonly Color Line = Color.FromArgb(52, 52, 58);
    public static readonly Color Text = Color.FromArgb(232, 232, 236);
    public static readonly Color Subtle = Color.FromArgb(150, 150, 160);
    public static readonly Color Accent = Color.FromArgb(108, 99, 255);
    public static readonly Color AccentHover = Color.FromArgb(128, 120, 255);
    public static readonly Color Enabled = Color.FromArgb(76, 190, 110);
    public static readonly Color Warn = Color.FromArgb(235, 90, 80);
    public static readonly Color Packages = Color.FromArgb(235, 150, 60);
    public static readonly Color Textures = Color.FromArgb(125, 130, 255);
    public static readonly Color Strings = Color.FromArgb(80, 195, 120);
    public static readonly Color Audio = Color.FromArgb(230, 110, 175);
    /// <summary>Window background: a vertical gradient from Kurt's navy (sampled from his reference image) to the dark grey.</summary>
    public static readonly Color GradientTop = Color.FromArgb(10, 35, 74);
    public static readonly Color GradientBottom = Color.FromArgb(30, 30, 32);
    /// <summary>Translucent dark overlay for the top / bottom bars on the gradient.</summary>
    public static readonly Color BarOverlay = Color.FromArgb(90, 0, 0, 0);
    /// <summary>Text on a filled badge / pill.</summary>
    public static readonly Color OnColor = Color.FromArgb(18, 18, 22);

    /// <summary>
    /// Fills <paramref name="area"/> (in <paramref name="c"/>'s coordinates) with the window gradient, aligned to the whole
    /// form, so controls that draw their own background (card list, tables, headers) continue it seamlessly.
    /// </summary>
    public static void PaintGradient(Graphics g, Control c, Rectangle area)
    {
        var form = c.FindForm();
        if (form == null || form.ClientSize.Height <= 0) { using var b = new SolidBrush(GradientBottom); g.FillRectangle(b, area); return; }
        var origin = c == form ? Point.Empty : form.PointToClient(c.PointToScreen(Point.Empty));
        var rect = new Rectangle(-origin.X, -origin.Y, Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height));
        using var brush = new LinearGradientBrush(rect, GradientTop, GradientBottom, LinearGradientMode.Vertical);
        g.FillRectangle(brush, area);
    }

    /// <summary>
    /// A dark tooltip (owner-drawn: the system one is a pale yellow box). <paramref name="text"/> supplies the text for
    /// tips shown with Show() (the mod list); otherwise the SetToolTip text is used.
    /// </summary>
    public static ToolTip NewTips(Func<string?>? text = null)
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 20000, ShowAlways = true };
        const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left;
        static (int Pad, int Max) Metrics(Control? c) { float s = (c?.DeviceDpi ?? 96) / 96f; return ((int)(7 * s), (int)(380 * s)); }
        tip.Popup += (_, e) =>
        {
            string t = text?.Invoke() ?? (e.AssociatedControl != null ? tip.GetToolTip(e.AssociatedControl) ?? "" : "");
            var (pad, max) = Metrics(e.AssociatedControl);
            var size = TextRenderer.MeasureText(t, TipFont, new Size(max, 0), flags);
            e.ToolTipSize = new Size(size.Width + 2 * pad, size.Height + 2 * pad);
        };
        tip.Draw += (_, e) =>
        {
            var (pad, _) = Metrics(e.AssociatedControl);
            using (var bg = new SolidBrush(Color.FromArgb(34, 36, 46))) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Color.FromArgb(108, 99, 255))) e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, TipFont, Rectangle.Inflate(e.Bounds, -pad, -pad), Text, flags);
        };
        return tip;
    }
    static readonly Font TipFont = Regular(9f);

    // Tag colours by category (Kurt: all teams one colour, all characters another). Chosen apart from the badge
    // colours (orange packages, blue-violet textures, green strings, pink audio).
    public static readonly Color TagCharacter = Color.FromArgb(240, 200, 80);    // gold
    public static readonly Color TagTeam = Color.FromArgb(90, 175, 240);         // sky blue
    public static readonly Color TagContent = Color.FromArgb(60, 200, 190);      // teal: Costume, Power effects …
    public static readonly Color TagMod = Color.FromArgb(200, 170, 250);         // lavender: the mod's own tags
    public static readonly Color TagUser = Color.FromArgb(205, 205, 215);        // light grey: your tags

    /// <summary>A tag's colour: by what it names (character, team, content), else by who set it (the mod, or you).</summary>
    public static Color TagColor(string tag, Mod? m = null) => AutoTags.Classify(tag) switch
    {
        AutoTags.TagClass.Character => TagCharacter,
        AutoTags.TagClass.Team => TagTeam,
        AutoTags.TagClass.Content => TagContent,
        _ => m != null && m.KindOf(tag) == Mod.TagKind.Mod ? TagMod : TagUser,
    };

    /// <summary>"team, automatic", "character, yours", "from the mod" … for tooltips.</summary>
    public static string TagDescription(Mod m, string tag)
    {
        string what = AutoTags.Classify(tag) switch { AutoTags.TagClass.Character => "character, ", AutoTags.TagClass.Team => "team, ", AutoTags.TagClass.Content => "content, ", _ => "" };
        return what + TagSource(m, tag);
    }

    /// <summary>
    /// Draws a tag chip at x, vertically centred on midY; returns its rectangle. Filled (tag colour, dark text) for the
    /// mod's own and the user's tags; <paramref name="soft"/> (tinted, coloured text and border) for automatic tags;
    /// <paramref name="outline"/> (subtle) for "+ Tag" / "+N".
    /// </summary>
    public static Rectangle DrawChip(Graphics g, string text, Font font, int x, int midY, float s, bool outline = false, bool soft = false, Mod? mod = null)
    {
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var r = new Rectangle(x, midY - (size.Height + (int)(3 * s)) / 2, size.Width + (int)(12 * s), size.Height + (int)(3 * s));
        var color = TagColor(text, mod);
        using (var path = Round(r, r.Height / 2f))
        {
            if (outline) { using var pen = new Pen(Subtle, Math.Max(1f, 1f * s)); g.DrawPath(pen, path); }
            else if (soft)
            {
                using var f = new SolidBrush(Color.FromArgb(45, color)); g.FillPath(f, path);
                using var pen = new Pen(Color.FromArgb(150, color), Math.Max(1f, 1f * s)); g.DrawPath(pen, path);
            }
            else { using var f = new SolidBrush(color); g.FillPath(f, path); }
        }
        TextRenderer.DrawText(g, text, font, r, outline ? Subtle : soft ? color : OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return r;
    }

    /// <summary>"automatic", "from the mod" or "yours", for tooltips.</summary>
    public static string TagSource(Mod m, string tag) => m.KindOf(tag) switch { Mod.TagKind.User => "yours", Mod.TagKind.Mod => "from the mod", _ => "automatic" };

    public static int ChipWidth(Graphics g, string text, Font font, float s) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)(12 * s);

    /// <summary>A one-line text prompt in the dark theme (with suggestions); null when cancelled or empty.</summary>
    public static string? Prompt(IWin32Window owner, string title, string label, string initial = "", IEnumerable<string>? suggestions = null, bool secret = false)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                                 StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Regular(9.5f), Padding = new Padding(12) };
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = TitleCase(title), AutoSize = true, Font = Bold(12f), Margin = new Padding(0, 0, 0, 8) });   // as every popup
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size((int)(460 * f.DeviceDpi / 96f), 0) });
        var box = new TextBox { Text = initial, Width = (int)(320 * f.DeviceDpi / 96f), Margin = new Padding(0, 0, 0, 10), UseSystemPasswordChar = secret };
        if (suggestions != null)
        {
            box.AutoCompleteMode = AutoCompleteMode.SuggestAppend; box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            box.AutoCompleteCustomSource.AddRange(suggestions.ToArray());
        }
        t.Controls.Add(box);
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        var ok = AccentButton("OK", () => f.DialogResult = DialogResult.OK, "OK (Enter)"); var cancel = FlatButton("Cancel", () => f.DialogResult = DialogResult.Cancel, "Cancel (Esc)");
        buttons.Controls.AddRange([cancel, ok]);
        t.Controls.Add(buttons);
        f.Controls.Add(t);
        f.AcceptButton = ok; f.CancelButton = cancel;
        MhoPackageModifier.Gui.Theme.Apply(f, MhoPackageModifier.Gui.Palette.Dark);
        RestyleButtons(f);
        box.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    // Microsoft-style Title Case: articles, conjunctions and prepositions of four letters or fewer stay lower case,
    // except as the first word of the text or after ":" / "·" / "(" / "—".
    static readonly HashSet<string> MinorWords = new(StringComparer.OrdinalIgnoreCase)
    { "a", "an", "the", "and", "but", "or", "nor", "for", "so", "yet", "as", "at", "by", "in", "of", "on", "per", "to", "via", "from", "into", "onto", "with", "over", "than", "like" };   // not "off" / "up": Turn Off, Set Up

    /// <summary>
    /// Title Case for labels and status lines (Kurt: every UI text in Title Case). Text in double quotes (mod and
    /// tag names) is left as it is, and so is the rest of each word (MHModManager, PC, .dds stay).
    /// </summary>
    public static string TitleCase(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool quoted = false, start = true;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"') { quoted = !quoted; sb.Append(c); i++; continue; }
            if (!quoted && char.IsLetter(c))
            {
                int j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '\'' || text[j] == '’')) j++;
                string word = text[i..j];
                bool afterDot = i > 0 && text[i - 1] == '.';   // .dds, .png, file extensions
                bool plural = word == "s" && i > 0 && text[i - 1] == '(';   // file(s), mod(s)
                bool minor = MinorWords.Contains(word) && !start;
                sb.Append(afterDot || minor || plural ? word : char.ToUpperInvariant(word[0]) + word[1..]);
                start = false;
                i = j;
                continue;
            }
            if (!quoted && (c == ':' || c == '·' || c == '(' || c == '—')) start = true;
            else if (!quoted && !char.IsWhiteSpace(c) && c != '-' && c != '●') start = start && (c == ' ');
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>A small preview of a .dds (null if it can't be read).</summary>
    public static Image? DdsThumb(string path, int size)
    {
        var d = TextureDecode.ReadDds(path, out _);
        var bgra = d is { } x ? TextureDecode.ToBgra(x.Format, x.W, x.H, x.Data, out _) : null;
        return bgra == null ? null : Thumb(bgra, d!.Value.W, d.Value.H, size);
    }

    /// <summary>A thumbnail (at most size × size) from decoded BGRA pixels.</summary>
    public static Image Thumb(byte[] bgra, int w, int h, int size)
    {
        using var full = TextureDecode.ToBitmap(bgra, w, h);
        float k = Math.Min(1f, Math.Min((float)size / full.Width, (float)size / full.Height));
        var bmp = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(full, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    public static Font Regular(float pt = 9.75f) => new("Segoe UI", pt);
    public static Font Bold(float pt = 10f) => new("Segoe UI Semibold", pt);
    /// <summary>True bold, for the letters on the small solid badges (Kurt: legibility).</summary>
    public static Font Heavy(float pt) => new("Segoe UI", pt, FontStyle.Bold);

    /// <summary>The mod's categories as badges: the full word when it has one kind of change (as MHModManager shows), letters otherwise.</summary>
    public static List<(string Text, Color Color)> Badges(Mod m)
    {
        var all = new List<(string Letter, string Word, Color Color)>();
        if (m.Manifest.UpkReplacements.Count > 0) all.Add(("P", "Packages", Packages));
        if (m.Manifest.Replacements.Count + m.Manifest.AchievementReplacements.Count + m.Manifest.StoreReplacements.Count + m.Manifest.Extra.Count() > 0) all.Add(("T", "Textures", Textures));
        if (m.Strings.Count > 0) all.Add(("S", "Strings", Strings));
        if (m.Manifest.AudioPacks.Count > 0) all.Add(("A", "Audio", Audio));
        return all.Count == 1 ? [(all[0].Word, all[0].Color)] : all.Select(a => (a.Letter, a.Color)).ToList();
    }

    public static int Modifications(Mod m) =>
        m.Manifest.UpkReplacements.Count + m.Manifest.Replacements.Count + m.Manifest.AchievementReplacements.Count + m.Manifest.StoreReplacements.Count + m.Manifest.Extra.Count() + m.Strings.Count + m.Manifest.AudioPacks.Count;

    public static string CountText(Mod m) =>
        Badges(m) is [("Packages", _)] ? $"{m.Manifest.UpkReplacements.Count} Package(s)" : $"{Modifications(m)} Modification(s)";

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Draws badges right-aligned ending at <paramref name="right"/>, vertically centred on <paramref name="midY"/>; returns their left edge.</summary>
    public static int DrawBadges(Graphics g, List<(string Text, Color Color)> badges, int right, int midY, Font font, float scale, bool filled = true)
    {
        int x = right;
        for (int i = badges.Count - 1; i >= 0; i--)
        {
            var (text, color) = badges[i];
            var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
            int w = Math.Max((int)(18 * scale), size.Width + (int)(10 * scale)), h = (int)(18 * scale);
            var r = new Rectangle(x - w, midY - h / 2, w, h);
            using (var path = Round(r, 3 * scale))
            {
                // Solid colour with dark text (Kurt: inverted from the outlined look).
                if (filled) { using var b = new SolidBrush(color); g.FillPath(b, path); }
                else { using var pen = new Pen(color, Math.Max(1f, scale)); g.DrawPath(pen, path); }
            }
            TextRenderer.DrawText(g, text, font, r, filled ? OnColor : color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x = r.Left - (int)(4 * scale);
        }
        return x;
    }

    public static Button AccentButton(string text, Action onClick, string? tip = null) => Tip(Style(new Button { Text = text, AutoSize = true, Padding = new Padding(10, 3, 10, 3) }, onClick, accent: true), tip);
    public static Button FlatButton(string text, Action onClick, string? tip = null) => Tip(Style(new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3) }, onClick, accent: false), tip);

    /// <summary>The app's one tooltip (dark). Every button gets a tip where it's made (Kurt: tooltips on all buttons);
    /// --tooltip-audit lists any that don't.</summary>
    public static readonly ToolTip Tips = NewTips();
    /// <summary>Stock images (the original icon packages) are decoded one at a time (the list's and the preview's pictures).</summary>
    public static readonly object StockLock = new();
    public static T Tip<T>(T c, string? text) where T : Control { if (!string.IsNullOrEmpty(text)) Tips.SetToolTip(c, text); return c; }

    /// <summary>Buttons under a control that have no tooltip (on the shared tooltip or <paramref name="other"/>).</summary>
    public static List<string> MissingTips(Control root, params ToolTip[] other)
    {
        var missing = new List<string>();
        void Walk(Control c)
        {
            if (c is Button b && string.IsNullOrEmpty(Tips.GetToolTip(b)) && other.All(o => string.IsNullOrEmpty(o.GetToolTip(b)))) missing.Add(b.Text.Replace("&", ""));
            foreach (Control k in c.Controls) Walk(k);
        }
        Walk(root);
        return missing;
    }

    /// <summary>Draws a bar above (top) or below the button's ▲ / ▼, making it a "to the top / bottom" button that matches the
    /// weight of the plain arrows (the ⤒ ⤓ glyphs came out thin).</summary>
    public static void AddEndBar(Button b, bool top)
    {
        b.Paint += (_, e) =>
        {
            var size = TextRenderer.MeasureText(e.Graphics, b.Text, b.Font, Size.Empty, TextFormatFlags.NoPadding);
            float s = b.DeviceDpi / 96f;
            float w = size.Width * 0.9f, x = (b.Width - w) / 2f;
            float glyphTop = (b.Height - size.Height) / 2f + size.Height * 0.22f, glyphBottom = (b.Height + size.Height) / 2f - size.Height * 0.2f;
            float y = top ? glyphTop - 2.5f * s : glyphBottom + 1f * s;
            using var brush = new SolidBrush(b.Enabled ? b.ForeColor : Ui.DisabledText);
            e.Graphics.FillRectangle(brush, x, y, w, Math.Max(1.5f, 1.6f * s));
        };
    }

    /// <summary>Text of a disabled button (Kurt: medium grey, not the near-black Windows draws on our dark buttons).</summary>
    public static readonly Color DisabledText = Color.FromArgb(128, 128, 136);

    static Button Style(Button b, Action onClick, bool accent)
    {
        Rounded(b);
        b.Click += (_, _) => onClick();
        b.Tag = accent ? "accent" : "flat";
        b.FlatStyle = FlatStyle.Flat;
        b.Font = Regular(9.5f);
        b.Margin = new Padding(4, 2, 4, 2);
        return b;
    }

    /// <summary>
    /// A table like MHModManager's: tall rows with a thin line between them, quiet headers, no grid lines or row headers.
    /// A width of 0 fills the remaining space; <paramref name="thumbs"/> adds an image column first. Widths are 96-dpi units.
    /// </summary>
    public static DataGridView Grid(float scale, bool thumbs, params (string Title, int Width)[] cols)
    {
        var g = new GradientGrid
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AllowUserToOrderColumns = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, Font = Regular(9.5f), Dock = DockStyle.Fill,
        };
        g.RowTemplate.Height = (int)((thumbs ? 52 : 30) * scale);
        g.ColumnHeadersHeight = (int)(30 * scale);
        if (thumbs) g.Columns.Add(new DataGridViewImageColumn { Name = "thumb", HeaderText = "", Width = (int)(56 * scale), ImageLayout = DataGridViewImageCellLayout.Zoom, DefaultCellStyle = { NullValue = null, Padding = new Padding((int)(4 * scale)) } });
        foreach (var (t, w) in cols)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = t, Name = t, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (w == 0) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; else c.Width = (int)(w * scale);
            g.Columns.Add(c);
        }
        StyleGrid(g);
        g.DataBindingComplete += (_, _) => g.ClearSelection();
        g.VisibleChanged += (_, _) => { if (g.Visible && g.Tag is not "keepselection") g.ClearSelection(); };   // no row highlighted until clicked
        return g;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Back;
        g.GridColor = Line;
        g.DefaultCellStyle.BackColor = Back;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = CardSelected;
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.ColumnHeadersDefaultCellStyle.BackColor = Bar;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Subtle;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Bar;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        g.ColumnHeadersDefaultCellStyle.Font = Regular(9f);
        foreach (DataGridViewColumn c in g.Columns)
            if (!c.ReadOnly && c is DataGridViewTextBoxColumn) { c.DefaultCellStyle.BackColor = Color.FromArgb(40, 40, 46); }   // editable cells stand out
    }

    /// <summary>
    /// After MPM's Theme.Apply: the manager's colours on top (dark panels, subtle labels, grids, card lists, accent / flat
    /// buttons, flat tabs). <paramref name="bars"/> get the bar colour (top / bottom strips).
    /// </summary>
    public static void Restyle(Control root, params Control[] bars)
    {
        void Walk(Control c)
        {
            if (c is Label { Tag: "subtle" } l) { l.ForeColor = Subtle; }
            else if (c is DataGridView dg) StyleGrid(dg);
            else if (c is ListBox lb) { lb.BackColor = Back; lb.BorderStyle = BorderStyle.None; }
            else if (c is TextBox { ReadOnly: false } tb) { tb.BackColor = Color.FromArgb(40, 40, 46); tb.ForeColor = Text; }
            else if (c is Panel { Tag: "card" } card) card.BackColor = Card;
            else if (c is Panel or TableLayoutPanel or FlowLayoutPanel or SplitterPanel or UserControl or TabPage) c.BackColor = c.Parent is Panel { Tag: "card" } ? Card : Color.Transparent;
            else if (c is GradientSplit gs) gs.Invalidate();
            if (c is Label lab) lab.BackColor = c.Parent is Panel { Tag: "card" } ? Card : Color.Transparent;
            foreach (Control k in c.Controls) Walk(k);
        }
        if (root is not Form) root.BackColor = Color.Transparent;
        Walk(root);
        foreach (var b in bars) { b.BackColor = BarOverlay; foreach (Control c in b.Controls) if (c is not Button and not TextBox) { c.BackColor = Color.Transparent; foreach (Control k in c.Controls) if (k is not Button and not TextBox) k.BackColor = Color.Transparent; } }
        RestyleButtons(root);
        foreach (var t in All<FlatTabs>(root)) t.Select(Math.Max(0, t.SelectedIndex));
    }

    /// <summary>A dialog's size in 96-dpi units, scaled for the display and kept within 90% of its working area; centred on its owner.</summary>
    public static void FitToScreen(Form f, int width, int height)
    {
        float s = f.DeviceDpi / 96f;
        var area = Screen.FromControl(f.Owner ?? f).WorkingArea;
        f.Size = new Size(Math.Min((int)(width * s), (int)(area.Width * 0.9)), Math.Min((int)(height * s), (int)(area.Height * 0.9)));
        var o = f.Owner?.Bounds ?? area;
        f.Location = new Point(o.X + (o.Width - f.Width) / 2, o.Y + (o.Height - f.Height) / 2);
    }

    public static IEnumerable<T> All<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t) yield return t;
            foreach (var k in All<T>(c)) yield return k;
        }
    }

    /// <summary>A dark rounded-looking panel with a subtle caption on top (e.g. an image preview).</summary>
    public static Panel CardPanel(string caption, Control content, Control? footer = null)
    {
        var p = new Panel { Dock = DockStyle.Fill, Tag = "card", Padding = new Padding(8), Margin = new Padding(4) };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        if (footer != null) { footer.Dock = DockStyle.Bottom; p.Controls.Add(footer); }
        p.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, AutoSize = true, Tag = "subtle", Font = Bold(8.5f), Padding = new Padding(0, 0, 0, 4) });
        return p;
    }

    // Buttons drawn with rounded corners (Kurt: every button), and their hover / pressed state.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, int[]> rounded = [];

    /// <summary>
    /// Draws a button with rounded corners (antialiased): the parent's background behind the corners, the fill for its
    /// state (normal, hover, pressed; the FlatAppearance colours), a 1 px border, and the text (grey when disabled).
    /// Paint handlers added later (e.g. AddEndBar's bar) draw on top.
    /// </summary>
    public static void Rounded(Button b)
    {
        if (rounded.TryGetValue(b, out _)) return;
        var st = new int[2];   // [0] hover, [1] pressed
        rounded.Add(b, st);
        b.MouseEnter += (_, _) => { st[0] = 1; b.Invalidate(); };
        b.MouseLeave += (_, _) => { st[0] = 0; st[1] = 0; b.Invalidate(); };
        b.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { st[1] = 1; b.Invalidate(); } };
        b.MouseUp += (_, _) => { st[1] = 0; b.Invalidate(); };
        b.Paint += (_, e) =>
        {
            var g = e.Graphics;
            var r = b.ClientRectangle;
            // Behind the corners: what the parent shows there.
            var parentColor = b.Parent?.BackColor ?? Back;
            if (parentColor.A < 255) PaintGradient(g, b, r);
            if (parentColor.A > 0) { using var pb = new SolidBrush(parentColor); g.FillRectangle(pb, r); }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = b.DeviceDpi / 96f;
            var box = new RectangleF(0.5f, 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            var fillColor = !b.Enabled ? b.BackColor : st[1] == 1 ? b.FlatAppearance.MouseDownBackColor : st[0] == 1 ? b.FlatAppearance.MouseOverBackColor : b.BackColor;
            if (fillColor.IsEmpty || fillColor.A == 0) fillColor = b.BackColor;
            using (var path = RoundF(box, 5 * s))
            {
                using (var f = new SolidBrush(fillColor)) g.FillPath(f, path);
                var border = b.FlatAppearance.BorderColor.IsEmpty ? Line : b.FlatAppearance.BorderColor;
                if (b.FlatAppearance.BorderSize > 0) { using var pen = new Pen(border, 1f); g.DrawPath(pen, path); }
            }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, b.Text, b.Font, r, b.Enabled ? b.ForeColor : DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }

    static GraphicsPath RoundF(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Re-applies accent / flat colours after MPM's theme (which colours every button the same), and rounds
    /// every button's corners (plain ones from other windows become flat buttons in the app's style).</summary>
    public static void RestyleButtons(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button pb && pb.Tag is not ("accent" or "flat") && pb.Tag == null)
            {
                pb.Tag = "flat"; pb.FlatStyle = FlatStyle.Flat;
                Rounded(pb);
            }
            if (c is Button b && b.Tag is "accent" or "flat")
            {
                bool accent = (string)b.Tag == "accent";
                b.UseVisualStyleBackColor = false;
                b.BackColor = accent ? Accent : Bar;
                b.ForeColor = accent ? Color.White : Text;
                b.FlatAppearance.BorderColor = accent ? Accent : Line;
                b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : CardHover;
                b.FlatAppearance.MouseDownBackColor = accent ? AccentHover : CardSelected;
            }
            RestyleButtons(c);
        }
    }
}

/// <summary>A group header in the mod list (grouped by tag or author); click to fold it.</summary>
sealed class ModGroup(string key, string name, int count, bool collapsed)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public int Count { get; } = count;
    public bool Collapsed { get; } = collapsed;
}

/// <summary>
/// The mod list as cards: costume icon, name, author and tag chips, category badges, a checkbox (enable), a padlock,
/// status and change count. Items are Mods, or ModGroup headers when the list is grouped. Hovering a part of a card
/// shows what it does (dark tooltip, after a short pause).
/// </summary>
sealed class ModListBox : ListBox
{
    public HashSet<Mod> Conflicted { get; set; } = [];
    /// <summary>Conflicting mods that lose at least one change to a higher mod (red stripe); the others only win (amber).</summary>
    public HashSet<Mod> Overridden { get; set; } = [];
    /// <summary>What each conflicting mod overrides / is overridden by (for the tooltip).</summary>
    public Func<Mod, string?>? ConflictText { get; set; }
    /// <summary>The selected mod's conflict partners: outlined in the list (a user: show which mods conflict).</summary>
    public HashSet<Mod> Partners { get; set; } = [];
    /// <summary>Drag and drop is allowed (the plain priority view).</summary>
    public Func<bool>? CanReorder { get; set; }
    /// <summary>A card dropped next to another: (moved mod, target mod, below the target).</summary>
    public event Action<Mod, Mod, bool>? Dropped;
    Mod? dragMod; Point dragFrom; bool dragging, leftDown; int dropItem = -1; bool dropBelow;
    readonly System.Windows.Forms.Timer scrollTimer = new() { Interval = 60 };
    public event Action<Mod>? CheckClicked;
    /// <summary>Padlock clicked (a locked mod, or one that can be locked where it is).</summary>
    public event Action<Mod>? LockClicked;
    public event Action<ModGroup>? GroupClicked;
    public event Action<string>? TagClicked;
    /// <summary>Right-click on a card (already selected), at a screen point.</summary>
    public event Action<Mod, Point>? MenuRequested;
    /// <summary>The newer Nexus version for a mod (null: none), and a click on its ↑ badge.</summary>
    public Func<Mod, string?>? UpdateFor { get; set; }
    /// <summary>Stock pictures for mods without one of their own (StockCatalog.DefaultIconFor); null: none.</summary>
    public StockCatalog? Catalog { get; set; }
    public event Action<Mod>? UpdateClicked;
    /// <summary>What a padlock click would lock the mod to (ModLibrary.CanLock); None hides the padlock of an unlocked mod.</summary>
    public Func<Mod, ModLock>? CanLock { get; set; }
    int hover = -1;
    readonly Font nameFont = Ui.Bold(10f), smallFont = Ui.Regular(8.25f), badgeFont = Ui.Heavy(7.5f), initialFont = Ui.Bold(14f), chipFont = Ui.Heavy(7.25f), groupFont = Ui.Bold(8.75f);
    // Costume icons (first costume… replacement of each mod), decoded in the background. Key: .dds path + write time,
    // so an edited mod shows its new icon. A null value = no icon / unreadable / still loading.
    readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);
    // Where each card's badges and tag chips were drawn (item index), for the tooltips and chip clicks.
    readonly Dictionary<int, (Rectangle Badges, List<(Rectangle Rect, string Tag)> Chips)> regions = [];
    // Where each card's Update pill and Nexus mark were drawn (item index).
    readonly Dictionary<int, (Rectangle Pill, Rectangle Mark)> nexusParts = [];
    readonly Font pillFont = Ui.Heavy(7.5f), markFont = Ui.Heavy(6.5f);
    readonly ToolTip tips;
    readonly System.Windows.Forms.Timer tipTimer = new() { Interval = 450 };
    string? tipText;
    bool tipShown;

    public ModListBox()
    {
        DrawMode = DrawMode.OwnerDrawVariable;
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        // The whole list is painted here, into one off-screen buffer (OnPaint): a native owner-drawn ListBox erases the
        // area first and then draws each card straight onto the screen, which flickered on every hover, tooltip and
        // scroll (a user, 2026-09-28).
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        tips = Ui.NewTips(() => tipText);
        // While dragging near the top or bottom edge, scroll.
        scrollTimer.Tick += (_, _) =>
        {
            if (!dragging) { scrollTimer.Stop(); return; }
            var p = PointToClient(MousePosition);
            int edge = (int)(28 * S);
            if (p.Y < edge && TopIndex > 0) TopIndex--;
            else if (p.Y > Height - edge && TopIndex < Items.Count - 1) TopIndex++;
            else return;
            UpdateDrop(p);
        };
        tipTimer.Tick += (_, _) =>
        {
            tipTimer.Stop();
            if (tipText == null || !IsHandleCreated) return;
            var p = PointToClient(MousePosition);
            tips.Show(tipText, this, p.X + (int)(14 * S), p.Y + (int)(20 * S), 15000);
            tipShown = true;
        };
    }

    float S => DeviceDpi / 96f;
    static readonly Color NexusOrange = Color.FromArgb(230, 140, 60);
    public static readonly Color ConflictAmber = Color.FromArgb(242, 170, 60);

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = Math.Min(255, (int)((e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is ModGroup ? 30 : 50) * S));
    }

    // Cards are laid out from the right edge (badges, checkbox): a width change must repaint every card, not just the
    // newly exposed strip, or the old badges stay behind (Kurt, 2026-09-27: "it repeats the status icons").
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    // No separate erase: OnPaint covers every pixel (erasing first is what made the list flash).
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { m.Result = 1; return; }   // WM_ERASEBKGND
        if (m.Msg is 0x0317 or 0x0318)   // WM_PRINT / WM_PRINTCLIENT (DrawToBitmap, snapshots): the same painting
        {
            // Painted into a bitmap and copied with GDI: the printing DC is offset to this window's place (window
            // origin), which GDI honours and GDI+ doesn't (the cards landed elsewhere in the picture).
            if (m.Msg == 0x0317) base.WndProc(ref m);   // the scroll bar
            using var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using (var bg = Graphics.FromImage(bmp)) OnPaint(new PaintEventArgs(bg, ClientRectangle));
            IntPtr hbm = bmp.GetHbitmap(), mem = CreateCompatibleDC(m.WParam), old = SelectObject(mem, hbm);
            BitBlt(m.WParam, 0, 0, bmp.Width, bmp.Height, mem, 0, 0, 0x00CC0020);   // SRCCOPY
            SelectObject(mem, old); DeleteDC(mem); DeleteObject(hbm);
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    /// <summary>The visible cards and, below the last one, the window gradient, all into the double buffer.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int bottom = 0;
        for (int i = Math.Max(0, TopIndex); i < Items.Count; i++)
        {
            var r = GetItemRectangle(i);
            if (r.Height <= 0 || r.Top >= ClientSize.Height) break;   // past the view (the list gives an empty rectangle there)
            bottom = r.Bottom;
            if (!r.IntersectsWith(e.ClipRectangle)) continue;
            var state = SelectedIndex == i ? DrawItemState.Selected : DrawItemState.None;
            OnDrawItem(new DrawItemEventArgs(g, Font, r, i, state));
        }
        if (bottom < ClientSize.Height) Ui.PaintGradient(g, this, new Rectangle(0, bottom, ClientSize.Width, ClientSize.Height - bottom));
    }

    Rectangle CheckRect(Rectangle b) { int s = (int)(16 * S); return new Rectangle(b.Right - s - (int)(10 * S), b.Top + (int)(9 * S), s, s); }
    // The padlock sits under the checkbox, on the second row.
    Rectangle LockRect(Rectangle b) { var c = CheckRect(b); return new Rectangle(c.X, b.Top + (int)(28 * S), c.Width, c.Height); }
    ModLock LockOffer(Mod m) => m.Lock != ModLock.None ? m.Lock : CanLock?.Invoke(m) ?? ModLock.None;

    /// <summary>A padlock: closed and gold when locked, open and grey when the mod can be locked here.</summary>
    static void DrawPadlock(Graphics g, Rectangle r, bool locked, float s)
    {
        var body = new RectangleF(r.X + r.Width * 0.14f, r.Y + r.Height * 0.44f, r.Width * 0.72f, r.Height * 0.52f);
        float sw = r.Width * 0.44f, sx = r.X + (r.Width - sw) / 2 + (locked ? 0 : r.Width * 0.18f), sy = r.Y + r.Height * 0.06f - (locked ? 0 : r.Height * 0.08f);
        var color = locked ? Color.FromArgb(242, 190, 60) : Ui.Subtle;
        using (var pen = new Pen(color, Math.Max(1.5f, 1.8f * s)))
        {
            using var shackle = new GraphicsPath();
            shackle.AddLine(sx, body.Y, sx, sy + sw / 2);
            shackle.AddArc(sx, sy, sw, sw, 180, 180);
            shackle.AddLine(sx + sw, sy + sw / 2, sx + sw, body.Y - (locked ? 0 : r.Height * 0.1f));
            g.DrawPath(pen, shackle);
        }
        using (var path = Ui.Round(Rectangle.Round(body), 1.5f * s))
        {
            if (locked) { using var f = new SolidBrush(color); g.FillPath(f, path); }
            else { using var pen = new Pen(color, Math.Max(1f, 1.2f * s)); g.DrawPath(pen, path); }
        }
        float k = r.Width * 0.12f;
        using var hole = new SolidBrush(locked ? Ui.OnColor : color);
        g.FillEllipse(hole, body.X + body.Width / 2 - k / 2, body.Y + body.Height * 0.3f, k, k);
    }

    void DrawGroup(Graphics g, ModGroup grp, Rectangle b)
    {
        int x = b.X + (int)(8 * S), mid = b.Y + b.Height / 2 + (int)(2 * S);
        // Chevron: pointing right when folded, down when open.
        float c = 4.5f * S;
        using (var br = new SolidBrush(Ui.Subtle))
            g.FillPolygon(br, grp.Collapsed
                ? [new PointF(x, mid - c), new PointF(x + c * 1.3f, mid), new PointF(x, mid + c)]
                : [new PointF(x - c * 0.2f, mid - c * 0.6f), new PointF(x + c * 1.5f, mid - c * 0.6f), new PointF(x + c * 0.65f, mid + c * 0.7f)]);
        x += (int)(14 * S);
        var r = grp.Key.StartsWith("tag:") && grp.Name != "Untagged" ? Ui.DrawChip(g, grp.Name, chipFont, x, mid, S) : Rectangle.Empty;
        if (r.IsEmpty)
        {
            string label = grp.Name.ToUpperInvariant();
            var sz = TextRenderer.MeasureText(g, label, groupFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, label, groupFont, new Point(x, mid - sz.Height / 2), Ui.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            r = new Rectangle(x, mid - sz.Height / 2, sz.Width, sz.Height);
        }
        string count = grp.Count.ToString();
        var cs = TextRenderer.MeasureText(g, count, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, count, smallFont, new Point(r.Right + (int)(8 * S), mid - cs.Height / 2), Ui.Subtle, TextFormatFlags.NoPadding);
        using var pen = new Pen(Color.FromArgb(50, 255, 255, 255));
        int lx = r.Right + cs.Width + (int)(16 * S);
        if (lx < b.Right - (int)(8 * S)) g.DrawLine(pen, lx, mid, b.Right - (int)(8 * S), mid);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var b = e.Bounds;
        Ui.PaintGradient(g, this, b);
        if (Items[e.Index] is ModGroup grp) { DrawGroup(g, grp, b); return; }
        var m = (Mod)Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var card = new Rectangle(b.X + (int)(4 * S), b.Y + (int)(2 * S), b.Width - (int)(8 * S), b.Height - (int)(4 * S));
        using (var path = Ui.Round(card, 4 * S))
        using (var fill = new SolidBrush(sel ? Ui.CardSelected : e.Index == hover ? Ui.CardHover : Ui.Card))
            g.FillPath(fill, path);
        // Conflicts (enabled mods changing the same thing): a stripe on the left edge, red when part of this mod is
        // overridden by a higher one, amber when it only overrides others; the selected mod's partners are outlined.
        if (Conflicted.Contains(m))
        {
            var stripe = new Rectangle(card.X, card.Y + (int)(3 * S), Math.Max(3, (int)(4 * S)), card.Height - (int)(6 * S));
            using var sb = new SolidBrush(Overridden.Contains(m) ? Ui.Warn : ConflictAmber);
            using var sp = Ui.Round(stripe, 2 * S);
            g.FillPath(sb, sp);
        }
        if (Partners.Contains(m))
        {
            using var pen = new Pen(Overridden.Contains(m) ? Ui.Warn : ConflictAmber, Math.Max(1.5f, 1.6f * S));
            using var op = Ui.Round(Rectangle.Inflate(card, -1, -1), 4 * S);
            g.DrawPath(pen, op);
        }
        // Drag and drop: where the card will go.
        if (dragging && e.Index == dropItem)
        {
            int y = dropBelow ? card.Bottom + (int)(1 * S) : card.Top - (int)(1 * S);
            using var lp = new Pen(Ui.Accent, Math.Max(2f, 3f * S));
            g.DrawLine(lp, card.X, y, card.Right, y);
        }

        int pad = (int)(10 * S);
        var check = CheckRect(b);

        // Costume icon on the left (or the name's first letter on a plain tile when the mod has none).
        int iconSize = card.Height - (int)(8 * S);
        var iconRect = new Rectangle(card.X + (int)(4 * S), card.Y + (card.Height - iconSize) / 2, iconSize, iconSize);
        var icon = Icon(m);
        using (var path = Ui.Round(iconRect, 3 * S))
        {
            if (icon != null)
            {
                var old = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Max((float)iconRect.Width / icon.Width, (float)iconRect.Height / icon.Height);
                float w = icon.Width * k, h = icon.Height * k;
                g.DrawImage(icon, iconRect.X + (iconRect.Width - w) / 2, iconRect.Y + (iconRect.Height - h) / 2, w, h);
                g.Clip = old;
            }
            else
            {
                using var f = new SolidBrush(Color.FromArgb(40, 255, 255, 255)); g.FillPath(f, path);
                string initial = m.Name.TrimStart().Length > 0 ? m.Name.TrimStart()[..1].ToUpperInvariant() : "?";
                TextRenderer.DrawText(g, initial, initialFont, iconRect, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
        // Nexus mark on the icon's corner: a green disc with ↑ when Nexus has a newer version, an orange N when linked.
        string? newer = UpdateFor?.Invoke(m);
        var mark = Rectangle.Empty;
        if (newer != null || m.NexusModId != null)
        {
            int ms = (int)((newer != null ? 17 : 14) * S);
            mark = new Rectangle(iconRect.Right - ms + (int)(3 * S), iconRect.Bottom - ms + (int)(3 * S), ms, ms);
            using (var ring = new SolidBrush(sel ? Ui.CardSelected : Ui.Card)) g.FillEllipse(ring, Rectangle.Inflate(mark, (int)(2 * S), (int)(2 * S)));
            using (var f = new SolidBrush(newer != null ? Ui.Enabled : NexusOrange)) g.FillEllipse(f, mark);
            if (newer != null)
            {
                float cx0 = mark.X + mark.Width / 2f, top = mark.Y + mark.Height * 0.22f, bot = mark.Bottom - mark.Height * 0.22f, w = mark.Width * 0.24f;
                using var p = new Pen(Ui.OnColor, Math.Max(1.5f, 1.8f * S)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLine(p, cx0, bot, cx0, top);
                g.DrawLines(p, [new PointF(cx0 - w, top + w), new PointF(cx0, top), new PointF(cx0 + w, top + w)]);
            }
            else TextRenderer.DrawText(g, "N", markFont, mark, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        int textLeft = iconRect.Right + (int)(8 * S);
        // Checkbox: white box, accent fill with a tick when enabled.
        using (var path = Ui.Round(check, 2.5f * S))
        {
            if (m.Enabled) { using var f = new SolidBrush(Ui.Accent); g.FillPath(f, path); }
            using var pen = new Pen(m.Enabled ? Ui.Accent : Ui.Subtle, Math.Max(1f, 1.2f * S));
            g.DrawPath(pen, path);
        }
        if (m.Enabled)
        {
            using var tick = new Pen(Color.White, 2f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(tick, [new PointF(check.X + check.Width * 0.22f, check.Y + check.Height * 0.52f), new PointF(check.X + check.Width * 0.42f, check.Y + check.Height * 0.72f), new PointF(check.X + check.Width * 0.78f, check.Y + check.Height * 0.3f)]);
        }

        var lockOffer = LockOffer(m);
        if (lockOffer != ModLock.None) DrawPadlock(g, LockRect(b), m.Lock != ModLock.None, S);

        // Badges left of the checkbox; conflict / missing-files marker before them.
        var badges = Ui.Badges(m);
        bool broken = m.LoadError != null || m.MissingFiles().Any();
        if (Conflicted.Contains(m)) badges.Insert(0, ("!", Ui.Warn));
        int badgesLeft = Ui.DrawBadges(g, badges, check.Left - (int)(8 * S), check.Top + check.Height / 2, badgeFont, S);
        // A newer version on Nexus: a green "↑ Update" pill before the badges (click: update).
        var pill = Rectangle.Empty;
        if (newer != null)
        {
            const string label = "↑ UPDATE";
            var ts = TextRenderer.MeasureText(g, label, pillFont, Size.Empty, TextFormatFlags.NoPadding);
            int ph = (int)(16 * S), pw = ts.Width + (int)(12 * S);
            pill = new Rectangle(badgesLeft - pw - (int)(6 * S), check.Top + check.Height / 2 - ph / 2, pw, ph);
            using (var path = Ui.Round(pill, ph / 2f))
            using (var f = new SolidBrush(Ui.Enabled)) g.FillPath(f, path);
            TextRenderer.DrawText(g, label, pillFont, pill, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            badgesLeft = pill.Left;
        }
        nexusParts[e.Index] = (pill, mark);

        var nameRect = new Rectangle(textLeft, card.Y + (int)(5 * S), badgesLeft - textLeft, (int)(20 * S));
        TextRenderer.DrawText(g, m.Name, nameFont, nameRect, m.Enabled ? Ui.Text : Color.FromArgb(200, 200, 205), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Second row: author and tag chips on the left, state and count on the right.
        var second = new Rectangle(textLeft, card.Y + (int)(25 * S), card.Right - pad - textLeft, (int)(16 * S));
        string state = broken ? "Missing Files" : m.Enabled ? "Enabled" : "Disabled";
        string count = "  ·  " + Ui.CountText(m);
        var countSize = TextRenderer.MeasureText(g, count, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        var stateSize = TextRenderer.MeasureText(g, state, smallFont, Size.Empty, TextFormatFlags.NoPadding);
        int right = check.Left - (int)(8 * S);
        int stateLeft = right - countSize.Width - stateSize.Width;
        string author = m.Manifest.Author ?? "";
        int authorW = author.Length == 0 ? 0 : Math.Min(TextRenderer.MeasureText(g, author, smallFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width, Math.Max(0, stateLeft - textLeft - (int)(8 * S)));
        TextRenderer.DrawText(g, author, smallFont, new Rectangle(second.X, second.Y, authorW + 1, second.Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        var chips = new List<(Rectangle, string)>();
        int cx = textLeft + authorW + (authorW > 0 ? (int)(8 * S) : 0), mid = second.Y + second.Height / 2;
        int limit = stateLeft - (int)(8 * S);
        foreach (string tag in m.Tags)
        {
            int w = Ui.ChipWidth(g, tag, chipFont, S);
            if (cx + w > limit)
            {
                string more = $"+{m.Tags.Count - chips.Count}";
                if (cx + Ui.ChipWidth(g, more, chipFont, S) <= limit) Ui.DrawChip(g, more, chipFont, cx, mid, S, outline: true);
                break;
            }
            chips.Add((Ui.DrawChip(g, tag, chipFont, cx, mid, S, soft: m.KindOf(tag) == Mod.TagKind.Auto, mod: m), tag));
            cx += w + (int)(4 * S);
        }
        regions[e.Index] = (new Rectangle(badgesLeft, check.Top - (int)(2 * S), check.Left - badgesLeft, check.Height + (int)(4 * S)), chips);
        TextRenderer.DrawText(g, count, smallFont, new Point(right - countSize.Width, second.Y + (second.Height - countSize.Height) / 2), Ui.Subtle, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, state, smallFont, new Point(stateLeft, second.Y + (second.Height - stateSize.Height) / 2),
            broken ? Ui.Warn : m.Enabled ? Ui.Enabled : Ui.Subtle, TextFormatFlags.NoPadding);
    }

    /// <summary>The mod's costume icon if it's decoded; otherwise starts decoding it and repaints when it's ready.</summary>
    Image? Icon(Mod m)
    {
        string? file = m.CostumeIconFile();
        if (file == null) return StockIcon(m);
        string key;
        try { key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; } catch { return null; }
        if (icons.TryGetValue(key, out var img)) return img;
        icons[key] = null;
        int size = (int)(64 * S);
        Task.Run(() => { try { return Ui.DdsThumb(file, size); } catch { return null; } }).ContinueWith(t =>
        {
            if (IsDisposed) { t.Result?.Dispose(); return; }
            icons[key] = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    /// <summary>The stock picture for a mod without one (decoded once per mod, in the background, one at a time).</summary>
    Image? StockIcon(Mod m)
    {
        if (Catalog is not { } cat) return null;
        string key = "stock|" + m.FolderName;
        if (icons.TryGetValue(key, out var img)) return img;
        icons[key] = null;
        int size = (int)(64 * S);
        Task.Run(() =>
        {
            try
            {
                lock (Ui.StockLock)
                    return cat.DefaultIconFor(m) is string tex && cat.Preview(Applier.IconPackages[0].File, tex) is { } p ? Ui.Thumb(p.Bgra, p.W, p.H, size) : null;
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (IsDisposed) { t.Result?.Dispose(); return; }
            icons[key] = t.Result;
            if (t.Result != null) Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return null;
    }

    /// <summary>What the part of the list under the mouse does (the tooltip text), or null.</summary>
    string? TipAt(Point p)
    {
        int i = IndexFromPoint(p);
        if (i < 0 || i >= Items.Count || !GetItemRectangle(i).Contains(p)) return null;
        var b = GetItemRectangle(i);
        if (Items[i] is ModGroup grp) return $"{grp.Name}: {grp.Count} mod(s). Click to {(grp.Collapsed ? "open" : "fold")} the group.";
        var m = (Mod)Items[i];
        if (CheckRect(b).Contains(p)) return m.Enabled ? "On. Click to turn it off, then Apply Changes." : "Off. Click to turn it on, then Apply Changes.";
        if (LockRect(b).Contains(p))
            return m.Lock != ModLock.None ? $"Locked at the {(m.Lock == ModLock.Top ? "top" : "bottom")}: it keeps its place, and new or moved mods can't pass it. Click to unlock."
                 : LockOffer(m) is var l && l != ModLock.None ? $"Click to lock it at the {(l == ModLock.Top ? "top" : "bottom")}: it keeps its place, and new or moved mods can't pass it."
                 : null;
        if (nexusParts.TryGetValue(i, out var np) && (np.Pill.Contains(p) || np.Mark.Contains(p)))
        {
            string have = (m.NexusLink?.Version ?? m.Manifest.Version ?? "?").TrimStart('v', 'V');
            if (UpdateFor?.Invoke(m) is string nv) return $"Update on Nexus: v{nv.TrimStart('v', 'V')} (you have v{have}). Click to update.";
            if (m.NexusModId is int id) return $"Linked to Nexus mod #{id} (you have v{have}). Right-click → Nexus for its page.";
        }
        if (regions.TryGetValue(i, out var r))
        {
            foreach (var (rect, tag) in r.Chips) if (rect.Contains(p)) return $"Tag \"{tag}\" ({Ui.TagDescription(m, tag)}). Click to show only mods with this tag.";
            if (r.Badges.Contains(p))
                return "Changes: " + m.Summary() + (Conflicted.Contains(m) ? ".\n! " + (ConflictText?.Invoke(m) ?? "Some of them are also changed by another enabled mod: the one higher in the list wins.") + "\nSelect it and open the Conflicts tab for the details." : ".");
        }
        return $"{m.Name}\n{m.Summary()}" + (Conflicted.Contains(m) ? "\n! " + (ConflictText?.Invoke(m) ?? "Conflicts with another enabled mod.") : "") +
               "\nDouble-click to edit. Right-click for tags and more." + (CanReorder?.Invoke() == true && m.Lock == ModLock.None ? " Drag to move it in the order." : "");
    }

    /// <summary>Test hook: the centre of item <paramref name="i"/>'s padlock or checkbox (client coordinates).</summary>
    public Point PartCentre(int i, bool padlock)
    {
        var r = padlock ? LockRect(GetItemRectangle(i)) : CheckRect(GetItemRectangle(i));
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        HideTip();
        int i = IndexFromPoint(e.Location);
        if (i < 0 || i >= Items.Count || !GetItemRectangle(i).Contains(e.Location)) { base.OnMouseDown(e); return; }
        var b = GetItemRectangle(i);
        if (Items[i] is ModGroup grp) { if (e.Button == MouseButtons.Left) GroupClicked?.Invoke(grp); return; }
        var m = (Mod)Items[i];
        if (e.Button == MouseButtons.Right)
        {
            SelectedIndex = i;
            MenuRequested?.Invoke(m, PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
        if (CheckRect(b).Contains(e.Location)) { SelectedIndex = i; CheckClicked?.Invoke(m); return; }
        if (LockRect(b).Contains(e.Location) && LockOffer(m) != ModLock.None) { SelectedIndex = i; LockClicked?.Invoke(m); return; }
        if (nexusParts.TryGetValue(i, out var np) && (np.Pill.Contains(e.Location) || np.Mark.Contains(e.Location)) && UpdateFor?.Invoke(m) != null)
        { SelectedIndex = i; UpdateClicked?.Invoke(m); return; }
        if (regions.TryGetValue(i, out var r))
        {
            foreach (var (rect, tag) in r.Chips)
                if (rect.Contains(e.Location)) { TagClicked?.Invoke(tag); return; }

        }
        // A press on the card itself can become a drag (priority view, unlocked mods).
        dragMod = CanReorder?.Invoke() == true && m.Lock == ModLock.None ? m : null;
        dragFrom = e.Location;
        leftDown = true;   // our own record: MouseEventArgs.Button on a move reflects the hardware state, not the message
        base.OnMouseDown(e);
    }

    void UpdateDrop(Point p)
    {
        int i = IndexFromPoint(p);
        if (i < 0 || i >= Items.Count) i = p.Y < 0 ? TopIndex : Items.Count - 1;
        var r = GetItemRectangle(i);
        bool below = p.Y > r.Top + r.Height / 2;
        if (i == dropItem && below == dropBelow) return;
        (dropItem, dropBelow) = (i, below);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        leftDown = false;
        if (!dragging) { dragMod = null; return; }
        var (m, i, below) = (dragMod, dropItem, dropBelow);
        EndDrag();
        if (m != null && i >= 0 && i < Items.Count && Items[i] is Mod target && target != m) Dropped?.Invoke(m, target, below);
    }

    void EndDrag()
    {
        dragging = false; dragMod = null; dropItem = -1; scrollTimer.Stop();
        Capture = false; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && dragging) { EndDrag(); e.Handled = true; return; }
        base.OnKeyUp(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragMod != null && leftDown)
        {
            var ds = SystemInformation.DragSize;
            if (!dragging && (Math.Abs(e.X - dragFrom.X) > ds.Width || Math.Abs(e.Y - dragFrom.Y) > ds.Height))
            { dragging = true; HideTip(); tipText = null; Capture = true; Cursor = Cursors.SizeNS; scrollTimer.Start(); }
            if (dragging) { UpdateDrop(e.Location); return; }
        }
        else if (dragging) EndDrag();
        int i = IndexFromPoint(e.Location);
        if (i >= 0 && (i >= Items.Count || !GetItemRectangle(i).Contains(e.Location))) i = -1;
        if (i != hover) { int old = hover; hover = i; if (old >= 0 && old < Items.Count) Invalidate(GetItemRectangle(old)); if (i >= 0 && i < Items.Count) Invalidate(GetItemRectangle(i)); }
        string? t = TipAt(e.Location);
        if (t != tipText) { HideTip(); tipText = t; if (t != null) tipTimer.Start(); }
    }

    void HideTip() { tipTimer.Stop(); if (tipShown) { tips.Hide(this); tipShown = false; } }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); HideTip(); tipText = null; if (hover >= 0 && hover < Items.Count) Invalidate(GetItemRectangle(hover)); hover = -1; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && SelectedItem is Mod m) { CheckClicked?.Invoke(m); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing) { if (disposing) { tipTimer.Dispose(); tips.Dispose(); scrollTimer.Dispose(); } base.Dispose(disposing); }
}

/// <summary>
/// The middle column of the Mods page: the selected mod's picture, big, with a strip of the pictures it can show below
/// (Kurt: authors choose one, users pick their own, and it's remembered). The candidates are the mod's own store images,
/// hero portraits, costume and inventory icons and the game's originals (PreviewImages). Which one shows: the user's pick,
/// else the mod's choice, else automatic. A click on a thumbnail picks it (Picked, saved per mod on this PC); a right-click
/// offers going back to the mod's choice. Decoded in the background (stock images one at a time, Ui.StockLock).
/// </summary>
sealed class StorePreview : Control
{
    Mod? mod;
    List<PreviewCandidate> items = [];
    int index = -1;
    // The 3D view (Kurt): the mod's skeletal meshes (ModMeshes), shown in MPM's MeshViewer where the picture goes.
    List<MeshRef> meshes = [];
    bool show3D;
    int meshIndex;
    MhoPackageModifier.Gui.MeshViewer? viewer;
    readonly Dictionary<string, ModMeshes.Loaded?> meshCache = [];
    Rectangle meshPrev, meshNext;
    // Animation (Kurt: pick one for the 3D view): the mesh's animations, the one playing, and the drop-down.
    List<AnimRef> anims = [];
    string? wantedAnim;                           // from the pick's "@animation" part
    AnimExportCli.Animation.BoneAnimation? playing;
    MeshAnimator? animator;
    float playFrames, playSeconds;
    readonly System.Diagnostics.Stopwatch playClock = new();
    readonly System.Windows.Forms.Timer playTimer = new() { Interval = 33 };
    ComboBox? animBox;
    bool fillingAnims;
    // Play / pause (an animation loads paused on its first frame), loop (remembered), reset view (the default camera).
    Button? playBtn, loopBtn, restBtn;
    bool paused = true;
    double playTime, lastTick;
    static string MeshPart(string key) { int at = key.IndexOf('@'); return at < 0 ? key : key[..at]; }
    static string? AnimPart(string? key) { int at = key?.IndexOf('@') ?? -1; return at < 0 ? null : key![(at + 1)..]; }
    bool MeshOk => meshIndex >= 0 && meshIndex < meshes.Count;
    string CurrentMeshKey() => !MeshOk ? "" : meshes[meshIndex].Key + (playing != null && animBox?.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? "@" + anims[animBox.SelectedIndex - 1].Name : "");
    int Offset => meshes.Count > 0 ? 1 : 0;   // strip tile 0 is "3D" when the mod has meshes
    int Tiles => items.Count + Offset;
    /// <summary>The game's CookedPCConsole (textures streamed from the .tfc caches).</summary>
    public string? CookedFolder { get; set; }
    Image? image;
    int request;
    int scroll;   // the strip's scroll offset (pixels)
    readonly Dictionary<string, Image?> thumbs = new(StringComparer.OrdinalIgnoreCase);   // folder|key → thumbnail (null: loading / none)
    readonly List<(Rectangle Rect, int Index)> thumbRects = [];
    Rectangle leftArrow, rightArrow, strip;
    int hoverThumb = -1;
    readonly ToolTip tips;
    readonly Font titleFont = Ui.Bold(8.5f), smallFont = Ui.Regular(8.25f);
    /// <summary>Stock pictures (the game's originals); null: none.</summary>
    public StockCatalog? Catalog { get; set; }
    /// <summary>The user picked a picture for a mod (a key), or went back to the mod's choice (null).</summary>
    public event Action<Mod, string?>? Picked;

    public StorePreview()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        tips = Ui.NewTips(() => null);
    }

    float S => DeviceDpi / 96f;
    int ThumbSize => (int)(56 * S);

    public Mod? Mod
    {
        get => mod;
        set
        {
            if (value != null && mod != null && value.FolderName == mod.FolderName && value.LocalPreview == mod.LocalPreview && value.Manifest.PreviewImage == mod.Manifest.PreviewImage && items.Count > 0)
            { mod = value; return; }   // the same mod after a reload: keep the pictures
            mod = value;
            items = []; meshes = []; index = -1; meshIndex = 0; scroll = 0; show3D = false;
            if (viewer != null) viewer.Visible = false;
            StopAnimation(); anims = []; animator = null; wantedAnim = null;
            HideAnimControls();
            var old = image; image = null; old?.Dispose();
            Invalidate();
            if (value == null) return;
            int req = ++request;
            var cat = Catalog; var m = value;
            Task.Run(() =>
            {
                List<PreviewCandidate> pics; List<MeshRef> ms;
                try { lock (Ui.StockLock) pics = PreviewImages.For(m, cat); } catch { pics = []; }
                try { ms = ModMeshes.List(m); } catch { ms = []; }
                return (pics, ms);
            }).ContinueWith(t =>
            {
                if (IsDisposed || req != request) return;
                (items, meshes) = t.Result;
                Resolve();
                LoadThumbs();
                Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    Image? Decode(PreviewCandidate c, int size)
    {
        try
        {
            if (c.FromMod) return c.File != null ? Ui.DdsThumb(c.File, size) : null;
            lock (Ui.StockLock) return Catalog?.Preview(c.Package, c.Texture) is { } p ? Ui.Thumb(p.Bgra, p.W, p.H, size) : null;
        }
        catch { return null; }
    }

    void LoadBig()
    {
        var old = image; image = null; old?.Dispose();
        Invalidate();
        if (index < 0 || index >= items.Count) return;
        int req = ++request;
        var c = items[index];
        Task.Run(() => Decode(c, 1024)).ContinueWith(t =>
        {
            if (IsDisposed || req != request) { t.Result?.Dispose(); return; }
            image = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void LoadThumbs()
    {
        if (mod is not { } m || items.Count < 2) return;
        var todo = items.Where(c => !thumbs.ContainsKey(m.FolderName + "|" + c.Key)).ToList();
        foreach (var c in todo) thumbs[m.FolderName + "|" + c.Key] = null;
        int size = ThumbSize * 2;
        Task.Run(() => todo.Select(c => (Key: m.FolderName + "|" + c.Key, Img: Decode(c, size))).ToList()).ContinueWith(t =>
        {
            if (IsDisposed) { foreach (var x in t.Result) x.Img?.Dispose(); return; }
            foreach (var (k, img) in t.Result) thumbs[k] = img;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ScrollIntoView()
    {
        int tile = show3D ? 0 : index + Offset;
        if (tile < 0 || strip.Width <= 0) return;
        int step = ThumbSize + (int)(6 * S), x = tile * step;
        if (x < scroll) scroll = x;
        else if (x + ThumbSize > scroll + strip.Width) scroll = x + ThumbSize - strip.Width;
    }

    int MaxScroll => Math.Max(0, Tiles * (ThumbSize + (int)(6 * S)) - (int)(6 * S) - strip.Width);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.PaintGradient(g, this, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = (int)(6 * S);
        var title = new Rectangle(pad, (int)(4 * S), Width - 2 * pad, (int)(20 * S));
        TextRenderer.DrawText(g, "PREVIEW", titleFont, title, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Card at the store images' 300:420 aspect, as wide as the column allows; caption and strip below.
        bool showStrip = Tiles > 1;
        int stripH = showStrip ? ThumbSize + (int)(12 * S) : 0;
        int captionH = (int)((show3D ? 70 : 40) * S);
        int w = Width - 2 * pad, h = (int)(w * 420f / 300f);
        int maxH = Height - title.Bottom - (int)(8 * S) - captionH - stripH;
        if (h > maxH && maxH > 0) { h = maxH; w = (int)(h * 300f / 420f); }
        if (w <= 0 || h <= 0) return;
        var card = new Rectangle((Width - w) / 2, title.Bottom + (int)(4 * S), w, h);
        if (show3D && viewer != null)
        {
            // The 3D view fills the card; the caption steps through the meshes.
            var inner = Rectangle.Inflate(card, -(int)(2 * S), -(int)(2 * S));
            if (viewer.Bounds != inner) viewer.Bounds = inner;
            if (!viewer.Visible) viewer.Visible = true;
            using (var path = Ui.Round(card, 5 * S)) using (var fill = new SolidBrush(Ui.Card)) g.FillPath(fill, path);
        }
        else using (var path = Ui.Round(card, 5 * S))
        {
            using (var fill = new SolidBrush(Ui.Card)) g.FillPath(fill, path);
            if (image != null)
            {
                var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Min((float)card.Width / image.Width, (float)card.Height / image.Height);   // (pictures only; the 3D view is its own control)
                float iw = image.Width * k, ih = image.Height * k;
                g.DrawImage(image, card.X + (card.Width - iw) / 2, card.Y + (card.Height - ih) / 2, iw, ih);
                g.Clip = clip;
            }
            else
            {
                string text = mod == null ? "" : items.Count == 0 && index < 0 && request > 0 ? "No Picture\nfor This Mod" : "Loading…";
                TextRenderer.DrawText(g, text, smallFont, card, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            }
        }
        // Caption (3D): "◀ mesh ▶", then the package and whose choice it is.
        meshPrev = meshNext = Rectangle.Empty;
        if (mod != null && show3D && meshIndex >= 0 && meshIndex < meshes.Count)
        {
            var r = meshes[meshIndex];
            var cap = new Rectangle(pad, card.Bottom + (int)(4 * S), Width - 2 * pad, (int)(18 * S));
            if (meshes.Count > 1)
            {
                meshPrev = new Rectangle(cap.X, cap.Y, (int)(24 * S), cap.Height);
                meshNext = new Rectangle(cap.Right - (int)(24 * S), cap.Y, (int)(24 * S), cap.Height);
                TextRenderer.DrawText(g, "◀", smallFont, meshPrev, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "▶", smallFont, meshNext, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            var mid = Rectangle.FromLTRB(cap.X + (int)(26 * S), cap.Y, cap.Right - (int)(26 * S), cap.Bottom);
            TextRenderer.DrawText(g, meshes.Count > 1 ? $"{r.Name}  ({meshIndex + 1} of {meshes.Count})" : r.Name, smallFont, mid, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview != null && MeshPart(mod.LocalPreview) == r.Key ? "Your Pick" : mod.Manifest.PreviewImage != null && MeshPart(mod.Manifest.PreviewImage) == r.Key ? "The Mod's Choice" : "3D View";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"3D  ·  {r.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}  ·  {whose}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (animBox != null && playBtn != null && loopBtn != null && restBtn != null)
            {
                int bh = animBox.Height, gap = (int)(4 * S), wPlay = (int)(30 * S), wLoop = (int)(56 * S), wRest = (int)(72 * S);
                int y = cap.Bottom + (int)(4 * S);
                var ab = new Rectangle(card.X, y, card.Width - wPlay - wLoop - wRest - 3 * gap, bh);
                if (animBox.Bounds != ab) animBox.Bounds = ab;
                var pb = new Rectangle(ab.Right + gap, y, wPlay, bh); if (playBtn.Bounds != pb) playBtn.Bounds = pb;
                var lb = new Rectangle(pb.Right + gap, y, wLoop, bh); if (loopBtn.Bounds != lb) loopBtn.Bounds = lb;
                var rb = new Rectangle(lb.Right + gap, y, wRest, bh); if (restBtn.Bounds != rb) restBtn.Bounds = rb;
                foreach (Control c in new Control[] { animBox, playBtn, loopBtn, restBtn }) if (!c.Visible) c.Visible = true;
            }
        }
        // Caption: the texture, where it's from, and whose choice it is.
        else if (mod != null && index >= 0 && index < items.Count)
        {
            var c = items[index];
            var cap = new Rectangle(pad, card.Bottom + (int)(4 * S), Width - 2 * pad, (int)(18 * S));
            TextRenderer.DrawText(g, c.Texture, smallFont, cap, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview == c.Key ? "Your Pick" : mod.Manifest.PreviewImage == c.Key ? "The Mod's Choice" : "Chosen Automatically";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"{c.Source}  ·  {whose}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // The strip: thumbnails, with arrows when they don't all fit.
        thumbRects.Clear();
        leftArrow = rightArrow = strip = Rectangle.Empty;
        if (!showStrip || mod == null) return;
        int top = Height - stripH + (int)(4 * S), arrowW = (int)(16 * S);
        strip = new Rectangle(pad, top, Width - 2 * pad, ThumbSize);
        int total = Tiles * (ThumbSize + (int)(6 * S)) - (int)(6 * S);
        if (total > strip.Width)
        {
            leftArrow = new Rectangle(pad, top, arrowW, ThumbSize);
            rightArrow = new Rectangle(Width - pad - arrowW, top, arrowW, ThumbSize);
            strip = new Rectangle(leftArrow.Right + (int)(4 * S), top, rightArrow.Left - leftArrow.Right - (int)(8 * S), ThumbSize);
            scroll = Math.Clamp(scroll, 0, MaxScroll);
            void Arrow(Rectangle r, bool left, bool on)
            {
                using var b = new SolidBrush(on ? Ui.Text : Color.FromArgb(70, 255, 255, 255));
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = 5 * S;
                g.FillPolygon(b, left ? [new PointF(cx + a / 2, cy - a), new PointF(cx - a / 2, cy), new PointF(cx + a / 2, cy + a)]
                                      : [new PointF(cx - a / 2, cy - a), new PointF(cx + a / 2, cy), new PointF(cx - a / 2, cy + a)]);
            }
            Arrow(leftArrow, true, scroll > 0); Arrow(rightArrow, false, scroll < MaxScroll);
        }
        else { scroll = 0; strip = new Rectangle((Width - total) / 2, top, total, ThumbSize); }
        var oldClip = g.Clip;
        g.SetClip(strip);
        for (int ti = 0; ti < Tiles; ti++)
        {
            var r = new Rectangle(strip.X + ti * (ThumbSize + (int)(6 * S)) - scroll, top, ThumbSize, ThumbSize);
            if (r.Right < strip.Left || r.Left > strip.Right) continue;
            thumbRects.Add((r, ti));
            if (ti < Offset)
            {
                // The 3D tile.
                using var p3 = Ui.Round(r, 4 * S);
                using (var fill = new SolidBrush(ti == hoverThumb ? Ui.CardHover : Ui.Card)) g.FillPath(fill, p3);
                TextRenderer.DrawText(g, "3D", Ui.Heavy(11f), r, show3D ? Ui.Text : Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using var pen3 = new Pen(show3D ? Ui.Accent : Ui.Line, show3D ? Math.Max(2f, 2f * S) : 1f);
                g.DrawPath(pen3, p3);
                continue;
            }
            int i = ti - Offset;
            using (var path = Ui.Round(r, 4 * S))
            {
                using (var fill = new SolidBrush(ti == hoverThumb ? Ui.CardHover : Ui.Card)) g.FillPath(fill, path);
                if (thumbs.GetValueOrDefault(mod.FolderName + "|" + items[i].Key) is Image t)
                {
                    var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float k = Math.Min((float)r.Width / t.Width, (float)r.Height / t.Height);
                    g.DrawImage(t, r.X + (r.Width - t.Width * k) / 2, r.Y + (r.Height - t.Height * k) / 2, t.Width * k, t.Height * k);
                    g.Clip = clip;
                }
                // The game's originals get a small "G" corner mark; the one showing gets an accent border.
                if (!items[i].FromMod)
                {
                    var mark = new Rectangle(r.Right - (int)(14 * S), r.Bottom - (int)(14 * S), (int)(12 * S), (int)(12 * S));
                    using var mb = new SolidBrush(Color.FromArgb(200, 20, 24, 32)); g.FillEllipse(mb, mark);
                    TextRenderer.DrawText(g, "G", Ui.Heavy(6.5f), mark, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                bool on = !show3D && i == index;
                using var pen = new Pen(on ? Ui.Accent : Ui.Line, on ? Math.Max(2f, 2f * S) : 1f);
                g.DrawPath(pen, path);
            }
        }
        g.Clip = oldClip;
    }

    /// <summary>The user's pick changed (saved by the window): show what's chosen now.</summary>
    public void ChoiceChanged() { if (mod != null) Resolve(); }

    /// <summary>What shows: the user's pick, else the mod's choice (a picture or a mesh), else a picture chosen automatically.</summary>
    void Resolve()
    {
        if (mod == null) return;
        string? Offered(string? k) => k == null ? null : k.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase)
            ? (meshes.Any(x => x.Key.Equals(MeshPart(k), StringComparison.OrdinalIgnoreCase)) ? k : null)
            : (items.Any(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase)) ? k : null);
        string? key = Offered(mod.LocalPreview) ?? Offered(mod.Manifest.PreviewImage) ?? PreviewImages.Automatic(items) ?? meshes.FirstOrDefault()?.Key;
        if (key != null && key.StartsWith("mesh:", StringComparison.OrdinalIgnoreCase))
        {
            int mi = meshes.FindIndex(x => x.Key.Equals(MeshPart(key), StringComparison.OrdinalIgnoreCase));
            bool changed = !show3D || mi != meshIndex;
            show3D = true; meshIndex = Math.Max(0, mi); index = -1;
            wantedAnim = AnimPart(key);
            if (changed) LoadMesh();
        }
        else
        {
            int i = items.FindIndex(x => x.Key == key);
            bool changed = show3D || i != index;
            show3D = false; index = i;
            if (viewer != null) viewer.Visible = false;
            StopAnimation(); HideAnimControls();
            if (changed) LoadBig();
        }
        ScrollIntoView();
        Invalidate();
    }

    /// <summary>Shows meshes[meshIndex] in the 3D view (read in the background; the last few are kept).</summary>
    void LoadMesh()
    {
        if (mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        if (viewer == null)
        {
            viewer = new MhoPackageModifier.Gui.MeshViewer { Compact = true, Background = Ui.Card, BackColor = Ui.Card, Visible = false };
            viewer.ViewChanged += () => { if (mod != null && meshIndex >= 0 && meshIndex < meshes.Count) PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState); };
            Controls.Add(viewer);
        }
        var r = meshes[meshIndex];
        viewer.Visible = true;
        StopAnimation(); anims = []; animator = null; FillAnims();
        Invalidate();
        if (meshCache.TryGetValue(r.File + "|" + r.Export + "|" + File.GetLastWriteTimeUtc(r.File).Ticks, out var hit) && hit != null) { Show(hit); return; }
        viewer.ShowMessage("Loading the 3D view…");
        int req = ++request;
        string? cooked = CookedFolder;
        string key = r.File + "|" + r.Export + "|" + File.GetLastWriteTimeUtc(r.File).Ticks;
        Task.Run(() => { try { var l = ModMeshes.Load(r, cooked, out string why); return (l, why); } catch (Exception ex) { return ((ModMeshes.Loaded?)null, ex.Message); } }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || viewer == null) return;
            var (loaded, why) = t.Result;
            if (meshCache.Count > 6) meshCache.Clear();
            meshCache[key] = loaded;
            if (loaded == null) viewer.ShowMessage($"{r.Name} can't be shown in 3D ({why}).");
            else Show(loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        void Show(ModMeshes.Loaded l)
        {
            viewer!.ShowMesh(l.Name, l.Positions, l.Normals, l.Uv, l.Indices, l.TriangleSection, l.Textures, l.Info);
            if (mod != null && StartView(r) is { } saved) viewer.ViewState = saved;
            animator = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences);
            var m = mod; string? cooked2 = CookedFolder;
            var pkgs = m == null ? [] : m.Manifest.UpkReplacements.Select(f => (f, Path.Combine(m.Folder, f))).ToList();
            int req2 = request;
            Task.Run(() => { try { return ModAnimations.For(r, l.Bones, pkgs, cooked2); } catch { return []; } }).ContinueWith(t =>
            {
                if (IsDisposed || req2 != request || mod != m) return;
                anims = t.Result;
                FillAnims();
                int want = wantedAnim == null ? -1 : anims.FindIndex(a => a.Name.Equals(wantedAnim, StringComparison.OrdinalIgnoreCase));
                if (want >= 0 && animBox != null) animBox.SelectedIndex = want + 1;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>The animation drop-down: "Rest Pose", then the mesh's animations (shown under the 3D caption).</summary>
    void FillAnims()
    {
        if (animBox == null)
        {
            animBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Ui.Card, ForeColor = Ui.Text, Font = Ui.Regular(9f), Visible = false, MaxDropDownItems = 24, DrawMode = DrawMode.OwnerDrawFixed };
            // Drawn here: Windows paints a drop-down list's face and items in system colours otherwise (white on the dark card).
            animBox.DrawItem += (_, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(sel ? Ui.CardSelected : Ui.Card)) e.Graphics.FillRectangle(bg, e.Bounds);
                TextRenderer.DrawText(e.Graphics, animBox.Items[e.Index]?.ToString(), animBox.Font, Rectangle.Inflate(e.Bounds, -4, 0),
                    animBox.Enabled ? Ui.Text : Ui.DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            Ui.Tip(animBox, "Play one of this mesh's animations in the 3D view (it loops). Remembered with the mesh for this mod.");
            animBox.SelectedIndexChanged += (_, _) => { if (!fillingAnims) PlaySelected(); };
            Controls.Add(animBox);
            playBtn = Ui.FlatButton("▶", TogglePlay, "Play or pause the animation (it loads paused on its first frame).");
            loopBtn = Ui.FlatButton("⟳ Loop", () => { PreviewViews.Loop = !PreviewViews.Loop; UpdateButtons(); }, "Loop the animation, or play it once and stop on its last frame. Remembered.");
            restBtn = Ui.FlatButton("Reset View", ResetView, "Back to the mod's own view of the model, or the default one (your turned, zoomed or panned view is saved per mesh; this forgets it).");
            foreach (var b in new[] { playBtn, loopBtn, restBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
        }
        fillingAnims = true;
        animBox.BeginUpdate();
        animBox.Items.Clear();
        animBox.Items.Add(anims.Count > 0 ? $"Rest Pose  ·  {anims.Count} animations" : show3D ? "Rest Pose  ·  (looking for animations…)" : "Rest Pose");
        foreach (var a in anims) animBox.Items.Add(a.Name);
        animBox.SelectedIndex = 0;
        animBox.EndUpdate();
        fillingAnims = false;
        animBox.Enabled = anims.Count > 0;
        UpdateButtons();
        Invalidate();
    }

    /// <summary>The buttons' state: play shows ▶ or ❚❚; loop is filled (accent) when on; both need an animation.</summary>
    void UpdateButtons()
    {
        if (playBtn == null || loopBtn == null) return;
        bool has = playing != null;
        playBtn.Text = has && !paused ? "❚❚" : "▶";
        playBtn.Enabled = has; loopBtn.Enabled = has;
        bool on = PreviewViews.Loop;
        loopBtn.Tag = on ? "accent" : "flat";
        loopBtn.BackColor = on ? Ui.Accent : Ui.Bar; loopBtn.ForeColor = on ? Color.White : Ui.Text;
        loopBtn.FlatAppearance.BorderColor = on ? Ui.Accent : Ui.Line;
        loopBtn.FlatAppearance.MouseOverBackColor = on ? Ui.AccentHover : Ui.CardHover;
        loopBtn.Invalidate(); playBtn.Invalidate();
    }

    void TogglePlay()
    {
        if (playing == null) return;
        if (paused && !PreviewViews.Loop && playTime >= playSeconds) playTime = 0;   // played once to the end: start over
        paused = !paused;
        if (!paused) { lastTick = playClock.Elapsed.TotalSeconds; playClock.Start(); if (!playTimer.Enabled) { playTimer.Tick -= PlayTick; playTimer.Tick += PlayTick; playTimer.Start(); } }
        else playTimer.Stop();
        UpdateButtons();
    }

    /// <summary>The view a mesh starts from: the one turned to on this PC, else the mod author's (manifest PreviewViews).</summary>
    float[]? StartView(MeshRef r) =>
        mod == null ? null : PreviewViews.Get(PreviewViews.Key(mod, r)) ?? (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var v) ? v : null);

    /// <summary>Back to the mod author's view when it has one, else the default framing; this PC's view is forgotten.</summary>
    void ResetView()
    {
        if (viewer == null || mod == null || meshIndex < 0 || meshIndex >= meshes.Count) return;
        var r = meshes[meshIndex];
        PreviewViews.Set(PreviewViews.Key(mod, r), null);
        if (mod.Manifest.PreviewViews is { } pv && pv.TryGetValue(r.Key, out var author)) viewer.ViewState = author;
        else viewer.ResetView();
    }

    void PlaySelected()
    {
        if (animBox == null || mod == null || animator == null || viewer == null) return;
        int i = animBox.SelectedIndex - 1;
        StopAnimation();
        if (i < 0 || i >= anims.Count)
        {
            animator.Pose(null, 0); viewer.UpdateGeometry(animator.Positions);
            if (!fillingAnims && MeshOk) Picked?.Invoke(mod, meshes[meshIndex].Key);
            return;
        }
        var a = anims[i];
        int req = request;
        Task.Run(() => ModAnimations.Load(a)).ContinueWith(t =>
        {
            if (IsDisposed || req != request || animBox == null || animBox.SelectedIndex - 1 != i || t.Result == null) return;
            playing = t.Result;
            (playFrames, playSeconds) = MeshAnimator.Span(playing);
            if (mod != null && MeshOk && StartView(meshes[meshIndex]) == null) viewer.ZoomOut(1.25f);   // room for reaching and lunging
            paused = true; playTime = 0;
            animator.Pose(playing, 0); viewer.UpdateGeometry(animator.Positions);
            UpdateButtons();
            if (mod != null && MeshOk) Picked?.Invoke(mod, CurrentMeshKey());
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void PlayTick(object? sender, EventArgs e)
    {
        if (playing == null || animator == null || viewer == null || !show3D || !Visible) { StopAnimation(); return; }
        if (paused) return;
        double now = playClock.Elapsed.TotalSeconds;
        playTime += now - lastTick; lastTick = now;
        float frame;
        if (playSeconds <= 0 || playFrames <= 0) frame = 0;
        else if (PreviewViews.Loop) frame = (float)(playTime % playSeconds / playSeconds * playFrames);
        else if (playTime >= playSeconds) { playTime = playSeconds; frame = playFrames; paused = true; playTimer.Stop(); UpdateButtons(); }
        else frame = (float)(playTime / playSeconds * playFrames);
        animator.Pose(playing, frame);
        viewer.UpdateGeometry(animator.Positions);
    }

    /// <summary>
    /// --preview-selftest: drives the 3D view's controls as a user would (use a scratch library: picks are saved). An
    /// animation loads paused on frame 0; Play advances it and moves the mesh; Pause holds it; without Loop it stops on
    /// its last frame; Reset View forgets the saved camera.
    /// </summary>
    internal async Task<List<string>> SelfTest()
    {
        var log = new List<string>();
        void Check(string what, bool ok) => log.Add($"{(ok ? "ok  " : "FAIL")} {what}");
        async Task Wait(Func<bool> until, int ms = 10000) { for (int t = 0; t < ms && !until(); t += 50) { await Task.Delay(50); Application.DoEvents(); } }
        async Task Idle(int ms) { for (int t = 0; t < ms; t += 20) { await Task.Delay(20); Application.DoEvents(); } }
        if (mod == null || meshes.Count == 0) { Check("the mod has a mesh", false); return log; }
        // 0.33.0 crash: an index left from a mod with more meshes, then a click on the 3D tile.
        if (!show3D && thumbRects.Any(t => t.Index == 0))
        {
            meshIndex = meshes.Count + 2;
            var tile = thumbRects.First(t => t.Index == 0).Rect;
            string? crash = null;
            try { OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0)); } catch (Exception ex) { crash = ex.GetType().Name; }
            Check("3D tile with an out-of-range mesh index doesn't crash" + (crash != null ? $" ({crash})" : ""), crash == null && MeshOk);
        }
        if (!show3D) { OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, thumbRects.First(t => t.Index == 0).Rect.X + 4, thumbRects.First(t => t.Index == 0).Rect.Y + 4, 0)); }
        await Wait(() => show3D && animator != null && anims.Count > 0 && animBox != null);
        Check($"3D view with {anims.Count} animation(s)", show3D && animator != null && anims.Count > 0);
        if (animBox == null || playBtn == null || loopBtn == null || restBtn == null || animator == null || viewer == null) return log;
        bool loopWas = PreviewViews.Loop;
        PreviewViews.Loop = true; UpdateButtons();
        animBox.SelectedIndex = 1;
        await Wait(() => playing != null);
        Check("an animation loads paused on frame 0 (▶ shown)", playing != null && paused && playTime == 0 && playBtn.Text == "▶");
        var p0 = (System.Numerics.Vector3[])animator.Positions.Clone();
        playBtn.PerformClick();
        await Idle(Math.Min(700, (int)(playSeconds * 1000 * 0.6)));
        Check($"Play runs it (time {playTime:0.00} s, ❚❚ shown) and the mesh moves", !paused && playTime > 0.1 && playBtn.Text == "❚❚" && animator.Positions.Where((p, i) => System.Numerics.Vector3.Distance(p, p0[i]) > 0.01f).Any());
        playBtn.PerformClick();
        double held = playTime;
        await Idle(300);
        Check("Pause holds the frame", paused && playTime == held);
        PreviewViews.Loop = false; UpdateButtons();
        playTime = Math.Max(0, playSeconds - 0.15);
        playBtn.PerformClick();
        await Idle(600);
        Check($"Loop off: it stops on its last frame (time {playTime:0.00} of {playSeconds:0.00} s, ▶ shown)", paused && Math.Abs(playTime - playSeconds) < 1e-6 && playBtn.Text == "▶");
        playBtn.PerformClick();
        await Idle(150);
        Check("Play after the end starts over", !paused && playTime < playSeconds * 0.5);
        playBtn.PerformClick();
        string key = PreviewViews.Key(mod, meshes[meshIndex]);
        viewer.ViewState = [1f, 0.2f, 1.5f, 0f, 0f, 0f];
        PreviewViews.Set(key, viewer.ViewState);
        Check("a turned view is saved for the mesh", PreviewViews.Get(key) is { Length: 6 });
        restBtn.PerformClick();
        Check("Reset View resets the camera and forgets the saved view", PreviewViews.Get(key) == null && Math.Abs(viewer.ViewState[2] - 2.6f) < 0.01f);
        // A mod carrying its author's view (an exported one): Reset View goes back to that view, not the default framing.
        var authorViews = mod.Manifest.PreviewViews;
        mod.Manifest.PreviewViews = new() { [meshes[meshIndex].Key] = [0.5f, 0.1f, 2.0f, 0f, 0f, 0f] };
        viewer.ViewState = [1f, 0.2f, 1.2f, 0f, 0f, 0f];
        restBtn.PerformClick();
        Check("with the mod's own view, Reset View goes to it", Math.Abs(viewer.ViewState[2] - 2.0f) < 0.01f && Math.Abs(viewer.ViewState[0] - 0.5f) < 0.01f);
        mod.Manifest.PreviewViews = authorViews;
        PreviewViews.Loop = loopWas; UpdateButtons();
        return log;
    }

    void HideAnimControls() { foreach (Control? c in new Control?[] { animBox, playBtn, loopBtn, restBtn }) if (c != null) c.Visible = false; }

    void StopAnimation() { playTimer.Stop(); playing = null; playClock.Reset(); paused = true; playTime = 0; UpdateButtons(); }

    int ThumbAt(Point p)
    {
        foreach (var (r, i) in thumbRects) if (r.Contains(p) && strip.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (mod == null) return;
        if (leftArrow.Contains(e.Location)) { scroll = Math.Max(0, scroll - (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (rightArrow.Contains(e.Location)) { scroll = Math.Min(MaxScroll, scroll + (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (e.Button == MouseButtons.Left && (meshPrev.Contains(e.Location) || meshNext.Contains(e.Location)) && meshes.Count > 1)
        {
            meshIndex = (meshIndex + (meshNext.Contains(e.Location) ? 1 : meshes.Count - 1)) % meshes.Count;
            wantedAnim = null;
            LoadMesh();
            Picked?.Invoke(mod, meshes[meshIndex].Key);
            return;
        }
        int ti = ThumbAt(e.Location);
        if (ti < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            if (ti < Offset)
            {
                if (meshIndex < 0 || meshIndex >= meshes.Count) meshIndex = 0;   // 0.33.0 crash: an index left from a mod with more meshes
                if (show3D && mod.LocalPreview != null && MeshPart(mod.LocalPreview) == meshes[meshIndex].Key) return;
                show3D = true; index = -1;
                var old = image; image = null; old?.Dispose();
                LoadMesh();
                Picked?.Invoke(mod, meshes[meshIndex].Key);
                return;
            }
            int i = ti - Offset;
            if (!show3D && i == index && mod.LocalPreview == items[i].Key) return;
            show3D = false; index = i;
            if (viewer != null) viewer.Visible = false;
            LoadBig();
            Picked?.Invoke(mod, items[i].Key);
        }
        else if (e.Button == MouseButtons.Right && mod.LocalPreview != null)
        {
            var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f), RenderMode = ToolStripRenderMode.System };
            menu.Items.Add(mod.Manifest.PreviewImage != null ? "Use the Mod's Choice" : "Choose Automatically Again", null, (_, _) => Picked?.Invoke(mod, null));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(this, e.Location);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (strip.IsEmpty || MaxScroll == 0) return;
        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * (ThumbSize + (int)(6 * S)), 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = ThumbAt(e.Location);
        Cursor = i >= 0 || leftArrow.Contains(e.Location) || rightArrow.Contains(e.Location) || meshPrev.Contains(e.Location) || meshNext.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (i == hoverThumb) return;
        hoverThumb = i; Invalidate();
        if (i >= 0 && i < Offset && mod != null)
            tips.Show($"3D view of the mod's meshes ({meshes.Count}).\nDrag to turn, wheel to zoom, double-click to frame; ◀ ▶ under it step through the meshes.\nClick to show it here (remembered for this mod).", this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
        else if (i >= 0 && mod != null)
        {
            var c = items[i - Offset];
            string whose = mod.Manifest.PreviewImage == c.Key ? "\nThe mod's choice." : "";
            tips.Show($"{c.Texture}\n{c.Source}{whose}\nClick to show it here (remembered for this mod)." + (mod.LocalPreview != null ? "\nRight-click: back to the mod's choice." : ""), this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
        }
        else tips.Hide(this);
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverThumb = -1; tips.Hide(this); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { playTimer.Dispose(); image?.Dispose(); foreach (var t in thumbs.Values) t?.Dispose(); tips.Dispose(); viewer?.Dispose(); animBox?.Dispose(); playBtn?.Dispose(); loopBtn?.Dispose(); restBtn?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A split container that shows the window gradient behind its panels, with a faint divider line.</summary>
sealed class GradientSplit : SplitContainer
{
    public GradientSplit() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Ui.PaintGradient(e.Graphics, this, ClientRectangle);
        var r = SplitterRectangle;
        using var pen = new Pen(Color.FromArgb(55, 255, 255, 255));
        if (Orientation == Orientation.Vertical) e.Graphics.DrawLine(pen, r.X + r.Width / 2, r.Top, r.X + r.Width / 2, r.Bottom);
        else e.Graphics.DrawLine(pen, r.Left, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2);
    }
}

/// <summary>A table whose empty area shows the window gradient (rows stay solid surfaces).</summary>
sealed class GradientGrid : DataGridView
{
    protected override void PaintBackground(Graphics graphics, Rectangle clipBounds, Rectangle gridBounds) =>
        Ui.PaintGradient(graphics, this, clipBounds);
}

/// <summary>A plain list of names with roomier rows (stock texture names).</summary>
sealed class NameList : ListBox
{
    public NameList()
    {
        DrawMode = DrawMode.OwnerDrawFixed; BorderStyle = BorderStyle.None; IntegralHeight = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = Math.Min(255, (int)(26 * DeviceDpi / 96f)); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) { using var g = Graphics.FromHdc(m.WParam); Ui.PaintGradient(g, this, ClientRectangle); m.Result = 1; return; }
        base.WndProc(ref m);
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        if (sel) { using var b = new SolidBrush(Ui.CardSelected); e.Graphics.FillRectangle(b, e.Bounds); }
        else Ui.PaintGradient(e.Graphics, this, e.Bounds);
        string text = GetItemText(Items[e.Index]) ?? "";
        var r = new Rectangle(e.Bounds.X + (int)(8 * DeviceDpi / 96f), e.Bounds.Y, e.Bounds.Width - (int)(12 * DeviceDpi / 96f), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text, Font, r, text.StartsWith('(') || text == "Loading…" ? Ui.Subtle : Ui.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Flat tabs (text with an accent underline) over switching pages, as in MHModManager.</summary>
sealed class FlatTabs : UserControl
{
    readonly FlowLayoutPanel strip = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(4, 2, 4, 0) };
    readonly Panel body = new() { Dock = DockStyle.Fill };
    readonly List<(Label Tab, Control Page)> tabs = [];
    public int SelectedIndex { get; private set; } = -1;
    public int Count => tabs.Count;
    public string TitleAt(int i) => tabs[i].Tab.Text;
    public void SetTitle(int i, string title) => tabs[i].Tab.Text = title;
    public Control PageAt(int i) => tabs[i].Page;
    /// <summary>Tab font size (the main window's top-level tabs are larger).</summary>
    public float TabPoints { get; set; } = 9.75f;
    public event Action<int>? SelectedChanged;

    public FlatTabs()
    {
        Controls.Add(body); Controls.Add(strip);
        strip.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(60, 255, 255, 255)); e.Graphics.DrawLine(pen, 0, strip.Height - 1, strip.Width, strip.Height - 1); };
    }

    /// <summary>
    /// Removes and disposes every tab and page. A removed control that isn't disposed keeps its window, and once the
    /// garbage collector takes the .NET side, the next message to that window kills the process ("callback was made on a
    /// garbage collected delegate": Kurt's padlock-click crash in 0.17.0, reproduced by --ui-selftest).
    /// </summary>
    public void Clear()
    {
        var old = strip.Controls.Cast<Control>().Concat(body.Controls.Cast<Control>()).ToList();
        strip.Controls.Clear(); body.Controls.Clear(); tabs.Clear(); SelectedIndex = -1;
        foreach (var c in old) c.Dispose();
    }

    public void Add(string title, Control page)
    {
        var tab = new Label { Text = title, AutoSize = true, Padding = new Padding(10, 6, 10, 8), Cursor = Cursors.Hand, Font = TabPoints > 10 ? Ui.Bold(TabPoints) : Ui.Regular(TabPoints), ForeColor = Ui.Subtle, BackColor = Color.Transparent };
        int index = tabs.Count;
        tab.Click += (_, _) => Select(index);
        tab.Paint += (_, e) =>
        {
            if (index != SelectedIndex) return;
            using var b = new SolidBrush(Ui.Accent);
            float s = DeviceDpi / 96f;
            e.Graphics.FillRectangle(b, 6 * s, tab.Height - 3 * s, tab.Width - 12 * s, 2.5f * s);
        };
        page.Dock = DockStyle.Fill; page.Visible = false;
        strip.Controls.Add(tab); body.Controls.Add(page);
        tabs.Add((tab, page));
        if (SelectedIndex < 0) Select(0);
    }

    public void Select(int i)
    {
        if (i < 0 || i >= tabs.Count) return;
        SelectedIndex = i;
        for (int k = 0; k < tabs.Count; k++)
        {
            tabs[k].Page.Visible = k == i;
            tabs[k].Tab.ForeColor = k == i ? Ui.Text : Ui.Subtle;
            tabs[k].Tab.Invalidate();
        }
        SelectedChanged?.Invoke(i);
    }
}

/// <summary>The selected mod's header: name and version, "by author · N modification(s)", badges and the Enabled pill.</summary>
sealed class DetailsHeader : Control
{
    public Mod? Mod { get; set; }
    public bool Conflicted { get; set; }
    public event Action? PillClicked;
    /// <summary>The "Conflict" badge clicked (opens the Conflicts tab).</summary>
    public event Action? ConflictClicked;
    Rectangle conflictRect;
    /// <summary>A tag chip or "+ Tag" clicked, at a screen point (opens the tags menu).</summary>
    public event Action<Point>? TagsClicked;
    Rectangle pill, tagArea;
    readonly Font chipFont = Ui.Heavy(8f);
    readonly Font title = Ui.Bold(14f), version = Ui.Regular(9.5f), by = Ui.Regular(9.5f), badge = Ui.Heavy(8.25f), pillFont = Ui.Heavy(8.5f);

    public DetailsHeader()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Default;
    }

    float S => DeviceDpi / 96f;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = (int)(64 * S); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.PaintGradient(g, this, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        if (Mod is not Mod m) { TextRenderer.DrawText(g, "Select a Mod", title, new Point((int)(12 * S), (int)(14 * S)), Ui.Subtle); return; }
        int x = (int)(12 * S), right = Width - (int)(12 * S);

        // Right side: the Enabled / Disabled pill, then the badges.
        string state = m.Enabled ? "Enabled" : "Disabled";
        var ps = TextRenderer.MeasureText(g, state, pillFont);
        pill = new Rectangle(right - ps.Width - (int)(8 * S), (int)(14 * S), ps.Width + (int)(8 * S), (int)(22 * S));
        var pc = m.Enabled ? Ui.Enabled : Ui.Subtle;
        using (var path = Ui.Round(pill, 4 * S))
        {
            using var f = new SolidBrush(pc); g.FillPath(f, path);   // solid pill, dark text
        }
        TextRenderer.DrawText(g, state, pillFont, pill, Ui.OnColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var badges = Ui.Badges(m);
        int left = Ui.DrawBadges(g, badges, pill.Left - (int)(8 * S), pill.Top + pill.Height / 2, badge, S * 1.15f);
        conflictRect = Rectangle.Empty;
        if (Conflicted)
        {
            int r0 = left - (int)(6 * S);
            int l0 = Ui.DrawBadges(g, [("Conflict", Ui.Warn)], r0, pill.Top + pill.Height / 2, badge, S * 1.15f);
            conflictRect = new Rectangle(l0, pill.Top, r0 - l0, pill.Height);
            left = l0;
        }

        // Left: name, version, by-line.
        var ts = TextRenderer.MeasureText(g, m.Name, title, Size.Empty, TextFormatFlags.NoPrefix);
        int maxName = left - x - (int)(80 * S);
        TextRenderer.DrawText(g, m.Name, title, new Rectangle(x, (int)(8 * S), Math.Min(ts.Width, maxName), ts.Height), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(m.Manifest.Version))
            TextRenderer.DrawText(g, m.Manifest.Version, version, new Point(x + Math.Min(ts.Width, maxName) + (int)(4 * S), (int)(8 * S) + ts.Height - TextRenderer.MeasureText(m.Manifest.Version, version).Height - (int)(2 * S)), Ui.Subtle, TextFormatFlags.NoPrefix);
        string line = (string.IsNullOrEmpty(m.Manifest.Author) ? "" : $"by {m.Manifest.Author}   ") + Ui.CountText(m) + (m.Enabled ? "" : "   ·   Turned Off");
        var byPos = new Point(x, (int)(8 * S) + ts.Height + (int)(2 * S));
        TextRenderer.DrawText(g, line, by, byPos, Ui.Subtle, TextFormatFlags.NoPrefix);

        // The mod's tags, then "+ Tag" (both open the tags menu).
        var ls = TextRenderer.MeasureText(g, line, by, Size.Empty, TextFormatFlags.NoPrefix);
        int cx = x + ls.Width + (int)(12 * S), mid = byPos.Y + ls.Height / 2;
        tagArea = Rectangle.Empty;
        for (int i = 0; i <= m.Tags.Count; i++)
        {
            bool add = i == m.Tags.Count;
            string tag = add ? "+ Tag" : m.Tags[i];
            if (cx + Ui.ChipWidth(g, tag, chipFont, S) > right) break;
            var r = Ui.DrawChip(g, tag, chipFont, cx, mid, S, outline: add, soft: !add && m.KindOf(tag) == Mod.TagKind.Auto, mod: m);
            tagArea = tagArea.IsEmpty ? r : Rectangle.Union(tagArea, r);
            cx = r.Right + (int)(4 * S);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = (pill.Contains(e.Location) || tagArea.Contains(e.Location) || conflictRect.Contains(e.Location)) && Mod != null ? Cursors.Hand : Cursors.Default; }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Mod == null) return;
        if (pill.Contains(e.Location)) PillClicked?.Invoke();
        else if (conflictRect.Contains(e.Location)) ConflictClicked?.Invoke();
        else if (tagArea.Contains(e.Location)) TagsClicked?.Invoke(PointToScreen(new Point(e.X, tagArea.Bottom)));
    }
}
