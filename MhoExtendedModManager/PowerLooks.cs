using System.Runtime.CompilerServices;

namespace MhoExtendedModManager;

/// <summary>
/// How a mod shows a hero's powers (Kurt, 2026-11-17: a Borrow Power's icon switched on the Powers tab, but the Mods and Model
/// tabs' power buttons kept the game's): the power's name (the mod's English string for its DisplayName), its icon (the mod's
/// replacement of its icon texture), the power whose look it borrows, and the game's own name. Shared by the Powers tab and
/// every 3D preview's power buttons, from an open draft or an installed mod. Reads only.
/// </summary>
sealed class PowerLooks
{
    readonly Func<IEnumerable<StringReplacement>> strings;
    /// <summary>(package file, texture) → the mod's image for it, or null.</summary>
    readonly Func<string, string, string?> iconSource;
    readonly Func<IEnumerable<PowerBorrowEntry>> borrows;
    readonly StockCatalog? catalog;

    PowerLooks(Func<IEnumerable<StringReplacement>> strings, Func<string, string, string?> iconSource, Func<IEnumerable<PowerBorrowEntry>> borrows, StockCatalog? catalog)
    { this.strings = strings; this.iconSource = iconSource; this.borrows = borrows; this.catalog = catalog; }

    /// <summary>An open draft (read live: a change shows at the next look).</summary>
    public static PowerLooks Of(Func<ModDraft> draft, StockCatalog? catalog) => new(() => draft().Strings, (pkg, tex) =>
    {
        var d = draft();
        int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
        return view >= 0 && view < d.Textures.Length
            ? d.Textures[view].LastOrDefault(t => t.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase)).Source
            : d.Extra.LastOrDefault(x => x.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase) && x.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase)).Source;
    }, () => draft().PowerBorrows, catalog);

    /// <summary>An installed mod (its manifest and strings as saved).</summary>
    public static PowerLooks Of(Mod m, StockCatalog? catalog) => new(() => m.Strings, (pkg, tex) =>
    {
        int view = Array.FindIndex(Applier.IconPackages, x => x.File.Equals(pkg, StringComparison.OrdinalIgnoreCase));
        string? file = view >= 0
            ? Applier.IconPackages[view].List(m.Manifest).LastOrDefault(r => r.TextureName.Equals(tex, StringComparison.OrdinalIgnoreCase))?.DdsFileName
            : m.Manifest.Extra.LastOrDefault(r => r.Package.Equals(pkg, StringComparison.OrdinalIgnoreCase) && r.TextureName.Equals(tex, StringComparison.OrdinalIgnoreCase))?.DdsFileName;
        return file == null ? null : Path.Combine(m.Folder, file);
    }, () => m.Manifest.PowerBorrows ?? [], catalog);

    public PowerBorrowEntry? Borrow(string proto) => borrows().FirstOrDefault(e => e.Power.Equals(proto, StringComparison.OrdinalIgnoreCase));

    /// <summary>The power's DisplayName string ID, or null (a part, a power without a name).</summary>
    static ulong? NameId(Fx.PowerList.Power p, Fx.GameData? db) =>
        p.Part == null && db?.Find(p.Prototype) is { } pe && Fx.PowerList.Field(db, pe.Id, "DisplayName", 'S', true) is { Found: true, Raw: var id and not 0 } ? id : null;

    /// <summary>
    /// The power's name as the mod leaves it: the mod's English string for its DisplayName, else the game's original, else the
    /// list's (which reads the game folder's strings, where an applied mod's earlier rename is).
    /// </summary>
    public string Name(Fx.PowerList.Power p, Fx.GameData? db)
    {
        if (NameId(p, db) is not ulong id) return p.Name;
        if (strings().LastOrDefault(x => x.Language == "eng" && x.Id == id) is { Text.Length: > 0 } s) return s.Text;
        return Stock(id) ?? p.Name;
    }

    /// <summary>The game's own name for the power (its original English string), else the list's.</summary>
    public string Original(Fx.PowerList.Power p, Fx.GameData? db) => NameId(p, db) is ulong id && Stock(id) is string t ? t : p.Name;

    string? Stock(ulong id)
    {
        if (catalog == null) return null;
        var map = stockEnglish.GetValue(catalog, c => c.Strings("eng").GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Text));
        return map.TryGetValue(id, out var t) && t.Length > 0 ? t : null;
    }
    static readonly ConditionalWeakTable<StockCatalog, Dictionary<ulong, string>> stockEnglish = new();

    /// <summary>The mod's image for a power icon ("MarvelUIIcons.power_thor_x", or without the package), or null.</summary>
    public string? IconFile(string iconPath)
    {
        string full = iconPath.Contains('.') ? iconPath : "MarvelUIIcons." + iconPath;
        int dot = full.IndexOf('.');
        string? src = iconSource($"ICO__{full[..dot]}_SF.upk", full[(dot + 1)..]);
        return src != null && File.Exists(src) ? src : null;
    }

    /// <summary>The mod's icon for a power, scaled to <paramref name="w"/>×<paramref name="h"/> (cached by file and size), or null
    /// when the mod doesn't replace it.</summary>
    public Bitmap? Icon(string iconPath, int w, int h)
    {
        if (w <= 0 || h <= 0 || IconFile(iconPath) is not string src) return null;
        var key = (src + "|" + Gui.Ui.FileStamp(src), w, h);
        lock (icons) if (icons.TryGetValue(key, out var hit)) return hit;
        Bitmap? bmp = null;
        try
        {
            if (Gui.Ui.DdsThumb(src, Math.Max(w, h) * 4) is { } thumb)
                using (thumb)
                {
                    bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using var g = Graphics.FromImage(bmp);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(thumb, new Rectangle(0, 0, w, h));
                }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { bmp = null; }
        lock (icons) icons[key] = bmp;
        return bmp;
    }
    static readonly Dictionary<(string, int, int), Bitmap?> icons = [];

    /// <summary>A power's name lines for a tooltip: its name, whose look it borrows, and the game's own name when either differs
    /// (Kurt, 2026-11-17: "a third line, the original power name").</summary>
    public List<string> TipLines(Fx.PowerList.Power p, Fx.GameData? db)
    {
        string name = Name(p, db), original = Original(p, db);
        var lines = new List<string> { name };
        var b = Borrow(p.Prototype);
        if (b != null) lines.Add("Looks like " + b.DonorName);
        if (b != null || !name.Equals(original, StringComparison.Ordinal)) lines.Add("Original: " + original);
        return lines;
    }
}

