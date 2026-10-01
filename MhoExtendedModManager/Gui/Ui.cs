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

    /// <summary>"team, automatic", "character, user", "from the mod" … for tooltips.</summary>
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

    /// <summary>"automatic", "from the mod" or "user", for tooltips.</summary>
    public static string TagSource(Mod m, string tag) => m.KindOf(tag) switch { Mod.TagKind.User => "user", Mod.TagKind.Mod => "from the mod", _ => "automatic" };

    public static int ChipWidth(Graphics g, string text, Font font, float s) =>
        TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + (int)(12 * s);

    /// <summary>
    /// Opens a menu under a button, kept on the button's own monitor (a user with two monitors: Settings, at the window's
    /// right edge, opened its menu on the other screen; Windows only keeps menus inside the whole desktop). Aligned to the
    /// button's right edge when it doesn't fit, above the button when there's no room below; submenus open to the left there.
    /// </summary>
    public static void ShowUnder(ContextMenuStrip menu, Control button)
    {
        var area = Screen.FromControl(button).WorkingArea;
        ShowOnScreen(menu, PlaceUnder(button.RectangleToScreen(button.ClientRectangle), menu.GetPreferredSize(Size.Empty), area), area);
    }

    /// <summary>Where a menu of <paramref name="menu"/> size goes under a button (screen rectangle) on a monitor's work area.</summary>
    internal static Point PlaceUnder(Rectangle button, Size menu, Rectangle area)
    {
        int x = button.Left, y = button.Bottom;
        if (x + menu.Width > area.Right) x = button.Right - menu.Width;      // right-aligned to the button
        if (y + menu.Height > area.Bottom) y = button.Top - menu.Height;     // above it
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    /// <summary>A menu at a screen point (right-click), kept on that point's monitor.</summary>
    public static void ShowAt(ContextMenuStrip menu, Point screenPoint) => ShowOnScreen(menu, screenPoint, Screen.FromPoint(screenPoint).WorkingArea, flip: true);

    static void ShowOnScreen(ContextMenuStrip menu, Point p, Rectangle area, bool flip = false)
    {
        var size = menu.GetPreferredSize(Size.Empty);
        int x = p.X, y = p.Y;
        if (x + size.Width > area.Right) x = flip ? p.X - size.Width : area.Right - size.Width;
        if (y + size.Height > area.Bottom) y = flip ? p.Y - size.Height : area.Bottom - size.Height;
        x = Math.Max(area.Left, x); y = Math.Max(area.Top, y);
        // Near the right edge, submenus (Nexus, Tags …) open to the left, so they stay on this monitor too.
        bool left = x + size.Width + 260 * menu.DeviceDpi / 96 > area.Right;
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>()) if (item.HasDropDownItems) item.DropDownDirection = left ? ToolStripDropDownDirection.Left : ToolStripDropDownDirection.Right;
        menu.Show(new Point(x, y));
    }

    /// <summary>
    /// A notification's heading as its only button (Kurt: no separate Close; the word itself, e.g. "Success", continues;
    /// Enter too): large, bold, in the tone's colour (green success, red error, else the accent).
    /// </summary>
    public static Button HeadingButton(string text, Color color, Action action, float points = 13f)
    {
        var b = AccentButton(text, action, "Continue (Enter).");
        b.Font = Bold(points);
        b.AutoSize = true; b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        b.Padding = new Padding(18, 6, 18, 6);
        b.BackColor = color; b.ForeColor = color == Accent ? Color.White : OnColor;
        b.FlatAppearance.BorderColor = color;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.25f);
        return b;
    }

    /// <summary>A one-line text prompt in the dark theme (with suggestions); null when cancelled or empty.</summary>
    public static string? Prompt(IWin32Window owner, string title, string label, string initial = "", IEnumerable<string>? suggestions = null, bool secret = false)
    {
        using var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                                 StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Regular(9.5f), Padding = new Padding(12) };
        DarkFrame(f);
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
        MhoPackageModifier.Gui.Theme.Apply(f, MhoPackageModifier.Gui.Palette.Dark); Modern.Modernize(f);
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

    /// <summary>The " (on Classic)" end of a moved costume mod's name (drawn in amber: our override, not part of the mod's
    /// name, Kurt), or null. Set by MainForm (it knows the costume each mod is for).</summary>
    public static Func<Mod, (string Suffix, bool OtherHero)?>? OverrideSuffix { get; set; }
    /// <summary>Kurt: amber for a costume moved to another hero, teal for one moved within its hero.</summary>
    public static readonly Color OverrideAmber = Color.FromArgb(242, 170, 60), OverrideTeal = TagContent;

    /// <summary>A mod's name in <paramref name="rect"/>: the name, then its override suffix in amber (the name is shortened first).</summary>
    public static void DrawModName(Graphics g, Mod m, Font font, Rectangle rect, Color color, TextFormatFlags flags)
    {
        var over = OverrideSuffix?.Invoke(m);
        string? suffix = over?.Suffix;
        if (suffix == null || !m.Name.EndsWith(suffix, StringComparison.Ordinal)) { TextRenderer.DrawText(g, m.Name, font, rect, color, flags); return; }
        string head = m.Name[..^suffix.Length];
        var nf = flags | TextFormatFlags.NoPadding;
        const TextFormatFlags measure = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;   // no ellipsis: with Size.Empty it shortens to nothing
        int sw = TextRenderer.MeasureText(g, suffix, font, Size.Empty, measure).Width;
        int hw = Math.Min(TextRenderer.MeasureText(g, head, font, Size.Empty, measure).Width, Math.Max(0, rect.Width - sw));
        TextRenderer.DrawText(g, head, font, new Rectangle(rect.X, rect.Y, hw, rect.Height), color, nf | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, suffix, font, new Rectangle(rect.X + hw, rect.Y, Math.Max(0, rect.Width - hw), rect.Height), over!.Value.OtherHero ? OverrideAmber : OverrideTeal, nf);
    }

    /// <summary>A small preview of a .dds (null if it can't be read).</summary>
    /// <summary>
    /// A file's identity for picture caches: size, last write and creation time. A saved or updated mod copies its files
    /// (a copy keeps the source's write time), so the write time alone missed a replaced icon (Kurt: the preview's
    /// thumbnails kept the old images); the new copy's creation time changes. "" when the file can't be read.
    /// </summary>
    public static string FileStamp(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? $"{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{fi.CreationTimeUtc.Ticks}" : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    public static Image? DdsThumb(string path, int size)
    {
        if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            // A custom picture (.png / .jpg / .bmp: ModPictures), read without locking the file.
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var src = Image.FromStream(ms);
            float k = Math.Min(1f, Math.Min((float)size / src.Width, (float)size / src.Height));
            var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
            return bmp;
        }
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

    // ---- the dark theme for everything Windows draws itself (Kurt: every popup dark with light text)

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> darkFrames = new();

    /// <summary>
    /// A window's title bar and frame in the app's colours: Windows' dark mode (Windows 10 1809+), and on Windows 11 the
    /// caption colour itself (<paramref name="caption"/>, default the popups' background) with light text, since "show
    /// accent colour on title bars" otherwise paints them in the system accent (seen teal on Kurt's PC).
    /// </summary>
    public static void DarkFrame(Form f, Color? caption = null)
    {
        if (darkFrames.TryGetValue(f, out _)) return;
        darkFrames.Add(f, new object());
        void Apply()
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE (19 before 20H1)
            int border = ColorTranslator.ToWin32(Line), cap = ColorTranslator.ToWin32(caption ?? Back), text = ColorTranslator.ToWin32(Text);
            DwmSetWindowAttribute(f.Handle, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR (Windows 11)
            DwmSetWindowAttribute(f.Handle, 35, ref cap, sizeof(int));      // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(f.Handle, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
        }
        if (f.IsHandleCreated) Apply();
        f.HandleCreated += (_, _) => Apply();
    }

    /// <summary>
    /// At start: every menu (right-click, the ▾ buttons, submenus) drawn dark, and every window's title bar dark, also
    /// windows made elsewhere (MHO Package Modifier's): a new window gets it as soon as the app is idle.
    /// </summary>
    public static void UseDarkTheme()
    {
        // Filter / search boxes' clear button (×) uses the app's own dark tooltips.
        MhoPackageModifier.Gui.SearchBox.Tip = (c, t) => Tip(c, t);
        ToolStripManager.Renderer = new DarkMenuRenderer();
        Application.Idle += (_, _) => { foreach (Form f in Application.OpenForms) DarkFrame(f); };
    }

    /// <summary>Menus in the app's colours: dark background, light text, the hover row highlighted, accent check marks.</summary>
    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Menu = Color.FromArgb(38, 38, 44), Hover = Color.FromArgb(58, 58, 80), Border = Color.FromArgb(70, 70, 80);
            public override Color ToolStripDropDownBackground => Menu;
            public override Color ImageMarginGradientBegin => Menu;
            public override Color ImageMarginGradientMiddle => Menu;
            public override Color ImageMarginGradientEnd => Menu;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientMiddle => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color MenuStripGradientBegin => Menu;
            public override Color MenuStripGradientEnd => Menu;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Menu;
            public override Color CheckBackground => Menu;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
            public override Color ButtonSelectedBorder => Hover;
            public override Color ToolStripBorder => Border;
            public override Color ToolStripGradientBegin => Menu;
            public override Color ToolStripGradientMiddle => Menu;
            public override Color ToolStripGradientEnd => Menu;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // An item can carry its own colour in Tag (Move to Another Costume: the hero's default costume in green).
            e.TextColor = e.Item.Tag is Color own ? (e.Item.Enabled ? own : Color.FromArgb(150, own)) : e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? DisabledText : Subtle;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float s = Math.Max(1.6f, r.Height / 9f);
            using var pen = new Pen(Accent, s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, [new PointF(r.X + r.Width * 0.18f, r.Y + r.Height * 0.52f), new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.76f), new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.26f)]);
        }
    }

    /// <summary>A toggle button's state: lit (accent) when on, flat when off (Loop, Spec / Reflect / Glow).</summary>
    public static void Lit(Button b, bool on)
    {
        b.Tag = on ? "accent" : "flat";
        b.BackColor = on ? Accent : Bar; b.ForeColor = on ? Color.White : Text;
        b.FlatAppearance.BorderColor = on ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = on ? AccentHover : CardHover;
        b.Invalidate();
    }

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
        if (styledGrids.TryGetValue(g, out _)) return;
        styledGrids.Add(g, new object());
        // Cell tooltips in the app's dark style (Kurt: the table's own were the white system box): a cell's own tip text, or
        // its whole text when the cell cuts it off. The table's built-in tooltip is off.
        g.ShowCellToolTips = false;
        string? cellTip = null;
        var cellTips = NewTips(() => cellTip);
        var cellTimer = new System.Windows.Forms.Timer { Interval = 450 };
        cellTimer.Tick += (_, _) =>
        {
            cellTimer.Stop();
            if (cellTip == null || g.IsDisposed || !g.IsHandleCreated) return;
            var p = g.PointToClient(Cursor.Position);
            float s = g.DeviceDpi / 96f;
            cellTips.Show(cellTip, g, p.X + (int)(14 * s), p.Y + (int)(20 * s), 20000);
        };
        g.CellMouseEnter += (_, e) =>
        {
            cellTimer.Stop(); cellTips.Hide(g); cellTip = null;
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= g.Rows.Count) return;
            var cell = g.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string? t = !string.IsNullOrEmpty(cell.ToolTipText) ? cell.ToolTipText : null;
            if (t == null && cell.FormattedValue is string v && v.Length > 0)
            {
                var font = cell.InheritedStyle.Font ?? g.Font;
                var pad = cell.InheritedStyle.Padding;
                int room = g.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false).Width - pad.Horizontal - 4;
                if (TextRenderer.MeasureText(v, font).Width > room) t = v;   // cut off: show it whole
            }
            cellTip = t;
            if (t != null) cellTimer.Start();
        };
        g.CellMouseLeave += (_, _) => { cellTimer.Stop(); cellTip = null; cellTips.Hide(g); };
        g.Disposed += (_, _) => { cellTimer.Dispose(); cellTips.Dispose(); };
        // Check boxes in cells drawn like the app's own (Modern.Toggle), not the system's.
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || g.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn || e.Graphics == null) return;
            e.PaintBackground(e.CellBounds, true);
            float s = g.DeviceDpi / 96f, box = 14 * s;
            var r = new RectangleF(e.CellBounds.X + (e.CellBounds.Width - box) / 2f, e.CellBounds.Y + (e.CellBounds.Height - box) / 2f, box, box);
            bool on = e.Value is true || e.Value is CheckState.Checked;
            bool ro = g.ReadOnly || g.Columns[e.ColumnIndex].ReadOnly || g.Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly;
            var gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Modern.Round(r, 3 * s))
            {
                using (var f = new SolidBrush(on ? (ro ? Color.FromArgb(90, Accent) : Accent) : Modern.FieldBack)) gr.FillPath(f, path);
                using var pen = new Pen(on ? Accent : ro ? Modern.FieldBorder : Color.FromArgb(110, 110, 124), 1.2f * s); gr.DrawPath(pen, path);
            }
            if (on)
            {
                using var pen = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                gr.DrawLines(pen, [new PointF(r.X + box * 0.22f, r.Y + box * 0.52f), new PointF(r.X + box * 0.43f, r.Y + box * 0.73f), new PointF(r.X + box * 0.8f, r.Y + box * 0.3f)]);
            }
            gr.SmoothingMode = SmoothingMode.None;
            e.Handled = true;
        };
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, object> styledGrids = [];

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
        Modern.Modernize(root);
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
    /// <summary>Buttons drawn with an icon instead of their text (the painter gets the button's area and text colour).</summary>
    public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, Action<Graphics, Rectangle, Color>> IconPainters = new();

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
            if (r.Width < 4 || r.Height < 4) return;   // nothing to draw (a button laid out at zero size crashed AddArc)
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
            if (IconPainters.TryGetValue(b, out var icon)) { icon(g, r, b.Enabled ? b.ForeColor : DisabledText); return; }
            g.SmoothingMode = SmoothingMode.None;
            TextRenderer.DrawText(g, b.Text, b.Font, r, b.Enabled ? b.ForeColor : DisabledText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
    }

    static GraphicsPath RoundF(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        if (d <= 0) { p.AddRectangle(r); return p; }
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
            if (c is Button d && d.Tag is "flat" && d.Text.TrimEnd().EndsWith('▾'))
            {
                // A button that opens a menu looks like the app's drop-downs (the header's Costume ▾): accent-tinted pill.
                static Color Mix(int a) => Color.FromArgb((Accent.R * a + Bar.R * (255 - a)) / 255, (Accent.G * a + Bar.G * (255 - a)) / 255, (Accent.B * a + Bar.B * (255 - a)) / 255);
                d.UseVisualStyleBackColor = false;
                d.BackColor = Mix(40); d.ForeColor = Text;
                d.FlatAppearance.BorderColor = Accent;
                d.FlatAppearance.MouseOverBackColor = Mix(62);
                d.FlatAppearance.MouseDownBackColor = Mix(80);
            }
            else if (c is Button b && b.Tag is "accent" or "flat")
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
    // Multi-select (a user, 2026-09-30: Shift to turn several on / off, drag them as a group, move them with the priority
    // buttons). The ListBox's own SelectedIndex stays the one card whose details show; the marked cards are kept by folder
    // name, so they survive the reload after a change (turn them on, then off again).
    readonly HashSet<string> marked = new(StringComparer.OrdinalIgnoreCase);
    int anchor = -1;
    bool clearOnUp;
    /// <summary>A checkbox of a marked card (or Space) with several marked: (the marked mods in list order, the new state).</summary>
    public event Action<List<Mod>, bool>? CheckManyClicked;
    /// <summary>Several marked cards dropped next to another: (the marked mods in list order, target, below the target).</summary>
    public event Action<List<Mod>, Mod, bool>? DroppedMany;
    /// <summary>The marking changed (how many are marked).</summary>
    public event Action<int>? MarksChanged;
    /// <summary>The marked mods shown in the list, in list order (empty unless more than one is marked).</summary>
    public List<Mod> MarkedMods => marked.Count > 1 ? Items.OfType<Mod>().Where(m => marked.Contains(m.FolderName)).ToList() : [];
    bool IsMarked(Mod m) => marked.Contains(m.FolderName);
    /// <summary>Back to a single selection (the card whose details show).</summary>
    public void ClearMarks()
    {
        int before = marked.Count;
        marked.Clear();
        if (SelectedItem is Mod m) marked.Add(m.FolderName);
        if (before > 1) { Invalidate(); MarksChanged?.Invoke(marked.Count); }
    }
    void MarkRange(int from, int to)
    {
        marked.Clear();
        for (int k = Math.Min(from, to); k <= Math.Max(from, to); k++)
            if (k >= 0 && k < Items.Count && Items[k] is Mod mk) marked.Add(mk.FolderName);
        Invalidate();
        MarksChanged?.Invoke(marked.Count);
    }
    /// <summary>Padlock clicked (a locked mod, or one that can be locked where it is).</summary>
    public event Action<Mod>? LockClicked;
    public event Action<ModGroup>? GroupClicked;
    public event Action<string>? TagClicked;
    /// <summary>Right-click on a card (already selected), at a screen point.</summary>
    public event Action<Mod, Point>? MenuRequested;
    /// <summary>The newer Nexus version for a mod (null: none), and a click on its ↑ badge.</summary>
    public Func<Mod, string?>? UpdateFor { get; set; }
    /// <summary>The single costume a mod is for ("Age of Ultron Movie"), shown on the card instead of the count; null = the count.</summary>
    public Func<Mod, string?>? CostumeLabel { get; set; }
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
            var state = SelectedIndex == i || marked.Count > 1 && Items[i] is Mod mk && IsMarked(mk) ? DrawItemState.Selected : DrawItemState.None;
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
        Ui.DrawModName(g, m, nameFont, nameRect, m.Enabled ? Ui.Text : Color.FromArgb(200, 200, 205), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // Second row: author and tag chips on the left, state and count on the right.
        var second = new Rectangle(textLeft, card.Y + (int)(25 * S), card.Right - pad - textLeft, (int)(16 * S));
        string state = broken ? "Missing Files" : m.Enabled ? "Enabled" : "Disabled";
        string count = "  ·  " + (CostumeLabel?.Invoke(m) ?? Ui.CountText(m));
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
        key = file + "|" + Ui.FileStamp(file);
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
        if (CheckRect(b).Contains(e.Location))
        {
            if (marked.Count > 1 && IsMarked(m)) { CheckManyClicked?.Invoke(MarkedMods, !m.Enabled); return; }   // all marked follow this one
            SelectedIndex = i; anchor = i; ClearMarks(); CheckClicked?.Invoke(m); return;
        }
        if (LockRect(b).Contains(e.Location) && LockOffer(m) != ModLock.None) { SelectedIndex = i; LockClicked?.Invoke(m); return; }
        if (nexusParts.TryGetValue(i, out var np) && (np.Pill.Contains(e.Location) || np.Mark.Contains(e.Location)) && UpdateFor?.Invoke(m) != null)
        { SelectedIndex = i; UpdateClicked?.Invoke(m); return; }
        if (regions.TryGetValue(i, out var r))
        {
            foreach (var (rect, tag) in r.Chips)
                if (rect.Contains(e.Location)) { TagClicked?.Invoke(tag); return; }

        }
        // Shift: mark the range from the last plain click; Ctrl: add / remove this card. The clicked card's details show.
        var keys = ModifierKeys;
        if ((keys & Keys.Shift) != 0 && anchor >= 0 && anchor < Items.Count)
        {
            SelectedIndex = i; MarkRange(anchor, i); Focus(); return;
        }
        if ((keys & Keys.Control) != 0)
        {
            if (marked.Count == 0 && SelectedItem is Mod cur) marked.Add(cur.FolderName);
            if (!marked.Remove(m.FolderName)) marked.Add(m.FolderName);
            SelectedIndex = i; anchor = i; Invalidate(); MarksChanged?.Invoke(marked.Count); Focus(); return;
        }
        bool group = marked.Count > 1 && IsMarked(m);
        clearOnUp = group;                       // a plain click inside the marking: kept in case this becomes a group drag
        if (!group) { int was = marked.Count; marked.Clear(); marked.Add(m.FolderName); if (was > 1) { Invalidate(); MarksChanged?.Invoke(1); } }
        anchor = i;
        // A press on the card itself can become a drag (priority view, unlocked mods; a marked group when none is locked).
        dragMod = CanReorder?.Invoke() == true && m.Lock == ModLock.None && (!group || MarkedMods.All(x => x.Lock == ModLock.None)) ? m : null;
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
        if (!dragging)
        {
            dragMod = null;
            if (clearOnUp) { clearOnUp = false; ClearMarks(); }   // a plain click on a marked card: just that one
            return;
        }
        clearOnUp = false;
        var (m, i, below) = (dragMod, dropItem, dropBelow);
        var group = MarkedMods;
        EndDrag();
        if (m == null || i < 0 || i >= Items.Count || Items[i] is not Mod target) return;
        if (group.Count > 1 && group.Contains(m)) { if (!group.Contains(target)) DroppedMany?.Invoke(group, target, below); }
        else if (target != m) Dropped?.Invoke(m, target, below);
    }

    void EndDrag()
    {
        dragging = false; dragMod = null; dropItem = -1; scrollTimer.Stop();
        Capture = false; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && dragging) { EndDrag(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && marked.Count > 1) { ClearMarks(); e.Handled = true; return; }
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
        if (e.KeyCode == Keys.Space && SelectedItem is Mod m)
        {
            if (marked.Count > 1 && IsMarked(m)) CheckManyClicked?.Invoke(MarkedMods, !m.Enabled); else CheckClicked?.Invoke(m);
            e.Handled = true; return;
        }
        // Shift+Up / Down extends the marking from the anchor; plain arrows go back to one card. (Ctrl+arrows are the
        // window's priority keys, which move the whole marking.)
        if (e.KeyCode is Keys.Up or Keys.Down && !e.Control)
        {
            int step = e.KeyCode == Keys.Up ? -1 : 1, to = SelectedIndex + step;
            while (to >= 0 && to < Items.Count && Items[to] is not Mod) to += step;
            if (to >= 0 && to < Items.Count)
            {
                if (e.Shift) { if (anchor < 0) anchor = SelectedIndex; SelectedIndex = to; MarkRange(anchor, to); }
                else { SelectedIndex = to; anchor = to; ClearMarks(); }
            }
            e.Handled = true; return;
        }
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
    // The 3D view (Kurt): the mod's skeletal meshes (ModMeshes), shown in ModelView where the picture goes.
    List<MeshRef> meshes = [];
    bool show3D;
    int meshIndex;
    ModelView? viewer;
    readonly Dictionary<string, ModMeshes.Loaded?> meshCache = [];
    Rectangle meshPrev, meshNext;
    // Animation (Kurt: pick one for the 3D view): the mesh's animations, the one playing, and the drop-down.
    List<AnimRef> anims = [];
    string? wantedAnim;                           // from the pick's "@animation" part
    double? restoreTime;                          // the saved frame (seconds) to show when the animation being restored has loaded
    string? shownMesh;                            // the mesh the 3D view holds (kept while a picture shows, so going back keeps pose and camera)
    AnimExportCli.Animation.BoneAnimation? playing;
    MeshAnimator? animator;
    float playFrames, playSeconds;
    readonly System.Diagnostics.Stopwatch playClock = new();
    readonly System.Windows.Forms.Timer playTimer = new() { Interval = 33 };
    DropDown? animBox;
    bool fillingAnims;
    // Play / pause (an animation loads paused on its first frame), loop (remembered), reset view (the default camera).
    Button? playBtn, loopBtn, restBtn;
    Button? specBtn, reflBtn, glowBtn;   // shading toggles (Kurt): specular, reflections, glow
    Button? propsBtn;                    // props (Kurt, 2026-09-30: weapons in the preview too)
    // Power effects (Kurt, 2026-09-30; ported from the MHO Hero Creator): the effects of the power an animation belongs to,
    // played with it. The game data is read once (in the background); each power's effects when its animation is picked.
    Button? powersBtn;
    Fx.PowerEffects.Player? fxPlayer;
    string fxNote = "";                  // "Shockwave · 5 Effects" in the 3D caption
    Dictionary<string, (string Bone, System.Numerics.Matrix4x4 Local)> fxSockets = new();
    double fxTime;                       // where the effects are (seconds into the animation)
    int fxRequest;
    static Task<Fx.GameData?>? fxDb;
    readonly PropRig rig = new();
    ModMeshes.Loaded? shownLoaded;       // the character as loaded (without props)
    LightSlider? lightSlider, lensSlider, frameSlider;
    bool settingFrame;   // the frame slider follows playback without scrubbing
    // Full screen (Kurt, 2026-09-30): the whole preview moves into a borderless window covering the app's monitor, the 3D
    // view filling it with the controls in a column on the right; Esc, F11 or the button bring it back.
    Button? fullBtn;
    // Compact 3D controls (Kurt, 2026-09-30: room for more): playback in a bar over the view's bottom and framing in its top
    // right corner, both shown while the mouse is over the view (always in full screen); the look toggles and the Light /
    // Lens sliders in a Look ▾ menu; under the view only the caption and Look ▾ · Reset View · ⛶.
    Panel? playBar;
    // Power buttons (Kurt, 2026-09-30: like the MHO Hero Creator's 3D View): the hero's powers as icons in the strip under
    // the preview while the 3D view shows (the Power Icons button switches back to the pictures); a click filters the
    // animations to that power's and plays its first, with its effects; a second click shows them all again.
    List<Fx.PowerList.Power> heroPowers = [];
    List<AnimRef> allAnims = [];
    bool autoPlay, powersLoaded;
    string? powerFilter;
    readonly List<(Rectangle Rect, int Index)> powerRects = [];
    Rectangle powerLeft, powerRight;
    int powerScroll, hoverPower = -1, heroPowersRequest;
    Button? lookBtn;
    ContextMenuStrip? lookMenu;
    readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 150 };
    bool overView;
    DateTime overUntil;
    Rectangle viewRect;
    Button? frameFullBtn, frameHeadBtn, frameBustBtn;   // framings (Kurt: as in Create from 3D)
    Form? fullForm;
    Control? homeParent;
    int homeIndex;
    /// <summary>The preview is in its full-screen window.</summary>
    public bool IsFull => fullForm != null;
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
    /// <summary>A pick made by the window for this preview (a custom picture), handled like a click's.</summary>
    public void RaisePicked(Mod m, string? key) => Picked?.Invoke(m, key);
    /// <summary>Right-click → Custom Image (a user's request): the window asks for a file and picks it.</summary>
    public event Action<Mod>? CustomRequested;

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
            string stamp = value == null ? "" : FilesStamp(value);
            if (value != null && mod != null && value.FolderName == mod.FolderName && value.FilesMade == mod.FilesMade && stamp == modStamp && (items.Count > 0 || meshes.Count > 0))
            {
                // The same mod after a reload (a pick, a tag, undo …): keep the pictures and the 3D view (Kurt: its pose,
                // zoom and animation were lost when a picture was picked and then 3D again); only show the new choice.
                bool again = value.LocalPreview != mod.LocalPreview || value.Manifest.PreviewImage != mod.Manifest.PreviewImage;
                mod = value;
                if (again) Resolve();
                return;
            }
            SaveAnim();   // leaving this mod: its animation and frame are kept for next time
            mod = value; modStamp = stamp;
            items = []; meshes = []; index = -1; meshIndex = 0; scroll = 0; show3D = false; shownMesh = null;
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

    string modStamp = "";

    /// <summary>The mod's files as they are (names, sizes, dates; not the manifest): an edit or update changes it even when
    /// the copied files keep their old write times, so the preview reloads its pictures and meshes.</summary>
    static string FilesStamp(Mod m)
    {
        try
        {
            return string.Join(";", Directory.EnumerateFiles(m.Folder, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => Path.GetFileName(f) + "|" + Ui.FileStamp(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>A strip thumbnail's cache key: the mod, the picture, and its file as it is now (a replaced icon gets a new one).</summary>
    static string ThumbKey(Mod m, PreviewCandidate c) => m.FolderName + "|" + c.Key + (c.FromMod && c.File != null ? "|" + Ui.FileStamp(c.File) : "");

    void LoadThumbs()
    {
        if (mod is not { } m || items.Count < 2) return;
        var todo = items.Where(c => !thumbs.ContainsKey(ThumbKey(m, c))).ToList();
        foreach (var c in todo) thumbs[ThumbKey(m, c)] = null;
        int size = ThumbSize * 2;
        Task.Run(() => todo.Select(c => (Key: ThumbKey(m, c), Img: Decode(c, size))).ToList()).ContinueWith(t =>
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
        bool full = IsFull && show3D;
        // The pictures strip is always there; the power block (Powers toggle + two rows of power buttons) sits at the bottom
        // (Kurt, 2026-09-30), kept while the hero's powers load and dropped when the hero has none.
        bool powerBlock = show3D && HeroOfMesh() != null && (!powersLoaded || heroPowers.Count > 0);
        bool showStrip = Tiles > 1 && !full;
        int stripH = showStrip ? ThumbSize + (int)(12 * S) : 0;
        int pbh = animBox?.Height ?? (int)(26 * S), pgap = (int)(6 * S);
        int powersH = powerBlock && !full ? pbh + pgap + 2 * ThumbSize + pgap + (int)(4 * S) : 0;
        int captionH = (int)((show3D ? 80 : 40) * S);
        int panelW = (int)(320 * S);
        Rectangle card;
        if (full)
        {
            // Full screen: the 3D view fills everything left of a controls column.
            card = Rectangle.FromLTRB(pad, title.Bottom + (int)(4 * S), Width - panelW - 2 * pad, Height - pad);
            if (card.Width <= 0 || card.Height <= 0) return;
        }
        else
        {
            int w = Width - 2 * pad, h = (int)(w * 420f / 300f);
            int maxH = Height - title.Bottom - (int)(8 * S) - captionH - stripH - powersH;
            if (h > maxH && maxH > 0) { h = maxH; if (!show3D) w = (int)(h * 300f / 420f); }   // the 3D view uses the column's whole width; pictures keep their shape
            if (w <= 0 || h <= 0) return;
            card = new Rectangle((Width - w) / 2, title.Bottom + (int)(4 * S), w, h);
        }
        // Where the caption and the 3D controls go: under the card, or the column on the right when full screen.
        int ctlX = full ? card.Right + pad : card.X, ctlW = full ? panelW : card.Width;
        int fbs = (int)(33 * S);   // ⛶ and the three framing buttons: one square size (Kurt: ⛶ 50 % bigger)
        // The caption under the card leaves room on the right for the ⛶ button (Kurt: lower right, under the view).
        int capX = full ? ctlX : pad, capW = full ? panelW : Width - 2 * pad, capY = full ? card.Top : card.Bottom + (int)(4 * S);
        if (show3D && viewer != null)
        {
            // The 3D view fills the card; the caption steps through the meshes.
            var inner = Rectangle.Inflate(card, -(int)(2 * S), -(int)(2 * S));
            // The playback bar (frame slider, animation, ▶, Loop) is always shown, under the view inside the card (Kurt:
            // it kept disappearing as a hover overlay).
            if (animBox != null) inner.Height -= (int)(22 * S) + animBox.Height + 3 * (int)(4 * S);
            if (viewer.Bounds != inner) viewer.Bounds = inner;
            viewRect = inner;
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
                k = Math.Min(k, 3f * S);   // small images (40×40 costume icons) at most 3× their size (Kurt: filling the card was far too blocky)
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
            var cap = new Rectangle(capX, capY, capW, (int)(18 * S));
            if (meshes.Count > 1)
            {
                meshPrev = new Rectangle(cap.X, cap.Y, (int)(24 * S), cap.Height);
                meshNext = new Rectangle(cap.Right - (int)(24 * S), cap.Y, (int)(24 * S), cap.Height);
                TextRenderer.DrawText(g, "◀", smallFont, meshPrev, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "▶", smallFont, meshNext, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            var mid = Rectangle.FromLTRB(cap.X + (int)(26 * S), cap.Y, cap.Right - (int)(26 * S), cap.Bottom);
            TextRenderer.DrawText(g, meshes.Count > 1 ? $"{r.Name}  ({meshIndex + 1} of {meshes.Count})" : r.Name, smallFont, mid, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview != null && MeshPart(mod.LocalPreview) == r.Key ? "User Pick" : mod.Manifest.PreviewImage != null && MeshPart(mod.Manifest.PreviewImage) == r.Key ? "The Mod's Choice" : "3D View";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"3D  ·  {r.Package.Replace(".upk", "", StringComparison.OrdinalIgnoreCase)}  ·  {whose}{(fxNote.Length > 0 ? "  ·  " + fxNote : "")}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (animBox != null && playBtn != null && loopBtn != null && restBtn != null && playBar != null && lookBtn != null && fullBtn != null && frameSlider != null)
            {
                int bh = animBox.Height, gap = (int)(4 * S), wPlay = (int)(30 * S), wLoop = (int)(56 * S), wRest = (int)(80 * S), wLook = (int)(78 * S);
                // Under the caption: Look ▾ · Reset View, and ⛶ at the right.
                // One row: Look ▾ · Reset View on the left; Full Body / Head / Bust and ⛶ (square icon buttons) on the right.
                int y = cap.Bottom + (int)(4 * S), by = y + (fbs - bh) / 2;
                var lk = new Rectangle(ctlX, by, wLook, bh); if (lookBtn.Bounds != lk) lookBtn.Bounds = lk;
                var rb = new Rectangle(lk.Right + gap, by, wRest, bh); if (restBtn.Bounds != rb) restBtn.Bounds = rb;
                var fr = new Rectangle(ctlX + ctlW - fbs, y, fbs, fbs); if (fullBtn.Bounds != fr) fullBtn.Bounds = fr;
                string label = full ? "Back" : "Full Screen";
                if (fullBtn.Text != label) { fullBtn.Text = label; fullBtn.Invalidate(); }
                foreach (Control c in new Control[] { lookBtn, restBtn, fullBtn }) if (!c.Visible) c.Visible = true;
                if (powersBtn != null)
                {
                    // The Powers toggle heads the power block: the bottom of the panel, or under the Look row when full screen.
                    var pw2 = full ? new Rectangle(ctlX, fr.Bottom + (int)(10 * S), (int)(92 * S), bh) : new Rectangle(pad, Height - powersH, (int)(92 * S), bh);
                    if (powersBtn.Bounds != pw2) powersBtn.Bounds = pw2;
                    if (powersBtn.Visible != powerBlock) powersBtn.Visible = powerBlock;
                }
                // The playback bar under the view, always shown.
                int barH = (int)(22 * S) + bh + 3 * gap;
                var bar = new Rectangle(viewRect.X, viewRect.Bottom, viewRect.Width, barH);
                if (playBar.Bounds != bar) playBar.Bounds = bar;
                int iw = bar.Width - 2 * gap;
                var fsb = new Rectangle(gap, gap, iw, (int)(22 * S)); if (frameSlider.Bounds != fsb) frameSlider.Bounds = fsb;
                var ab = new Rectangle(gap, fsb.Bottom + gap, iw - wPlay - wLoop - 2 * gap, bh); if (animBox.Bounds != ab) animBox.Bounds = ab;
                var pb = new Rectangle(ab.Right + gap, ab.Y, wPlay, bh); if (playBtn.Bounds != pb) playBtn.Bounds = pb;
                var lb = new Rectangle(pb.Right + gap, ab.Y, wLoop, bh); if (loopBtn.Bounds != lb) loopBtn.Bounds = lb;
                if (!playBar.Visible) playBar.Visible = true;
                if (frameFullBtn != null && frameHeadBtn != null && frameBustBtn != null)
                {
                    // Left to right: Head, Bust, Full Body (Kurt).
                    var f3 = new Rectangle(fr.X - 2 * gap - fbs, y, fbs, fbs); if (frameFullBtn.Bounds != f3) frameFullBtn.Bounds = f3;
                    var f2 = new Rectangle(f3.X - gap - fbs, y, fbs, fbs); if (frameBustBtn.Bounds != f2) frameBustBtn.Bounds = f2;
                    var f1 = new Rectangle(f2.X - gap - fbs, y, fbs, fbs); if (frameHeadBtn.Bounds != f1) frameHeadBtn.Bounds = f1;
                    foreach (var b in new[] { frameFullBtn, frameHeadBtn, frameBustBtn }) if (!b.Visible) b.Visible = true;
                }
            }
        }
        // Caption: the texture, where it's from, and whose choice it is.
        else if (mod != null && index >= 0 && index < items.Count)
        {
            var c = items[index];
            var cap = new Rectangle(pad, card.Bottom + (int)(4 * S), Width - 2 * pad, (int)(18 * S));
            TextRenderer.DrawText(g, c.Texture, smallFont, cap, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string whose = mod.LocalPreview == c.Key ? "User Pick" : mod.Manifest.PreviewImage == c.Key ? "The Mod's Choice" : "Chosen Automatically";
            cap.Offset(0, (int)(18 * S));
            TextRenderer.DrawText(g, $"{c.Source}  ·  {whose}", smallFont, cap, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // The strip: thumbnails, with arrows when they don't all fit.
        thumbRects.Clear();
        leftArrow = rightArrow = strip = Rectangle.Empty;
        powerRects.Clear(); powerLeft = powerRight = Rectangle.Empty;
        if (mod != null && powerBlock && powersBtn != null)
        {
            var hb = powersBtn.Bounds;
            string head = !powersLoaded ? "Loading Powers…" : powerFilter != null && heroPowers.FirstOrDefault(p => p.Prototype == powerFilter) is { } fp ? fp.Name + " (Click Again for All)" : $"{heroPowers.Count} Powers: Click One to Play It";
            var ht = Rectangle.FromLTRB(hb.Right + pgap, hb.Top, full ? ctlX + ctlW : Width - pad, hb.Bottom);
            TextRenderer.DrawText(g, head, smallFont, ht, Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        if (mod != null && show3D && heroPowers.Count > 0 && powerBlock && powersBtn != null)
        {
            if (full)
            {
                // Full screen: every power as a grid in the column, under the Powers toggle.
                int gx = card.Right + pad, gy = powersBtn.Bottom + pgap, cell = ThumbSize, gp = pgap;
                int per = Math.Max(1, (panelW + gp) / (cell + gp));
                for (int k = 0; k < heroPowers.Count; k++)
                {
                    var r = new Rectangle(gx + k % per * (cell + gp), gy + k / per * (cell + gp), cell, cell);
                    if (r.Bottom > Height - pad) break;
                    DrawPower(g, r, k);
                }
            }
            else
            {
                // Two rows under the Powers toggle, scrolled sideways together when they don't fit.
                int ptop = powersBtn.Bottom + pgap, aw = (int)(16 * S), step = ThumbSize + pgap, areaH = 2 * ThumbSize + pgap;
                int cols = (heroPowers.Count + 1) / 2;
                var pstrip = new Rectangle(pad, ptop, Width - 2 * pad, areaH);
                int ptotal = cols * step - pgap;
                if (ptotal > pstrip.Width)
                {
                    powerLeft = new Rectangle(pad, ptop, aw, areaH);
                    powerRight = new Rectangle(Width - pad - aw, ptop, aw, areaH);
                    pstrip = new Rectangle(powerLeft.Right + (int)(4 * S), ptop, powerRight.Left - powerLeft.Right - (int)(8 * S), areaH);
                    powerScroll = Math.Clamp(powerScroll, 0, Math.Max(0, ptotal - pstrip.Width));
                    foreach (var (ar, left, on) in new[] { (powerLeft, true, powerScroll > 0), (powerRight, false, powerScroll < ptotal - pstrip.Width) })
                    {
                        using var ab = new SolidBrush(on ? Ui.Text : Color.FromArgb(70, 255, 255, 255));
                        float cx = ar.X + ar.Width / 2f, cy = ar.Y + ar.Height / 2f, aa = 5 * S;
                        g.FillPolygon(ab, left ? [new PointF(cx + aa / 2, cy - aa), new PointF(cx - aa / 2, cy), new PointF(cx + aa / 2, cy + aa)]
                                               : [new PointF(cx - aa / 2, cy - aa), new PointF(cx + aa / 2, cy), new PointF(cx - aa / 2, cy + aa)]);
                    }
                }
                else { powerScroll = 0; pstrip = new Rectangle(pad, ptop, Math.Max(0, ptotal), areaH); }
                var pclip = g.Clip;
                g.SetClip(pstrip);
                for (int k = 0; k < heroPowers.Count; k++)
                {
                    var r = new Rectangle(pstrip.X + k % cols * step - powerScroll, ptop + k / cols * step, ThumbSize, ThumbSize);
                    if (r.Right < pstrip.Left || r.Left > pstrip.Right) continue;
                    DrawPower(g, r, k);
                }
                g.Clip = pclip;
            }
        }
        if (!showStrip || mod == null) return;
        int top = card.Bottom + captionH - (int)(2 * S), arrowW = (int)(16 * S);
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
                if (thumbs.GetValueOrDefault(ThumbKey(mod, items[i])) is Image t)
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
    public void ChoiceChanged()
    {
        if (mod == null) return;
        // A custom picture may be new, or a new file under the same name: read the pictures again.
        if (ModPictures.IsFile(mod.LocalPreview))
        { var m = mod; mod = null; Mod = m; return; }
        Resolve();
    }

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
            if (changed && !Reveal3D()) { wantedAnim = AnimPart(key); LoadMesh(); }
        }
        else
        {
            int i = items.FindIndex(x => x.Key == key);
            bool changed = show3D || i != index;
            show3D = false; index = i;
            Hide3D();
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
            viewer = new ModelView { Background = Ui.Card, BackColor = Ui.Card, Visible = false };
            viewer.ViewChanged += () => { if (mod != null && meshIndex >= 0 && meshIndex < meshes.Count) PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState); };
            Controls.Add(viewer);
        }
        var r = meshes[meshIndex];
        viewer.Visible = true;
        SaveAnim();
        shownMesh = null;
        StopAnimation(); ClearEffects(); anims = []; animator = null; FillAnims();
        if (mod != null) { float lv = PreviewViews.Light(mod); viewer.Brightness = lv; if (lightSlider != null) lightSlider.Value = lv; }   // this mod's light
        if (mod != null) { float fl = PreviewViews.Lens(mod); viewer.FocalLength = fl; if (lensSlider != null) lensSlider.Value = fl; }    // and lens
        Invalidate();
        if (meshCache.TryGetValue(r.File + "|" + r.Export + "|" + Ui.FileStamp(r.File), out var hit) && hit != null) { Show(hit); return; }
        viewer.ShowMessage("Loading the 3D view…");
        int req = ++request;
        string? cooked = CookedFolder;
        string key = r.File + "|" + r.Export + "|" + Ui.FileStamp(r.File);
        Task.Run(() => { try { var l = ModMeshes.Load(r, cooked, out string why); return (l, why); } catch (Exception ex) { return ((ModMeshes.Loaded?)null, ex.Message); } }).ContinueWith(t =>
        {
            if (IsDisposed || req != request || viewer == null) return;
            var (loaded, why) = t.Result;
            if (meshCache.Count > 3) meshCache.Clear();   // with their mipmapped maps, a few are enough
            meshCache[key] = loaded;
            if (loaded == null) viewer.ShowMessage($"{r.Name} can't be shown in 3D ({why}).");
            else Show(loaded);
        }, TaskScheduler.FromCurrentSynchronizationContext());

        void Show(ModMeshes.Loaded l)
        {
            viewer!.ShowMesh(l);
            if (mod != null && StartView(r) is { } saved) viewer.ViewState = saved;
            shownMesh = r.Key;
            shownLoaded = l;
            rig.Clear();
            animator = new MeshAnimator(l.Bones, l.Positions, l.Normals, l.Influences, l.Tangents);
            // Posed at once (rest): a new animator's positions and bone matrices are all zero until its first pose, and the
            // props redraw "the last pose" when they arrive; with no animation restored (Unworthy Thor, Thor Infinity War)
            // the whole model collapsed to a point and vanished.
            animator.Pose(null, 0);
            LoadProps();
            var m = mod; string? cooked2 = CookedFolder;
            var pkgs = m == null ? [] : m.Manifest.UpkReplacements.Select(f => (f, Path.Combine(m.Folder, f))).ToList();
            int req2 = request;
            Task.Run(() => { try { return ModAnimations.For(r, l.Bones, pkgs, cooked2); } catch { return []; } }).ContinueWith(t =>
            {
                if (IsDisposed || req2 != request || mod?.FolderName != m?.FolderName) return;   // (a reload of the same mod is a new object)
                anims = allAnims = t.Result;
                FillAnims();
                LoadHeroPowers();
                // This PC's last animation and frame for the mesh, else the pick's "@animation".
                var saved = mod == null ? null : PreviewViews.GetAnim(PreviewViews.Key(mod, r));
                string? name = saved?.Name ?? wantedAnim;
                int want = name == null ? -1 : anims.FindIndex(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (want >= 0 && animBox != null) { restoreTime = saved != null && want >= 0 && anims[want].Name.Equals(saved.Name, StringComparison.OrdinalIgnoreCase) ? saved.Time : null; animBox.SelectedIndex = want + 1; }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>The animation drop-down: "Rest Pose", then the mesh's animations (shown under the 3D caption).</summary>
    void FillAnims()
    {
        if (animBox == null)
        {
            animBox = new DropDown { Font = Ui.Regular(9f), Visible = false, MaxDropDownItems = 24 };
            Ui.Tip(animBox, "Play one of this mesh's animations in the 3D view (it loops). Remembered with the mesh for this mod.");
            animBox.SelectedIndexChanged += (_, _) => { if (!fillingAnims) PlaySelected(); };
            Controls.Add(animBox);
            playBtn = Ui.FlatButton("▶", TogglePlay, "Play or pause the animation (it loads paused on its first frame).");
            loopBtn = Ui.FlatButton("⟳ Loop", () => { PreviewViews.Loop = !PreviewViews.Loop; UpdateButtons(); }, "Loop the animation, or play it once and stop on its last frame. Remembered.");
            restBtn = Ui.FlatButton("Reset View", ResetView, "Back to the mod's own view of the model, or the default one (your turned, zoomed or panned view is saved per mesh; this forgets it).");
            foreach (var b in new[] { playBtn, loopBtn, restBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
            specBtn = Ui.FlatButton("Spec", () => { PreviewViews.Spec = !PreviewViews.Spec; ApplyShading(); }, "Show the specular highlights (shine) the materials set. Lit when on; remembered on this PC.");
            reflBtn = Ui.FlatButton("Reflect", () => { PreviewViews.Reflect = !PreviewViews.Reflect; ApplyShading(); }, "Show reflections of the materials' own environment images. Lit when on; remembered on this PC.");
            glowBtn = Ui.FlatButton("Glow", () => { PreviewViews.Glow = !PreviewViews.Glow; ApplyShading(); }, "Show glowing (emissive) parts. Lit when on; remembered on this PC.");
            foreach (var b in new[] { specBtn, reflBtn, glowBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
            propsBtn = Ui.FlatButton("Props", () => { PreviewViews.Props = !PreviewViews.Props; ApplyShading(); LoadProps(); }, "Show the weapons and props the game attaches to this character (Thor's hammer in his hand …), held on their bones. Lit when on; remembered on this PC.");
            propsBtn.AutoSize = false; propsBtn.Padding = new Padding(0); propsBtn.Visible = false; Controls.Add(propsBtn);
            powersBtn = Ui.FlatButton("Power FXs", () => { PreviewViews.Powers = !PreviewViews.Powers; ApplyShading(); LoadEffects(); }, "Play the effects of the power an animation belongs to with it (lightning, shockwaves, trails …, read from the game's or the mod's power packages). Lit when on; remembered on this PC.");
            powersBtn.AutoSize = false; powersBtn.Padding = new Padding(0); powersBtn.Visible = false; Controls.Add(powersBtn);
            frameFullBtn = Ui.FlatButton("Full Body", () => FrameShot(Framing.Shot.Full), "Frame the whole character (as Create from 3D does). Saved as this mesh's view.");
            frameHeadBtn = Ui.FlatButton("Head", () => FrameShot(Framing.Shot.HeadShoulders), "Frame the head and shoulders. Saved as this mesh's view.");
            frameBustBtn = Ui.FlatButton("Bust", () => FrameShot(Framing.Shot.Bust), "Frame head and chest. Saved as this mesh's view.");
            foreach (var b in new[] { frameFullBtn, frameHeadBtn, frameBustBtn }) { b.AutoSize = false; b.Padding = new Padding(0); b.Visible = false; Controls.Add(b); }
            Ui.IconPainters.AddOrUpdate(frameFullBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Full));
            Ui.IconPainters.AddOrUpdate(frameHeadBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.HeadShoulders));
            Ui.IconPainters.AddOrUpdate(frameBustBtn, (g, r, c) => PersonIcon(g, r, c, Framing.Shot.Bust));
            fullBtn = Ui.FlatButton("⛶", ToggleFull, "Full screen: the 3D view fills the screen with its controls beside it (F11). Esc, F11 or ✕ come back.");
            fullBtn.AutoSize = false; fullBtn.Padding = new Padding(0); fullBtn.Visible = false; Controls.Add(fullBtn);
            Ui.IconPainters.AddOrUpdate(fullBtn, (g, r, c) => FullScreenIcon(g, r, c, IsFull));
            // Frame (Kurt: like the icon maker's): where the animation is; dragging it pauses and scrubs.
            frameSlider = new LightSlider { Visible = false, Label = "Frame", Min = 0, Max = 1, Step = 1, Mark = null, Enabled = false, Home = () => 0, Format = v => playing == null ? "—" : $"{v:0} / {playFrames:0}" };
            frameSlider.ValueChanged += ScrubTo;
            frameSlider.Committed += SaveAnim;
            Ui.Tip(frameSlider, "The animation's frame: drag to scrub through it (it pauses), or use the arrow keys (Shift: 5 frames, Home / End: first / last). ▶ plays on from there.");
            Controls.Add(frameSlider);
            lightSlider = new LightSlider { Visible = false, Home = () => mod == null ? 1f : PreviewViews.AuthorLight(mod) };
            lightSlider.ValueChanged += () => { if (viewer != null) viewer.Brightness = lightSlider.Value; };
            lightSlider.Committed += () => { if (mod != null) PreviewViews.SetLight(mod, lightSlider.Value); };
            Ui.Tip(lightSlider, "Light brightness in the 3D view for this mod (drag, or the mouse wheel; double-click: back to the mod's own level, else 100%). Remembered per mod on this PC; Export can put it into the mod.");
            Controls.Add(lightSlider);
            lensSlider = new LightSlider { Visible = false, Label = "Lens", Min = 15, Max = 200, Step = 0.5f, Mark = ModelView.DefaultFocalLength, Format = v => $"{v:0} mm", Home = () => ModelView.DefaultFocalLength };
            lensSlider.ValueChanged += () => { if (viewer != null) viewer.FocalLength = lensSlider.Value; };
            lensSlider.Committed += () => { if (mod != null) PreviewViews.SetLens(mod, lensSlider.Value); };
            Ui.Tip(lensSlider, "The camera's lens (35 mm equivalent): short is wide with strong perspective, long is flatter; the model stays the same size (double-click: 50 mm). Remembered per mod on this PC.");
            Controls.Remove(lightSlider);   // both live in the Look ▾ menu
            // The playback bar over the view's bottom (shown while the mouse is over the view).
            playBar = new Panel { Visible = false, BackColor = Color.FromArgb(24, 26, 34) };
            foreach (Control c in new Control[] { frameSlider, animBox, playBtn, loopBtn }) { playBar.Controls.Add(c); c.Visible = true; }
            Controls.Add(playBar);
            lookBtn = Ui.FlatButton("Look ▾", ShowLookMenu, "How the model is shown: Spec, Reflect, Glow and Props on or off, and the Light and Lens sliders. Remembered on this PC.");
            lookBtn.AutoSize = false; lookBtn.Padding = new Padding(0); lookBtn.Visible = false; Controls.Add(lookBtn);
            hoverTimer.Tick += (_, _) => CheckHover();
            hoverTimer.Start();
        }
        fillingAnims = true;
        animBox.BeginUpdate();
        animBox.Items.Clear();
        string? filterName = powerFilter == null ? null : heroPowers.FirstOrDefault(p => p.Prototype == powerFilter)?.Name;
        animBox.Items.Add(filterName != null ? $"Rest Pose  ·  {anims.Count} of {allAnims.Count} animations ({filterName})"
            : anims.Count > 0 ? $"Rest Pose  ·  {anims.Count} animations" : show3D ? "Rest Pose  ·  (looking for animations…)" : "Rest Pose");
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
        ApplyShading();
        if (viewer != null) viewer.Moving = playing != null && !paused;   // smaller frames while playing (ModelView.Moving)
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

    /// <summary>The Spec / Reflect / Glow toggles into the 3D view, and their look (accent when on, like Loop).</summary>
    void ApplyShading()
    {
        if (viewer != null) { viewer.ShowSpec = PreviewViews.Spec; viewer.ShowReflections = PreviewViews.Reflect; viewer.ShowGlow = PreviewViews.Glow; }
        if (lookMenu != null)
            foreach (ToolStripItem it in lookMenu.Items)
                if (it is ToolStripMenuItem mi)
                    mi.Checked = mi.Text switch { "Spec" => PreviewViews.Spec, "Reflect" => PreviewViews.Reflect, "Glow" => PreviewViews.Glow, "Props" => PreviewViews.Props, "Powers" => PreviewViews.Powers, _ => mi.Checked };
        foreach (var (b, on) in new[] { (specBtn, PreviewViews.Spec), (reflBtn, PreviewViews.Reflect), (glowBtn, PreviewViews.Glow), (propsBtn, PreviewViews.Props), (powersBtn, PreviewViews.Powers) })
            if (b != null) Ui.Lit(b, on);
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
            ClearEffects();
            animator.Pose(null, 0); ShowPose();
            ShowFrame(0);
            if (!fillingAnims && MeshOk) { Picked?.Invoke(mod, meshes[meshIndex].Key); SaveAnim(); }
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
            paused = true;
            bool restoring = restoreTime != null;
            playTime = Math.Clamp(restoreTime ?? 0, 0, playSeconds); restoreTime = null;
            rig.SetParentAnimation(playing);
            animator.Pose(playing, playSeconds > 0 ? (float)(playTime / playSeconds * playFrames) : 0); ShowPose();
            ShowFrame(playSeconds > 0 ? (float)(playTime / playSeconds * playFrames) : 0);
            UpdateButtons();
            LoadEffects();
            LoadPropSwitches();
            if (autoPlay) { autoPlay = false; if (paused) TogglePlay(); }
            // A restored animation is shown as it was left, not a new pick (no undo step, the preview choice unchanged).
            if (mod != null && MeshOk && !restoring) Picked?.Invoke(mod, CurrentMeshKey());
            SaveAnim();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The frame slider moved by the user: pause and show that frame.</summary>
    void ScrubTo()
    {
        if (settingFrame || frameSlider == null || playing == null || animator == null || viewer == null || playFrames <= 0) return;
        if (!paused) { playTimer.Stop(); playClock.Reset(); paused = true; UpdateButtons(); }
        playTime = frameSlider.Value / playFrames * playSeconds;
        FxReplay(playTime);
        animator.Pose(playing, frameSlider.Value);
        ShowPose();
    }

    /// <summary>The frame slider follows the animation (set without scrubbing).</summary>
    void ShowFrame(float frame)
    {
        if (frameSlider == null) return;
        settingFrame = true;
        frameSlider.Enabled = playing != null;
        frameSlider.Max = Math.Max(1, playFrames);
        frameSlider.Value = playing == null ? 0 : Math.Clamp(frame, 0, Math.Max(1, playFrames));
        settingFrame = false;
        frameSlider.Invalidate();
    }

    void PlayTick(object? sender, EventArgs e)
    {
        if (playing == null || animator == null || viewer == null) { StopAnimation(); return; }
        if (!show3D || !Visible) { Pause(); return; }   // hidden: hold the frame
        if (paused) return;
        double now = playClock.Elapsed.TotalSeconds;
        playTime += now - lastTick; lastTick = now;
        float frame;
        if (playSeconds <= 0 || playFrames <= 0) frame = 0;
        else if (PreviewViews.Loop) frame = (float)(playTime % playSeconds / playSeconds * playFrames);
        else if (playTime >= playSeconds) { playTime = playSeconds; frame = playFrames; paused = true; playTimer.Stop(); UpdateButtons(); }
        else frame = (float)(playTime / playSeconds * playFrames);
        animator.Pose(playing, frame);
        FxAdvance(PreviewViews.Loop && playSeconds > 0 ? playTime % playSeconds : playTime, ended: !PreviewViews.Loop && playTime >= playSeconds);
        ShowPose();
        ShowFrame(frame);
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
        if (frameSlider != null)
        {
            Check("the frame slider follows the animation", frameSlider.Enabled && Math.Abs(frameSlider.Max - Math.Max(1, playFrames)) < 1e-4 && frameSlider.Value > 0);
            playBtn.PerformClick();   // playing again, then a drag on the slider
            await Idle(200);
            frameSlider.Value = playFrames * 0.5f;
            Check("dragging the frame slider pauses and shows that frame", paused && Math.Abs(playTime - playSeconds * 0.5) < 1e-3);
            held = playTime;
        }
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

        if (lightSlider != null)
        {
            float was = lightSlider.Value;
            lightSlider.Value = 1.5f;
            Check("the light slider sets the 3D view's brightness", Math.Abs(viewer.Brightness - 1.5f) < 1e-4 && lightSlider.Visible);
            float saved = PreviewViews.Light(mod);
            PreviewViews.SetLight(mod, 1.5f);
            Check("and is kept for this mod", Math.Abs(PreviewViews.Light(mod) - 1.5f) < 1e-4);
            PreviewViews.SetLight(mod, saved);
            lightSlider.Value = was;
        }

        // Kurt: posed, zoomed, then a picture, then 3D again forgot it all. Clicks go through the window (a pick saves
        // and reloads the list), as a user's do.
        if (thumbRects.Any(t => t.Index == Offset))
        {
            PreviewViews.Loop = true; UpdateButtons();
            animBox.SelectedIndex = Math.Min(2, anims.Count);
            await Wait(() => playing != null && animBox.SelectedIndex - 1 < anims.Count);
            string animWas = animBox.SelectedItem?.ToString() ?? "";
            playTime = playSeconds * 0.4; animator.Pose(playing, (float)(playTime / playSeconds * playFrames)); ShowPose();
            double frameWas = playTime;
            viewer.ViewState = [0.7f, 0.3f, 1.4f, 0.05f, 0f, 0f];
            PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState);   // as a drag saves it
            var viewWas = viewer.ViewState;
            var pic = thumbRects.First(t => t.Index == Offset).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(800);
            Check("a picture shows (the 3D view hidden)", !show3D && viewer.Visible == false);
            var tile = thumbRects.First(t => t.Index == 0).Rect;
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(800);
            Check($"back to 3D: same animation ({animWas}), same frame, same camera",
                show3D && viewer.Visible && animBox.SelectedItem?.ToString() == animWas && playing != null && Math.Abs(playTime - frameWas) < 1e-6
                && viewer.ViewState.Zip(viewWas).All(p => Math.Abs(p.First - p.Second) < 1e-4));
            playBtn.PerformClick();
            await Idle(300);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, pic.X + 4, pic.Y + 4, 0));
            await Idle(500);
            double heldAt = playTime;
            await Idle(300);
            Check("playing, then a picture: it holds its frame", paused && playTime == heldAt);
            OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, tile.X + 4, tile.Y + 4, 0));
            await Idle(400);
            Check("and plays on when 3D shows again", !paused && playTime > heldAt);
            playBtn.PerformClick();
        }
        PreviewViews.Loop = loopWas; UpdateButtons();
        return log;
    }

    /// <summary>--preview-selftest: poses the 3D view (an animation, a frame, a camera) as a user would; returns it.</summary>
    internal async Task<(string Anim, double Time, float[] View)?> PoseForTest()
    {
        if (animBox == null || viewer == null || animator == null || anims.Count < 3 || !MeshOk) return null;
        animBox.SelectedIndex = 3;
        for (int t = 0; t < 10000 && playing == null; t += 50) { await Task.Delay(50); Application.DoEvents(); }
        if (playing == null) return null;
        playTime = playSeconds * 0.55; animator.Pose(playing, (float)(playTime / playSeconds * playFrames)); ShowPose();
        Pause();   // as the pause button: saved
        viewer.ViewState = [0.9f, -0.2f, 1.3f, 0f, 0.04f, 0f];
        PreviewViews.Set(PreviewViews.Key(mod!, meshes[meshIndex]), viewer.ViewState);   // as a drag saves it
        return (anims[2].Name, playTime, viewer.ViewState);
    }

    /// <summary>--preview-selftest: the 3D view's light now, or null.</summary>
    internal float? ShownLight => show3D && viewer != null ? viewer.Brightness : null;

    /// <summary>--preview-selftest: what the 3D view shows now (animation, time, camera), or null when it isn't showing.</summary>
    internal (string? Anim, double Time, float[] View)? Shown3D() =>
        !show3D || viewer == null || animBox == null ? null : (animBox.SelectedIndex > 0 && playing != null ? anims[animBox.SelectedIndex - 1].Name : null, playTime, viewer.ViewState);

    /// <summary>A picture shows: the 3D view is hidden but kept (mesh, animation paused where it was, camera).</summary>
    void Hide3D()
    {
        if (viewer != null) viewer.Visible = false;
        if (playing != null && !paused) resumeOnReveal = true;
        Pause();
        HideAnimControls();
    }
    bool resumeOnReveal;

    /// <summary>Back to 3D on the mesh the view still holds: shown as it was left. False when it has to be loaded.</summary>
    bool Reveal3D()
    {
        if (viewer == null || !MeshOk || shownMesh != meshes[meshIndex].Key) return false;
        viewer.Visible = true;
        UpdateButtons();
        if (resumeOnReveal && playing != null && paused) TogglePlay();   // it was playing: carry on
        resumeOnReveal = false;
        Invalidate();
        return true;
    }

    void Pause() { playTimer.Stop(); playClock.Reset(); paused = true; UpdateButtons(); SaveAnim(); }

    /// <summary>Esc (Kurt): pauses a playing animation; false when nothing was playing (Esc then does its usual job).</summary>
    public bool PausePlayback()
    {
        if (playing == null || paused || !show3D) return false;
        Pause();
        return true;
    }

    /// <summary>Stores the shown mesh's animation and the frame it's on (preview_views.json), so it comes back as it was.</summary>
    void SaveAnim()
    {
        if (mod == null || shownMesh == null || animBox == null || fillingAnims) return;
        int i = animBox.SelectedIndex - 1;
        if (i >= 0 && playing == null) return;   // still loading
        double t = playSeconds <= 0 ? 0 : PreviewViews.Loop ? playTime % playSeconds : Math.Min(playTime, playSeconds);   // a loop's clock runs on
        PreviewViews.SetAnim(PreviewViews.Key(mod, shownMesh), i >= 0 && i < anims.Count ? anims[i].Name : null, t);
    }

    void HideAnimControls()
    {
        // (Not the playback bar's own controls: hiding the bar hides them, and nothing showed them again: Kurt's
        // "animation filter and scrub bar keep disappearing".)
        foreach (Control? c in new Control?[] { restBtn, lightSlider, lensSlider, specBtn, reflBtn, glowBtn, propsBtn, powersBtn, fullBtn, frameFullBtn, frameHeadBtn, frameBustBtn, playBar, lookBtn }) if (c != null) c.Visible = false;
        if (IsFull) ToggleFull();   // a picture (or another mod without 3D) shows: back from full screen
    }

    /// <summary>The pose last made by the animator, with the props on their bones.</summary>
    void ShowPose()
    {
        if (viewer == null || animator == null) return;
        if (fxPlayer != null) { viewer.Effects = fxPlayer.Quads(); viewer.EffectTris = fxPlayer.Tris(); }
        else if (viewer.Effects.Count > 0 || viewer.EffectTris.Count > 0) { viewer.Effects = []; viewer.EffectTris = []; }
        // Props shown at this moment of the animation (a power's rules have their times).
        rig.At(playing == null || playSeconds <= 0 ? 0 : PreviewViews.Loop ? playTime % playSeconds : Math.Min(playTime, playSeconds));
        rig.Update(viewer, animator);
    }

    void ClearEffects() { fxRequest++; fxPlayer = null; fxNote = ""; if (viewer != null) { viewer.Effects = []; viewer.EffectTris = []; } }

    /// <summary>The game data (Calligraphy.sip), read once in the background for every power lookup.</summary>
    Task<Fx.GameData?> GameDb(string cooked)
    {
        if (fxDb != null) return fxDb;
        string sip = Path.GetFullPath(Path.Combine(cooked, "..", "..", "..", "Data", "Game", "Calligraphy.sip"));
        return fxDb = Task.Run(() => { try { return File.Exists(sip) ? new Fx.GameData(Fx.SipArchive.Load(sip)) : null; } catch (Exception ex) when (ex is IOException or InvalidDataException) { return (Fx.GameData?)null; } });
    }

    /// <summary>
    /// The effects of the power the playing animation belongs to (Powers on): the hero's power packages name it
    /// (PowerIndex, the mod's copies first), the game data gives the power with what it sets off (PowerEffects.For),
    /// then they play from the animation's time. Read in the background; nothing is added to the mod.
    /// </summary>
    void LoadEffects()
    {
        ClearEffects();
        if (!PreviewViews.Powers || playing == null || animator == null || mod == null || !MeshOk || CookedFolder is not string cooked || animBox == null) { ShowPose(); Invalidate(); return; }
        int ai = animBox.SelectedIndex - 1;
        if (ai < 0 || ai >= anims.Count) return;
        string anim = anims[ai].Name;
        var r = meshes[meshIndex];
        // The hero whose animations these are: UC__MarvelPlayer_<Hero>_… (a moved costume plays its target hero's).
        var parts = r.Package.Split('_', StringSplitOptions.RemoveEmptyEntries);
        string? hero = parts.Length >= 2 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3 ? parts[2] : null;
        if (hero == null) return;
        var modFiles = mod.Manifest.UpkReplacements.Select(f => Path.Combine(mod.Folder, f)).ToList();
        int req = fxRequest;
        var a = animator; var l = shownLoaded;
        string meshFile = r.File, meshName = r.Name;
        fxNote = "Reading Powers…"; Invalidate();
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            var db = dbt.Result;
            if (db == null) return ((Fx.PowerEffects?)null, "", (Dictionary<string, (string, System.Numerics.Matrix4x4)>?)null);
            var idx = Fx.PowerIndex.For(hero, cooked, modFiles);
            if (!idx.TryGetValue(anim, out var powers) || powers.Count == 0) return (null, "", null);
            var byClass = Fx.PowerIndex.PrototypesByClass(db);
            var proto = powers.SelectMany(p => byClass.TryGetValue(p.Class, out var list) ? list : []).FirstOrDefault();
            if (proto == null) return (null, "", null);
            var fx = Fx.PowerEffects.For(new Fx.FxGame(cooked, modFiles), db, proto, hero);
            return (fx, Path.GetFileNameWithoutExtension(proto), Fx.FxSockets.Of(meshFile, meshName));
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != fxRequest || animator != a || playing == null || viewer == null) return;
            var (fx, power, sockets) = t.Status == TaskStatus.RanToCompletion ? t.Result : (null, "", null);
            if (fx == null) { fxNote = ""; Invalidate(); return; }
            fxSockets = sockets ?? new();
            var phase = Fx.PowerEffects.Player.PhaseOf(anim);
            // The target of effects at the world position: the ground 250 units in front (characters face +X).
            float ground = l == null || l.Positions.Length == 0 ? 0 : l.Positions.Min(v => v.Z);
            fxPlayer = new Fx.PowerEffects.Player(fx, Socket, new System.Numerics.Vector3(250, 0, ground), phase) { AnimSeconds = Math.Max(0.1f, playSeconds) };
            viewer.EffectStrength = PreviewViews.FxPower;   // the effects' opacity / glow (default 15 %)
            int n = fx.Effects.Count(e => phase(e));
            fxNote = $"{Ui.TitleCase(SplitWords(power))} · {n} Effect{(n == 1 ? "" : "s")}";
            FxReplay(PreviewViews.Loop && playSeconds > 0 ? playTime % playSeconds : playTime);
            animator.Pose(playing, playSeconds > 0 ? (float)((PreviewViews.Loop ? playTime % playSeconds : playTime) / playSeconds * playFrames) : 0);
            ShowPose();
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static string SplitWords(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>A socket's place in the pose last made: the mesh's socket on its bone, else a bone of that name, else null.</summary>
    System.Numerics.Matrix4x4? Socket(string name)
    {
        if (animator == null) return null;
        if (fxSockets.TryGetValue(name, out var sk) && animator.BoneIndex(sk.Bone) is int b && b >= 0) return sk.Local * animator.BoneMatrix(b);
        int bi = animator.BoneIndex(name);
        return bi >= 0 ? animator.BoneMatrix(bi) : null;
    }

    /// <summary>The effects moved on to <paramref name="seconds"/> (from where they were; started over when time went back: a loop).</summary>
    void FxAdvance(double seconds, bool ended)
    {
        if (fxPlayer == null) return;
        if (seconds < fxTime - 1e-6) { fxPlayer.Reset(); fxTime = 0; }
        float dt = (float)(seconds - fxTime);
        if (dt > 0 || ended) fxPlayer.Step(Math.Max(0, dt), ended);
        fxTime = seconds;
    }

    /// <summary>The effects as they are at <paramref name="seconds"/>: played from the start in 1/30 s steps, posing for the sockets.</summary>
    void FxReplay(double seconds)
    {
        if (fxPlayer == null || playing == null || animator == null) return;
        fxPlayer.Reset(); fxTime = 0;
        for (double t = 0; t + 1e-6 < seconds; t += 1.0 / 30)
        {
            animator.Pose(playing, playSeconds > 0 ? (float)(t / playSeconds * playFrames) : 0);
            fxPlayer.Step(1f / 30, false);
            fxTime = t + 1.0 / 30;
        }
    }

    /// <summary>
    /// The props the game attaches to the shown character (PropRig.Attached: other meshes of the mod's packages that a
    /// marvelattachment names), loaded in the background and shown with it; none when the Props toggle is off. The view
    /// (camera) stays as it was; the character's framing isn't changed by a prop.
    /// </summary>
    void LoadProps()
    {
        if (viewer == null || animator == null || shownLoaded == null || !MeshOk) return;
        var main = meshes[meshIndex];
        var l = shownLoaded;
        var a = animator;
        int req = request;
        bool on = PreviewViews.Props;
        var all = meshes.ToList();
        string? cooked = CookedFolder;
        if (!on && rig.Count == 0) return;
        // (in the background: the hero's base package may be read for its props)
        var modPkgs = mod == null ? [] : mod.Manifest.UpkReplacements.Select(f => (f, Path.Combine(mod.Folder, f))).ToList();
        Task.Run(() => (on ? PropRig.Attached(main, all, cooked) : []).Select(w =>
        {
            try
            {
                var lm = ModMeshes.Load(w.Ref, cooked, out _);
                // An animated prop gets its own animator and the animations made for its skeleton.
                if (lm != null && w.UseParentAnim)
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: (List<AnimRef>?)null);
                if (lm != null && w.Class.StartsWith("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase))
                    return (W: w, Mesh: lm, A: new MeshAnimator(lm.Bones, lm.Positions, lm.Normals, lm.Influences, lm.Tangents), R: ModAnimations.For(w.Ref, lm.Bones, modPkgs, cooked, minBones: 1));
                return (W: w, Mesh: lm, A: (MeshAnimator?)null, R: (List<AnimRef>?)null);
            }
            catch { return (W: w, Mesh: (ModMeshes.Loaded?)null, A: (MeshAnimator?)null, R: (List<AnimRef>?)null); }
        }).ToList())
            .ContinueWith(t =>
            {
                if (IsDisposed || req != request || viewer == null || animator != a || shownLoaded != l) return;
                baseProps = [.. t.Result.Where(x => x.Mesh != null).Select(x => (x.W, x.Mesh!, x.A, x.R))];
                RebuildRig();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // The character's own props (LoadProps) and the ones the playing power brings from its own package (LoadPropSwitches:
    // Jean Grey's, Luke Cage's and Magneto's thrown cars are defined in their power packages).
    List<(PropRig.Prop W, ModMeshes.Loaded M, MeshAnimator? A, List<AnimRef>? R)> baseProps = [];
    List<(PropRig.Prop W, ModMeshes.Loaded M)> powerProps = [];
    Dictionary<string, AnimExportCli.Animation.BoneAnimation> propSeqs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character with its props and the power's, as one mesh; the view and the pose kept.</summary>
    void RebuildRig()
    {
        if (viewer == null || animator == null || shownLoaded == null) return;
        rig.Clear();
        // (a prop rigged to the character's skeleton is held at the root: its bind pose is already in place)
        rig.SetParentAnimation(playing);
        foreach (var (w, m, an, refs) in baseProps) rig.Add(m, w.UseParentAnim ? -1 : PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class, an, refs, w.UseParentAnim);
        foreach (var (w, m) in powerProps) rig.Add(m, PropRig.BoneFor(animator, w.Bone), w.Slots, w.OnDemand, w.Class);
        rig.SetRules(propRules, propContact, playSeconds);
        rig.SetMotions(propSeqs);
        var keep = viewer.ViewState;
        viewer.ShowMesh(rig.Combine(shownLoaded), shownLoaded.Positions.Length);   // framed (and the saved view measured) by the character alone
        viewer.ViewState = keep;
        ShowPose();
    }

    /// <summary>One power button: its icon on a card; accent border while one of its animations plays, tinted while it filters.</summary>
    void DrawPower(Graphics g, Rectangle r, int k)
    {
        var pw = heroPowers[k];
        powerRects.Add((r, k));
        string? current = animBox != null && animBox.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? anims[animBox.SelectedIndex - 1].Name : null;
        bool playingIt = current != null && pw.Animations.Contains(current, StringComparer.OrdinalIgnoreCase);
        bool filtering = powerFilter == pw.Prototype;
        using var path = Ui.Round(r, 4 * S);
        using (var fill = new SolidBrush(filtering ? Color.FromArgb(70, Ui.Accent) : k == hoverPower ? Ui.CardHover : Ui.Card)) g.FillPath(fill, path);
        if (pw.Icon != null && CookedFolder is string cooked && Fx.PowerList.Icon(pw.Icon, cooked) is { } img)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int inset = (int)(4 * S);
            g.DrawImage(img, Rectangle.Inflate(r, -inset, -inset));
        }
        else TextRenderer.DrawText(g, string.Concat(pw.Name.Split(' ').Where(w => w.Length > 0).Take(2).Select(w => w[0])), Ui.Heavy(11f), r, Ui.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var pen = new Pen(playingIt || filtering ? Ui.Accent : Ui.Line, playingIt || filtering ? Math.Max(2f, 2f * S) : 1f);
        g.DrawPath(pen, path);
    }

    /// <summary>The hero's powers for the power buttons (in the background; their icons decoded there too).</summary>
    void LoadHeroPowers()
    {
        heroPowers = []; powerRects.Clear(); hoverPower = -1; powerFilter = null; powerScroll = 0; powersLoaded = false;
        int req = ++heroPowersRequest;
        if (mod == null || !MeshOk || CookedFolder is not string cooked) { powersLoaded = true; return; }
        string? hero = HeroOfMesh();
        if (hero == null) { powersLoaded = true; return; }
        var modFiles = mod.Manifest.UpkReplacements.Select(f => Path.Combine(mod.Folder, f)).ToList();
        var names = allAnims.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        GameDb(cooked).ContinueWith(dbt => Task.Run(() =>
        {
            if (dbt.Result is not { } db) return new List<Fx.PowerList.Power>();
            var list = Fx.PowerList.For(db, hero, cooked, modFiles).Where(p => p.Animations.Any(names.Contains)).ToList();
            foreach (var p in list) if (p.Icon != null) Fx.PowerList.Icon(p.Icon, cooked);   // decoded here, off the UI thread
            return list;
        })).Unwrap().ContinueWith(t =>
        {
            if (IsDisposed || req != heroPowersRequest || t.Status != TaskStatus.RanToCompletion) return;
            heroPowers = t.Result;
            powersLoaded = true;
            Invalidate();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>A framing button's icon: a figure cropped as the shot frames it (whole body, head and shoulders, head and chest).</summary>
    static void PersonIcon(Graphics g, Rectangle r, Color c, Framing.Shot shot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.18f;
        var box = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        float w = box.Width, h = box.Height, cx = box.X + w / 2;
        var clip = g.Clip;
        g.SetClip(box);
        using var br = new SolidBrush(c);
        void Head(float cy, float rad) => g.FillEllipse(br, cx - rad, cy - rad, 2 * rad, 2 * rad);
        void Block(RectangleF b, float radius) { using var p = RoundRect(b, radius); g.FillPath(br, p); }
        switch (shot)
        {
            case Framing.Shot.Full:
                Head(box.Y + h * 0.12f, h * 0.12f);
                Block(new RectangleF(cx - w * 0.17f, box.Y + h * 0.27f, w * 0.34f, h * 0.36f), w * 0.08f);
                g.FillRectangle(br, cx - w * 0.15f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                g.FillRectangle(br, cx + w * 0.03f, box.Y + h * 0.58f, w * 0.12f, h * 0.42f);
                break;
            case Framing.Shot.HeadShoulders:
                Head(box.Y + h * 0.36f, h * 0.26f);
                Block(new RectangleF(cx - w * 0.5f, box.Y + h * 0.72f, w, h * 1.2f), w * 0.35f);
                break;
            default:   // bust
                Head(box.Y + h * 0.22f, h * 0.18f);
                Block(new RectangleF(cx - w * 0.36f, box.Y + h * 0.46f, w * 0.72f, h * 1.2f), w * 0.25f);
                break;
        }
        g.Clip = clip;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(0.1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>The full screen button's icon: four corners pointing out, or a cross while full screen (back).</summary>
    static void FullScreenIcon(Graphics g, Rectangle r, Color c, bool full)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float m = r.Width * 0.27f, l = r.Width * 0.16f;
        var b = RectangleF.FromLTRB(r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        using var pen = new Pen(c, Math.Max(1.6f, r.Width / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (full) { g.DrawLine(pen, b.Left, b.Top, b.Right, b.Bottom); g.DrawLine(pen, b.Right, b.Top, b.Left, b.Bottom); return; }
        g.DrawLines(pen, new[] { new PointF(b.Left, b.Top + l), new PointF(b.Left, b.Top), new PointF(b.Left + l, b.Top) });
        g.DrawLines(pen, new[] { new PointF(b.Right - l, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Top + l) });
        g.DrawLines(pen, new[] { new PointF(b.Right, b.Bottom - l), new PointF(b.Right, b.Bottom), new PointF(b.Right - l, b.Bottom) });
        g.DrawLines(pen, new[] { new PointF(b.Left + l, b.Bottom), new PointF(b.Left, b.Bottom), new PointF(b.Left, b.Bottom - l) });
    }

    /// <summary>The hero of the shown mesh's package (UC__MarvelPlayer_<Hero>_…), else null.</summary>
    string? HeroOfMesh()
    {
        if (!MeshOk) return null;
        var parts = meshes[meshIndex].Package.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0].Equals("UC", StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase) ? parts[2] : null;
    }

    /// <summary>A power button clicked: its animations only (its first plays), or all again when it was filtering.</summary>
    void PowerClicked(int k)
    {
        if (animBox == null || k < 0 || k >= heroPowers.Count) return;
        var pw = heroPowers[k];
        string? current = animBox.SelectedIndex > 0 && animBox.SelectedIndex - 1 < anims.Count ? anims[animBox.SelectedIndex - 1].Name : null;
        if (powerFilter == pw.Prototype)
        {
            powerFilter = null;
            anims = allAnims;
            FillAnims();
            int at = current == null ? -1 : anims.FindIndex(a => a.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) { fillingAnims = true; animBox.SelectedIndex = at + 1; fillingAnims = false; }   // the playing one stays
        }
        else
        {
            powerFilter = pw.Prototype;
            anims = [.. allAnims.Where(a => pw.Animations.Contains(a.Name, StringComparer.OrdinalIgnoreCase))];
            FillAnims();
            if (anims.Count > 0) { autoPlay = true; animBox.SelectedIndex = 1; }
        }
        Invalidate();
    }

    /// <summary>Whether the mouse is over the 3D view (or was a moment ago): the playback bar and framing buttons show then.</summary>
    void CheckHover()
    {
        if (!show3D || viewer == null || !IsHandleCreated || !Visible) { if (overView) { overView = false; Invalidate(); } return; }
        bool over = viewRect.Contains(PointToClient(Cursor.Position)) && (FindForm()?.ContainsFocus ?? false) || lookMenu?.Visible == true || animBox?.IsOpen == true;
        if (over) overUntil = DateTime.Now.AddSeconds(1.2);
        bool now = over || DateTime.Now < overUntil;
        if (now != overView) { overView = now; Invalidate(); }
    }

    /// <summary>Look ▾: the look toggles and the Light / Lens sliders; stays open while toggling.</summary>
    void ShowLookMenu()
    {
        if (lookBtn == null || lightSlider == null || lensSlider == null) return;
        if (lookMenu == null)
        {
            lookMenu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            ToolStripMenuItem Item(string text, string tip, Func<bool> get, Action set)
            {
                var it = new ToolStripMenuItem(text) { ToolTipText = tip };
                it.Click += (_, _) => { set(); it.Checked = get(); ApplyShading(); };
                lookMenu.Opening += (_, _) => it.Checked = get();
                return it;
            }
            lookMenu.Items.Add(Item("Spec", "Specular highlights (shine) the materials set.", () => PreviewViews.Spec, () => PreviewViews.Spec = !PreviewViews.Spec));
            lookMenu.Items.Add(Item("Reflect", "Reflections of the materials' own environment images.", () => PreviewViews.Reflect, () => PreviewViews.Reflect = !PreviewViews.Reflect));
            lookMenu.Items.Add(Item("Glow", "Glowing (emissive) parts.", () => PreviewViews.Glow, () => PreviewViews.Glow = !PreviewViews.Glow));
            lookMenu.Items.Add(Item("Props", "The weapons and props the game attaches to the character, held on their bones.", () => PreviewViews.Props, () => { PreviewViews.Props = !PreviewViews.Props; LoadProps(); }));
            lookMenu.Items.Add(new ToolStripSeparator());
            foreach (var sl in new Control[] { lightSlider, lensSlider })
            {
                sl.Visible = true;
                var host = new ToolStripControlHost(sl) { AutoSize = false, Size = new Size((int)(260 * S), (int)(26 * S)), Margin = new Padding((int)(6 * S), 2, (int)(6 * S), 2) };
                lookMenu.Items.Add(host);
            }
            // Clicking a toggle keeps the menu open (several at once); a click outside closes it.
            lookMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        }
        lightSlider.Visible = lensSlider.Visible = true;   // (hidden with the other 3D controls while a picture shows)
        Ui.ShowUnder(lookMenu, lookBtn);
    }

    /// <summary>A framing button: aims the camera like Create from 3D, and keeps it as this mesh's view (as a drag does).</summary>
    void FrameShot(Framing.Shot shot)
    {
        if (viewer == null || !MeshOk || mod == null) return;
        if (animator != null) { Framing.Apply(viewer, animator, playing != null, shot, centerHead: true); if (playing == null) { animator.Pose(null, 0); } }
        else Framing.Apply(viewer, null, false, shot);
        PreviewViews.Set(PreviewViews.Key(mod, meshes[meshIndex]), viewer.ViewState);
    }

    /// <summary>Into or out of full screen: this control moves into a borderless window on the app's monitor and back.</summary>
    public void ToggleFull()
    {
        if (fullForm != null)
        {
            var f = fullForm;
            fullForm = null;
            if (homeParent != null && !homeParent.IsDisposed)
            {
                Parent = homeParent;
                homeParent.Controls.SetChildIndex(this, homeIndex);
            }
            f.Close();
            f.Dispose();
            Invalidate();
            FindForm()?.Activate();
            return;
        }
        if (!show3D || viewer == null || Parent == null) return;
        var owner = FindForm();
        homeParent = Parent;
        homeIndex = homeParent.Controls.GetChildIndex(this);
        var screen = Screen.FromControl(this).Bounds;
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = screen, ShowInTaskbar = false,
            Text = mod == null ? "Preview" : $"Preview: {mod.Name}", BackColor = Ui.GradientTop, KeyPreview = true,
        };
        form.KeyDown += (_, e) => { if (e.KeyCode is Keys.Escape or Keys.F11) { e.Handled = true; if (e.KeyCode == Keys.F11 || !PausePlayback()) ToggleFull(); } };   // Esc: pause first, then back
        form.FormClosing += (_, e) => { if (fullForm == form && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; ToggleFull(); } };   // Alt+F4: back, not closed
        fullForm = form;
        Parent = form;
        form.Show(owner);
        viewer.Focus();
        Invalidate();
    }

    void StopAnimation() { resumeOnReveal = false; playTimer.Stop(); playing = null; rig.SetParentAnimation(null); playClock.Reset(); paused = true; playTime = 0; UpdateButtons(); ShowFrame(0); if (propRules.Count > 0) { propRules = []; rig.SetRules(propRules, 0, 0); } }

    List<ModMeshes.PropRule> propRules = [];
    float propContact;
    readonly Dictionary<string, Task<Dictionary<string, List<Fx.PowerIndex.PowerRef>>>> powerIndexCache = new(StringComparer.OrdinalIgnoreCase);
    int propSwitchRequest;

    /// <summary>
    /// The props for the animation picked (Kurt: Punisher's sawed-off shotgun, not his pistols): the power playing it
    /// (PowerIndex) switches weapon slots (its power package's PowerFxMeshAttachment), read in the background; the
    /// rest pose shows what's always held.
    /// </summary>
    void LoadPropSwitches()
    {
        int req = ++propSwitchRequest;
        if (!PreviewViews.Props || mod == null || !MeshOk || CookedFolder is not string cooked || animBox == null || HeroOfMesh() is not string hero)
        {
            if (propRules.Count > 0 || powerProps.Count > 0) { propRules = []; bool had = powerProps.Count > 0; powerProps = []; if (had) RebuildRig(); else { rig.SetRules(propRules, 0, 0); ShowPose(); } }
            return;
        }
        var have = baseProps.Select(x => x.W).ToList();
        int ai0 = animBox.SelectedIndex - 1;
        var motionRefs = rig.MotionRefs(playing != null && ai0 >= 0 && ai0 < anims.Count ? anims[ai0].Name : null);
        int ai = animBox.SelectedIndex - 1;
        string? anim = playing != null && ai >= 0 && ai < anims.Count ? anims[ai].Name : null;
        var modFiles = mod.Manifest.UpkReplacements.Select(f => Path.Combine(mod.Folder, f)).ToList();
        string key = hero + "|" + mod.Folder;
        if (!powerIndexCache.TryGetValue(key, out var idxTask)) powerIndexCache[key] = idxTask = Task.Run(() => Fx.PowerIndex.For(hero, cooked, modFiles));
        float seconds = playSeconds;
        var dbTask = GameDb(cooked);
        Task.WhenAll(idxTask, dbTask).ContinueWith(done =>
            {
                if (anim == null || idxTask.Status != TaskStatus.RanToCompletion || !idxTask.Result.TryGetValue(anim, out var refs)) return (Rules: new List<ModMeshes.PropRule>(), Contact: 0f, Extra: new List<(PropRig.Prop, ModMeshes.Loaded)>(), Seqs: new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase));
                var files = refs.Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rules = files.SelectMany(ModMeshes.PropRules).Distinct().ToList();
                // A prop the power shows that the character doesn't have: the power package's own attachment (a thrown car).
                var extra = new List<(PropRig.Prop, ModMeshes.Loaded)>();
                foreach (var r in rules.Where(r => r.Show && !have.Any(h => PropRig.Fills(h, r.Target))))
                    foreach (string f in files)
                    {
                        var found = ModMeshes.Attachments(f).Where(x => PropRig.Fills(new PropRig.Prop(null!, x.Bone) { Slots = x.Slots, Class = x.Class }, r.Target)).ToList();
                        if (found.Count == 0) continue;
                        var inPkg = ModMeshes.List([(Path.GetFileName(f), f)], anyPackage: true);
                        foreach (var x in found)
                            if (!extra.Any(e => e.Item1.Class.Equals(x.Class, StringComparison.OrdinalIgnoreCase)) && inPkg.FirstOrDefault(m => m.Name.Equals(x.Mesh, StringComparison.OrdinalIgnoreCase)) is { } mr)
                                try { if (ModMeshes.Load(mr, cooked, out _) is { } lm) extra.Add((new PropRig.Prop(mr, x.Bone) { Slots = x.Slots, OnDemand = true, Class = x.Class }, lm)); }
                                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
                        break;
                    }
                // The contact time: the power's AnimationContactTimePercent (else 0.4) of the animation.
                float pct = 0.4f;
                if (dbTask.Status == TaskStatus.RanToCompletion && dbTask.Result is { } db)
                {
                    var byClass = Fx.PowerIndex.PrototypesByClass(db);
                    if (refs.SelectMany(r => byClass.TryGetValue(r.Class, out var l) ? l : []).FirstOrDefault() is string proto) pct = Fx.PowerEffects.ContactPercentOf(db, proto);
                }
                // The animated props' own animations of this name.
                var seqs = new Dictionary<string, AnimExportCli.Animation.BoneAnimation>(StringComparer.OrdinalIgnoreCase);
                foreach (var (cls, ar) in motionRefs) if (!seqs.ContainsKey(cls) && ModAnimations.Load(ar) is { } ba) seqs[cls] = ba;
                return (Rules: rules, Contact: pct * seconds, Extra: extra, Seqs: seqs);
            })
            .ContinueWith(t =>
            {
                if (IsDisposed || req != propSwitchRequest || t.Status != TaskStatus.RanToCompletion) return;
                (propRules, propContact) = (t.Result.Rules, t.Result.Contact);
                propSeqs = t.Result.Seqs;
                bool rebuild = powerProps.Count > 0 || t.Result.Extra.Count > 0;
                powerProps = t.Result.Extra;
                if (rebuild) RebuildRig();
                else { rig.SetRules(propRules, propContact, playSeconds); rig.SetMotions(propSeqs); ShowPose(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    int ThumbAt(Point p)
    {
        foreach (var (r, i) in thumbRects) if (r.Contains(p) && strip.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (mod == null) return;
        if (powerLeft.Contains(e.Location)) { powerScroll = Math.Max(0, powerScroll - (ThumbSize + (int)(6 * S)) * 3); Invalidate(); return; }
        if (powerRight.Contains(e.Location)) { powerScroll += (ThumbSize + (int)(6 * S)) * 3; Invalidate(); return; }
        if (e.Button == MouseButtons.Left && powerRects.FirstOrDefault(x => x.Rect.Contains(e.Location)) is { Rect.Width: > 0 } hit) { PowerClicked(hit.Index); return; }
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
                if (!Reveal3D()) LoadMesh();
                Picked?.Invoke(mod, CurrentMeshKey());
                return;
            }
            int i = ti - Offset;
            if (!show3D && i == index && mod.LocalPreview == items[i].Key) return;
            show3D = false; index = i;
            Hide3D();
            LoadBig();
            Picked?.Invoke(mod, items[i].Key);
        }
        else if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip { Font = Ui.Regular(9.5f) };
            var m = mod;
            menu.Items.Add("Custom Image", null, (_, _) => CustomRequested?.Invoke(m)).ToolTipText = "Show a picture of your own for this mod (a .PNG, .JPG or .DDS; kept on this PC, and Export can put it into the mod).";
            if (mod.LocalPreview != null) menu.Items.Add(mod.Manifest.PreviewImage != null ? "Use the Mod's Choice" : "Choose Automatically Again", null, (_, _) => Picked?.Invoke(m, null));
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            Ui.ShowAt(menu, PointToScreen(e.Location));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!powerLeft.IsEmpty && powerRects.Count > 0 && e.Y >= powerLeft.Top && e.Y <= powerLeft.Bottom) { powerScroll = Math.Max(0, powerScroll - Math.Sign(e.Delta) * (ThumbSize + (int)(6 * S))); Invalidate(); return; }
        if (strip.IsEmpty || MaxScroll == 0) return;
        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * (ThumbSize + (int)(6 * S)), 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int pk = powerRects.FirstOrDefault(x => x.Rect.Contains(e.Location)) is { Rect.Width: > 0 } ph ? ph.Index : -1;
        if (pk >= heroPowers.Count) pk = -1;   // 0.37.49 crash: areas from the last paint while the list reloads
        if (pk >= 0 || hoverPower >= 0)
        {
            Cursor = pk >= 0 || powerLeft.Contains(e.Location) || powerRight.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (pk != hoverPower)
            {
                hoverPower = pk; Invalidate();
                if (pk >= 0)
                {
                    var pw = heroPowers[pk];
                    tips.Show($"{pw.Name}\n{string.Join(", ", pw.Animations)}\n{(powerFilter == pw.Prototype ? "Click to show all animations again." : "Click to play it with its effects (the animation list shows only this power's).")}", this, e.X + (int)(14 * S), e.Y + (int)(20 * S), 8000);
                }
                else tips.Hide(this);
            }
            if (pk >= 0) return;
        }
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

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverThumb = -1; hoverPower = -1; tips.Hide(this); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { SaveAnim(); playTimer.Dispose(); image?.Dispose(); foreach (var t in thumbs.Values) t?.Dispose(); tips.Dispose(); viewer?.Dispose(); animBox?.Dispose(); playBtn?.Dispose(); loopBtn?.Dispose(); restBtn?.Dispose(); lightSlider?.Dispose(); lensSlider?.Dispose(); frameSlider?.Dispose(); specBtn?.Dispose(); reflBtn?.Dispose(); glowBtn?.Dispose(); }
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
    // A tooltip still holding a disposed table asks for its handle when the window deactivates (ToolTip.HideAllToolTips),
    // which threw ObjectDisposedException; a disposed table now has no handle to give instead.
    protected override void CreateHandle() { if (IsDisposed || Disposing) return; base.CreateHandle(); }

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
        // Disposed while still in the window (a control removes itself as it goes): a table's own cell tooltip listens to
        // the window's Deactivate, and one taken out of the window first was left behind there; switching windows then
        // reached the disposed table ("Cannot access a disposed object: GradientGrid", a user, 2026-09-30).
        foreach (var c in old) c.Dispose();
        strip.Controls.Clear(); body.Controls.Clear(); tabs.Clear(); SelectedIndex = -1;
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
    /// <summary>The single costume the mod is for ("Age of Ultron Movie"), or null (then no costume control).</summary>
    public string? CostumeTitle { get; set; }
    /// <summary>The costume control clicked, at a screen point (opens the Move to Another Costume menu).</summary>
    public event Action<Point>? CostumeClicked;
    Rectangle pill, tagArea, costumeRect;
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
        // Level with the name, so the Costume pill below keeps a gap (Kurt: the badges hit it).
        pill = new Rectangle(right - ps.Width - (int)(8 * S), (int)(8 * S), ps.Width + (int)(8 * S), (int)(21 * S));
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
        Ui.DrawModName(g, m, title, new Rectangle(x, (int)(8 * S), Math.Min(ts.Width, maxName), ts.Height), Ui.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrEmpty(m.Manifest.Version))
            TextRenderer.DrawText(g, m.Manifest.Version, version, new Point(x + Math.Min(ts.Width, maxName) + (int)(4 * S), (int)(8 * S) + ts.Height - TextRenderer.MeasureText(m.Manifest.Version, version).Height - (int)(2 * S)), Ui.Subtle, TextFormatFlags.NoPrefix);
        string line = (string.IsNullOrEmpty(m.Manifest.Author) ? "" : $"by {m.Manifest.Author}   ") + Ui.CountText(m) + (m.Enabled ? "" : "   ·   Turned Off");
        var byPos = new Point(x, (int)(8 * S) + ts.Height + (int)(2 * S));
        TextRenderer.DrawText(g, line, by, byPos, Ui.Subtle, TextFormatFlags.NoPrefix);

        // Right of the by-line: the mod's costume, a drop-down (Kurt: show the source costume, and choices to move it).
        costumeRect = Rectangle.Empty;
        int tagsRight = right;
        if (!string.IsNullOrEmpty(CostumeTitle))
        {
            string ct = "Costume: " + CostumeTitle + "  ▾";
            var cs = TextRenderer.MeasureText(g, ct, pillFont, Size.Empty, TextFormatFlags.NoPrefix);
            var bs = TextRenderer.MeasureText(g, "X", by);
            costumeRect = new Rectangle(right - cs.Width - (int)(12 * S), byPos.Y + bs.Height / 2 - (int)(11 * S), cs.Width + (int)(12 * S), (int)(22 * S));
            if (costumeRect.Top < pill.Bottom + (int)(5 * S)) costumeRect.Y = pill.Bottom + (int)(5 * S);
            using (var path = Ui.Round(costumeRect, 4 * S))
            {
                using var fill = new SolidBrush(Color.FromArgb(40, Ui.Accent)); g.FillPath(fill, path);
                using var pen = new Pen(Ui.Accent); g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, ct, pillFont, costumeRect, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            tagsRight = costumeRect.Left - (int)(8 * S);
        }

        // The mod's tags, then "+ Tag" (both open the tags menu).
        var ls = TextRenderer.MeasureText(g, line, by, Size.Empty, TextFormatFlags.NoPrefix);
        int cx = x + ls.Width + (int)(12 * S), mid = byPos.Y + ls.Height / 2;
        tagArea = Rectangle.Empty;
        for (int i = 0; i <= m.Tags.Count; i++)
        {
            bool add = i == m.Tags.Count;
            string tag = add ? "+ Tag" : m.Tags[i];
            if (cx + Ui.ChipWidth(g, tag, chipFont, S) > tagsRight) break;
            var r = Ui.DrawChip(g, tag, chipFont, cx, mid, S, outline: add, soft: !add && m.KindOf(tag) == Mod.TagKind.Auto, mod: m);
            tagArea = tagArea.IsEmpty ? r : Rectangle.Union(tagArea, r);
            cx = r.Right + (int)(4 * S);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hand = (pill.Contains(e.Location) || tagArea.Contains(e.Location) || conflictRect.Contains(e.Location) || costumeRect.Contains(e.Location)) && Mod != null;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
        string? tip = costumeRect.Contains(e.Location) ? "The costume this mod is for. Click to move it to another costume of the same hero (as a new mod; this one isn't changed)." : null;
        if (tip != shownTip) { shownTip = tip; if (tip == null) Ui.Tips.Hide(this); else Ui.Tips.Show(tip, this, e.X + 12, e.Y + 18, 8000); }
    }
    string? shownTip;
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (shownTip != null) { shownTip = null; Ui.Tips.Hide(this); } }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Mod == null) return;
        if (pill.Contains(e.Location)) PillClicked?.Invoke();
        else if (conflictRect.Contains(e.Location)) ConflictClicked?.Invoke();
        else if (tagArea.Contains(e.Location)) TagsClicked?.Invoke(PointToScreen(new Point(e.X, tagArea.Bottom)));
        else if (costumeRect.Contains(e.Location)) CostumeClicked?.Invoke(PointToScreen(new Point(costumeRect.Left, costumeRect.Bottom)));
    }
}

/// <summary>
/// The 3D view's light brightness (Kurt): a flat slider in the app's colours, 50–200 %, labelled "Light", with the value.
/// Drag or use the mouse wheel; double-click goes back to Home (the mod's own level, else 100 %). ValueChanged fires while it moves, Committed once it's set.
/// </summary>
sealed class LightSlider : Control
{
    float value = 1;
    bool dragging;
    public event Action? ValueChanged, Committed;
    /// <summary>What a double-click goes back to (default 1).</summary>
    public Func<float>? Home { get; set; }
    /// <summary>Range and step (the Light slider: 0.5–2 in 0.05); the icon creator's frame slider uses its own.</summary>
    public float Min { get; set; } = 0.5f;
    public float Max { get; set; } = 2f;
    public float Step { get; set; } = 0.05f;
    /// <summary>The label on the left and how the value is written on the right.</summary>
    public string Label { get; set; } = "Light";
    public Func<float, string> Format { get; set; } = v => $"{v * 100:0} %";
    readonly Font labelFont = Ui.Regular(8.5f);
    /// <summary>A tick at this value (the Light slider's 100 %), or null.</summary>
    public float? Mark { get; set; } = 1f;

    public LightSlider()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    // Keyboard (Kurt: click once, then step with the arrow keys, Shift for faster): ← → ↑ ↓ one step, Shift five,
    // Home / End the ends. The steps while a key is held are one change; Committed fires when it's let go.
    float KeyStep => Step > 0 ? Step : (Max - Min) / 100f;
    bool keyChanged;
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        float step = KeyStep * (e.Shift ? 5 : 1), was = value;
        switch (e.KeyCode)
        {
            case Keys.Left: case Keys.Down: Value = value - step; break;
            case Keys.Right: case Keys.Up: Value = value + step; break;
            case Keys.Home: Value = Min; break;
            case Keys.End: Value = Max; break;
            default: return;
        }
        e.Handled = true;
        if (Math.Abs(was - value) > 1e-6) keyChanged = true;
    }
    /// <summary>Tests: a key pressed and let go, as the keyboard does.</summary>
    internal void TestKey(Keys key) { OnKeyDown(new KeyEventArgs(key)); OnKeyUp(new KeyEventArgs(key)); }
    protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (keyChanged) { keyChanged = false; Committed?.Invoke(); } }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (keyChanged) { keyChanged = false; Committed?.Invoke(); } Invalidate(); }

    public float Value
    {
        get => value;
        set { float v = Math.Clamp(Step > 0 ? MathF.Round(value / Step) * Step : value, Min, Max); if (Math.Abs(v - this.value) < 1e-4) return; this.value = v; Invalidate(); ValueChanged?.Invoke(); }
    }

    float S => DeviceDpi / 96f;
    Rectangle Track
    {
        get
        {
            // Room for the label (measured: "Light", "Frame", "Original") and the value on the right, measured at the widest
            // it gets (Kurt: the frame count "122 / 122" was cut off in the fixed 52 px).
            int left = Math.Max((int)(44 * S), TextRenderer.MeasureText(Label, labelFont).Width + (int)(8 * S));
            int widest = Math.Max(TextRenderer.MeasureText(Format(Max), labelFont).Width, TextRenderer.MeasureText(Format(value), labelFont).Width);
            int right = Math.Max((int)(52 * S), widest + (int)(10 * S));
            return new Rectangle(left, Height / 2 - (int)(2 * S), Math.Max(10, Width - left - right), Math.Max(3, (int)(4 * S)));
        }
    }

    float ValueAt(int x) { var t = Track; return Min + (Max - Min) * Math.Clamp((x - t.X) / (float)t.Width, 0, 1); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var t = Track;
        float k = Max > Min ? (value - Min) / (Max - Min) : 0;
        int tx = t.X + (int)(k * t.Width);
        using var font = Ui.Regular(8.5f);
        TextRenderer.DrawText(g, Label, font, new Rectangle(0, 0, t.X - (int)(8 * S), Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        using (var back = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) using (var p = Ui.Round(t, t.Height / 2f)) g.FillPath(back, p);
        var done = new Rectangle(t.X, t.Y, Math.Max(1, tx - t.X), t.Height);
        using (var acc = new SolidBrush(Ui.Accent)) using (var p = Ui.Round(done, t.Height / 2f)) g.FillPath(acc, p);
        if (Mark is float mark && Max > Min)
        {
            int one = t.X + (int)((mark - Min) / (Max - Min) * t.Width);   // e.g. the 100 % mark
            using var pen = new Pen(Color.FromArgb(140, 255, 255, 255), Math.Max(1f, S));
            g.DrawLine(pen, one, t.Y - (int)(3 * S), one, t.Bottom + (int)(3 * S));
        }
        float r = 6 * S;
        using (var thumb = new SolidBrush(Ui.Text)) g.FillEllipse(thumb, tx - r, Height / 2f - r, 2 * r, 2 * r);
        if (Focused) using (var ring = new Pen(Ui.Accent, Math.Max(1.5f, 2 * S))) g.DrawEllipse(ring, tx - r - 2 * S, Height / 2f - r - 2 * S, 2 * r + 4 * S, 2 * r + 4 * S);   // selected: the arrow keys move it
        TextRenderer.DrawText(g, Enabled ? Format(value) : "–", font, new Rectangle(t.Right + (int)(6 * S), 0, Width - t.Right - (int)(6 * S), Height), Ui.Subtle, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();   // then the arrow keys step it
        // A press on the thumb only selects it (no jump); elsewhere on the track it goes there, as before.
        var t = Track;
        int tx = t.X + (int)((Max > Min ? (value - Min) / (Max - Min) : 0) * t.Width);
        dragging = true; Capture = true;
        if (Math.Abs(e.X - tx) > 7 * S) Value = ValueAt(e.X);
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Value = ValueAt(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (!dragging) return; dragging = false; Capture = false; Committed?.Invoke(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Value += e.Delta > 0 ? Math.Max(Step, (Max - Min) / 40) : -Math.Max(Step, (Max - Min) / 40); Committed?.Invoke(); }
    protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); Value = Home?.Invoke() ?? 1; Committed?.Invoke(); }
}
