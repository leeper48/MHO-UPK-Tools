namespace MhoExtendedModManager;

/// <summary>One of a hero's five gear slots (Entity\Items\Armor\Blueprints\&lt;Hero&gt;\*.defaults): its name string and icon
/// textures. Every gear item of that slot (each rarity) takes them from this blueprint. Or one of the hero's unique items
/// (<see cref="Unique"/>: Entity\Items\Armor\UniquePrototypes\Avatars\&lt;Hero&gt;\UniqueNNN), with its own name and small icon;
/// its large icon is often the slot's (<see cref="HdFromSlot"/>), and then only its small one is its own.</summary>
sealed record GearSlot(string Hero, int Number, string Blueprint, ulong NameId, string? Package, string? Texture, string? HdPackage, string? HdTexture,
    string? Unique = null, string? HdFromSlot = null, ulong TypeId = 0)
{
    /// <summary>The tooltip's second line, before " – &lt;hero&gt;" (Kurt, 2026-11-17: "Son of Ultron's Belt – Sentry"): a slot's
    /// ItemCategory ("Light Belt"), a unique's ItemSubcategory ("Vision" in "Unique – Vision"; its category "Unique" is every
    /// unique's). <see cref="TypeId"/> is that string (0 = none).</summary>
    public string TypeField => Unique != null ? "ItemSubcategory" : "ItemCategory";
    /// <summary>"Storm · Slot 1", "Storm · Unique042 (Slot 1)".</summary>
    public string Label => Unique != null ? $"{GearData.HeroName(Hero)} · {Unique} (Slot {Number})" : $"{GearData.HeroName(Hero)} · Slot {Number}";
}

/// <summary>
/// The heroes' gear (Kurt, 2026-11-17: change gear names and icons). The game data says which string and which icon textures
/// each slot uses; that can't change (Calligraphy.sip stays as it is), but the string's text and the textures' pictures can,
/// as any string and icon mod does: the name in eng.all, the icon in ICO__MarvelUIIcons_SF (40 × 40) and its large copy in
/// ICO__MarvelUIIcons_HD_SF (128 × 128), both DXT5 on transparency. Some slots share a name string or an icon with other
/// heroes' (a "Boots" string by several, Spider-Woman on Spider-Man's icons): changing it changes those too.
/// </summary>
static class GearData
{
    const string Root = @"Entity\Items\Armor\Blueprints\";
    const string UniqueRoot = @"Entity\Items\Armor\UniquePrototypes\Avatars\";
    const string HdPackage = "ICO__MarvelUIIcons_HD_SF.upk";

    static Dictionary<string, List<GearSlot>>? cached;
    static Fx.GameData? cachedDb;

