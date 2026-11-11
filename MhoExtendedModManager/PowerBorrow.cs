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
            if (File.Exists(kept))
            {
                // a kept copy that is itself a borrow's output (Kurt, 2026-11-02: the travel tests, built before a borrow named
                // its packages, kept the built power package as the start: every build added the bike again, as
                // borrowed_bike_spawn_2, and the bike didn't show in game): dropped, the build starts from the stock original
                if (!HoldsBorrow(kept)) return kept;
                File.Delete(kept);
                log.Add($"{f}: the kept starting copy already held a borrow (an earlier build): starting from the game's original");
            }
            int k = draft.Packages.FindIndex(p => p.File.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (k >= 0 && !before.Contains(f) && !HoldsBorrow(draft.Packages[k].Source))
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
            string dh = DonorPackage(game.Cooked, e.DonorHero, e.Donor, tp?.DonorClass, tp == null ? [] : AnimNames(tp.DonorFile, $"marvelgamecontent.default__{tp.DonorClass}".ToLowerInvariant()))
                ?? throw new InvalidDataException($"{e.DonorName}: its character's package wasn't found");
            e.Hero = th; e.DonorHero = dh;
            string heroFile = $"UC__MarvelPlayer_{th}_SF.upk";
            string donorHero = StockFiles.For(game.Cooked, DonorFile(dh));
            // what the donor shows through its conditions (a ride's vehicle), spawned by the borrowed power instead
            // (Props unticked: none of the other power's weapons or vehicles; a user, 2026-11-09: Taskmaster's shield and sword)
            var spawn = e.KeepTiming || e.NoProps ? [] : ConditionAttachments(db, fx, e.Donor, donorHero, log);
            // a travel power (Kurt, 2026-10-21: what Thor's six travel tests settled goes to every travel borrow): the other
            // hero's always-ridden mount (Green Goblin's glider) comes along, and the hero's own cape moves (AddOwnTracks)
            bool travel = e.Power.Contains(@"\TravelPower\", StringComparison.OrdinalIgnoreCase);
            if (!e.KeepTiming && !e.NoProps && travel) spawn.AddRange(MountAttachments(donorHero, log));
            // the other hero's always-held weapons (Thor's hammer in his flight): its animations hold them (Kurt, 2026-10-21)
            // ... except those the donor's own power hides while it runs (Hawkeye puts his bow and arrow away on the Sky-Cycle:
            // hidebow / hidearrow; brought along, the bow stayed out on every rider, Kurt 2026-11-04)
            var donorHides = tp == null ? [] : PowerHiddenSlots(tp.DonorFile, $"marvelgamecontent.default__{tp.DonorClass}".ToLowerInvariant(), wholeOnly: !travel);
            if (!e.KeepTiming && !e.NoProps && !th.Equals(dh, StringComparison.OrdinalIgnoreCase))
                foreach (var w in HeldAttachments(donorHero, log, HeroBones(StockFiles.For(game.Cooked, heroFile))))
                {
                    // (travel only: an attack hides its arrow for part of the shot; Kurt, 2026-11-04: Thor with Hawkeye's Nerve Gas
                    // Arrow had no arrow)
                    // (attacks too when hidden for the whole power: Kurt, 2026-11-04, Doctor Strange with Deadpool's Real Armor
                    // Buster held two pistols that power puts away)
                    // (any of its slots: Deadpool's guns fill lefthand / righthand and pistol, and his power hides pistol)
                    if (ClassSlots(w.File, w.Export).Prepend(w.Slot).FirstOrDefault(donorHides.Contains) is string hid) { log.Add($"  {Package.Open(w.File).Exports[w.Export].ObjectName}: not brought (the donor's power hides its slot {hid})"); continue; }
                    // ... and only a weapon the power's effects come out of (Kurt, 2026-11-04: Thor's flight comes from his
                    // hammer, its effects spawn on socket_hammer on the hammer's g_r_palm; Kitty Pryde's katana has nothing to
                    // do with her Air Walk, whose effects are at her feet, and every borrower carried it)
                    // (travel powers only: an attack's animation is made holding the weapon; Kurt, 2026-11-04: Scarlet Witch with
                    // Angela's Hunter's Blades, a sword combo, swung no sword)
                    // (an always-held attachment with no model, effects only: an aura, not a weapon (Human Torch's body flames came
                    // with Searing Arcfire onto Gambit): as for travel, only when the power's effects spawn on it)
                    if ((travel || !HasModel(w.File, w.Export)) && tp != null && !EffectsUseWeapon(tp.DonorFile, $"marvelgamecontent.default__{tp.DonorClass}".ToLowerInvariant(), donorHero, w, game.Cooked, out string why))
                    { log.Add($"  {Package.Open(w.File).Exports[w.Export].ObjectName}: not brought ({why})"); continue; }
                    if (!spawn.Any(x => x.File.Equals(w.File, StringComparison.OrdinalIgnoreCase) && x.Export == w.Export)) spawn.Add(w);
                }
            // a ground power's animations on a high-flying one: the game lifts the hero (Globals' HighFlyingHeight) and the
            // ground animations sit at the floor, so they are lowered by that much (Spider-Man riding Cyclops' bike floated
            // in Web-Swinging; Kurt, 2026-10-20)
            float lift = 0;
            // (MHO_BORROW_NO_LIFT=1: not lowered at all, a probe: in game the hero then floats exactly as high as the game lifts him)
            if (!e.KeepTiming && Environment.GetEnvironmentVariable("MHO_BORROW_NO_LIFT") != "1" && Field(db, e.Power, "HighFlying", 'B') is 1 && Field(db, e.Donor, "HighFlying", 'B') is not 1
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
            var slots = new List<string>();
            // the game's lift for this power (FlightLift) and the hero's climb speed: his own, or the user's (Climb Speed)
            var flightNow = lift != 0 ? FlightLift(db, e.Power) : null;
            var (ownSpeed, fallSpeed) = Gui.PowerPreview.FlyingSpeeds(StockFiles.For(game.Cooked, heroFile), th);
            float climbSpeed = e.ClimbSpeed is float cs && cs > 1 ? cs : ownSpeed;
            // a ground ride lowered onto a high flyer whose own climb outlasts the power's wind-up: the start plays over the
            // wind-up only and the loop (shared by every repeat) is never keyed, so the loop began sunk until the climb ended
            // (Cyclops' ride on Kitty Pryde: 65 under the floor at the loop's start, level 0.67 s later; telemetry ride46, Kurt
            // 2026-11-04). Unless the user set one, the climb speed is raised so the lift is done by the loop
            float autoClimb = 0;
            if (flightNow is { } fa && !(e.ClimbSpeed is float us && us > 1) && fa.ChannelStart - fa.ApplyAt > 0.05f
                && ownSpeed * (fa.ChannelStart - fa.ApplyAt) < fa.Height - 0.5f)
            {
                climbSpeed = autoClimb = MathF.Ceiling(fa.Height / (fa.ChannelStart - fa.ApplyAt) * 1.02f);
                log.Add($"climb: {th} climbs {ownSpeed:0} a second, too slow for the {fa.Height:0} lift in the {fa.ChannelStart - fa.ApplyAt:0.##} s wind-up: {autoClimb:0} a second for this power's look");
            }
            // both powers fly high (Thor's flight on Kitty Pryde): the start follows the donor's takeoff (Build: takeoff)
            ((float, float, float) Target, float Rise, float DonorApplyAt, float DonorChannelStart)? takeoffNow = null;
            if (!e.KeepTiming && Environment.GetEnvironmentVariable("MHO_BORROW_NO_TAKEOFF") != "1" && FlightLift(db, e.Power) is { } tf0 && FlightLift(db, e.Donor) is { } df0)
            {
                var (ownT, _) = Gui.PowerPreview.FlyingSpeeds(StockFiles.For(game.Cooked, heroFile), th);
                takeoffNow = (tf0, e.ClimbSpeed is float cs2 && cs2 > 1 ? cs2 : ownT, df0.ApplyAt, df0.ChannelStart);
                if (df0.ApplyAt <= tf0.ApplyAt + 0.01f) takeoffNow = null;   // the donor launches no later: nothing to hold
            }
            e.Climb = flightNow is { } fc ? (ownSpeed, fc.ApplyAt, fc.ChannelStart, fc.Height, fallSpeed) : null;
            // the hero's costumes built as characters of their own (Kate Bishop for Hawkeye): the power stays stock on them
            var ownClass = e.KeepTiming ? [] : OwnClassCostumes(game.Root, th);
            if (ownClass.Count > 0) log.Add($"stock look kept for {string.Join(", ", ownClass)} (costumes of their own class)");
            // a vehicle on a hero whose travel condition plays his ride (Hawkeye): the condition shows it and plays the borrowed
            // ride (ConditionVehicle), its start a copy of its own lowered on the condition's clock
            bool condStart = !e.KeepTiming && travel && flightNow != null && spawn.Any(x => x.Vehicle) && ConditionPlaysAnims(db, fx, e.Power);
            var built = Build(plan, Start(heroFile) ?? throw new InvalidDataException($"no {heroFile}"), $"MarvelPlayer_{th}", donorHero, log, e.Fit, e.KeepTiming, Start, game.Cooked, spawn, lift,
                e.VehicleScale, spawn.Any(x => x.Vehicle) && !rider.IsNone ? rider : null, ownTracks: travel, props: e.Props, spawnedOut: spawned, ownClassCostumes: ownClass, keepWeapons: e.KeepWeapons,
                held: (draft.AddedProps.Select(a => a.Class).ToHashSet(StringComparer.OrdinalIgnoreCase), draft.AddedProps.Where(a => a.Slot != null).Select(a => a.Slot!).ToHashSet(StringComparer.OrdinalIgnoreCase)),
                heldBy: [.. draft.AddedProps.Select(a => CostumeClassOf(a.Package)).Distinct()],
                heights: e.Heights?.Keys.ToDictionary(k => k, e.HeightKeys, StringComparer.OrdinalIgnoreCase), slotsOut: slots,
                flight: flightNow is { } fn ? (fn.ApplyAt, climbSpeed, fn.ChannelStart, fallSpeed) : null, endFromLoop: e.End == PowerBorrowEntry.EndLoop, canChooseEnd: v => e.EndChoosable = v, conditionStart: condStart,
                takeoff: takeoffNow,
                // costume packages a costume mod (one without borrows) replaces: the borrow doesn't ship them (AddBorrowedSockets)
                costumeModPackages: lib.Mods.Where(m => m.Folder != editing?.Folder && m.Manifest.PowerBorrows is not { Count: > 0 })
                    .SelectMany(m => m.Manifest.UpkReplacements.Select(f => (File: f, Mod: m.Manifest.Name))).GroupBy(x => x.File, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Mod, StringComparer.OrdinalIgnoreCase),
                // the packages this mod ships already (a costume the borrow carries for its animation list: gets the sockets too)
                modPackages: draft.Packages.Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase),
                anims: !e.NoAnimations, effects: !e.NoEffects, takeProps: !e.NoProps);
            e.Spawned = spawned;
            e.Slots = slots.Count > 0 ? slots : null;
            if (!e.KeepTiming && !e.NoAnimations && CostumeListsNote(CostumeListsHiding(lib, editing?.Folder, th, game.Cooked), th) is { } hidden) log.Add("warning: " + hidden);
            // the hero's own animated attachments (Kitty Pryde's Lockheed) play the power's animation names too: their own
            // travel animations also under the borrowed names (Lockheed hung still, no wing flaps, under Hawkeye's
            // movement_rideskycycle_*: he has only movement_run_flying_*; Kurt 2026-11-04, telemetry ride41)
            if (!e.KeepTiming && tp != null && slots.Count > 0 && built.TryGetValue(heroFile, out var hpb))
            {
                var own = AnimNames(tp.TargetFile, $"marvelgamecontent.default__{tp.TargetClass}".ToLowerInvariant());
                var pairs = new List<(string From, string To)>();
                foreach (string to in slots)
                    if (new[] { "_start", "_loop", "_end" }.FirstOrDefault(x => to.EndsWith(x, StringComparison.OrdinalIgnoreCase)) is { } part
                        && own.FirstOrDefault(o => o.EndsWith(part, StringComparison.OrdinalIgnoreCase)) is { } from && !from.Equals(to, StringComparison.OrdinalIgnoreCase))
                        pairs.Add((from, to));
                if (pairs.Count > 0) built[heroFile] = OwnAttachmentNames(hpb, pairs, log);
            }
            // the donor's copied AnimSet keys bones of another skeleton sharing it: Hawkeye's Sky-Cycle set keys Kate Bishop's
            // g_mountattach 16.6 up, and Cyclops' and Green Goblin's models have that bone, so they sat 16 above the seat
            // (telemetry ride41, Kurt 2026-11-04): a mount bone the donor's own model lacks is renamed None in the copy
            if (!e.KeepTiming && DonorModelBones(donorHero) is { Count: > 0 } dbones)
                foreach (string bf in built.Keys.Where(k => k.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)).ToList())
                    built[bf] = DropForeignMountTracks(built[bf], $"_on_marvelplayer_{th}", dbones, log);
            // the donor's animations switch skeletal controls on by their metadata (Cyclops' ride loop: his tilt control, which
            // aims g_mountgroundaim along the ground); a rider skeleton (Green Goblin: body hung from g_mountgroundaim and
            // g_mountattach) has a control of that name too, aimed for his glider, and leaned back 14° on level ground
            // (telemetry ride46, Kurt 2026-11-04): on such a hero, when the donor's own model has no such bones, the borrowed
            // sequences' skeletal-control metadata is left out
            if (!e.KeepTiming && HeroBones(StockFiles.For(game.Cooked, heroFile)) is { } tbm && tbm.Contains("g_mountgroundaim") && tbm.Contains("g_mountattach")
                && DonorModelBones(donorHero) is { } dbm && !dbm.Contains("g_mountgroundaim"))
                foreach (string bf in built.Keys.Where(k => k.StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase)).ToList())
                    built[bf] = DropSkelControlMetadata(built[bf], $"_on_marvelplayer_{th}", log);
            // the user's climb speed into the hero's class (the package the borrow ships already)
            if (flightNow != null && (e.ClimbSpeed is float set && set > 1 ? set : autoClimb) is var setTo && setTo > 1 && Math.Abs(setTo - ownSpeed) > 0.5f && built.TryGetValue(heroFile, out var hb))
                built[heroFile] = SetClimbSpeed(hb, $"MarvelPlayer_{th}", setTo, log);
            // the hero's own ride (Hawkeye's Sky-Cycle): shown by the travel power's condition and hidden again by the power's
            // own components, which the borrow replaces, so it stayed under him after the power ended (Kurt, 2026-10-22):
            // the condition's vehicle attachments go
            if (!e.KeepTiming && travel)
            {
                var conds = DropConditionVehicles(db, fx, e.Power, Start, log, ownClass);
                // ... and the borrowed vehicle goes into that condition instead of the power (Kurt, 2026-11-03: Cyclops' bike on
                // Hawkeye never showed, nor did his own Sky-Cycle when the power alone showed it: on a hero whose travel
                // condition shows his ride, the condition is what shows a vehicle, and it plays its own animations, Hawkeye's
                // high Sky-Cycle ride, over the borrowed ones)
                string? pf = tp == null ? null : Path.GetFileName(tp.TargetFile);
                var vclasses = spawn.Where(x => x.Vehicle).Select(x => Package.Open(x.File).Exports[x.Export].ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool uniqued = false;
                if (pf != null && vclasses.Count > 0 && built.TryGetValue(pf, out var pb))
                    foreach (string cf in conds.Keys.ToList())
                        if (ConditionVehicle(conds[cf], pb, $"marvelgamecontent.default__{tp!.TargetClass}".ToLowerInvariant(), vclasses, slots, ownClass, game.Cooked, log, condStart) is { } cv)
                        {
                            byte[] cb = cv.Condition;
                            foreach (var (dfile, ddef) in DonorRideConditions(db, fx, e.Donor)) cb = ConditionExtras(cb, dfile, ddef, ownClass, game.Cooked, log);
                            conds[cf] = UniqueVehicle(cb, vclasses, "on_" + th.ToLowerInvariant(), Path.GetFileName(cf), log);
                            built[pf] = pb = PivotFixedParticles(UniqueVehicle(cv.Power, vclasses, "on_" + th.ToLowerInvariant(), pf, log), log);
                            uniqued = true;
                            // the costumes kept stock (Kate Bishop) play the renamed condition animations too: their own ride
                            // under those names (OwnRideNames)
                            // ... and the borrowed power's animation names (it plays for them too: SlotFadesForAll), from their own
                            // animation of the same part (_start, _loop, _end)
                            var ownNames = cv.Anims.ToList();
                            foreach (string slot in slots)
                                if (cv.Anims.FirstOrDefault(a => new[] { "_start", "_loop", "_end" }.Any(s => slot.EndsWith(s, StringComparison.OrdinalIgnoreCase) && a.From.EndsWith(s, StringComparison.OrdinalIgnoreCase))) is { From: { } from }
                                    && !ownNames.Any(a => a.To.Equals(slot, StringComparison.OrdinalIgnoreCase)))
                                    ownNames.Add((from, slot));
                            if (cv.Anims.Count > 0)
                                foreach (string oc in ownClass)
                                {
                                    // (the game's own spelling, from the stock list: the class name is lower case, the file
                                    // UC__MarvelPlayer_KateBishop_SF.upk)
                                    string of = game.StockName($"UC__{oc}_SF.upk") ?? $"UC__{oc}_SF.upk";
                                    if (Start(of) is { } ofile && OwnRideNames(ofile, oc, ownNames, log) is { } ob) built[of] = ob;
                                }
                        }
                foreach (var (f, bytes) in conds) built[f] = bytes;
                // a vehicle the power spawns itself keeps the stock path otherwise, and the donor's own class wins once his
                // package is loaded: Green Goblin's glider on Captain America and Kitty Pryde used his class (his bone, no lift)
                // after Goblin had been played (telemetry ride43, Kurt 2026-11-04)
                if (!uniqued && pf != null && vclasses.Count > 0 && built.TryGetValue(pf, out var pb2))
                    built[pf] = UniqueVehicle(pb2, vclasses, "on_" + th.ToLowerInvariant(), pf, log);
            }
            // a charge-and-release donor on a power that isn't one: its charge from the start, its release at the channel's end or
            // the hit (PowerBorrow.ChargeUp)
            if (!e.KeepTiming && tp != null && built.TryGetValue(Path.GetFileName(tp.TargetFile), out var cpk)
                && KindOf(db, fx, e.Donor) is { ChargeUp: true } && KindOf(db, fx, e.Power) is { ChargeUp: false } tk)
                built[Path.GetFileName(tp.TargetFile)] = RemapChargeUp(cpk, $"marvelgamecontent.default__{tp.TargetClass}".ToLowerInvariant(), tk.Channeled, fx, log);
            // the other hero's held weapons and props the borrow brought into the power package (Deadpool's cannon in Real
            // Armor Buster, Punisher's M60 in Full Auto): under paths of their own too, as the vehicles above. A class is found by
            // path and the package loaded first wins: once the other hero was loaded, his own copy (stock animations, his
            // bones) would stand in for the borrowed one (as with Green Goblin's glider, telemetry ride43). Every attachment class
            // in the built power package that the stock power doesn't have, not renamed yet.
            if (!e.KeepTiming && crossHeroNow && tp != null && built.TryGetValue(Path.GetFileName(tp.TargetFile), out var hpk))
            {
                string pfh = Path.GetFileName(tp.TargetFile), tagh = "on_" + th.ToLowerInvariant();
                var stockClasses = Package.Open(StockFiles.For(game.Cooked, pfh) is var sp0 && File.Exists(sp0) ? sp0 : tp.TargetFile).Exports
                    .Select(x => x.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var hp0 = Package.FromBytes(hpk);
                var held = hp0.Exports.Where(x => hp0.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase)
                        && x.ObjectName.StartsWith("marvelattachment", StringComparison.OrdinalIgnoreCase)
                        && !stockClasses.Contains(x.ObjectName) && !x.ObjectName.EndsWith("_" + tagh, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (held.Count > 0) built[pfh] = UniqueVehicle(hpk, held, tagh, pfh, log, "weapon or prop");
            }
            // Its Summon's Look Too: each summon this power puts in the world (a model) takes the other power's summon's look,
            // in its own package (SummonLook; the first donor summon for every target summon past the donor's count)
            if (e.SummonLook)
            {
                var tsum = SummonLook.Of(db, fx, e.Power, HeroOf(e.Power));
                var dsum = SummonLook.Of(db, fx, e.Donor, HeroOf(e.Donor));
                if (tsum.Count == 0 || dsum.Count == 0) log.Add($"summon look: {(tsum.Count == 0 ? "this power summons" : "the other power summons")} no model; nothing to change");
                for (int i = 0; i < tsum.Count && dsum.Count > 0; i++)
                {
                    var t = tsum[i]; var d = dsum[Math.Min(i, dsum.Count - 1)];
                    string sf = game.StockName(Path.GetFileName(t.File)) ?? Path.GetFileName(t.File);
                    if (Start(sf) is not { } sstart) { log.Add($"summon look: {sf} has no clean copy to start from"); continue; }
                    var slog = new List<string>();
                    built[sf] = SummonLook.Build(sstart, t, d, slog);
                    log.Add($"summon look: {t.Class} takes {d.Class}'s ({sf})");
                    log.AddRange(slog.Where(l => !l.StartsWith("  ")).Select(l => "  " + l));
                }
            }
            // Its Area's Look Too: the other power's area effects, as this power's own at the target
            if (e.AreaLook && tp != null)
            {
                string pfile = built.Keys.FirstOrDefault(k => k.Equals(Path.GetFileName(tp.TargetFile), StringComparison.OrdinalIgnoreCase)) ?? "";
                var looks = AreaLooks(db, fx, e.Donor, log);
                if (looks.Count == 0) log.Add("area look: the other power puts down no area with a look of its own");
                else if (pfile.Length == 0) log.Add("area look: this power's package wasn't built; left out");
                else built[pfile] = AddAreaLooks(built[pfile], $"marvelgamecontent.default__{tp.TargetClass}".ToLowerInvariant(), looks, game.Cooked, log);
            }
            // Its Triggered Powers' Look Too: the powers this one sets off take the look of the other power's (PowerBorrow.Triggered)
            if (e.TriggeredLook)
            {
                var tps = TriggeredPairs(db, fx, e.Power, e.Donor, log);
                if (tps.Count == 0) log.Add("triggered look: one of the two powers sets off nothing with a look of its own; nothing to change");
                else foreach (var (f, bytes) in BuildTriggered(tps, f => built.ContainsKey(f) ? null : Start(f), game.Cooked, log)) built[f] = bytes;
            }
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
    public static byte[] Graft(byte[] targetPackage, string targetDefault, string donorPackage, string donorDefault, List<string> log, bool voice = false, string? cooked = null,
        Func<string, bool>? take = null)
    {
        var dst = Package.FromBytes(targetPackage);
        var src = Package.Open(donorPackage);
        int sd = Find(src, donorDefault), td = Find(dst, targetDefault);
        if (sd < 0) throw new InvalidDataException($"no {donorDefault} in {Path.GetFileName(donorPackage)}");
        if (td < 0) throw new InvalidDataException($"no {targetDefault} in the target package");
        var donorComps = Components(src, sd, out _, out _);
        // Whole Power's parts (take: a component class → whether the donor's kind of it comes in): the parts left out stay the
        // target's own (by path: copies below only append exports)
        var keptOwn = take == null ? [] : Components(dst, td, out _, out _).Where(r => r > 0 && !take(dst.ClassOf(dst.Exports[r - 1])))
            .Select(r => dst.PathOf(dst.Exports[r - 1])).ToList();
        if (take != null)
            foreach (int r in donorComps.Where(r => r > 0 && !take(src.ClassOf(src.Exports[r - 1]))))
                log.Add($"  {src.Exports[r - 1].ObjectName} ({src.ClassOf(src.Exports[r - 1])}): left out (unticked part); this power keeps its own");
        // (does the target power loop? a looping animation, or any part timed on a loop phase)
        bool targetLoops = Components(dst, td, out _, out _).Any(r => r > 0 && (dst.ClassOf(dst.Exports[r - 1]).Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase)
            || TagWalker.Walk(dst, dst.ReadExportBytes(dst.Exports[r - 1]).ToArray(), 16) is { } tt && tt.Any(t => (t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase))
                && TagWalker.NameAt(dst, dst.ReadExportBytes(dst.Exports[r - 1]).ToArray(), t.ValueAt).Contains("loop", StringComparison.OrdinalIgnoreCase))));
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [src.PathOf(src.Exports[sd])] = dst.PathOf(dst.Exports[td]) };
        var refs = new List<int>();
        var un = UnloadedFor(cooked, targetDefault, donorDefault, donorPackage);
        if (un != null && un.Packages.Count > 0) log.Add($"  the game doesn't load {string.Join(", ", un.Packages.Select(x => x.Name))} with this power (the donor's needs them): parts are made to stand without them");
        // (Tested in game 2026-10-10, Cosmic Haymaker on Daredevil: the donor's FxSetIndex sets and its PowerFxPhysicsWeight
        // copy as they are; the freeze was costumes with their own animation list, see Build.)
        foreach (int r in donorComps)
        {
            if (r <= 0) continue;
            var e = src.Exports[r - 1];
            string cls = src.ClassOf(e);
            if (take != null && !take(cls)) continue;
            if (!voice && cls.Equals("PowerFxSound", StringComparison.OrdinalIgnoreCase) && e.ObjectName.Contains("voice", StringComparison.OrdinalIgnoreCase))
            { log.Add($"  {e.ObjectName} ({cls}): left out (the donor hero's voice)"); continue; }
            // an enemy's telegraph (Kurt, 2026-11-10: an enemy power's warningindicator, PowerFxTell_Circle, the circle on the ground
            // before the hit, failed to copy on its class's enum): a warning for the player, not a look for a hero's power
            if (cls.StartsWith("PowerFxTell", StringComparison.OrdinalIgnoreCase))
            { log.Add($"  {e.ObjectName} ({cls}): left out (an enemy's warning on the ground before its attack)"); continue; }
            string name = "borrowed_" + e.ObjectName;
            int existing = Find(dst, dst.PathOf(dst.Exports[td]) + "." + name);
            if (existing >= 0) { refs.Add(existing + 1); log.Add($"  {e.ObjectName} ({cls}): already in from an earlier borrow"); continue; }
            // (a, 0.60.7) a component whose settings come from a template in another class's package (Cyclops' ride: its anim
            // inherits the ride animations from powershared_ridebike's): that package isn't loaded for the target, so the
            // template is copied in under its own path and the copy points at it
            // (0.60.428) under the target power's own default with its own name, never under its path: a copy under its path
            // brought the other class's default object AND the class itself (Emma Frost's sprint on Taskmaster: a second
            // marvelgamecontent.powershared_sprint_y beside UC__PowerShared_Sprint_Y_SF's: the game crashed on load, Kurt 2026-11-07)
            // (0.60.430) with the game's package lists known: a part whose class only a package the target doesn't load
            // defines is left out; its templates are flattened while they're in such packages, else imported as they are
            if (un != null)
            {
                if (un.ClassHome(cls) is string home)
                { log.Add($"  {e.ObjectName} ({cls}): left out (its class is defined in {home}, which the game doesn't load with this power)"); continue; }
                var ucopy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out string usaid);
                if (ucopy == null && (usaid.Contains("the target has no copy of it") || usaid.Contains("(compiled code) isn't copied yet")))
                { log.Add($"  {e.ObjectName} ({cls}): left out (its class is the donor package's own and couldn't be copied)"); continue; }
                ucopy = ucopy ?? throw new InvalidDataException($"{e.ObjectName} ({cls}) couldn't be copied: " + string.Join(" ", usaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
                dst = Package.FromBytes(ucopy.Output);
                dst = Flatten(dst, ucopy.RootRef - 1, dst.PathOf(dst.Exports[Find(dst, targetDefault)]), e.ObjectName, un, log);
                td = Find(dst, targetDefault);
                refs.Add(ucopy.RootRef);
                log.Add($"  {e.ObjectName} ({cls}) → {dst.PathOf(dst.Exports[ucopy.RootRef - 1])}");
                continue;
            }
            if (cooked != null && Template(src, r - 1) is string tpl && Find(dst, tpl) < 0 && TemplatePackage(cooked, tpl) is string tf)
            {
                string tdPath = dst.PathOf(dst.Exports[td]), tplLeaf = tpl[(tpl.LastIndexOf('.') + 1)..], tplOuter = tpl[..tpl.LastIndexOf('.')];
                string tplCopy = tdPath + ".borrowedtemplate_" + tplLeaf;
                if (Find(dst, tplCopy) < 0)
                {
                    var tp = Package.Open(tf);
                    int ti = Find(tp, tpl);
                    var tc = ti < 0 ? null : CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, [], "borrowedtemplate_" + tplLeaf,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplOuter] = tdPath }), out string tsaid);
                    if (tc != null) { dst = Package.FromBytes(tc.Output); td = Find(dst, targetDefault); log.Add($"  {e.ObjectName}: its template {tpl} copied in as {tplCopy} (from {Path.GetFileName(tf)})"); }
                    else log.Add($"  {e.ObjectName}: its template {tpl} couldn't be copied: its inherited settings won't show");
                }
                if (Find(dst, tplCopy) >= 0) replace[tpl] = tplCopy;
            }
            string? mergeFrom = Template(src, r - 1) is string tpl1 && replace.TryGetValue(tpl1, out string? tc1) && tc1.Contains(".borrowedtemplate_", StringComparison.OrdinalIgnoreCase) ? tc1 : null;
            if (Template(src, r - 1) is string tpl2 && Find(dst, tpl2) >= 0) replace[tpl2] = tpl2;
            var copy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out string said);
            if (copy == null && (said.Contains("the target has no copy of it") || said.Contains("(compiled code) isn't copied yet")))
            { log.Add($"  {e.ObjectName} ({cls}): left out (its class is the donor package's own and couldn't be copied)"); continue; }
            copy = copy
                ?? throw new InvalidDataException($"{e.ObjectName} ({cls}) couldn't be copied: " + string.Join(" ", said.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL") || l.Contains("not supported")).Select(l => l.Trim())));
            dst = Package.FromBytes(copy.Output);
            if (mergeFrom != null && Find(dst, mergeFrom) is int tci && tci >= 0) dst = MergeTemplate(dst, copy.RootRef - 1, tci, e.ObjectName, log);
            td = Find(dst, targetDefault);
            refs.Add(copy.RootRef);
            log.Add($"  {e.ObjectName} ({cls}) → {dst.PathOf(dst.Exports[copy.RootRef - 1])}");
        }
        // the target's default lists the copies (its own components stay in the package, unused), after the own parts kept
        if (keptOwn.Count > 0)
        {
            refs.InsertRange(0, keptOwn.Select(p => Find(dst, p) + 1).Where(x => x > 0));
            log.Add($"  kept this power's own: {string.Join(", ", keptOwn.Select(p => p[(p.LastIndexOf('.') + 1)..]))}");
        }
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
        return Unloop(output, targetDefault, targetLoops, refs, log);
    }

    /// <summary>
    /// A channeled donor on a single-shot target: the target never reaches power_on_loop_start / _loop_end, so parts timed
    /// there never show (Kurt 2026-11-04, Cyclops' Optic Blast with Vision's Channeled Solar Beam: the trace shows only the
    /// start sound and the start animation firing, no beam). Those parts move to the target's own clock: loop start →
    /// power_at_contact_time (where Cyclops' own beam starts), loop end → power_on_end. A hypothesis until seen in game.
    /// </summary>
    static byte[] Unloop(byte[] package, string targetDefault, bool targetLoops, List<int> refs, List<string> log)
    {
        if (targetLoops) return package;
        var p = Package.FromBytes(package);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["power_on_loop_start"] = "power_at_contact_time", ["power_on_loop_end"] = "power_on_end" };
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var moved = new List<string>();
        foreach (int r in refs)
        {
            if (r <= 0) continue;
            byte[] b = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            var tags = TagWalker.Walk(p, b, 16);
            if (tags == null) continue;
            bool changed = false;
            foreach (var t in tags)
            {
                if (!(t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase))) continue;
                string at = TagWalker.NameAt(p, b, t.ValueAt);
                if (!map.TryGetValue(at, out string? to)) continue;
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.ValueAt), NameIdx(to));
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(t.ValueAt + 4), 0);
                moved.Add($"{p.Exports[r - 1].ObjectName} {t.Name.ToLowerInvariant()} {at} → {to}");
                changed = true;
            }
            if (changed) { byte[] nb = b; rep[r - 1] = _ => nb; }
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the loop timings: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {targetDefault}: the target doesn't loop, so the donor's loop parts run on its clock: {string.Join("; ", moved)}");
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
            bool IsProjClass(ExportEntry e) => pk.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) && e.ObjectName.StartsWith("marvelprojectile", StringComparison.OrdinalIgnoreCase);
            // a class the package holds only because another projectile class there extends it (Fastball Special keeps
            // marvelprojectileanimated beside marvelprojectileanimated_fastballspecial, the one that flies: its fire_projectile
            // names it) isn't one the power fires: its SuperIndex (4 bytes into the export entry) says which
            var parents = new HashSet<int>();
            for (int i = 0; i < pk.Exports.Length; i++)
                if (IsProjClass(pk.Exports[i]) && BitConverter.ToInt32(pk.Body, pk.ExportEntryStart[i] + 4) is int sup && sup > 0) parents.Add(sup - 1);
            for (int i = 0; i < pk.Exports.Length; i++)
                if (pk.Exports[i] is var e && IsProjClass(e) && !parents.Contains(i)
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
    public sealed record PowerKind(bool Projectile, bool Beam, bool Channeled, bool HighFlying = false, bool ChargeUp = false)
    {
        /// <summary>A short label: "Fires Projectiles", "Beam", "Charge and Release", "Channeled", or "Melee / Area".</summary>
        public string Label => Projectile ? "Fires Projectiles" : Beam ? "Beam" : ChargeUp ? "Charge and Release" : Channeled ? "Channeled" : "Melee / Area";
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
                bool beam = false, loop = false, charge = false;
                if (file != null)
                {
                    var pk = Package.Open(file);
                    // (a projectile class the power's own package holds counts too: Iceman's Rapid Fire)
                    proj |= pk.Exports.Any(e => pk.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase) && e.ObjectName.StartsWith("marvelprojectile", StringComparison.OrdinalIgnoreCase));
                    int d = Find(pk, $"marvelgamecontent.default__{pw.Class}".ToLowerInvariant());
                    // (a charge-up's looping charge isn't a channel: Human Torch's Extreme Combustion was filed as one, 2026-11-04)
                    if (d >= 0) charge = HasChargeUp(pk, d);
                    if (d >= 0) foreach (int r in Components(pk, d, out _, out _))
                    {
                        if (r <= 0) continue;
                        string c = pk.ClassOf(pk.Exports[r - 1]);
                        beam |= c.StartsWith("PowerFxBeam", StringComparison.OrdinalIgnoreCase);
                        loop |= c.Equals("PowerFxAnimationLooping", StringComparison.OrdinalIgnoreCase);
                    }
                }
                kind = new PowerKind(proj, beam, loop && !charge, Field(db, power, "HighFlying", 'B') is 1, charge);
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
        // (Kurt, 2026-11-04: Ol' Painless with Extreme Combustion "doesn't do much": the charge never played)
        if (donor.ChargeUp && !target.ChargeUp)
            lines.Add($"{donorName} charges up, then releases; {targetName} doesn't: its charge plays from {targetName}'s start and its release {(target.Channeled ? $"when {targetName}'s channel ends" : "at the hit")}.");
        if (target.ChargeUp && !donor.ChargeUp)
            lines.Add($"{targetName} charges up and {donorName} doesn't: while you hold to charge, {donorName}'s look doesn't play yet; it plays from the release.");
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

    /// <summary>
    /// The donor's character package (Kurt, 2026-11-10: team-ups' and NPC / enemy powers as donors): a hero's name (its base
    /// package, as before), or for a team-up's or an NPC's power its own package file (UC__MarvelTeamUp_Firestar_SF.upk,
    /// UC__MarvelAgent_CloneColossus_SF.upk), as the Borrow Power window gives it, else a team-up power's folder
    /// (Powers\TeamUps\&lt;Name&gt;\…) when that package exists. The entry keeps it (DonorHero).
    /// </summary>
    public static string? DonorPackage(string cooked, string? given, string power, string? powerClass, List<string> anims)
    {
        if (given != null && given.EndsWith(".upk", StringComparison.OrdinalIgnoreCase)) return File.Exists(StockFiles.For(cooked, given)) ? given : null;
        if (!IsCharacterPower(power) && HeroPackage(cooked, given, power, powerClass, anims) is string h) return h;
        var parts = power.Replace('/', '\\').Split('\\');
        int t = Array.FindIndex(parts, x => x.Equals("TeamUps", StringComparison.OrdinalIgnoreCase));
        if (t >= 0 && t + 1 < parts.Length - 1 && $"UC__MarvelTeamUp_{parts[t + 1]}_SF.upk" is var tf && File.Exists(StockFiles.For(cooked, tf))) return tf;
        return null;
    }

    /// <summary>A team-up's or an NPC / enemy power (not a hero's): Powers\TeamUps\…, Powers\EnemyPowers\…, Powers\NPCPowers\….</summary>
    public static bool IsCharacterPower(string power) => power.Replace('/', '\\').Split('\\') is var p && p.Length > 1
        && (p[1].Equals("TeamUps", StringComparison.OrdinalIgnoreCase) || p[1].Equals("EnemyPowers", StringComparison.OrdinalIgnoreCase) || p[1].Contains("NPC", StringComparison.OrdinalIgnoreCase));

    /// <summary>The package file of a DonorHero value: a package file as it is, a hero's name as its base package.</summary>
    public static string DonorFile(string donor) => donor.EndsWith(".upk", StringComparison.OrdinalIgnoreCase) ? donor : $"UC__MarvelPlayer_{donor}_SF.upk";

    /// <summary>
    /// The animations a donor character plays from sets in other packages (its component's list: an enemy's set in its parent
    /// package or a hero's), by name; the sets in its own package are searched first (Build).
    /// </summary>
    static Dictionary<string, AnimSwap.Swap> DonorPlays(string donorPackage, string cooked)
    {
        var res = new Dictionary<string, AnimSwap.Swap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ca = CostumeAnims.Read(donorPackage, Path.GetFileName(donorPackage), CostumeAnims.FilesFor(null, cooked));
            foreach (var a in ca?.Anims ?? [])
                if (a.From.File != null && a.Export >= 0 && a.From.Kind != CostumeAnims.Source.Shared) res[a.Name] = new AnimSwap.Swap(a.Name, a.From.File, a.From.Export, a.Export);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return res;
    }

    /// <summary>A hero's name from a power prototype's path (Powers\Player\&lt;Hero&gt;\…), else null.</summary>
    /// <summary>
    /// The power has an icon of its own in the game (Kurt, 2026-11-10: Kingpin's Obliterator Cane and many enemy powers name only
    /// the "No Icon" placeholder, powernoicon): Use Its Icon is grayed out for the others.
    /// </summary>
    public static bool HasOwnIcon(Fx.GameData db, string power) =>
        db.Find(power) is { } d && Fx.PowerList.Field(db, d.Id, "IconPath", 'A', true) is { Found: true } f && db.Assets.TryGetValue(f.Raw, out var a)
        && Costume.IconTexture(a.Asset.Name) is { } t && !t.Texture.Contains("noicon", StringComparison.OrdinalIgnoreCase);

    public static string? HeroOf(string power)
    {
        var parts = power.Replace('/', '\\').Split('\\');
        int i = Array.FindIndex(parts, x => x.Equals("Player", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < parts.Length - 1 ? parts[i + 1] : null;
    }

    /// <summary>
    /// Other enabled mods whose copy of one of <paramref name="hero"/>'s costume packages sets an animation list of its own
    /// without the borrowed sets: on that costume the borrowed animations aren't found, so the character stands in the rest pose
    /// for the borrowed power (Kurt, 2026-11-05: Black Bolt with Squirrel Girl's It's Raining Rodents A-posed; Havok Full
    /// Conversion and X-Factor Havok for Conversion replace his ANAD and Classic costumes with lists of their own; with both off
    /// the borrow played). Shipped: the stock costume has a list of its own too, so the borrow ships that package as well. Returns (mod name, costume package, shipped).
    /// </summary>
    public static List<(string Mod, string File, bool Shipped)> CostumeListsHiding(ModLibrary lib, string? editingFolder, string hero, string? cooked, bool includeOff = false)
    {
        var res = new List<(string, string, bool)>();
        string prefix = $"UC__MarvelPlayer_{hero}_";
        foreach (var m in lib.Mods.Where(m => (m.Enabled || includeOff) && m.Folder != editingFolder))
            foreach (string f in m.Manifest.UpkReplacements.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith("_SF.upk", StringComparison.OrdinalIgnoreCase)
                && !f.Equals(prefix + "SF.upk", StringComparison.OrdinalIgnoreCase)))
            {
                string path = Path.Combine(m.Folder, f);
                if (!File.Exists(path)) continue;
                CostumeAnims? ca;
                try { ca = CostumeAnims.Read(path, f, CostumeAnims.FilesFor(m, cooked)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                if (ca == null || ca.Inherited || ca.Sets.Any(s => s.Path.Contains("_on_", StringComparison.OrdinalIgnoreCase))) continue;
                // the stock costume has a list of its own too: the borrow ships its own copy of it (Build), so the two mods carry
                // the same package and the higher one in the list wins
                bool shipped = false;
                if (cooked != null && StockFiles.For(cooked, f) is var st && File.Exists(st))
                    try { shipped = CostumeAnims.Read(st, f, CostumeAnims.FilesFor(null, cooked)) is { Inherited: false }; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
                res.Add((m.Manifest.Name, f, shipped));
            }
        return res;
    }

    /// <summary>The warning for <see cref="CostumeListsHiding"/>, else null.</summary>
    public static string? CostumeListsNote(List<(string Mod, string File, bool Shipped)> hiding, string hero)
    {
        if (hiding.Count == 0) return null;
        string Costume(string f) => f[$"UC__MarvelPlayer_{hero}_".Length..^"_SF.upk".Length];
        string List(IEnumerable<(string Mod, string File, bool Shipped)> h) => string.Join(", ", h.GroupBy(x => x.Mod).Select(g => $"\"{g.Key}\" ({string.Join(", ", g.Select(x => Costume(x.File)))})"));
        string name = AutoTags.DisplayName(hero) ?? hero;
        var parts = new List<string>();
        if (hiding.Where(h => !h.Shipped).ToList() is { Count: > 0 } hidden)
            parts.Add($"Other mods you have on replace some of {name}'s costumes with an animation list of their own: {List(hidden)}. In those costumes the borrowed animations aren't found, and the character stands still in the rest pose for this power.");
        if (hiding.Where(h => h.Shipped).ToList() is { Count: > 0 } both)
            parts.Add($"This borrow and other mods you have on both carry some of {name}'s costumes: {List(both)}. The mod higher in the list wins: if it's this one, those costumes lose the other mod's look; if it's the other, they stand still in the rest pose for this power.");
        return string.Join(" ", parts);
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

    /// <summary>
    /// A power with no animation of its own that is still cast (2026-11-08, a user: Invisible Woman's Seeker Orbs and Psylocke's
    /// Katana Typhoon play none, by design): its Activation isn't Passive and it isn't a toggle. It can take a borrowed look,
    /// whose animation is added under the donor's name (confirmed in game: Seeker Orbs with Jean Grey's Telekinetic Throw).
    /// Passives and talents can't.
    /// </summary>
    public static bool CastWithoutAnimation(Fx.GameData db, string power)
    {
        string? act = Field(db, power, "Activation", 'A') is long a && db.Assets.TryGetValue((ulong)a, out var ae) ? ae.Asset.Name : null;
        if (act == null || act.Equals("Passive", StringComparison.OrdinalIgnoreCase)) return false;
        return Field(db, power, "IsToggled", 'B') is not 1;
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

    /// <summary>Read-only (--proto-fields): every simple field of a prototype and its parents, nearest first, as "name (type) = value".</summary>
    internal static List<string> FieldList(Fx.GameData db, string path)
    {
        var list = new List<string>();
        if (db.Find(path.Replace('/', '\\')) is not { } pe) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ulong id = pe.Id;
        for (int guard = 0; guard < 32 && id != 0; guard++)
        {
            var d = db.Prototype(id).Data;
            foreach (var gr in d.Groups)
                foreach (var f in gr.Simple)
                {
                    string n = db.FieldName(gr.Blueprint, f.Id);
                    if (!seen.Add(n)) continue;
                    string v = f.Type switch { 'B' => (f.Value.Raw != 0).ToString(), 'D' => BitConverter.Int64BitsToDouble((long)f.Value.Raw).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture), 'L' => ((long)f.Value.Raw).ToString(), _ => $"0x{f.Value.Raw:X}" };
                    list.Add($"{n} ({f.Type}) = {v}{(guard > 0 ? "  (inherited)" : "")}");
                }
            if (!d.HasParent) break;
            id = d.Parent;
        }
        return list;
    }

    /// <summary>
    /// The game's lift for a high-flying power (Kurt, 2026-11-02, from the 1.53 client's code: Power::applyInternal switches the
    /// locomotor to HighFlying when the power applies; AMarvelEntity::MoveTo / physCustom then raise the hero in a straight line
    /// at the class's flyingZSpeed to Globals' HighFlyingHeight, and lower him at landingZSpeed when it ends). A channeled,
    /// non-recurring power applies at ChannelStartTimeMS × AnimationContactTimePercent (MHServerEmu Power.GetActivationTime):
    /// Hawkeye's flight 0.75 × 0.35 = 0.26 s, the flight default 0.75 s. Returns when it applies (seconds after activation) and
    /// the height; null when the power isn't high-flying.
    /// </summary>
    /// <summary>A package holding a borrow's output: a power's or a condition's default with borrowed components (borrowed_…)
    /// under it. Only those defaults count: the hero's own package may carry the mod's other edits and is never judged by this.</summary>
    static bool HoldsBorrow(string path)
    {
        try
        {
            var p = Package.Open(path);
            for (int i = 0; i < p.Exports.Length; i++)
                if (p.Exports[i].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)
                    && (p.PathOf(p.Exports[i]).StartsWith("marvelgamecontent.default__power", StringComparison.OrdinalIgnoreCase)
                        // a travel condition the borrow gave the vehicle (borrowed_vehicle_attach): kept as a start, the next
                        // build found no vehicle there to move, left the power's spawn in, and Cyclops rode two gliders
                        // (telemetry ride43, Kurt 2026-11-04)
                        || p.PathOf(p.Exports[i]).StartsWith("marvelgamecontent.default__marvelconditioneffect", StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return false;
    }

    /// <summary>
    /// A borrow's climb as its build sets it (PowerBorrowEntry.Climb), without building: for the Height panel before the
    /// borrows were built this session. Null when the borrow isn't lifted (its timing kept, the target doesn't fly high, the
    /// donor flies high itself) or its hero isn't known yet.
    /// </summary>
    public static (float OwnSpeed, float ApplyAt, float LoopAt, float Height, float Fall)? ClimbOf(Fx.GameData db, string cooked, PowerBorrowEntry e)
    {
        if (e.KeepTiming || string.IsNullOrEmpty(e.Hero) || Field(db, e.Donor, "HighFlying", 'B') is 1 || FlightLift(db, e.Power) is not { } fc) return null;
        var (own, fall) = Gui.PowerPreview.FlyingSpeeds(StockFiles.For(cooked, $"UC__MarvelPlayer_{e.Hero}_SF.upk"), e.Hero);
        return (own, fc.ApplyAt, fc.ChannelStart, fc.Height, fall);
    }

    public static (float ApplyAt, float Height, float ChannelStart)? FlightLift(Fx.GameData db, string power)
    {
        if (Field(db, power, "HighFlying", 'B') is not 1) return null;
        double start = Field(db, power, "ChannelStartTimeMS", 'L') is long ms ? ms / 1000.0 : 0.75;
        double contact = Field(db, power, "AnimationContactTimePercent", 'D') is double c ? c : 1;
        double height = Field(db, "Globals/Globals.defaults", "HighFlyingHeight", 'D') is double h ? h : 140;
        return ((float)(start * contact), (float)height, (float)start);
    }

    /// <summary>
    /// The hero's climb speed (his class default's flyingZSpeed, which the client lifts him at: FlightLift) set to
    /// <paramref name="speed"/> in the hero's base package (bytes), or added when the class has none of its own; verified.
    /// The user's Climb Speed on the Powers tab (PowerBorrowEntry.ClimbSpeed): only an existing package of the mod changes.
    /// </summary>
    public static byte[] SetClimbSpeed(byte[] heroPackage, string heroClass, float speed, List<string> log)
    {
        var dst = Package.FromBytes(heroPackage);
        string path = $"marvelgamecontent.default__{heroClass}".ToLowerInvariant();
        int d = Array.FindIndex(dst.Exports, e => dst.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (d < 0) throw new InvalidDataException($"no {path} in the hero's package: the climb speed can't be set");
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var tags = TagWalker.Walk(dst, b, 4) ?? throw new InvalidDataException($"{path}'s properties don't read: the climb speed can't be set");
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] nb;
        if (tags.FirstOrDefault(t => t.Name.Equals("flyingZSpeed", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st)
        {
            nb = b.ToArray();
            BinaryPrimitives.WriteSingleLittleEndian(nb.AsSpan(st.ValueAt), speed);
        }
        else
        {
            var tag = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("flyingZSpeed"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("FloatProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
            BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), speed);
            nb = [.. b.AsSpan(0, tags.NoneAt), .. tag, .. b.AsSpan(tags.NoneAt)];
        }
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the climb speed: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {heroClass}: climb speed {speed:0} a second (flyingZSpeed, set on the Powers tab)");
        return output;
    }

    /// <summary>The lift a high-flying power gives <paramref name="t"/> seconds after activation (FlightLift, the hero's
    /// flyingZSpeed): none until it applies, then up in a straight line to the full height.</summary>
    public static float LiftAt(float t, float applyAt, float rise, float height) => Math.Clamp((t - applyAt) * rise, 0, height);

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
        (IReadOnlySet<string> Classes, IReadOnlySet<string> Slots)? held = null, IReadOnlyList<string>? heldBy = null,
        IReadOnlyDictionary<string, List<(float At, float Z)>>? heights = null, List<string>? slotsOut = null, (float ApplyAt, float Rise, float ChannelStart, float Fall)? flight = null, bool endFromLoop = false, Action<bool>? canChooseEnd = null,
        bool conditionStart = false,
        ((float ApplyAt, float Height, float ChannelStart) Target, float Rise, float DonorApplyAt, float DonorChannelStart)? takeoff = null,
        IReadOnlyDictionary<string, string>? costumeModPackages = null, IReadOnlySet<string>? modPackages = null,
        bool anims = true, bool effects = true, bool takeProps = true)
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
            // Whole Power's parts: a projectile's look is an effect
            if (pr.Role == "projectile" && !effects) { log.Add($"  {pr.TargetClass}: keeps its own look (Effects unticked)"); continue; }
            done[tf] = Graft(cur, td, pr.DonorFile, dd, log, cooked: cooked, take: anims && effects && takeProps ? null : cls => PartOf(cls) switch { "anim" => anims, "prop" => takeProps, _ => effects });
            if (pr.Role == "projectile") done[tf] = ProjectileMesh(ProjectileSlots(done[tf], td, pr.DonorFile, dd, log), td, pr.DonorFile, dd, log);
            if (crossHero) done[tf] = SpawnBorrowedAttachments(done[tf], td, log, held, HeroSlots(targetHeroPackage));
            // a spawned vehicle sized for this hero: the Vehicle Fit's size, else the two heroes' heights (Kurt, 2026-10-20:
            // Cyclops' bike looked small under Thor; x1.14 by the heights looked right in game)
            float vscale = vehicleScale is float vs && vs > 0 ? vs : HeroHeight(targetHeroPackage) is float th0 && HeroHeight(donorHeroPackage) is float dh0 && dh0 > 1 ? th0 / dh0 : 1;
            // (MHO_BORROW_VEHICLE_SCALE_FORCE=<n>: that scale instead, to see whether it reaches the vehicle at all)
            if (float.TryParse(Environment.GetEnvironmentVariable("MHO_BORROW_VEHICLE_SCALE_FORCE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float vforce) && vforce > 0) vscale = vforce;
            if (crossHero && pr.Role == "power" && spawn is { Count: > 0 } && cooked != null) done[tf] = SpawnAttachments(done[tf], td, spawn, cooked, log, HeroBones(targetHeroPackage), holdAnims: donorAnims, heroSlots: HeroSlots(targetHeroPackage));
            // a prop the costumes in heldBy hold now (added to them): its spawn left out for them, so the power throws the held one
            if (crossHero && pr.Role == "power" && held is { Classes.Count: > 0 } hc && heldBy is { Count: > 0 }) done[tf] = NotForHolders(done[tf], td, hc.Classes, heldBy, log);
            // (Animations unticked: this power's own animation holds this hero's weapons)
            if (crossHero && pr.Role == "power" && cooked != null && !keepWeapons && anims) done[tf] = HideHeldWeapons(done[tf], td, targetHeroPackage, donorHeroPackage, cooked, log, spawn?.Where(x => !x.Vehicle).Select(x => x.Slot));
            else if (crossHero && pr.Role == "power" && keepWeapons) log.Add("  this hero's weapons stay shown while the power runs (Keep This Hero's Weapons)");
            // every prop the power now brings (a spawned vehicle or weapon, the donor's own shown ones): its fit
            if (crossHero && pr.Role == "power")
            {
                var vehicleClasses = (spawn ?? []).Where(x => x.Vehicle).Select(x => Package.Open(x.File).Exports[x.Export].ObjectName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                done[tf] = FitProps(done[tf], td, vehicleClasses, vscale, props, log, spawnedHere, ReparentedBones(donorHeroPackage, targetHeroPackage));
            }
            // a travel power bringing a vehicle: no entity lists in the power (they don't work there, see SlotFadesForAll);
            // the costumes kept stock get their ride through the condition and their own animations under the borrowed names
            if (ownClassCostumes is { Count: > 0 } && ownTracks && pr.Role == "power" && spawn?.Any(x => x.Vehicle) == true) done[tf] = SlotFadesForAll(done[tf], td, pr.TargetFile, ownClassCostumes, log);
            // a travel power keeps no stock components for the classes sharing it (Kurt, 2026-11-04): Cap's Stride kept his 0.5 s
            // run loop for Domino, MODOK ... beside the borrowed swing, and it set the loop-repeat clock for everyone (Spider-Man's
            // web casts every 0.5 s on Cap; without it, his stock times exactly: telemetry ride51 / ride52); entity lists on a
            // power's animation don't work anyway (see SlotFadesForAll). Those classes get the borrowed look too
            else if (ownClassCostumes is { Count: > 0 } && !ownTracks) done[tf] = KeepStockFor(done[tf], td, pr.TargetFile, ownClassCostumes, log);
            else if (ownClassCostumes is { Count: > 0 } && pr.Role == "power") log.Add($"  {td}: the borrowed look for everyone using this travel power, {string.Join(", ", ownClassCostumes)} too (a kept stock loop would set the borrowed parts' repeat clock)");
        }
        donorAnims = donorAnims.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (keepTiming) { log.Add("anims: the target's own (its timing kept)"); return done; }
        if (!anims) { log.Add("anims: the target's own (Animations unticked)"); return done; }
        if (donorAnims.Count == 0) { log.Add("anims: the donor power plays no animation of its own"); return done; }
        // a looping animation (a travel power's start / loop / end) plays as long as the power runs: fitted to the target's
        // length, Spider-Man's 6 s swing loop on Thor's flight played ×4.6 (Kurt, 2026-10-20: flips way too fast)
        if (fit && donorLoops) { fit = false; log.Add("anims: the donor's animation loops, so it keeps its own speed"); }
        // the target's own power animation: the length the borrowed ones are fitted to (its longest, when it has several)
        float targetLen = targetAnims.Select(a => Length(targetHeroPackage, a)).DefaultIfEmpty(0).Max();
        // the donor's animations with a hit marker (see the fit below)
        var contactsAt = new Lazy<Dictionary<string, (float Time, float Length)>>(() => Gui.PowerPreview.ContactFramesIn(donorHeroPackage));
        var ap = AnimExportCli.Packages.Package.Open(donorHeroPackage);
        var swaps = new List<AnimSwap.Swap>();
        var mountSkeleton = crossHero ? HeroSkeleton(donorHeroPackage) : null;
        var loopCuts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool targetNoMount = HeroBones(targetHeroPackage) is { } tb0 && !tb0.Contains("g_mountattach");
        // the game's lift, as its code does it (FlightLift): none until the power applies, then up at the hero's flyingZSpeed;
        // fully up at liftDone seconds after activation (Hawkeye: 0.26 + 140 / 170 = 1.09 s)
        float liftDone = flight is { } fl0 ? fl0.ApplyAt + Math.Abs(lift) / fl0.Rise : 0;
        // the donor hero's form sets (Jean Grey's Phoenix set: the same names as her normal ones, the same 165 bones): a tie goes
        // to the normal set (2026-11-08: Seeker Orbs borrowing her Telekinetic Throw took the Phoenix, flying version)
        var formSets = new HashSet<int>();
        try
        {
            var dmp = Package.Open(donorHeroPackage);
            int ddef = ModMeshes.CharacterDefault(dmp);
            string dcls = ddef >= 0 ? dmp.ClassOf(dmp.Exports[ddef]) : "MarvelPlayer_" + Path.GetFileNameWithoutExtension(donorHeroPackage)["UC__MarvelPlayer_".Length..^3];
            foreach (var (_, sp) in CostumeAnims.Aliases(dmp, dcls))
                if (Array.FindIndex(dmp.Exports, e => dmp.PathOf(e).Equals(sp, StringComparison.OrdinalIgnoreCase)) is int fi && fi >= 0) formSets.Add(fi);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException or ArgumentException) { }
        foreach (string an in donorAnims)
        {
            // the character's set, not a vehicle's: Cyclops' bike has a set of its own (cyclops_bike_as, 22 chopper bones) with the
            // same sequence names as his riding ones in cyclops_as (89 bones): the set with the most bones is the rider's
            AnimSwap.Swap? found = null;
            int foundBones = -1;
            bool foundForm = false;
            foreach (var set in AnimExportCli.Animation.AnimObjectReader.FindAnimSets(ap))
                foreach (var seq in set.Sequences.Where(q => q.IsExport))
                    if ((set.TrackBoneNames.Count > foundBones || set.TrackBoneNames.Count == foundBones && foundForm && !formSets.Contains(set.ExportIndex))
                        && AnimExportCli.Animation.AnimObjectReader.GetSequenceDisplayName(ap, seq.ExportIndex).Equals(an, StringComparison.OrdinalIgnoreCase))
                    {
                        found = new AnimSwap.Swap(an, donorHeroPackage, set.ExportIndex, seq.ExportIndex);
                        foundBones = set.TrackBoneNames.Count;
                        foundForm = formSets.Contains(set.ExportIndex);
                    }
            if (found == null && cooked != null && DonorPlays(donorHeroPackage, cooked).TryGetValue(an, out var via)) found = via;
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
            // ... not one with a hit marker (MarvelAnimNotify_ContactFrame): the game already plays it so the marker lands on the
            // power's contact, and multiplies that by the sequence's RateScale (telemetry ride62, Kurt 2026-11-05: Deadpool's Real
            // Armor Buster fitted ×3.73 to Doctor Strange's 0.37 s bolts played at 2.575 × 3.727 = 9.6, done in 0.14 s; the hands
            // went back to his spell pose and his spawned cannon, played at the body's 9.6 too, finished as early)
            bool marked = contactsAt.Value.ContainsKey(an);
            float rate = fit && !marked && targetLen > 0.01f && donorLen > 0.01f ? donorLen / targetLen : 1;
            if (fit && marked && targetLen > 0.01f && Math.Abs(donorLen / targetLen - 1) > 0.01f) log.Add($"anims: {an} has a hit marker: the game times it to this power's contact, so it isn't fitted");
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
            // 2026-11-02 (Kurt: "trust math instead of all this guessing"): with the game's lift known (FlightLift), the start is
            // played at least as long as the climb takes (else the loop, lowered the full height, starts sunk), and its body is
            // lowered by exactly the lift at each moment: none until the power applies, then in a straight line. On the floor by
            // construction; height keys add to it.
            // The client plays the start over exactly the power's wind-up, whatever its own length or RateScale
            // (PowerFXAnimationLooping.Activate: startAnimRate = length / ChannelStartTime), then the loop: so the start is
            // lowered on that clock (a frame at fraction f plays at f × the wind-up) and its own speed is left alone.
            // a high-flying donor on a high-flying target (Thor's flight on Kitty Pryde: Thor's lift applies at 0.675 s, Kitty's at
            // 0; 2026-11-03 telemetry: she rose from the first frame while his crouch-and-launch start played): the start keeps the
            // body on the floor until the donor's launch moment (the game's lift taken off), then rises to meet the lift by the
            // start's end, where the loop takes over at the game's height (the loop is shared by every repeat: never keyed)
            if (ramp > 0 && takeoff is { } tk && tk.Target.ChannelStart > 0.01f && donorLen > 0.01f)
            {
                float w = tk.Target.ChannelStart, h = tk.Target.Height;
                float launch = Math.Clamp(tk.DonorApplyAt / Math.Max(0.01f, tk.DonorChannelStart), 0, 0.98f);
                float TL(float f) => LiftAt(f * w, tk.Target.ApplyAt, tk.Rise, h);
                var model = new List<(float At, float Z)> { (0, -TL(0)) };
                float applyF = Math.Clamp(tk.Target.ApplyAt / w, 0, 1);
                if (applyF > 0 && applyF < launch) model.Add((applyF, -TL(applyF)));
                model.Add((launch, -TL(launch)));
                model.Add((1, 0));
                swaps.Add(found with { Model = model });
                log.Add($"anims: {an} plays over the {w:0.##} s wind-up: on the floor until the other hero's launch ({launch * w:0.##} s, its power applies {tk.DonorApplyAt:0.##} s in), "
                        + $"then up to the game's lift ({TL(1):0} at the loop; this hero climbs {tk.Rise:0} a second{(TL(1) < h - 0.5f ? $", level {(h - TL(1)) / tk.Rise:0.##} s into the loop" : "")})");
                continue;
            }
            if (ramp > 0 && flight is { } fl && fl.ChannelStart > 0.01f && donorLen > 0.01f)
            {
                float h = Math.Abs(lift), w = fl.ChannelStart;
                var model = new List<(float At, float Z)> { (0, 0), (Math.Min(1, fl.ApplyAt / w), 0) };
                if (liftDone < w) model.Add((liftDone / w, -h));
                model.Add((1, -LiftAt(w, fl.ApplyAt, fl.Rise, h)));
                swaps.Add(found with { Model = model });
                // the travel condition plays the start too, from its own first frame when it applies, at the start's own
                // speed (Kurt's recording, 2026-11-03: Clint stood, then Cyclops' bike rolled in from its first frame and
                // high): a copy of its own (ConditionStartSuffix), lowered on that clock: the lift from its first frame
                if (conditionStart)
                {
                    var cmodel = new List<(float At, float Z)> { (0, 0) };
                    float up = h / Math.Max(1, fl.Rise);
                    cmodel.Add(up < donorLen ? (up / donorLen, -h) : (1, -fl.Rise * donorLen));
                    if (up < donorLen) cmodel.Add((1, -h));
                    swaps.Add(found with { Slot = an + ConditionStartSuffix, Model = cmodel });
                    log.Add($"anims: {an}{ConditionStartSuffix} (the travel condition's start, {donorLen:0.##} s from when it applies): lowered as the game lifts from its first frame, all {h:0} after {Math.Min(up, donorLen):0.##} s");
                }
                float atLoop = LiftAt(w, fl.ApplyAt, fl.Rise, h);
                log.Add($"anims: {an} plays over the {w:0.##} s wind-up, lowered as the game lifts: from {fl.ApplyAt:0.##} s (the power applies) at {fl.Rise:0} a second, {atLoop:0} by the loop"
                        + (atLoop < h - 0.5f ? $"; the loop starts {h - atLoop:0} low and is level after {(h - atLoop) / fl.Rise:0.##} s (the climb is still going)" : ""));
                continue;
            }
            // the donor's own end (Cyclops's ride on Hawkeye's Sky-Cycle; Kurt, 2026-11-02: it rose 57 above the floor on the
            // old tuned ramp): lowered by exactly the game's descent, from the flying height at the hero's landingZSpeed from the
            // end's first frame, as the End choice does for a donor without one
            if (ramp < 0 && flight is { } fe0 && donorLen > 0.01f)
            {
                float h = Math.Abs(lift), playLen = donorLen / (rate > 0 ? rate : 1), down = h / Math.Max(1, fe0.Fall);
                var model = new List<(float At, float Z)> { (0, -h), down < playLen ? (down / playLen, 0) : (1, -Math.Max(0, h - fe0.Fall * playLen)) };
                if (down < playLen) model.Add((1, 0));
                swaps.Add(found with { Rate = rate, Model = model });
                log.Add($"anims: {an} ({playLen:0.##} s) lowered as the game lands: from {h:0} at {fe0.Fall:0} a second ({down:0.##} s)");
                continue;
            }
            float atStart = lift * 3f / 140f, atEnd = lift * 151f / 140f;   // (Kurt, in game: 0.60.44's end (151) "ends really well"; its beginning (13) still dipped: 10 units up)
            swaps.Add(ramp != 0 ? found with { Rate = rate, Lift = atStart, Ramp = ramp, RampBy = atEnd - atStart } : found with { Rate = rate, Lift = lift });
        }
        // a ground run with no end animation on a flyer whose own power lands with one (Hawkeye's Sky-Cycle with Cap's Stride):
        // the user's End choice (Kurt, 2026-11-02; PowerBorrowEntry.End). The Donor's Loop, Landing: the empty end slot plays
        // the donor's loop at its own speed, its body lowered by exactly the game's descent (from the flying height at the
        // hero's landingZSpeed, from the end's first frame: when the descent begins is a reading to check in game), so the feet
        // stay on the floor, as the start is lowered by the climb
        bool endable = lift != 0 && powerFile != null && flight != null
            && !donorAnims.Any(a => a.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
            && targetAnims.Any(a => a.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
            && swaps.Any(s => s.Slot.EndsWith("_loop", StringComparison.OrdinalIgnoreCase));
        canChooseEnd?.Invoke(endable);
        if (endable && endFromLoop && flight is { } fe && swaps.FirstOrDefault(s => s.Slot.EndsWith("_loop", StringComparison.OrdinalIgnoreCase)) is { } endLoop)
        {
            string endName = endLoop.Slot[..^"_loop".Length] + "_end";
            var pairE = pairs.First(p => p.Role == "power");
            byte[] filled = FillEndSlot(done[powerFile!], $"marvelgamecontent.default__{pairE.TargetClass}".ToLowerInvariant(), endName, log);
            float len = Length(donorHeroPackage, endLoop.Slot);
            if (!ReferenceEquals(filled, done[powerFile!]) && len > 0.01f)
            {
                done[powerFile!] = filled;
                float h = Math.Abs(lift), down = h / Math.Max(1, fe.Fall);
                var model = new List<(float At, float Z)> { (0, -h) };
                model.Add(down < len ? (down / len, 0) : (1, -Math.Max(0, h - fe.Fall * len)));
                if (down < len) model.Add((1, 0));
                swaps.Add(endLoop with { Slot = endName, Rate = 0, Lift = 0, Ramp = 0, RampBy = 0, RampPower = 1, Model = model });
                log.Add($"anims: {endName} = the donor's {endLoop.Slot} ({len:0.##} s), lowered as the game lands: from {h:0} at {fe.Fall:0} a second ({down:0.##} s)");
            }
            else log.Add($"anims: the end couldn't be filled with the donor's {endLoop.Slot} (its slot isn't empty, or the loop has no length)");
        }
        // the Animations tab's height keys, on top of the lift (the bones under the root, never the root: AnimSwap.Build)
        if (heights is { Count: > 0 })
            for (int i = 0; i < swaps.Count; i++)
                if (heights.TryGetValue(swaps[i].Slot.EndsWith(ConditionStartSuffix, StringComparison.OrdinalIgnoreCase) ? swaps[i].Slot[..^ConditionStartSuffix.Length] : swaps[i].Slot, out var hk) && hk.Count > 0) { swaps[i] = swaps[i] with { Heights = hk }; log.Add($"anims: {swaps[i].Slot}: {hk.Count} height key(s)"); }
        slotsOut?.AddRange(swaps.Select(s => s.Slot).Where(s => !s.EndsWith(ConditionStartSuffix, StringComparison.OrdinalIgnoreCase)));
        if (swaps.Count > 0)
        {
            string hf = Path.GetFileName(targetHeroPackage);
            string heroPath = startFrom?.Invoke(hf) ?? targetHeroPackage;
            var temps = new List<string>();
            (HashSet<string> Wanted, string? Hero)? costumeSockets = null;
            try
            {
            if (done.TryGetValue(hf, out var hb)) temps.Add(heroPath = WriteTemp(hf, hb));
            // another hero's skeleton: bones that rest turned differently get their keys turned to fit (Thor's right toes)
            var turns = crossHero && HeroSkeleton(donorHeroPackage) is { } dsk && HeroSkeleton(targetHeroPackage) is { } tsk ? AnimSwap.RestTurns(dsk, tsk) : null;
            // Pivot on the Hand: the props' bones the borrowed animations stop moving (they still turn them)
            var pinned = (props ?? []).Where(f => f.PinPivot).SelectMany(f => spawnedHere.Where(s => s.Class.Equals(f.Class, StringComparison.OrdinalIgnoreCase)).SelectMany(s => s.Bones ?? []))
                .Where(b => !b.Equals("none", StringComparison.OrdinalIgnoreCase) && !b.Equals("root", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            done[hf] = AnimSwap.Build(heroPath, targetHeroClass, swaps, log, retarget: turns, rider: rider, skeleton: HeroSkeleton(targetHeroPackage), noPositions: pinned,
                donorSkeleton: crossHero ? HeroSkeleton(donorHeroPackage) : null,
                // (a team-up's, an NPC's or an enemy's body: its bone lengths aren't a hero's; heroes as before)
                ownLengths: crossHero && !Path.GetFileName(donorHeroPackage).StartsWith("UC__MarvelPlayer_", StringComparison.OrdinalIgnoreCase));
            // the target's own animated bones the donor's animations don't move (Thor's cape in Kitty's Air Walking) get the
            // target's own run's keys, looped (travel borrows; tested on Thor's six, Kurt 2026-10-20 / 21)
            // ... and an attack too, from the target's own power animation, every bone it poses (Kurt, 2026-11-04: Angela with
            // Cyclops' Tactical Disengage locked to an A-pose: his set drives 82 of her 176 bones; her ribbons, dress, cape,
            // hair and extra knee bones sat at rest)
            if (crossHero)
                done[hf] = AddOwnTracks(done[hf], "_on_" + targetHeroClass.ToLowerInvariant(), targetHeroPackage, targetAnims, log, attack: !ownTracks);
            if (loopCuts.Count > 0) done[hf] = TrimLoops(done[hf], "_on_" + targetHeroClass.ToLowerInvariant(), loopCuts, log);
            // the sockets the borrowed effects spawn on that this hero's model lacks (Thor's socket_hammer on Kitty Pryde)
            if (crossHero && powerFile != null && pairs.FirstOrDefault(x => x.Role == "power") is { } pp
                && SpawnSocketNames(done[powerFile], $"marvelgamecontent.default__{pp.TargetClass}".ToLowerInvariant()) is { Count: > 0 } wanted)
            {
                string? heroName = targetHeroClass.StartsWith("MarvelPlayer_", StringComparison.OrdinalIgnoreCase) ? targetHeroClass["MarvelPlayer_".Length..] : null;
                // sockets the donor turns another way than this hero (Emma's socket_head forward, Blade's sideways): the borrowed
                // effects spawn on the donor's, added under its own name
                string ppDefault = $"marvelgamecontent.default__{pp.TargetClass}".ToLowerInvariant();
                if (TurnedSockets(donorHeroPackage, targetHeroPackage, wanted, log) is { Count: > 0 } turned)
                {
                    done[powerFile] = UseBorrowedSockets(done[powerFile], ppDefault, turned, log);
                    wanted = SpawnSocketNames(done[powerFile], ppDefault);
                }
                done[hf] = AddBorrowedSockets(done[hf], donorHeroPackage, wanted, cooked, log, heroName);
                costumeSockets = (wanted, heroName);   // (the costumes' models: after their animation lists, below)
            }
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
            // ... and the sockets on the hero's costumes with a model of their own (Cyclops 90s X-Men: Kurt chose to ship those
            // packages, 2026-11-03; naming the bone instead didn't work in game). A costume package a costume mod replaces is left
            // out, unless the borrow ships it anyway (a costume with its own animation list, above: Kurt, 2026-11-03, Cyclops'
            // Astonishing came without the socket while the borrow carried it for its list)
            if (cooked != null && costumeSockets is var (cwanted, cheroName))
            {
                string stem = Path.GetFileNameWithoutExtension(hf), cprefix = stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
                var costumeFiles = new List<string>();
                try
                {
                    costumeFiles.AddRange(Directory.EnumerateFiles(cooked, cprefix + "_*_SF.upk").Select(Path.GetFileName).OfType<string>()
                        .Where(f => !f.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Contains("copy", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                catch (IOException) { }
                var leftOut = new List<string>();
                foreach (string cf in costumeFiles)
                {
                    bool shipped = done.ContainsKey(cf) || modPackages?.Contains(cf) == true;
                    if (!shipped && costumeModPackages != null && costumeModPackages.TryGetValue(cf, out var other)) { leftOut.Add($"{cf} ({other})"); continue; }
                    byte[] start;
                    if (done.TryGetValue(cf, out var cb)) start = cb;
                    else if ((startFrom?.Invoke(cf) ?? StockFiles.For(cooked, cf)) is { } sp && File.Exists(sp)) start = File.ReadAllBytes(sp);
                    else continue;
                    byte[] withSockets = AddBorrowedSockets(start, donorHeroPackage, cwanted, cooked, log, cheroName);
                    if (!ReferenceEquals(withSockets, start)) done[cf] = withSockets;
                }
                if (leftOut.Count > 0) log.Add($"  not given the borrowed effects' sockets (a costume mod replaces them): {string.Join(", ", leftOut)}");
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
    /// <param name="heroSlots">The weapon slots the hero's own attachments fill (any, shown on demand too); a slot outside
    /// them isn't hidden: the hero has nothing there, and the spawned weapon of that slot would be hidden too (Kurt,
    /// 2026-11-04: Blade with Punisher's Full Auto hid the M60 it spawned, slot m60).</param>
    public static byte[] SpawnBorrowedAttachments(byte[] package, string targetDefault, List<string> log, (IReadOnlySet<string> Classes, IReadOnlySet<string> Slots)? held = null, IReadOnlySet<string>? heroSlots = null)
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
            // (only the other hero's: this power's own, kept when Props is unticked, shows this hero's own weapons)
            if (!e.ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(e).ToArray();
            var tags = TagWalker.Walk(p, d, 16);
            if (tags == null) continue;
            var show = tags.FirstOrDefault(t => t.Name.Equals("AttachmentsToShowOnActivate", StringComparison.OrdinalIgnoreCase));
            var slot = tags.FirstOrDefault(t => t.Name.Equals("WeaponSlotToShowOnActivate", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("WhileActivatedShowWeaponSlot", StringComparison.OrdinalIgnoreCase));
            var swt = tags.FirstOrDefault(t => t.Name.Equals("SwitchAttachments", StringComparison.OrdinalIgnoreCase));
            if (show == null && slot == null && swt == null) continue;
            var classes = new List<int>();
            if (show != null)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(show.ValueAt));
                for (int k = 0; k < n; k++) { int c = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(show.ValueAt + 4 + 4 * k)); if (c != 0) classes.Add(c); }
            }
            string? slotName = slot != null ? TagWalker.NameAt(p, d, slot.ValueAt) : swt != null ? SwitchTo(p, d, swt) : null;
            // a prop the hero holds now (an added prop): shown as the other hero's power shows its own, not spawned
            string ClassName(int c) => c > 0 ? p.Exports[c - 1].ObjectName : p.RefName(c);
            if (held is { } h && (classes.Count > 0 ? classes.All(c => h.Classes.Contains(ClassName(c))) : slotName != null && h.Slots.Contains(slotName)))
            {
                log.Add($"  {e.ObjectName}: shows this hero's held {(classes.Count > 0 ? string.Join(", ", classes.Select(ClassName)) : slotName)} (an added prop), as the other hero's power does");
                continue;
            }
            // the component's other properties (timing, conditions) stay; the show / hide lists and slots go
            string[] drop = ["AttachmentsToShowOnActivate", "AttachmentsToHideOnActivate", "WeaponSlotToShowOnActivate", "WhileActivatedShowWeaponSlot", "WhileActivatedAttachmentClass", "WhileActivatedHideWeaponSlot", "SwitchAttachments"];
            byte[] head = d[..16];
            var keep = new MemoryStream();
            foreach (var t in tags) if (!drop.Any(x => t.Name.Equals(x, StringComparison.OrdinalIgnoreCase))) keep.Write(d, t.Start, t.End - t.Start);
            byte[] kept = keep.ToArray(), tail = d[(tags.NoneAt + 8)..];
            byte[] Build(int cls, bool hide)
            {
                var m = new MemoryStream();
                m.Write(head); m.Write(kept);
                if (cls != 0) { var v = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(v, cls); m.Write(Tag("WhileActivatedAttachmentClass", "ObjectProperty", v)); }
                if (hide && slotName != null && !slotName.Equals("None", StringComparison.OrdinalIgnoreCase) && (heroSlots == null || heroSlots.Contains(slotName))) m.Write(Tag("WhileActivatedHideWeaponSlot", "NameProperty", NameValue(slotName)));
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

    /// <summary>The components at <paramref name="paths"/> with their EntityRequired / EntityExcluded taken off (--strip-entity-lists,
    /// a split test); null when one isn't found or the rebuild doesn't verify.</summary>
    public static byte[]? StripEntityLists(byte[] package, IReadOnlyList<string> paths, List<string> log)
    {
        var p = Package.FromBytes(package);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        foreach (string path in paths)
        {
            int e = Find(p, path);
            if (e < 0) { log.Add($"not found: {path}"); return null; }
            byte[] d = p.ReadExportBytes(p.Exports[e]).ToArray();
            if (TagWalker.Walk(p, d, 16) is not { } tags) { log.Add($"properties not read: {path}"); return null; }
            var m = new MemoryStream();
            m.Write(d, 0, 16);
            int dropped = 0;
            foreach (var t in tags)
                if (t.Name.Equals("EntityRequired", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("EntityExcluded", StringComparison.OrdinalIgnoreCase)) dropped++;
                else m.Write(d, t.Start, t.End - t.Start);
            m.Write(d, tags.NoneAt, d.Length - tags.NoneAt);
            byte[] nd = m.ToArray();
            rep[e] = _ => nd;
            log.Add($"{path}: {dropped} list(s) taken off ({d.Length} → {nd.Length} bytes)");
        }
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, []);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, []);
        if (problems.Count > 0) { log.Add("not verified: " + string.Join("; ", problems.Take(3))); return null; }
        return output;
    }

    /// <summary>
    /// A travel power bringing a vehicle, on a hero with costumes of their own class (Kate Bishop): the borrowed components for
    /// everyone, no entity lists, plus the stock fades of a weapon slot (PowerFxAttachmentMaterialParameter: her Sky-Cycle's
    /// bike_fadein / bike_fadeout), which find nothing to fade on the others. Evidence (trace.anims, Kurt 2026-11-03):
    /// with entity lists, neither the borrowed animation (EntityExcluded Kate) nor Kate's stock one (EntityRequired Kate)
    /// ever played, and her Sky-Cycle never faded out after release (stock Kate: the power plays the ride, and bike_fadeout
    /// takes the Sky-Cycle away; it's never hidden or destroyed). The client's UMarvelFX::powerUserIsAnyEntity fails when the
    /// component has no power user ("EntityRequired/Excluded ... may not be supported for this activation point"). The
    /// condition's own entity lists work (the Sky-Cycle only on Kate, the bike only on Hawkeye).
    /// </summary>
    static byte[] SlotFadesForAll(byte[] package, string targetDefault, string stockFile, IReadOnlyList<string> classes, List<string> log)
    {
        var p = Package.FromBytes(package);
        if (Find(p, targetDefault) < 0) return package;
        var sp = Package.Open(stockFile);
        int sd = Find(sp, targetDefault);
        var stockComps = sd < 0 ? [] : Components(sp, sd, out _, out _).Where(r => r > 0).Select(r => sp.PathOf(sp.Exports[r - 1])).ToList();
        string? ClassAt(string path) => Find(p, path) is int i && i >= 0 ? p.ClassOf(p.Exports[i]) : null;
        var fadePaths = stockComps.Where(x => ClassAt(x)?.Equals("PowerFxAttachmentMaterialParameter", StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (fadePaths.Count == 0) return package;
        // The slot's own hide at the power's end: in stock the power's skycycleattach (show the slot while active) hides it
        // when the power ends. Evidence (trace.anims, stock Kate, 2026-11-03): SetWeaponAttachVisibleOrSkip(skycycle, 1) at the
        // power's start, again 0.3 s later from the condition, and (skycycle, 0) at release, the instant the end animation
        // starts; no fade-out, no hide or destroy of the actor. That component can't come along (on Hawkeye it would show his
        // own Sky-Cycle), so a copy of it hides the slot instead, once, at the end of the loop (its slot property renamed
        // WeaponSlotToHideOnActivate, plus bike_fadeout's ActivationPoint = power_on_loop_end tag byte for byte). On a costume
        // without that vehicle it hides nothing.
        var addNames = new List<string>();
        string? hideNote = null;
        string? showPath = stockComps.FirstOrDefault(x => ClassAt(x)?.Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase) == true
            && SlotProp(p, x, "WhileActivatedShowWeaponSlot") is { } slot && fadePaths.Any(f => SlotProp(p, f, "WeaponSlot")?.Equals(slot, StringComparison.OrdinalIgnoreCase) == true));
        string? atPath = fadePaths.FirstOrDefault(f => Tags(p, f)?.Any(t => t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase)
            && TagWalker.NameAt(p, p.ReadExportBytes(p.Exports[Find(p, f)]).ToArray(), t.ValueAt).Equals("power_on_loop_end", StringComparison.OrdinalIgnoreCase)) == true);
        int hideRef = 0;
        if (showPath != null && atPath != null && CrossMove.Quiet(() => ExportCopy.Copy(p, Find(p, showPath), p, [], "borrowed_slot_hide_at_end"), out _) is { } hc)
        {
            var hp = Package.FromBytes(hc.Output);
            int he = hc.RootRef - 1;
            byte[] hd = hp.ReadExportBytes(hp.Exports[he]).ToArray();
            byte[] ad = hp.ReadExportBytes(hp.Exports[Find(hp, atPath)]).ToArray();
            var htags = TagWalker.Walk(hp, hd, 16);
            var atag = TagWalker.Walk(hp, ad, 16)?.First(t => t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase));
            var stag = htags?.FirstOrDefault(t => t.Name.Equals("WhileActivatedShowWeaponSlot", StringComparison.OrdinalIgnoreCase));
            if (htags != null && atag != null && stag != null)
            {
                int ni = Array.FindIndex(hp.Names, x => x.Equals("weaponslottohideonactivate", StringComparison.OrdinalIgnoreCase));
                if (ni < 0) { addNames.Add("weaponslottohideonactivate"); ni = hp.Names.Length; }
                byte[] slotTag = hd.AsSpan(stag.Start, stag.End - stag.Start).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(slotTag, ni);
                BinaryPrimitives.WriteInt32LittleEndian(slotTag.AsSpan(4), 0);
                byte[] nh = [.. hd.AsSpan(0, 16), .. slotTag, .. ad.AsSpan(atag.Start, atag.End - atag.Start), .. hd.AsSpan(htags.NoneAt)];
                var hrep = new Dictionary<int, Func<long, byte[]>> { [he] = _ => nh };
                byte[] ho = PackageRebuilder.Rebuild(hp, hrep, [], out var hw, addNames);
                var hprob = PackageRebuilder.Verify(hp, ho, hrep.Keys.ToList(), [], hw, addNames);
                if (hprob.Count == 0) { p = Package.FromBytes(ho); hideRef = he + 1; hideNote = $"; {SlotProp(p, showPath, "WhileActivatedShowWeaponSlot")} hidden at the power's end (borrowed_slot_hide_at_end)"; }
                else log.Add($"  {targetDefault}: the slot's hide at the end didn't verify ({string.Join("; ", hprob.Take(2))})");
                addNames.Clear();
            }
        }
        int td = Find(p, targetDefault);
        var now = Components(p, td, out var tag, out byte[] d);
        var fades = fadePaths.Select(x => Find(p, x) + 1).Where(r => r > 0 && !now.Contains(r)).ToList();
        var list = now.Concat(fades).ToList();
        if (hideRef > 0 && !list.Contains(hideRef)) list.Add(hideRef);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        // Sounds keep their entity lists: the borrowed ones not on the costumes kept stock, their own stock ones only on them.
        // Evidence (Kurt 2026-11-03): with the lists on (0.60.322 / 0.60.323) Kate heard no motorcycle; with every list taken
        // off (0.60.325 / 0.60.326) she did. The lists fail on the animation (no power user at its activation point:
        // SlotFadesForAll above), not on the sounds. A hypothesis for the sounds until heard in game.
        // Particles and the costumes' own slot shows / hides likewise (stock Kate against the borrow, compare.py 2026-11-03:
        // her Sky-Cycle showed 0.32 s late, only from the condition; her bow wasn't hidden, both hands; her exhaust
        // thrusters were gone). Their own (stock) ones only on them; the borrowed particles (Cyclops' wheel sparks) not on
        // them. A borrowed slot hide stays for everyone. If a list doesn't work on one of these, the trace's FxConsider
        // without FxActivate shows it.
        string? soundNote = null;
        if (classes.Count > 0)
        {
            static bool Is(string c, params string[] names) => names.Any(n => c.Equals(n, StringComparison.OrdinalIgnoreCase));
            string Cls(int r) => p.ClassOf(p.Exports[r - 1]);
            var stockRefs = stockComps.Select(x => Find(p, x) + 1).Where(r => r > 0).ToHashSet();
            var mine = new List<string>(); var theirs = new List<string>();
            // (the borrowed particles stay without lists: Cyclops' pivot sparks fire on a turn, where the list check has no
            // power user and fails for everyone. Evidence, trace 2026-11-03 with sharp turns: stock Cyclops fired the pivot
            // sound and 2 pivot particle systems per turn; Hawkeye with "not on Kate" lists fired the sound 3 times and the
            // particles never. Sounds' lists and Kate's own thrusters (at the power's start) worked.)
            foreach (int r in list.Where(r => r > 0 && !stockRefs.Contains(r) && Is(Cls(r), "PowerFxSound")).ToList())
                if (WithEntityLists(p, r - 1, classes, require: false, addNames) is { } cd) { rep[r - 1] = _ => cd; mine.Add(p.Exports[r - 1].ObjectName); }
            foreach (int r in stockRefs.Where(r => Is(Cls(r), "PowerFxSound", "PowerFxParticle", "PowerFxMeshAttachment")).ToList())
                if (WithEntityLists(p, r - 1, classes, require: true, addNames) is { } cd) { rep[r - 1] = _ => cd; if (!list.Contains(r)) list.Add(r); theirs.Add(p.Exports[r - 1].ObjectName); }
            if (mine.Count + theirs.Count > 0) soundNote = $"; {string.Join(", ", mine)} not on {string.Join(", ", classes)}; {string.Join(", ", theirs)} only on them";
        }
        var value = new byte[4 + 4 * list.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
        for (int k = 0; k < list.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), list[k]);
        byte[] nd = [.. d.AsSpan(0, tag.ValueAt), .. value, .. d.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        rep[td] = _ => nd;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {targetDefault}: the stock fades couldn't be kept ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  {targetDefault}: the borrowed components for every costume (no entity lists on the animation: they don't work there); the stock fades kept: {string.Join(", ", fades.Select(r => p.Exports[r - 1].ObjectName))}{hideNote}{soundNote}");
        return output;
    }

    /// <summary>For --slot-fades-test (writes nothing itself).</summary>
    public static byte[] SlotFadesForAllTest(byte[] package, string targetDefault, string stockFile, IReadOnlyList<string> classes, List<string> log) => SlotFadesForAll(package, targetDefault, stockFile, classes, log);

    /// <summary>The properties of the export at <paramref name="path"/> (a component: from 16), or null.</summary>
    static TagWalker? Tags(Package p, string path) => Find(p, path) is int i && i >= 0 ? TagWalker.Walk(p, p.ReadExportBytes(p.Exports[i]).ToArray(), 16) : null;

    /// <summary>A name property's value on the component at <paramref name="path"/>, or null.</summary>
    static string? SlotProp(Package p, string path, string prop)
    {
        int i = Find(p, path);
        if (i < 0) return null;
        byte[] d = p.ReadExportBytes(p.Exports[i]).ToArray();
        return TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals(prop, StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } t ? TagWalker.NameAt(p, d, t.ValueAt) : null;
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
    /// A borrowed projectile's model (2026-11-05, Shield Toss ← Fastball Special): an animated projectile (MarvelProjectileAnimated,
    /// a content class of MarvelProjectile with nothing of its own but defaults) flies a skeletal mesh component named by its
    /// default's Mesh, which its ProjectileFxAnimation plays (Wolverine's wolverine_bullet_bullet). The target's projectile is the
    /// same native class, so its default gets Mesh = the borrowed copy of that component (borrowed_&lt;name&gt;). A donor
    /// without one changes nothing.
    /// </summary>
    static byte[] ProjectileMesh(byte[] package, string targetDefault, string donorFile, string donorDefault, List<string> log)
    {
        var sp = Package.Open(donorFile);
        int si = Find(sp, donorDefault);
        if (si < 0) return package;
        byte[] sd = sp.ReadExportBytes(sp.Exports[si]).ToArray();
        if (TagWalker.Walk(sp, sd, 4) is not { } st) return package;
        var sm = st.FirstOrDefault(t => t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ObjectProperty", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
        if (sm == null) return package;
        int r0 = BitConverter.ToInt32(sd, sm.ValueAt);
        if (r0 <= 0) return package;
        string leaf = sp.Exports[r0 - 1].ObjectName;
        var p = Package.FromBytes(package);
        int ti = Find(p, targetDefault);
        int ci = Find(p, targetDefault + ".borrowed_" + leaf);
        if (ti < 0) return package;
        if (ci < 0) { log.Add($"  {targetDefault}: the other projectile's model ({leaf}) wasn't copied, so it won't fly"); return package; }
        byte[] d = p.ReadExportBytes(p.Exports[ti]).ToArray();
        if (TagWalker.Walk(p, d, 4) is not { } tt) { log.Add($"  {targetDefault}: its settings don't read, so the other projectile's model won't fly"); return package; }
        var addNames = new List<string>();
        foreach (string n in new[] { "Mesh", "ObjectProperty" })
            if (!p.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))) addNames.Add(n);
        var w = new TagWriter(p, addNames);
        var nd = new List<byte>(d.AsSpan(0, 4).ToArray());
        foreach (var t in tt) if (!t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase)) nd.AddRange(d.AsSpan(t.Start, t.End - t.Start).ToArray());
        nd.AddRange(w.Tag("Mesh", "ObjectProperty", null, BitConverter.GetBytes(ci + 1)));
        nd.AddRange(d.AsSpan(tt.NoneAt));
        byte[] data = [.. nd];
        var rep = new Dictionary<int, Func<long, byte[]>> { [ti] = _ => data };
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {targetDefault}: the other projectile's model couldn't be set ({string.Join("; ", problems.Take(2))})"); return package; }
        log.Add($"  {targetDefault}: flies the other projectile's model (Mesh → borrowed_{leaf})");
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

    /// <summary>
    /// A borrowed vehicle shown by the target's travel condition, as the game's own Fantasticar is (Thing's travel condition
    /// carries the vehicle class and spawns it, WhileActivatedAttachmentClass, and plays animations its AnimSet has, with
    /// ConditionAnimIncludeAttachments): the vehicle class the power spawns is copied into <paramref name="condition"/> with
    /// a spawn component of its own (a copy of the Fantasticar's car_attach; not on <paramref name="ownClass"/>), the
    /// condition's animations become the borrowed ones (<paramref name="slots"/>, by start / loop / end) and the power's
    /// spawn goes. Evidence (Kurt, 2026-11-03, Hawkeye): with the Kate lists off, neither Cyclops' bike nor the Sky-Cycle
    /// showed from the power; the Sky-Cycle showed once the condition's list was off. Null when there's nothing to move.
    /// </summary>
    /// <summary>
    /// <paramref name="pkg"/> with the sequence named <paramref name="from"/> in the AnimSets of the attachment class default
    /// <paramref name="classDefault"/> copied as <paramref name="to"/> (same keys) and added to its set; unchanged when there's none.
    /// </summary>
    /// <summary>In each AnimSet copied for this hero (its name ending <paramref name="suffix"/>), every sequence's Metadata list
    /// without its AnimMetadata_SkelControlKeyFrame entries (the property left out when nothing else is in it).</summary>
    static byte[] DropSkelControlMetadata(byte[] package, string suffix, List<string> log)
    {
        var pkg = Package.FromBytes(package);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var touched = new List<string>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var set = pkg.Exports[i];
            if (!pkg.ClassOf(set).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) || !set.ObjectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            byte[] sd = pkg.ReadExportBytes(set).ToArray();
            if (TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase)) is not { } st) continue;
            for (int q = 0, c = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt)); q < c; q++)
            {
                int seq = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt + 4 + 4 * q));
                if (seq <= 0) continue;
                byte[] qd = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                var tags = TagWalker.Walk(pkg, qd, 4);
                if (tags?.FirstOrDefault(t => t.Name.Equals("Metadata", StringComparison.OrdinalIgnoreCase)) is not { } mt) continue;
                int n = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(mt.ValueAt));
                var keep = new List<int>();
                for (int k = 0; k < n; k++)
                {
                    int r = BinaryPrimitives.ReadInt32LittleEndian(qd.AsSpan(mt.ValueAt + 4 + 4 * k));
                    string cls = r > 0 ? pkg.ClassOf(pkg.Exports[r - 1]) : r < 0 ? pkg.Imports[-r - 1].ClassName : "";
                    if (!cls.Equals("AnimMetadata_SkelControlKeyFrame", StringComparison.OrdinalIgnoreCase)) keep.Add(r);
                }
                if (keep.Count == n) continue;
                byte[] nq;
                if (keep.Count == 0) nq = [.. qd.AsSpan(0, mt.Start), .. qd.AsSpan(mt.End)];
                else
                {
                    var v = new byte[4 + 4 * keep.Count];
                    BinaryPrimitives.WriteInt32LittleEndian(v, keep.Count);
                    for (int k = 0; k < keep.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + 4 * k), keep[k]);
                    nq = [.. qd.AsSpan(0, mt.ValueAt), .. v, .. qd.AsSpan(mt.ValueAt + mt.Size)];
                    BinaryPrimitives.WriteInt32LittleEndian(nq.AsSpan(mt.Start + 16), v.Length);
                }
                rep[seq - 1] = _ => nq;
                touched.Add(TagWalker.Walk(pkg, qd, 4)?.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } sn ? TagWalker.NameAt(pkg, qd, sn.ValueAt) : pkg.Exports[seq - 1].ObjectName);
            }
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(pkg, rep, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("leaving out the donor's skeletal-control metadata: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {string.Join(", ", touched.Distinct())}: the donor's skeletal-control switches left out (this hero's own controls of those names lean his mount)");
        return output;
    }

    /// <summary>In each AnimSet copied for this hero (its name ending <paramref name="suffix"/>), TrackBoneNames entries for
    /// g_mount* bones <paramref name="donorBones"/> lacks set to None (the track then drives no bone).</summary>
    static byte[] DropForeignMountTracks(byte[] package, string suffix, IReadOnlySet<string> donorBones, List<string> log)
    {
        var pkg = Package.FromBytes(package);
        int none = Array.FindIndex(pkg.Names, x => x.Equals("None", StringComparison.OrdinalIgnoreCase));
        if (none < 0) return package;
        var rep = new Dictionary<int, Func<long, byte[]>>();
        for (int i = 0; i < pkg.Exports.Length; i++)
        {
            var e = pkg.Exports[i];
            if (!pkg.ClassOf(e).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) || !e.ObjectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            byte[] b = pkg.ReadExportBytes(e).ToArray();
            if (TagWalker.Walk(pkg, b, 4)?.FirstOrDefault(t => t.Name.Equals("TrackBoneNames", StringComparison.OrdinalIgnoreCase)) is not { } tb) continue;
            var dropped = new List<string>();
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(tb.ValueAt)); k < n; k++)
            {
                string bone = TagWalker.NameAt(pkg, b, tb.ValueAt + 4 + 8 * k);
                if (!bone.StartsWith("g_mount", StringComparison.OrdinalIgnoreCase) || donorBones.Contains(bone)) continue;
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(tb.ValueAt + 4 + 8 * k), none);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(tb.ValueAt + 8 + 8 * k), 0);
                dropped.Add(bone);
            }
            if (dropped.Count == 0) continue;
            byte[] nb = b;
            rep[i] = _ => nb;
            log.Add($"  {e.ObjectName}: {string.Join(", ", dropped)} track(s) left out (another skeleton's; the donor's own model has no such bone)");
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(pkg, rep, [], out var written);
        var problems = PackageRebuilder.Verify(pkg, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("leaving out another skeleton's mount tracks: " + string.Join("; ", problems.Take(3)));
        return output;
    }

    /// <summary>
    /// A prop that follows its wearer's animation (bAttachUseParentAnim: Hawkeye's bow, its own g_bow / g_bowstring bones
    /// driven by name from his skeleton) on a hero without those bones: it got no pose and didn't show (Kurt, 2026-11-04,
    /// Thor with Hawkeye's Nerve Gas Arrow). It's held on the hand its first such bone hangs from instead (g_bow under
    /// g_l_palm): bAttachUseParentAnim off, AttachmentBones that hand, and the bake that moves its model into that hand's space
    /// (the hand's rest model-to-bone matrix). Null when it isn't such a prop or the hero has its bones.
    /// </summary>
    static (Package Pkg, System.Numerics.Matrix4x4 Bake)? ParentAnimToHand(Package dst, string classDefault, IReadOnlySet<string> targetBones, List<string> log)
    {
        int d = Find(dst, classDefault);
        if (d < 0) return null;
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var tags = TagWalker.Walk(dst, b, 4);
        var pa = tags?.FirstOrDefault(t => t.Name.Equals("bAttachUseParentAnim", StringComparison.OrdinalIgnoreCase));
        var mm = tags?.FirstOrDefault(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase));
        if (tags == null || pa == null || b[pa.ValueAt] == 0 || mm == null || BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(mm.ValueAt)) < 1) return null;
        int mesh = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(mm.ValueAt + 4));
        if (mesh <= 0) return null;
        var ap = AnimExportCli.Packages.Package.Read(dst.RawFile);
        var sm = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, mesh - 1);
        var bones = sm?.Bones;
        if (sm == null || bones == null) return null;
        // the bones its own vertices hang on (the bow's g_bow / g_bowstring ...; its skeleton is all of Hawkeye's, and the first
        // bone Thor lacks in skeleton order was a sash bone under the pelvis: 0.60.363 hung the bow on Thor's pelvis)
        var skinned = new HashSet<int>();
        foreach (var inf in sm.HighestDetail?.Influences ?? [])
            for (int k = 0; k < inf.Bones.Count; k++) if (inf.Weights[k] > 0) skinned.Add(inf.Bones[k]);
        int first = -1;
        foreach (int i in skinned.Order())
            if (i > 0 && i < bones.Count && !targetBones.Contains(bones[i].Name)) { first = i; break; }
        if (first < 0) return null;
        // up from it to the nearest bone the hero has (g_bow → g_l_palm)
        int handIx = bones[first].ParentIndex;
        while (handIx > 0 && !targetBones.Contains(bones[handIx].Name)) handIx = bones[handIx].ParentIndex;
        if (handIx < 0) return null;
        string hand = bones[handIx].Name;
        var bake = AnimExportCli.Fbx.SkeletonPose.Rest(bones).ModelToBone[handIx];
        // the default: bAttachUseParentAnim out, AttachmentBones [hand] in (or set)
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var ms = new MemoryStream();
        ms.Write(b, 0, 4);
        var ab = tags.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase));
        foreach (var t in tags)
        {
            if (t == pa || t == ab) continue;
            ms.Write(b, t.Start, t.End - t.Start);
        }
        var v = new byte[4 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(v, 1);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4), NameIdx(hand));
        var tag = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx("AttachmentBones"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), v.Length);
        ms.Write(tag); ms.Write(v);
        ms.Write(b, tags.NoneAt, b.Length - tags.NoneAt);
        byte[] nb = ms.ToArray();
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => nb };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {dst.Exports[d].ObjectName}: couldn't be put on a hand ({string.Join("; ", problems.Take(2))})"); return null; }
        log.Add($"  {dst.Exports[d].ObjectName}: follows its wearer's animation by bones this hero lacks ({bones[first].Name}): held on {hand} instead");
        return (Package.FromBytes(output), bake);
    }

    /// <summary>Whether an attachment class's default names a model (ModelMesh, or a mesh component).</summary>
    static bool HasModel(string file, int classExport)
    {
        try
        {
            var p = Package.Open(file);
            int d = Find(p, "marvelgamecontent.default__" + p.Exports[classExport].ObjectName);
            if (d < 0) return true;
            byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
            return TagWalker.Walk(p, b, 4)?.Any(t => t.Name.Equals("ModelMesh", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("Mesh", StringComparison.OrdinalIgnoreCase)) ?? true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return true; }
    }

    /// <summary>A class default's AttachmentBones names.</summary>
    static List<string> AttachmentBoneNames(Package p, string classDefault)
    {
        var list = new List<string>();
        int d = Find(p, classDefault);
        if (d < 0) return list;
        byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
        if (TagWalker.Walk(p, b, 4)?.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase)) is { } ab)
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ab.ValueAt)); k < n; k++) list.Add(TagWalker.NameAt(p, b, ab.ValueAt + 4 + 8 * k));
        return list;
    }

    /// <summary>
    /// The hand (palm, else wrist) the donor's animations hold <paramref name="bone"/> still against, and the bone's place in
    /// that hand's space (row vectors: point in the bone's space × Rel = point in the hand's), from the frames of every donor
    /// animation that moves the bone; the hand it stays steadiest on (spread under 3 units). Null when none holds it.
    /// </summary>
    internal static (string Hand, System.Numerics.Matrix4x4 Rel)? HeldOnHand(string donorHeroFile, string bone, string? cooked, IReadOnlySet<string> targetBones, List<string> log, IReadOnlyCollection<string>? anims = null)
    {
        var mm = MountMotion(donorHeroFile, bone, cooked, []);
        if (mm == null || mm.Anims.Count == 0) return null;
        // the borrowed power's own animations (Punisher moves g_gun_body in 14, each weapon held its own way)
        var use = mm.Anims.Where(a => anims == null || anims.Count == 0 || anims.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).Select(a => a.Value).ToList();
        if (use.Count == 0) { log.Add($"  {bone}: the borrowed animations don't move it: left at root"); return null; }
        (string Hand, System.Numerics.Matrix4x4 Rel, float Spread)? best = null;
        foreach (string hand in new[] { "g_r_palm", "g_l_palm", "g_r_wrist", "g_l_wrist" })
        {
            int hi = mm.Animator.BoneIndex(hand);
            if (hi < 0 || !targetBones.Contains(hand)) continue;
            var rels = new List<System.Numerics.Matrix4x4>();
            foreach (var (a, frames, _) in use)
                for (float f = 0; f <= frames; f += Math.Max(1, frames / 12))
                {
                    mm.Animator.Pose(a, f);
                    if (System.Numerics.Matrix4x4.Invert(mm.Animator.BoneMatrix(hi), out var inv)) rels.Add(mm.Animator.BoneMatrix(mm.BoneIndex) * inv);
                }
            if (rels.Count == 0) continue;
            var mean = rels.Aggregate(System.Numerics.Vector3.Zero, (acc, r) => acc + r.Translation) / rels.Count;
            float spread = rels.Average(r => System.Numerics.Vector3.Distance(r.Translation, mean));
            var pick = rels.OrderBy(r => System.Numerics.Vector3.Distance(r.Translation, mean)).First();
            if (best == null || spread < best.Value.Spread) best = (hand, pick, spread);
        }
        if (Environment.GetEnvironmentVariable("MHO_HOLD_DEBUG") == "1") Console.WriteLine($"hold {bone}: best {best?.Hand} spread {best?.Spread}");
        if (best is not { } b || b.Spread > 3) { if (best != null) log.Add($"  {bone}: no hand holds it still (steadiest {best.Value.Hand}, {best.Value.Spread:0.#} units): left at root"); return null; }
        log.Add($"  {bone}: held in {b.Hand} in the donor's animations ({use.Count} of them, spread {b.Spread:0.##}): the weapon goes on that hand, {b.Rel.Translation.Length():0.#} from it");
        return (b.Hand, b.Rel);
    }

    /// <summary>An attachment class default's AnimSet refs: its own AnimationSet list (the Sky-Cycle), else its mesh
    /// component's AnimSets (Kitty Pryde's Lockheed: default__…_lockheed.initialskeletalmesh).</summary>
    static List<int> AttachmentSets(Package pkg, string classDefault)
    {
        var sets = new List<int>();
        void From(string path, string prop, int at)
        {
            int e = Find(pkg, path);
            if (e < 0) return;
            byte[] b = pkg.ReadExportBytes(pkg.Exports[e]).ToArray();
            var t = (TagWalker.Walk(pkg, b, at) ?? (at == 16 ? TagWalker.Walk(pkg, b, 4) : null))?.FirstOrDefault(x => x.Name.Equals(prop, StringComparison.OrdinalIgnoreCase));
            if (t == null) return;
            for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt)); k < n; k++)
                if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(t.ValueAt + 4 + 4 * k)) is var r && r > 0 && !sets.Contains(r)) sets.Add(r);
        }
        From(classDefault, "AnimationSet", 4);
        if (sets.Count == 0) From(classDefault + ".initialskeletalmesh", "AnimSets", 16);
        return sets;
    }

    static Package VehicleSequenceCopy(Package pkg, string classDefault, string from, string to, List<string> log)
    {
        foreach (int set in AttachmentSets(pkg, classDefault))
        {
            byte[] sd = pkg.ReadExportBytes(pkg.Exports[set - 1]).ToArray();
            var st = TagWalker.Walk(pkg, sd, 4)?.FirstOrDefault(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase));
            if (st == null) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt));
            for (int q = 0; q < count; q++)
            {
                int seq = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt + 4 + 4 * q));
                if (seq <= 0) continue;
                byte[] qd = pkg.ReadExportBytes(pkg.Exports[seq - 1]).ToArray();
                var nt = TagWalker.Walk(pkg, qd, 4)?.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8);
                if (nt == null || !TagWalker.NameAt(pkg, qd, nt.ValueAt).Equals(from, StringComparison.OrdinalIgnoreCase)) continue;
                string setPath = pkg.PathOf(pkg.Exports[set - 1]);
                // (a name of its own per copy: one sequence can be copied under two names, Kate's Sky-Cycle start as both the
                // condition's and the power's start)
                string copyName = $"{pkg.Exports[seq - 1].ObjectName}_{to}";
                for (int u = 2; pkg.Exports.Any(x => x.ObjectName.Equals(copyName, StringComparison.OrdinalIgnoreCase)); u++) copyName = $"{pkg.Exports[seq - 1].ObjectName}_{to}_{u}";
                var cc = CrossMove.Quiet(() => ExportCopy.Copy(pkg, seq - 1, pkg, [], copyName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [setPath] = setPath }), out string said);
                if (cc == null) { log.Add($"  the vehicle's {from} couldn't be copied as {to}: it stays still in the condition's start"); return pkg; }
                var np = Package.FromBytes(cc.Output);
                int ns = cc.RootRef - 1, si = Find(np, setPath);
                var addNames = new List<string>();
                int toIdx = Array.FindIndex(np.Names, x => x.Equals(to, StringComparison.OrdinalIgnoreCase));
                if (toIdx < 0) { addNames.Add(to); toIdx = np.Names.Length; }
                byte[] nq = np.ReadExportBytes(np.Exports[ns]).ToArray();
                var nnt = TagWalker.Walk(np, nq, 4)!.First(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8);
                BinaryPrimitives.WriteInt32LittleEndian(nq.AsSpan(nnt.ValueAt), toIdx);
                BinaryPrimitives.WriteInt32LittleEndian(nq.AsSpan(nnt.ValueAt + 4), 0);
                byte[] nsd = np.ReadExportBytes(np.Exports[si]).ToArray();
                var nst = TagWalker.Walk(np, nsd, 4)!.First(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase));
                int nc = BinaryPrimitives.ReadInt32LittleEndian(nsd.AsSpan(nst.ValueAt));
                var v = new byte[4 + 4 * (nc + 1)];
                BinaryPrimitives.WriteInt32LittleEndian(v, nc + 1);
                nsd.AsSpan(nst.ValueAt + 4, 4 * nc).CopyTo(v.AsSpan(4));
                BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + 4 * nc), ns + 1);
                byte[] nsd2 = [.. nsd.AsSpan(0, nst.ValueAt), .. v, .. nsd.AsSpan(nst.ValueAt + nst.Size)];
                BinaryPrimitives.WriteInt32LittleEndian(nsd2.AsSpan(nst.Start + 16), v.Length);
                var rep = new Dictionary<int, Func<long, byte[]>> { [ns] = _ => nq, [si] = _ => nsd2 };
                byte[] o = PackageRebuilder.Rebuild(np, rep, [], out var w, addNames);
                var prob = PackageRebuilder.Verify(np, o, rep.Keys.ToList(), [], w, addNames);
                if (prob.Count > 0) { log.Add($"  the vehicle's {to} didn't verify ({string.Join("; ", prob.Take(2))}): it stays still in the condition's start"); return pkg; }
                log.Add($"  {setPath}: {from} also as {to} (the condition's start)");
                return Package.FromBytes(o);
            }
        }
        return pkg;
    }

    /// <summary>
    /// <paramref name="bytes"/> with the borrowed vehicle's class (and its default) and the content group of its AnimSets
    /// renamed <c>&lt;name&gt;_&lt;tag&gt;</c> (only names added: CostumeMove.RenameAndSplit, as Make Unique). A class and its
    /// animations are found by path, and the package loaded first wins. Evidence (trace.anims in the game, Kurt 2026-11-03):
    /// with Cyclops ridden first, Hawkeye's bike was Cyclops' stock class (mesh wolverine_bike, not the borrow's
    /// wolverine_bike_x104) and its stock AnimSet, without the condition's start, so the bike had no sequence and sat in
    /// its rest pose. Unchanged when there's nothing to rename or it's renamed already.
    /// </summary>
    static byte[] UniqueVehicle(byte[] bytes, IReadOnlySet<string> vehicleClasses, string tag, string file, List<string> log, string what = "vehicle")
    {
        var pkg = Package.FromBytes(bytes);
        var renames = new Dictionary<int, string>();
        var splits = new List<CostumeMove.Split>();
        int Top(int i) { for (int g = 0; g < 64 && pkg.Exports[i].OuterIndex > 0; g++) i = pkg.Exports[i].OuterIndex - 1; return i; }
        // A group holding a streamed texture keeps its path (the .tfc manifest finds the texture by it): it's split as a costume
        // move splits one, its other objects (the AnimSets) under the new name (2026-11-04: Hawkeye's Sky-Cycle on Cyclops,
        // group skycycle with 3 streamed textures, was refused).
        var streamed = new HashSet<int>();
        for (int i = 0; i < pkg.Exports.Length; i++)
            if (pkg.ClassOf(pkg.Exports[i]).Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                try { if (TextureInfo.Read(pkg, pkg.Exports[i]).Mips.Any(m => m.InSeparateFile)) streamed.Add(i); }
                catch (Exception ex) when (ex is PackageFormatException or InvalidDataException or ArgumentOutOfRangeException) { }
        var holds = new HashSet<int>();
        foreach (int t in streamed) for (int up = pkg.Exports[t].OuterIndex, g = 0; up > 0 && g < 64; up = pkg.Exports[up - 1].OuterIndex, g++) holds.Add(up - 1);
        void SplitGroup(int g, string newName, int parent)
        {
            var kids = Enumerable.Range(0, pkg.Exports.Length).Where(j => pkg.Exports[j].OuterIndex == g + 1).ToList();
            int me = splits.Count;
            splits.Add(new CostumeMove.Split(g, pkg.Exports[g].ObjectName, newName, kids.Where(j => !streamed.Contains(j) && !holds.Contains(j)).ToList(), kids.Count(streamed.Contains)) { Parent = parent });
            foreach (int sub in kids.Where(holds.Contains)) SplitGroup(sub, pkg.Exports[sub].ObjectName, me);
        }
        foreach (string cls in vehicleClasses)
        {
            int c = Array.FindIndex(pkg.Exports, x => x.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("Class", StringComparison.OrdinalIgnoreCase));
            int d = Array.FindIndex(pkg.Exports, x => x.ObjectName.Equals("default__" + cls, StringComparison.OrdinalIgnoreCase));
            if (c < 0 || d < 0) continue;
            renames[c] = $"{cls}_{tag}";
            renames[d] = $"default__{cls}_{tag}";
            byte[] dd = pkg.ReadExportBytes(pkg.Exports[d]).ToArray();
            if (TagWalker.Walk(pkg, dd, 4)?.FirstOrDefault(t => t.Name.Equals("AnimationSet", StringComparison.OrdinalIgnoreCase)) is { } at)
                for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(dd.AsSpan(at.ValueAt)); k < n; k++)
                {
                    int set = BinaryPrimitives.ReadInt32LittleEndian(dd.AsSpan(at.ValueAt + 4 + 4 * k));
                    if (set <= 0) continue;
                    int g = Top(set - 1);
                    if (g != set - 1 && pkg.ClassOf(pkg.Exports[g]).Equals("Package", StringComparison.OrdinalIgnoreCase) && !pkg.Exports[g].ObjectName.EndsWith("_" + tag, StringComparison.OrdinalIgnoreCase)
                        && !renames.ContainsKey(g) && !splits.Any(s => s.Group == g)
                        && !pkg.Exports.Any(x => x.OuterIndex == 0 && x.ObjectName.Equals($"{pkg.Exports[g].ObjectName}_{tag}", StringComparison.OrdinalIgnoreCase)))   // (split already)
                    {
                        if (holds.Contains(g)) SplitGroup(g, $"{pkg.Exports[g].ObjectName}_{tag}", -1);
                        else renames[g] = $"{pkg.Exports[g].ObjectName}_{tag}";
                    }
                }
        }
        if (renames.Count == 0 && splits.Count == 0) return bytes;
        var problems = new List<string>();
        byte[] o = CostumeMove.RenameAndSplit(pkg, renames, splits, problems);
        if (problems.Count > 0) throw new InvalidDataException($"{file}: the borrowed {what}'s own paths: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {file}: the borrowed {what} under its own paths ({string.Join(", ", renames.Select(r => $"{pkg.Exports[r.Key].ObjectName} → {r.Value}").Concat(splits.Where(s => s.Parent < 0).Select(s => $"{s.GroupName} → {s.NewName} (its streamed textures keep their path)")))}), so a stock copy loaded first can't stand in for it");
        return o;
    }

    /// <summary>
    /// The donor's ride conditions: the conditions its power sets off whose class default shows a vehicle
    /// (ConditionFxMeshAttachment): (stock package, class default path). Cyclops: MarvelConditionEffect_CyclopsRideBike.
    /// </summary>
    static List<(string File, string Default)> DonorRideConditions(Fx.GameData db, Fx.FxGame game, string donorPower)
    {
        var list = new List<(string, string)>();
        foreach (var a in Fx.PowerClosure.Of(db, donorPower).Where(a => a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase)))
        {
            string def = $"marvelgamecontent.default__{a.Class}".ToLowerInvariant();
            if (game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), def) >= 0) is not string file) continue;
            var p = Package.Open(file);
            // (a condition without components shows no vehicle: Green Goblin's flight condition only plays custom animations)
            List<int> comps;
            try { comps = Components(p, Find(p, def), out _, out _); }
            catch (InvalidDataException) { continue; }
            if (comps.Any(r => r > 0 && p.ClassOf(p.Exports[r - 1]).Equals("ConditionFxMeshAttachment", StringComparison.OrdinalIgnoreCase)))
                list.Add((file, def));
        }
        return list;
    }

    /// <summary>
    /// The donor ride condition's other effects into the travel condition (everything but its vehicle attachment, which
    /// ConditionVehicle puts in): with their templates, not on the costumes kept stock. Evidence (trace, 2026-11-03): stock
    /// Cyclops' ride condition starts vfx_motorcycle_trail (x2) and vfx_wolverine_bike_exhaust when he gets on, and fades
    /// the bike in (bikefadein); Hawkeye with the borrowed bike had none of them (the borrow copied only the power's).
    /// Unchanged when there's nothing to add.
    /// </summary>
    static byte[] ConditionExtras(byte[] condition, string donorFile, string donorDefault, IReadOnlyList<string> ownClass, string cooked, List<string> log)
    {
        var dst = Package.FromBytes(condition);
        int cd = Array.FindIndex(dst.Exports, x => x.ObjectName.StartsWith("default__marvelconditioneffect", StringComparison.OrdinalIgnoreCase) && dst.PathOf(x).Count(ch => ch == '.') == 1);
        var src = Package.Open(donorFile);
        int sd = Find(src, donorDefault);
        if (cd < 0 || sd < 0) return condition;
        string condDefault = dst.PathOf(dst.Exports[cd]);
        var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [donorDefault] = condDefault };
        var added = new List<int>();
        var names = new List<string>();
        var un = UnloadedFor(cooked, condDefault, donorDefault, donorFile);
        if (un != null && un.Packages.Count > 0) log.Add($"  the game doesn't load {string.Join(", ", un.Packages.Select(x => x.Name))} with this condition (the donor's needs them): its parts are made to stand without them");
        foreach (int r in Components(src, sd, out _, out _))
        {
            if (r <= 0) continue;
            var e = src.Exports[r - 1];
            string cls = src.ClassOf(e);
            if (cls.Equals("ConditionFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;   // (the vehicle: ConditionVehicle's)
            string name = "borrowed_" + e.ObjectName;
            if (Find(dst, condDefault + "." + name) >= 0) continue;
            // (0.60.431) the game's package lists known: as for powers (Graft): a part whose class only a package this
            // condition doesn't load defines is left out; templates in such packages are merged in level by level
            if (un != null)
            {
                if (un.ClassHome(cls) is string home)
                { log.Add($"  {e.ObjectName} ({cls}): left out (its class is defined in {home}, which the game doesn't load with this condition)"); continue; }
                var ucopy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out _);
                if (ucopy == null) { log.Add($"  {e.ObjectName} ({cls}): couldn't be copied, left out"); continue; }
                dst = Package.FromBytes(ucopy.Output);
                dst = Flatten(dst, ucopy.RootRef - 1, condDefault, e.ObjectName, un, log);
                added.Add(ucopy.RootRef);
                names.Add(e.ObjectName);
                continue;
            }
            // its settings from a template in the shared ride condition's package, which isn't loaded for this hero (as Graft)
            // (0.60.429: under the condition's own default with its own name, then merged into the copy, as for powers:
            // never under its own path, which brings the shared class along)
            string? condMerge = null;
            if (Template(src, r - 1) is string tpl && Find(dst, tpl) < 0 && TemplatePackage(cooked, tpl) is string tf)
            {
                var tp = Package.Open(tf);
                int ti = Find(tp, tpl);
                string cLeaf = tpl[(tpl.LastIndexOf('.') + 1)..], cOuter = tpl[..tpl.LastIndexOf('.')], cCopy = condDefault + ".borrowedtemplate_" + cLeaf;
                if (Find(dst, cCopy) < 0 && ti >= 0 && CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, [], "borrowedtemplate_" + cLeaf,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [cOuter] = condDefault }), out _) is { } tc)
                    dst = Package.FromBytes(tc.Output);
                if (Find(dst, cCopy) >= 0) { replace[tpl] = cCopy; condMerge = cCopy; }
                else
                {
                    // the template can't come along under its own path (its class default, marvelconditioneffect_shared_
                    // ridemotorcycle, has compiled code): a component with no settings of its own (Cyclops' bikefadein,
                    // wheeltrailsvfx) is the template itself, so the template is copied under the component's name instead
                    byte[] own = src.ReadExportBytes(e).ToArray();
                    bool empty = TagWalker.Walk(src, own, 16) is { Count: 0 };
                    string tplOuter = tpl[..tpl.LastIndexOf('.')];
                    var flat = ti >= 0 && empty
                        ? CrossMove.Quiet(() => ExportCopy.Copy(tp, ti, dst, [], name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplOuter] = condDefault }), out _)
                        : null;
                    if (flat == null) { log.Add($"  {e.ObjectName}: its template {tpl} couldn't be copied, left out"); continue; }
                    dst = Package.FromBytes(flat.Output);
                    added.Add(flat.RootRef);
                    names.Add(e.ObjectName + " (its template's settings)");
                    continue;
                }
            }
            if (Template(src, r - 1) is string tpl2 && Find(dst, tpl2) >= 0) replace[tpl2] = tpl2;
            var copy = CrossMove.Quiet(() => ExportCopy.Copy(src, r - 1, dst, [], name, replace), out string said);
            if (copy == null) { log.Add($"  {e.ObjectName} ({cls}): couldn't be copied, left out"); continue; }
            dst = Package.FromBytes(copy.Output);
            if (condMerge != null && Find(dst, condMerge) is int cmi && cmi >= 0) dst = MergeTemplate(dst, copy.RootRef - 1, cmi, e.ObjectName, log);
            added.Add(copy.RootRef);
            names.Add(e.ObjectName);
        }
        if (added.Count == 0) return condition;
        cd = Find(dst, condDefault);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        if (ownClass.Count > 0)
            foreach (int r in added)
                if (WithEntityLists(dst, r - 1, ownClass, require: false, addNames) is { } ld) rep[r - 1] = _ => ld;
        var list = Components(dst, cd, out var tag, out byte[] dd);
        list.AddRange(added);
        var value = new byte[4 + 4 * list.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
        for (int k = 0; k < list.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * k), list[k]);
        byte[] nd = [.. dd.AsSpan(0, tag.ValueAt), .. value, .. dd.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        rep[cd] = _ => nd;
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the condition's borrowed effects didn't verify: " + string.Join("; ", problems.Take(2)));
        log.Add($"  {condDefault}: the donor ride's effects too ({string.Join(", ", names)}){(ownClass.Count > 0 ? $", not on {string.Join(", ", ownClass)}" : "")}");
        return output;
    }

    /// <summary>
    /// Borrowed pivot dust (confirmed in game 2026-11-03, 0.60.333: Hawkeye on Cyclops's bike gets the turn dust). These
    /// effects pick their particle by the ground material under them (GroundMaterialOverrideList); for Hawkeye the lookup
    /// gave vfx_null (ride21 telemetry: Cyclops agmdirt, Hawkeye vfx_null at the same turns). Editing the template copy
    /// alone did nothing: the power's own borrowed_* components inherit from marvelgamecontent.default__powershared_ridebike,
    /// a path the stock bike packages share (first loaded wins). So the template copies and the borrowed components both
    /// name the dirt particle directly, the borrowed ones with an empty list of their own. Always dirt, whatever the
    /// ground. Unchanged when there's nothing to change.
    /// </summary>
    static byte[] PivotFixedParticles(byte[] power, List<string> log)
    {
        var p = Package.FromBytes(power);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var done = new List<string>();
        var fixedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < p.Exports.Length; i++)
        {
            if (!p.ClassOf(p.Exports[i]).Equals("PowerFxParticle", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(p.Exports[i]).ToArray();
            var tags = TagWalker.Walk(p, d, 16);
            var gl = tags?.FirstOrDefault(t => t.Name.Equals("GroundMaterialOverrideList", StringComparison.OrdinalIgnoreCase));
            var pt = tags?.FirstOrDefault(t => t.Name.Equals("ParticleSystemTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4);
            if (tags == null || gl == null || pt == null) continue;
            // the list's particle systems (export refs inside it), the dirt one preferred
            int dirt = 0, any = 0;
            for (int at = gl.ValueAt; at + 4 <= gl.ValueAt + gl.Size; at++)
            {
                int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
                if (v <= 0 || v > p.Exports.Length || !p.ClassOf(p.Exports[v - 1]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) continue;
                any = any == 0 ? v : any;
                if (p.Exports[v - 1].ObjectName.EndsWith("agmdirt", StringComparison.OrdinalIgnoreCase)) { dirt = v; break; }
            }
            int use = dirt != 0 ? dirt : any;
            if (use == 0) continue;
            var m = new MemoryStream();
            m.Write(d, 0, 16);
            foreach (var t in tags)
            {
                if (t == gl) continue;
                byte[] tb = d.AsSpan(t.Start, t.End - t.Start).ToArray();
                if (t == pt) BinaryPrimitives.WriteInt32LittleEndian(tb.AsSpan(t.ValueAt - t.Start), use);
                m.Write(tb);
            }
            m.Write(d, tags.NoneAt, d.Length - tags.NoneAt);
            byte[] nd = m.ToArray();
            rep[i] = _ => nd;
            done.Add($"{p.Exports[i].ObjectName} → {p.Exports[use - 1].ObjectName}");
            fixedBy[p.Exports[i].ObjectName] = use;
        }
        // 0.60.333 (ride21): the game never used the edited copies. The power's own components (borrowed_pivotwheelvfx …)
        // inherit from marvelgamecontent.default__powershared_ridebike's, a path the stock bike packages have too (the
        // first one loaded wins), and that stock copy's ground lookup gave vfx_null for Hawkeye while Cyclops got dirt.
        // So the borrowed components carry the particle and an empty list themselves: whichever copy loads, theirs win.
        int Name(string n) => Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        int nPst = Name("ParticleSystemTemplate"), nObj = Name("ObjectProperty"), nGl = Name("GroundMaterialOverrideList"), nArr = Name("ArrayProperty");
        if (fixedBy.Count > 0 && nPst >= 0 && nObj >= 0 && nGl >= 0 && nArr >= 0)
            for (int i = 0; i < p.Exports.Length; i++)
            {
                string on = p.Exports[i].ObjectName;
                if (!on.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase) || !fixedBy.TryGetValue(on["borrowed_".Length..], out int use)
                    || !p.ClassOf(p.Exports[i]).Equals("PowerFxParticle", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] d = p.ReadExportBytes(p.Exports[i]).ToArray();
                var tags = TagWalker.Walk(p, d, 16);
                if (tags == null) continue;
                byte[] Tag(int name, int type, byte[] value)
                {
                    var t = new byte[24 + value.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(t, name);
                    BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(8), type);
                    BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(16), value.Length);
                    value.CopyTo(t, 24);
                    return t;
                }
                var refv = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(refv, use);
                var m = new MemoryStream();
                m.Write(d, 0, 16);
                foreach (var t in tags)
                {
                    if (t.Name.Equals("ParticleSystemTemplate", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("GroundMaterialOverrideList", StringComparison.OrdinalIgnoreCase)) continue;
                    m.Write(d, t.Start, t.End - t.Start);
                }
                m.Write(Tag(nPst, nObj, refv));
                m.Write(Tag(nGl, nArr, new byte[4]));   // an empty array: count 0
                m.Write(d, tags.NoneAt, d.Length - tags.NoneAt);
                byte[] nd = m.ToArray();
                rep[i] = _ => nd;
                done.Add($"{on} → {p.Exports[use - 1].ObjectName} (own, empty list)");
            }
        if (rep.Count == 0) return power;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) { log.Add("  the fixed pivot particles didn't verify: " + string.Join("; ", problems.Take(2))); return power; }
        log.Add($"  experiment: ground-material particles fixed ({string.Join(", ", done)})");
        return output;
    }

    /// <summary>The travel condition's own copy of a borrowed start (Build's conditionStart): "movement_ridebike_start_cond".</summary>
    public const string ConditionStartSuffix = "_cond";

    /// <summary>Whether <paramref name="power"/> sets off a condition that plays animations of its own (ConditionAnimEffect custom
    /// with ConditionAnimNames: Hawkeye's Sky-Cycle condition plays his ride).</summary>
    static bool ConditionPlaysAnims(Fx.GameData db, Fx.FxGame game, string power)
    {
        foreach (var a in Fx.PowerClosure.Of(db, power).Where(a => a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase)))
        {
            string def = $"marvelgamecontent.default__{a.Class}".ToLowerInvariant();
            if (game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().Select(f => (P: Package.Open(f), F: f)).FirstOrDefault(x => Find(x.P, def) >= 0) is not { P: { } p }) continue;
            byte[] d = p.ReadExportBytes(p.Exports[Find(p, def)]).ToArray();
            if (TagWalker.Walk(p, d, 4)?.Any(t => t.Name.Equals("ConditionAnimNames", StringComparison.OrdinalIgnoreCase)) == true) return true;
        }
        return false;
    }

    static (byte[] Condition, byte[] Power, List<(string From, string To)> Anims)? ConditionVehicle(byte[] condition, byte[] power, string powerDefault, IReadOnlySet<string> vehicleClasses,
        IReadOnlyList<string> slots, IReadOnlyList<string> ownClass, string cooked, List<string> log, bool condStart = false)
    {
        var pp = Package.FromBytes(power);
        int pd = Find(pp, powerDefault);
        if (pd < 0) return null;
        var spawns = new List<(int Comp, int Class)>();
        foreach (int r in Components(pp, pd, out _, out _))
        {
            if (r <= 0 || !pp.ClassOf(pp.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = pp.ReadExportBytes(pp.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(pp, d, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } ct) continue;
            int c = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(ct.ValueAt));
            if (c > 0 && vehicleClasses.Contains(pp.Exports[c - 1].ObjectName)) spawns.Add((r, c));
        }
        if (spawns.Count == 0) return null;
        var cp = Package.FromBytes(condition);
        int cd = Array.FindIndex(cp.Exports, x => x.ObjectName.StartsWith("default__marvelconditioneffect", StringComparison.OrdinalIgnoreCase)
            && cp.PathOf(x).Count(ch => ch == '.') == 1);
        if (cd < 0) return null;
        string condDefault = cp.PathOf(cp.Exports[cd]);
        string tplFile = StockFiles.For(cooked, "UC__MarvelConditionEffect_ThingFantasticar_SF.upk");
        const string tplDefault = "marvelgamecontent.default__marvelconditioneffect_thingfantasticar", tplComp = tplDefault + ".car_attach";
        var tpk = File.Exists(tplFile) ? Package.Open(tplFile) : null;
        int ti = tpk == null ? -1 : Find(tpk, tplComp);
        if (tpk == null || ti < 0) { log.Add("  the condition's vehicle template (Fantasticar) isn't found: the vehicle stays in the power"); return null; }
        // 1: the vehicle class and its spawn component copied in
        var comps = new List<int>();
        var shown = new List<string>();
        foreach (int cls in spawns.Select(s => s.Class).Distinct())
        {
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(pp, cls - 1, cp, []), out string csaid)
                ?? throw new InvalidDataException($"{pp.Exports[cls - 1].ObjectName} couldn't be copied into the condition: " + string.Join(" ", csaid.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            cp = Package.FromBytes(cc.Output);
            int classRef = cc.RootRef;
            string name = "borrowed_vehicle_attach";
            for (int n2 = 2; Find(cp, condDefault + "." + name) >= 0; n2++) name = $"borrowed_vehicle_attach_{n2}";
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(tpk, ti, cp, ["whileactivatedattachmentclass"], name, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [tplDefault] = condDefault }), out string ssaid)
                ?? throw new InvalidDataException("the condition's vehicle component couldn't be copied: " + string.Join(" ", ssaid.Split(Environment.NewLine).Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3)));
            cp = Package.FromBytes(sc.Output);
            int comp = sc.RootRef - 1;
            byte[] d = cp.ReadExportBytes(cp.Exports[comp]).ToArray();
            var ct = TagWalker.Walk(cp, d, 16)?.FirstOrDefault(t => t.Name.Equals("WhileActivatedAttachmentClass", StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidDataException("the condition's vehicle component has no WhileActivatedAttachmentClass");
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(ct.ValueAt), classRef);
            var r1 = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => d };
            byte[] o1 = PackageRebuilder.Rebuild(cp, r1, [], out var w1);
            var p1 = PackageRebuilder.Verify(cp, o1, r1.Keys.ToList(), [], w1);
            if (p1.Count > 0) throw new InvalidDataException("the condition's vehicle component didn't verify: " + string.Join("; ", p1.Take(2)));
            cp = Package.FromBytes(o1);
            comps.Add(comp + 1);
            shown.Add(cp.Exports[classRef - 1].ObjectName);
            // the vehicle's own animation under the condition's start name too (it plays what the rider plays, by name)
            if (condStart && slots.FirstOrDefault(s => s.EndsWith("_start", StringComparison.OrdinalIgnoreCase)) is { } startSlot)
                cp = VehicleSequenceCopy(cp, "marvelgamecontent.default__" + cp.Exports[classRef - 1].ObjectName, startSlot, startSlot + ConditionStartSuffix, log);
        }
        // 2: listed by the condition, not on the costumes kept stock; the condition's animations the borrowed ones
        cd = Find(cp, condDefault);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(cp.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return cp.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        if (ownClass.Count > 0)
            foreach (int r in comps)
                if (WithEntityLists(cp, r - 1, ownClass, require: false, addNames) is { } ld) rep[r - 1] = _ => ld;
        var list = Components(cp, cd, out var tag, out byte[] dd);
        list.AddRange(comps);
        var value = new byte[4 + 4 * list.Count];
        BinaryPrimitives.WriteInt32LittleEndian(value, list.Count);
        for (int j = 0; j < list.Count; j++) BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4 + 4 * j), list[j]);
        byte[] nd = [.. dd.AsSpan(0, tag.ValueAt), .. value, .. dd.AsSpan(tag.ValueAt + tag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(tag.Start + 16), value.Length);
        var anims = new List<(string From, string To)>();
        if (TagWalker.Walk(cp, nd, 4)?.FirstOrDefault(t => t.Name.Equals("ConditionAnimNames", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)) is { } at)
        {
            int n = BinaryPrimitives.ReadInt32LittleEndian(nd.AsSpan(at.ValueAt));
            for (int k = 0; k < n; k++)
            {
                string old = TagWalker.NameAt(cp, nd, at.ValueAt + 4 + 8 * k);
                string? suffix = new[] { "_start", "_loop", "_end" }.FirstOrDefault(s => old.EndsWith(s, StringComparison.OrdinalIgnoreCase));
                if (suffix == null || slots.FirstOrDefault(s => s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) is not { } slot) continue;
                if (condStart && suffix == "_start") slot += ConditionStartSuffix;
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at.ValueAt + 4 + 8 * k), NameIdx(slot));
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(at.ValueAt + 8 + 8 * k), 0);
                anims.Add((old, slot));
            }
        }
        rep[cd] = _ => nd;
        byte[] cout = PackageRebuilder.Rebuild(cp, rep, [], out var cw, addNames);
        var cprob = PackageRebuilder.Verify(cp, cout, rep.Keys.ToList(), [], cw, addNames);
        if (cprob.Count > 0) throw new InvalidDataException("the condition with the vehicle didn't verify: " + string.Join("; ", cprob.Take(2)));
        // 3: the power no longer spawns it
        var plist = Components(pp, pd, out var ptag, out byte[] pdd).Where(r => !spawns.Any(s => s.Comp == r)).ToList();
        var pv = new byte[4 + 4 * plist.Count];
        BinaryPrimitives.WriteInt32LittleEndian(pv, plist.Count);
        for (int j = 0; j < plist.Count; j++) BinaryPrimitives.WriteInt32LittleEndian(pv.AsSpan(4 + 4 * j), plist[j]);
        byte[] pnd = [.. pdd.AsSpan(0, ptag.ValueAt), .. pv, .. pdd.AsSpan(ptag.ValueAt + ptag.Size)];
        BinaryPrimitives.WriteInt32LittleEndian(pnd.AsSpan(ptag.Start + 16), pv.Length);
        var prep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => pnd };
        byte[] pout = PackageRebuilder.Rebuild(pp, prep, [], out var pw);
        var pprob = PackageRebuilder.Verify(pp, pout, prep.Keys.ToList(), [], pw);
        if (pprob.Count > 0) throw new InvalidDataException("the power without its vehicle spawn didn't verify: " + string.Join("; ", pprob.Take(2)));
        // The power keeps its copy of the vehicle class and its AnimSet under the same paths, and it loads before the condition,
        // so the game uses the power's set: the condition's start copy must be there too. Evidence (trace.anims in the game,
        // Kurt 2026-11-03, 0.60.316): the bike's sequence nodes had no sequence (time 0.000 / 0.000 the whole ride), so the bike
        // sat in its rest pose under the rider's loop (rider "too low and forward"); the power's cyclops_bike_as had 3 sequences,
        // the condition's 4.
        if (condStart && slots.FirstOrDefault(s => s.EndsWith("_start", StringComparison.OrdinalIgnoreCase)) is { } powerStart)
        {
            var pk = Package.FromBytes(pout);
            foreach (string cls in spawns.Select(s => pp.Exports[s.Class - 1].ObjectName).Distinct(StringComparer.OrdinalIgnoreCase))
                pk = VehicleSequenceCopy(pk, "marvelgamecontent.default__" + cls, powerStart, powerStart + ConditionStartSuffix, log);
            pout = pk.RawFile;
        }
        log.Add($"  {condDefault}: shows the borrowed vehicle ({string.Join(", ", shown)}){(ownClass.Count > 0 ? $", not on {string.Join(", ", ownClass)}" : "")}; plays {(anims.Count > 0 ? string.Join(", ", anims.Select(a => $"{a.From} → {a.To}")) : "its own animations")}; the power's spawn taken out");
        return (cout, pout, anims);
    }

    /// <summary>
    /// A costume of its own class (Kate Bishop) with its own animations also under the names the travel condition now plays
    /// (ConditionVehicle's renames: movement_rideskycycle_start → movement_ridebike_start_cond …), on her and on her own
    /// vehicle: the condition's animations apply to everyone it's on, and she has no sequences of those names. Evidence
    /// (trace.anims, Kurt 2026-11-03, 0.60.321): Kate's condition played movement_ridebike_start_cond / _loop / _end, which
    /// her set lacks, and she rode her Sky-Cycle in the reference pose (A-pose). Null when there's nothing to add.
    /// </summary>
    static byte[]? OwnRideNames(string costumeFile, string costumeClass, IReadOnlyList<(string From, string To)> renames, List<string> log)
    {
        var pkg = Package.Open(costumeFile);
        var swaps = new List<AnimSwap.Swap>();
        foreach (var (from, to) in renames)
        {
            int seq = Array.FindIndex(pkg.Exports, x => pkg.ClassOf(x).Equals("AnimSequence", StringComparison.OrdinalIgnoreCase)
                && TagWalker.Walk(pkg, pkg.ReadExportBytes(x).ToArray(), 4)?.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } nt
                && TagWalker.NameAt(pkg, pkg.ReadExportBytes(x).ToArray(), nt.ValueAt).Equals(from, StringComparison.OrdinalIgnoreCase));
            if (seq < 0) { log.Add($"  {Path.GetFileName(costumeFile)}: no {from} of its own: it plays {to} as nothing"); continue; }
            swaps.Add(new AnimSwap.Swap(to, costumeFile, pkg.Exports[seq].OuterIndex - 1, seq));
        }
        if (swaps.Count == 0) return null;
        byte[] built = AnimSwap.Build(costumeFile, costumeClass, swaps, log);
        // its own vehicle (the Sky-Cycle, an animated attachment of its package) plays the same names
        var bp = Package.FromBytes(built);
        foreach (var e in bp.Exports.Where(x => x.ObjectName.StartsWith("default__marvelattachmentanimated_", StringComparison.OrdinalIgnoreCase)).ToList())
            foreach (var (from, to) in renames)
                bp = VehicleSequenceCopy(bp, bp.PathOf(e), from, to, log);
        log.Add($"  {Path.GetFileName(costumeFile)}: its own {string.Join(", ", renames.Select(r => r.From))} also as {string.Join(", ", renames.Select(r => r.To))} (the travel condition plays those names)");
        return bp.RawFile;
    }

    public static List<(string File, int Export, string Slot, bool Vehicle)> ConditionAttachments(Fx.GameData db, Fx.FxGame game, string donorPower, string donorHeroPackage, List<string> log)
    {
        var slots = new List<string>();
        // a slot a condition shows is a vehicle (a ride's bike, the Sky-Cycle); one the power itself shows is a weapon (webbing)
        var vehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hiddenWhole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Fx.PowerClosure.Of(db, donorPower).Where(a => a.Class.StartsWith("MarvelConditionEffect", StringComparison.OrdinalIgnoreCase) || a.IsPower && a.Depth == 0))
        {
            string? file = game.PackagesOf(a.Class).Select(game.FileFor).OfType<string>().FirstOrDefault(f => Find(Package.Open(f), $"marvelgamecontent.default__{a.Class}".ToLowerInvariant()) >= 0);
            if (file == null) continue;
            var p = Package.Open(file);
            List<int> comps;
            try { comps = Components(p, Find(p, $"marvelgamecontent.default__{a.Class}".ToLowerInvariant()), out _, out _); }
            catch (InvalidDataException) { continue; }
            // (the power's own: slots it hides from start to end; Deadpool's Real Armor Buster shows his pistols on activation
            // and hides them the whole attack, so they come back after it; spawned, they stayed in Doctor Strange's hands,
            // Kurt 2026-11-04)
            if (a.IsPower) hiddenWhole.UnionWith(PowerHiddenSlots(file, $"marvelgamecontent.default__{a.Class}".ToLowerInvariant(), wholeOnly: true));
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
        foreach (string hs in slots.Where(x => hiddenWhole.Contains(x) && !vehicles.Contains(x)).ToList())
        { slots.Remove(hs); log.Add($"  the donor's power hides slot {hs} the whole time: not spawned"); }
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
    /// <summary>The weapon slots a power's own mesh-attachment components hide while it runs (WhileActivatedHideWeaponSlot;
    /// "bothhands" = lefthand and righthand).</summary>
    static HashSet<string> PowerHiddenSlots(string file, string powerDefault, bool wholeOnly = false)
    {
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var p = Package.Open(file);
            int td = Find(p, powerDefault);
            if (td < 0) return slots;
            foreach (int r in Components(p, td, out _, out _))
            {
                if (r <= 0 || !p.ClassOf(p.Exports[r - 1]).Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] b = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
                var tags = TagWalker.Walk(p, b, 16) ?? [];
                // (wholeOnly: hidden from the power's start to its end, no timing of its own; Hawkeye's attacks hide the arrow
                // from contact time, Deadpool's Real Armor Buster hides his pistols the whole time)
                if (wholeOnly && tags.Any(t => t.Name.Equals("ActivationOffset", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase)
                    || t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) && !TagWalker.NameAt(p, b, t.ValueAt).Equals("power_on_start", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var t in tags)
                    if (t.Name.Equals("WhileActivatedHideWeaponSlot", StringComparison.OrdinalIgnoreCase) && TagWalker.NameAt(p, b, t.ValueAt) is { } s)
                    {
                        if (s.Equals("bothhands", StringComparison.OrdinalIgnoreCase)) { slots.Add("lefthand"); slots.Add("righthand"); }
                        else if (!s.Equals("none", StringComparison.OrdinalIgnoreCase)) slots.Add(s);
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return slots;
    }

    /// <summary>Every animated attachment of a hero package (default__marvelattachmentanimated_*) with a sequence of each
    /// pair's first name and none of its second: a copy under the second (VehicleSequenceCopy).</summary>
    static byte[] OwnAttachmentNames(byte[] package, IReadOnlyList<(string From, string To)> pairs, List<string> log)
    {
        var pkg = Package.FromBytes(package);
        bool Has(Package p, string def, string name)
        {
            foreach (int set in AttachmentSets(p, def))
            {
                byte[] sd = p.ReadExportBytes(p.Exports[set - 1]).ToArray();
                if (TagWalker.Walk(p, sd, 4)?.FirstOrDefault(t => t.Name.Equals("Sequences", StringComparison.OrdinalIgnoreCase)) is not { } st) continue;
                for (int q = 0, c = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt)); q < c; q++)
                {
                    int seq = BinaryPrimitives.ReadInt32LittleEndian(sd.AsSpan(st.ValueAt + 4 + 4 * q));
                    if (seq <= 0) continue;
                    byte[] qd = p.ReadExportBytes(p.Exports[seq - 1]).ToArray();
                    if (TagWalker.Walk(p, qd, 4)?.FirstOrDefault(t => t.Name.Equals("SequenceName", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } nt
                        && TagWalker.NameAt(p, qd, nt.ValueAt).Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
        var defs = pkg.Exports.Where(x => x.ObjectName.StartsWith("default__marvelattachmentanimated_", StringComparison.OrdinalIgnoreCase)).Select(x => pkg.PathOf(x)).ToList();
        foreach (string def in defs)
            foreach (var (from, to) in pairs)
                if (Has(pkg, def, from) && !Has(pkg, def, to))
                {
                    pkg = VehicleSequenceCopy(pkg, def, from, to, log);
                    if (Has(pkg, def, to)) log.Add($"  {def.Split("__")[^1]}: its own {from} also as {to} (the power plays that name)");
                }
        return pkg.RawFile;
    }

    static string? ShownSlot(Package p, int export)
    {
        byte[] b = p.ReadExportBytes(p.Exports[export]).ToArray();
        var tags = TagWalker.Walk(p, b, 16);
        var t = tags?.FirstOrDefault(x => x.Name.Equals("WhileActivatedShowWeaponSlot", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("WeaponSlotToShowOnActivate", StringComparison.OrdinalIgnoreCase));
        if (t != null) return TagWalker.NameAt(p, b, t.ValueAt);
        // a weapon switch (Punisher's Full Auto: SwitchAttachments { From, To }) shows its To slot (Kurt, 2026-11-04: Blade with it
        // showed no gun: the switch named a slot only Punisher fills)
        return tags?.FirstOrDefault(x => x.Name.Equals("SwitchAttachments", StringComparison.OrdinalIgnoreCase)) is { } sw ? SwitchTo(p, b, sw) : null;
    }

    /// <summary>A SwitchAttachments value's (first) To slot: one struct, or an array of them.</summary>
    static string? SwitchTo(Package p, byte[] b, TagWalker.Tag sw)
    {
        string? One(int at) => TagWalker.Walk(p, b, at)?.FirstOrDefault(x => x.Name.Equals("To", StringComparison.OrdinalIgnoreCase)) is { } to ? TagWalker.NameAt(p, b, to.ValueAt) : null;
        if (sw.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) return One(sw.ValueAt);
        if (sw.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase) && BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(sw.ValueAt)) > 0) return One(sw.ValueAt + 4);
        return null;
    }

    /// <summary>
    /// The attachment classes copied into the target power's package, each spawned by a component of its own while the power
    /// runs (WhileActivatedAttachmentClass). The component is a copy of a stock one (Ant-Man's ant_outro, the same form) with
    /// its class pointed at the copy.
    /// </summary>
    public static byte[] SpawnAttachments(byte[] package, string targetDefault, IReadOnlyList<(string File, int Export, string Slot, bool Vehicle)> classes, string cooked, List<string> log, IReadOnlySet<string>? targetBones = null, float scale = 1, IReadOnlyCollection<string>? holdAnims = null, IReadOnlySet<string>? heroSlots = null)
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
            var rooted = new List<string>();
            // a weapon on a bone this hero lacks that the donor's animations hold in a hand (Punisher's M60 on g_gun_body;
            // Kurt, 2026-11-04: on Blade it stood between his legs, pointing up): onto that hand, the hold baked into its model
            var onHand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            System.Numerics.Matrix4x4? hold = null;
            string clsDef = "marvelgamecontent.default__" + dst.Exports[classRef - 1].ObjectName;
            if (!vehicle && targetBones is { Count: > 0 } && !dst.Exports[classRef - 1].ObjectName.Contains("animated", StringComparison.OrdinalIgnoreCase))
                foreach (string bn in AttachmentBoneNames(dst, clsDef).Where(x => !x.Equals("none", StringComparison.OrdinalIgnoreCase) && !targetBones.Contains(x) && !x.StartsWith("g_mount", StringComparison.OrdinalIgnoreCase)))
                    if (HeldOnHand(file, bn, cooked, targetBones, log, holdAnims) is { } hh) { onHand[bn] = hh.Hand; hold = hh.Rel; break; }
            if (!vehicle && targetBones is { Count: > 0 } && ParentAnimToHand(dst, clsDef, targetBones, log) is { } pa) { dst = pa.Pkg; hold = pa.Bake; }
            if (heroSlots is { Count: > 0 }) dst = OwnSlots(dst, clsDef, heroSlots, log);
            if (targetBones is { Count: > 0 }) dst = RootMissingBones(dst, clsDef, targetBones, log, out rooted, onHand);
            if (hold is { } hm && FitModel(dst, clsDef, 1, default, default, log, hm) is { } held2) dst = held2;
            // a ridden prop on the donor's mount bone, held at root here: up by that bone's height over the donor's root in
            // his flight loop (Green Goblin: 4.8). The root itself rides up with the borrowed animation (31.6 on Captain
            // America), so the prop does too; its height over the model's origin (36.5) put the glider at their waist
            // (telemetry ride44, Kurt 2026-11-04). Moved by its model (FitModel's Origin)
            float mountLift = 0;
            string mountFrom = "";
            if (rooted.Contains("g_mountattach", StringComparer.OrdinalIgnoreCase) && MountMotion(file, "g_mountattach", cooked, log) is { } mmo && mmo.Reference.M43 > 1)
                (mountLift, mountFrom) = (mmo.Reference.M43, mmo.ReferenceFrom);
            if (mountLift > 0 && FitModel(dst, "marvelgamecontent.default__" + dst.Exports[classRef - 1].ObjectName, vehicle ? scale : 1, new System.Numerics.Vector3(0, 0, mountLift), default, log) is { } lifted)
            {
                dst = lifted;
                log.Add($"  {dst.Exports[classRef - 1].ObjectName}: raised {mountLift:0.#} (the donor's g_mountattach in its {mountFrom})");
            }
            else if (vehicle && Math.Abs(scale - 1) > 0.02f) dst = ScaleAttachment(dst, "marvelgamecontent.default__" + dst.Exports[classRef - 1].ObjectName, scale, cooked, log);
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
    static byte[] AddOwnTracks(byte[] package, string setSuffix, string targetHeroPackage, IReadOnlyList<string> targetAnims, List<string> log, bool attack = false)
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
                // (an attack: its own power's longest animation, not the run)
                string pick = Environment.GetEnvironmentVariable("MHO_BORROW_OWN_ANIM") is { Length: > 0 } env ? env : attack ? "" : "movement_run";
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
            var bones = own.Tracks.Where(t => t.Value.RotationKeys.Count > (attack ? 0 : 1) && !have.Contains(t.Key) && rest.ContainsKey(t.Key)).Select(t => t.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
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
    /// <summary>Every weapon slot a hero's attachments fill (on demand ones too), from its base package; null when unread.</summary>
    /// <summary>
    /// The spawned copy's WeaponSlot names the hero fills too get borrowed-only names ("borrowed_m60"): the borrow hides the
    /// hero's own weapons by slot, and that hid the copy as well (Kurt 2026-11-04, Nick Fury's Covering Fire with Punisher's
    /// Full Auto: Nick has his own m60, hasweaponsight and righthand slots, and the M60 never showed). Slots the hero doesn't
    /// have keep their names. The copy is spawned by its own component, so nothing needs to show it by slot.
    /// </summary>
    static Package OwnSlots(Package dst, string clsDef, IReadOnlySet<string> heroSlots, List<string> log)
    {
        int d = Find(dst, clsDef);
        if (d < 0) return dst;
        byte[] b = dst.ReadExportBytes(dst.Exports[d]).ToArray();
        var tag = TagWalker.Walk(dst, b, 4)?.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase));
        if (tag == null) return dst;
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var at = new List<int>();
        if (tag.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
            for (int j = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(tag.ValueAt)); j < n; j++) at.Add(tag.ValueAt + 4 + 8 * j);
        else if (tag.Type.Equals("NameProperty", StringComparison.OrdinalIgnoreCase)) at.Add(tag.ValueAt);
        var renamed = new List<string>();
        foreach (int o in at)
        {
            string s = TagWalker.NameAt(dst, b, o);
            if (!heroSlots.Contains(s)) continue;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), NameIdx("borrowed_" + s));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), 0);
            renamed.Add(s);
        }
        if (renamed.Count == 0) return dst;
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => b };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) { log.Add($"  {clsDef}: its slots couldn't be renamed ({string.Join("; ", problems.Take(2))})"); return dst; }
        log.Add($"  {dst.Exports[d].ObjectName}: slots the hero fills too get borrowed names ({string.Join(", ", renamed)}), so hiding the hero's own weapons leaves it alone");
        return Package.FromBytes(output);
    }

    /// <summary>Every WeaponSlot name of an attachment class (its default's list) in a package.</summary>
    static List<string> ClassSlots(string file, int classExport)
    {
        var slots = new List<string>();
        try
        {
            var p = Package.Open(file);
            int d = Find(p, "marvelgamecontent.default__" + p.Exports[classExport].ObjectName);
            if (d < 0) return slots;
            byte[] b = p.ReadExportBytes(p.Exports[d]).ToArray();
            var ws = TagWalker.Walk(p, b, 4)?.FirstOrDefault(t => t.Name.Equals("WeaponSlot", StringComparison.OrdinalIgnoreCase));
            if (ws == null) return slots;
            if (ws.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase))
                for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ws.ValueAt)); k < n; k++) slots.Add(TagWalker.NameAt(p, b, ws.ValueAt + 4 + 8 * k));
            else slots.Add(TagWalker.NameAt(p, b, ws.ValueAt));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return slots;
    }

    static HashSet<string>? HeroSlots(string heroPackage)
    {
        try
        {
            var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ModMeshes.Attachments(heroPackage))
                foreach (string s in a.Slots.Count > 0 ? a.Slots : a.Slot is { } one ? [one] : [])
                    if (!s.Equals("none", StringComparison.OrdinalIgnoreCase)) slots.Add(s);
            return slots;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
    }

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
    public static byte[] HideHeldWeapons(byte[] package, string targetDefault, string targetHeroPackage, string donorHeroPackage, string cooked, List<string> log, IEnumerable<string>? spawnedSlots = null)
    {
        var slots = HeldSlots(targetHeroPackage);
        // a slot the borrowed power spawns a prop into stays shown: hiding it hid the prop too (Kitty Pryde with Thor's
        // flight, 2026-11-03 telemetry: the hammer attached to g_r_palm, then WeaponSlotVisible(RightHand, 0) hid it; Kitty's
        // katana sits in RightHand, katana and kittyprydeswords, so the other two still hide it)
        foreach (string s in spawnedSlots ?? [])
            if (slots.Remove(s)) log.Add($"  this hero's {s} slot isn't hidden: the borrowed power's prop is held there");
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
        // the hero's particle attachments that go with a hidden slot (Green Goblin's glider exhaust: its own attachment class
        // marvelattachment_greengoblinboard_vfx, Tag goblingliderexhaust, on sockets socket_glider_exhaust_*, burned on under the
        // hidden glider in Thor's flight; 2026-11-03 telemetry). Hidden by tag as the game's own powers do (Goblin's revive:
        // hideexhaust, WhileActivatedHideParticles).
        var particleTags = ParticleTagsFor(targetHeroPackage, cooked, slots, log);
        string tplFile = StockFiles.For(cooked, "UC__PowerAntMan_AvatarSwapIn_SF.upk");
        const string tplDefault = "marvelgamecontent.default__powerantman_avatarswapin", tplComp = tplDefault + ".ant_outro";
        if (!File.Exists(tplFile)) { log.Add("  the hide component's template package isn't found: this hero's weapons stay shown"); return package; }
        var tp = Package.Open(tplFile);
        int ti = Find(tp, tplComp);
        if (ti < 0) { log.Add("  the hide component's template isn't found: this hero's weapons stay shown"); return package; }
        var hides = slots.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Select(s => (Prop: "WhileActivatedHideWeaponSlot", Value: s))
            .Concat(particleTags.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Select(s => (Prop: "WhileActivatedHideParticles", Value: s))).ToList();
        foreach (var (hideProp, slot) in hides)
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
            BinaryPrimitives.WriteInt32LittleEndian(tag, NameIdx(hideProp));
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
            log.Add(hideProp == "WhileActivatedHideParticles"
                ? $"  {name}: hides this hero's {slot} particles while the power runs (they go with a hidden weapon)"
                : $"  {name}: hides this hero's {slot} weapons while the power runs (the other hero's animation doesn't hold them)");
        }
        return dst.RawFile;
    }

    /// <summary>
    /// The tags of a hero's particle attachments (attachment classes with PEffectTemplates and a Tag) whose spawn sockets
    /// belong to one of <paramref name="slots"/> (the socket's name has the slot's: socket_glider_exhaust_l for glider), in
    /// its base package and its costumes' (Goblin's exhaust is in UC__MarvelPlayer_GreenGoblin_Classic_SF).
    /// </summary>
    static HashSet<string> ParticleTagsFor(string heroPackage, string cooked, IReadOnlyCollection<string> slots, List<string> log)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string stem = Path.GetFileNameWithoutExtension(heroPackage);                 // UC__MarvelPlayer_GreenGoblin_SF
        string prefix = stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
        var files = new List<string> { heroPackage };
        try
        {
            files.AddRange(Directory.EnumerateFiles(cooked, prefix + "_*_SF.upk")
                .Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                .Select(f => StockFiles.For(cooked, Path.GetFileName(f))));
        }
        catch (IOException) { }
        foreach (string f in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Package hp;
            try { hp = Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
            for (int i = 0; i < hp.Exports.Length; i++)
            {
                if (!hp.Exports[i].ObjectName.StartsWith("default__marvelattachment", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] b = hp.ReadExportBytes(hp.Exports[i]).ToArray();
                var t = TagWalker.Walk(hp, b, 4);
                if (t == null || !t.Any(x => x.Name.Equals("PEffectTemplates", StringComparison.OrdinalIgnoreCase))) continue;
                if (t.FirstOrDefault(x => x.Name.Equals("Tag", StringComparison.OrdinalIgnoreCase)) is not { } tg) continue;
                if (t.FirstOrDefault(x => x.Name.Equals("SpawnSockets", StringComparison.OrdinalIgnoreCase)) is not { } ss) continue;
                string tag = TagWalker.NameAt(hp, b, tg.ValueAt);
                int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ss.ValueAt));
                var sockets = Enumerable.Range(0, Math.Clamp(n, 0, 64)).Select(k => TagWalker.NameAt(hp, b, ss.ValueAt + 4 + 8 * k)).ToList();
                var slot = slots.FirstOrDefault(s => sockets.Any(sk => sk.Contains(s, StringComparison.OrdinalIgnoreCase)));
                if (slot != null && tags.Add(tag)) log.Add($"  {hp.Exports[i].ObjectName[9..]} ({Path.GetFileName(f)}): its particles ({tag}, on {string.Join(", ", sockets)}) go with the {slot} slot");
            }
        }
        return tags;
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

    /// <summary>Read-only (--hold-turn): a bone's chain up to the root in a hero's main mesh, each with its rest turn
    /// relative to its parent (what AnimSwap.RestTurns compares) as quaternion x y z w.</summary>
    internal static List<string> HoldChain(string heroPackage, string bone)
    {
        var lines = new List<string>();
        var bones = MainMesh(heroPackage)?.Bones;
        if (bones == null) return lines;
        int i = bones.ToList().FindIndex(b => b.Name.Equals(bone, StringComparison.OrdinalIgnoreCase));
        for (int k = i; k >= 0; k = k == 0 ? -1 : bones[k].ParentIndex)
        {
            var q = System.Numerics.Quaternion.Normalize(bones[k].Orientation);
            lines.Add($"{bones[k].Name} ({q.X:0.000} {q.Y:0.000} {q.Z:0.000} {q.W:0.000})");
            if (k == 0) break;
        }
        return lines;
    }

    /// <summary>
    /// Read-only census (--parent-census, 2026-11-03): every bone that hangs from different parents in different heroes'
    /// main meshes (Thor's g_r_palm under g_spine03_offset, Kitty Pryde's under g_r_wrist). An animation stores each bone's
    /// turn relative to its parent, so a borrowed key for such a bone means something else on the other hero; RestTurns
    /// compares rest turns relative to the parent and can't fix it. Per bone: the parents and the heroes using each.
    /// </summary>
    internal static List<string> ParentCensus(IEnumerable<string> heroPackages, out int heroes)
    {
        // bone -> parent -> heroes
        var map = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        heroes = 0;
        foreach (string f in heroPackages)
        {
            AnimExportCli.Meshes.SkeletalMesh? m;
            try { m = MainMesh(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or AnimExportCli.Packages.InvalidPackageException) { continue; }
            if (m?.Bones is not { Count: > 1 } bones) continue;
            heroes++;
            string hero = Path.GetFileNameWithoutExtension(f).Split('_', StringSplitOptions.RemoveEmptyEntries) is [_, _, var h, ..] ? h : f;
            for (int i = 1; i < bones.Count; i++)
            {
                string parent = bones[i].ParentIndex >= 0 && bones[i].ParentIndex < bones.Count ? bones[bones[i].ParentIndex].Name : "?";
                if (!map.TryGetValue(bones[i].Name, out var byParent)) map[bones[i].Name] = byParent = new(StringComparer.OrdinalIgnoreCase);
                if (!byParent.TryGetValue(parent, out var list)) byParent[parent] = list = [];
                list.Add(hero);
            }
        }
        var lines = new List<string>();
        foreach (var (bone, byParent) in map.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Value.Values.Sum(v => v.Count)).ThenBy(kv => kv.Key))
        {
            lines.Add($"{bone}: {byParent.Values.Sum(v => v.Count)} heroes, {byParent.Count} parents");
            foreach (var (parent, list) in byParent.OrderByDescending(kv => kv.Value.Count))
                lines.Add($"    under {parent} ({list.Count}): {string.Join(", ", list.OrderBy(x => x))}");
        }
        return lines;
    }

    static AnimExportCli.Meshes.SkeletalMesh? MainMesh(string heroPackage)
    {
        var ap = AnimExportCli.Packages.Package.Open(heroPackage);
        string hero = Path.GetFileNameWithoutExtension(heroPackage).Split('_', StringSplitOptions.RemoveEmptyEntries) is [_, _, var h, ..] ? h : "";
        var meshes = new List<AnimExportCli.Meshes.SkeletalMesh>();
        foreach (int i in ap.FindExportsOfClass("skeletalmesh"))
            if (AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, i) is { } m) meshes.Add(m);
        int Verts(AnimExportCli.Meshes.SkeletalMesh m) => m.HighestDetail?.Positions.Count ?? 0;
        // a character's model (its skeleton has a spine) before a prop's: Blade's motorcycle has more vertices than he does, and
        // its 21 bones made the borrow think he had no hands (Kurt, 2026-11-04: Punisher's M60 stood between his legs)
        static bool Body(AnimExportCli.Meshes.SkeletalMesh m) => m.Bones.Any(b => b.Name.Equals("g_spine01", StringComparison.OrdinalIgnoreCase));
        return meshes.Where(m => hero.Length > 0 && m.Name.StartsWith(hero, StringComparison.OrdinalIgnoreCase)).OrderByDescending(Body).ThenByDescending(Verts).FirstOrDefault()
            ?? meshes.OrderByDescending(Body).ThenByDescending(Verts).FirstOrDefault();
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
        BakeMoveTurn(md, where, ModMeshes.Rotator(Units(turn.Y), Units(turn.Z), Units(turn.X)), move);
    }

    /// <summary>The same with the turn as a rotation matrix (row vectors: a point becomes point × rot + move).</summary>
    static void BakeMoveTurn(byte[] md, List<AnimExportCli.Meshes.SkeletalMeshReader.PositionSpot> where, System.Numerics.Matrix4x4 rot, System.Numerics.Vector3 move)
    {
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

    /// <summary>
    /// Whether a power's effects spawn on the held weapon <paramref name="w"/>: on a socket whose bone is one the weapon hangs
    /// from (its class default's AttachmentBones), or whose name holds the weapon's slot (socket_hammer). The sockets' bones
    /// come from the donor's models (base package and costumes, as AddBorrowedSockets reads them).
    /// </summary>
    static bool EffectsUseWeapon(string powerFile, string powerDefault, string donorHeroPackage, (string File, int Export, string Slot, bool Vehicle) w, string? cooked, out string why)
    {
        HashSet<string> sockets;
        try { sockets = SpawnSocketNames(File.ReadAllBytes(powerFile), powerDefault); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { why = "the power doesn't read"; return true; }
        var hp = Package.Open(w.File);
        string cls = hp.Exports[w.Export].ObjectName;
        var bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int d = Find(hp, "marvelgamecontent.default__" + cls);
        if (d >= 0)
        {
            byte[] b = hp.ReadExportBytes(hp.Exports[d]).ToArray();
            if (TagWalker.Walk(hp, b, 4)?.FirstOrDefault(t => t.Name.Equals("AttachmentBones", StringComparison.OrdinalIgnoreCase)) is { } ab)
                for (int k = 0, n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ab.ValueAt)); k < n; k++)
                    if (TagWalker.NameAt(hp, b, ab.ValueAt + 4 + 8 * k) is var bn && !bn.Equals("none", StringComparison.OrdinalIgnoreCase)) bones.Add(bn);
        }
        if (sockets.Any(x => x.Contains(w.Slot, StringComparison.OrdinalIgnoreCase))) { why = "its effects spawn on its socket"; return true; }
        if (bones.Count > 0)
        {
            var files = new List<string> { donorHeroPackage };
            string stem = Path.GetFileNameWithoutExtension(donorHeroPackage), prefix = stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
            if (cooked != null)
                try
                {
                    files.AddRange(Directory.EnumerateFiles(cooked, prefix + "_*_SF.upk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                        .Select(f => StockFiles.For(cooked, Path.GetFileName(f))));
                }
                catch (IOException) { }
            foreach (string f in files)
            {
                Package sp;
                try { sp = Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
                foreach (var (_, name, bone) in MeshSockets(sp))
                    if (sockets.Contains(name) && bones.Contains(bone)) { why = $"its effects spawn on {name}, on its {bone}"; return true; }
            }
        }
        why = sockets.Count == 0 ? "the power's effects spawn on no socket of it" : $"the power's effects spawn on {string.Join(", ", sockets.Order())}, none on it";
        return false;
    }

    /// <summary>The ending of a socket a borrow adds under its own name (the donor's socket, turned as the donor has it).</summary>
    const string BorrowedSocket = "_borrowed";
    static string DonorSocket(string s) => s.EndsWith(BorrowedSocket, StringComparison.OrdinalIgnoreCase) ? s[..^BorrowedSocket.Length] : s;

    /// <summary>
    /// The sockets in <paramref name="wanted"/> that both heroes' main models have but turned more than 30 degrees apart in the
    /// rest pose (Kurt, 2026-11-05: Emma Frost's Psychic Blast on Blade came out of the side of his head. Her socket_head
    /// points forward, his sideways: 52 of 66 heroes' socket_head points sideways, 13 forward (Emma, Jean Grey, Thor, Doom,
    /// Vision ...); a cone or beam spawned there fires along the socket). Those get the donor's socket under their own name
    /// (socket_head_borrowed), so the hero's own powers keep the hero's.
    /// </summary>
    static List<string> TurnedSockets(string donorHeroPackage, string targetHeroPackage, IEnumerable<string> wanted, List<string> log)
    {
        var res = new List<string>();
        foreach (string s in wanted)
        {
            if (s.EndsWith(BorrowedSocket, StringComparison.OrdinalIgnoreCase) || s.Equals("socket_hit", StringComparison.OrdinalIgnoreCase)) continue;   // (the enemy's)
            if (SocketFacing(donorHeroPackage, s) is not { } d || SocketFacing(targetHeroPackage, s) is not { } t) continue;
            float deg = MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(d, t), -1, 1)) * 180 / MathF.PI;
            if (deg <= 30) continue;
            res.Add(s);
            log.Add($"  {s}: the donor's points {deg:0} degrees from this hero's: the borrowed effects use the donor's as {s}{BorrowedSocket}");
        }
        return res;
    }

    /// <summary>A socket's direction (its X axis) in the rest pose of a hero's main model, else null.</summary>
    internal static System.Numerics.Vector3? SocketFacing(string heroPackage, string socket)
    {
        try
        {
            if (MainMesh(heroPackage) is not { } m || !Fx.FxSockets.Of(heroPackage, m.Name).TryGetValue(socket, out var so)) return null;
            var mats = new System.Numerics.Matrix4x4[m.Bones.Count];
            for (int i = 0; i < mats.Length; i++)
            {
                var b = m.Bones[i];
                var local = System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Normalize(b.Orientation)) * System.Numerics.Matrix4x4.CreateTranslation(b.Position);
                mats[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? local * mats[b.ParentIndex] : local;
                if (b.Name.Equals(so.Bone, StringComparison.OrdinalIgnoreCase))
                {
                    var w = so.Local * mats[i];
                    return System.Numerics.Vector3.Normalize(new(w.M11, w.M12, w.M13));
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return null; }
    }

    /// <summary>The power's components' SpawnSockets naming one of <paramref name="sockets"/> switched to its _borrowed name.</summary>
    static byte[] UseBorrowedSockets(byte[] package, string powerDefault, IReadOnlyCollection<string> sockets, List<string> log)
    {
        var p = Package.FromBytes(package);
        int td = Find(p, powerDefault);
        if (td < 0 || sockets.Count == 0) return package;
        var addNames = new List<string>();
        int NameOf(string n)
        {
            int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        var rep = new Dictionary<int, Func<long, byte[]>>();
        int changed = 0;
        foreach (int r in Components(p, td, out _, out _).Distinct())
        {
            if (r <= 0) continue;
            byte[] d = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            var tags = TagWalker.Walk(p, d, 16);
            if (tags?.FirstOrDefault(t => t.Name.Equals("SpawnSockets", StringComparison.OrdinalIgnoreCase)) is not { } ss) continue;
            // (an effect on the one hit, timed by the hit, plays on the enemy's socket: left alone, the enemy has no _borrowed one)
            if (tags.FirstOrDefault(t => t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase)) is { Size: 8 } apt
                && TagWalker.NameAt(p, d, apt.ValueAt).Contains("target_result", StringComparison.OrdinalIgnoreCase)) continue;
            bool any = false;
            // (as SpawnSocketNames reads them: every name value in the list that's one of these sockets)
            for (int at = ss.ValueAt + 4; at + 8 <= ss.ValueAt + ss.Size; at += 4)
            {
                int ni = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
                if (ni <= 0 || ni >= p.Names.Length || BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4)) != 0) continue;
                if (sockets.FirstOrDefault(x => x.Equals(p.Names[ni], StringComparison.OrdinalIgnoreCase)) is not { } hit) continue;
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at), NameOf(hit + BorrowedSocket));
                any = true; changed++;
            }
            if (any) { byte[] nd = d; rep[r - 1] = _ => nd; }
        }
        if (rep.Count == 0) return package;
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the borrowed sockets: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {powerDefault}: {changed} effect socket(s) switched to the donor's ({string.Join(", ", sockets.Select(x => x + BorrowedSocket))})");
        return output;
    }

    /// <summary>The socket names a power's components spawn on (SpawnSockets of every component of <paramref name="powerDefault"/>).</summary>
    static HashSet<string> SpawnSocketNames(byte[] package, string powerDefault)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var p = Package.FromBytes(package);
        int td = Find(p, powerDefault);
        if (td < 0) return names;
        foreach (int r in Components(p, td, out _, out _))
        {
            if (r <= 0) continue;
            byte[] d = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("SpawnSockets", StringComparison.OrdinalIgnoreCase)) is not { } ss) continue;
            // a list of tagged structs (108 bytes for one): every name value in it that's a socket's
            for (int at = ss.ValueAt + 4; at + 8 <= ss.ValueAt + ss.Size; at += 4)
            {
                int ni = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
                if (ni <= 0 || ni >= p.Names.Length) continue;
                string s = p.Names[ni];
                if (s.StartsWith("socket_", StringComparison.OrdinalIgnoreCase)) names.Add(s);
            }
        }
        return names;
    }

    /// <summary>
    /// The donor's sockets in <paramref name="wanted"/> that this hero's models (in <paramref name="heroPackage"/>) lack, on
    /// bones they have, copied onto them (bone, place and turn as the donor has them) and appended to their Sockets lists;
    /// verified. A borrowed effect on a socket the model lacks never spawns (2026-11-03 telemetry: Thor's vfx_fly and
    /// vfx_plowthrough_hammer_01 on his socket_hammer, g_r_palm; on Kitty Pryde the components activated and nothing showed).
    /// </summary>
    static byte[] AddBorrowedSockets(byte[] heroPackage, string donorHeroPackage, IReadOnlyCollection<string> wanted, string? cooked, List<string> log, string? hero = null)
    {
        // the donor's sockets by name: its base package, then its costumes' (Thor's socket_hammer is on thor_classic in
        // UC__MarvelPlayer_Thor_Classic_SF; his base package has only the hammer's own)
        var donorSockets = new Dictionary<string, (Package Pkg, int Export, string Bone)>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string> { donorHeroPackage };
        string stem = Path.GetFileNameWithoutExtension(donorHeroPackage), prefix = stem.EndsWith("_SF", StringComparison.OrdinalIgnoreCase) ? stem[..^3] : stem;
        if (cooked != null)
            try
            {
                files.AddRange(Directory.EnumerateFiles(cooked, prefix + "_*_SF.upk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Where(f => !Path.GetFileName(f).Contains("bak", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("copy", StringComparison.OrdinalIgnoreCase))
                    .Select(f => StockFiles.For(cooked, Path.GetFileName(f))));
            }
            catch (IOException) { }
        foreach (string f in files)
        {
            if (wanted.Select(DonorSocket).All(donorSockets.ContainsKey)) break;
            Package sp;
            try { sp = Package.Open(f); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
            foreach (var (sock, name, bone) in MeshSockets(sp)) donorSockets.TryAdd(name, (sp, sock, bone));
        }
        var need = wanted.Where(w => donorSockets.ContainsKey(DonorSocket(w))).ToList();
        if (need.Count == 0) return heroPackage;
        var dst = Package.FromBytes(heroPackage);
        var ap = AnimExportCli.Packages.Package.Read(dst.RawFile);
        var meshes = new List<int>();
        for (int i = 0; i < dst.Exports.Length; i++)
            if (dst.ClassOf(dst.Exports[i]).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase)) meshes.Add(i);
        var added = new List<string>();
        foreach (int mi0 in meshes)
        {
            int mi = mi0;
            var bones = AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, mi)?.Bones.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bones == null || !bones.Contains("g_spine01")) continue;   // a character's model, not a prop's
            if (hero is { Length: > 0 } && !dst.Exports[mi].ObjectName.StartsWith(hero, StringComparison.OrdinalIgnoreCase)) continue;   // the hero's, not a pet's or a summon's (Lockheed, Angel)
            var have = MeshSockets(dst).Where(x => dst.Exports[x.Export].OuterIndex == mi + 1).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string meshPath = dst.PathOf(dst.Exports[mi]);
            foreach (string s in need)
            {
                var ds = donorSockets[DonorSocket(s)];
                if (have.Contains(s) || !bones.Contains(ds.Bone)) continue;
                var src = ds.Pkg;
                int se = ds.Export;
                string donorMesh = src.PathOf(src.Exports[src.Exports[se].OuterIndex - 1]);
                var sc = CrossMove.Quiet(() => ExportCopy.Copy(src, se, dst, [], $"borrowed_{s}", new Dictionary<string, string> { [donorMesh] = meshPath }), out string said);
                if (sc == null) { log.Add($"  {s}: the socket couldn't be copied onto {dst.Exports[mi].ObjectName}"); continue; }
                dst = Package.FromBytes(sc.Output);
                // the mesh's Sockets array: the new socket appended
                byte[] md = dst.ReadExportBytes(dst.Exports[mi]).ToArray();
                if (TagWalker.Walk(dst, md, 4)?.FirstOrDefault(t => t.Name.Equals("Sockets", StringComparison.OrdinalIgnoreCase)) is not { } arr) { log.Add($"  {s}: {dst.Exports[mi].ObjectName} has no socket list"); continue; }
                int count = BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(arr.ValueAt)), end = arr.ValueAt + 4 + 4 * count;
                byte[] nd = [.. md.AsSpan(0, end), .. BitConverter.GetBytes(sc.RootRef), .. md.AsSpan(end)];
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(arr.ValueAt), count + 1);
                BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(arr.Start + 16), arr.Size + 4);
                var rep = new Dictionary<int, Func<long, byte[]>> { [mi] = _ => nd };
                // a socket under its own name (socket_head_borrowed: the donor's turns another way than the hero's own, see
                // TurnedSockets): the copy's SocketName set to it
                var addNames = new List<string>();
                if (!s.Equals(DonorSocket(s), StringComparison.OrdinalIgnoreCase))
                {
                    int sx = sc.RootRef - 1;
                    byte[] sd = dst.ReadExportBytes(dst.Exports[sx]).ToArray();
                    if (TagWalker.Walk(dst, sd, 4)?.FirstOrDefault(t => t.Name.Equals("SocketName", StringComparison.OrdinalIgnoreCase)) is not { } sn) { log.Add($"  {s}: the copied socket has no name"); continue; }
                    int ni = Array.FindIndex(dst.Names, x => x.Equals(s, StringComparison.OrdinalIgnoreCase));
                    if (ni < 0) { addNames.Add(s); ni = dst.Names.Length; }
                    BinaryPrimitives.WriteInt32LittleEndian(sd.AsSpan(sn.ValueAt), ni);
                    BinaryPrimitives.WriteInt32LittleEndian(sd.AsSpan(sn.ValueAt + 4), 0);
                    rep[sx] = _ => sd;
                }
                byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
                var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
                if (problems.Count > 0) throw new InvalidDataException("the borrowed socket: " + string.Join("; ", problems.Take(3)));
                dst = Package.FromBytes(output);
                added.Add($"{s} ({ds.Bone}) on {dst.Exports[mi].ObjectName}");
            }
        }
        if (added.Count == 0) return heroPackage;
        log.Add($"  sockets the borrowed effects spawn on, added: {string.Join(", ", added)}");
        return dst.RawFile;
    }

    /// <summary>
    /// Right-click → Add Borrowed Power Sockets (Kurt, 2026-11-03): a costume mod's own costume packages get the sockets that
    /// the library's borrow mods for the same hero spawn effects on (a borrow leaves a costume package a costume mod replaces
    /// alone, so on that costume the effect never spawned: Cyclops' 90s X-Men Brown Jacket under Thor's flight). Returns the
    /// changed packages (file name, new bytes, verified by AddBorrowedSockets); writes nothing. Notes say what was found.
    /// </summary>
    public static List<(string File, byte[] Bytes)> SocketsForCostumeMod(ModLibrary lib, Mod costume, string cooked, List<string> notes)
    {
        var changed = new List<(string, byte[])>();
        var rx = new System.Text.RegularExpressions.Regex(@"^UC__MarvelPlayer_([A-Za-z0-9]+)(_.+)?_SF\.upk$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var byHero = costume.Manifest.UpkReplacements.Select(f => (File: f, M: rx.Match(f))).Where(x => x.M.Success)
            .GroupBy(x => x.M.Groups[1].Value, StringComparer.OrdinalIgnoreCase);
        foreach (var hg in byHero)
        {
            string hero = hg.Key;
            // the borrows for this hero: (donor hero package, the socket names its borrowed effects spawn on)
            var borrows = new List<(string Donor, HashSet<string> Sockets, string Mod)>();
            foreach (var bm in lib.Mods.Where(x => x.Folder != costume.Folder && x.Manifest.PowerBorrows is { Count: > 0 }))
                foreach (var e in bm.Manifest.PowerBorrows!.Where(e => hero.Equals(e.Hero, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.DonorHero)))
                {
                    var socks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string pf in (e.Packages ?? []).Where(f => f.StartsWith("UC__Power", StringComparison.OrdinalIgnoreCase)))
                    {
                        string path = Path.Combine(bm.Folder, pf);
                        if (!File.Exists(path)) continue;
                        var pp = Package.Open(path);
                        for (int i = 0; i < pp.Exports.Length; i++)
                        {
                            if (!pp.Exports[i].ObjectName.StartsWith("borrowed_", StringComparison.OrdinalIgnoreCase)) continue;
                            byte[] d = pp.ReadExportBytes(pp.Exports[i]).ToArray();
                            if (TagWalker.Walk(pp, d, 16)?.FirstOrDefault(t => t.Name.Equals("SpawnSockets", StringComparison.OrdinalIgnoreCase)) is not { } ss) continue;
                            for (int at = ss.ValueAt + 4; at + 8 <= ss.ValueAt + ss.Size; at += 4)
                            {
                                int ni = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
                                if (ni > 0 && ni < pp.Names.Length && pp.Names[ni].StartsWith("socket_", StringComparison.OrdinalIgnoreCase)) socks.Add(pp.Names[ni]);
                            }
                        }
                    }
                    if (socks.Count > 0) borrows.Add((StockFiles.For(cooked, DonorFile(e.DonorHero!)), socks, bm.Manifest.Name));
                }
            if (borrows.Count == 0) { notes.Add($"No borrow mod in the library gives {hero} effects on sockets."); continue; }
            notes.Add($"{hero}: {borrows.Count} borrow(s) checked ({string.Join(", ", borrows.Select(b => b.Mod).Distinct())}).");
            foreach (var (file, _) in hg)
            {
                string path = Path.Combine(costume.Folder, file);
                if (!File.Exists(path)) continue;
                byte[] start = File.ReadAllBytes(path), cur = start;
                var log = new List<string>();
                foreach (var b in borrows.GroupBy(b => b.Donor, StringComparer.OrdinalIgnoreCase))
                    cur = AddBorrowedSockets(cur, b.Key, b.SelectMany(x => x.Sockets).ToHashSet(StringComparer.OrdinalIgnoreCase), cooked, log, hero);
                if (!ReferenceEquals(cur, start)) { changed.Add((file, cur)); notes.AddRange(log.Select(l => $"{file}: {l.Trim()}")); }
            }
        }
        if (changed.Count == 0) notes.Add("Its models already have every socket those borrows use: nothing to add.");
        return changed;
    }

    /// <summary>Writes <see cref="SocketsForCostumeMod"/>'s packages into the mod's folder: each to a temp file beside it,
    /// opened again as a package, then put in place. Returns the files written.</summary>
    public static List<string> WriteCostumeModSockets(Mod costume, List<(string File, byte[] Bytes)> changes)
    {
        var done = new List<string>();
        foreach (var (file, bytes) in changes)
        {
            string path = Path.Combine(costume.Folder, file), tmp = path + ".sockets.tmp";
            File.WriteAllBytes(tmp, bytes);
            var check = Package.Open(tmp);
            if (check.Exports.Length == 0) { File.Delete(tmp); throw new InvalidDataException($"{file}: the new package doesn't read"); }
            File.Move(tmp, path, true);
            done.Add(file);
        }
        return done;
    }

    /// <summary>Every SkeletalMeshSocket in a package whose outer is a mesh: (export, SocketName, BoneName).</summary>
    static List<(int Export, string Name, string Bone)> MeshSockets(Package p)
    {
        var list = new List<(int, string, string)>();
        for (int i = 0; i < p.Exports.Length; i++)
        {
            if (!p.ClassOf(p.Exports[i]).Equals("SkeletalMeshSocket", StringComparison.OrdinalIgnoreCase) || p.Exports[i].OuterIndex <= 0) continue;
            byte[] sd = p.ReadExportBytes(p.Exports[i]).ToArray();
            var props = TagWalker.Walk(p, sd, 4);
            string? Nm(string n) => props?.FirstOrDefault(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } t ? TagWalker.NameAt(p, sd, t.ValueAt) : null;
            if (Nm("SocketName") is string sn && Nm("BoneName") is string bn) list.Add((i, sn, bn));
        }
        return list;
    }

    /// <summary>Bones that hang from another parent on the target's skeleton than on the donor's (AnimSwap.Reparent fixes their keys).</summary>
    internal static HashSet<string>? ReparentedBones(string donorHeroPackage, string targetHeroPackage)
    {
        if (HeroSkeleton(donorHeroPackage) is not { Count: > 1 } d || HeroSkeleton(targetHeroPackage) is not { Count: > 1 } t) return null;
        static string Parent(IReadOnlyList<AnimExportCli.Meshes.MeshBone> sk, int i) => i > 0 && sk[i].ParentIndex >= 0 && sk[i].ParentIndex < sk.Count ? sk[sk[i].ParentIndex].Name : "";
        var dp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < d.Count; i++) dp.TryAdd(d[i].Name, Parent(d, i));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < t.Count; i++) if (dp.TryGetValue(t[i].Name, out var p) && !p.Equals(Parent(t, i), StringComparison.OrdinalIgnoreCase)) set.Add(t[i].Name);
        return set;
    }

    internal static float DefaultTurnZ(string cls) => cls.StartsWith("marvelattachment_thorhammer", StringComparison.OrdinalIgnoreCase) ? -170 : 0;

    /// <summary>
    /// Prop Fit (Kurt, 2026-10-21: glider, Thor's hammer, Cap's shield: size, place, turn): every attachment class the
    /// target power's parts spawn (WhileActivatedAttachmentClass), drawn at its fit (FitModel): a vehicle sized for the hero
    /// by default (<paramref name="vehicleAuto"/>), anything else as made; listed in <paramref name="spawnedOut"/>.
    /// </summary>
    static byte[] FitProps(byte[] package, string targetDefault, IReadOnlySet<string> vehicleClasses, float vehicleAuto, IReadOnlyList<PropFit>? fits, List<string> log, List<SpawnedProp>? spawnedOut, IReadOnlySet<string>? reparented = null)
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
            var bones = AttachBones(dst, "marvelgamecontent.default__" + cls);
            // a prop on a bone the borrowed animations now turn as in the donor's body (AnimSwap.Reparent: Thor's palm) is held
            // as the donor holds it: no default turn (the -170 made up for that palm; 2026-11-03)
            float turnAuto = reparented != null && bones?.Any(reparented.Contains) == true ? 0 : DefaultTurnZ(cls);
            spawnedOut?.Add(new SpawnedProp(cls, vehicle, auto, turnAuto, bones));
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
    /// <summary>A prop whose animations' hand keys are rewritten (PropHold): pinned (Pivot on the Hand), or adjusted in one of
    /// its animations (In This Animation works without the pin too, Kurt 2026-10-30).</summary>
    public static bool NeedsHold(AddedPropEntry e) => e.Fit.PinPivot || e.Anims?.Any(a => !a.IsZero) == true;

    public static byte[] AddProp(byte[] package, AddedPropEntry e, string fromPath, List<string> log, Func<byte[], AddedPropEntry, IReadOnlyList<string>, List<string>, byte[]>? hold = null, string? modelFrom = null)
    {
        hold ??= (b, pe, bones, l) => pe.Fit.PinPivot ? PinPropBones(b, bones, true, l) : b;
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
            // (the character's class default by its own name: def is the renamed copy's, which isn't in the character's package,
            // so the bone was taken out and the prop hung on none: other players' clients crashed drawing Worthy Captain America
            // with Thor's hammer, 2026-10-30)
            var want = e.Bone is string eb ? [eb] : AttachBones(Package.Open(fromPath), "marvelgamecontent.default__" + e.Class.ToLowerInvariant());
            if (!AttachBones(dst, def).SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) dst = WithAttachBones(dst, Find(dst, def), want, log);
        }
        // listed in the costume's own props
        int classRef = ClassExport(dst) + 1;
        if (classRef <= 0) throw new InvalidDataException($"{e.Class} isn't in the package after copying");
        int pd = ModMeshes.CharacterDefault(dst);
        if (pd < 0) throw new InvalidDataException("the package has no character class default (not a costume, team-up or enemy)");
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
        for (int k = 0; k < n; k++) if (BinaryPrimitives.ReadInt32LittleEndian(pb.AsSpan(at.ValueAt + 4 + 4 * k)) == classRef) return NeedsHold(e) ? hold(dst.RawFile, e, pinBones, log) : dst.RawFile;   // listed already
        byte[] np = [.. pb.AsSpan(0, at.End), .. BitConverter.GetBytes(classRef), .. pb.AsSpan(at.End)];
        BinaryPrimitives.WriteInt32LittleEndian(np.AsSpan(at.ValueAt), n + 1);
        BinaryPrimitives.WriteInt32LittleEndian(np.AsSpan(at.Start + 16), at.Size + 4);
        var rep = new Dictionary<int, Func<long, byte[]>> { [pd] = _ => np };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException("the costume's prop list couldn't be changed: " + string.Join("; ", problems.Take(2)));
        log.Add($"{e.Class} added to {dst.Exports[pd].ObjectName}'s props ({n + 1} now)");
        return NeedsHold(e) ? hold(output, e, pinBones, log) : output;
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
        int pd = ModMeshes.CharacterDefault(dst);
        if (pd < 0) throw new InvalidDataException("the package has no character class default (not a costume, team-up or enemy)");
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
        // (an enemy or team-up package without a list, Skrull Captain America: given an empty one; Kurt, 2026-10-31)
        string? baseFile = stem.StartsWith(Pre, StringComparison.OrdinalIgnoreCase) ? $"{Pre}{stem[Pre.Length..].Split('_')[0]}_SF.upk" : null;
        var heroPaths = new List<string>();
        if (baseFile != null && !baseFile.Equals(costumeFile, StringComparison.OrdinalIgnoreCase) && StockFiles.Cooked is string cooked && StockFiles.For(cooked, baseFile) is var basePath && File.Exists(basePath))
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
        if (baseFile == null) { log.Add($"{dst.Exports[pd].ObjectName} had no prop list: given one of its own"); return back; }
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
        int pd = ModMeshes.CharacterDefault(dst);
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
        int pd = ModMeshes.CharacterDefault(p);
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

    static Package? FitModel(Package dst, string classDefault, float scale, System.Numerics.Vector3 move, System.Numerics.Vector3 turn, List<string> log, System.Numerics.Matrix4x4? bakeMatrix = null)
    {
        // (bakeMatrix: the whole transform baked into a plain prop's points: a weapon moved from a bone the hero lacks onto
        // his hand; its translation stands in for move in the copy's name)
        if (bakeMatrix is { } bm0) move = bm0.Translation + new System.Numerics.Vector3(0.01f, 0, 0);
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
        if (bake && bakeMatrix is { } bm) { var r = bm; r.Translation = System.Numerics.Vector3.Zero; BakeMoveTurn(md, where, r, bm.Translation); }
        else if (bake && moved) BakeMoveTurn(md, where, move, turn);
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

    /// <summary>The bones of a donor package's own model (HeroBones), cached per file for the build.</summary>
    internal static HashSet<string>? DonorModelBones(string package)
    {
        lock (donorBonesCache)
        {
            if (!donorBonesCache.TryGetValue(package, out var b)) donorBonesCache[package] = b = HeroBones(package);
            return b;
        }
    }
    static readonly Dictionary<string, HashSet<string>?> donorBonesCache = new(StringComparer.OrdinalIgnoreCase);

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
    static Package RootMissingBones(Package dst, string classDefault, IReadOnlySet<string> bones, List<string> log) => RootMissingBones(dst, classDefault, bones, log, out _);

    static Package RootMissingBones(Package dst, string classDefault, IReadOnlySet<string> bones, List<string> log, out List<string> movedBones, IReadOnlyDictionary<string, string>? instead = null)
    {
        movedBones = [];
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
            int to = root;
            if (instead != null && instead.TryGetValue(bone, out var hand))
            {
                to = Array.FindIndex(dst.Names, x => x.Equals(hand, StringComparison.OrdinalIgnoreCase));
                if (to < 0) { if (!addNames.Contains(hand, StringComparer.OrdinalIgnoreCase)) addNames.Add(hand); to = dst.Names.Length + addNames.FindIndex(x => x.Equals(hand, StringComparison.OrdinalIgnoreCase)); }
            }
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), to);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), 0);
            moved.Add(bone);
        }
        if (moved.Count == 0) return dst;
        movedBones = moved;
        var rep = new Dictionary<int, Func<long, byte[]>> { [d] = _ => b };
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the spawned attachment's bone: " + string.Join("; ", problems.Take(3)));
        log.Add($"  {dst.Exports[d].ObjectName}: " + string.Join("; ", moved.GroupBy(m => instead != null && instead.TryGetValue(m, out var h) ? h : "root").Select(g => $"held at {g.Key} (this hero has no {string.Join(", ", g)})")));
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

    static Fx.AssetPackageCache? apcCache;
    static string? apcFrom;

    /// <summary>The packages the game loads with a power class (AssetPackageCache.bin: marvelgamecontent.&lt;Class&gt; →
    /// package names, lower case, no extension), or null when the cache doesn't list it.</summary>
    static List<string>? PowerPackages(string cooked, string defaultPath)
    {
        string f = Path.Combine(cooked, "AssetPackageCache.bin");
        if (!File.Exists(f)) return null;
        string cls = "marvelgamecontent." + defaultPath.Split('.')[^1]["default__".Length..];
        lock (typeof(AssetPackageCacheLock))
        {
            if (apcCache == null || apcFrom != f) { apcCache = Fx.AssetPackageCache.Parse(File.ReadAllBytes(f)); apcFrom = f; }
            var hit = apcCache.Packages.FirstOrDefault(p => p.Path.Equals(cls, StringComparison.OrdinalIgnoreCase));
            return hit.Packages;
        }
    }
    sealed class AssetPackageCacheLock { }

    /// <summary>
    /// (0.60.430) What a borrowed part can't use on the target: the packages the game loads with the donor power but not
    /// with the target (Emma Frost's sprint: uc__powershared_sprint_sf and _y_sf; Taskmaster's flight loads only
    /// uc__powershared_travelpower_sf and its own), opened from the game's originals, with the classes they define. A part
    /// whose class or template lives only there comes out null in game: CreateUnrealPower reads a null component's class
    /// (access violation; the decompiled 1.52 client, Kurt's crash 2026-11-07). Null when the cache doesn't list both.
    /// </summary>
    sealed record Unloaded(List<(string Name, Package Pkg)> Packages, HashSet<string> Classes)
    {
        public (string Name, Package Pkg, int Index)? Holder(string path)
        {
            foreach (var (n, p) in Packages) { int i = Find(p, path); if (i >= 0) return (n, p, i); }
            return null;
        }
        public string? ClassHome(string cls) => Classes.Contains(cls)
            ? Packages.First(x => x.Pkg.Exports.Any(e => e.ObjectName.Equals(cls, StringComparison.OrdinalIgnoreCase) && x.Pkg.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase))).Name : null;
    }

    static Unloaded? UnloadedFor(string? cooked, string targetDefault, string donorDefault, string donorPackage)
    {
        if (cooked == null || PowerPackages(cooked, targetDefault) is not { } tl || PowerPackages(cooked, donorDefault) is not { } dl) return null;
        string own = Path.GetFileNameWithoutExtension(donorPackage);
        var list = new List<(string, Package)>();
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in dl.Where(n => !tl.Contains(n, StringComparer.OrdinalIgnoreCase) && !n.Equals(own, StringComparison.OrdinalIgnoreCase)))
        {
            string file = Directory.EnumerateFiles(cooked, n + ".upk").FirstOrDefault() ?? "";
            if (file.Length == 0) continue;
            string stock = StockFiles.For(cooked, Path.GetFileName(file));
            if (!File.Exists(stock)) continue;
            var p = Package.Open(stock);
            list.Add((Path.GetFileName(file), p));
            foreach (var e in p.Exports) if (p.ClassOf(e).Equals("Class", StringComparison.OrdinalIgnoreCase)) classes.Add(e.ObjectName);
        }
        return new Unloaded(list, classes);
    }

    /// <summary>An object reference's full path (an import's through its outers).</summary>
    static string RefPathOf(Package p, int r)
    {
        if (r > 0) return p.PathOf(p.Exports[r - 1]);
        var parts = new List<string>();
        for (int g = 0; r < 0 && -r <= p.Imports.Length && g < 32; g++)
        {
            var im = p.Imports[-r - 1]; parts.Insert(0, im.ObjectName); r = im.OuterIndex;
            if (r > 0) { parts.Insert(0, p.PathOf(p.Exports[r - 1])); break; }
        }
        return string.Join('.', parts);
    }

    /// <summary>
    /// (0.60.430) The borrowed component's templates, level by level, while the next one is in a package the game doesn't
    /// load with the target: each copied under the target's default and merged into the component (MergeTemplate), until its
    /// archetype is something the target has (default__powershared_travelpower.streaktrails, or a class default). The
    /// template copies take that archetype too.
    /// </summary>
    static Package Flatten(Package dst, int comp, string tdPath, string what, Unloaded un, List<string> log)
    {
        var copies = new List<string>();
        for (int level = 0; level < 8; level++)
        {
            int arch = BinaryPrimitives.ReadInt32LittleEndian(dst.Body.AsSpan(dst.ExportEntryStart[comp] + 20));
            if (arch >= 0) break;
            string path = RefPathOf(dst, arch);
            if (un.Holder(path) is not { } h) break;
            string leaf = path[(path.LastIndexOf('.') + 1)..], outer = path[..path.LastIndexOf('.')];
            string name = $"borrowedtemplate{(level == 0 ? "" : (level + 1).ToString())}_{leaf}";
            string copyPath = tdPath + "." + name;
            if (Find(dst, copyPath) < 0)
            {
                var tc = CrossMove.Quiet(() => ExportCopy.Copy(h.Pkg, h.Index, dst, [], name,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [outer] = tdPath }), out string said);
                if (tc == null) throw new InvalidDataException($"{what}: its template {path} (in {h.Name}) couldn't be copied");
                dst = Package.FromBytes(tc.Output);
            }
            int ti = Find(dst, copyPath);
            log.Add($"    {what}: its template {path} is in {h.Name}, which the game doesn't load with this power: its settings merged in");
            dst = MergeTemplate(dst, comp, ti, what, log);
            copies.Add(copyPath);
        }
        // the template copies stand on the same archetype (theirs pointed into the packages that aren't loaded)
        int final = BinaryPrimitives.ReadInt32LittleEndian(dst.Body.AsSpan(dst.ExportEntryStart[comp] + 20));
        if (copies.Count > 0)
        {
            byte[] body = dst.Body;
            foreach (string c in copies) BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(dst.ExportEntryStart[Find(dst, c)] + 20), final);
            byte[] output = PackageRebuilder.Rebuild(dst, new Dictionary<int, Func<long, byte[]>>(), [], out var written);
            var problems = PackageRebuilder.Verify(dst, output, [], [], written);
            if (problems.Count > 0) throw new InvalidDataException($"{what}: its templates' archetypes: " + string.Join("; ", problems.Take(3)));
            dst = Package.FromBytes(output);
        }
        if (final < 0 && un.Holder(RefPathOf(dst, final)) is { } still) throw new InvalidDataException($"{what}: still needs {RefPathOf(dst, final)} from {still.Name}");
        return dst;
    }

    /// <summary>
    /// (0.60.429) A borrowed component whose template was copied in: the template's settings merged into it (those it
    /// doesn't set itself, by name and array index) and the template's own archetype taken, so it stands alone like any
    /// component; the template copy gets its own name as its template name. Two components of one default with the same
    /// template name ("anim1": borrowed_anim1 pointing at its sibling borrowedtemplate_anim1) crashed the game when the
    /// power was made (CreateUnrealPower → array index out of range; Emma Frost's sprint on Taskmaster, Kurt 2026-11-07).
    /// </summary>
    static Package MergeTemplate(Package p, int comp, int tpl, string what, List<string> log)
    {
        byte[] c = p.ReadExportBytes(p.Exports[comp]).ToArray(), t = p.ReadExportBytes(p.Exports[tpl]).ToArray();
        var ct = TagWalker.Walk(p, c, 16);
        var tt = TagWalker.Walk(p, t, 16);
        if (ct == null || tt == null) throw new InvalidDataException($"{what}: its template's settings couldn't be read");
        var have = ct.Select(x => (x.Name.ToLowerInvariant(), BitConverter.ToInt32(c, x.Start + 20))).ToHashSet();
        var extra = tt.Where(x => !have.Contains((x.Name.ToLowerInvariant(), BitConverter.ToInt32(t, x.Start + 20)))).ToList();
        byte[] merged = [.. c.AsSpan(0, ct.NoneAt), .. extra.SelectMany(x => t[x.Start..x.End]), .. c.AsSpan(ct.NoneAt)];
        byte[] body = p.Body;
        byte[] t2 = (byte[])t.Clone();
        body.AsSpan(p.ExportEntryStart[tpl] + 12, 8).CopyTo(t2.AsSpan(4, 8));   // the template copy's template name = its own name
        int arch = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(p.ExportEntryStart[tpl] + 20));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(p.ExportEntryStart[comp] + 20), arch);   // the component: the template's archetype
        var rep = new Dictionary<int, Func<long, byte[]>> { [comp] = _ => merged, [tpl] = _ => t2 };
        byte[] output = PackageRebuilder.Rebuild(p, rep, [], out var written);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), [], written);
        if (problems.Count > 0) throw new InvalidDataException($"{what}: merging its template: " + string.Join("; ", problems.Take(3)));
        var back = Package.FromBytes(output);
        if (TagWalker.Walk(back, back.ReadExportBytes(back.Exports[comp]).ToArray(), 16)?.Count != ct.Count + extra.Count)
            throw new InvalidDataException($"{what}: the merged settings didn't read back");
        log.Add($"    {what}: {extra.Count} setting(s) from its template merged in; it stands alone (archetype {(arch > 0 ? back.PathOf(back.Exports[arch - 1]) : arch < 0 ? back.Imports[-arch - 1].ObjectName : "none")})");
        return back;
    }

    /// <summary>Which of Whole Power's parts a power component is: "anim" (PowerFxAnimation, its looping kind, physics weight),
    /// "prop" (PowerFxMeshAttachment: weapons and vehicles shown, hidden or spawned) or "effect" (particles, sounds, camera
    /// shakes, decals, beams, lights …).</summary>
    public static string PartOf(string cls) =>
        cls.Contains("Anim", StringComparison.OrdinalIgnoreCase) || cls.Equals("PowerFxPhysicsWeight", StringComparison.OrdinalIgnoreCase) ? "anim"
        : cls.Equals("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase) ? "prop" : "effect";

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

    /// <summary>Each component of a class default with its class, its archetype's full path (export or import chain) and what
    /// Template() makes of it (--component-templates). Read only.</summary>
    internal static List<string> TemplateReport(string package, string defaultPath)
    {
        var p = Package.Open(package);
        int d = Find(p, defaultPath);
        if (d < 0) return [$"{defaultPath} not found"];
        var lines = new List<string>();
        foreach (int r in Components(p, d, out _, out _))
        {
            if (r <= 0) continue;
            int arch = BinaryPrimitives.ReadInt32LittleEndian(p.Body.AsSpan(p.ExportEntryStart[r - 1] + 20));
            var parts = new List<string>();
            for (int a = arch, guard = 0; a != 0 && guard < 16; guard++)
            {
                if (a > 0) { parts.Insert(0, p.PathOf(p.Exports[a - 1]) + " (export)"); break; }
                var im = p.Imports[-a - 1]; parts.Insert(0, im.ObjectName); a = im.OuterIndex;
            }
            lines.Add($"{p.Exports[r - 1].ObjectName} ({p.ClassOf(p.Exports[r - 1])}): archetype {(arch == 0 ? "none" : string.Join(".", parts))} → template {Template(p, r - 1) ?? "none"}");
        }
        return lines;
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
