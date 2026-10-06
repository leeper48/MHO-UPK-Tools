using System.Buffers.Binary;
using System.Numerics;
using MhoPackageModifier;
using MhoExtendedModManager.Fx;

namespace MhoExtendedModManager;

/// <summary>
/// A power's color change (Kurt, 2026-10-01: the power customizer): hue turned by <see cref="Hue"/> degrees (luma kept:
/// the YIQ hue rotation), saturation and brightness scaled. Linear in RGB, so it applies the same to HDR particle colors
/// (lightning's 4, 5, 50) and to each sample of a package's color tables. Ported from the MHO Hero Creator's PowerColor.
/// </summary>
public sealed record PowerColor(float Hue, float Saturation = 1, float Brightness = 1)
{
    /// <summary>Single colors replaced (Kurt, 2026-10-03: change one color of a power, not all of them), applied before
    /// the hue / saturation / brightness change. Empty: none.</summary>
    public IReadOnlyList<ColorMap> Maps { get; init; } = [];

    /// <summary>How visible the effects are (Kurt, 2026-10-05: fade a power out; brightness alone leaves smoke as a black
    /// cloud): 1 as the game has it; below, the particles' alpha (and smooth-alpha effect textures) scaled; 0 also switches
    /// every emitter off, so nothing draws.</summary>
    public float Opacity { get; init; } = 1;
    public const float Invisible = 0.005f;

    public bool IsNone => Maps.Count == 0 && Math.Abs(Hue) < 0.5f && Math.Abs(Saturation - 1) < 0.005f && Math.Abs(Brightness - 1) < 0.005f && Math.Abs(Opacity - 1) < 0.005f;
    bool Shifts => Math.Abs(Hue) >= 0.5f || Math.Abs(Saturation - 1) >= 0.005f || Math.Abs(Brightness - 1) >= 0.005f;

    public bool Equals(PowerColor? o) => o != null && Hue == o.Hue && Saturation == o.Saturation && Brightness == o.Brightness && Opacity == o.Opacity && Maps.SequenceEqual(o.Maps);
    public override int GetHashCode() => HashCode.Combine(Hue, Saturation, Brightness, Opacity, Maps.Count);

    public Vector3 Apply(Vector3 c)
    {
        // MHO_RECOLOR_SWAPRB=1 (test only): red and blue swapped, so a table keeps its [min, max] exactly.
        if (Environment.GetEnvironmentVariable("MHO_RECOLOR_SWAPRB") == "1") return new Vector3(c.Z, c.Y, c.X);
        if (Maps.Count > 0) c = ColorMap.Apply(Maps, c);
        return Shifts ? Shift(c) : c;
    }

    Vector3 Shift(Vector3 c)
    {
        float a = Hue * MathF.PI / 180, co = MathF.Cos(a), si = MathF.Sin(a);
        var r = new Vector3(
            (.299f + .701f * co + .168f * si) * c.X + (.587f - .587f * co + .330f * si) * c.Y + (.114f - .114f * co - .497f * si) * c.Z,
            (.299f - .299f * co - .328f * si) * c.X + (.587f + .413f * co + .035f * si) * c.Y + (.114f - .114f * co + .292f * si) * c.Z,
            (.299f - .300f * co + 1.25f * si) * c.X + (.587f - .588f * co - 1.05f * si) * c.Y + (.114f + .886f * co - .203f * si) * c.Z);
        float luma = .299f * r.X + .587f * r.Y + .114f * r.Z;
        r = new Vector3(luma) + (r - new Vector3(luma)) * Saturation;
        return Vector3.Max(Vector3.Zero, r * Brightness);
    }
}

/// <summary>
/// One color of a power replaced by another (Kurt, 2026-10-03). Colors are compared by their tint: a color divided by its
/// largest channel (so the dim and the HDR-bright versions of a blue, 0.1 or 50, are the same blue), and the replacement
/// keeps each value's own intensity: <see cref="To"/> at full brightness (#FF8000) swaps the tint; a darker
/// <see cref="To"/> (#804000) also dims it by that much. <see cref="Tolerance"/> (0–1, distance between tints with their
/// largest channel at 1) says how close a color must be to count: full replacement up to half of it, fading to none at it.
/// A color near several maps takes the closest one's.
/// </summary>
public sealed record ColorMap(Vector3 From, Vector3 To, float Tolerance)
{
    public static Vector3 Tint(Vector3 v) { float m = MathF.Max(v.X, MathF.Max(v.Y, v.Z)); return m > 1e-6f ? v / m : Vector3.Zero; }

    /// <summary>How much of the replacement a tint gets (1 = all).</summary>
    public float Weight(Vector3 tint)
    {
        float d = Vector3.Distance(tint, Tint(From)), t = MathF.Max(Tolerance, 0.001f);
        if (d >= t) return 0;
        if (d <= t * 0.5f) return 1;
        float x = (t - d) / (t * 0.5f);
        return x * x * (3 - 2 * x);
    }

    public static Vector3 Apply(IReadOnlyList<ColorMap> maps, Vector3 v)
    {
        float i = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
        if (i <= 1e-6f) return v;
        var tint = v / i;
        ColorMap? best = null; float bw = 0;
        foreach (var m in maps) { float w = m.Weight(tint); if (w > bw) { bw = w; best = m; } }
        if (best == null) return v;
        float to = MathF.Max(best.To.X, MathF.Max(best.To.Y, best.To.Z));
        var toTint = to > 1e-6f ? best.To / to : Vector3.Zero;
        return Vector3.Lerp(tint, toTint, bw) * i * (1 + (to - 1) * bw);
    }

