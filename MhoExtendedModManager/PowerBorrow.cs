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
            var built = Build(plan, Start(heroFile) ?? throw new InvalidDataException($"no {heroFile}"), $"MarvelPlayer_{th}", donorHero, log, e.Fit, e.KeepTiming, Start, game.Cooked);
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
    public static byte[] Graft(byte[] targetPackage, string targetDefault, string donorPackage, string donorDefault, List<string> log, bool voice = false)
    {
        var dst = Package.FromBytes(targetPackage);
        var src = Package.Open(donorPackage);
        int sd = Find(src, donorDefault), td = Find(dst, targetDefault);
        if (sd < 0) throw new InvalidDataException($"no {donorDefault} in {Path.GetFileName(donorPackage)}");
        if (td < 0) throw new InvalidDataException($"no {targetDefault} in the target package");
        var donorComps = Components(src, sd, out _, out _);
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [src.PathOf(src.Exports[sd])] = dst.PathOf(dst.Exports[td]) };
        var refs = new List<int>();
        // Effect sets (Kurt, 2026-10-10: Haymaker froze Daredevil): the donor's components can belong to numbered FX sets
        // (Haymaker's three swings: FxSetIndex 1-3), the target's to combo steps (Daredevil's Assault and Battery: the
        // PeriodicActivationCount 1-5 of his five swings). The target's power never asks for a donor set, so nothing played.
        // A combo target gets the donor's sets laid over its steps in turn (step k plays set (k-1) mod N), each component copied
        // per step with that step's count; a target with neither keeps the donor's first set, played every time.
        static int Int(Package p, int exp, string prop)
        {
            byte[] b = p.ReadExportBytes(p.Exports[exp]).ToArray();
            return TagWalker.Walk(p, b, 16)?.FirstOrDefault(x => x.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && x.Size == 4) is { } t
                ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt)) : 0;
        }
        var sets = donorComps.Where(r => r > 0).Select(r => Int(src, r - 1, "FxSetIndex")).Where(v => v != 0).Distinct().Order().ToList();
        int steps = sets.Count == 0 ? 0 : Components(dst, td, out _, out _).Where(r => r > 0).Select(r => Int(dst, r - 1, "PeriodicActivationCount")).DefaultIfEmpty(0).Max();
        if (sets.Count > 0)
            log.Add(steps > 0 ? $"  the donor's {sets.Count} effect set(s) laid over the target's {steps} combo step(s)"
                              : $"  the donor's effect set {sets[0]} kept (of {sets.Count}; the target has no combo steps)");
        var setEdits = new Dictionary<int, int>();   // copied component -> its combo step (0: the set index cleared)
        foreach (int r in donorComps)
        {
            if (r <= 0) continue;
            var e = src.Exports[r - 1];
            string cls = src.ClassOf(e);
            if (!voice && cls.Equals("PowerFxSound", StringComparison.OrdinalIgnoreCase) && e.ObjectName.Contains("voice", StringComparison.OrdinalIgnoreCase))
            { log.Add($"  {e.ObjectName} ({cls}): left out (the donor hero's voice)"); continue; }
            // physics blending for the donor's own floppy bones (Captain Marvel's sash and hair: her costume lists
            // physicsfloppybones); on Daredevil, who lists none, Haymaker left him stuck in one pose (Kurt, 2026-10-10)
            if (cls.Equals("PowerFxPhysicsWeight", StringComparison.OrdinalIgnoreCase))
            { log.Add($"  {e.ObjectName} ({cls}): left out (the donor hero's own physics bones)"); continue; }
            int set =Int(src, r - 1, "FxSetIndex");
            var copies = new List<(string Name, int Step)>();
            if (set == 0) copies.Add(("borrowed_" + e.ObjectName, -1));
            else if (steps > 0)
            {
                int rank = sets.IndexOf(set);
                for (int k = 1; k <= steps; k++)
                    if ((k - 1) % sets.Count == rank) copies.Add((copies.Count == 0 ? "borrowed_" + e.ObjectName : $"borrowed_{e.ObjectName}_step{k}", k));
                if (copies.Count == 0) { log.Add($"  {e.ObjectName} ({cls}): left out (effect set {set}: more sets than combo steps)"); continue; }
            }
            else if (set == sets[0]) copies.Add(("borrowed_" + e.ObjectName, 0));
            else { log.Add($"  {e.ObjectName} ({cls}): left out (effect set {set})"); continue; }
            foreach (var (name, step) in copies)
            {
            td = Find(dst, targetDefault);
            int existing = Find(dst, dst.PathOf(dst.Exports[td]) + "." + name);
            if (existing >= 0) { refs.Add(existing + 1); if (step >= 0) setEdits[existing + 1] = step; log.Add($"  {e.ObjectName} ({cls}): already in from an earlier borrow"); continue; }
            var copy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out string said);
            if (copy == null && said.Contains("the target has no copy of it"))
            { log.Add($"  {e.ObjectName} ({cls}): left out (its class is the donor package's own, and this package has none)"); break; }
            copy = copy
                ?? throw new InvalidDataException($"{e.ObjectName} ({cls}) couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
            dst = Package.FromBytes(copy.Output);
            refs.Add(copy.RootRef);
            if (step >= 0) setEdits[copy.RootRef] = step;
            log.Add($"  {e.ObjectName} ({cls}) → {dst.PathOf(dst.Exports[copy.RootRef - 1])}{(step > 0 ? $" (combo step {step})" : step == 0 ? " (every time)" : "")}");
            }
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
        // the set index becomes the combo step (same IntProperty, renamed), or 0 (played every time)
        var addNames = new List<string>();
        foreach (var (comp, step) in setEdits)
        {
            byte[] b = dst.ReadExportBytes(dst.Exports[comp - 1]).ToArray();
            var t = TagWalker.Walk(dst, b, 16)?.FirstOrDefault(x => x.Name.Equals("FxSetIndex", StringComparison.OrdinalIgnoreCase) && x.Size == 4);
            if (t == null) continue;   // an earlier borrow's copy, already a combo step
            if (step > 0)
            {
                int ni = Array.FindIndex(dst.Names, x => x.Equals("PeriodicActivationCount", StringComparison.OrdinalIgnoreCase));
                if (ni < 0) { if (addNames.Count == 0) addNames.Add("PeriodicActivationCount"); ni = dst.Names.Length; }
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.Start), ni);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.Start + 4), 0);
            }
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.ValueAt), Math.Max(step, 0));
            byte[] nb = b;
            rep[comp - 1] = _ => nb;
        }
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
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

    /// <summary>A sequence's length in seconds in a hero package's animation sets (its first by that name), else 0.</summary>
    public static float Length(string heroPackage, string anim)
    {
        var ap = AnimExportCli.Packages.Package.Open(heroPackage);
        foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
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
    public static Dictionary<string, byte[]> Build(List<Pair> pairs, string targetHeroPackage, string targetHeroClass, string donorHeroPackage, List<string> log, bool fit = true, bool keepTiming = false, Func<string, string?>? startFrom = null, string? cooked = null)
    {
        var done = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var targetAnims = new List<string>(); var donorAnims = new List<string>();
        foreach (var pr in pairs)
        {
            string tf = Path.GetFileName(pr.TargetFile);
            byte[] cur = done.TryGetValue(tf, out var b) ? b : File.ReadAllBytes(startFrom?.Invoke(tf) ?? pr.TargetFile);
            string td = $"marvelgamecontent.default__{pr.TargetClass}".ToLowerInvariant(), dd = $"marvelgamecontent.default__{pr.DonorClass}".ToLowerInvariant();
            log.Add($"{pr.Role}: {pr.TargetClass} ({tf}) ← {pr.DonorClass} ({Path.GetFileName(pr.DonorFile)})");
            if (keepTiming && pr.Role == "power") { done[tf] = SwapLooks(cur, td, pr.DonorFile, dd, log); continue; }
            if (pr.Role == "power") { targetAnims.AddRange(AnimNames(pr.TargetFile, td)); donorAnims.AddRange(AnimNames(pr.DonorFile, dd)); }
            done[tf] = Graft(cur, td, pr.DonorFile, dd, log);
        }
        donorAnims = donorAnims.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keepTiming) { log.Add("anims: the target's own (its timing kept)"); return done; }
        if (donorAnims.Count == 0) { log.Add("anims: the donor power plays no animation of its own"); return done; }
        // the target's own power animation: the length the borrowed ones are fitted to (its longest, when it has several)
        float targetLen = targetAnims.Select(a => Length(targetHeroPackage, a)).DefaultIfEmpty(0).Max();
        var ap = AnimExportCli.Packages.Package.Open(donorHeroPackage);
        var swaps = new List<AnimSwap.Swap>();
        foreach (string an in donorAnims)
        {
            AnimSwap.Swap? found = null;
            foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                foreach (var seq in set.Sequences.Where(q => q.IsExport))
                    if (found == null && AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex).Equals(an, StringComparison.OrdinalIgnoreCase))
                        found = new AnimSwap.Swap(an, donorHeroPackage, set.ExportIndex, seq.ExportIndex);
            if (found == null) { log.Add($"anims: {an} not found in {Path.GetFileName(donorHeroPackage)}"); continue; }
            float donorLen = Length(donorHeroPackage, an);
            float rate = fit && targetLen > 0.01f && donorLen > 0.01f ? donorLen / targetLen : 1;
            log.Add($"anims: {an} {donorLen:0.##} s{(Math.Abs(rate - 1) > 0.01f ? $", played ×{rate:0.##} to last {targetLen:0.##} s like the target's {string.Join(" / ", targetAnims)}" : "")}");
            swaps.Add(found with { Rate = rate });
        }
        if (swaps.Count > 0)
        {
            string hf = Path.GetFileName(targetHeroPackage);
            string heroPath = startFrom?.Invoke(hf) ?? targetHeroPackage;
            if (done.TryGetValue(hf, out var hb)) { heroPath = Path.Combine(Path.GetTempPath(), "mhoextmm_borrow_" + hf); File.WriteAllBytes(heroPath, hb); }
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
                    if (!File.Exists(start)) continue;
                    if (AnimSwap.AddImportedSets(start, cf[4..^7], sets, log) is { } costume) done[cf] = costume;
                }
            }
        }
        return done;
    }

    static string WriteTemp(string file, byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "mhoextmm_borrow_" + file);
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
    public static List<string> AnimNames(string package, string defaultPath)
    {
        var p = Package.Open(package);
        int d = Find(p, defaultPath);
        if (d < 0) return [];
        var names = new List<string>();
        foreach (int r in Components(p, d, out _, out _))
        {
            if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).Equals("PowerFxAnimation", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] b = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            foreach (var t in TagWalker.Walk(p, b, 16) ?? [])
                if (t.Name.Equals("AnimName", StringComparison.OrdinalIgnoreCase) && TagWalker.NameAt(p, b, t.ValueAt) is string n && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        return names;
    }
}