    /// <summary>Every hero's slots (by gear folder: Storm, Spiderman, X23 …), slot 1 to 5. <paramref name="hdNames"/>: the
    /// textures the large icon package holds (a slot whose game data names no large icon still has one there by the same name).</summary>
    public static Dictionary<string, List<GearSlot>> All(Fx.GameData db, ICollection<string>? hdNames)
    {
        if (cached != null && ReferenceEquals(cachedDb, db)) return cached;
        var all = new Dictionary<string, List<GearSlot>>(StringComparer.OrdinalIgnoreCase);
        // the slots' blueprints, then the heroes' unique items (not AnyHero's: those aren't a character's)
        var entries = db.Prototypes.Values.Where(e => e.Path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && e.Path.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase))
            .Concat(db.Prototypes.Values.Where(e => e.Path.StartsWith(UniqueRoot, StringComparison.OrdinalIgnoreCase) && e.Path.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)
                && !e.Path.StartsWith(UniqueRoot + "AnyHero", StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(e => e.Path);
        foreach (var e in entries)
        {
            bool unique = e.Path.StartsWith(UniqueRoot, StringComparison.OrdinalIgnoreCase);
            string rest = e.Path[(unique ? UniqueRoot : Root).Length..];
            int slash = rest.IndexOf('\\');
            if (slash <= 0) continue;
            string hero = rest[..slash];
            try
            {
                var sort = Fx.PowerList.Field(db, e.Id, "SortSubCategory", 'P', true);
                string sortName = sort.Found ? db.Name(sort.Raw) : "";
                // GearSlot01 … GearSlot05 (the inventory's own order)
                int at = sortName.IndexOf("GearSlot", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || !int.TryParse(sortName.AsSpan(at + 8, 2), out int number)) continue;
                ulong name = Fx.PowerList.Field(db, e.Id, "DisplayName", 'S', true).Raw;
                ulong type = Fx.PowerList.Field(db, e.Id, unique ? "ItemSubcategory" : "ItemCategory", 'S', true).Raw;
                (string, string)? Icon(string field) =>
                    Fx.PowerList.Field(db, e.Id, field, 'A', true) is { Found: true, Raw: var raw and not 0 } && db.Assets.TryGetValue(raw, out var a) ? Costume.IconTexture(a.Asset.Name) : null;
                var icon = Icon("IconPath");
                var hd = Icon("IconPathHiRes");
                if (hd == null && icon is { } i && hdNames != null && hdNames.Contains(i.Item2)) hd = (HdPackage, i.Item2);
                if (!all.TryGetValue(hero, out var list)) all[hero] = list = [];
                // a unique's large icon is usually the slot's own armor_ one (inherited from the slot): only its small one is its own
                string? hdFromSlot = null;
                if (unique && hd is { } h && h.Item2.StartsWith("armor_", StringComparison.OrdinalIgnoreCase)) { hdFromSlot = h.Item2; hd = null; }
                list.Add(new GearSlot(hero, number, e.Path, name, icon?.Item1, icon?.Item2, hd?.Item1, hd?.Item2,
                    unique ? Path.GetFileNameWithoutExtension(e.Path) : null, hdFromSlot, type));
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { }
        }
        foreach (var k in all.Keys.ToList()) all[k] = [.. all[k].OrderBy(s => s.Unique != null).ThenBy(s => s.Number).ThenBy(s => s.Blueprint)];
        cachedDb = db;
        return cached = all;
    }

    /// <summary>One of a slot's item versions (Entity\Items\Armor\Prototypes\&lt;Hero&gt;\…\Belt10: 15 per slot, by level): the
    /// tooltip's second line it shows (its own ItemCategory, else the slot's: "Son of Ultron's Belt" on Vision's Belt10).</summary>
    public sealed record Tier(string Hero, int Slot, string Item, ulong TypeId);

    const string TierRoot = @"Entity\Items\Armor\Prototypes\";
    static Dictionary<(string Hero, int Slot), List<Tier>>? tiers;
    static Fx.GameData? tiersDb;

    /// <summary>Every slot's item versions, by hero folder and slot number (GearSlot01 … 05), in name order.</summary>
    public static Dictionary<(string Hero, int Slot), List<Tier>> Tiers(Fx.GameData db)
    {
        if (tiers != null && ReferenceEquals(tiersDb, db)) return tiers;
        var res = new Dictionary<(string, int), List<Tier>>();
        foreach (var e in db.Prototypes.Values.Where(e => e.Path.StartsWith(TierRoot, StringComparison.OrdinalIgnoreCase) && e.Path.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)).DistinctBy(e => e.Path))
        {
            string rest = e.Path[TierRoot.Length..];
            int slash = rest.IndexOf('\\');
            if (slash <= 0) continue;
            string hero = rest[..slash];
            try
            {
                var sort = Fx.PowerList.Field(db, e.Id, "SortSubCategory", 'P', true);
                string sortName = sort.Found ? db.Name(sort.Raw) : "";
                int at = sortName.IndexOf("GearSlot", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || !int.TryParse(sortName.AsSpan(at + 8, 2), out int number)) continue;
                ulong type = Fx.PowerList.Field(db, e.Id, "ItemCategory", 'S', true).Raw;
                var key = (hero.ToLowerInvariant(), number);
                if (!res.TryGetValue(key, out var list)) res[key] = list = [];
                list.Add(new Tier(hero, number, Path.GetFileNameWithoutExtension(e.Path), type));
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { }
        }
        foreach (var k in res.Keys.ToList()) res[k] = [.. res[k].OrderBy(t => t.Item, StringComparer.OrdinalIgnoreCase)];
        tiersDb = db;
        return tiers = res;
    }

    /// <summary>A slot's item versions grouped by the second line they show: (string, "Belt01–04" style label, item names).</summary>
    public static List<(ulong TypeId, string Items)> TierGroups(Fx.GameData db, GearSlot s)
    {
        if (s.Unique != null || !Tiers(db).TryGetValue((s.Hero.ToLowerInvariant(), s.Number), out var list)) return [];
        return [.. list.Where(t => t.TypeId != 0).GroupBy(t => t.TypeId).Select(g => (g.Key, Compact(g.Select(t => t.Item).ToList())))
            .OrderBy(g => list.FindIndex(t => t.TypeId == g.Key))];
    }

    /// <summary>"Belt01, Belt02, Belt03, Belt05" → "Belt01–03, Belt05".</summary>
    static string Compact(List<string> items)
    {
        var parts = new List<string>();
        int i = 0;
        static (string Stem, int N)? Split(string x) { var m = System.Text.RegularExpressions.Regex.Match(x, @"^(.*?)(\d+)$"); return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value)) : null; }
        while (i < items.Count)
        {
            int j = i;
            while (j + 1 < items.Count && Split(items[j]) is { } a && Split(items[j + 1]) is { } b && a.Stem == b.Stem && b.N == a.N + 1) j++;
            parts.Add(j > i ? $"{items[i]}–{items[j][Split(items[j])!.Value.Stem.Length..]}" : items[i]);
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>Who else shows a second-line string: other heroes' slots and item versions, as "Hero (Item)" (empty: no one).</summary>
    public static List<string> TypeUsers(Fx.GameData db, ulong id, string hero, int slot)
    {
        if (id == 0) return [];
        return [.. Tiers(db).Values.SelectMany(l => l).Where(t => t.TypeId == id && !(t.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) && t.Slot == slot))
            .Select(t => t.Hero.Equals(hero, StringComparison.OrdinalIgnoreCase) ? $"slot {t.Slot}" : HeroName(t.Hero)).Distinct()];
    }

    /// <summary>A gear folder's hero as players know it (Spiderman → Spider-Man), else the folder's own name.</summary>
    public static string HeroName(string folder) => AutoTags.DisplayName(folder) ?? folder;

    /// <summary>The other slots sharing this one's name string.</summary>
    public static List<GearSlot> SharingName(Dictionary<string, List<GearSlot>> all, GearSlot s) =>
        s.NameId == 0 ? [] : [.. all.Values.SelectMany(l => l).Where(o => o != s && o.NameId == s.NameId)];

    /// <summary>The other slots sharing this one's type-line string (ItemCategory / ItemSubcategory).</summary>
    public static List<GearSlot> SharingType(Dictionary<string, List<GearSlot>> all, GearSlot s) =>
        s.TypeId == 0 ? [] : [.. all.Values.SelectMany(l => l).Where(o => o != s && o.TypeId == s.TypeId)];

    /// <summary>The other slots sharing this one's icon (either size).</summary>
    public static List<GearSlot> SharingIcon(Dictionary<string, List<GearSlot>> all, GearSlot s) =>
        [.. all.Values.SelectMany(l => l).Where(o => o != s && (Same(o.Texture, s.Texture) || Same(o.HdTexture, s.HdTexture)))];

    static bool Same(string? a, string? b) => a != null && b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The icon textures to write for a slot: the small one and the large one (where there is one).</summary>
    public static List<(string Package, string Texture)> Textures(GearSlot s)
    {
        var list = new List<(string, string)>();
        if (s.Package != null && s.Texture != null) list.Add((s.Package, s.Texture));
        if (s.HdPackage != null && s.HdTexture != null) list.Add((s.HdPackage, s.HdTexture));
        return list;
    }

    /// <summary>The gear folder of a mod's hero (its UC__MarvelPlayer_&lt;Hero&gt; packages), or null.</summary>
    public static string? HeroOf(IEnumerable<string> packages, IEnumerable<string> folders)
    {
        var list = folders.ToList();
        foreach (string p in packages)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(p), @"^UC__MarvelPlayer_([A-Za-z0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string token = m.Groups[1].Value;
            string? hit = list.FirstOrDefault(f => f.Equals(token, StringComparison.OrdinalIgnoreCase))
                ?? (AutoTags.DisplayName(token) is string dn ? list.FirstOrDefault(f => HeroName(f).Equals(dn, StringComparison.OrdinalIgnoreCase)) : null);
            if (hit != null) return hit;
        }
        return null;
    }
}
