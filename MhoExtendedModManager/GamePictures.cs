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
/// The game names some textures that aren't there (herohor_punisher_original, herohor_warmachine_ironman3movie), and some
/// packages have no prototype at all (Rogue's UltimateForm_*, Spiderman_Original …): callers keep their name-based fallback.
/// </summary>
static class GamePictures
{
    /// <summary>Asset names ("MarvelUIIcons.HeroHor_Thor_Classic"); null where the game names none.</summary>
    public sealed record Pics(string? Portrait, string? Store, string? Icon);

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
        return result;
    }
}