    public static string Hex(Vector3 c) => $"#{(int)Math.Clamp(MathF.Round(c.X * 255), 0, 255):X2}{(int)Math.Clamp(MathF.Round(c.Y * 255), 0, 255):X2}{(int)Math.Clamp(MathF.Round(c.Z * 255), 0, 255):X2}";

    public static Vector3? FromHex(string? s)
    {
        s = s?.Trim().TrimStart('#');
        if (s == null || s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int n)) return null;
        return new Vector3((n >> 16 & 255) / 255f, (n >> 8 & 255) / 255f, (n & 255) / 255f);
    }
}

/// <summary>
/// A recolored power package for a mod (Kurt, 2026-10-01; ported from the MHO Hero Creator's PowerRecolor, package side
/// only). The power's own stock package, the SAME file, class and GUID (nothing the server or the game data has to know
/// about: rule 7), with:
///   1. every particle color table recolored (StartColor, ColorOverLife, ColorScaleOverLife of every color module, every
///      LOD level): each RGB sample through <see cref="PowerColor.Apply"/>, the table's [min, max] header recomputed; a
///      distribution object's constant / range vectors too; curves are left (rare);
///   2. the color parameters of its material instances (VectorParameterValues[].ParameterValue, a LinearColor);
///   3. the effect textures the 3D preview draws for its emitters (FxTextures' pick) recolored and put back with every mip
///      in the package (MHO Package Modifier's ReplaceMany: DXT1 / DXT5 only);
///   4. the recolored textures, the materials showing them (with their parents) and the particle systems moved under a
///      group of their own (&lt;class&gt;_recolor_fx.&lt;original groups&gt;…): objects are shared by path across loaded packages,
///      so at their stock paths they would recolor (or be un-recolored by) the same effect in another package.
/// The result is the whole hero's power: every costume shows it.
/// </summary>
static class PowerRecolor
{
    /// <summary>
    /// The packages a power's color goes into: its own class's and those of everything it sets off (PowerClosure: combo and
    /// triggered powers, conditions, missiles, hotspots, pets and summons, and what those set off: Thor's Rolling Thunder has
    /// 6, Squirrel Girl's squirrel missile, Rocket's plasma cannon hotspot), each UC__&lt;class&gt;_SF.upk the game has; only
    /// classes few others use: the prototypes using a class are grouped by owner (this hero: reached from its powers or in its
    /// folders; else the hero, team-up or pet folder they sit in), and a class with 6 or more owners is generic (Knockdown,
    /// Slow, Taunt, DebuffDamage: every hero's) and left. One with a few (Thor's Death From Above: Beta Ray Bill's team-up
    /// copy too) is recolored; <see cref="SharedWith"/> names them so the editor can say so.
    /// </summary>
    public static List<string> PackagesOf(GameData db, string power, string hero, string cooked) =>
        PackagesOf(db, power, HeroPrototypes(db, hero, cooked), hero, cooked);

    /// <summary>An NPC's or enemy's power (Fx.AgentPowers): the same, with that character's powers as "its own".</summary>
    public static List<string> PackagesOfAgent(GameData db, string power, string cls, string cooked) =>
        PackagesOf(db, power, AgentPowers.Mine(db, cls), cls, cooked);

    static List<string> PackagesOf(GameData db, string power, HashSet<string> mine, string owner, string cooked)
    {
        var classes = new List<string>();
        if (PowerEffects.UnrealClassOf(db, power) is string own) classes.Add(own);
        foreach (var a in PowerClosure.Of(db, power)) if (!string.IsNullOrEmpty(a.Class)) classes.Add(a.Class);
        bool Owned(string cls) => Owners(db, cls, mine, owner).Count < 6;   // (generic ones have dozens; a power copied by Rogue, a team-up and Omega has 4)
        return [.. classes.Distinct(StringComparer.OrdinalIgnoreCase).Where(Owned)
            .Select(c => $"UC__{c}_SF.upk").Where(f => File.Exists(Path.Combine(cooked, f)))];
    }

