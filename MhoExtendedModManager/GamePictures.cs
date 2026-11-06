namespace MhoExtendedModManager;

/// <summary>
/// The pictures the game itself names for a costume, team-up or hero package (Kurt, 2026-10-01: the MHO MFF Importer's
/// game-data route proved better than guessing from package names). Read once per session from Calligraphy.sip, read only:
/// - costumes (Costume.All): CostumeUnrealClass → PortraitIconPath, StoreIconPath, IconPath (the default costume wins when
///   two share a class: Thor Modern and VisualUpdate both use ModernVU);
/// - team-ups (Entity\Characters\TeamUps): UnrealClass → PortraitPath, UnlockDialogImage (store), IconPath;
/// - heroes (Entity\Characters\Avatars): UnrealClass → PortraitPath, UnlockDialogImage, IconPath, also for the hero's base
///   class MarvelPlayer_&lt;Hero&gt; (an avatar can name its default costume's class: Ms. Marvel's names
///   MarvelPlayer_MsMarvel_CaptainMarvelANAD).
/// - vanity pets (Kurt, 2026-11-06: pet mods showed a letter on their cards): the pet item (Entity\Items\Pets) names its
///   IconPath (Inventory_PetSpiderHam) and StoreIconPath; its power (ActionsTriggeredOnItemEvent … Power) summons the pet
///   (SummonEntity), whose UnrealClass is the package's class (MarvelVanityPet_Spiderham). Item and pet names often
///   differ (Pet032Bamf, IceGolem → Inventory_PetIceHolem), so the chain is followed, not the names.
/// The game names some textures that aren't there (herohor_punisher_original, herohor_warmachine_ironman3movie), and some
/// packages have no prototype at all (Rogue's UltimateForm_*, Spiderman_Original …): callers keep their name-based fallback.
/// </summary>
static class GamePictures
{
    /// <summary>Asset names ("MarvelUIIcons.HeroHor_Thor_Classic"); null where the game names none.</summary>
    public sealed record Pics(string? Portrait, string? Store, string? Icon)
    {
        /// <summary>A pet item's name (string ID; the name the pet shows in the inventory), else 0.</summary>
        public ulong NameId { get; init; }
    }

    static readonly object gate = new();
    static Dictionary<string, Pics>? map;
    static string? mapRoot;

    /// <summary>The game's pictures for a package (UC__MarvelPlayer_…_SF, UC__MarvelTeamUp_…_SF), or null.</summary>
    public static Pics? For(string gameRoot, string packageFile)
    {
        string n = Path.GetFileNameWithoutExtension(packageFile);
        if (!n.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) || !n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Map(gameRoot);
        return m != null && m.TryGetValue(n[4..^3], out var p) ? p : null;
    }

    /// <summary>An asset name as (icon package file, texture name): "MarvelUIIcons_Store.Store_X" → (ICO__MarvelUIIcons_Store_SF.upk, store_x).</summary>
    public static (string Package, string Texture)? Split(string? asset)
    {
        if (string.IsNullOrEmpty(asset)) return null;
        int dot = asset.IndexOf('.');
        return dot <= 0 ? null : ($"ICO__{asset[..dot]}_SF.upk", asset[(dot + 1)..].ToLowerInvariant());
    }

    static Dictionary<string, Pics>? Map(string gameRoot)
    {
        lock (gate)
        {
            if (map != null && mapRoot == gameRoot) return map;
            try { map = Build(gameRoot); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException) { map = null; }
            mapRoot = gameRoot;
            return map;
        }
    }

    static Dictionary<string, Pics>? Build(string gameRoot)
    {
        string sip = Path.Combine(gameRoot, "Data", "Game", "Calligraphy.sip");
        if (!File.Exists(sip)) return null;
        var result = new Dictionary<string, Pics>(StringComparer.OrdinalIgnoreCase);
        // Costumes first; the default costume wins a class two costumes share.
        foreach (var c in (Costume.All(gameRoot) ?? []).OrderByDescending(c => c.IsDefault))
            if (!c.Prototype.Contains("zTesting", StringComparison.OrdinalIgnoreCase))
                result.TryAdd(c.Class, new Pics(c.Portrait, c.Store, c.Icon));

        var db = new Fx.GameData(Fx.SipArchive.Load(sip));
        foreach (var (id, e) in db.Prototypes)
        {
            string path = e.Path.Replace('/', '\\');
            bool teamUp = path.StartsWith("Entity\\Characters\\TeamUps\\", StringComparison.OrdinalIgnoreCase);
            bool avatar = path.StartsWith("Entity\\Characters\\Avatars\\", StringComparison.OrdinalIgnoreCase);
            if (!teamUp && !avatar || !path.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)) continue;
            string? cls = null, portrait = null, store = null, icon = null;
            try
            {
                foreach (var g in db.Prototype(id).Data.Groups)
                    foreach (var f in g.Simple)
                    {
                        if (f.Type != 'A' || !db.Assets.TryGetValue(f.Value.Raw, out var a)) continue;
                        switch (db.FieldName(g.Blueprint, f.Id))
                        {
                            case "UnrealClass": cls = a.Asset.Name; break;
                            case "PortraitPath": portrait = a.Asset.Name; break;
                            case "UnlockDialogImage": store = a.Asset.Name; break;
                            case "IconPath": icon = a.Asset.Name; break;
                        }
                    }
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
            if (cls == null || portrait == null && store == null) continue;
            var pics = new Pics(portrait, store, icon);
            result.TryAdd(cls, pics);
            // A hero's base package (UC__MarvelPlayer_<Hero>_SF) gets the hero's own pictures.
            var parts = cls.Split('_');
            if (avatar && parts.Length > 2 && parts[0].Equals("MarvelPlayer", StringComparison.OrdinalIgnoreCase)) result.TryAdd(parts[0] + "_" + parts[1], pics);
        }
        Pets(db, result);
        return result;
    }

