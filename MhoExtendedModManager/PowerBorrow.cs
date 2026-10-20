using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// A power's look borrowed from another hero (Kurt, 2026-10-10: "an Iceman base borrowing Human Torch's fireball attack",
/// visual only, client side only, only the borrowing hero changes). Evidence (--power-anims, --power-fx, --list-exports): a
/// power's look is its class default's effect components (PowerFxAnimation naming the animation, PowerFxParticle, PowerFxSound
/// …) in the power's own package, and a projectile's is its projectile class default's (ProjectileFxParticle …), in the
/// power's package or a projectile package of its own. The server only names the classes, so the target's packages keep their
/// file, classes, names and GUID; only which components their defaults list changes: the donor's, copied in under the
/// target's default (renamed borrowed_&lt;name&gt;, so no path is the donor's own). The target's old components stay, unused.
/// The donor's packages are only read.
/// </summary>
static class PowerBorrow
{
    /// <summary>
    /// The editor's borrows rebuilt into <paramref name="work"/> and put into the draft's packages (Kurt, 2026-10-10: the
    /// Powers tab). Every borrow starts clean: a power package from its stock original, the hero's base package (the borrowed
    /// animations) from the mod's own copy kept the first time (Model\borrow_base), else stock; borrows into the same package
    /// build on each other in turn. Packages a borrow built before and none builds now leave the mod, or go back to the mod's
    /// own copy. Run when the borrows change (the preview shows them) and at Save, before the power colors (which then start
    /// from these). Returns the files built (file → path).
    /// </summary>
    public static Dictionary<string, string> Rebuild(ModDraft draft, Mod? editing, ModLibrary lib, GameState game, Fx.GameData db, string work, string modelFolder, List<string> log)
    {
        var before = (editing?.Manifest.PowerBorrows ?? []).Concat(draft.PowerBorrows).SelectMany(e => e.Packages).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originals = new Originals(lib.DataFolder, game);
        string baseDir = Path.Combine(modelFolder, "borrow_base");
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // where a package's rebuild starts: the last borrow's output, the mod's own copy (kept), else the stock original
        string? Start(string f)
        {
            if (current.TryGetValue(f, out var c)) return c;
            string kept = Path.Combine(baseDir, f);
            if (File.Exists(kept)) return kept;
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k >= 0 && !before.Contains(f))
            {
                Directory.CreateDirectory(baseDir);
                File.Copy(draft.Packages[k].Source, kept, true);
                return kept;
            }
            return originals.Find(f) ?? (StockFiles.For(game.Cooked, f) is var st && File.Exists(st) ? st : null);
        }
        var fx = new Fx.FxGame(game.Cooked, []);
        Directory.CreateDirectory(work);
        foreach (var e in draft.PowerBorrows)
        {
            e.Packages = [];
            var plan = Plan(db, fx, e.Power, e.Donor, log);
            var tp = plan.FirstOrDefault(x => x.Role == "power");
            string th = HeroPackage(game.Cooked, e.Hero, e.Power, tp?.TargetClass, tp == null ? [] : AnimNames(tp.TargetFile, $"marvelgamecontent.default__{tp.TargetClass}".ToLowerInvariant()))
                ?? throw new InvalidDataException($"{e.Name}: its hero's package wasn't found");
            string dh = HeroPackage(game.Cooked, e.DonorHero, e.Donor, tp?.DonorClass, tp == null ? [] : AnimNames(tp.DonorFile, $"marvelgamecontent.default__{tp.DonorClass}".ToLowerInvariant()))
                ?? throw new InvalidDataException($"{e.DonorName}: its hero's package wasn't found");
            e.Hero = th; e.DonorHero = dh;
            string heroFile = $"UC__MarvelPlayer_{th}_SF.upk";
            string donorHero = StockFiles.For(game.Cooked, $"UC__MarvelPlayer_{dh}_SF.upk");
            // what the donor shows through its conditions (a ride's vehicle), spawned by the borrowed power instead
            var spawn = e.KeepTiming ? [] : ConditionAttachments(db, fx, e.Donor, donorHero, log);
            // a ground power's animations on a high-flying one: the game lifts the hero (Globals' HighFlyingHeight) and the
            // ground animations sit at the floor, so they are lowered by that much (Spider-Man riding Cyclops' bike floated
            // in Web-Swinging; Kurt, 2026-10-20)
            float lift = 0;
            if (!e.KeepTiming && Field(db, e.Power, "HighFlying", 'B') is 1 && Field(db, e.Donor, "HighFlying", 'B') is not 1
                && Field(db, "Globals/Globals.defaults", "HighFlyingHeight", 'D') is double hf && hf > 0)
            {
                lift = -(float)hf;
                log.Add($"anims: lowered {hf:0} (the target flies high, the donor's animations are on the ground)");
            }
            var built = Build(plan, Start(heroFile) ?? throw new InvalidDataException($"no {heroFile}"), $"MarvelPlayer_{th}", donorHero, log, e.Fit, e.KeepTiming, Start, game.Cooked, spawn, lift);
            foreach (var (f, bytes) in built)
            {
                string path = Path.Combine(work, f);
                File.WriteAllBytes(path, bytes);
                current[f] = path;
                e.Packages.Add(f);
            }
            log.Add($"{e.Name} ← {e.DonorName}: {string.Join(", ", e.Packages)}");
        }
        // the draft's packages: the built ones in; ones built before and not now back to the mod's own copy, or out
        foreach (var (f, path) in current)
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) draft.Packages[k] = (f, path); else draft.Packages.Add((f, path));
        }
        foreach (string f in before.Where(f => !current.ContainsKey(f)))
        {
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k < 0) continue;
            string kept = Path.Combine(baseDir, f);
            if (File.Exists(kept)) { draft.Packages[k] = (f, kept); log.Add($"{f}: back to the mod's own copy"); }
            else { draft.Packages.RemoveAt(k); log.Add($"{f}: out of the mod (no borrow needs it now)"); }
        }
        return current;
    }

    /// <summary>
    /// <paramref name="targetPackage"/> (bytes) with <paramref name="targetDefault"/>'s components replaced by copies of
    /// <paramref name="donorDefault"/>'s from <paramref name="donorPackage"/>; voice lines are left out (another hero's voice).
    /// Verified; throws InvalidDataException with the reason.
    /// </summary>
    public static byte[] Graft(byte[] targetPackage, string targetDefault, string donorPackage, string donorDefault, List<string> log, bool voice = false, string? cooked = null)
    {
        var dst = Package.FromBytes(targetPackage);
        var src = Package.Open(donorPackage);
        int sd = Find(src, donorDefault), td = Find(dst, targetDefault);
        if (sd < 0) throw new InvalidDataException($"no {donorDefault} in {Path.GetFileName(donorPackage)}");
        if (td < 0) throw new InvalidDataException($"no {targetDefault} in the target package");
        var donorComps = Components(src, sd, out _, out _);
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [src.PathOf(src.Exports[sd])] = dst.PathOf(dst.Exports[td]) };
        var refs = new List<int>();
        // (Tested in game 2026-10-10, Cosmic Haymaker on Daredevil: the donor's FxSetIndex sets and its PowerFxPhysicsWeight
        // copy as they are; the freeze was costumes with their own animation list, see Build.)
        foreach (int r in donorComps)
        {
            if (r <= 0) continue;
            var e = src.Exports[r - 1];
            string cls = src.ClassOf(e);
            if (!voice && cls.Equals("PowerFxSound", StringComparison.OrdinalIgnoreCase) && e.ObjectName.Contains("voice", StringComparison.OrdinalIgnoreCase))
            { log.Add($"  {e.ObjectName} ({cls}): left out (the donor hero's voice)"); continue; }
            string name = "borrowed_" + e.ObjectName;
            int existing = Find(dst, dst.PathOf(dst.Exports[td]) + "." + name);
            if (existing >= 0) { refs.Add(existing + 1); log.Add($"  {e.ObjectName} ({cls}): already in from an earlier borrow"); continue; }
            // (a, 0.60.7) a component whose settings come from a template in another class's package (Cyclops' ride: its anim
            // inherits the ride animations from powershared_ridebike's): that package isn't loaded for the target, so the
            // template is copied in under its own path and the copy points at it
            if (cooked != null && Template(src, r - 1) is string tpl && Find(dst, tpl) < 0 && TemplatePackage(cooked, tpl) is string tf)
            {
                var tp = Package.Open(tf);
                int ti = Find(tp, tpl);
                var tc = ti < 0 ? null : CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, []), out string tsaid);
                if (tc != null) { dst = Package.FromBytes(tc.Output); td = Find(dst, targetDefault); log.Add($"  {e.ObjectName}: its template {tpl} copied in (from {Path.GetFileName(tf)})"); }
                else log.Add($"  {e.ObjectName}: its template {tpl} couldn't be copied: its inherited settings won't show");
            }
            if (Template(src, r - 1) is string tpl2 && Find(dst, tpl2) >= 0) replace[tpl2] = tpl2;
            var copy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out string said);
            if (copy == null && (said.Contains("the target has no copy of it") || said.Contains("(compiled code) isn't copied yet")))
            { log.Add($"  {e.ObjectName} ({cls}): left out (its class is the donor package's own and couldn't be copied)"); continue; }
            copy = copy
                ?? throw new InvalidDataException($"{e.ObjectName} ({cls}) couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
            dst = Package.FromBytes(copy.Output);
            td = Find(dst, targetDefault);
            refs.Add(copy.RootRef);
            log.Add($"  {e.ObjectName} ({cls}) → {dst.PathOf(dst.Exports[copy.RootRef - 1])}");
        }
        // the target's default lists the copies (its own components stay in the package, unused)
        td = Find(dst, targetDefault);
        Components(dst, td, out var tag, out byte[] d);
        var value = new byte[4 + 4 * refs.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, refs.Count);
        for (int k = 0; k < refs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), refs[k]);
        byte[] nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        var rep = new Dictionary<int, Func<long, byte[]>> { [td] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the target's component list: " + string.Join("; ", problems.Take(3)));
        var back = Package.FromBytes(output);
        var now = Components(back, Find(back, targetDefault), out _, out _);
        if (!now.SequenceEqual(refs)) throw new InvalidDataException("the target's component list didn't read back");
        log.Add($"{targetDefault}: {refs.Count} component(s) from {donorDefault} (its own {donorComps.Count} listed; the target's old ones kept, unused)");
        return output;
    }

    /// <summary>One class default whose components become the donor's: the target's class and package, the donor's.</summary>
    public sealed record Pair(string Role, string TargetClass, string TargetFile, string DonorClass, string DonorFile);

    /// <summary>
    /// What a borrow changes, from the two powers' prototypes (Powers\Player\Iceman\RapidFire.prototype …): the target power's
    /// own class gets the donor power's components, and each of the target's projectile classes one of the donor's (in turn).
    /// What else the target sets off (an ice wall, a condition, a hotspot) stays its own: <paramref name="notes"/> says so.
    /// Files are the stock copies (FxGame: the class's package from AssetPackageCache).
    /// </summary>
    public static List<Pair> Plan(Fx.GameData db, Fx.FxGame game, string targetPower, string donorPower, List<string> notes)
    {
        var t = Fx.PowerClosure.Of(db, targetPower); var d = Fx.PowerClosure.Of(db, donorPower);
        if (t.Count == 0) throw new InvalidDataException($"no power {targetPower}");
        if (d.Count == 0) throw new InvalidDataException($"no power {donorPower}");
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // classes found in a power's own package
        string? File(string cls) => known.TryGetValue(cls, out var kf) ? kf : game.PackagesOf(cls).Select(game.FileFor).OfType<string>().Select(TrueCase)
            .FirstOrDefault(f => Find(Package.Open(f), $"marvelgamecontent.default__{cls}".ToLowerInvariant()) >= 0);
        // the file as the folder names it (the cache's names are lower case; the mod's package must keep the game's)
        static string TrueCase(string f) => Path.GetDirectoryName(f) is string dir && Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, Path.GetFileName(f)).FirstOrDefault() ?? f : f;
        if (Environment.GetEnvironmentVariable("MHO_BORROW_DEBUG") == "1")
            foreach (var (side, arts) in new[] { ("target", t), ("donor", d) })
                foreach (var x in arts) notes.Add($"closure {side}: {x.Class} ({x.Prototype}) depth {x.Depth}{(x.IsPower ? ", power" : "")}");
        var pairs = new List<Pair>();
        var tp = t.FirstOrDefault(x => x.IsPower && x.Depth == 0) ?? throw new InvalidDataException($"{targetPower} names no power class");
        var dp = d.FirstOrDefault(x => x.IsPower && x.Depth == 0) ?? throw new InvalidDataException($"{donorPower} names no power class");
        void Add(string role, string tc, string dc)
        {
            string tf = File(tc) ?? throw new InvalidDataException($"no package with {tc}");
            string df = File(dc) ?? throw new InvalidDataException($"no package with {dc}");
            pairs.Add(new Pair(role, tc, tf, dc, df));
        }
        Add("power", tp.Class, dp.Class);
        static bool Projectile(Fx.PowerClosure.Art a) => a.Class.StartsWith("MarvelProjectile", StringComparison.OrdinalIgnoreCase);
        // projectile classes the power's own package holds (Iceman's Rapid Fire keeps MarvelProjectile_IcemanRapidFire there, and
        // the game data reaches it some other way than the closure follows)
        List<Fx.PowerClosure.Art> Own(Fx.PowerClosure.Art power, List<Fx.PowerClosure.Art> closure)
        {
            var list = closure.Where(Projectile).ToList();
            if (File(power.Class) is not string pf) return list;
            var pk = Package.Open(pf);
            foreach (var e in pk.Exports)
                if (pk.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) && e.ObjectName.StartsWith("marvelprojectile", StringComparison.OrdinalIgnoreCase)
                    && !list.Any(x => x.Class.Equals(e.ObjectName, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(new Fx.PowerClosure.Art(power.Prototype, e.ObjectName, false) { Depth = 1 });
                    known[e.ObjectName] = pf;
                }
            return list;
        }
        var tProj = Own(tp, t); var dProj = Own(dp, d);
        for (int k = 0; k < tProj.Count; k++)
        {
            if (dProj.Count == 0) { notes.Add($"{tProj[k].Class}: the donor fires no projectile: this one keeps its own look"); continue; }
            Add("projectile", tProj[k].Class, dProj[k % dProj.Count].Class);
        }
        if (tProj.Count == 0 && dProj.Count > 0) notes.Add($"the donor's projectile ({string.Join(", ", dProj.Select(x => x.Class))}) has nothing to go on: the target fires none (its look won't show)");
        foreach (var other in t.Where(x => x != tp && !Projectile(x)))
            notes.Add($"{other.Class} ({Path.GetFileNameWithoutExtension(other.Prototype)}): set off by the target, keeps its own look");
        return pairs;
    }

    /// <summary>How a power is built, for matching a borrow (Kurt, 2026-10-19: Black Panther's Triple Throw on Captain America's
    /// Shield Strike showed the daggers in hand, but nothing flew: the server spawns projectiles from the power's own data, so a
    /// melee power never throws the donor's).</summary>
    public sealed record PowerKind(bool Projectile, bool Beam, bool Channeled, bool HighFlying = false)
    {
        /// <summary>A short label: "Fires Projectiles", "Beam", "Channeled", or "Melee / Area".</summary>
        public string Label => Projectile ? "Fires Projectiles" : Beam ? "Beam" : Channeled ? "Channeled" : "Melee / Area";
        public bool Matches(PowerKind o) => Projectile == o.Projectile && Beam == o.Beam;
    }

    static readonly Dictionary<string, PowerKind?> kinds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A power's kind, or null when its class or package isn't found (cached per power).</summary>
    public static PowerKind? KindOf(Fx.GameData db, Fx.FxGame game, string power)
    {
        lock (kinds) if (kinds.TryGetValue(power, out var hit)) return hit;
        PowerKind? kind = null;
        try
        {
            var arts = Fx.PowerClosure.Of(db, power);
            var pw = arts.FirstOrDefault(x => x.IsPower && x.Depth == 0);
            if (pw != null)
            {
                bool proj = arts.Any(a => a.Class.StartsWith("MarvelProjectile", StringComparison.OrdinalIgnoreCase));
                string? file = game.PackagesOf(pw.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), $"marvelgamecontent.default__{pw.Class}".ToLowerInvariant()) >= 0);
                bool beam = false, loop = false;
                if (file != null)
                {
                    var pk = Package.Open(file);
                    // (a projectile class the power's own package holds counts too: Iceman's Rapid Fire)
                    proj |= pk.Exports.Any(e => pk.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) && e.ObjectName.StartsWith("marvelprojectile", StringComparison.OrdinalIgnoreCase));
                    int d = Find(pk, $"marvelgamecontent.default__{pw.Class}".ToLowerInvariant());
                    if (d >= 0) foreach (int r in Components(pk, d, out _, out _))
                    {
                        if (r <= 0) continue;
                        string c = pk.ClassOf(pk.Exports[r - 1]);
                        beam |= c.StartsWith("PowerFxBeam", StringComparison.OrdinalIgnoreCase);
                        loop |= c.Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase);
                    }
                }
                kind = new PowerKind(proj, beam, loop, Field(db, power, "HighFlying", 'B') is 1);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or KeyNotFoundException or ArgumentException) { }
        lock (kinds) kinds[power] = kind;
        return kind;
    }

    /// <summary>What the player will see when the two powers are built differently, in words, or null when they match.</summary>
    public static string? Mismatch(PowerKind? target, PowerKind? donor, string targetName, string donorName)
    {
        if (target == null || donor == null) return null;
        var lines = new List<string>();
        // (Kurt, 2026-10-20: Cyclops' bike on Spider-Man's Web-Swinging sat right on flat ground, sank into ramps)
        if (target.HighFlying && !donor.HighFlying)
            lines.Add($"{targetName} flies and {donorName} is on the ground: its animations are lowered to the ground, but the game still moves the hero as a flyer, so on ramps and slopes it dips into the ground until it reaches the top.");
        if (target.Matches(donor)) return lines.Count == 0 ? null : string.Join(" ", lines);
        if (donor.Projectile && !target.Projectile)
            lines.Add($"{donorName} fires projectiles and {targetName} doesn't: you'll see its animation and what it holds, but nothing flies. The server decides what a power does, so the damage and range stay {targetName}'s.");
        if (target.Projectile && !donor.Projectile)
            lines.Add($"{targetName} fires projectiles and {donorName} doesn't: {targetName}'s projectiles keep their own look.");
        if (donor.Beam && !target.Beam)
            lines.Add($"{donorName} is a beam and {targetName} isn't: its beam has nothing to draw along, so it won't show.");
        if (target.Beam && !donor.Beam)
            lines.Add($"{targetName} is a beam and {donorName} isn't: the beam keeps its own look.");
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    /// <summary>
    /// The mode to suggest for borrowing <paramref name="donorPower"/>'s look onto <paramref name="targetPower"/>, and why: the
    /// whole power when they're built alike; the target's timing with the donor's look when the donor is channeled and the
    /// target isn't (its looping components switch on with a channel the target never starts: Torch's Incinerating Blast on
    /// Iceman's Chillblain showed nothing), or when only one of them fires a projectile (a fireball needs one to fly on).
    /// </summary>
    public static (bool KeepTiming, string Why) Suggest(Fx.GameData db, Fx.FxGame game, string targetPower, string donorPower)
    {
        var notes = new List<string>();
        List<Pair> plan;
        try { plan = Plan(db, game, targetPower, donorPower, notes); }
        catch (InvalidDataException ex) { return (true, ex.Message); }
        var power = plan.First(x => x.Role == "power");
        bool Looping(string file, string cls)
        {
            var pk = Package.Open(file);
            int d = Find(pk, $"marvelgamecontent.default__{cls}".ToLowerInvariant());
            return d >= 0 && Components(pk, d, out _, out _).Any(r => r > 0 && pk.ClassOf(pk.Exports[r - 1]).Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase));
        }
        bool dLoop = Looping(power.DonorFile, power.DonorClass), tLoop = Looping(power.TargetFile, power.TargetClass);
        if (dLoop && !tLoop) return (true, "the other power is channeled (its effects switch on with a channel this power doesn't have), so this power keeps its timing and takes only what the other's effects draw.");
        if (notes.Any(n => n.Contains("fires no projectile") || n.Contains("has nothing to go on")))
            return (true, "only one of the two fires a projectile, so this power keeps its timing and takes only what the other's effects draw.");
        return (false, "the two powers are built alike, so the whole power: its animation, effects and projectile look.");
    }

    /// <summary>
    /// What Keep This Power's Timing would change, in words, or null when nothing (the census: it changed nothing in 990 of
    /// the pairs, Kurt 2026-10-11): it only redraws the target's beams with the donor's beam systems (both need beams), and adds
    /// the donor's particles that start with the power when the target has no particle effects of its own (SwapLooks).
    /// </summary>
    public static string? KeepTimingTakes(Fx.GameData db, Fx.FxGame game, string targetPower, string donorPower)
    {
        List<Pair> plan;
        try { plan = Plan(db, game, targetPower, donorPower, []); }
        catch (InvalidDataException) { return null; }
        var power = plan.FirstOrDefault(x => x.Role == "power");
        if (power == null) return null;
        (int Beams, int Particles, int StartParticles) Count(string file, string cls)
        {
            var pk = Package.Open(file);
            int d = Find(pk, $"marvelgamecontent.default__{cls}".ToLowerInvariant());
            if (d < 0) return (0, 0, 0);
            int beams = 0, parts = 0, start = 0;
            foreach (int r in Components(pk, d, out _, out _))
            {
                if (r <= 0) continue;
                string c = pk.ClassOf(pk.Exports[r - 1]);
                if (c.StartsWith("PowerFxBeam", StringComparison.OrdinalIgnoreCase)) beams++;
                else if (c.Equals("PowerFxParticle", StringComparison.OrdinalIgnoreCase))
                {
                    parts++;
                    byte[] b = pk.ReadExportBytes(pk.Exports[r - 1]).ToArray();
                    string? at = TagWalker.Walk(pk, b, 16)?.FirstOrDefault(x => x.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase)) is { } t ? TagWalker.NameAt(pk, b, t.ValueAt) : null;
                    if (at == null || at.Equals("power_on_start", StringComparison.OrdinalIgnoreCase) || at.Contains("contact", StringComparison.OrdinalIgnoreCase)) start++;
                }
            }
            return (beams, parts, start);
        }
        try
        {
            var t = Count(power.TargetFile, power.TargetClass);
            var d = Count(power.DonorFile, power.DonorClass);
            var what = new List<string>();
            if (t.Beams > 0 && d.Beams > 0) what.Add($"its {t.Beams} beam(s) draw the other power's");
            if (t.Particles == 0 && d.StartParticles > 0) what.Add($"the other power's {d.StartParticles} effect(s) at the start are added");
            return what.Count == 0 ? null : string.Join("; ", what);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
    }

    /// <summary>
    /// Enemy powers that use one of the target power's classes (PowerUsage: every power prototype's Unreal classes): a borrow
    /// changes that class's package, so those enemies get the borrowed look too (Kurt, 2026-10-11: the Taskmaster boss threw
    /// Captain Carter's shield). Their prototype names (Powers\EnemyPowers\…), at most <paramref name="max"/>.
    /// </summary>
    public static List<string> EnemyUsers(Fx.GameData db, Fx.FxGame game, string gameRoot, string targetPower, string donorPower, int max = 4)
    {
        List<Pair> plan;
        try { plan = Plan(db, game, targetPower, donorPower, []); }
        catch (InvalidDataException) { return []; }
        var map = PowerUsage.Index(gameRoot);
        return plan.Select(p => p.TargetClass.ToLowerInvariant()).Distinct()
            .SelectMany(c => map.TryGetValue(c, out var users) ? users : [])
            .Where(u => u.StartsWith(@"Powers\EnemyPowers\", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>A hero's name from a power prototype's path (Powers\Player\&lt;Hero&gt;\…), else null.</summary>
    public static string? HeroOf(string power)
    {
        var parts = power.Replace('/', '\\').Split('\\');
        int i = Array.FindIndex(parts, x => x.Equals("Player", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length - 1 ? parts[i + 1] : null;
    }

    static readonly Dictionary<string, string?> heroByAnims = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The hero part of a power's hero base package (UC__MarvelPlayer_&lt;hero&gt;_SF): the one given, else the power class's
    /// last part (PowerDeathFromAbove_MsMarvel), else the prototype's folder, when that package exists; else the base package
    /// whose names hold the power's animations (Kurt, 2026-10-10: Captain Marvel's powers are in Powers\Player\CaptainMarvel,
    /// her package is UC__MarvelPlayer_MsMarvel_SF; names alone are a last resort: Beast has an absattack_deathfromabove_start too).
    /// </summary>
    public static string? HeroPackage(string cooked, string? given, string power, string? powerClass, List<string> anims)
    {
        bool Has(string? h) => h != null && File.Exists(StockFiles.For(cooked, $"UC__MarvelPlayer_{h}_SF.upk"));
        if (Has(given)) return given;
        string? byClass = powerClass?.Split('_') is { Length: > 1 } cp ? cp[^1] : null;
        if (Has(byClass)) return byClass;
        string? byFolder = HeroOf(power);
        if (Has(byFolder)) return byFolder;
        // travel powers sit in Powers\Player\TravelPower\ (0.60.7: Spider-Man's Web-Swinging found no hero): the class's first
        // part (powerspiderman_webswingingmvmt) or the prototype's name (SpidermanFlight, CyclopsRide) names the hero
        string? byClassHead = powerClass?.Split('_')[0] is { Length: > 5 } ch && ch.StartsWith("power", StringComparison.OrdinalIgnoreCase) ? ch[5..] : null;
        if (Has(byClassHead)) return byClassHead;
        string file = Path.GetFileNameWithoutExtension(power);
        string? byName = Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk").Select(f => Path.GetFileNameWithoutExtension(f).Split('_'))
            .Where(p => p.Length == 5).Select(p => p[3]).Where(h => file.StartsWith(h, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Length).FirstOrDefault();
        if (Has(byName)) return byName;
        if (anims.Count == 0) return null;
        string key = string.Join("|", anims.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        lock (heroByAnims) if (heroByAnims.TryGetValue(key, out var hit)) return hit;
        string? found = null;
        foreach (string f in Directory.EnumerateFiles(cooked, "UC__MarvelPlayer_*_SF.upk"))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            var parts = n.Split('_');
            if (parts.Length != 5 || n.Contains("bak", StringComparison.OrdinalIgnoreCase) || n.Contains("copy", StringComparison.OrdinalIgnoreCase)) continue;
            Package p;
            try { p = Package.Open(StockFiles.For(cooked, Path.GetFileName(f))); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            var names = p.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (anims.All(names.Contains)) { found = parts[3]; break; }
        }
        lock (heroByAnims) heroByAnims[key] = found;
        return found;
    }

    /// <summary>Whether a power's default lists a looping animation component (PowerFxAnimationLooping).</summary>
    static bool Loops(string file, string defaultPath)
    {
        try
        {
            var pk = Package.Open(file);
            int d = Find(pk, defaultPath);
            return d >= 0 && Components(pk, d, out _, out _).Any(r => r > 0 && pk.ClassOf(pk.Exports[r - 1]).Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return false; }
    }

    /// <summary>A prototype's field (its own, else its parents'): a bool as 0 / 1, a float as a double, an int as a long;
    /// null when no prototype on the way sets it.</summary>
    public static object? Field(Fx.GameData db, string path, string field, char type)
    {
        if (db.Find(path.Replace('/', '\\')) is not { } pe) return null;
        ulong id = pe.Id;
        for (int guard = 0; guard < 32 && id != 0; guard++)
        {
            var d = db.Prototype(id).Data;
            foreach (var gr in d.Groups)
                foreach (var f in gr.Simple)
                    if (f.Type == type && db.FieldName(gr.Blueprint, f.Id) == field)
                        return type switch { 'B' => (object)(f.Value.Raw != 0 ? 1 : 0), 'D' => BitConverter.Int64BitsToDouble((long)f.Value.Raw), _ => (long)f.Value.Raw };
            if (!d.HasParent) break;
            id = d.Parent;
        }
        return null;
    }

    /// <summary>A sequence's length in seconds in a hero package's animation sets (the character's: the set with the most bones
    /// holding that name, not a vehicle's), else 0.</summary>
    public static float Length(string heroPackage, string anim)
    {
        var ap = AnimExportCli.Packages.Package.Open(heroPackage);
        foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).OrderByDescending(x => x.TrackBoneNames.Count))
            foreach (var seq in set.Sequences.Where(q => q.IsExport))
                if (AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex).Equals(anim, StringComparison.OrdinalIgnoreCase))
                {
                    var pr = ap.TryReadProperties(seq.ExportIndex);
                    float len = pr?.GetFloat("SequenceLength") ?? 0, rate = pr != null && pr.Find("RateScale") != null ? pr.GetFloat("RateScale") : 1;
                    return rate > 0 ? len / rate : len;
                }
        return 0;
    }

    /// <summary>
    /// The whole borrow: the planned pairs grafted (per target package, in turn), and the donor power's animations put into the
    /// target hero's base package, each played at the rate that fits it to the length of the target's own power animation (Kurt,
    /// 2026-10-10: Torch's 0.6 s fireball on Iceman's 0.4 s Rapid Fire looked extremely rapid; fit = false keeps the donor's).
    /// Returns the changed packages by file name.
    /// </summary>
    public static Dictionary<string, byte[]> Build(List<Pair> pairs, string targetHeroPackage, string targetHeroClass, string donorHeroPackage, List<string> log, bool fit = true, bool keepTiming = false, Func<string, string?>? startFrom = null, string? cooked = null,
        IReadOnlyList<(string File, int Export, string Slot)>? spawn = null, float lift = 0)
    {
        MeshCopy.Register();   // (skeletal meshes copy too: a borrowed attachment class's model, Black Panther's daggers; 0.60.2)
        var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targetAnims = new List<string>(); var donorAnims = new List<string>();
        bool donorLoops = false;
        // another hero's attachments are spawned, not switched on (SpawnBorrowedAttachments)
        bool crossHero = !Path.GetFileNameWithoutExtension(donorHeroPackage).Equals($"UC__{targetHeroClass}_SF", StringComparison.OrdinalIgnoreCase);
        foreach (var pr in pairs)
        {
            string tf = Path.GetFileName(pr.TargetFile);
            byte[] cur = done.TryGetValue(tf, out var b) ? b : File.ReadAllBytes(startFrom?.Invoke(tf) ?? pr.TargetFile);
            string td = $"marvelgamecontent.default__{pr.TargetClass}".ToLowerInvariant(), dd = $"marvelgamecontent.default__{pr.DonorClass}".ToLowerInvariant();
            log.Add($"{pr.Role}: {pr.TargetClass} ({tf}) ← {pr.DonorClass} ({Path.GetFileName(pr.DonorFile)})");
            if (keepTiming && pr.Role == "power") { done[tf] = SwapLooks(cur, td, pr.DonorFile, dd, log); continue; }
            if (pr.Role == "power") { targetAnims.AddRange(AnimNames(pr.TargetFile, td, cooked)); donorAnims.AddRange(AnimNames(pr.DonorFile, dd, cooked)); donorLoops |= Loops(pr.DonorFile, dd); }
            done[tf] = Graft(cur, td, pr.DonorFile, dd, log, cooked: cooked);
            if (crossHero) done[tf] = SpawnBorrowedAttachments(done[tf], td, log);
            if (crossHero && pr.Role == "power" && spawn is { Count: > 0 } && cooked != null) done[tf] = SpawnAttachments(done[tf], td, spawn, cooked, log);
        }
        donorAnims = donorAnims.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keepTiming) { log.Add("anims: the target's own (its timing kept)"); return done; }
        if (donorAnims.Count == 0) { log.Add("anims: the donor power plays no animation of its own"); return done; }
        // a looping animation (a travel power's start / loop / end) plays as long as the power runs: fitted to the target's
        // length, Spider-Man's 6 s swing loop on Thor's flight played ×4.6 (Kurt, 2026-10-20: flips way too fast)
        if (fit && donorLoops) { fit = false; log.Add("anims: the donor's animation loops, so it keeps its own speed"); }
        // the target's own power animation: the length the borrowed ones are fitted to (its longest, when it has several)
        float targetLen = targetAnims.Select(a => Length(targetHeroPackage, a)).DefaultIfEmpty(0).Max();
        var ap = AnimExportCli.Packages.Package.Open(donorHeroPackage);
        var swaps = new List<AnimSwap.Swap>();
        foreach (string an in donorAnims)
        {
            // the character's set, not a vehicle's: Cyclops' bike has a set of its own (cyclops_bike_as, 22 chopper bones) with the
            // same sequence names as his riding ones in cyclops_as (89 bones): the set with the most bones is the rider's
            AnimSwap.Swap? found = null;
            int foundBones = -1;
            foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                foreach (var seq in set.Sequences.Where(q => q.IsExport))
                    if (set.TrackBoneNames.Count > foundBones && AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex).Equals(an, StringComparison.OrdinalIgnoreCase))
                    {
                        found = new AnimSwap.Swap(an, donorHeroPackage, set.ExportIndex, seq.ExportIndex);
                        foundBones = set.TrackBoneNames.Count;
                    }
            if (found == null) { log.Add($"anims: {an} not found in {Path.GetFileName(donorHeroPackage)}"); continue; }
            float donorLen = Length(donorHeroPackage, an);
            float rate = fit && targetLen > 0.01f && donorLen > 0.01f ? donorLen / targetLen : 1;
            log.Add($"anims: {an} {donorLen:0.##} s{(Math.Abs(rate - 1) > 0.01f ? $", played ×{rate:0.##} to last {targetLen:0.##} s like the target's {string.Join(" / ", targetAnims)}" : "")}");
            swaps.Add(found with { Rate = rate, Lift = lift });
        }
        if (swaps.Count > 0)
        {
            string hf = Path.GetFileName(targetHeroPackage);
            string heroPath = startFrom?.Invoke(hf) ?? targetHeroPackage;
            var temps = new List<string>();
            try
            {
            if (done.TryGetValue(hf, out var hb)) temps.Add(heroPath = WriteTemp(hf, hb));
            done[hf] = AnimSwap.Build(heroPath, targetHeroClass, swaps, log);
            log.Add($"anims: {string.Join(", ", swaps.Select(x => x.Slot))} added to {hf} ({targetHeroClass})");
            // costumes with an animation list of their own don't play the hero's: the borrowed sets are imported into theirs
            if (cooked != null)
            {
                var hp = Package.FromBytes(done[hf]);
                string suffix = "_on_" + targetHeroClass.ToLowerInvariant();
                var sets = hp.Exports.Where(x => hp.ClassOf(x).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) && x.ObjectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .Select(hp.PathOf).ToList();
                string hero = targetHeroClass["MarvelPlayer_".Length..];
                foreach (string cf in Directory.EnumerateFiles(cooked, $"UC__MarvelPlayer_{hero}_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                    .Where(f => !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase)))
                {
                    string start = done.TryGetValue(cf, out var cb) ? WriteTemp(cf, cb) : startFrom?.Invoke(cf) ?? StockFiles.For(cooked, cf);
                    if (done.ContainsKey(cf)) temps.Add(start);
                    if (!File.Exists(start)) continue;
                    if (AnimSwap.AddImportedSets(start, cf[4..^7], sets, log) is { } costume) done[cf] = costume;
                }
            }
            }
            finally { foreach (string t in temps) try { File.Delete(t); } catch (IOException) { } }
        }
        return done;
    }

    static string WriteTemp(string file, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"mhoextmm_borrow_{Guid.NewGuid():N}_{file}");   // unique: builds can run side by side (the census)
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Keep the target's timing, take the donor's look (Kurt, 2026-10-10: Torch's fire on Iceman's Chillblain): powers built
    /// differently can't swap components (Chillblain's beams fire at the hit; Torch's Incinerating Blast's switch on with a
    /// channel loop Chillblain never starts, so they'd never show). Here the target keeps its components (sockets, timing,
    /// animations, sounds) and only what they draw changes: each beam draws the donor's beam particle system, in turn; when the
    /// target has no particle effects of its own, the donor's that start with the power (hand glows …) are added. The donor's
    /// particle systems are copied in under their own paths (the same objects the donor's package has).
    /// </summary>
    public static byte[] SwapLooks(byte[] targetPackage, string targetDefault, string donorPackage, string donorDefault, List<string> log)
    {
        var dst = Package.FromBytes(targetPackage);
        var src = Package.Open(donorPackage);
        int sd = Find(src, donorDefault), td = Find(dst, targetDefault);
        if (sd < 0) throw new InvalidDataException($"no {donorDefault} in {Path.GetFileName(donorPackage)}");
        if (td < 0) throw new InvalidDataException($"no {targetDefault} in the target package");
        bool IsBeam(Package p, int r) => r > 0 && p.ClassOf(p.Exports[r - 1]).StartsWith("PowerFxBeam", StringComparison.OrdinalIgnoreCase);
        bool IsParticle(Package p, int r) => r > 0 && p.ClassOf(p.Exports[r - 1]).Equals("PowerFxParticle", StringComparison.OrdinalIgnoreCase);
        int Ref(Package p, int comp, string prop)
        {
            byte[] b = p.ReadExportBytes(p.Exports[comp - 1]).ToArray();
            var t = TagWalker.Walk(p, b, 16)?.FirstOrDefault(x => x.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && x.Size == 4);
            return t == null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
        }
        string? Point(Package p, int comp)
        {
            byte[] b = p.ReadExportBytes(p.Exports[comp - 1]).ToArray();
            var t = TagWalker.Walk(p, b, 16)?.FirstOrDefault(x => x.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase));
            return t == null ? null : TagWalker.NameAt(p, b, t.ValueAt);
        }
        var donorComps = Components(src, sd, out _, out _);
        var donorBeams = donorComps.Where(r => IsBeam(src, r)).Select(r => Ref(src, r, "BeamParticleSystemTemplate")).Where(x => x > 0).Distinct().ToList();
        var donorImpacts = donorComps.Where(r => IsBeam(src, r)).Select(r => Ref(src, r, "ImpactParticleSystemTemplate")).Where(x => x > 0).Distinct().ToList();
        // the donor's systems copied in (each once; the same path as in the donor's package)
        var copied = new Dictionary<int, int>();
        int Bring(int sysRef)
        {
            if (copied.TryGetValue(sysRef, out int have)) return have;
            var c = CrossMove.Quiet(() => ExportCopy.Copy(src, sysRef - 1, dst, []), out string said)
                ?? throw new InvalidDataException($"{src.PathOf(src.Exports[sysRef - 1])} couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(c.Output);
            return copied[sysRef] = c.RootRef;
        }
        var targetComps = Components(dst, td, out _, out _);
        var targetBeams = targetComps.Where(r => IsBeam(dst, r)).ToList();
        var newRef = new Dictionary<int, (int Beam, int Impact)>();   // target beam component → the donor systems it now draws
        if (donorBeams.Count == 0 && targetBeams.Count > 0) log.Add("  the donor has no beam: the target's beams keep their own look");
        else
            for (int k = 0; k < targetBeams.Count; k++)
                newRef[targetBeams[k]] = (Bring(donorBeams[k % donorBeams.Count]), donorImpacts.Count > 0 ? Bring(donorImpacts[k % donorImpacts.Count]) : 0);
        // the donor's particles that start with the power, when the target has none of its own
        var added = new List<int>();
        if (!targetComps.Any(r => IsParticle(dst, r)))
        {
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [src.PathOf(src.Exports[sd])] = dst.PathOf(dst.Exports[td]) };
            foreach (int r in donorComps.Where(r => IsParticle(src, r)))
            {
                string? at = Point(src, r);
                if (at != null && !at.Equals("power_on_start", StringComparison.OrdinalIgnoreCase) && !at.Contains("contact", StringComparison.OrdinalIgnoreCase)) { log.Add($"  {src.Exports[r - 1].ObjectName}: starts at {at} (left out: the target may never reach it)"); continue; }
                var c = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], "borrowed_" + src.Exports[r - 1].ObjectName, replace), out string said)
                    ?? throw new InvalidDataException($"{src.Exports[r - 1].ObjectName} couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
                dst = Package.FromBytes(c.Output);
                added.Add(c.RootRef);
                log.Add($"  {src.Exports[r - 1].ObjectName} (powerfxparticle) added");
            }
        }
        // the beams point at the donor's systems; the default lists the added particles too
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var (comp, sys) in newRef)
        {
            byte[] b = dst.ReadExportBytes(dst.Exports[comp - 1]).ToArray();
            var tags = TagWalker.Walk(dst, b, 16) ?? throw new InvalidDataException($"{dst.PathOf(dst.Exports[comp - 1])}: its properties don't read");
            var t = tags.FirstOrDefault(x => x.Name.Equals("BeamParticleSystemTemplate", StringComparison.OrdinalIgnoreCase) && x.Size == 4)
                    ?? throw new InvalidDataException($"{dst.PathOf(dst.Exports[comp - 1])} names no beam system");
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.ValueAt), sys.Beam);
            if (sys.Impact > 0 && tags.FirstOrDefault(x => x.Name.Equals("ImpactParticleSystemTemplate", StringComparison.OrdinalIgnoreCase) && x.Size == 4) is { } it)
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(it.ValueAt), sys.Impact);
            // a borrowed beam can loop (Torch's channeled flame beam: it runs until his channel ends; Chillblain has none, so it
            // stayed in the world, Kurt 2026-10-10): it stops emitting when the power ends, as the donor's own does
            if (tags.FirstOrDefault(x => x.Name.Equals("StopEmittingOnEnd", StringComparison.OrdinalIgnoreCase)) is { } se) b[se.ValueAt - 1] = 1;
            else
            {
                var tag = new byte[25];   // name, type, size 0, array index 0, the value
                BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("StopEmittingOnEnd"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("BoolProperty"));
                tag[24] = 1;
                b = [.. b.AsSpan(0, tags.NoneAt), .. tag, .. b.AsSpan(tags.NoneAt)];
            }
            byte[] nb = b;
            rep[comp - 1] = _ => nb;
            log.Add($"  {dst.Exports[comp - 1].ObjectName}: draws {dst.PathOf(dst.Exports[sys.Beam - 1])}{(sys.Impact > 0 ? $", impact {dst.PathOf(dst.Exports[sys.Impact - 1])}" : "")}, stops emitting at the power's end");
        }
        if (added.Count > 0)
        {
            td = Find(dst, targetDefault);
            var list = Components(dst, td, out var tag, out byte[] d);
            list.AddRange(added);
            var value = new byte[4 + 4 * list.Count];
            BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
            for (int k = 0; k < list.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), list[k]);
            byte[] nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.ValueAt + tag.Size)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
            rep[td] = _ => nd;
        }
        if (rep.Count == 0) { log.Add($"{targetDefault}: nothing of the donor's fits (no beams or particles to take)"); return dst.RawFile; }
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the target's components: " + string.Join("; ", problems.Take(3)));
        log.Add($"{targetDefault}: keeps its timing; {newRef.Count} beam(s) draw the donor's, {added.Count} particle effect(s) added");
        return output;
    }

    static int Find(Package p, string path) => Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A class default's Components list (object refs), its tag and the export's bytes.</summary>
    /// <summary>
    /// A borrow from another hero (Kurt, 2026-10-19: Black Widow's Snap Shot borrowing Black Panther's Triple Throw showed her
    /// batons, not the daggers): a borrowed attachment component that switches on attachments by class
    /// (AttachmentsToShowOnActivate) only switches on ones the character already has, and one that shows a weapon slot
    /// (WeaponSlotToShowOnActivate "bothhands") shows the target's own weapons in that slot. Each such component becomes one
    /// component per attachment class with WhileActivatedAttachmentClass (the game spawns that class for the power's length,
    /// as Ant-Man's flying-ant outro does), and the slot it showed is hidden instead (WhileActivatedHideWeaponSlot, as
    /// Angela's bladed bow hides her sword), so the target's own weapons don't show. Components that only show a slot are
    /// turned into hiding it (the donor's weapon isn't the target's to show).
    /// </summary>
    public static byte[] SpawnBorrowedAttachments(byte[] package, string targetDefault, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, targetDefault);
        if (td < 0) return package;
        var comps = Components(p, td, out _, out _);
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var add = new List<NewExport>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] Tag(string name, string type, byte[] value)
        {
            var t = new byte[24 + value.Length];
            BinaryPrimitives.WriteInt32LittleEndian(t, NameIdx(name));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), NameIdx(type));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), value.Length);
            value.CopyTo(t, 24);
            return t;
        }
        byte[] NameValue(string n) { var v = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(v, NameIdx(n)); return v; }
        var newList = new List<int>(comps);
        foreach (int r in comps)
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;
            var e = p.Exports[r - 1];
            byte[] d = p.ReadExportBytes(e).ToArray();
            var tags = TagWalker.Walk(p, d, 16);
            if (tags == null) continue;
            var show = tags.FirstOrDefault(t => t.Name.Equals("AttachmentsToShowOnActivate", StringComparison.OrdinalIgnoreCase));
            var slot = tags.FirstOrDefault(t => t.Name.Equals("WeaponSlotToShowOnActivate", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("WhileActivatedShowWeaponSlot", StringComparison.OrdinalIgnoreCase));
            if (show == null && slot == null) continue;
            var classes = new List<int>();
            if (show != null)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(show.ValueAt));
                for (int k = 0; k < n; k++) { int c = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(show.ValueAt + 4 + 4 * k)); if (c != 0) classes.Add(c); }
            }
            string? slotName = slot != null ? TagWalker.NameAt(p, d, slot.ValueAt) : null;
            // the component's other properties (timing, conditions) stay; the show / hide lists and slots go
            string[] drop = ["AttachmentsToShowOnActivate", "AttachmentsToHideOnActivate", "WeaponSlotToShowOnActivate", "WhileActivatedShowWeaponSlot", "WhileActivatedAttachmentClass", "WhileActivatedHideWeaponSlot"];
            byte[] head = d[..16];
            var keep = new MemoryStream();
            foreach (var t in tags) if (!drop.Any(x => t.Name.Equals(x, StringComparison.OrdinalIgnoreCase))) keep.Write(d, t.Start, t.End - t.Start);
            byte[] kept = keep.ToArray(), tail = d[(tags.NoneAt + 8)..];
            byte[] Build(int cls, bool hide)
            {
                var m = new MemoryStream();
                m.Write(head); m.Write(kept);
                if (cls != 0) { var v = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(v, cls); m.Write(Tag("WhileActivatedAttachmentClass", "ObjectProperty", v)); }
                if (hide && slotName != null && !slotName.Equals("None", StringComparison.OrdinalIgnoreCase)) m.Write(Tag("WhileActivatedHideWeaponSlot", "NameProperty", NameValue(slotName)));
                m.Write(NameValue("None")); m.Write(tail);
                return m.ToArray();
            }
            // the first class (or none: only the slot hidden) in this component, the others in copies of it
            byte[] first = Build(classes.Count > 0 ? classes[0] : 0, true);
            replace[r - 1] = _ => first;
            for (int k = 1; k < classes.Count; k++)
            {
                byte[] entry = p.Body.AsSpan(p.ExportEntryStart[r - 1], p.ExportEntryEnd[r - 1] - p.ExportEntryStart[r - 1]).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), NameIdx($"{e.ObjectName}_{k + 1}"));
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 0);
                byte[] more = Build(classes[k], false);
                add.Add(new NewExport(r - 1, 0, _ => more) { Entry = entry });
                newList.Add(p.Exports.Length + add.Count);
            }
            log.Add($"  {e.ObjectName}: spawns {(classes.Count == 0 ? "nothing" : string.Join(", ", classes.Select(c => p.PathOf(p.Exports[c - 1]).Split('.')[^1])))} while the power runs{(slotName != null ? $", hides this hero's {slotName} weapons" : "")} (another hero's attachments)");
        }
        if (replace.Count == 0) return package;
        foreach (string n in new[] { "WhileActivatedAttachmentClass", "ObjectProperty", "WhileActivatedHideWeaponSlot", "NameProperty", "None" }) NameIdx(n);
        if (newList.Count != comps.Count)
        {
            Components(p, td, out var tag, out byte[] dd);
            var value = new byte[4 + 4 * newList.Count];
            BinaryPrimitives.WriteInt32LittleEndian(value, newList.Count);
            for (int k = 0; k < newList.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), newList[k]);
            byte[] nd = [.. dd.AsSpan(0, tag.ValueAt), .. value, .. dd.AsSpan(tag.ValueAt + tag.Size)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
            replace[td] = _ => nd;
        }
        byte[] output = PackageRebuilder.Rebuild(p, replace, add, out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, replace.Keys.ToList(), add, written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the borrowed attachments: " + string.Join("; ", problems.Take(3)));
        var back = Package.FromBytes(output);
        if (!Components(back, Find(back, targetDefault), out _, out _).SequenceEqual(newList)) throw new InvalidDataException("the borrowed attachments: the component list didn't read back");
        return output;
    }

    /// <summary>
    /// (b, 0.60.7) What a power shows through its conditions (the server applies them, so a borrow can't): a ride's vehicle.
    /// Cyclops' ride sets off MarvelConditionEffect_CyclopsRideBike, whose motorcycle_attach (by its template, the shared
    /// ride condition's) shows weapon slot "bike", where Cyclops keeps marvelattachmentanimated_cyclopsbike. Returns the
    /// donor hero's attachment classes in the slots the donor's conditions show: (hero package, class export).
    /// The power's own components count too (Kurt, 2026-10-20: Spider-Man's Web-Swinging on Thor showed no webbing: its
    /// showlefttendrils / showrighttendrils switch on weapon slots weblefthand / webrighthand, where Spider-Man keeps his
    /// webbing_lt / webbing_rt, which Thor doesn't have).
    /// </summary>
    public static List<(string File, int Export, string Slot)> ConditionAttachments(Fx.GameData db, Fx.FxGame game, string donorPower, string donorHeroPackage, List<string> log)
    {
        var slots = new List<string>();
        foreach (var a in Fx.PowerClosure.Of(db, donorPower).Where(a => a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase) || a.IsPower && a.Depth == 0))
        {
            string? file = game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), $"marvelgamecontent.default__{a.Class}".ToLowerInvariant()) >= 0);
            if (file == null) continue;
            var p = Package.Open(file);
            List<int> comps;
            try { comps = Components(p, Find(p, $"marvelgamecontent.default__{a.Class}".ToLowerInvariant()), out _, out _); }
            catch (InvalidDataException) { continue; }
            foreach (int r in comps)
            {
                if (r <= 0 || !(p.ClassOf(p.Exports[r - 1]).Equals("ConditionFxMeshAttachment", StringComparison.OrdinalIgnoreCase) || p.ClassOf(p.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase))) continue;
                string? slot = ShownSlot(p, r - 1);
                if (slot == null && Template(p, r - 1) is string tpl && TemplatePackage(game.Cooked, tpl) is string tf)
                {
                    var tp = Package.Open(tf);
                    int ti = Find(tp, tpl);
                    if (ti >= 0) slot = ShownSlot(tp, ti);
                }
                if (slot != null && !slots.Contains(slot, StringComparer.OrdinalIgnoreCase)) slots.Add(slot);
            }
        }
        var found = new List<(string, int, string)>();
        if (slots.Count == 0) return found;
        var hp = Package.Open(donorHeroPackage);
        for (int i = 0; i < hp.Exports.Length; i++)
        {
            var e = hp.Exports[i];
            if (!hp.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
            int d = Find(hp, "marvelgamecontent.default__" + e.ObjectName);
            if (d < 0) continue;
            byte[] b = hp.ReadExportBytes(hp.Exports[d]).ToArray();
            var ws = TagWalker.Walk(hp, b, 4)?.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase));
            if (ws == null) continue;
            int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ws.ValueAt));
            for (int k = 0; k < n; k++)
            {
                string s = TagWalker.NameAt(hp, b, ws.ValueAt + 4 + 8 * k);
                if (slots.Contains(s, StringComparer.OrdinalIgnoreCase)) { found.Add((donorHeroPackage, i, s)); break; }
            }
        }
        foreach (var (_, i, s) in found) log.Add($"  the donor shows weapon slot {s}: {hp.Exports[i].ObjectName} (spawned while the power runs)");
        return found;
    }

    /// <summary>The weapon slot a mesh attachment component shows (WhileActivatedShowWeaponSlot / WeaponSlotToShowOnActivate), its own; else null.</summary>
    static string? ShownSlot(Package p, int export)
    {
        byte[] b = p.ReadExportBytes(p.Exports[export]).ToArray();
        var t = TagWalker.Walk(p, b, 16)?.FirstOrDefault(x => x.Name.Equals("WhileActivatedShowWeaponSlot", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("WeaponSlotToShowOnActivate", StringComparison.OrdinalIgnoreCase));
        return t == null ? null : TagWalker.NameAt(p, b, t.ValueAt);
    }

    /// <summary>
    /// The attachment classes copied into the target power's package, each spawned by a component of its own while the power
    /// runs (WhileActivatedAttachmentClass). The component is a copy of a stock one (Ant-Man's ant_outro, the same form) with
    /// its class pointed at the copy.
    /// </summary>
    public static byte[] SpawnAttachments(byte[] package, string targetDefault, IReadOnlyList<(string File, int Export, string Slot)> classes, string cooked, List<string> log)
    {
        var dst = Package.FromBytes(package);
        string tplFile = StockFiles.For(cooked, "UC__PowerAntMan_AvatarSwapIn_SF.upk");
        const string tplDefault = "marvelgamecontent.default__powerantman_avatarswapin", tplComp = tplDefault + ".ant_outro";
        if (!File.Exists(tplFile)) { log.Add("  the spawn component's template package isn't found: vehicles left out"); return package; }
        var tp = Package.Open(tplFile);
        int ti = Find(tp, tplComp);
        if (ti < 0) { log.Add("  the spawn component's template isn't found: vehicles left out"); return package; }
        int k = 0;
        foreach (var (file, export, slot) in classes)
        {
            var hp = Package.Open(file);
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(hp, export, dst, []), out string csaid)
                ?? throw new InvalidDataException($"{hp.Exports[export].ObjectName} couldn't be copied: " + string.Join(" ", csaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(cc.Output);
            int classRef = cc.RootRef;
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplDefault] = targetDefault };
            string name = $"borrowed_{slot}_spawn{(k++ == 0 ? "" : k.ToString())}";
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, ["whileactivatedattachmentclass"], name, replace), out string ssaid)
                ?? throw new InvalidDataException($"the spawn component couldn't be copied: " + string.Join(" ", ssaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(sc.Output);
            // its class: the copied attachment; the default lists it
            int comp = sc.RootRef - 1, td = Find(dst, targetDefault);
            byte[] cd = dst.ReadExportBytes(dst.Exports[comp]).ToArray();
            var ct = TagWalker.Walk(dst, cd, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("the spawn component has no WhileActivatedAttachmentClass");
            BinaryPrimitives.WriteInt32LittleEndian(cd.AsSpan(ct.ValueAt), classRef);
            // its timing: the donor component that showed this slot (now hiding it here) (Kurt, 2026-10-20: Spider-Man's
            // webbing on Thor showed at the start and was gone: its showlefttendrils runs from the loop's start to its end)
            string timing = "";
            foreach (int r in Components(dst, td, out _, out _))
            {
                if (r <= 0 || r - 1 == comp || !dst.ClassOf(dst.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] od = dst.ReadExportBytes(dst.Exports[r - 1]).ToArray();
                var ot = TagWalker.Walk(dst, od, 16);
                if (ot?.FirstOrDefault(t => t.Name.Equals("WhileActivatedHideWeaponSlot", StringComparison.OrdinalIgnoreCase)) is not { } hs
                    || !TagWalker.NameAt(dst, od, hs.ValueAt).Equals(slot, StringComparison.OrdinalIgnoreCase)) continue;
                var keep = ot.Where(t => t.Name is var n && (n.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) || n.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("ActivationOffset", StringComparison.OrdinalIgnoreCase) || n.Equals("DeactivationOffset", StringComparison.OrdinalIgnoreCase))).ToList();
                if (keep.Count == 0) break;
                var ctags = TagWalker.Walk(dst, cd, 16)!;
                byte[] add = [.. keep.SelectMany(t => od[t.Start..t.End])];
                cd = [.. cd.AsSpan(0, ctags.NoneAt), .. add, .. cd.AsSpan(ctags.NoneAt)];
                timing = $", timed like {dst.Exports[r - 1].ObjectName} ({string.Join(", ", keep.Select(t => t.Name.ToLowerInvariant()))})";
                break;
            }
            var list = Components(dst, td, out var tag, out byte[] dd);
            list.Add(comp + 1);
            var value = new byte[4 + 4 * list.Count];
            BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
            for (int j = 0; j < list.Count; j++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * j), list[j]);
            byte[] nd = [.. dd.AsSpan(0, tag.ValueAt), .. value, .. dd.AsSpan(tag.ValueAt + tag.Size)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
            var rep = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => cd, [td] = _ => nd };
            byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
            var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
            if (problems.Count > 0) throw new InvalidDataException("the spawned attachment: " + string.Join("; ", problems.Take(3)));
            dst = Package.FromBytes(output);
            log.Add($"  {name}: spawns {dst.PathOf(dst.Exports[classRef - 1])} while the power runs (the donor's {slot}){timing}");
        }
        return dst.RawFile;
    }

    /// <summary>An export's archetype when it's a component template in another class's default (an import
    /// marvelgamecontent.default__&lt;class&gt;.&lt;component&gt;), as a path; else null.</summary>
    static string? Template(Package p, int export)
    {
        int arch = BinaryPrimitives.ReadInt32LittleEndian(p.Body.AsSpan(p.ExportEntryStart[export] + 20));
        if (arch >= 0) return null;
        var parts = new List<string>();
        for (int r = arch, guard = 0; r != 0 && guard < 16; guard++)
        {
            if (r > 0) { parts.Insert(0, p.PathOf(p.Exports[r - 1])); break; }
            var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex;
        }
        string path = string.Join(".", parts);
        return path.StartsWith("marvelgamecontent.default__", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '.') == 2 ? path : null;
    }

    /// <summary>The stock package defining a template's class (marvelgamecontent.default__powershared_ridebike.anim →
    /// UC__PowerShared_RideBike_SF.upk), when it has that export; else null.</summary>
    static string? TemplatePackage(string cooked, string templatePath)
    {
        string cls = templatePath.Split('.')[1]["default__".Length..];
        string? file = Directory.EnumerateFiles(cooked, $"UC__{cls}_SF.upk").FirstOrDefault();
        if (file == null) return null;
        string stock = StockFiles.For(cooked, Path.GetFileName(file));
        try { return File.Exists(stock) && Find(Package.Open(stock), templatePath) >= 0 ? stock : null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
    }

    static List<int> Components(Package p, int export, out TagWalker.Tag tag, out byte[] data)
    {
        data = p.ReadExportBytes(p.Exports[export]).ToArray();
        var tags = TagWalker.Walk(p, data, 4) ?? throw new InvalidDataException($"{p.PathOf(p.Exports[export])}: its properties don't read");
        tag = tags.FirstOrDefault(t => t.Name.Equals("Components", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidDataException($"{p.PathOf(p.Exports[export])} has no Components list");
        int n = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tag.ValueAt));
        var list = new List<int>();
        for (int k = 0; k < n; k++) list.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(tag.ValueAt + 4 + 4 * k)));
        return list;
    }

    /// <summary>The animation names a class default's PowerFxAnimation components play (their AnimName).</summary>
    public static List<string> AnimNames(string package, string defaultPath, string? cooked = null)
    {
        var p = Package.Open(package);
        int d = Find(p, defaultPath);
        if (d < 0) return [];
        var names = new List<string>();
        // AnimName, and a looping one's PowerAnims list (a ride's start, loop, end: 0.60.7)
        bool Read(Package q, int export)
        {
            byte[] b = q.ReadExportBytes(q.Exports[export]).ToArray();
            bool any = false;
            foreach (var t in TagWalker.Walk(q, b, 16) ?? [])
            {
                if (t.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase)) { Add(TagWalker.NameAt(q, b, t.ValueAt)); any = true; }
                else if (t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
                {
                    int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
                    for (int k = 0; k < n; k++) Add(TagWalker.NameAt(q, b, t.ValueAt + 4 + 8 * k));
                    any = true;
                }
            }
            return any;
        }
        void Add(string n) { if (!n.Equals("None", StringComparison.OrdinalIgnoreCase) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n); }
        foreach (int r in Components(p, d, out _, out _))
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
            // its own, else its template's (Cyclops' ride anim inherits powershared_ridebike's)
            if (Read(p, r - 1) || cooked == null || Template(p, r - 1) is not string tpl || TemplatePackage(cooked, tpl) is not string tf) continue;
            var tp = Package.Open(tf);
            int ti = Find(tp, tpl);
            if (ti >= 0) Read(tp, ti);
        }
        return names;
    }
}