    /// <summary>The owners of the prototypes using a class: this hero (its own), else "Beta Ray Bill (team-up)", "Deadpool",
    /// "Rogue (pet)", or the folder (enemies and other content).</summary>
    static HashSet<string> Owners(GameData db, string cls, HashSet<string> mine, string owner)
    {
        var o = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner };
        if (ClassUsers(db).TryGetValue(cls, out var list))
            foreach (string u in list) o.Add(mine.Contains(u) ? owner : OwnerOf(u));
        return o;
    }

    static string OwnerOf(string path)
    {
        var p = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length > 2 && p[0].Equals("Powers", StringComparison.OrdinalIgnoreCase))
            return p[1].Equals("Player", StringComparison.OrdinalIgnoreCase) ? Spaced(p[2]) : p[1].Equals("TeamUps", StringComparison.OrdinalIgnoreCase) ? Spaced(p[2]) + " (team-up)" : p[1] + "/" + p[2];
        int k = Array.FindIndex(p, x => x.Equals("PetsAndSummons", StringComparison.OrdinalIgnoreCase));
        if (k >= 0 && k + 1 < p.Length - 1) return Spaced(p[k + 1]) + " (pet / summon)";
        return string.Join("/", p.Take(Math.Min(3, p.Length - 1)));
    }

    static string Spaced(string s) => System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>Who else a power's recolor changes: the other owners of the packages it recolors (Beta Ray Bill (team-up),
    /// Deadpool …); empty when it's the hero's alone.</summary>
    public static List<string> SharedWith(GameData db, string power, string hero, string cooked) =>
        SharedWith(db, power, HeroPrototypes(db, hero, cooked), hero, cooked);

    /// <summary>An NPC's or enemy's power: who else its recolor changes.</summary>
    public static List<string> SharedWithAgent(GameData db, string power, string cls, string cooked) =>
        SharedWith(db, power, AgentPowers.Mine(db, cls), cls, cooked);

    static List<string> SharedWith(GameData db, string power, HashSet<string> mine, string hero, string cooked)
    {
        var files = PackagesOf(db, power, mine, hero, cooked).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var classes = new List<string>();
        if (PowerEffects.UnrealClassOf(db, power) is string own) classes.Add(own);
        foreach (var a in PowerClosure.Of(db, power)) if (!string.IsNullOrEmpty(a.Class)) classes.Add(a.Class);
        return [.. classes.Distinct(StringComparer.OrdinalIgnoreCase).Where(c => files.Contains($"UC__{c}_SF.upk"))
            .SelectMany(c => Owners(db, c, mine, hero)).Where(o => !o.Equals(hero, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>For --power-packages: the prototypes using a class that aren't the hero's (why it's left).</summary>
    public static IEnumerable<string> UsersOf(GameData db, string cls, string hero, string cooked)
    {
        var mine = HeroPrototypes(db, hero, cooked);
        return ClassUsers(db).TryGetValue(cls, out var l) ? l.Where(u => !mine.Contains(u)).Take(4) : [];
    }

    static Dictionary<string, List<string>>? classUsers;
    static readonly object usersLock = new();

    /// <summary>Every Unreal class (UnrealClass / PowerUnrealClass asset fields) → the prototypes using it (paths, '/'),
    /// over the whole game data; once per session.</summary>
    static Dictionary<string, List<string>> ClassUsers(GameData db)
    {
        lock (usersLock)
        {
            if (classUsers != null) return classUsers;
            var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, e) in db.Prototypes)
            {
                Calligraphy.Data data;
                try { data = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { continue; }
                foreach (var g in data.Groups)
                    foreach (var f in g.Simple)
                        if (f.Type == 'A' && db.FieldName(g.Blueprint, f.Id) is "UnrealClass" or "PowerUnrealClass" && db.Assets.TryGetValue(f.Value.Raw, out var a))
                        {
                            if (!d.TryGetValue(a.Asset.Name, out var list)) d[a.Asset.Name] = list = [];
                            list.Add(e.Path.Replace('\\', '/'));
                        }
            }
            return classUsers = d;
        }
    }

    static readonly Dictionary<string, HashSet<string>> heroProtos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The prototypes that are the hero's: everything reached from its powers (power buttons, PowerClosure), and
    /// every prototype in a folder named after the hero (Powers/Player/SquirrelGirl/…, …/PetsAndSummons/DoctorStrange/…).</summary>
    static HashSet<string> HeroPrototypes(GameData db, string hero, string cooked)
    {
        lock (heroProtos) if (heroProtos.TryGetValue(hero, out var hit)) return hit;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in PowerList.For(db, hero, cooked, []))
        {
            set.Add(p.Prototype.Replace('\\', '/'));
            foreach (var a in PowerClosure.Of(db, p.Prototype)) set.Add(a.Prototype.Replace('\\', '/'));
        }
        foreach (var e in db.Prototypes.Values)
            if (e.Path.Replace('\\', '/').Contains("/" + hero + "/", StringComparison.OrdinalIgnoreCase)) set.Add(e.Path.Replace('\\', '/'));
        // The powers the hero's avatar names, with what they set off (Ms. Marvel's live in Powers/Player/CaptainMarvel/:
        // they read as another owner, "Captain Marvel").
        foreach (ulong id in PowerList.AvatarPowers(db, hero))
            if (db.Prototypes.TryGetValue(id, out var ap) && ap.Path.Replace('\\', '/').StartsWith("Powers/", StringComparison.OrdinalIgnoreCase))
            {
                set.Add(ap.Path.Replace('\\', '/'));
                foreach (var a in PowerClosure.Of(db, ap.Path)) set.Add(a.Prototype.Replace('\\', '/'));
            }
        lock (heroProtos) heroProtos[hero] = set;
        return set;
    }

    /// <summary>Builds the recolored package from a stock copy; null when it has nothing to recolor (Crack the Sky's own class:
    /// its lightning is the powers it sets off). Throws on any problem; writes nothing.</summary>
    public static byte[]? Build(string stockFile, PowerColor color, string cooked, Action<string> log)
    {
        var pkg = Package.Open(stockFile);
        string file = Path.GetFileName(stockFile);
        string cls = Path.GetFileNameWithoutExtension(file);
        if (cls.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) cls = cls[4..];
        if (cls.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) cls = cls[..^3];
        cls = cls.ToLowerInvariant();

        // 1 + 2. Color tables, distribution vectors and material color parameters: in a copy of the body, sizes unchanged.
        byte[] body = pkg.Body.ToArray();
        var t = new FxTables(pkg);
        var (tables, objects, left) = RecolorTables(body, t, color.Apply);
        if (color.Opacity < 0.995f)
        {
            var (at, ao, al) = ScaleAlpha(body, t, color.Opacity);
            log($"opacity {color.Opacity * 100:0} %: {at} alpha table(s) and {ao} alpha value(s) scaled" + (al > 0 ? $"; {al} alpha curve(s) left as they are" : ""));
        }
        var tinted = new SortedSet<int>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (_, at) in VectorParameters(body, t, i))
            {
                var r = color.Apply(new Vector3(F(body, at), F(body, at + 4), F(body, at + 8)));
                W(body, at, r.X); W(body, at + 4, r.Y); W(body, at + 8, r.Z);
                tinted.Add(i);
            }
        }
        log($"{tables} particle color table(s) and {objects} color value(s) recolored" + (left > 0 ? $"; {left} curve(s) left as they are" : "") +
            (tinted.Count > 0 ? $"; {tinted.Count} material instance(s)' color parameters" : ""));
        var changed = new Dictionary<int, Func<long, byte[]>>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = t.Exports[i];
            if (e.SerialSize <= 0) continue;
            if (!body.AsSpan(e.SerialOffset, e.SerialSize).SequenceEqual(pkg.Body.AsSpan(e.SerialOffset, e.SerialSize)))
            {
                byte[] d = body.AsSpan(e.SerialOffset, e.SerialSize).ToArray();
                changed[i] = _ => d;
            }
        }
        // Opacity 0: every emitter switched off (its LOD levels' bEnabled false), so nothing draws, whatever its alpha setup
        var offNames = new List<string>();
        if (color.Opacity < PowerColor.Invisible)
        {
            int off = 0;
            for (int i = 0; i < pkg.Exports.Length; i++)
            {
                if (!t.ClassOf(t.Exports[i]).Equals("ParticleLODLevel", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] d = changed.TryGetValue(i, out var f0) ? f0(0) : pkg.ReadExportBytes(pkg.Exports[i]).ToArray();
                if (EmitterOff(pkg, d, offNames) is { } nd) { changed[i] = _ => nd; off++; }
            }
            log($"opacity 0 %: {off} emitter level(s) switched off");
            // a hologram the character's mesh component puts on it (the Holo Wolverine team-up): taken off, so the mesh's
            // own materials show
            string compPath = $"marvelgamecontent.default__{cls}.initialskeletalmesh";
            int comp = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals(compPath, StringComparison.OrdinalIgnoreCase));
            if (comp >= 0)
            {
                byte[] cd = changed.TryGetValue(comp, out var cf) ? cf(0) : pkg.ReadExportBytes(pkg.Exports[comp]).ToArray();
                var ct = TagWalker.Walk(pkg, cd, 16);
                if (ct?.FirstOrDefault(x => x.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)) is { } mt && BitConverter.ToInt32(cd, mt.ValueAt) is int mn && mn > 0 && mt.Size == 4 + 4 * mn
                    && Enumerable.Range(0, mn).Any(k => Hologram(pkg, BitConverter.ToInt32(cd, mt.ValueAt + 4 + 4 * k))))
                {
                    byte[] nd = [.. cd.AsSpan(0, mt.Start), .. cd.AsSpan(mt.End)];
                    changed[comp] = _ => nd;
                    log("opacity 0 %: the hologram material on its mesh taken off (its own materials show)");
                }
            }
        }
        byte[] stage = PackageRebuilder.Rebuild(pkg, changed, [], out _, offNames);
        bool anySystem = Enumerable.Range(0, t.Exports.Count).Any(i => t.ClassOf(t.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase));
        if (changed.Count == 0 && !anySystem) { log($"{file}: nothing to recolor (no particle effects or material colors in it)"); return null; }

        // 3. The drawn textures (the preview's pick for each emitter's material), recolored.
        var fx = new FxPkg(file, pkg.Body, t);
        var tex = new FxTextures([fx], cooked);
        var (picked, materials, systems) = PickTextures(pkg, t, fx, tex);
        var items = new List<TextureImport.Replacement>();
        var recolored = new SortedSet<int>();
        string temp = Path.Combine(Path.GetTempPath(), "mhoextmm_recolor_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            foreach (int i in picked)
            {
                string path = t.PathOf(i + 1);
                var props = FxProps.Find(pkg.Body, t, t.Exports[i])?.Props ?? [];
                string fmt = props.FirstOrDefault(x => x.Name.Equals("Format", StringComparison.OrdinalIgnoreCase))?.Value?.ToLowerInvariant() ?? "";
                string? want = fmt.EndsWith("pf_dxt1") ? "dxt1" : fmt.EndsWith("pf_dxt5") ? "dxt5" : null;
                if (want == null) { log($"note: {path}: format {fmt} can't be written back (DXT1 / DXT5 only), its colors stay"); continue; }
                var d = tex.Decoded(fx, i);
                if (d == null) { log($"note: {path}: its pixels can't be read, its colors stay"); continue; }
                string png = Path.Combine(temp, t.Exports[i].ObjectName + "_" + i + ".png");
                // opacity: smooth alpha (DXT5) scaled; a DXT1's one-bit alpha only off at 0 (any fade in between would snap back)
                WriteRecolored(d.Value.Bgra, d.Value.W, d.Value.H, color, png, want == "dxt5" || color.Opacity < PowerColor.Invisible ? color.Opacity : 1);
                var enc = TextureEncode.FromImage(png, want, 85, 1f);
                items.Add(new TextureImport.Replacement(path, TextureImport.WriteDds(enc), t.Exports[i].ObjectName));
                recolored.Add(i);
            }
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        if (items.Count > 0)
        {
            var sp = Package.FromBytes(stage);
            stage = TextureImport.ReplaceMany(sp, items, out var problems, out var verify) ?? throw new InvalidDataException("textures: " + string.Join("; ", problems));
            var vp = verify(stage);
            if (vp.Count > 0) throw new InvalidDataException("textures: " + string.Join("; ", vp.Take(5)));
        }
        log($"{recolored.Count} of {picked.Count} effect texture(s) recolored");

        // 4. Moved under a group of their own.
        foreach (int i in tinted) materials.Add(i);
        materials.RemoveWhere(m => m < 0);
        var moved = recolored.Concat(materials).Concat(systems).Distinct().OrderBy(x => x).ToList();
        string top = cls + "_recolor_fx";
        // MHO_RECOLOR_NOMOVE=1 (test only): leave everything at its stock path, to tell a path problem from a color one.
        if (Environment.GetEnvironmentVariable("MHO_RECOLOR_NOMOVE") == "1") { log("test: nothing moved (MHO_RECOLOR_NOMOVE)"); moved.Clear(); }
        List<string> mp = [];
        byte[] output = moved.Count == 0 ? stage : Move(Package.FromBytes(stage), moved, top, out mp);
        if (mp.Count > 0) throw new InvalidDataException($"{file}: " + string.Join("; ", mp.Take(5)));
        log($"{moved.Count} object(s) under {top} ({recolored.Count} texture(s), {materials.Count} material(s), {systems.Count} particle system(s)): no path shared with the stock effects");

        // Read back: every particle system still reads.
        var back = Package.FromBytes(output);
        var bt = new FxTables(back);
        var bp = new FxPkg(file, back.Body, bt);
        int bad = systems.Count(i => ParticleData.Read(bp, i) == null);
        if (bad > 0) throw new InvalidDataException($"{file}: {bad} particle system(s) don't read back");
        if (!back.Exports.Select(e => back.ClassOf(e)).SequenceEqual(pkg.Exports.Select(e => pkg.ClassOf(e)).Concat(back.Exports.Skip(pkg.Exports.Length).Select(e => back.ClassOf(e)))))
            throw new InvalidDataException($"{file}: the export table changed");
        log($"{file}: reads back ({back.Exports.Length} exports, {systems.Count} particle system(s))");
        return output;
    }

    /// <summary>The effect textures the package's particle systems draw (FxTextures' pick, those in this package), the
    /// materials showing them (with their parents) and the particle systems.</summary>
    static (SortedSet<int> Picked, SortedSet<int> Materials, SortedSet<int> Systems) PickTextures(Package pkg, FxTables t, FxPkg fx, FxTextures tex)
    {
        var picked = new SortedSet<int>(); var materials = new SortedSet<int>(); var systems = new SortedSet<int>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) continue;
            systems.Add(i);
            var data = ParticleData.Read(fx, i);
            if (data == null) continue;
            foreach (var em in data.Emitters)
            {
                int mat = em.Required.Ref("Material");
                bool sub = em.Required.Int("SubImages_Horizontal", 1) * em.Required.Int("SubImages_Vertical", 1) > 1;
                if (tex.ParticleTexture(fx, mat, sub, out _) is { } pk && ReferenceEquals(pk.P, fx)) { picked.Add(pk.Export); MaterialChain(t, pkg.Body, mat, materials); }
            }
        }
        return (picked, materials, systems);
    }

    /// <summary>One color a power uses (its tint: largest channel 1), how much of it there is, and where it's from.</summary>
    public sealed record Swatch(Vector3 Tint, float Weight, string Sources, float Share);

    /// <summary>
    /// The colors a power's packages use (Kurt, 2026-10-03: pick one to replace): every particle color table sample and
    /// color value, material color parameters, and the pixels of the effect textures it draws (each texture weighs as much
    /// as 24 color values, spread over its pixels by alpha). Near-black is left out. Grouped by tint: greys (low saturation)
    /// together, else by 15 degrees of hue and three saturation steps; each group shows its weighted mean tint. Groups under
    /// 1 % are dropped; the largest first, at most <paramref name="max"/>.
    /// </summary>
    public static List<Swatch> Palette(IEnumerable<string> files, string cooked, int max = 16)
    {
        var groups = new Dictionary<int, (Vector3 Sum, float W, HashSet<string> From)>();
        void Add(Vector3 v, float w, string from)
        {
            float i = MathF.Max(v.X, MathF.Max(v.Y, v.Z));
            if (i < 0.02f || w <= 0) return;
            var tint = v / i;
            float sat = 1 - MathF.Min(tint.X, MathF.Min(tint.Y, tint.Z));
            int key = sat < 0.12f ? -1 : (int)(Hue(tint) / 15) % 24 * 3 + (sat < 0.4f ? 0 : sat < 0.75f ? 1 : 2);
            groups.TryGetValue(key, out var g);
            var set = g.From ?? new HashSet<string>();
            set.Add(from);
            groups[key] = (g.Sum + tint * w, g.W + w, set);
        }
        foreach (string f in files)
        {
            var pkg = Package.Open(f);
            byte[] body = pkg.Body.ToArray();
            var t = new FxTables(pkg);
            RecolorTables(body, t, v => { Add(v, 1, "particles"); return v; });
            for (int i = 0; i < t.Exports.Count; i++)
            {
                if (!t.ClassOf(t.Exports[i]).StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (_, at) in VectorParameters(body, t, i)) Add(new Vector3(F(body, at), F(body, at + 4), F(body, at + 8)), 1, "materials");
            }
            var fx = new FxPkg(Path.GetFileName(f), pkg.Body, t);
            var tex = new FxTextures([fx], cooked);
            foreach (int i in PickTextures(pkg, t, fx, tex).Picked)
            {
                if (tex.Decoded(fx, i) is not { } d) continue;
                byte[] p = d.Bgra;
                bool alphaVaries = false;
                for (int k = 7; k < p.Length && !alphaVaries; k += 4) if (p[k] != p[3]) alphaVaries = true;
                int step = Math.Max(1, d.W * d.H / 16384);
                var px = new List<(Vector3, float)>();
                float total = 0;
                for (int k = 0; k < d.W * d.H; k += step)
                {
                    int o = k * 4;
                    float a = alphaVaries ? p[o + 3] / 255f : 1;
                    var v = new Vector3(p[o + 2], p[o + 1], p[o]) / 255f;
                    if (a <= 0.02f || MathF.Max(v.X, MathF.Max(v.Y, v.Z)) < 0.02f) continue;
                    px.Add((v, a)); total += a;
                }
                foreach (var (v, a) in px) Add(v, a / total * 24, "textures");
            }
        }
        float all = groups.Values.Sum(g => g.W);
        return [.. groups.Values.Where(g => g.W >= all * 0.01f).OrderByDescending(g => g.W).Take(max)
            .Select(g => new Swatch(ColorMap.Tint(g.Sum / g.W), g.W, string.Join(", ", g.From.Order()), g.W / all))];
    }

    /// <summary>A tint's hue, 0 to 360 degrees.</summary>
    public static float Hue(Vector3 c)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z)), d = mx - mn;
        if (d < 1e-6f) return 0;
        float h = mx == c.X ? (c.Y - c.Z) / d % 6 : mx == c.Y ? (c.Z - c.X) / d + 2 : (c.X - c.Y) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>A material instance's color parameters: each VectorParameterValues element's ParameterName and where its
    /// ParameterValue (LinearColor: 4 floats) sits in the body (MIC and MITV).</summary>
    public static List<(string Name, int At)> VectorParameters(byte[] b, FxTables t, int export)
    {
        var o = new List<(string, int)>();
        var arr = FxProps.Find(b, t, t.Exports[export])?.Props.FirstOrDefault(x => x.Name.Equals("VectorParameterValues", StringComparison.OrdinalIgnoreCase));
        if (arr == null || !arr.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)) return o;
        int n = BitConverter.ToInt32(b, arr.ValueAt), at = arr.ValueAt + 4, end = arr.ValueAt + arr.Size;
        for (int k = 0; k < n && at < end; k++)
        {
            var el = FxProps.TryRead(b, t, at, end);
            if (el == null || el.Count == 0) break;
            string name = el.FirstOrDefault(x => x.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            if (el.FirstOrDefault(x => x.Name.Equals("ParameterValue", StringComparison.OrdinalIgnoreCase)) is { Size: 16 } pv) o.Add((name, pv.ValueAt));
            var last = el[^1];
            at = last.ValueAt + (last.Type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : last.Size) + 8;   // past its None
        }
        return o;
    }

    /// <summary>A material and its parents (MIC → parent …) that are exports of this package.</summary>
    static void MaterialChain(FxTables t, byte[] b, int reference, SortedSet<int> into)
    {
        for (int depth = 0; depth < 8 && reference > 0; depth++)
        {
            int i = reference - 1;
            if (!into.Add(i)) return;
            var props = FxProps.Find(b, t, t.Exports[i])?.Props ?? [];
            var parent = props.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase));
            reference = parent != null && parent.Size == 4 ? BitConverter.ToInt32(b, parent.ValueAt) : 0;
        }
    }

    /// <summary>A texture's pixels recolored into a PNG. Additive effect textures often carry an unused, flat alpha (Forked
    /// Lightning's is all 0): that's written opaque, so the encoder keeps the colors (its mips weigh color by alpha).</summary>
    static void WriteRecolored(byte[] bgra, int w, int h, PowerColor c, string png, float alpha = 1)
    {
        bool alphaVaries = false;
        for (int k = 7; k < bgra.Length && !alphaVaries; k += 4) if (bgra[k] != bgra[3]) alphaVaries = true;
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var outp = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            var v = c.Apply(new Vector3(bgra[o + 2], bgra[o + 1], bgra[o]) * (1f / 255f));
            outp[o + 2] = (byte)Math.Clamp((int)(v.X * 255 + 0.5f), 0, 255); outp[o + 1] = (byte)Math.Clamp((int)(v.Y * 255 + 0.5f), 0, 255);
            outp[o] = (byte)Math.Clamp((int)(v.Z * 255 + 0.5f), 0, 255);
            outp[o + 3] = (byte)Math.Clamp((int)((alphaVaries ? bgra[o + 3] : 255) * alpha + 0.5f), 0, 255);
        }
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(outp, y * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
        bmp.UnlockBits(bd);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Every color module's RGB distributions recolored in place (sizes unchanged). Returns the lookup tables and
    /// distribution-object vectors changed, and the curves left (not baked: rare).</summary>
    public static (int Tables, int Objects, int Left) RecolorTables(byte[] b, FxTables t, Func<Vector3, Vector3> apply)
    {
        int tables = 0, objects = 0, left = 0;
        var doneObjects = new HashSet<int>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).StartsWith("ParticleModuleColor", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var prop in FxProps.Find(b, t, t.Exports[i])?.Props ?? [])
            {
                if (!prop.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) continue;
                string n = prop.Name.ToLowerInvariant();
                if (n is not ("startcolor" or "coloroverlife" or "colorscaleoverlife")) continue;
                var inner = FxProps.TryRead(b, t, prop.ValueAt, prop.ValueAt + prop.Size) ?? [];
                int chunk = 0, dist = 0;
                FxProps.Prop? table = null;
                foreach (var x in inner)
                {
                    string xn = x.Name.ToLowerInvariant();
                    if (xn == "lookuptablechunksize" && x.Size == 1) chunk = b[x.ValueAt];
                    else if (xn == "lookuptable") table = x;
                    else if (xn == "distribution" && x.Size == 4) dist = BitConverter.ToInt32(b, x.ValueAt);
                }
                if (table != null && BitConverter.ToInt32(b, table.ValueAt) is int count && count > 2 && table.Size == 4 + 4 * count)
                {
                    if (chunk == 0) chunk = 3;
                    if (chunk % 3 != 0) continue;
                    int at = table.ValueAt + 4;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int k = 2; k + 3 <= count; k += 3)
                    {
                        var v = apply(new Vector3(F(b, at + 4 * k), F(b, at + 4 * k + 4), F(b, at + 4 * k + 8)));
                        W(b, at + 4 * k, v.X); W(b, at + 4 * k + 4, v.Y); W(b, at + 4 * k + 8, v.Z);
                        lo = MathF.Min(lo, MathF.Min(v.X, MathF.Min(v.Y, v.Z))); hi = MathF.Max(hi, MathF.Max(v.X, MathF.Max(v.Y, v.Z)));
                    }
                    W(b, at, lo); W(b, at + 4, hi);
                    tables++;
                }
                if (dist > 0 && doneObjects.Add(dist - 1))
                {
                    var de = t.Exports[dist - 1];
                    string dc = t.ClassOf(de).ToLowerInvariant();
                    if (dc.Contains("curve")) { left++; continue; }
                    foreach (var v in FxProps.Find(b, t, de)?.Props ?? [])
                        if (v.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase) && v.Size == 12 && v.Name.ToLowerInvariant() is "constant" or "min" or "max" or "minlow" or "minhigh" or "maxlow" or "maxhigh")
                        {
                            var r = apply(new Vector3(F(b, v.ValueAt), F(b, v.ValueAt + 4), F(b, v.ValueAt + 8)));
                            W(b, v.ValueAt, r.X); W(b, v.ValueAt + 4, r.Y); W(b, v.ValueAt + 8, r.Z);
                            objects++;
                        }
                }
            }
        }
        return (tables, objects, left);
    }

    /// <summary>Every color module's alpha (StartAlpha, AlphaOverLife, AlphaScaleOverLife: lookup tables and distribution
    /// objects' Constant / Min / Max) times <paramref name="opacity"/>, in place. Returns the tables and values changed, and
    /// the curves left.</summary>
    public static (int Tables, int Objects, int Left) ScaleAlpha(byte[] b, FxTables t, float opacity)
    {
        int tables = 0, objects = 0, left = 0;
        var doneObjects = new HashSet<int>();
        for (int i = 0; i < t.Exports.Count; i++)
        {
            if (!t.ClassOf(t.Exports[i]).StartsWith("ParticleModuleColor", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var prop in FxProps.Find(b, t, t.Exports[i])?.Props ?? [])
            {
                if (!prop.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Name.ToLowerInvariant() is not ("startalpha" or "alphaoverlife" or "alphascaleoverlife")) continue;
                var inner = FxProps.TryRead(b, t, prop.ValueAt, prop.ValueAt + prop.Size) ?? [];
                int dist = 0;
                FxProps.Prop? table = null;
                foreach (var x in inner)
                {
                    string xn = x.Name.ToLowerInvariant();
                    if (xn == "lookuptable") table = x;
                    else if (xn == "distribution" && x.Size == 4) dist = BitConverter.ToInt32(b, x.ValueAt);
                }
                // a float table: [min, max, values …]; scaling by a factor ≥ 0 keeps min and max in order
                if (table != null && BitConverter.ToInt32(b, table.ValueAt) is int count && count > 2 && table.Size == 4 + 4 * count)
                {
                    int at = table.ValueAt + 4;
                    for (int k = 0; k < count; k++) W(b, at + 4 * k, F(b, at + 4 * k) * opacity);
                    tables++;
                }
                if (dist > 0 && doneObjects.Add(dist - 1))
                {
                    var de = t.Exports[dist - 1];
                    if (t.ClassOf(de).ToLowerInvariant().Contains("curve")) { left++; continue; }
                    foreach (var v in FxProps.Find(b, t, de)?.Props ?? [])
                        if (v.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) && v.Size == 4 && v.Name.ToLowerInvariant() is "constant" or "min" or "max")
                        { W(b, v.ValueAt, F(b, v.ValueAt) * opacity); objects++; }
                }
            }
        }
        return (tables, objects, left);
    }

    /// <summary>A material whose parent chain is a hologram material (its name has "hologram").</summary>
    static bool Hologram(Package pkg, int r)
    {
        for (int depth = 0; r != 0 && depth < 6; depth++)
        {
            string name = r > 0 ? pkg.Exports[r - 1].ObjectName : pkg.RefName(r);
            if (name.Contains("hologram", StringComparison.OrdinalIgnoreCase)) return true;
            if (r < 0) return false;
            byte[] d = pkg.ReadExportBytes(pkg.Exports[r - 1]).ToArray();
            var t = TagWalker.Walk(pkg, d, 4)?.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
            if (t == null) return false;
            r = BitConverter.ToInt32(d, t.ValueAt);
        }
        return false;
    }

    /// <summary>A ParticleLODLevel switched off: its bEnabled set to false (the tag added before None when it's omitted, as
    /// the default true is). Null when it can't be read or is off already.</summary>
    static byte[]? EmitterOff(Package pkg, byte[] d, List<string> addNames)
    {
        var tags = TagWalker.Walk(pkg, d, 4);
        if (tags == null) return null;
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var en = tags.FirstOrDefault(x => x.Name.Equals("bEnabled", StringComparison.OrdinalIgnoreCase) && x.Type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase));
        if (en != null)
        {
            if (d[en.ValueAt - 1] == 0) return null;
            var c = (byte[])d.Clone(); c[en.ValueAt - 1] = 0; return c;
        }
        var tag = new byte[25];   // name, type, size 0, array index 0, the value byte (false)
        BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("bEnabled"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("BoolProperty"));
        return [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
    }

    static float F(byte[] b, int at) => BitConverter.ToSingle(b, at);
    static void W(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);

    /// <summary>
    /// Moves exports under a new top group <paramref name="top"/> (a copy of the first moved object's top group entry,
    /// renamed), holding copies of the groups each moved object sits in (same names), so a moved object keeps its path
    /// below the top. Adds one name and the group exports; changes only the moved exports' Outer. Checked: every path as
    /// planned, every original export's data identical.
    /// </summary>
    static byte[] Move(Package pkg, List<int> moved, string top, out List<string> problems)
    {
        problems = new List<string>();
        if (moved.Count == 0) return PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), [], out _);
        List<int> Chain(int i) { var c = new List<int>(); for (int o = pkg.Exports[i].OuterIndex; o > 0 && c.Count < 32; o = pkg.Exports[o - 1].OuterIndex) c.Insert(0, o - 1); return c; }
        var addNames = pkg.Names.Any(n => n.Equals(top, StringComparison.OrdinalIgnoreCase)) ? new List<string>() : new List<string> { top };
        int topName = Array.FindIndex(pkg.Names, n => n.Equals(top, StringComparison.OrdinalIgnoreCase)) is int k0 && k0 >= 0 ? k0 : pkg.Names.Length;
        byte[] Entry(int i) => pkg.Body.AsSpan(pkg.ExportEntryStart[i], pkg.ExportEntryEnd[i] - pkg.ExportEntryStart[i]).ToArray();
        var add = new List<NewExport>();
        var copyOf = new Dictionary<int, int>();
        int NewRef() => pkg.Exports.Length + add.Count + 1;
        var firstChain = moved.Select(Chain).FirstOrDefault(c => c.Count > 0) ?? throw new InvalidDataException("the moved objects sit in no group");
        byte[] topEntry = Entry(firstChain[0]);
        BinaryPrimitives.WriteInt32LittleEndian(topEntry.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(topEntry.AsSpan(12), topName);
        BinaryPrimitives.WriteInt32LittleEndian(topEntry.AsSpan(16), 0);
        int topRef = NewRef();
        byte[] topData = pkg.ReadExportBytes(pkg.Exports[firstChain[0]]);
        add.Add(new NewExport(firstChain[0], 0, _ => topData) { Entry = topEntry });
        var outers = new Dictionary<int, int>();
        foreach (int i in moved)
        {
            int parentRef = topRef;
            foreach (int g in Chain(i))
            {
                if (!copyOf.TryGetValue(g, out int gRef))
                {
                    byte[] ge = Entry(g);
                    BinaryPrimitives.WriteInt32LittleEndian(ge.AsSpan(8), parentRef);
                    gRef = NewRef();
                    byte[] gd = pkg.ReadExportBytes(pkg.Exports[g]);
                    add.Add(new NewExport(g, 0, _ => gd) { Entry = ge });
                    copyOf[g] = gRef;
                }
                parentRef = gRef;
            }
            outers[i] = parentRef;
        }
        byte[] output = PackageRebuilder.Rebuild(pkg, new Dictionary<int, Func<long, byte[]>>(), add, out var data, addNames, null, null, outers);
        problems.AddRange(PackageRebuilder.Verify(pkg, output, [], add, data, addNames, null, null, outers));
        var back = Package.FromBytes(output);
        bool Inside(int i) { for (int o = i, k = 0; o >= 0 && k < 64; o = pkg.Exports[o].OuterIndex - 1, k++) if (outers.ContainsKey(o)) return true; return false; }
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            string was = pkg.PathOf(pkg.Exports[i]), now = back.PathOf(back.Exports[i]);
            if (Inside(i)) { if (!now.Equals(top + "." + was, StringComparison.OrdinalIgnoreCase)) problems.Add($"{was} is {now}"); }
            else if (!now.Equals(was, StringComparison.OrdinalIgnoreCase)) problems.Add($"{was} changed to {now}");
            if (!pkg.ReadExportBytes(pkg.Exports[i]).AsSpan().SequenceEqual(back.ReadExportBytes(back.Exports[i]))) problems.Add($"{was}: data changed");
        }
        return output;
    }
}
