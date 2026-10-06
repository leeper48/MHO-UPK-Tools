using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// A hero's powers as the 3D preview's power buttons show them (Kurt, 2026-09-30: like the MHO Hero Creator's 3D View):
/// each power that plays animations of the hero (PowerIndex: the power packages' PowerFxAnimation names, then the power
/// prototype naming that class), with its name (DisplayName, the game's English strings) and icon (IconPath:
/// "MarvelUIIcons.Power_Thor_…" = ICO__MarvelUIIcons_SF.upk, that texture). Read only.
/// </summary>
static class PowerList
{
    public sealed record Power(string Prototype, string Name, string? Icon, List<string> Animations)
    {
        /// <summary>The game data gives it a display name (else it's a fragment: a combo's part).</summary>
        public bool HasName { get; init; }
    }

    static Dictionary<ulong, string>? english;
    static string? englishRoot;

    /// <summary>The game's English strings (Data\Game\Loco\eng.all\*.string), read once.</summary>
    internal static Dictionary<ulong, string> English(string gameRoot)
    {
        if (english != null && englishRoot == gameRoot) return english;
        var d = new Dictionary<ulong, string>();
        string dir = Path.Combine(gameRoot, "Data", "Game", "Loco", "eng.all");
        if (Directory.Exists(dir))
            foreach (string f in Directory.EnumerateFiles(dir, "*.string"))
                try { foreach (var (id, e) in StringFile.Parse(File.ReadAllBytes(f)).Entries) d.TryAdd(id, e.Text); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
        englishRoot = gameRoot;
        return english = d;
    }

    internal static (ulong Raw, bool Found) Field(GameData db, string path, string name, char type) =>
        db.Find(path) is { } e ? Field(db, e.Id, name, type, false) : (0, false);

    /// <summary>A simple field of a prototype; <paramref name="inherit"/>: else its parents' (a travel power such as
    /// WolverineRide takes its name and icon from the shared bike it's made from).</summary>
    static (ulong Raw, bool Found) Field(GameData db, ulong id, string name, char type, bool inherit)
    {
        for (int guard = 0; guard < 8 && db.Prototypes.ContainsKey(id); guard++)
        {
            var d = db.Prototype(id).Data;
            foreach (var g in d.Groups)
                foreach (var f in g.Simple)
                    if (f.Type == type && db.FieldName(g.Blueprint, f.Id) == name) return (f.Value.Raw, true);
            if (!inherit || !d.HasParent) break;
            id = d.Parent;
        }
        return (0, false);
    }

    /// <summary>The hero's powers with their animations, by name.</summary>
    /// <param name="effectOnly">Also powers with effects but no animation (procs, passives): for colors, not for playing.</param>
    public static List<Power> For(GameData db, string hero, string cooked, IEnumerable<string> modFiles, bool effectOnly = false)
    {
        string gameRoot = Path.GetFullPath(Path.Combine(cooked, "..", "..", ".."));
        var idx = PowerIndex.For(hero, cooked, modFiles);
        var byClass = PowerIndex.PrototypesByClass(db);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (anim, refs) in idx)
            foreach (var r in refs)
                if (byClass.TryGetValue(r.Class, out var protos))
                    foreach (var proto in protos.Take(1))
                    {
                        if (!map.TryGetValue(proto, out var list)) map[proto] = list = [];
                        if (!list.Contains(anim, StringComparer.OrdinalIgnoreCase)) list.Add(anim);
                    }
        var strings = English(gameRoot);
        var result = new List<Power>();
        foreach (var (proto, anims) in map)
        {
            bool hasName = Field(db, proto, "DisplayName", 'S') is { Found: true } dn && strings.TryGetValue(dn.Raw, out var t) && t.Length > 0;
            string name = hasName ? strings[Field(db, proto, "DisplayName", 'S').Raw] : Path.GetFileNameWithoutExtension(proto);
            // Text markup in names: "#powerkeyword#Ribbon#/powerkeyword#" (Angela) reads "Ribbon".
            name = System.Text.RegularExpressions.Regex.Replace(name, "#/?powerkeyword#", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string? icon = Field(db, proto, "IconPath", 'A') is { Found: true } ip && db.Assets.TryGetValue(ip.Raw, out var a) ? a.Asset.Name : null;
            result.Add(new Power(proto, name, icon, [.. anims.Order(StringComparer.OrdinalIgnoreCase)]) { HasName = hasName });
        }
        // Fragments (a combo playing a power's "…_end": BigDFACombo, HammerDashCombo …) have no display name of their own:
        // their animations go to the named power that sets them off (PowerClosure), else they stay as they are.
        // A power with a name and an icon is a button; one without either is a part (a combo's step, a demon form's
        // attack Magik's Infect / Backhand, Colossus's alternate swings: 43 named ones had no icon across the heroes).
        static bool Named(Power p) => p.HasName && p.Icon != null;
        foreach (var frag in result.Where(p => !Named(p)).ToList())
        {
            // (else the power playing the same animation's other part: aerialbarrage_rework_end beside …_start)
            static string Stem(string an) => System.Text.RegularExpressions.Regex.Replace(an, "_(start|end|loop)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var owner = result.Where(Named).FirstOrDefault(p => PowerClosure.Of(db, p.Prototype).Any(x => x.Prototype.Equals(frag.Prototype, StringComparison.OrdinalIgnoreCase)))
                ?? result.Where(Named).FirstOrDefault(p => p.Animations.Any(x => frag.Animations.Any(y => Stem(x).Equals(Stem(y), StringComparison.OrdinalIgnoreCase))))
                // (else the named power whose prototype name starts its name: Punisher's ChemicalBombLauncher, FlashbangLauncher,
                // PineappleGrenadeLauncher → Volatile Nerve Gas, Flashbang, Pineapple Grenade; they showed as letter tiles)
                ?? result.Where(Named).Where(p => Path.GetFileNameWithoutExtension(frag.Prototype).StartsWith(Path.GetFileNameWithoutExtension(p.Prototype), StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => Path.GetFileNameWithoutExtension(p.Prototype).Length).FirstOrDefault();
            if (owner == null) continue;
            foreach (var an in frag.Animations) if (!owner.Animations.Contains(an, StringComparer.OrdinalIgnoreCase)) owner.Animations.Add(an);
            result.Remove(frag);
        }
        // Nameless parts that joined nothing and have no icon (Hulk's Talent2DeflectBonusRevive, ThrowRockComboBigger) aren't
        // buttons in the game: left out here (their animations stay in the list).
        result.RemoveAll(p => !p.HasName || p.Icon == null);
        // The hero's travel power (Kurt): the avatar's TravelPower (Powers\Player\TravelPower\…), found through the avatar
        // (the prototype whose UnrealClass is MarvelPlayer_<Hero>); its animations from its own package (bikes, the
        // Sky-Cycle and gliders live in shared packages: PowerIndex's travel overload reads them).
        if (TravelPowerOf(db, hero) is string travel && !result.Any(x => x.Prototype.Equals(travel, StringComparison.OrdinalIgnoreCase))
            && TravelClassOf(db, travel) is string tcls)
        {
            var anims = PowerIndex.For(hero, cooked, modFiles, db).Where(x => x.Value.Any(r => r.Class.Equals(tcls, StringComparison.OrdinalIgnoreCase))).Select(x => x.Key).ToList();
            ulong tid = db.Find(travel)!.Id;
            string? tname = Field(db, tid, "DisplayName", 'S', true) is { Found: true } tdn && strings.TryGetValue(tdn.Raw, out var tt) && tt.Length > 0 ? tt : null;
            string? ticon = Field(db, tid, "IconPath", 'A', true) is { Found: true } tip && db.Assets.TryGetValue(tip.Raw, out var ta) ? ta.Asset.Name : null;
            // (Beast's, Gambit's, Rogue's … sprints and flights have no name of their own: "Travel Power")
            if (anims.Count > 0)
                result.Add(new Power(travel, tname ?? "Travel Power", ticon, anims) { HasName = true });
        }
        // One button per name (Ragnarok has two prototypes: the leap and the forward strike).
        foreach (var grp in result.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            var keep = grp.First();
            foreach (var other in grp.Skip(1))
            {
                foreach (var an in other.Animations) if (!keep.Animations.Contains(an, StringComparer.OrdinalIgnoreCase)) keep.Animations.Add(an);
                result.Remove(other);
            }
        }
        // Powers with effects but no animation of their own (Kurt: Radiant Cascade, a proc that fires during other attacks,
        // had no color): powers the hero's avatar names (its power progression, passives …), with a name and an icon, not
        // listed yet, with effect packages to recolor. Unused leftovers in the game data (Thor's pre-rework powers) aren't
        // named by the avatar. They have no animations: the preview's power buttons leave them out (nothing to play); the
        // editor's Powers tab lists them for colors.
        if (effectOnly)
        {
            var names = result.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var protos = result.Select(p => p.Prototype.Replace('/', '\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (ulong id in AvatarPowers(db, hero))
            {
                if (!db.Prototypes.TryGetValue(id, out var e)) continue;
                string path = e.Path.Replace('/', '\\');
                if (!path.StartsWith("Powers\\", StringComparison.OrdinalIgnoreCase) || protos.Contains(path)) continue;
                if (Field(db, e.Path, "DisplayName", 'S') is not { Found: true } dn || !strings.TryGetValue(dn.Raw, out var name) || name.Length == 0) continue;
                name = System.Text.RegularExpressions.Regex.Replace(name, "#/?powerkeyword#", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                if (names.Contains(name)) continue;
                if (Field(db, e.Path, "IconPath", 'A') is not { Found: true } ip || !db.Assets.TryGetValue(ip.Raw, out var ia)) continue;
                List<string> files;
                try { files = PowerRecolor.PackagesOf(db, e.Path, hero, cooked); }
                catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
                if (files.Count == 0) continue;
                result.Add(new Power(e.Path, name, ia.Asset.Name, []) { HasName = true });
                names.Add(name);
            }
        }
        // The travel power is always the first button (Kurt, 2026-10-02: the same place for every hero), then by name.
        string? travelPath = TravelPowerOf(db, hero)?.Replace('/', '\\');
        return [.. result.OrderBy(p => travelPath != null && p.Prototype.Replace('/', '\\').Equals(travelPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A travel power's Unreal class, its own or its parents' (BeastSprint takes the shared sprint's).</summary>
    public static string? TravelClassOf(GameData db, string travel) =>
        db.Find(travel) is { } e && Field(db, e.Id, "PowerUnrealClass", 'A', true) is { Found: true } f && db.Assets.TryGetValue(f.Raw, out var a) ? a.Asset.Name : null;

    /// <summary>The avatar's UnrealClass is the hero's base class or one of its costume classes (Ms. Marvel's avatar names
    /// MarvelPlayer_MsMarvel_CaptainMarvelANAD).</summary>
    static bool IsHeroClass(string name, string baseClass) =>
        name.Equals(baseClass, StringComparison.OrdinalIgnoreCase) || name.StartsWith(baseClass + "_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every prototype the hero's avatar names in a P field (nested structs and lists too: power progression,
    /// passives, talents …). The avatar: the prototype under Entity\Characters\Avatars whose UnrealClass is
    /// MarvelPlayer_&lt;hero&gt;.</summary>
    public static HashSet<ulong> AvatarPowers(GameData db, string hero)
    {
        var set = new HashSet<ulong>();
        string cls = "MarvelPlayer_" + hero;
        foreach (var (id, e) in db.Prototypes)
        {
            if (!e.Path.Replace('/', '\\').StartsWith("Entity\\Characters\\Avatars", StringComparison.OrdinalIgnoreCase)) continue;
            Calligraphy.Data d;
            try { d = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
            bool mine = d.Groups.Any(g => g.Simple.Any(f => f.Type == 'A' && db.FieldName(g.Blueprint, f.Id) == "UnrealClass" && db.Assets.TryGetValue(f.Value.Raw, out var a) && IsHeroClass(a.Asset.Name, cls)));
            if (mine) Collect(d, set);
        }
        return set;

        static void Collect(Calligraphy.Data d, HashSet<ulong> set)
        {
            foreach (var g in d.Groups)
            {
                foreach (var f in g.Simple)
                    if (f.Type == 'R') { if (f.Value.Struct != null) Collect(f.Value.Struct, set); }
                    else if (f.Type == 'P' && f.Value.Raw != 0) set.Add(f.Value.Raw);
                foreach (var f in g.Lists)
                    foreach (var v in f.Values)
                        if (f.Type == 'R') { if (v.Struct != null) Collect(v.Struct, set); }
                        else if (f.Type == 'P' && v.Raw != 0) set.Add(v.Raw);
            }
        }
    }

    /// <summary>The hero's travel power prototype (the avatar's TravelPower), or null. The avatar: a prototype whose
    /// UnrealClass is MarvelPlayer_&lt;hero&gt; and that has a TravelPower.</summary>
    public static string? TravelPowerOf(GameData db, string hero)
    {
        string cls = "MarvelPlayer_" + hero;
        foreach (var (id, e) in db.Prototypes)
        {
            if (!e.Path.Replace('/', '\\').StartsWith("Entity\\Characters\\Avatars", StringComparison.OrdinalIgnoreCase)) continue;
            Calligraphy.Data d;
            try { d = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
            bool mine = false; ulong travel = 0;
            foreach (var g in d.Groups)
                foreach (var f in g.Simple)
                {
                    string n = db.FieldName(g.Blueprint, f.Id);
                    if (n == "UnrealClass" && f.Type == 'A' && db.Assets.TryGetValue(f.Value.Raw, out var a) && IsHeroClass(a.Asset.Name, cls)) mine = true;
                    else if (n == "TravelPower" && f.Type == 'P') travel = f.Value.Raw;
                }
            if (mine && travel != 0 && db.Prototypes.TryGetValue(travel, out var te)) return te.Path;
        }
        return null;
    }

    static readonly Dictionary<string, Bitmap?> icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A power icon ("MarvelUIIcons.Power_Thor_X" → ICO__MarvelUIIcons_SF.upk's texture power_thor_x), decoded once; null if not found.</summary>
    public static Bitmap? Icon(string iconPath, string cooked)
    {
        lock (icons) if (icons.TryGetValue(iconPath, out var hit)) return hit;
        Bitmap? bmp = null;
        // A path without its package (Hulk's Ultimate Destruction: "Power_Hulk_Clap") is in the main icon package.
        if (!iconPath.Contains('.')) iconPath = "MarvelUIIcons." + iconPath;
        int dot = iconPath.IndexOf('.');
        if (dot > 0)
            try
            {
                string f = StockFiles.For(cooked, $"ICO__{iconPath[..dot]}_SF.upk");
                if (File.Exists(f))
                {
                    var p = FxPkg.Open(f);
                    string tex = iconPath[(dot + 1)..];
                    int i = Enumerable.Range(0, p.T.Exports.Count).FirstOrDefault(k => p.T.Exports[k].ObjectName.Equals(tex, StringComparison.OrdinalIgnoreCase)
                        && p.T.ClassOf(p.T.Exports[k]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase), -1);
                    if (i >= 0 && new FxTextures([p], cooked).Decoded(p, i) is { } d)
                    {
                        bmp = new Bitmap(d.W, d.H, PixelFormat.Format32bppArgb);
                        var bd = bmp.LockBits(new Rectangle(0, 0, d.W, d.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        for (int y = 0; y < d.H; y++) Marshal.Copy(d.Bgra, y * d.W * 4, bd.Scan0 + y * bd.Stride, d.W * 4);
                        bmp.UnlockBits(bd);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException or ArgumentException) { bmp = null; }
        lock (icons) icons[iconPath] = bmp;
        return bmp;
    }
}
