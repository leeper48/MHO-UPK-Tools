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
static partial class PowerBorrow
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
            // a travel power (Kurt, 2026-10-21: what Thor's six travel tests settled goes to every travel borrow): the other
            // hero's always-ridden mount (Green Goblin's glider) comes along, and the hero's own cape moves (AddOwnTracks)
            bool travel = e.Power.Contains(@"\TravelPower\", StringComparison.OrdinalIgnoreCase);
            if (!e.KeepTiming && travel) spawn.AddRange(MountAttachments(donorHero, log));
            // the other hero's always-held weapons (Thor's hammer in his flight): its animations hold them (Kurt, 2026-10-21)
            if (!e.KeepTiming && !th.Equals(dh, StringComparison.OrdinalIgnoreCase))
                foreach (var w in HeldAttachments(donorHero, log, HeroBones(StockFiles.For(game.Cooked, heroFile))))
                    if (!spawn.Any(x => x.File.Equals(w.File, StringComparison.OrdinalIgnoreCase) && x.Export == w.Export)) spawn.Add(w);
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
            var rider = new AnimSwap.RiderFit(e.RiderForward, e.RiderUp, e.RiderLean);
            // (for the Powers tab's Vehicle Fit: shown once a vehicle comes with the borrow)
            bool crossHeroNow = !th.Equals(dh, StringComparison.OrdinalIgnoreCase);
            e.VehicleAuto = crossHeroNow && spawn.Any(x => x.Vehicle) && HeroHeight(StockFiles.For(game.Cooked, heroFile)) is float hh && HeroHeight(donorHero) is float dhh && dhh > 1 ? hh / dhh : null;
            var spawned = new List<SpawnedProp>();
            // the hero's costumes built as characters of their own (Kate Bishop for Hawkeye): the power stays stock on them
            var ownClass = e.KeepTiming ? [] : OwnClassCostumes(game.Root, th);
            if (ownClass.Count > 0) log.Add($"stock look kept for {string.Join(", ", ownClass)} (costumes of their own class)");
            var built = Build(plan, Start(heroFile) ?? throw new InvalidDataException($"no {heroFile}"), $"MarvelPlayer_{th}", donorHero, log, e.Fit, e.KeepTiming, Start, game.Cooked, spawn, lift,
                e.VehicleScale, spawn.Any(x => x.Vehicle) && !rider.IsNone ? rider : null, ownTracks: travel, props: e.Props, spawnedOut: spawned, ownClassCostumes: ownClass, keepWeapons: e.KeepWeapons,
                held: (draft.AddedProps.Select(a => a.Class).ToHashSet(StringComparer.OrdinalIgnoreCase), draft.AddedProps.Where(a => a.Slot != null).Select(a => a.Slot!).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                heldBy: [.. draft.AddedProps.Select(a => CostumeClassOf(a.Package)).Distinct()]);
            e.Spawned = spawned;
            // the hero's own ride (Hawkeye's Sky-Cycle): shown by the travel power's condition and hidden again by the power's
            // own components, which the borrow replaces, so it stayed under him after the power ended (Kurt, 2026-10-22):
            // the condition's vehicle attachments go
            if (!e.KeepTiming && travel)
                foreach (var (f, bytes) in DropConditionVehicles(db, fx, e.Power, Start, log, ownClass)) built[f] = bytes;
            foreach (var (f, bytes) in built)
            {
                string path = Path.Combine(work, f);
                File.WriteAllBytes(path, bytes);
                current[f] = path;
                e.Packages.Add(f);
            }
            log.Add($"{e.Name} ← {e.DonorName}: {string.Join(", ", e.Packages)}");
        }
        // the draft's packages: the built ones in; ones built before and not now back to the mod's own copy, or out (one
        // build at a time: the Powers tab's background build and Save's could both add the same package)
        lock (draft.Packages)
        {
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
        IReadOnlyList<(string File, int Export, string Slot, bool Vehicle)>? spawn = null, float lift = 0, float? vehicleScale = null, AnimSwap.RiderFit? rider = null, bool ownTracks = false,
        IReadOnlyList<PropFit>? props = null, List<SpawnedProp>? spawnedOut = null, IReadOnlyList<string>? ownClassCostumes = null, bool keepWeapons = false,
        (IReadOnlySet<string> Classes, IReadOnlySet<string> Slots)? held = null, IReadOnlyList<string>? heldBy = null)
    {
        MeshCopy.Register();   // (skeletal meshes copy too: a borrowed attachment class's model, Black Panther's daggers; 0.60.2)
        var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targetAnims = new List<string>(); var donorAnims = new List<string>();
        bool donorLoops = false;
        string? powerFile = null;
        var spawnedHere = spawnedOut ?? [];   // (the props each pair brings: Pivot on the Hand needs their bones)
        // another hero's attachments are spawned, not switched on (SpawnBorrowedAttachments)
        bool crossHero = !Path.GetFileNameWithoutExtension(donorHeroPackage).Equals($"UC__{targetHeroClass}_SF", StringComparison.OrdinalIgnoreCase);
        foreach (var pr in pairs)
        {
            string tf = Path.GetFileName(pr.TargetFile);
            byte[] cur = done.TryGetValue(tf, out var b) ? b : File.ReadAllBytes(startFrom?.Invoke(tf) ?? pr.TargetFile);
            string td = $"marvelgamecontent.default__{pr.TargetClass}".ToLowerInvariant(), dd = $"marvelgamecontent.default__{pr.DonorClass}".ToLowerInvariant();
            log.Add($"{pr.Role}: {pr.TargetClass} ({tf}) ← {pr.DonorClass} ({Path.GetFileName(pr.DonorFile)})");
            if (keepTiming && pr.Role == "power") { done[tf] = SwapLooks(cur, td, pr.DonorFile, dd, log); continue; }
            if (pr.Role == "power") { targetAnims.AddRange(AnimNames(pr.TargetFile, td, cooked)); donorAnims.AddRange(AnimNames(pr.DonorFile, dd, cooked)); donorLoops |= Loops(pr.DonorFile, dd); powerFile = tf; }
            done[tf] = Graft(cur, td, pr.DonorFile, dd, log, cooked: cooked);
            if (pr.Role == "projectile") done[tf] = ProjectileSlots(done[tf], td, pr.DonorFile, dd, log);
            if (crossHero) done[tf] = SpawnBorrowedAttachments(done[tf], td, log, held);
            // a spawned vehicle sized for this hero: the Vehicle Fit's size, else the two heroes' heights (Kurt, 2026-10-20:
            // Cyclops' bike looked small under Thor; x1.14 by the heights looked right in game)
            float vscale = vehicleScale is float vs && vs > 0 ? vs : HeroHeight(targetHeroPackage) is float th0 && HeroHeight(donorHeroPackage) is float dh0 && dh0 > 1 ? th0 / dh0 : 1;
            // (MHO_BORROW_VEHICLE_SCALE_FORCE=<n>: that scale instead, to see whether it reaches the vehicle at all)
            if (float.TryParse(Environment.GetEnvironmentVariable("MHO_BORROW_VEHICLE_SCALE_FORCE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vforce) && vforce > 0) vscale = vforce;
            if (crossHero && pr.Role == "power" && spawn is { Count: > 0 } && cooked != null) done[tf] = SpawnAttachments(done[tf], td, spawn, cooked, log, HeroBones(targetHeroPackage));
            // a prop the costumes in heldBy hold now (added to them): its spawn left out for them, so the power throws the held one
            if (crossHero && pr.Role == "power" && held is { Classes.Count: > 0 } hc && heldBy is { Count: > 0 }) done[tf] = NotForHolders(done[tf], td, hc.Classes, heldBy, log);
            if (crossHero && pr.Role == "power" && cooked != null && !keepWeapons) done[tf] = HideHeldWeapons(done[tf], td, targetHeroPackage, donorHeroPackage, cooked, log);
            else if (crossHero && pr.Role == "power" && keepWeapons) log.Add("  this hero's weapons stay shown while the power runs (Keep This Hero's Weapons)");
            // every prop the power now brings (a spawned vehicle or weapon, the donor's own shown ones): its fit
            if (crossHero && pr.Role == "power")
            {
                var vehicleClasses = (spawn ?? []).Where(x => x.Vehicle).Select(x => Package.Open(x.File).Exports[x.Export].ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                done[tf] = FitProps(done[tf], td, vehicleClasses, vscale, props, log, spawnedHere);
            }
            if (ownClassCostumes is { Count: > 0 }) done[tf] = KeepStockFor(done[tf], td, pr.TargetFile, ownClassCostumes, log);
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
        var mountSkeleton = crossHero ? HeroSkeleton(donorHeroPackage) : null;
        var loopCuts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool targetNoMount = HeroBones(targetHeroPackage) is { } tb0 && !tb0.Contains("g_mountattach");
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
            // a loop that turns the donor's mount a lot (Goblin's barrel roll) on a hero without the mount bone: the loop ends
            // before the roll (Kurt, 2026-10-29: his own flight loop was right but for the flip; the run swapped in for it bobbed
            // above and below the glider). Only the key counts and the length are cut (TrimLoops), never the keys: every edit of
            // the rider's borrowed keys broke him in game (2026-10-28)
            if (crossHero && an.Contains("_loop", StringComparison.OrdinalIgnoreCase) && mountSkeleton is { } msk && targetNoMount
                && AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).FirstOrDefault(x => x.ExportIndex == found.DonorSet) is { } fset
                && MountTurn(ap, fset, found.DonorSequence, msk) is float turned && turned > 30)
            {
                if (MountCut(ap, fset, found.DonorSequence, msk, out string why) is int cut) { loopCuts[an] = cut; log.Add($"anims: {an}: the donor's g_mountattach turns {turned:0}° in it (a roll this hero can't play: no such bone): {why}"); }
                else log.Add($"anims: {an}: the donor's g_mountattach turns {turned:0}° in it; {why}");
            }
            float donorLen = Length(donorHeroPackage, an);
            float rate = fit && targetLen > 0.01f && donorLen > 0.01f ? donorLen / targetLen : 1;
            log.Add($"anims: {an} {donorLen:0.##} s{(Math.Abs(rate - 1) > 0.01f ? $", played ×{rate:0.##} to last {targetLen:0.##} s like the target's {string.Join(" / ", targetAnims)}" : "")}");
            // the high-flying lift comes on while the start plays (Kurt, 2026-10-22: Spider-Man with Cap's run sank below the
            // ground during the shield-to-back start, then popped up): only the loop is lowered
            // (Kurt, 2026-10-22: not lowered, he floated through the start instead: the lift ramps in, so the lowering does)
            // ... a ramp (keys added to the body's bones) made him vanish, then stretch like a somersault (Kurt, 2026-10-22):
            // the start and end are lowered half way, steadily, on the root like the loop (lowered fully he sank, not at
            // all he floated: the lift is part way in while they play)
            // 0.60.32: ramped again, on the root (its rotation kept after its moved positions: the order that made him vanish
            // before), shaped like the lift Kurt measured (about 20 at the start's beginning, 120 mid-way, 140 at its end)
            // ... the root ramp somersaulted and vanished too (Kurt, 2026-10-22): back to a steady lowering on the root; half the
            // lift was 3 ft too low at first and 3 ft too high mid-way: Kurt's test, half the lift raised 3 ft (about 47)
            // Kurt's flat tests (2026-10-22): root -23 put the start's beginning at the ground, -117 its end: the root steady at
            // the first, the body eased the rest of the way in place (RampBodyInPlace)
            int ramp = an.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ? 1 : an.EndsWith("_end", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
            float atStart = lift * 3f / 140f, atEnd = lift * 151f / 140f;   // (Kurt, in game: 0.60.44's end (151) "ends really well"; its beginning (13) still dipped: 10 units up)
            swaps.Add(ramp != 0 ? found with { Rate = rate, Lift = atStart, Ramp = ramp, RampBy = atEnd - atStart } : found with { Rate = rate, Lift = lift });
        }
        // a ground run with no end animation on a flyer whose own power lands with one (Hawkeye's Sky-Cycle: Kurt, 2026-10-29,
        // Cap's run popped him up about 3 ft at the end, Spider-Man's swing didn't): the empty end slot gets the donor's loop,
        // lowered and easing back up to the ground over the target's landing, the start's mirror. TEST: MHO_BORROW_END_FROM_LOOP=1
        if (Environment.GetEnvironmentVariable("MHO_BORROW_END_FROM_LOOP") == "1" && lift != 0 && powerFile != null
            && !donorAnims.Any(a => a.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
            && targetAnims.FirstOrDefault(a => a.EndsWith("_end", StringComparison.OrdinalIgnoreCase)) is string targetEnd
            && swaps.FirstOrDefault(s => s.Slot.EndsWith("_loop", StringComparison.OrdinalIgnoreCase)) is { } loopSwap)
        {
            string endName = loopSwap.Slot[..^"_loop".Length] + "_end";
            // (Kurt, 2026-10-29: with Cap's 0.27 s start he sank to his armpits right away, and after a 0.33 s end he popped up
            // once standing: Hawkeye's lift comes on and goes off over about his own 1.07 s start, so both play over that)
            float liftLen = targetAnims.FirstOrDefault(a => a.EndsWith("_start", StringComparison.OrdinalIgnoreCase)) is string targetStart ? Length(targetHeroPackage, targetStart) : 0;
            int si = swaps.FindIndex(s => s.Slot.EndsWith("_start", StringComparison.OrdinalIgnoreCase));
            if (si >= 0 && liftLen > 0.01f && Length(donorHeroPackage, swaps[si].Slot) is float sl && sl > 0.01f)
            {
                // (Kurt: over 1.07 s he still sank right away, to his waist: the lowering eases in, MHO_BORROW_START_EASE, default 3)
                float ease = float.TryParse(Environment.GetEnvironmentVariable("MHO_BORROW_START_EASE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ev) && ev > 0 ? ev : 3;   // (t^2 brought him from his waist to his shins: t^3)
                // (Kurt, t^3: the beginning right, then a dip to the knees as the shield goes behind his back, gone once he runs:
                // the start ended lower (151) than his lift by then; it ends at MHO_BORROW_START_TO, default 95 for 140)
                float startTo = float.TryParse(Environment.GetEnvironmentVariable("MHO_BORROW_START_TO"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float st) && st > 0 ? st : 95;   // (115: mid-shin; Kurt 2026-10-29)
                float to = lift * startTo / 140f;
                swaps[si] = swaps[si] with { Rate = sl / liftLen, RampPower = ease, RampBy = to - swaps[si].Lift };
                log.Add($"anims: {swaps[si].Slot} played ×{sl / liftLen:0.##} to last {liftLen:0.##} s like the target's start (its lift)");
            }
            var pairTd = pairs.First(p => p.Role == "power");
            byte[] filled = FillEndSlot(done[powerFile], $"marvelgamecontent.default__{pairTd.TargetClass}".ToLowerInvariant(), endName, log);
            if (!ReferenceEquals(filled, done[powerFile]))
            {
                done[powerFile] = filled;
                float donorLen = Length(donorHeroPackage, loopSwap.Slot), endLen = liftLen > 0.01f ? liftLen : Length(targetHeroPackage, targetEnd);
                float rate = donorLen > 0.01f && endLen > 0.01f ? donorLen / endLen : 1;
                float atStart = lift * 3f / 140f, atEnd = lift * 151f / 140f;
                swaps.Add(loopSwap with { Slot = endName, Rate = rate, Lift = atStart, Ramp = -1, RampBy = atEnd - atStart });
                log.Add($"anims: {endName} = the donor's {loopSwap.Slot}, played ×{rate:0.##} to last {endLen:0.##} s (the target's lift), lowered from {atEnd:0} back to {atStart:0}");
            }
        }
        if (swaps.Count > 0)
        {
            string hf = Path.GetFileName(targetHeroPackage);
            string heroPath = startFrom?.Invoke(hf) ?? targetHeroPackage;
            var temps = new List<string>();
            try
            {
            if (done.TryGetValue(hf, out var hb)) temps.Add(heroPath = WriteTemp(hf, hb));
            // another hero's skeleton: bones that rest turned differently get their keys turned to fit (Thor's right toes)
            var turns = crossHero && HeroSkeleton(donorHeroPackage) is { } dsk && HeroSkeleton(targetHeroPackage) is { } tsk ? AnimSwap.RestTurns(dsk, tsk) : null;
            // Pivot on the Hand: the props' bones the borrowed animations stop moving (they still turn them)
            var pinned = (props ?? []).Where(f => f.PinPivot).SelectMany(f => spawnedHere.Where(s => s.Class.Equals(f.Class, StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Bones ?? []))
                .Where(b => !b.Equals("none", StringComparison.OrdinalIgnoreCase) && !b.Equals("root", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            done[hf] = AnimSwap.Build(heroPath, targetHeroClass, swaps, log, retarget: turns, rider: rider, skeleton: HeroSkeleton(targetHeroPackage), noPositions: pinned);
            // the target's own animated bones the donor's animations don't move (Thor's cape in Kitty's Air Walking) get the
            // target's own run's keys, looped (travel borrows; tested on Thor's six, Kurt 2026-10-20 / 21)
            if (crossHero && (ownTracks || Environment.GetEnvironmentVariable("MHO_BORROW_OWN_TRACKS") == "1"))
                done[hf] = AddOwnTracks(done[hf], "_on_" + targetHeroClass.ToLowerInvariant(), targetHeroPackage, targetAnims, log);
            if (loopCuts.Count > 0) done[hf] = TrimLoops(done[hf], "_on_" + targetHeroClass.ToLowerInvariant(), loopCuts, log);
            log.Add($"anims: {string.Join(", ", swaps.Select(x => x.Slot))} added to {hf} ({targetHeroClass})");
            // costumes with an animation list of their own don't play the hero's: the borrowed sets are imported into theirs
            if (cooked != null)
            {
                var hp = Package.FromBytes(done[hf]);
                string suffix = "_on_" + targetHeroClass.ToLowerInvariant();
                // (every borrow's set: the copy gets a number when the name's taken, …_on_<class>_2: Kurt, 2026-10-23, only the
                // first was listed, so the costumes with a list of their own stood in their rest pose for the other borrows in game)
                var setName = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(suffix) + @"(_\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var sets = hp.Exports.Where(x => hp.ClassOf(x).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) && setName.IsMatch(x.ObjectName))
                    .Select(hp.PathOf).ToList();
                string hero = targetHeroClass["MarvelPlayer_".Length..];
                foreach (string cf in Directory.EnumerateFiles(cooked, $"UC__MarvelPlayer_{hero}_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                    .Where(f => !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
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
    /// <param name="held">Props this hero now holds (added to the costume: Thor's hammer in Cap's hand) and their slots: a
    /// component showing only those is left as it is, so the power uses the held one (as the other hero's own power does).</param>
    public static byte[] SpawnBorrowedAttachments(byte[] package, string targetDefault, List<string> log, (IReadOnlySet<string> Classes, IReadOnlySet<string> Slots)? held = null)
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
            // a prop the hero holds now (an added prop): shown as the other hero's power shows its own, not spawned
            string ClassName(int c) => c > 0 ? p.Exports[c - 1].ObjectName : p.RefName(c);
            if (held is { } h && (classes.Count > 0 ? classes.All(c => h.Classes.Contains(ClassName(c))) : slotName != null && h.Slots.Contains(slotName)))
            {
                log.Add($"  {e.ObjectName}: shows this hero's held {(classes.Count > 0 ? string.Join(", ", classes.Select(ClassName)) : slotName)} (an added prop), as the other hero's power does");
                continue;
            }
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
    /// <summary>
    /// The hero's costumes whose class isn't the hero's (Kate Bishop: MarvelPlayer_KateBishop extends MarvelPlayer, in her own
    /// package): the game data's costumes for the same avatar not named MarvelPlayer_&lt;hero&gt;[_…], as lower-case class
    /// names (what EntityRequired / EntityExcluded list). Kurt, 2026-10-22: a borrow must leave such a costume as it is (her
    /// package is for her own mods), so the power stays stock on her.
    /// </summary>
    static List<string> OwnClassCostumes(string gameRoot, string hero)
    {
        List<Costume>? all;
        lock (costumesLock) all = costumes ??= Costume.All(gameRoot);
        if (all == null) return [];
        bool Heros(string c) => c.Equals("MarvelPlayer_" + hero, StringComparison.OrdinalIgnoreCase) || c.StartsWith($"MarvelPlayer_{hero}_", StringComparison.OrdinalIgnoreCase);
        var avatars = all.Where(c => c.Hero != null && Heros(c.Class)).Select(c => c.Hero!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(c => c.Hero != null && avatars.Contains(c.Hero) && c.Class.StartsWith("MarvelPlayer_", StringComparison.OrdinalIgnoreCase) && !Heros(c.Class))
            .Select(c => c.Class.ToLowerInvariant()).Distinct().OrderBy(x => x).ToList();
    }
    static List<Costume>? costumes;
    static readonly object costumesLock = new();

    /// <summary>A costume package's player class, as EntityRequired / EntityExcluded name it (UC__MarvelPlayer_CaptainAmerica_Classic_SF.upk
    /// → marvelplayer_captainamerica_classic).</summary>
    static string CostumeClassOf(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file);
        if (n.StartsWith("UC__", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        if (n.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) n = n[..^3];
        return n.ToLowerInvariant();
    }

    /// <summary>
    /// The power's spawn components for <paramref name="classes"/> (a prop added to some costumes: Thor's hammer in Cap Classic's
    /// hand) made EntityExcluded for <paramref name="holders"/>: those costumes throw the held one; the others still get the
    /// spawned copy.
    /// </summary>
    static byte[] NotForHolders(byte[] package, string targetDefault, IReadOnlySet<string> classes, IReadOnlyList<string> holders, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, targetDefault);
        if (td < 0) return package;
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        var done = new List<string>();
        foreach (int r in Components(p, td, out _, out _))
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } ct) continue;
            int c = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ct.ValueAt));
            string cn = c > 0 ? p.Exports[c - 1].ObjectName : c < 0 ? p.RefName(c) : "";
            if (!classes.Contains(cn)) continue;
            if (WithEntityLists(p, r - 1, holders, require: false, addNames) is { } nd) { rep[r - 1] = _ => nd; done.Add(p.Exports[r - 1].ObjectName); }
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  the held props' spawns couldn't be left out for {string.Join(", ", holders)} ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  {string.Join(", ", done)}: not for {string.Join(", ", holders)} (they hold the prop: the power uses theirs)");
        return output;
    }

    /// <summary>
    /// A component's data with its EntityRequired / EntityExcluded fitted to <paramref name="classes"/>: required (only on
    /// them: the hero's stock parts) or excluded (never on them: the borrowed parts). A list it already has is merged (a
    /// stock Yank web for Spider-Girl only stays only for her, if she's one of them). Null when it can't apply on them at all
    /// (require) or would apply on nobody (exclude).
    /// </summary>
    static byte[]? WithEntityLists(Package p, int export, IReadOnlyList<string> classes, bool require, List<string> addNames)
    {
        byte[] d = p.ReadExportBytes(p.Exports[export]).ToArray();
        var tags = TagWalker.Walk(p, d, 16);
        if (tags == null) return null;
        List<string> Read(string prop)
        {
            var t = tags.FirstOrDefault(x => x.Name.Equals(prop, StringComparison.OrdinalIgnoreCase));
            var l = new List<string>();
            if (t == null) return l;
            int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt));
            for (int k = 0; k < n; k++) l.Add(TagWalker.NameAt(p, d, t.ValueAt + 4 + 8 * k));
            return l;
        }
        var req = Read("EntityRequired"); var exc = Read("EntityExcluded");
        var set = new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase);
        if (require)
        {
            req = req.Count > 0 ? req.Where(set.Contains).ToList() : classes.Where(c => !exc.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            exc = [];
            if (req.Count == 0) return null;
        }
        else if (req.Count > 0) { req = req.Where(c => !set.Contains(c)).ToList(); if (req.Count == 0) return null; }
        else exc = exc.Concat(classes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] List(string prop, List<string> names)
        {
            var v = new byte[4 + 8 * names.Count];
            BinaryPrimitives.WriteInt32LittleEndian(v, names.Count);
            for (int k = 0; k < names.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + 8 * k), NameIdx(names[k]));
            var t = new byte[24 + v.Length];
            BinaryPrimitives.WriteInt32LittleEndian(t, NameIdx(prop));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), NameIdx("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), v.Length);
            v.CopyTo(t, 24);
            return t;
        }
        var m = new MemoryStream();
        m.Write(d, 0, 16);
        foreach (var t in tags)
            if (!t.Name.Equals("EntityRequired", StringComparison.OrdinalIgnoreCase) && !t.Name.Equals("EntityExcluded", StringComparison.OrdinalIgnoreCase)) m.Write(d, t.Start, t.End - t.Start);
        if (req.Count > 0) m.Write(List("EntityRequired", req));
        if (exc.Count > 0) m.Write(List("EntityExcluded", exc));
        m.Write(d, tags.NoneAt, d.Length - tags.NoneAt);
        return m.ToArray();
    }

    /// <summary>
    /// The power on <paramref name="classes"/> (the hero's costumes of their own class) as stock: every component the borrow
    /// listed excluded from them, and the class default's stock components (by path, from the stock file) listed again, only
    /// for them.
    /// </summary>
    static byte[] KeepStockFor(byte[] package, string targetDefault, string stockFile, IReadOnlyList<string> classes, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, targetDefault);
        if (td < 0) return package;
        var now = Components(p, td, out var tag, out byte[] d);
        var sp = Package.Open(stockFile);
        int sd = Find(sp, targetDefault);
        var stock = sd < 0 ? [] : Components(sp, sd, out _, out _).Where(r => r > 0).Select(r => Find(p, sp.PathOf(sp.Exports[r - 1]))).Where(i => i >= 0).Select(i => i + 1).ToList();
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        var list = new List<int>();
        foreach (int r in now)
        {
            if (r <= 0 || stock.Contains(r)) continue;
            if (WithEntityLists(p, r - 1, classes, require: false, addNames) is { } cd) { rep[r - 1] = _ => cd; list.Add(r); }
        }
        int kept = 0;
        var meshes = new List<string>();
        foreach (int r in stock)
        {
            // a model (Cap's flying shield, a projectile's StaticMeshComponent) can't be limited to some costumes: a mesh
            // component draws for everyone whatever its EntityRequired says (Kurt, 2026-10-23: Shield Toss ← Hammer of Storms
            // threw the hammer and the shield), so it's left out
            string cls = p.ClassOf(p.Exports[r - 1]);
            if (cls.Equals("StaticMeshComponent", StringComparison.OrdinalIgnoreCase) || cls.Equals("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase)) { meshes.Add(p.Exports[r - 1].ObjectName); continue; }
            if (WithEntityLists(p, r - 1, classes, require: true, addNames) is { } cd) { rep[r - 1] = _ => cd; list.Add(r); kept++; }
        }
        if (meshes.Count > 0) log.Add($"  {targetDefault}: its model ({string.Join(", ", meshes)}) left out for every costume (a model can't be kept for some only)");
        var value = new byte[4 + 4 * list.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
        for (int k = 0; k < list.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), list[k]);
        byte[] nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        rep[td] = _ => nd;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {targetDefault}: the stock look for {string.Join(", ", classes)} couldn't be kept ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  {targetDefault}: {list.Count - kept} borrowed component(s) not on {string.Join(", ", classes)}; {kept} stock one(s) only on them");
        return output;
    }

    /// <summary>
    /// A borrowed projectile's own settings for what flies (Kurt, 2026-10-23: Shield Toss ← Hammer of Storms threw the shield
    /// with the hammer): the hero's weapon slot it takes from the hand (OwnerWeaponSlotToDetach: Cap's lefthand shield,
    /// Thor's righthand hammer) and the socket it comes back to (TargetSocket), as the donor's projectile has them; one the
    /// donor doesn't set is taken off (its default).
    /// </summary>
    static byte[] ProjectileSlots(byte[] package, string targetDefault, string donorFile, string donorDefault, List<string> log)
    {
        string[] props = ["OwnerWeaponSlotToDetach", "TargetSocket"];
        var p = Package.FromBytes(package);
        int ti = Find(p, targetDefault);
        var sp = Package.Open(donorFile);
        int si = Find(sp, donorDefault);
        if (ti < 0 || si < 0) return package;
        byte[] d = p.ReadExportBytes(p.Exports[ti]).ToArray(), sd = sp.ReadExportBytes(sp.Exports[si]).ToArray();
        if (TagWalker.Walk(p, d, 4) is not { } tt || TagWalker.Walk(sp, sd, 4) is not { } st) return package;
        bool Ours(TagWalker.Tag t) => props.Any(x => x.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) && t.Type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase);
        string Val(Package q, byte[] b, TagWalker.Tag? t) => t == null ? "(none)" : TagWalker.NameAt(q, b, t.ValueAt);
        var changes = new List<string>();
        foreach (string prop in props)
        {
            var a = tt.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && Ours(t));
            var b = st.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && Ours(t));
            if (!Val(p, d, a).Equals(Val(sp, sd, b), StringComparison.OrdinalIgnoreCase)) changes.Add($"{prop} {Val(p, d, a)} → {Val(sp, sd, b)}");
        }
        if (changes.Count == 0) return package;
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return p.Names.Length + j;
        }
        var nd = new List<byte>(d.AsSpan(0, 4).ToArray());
        foreach (var t in tt) if (!Ours(t)) nd.AddRange(d.AsSpan(t.Start, t.End - t.Start).ToArray());
        foreach (var t in st.Where(Ours))
        {
            // name, NameProperty, size 8, array index, then the value (a name and its number)
            nd.AddRange(BitConverter.GetBytes(NameIdx(t.Name))); nd.AddRange(BitConverter.GetBytes(0));
            nd.AddRange(BitConverter.GetBytes(NameIdx("NameProperty"))); nd.AddRange(BitConverter.GetBytes(0));
            nd.AddRange(BitConverter.GetBytes(8)); nd.AddRange(sd.AsSpan(t.Start + 20, 4).ToArray());
            nd.AddRange(BitConverter.GetBytes(NameIdx(TagWalker.NameAt(sp, sd, t.ValueAt)))); nd.AddRange(sd.AsSpan(t.ValueAt + 4, 4).ToArray());
        }
        nd.AddRange(d.AsSpan(tt.NoneAt));
        byte[] data = [.. nd];
        var rep = new Dictionary<int, Func<long, byte[]>> { [ti] = _ => data };
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {targetDefault}: what it throws couldn't be changed ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  {targetDefault}: throws what the other projectile throws: {string.Join(", ", changes)}");
        return output;
    }

    /// <summary>
    /// The conditions <paramref name="power"/> sets off, each without its mesh attachments that show a weapon slot (a ride's
    /// vehicle: Hawkeye's skycycleattach shows his "skycycle"); only packages that had one. The condition's other parts
    /// (its animation names, effects) stay.
    /// </summary>
    static Dictionary<string, byte[]> DropConditionVehicles(Fx.GameData db, Fx.FxGame game, string power, Func<string, string?> start, List<string> log, IReadOnlyList<string>? onlyFor = null)
    {
        var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Fx.PowerClosure.Of(db, power).Where(a => a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase)))
        {
            string def = $"marvelgamecontent.default__{a.Class}".ToLowerInvariant();
            string? stock = game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), def) >= 0);
            if (stock == null) continue;
            // (the game's own spelling: the package index gives names in lower case)
            string name = Directory.EnumerateFiles(Path.GetDirectoryName(stock)!, Path.GetFileName(stock)).Select(Path.GetFileName).OfType<string>().FirstOrDefault() ?? Path.GetFileName(stock);
            if (done.ContainsKey(name)) continue;
            var p = Package.Open(start(name) ?? stock);
            int d = Find(p, def);
            if (d < 0) continue;
            List<int> comps;
            TagWalker.Tag tag; byte[] data;
            try { comps = Components(p, d, out tag, out data); }
            catch (InvalidDataException) { continue; }
            var gone = new List<string>();
            var keep = new List<int>();
            foreach (int r in comps)
            {
                string? slot = null;
                if (r > 0 && p.ClassOf(p.Exports[r - 1]).Equals("ConditionFxMeshAttachment", StringComparison.OrdinalIgnoreCase))
                {
                    slot = ShownSlot(p, r - 1);
                    if (slot == null && Template(p, r - 1) is string tpl && TemplatePackage(game.Cooked, tpl) is string tf)
                    {
                        var tp = Package.Open(tf);
                        int ti = Find(tp, tpl);
                        if (ti >= 0) slot = ShownSlot(tp, ti);
                    }
                }
                if (slot != null) gone.Add($"{p.Exports[r - 1].ObjectName} ({slot})"); else keep.Add(r);
            }
            if (gone.Count == 0) continue;
            if (onlyFor is { Count: > 0 })
            {
                // kept for the costumes the borrow leaves stock (Kate Bishop rides her Sky-Cycle as before)
                var pr2 = new Dictionary<int, Func<long, byte[]>>();
                var names2 = new List<string>();
                foreach (int r in comps.Except(keep))
                    if (WithEntityLists(p, r - 1, onlyFor, require: true, names2) is { } cd2) pr2[r - 1] = _ => cd2;
                byte[] out2 = PackageRebuilder.Rebuild(p, pr2, [], out var w2, names2);
                var pb2 = PackageRebuilder.Verify(p, out2, pr2.Keys.ToList(), [], w2, names2);
                if (pb2.Count > 0) { log.Add($"  {a.Class}: its vehicle couldn't be limited ({string.Join("; ", pb2.Take(2))})"); continue; }
                done[name] = out2;
                log.Add($"  {a.Class}: this hero's own ride shows only for {string.Join(", ", onlyFor)} ({string.Join(", ", gone)})");
                continue;
            }
            var list = new byte[4 + 4 * keep.Count];
            BinaryPrimitives.WriteInt32LittleEndian(list, keep.Count);
            for (int k = 0; k < keep.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(list.AsSpan(4 + 4 * k), keep[k]);
            byte[] nd = [.. data.AsSpan(0, tag.ValueAt), .. list, .. data.AsSpan(tag.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), list.Length);   // the tag's size
            var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nd };
            byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written);
            var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written);
            if (problems.Count > 0) { log.Add($"  {a.Class}: its vehicle couldn't be taken off ({string.Join("; ", problems.Take(2))})"); continue; }
            done[name] = output;
            log.Add($"  {a.Class}: this hero's own ride no longer shows ({string.Join(", ", gone)})");
        }
        return done;
    }

    public static List<(string File, int Export, string Slot, bool Vehicle)> ConditionAttachments(Fx.GameData db, Fx.FxGame game, string donorPower, string donorHeroPackage, List<string> log)
    {
        var slots = new List<string>();
        // a slot a condition shows is a vehicle (a ride's bike, the Sky-Cycle); one the power itself shows is a weapon (webbing)
        var vehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (slot != null && a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase)) vehicles.Add(slot);
            }
        }
        var found = new List<(string, int, string, bool)>();
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
                if (slots.Contains(s, StringComparer.OrdinalIgnoreCase)) { found.Add((donorHeroPackage, i, s, vehicles.Contains(s))); break; }
            }
        }
        foreach (var (_, i, s, v) in found) log.Add($"  the donor shows {(v ? "vehicle" : "weapon")} slot {s}: {hp.Exports[i].ObjectName} (spawned while the power runs)");
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
    public static byte[] SpawnAttachments(byte[] package, string targetDefault, IReadOnlyList<(string File, int Export, string Slot, bool Vehicle)> classes, string cooked, List<string> log, IReadOnlySet<string>? targetBones = null, float scale = 1)
    {
        var dst = Package.FromBytes(package);
        string tplFile = StockFiles.For(cooked, "UC__PowerAntMan_AvatarSwapIn_SF.upk");
        const string tplDefault = "marvelgamecontent.default__powerantman_avatarswapin", tplComp = tplDefault + ".ant_outro";
        if (!File.Exists(tplFile)) { log.Add("  the spawn component's template package isn't found: vehicles left out"); return package; }
        var tp = Package.Open(tplFile);
        int ti = Find(tp, tplComp);
        if (ti < 0) { log.Add("  the spawn component's template isn't found: vehicles left out"); return package; }
        int k = 0;
        foreach (var (file, export, slot, vehicle) in classes)
        {
            var hp = Package.Open(file);
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(hp, export, dst, []), out string csaid)
                ?? throw new InvalidDataException($"{hp.Exports[export].ObjectName} couldn't be copied: " + string.Join(" ", csaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(cc.Output);
            int classRef = cc.RootRef;
            if (targetBones is { Count: > 0 }) dst = RootMissingBones(dst, "marvelgamecontent.default__" + dst.Exports[classRef - 1].ObjectName, targetBones, log);
            if (vehicle && Math.Abs(scale - 1) > 0.02f) dst = ScaleAttachment(dst, "marvelgamecontent.default__" + dst.Exports[classRef - 1].ObjectName, scale, cooked, log);
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplDefault] = targetDefault };
            string name = $"borrowed_{slot}_spawn{(k++ == 0 ? "" : k.ToString())}";
            // (a name a part from an earlier build already has: the next free one)
            for (int n2 = 2; Find(dst, targetDefault + "." + name) >= 0; n2++) name = $"borrowed_{slot}_spawn_{n2}";
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, ["whileactivatedattachmentclass"], name, replace), out string ssaid)
                ?? throw new InvalidDataException($"the spawn component couldn't be copied: " + (ssaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim()).ToList() is { Count: > 0 } why ? string.Join(" ", why) : string.Join(" ", ssaid.Split(Environment.NewLine).Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3))));
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

    /// <summary>
    /// For a travel borrow (and MHO_BORROW_OWN_TRACKS=1 for any): adds to each borrowed animation (the sets named *<paramref name="setSuffix"/>)
    /// tracks for the bones the target hero's own power animation moves (more than one rotation key) and the borrowed set
    /// has no track for: Thor's cape (g_cape1, g_l/r_cape1-3: his flight keys them every frame) in Kitty Pryde's Air
    /// Walking, which has none, so the cape hung still. Rotation keys: the target's own animation (its longest of
    /// <paramref name="targetAnims"/>) sampled at every frame of the borrowed one, looped; position: one key, the bone's
    /// rest. Written as the borrowed sequence stores its tracks (Float96NoW rotation, plain float positions; frame
    /// numbers after the keys for VariableKeyLerp, each part aligned to 4). Other formats: left as they were.
    /// </summary>
    static byte[] AddOwnTracks(byte[] package, string setSuffix, string targetHeroPackage, IReadOnlyList<string> targetAnims, List<string> log)
    {
        // the target's own animation and skeleton
        var ap = AnimExportCli.Packages.Package.Open(targetHeroPackage);
        AnimExportCli.Animation.BoneAnimation? own = null; int ownFrames = 0; float ownLen = 0; string ownName = ""; bool havePick = false;
        foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap).OrderByDescending(x => x.TrackBoneNames.Count))
            foreach (var seq in set.Sequences.Where(q => q.IsExport))
            {
                string n = AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex);
                // the target's run (MHO_BORROW_OWN_ANIM: another), else its own power's longest (Kurt 2026-10-20: the flight
                // loop's cape drifted left, its body banks; the run's kept the cape centered on all six Thor travel tests)
                string pick = Environment.GetEnvironmentVariable("MHO_BORROW_OWN_ANIM") is { Length: > 0 } env ? env : "movement_run";
                bool isPick = n.Equals(pick, StringComparison.OrdinalIgnoreCase);
                if (!isPick && (havePick || !targetAnims.Contains(n, StringComparer.OrdinalIgnoreCase))) continue;
                var pr = ap.TryReadProperties(seq.ExportIndex);
                float len = pr?.GetFloat("SequenceLength") ?? 0;
                if (!isPick && len <= ownLen || AnimExportCli.Animation.AnimObjectReader.TryRead(ap, seq.ExportIndex, set.TrackBoneNames) is not { } ba) continue;
                (own, ownFrames, ownLen, ownName) = (ba, pr!.GetInt("NumFrames"), len, n);
                havePick |= isPick;
            }
        var skeleton = HeroSkeleton(targetHeroPackage);
        if (own == null || skeleton == null || ownFrames < 2) { log.Add("own tracks: the target's own animation wasn't read"); return package; }
        var rest = skeleton.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var p = Package.FromBytes(package);
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        for (int si = 0; si < p.Exports.Length; si++)
        {
            var se = p.Exports[si];
            if (!p.ClassOf(se).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) || !se.ObjectName.EndsWith(setSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            byte[] sd = p.ReadExportBytes(se).ToArray();
            var stags = TagWalker.Walk(p, sd, 4);
            var tb = stags?.FirstOrDefault(t => t.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase));
            var sq = stags?.FirstOrDefault(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase));
            if (tb == null || sq == null) continue;
            int nb = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(tb.ValueAt));
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < nb; k++) have.Add(TagWalker.NameAt(p, sd, tb.ValueAt + 4 + 8 * k));
            var bones = own.Tracks.Where(t => t.Value.RotationKeys.Count > 1 && !have.Contains(t.Key) && rest.ContainsKey(t.Key)).Select(t => t.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (bones.Count == 0) continue;
            // each sequence of the set: the new tracks appended
            int ns = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(sq.ValueAt));
            bool all = true;
            for (int k = 0; k < ns; k++)
            {
                int r = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(sq.ValueAt + 4 + 4 * k));
                if (r <= 0) continue;
                byte[] d = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
                if (WithOwnTracks(p, d, nb, bones, own, ownFrames, ownLen, rest, out string why) is { } nd) replace[r - 1] = _ => nd;
                else { all = false; log.Add($"own tracks: {p.Exports[r - 1].ObjectName}: {why}"); }
            }
            if (!all) { replace.Clear(); log.Add("own tracks: left out (a sequence of the set couldn't take them)"); return package; }
            // the set: the new bones at the end of its track list (its tracks are by index)
            var add = new byte[8 * bones.Count];
            for (int k = 0; k < bones.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(add.AsSpan(8 * k), NameIdx(bones[k]));
            byte[] nsd = [.. sd.AsSpan(0, tb.End), .. add, .. sd.AsSpan(tb.End)];
            BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(tb.ValueAt), nb + bones.Count);
            BinaryPrimitives.WriteInt32LittleEndian(nsd.AsSpan(tb.Start + 16), tb.Size + add.Length);
            replace[si] = _ => nsd;
            log.Add($"own tracks: {se.ObjectName} gets {string.Join(", ", bones)} from the target's {ownName} ({ownLen:0.##} s, looped)");
        }
        if (replace.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, replace, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("own tracks: " + string.Join("; ", problems.Take(3)));
        return output;
    }

    /// <summary>A sequence's data with tracks appended for <paramref name="bones"/> (AddOwnTracks), else null with the reason.</summary>
    static byte[]? WithOwnTracks(Package p, byte[] d, int trackCount, List<string> bones, AnimExportCli.Animation.BoneAnimation own, int ownFrames, float ownLen,
        Dictionary<string, AnimExportCli.Meshes.MeshBone> rest, out string why)
    {
        why = "";
        var tags = TagWalker.Walk(p, d, 4);
        if (tags == null) { why = "its properties don't read"; return null; }
        string Enum(string n) => tags.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t && t.Size == 8 ? p.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(t.ValueAt))] : "";
        string tf = Enum("TranslationCompressionFormat"), rf = Enum("RotationCompressionFormat"), kf = Enum("KeyEncodingFormat");
        if (tf.Length > 0 && !tf.Equals("acf_none", StringComparison.OrdinalIgnoreCase)) { why = "positions stored as " + tf; return null; }
        if (rf.Length > 0 && !rf.Equals("acf_float96now", StringComparison.OrdinalIgnoreCase)) { why = "rotations stored as " + rf; return null; }
        bool variable = kf.Equals("akf_variablekeylerp", StringComparison.OrdinalIgnoreCase);
        if (!variable && kf.Length > 0 && !kf.Equals("akf_constantkeylerp", StringComparison.OrdinalIgnoreCase)) { why = "keys stored as " + kf; return null; }
        var nf = tags.FirstOrDefault(x => x.Name.Equals("NumFrames", StringComparison.OrdinalIgnoreCase));
        var sl = tags.FirstOrDefault(x => x.Name.Equals("SequenceLength", StringComparison.OrdinalIgnoreCase));
        var ot = tags.FirstOrDefault(x => x.Name.Equals("CompressedTrackOffsets", StringComparison.OrdinalIgnoreCase));
        if (nf == null || sl == null || ot == null) { why = "no frame count, length or track offsets"; return null; }
        int frames = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(nf.ValueAt));
        float seconds = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(sl.ValueAt));
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ot.ValueAt)) != 4 * trackCount) { why = "the track offsets don't match the bones"; return null; }
        // the compressed stream: after the raw tracks
        int q = tags.NoneAt + 8;
        int raw = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)); q += 4;
        for (int k = 0; k < raw; k++)
        {
            q += 4 + 12 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
            q += 4 + 16 * BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q));
        }
        int countAt = q, len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(q)), streamAt = q + 4;
        // the new tracks' bytes and their offsets
        var data = new MemoryStream();
        data.Write(d, streamAt, len);
        void Pad() { while (data.Length % 4 != 0) data.WriteByte(0); }
        Pad();
        var offsets = new List<int>();
        int keys = Math.Max(2, frames);
        foreach (string bone in bones)
        {
            var b = rest[bone];
            int tOff = (int)data.Length;
            data.Write(BitConverter.GetBytes(b.Position.X)); data.Write(BitConverter.GetBytes(b.Position.Y)); data.Write(BitConverter.GetBytes(b.Position.Z));
            int rOff = (int)data.Length;
            var tr = own.Tracks[bone].RotationKeys;
            for (int i = 0; i < keys; i++)
            {
                float t = seconds * i / Math.Max(1, keys - 1);
                float f = (t % ownLen) / ownLen * (ownFrames - 1);   // the target's frame, looped
                var rq = SampleRotation(tr, f);
                if (rq.W < 0) rq = -rq;
                data.Write(BitConverter.GetBytes(-rq.X)); data.Write(BitConverter.GetBytes(-rq.Y)); data.Write(BitConverter.GetBytes(-rq.Z));   // stored: the conjugate
            }
            if (variable)
            {
                Pad();
                for (int i = 0; i < keys; i++)
                {
                    int fr = (int)MathF.Round((float)i * (frames - 1) / Math.Max(1, keys - 1));
                    if (frames > 0xFF) data.Write(BitConverter.GetBytes((ushort)fr)); else data.WriteByte((byte)fr);
                }
                Pad();
            }
            offsets.AddRange([tOff, 1, rOff, keys]);
        }
        byte[] stream = data.ToArray();
        var arr = new byte[4 * offsets.Count];
        for (int k = 0; k < offsets.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(arr.AsSpan(4 * k), offsets[k]);
        int arrEnd = ot.ValueAt + ot.Size;
        byte[] o = [.. d.AsSpan(0, arrEnd), .. arr, .. d.AsSpan(arrEnd, countAt - arrEnd), .. BitConverter.GetBytes(stream.Length), .. stream, .. d.AsSpan(streamAt + len)];
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(ot.ValueAt), 4 * (trackCount + bones.Count));
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(ot.Start + 16), ot.Size + arr.Length);
        return o;
    }

    /// <summary>A rotation track at a frame (keys by frame number), spherically blended between its two keys.</summary>
    static System.Numerics.Quaternion SampleRotation(IReadOnlyList<AnimExportCli.Animation.BoneRotationKey> keys, float frame)
    {
        if (frame <= keys[0].TimeFrame) return System.Numerics.Quaternion.Normalize(keys[0].Rotation);
        for (int i = 1; i < keys.Count; i++)
            if (frame <= keys[i].TimeFrame)
            {
                float span = keys[i].TimeFrame - keys[i - 1].TimeFrame;
                float a = span > 0 ? (frame - keys[i - 1].TimeFrame) / span : 0;
                var q0 = keys[i - 1].Rotation; var q1 = keys[i].Rotation;
                if (System.Numerics.Quaternion.Dot(q0, q1) < 0) q1 = -q1;
                return System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Slerp(q0, q1, a));
            }
        return System.Numerics.Quaternion.Normalize(keys[^1].Rotation);
    }

    /// <summary>
    /// For a travel borrow: the donor hero's always-held attachments on its mount bone (g_mountattach):
    /// what it always rides. Green Goblin's glider is one of his character's attachments (slot glider), shown all the time,
    /// so neither his flight power nor its condition shows it and a borrow found nothing to spawn (Kurt, 2026-10-20: Thor
    /// with Goblin's Glide, no glider).
    /// </summary>
    public static List<(string File, int Export, string Slot, bool Vehicle)> MountAttachments(string donorHeroPackage, List<string> log)
    {
        var found = new List<(string, int, string, bool)>();
        var hp = Package.Open(donorHeroPackage);
        for (int i = 0; i < hp.Exports.Length; i++)
        {
            var e = hp.Exports[i];
            if (!hp.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
            int d = Find(hp, "marvelgamecontent.default__" + e.ObjectName);
            if (d < 0) continue;
            byte[] b = hp.ReadExportBytes(hp.Exports[d]).ToArray();
            var tags = TagWalker.Walk(hp, b, 4);
            if (tags == null) continue;
            if (tags.FirstOrDefault(t => t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase)) is { } vp && TagWalker.NameAt(hp, b, vp.ValueAt).Contains("on_demand", StringComparison.OrdinalIgnoreCase)) continue;
            var bones = tags.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase));
            var ws = tags.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase));
            if (bones == null) continue;
            bool mount = false;
            int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(bones.ValueAt));
            for (int k = 0; k < n; k++) mount |= TagWalker.NameAt(hp, b, bones.ValueAt + 4 + 8 * k).Equals("g_mountattach", StringComparison.OrdinalIgnoreCase);
            if (!mount) continue;
            string slot = ws != null && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ws.ValueAt)) > 0 ? TagWalker.NameAt(hp, b, ws.ValueAt + 4) : e.ObjectName;
            found.Add((donorHeroPackage, i, slot, true));
            log.Add($"  the donor always rides {e.ObjectName} (slot {slot}, on g_mountattach): spawned while the power runs");
        }
        return found;
    }

    /// <summary>
    /// The donor hero's always-held weapons (its default's attachment classes that aren't shown on demand nor ridden: Thor's
    /// hammer): a borrowed animation of that hero is made holding them, so they're spawned while the power runs (this hero's
    /// own held weapons are hidden then: HideHeldWeapons).
    /// </summary>
    /// <param name="targetBones">The target's skeleton: a weapon on a bone it lacks isn't brought (Captain America's shield
    /// hangs from his own g_shield, which his run moves to his back: on Spider-Man it showed at his feet; Kurt 2026-10-22).</param>
    public static List<(string File, int Export, string Slot, bool Vehicle)> HeldAttachments(string donorHeroPackage, List<string> log, IReadOnlySet<string>? targetBones = null)
    {
        var found = new List<(string, int, string, bool)>();
        var listed = ModMeshes.AttachmentClasses(donorHeroPackage)?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (listed == null || listed.Count == 0) return found;
        var hp = Package.Open(donorHeroPackage);
        for (int i = 0; i < hp.Exports.Length; i++)
        {
            var e = hp.Exports[i];
            if (!hp.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !listed.Contains(e.ObjectName)) continue;
            int d = Find(hp, "marvelgamecontent.default__" + e.ObjectName);
            if (d < 0) continue;
            byte[] b = hp.ReadExportBytes(hp.Exports[d]).ToArray();
            var tags = TagWalker.Walk(hp, b, 4);
            if (tags == null) continue;
            if (tags.FirstOrDefault(t => t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase)) is { } vp && TagWalker.NameAt(hp, b, vp.ValueAt).Contains("on_demand", StringComparison.OrdinalIgnoreCase)) continue;
            var bones = tags.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase));
            bool mount = false;
            if (bones != null)
                for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(bones.ValueAt)); k < n; k++) mount |= TagWalker.NameAt(hp, b, bones.ValueAt + 4 + 8 * k).Equals("g_mountattach", StringComparison.OrdinalIgnoreCase);
            if (mount) continue;
            string? missing = null;
            if (bones != null && targetBones is { Count: > 0 })
                for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(bones.ValueAt)); k < n && missing == null; k++)
                    if (TagWalker.NameAt(hp, b, bones.ValueAt + 4 + 8 * k) is var bn && !bn.Equals("none", StringComparison.OrdinalIgnoreCase) && !targetBones.Contains(bn)) missing = bn;
            if (missing != null) { log.Add($"  the donor always holds {e.ObjectName}, on {missing}: this hero has no such bone, so it isn't brought"); continue; }
            var ws = tags.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase));
            string slot = ws != null && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ws.ValueAt)) > 0 ? TagWalker.NameAt(hp, b, ws.ValueAt + 4) : e.ObjectName;
            found.Add((donorHeroPackage, i, slot, false));
            log.Add($"  the donor always holds {e.ObjectName} (slot {slot}): spawned while the power runs");
        }
        return found;
    }

    /// <summary>The weapon slots a hero always holds something in (attachments not shown on demand: Thor's hammer in
    /// righthand), from its base package.</summary>
    static HashSet<string> HeldSlots(string heroPackage)
    {
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var a in ModMeshes.Attachments(heroPackage).Where(a => !a.OnDemand))
                foreach (string s in a.Slots.Count > 0 ? a.Slots : a.Slot is { } one ? [one] : [])
                    if (!s.Equals("none", StringComparison.OrdinalIgnoreCase)) slots.Add(s);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return slots;
    }

    /// <summary>
    /// Another hero's animation isn't made to hold this hero's weapons: Thor's hammer stuck out of his chest in Kitty Pryde's
    /// Air Walking (Kurt, 2026-10-20: held properly or hidden). Each weapon slot this hero always holds something in is hidden (even
    /// where the donor holds something too: its weapon is held its own way; Kitty's katana pose put the hammer in his chest) while the power runs (a component of its own: a copy of Ant-Man's ant_outro, the spawn
    /// components' form, with WhileActivatedHideWeaponSlot instead of a class). Slots the borrowed parts hide already are left.
    /// </summary>
    public static byte[] HideHeldWeapons(byte[] package, string targetDefault, string targetHeroPackage, string donorHeroPackage, string cooked, List<string> log)
    {
        var slots = HeldSlots(targetHeroPackage);
        if (slots.Count == 0) return package;
        var dst = Package.FromBytes(package);
        int td0 = Find(dst, targetDefault);
        if (td0 < 0) return package;
        // already hidden by the borrowed parts
        foreach (int r in Components(dst, td0, out _, out _))
        {
            if (r <= 0) continue;
            byte[] od = dst.ReadExportBytes(dst.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(dst, od, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedHideWeaponSlot", StringComparison.OrdinalIgnoreCase)) is { } hs)
                slots.Remove(TagWalker.NameAt(dst, od, hs.ValueAt));
        }
        if (slots.Count == 0) return package;
        string tplFile = StockFiles.For(cooked, "UC__PowerAntMan_AvatarSwapIn_SF.upk");
        const string tplDefault = "marvelgamecontent.default__powerantman_avatarswapin", tplComp = tplDefault + ".ant_outro";
        if (!File.Exists(tplFile)) { log.Add("  the hide component's template package isn't found: this hero's weapons stay shown"); return package; }
        var tp = Package.Open(tplFile);
        int ti = Find(tp, tplComp);
        if (ti < 0) { log.Add("  the hide component's template isn't found: this hero's weapons stay shown"); return package; }
        foreach (string slot in slots.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplDefault] = targetDefault };
            string name = $"borrowed_hide_{slot}";
            for (int n2 = 2; Find(dst, targetDefault + "." + name) >= 0; n2++) name = $"borrowed_hide_{slot}_{n2}";
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, ["whileactivatedattachmentclass"], name, replace), out string ssaid)
                ?? throw new InvalidDataException("the hide component couldn't be copied: " + string.Join(" ", ssaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(sc.Output);
            int comp = sc.RootRef - 1, td = Find(dst, targetDefault);
            byte[] cd = dst.ReadExportBytes(dst.Exports[comp]).ToArray();
            var ctags = TagWalker.Walk(dst, cd, 16) ?? throw new InvalidDataException("the hide component doesn't read");
            // drop the (cut, so empty) class tag; add the hidden slot
            var addNames = new List<string>();
            int NameIdx(string n)
            {
                int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
                if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
                return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            }
            var body = new MemoryStream();
            body.Write(cd, 0, 16);
            foreach (var t in ctags.Where(t => !t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase))) body.Write(cd, t.Start, t.End - t.Start);
            var tag = new byte[32];
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("WhileActivatedHideWeaponSlot"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("NameProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 8);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), NameIdx(slot));
            body.Write(tag);
            body.Write(cd, ctags.NoneAt, cd.Length - ctags.NoneAt);
            byte[] ncd = body.ToArray();
            var list = Components(dst, td, out var ltag, out byte[] dd);
            list.Add(comp + 1);
            var value = new byte[4 + 4 * list.Count];
            BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
            for (int j = 0; j < list.Count; j++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * j), list[j]);
            byte[] nd = [.. dd.AsSpan(0, ltag.ValueAt), .. value, .. dd.AsSpan(ltag.ValueAt + ltag.Size)];
            BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(ltag.Start + 16), value.Length);
            var rep = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => ncd, [td] = _ => nd };
            byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
            var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
            if (problems.Count > 0) throw new InvalidDataException("the hidden weapon slot: " + string.Join("; ", problems.Take(3)));
            dst = Package.FromBytes(output);
            log.Add($"  {name}: hides this hero's {slot} weapons while the power runs (the other hero's animation doesn't hold them)");
        }
        return dst.RawFile;
    }

    /// <summary>A hero's own model in its base package: the skeletal mesh named after the hero (UC__MarvelPlayer_Cyclops_SF →
    /// cyclops…), else the one with the most vertices. (By vertices alone Cyclops' package gave Angel, his summon.)</summary>
    /// <summary>
    /// The turn (in the prop's mesh space, row vectors: v * turn, then the bone) that keeps a prop held on
    /// <paramref name="bone"/> facing as it does in the donor's hand: the bone's rest orientation in the donor's model space
    /// times the inverse of the target's. Null when either skeleton lacks the bone.
    /// </summary>
    public static System.Numerics.Matrix4x4? HoldTurn(string donorHeroPackage, string targetHeroPackage, string bone)
    {
        System.Numerics.Matrix4x4? Rest(string pkg)
        {
            var bones = MainMesh(pkg)?.Bones;
            if (bones == null) return null;
            int i = bones.ToList().FindIndex(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            var m = System.Numerics.Matrix4x4.Identity;
            for (int k = i; k >= 0; k = k == 0 ? -1 : bones[k].ParentIndex)
            {
                var q = System.Numerics.Quaternion.Normalize(bones[k].Orientation);
                m *= System.Numerics.Matrix4x4.CreateFromQuaternion(k == 0 ? q : q);
                if (k == 0) break;
            }
            return m;
        }
        if (Rest(donorHeroPackage) is not { } d || Rest(targetHeroPackage) is not { } t) return null;
        System.Numerics.Matrix4x4.Invert(t, out var ti);
        return d * ti;
    }

    static AnimExportCli.Meshes.SkeletalMesh? MainMesh(string heroPackage)
    {
        var ap = AnimExportCli.Packages.Package.Open(heroPackage);
        string hero = Path.GetFileNameWithoutExtension(heroPackage).Split('_', StringSplitOptions.RemoveEmptyEntries) is [_, _, var h, ..] ? h : "";
        var meshes = new List<AnimExportCli.Meshes.SkeletalMesh>();
        foreach (int i in ap.FindExportsOfClass("skeletalmesh"))
            if (AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i) is { } m) meshes.Add(m);
        int Verts(AnimExportCli.Meshes.SkeletalMesh m) => m.HighestDetail?.Positions.Count ?? 0;
        return meshes.Where(m => hero.Length > 0 && m.Name.StartsWith(hero, StringComparison.OrdinalIgnoreCase)).OrderByDescending(Verts).FirstOrDefault()
            ?? meshes.OrderByDescending(Verts).FirstOrDefault();
    }

    /// <summary>
    /// For the Borrowed Look window's Vehicle Fit: whether borrowing <paramref name="donorPower"/> (of hero
    /// <paramref name="donorHero"/>) onto <paramref name="targetPower"/> brings a vehicle (a condition shows it: a ride), and
    /// the vehicle's automatic size (the target hero's height over the donor hero's), or null when it brings none.
    /// </summary>
    public static float? VehicleAutoScale(Fx.GameData db, Fx.FxGame game, string targetPower, string donorPower, string donorHero, string? targetHero)
    {
        string cooked = game.Cooked;
        string donorPackage = StockFiles.For(cooked, $"UC__MarvelPlayer_{donorHero}_SF.upk");
        if (!File.Exists(donorPackage)) return null;
        var quiet = new List<string>();
        if (!ConditionAttachments(db, game, donorPower, donorPackage, quiet).Any(x => x.Vehicle)) return null;
        string? th = HeroPackage(cooked, targetHero, targetPower, null, []);
        if (th == null || th.Equals(donorHero, StringComparison.OrdinalIgnoreCase)) return null;   // (a vehicle is only spawned on another hero)
        return HeroHeight(StockFiles.For(cooked, $"UC__MarvelPlayer_{th}_SF.upk")) is float a && HeroHeight(donorPackage) is float b && b > 1 ? a / b : 1;
    }

    /// <summary>
    /// A plain prop's move and turn baked into its model copy (<paramref name="md"/>, edited in place): every vertex turned
    /// (as RotOrigin would: pitch about Y, yaw about Z, roll about X) then moved, packed positions unpacked and packed again
    /// against a new range, the tangent frames turned, and the bounds moved (their extent made the radius every way). The
    /// root bone's rest pose is turned and moved the same way (Kurt, 2026-10-28: the strap of Thor's hammer on Hawkeye flapped
    /// around a lot): the skin draws the same (its bones' rest poses and its vertices moved together), and bones the model's
    /// physics asset swings (the strap's g_hammercloth) stay with the vertices they hold instead of swinging them from where
    /// they were before the turn.
    /// </summary>
    static void BakeMoveTurn(byte[] md, List<AnimExportCli.Meshes.SkeletalMeshReader.PositionSpot> where, System.Numerics.Vector3 move, System.Numerics.Vector3 turn)
    {
        static int Units(float deg) => (int)MathF.Round(deg / 360f * 65536f);
        var rot = ModMeshes.Rotator(Units(turn.Y), Units(turn.Z), Units(turn.X));
        System.Numerics.Vector3 P(System.Numerics.Vector3 v) => System.Numerics.Vector3.Transform(v, rot) + move;
        float F(int at) => BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(at));
        void W(int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(md.AsSpan(at), v);
        foreach (var w in where)
            switch (w.Kind)
            {
                case "verts":
                    for (int k = 0; k < w.Count; k++)
                    {
                        int va = w.At + k * w.Stride;
                        var v = P(new System.Numerics.Vector3(F(va), F(va + 4), F(va + 8)));
                        W(va, v.X); W(va + 4, v.Y); W(va + 8, v.Z);
                    }
                    break;
                case "packed":
                {
                    var ext = new System.Numerics.Vector3(F(w.At2), F(w.At2 + 4), F(w.At2 + 8));
                    var org = new System.Numerics.Vector3(F(w.At2 + 12), F(w.At2 + 16), F(w.At2 + 20));
                    var pts = new System.Numerics.Vector3[w.Count];
                    for (int k = 0; k < w.Count; k++)
                    {
                        uint pk = BinaryPrimitives.ReadUInt32LittleEndian(md.AsSpan(w.At + k * w.Stride));
                        int x = (int)(pk & 0x7FF), y = (int)((pk >> 11) & 0x7FF), z = (int)((pk >> 22) & 0x3FF);
                        if (x > 1023) x -= 2048; if (y > 1023) y -= 2048; if (z > 511) z -= 1024;
                        pts[k] = P(new System.Numerics.Vector3(x / 1023f * ext.X + org.X, y / 1023f * ext.Y + org.Y, z / 511f * ext.Z + org.Z));
                    }
                    if (w.Count == 0) break;
                    var lo = pts.Aggregate(System.Numerics.Vector3.Min); var hi = pts.Aggregate(System.Numerics.Vector3.Max);
                    var no = (lo + hi) / 2; var ne = System.Numerics.Vector3.Max((hi - lo) / 2, new System.Numerics.Vector3(1e-3f));
                    W(w.At2, ne.X); W(w.At2 + 4, ne.Y); W(w.At2 + 8, ne.Z); W(w.At2 + 12, no.X); W(w.At2 + 16, no.Y); W(w.At2 + 20, no.Z);
                    for (int k = 0; k < w.Count; k++)
                    {
                        var r = (pts[k] - no) / ne;
                        uint x = (uint)Math.Clamp((int)MathF.Round(r.X * 1023f), -1023, 1023) & 0x7FF;
                        uint y = (uint)Math.Clamp((int)MathF.Round(r.Y * 1023f), -1023, 1023) & 0x7FF;
                        uint z = (uint)Math.Clamp((int)MathF.Round(r.Z * 511f), -511, 511) & 0x3FF;
                        BinaryPrimitives.WriteUInt32LittleEndian(md.AsSpan(w.At + k * w.Stride), x | (y << 11) | (z << 22));
                    }
                    break;
                }
                case "frames":
                    for (int k = 0; k < w.Count; k++)
                        for (int f = 0; f < 2; f++)
                        {
                            int fa = w.At + k * w.Stride + 4 * f;
                            uint pk = BinaryPrimitives.ReadUInt32LittleEndian(md.AsSpan(fa));
                            var n = new System.Numerics.Vector3((pk & 0xFF) / 127.5f - 1, ((pk >> 8) & 0xFF) / 127.5f - 1, ((pk >> 16) & 0xFF) / 127.5f - 1);
                            n = System.Numerics.Vector3.TransformNormal(n, rot);
                            uint B(float c) => (uint)Math.Clamp((int)MathF.Round((c + 1) * 127.5f), 0, 255);
                            BinaryPrimitives.WriteUInt32LittleEndian(md.AsSpan(fa), B(n.X) | (B(n.Y) << 8) | (B(n.Z) << 16) | (pk & 0xFF000000));
                        }
                    break;
                case "rootbone":
                {
                    var q = new System.Numerics.Quaternion(F(w.At), F(w.At + 4), F(w.At + 8), F(w.At + 12));
                    // the bone's rest matrix (rotation, then position) followed by the bake's turn and move
                    var nq = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Concatenate(q, System.Numerics.Quaternion.CreateFromRotationMatrix(rot)));
                    var p = P(new System.Numerics.Vector3(F(w.At + 16), F(w.At + 20), F(w.At + 24)));
                    W(w.At, nq.X); W(w.At + 4, nq.Y); W(w.At + 8, nq.Z); W(w.At + 12, nq.W);
                    W(w.At + 16, p.X); W(w.At + 20, p.Y); W(w.At + 24, p.Z);
                    break;
                }
                case "bounds":
                {
                    var o = P(new System.Numerics.Vector3(F(w.At), F(w.At + 4), F(w.At + 8)));
                    float r = F(w.At + 24);
                    W(w.At, o.X); W(w.At + 4, o.Y); W(w.At + 8, o.Z); W(w.At + 12, r); W(w.At + 16, r); W(w.At + 20, r);
                    break;
                }
            }
    }

    /// <summary>A prop a borrow brings: its attachment class, whether it's a vehicle (ridden) and its automatic size.</summary>
    /// <param name="Bones">The bones its attachment hangs it on (AttachmentBones).</param>
    public sealed record SpawnedProp(string Class, bool Vehicle, float Auto, float TurnZAuto = 0, IReadOnlyList<string>? Bones = null);

    /// <summary>
    /// A prop's starting turn about its Z axis (degrees) on another hero, before any Prop Fit: Thor's hammer faces backward
    /// in another hero's hand (Kurt fitted it by hand: -161 on Spider-Man, -180 on Hawkeye; both right in game, 2026-10-22),
    /// though the palm bones rest alike: about -170 by default.
    /// </summary>
    /// <summary>An added prop's fit as its fitted model is made (FitModel, BakeMoveTurn): the original model's points scaled,
    /// turned, then moved, in the frame of the bone that holds it.</summary>
    internal static System.Numerics.Matrix4x4 FitMatrix(AddedPropEntry e)
    {
        var f = e.Fit;
        float scale = f.Scale is float fs && fs > 0 ? fs : 1;
        float tz = f.TurnZ == 0 && f.IsIdentity ? DefaultTurnZ(e.Class.ToLowerInvariant()) : f.TurnZ;
        static int Units(float deg) => (int)MathF.Round(deg / 360f * 65536f);
        return System.Numerics.Matrix4x4.CreateScale(scale) * ModMeshes.Rotator(Units(f.TurnY), Units(tz), Units(f.TurnX)) * System.Numerics.Matrix4x4.CreateTranslation(f.X, f.Y, f.Z);
    }

    internal static float DefaultTurnZ(string cls) => cls.StartsWith("marvelattachment_thorhammer", StringComparison.OrdinalIgnoreCase) ? -170 : 0;

    /// <summary>
    /// Prop Fit (Kurt, 2026-10-21: glider, Thor's hammer, Cap's shield: size, place, turn): every attachment class the
    /// target power's parts spawn (WhileActivatedAttachmentClass), drawn at its fit (FitModel): a vehicle sized for the hero
    /// by default (<paramref name="vehicleAuto"/>), anything else as made; listed in <paramref name="spawnedOut"/>.
    /// </summary>
    static byte[] FitProps(byte[] package, string targetDefault, IReadOnlySet<string> vehicleClasses, float vehicleAuto, IReadOnlyList<PropFit>? fits, List<string> log, List<SpawnedProp>? spawnedOut)
    {
        var dst = Package.FromBytes(package);
        int td = Find(dst, targetDefault);
        if (td < 0) return package;
        var classes = new List<string>();
        foreach (int r in Components(dst, td, out _, out _))
        {
            if (r <= 0) continue;
            byte[] cd = dst.ReadExportBytes(dst.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(dst, cd, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } ct) continue;
            int cls = BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(ct.ValueAt));
            if (cls > 0 && !classes.Contains(dst.Exports[cls - 1].ObjectName, StringComparer.OrdinalIgnoreCase)) classes.Add(dst.Exports[cls - 1].ObjectName);
        }
        foreach (string cls in classes)
        {
            bool vehicle = vehicleClasses.Contains(cls);
            float auto = vehicle ? vehicleAuto : 1;
            float turnAuto = DefaultTurnZ(cls);
            spawnedOut?.Add(new SpawnedProp(cls, vehicle, auto, turnAuto, AttachBones(dst, "marvelgamecontent.default__" + cls)));
            var fit = fits?.FirstOrDefault(f => f.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
            float scale = fit?.Scale is float fs && fs > 0 ? fs : auto;
            var move = fit == null ? default : new System.Numerics.Vector3(fit.X, fit.Y, fit.Z);
            var turn = fit == null ? new System.Numerics.Vector3(0, 0, turnAuto) : new System.Numerics.Vector3(fit.TurnX, fit.TurnY, fit.TurnZ);
            if (Math.Abs(scale - 1) < 0.005f && move == default && turn == default) continue;
            if (FitModel(dst, "marvelgamecontent.default__" + cls, scale, move, turn, log) is { } fitted) dst = fitted;
        }
        return dst.RawFile;
    }

    /// <summary>The props a character package has: its attachment classes with a model (its own list, mAttachmentClasses, first).</summary>
    public static List<string> PropClasses(string package)
    {
        var p = Package.Open(package);
        var own = ModMeshes.AttachmentClasses(package) ?? [];
        var list = new List<string>();
        foreach (var e in p.Exports)
        {
            if (!p.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
            int d = Find(p, "marvelgamecontent.default__" + e.ObjectName);
            if (d < 0) continue;
            byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
            if (TagWalker.Walk(p, b, 4)?.Any(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase)) == true) list.Add(e.ObjectName);
        }
        return [.. list.OrderBy(c => own.Contains(c, StringComparer.OrdinalIgnoreCase) ? 0 : 1)];
    }

    /// <summary>
    /// A prop added to a costume (AddedPropEntry; Kurt, 2026-10-23): its attachment class copied from <paramref name="fromPath"/>
    /// into <paramref name="package"/> when it isn't there yet (bones the costume's skeleton lacks set to its root), fitted
    /// from its original model (so a fit applied again doesn't stack), and listed in the costume's own props
    /// (mAttachmentClasses of its player class default). Applying it again gives the same package. Verified; throws
    /// InvalidDataException with the reason.
    /// </summary>
    /// <param name="hold">Pivot on the Hand: the package, the prop, its bones, the log → the package
    /// holding them (PropHold, baked into the copied animations); null: the copied sets stop moving the bones (PinPropBones).</param>
    /// <param name="modelFrom">A package holding a model of the prop's own (the Model tab's build: the same class, its ModelMesh
    /// the new model), put in its place; null: its character's own model.</param>
    public static byte[] AddProp(byte[] package, AddedPropEntry e, string fromPath, List<string> log, Func<byte[], AddedPropEntry, IReadOnlyList<string>, List<string>, byte[]>? hold = null, string? modelFrom = null)
    {
        hold ??= (b, _, bones, l) => PinPropBones(b, bones, true, l);
        // only a hero's base package's prop class (Kurt, 2026-10-28: Beta Ray Bill's hammer added to Doom crashed the game): a
        // costume's own class builds on its hero's (marvelattachment_thorhammer_betaraybill on Thor's), which the game loads only
        // with that hero; copied to another hero, its parent class can't be found
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(fromPath), @"^UC__MarvelPlayer_[^_]+_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !Path.GetFileName(fromPath).Equals(e.Package, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{e.Name} comes from a costume's own package ({Path.GetFileName(fromPath)}): its class builds on its hero's, which the game doesn't load for another hero (Doom crashed with Beta Ray Bill's hammer). Remove it, then add it again from the Model tab's Props view: it comes in as a model of its own.");
        MeshCopy.Register();   // (the prop's skeletal mesh copies with the class)
        var dst = Package.FromBytes(package);
        string cls = e.Class.ToLowerInvariant(), def = "marvelgamecontent.default__" + cls;
        int ClassExport(Package p) => Array.FindIndex(p.Exports, x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && p.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase));
        // there already, perhaps renamed by Save's Make Unique (marvelattachment_thorhammer_<mod tag>): that one, by its own name
        if (HeldClass(dst, cls) is int had && had >= 0)
        {
            cls = dst.Exports[had].ObjectName.ToLowerInvariant(); def = "marvelgamecontent.default__" + cls;
            // added by an earlier version under its own character's name: renamed now (another character's prop only)
            if (cls.Equals(e.Class, StringComparison.OrdinalIgnoreCase) && !e.From.Equals(e.Package, StringComparison.OrdinalIgnoreCase)
                && !(StockFiles.Cooked is string ck && File.Exists(StockFiles.For(ck, e.Package)) && Package.Open(StockFiles.For(ck, e.Package)).Exports.Any(x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase))))   // (not a class the costume has itself)
                dst = OwnClassName(dst, cls, def, e.Package, log, out cls, out def);
        }
        // there under its own name but off the list (unticked, then ticked again): that one
        else if (Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(OwnName(cls, e.Package), StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase)) is int own && own >= 0)
        { cls = dst.Exports[own].ObjectName.ToLowerInvariant(); def = "marvelgamecontent.default__" + cls; }
        else
        {
            var hp = Package.Open(fromPath);
            int ce = ClassExport(hp);
            if (ce < 0) throw new InvalidDataException($"{e.Class} isn't in {Path.GetFileName(fromPath)}");
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(hp, ce, dst, []), out string said)
                ?? throw new InvalidDataException($"{e.Class} couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(cc.Output);
            log.Add($"{e.Class} copied from {Path.GetFileName(fromPath)}");
            // bones the costume's skeleton doesn't have: its root
            if (CostumeBones(dst) is { Count: > 0 } bones) dst = RootMissingBones(dst, def, bones, log);
            // a name of its own in this costume (Kurt, 2026-10-27: Doom showed Angela's axe and another mod's Agatha instead of
            // his added props): the game knows a class by its path, so the copy loaded first (Angela's own, another mod's
            // carrier of the same class) is the one every costume gets
            dst = OwnClassName(dst, cls, def, e.Package, log, out cls, out def);
        }
        // the fit, from the original model (a refit replaces the earlier one)
        int d = Find(dst, def);
        if (d < 0) throw new InvalidDataException($"{def} isn't in the package");
        dst = WithOriginalModel(dst, def, log);
        if (!e.From.Equals(e.Package, StringComparison.OrdinalIgnoreCase)) dst = OwnModelName(dst, def, e.Package, log);
        dst = WithModel(dst, def, modelFrom, log, "marvelgamecontent.default__" + e.Class.ToLowerInvariant());   // (a model of its own: then the fit is made from that)
        var f = e.Fit;
        float scale = f.Scale is float fs && fs > 0 ? fs : 1;
        var move = new System.Numerics.Vector3(f.X, f.Y, f.Z);
        var turn = new System.Numerics.Vector3(f.TurnX, f.TurnY, f.TurnZ == 0 && f.IsIdentity ? DefaultTurnZ(cls) : f.TurnZ);
        if (Math.Abs(scale - 1) > 0.005f || move != default || turn != default)
            if (FitModel(dst, def, scale, move, turn, log) is { } fitted) dst = fitted;
        dst = AlwaysHeld(dst, def, log);
        // the bone it's held on: the Model tab's Held On, else its character's (put back when one was set before)
        {
            var want = e.Bone is string eb ? [eb] : AttachBones(Package.Open(fromPath), def);
            if (!AttachBones(dst, def).SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) dst = WithAttachBones(dst, Find(dst, def), want, log);
        }
        // listed in the costume's own props
        int classRef = ClassExport(dst) + 1;
        if (classRef <= 0) throw new InvalidDataException($"{e.Class} isn't in the package after copying");
        int pd = Array.FindIndex(dst.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
        if (pd < 0) throw new InvalidDataException("the package has no player class default (not a costume)");
        byte[] pb = dst.ReadExportBytes(dst.Exports[pd]).ToArray();
        var at = TagWalker.Walk(dst, pb, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase));
        if (at == null)
        {
            // a costume that uses its hero's list (Doctor Doom Classic; Kurt, 2026-10-27): given a list of its own, the hero's
            dst = WithOwnPropList(dst, pd, e.Package, log);
            classRef = ClassExport(dst) + 1;
            pb = dst.ReadExportBytes(dst.Exports[pd]).ToArray();
            at = TagWalker.Walk(dst, pb, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidDataException("the costume's own prop list didn't read back");
        }
        int n = BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt));
        var pinBones = AttachBones(dst, def);
        for (int k = 0; k < n; k++) if (BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt + 4 + 4 * k)) == classRef) return e.Fit.PinPivot ? hold(dst.RawFile, e, pinBones, log) : dst.RawFile;   // listed already
        byte[] np = [.. pb.AsSpan(0, at.End), .. BitConverter.GetBytes(classRef), .. pb.AsSpan(at.End)];
        BinaryPrimitives.WriteInt32LittleEndian(np.AsSpan(at.ValueAt), n + 1);
        BinaryPrimitives.WriteInt32LittleEndian(np.AsSpan(at.Start + 16), at.Size + 4);
        var rep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => np };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the costume's prop list couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add($"{e.Class} added to {dst.Exports[pd].ObjectName}'s props ({n + 1} now)");
        return e.Fit.PinPivot ? hold(output, e, pinBones, log) : output;
    }

    /// <summary>
    /// A prop the costume gets from its hero (its class in the hero's base package: Doctor Doom Classic's pistol and throne) taken
    /// off this costume's prop list, or put back on it (Kurt, 2026-10-28: the Target's Props list shows them; untick = left off).
    /// A costume without a list of its own is given one first (the hero's). <paramref name="classPath"/>: the class's path
    /// (marvelgamecontent.marvelattachment_drdoom_pistol). Verified; throws InvalidDataException with the reason.
    /// </summary>
    public static byte[] SetListed(byte[] package, string costumeFile, string classPath, bool listed, List<string> log)
    {
        var dst = Package.FromBytes(package);
        int pd = Array.FindIndex(dst.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
        if (pd < 0) throw new InvalidDataException("the package has no player class default (not a costume)");
        if (TagWalker.Walk(dst, dst.ReadExportBytes(dst.Exports[pd]).ToArray(), 4)?.Any(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)) != true)
            dst = WithOwnPropList(dst, pd, costumeFile, log);
        byte[] b = dst.ReadExportBytes(dst.Exports[pd]).ToArray();
        var at = TagWalker.Walk(dst, b, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidDataException("the costume's prop list doesn't read");
        var t = new Fx.FxTables(dst);
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at.ValueAt));
        var refs = Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at.ValueAt + 4 + 4 * k))).ToList();
        int had = refs.FindIndex(r => r != 0 && t.PathOf(r).Equals(classPath, StringComparison.OrdinalIgnoreCase));
        if (listed == (had >= 0)) return dst.RawFile;
        var addNames = new List<string>(); var addImports = new List<NewImport>();
        if (listed) refs.Add(ClassRef(dst, t, classPath, addNames, addImports)); else refs.RemoveAt(had);
        var value = new byte[4 + 4 * refs.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, refs.Count);
        for (int k = 0; k < refs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), refs[k]);
        byte[] nb = [.. b.AsSpan(0, at.ValueAt), .. value, .. b.AsSpan(at.End)];
        BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(at.Start + 16), value.Length);
        var rep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the costume's prop list couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add($"{classPath[(classPath.LastIndexOf('.') + 1)..]}: {(listed ? "back on" : "taken off")} {dst.Exports[pd].ObjectName}'s props ({refs.Count} now)");
        return FixClassImports(output, log);
    }

    /// <summary>A class by its path in <paramref name="dst"/>: an export or import there already, else imported (its outers as
    /// package imports; names and imports to add collected).</summary>
    static int ClassRef(Package dst, Fx.FxTables t, string path, List<string> addNames, List<NewImport> addImports)
    {
        for (int k = 0; k < dst.Exports.Length; k++) if (t.PathOf(k + 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return k + 1;
        for (int k = 0; k < dst.Imports.Length; k++) if (t.PathOf(-k - 1).Equals(path, StringComparison.OrdinalIgnoreCase)) return -k - 1;
        void Name(string n) { if (!dst.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) && !addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n); }
        string[] parts = path.Split('.');
        int outer = 0;
        for (int k = 0; k < parts.Length; k++)
        {
            string cn = k == parts.Length - 1 ? "Class" : "Package";
            // (an outer the package has as an export: its forced-export MarvelGameContent, which the game's own costumes import
            // their hero's classes under; a top-level package import of that name crashed the game, Kurt 2026-10-28)
            if (k < parts.Length - 1)
            {
                int ex = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && x.OuterIndex == outer && dst.ClassOf(x).Equals("Package", StringComparison.OrdinalIgnoreCase));
                if (ex >= 0) { outer = ex + 1; continue; }
            }
            int found = Array.FindIndex(dst.Imports, im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer && im.ClassName.Equals(cn, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) { outer = -1 - found; continue; }
            int added = addImports.FindIndex(im => im.ObjectName.Equals(parts[k], StringComparison.OrdinalIgnoreCase) && im.OuterIndex == outer);
            if (added < 0)
            {
                foreach (string n in new[] { "Core", cn, parts[k] }) Name(n);
                addImports.Add(new NewImport("Core", cn, outer, parts[k]));
                added = addImports.Count - 1;
            }
            outer = -(dst.Imports.Length + added + 1);
        }
        return outer;
    }

    /// <summary>
    /// The prop classes in a costume package that the game can't load for this costume (Kurt, 2026-10-28: Doom kept crashing after
    /// Beta Ray Bill's hammer was removed: Remove only takes a prop off the list, its class stays, and it builds on Thor's hammer
    /// class): attachment classes imported under the package's MarvelGameContent that neither this package nor its hero's base
    /// package defines (the game loads those two for the costume). Empty when all is well.
    /// </summary>
    public static List<string> UnloadableClasses(byte[] package, string costumeFile)
    {
        var list = new List<string>();
        try
        {
            var pkg = Package.FromBytes(package);
            string stem = Path.GetFileNameWithoutExtension(costumeFile);
            var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(UC__MarvelPlayer_[^_]+)_.+_SF$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            HashSet<string> heroClasses = new(StringComparer.OrdinalIgnoreCase);
            if (m.Success && StockFiles.Cooked is string ck && StockFiles.For(ck, m.Groups[1].Value + "_SF.upk") is var bp && File.Exists(bp))
            {
                var b = Package.Open(bp);
                foreach (var e in b.Exports) if (b.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)) heroClasses.Add(e.ObjectName);
            }
            else if (m.Success) return list;   // (the hero's package isn't readable: nothing to compare with)
            var own = pkg.Exports.Where(e => pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)).Select(e => e.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < pkg.Imports.Length; i++)
            {
                var im = pkg.Imports[i];
                if (!im.ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase) || !im.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
                string outer = im.OuterIndex > 0 ? pkg.Exports[im.OuterIndex - 1].ObjectName : im.OuterIndex < 0 ? pkg.Imports[-im.OuterIndex - 1].ObjectName : "";
                if (!outer.Equals("marvelgamecontent", StringComparison.OrdinalIgnoreCase)) continue;
                if (!own.Contains(im.ObjectName) && !heroClasses.Contains(im.ObjectName)) list.Add(im.ObjectName);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return list;
    }

    /// <summary>
    /// Class imports of a costume's prop list put under the package's own MarvelGameContent export (Kurt, 2026-10-28: Doctor Doom
    /// crashed the game after 0.60.206 made his prop list: its hero's classes were imported under a new top-level package import
    /// "marvelgamecontent", while the game's own costumes import them under the package's forced-export MarvelGameContent object;
    /// 77 of 77 checked, --prop-list-census). Each Class import under a top-level Package import that has the name of a top-level
    /// Package export is moved under that export (its outer index patched in the import table, kept byte for byte otherwise), and
    /// the stray package import is made a copy of the Core import (nothing is left that names a package the game doesn't have).
    /// Returns the bytes unchanged when there's nothing to put right. Verified by reading back.
    /// </summary>
    public static byte[] FixClassImports(byte[] file, List<string> log)
    {
        var pkg = Package.FromBytes(file);
        if (!ReferenceEquals(pkg.Body, pkg.RawFile) && pkg.Body.Length != pkg.RawFile.Length) return file;   // (compressed: never ours)
        var stray = new List<(int Import, int Export)>();
        for (int i = 0; i < pkg.Imports.Length; i++)
        {
            var im = pkg.Imports[i];
            if (im.OuterIndex != 0 || !im.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase)) continue;
            int ex = Array.FindIndex(pkg.Exports, x => x.OuterIndex == 0 && x.ObjectName.Equals(im.ObjectName, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("Package", StringComparison.OrdinalIgnoreCase));
            if (ex >= 0) stray.Add((i, ex));
        }
        if (stray.Count == 0) return file;
        int size = pkg.Imports.Length == 0 ? 0 : (pkg.ExportOffset - pkg.ImportOffset) / pkg.Imports.Length;
        if (size != 28 || pkg.ImportOffset + size * pkg.Imports.Length != pkg.ExportOffset) { log.Add("  the import table isn't laid out as expected: its class imports were left as they are"); return file; }
        int core = Array.FindIndex(pkg.Imports, im => im.OuterIndex == 0 && im.ObjectName.Equals("Core", StringComparison.OrdinalIgnoreCase) && im.ClassName.Equals("Package", StringComparison.OrdinalIgnoreCase));
        byte[] outb = (byte[])file.Clone();
        var t0 = new Fx.FxTables(pkg);
        var expect = new Dictionary<int, string>();
        int moved = 0;
        var fixedPkgs = new List<int>();
        foreach (var (imp, ex) in stray)
        {
            // only a package import holding prop classes (attachment classes) and nothing else: the game's own packages have other
            // top-level imports beside a forced export of the same name (chbasematerials), which stay as they are
            var children = Enumerable.Range(0, pkg.Imports.Length).Where(j => pkg.Imports[j].OuterIndex == -imp - 1).ToList();
            if (children.Count == 0 || !children.All(j => pkg.Imports[j].ClassName.Equals("Class", StringComparison.OrdinalIgnoreCase)
                    && pkg.Imports[j].ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (int j in children)
            {
                BinaryPrimitives.WriteInt32LittleEndian(outb.AsSpan(pkg.ImportOffset + j * size + 16), ex + 1);
                expect[-j - 1] = t0.PathOf(-j - 1);
                moved++;
            }
            if (core >= 0) Array.Copy(outb, pkg.ImportOffset + core * size, outb, pkg.ImportOffset + imp * size, size);
            fixedPkgs.Add(ex);
        }
        if (moved == 0) return file;
        // read back: every moved import keeps its path, now under the export
        var back = Package.FromBytes(outb);
        var t1 = new Fx.FxTables(back);
        foreach (var (r, path) in expect)
            if (!t1.PathOf(r).Equals(path, StringComparison.OrdinalIgnoreCase) || back.Imports[-r - 1].OuterIndex <= 0)
                throw new InvalidDataException($"the prop list's class import {path} didn't read back under the package's own object");
        log.Add($"  {moved} prop class import(s) put under the package's own {string.Join(", ", fixedPkgs.Select(x => pkg.Exports[x].ObjectName).Distinct())} (as the game's costumes import their hero's props)");
        return outb;
    }

    /// <summary>
    /// A costume class default without mAttachmentClasses (it keeps its hero's: Doctor Doom Classic) given a list of its own: the
    /// hero's (its base package's player default; classes there referenced by import), so a prop can be added to it without
    /// taking the hero's away. Verified and read back; throws InvalidDataException with the reason.
    /// </summary>
    static Package WithOwnPropList(Package dst, int pd, string costumeFile, List<string> log)
    {
        // the hero's base package: UC__MarvelPlayer_<Hero>_SF
        string stem = Path.GetFileNameWithoutExtension(costumeFile);
        const string Pre = "UC__MarvelPlayer_";
        if (!stem.StartsWith(Pre, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("this package's character keeps its prop list elsewhere: not a costume");
        string baseFile = $"{Pre}{stem[Pre.Length..].Split('_')[0]}_SF.upk";
        var heroPaths = new List<string>();
        if (!baseFile.Equals(costumeFile, StringComparison.OrdinalIgnoreCase) && StockFiles.Cooked is string cooked && StockFiles.For(cooked, baseFile) is var basePath && File.Exists(basePath))
        {
            var bp = Package.Open(basePath);
            var bt = new Fx.FxTables(bp);
            int bd = Array.FindIndex(bp.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && bp.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
            if (bd >= 0)
            {
                byte[] bb = bp.ReadExportBytes(bp.Exports[bd]).ToArray();
                if (TagWalker.Walk(bp, bb, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)) is { } bl)
                {
                    int bn = BinaryPrimitives.ReadInt32LittleEndian(bb.AsSpan(bl.ValueAt));
                    for (int k = 0; k < bn; k++) if (BinaryPrimitives.ReadInt32LittleEndian(bb.AsSpan(bl.ValueAt + 4 + 4 * k)) is int r && r != 0) heroPaths.Add(bt.PathOf(r));
                }
            }
        }
        var t = new Fx.FxTables(dst);
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return dst.Names.Length + j;
        }
        var addImports = new List<NewImport>();
        int RefOf(string path) => ClassRef(dst, t, path, addNames, addImports);
        var refs = heroPaths.Select(RefOf).ToList();
        var value = new byte[4 + 4 * refs.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, refs.Count);
        for (int k = 0; k < refs.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), refs[k]);
        byte[] b = dst.ReadExportBytes(dst.Exports[pd]).ToArray();
        var tags = TagWalker.Walk(dst, b, 4) ?? throw new InvalidDataException($"{dst.Exports[pd].ObjectName} doesn't read");
        var tag = new byte[24 + value.Length];
        BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("mAttachmentClasses"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), value.Length);
        value.CopyTo(tag.AsSpan(24));
        byte[] nb = [.. b.AsSpan(0, tags.NoneAt), .. tag, .. b.AsSpan(tags.NoneAt)];
        var rep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames, addImports);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames, addImports);
        if (problems.Count > 0) throw new InvalidDataException("the costume's own prop list couldn't be made: " + string.Join("; ", problems.Take(2)));
        var back = Package.FromBytes(output);
        var bt2 = new Fx.FxTables(back);
        byte[] rb = back.ReadExportBytes(back.Exports[pd]).ToArray();
        var rl = TagWalker.Walk(back, rb, 4)?.FirstOrDefault(x => x.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase))
                 ?? throw new InvalidDataException("the costume's own prop list didn't read back");
        var got = Enumerable.Range(0, BinaryPrimitives.ReadInt32LittleEndian(rb.AsSpan(rl.ValueAt))).Select(k => bt2.PathOf(BinaryPrimitives.ReadInt32LittleEndian(rb.AsSpan(rl.ValueAt + 4 + 4 * k)))).ToList();
        if (!got.SequenceEqual(heroPaths, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"the costume's own prop list reads back as [{string.Join(", ", got)}], not its hero's");
        back = Package.FromBytes(FixClassImports(back.RawFile, log));
        log.Add($"{dst.Exports[pd].ObjectName} kept its hero's prop list: given one of its own, the hero's {heroPaths.Count}{(heroPaths.Count > 0 ? " (" + string.Join(", ", heroPaths.Select(x => x.Split('.').Last())) + ")" : "")}{(addImports.Count > 0 ? $"; {addImports.Count} import(s) added" : "")}");
        return back;
    }

    /// <summary>
    /// An added prop is held all the time (Kurt, 2026-10-26: Angela's axe added to Thor never showed): a prop its own hero shows
    /// only while a power calls for it (VisibilityPoint visible_on_demand: Angela's axes, her powers switch their slots in) has
    /// that setting dropped in the copy, so it shows as Thor's hammer does (it has none). The copy only; Angela's are unchanged.
    /// </summary>
    static Package AlwaysHeld(Package dst, string def, List<string> log)
    {
        int d = Find(dst, def);
        if (d < 0) return dst;
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        if (TagWalker.Walk(dst, b, 4)?.FirstOrDefault(t => t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase)) is not { } vp
            || !TagWalker.NameAt(dst, b, vp.ValueAt).Contains("on_demand", StringComparison.OrdinalIgnoreCase)) return dst;
        byte[] nd = [.. b.AsSpan(0, vp.Start), .. b.AsSpan(vp.End)];
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the prop couldn't be set to show all the time: " + string.Join("; ", problems.Take(2)));
        var back = Package.FromBytes(output);
        if (TagWalker.Walk(back, back.ReadExportBytes(back.Exports[d]).ToArray(), 4)?.Any(t => t.Name.Equals("VisibilityPoint", StringComparison.OrdinalIgnoreCase)) != false)
            throw new InvalidDataException("the prop couldn't be set to show all the time (it didn't read back)");
        log.Add($"{dst.Exports[d].ObjectName}: held all the time (its own hero shows it only for some powers)");
        return back;
    }

    /// <summary>
    /// The prop taken off the costume again: out of its props list (its copied class and models stay in the package, unused).
    /// </summary>
    public static byte[] RemoveProp(byte[] package, string cls, List<string> log)
    {
        var dst = Package.FromBytes(package);
        int ce = HeldClass(dst, cls.ToLowerInvariant());
        int pd = Array.FindIndex(dst.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
        if (ce < 0 || pd < 0) return package;
        byte[] pb = dst.ReadExportBytes(dst.Exports[pd]).ToArray();
        if (TagWalker.Walk(dst, pb, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)) is not { } at) return package;
        int n = BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt));
        var refs = Enumerable.Range(0, n).Select(k => BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt + 4 + 4 * k))).ToList();
        if (!refs.Remove(ce + 1)) return package;
        byte[] value = [.. BitConverter.GetBytes(refs.Count), .. refs.SelectMany(BitConverter.GetBytes)];
        byte[] np = [.. pb.AsSpan(0, at.ValueAt), .. value, .. pb.AsSpan(at.End)];
        BinaryPrimitives.WriteInt32LittleEndian(np.AsSpan(at.Start + 16), value.Length);
        var rep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => np };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the costume's prop list couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add($"{cls} taken off {dst.Exports[pd].ObjectName}'s props");
        return output;
    }

    /// <summary>
    /// An added prop's Pivot on the Hand (Kurt, 2026-10-23: Thor's fidget01 on Cap moved the palm, and the hammer, away from his
    /// hand): the animation sets this costume took from other characters (the Animations tab's copies, named …_on_…) stop
    /// (<paramref name="pin"/>) or start again taking positions for <paramref name="bones"/>; they still turn them.
    /// </summary>
    public static byte[] PinPropBones(byte[] package, IReadOnlyList<string> bones, bool pin, List<string> log)
    {
        if (bones.Count == 0) return package;
        var p = Package.FromBytes(package);
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return p.Names.Length + j;
        }
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var sets = new List<string>();
        for (int i = 0; i < p.Exports.Length; i++)
        {
            if (!p.ClassOf(p.Exports[i]).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) || !p.Exports[i].ObjectName.Contains("_on_", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(p.Exports[i]).ToArray();
            bool changed = false;
            foreach (string b in bones)
            {
                if (b.Equals("none", StringComparison.OrdinalIgnoreCase) || b.Equals("root", StringComparison.OrdinalIgnoreCase)) continue;
                if (pin) { if (AnimSwap.WithoutTranslationBone(p, d, b) is { } nd) { d = nd; changed = true; } }
                else { byte[] nd = AnimSwap.WithTranslationBone(p, d, b, NameIdx); if (nd.Length != d.Length) { d = nd; changed = true; } }
            }
            if (!changed) continue;
            byte[] keep = d;
            rep[i] = _ => keep;
            sets.Add(p.Exports[i].ObjectName);
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the pivot couldn't be " + (pin ? "kept on the hand: " : "let go again: ") + string.Join("; ", problems.Take(2)));
        log.Add($"{string.Join(", ", bones)}: {(pin ? "not moved" : "moved again")} by {string.Join(", ", sets)}");
        return output;
    }

    /// <summary>The bones a held prop hangs on, in the package (its class as Save may have renamed it).</summary>
    public static List<string> HeldPropBones(byte[] package, string cls)
    {
        var p = Package.FromBytes(package);
        int c = HeldClass(p, cls.ToLowerInvariant());
        return c < 0 ? [] : AttachBones(p, "marvelgamecontent.default__" + p.Exports[c].ObjectName.ToLowerInvariant());
    }

    /// <summary>
    /// A prop class of the package: <paramref name="cls"/> itself, else one Save's Make Unique renamed (<paramref name="cls"/> +
    /// "_" + the mod's tag) that the costume's own props list. -1 when there's none.
    /// </summary>
    /// <summary>An added prop's class in a package (renamed there to <c>&lt;class&gt;_on_&lt;costume&gt;</c>, or as it is) is
    /// the added prop <paramref name="added"/> (its class's own name).</summary>
    public static bool SameClass(string inPackage, string added) =>
        inPackage.Equals(added, StringComparison.OrdinalIgnoreCase) || inPackage.StartsWith(added + "_on_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The class just copied in renamed with its default to <c>&lt;class&gt;_on_&lt;costume&gt;</c> (as Make Unique renames shared
    /// attachment classes, confirmed in game 2026-10-11). Verified; the new names come back in <paramref name="newCls"/> and
    /// <paramref name="newDef"/>.
    /// </summary>
    /// <summary>An added prop's class name in a costume: &lt;class&gt;_on_&lt;costume&gt; (drdoom_classic).</summary>
    static string OwnName(string cls, string costumeFile)
    {
        string stem = Path.GetFileNameWithoutExtension(costumeFile);
        foreach (string pre in new[] { "UC__MarvelPlayer_", "UC__MarvelTeamUp_", "UC__" }) if (stem.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) { stem = stem[pre.Length..]; break; }
        if (stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase)) stem = stem[..^3];
        return $"{cls.ToLowerInvariant()}_on_{new string(stem.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray())}";
    }

    static Package OwnClassName(Package dst, string cls, string def, string costumeFile, List<string> log, out string newCls, out string newDef)
    {
        newCls = cls; newDef = def;
        string name = OwnName(cls, costumeFile);
        int ce = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase));
        int d = Find(dst, def);
        if (ce < 0 || d < 0 || dst.Exports.Any(x => x.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase))) return dst;
        var renames = new Dictionary<int, string> { [ce] = name, [d] = "default__" + name };
        var addNames = renames.Values.Where(n => !dst.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        byte[] output = PackageRebuilder.Rebuild(dst, new Dictionary<int, Func<long, byte[]>>(), [], out var written, addNames, null, renames);
        var problems = PackageRebuilder.Verify(dst, output, [], [], written, addNames, null, renames);
        if (problems.Count > 0) throw new InvalidDataException("the prop's class couldn't be given a name of its own: " + string.Join("; ", problems.Take(2)));
        var back = Package.FromBytes(output);
        if (Find(back, "marvelgamecontent.default__" + name) < 0) throw new InvalidDataException("the prop's class didn't read back under its own name");
        log.Add($"  {cls} → {name} (a name of its own: the game knows a class by its path, so another copy of it would win)");
        newCls = name; newDef = "marvelgamecontent.default__" + name;
        return back;
    }

    /// <summary>
    /// An added prop's model (its character's, which the copies are made from) given a name of its own in this costume
    /// (&lt;model&gt;_on_&lt;costume&gt;), as its class is (Monarch, 2026-10-29: every mod with an added prop held Angela's axe at
    /// angela_weapons.angelaaxe_left, so two such mods "changed the same model", and the copy loaded first is the one the game
    /// uses for that path: Angela's own, another mod's). A model renamed already, or another prop's renamed copy of it, is used.
    /// </summary>
    static Package OwnModelName(Package dst, string classDefault, string costumeFile, List<string> log)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return dst;
        byte[] db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        if (TagWalker.Walk(dst, db, 4)?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase)) is not { } mt || BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt)) < 1) return dst;
        int mesh = BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt + 4)) - 1;
        if (mesh < 0 || !dst.ClassOf(dst.Exports[mesh]).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase)) return dst;
        bool keep = false;
        // (its own model, <model>_m12345, is named from its bytes already: the original it was made from beside it is renamed)
        if (System.Text.RegularExpressions.Regex.Match(dst.Exports[mesh].ObjectName, @"^(.+)_m\d+$") is { Success: true } mm)
        {
            mesh = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(mm.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase));
            if (mesh < 0) return dst;
            keep = true;   // (the attachment keeps its model)
        }
        string name = dst.Exports[mesh].ObjectName;
        string own = OwnName(name, costumeFile);
        if (name.Contains("_on_", StringComparison.OrdinalIgnoreCase)) return dst;
        Dictionary<int, Func<long, byte[]>> rep = [];
        Dictionary<int, string>? renames = null;
        List<string> addNames = [];
        int had = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(own, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase));
        if (had >= 0)
        {
            if (keep) return dst;
            BinaryPrimitives.WriteInt32LittleEndian(db.AsSpan(mt.ValueAt + 4), had + 1);
            rep[d] = _ => db;
        }
        else
        {
            renames = new() { [mesh] = own };
            if (!dst.Names.Any(x => x.Equals(own, StringComparison.OrdinalIgnoreCase))) addNames.Add(own);
        }
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames, null, renames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames, null, renames);
        if (problems.Count > 0) throw new InvalidDataException("the prop's model couldn't be given a name of its own: " + string.Join("; ", problems.Take(2)));
        log.Add($"  {name} → {own} (a name of its own: the game knows a model by its path, so another copy of it would win)");
        return Package.FromBytes(output);
    }

    static int HeldClass(Package p, string cls)
    {
        bool IsClass(int i) => p.ClassOf(p.Exports[i]).Equals("Class", StringComparison.OrdinalIgnoreCase);
        int exact = Array.FindIndex(p.Exports, x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0 && IsClass(exact)) return exact;
        int pd = Array.FindIndex(p.Exports, x => x.ObjectName.StartsWith("default__marvelplayer", StringComparison.OrdinalIgnoreCase) && p.ClassOf(x).StartsWith("marvelplayer", StringComparison.OrdinalIgnoreCase));
        if (pd < 0) return -1;
        byte[] pb = p.ReadExportBytes(p.Exports[pd]).ToArray();
        if (TagWalker.Walk(p, pb, 4)?.FirstOrDefault(t => t.Name.Equals("mAttachmentClasses", StringComparison.OrdinalIgnoreCase)) is not { } at) return -1;
        int n = BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt));
        for (int k = 0; k < n; k++)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt + 4 + 4 * k));
            if (r > 0 && p.Exports[r - 1].ObjectName.StartsWith(cls + "_", StringComparison.OrdinalIgnoreCase) && IsClass(r - 1)) return r - 1;
        }
        return -1;
    }

    /// <summary>The bones of the costume's own model (CostumeMesh), else of its first skeletal mesh.</summary>
    static HashSet<string>? CostumeBones(Package p)
    {
        try
        {
            var ap = AnimExportCli.Packages.Package.Read(p.RawFile);
            var meshes = ap.FindExportsOfClass(AnimExportCli.Meshes.SkeletalMeshReader.ClassName).ToList();
            if (meshes.Count == 0) return null;
            int pick = meshes.OrderByDescending(i => AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i)?.Bones.Count ?? 0).First();
            return AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, pick)?.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The class default's model pointed back at the original it was fitted from (FitModel names its copies
    /// "&lt;original&gt;_x&lt;size&gt;[_fit&lt;n&gt;]").</summary>
    static Package WithOriginalModel(Package dst, string classDefault, List<string> log)
    {
        int d = Find(dst, classDefault);
        byte[] db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        if (TagWalker.Walk(dst, db, 4)?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase)) is not { } mt || BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt)) < 1) return dst;
        int mesh = BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt + 4));
        if (mesh <= 0) return dst;
        string name = dst.Exports[mesh - 1].ObjectName;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.+?)_x\d+(_fit\d+)?$");
        if (!m.Success) return dst;
        int orig = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase));
        if (orig < 0) return dst;
        BinaryPrimitives.WriteInt32LittleEndian(db.AsSpan(mt.ValueAt + 4), orig + 1);
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => db };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        return PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written).Count == 0 ? Package.FromBytes(output) : dst;
    }

    /// <summary>
    /// The model an added prop is drawn with (Kurt, 2026-10-27: an added prop given a new model on the Model tab): the model in
    /// <paramref name="modelFrom"/> (the build's package: the same class default, its ModelMesh the new model) copied in under
    /// a name of its own (&lt;its character's model&gt;_m&lt;number from that model's bytes&gt;: the same model, the same copy), or with
    /// none, its character's own model again. The attachment's ModelMesh points at it; the fit is made from it after.
    /// </summary>
    static Package WithModel(Package dst, string classDefault, string? modelFrom, List<string> log, string? otherDefault = null)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return dst;
        byte[] db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        if (TagWalker.Walk(dst, db, 4)?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase)) is not { } mt || BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt)) < 1)
        {
            if (modelFrom != null) log.Add("  the attachment names no model of its own (ModelMesh): its new model isn't used");
            return dst;
        }
        int mesh = BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt + 4));
        if (mesh <= 0) return dst;
        string name = dst.Exports[mesh - 1].ObjectName, own = System.Text.RegularExpressions.Regex.Replace(name, @"_m\d+$", "");
        static bool IsMesh(Package p, int i) => p.ClassOf(p.Exports[i]).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase);
        Package? src = null; int srcMesh = -1;
        string want = own;
        if (modelFrom != null)
        {
            src = Package.Open(modelFrom);
            int sd = Find(src, classDefault);
            if (sd < 0 && otherDefault != null) sd = Find(src, otherDefault);   // (a model built before the class had a name of its own)
            if (sd < 0 && otherDefault != null)   // (or under another costume's name for it)
            {
                string stem = otherDefault[(otherDefault.LastIndexOf('.') + 1)..] + "_on_";
                sd = Array.FindIndex(src.Exports, x => x.ObjectName.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && !src.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase));
            }
            if (sd < 0)
                throw new InvalidDataException($"its new model isn't in {Path.GetFileName(modelFrom)} (no {classDefault[(classDefault.LastIndexOf('.') + 1)..]} there; its attachment defaults: {string.Join(", ", src.Exports.Where(x => x.ObjectName.StartsWith("default__marvelattachment", StringComparison.OrdinalIgnoreCase)).Select(x => x.ObjectName).Take(6))})");
            byte[] sb = sd >= 0 ? src.ReadExportBytes(src.Exports[sd]).ToArray() : [];
            if (sd < 0 || TagWalker.Walk(src, sb, 4)?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase)) is not { } smt
                || BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(smt.ValueAt)) < 1 || (srcMesh = BinaryPrimitives.ReadInt32LittleEndian(sb.AsSpan(smt.ValueAt + 4)) - 1) < 0 || !IsMesh(src, srcMesh))
                throw new InvalidDataException($"its new model isn't in {Path.GetFileName(modelFrom)}");
            uint h = 2166136261;
            foreach (byte c in src.ReadExportBytes(src.Exports[srcMesh])) h = (h ^ c) * 16777619;
            want = $"{own}_m{h % 100000}";
        }
        if (name.Equals(want, StringComparison.OrdinalIgnoreCase)) return dst;
        int target = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(want, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase));
        if (target < 0)
        {
            if (src == null) { log.Add($"  its character's model {want} isn't in the package any more: the model stays {name}"); return dst; }
            var s = src;
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(s, srcMesh, dst, [], want), out string said)
                ?? throw new InvalidDataException("its new model couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(cc.Output);
            target = cc.RootRef - 1;
            db = dst.ReadExportBytes(dst.Exports[d]).ToArray();   // (the copy went to the end: the default's index is unchanged)
        }
        BinaryPrimitives.WriteInt32LittleEndian(db.AsSpan(mt.ValueAt + 4), target + 1);
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => db };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the prop's model couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add(modelFrom != null ? $"  {dst.Exports[d].ObjectName}: its own model {want}" : $"  {dst.Exports[d].ObjectName}: its character's model {want} again");
        return Package.FromBytes(output);
    }

    /// <summary>
    /// An added prop's fit as its model gets it (FitModel: the size, then the turn and move baked into a plain prop's
    /// vertices), for the preview to show a model that isn't built in yet as it will be.
    /// </summary>
    public static System.Numerics.Matrix4x4 FitMatrix(PropFit f, string cls)
    {
        static int Units(float deg) => (int)MathF.Round(deg / 360f * 65536f);
        float scale = f.Scale is float fs && fs > 0 ? fs : 1;
        var turn = new System.Numerics.Vector3(f.TurnX, f.TurnY, f.TurnZ == 0 && f.IsIdentity ? DefaultTurnZ(cls) : f.TurnZ);
        return System.Numerics.Matrix4x4.CreateScale(scale) * ModMeshes.Rotator(Units(turn.Y), Units(turn.Z), Units(turn.X))
            * System.Numerics.Matrix4x4.CreateTranslation(f.X, f.Y, f.Z);
    }

    /// <summary>The weapon slot a prop class is held in (its class default's WeaponSlot), or null.</summary>
    public static string? PropSlot(string package, string cls)
    {
        var p = Package.Open(package);
        int d = Find(p, "marvelgamecontent.default__" + cls.ToLowerInvariant());
        if (d < 0) return null;
        byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
        return TagWalker.Walk(p, b, 4)?.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase)) is { } t && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt)) > 0
            ? TagWalker.NameAt(p, b, t.ValueAt + 4) : null;
    }

    /// <summary>The class default of attachment <paramref name="cls"/> in the package (renamed by Make Unique too), or -1.</summary>
    static int AttachDefault(Package p, string cls)
    {
        string c = cls.ToLowerInvariant();
        if (HeldClass(p, c) is int had && had >= 0) c = p.Exports[had].ObjectName;
        return Array.FindIndex(p.Exports, x => x.ObjectName.Equals("default__" + c, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The bones attachment <paramref name="cls"/>'s own class default names (empty: none of its own: its parent
    /// class's), or null when its class default isn't in the package.</summary>
    public static List<string>? HeldBones(byte[] package, string cls)
    {
        var p = Package.FromBytes(package);
        int d = AttachDefault(p, cls);
        return d < 0 ? null : AttachBones(p, p.PathOf(p.Exports[d]));
    }

    /// <summary>
    /// A prop held on other bones (Kurt, 2026-10-27: the Model tab's Held On): its class default's AttachmentBones set to
    /// <paramref name="bones"/> (empty: the property taken out, so its parent class's apply again). Only this package changes.
    /// </summary>
    public static byte[] SetHeldBones(byte[] package, string cls, IReadOnlyList<string> bones, List<string> log)
    {
        var p = Package.FromBytes(package);
        int d = AttachDefault(p, cls);
        if (d < 0) throw new InvalidDataException($"{cls}'s settings (its class default) aren't in this package: it's another package's prop");
        if (AttachBones(p, p.PathOf(p.Exports[d])).SequenceEqual(bones, StringComparer.OrdinalIgnoreCase)) return package;
        return WithAttachBones(p, d, bones, log).RawFile;
    }

    static Package WithAttachBones(Package dst, int d, IReadOnlyList<string> bones, List<string> log)
    {
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var tags = TagWalker.Walk(dst, b, 4) ?? throw new InvalidDataException($"{dst.Exports[d].ObjectName} doesn't read");
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            int j = addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (j < 0) { addNames.Add(n); j = addNames.Count - 1; }
            return dst.Names.Length + j;
        }
        // the value: count, then each bone's name (index, number)
        var value = new byte[4 + 8 * bones.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, bones.Count);
        for (int k = 0; k < bones.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 8 * k), NameIdx(bones[k]));
        byte[] nb;
        if (tags.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase)) is { } at)
        {
            if (bones.Count == 0) nb = [.. b.AsSpan(0, at.Start), .. b.AsSpan(at.End)];
            else
            {
                nb = [.. b.AsSpan(0, at.ValueAt), .. value, .. b.AsSpan(at.End)];
                BinaryPrimitives.WriteInt32LittleEndian(nb.AsSpan(at.Start + 16), value.Length);
            }
        }
        else if (bones.Count == 0) return dst;
        else
        {
            var tag = new byte[24 + value.Length];
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("AttachmentBones"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), value.Length);
            value.CopyTo(tag.AsSpan(24));
            int none = tags.NoneAt;   // (new tags go before None)
            nb = [.. b.AsSpan(0, none), .. tag, .. b.AsSpan(none)];
        }
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the prop's bone couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add($"  {dst.Exports[d].ObjectName}: held on {(bones.Count > 0 ? string.Join(", ", bones) : "its parent class's bone")}");
        return Package.FromBytes(output);
    }

    /// <summary>The bones a class default names in AttachmentBones (none: empty).</summary>
    static List<string> AttachBones(Package p, string classDefault)
    {
        var list = new List<string>();
        int d = Find(p, classDefault);
        if (d < 0) return list;
        byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
        if (TagWalker.Walk(p, b, 4)?.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase)) is not { } t) return list;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
        for (int k = 0; k < n && t.ValueAt + 12 + 8 * k <= t.End; k++) list.Add(TagWalker.NameAt(p, b, t.ValueAt + 4 + 8 * k));
        return list;
    }

    /// <summary>A hero's main model's height (its base package's skeletal mesh with the most vertices: top minus bottom of
    /// its highest-detail vertices), else null.</summary>
    internal static float? HeroHeight(string heroPackage)
    {
        try
        {
            var pos = MainMesh(heroPackage)?.HighestDetail?.Positions;
            return pos is { Count: > 0 } ? pos.Max(v => v.Z) - pos.Min(v => v.Z) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// TEST: a spawned attachment drawn <paramref name="scale"/> times its size: its class default's DrawScale (Actor's,
    /// default 1), set or added before its None. A hypothesis until seen in game.
    /// </summary>
    static Package ScaleAttachment(Package dst, string classDefault, float scale, string cooked, List<string> log)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return dst;
        // the model itself, scaled (ScaledModel); a scale on the attachment or its component isn't read by the game
        if (ScaledModel(dst, classDefault, scale, log) is { } scaled) return scaled;
        // (older tries, kept for the record: its own mesh component, scaled)
        byte[] b0 = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var mt = TagWalker.Walk(dst, b0, 4)?.FirstOrDefault(t => t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
        int mr = mt == null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(b0.AsSpan(mt.ValueAt));
        if (mr <= 0)
        {
            if (OwnMeshComponent(dst, classDefault, cooked, log) is not { } withOwn) return dst;
            dst = withOwn;
            d = Find(dst, classDefault);
            b0 = dst.ReadExportBytes(dst.Exports[d]).ToArray();
            mt = TagWalker.Walk(dst, b0, 4)?.FirstOrDefault(t => t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
            mr = mt == null ? 0 : BinaryPrimitives.ReadInt32LittleEndian(b0.AsSpan(mt.ValueAt));
            if (mr <= 0) return dst;
        }
        int comp = mr - 1;
        byte[] cd = dst.ReadExportBytes(dst.Exports[comp]).ToArray();
        var ct = TagWalker.Walk(dst, cd, 16);
        if (ct == null) { log.Add($"  {dst.Exports[comp].ObjectName}: its properties don't read: not scaled"); return dst; }
        const string ScaleName = "Scale";
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] ncd;
        if (ct.FirstOrDefault(t => t.Name.Equals(ScaleName, StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st)
        {
            ncd = cd.ToArray();
            BinaryPrimitives.WriteSingleLittleEndian(ncd.AsSpan(st.ValueAt), BinaryPrimitives.ReadSingleLittleEndian(cd.AsSpan(st.ValueAt)) * scale);
        }
        else
        {
            var tag = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx(ScaleName));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("FloatProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
            BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), scale);
            ncd = [.. cd.AsSpan(0, ct.NoneAt), .. tag, .. cd.AsSpan(ct.NoneAt)];
        }
        var rep = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => ncd };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the vehicle's scale: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {dst.Exports[d].ObjectName}: drawn x{scale:0.00} (this hero's height over the other's)");
        return Package.FromBytes(output);
    }

    /// <summary>
    /// TEST: the attachment drawn <paramref name="scale"/> times its size by its model itself: its ModelMesh copied under a
    /// name of its own (Cyclops' bike model is Wolverine's wolverine_bike: the stock path must stay as it is) with every
    /// position scaled (bounds, mesh origin, bones' rest positions, vertices or a packed buffer's origin / extension), and
    /// ModelMesh pointed at the copy. The game builds the attachment's component from ModelMesh in native code
    /// (AMarvelAttachment_Models::CreateAttachments) and reads no scale from the attachment (Kurt, 2026-10-20: DrawScale and
    /// an own component's Scale both left the bike as it was).
    /// </summary>
    static Package? ScaledModel(Package dst, string classDefault, float scale, List<string> log) => FitModel(dst, classDefault, scale, default, default, log);

    /// <summary>
    /// A prop drawn <paramref name="scale"/> times its size, moved by <paramref name="move"/> and turned by
    /// <paramref name="turn"/> (degrees about X, Y, Z) by its model itself: a copy of its ModelMesh (a name of its own) with
    /// every position scaled and its Origin / RotOrigin set (the engine draws a skeletal mesh turned by RotOrigin, then moved
    /// by Origin: Blade's motorcycle's yaw 90), and the attachment's ModelMesh pointed at the copy.
    /// </summary>
    /// <summary>The same number for the same move and turn in every run (HashCode.Combine changes per process: a fit saved again in
    /// another session made another copy of the model).</summary>
    static uint StableFitHash(System.Numerics.Vector3 move, System.Numerics.Vector3 turn)
    {
        uint h = 2166136261;
        foreach (char c in FormattableString.Invariant($"{move.X:0.##},{move.Y:0.##},{move.Z:0.##},{turn.X:0.##},{turn.Y:0.##},{turn.Z:0.##}")) h = (h ^ c) * 16777619;
        return h;
    }

    static Package? FitModel(Package dst, string classDefault, float scale, System.Numerics.Vector3 move, System.Numerics.Vector3 turn, List<string> log)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return null;
        byte[] db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var mt = TagWalker.Walk(dst, db, 4)?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase));
        // the model: ModelMesh's first (Cyclops' bike), else the SkeletalMesh of the attachment's own mesh component (Green
        // Goblin's glider names its model there)
        int holder = d, at = -1;
        if (mt != null && BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mt.ValueAt)) >= 1) at = mt.ValueAt + 4;
        else if (TagWalker.Walk(dst, db, 4)?.FirstOrDefault(t => t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } mc
                 && BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(mc.ValueAt)) is int comp && comp > 0)
        {
            byte[] cb = dst.ReadExportBytes(dst.Exports[comp - 1]).ToArray();
            if (TagWalker.Walk(dst, cb, 16)?.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } sm) { holder = comp - 1; at = sm.ValueAt; db = cb; }
        }
        if (at < 0) { log.Add("  the attachment names no model: not scaled"); return null; }
        int mesh = BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(at));
        if (mesh <= 0) { log.Add("  the attachment's model isn't in this package: not scaled"); return null; }
        bool moved = move != default || turn != default;
        // A plain (not animated) prop: the move and turn baked into its vertices (Kurt, 2026-10-21: Thor's hammer on
        // Spider-Man took its size in game but not the Origin / RotOrigin offset; a plain prop is drawn in its rest pose,
        // where the skinning leaves the vertices as stored). An animated one (a vehicle, the glider) is drawn by its own
        // animation's root: its model's Origin / RotOrigin (the glider's moved in game).
        bool bake = !dst.Exports[d].ObjectName.Contains("marvelattachmentanimated", StringComparison.OrdinalIgnoreCase)
                    && TagWalker.Walk(dst, dst.ReadExportBytes(dst.Exports[d]).ToArray(), 4)?.Any(t => t.Name.Equals("AnimationSet", StringComparison.OrdinalIgnoreCase)) != true;
        string newName = $"{dst.Exports[mesh - 1].ObjectName}_x{(int)MathF.Round(scale * 100)}" + (moved ? $"_fit{StableFitHash(move, turn) % 100000}" : "");
        // a fitted copy of that name there already (the same fit applied again: an added prop on Save): used as it is
        int had = Array.FindIndex(dst.Exports, x => x.ObjectName.Equals(newName, StringComparison.OrdinalIgnoreCase) && dst.ClassOf(x).Equals(AnimExportCli.Meshes.SkeletalMeshReader.ClassName, StringComparison.OrdinalIgnoreCase));
        if (had >= 0)
        {
            byte[] hb = dst.ReadExportBytes(dst.Exports[holder]).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(hb.AsSpan(at), had + 1);
            var hrep = new Dictionary<int, Func<long, byte[]>> { [holder] = _ => hb };
            byte[] hout = PackageRebuilder.Rebuild(dst, hrep, [], out var hwritten);
            if (PackageRebuilder.Verify(dst, hout, hrep.Keys.ToList(), [], hwritten).Count > 0) return null;
            log.Add($"  {dst.Exports[d].ObjectName}: its fitted model {newName} (already there)");
            return Package.FromBytes(hout);
        }
        var cc = CrossMove.Quiet(() => ExportCopy.Copy(dst, mesh - 1, dst, [], newName), out string said);
        if (cc == null) { log.Add("  the model couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim()))); return null; }
        dst = Package.FromBytes(cc.Output);
        int copy = cc.RootRef - 1;
        // the copy's positions, scaled
        var ap = AnimExportCli.Packages.Package.Read(dst.RawFile);
        var where = AnimExportCli.Meshes.SkeletalMeshReader.PositionSpots(ap, copy);
        if (where == null) { log.Add("  the model's copy doesn't read: not scaled"); return null; }
        byte[] md = dst.ReadExportBytes(dst.Exports[copy]).ToArray();
        void Mul(int at) => BinaryPrimitives.WriteSingleLittleEndian(md.AsSpan(at), BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(at)) * scale);
        static int Units(float deg) => (int)MathF.Round(deg / 360f * 65536f);
        int verts = 0;
        foreach (var w in where)
            switch (w.Kind)
            {
                case "vec": Mul(w.At); Mul(w.At + 4); Mul(w.At + 8); break;
                case "radius": Mul(w.At); break;
                case "verts": for (int k = 0; k < w.Count; k++) { int va = w.At + k * w.Stride; Mul(va); Mul(va + 4); Mul(va + 8); } verts += w.Count; break;
                case "origin":
                    Mul(w.At); Mul(w.At + 4); Mul(w.At + 8);
                    if (bake) break;
                    for (int c = 0; c < 3; c++) BinaryPrimitives.WriteSingleLittleEndian(md.AsSpan(w.At + 4 * c), BinaryPrimitives.ReadSingleLittleEndian(md.AsSpan(w.At + 4 * c)) + (c == 0 ? move.X : c == 1 ? move.Y : move.Z));
                    break;
                case "rotorigin":
                    if (bake) break;
                    // (pitch about Y, yaw about Z, roll about X; 65536 a turn)
                    BinaryPrimitives.WriteInt32LittleEndian(md.AsSpan(w.At), BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(w.At)) + Units(turn.Y));
                    BinaryPrimitives.WriteInt32LittleEndian(md.AsSpan(w.At + 4), BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(w.At + 4)) + Units(turn.Z));
                    BinaryPrimitives.WriteInt32LittleEndian(md.AsSpan(w.At + 8), BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(w.At + 8)) + Units(turn.X));
                    break;
            }
        if (bake && moved) BakeMoveTurn(md, where, move, turn);
        // the attachment's model (ModelMesh's first, or its mesh component's SkeletalMesh): the copy (the copy went to the
        // end, so the holder's index is unchanged)
        db = dst.ReadExportBytes(dst.Exports[holder]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(db.AsSpan(at), copy + 1);
        bool noPhysics = false;   // (0.60.23 dropped the physics asset of a moved prop: that wasn't why the hammer stayed; reverted)
        var rep = new Dictionary<int, Func<long, byte[]>> { [copy] = _ => md, [holder] = _ => db };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) { log.Add("  the scaled model: " + string.Join("; ", problems.Take(3))); return null; }
        log.Add($"  {dst.Exports[d].ObjectName}: its model copied as {newName}, drawn x{scale:0.00}{(moved ? $", moved {move.X:0.#} / {move.Y:0.#} / {move.Z:0.#}, turned {turn.X:0.#} / {turn.Y:0.#} / {turn.Z:0.#} degrees" : "")}{(noPhysics ? ", without its physics asset" : "")} ({where.Count(w => w.Kind == "vec")} points and {verts} vertices scaled)");
        return Package.FromBytes(output);
    }

    /// <summary>
    /// TEST: gives an animated attachment's class default a mesh component of its own, for one that only inherits its
    /// parent class's (Cyclops' bike: ModelMesh and AnimationSet, no Mesh): a copy of Green Goblin's glider's
    /// (default__marvelattachmentanimated_greengoblinglider.initialskeletalmesh: SkeletalMesh, AnimTreeTemplate
    /// attachment_at, AnimSets) under this default, with its SkeletalMesh and AnimSets pointed at this attachment's own
    /// ModelMesh / AnimationSet; the default then names it (Mesh) and lists it (Components), as the glider's does.
    /// </summary>
    static Package? OwnMeshComponent(Package dst, string classDefault, string cooked, List<string> log)
    {
        string srcFile = StockFiles.For(cooked, "UC__MarvelPlayer_GreenGoblin_SF.upk");
        const string srcDefault = "marvelgamecontent.default__marvelattachmentanimated_greengoblinglider", srcComp = srcDefault + ".initialskeletalmesh";
        if (!File.Exists(srcFile)) { log.Add("  the mesh component's template package isn't found: not scaled"); return null; }
        var sp = Package.Open(srcFile);
        int si = Find(sp, srcComp);
        if (si < 0) { log.Add("  the mesh component's template isn't found: not scaled"); return null; }
        int d = Find(dst, classDefault);
        byte[] db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var dtags = TagWalker.Walk(dst, db, 4);
        int FirstRef(string name) => dtags?.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } t && BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(t.ValueAt)) > 0 ? BinaryPrimitives.ReadInt32LittleEndian(db.AsSpan(t.ValueAt + 4)) : 0;
        int mesh = FirstRef("ModelMesh"), set = FirstRef("AnimationSet");
        if (mesh == 0) { log.Add("  the attachment names no model: not scaled"); return null; }
        var cc = CrossMove.Quiet(() => ExportCopy.Copy(sp, si, dst, [], null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [srcDefault] = classDefault }), out string said);
        if (cc == null) { log.Add("  the mesh component couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim()))); return null; }
        dst = Package.FromBytes(cc.Output);
        int comp = cc.RootRef - 1;
        d = Find(dst, classDefault);
        byte[] cd = dst.ReadExportBytes(dst.Exports[comp]).ToArray();
        var ct = TagWalker.Walk(dst, cd, 16);
        if (ct == null) return null;
        if (ct.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } smt) BinaryPrimitives.WriteInt32LittleEndian(cd.AsSpan(smt.ValueAt), mesh);
        if (set != 0 && ct.FirstOrDefault(t => t.Name.Equals("AnimSets", StringComparison.OrdinalIgnoreCase)) is { } ast && BinaryPrimitives.ReadInt32LittleEndian(cd.AsSpan(ast.ValueAt)) >= 1) BinaryPrimitives.WriteInt32LittleEndian(cd.AsSpan(ast.ValueAt + 4), set);
        // the default: Mesh and Components naming it
        db = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        dtags = TagWalker.Walk(dst, db, 4)!;
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
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
        byte[] one = BitConverter.GetBytes(comp + 1);
        byte[] list = [.. BitConverter.GetBytes(1), .. one];
        byte[] nd = [.. db.AsSpan(0, dtags.NoneAt), .. Tag("Mesh", "ObjectProperty", one), .. Tag("Components", "ArrayProperty", list), .. db.AsSpan(dtags.NoneAt)];
        var rep = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => cd, [d] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add("  the mesh component: " + string.Join("; ", problems.Take(3))); return null; }
        log.Add($"  {dst.Exports[d].ObjectName}: a mesh component of its own (the glider's form) for its model");
        return Package.FromBytes(output);
    }

    /// <summary>The bones of a hero's main model (HeroSkeleton), else null.</summary>
    static HashSet<string>? HeroBones(string heroPackage) => HeroSkeleton(heroPackage)?.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>A hero's main model's skeleton (its base package's skeletal mesh with the most vertices), else null.</summary>
    internal static IReadOnlyList<AnimExportCli.Meshes.MeshBone>? HeroSkeleton(string heroPackage)
    {
        try
        {
            return MainMesh(heroPackage)?.Bones;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// A spawned attachment's class default with every AttachmentBones entry the target's skeleton lacks set to root (the
    /// character's origin; a ride's own animation lifts it into place). Hawkeye's Sky-Cycle names g_mountattach, which no
    /// Thor model has: spawned there it didn't show at all (Kurt, 2026-10-20). (Hawkeye's own model lacks it too; his
    /// Sky-Cycle is one of his character's attachments, which the game evidently places another way.)
    /// </summary>
    static Package RootMissingBones(Package dst, string classDefault, IReadOnlySet<string> bones, List<string> log)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return dst;
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var t = TagWalker.Walk(dst, b, 4)?.FirstOrDefault(x => x.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase));
        if (t == null) return dst;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
        var addNames = new List<string>();
        int root = Array.FindIndex(dst.Names, x => x.Equals("root", StringComparison.OrdinalIgnoreCase));
        if (root < 0) { addNames.Add("root"); root = dst.Names.Length; }
        var moved = new List<string>();
        for (int k = 0; k < n; k++)
        {
            int at = t.ValueAt + 4 + 8 * k;
            string bone = TagWalker.NameAt(dst, b, at);
            if (bone.Equals("none", StringComparison.OrdinalIgnoreCase) || bones.Contains(bone)) continue;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), root);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), 0);
            moved.Add(bone);
        }
        if (moved.Count == 0) return dst;
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => b };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the spawned attachment's bone: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {dst.Exports[d].ObjectName}: held at root (this hero has no {string.Join(", ", moved)})");
        return Package.FromBytes(output);
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
    /// <summary>
    /// The power's looping animation components whose PowerAnims end slot is empty (start, loop, None) get <paramref name="endName"/>
    /// there: the name's index changes in place, nothing else (the package unchanged when there's none).
    /// </summary>
    static byte[] FillEndSlot(byte[] package, string targetDefault, string endName, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, targetDefault);
        if (td < 0) return package;
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        foreach (int r in Components(p, td, out _, out _))
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)) is not { } pa) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pa.ValueAt)) != 3 || !TagWalker.NameAt(p, d, pa.ValueAt + 4 + 16).Equals("None", StringComparison.OrdinalIgnoreCase)
                || !TagWalker.NameAt(p, d, pa.ValueAt + 4 + 8).EndsWith("_loop", StringComparison.OrdinalIgnoreCase)) continue;
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(pa.ValueAt + 4 + 16), NameIdx(endName));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(pa.ValueAt + 4 + 20), 0);
            rep[r - 1] = _ => d;
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  the power's end animation couldn't be set ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  the power's empty end slot plays {endName}");
        return output;
    }

    /// <summary>Each animation component of a power with its AnimName / PowerAnims entries in order, None included (--power-anim-list).</summary>
    internal static List<string> AnimList(string package, string defaultPath)
    {
        var p = Package.Open(package);
        int d = Find(p, defaultPath);
        if (d < 0) return [$"{defaultPath} not found"];
        var lines = new List<string>();
        foreach (int r in Components(p, d, out _, out _))
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).StartsWith("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] b = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            var parts = new List<string>();
            foreach (var t in TagWalker.Walk(p, b, 16) ?? [])
            {
                if (t.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase)) parts.Add("AnimName " + TagWalker.NameAt(p, b, t.ValueAt));
                else if (t.Name.Equals("PowerAnims", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
                {
                    int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt));
                    parts.Add("PowerAnims [" + string.Join(", ", Enumerable.Range(0, n).Select(k => TagWalker.NameAt(p, b, t.ValueAt + 4 + 8 * k))) + "]");
                }
            }
            lines.Add($"{p.PathOf(p.Exports[r - 1])} ({p.ClassOf(p.Exports[r - 1])}, template {Template(p, r - 1) ?? "none"}): {string.Join("; ", parts)}");
        }
        return lines;
    }

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
