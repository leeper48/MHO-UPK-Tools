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
        b.Paint += (_, e) =>
        {
            if (b.Enabled) return;
            var r = b.ClientRectangle;
            using (var bg = new SolidBrush(b.BackColor)) e.Graphics.FillRectangle(bg, Rectangle.Inflate(r, -1, -1));
            TextRenderer.DrawText(e.Graphics, b.Text, b.Font, r, DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        };
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

    /// <summary>Re-applies accent / flat colours after MPM's theme (which colours every button the same).</summary>
    public static void RestyleButtons(Control root)
    {
        foreach (Control c in root.Controls)
        {
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
    static readonly object stockLock = new();
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
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        tips = Ui.NewTips(() => tipText);
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

    protected override void OnMeasureItem(MeasureItemEventArgs e)
    {
        e.ItemHeight = Math.Min(255, (int)((e.Index >= 0 && e.Index < Items.Count && Items[e.Index] is ModGroup ? 30 : 50) * S));
    }

    // Cards are laid out from the right edge (badges, checkbox): a width change must repaint every card, not just the
    // newly exposed strip, or the old badges stay behind (Kurt, 2026-09-27: "it repeats the status icons").
    protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }

    // The area below the last card: the window gradient instead of a flat colour.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014)   // WM_ERASEBKGND
        {
            using var g = Graphics.FromHdc(m.WParam);
            Ui.PaintGradient(g, this, ClientRectangle);
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
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
                lock (stockLock)
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
                return "Changes: " + m.Summary() + (Conflicted.Contains(m) ? ".\n! Some of them are also changed by another enabled mod: the one higher in the list wins (see Conflicts)." : ".");
        }
        return $"{m.Name}\n{m.Summary()}\nDouble-click to edit. Right-click for tags and more.";
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
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
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

    protected override void Dispose(bool disposing) { if (disposing) { tipTimer.Dispose(); tips.Dispose(); } base.Dispose(disposing); }
}

/// <summary>The middle column of the Mods page: the selected mod's store image(s) (StoreReplacements, 300×420 in the
/// mods), fitted into a card. Click to step through a mod with several. A mod without one shows the stock store image of
/// what it changes (StockCatalog.DefaultStoreFor), captioned as the game's. Decoded in the background.</summary>
sealed class StorePreview : Control
{
    Mod? mod;
    List<(string Texture, string Path)> items = [];
    string? stock;   // the stock store image shown for a mod without one of its own
    /// <summary>Stock store images for mods without one; null: none.</summary>
    public StockCatalog? Catalog { get; set; }
    int index;
    Image? image;
    int request;
    readonly Font titleFont = Ui.Bold(8.5f), smallFont = Ui.Regular(8.25f);

    public StorePreview()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Cursor = Cursors.Default;
    }

    float S => DeviceDpi / 96f;

    public Mod? Mod
    {
        get => mod;
        set
        {
            if (value == mod && value != null) return;
            mod = value;
            items = value == null ? [] : value.Manifest.StoreReplacements
                .Where(r => r.DdsFileName != null).Select(r => (r.TextureName ?? "", Path.Combine(value.Folder, r.DdsFileName!)))
                .Where(x => File.Exists(x.Item2)).ToList();
            index = 0;
            Load();
        }
    }

    void Load()
    {
        var old = image; image = null; old?.Dispose();
        stock = null;
        Cursor = items.Count > 1 ? Cursors.Hand : Cursors.Default;
        Invalidate();
        if (items.Count == 0) { LoadStock(); return; }
        int req = ++request;
        string path = items[index].Path;
        Task.Run(() => { try { return Ui.DdsThumb(path, 1024); } catch { return null; } }).ContinueWith(t =>
        {
            if (IsDisposed || req != request) { t.Result?.Dispose(); return; }
            image = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>No store image in the mod: the game's own one for what it changes.</summary>
    void LoadStock()
    {
        if (Catalog is not { } cat || mod is not { } m) return;
        int req = ++request;
        string storePkg = Applier.IconPackages[2].File;
        Task.Run(() =>
        {
            try
            {
                if (cat.DefaultStoreFor(m) is not string tex || cat.Preview(storePkg, tex) is not { } p) return (null, null);
                return ((string?)tex, (Image?)Ui.Thumb(p.Bgra, p.W, p.H, 1024));
            }
            catch { return (null, null); }
        }).ContinueWith(t =>
        {
            if (IsDisposed || req != request) { t.Result.Item2?.Dispose(); return; }
            (stock, image) = t.Result;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (items.Count > 1) { index = (index + 1) % items.Count; Load(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.PaintGradient(g, this, ClientRectangle);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = (int)(6 * S);
        var title = new Rectangle(pad, (int)(4 * S), Width - 2 * pad, (int)(20 * S));
        TextRenderer.DrawText(g, "STORE IMAGE", titleFont, title, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // Card at the store images' 300:420 aspect, as wide as the column allows.
        int w = Width - 2 * pad, h = (int)(w * 420f / 300f);
        int captionH = (int)(40 * S);
        int maxH = Height - title.Bottom - (int)(8 * S) - captionH;
        if (h > maxH && maxH > 0) { h = maxH; w = (int)(h * 300f / 420f); }
        if (w <= 0 || h <= 0) return;
        var card = new Rectangle((Width - w) / 2, title.Bottom + (int)(4 * S), w, h);
        using (var path = Ui.Round(card, 5 * S))
        {
            using (var fill = new SolidBrush(Ui.Card)) g.FillPath(fill, path);
            if (image != null)
            {
                var clip = g.Clip; g.SetClip(path, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Min((float)card.Width / image.Width, (float)card.Height / image.Height);
                float iw = image.Width * k, ih = image.Height * k;
                g.DrawImage(image, card.X + (card.Width - iw) / 2, card.Y + (card.Height - ih) / 2, iw, ih);
                g.Clip = clip;
            }
            else
            {
                string text = mod == null ? "" : items.Count == 0 ? "No Store Image\nin This Mod" : "Loading…";
                TextRenderer.DrawText(g, text, smallFont, card, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            }
        }
        if (items.Count == 0)
        {
            if (stock == null) return;
            var sc = new Rectangle(card.X, card.Bottom + (int)(4 * S), card.Width, (int)(18 * S));
            TextRenderer.DrawText(g, stock, smallFont, sc, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            sc.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, "The Game's Image (Not in This Mod)", smallFont, sc, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        var cap = new Rectangle(card.X, card.Bottom + (int)(4 * S), card.Width, (int)(18 * S));
        TextRenderer.DrawText(g, items[index].Texture, smallFont, cap, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (items.Count > 1)
        {
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"{index + 1} of {items.Count}  ·  click for next", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }
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
        if (Conflicted) badges.Insert(0, ("Conflict", Ui.Warn));
        int left = Ui.DrawBadges(g, badges, pill.Left - (int)(8 * S), pill.Top + pill.Height / 2, badge, S * 1.15f);

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

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = (pill.Contains(e.Location) || tagArea.Contains(e.Location)) && Mod != null ? Cursors.Hand : Cursors.Default; }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Mod == null) return;
        if (pill.Contains(e.Location)) PillClicked?.Invoke();
        else if (tagArea.Contains(e.Location)) TagsClicked?.Invoke(PointToScreen(new Point(e.X, tagArea.Bottom)));
    }
}
