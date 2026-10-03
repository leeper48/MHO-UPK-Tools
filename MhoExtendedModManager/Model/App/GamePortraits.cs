using MhoExtendedModManager.Fx;

namespace MhoMffImporter;

/// <summary>
/// Which portrait the game itself shows for a base hero package (0.10.16; Kurt: the base-hero thumbnails were inconsistent;
/// the MHO Extended Mod Manager session's advice): read from the game data (Data\Game\Calligraphy.sip, read only), not from
/// the package name. Costume prototypes (Entity/Items/Costumes/Prototypes) name their class (CostumeUnrealClass
/// "MarvelPlayer_Thor_Classic" → UC__MarvelPlayer_Thor_Classic_SF) and portrait (PortraitIconPath
/// "MarvelUIIcons.HeroHor_Thor_Classic"); team-ups (Entity/Characters/TeamUps, UnrealClass + PortraitPath) and avatars
/// (Entity/Characters/Avatars, UnrealClass + PortraitPath) likewise. The part before the dot names the icon package
/// (ICO__&lt;part&gt;_SF.upk: MarvelUIIcons, SilverSurferIcons …). Two costumes can share a class (the hero's default wins); zTesting
/// prototypes are skipped. Null when the game data can't be read: the caller falls back to name matching.
/// </summary>
static class GamePortraits
{
    static Dictionary<string, (string Package, string Texture)>? byClass;
    static bool tried;
    static readonly object gate = new();

    /// <summary>The portrait (icon package file, texture name in lower case) for a package file name, or null.</summary>
    public static (string Package, string Texture)? For(string packageFile)
    {
        string stem = Path.GetFileNameWithoutExtension(packageFile);
        if (!stem.StartsWith("UC__", StringComparison.OrdinalIgnoreCase) || !stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) return null;
        string cls = stem[4..^3];
        var map = Map();
        return map != null && map.TryGetValue(cls, out var t) ? t : null;
    }

    static Dictionary<string, (string, string)>? Map()
    {
        lock (gate)
        {
            if (tried) return byClass;
            tried = true;
            try { byClass = Build(); } catch (Exception) { byClass = null; }
            return byClass;
        }
    }

    static Dictionary<string, (string, string)>? Build()
    {
        string? game = Settings.Current.GameFolder;
        if (game == null) return null;
        string sipPath = Path.Combine(game, "Data", "Game", "Calligraphy.sip");
        if (!File.Exists(sipPath)) return null;
        var db = new GameData(SipArchive.Load(sipPath));
        var assets = db.Assets;
        string? Asset(ulong id) => assets.TryGetValue(id, out var a) ? a.Asset.Name : null;

        // a simple asset field of a prototype or (inherit) its parents
        ulong FieldOf(ulong id, string name)
        {
            for (int guard = 0; guard < 8 && db.Prototypes.ContainsKey(id); guard++)
            {
                var d = db.Prototype(id).Data;
                foreach (var g in d.Groups)
                    foreach (var f in g.Simple)
                        if (db.FieldName(g.Blueprint, f.Id).Equals(name, StringComparison.OrdinalIgnoreCase)) return f.Value.Raw;
                if (!d.HasParent) break;
                id = d.Parent;
            }
            return 0;
        }
        static (string, string)? Icon(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int dot = path.IndexOf('.');
            return dot <= 0 ? null : ($"ICO__{path[..dot]}_SF.upk", path[(dot + 1)..].ToLowerInvariant());
        }

        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var defaults = new HashSet<ulong>();   // the heroes' starting costumes (preferred when two share a class)
        var entries = db.Prototypes.Values.Select(e => (e, Path: e.Path.Replace('\\', '/'))).Where(x => !x.Path.Contains("zTesting", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (e, path) in entries.Where(x => x.Path.StartsWith("Entity/Characters/Avatars/", StringComparison.OrdinalIgnoreCase)))
            if (FieldOf(e.Id, "StartingCostume") is ulong sc && sc != 0) defaults.Add(sc);

        // costumes first (the hero's default before the others), then team-ups, then avatars for classes no costume names
        foreach (var (e, path) in entries.Where(x => x.Path.StartsWith("Entity/Items/Costumes/Prototypes/", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(x => defaults.Contains(x.e.Id) ? 0 : 1))
            if (Asset(FieldOf(e.Id, "CostumeUnrealClass")) is string cls && Icon(Asset(FieldOf(e.Id, "PortraitIconPath"))) is { } icon)
                map.TryAdd(cls, icon);
        foreach (var (e, path) in entries.Where(x => x.Path.StartsWith("Entity/Characters/TeamUps/", StringComparison.OrdinalIgnoreCase)
                                                  || x.Path.StartsWith("Entity/Characters/Avatars/", StringComparison.OrdinalIgnoreCase)))
            if (Asset(FieldOf(e.Id, "UnrealClass")) is string cls && Icon(Asset(FieldOf(e.Id, "PortraitPath"))) is { } icon)
                map.TryAdd(cls, icon);
        // an avatar may name its default costume's class (Ms. Marvel: MarvelPlayer_MsMarvel_CaptainMarvelANAD): its portrait
        // also stands for the hero's base class (MarvelPlayer_MsMarvel) when nothing else names that one
        foreach (var (e, path) in entries.Where(x => x.Path.StartsWith("Entity/Characters/Avatars/", StringComparison.OrdinalIgnoreCase)))
            if (Asset(FieldOf(e.Id, "UnrealClass")) is string cls && cls.Split('_') is { Length: > 2 } parts && Icon(Asset(FieldOf(e.Id, "PortraitPath"))) is { } icon)
                map.TryAdd(parts[0] + "_" + parts[1], icon);
        return map;
    }
}