/// <summary>A power button's right-click menu (Kurt, 2026-11-17).</summary>
enum PowerMenuAction { Rename, ReplaceIcon, MakeIcon, ModifyAnimation, Borrow, ResetBorrow, Color, Dark, GamesColors, Parts }

/// <summary>A power's right-click menu, the same on every tab (Kurt, 2026-11-17: the shortcuts first, then the Powers tab's own
/// actions for the power).</summary>
static class PowerMenuItems
{
    public static void Fill(ContextMenuStrip menu, Fx.PowerList.Power pw, bool borrowed, Action<PowerMenuAction> pick)
    {
        foreach (var item in Items(pw, borrowed, pick)) menu.Items.Add(item);
    }

    public static List<ToolStripItem> Items(Fx.PowerList.Power pw, bool borrowed, Action<PowerMenuAction> pick)
    {
        var menu = new List<ToolStripItem>();
        void Add(string text, PowerMenuAction a, string tip) => menu.Add(new ToolStripMenuItem(text, null, (_, _) => pick(a)) { ToolTipText = tip });
        Add("Rename Power", PowerMenuAction.Rename, "Change the power's name in game (English): put in the Strings tab.");
        Add("Replace Icon", PowerMenuAction.ReplaceIcon, "Use your own image (PNG, JPG, BMP or .DDS) as the power's icon, converted to its size.");
        Add("Make Icon From 3D View", PowerMenuAction.MakeIcon, "Take a snapshot of the 3D view playing this power, on a background color of your choice, as the power's icon.");
        if (pw.Animations.Count > 0) Add("Change Its Animations", PowerMenuAction.ModifyAnimation, "Open the Animations tab on this power's animations, to swap them for another character's.");
        if (pw.Animations.Count > 0) Add("Borrow Power", PowerMenuAction.Borrow, "Give this power another power's look (its animation, effects, sounds and props), on the Powers tab.");
        // (Kurt, 2026-11-17: Borrow Power only; Reset, Color, Dark, Game's Colors and Parts dropped from the menu)
        return menu;
    }
}
