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
    static Dictionary<ulong, string> English(string gameRoot)
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

    static (ulong Raw, bool Found) Field(GameData db, string path, string name, char type)
    {
        if (db.Find(path) is not { } e) return (0, false);
        foreach (var g in db.Prototype(e.Id).Data.Groups)
            foreach (var f in g.Simple)
                if (f.Type == type && db.FieldName(g.Blueprint, f.Id) == name) return (f.Value.Raw, true);
        return (0, false);
    }

    /// <summary>The hero's powers with their animations, by name.</summary>
    public static List<Power> For(GameData db, string hero, string cooked, IEnumerable<string> modFiles)
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
        return [.. result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
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
                string f = Path.Combine(cooked, $"ICO__{iconPath[..dot]}_SF.upk");
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