    /// <summary>Every value of the fields named <paramref name="name"/> of type <paramref name="type"/> in a prototype's own
    /// data, nested structs and lists included.</summary>
    static IEnumerable<ulong> Values(Fx.GameData db, Fx.Calligraphy.Data d, string name, char type)
    {
        foreach (var g in d.Groups)
        {
            foreach (var f in g.Simple)
                if (f.Type == 'R') { foreach (var v in Values(db, f.Value.Struct!, name, type)) yield return v; }
                else if (f.Type == type && db.FieldName(g.Blueprint, f.Id) == name) yield return f.Value.Raw;
            foreach (var f in g.Lists)
                foreach (var x in f.Values)
                    if (f.Type == 'R') { foreach (var v in Values(db, x.Struct!, name, type)) yield return v; }
                    else if (f.Type == type && db.FieldName(g.Blueprint, f.Id) == name) yield return x.Raw;
        }
    }

    /// <summary>A field's value in a prototype or, when it doesn't set it, its parents'.</summary>
    static ulong? Inherited(Fx.GameData db, ulong id, string name, char type)
    {
        for (int hop = 0; hop < 8 && id != 0 && db.Prototypes.ContainsKey(id); hop++)
        {
            var d = db.Prototype(id).Data;
            foreach (var v in Values(db, d, name, type)) return v;
            id = d.HasParent ? d.Parent : 0;
        }
        return null;
    }

    static void Pets(Fx.GameData db, Dictionary<string, Pics> result)
    {
        var pets = new Dictionary<string, Pics>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, e) in db.Prototypes.OrderBy(x => x.Value.Path, StringComparer.OrdinalIgnoreCase))
        {
            string path = e.Path.Replace('/', '\\');
            if (!path.StartsWith("Entity\\Items\\Pets\\", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string? Asset(ulong? v) => v is ulong a && db.Assets.TryGetValue(a, out var x) ? x.Asset.Name : null;
                string? icon = Asset(Inherited(db, id, "IconPath", 'A')), store = Asset(Inherited(db, id, "StoreIconPath", 'A'));
                if (icon == null && store == null) continue;
                foreach (ulong power in Values(db, db.Prototype(id).Data, "Power", 'P').Where(db.Prototypes.ContainsKey))
                    foreach (ulong pet in Values(db, db.Prototype(power).Data, "SummonEntity", 'P').Where(db.Prototypes.ContainsKey))
                        if (Asset(Inherited(db, pet, "UnrealClass", 'A')) is string cls)
                            pets.TryAdd(cls, new Pics(null, store, icon) { NameId = Inherited(db, id, "DisplayName", 'S') ?? 0 });
            }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { }
        }
        // Placeholder pictures (Kurt, 2026-11-06: Potted Groot showed Old Lace): the game names one picture for several pets
        // (Store_PetOldLace for Baby Groot and Old Lace Tiger, Store_PetHeadpool for the ants and Limbo demons, Store_PetDroneHead
        // for Ignatz and Mr. Hammond Cat, Inventory_PetDragonMan for Mini Kronan). A shared picture stays only with the pets whose
        // class (without underscores: oldlace, herbie) is in its name; when none is (Store_PetMarkI, the Mk1 recolors), all keep it.
        // So the Pet window doesn't show another pet, and a move can't change another pet's picture.
        static string Key(string cls) => cls.Replace("MarvelVanityPet_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").ToLowerInvariant();
        string? Owned(string cls, string? asset, Func<Pics, string?> of)
        {
            if (asset == null) return null;
            var sharers = pets.Where(p => string.Equals(of(p.Value), asset, StringComparison.OrdinalIgnoreCase)).Select(p => Key(p.Key)).ToList();
            if (sharers.Count < 2) return asset;
            // The picture's pet part: MarvelUIIcons_Store.Store_PetDroneHead → dronehead (Ultron Drone Head's class is ultrondronehead).
            string part = asset[(asset.IndexOf('.') + 1)..].ToLowerInvariant();
            foreach (string pre in new[] { "store_", "inventory_", "pet" }) if (part.StartsWith(pre)) part = part[pre.Length..];
            part = part.Replace("_", "");
            // Exact first (Store_PetOldLace is Old Lace's, not Old Lace Tiger's; Store_PetHerbie is Herbie's, not Herbie Doom's),
            // else contained (Store_PetDroneHead: Ultron Drone Head's), else nobody's in particular: all keep it (Store_PetMarkI).
            Func<string, bool> match = sharers.Contains(part) ? key => key == part : key => key.Contains(part) || part.Contains(key);
            return !sharers.Any(match) || match(Key(cls)) ? asset : null;
        }
        foreach (var (cls, p) in pets)
            result.TryAdd(cls, new Pics(null, Owned(cls, p.Store, x => x.Store), Owned(cls, p.Icon, x => x.Icon)) { NameId = p.NameId });
    }
}
