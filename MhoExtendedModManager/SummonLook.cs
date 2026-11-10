using System.Buffers.Binary;
using System.Numerics;
using AnimExportCli.Animation;
using MhoPackageModifier;
using AnimPackage = AnimExportCli.Packages.Package;

namespace MhoExtendedModManager;

/// <summary>
/// A summon's look from another summon (Kurt, 2026-11-05: summon-to-summon borrowing). The target summon's own package (its
/// class, GUID and settings: what it does stays the game's) with the donor's model copied in (CrossMove.Build: mesh,
/// materials, the component pointed at it), the target's sockets fitted onto that model (FitSockets), and the donor's
/// animations copied under the names the target plays (AnimSwap.Build: a set of its own, appended to the component's
/// list), plus every donor animation under its own name. Objects keep their paths or get new ones, so other packages sharing
/// the target's model are unaffected.
/// Confirmed in game 2026-11-05: Hawkeye's StarkTech Claymore Turret as Squirrel Girl's squirrel (telemetry ride64: the
/// turret played Spawn, Idle and Idle_Combat from the squirrel's set). Not every summon a power names is a model: Doctor
/// Doom's Unthinkable summons an invisible placeholder (vfxloot_null, mat_invisible) and draws its orb with a hotspot's
/// effects, so a squirrel there never showed (ride63): such summons are left out (<see cref="Of"/>).
/// </summary>
static class SummonLook
{
    /// <summary>A summon a power puts in the world as a model: its class, its own package, its mesh (file, export, name) and
    /// its animation set (file, export) with the set's animations (name, export).</summary>
    public sealed record Summon(string Class, string File, string MeshFile, int MeshExport, string MeshName, string? SetFile, int SetExport,
        IReadOnlyList<(string Name, int Export)> Anims);

    /// <summary>
    /// The summons <paramref name="power"/> puts in the world as models, as the 3D preview finds them (PowerEffects: the power's
    /// summon entities, the costume's overrides, talent-only summons left out), each with its own package. Left out (with a
    /// note): a summon whose package isn't found and one whose model draws nothing (all its materials invisible).
    /// </summary>
    public static List<Summon> Of(Fx.GameData db, Fx.FxGame game, string power, string? hero, List<string>? notes = null)
    {
        var res = new List<Summon>();
        Fx.PowerEffects fx;
        try { fx = Fx.PowerEffects.For(game, db, power, hero); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException) { return res; }
        foreach (var a in fx.Actors.Where(a => a.Kind == "summon").DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            string def = $"marvelgamecontent.default__{a.Name}".ToLowerInvariant();
            string? file = game.PackagesOf(a.Name).Select(game.FileFor).OfType<string>().FirstOrDefault(f => HasExport(f, def));
            if (file == null) { notes?.Add($"{a.Name}: its package isn't found"); continue; }
            Package mp;
            try { mp = Package.Open(a.MeshFile); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { continue; }
            if (Invisible(mp, a.MeshFile, a.MeshExport)) { notes?.Add($"{a.Name}: its model draws nothing (its look is effects)"); continue; }
            // its animation set: the one it plays (the last in its list), unless that's a placeholder set (biped_lib.debug_as:
            // temp, placeholderanim_01 … is listed last by Squirrel Girl's Triple Squirrel); then the set with the most real
            // animations in its mesh's package or its own
            var sets = new List<(string File, int Export, List<(string, int)> Anims)>();
            if (a.SetFile != null) sets.Add((a.SetFile, a.SetExport, SetAnims(a.SetFile, a.SetExport)));
            foreach (string f in new[] { a.MeshFile, file }.Distinct(StringComparer.OrdinalIgnoreCase))
                try
                {
                    var sp = Package.Open(f);
                    for (int i = 0; i < sp.Exports.Length; i++)
                        if (sp.ClassOf(sp.Exports[i]).Equals("AnimSet", StringComparison.OrdinalIgnoreCase) && !sets.Any(x => x.File.Equals(f, StringComparison.OrdinalIgnoreCase) && x.Export == i))
                            sets.Add((f, i, SetAnims(f, i)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
            static int Real(List<(string Name, int)> l) => l.Count(x => !x.Name.StartsWith("placeholder", StringComparison.OrdinalIgnoreCase) && !x.Name.Equals("temp", StringComparison.OrdinalIgnoreCase));
            var best = sets.Count == 0 ? default : Real(sets[0].Anims) > 0 ? sets[0] : sets.OrderByDescending(x => Real(x.Anims)).First();
            res.Add(new Summon(a.Name, file, a.MeshFile, a.MeshExport, mp.Exports[a.MeshExport].ObjectName, best.File, best.File == null ? -1 : best.Export, best.Anims ?? []));
        }
        return res;
    }

    static bool HasExport(string file, string path)
    {
        try { var p = Package.Open(file); return p.Exports.Any(e => p.PathOf(e).Equals(path, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return false; }
    }

    /// <summary>A mesh whose every material is an invisible one (Doom's orb placeholder: vfx_frentonspackage.mat_invisible).</summary>
    static bool Invisible(Package mp, string file, int export)
    {
        List<int> refs;
        try { refs = ModMeshes.MaterialRefs(AnimPackage.Read(mp.RawFile), export); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { return false; }
        string Name(int r) => r > 0 && r <= mp.Exports.Length ? mp.Exports[r - 1].ObjectName : r < 0 && -r <= mp.Imports.Length ? mp.Imports[-r - 1].ObjectName : "";
        return refs.Count > 0 && refs.All(r => Name(r).Contains("invisible", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An AnimSet's animations (name, sequence export).</summary>
    static List<(string, int)> SetAnims(string file, int export)
    {
        var list = new List<(string, int)>();
        try
        {
            var ap = AnimPackage.Read(Package.Open(file).RawFile);
            if (AnimObjectReader.FindAnimSets(ap).FirstOrDefault(s => s.ExportIndex == export) is not { } info) return list;
            foreach (var s in info.Sequences.Where(s => s.IsExport))
                try { list.Add((AnimObjectReader.GetSequenceDisplayName(ap, s.ExportIndex), s.ExportIndex)); }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { }
        return list;
    }

    /// <summary>
    /// Which donor animation plays under each of the target's names the donor doesn't have (the donor's own names come along
    /// too): by what the name is for. Spawn → spawn, despawn / death → despawn, combat idle → combat idle, idle and fidgets →
    /// idle, movement → run, attacks (shoot, fire, absattack…) → the donor's attack; anything else → idle. Additive aim
    /// animations (add_left180 …: a turret's aiming) are left alone: a whole animation there would add the body twice.
    /// </summary>
    public static Dictionary<string, string> AutoSlots(IEnumerable<string> target, IReadOnlyCollection<string> donor)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? First(Func<string, bool> t) => donor.FirstOrDefault(t);
        string? Pick(params Func<string, bool>[] tests) => tests.Select(First).FirstOrDefault(x => x != null);
        static bool Is(string n, string w) => n.Equals(w, StringComparison.OrdinalIgnoreCase);
        static bool Starts(string n, string w) => n.StartsWith(w, StringComparison.OrdinalIgnoreCase);
        static bool Has(string n, string w) => n.Contains(w, StringComparison.OrdinalIgnoreCase);
        static bool Attack(string n) => Starts(n, "absattack") || Has(n, "attack") || Has(n, "shoot") || Has(n, "fire") || Has(n, "shot") || Has(n, "cast");
        foreach (string t in target.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (donor.Contains(t, StringComparer.OrdinalIgnoreCase) || Starts(t, "add_")) continue;
            string? from =
                Has(t, "despawn") || Has(t, "death") || Starts(t, "die") ? Pick(n => Is(n, "despawn"), n => Starts(n, "death"), n => Is(n, "idle")) :
                Starts(t, "spawn") ? Pick(n => Is(n, "spawn"), n => Starts(n, "spawn"), n => Is(n, "idle")) :
                Starts(t, "idle_combat") ? Pick(n => Is(n, "idle_combat"), n => Is(n, "idle")) :
                Starts(t, "idle") || Starts(t, "fidget") ? Pick(n => Is(n, "idle"), n => Is(n, "idle_combat")) :
                Starts(t, "movement_") || Has(t, "walk") || Has(t, "run") ? Pick(n => Is(n, "movement_run"), n => Starts(n, "movement_run"), n => Has(n, "walk"), n => Is(n, "idle")) :
                Attack(t) ? Pick(n => Starts(n, "absattack"), n => Attack(n), n => Is(n, "idle_combat")) :
                Pick(n => Is(n, "idle"), n => Is(n, "idle_combat"));
            if (from != null) map[t] = from;
        }
        return map;
    }

    /// <summary>The target summon's package with the donor summon's look, verified (see the class summary); the slots are
    /// <see cref="AutoSlots"/>'s unless given. Throws InvalidDataException with the reason.</summary>
    public static byte[] Build(string targetPackage, Summon target, Summon donor, List<string> log, IReadOnlyDictionary<string, string>? slots = null)
    {
        if (donor.SetFile == null || donor.Anims.Count == 0) throw new InvalidDataException($"{donor.Class} has no animations to take");
        return Build(targetPackage, target.Class, donor.MeshFile, donor.MeshName, donor.SetFile, donor.SetExport, donor.Anims,
            slots ?? AutoSlots(target.Anims.Select(a => a.Name), donor.Anims.Select(a => a.Name).ToList()), log);
    }

    /// <summary>The same from file and export names (--summon-look): the donor's animation set is the first AnimSet in its package.</summary>
    public static byte[] Build(string targetPackage, string targetClass, string donorPackage, string donorMesh, IReadOnlyDictionary<string, string> slots, List<string> log)
    {
        var dp = Package.Open(donorPackage);
        int set = Array.FindIndex(dp.Exports, e => dp.ClassOf(e).Equals("AnimSet", StringComparison.OrdinalIgnoreCase));
        if (set < 0) throw new InvalidDataException($"{Path.GetFileName(donorPackage)} has no animation set");
        return Build(targetPackage, targetClass, donorPackage, donorMesh, donorPackage, set, SetAnims(donorPackage, set), slots, log);
    }

    static byte[] Build(string targetPackage, string targetClass, string donorMeshFile, string donorMesh, string donorSetFile, int donorSetExport,
        IReadOnlyList<(string Name, int Export)> donorAnims, IReadOnlyDictionary<string, string> slots, List<string> log)
    {
        // the target's own mesh and the donor's own socket names, before the copy (the fit needs them)
        string? stockMesh = ComponentMesh(Package.Open(targetPackage), targetClass);
        var donorSockets = MeshSocketNames(Package.Open(donorMeshFile), donorMesh);
        byte[] withMesh = CrossMove.Build(donorMeshFile, donorMesh, targetPackage, targetClass, null, log, sounds: false);
        if (stockMesh != null) withMesh = FitSockets(withMesh, targetClass, stockMesh, donorSockets, log);
        string temp = Path.Combine(Path.GetTempPath(), $"summonlook_{Guid.NewGuid():N}_{Path.GetFileName(targetPackage)}");
        File.WriteAllBytes(temp, withMesh);
        try
        {
            if (donorAnims.Count == 0) throw new InvalidDataException($"{Path.GetFileName(donorSetFile)}: its animation set has no animations");
            var byName = donorAnims.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Export, StringComparer.OrdinalIgnoreCase);
            var swaps = new List<AnimSwap.Swap>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slot, from) in slots)
            {
                if (!byName.TryGetValue(from, out int seq)) { log.Add($"anims: {slot} ← {from}: the donor has no such animation"); continue; }
                swaps.Add(new AnimSwap.Swap(slot, donorSetFile, donorSetExport, seq));
                taken.Add(slot);
            }
            // the donor's own names too (the target may play names of its own kind: idle, spawn, movement …)
            foreach (var (name, seq) in byName.Where(kv => !taken.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value)))
                swaps.Add(new AnimSwap.Swap(name, donorSetFile, donorSetExport, seq));
            log.Add($"anims: {string.Join(", ", slots.Select(kv => $"{kv.Key} ← {kv.Value}"))}; and the donor's own {byName.Count}");
            return AnimSwap.Build(temp, targetClass, swaps, log);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    /// <summary>
    /// A summon as another character, whole (a user, 2026-11-09: "replace a Loki summon or a Magik demon with a dinosaur, something
    /// that moves into melee and whacks", without mimicking the summon's skeleton): the summon's own package (its class, GUID
    /// and settings: what it does stays the game's) with the character's model and skeleton (CrossMove.Build, the summon's
    /// sockets fitted onto it) and, under every animation name the summon plays, the character's animation of that name, else
    /// the one <see cref="AutoSlots"/> picks (spawn, idle, combat idle, run, attacks, death). Any character package: a hero or
    /// costume (its animations from its own list or its hero's), a team-up, an enemy, a pet. Only those animations are copied
    /// (a hero has hundreds). <paramref name="fileFor"/> finds the packages animation sets come from.
    /// </summary>
    public static byte[] BuildWhole(string targetPackage, string targetFile, string sourcePackage, string sourceFile, Func<string, (string Path, bool FromMod)?> fileFor, List<string> log, string? cooked = null, bool keepLook = true, float size = 1)
    {
        var tp = Package.Open(targetPackage);
        int tdef = ModMeshes.CharacterDefault(tp);
        if (tdef < 0) throw new InvalidDataException($"{targetFile} has no character class default");
        string targetClass = tp.ClassOf(tp.Exports[tdef]);
        var sp = Package.Open(sourcePackage);
        int sdef = ModMeshes.CharacterDefault(sp);
        if (sdef < 0) throw new InvalidDataException($"{sourceFile} has no character class default");
        string sourceClass = sp.ClassOf(sp.Exports[sdef]);
        string meshPath = ComponentMesh(sp, sourceClass) ?? throw new InvalidDataException($"{sourceFile}: its character shows no model");
        string meshName = meshPath[(meshPath.LastIndexOf('.') + 1)..];
        // (its names from the shared always-loaded sets, Startup's debug placeholders, are left as they are)
        var targetAnims = CostumeAnims.Read(targetPackage, targetFile, fileFor)?.Anims.Where(a => a.From.Kind != CostumeAnims.Source.Shared).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        // a summon whose class extends another package's (Magik's spitter demons: the blue lesser demon's) plays that one's list
        // (up the chain: the spitter extends the blue lesser demon, which extends another)
        List<string>? inherited = null;   // (the list such a summon plays, imported as the start of its own: AnimSwap)
        for (string at = targetPackage, guard = ""; targetAnims.Count == 0 && cooked != null && guard.Length < 4 && PowerColorBuild.ParentPackage(at, cooked) is string parent && fileFor(parent) is { } pp; at = pp.Path, guard += "x")
        {
            var pca = CostumeAnims.Read(pp.Path, parent, fileFor);
            targetAnims = pca?.Anims.Where(a => a.From.Kind != CostumeAnims.Source.Shared).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            if (targetAnims.Count > 0) inherited = pca!.Sets.OrderBy(x => x.Order).Select(x => x.Path).ToList();
            log.Add($"{targetFile} extends {parent}: {(targetAnims.Count > 0 ? $"it plays that one's animations ({targetAnims.Count})" : "no animation list there either")}");
        }
        var donor = CostumeAnims.Read(sourcePackage, sourceFile, fileFor) ?? throw new InvalidDataException($"{sourceFile}: its animations aren't found");
        var byName = donor.Anims.Where(a => a.From.File != null && a.Export >= 0).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (byName.Count == 0) throw new InvalidDataException($"{sourceFile} has no animations to take");
        if (targetAnims.Count == 0) throw new InvalidDataException($"{targetFile}: the animations it plays aren't found");
        // (the shared always-loaded sets, Startup's debug placeholders: the summon plays those already)
        foreach (var k in byName.Where(kv => kv.Value.From.Kind == CostumeAnims.Source.Shared).Select(kv => kv.Key).ToList()) byName.Remove(k);
        var mapped = WholeSlots(targetAnims, byName.Keys.ToList());
        // the summon's sockets, before the copy (the fit needs them), then the character's model in
        string? stockMesh = ComponentMesh(tp, targetClass);
        var donorSockets = MeshSocketNames(sp, meshName);
        // its size: the character's own (its component's Scale) times Size in Game (Kurt, 2026-11-10)
        float scale = PetMove.ScaleOf(sourcePackage, sourceClass.ToLowerInvariant()) * size;
        byte[] withMesh = CrossMove.Build(sourcePackage, meshName, targetPackage, targetClass, null, log, sounds: false, scale: Math.Abs(scale - 1) > 1e-4 || ComponentScaled(tp, targetClass) ? scale : null);
        if (stockMesh != null) withMesh = FitSockets(withMesh, targetClass, stockMesh, donorSockets, log);
        string temp = Path.Combine(Path.GetTempPath(), $"summonwhole_{Guid.NewGuid():N}_{targetFile}");
        File.WriteAllBytes(temp, withMesh);
        try
        {
            var swaps = new List<AnimSwap.Swap>();
            var lines = new List<string>();
            foreach (string slot in targetAnims)
            {
                if (slot.StartsWith("add_", StringComparison.OrdinalIgnoreCase)) continue;   // (additive aims: a whole animation would add the body twice)
                string? from = byName.ContainsKey(slot) ? slot : mapped.GetValueOrDefault(slot);
                if (from == null || !byName.TryGetValue(from, out var a)) { lines.Add($"{slot}: kept"); continue; }
                swaps.Add(new AnimSwap.Swap(slot, a.From.File!, a.From.Export, a.Export));
                lines.Add(from.Equals(slot, StringComparison.OrdinalIgnoreCase) ? slot : $"{slot} ← {from}");
            }
            if (swaps.Count == 0) throw new InvalidDataException($"none of {sourceFile}'s animations fit what {targetFile} plays");
            log.Add($"anims ({swaps.Count} of {targetAnims.Count}): {string.Join(", ", lines)}");
            byte[] built = AnimSwap.Build(temp, targetClass, swaps, log, inherited);
            // the summon's random colors (Kurt, 2026-11-10: the raptors on Magik's lesser demons came out red, green and purple,
            // the demons' variation materials on the raptor's first material): an empty list of its own, so it always shows the
            // character's own material, whatever list it had or inherited
            File.WriteAllBytes(temp, built);
            if (MaterialVariations.ReadClass(temp) is { } cv && (cv.Materials is { Count: > 0 } || cv.Super != null))
            {
                built = MaterialVariations.SetList(temp, []);
                log.Add("variations: none (the summon's random materials were for its own model): the character's own material");
            }
            // the summon's own glow and effects off (Kurt, 2026-11-10: Venom as Magik's spitter demons came out see-through blue
            // with flames: the blue demon's effect attachment and its component's material, both inherited)
            // its material override (the blue demon's see-through material) is made for the summon's own model and its UV
            // layout: on the character it drew the demon's texture (Kurt, 2026-11-10, Venom): always taken out
            built = ClearMaterials(built, targetClass, log);
            if (!keepLook)
            {
                string? home = cooked == null ? null : AttachmentHome(targetPackage, cooked);
                built = OwnFxBorrow.Rebuild(built, targetFile, home, ModMeshes.AttachmentClasses(targetPackage), null, null, true, log);
            }
            return built;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    /// <summary>
    /// <see cref="AutoSlots"/> with attacks matched more closely, for a whole character (2026-11-10: Magik's lesser demon as the
    /// Raptor put the raptor's leap end under all seven of the demon's attacks): an attack takes the source's attack sharing a
    /// word (bite → defaultattack_bite; pounce / jump → leap), in the same phase (_start / _loop / _end); else the source's
    /// attacks in turn, its basic (default) attacks first, so the summon doesn't repeat one animation.
    /// </summary>
    public static Dictionary<string, string> WholeSlots(IReadOnlyList<string> target, IReadOnlyList<string> donor)
    {
        var map = AutoSlots(target, donor.ToList());
        static bool Attack(string n) => n.StartsWith("absattack", StringComparison.OrdinalIgnoreCase) || n.Contains("attack", StringComparison.OrdinalIgnoreCase)
            || n.Contains("shoot", StringComparison.OrdinalIgnoreCase) || n.Contains("shot", StringComparison.OrdinalIgnoreCase) || n.Contains("fire", StringComparison.OrdinalIgnoreCase);
        static string Phase(string n) => n.EndsWith("_start", StringComparison.OrdinalIgnoreCase) ? "start" : n.EndsWith("_loop", StringComparison.OrdinalIgnoreCase) ? "loop"
            : System.Text.RegularExpressions.Regex.IsMatch(n, "_end\\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "end" : "";
        static HashSet<string> Words(string n)
        {
            var w = n.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x is not ("absattack" or "defaultattack" or "classattack" or "attack" or "start" or "loop" or "end" or "cast") && !x.All(char.IsDigit))
                .Select(x => x.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')).Where(x => x.Length > 2).ToHashSet();
            if (w.Overlaps(["pounce", "jump", "leap", "lunge"])) w.UnionWith(["pounce", "jump", "leap", "lunge"]);
            if (w.Overlaps(["claw", "slash", "swipe", "scratch"])) w.UnionWith(["claw", "slash", "swipe", "scratch"]);
            if (w.Overlaps(["bite", "fangs", "fang", "chomp", "devour", "eat"])) w.UnionWith(["bite", "fangs", "fang", "chomp", "devour", "eat"]);
            return w;
        }
        static bool Ranged(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, "shoot|shot|blast|web|beam|throw|fire|missile|bolt|projectile|gun|ranged", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static bool Melee(string n) => System.Text.RegularExpressions.Regex.IsMatch(n, "bite|claw|slash|swipe|punch|kick|smash|slam|strike|stab|melee|chomp|hit", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // a spawn: one that enters (the Raptor's spawn_leapingin) before a hatching (spawn_egg)
        var spawns = donor.Where(n => n.StartsWith("spawn", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Contains("egg", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(n => n.Equals("spawn", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        if (spawns.Count > 0)
            foreach (string t in target.Where(t => t.StartsWith("spawn", StringComparison.OrdinalIgnoreCase) && !donor.Contains(t, StringComparer.OrdinalIgnoreCase))) map[t] = spawns[0];
        var attacks = donor.Where(Attack).OrderBy(n => n.StartsWith("defaultattack", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (attacks.Count == 0) return map;
        int turn = 0;
        foreach (string t in target.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Attack(t) || donor.Contains(t, StringComparer.OrdinalIgnoreCase) || t.StartsWith("add_", StringComparison.OrdinalIgnoreCase)) continue;
            var tw = Words(t);
            string ph = Phase(t);
            var same = attacks.Where(a => Words(a).Overlaps(tw) || tw.Any(w => a.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();   // (fangs inside carnivorousfangs)
            string? pick = same.FirstOrDefault(a => Phase(a) == ph) ?? (ph == "" ? same.FirstOrDefault(a => Phase(a) is "" or "end") : null) ?? same.FirstOrDefault();
            if (pick == null)
            {
                // in turn, a whole attack (no phase) for one without phases, else the same phase
                var pool = attacks.Where(a => ph == "" ? Phase(a) is "" or "end" : Phase(a) == ph).ToList();
                // a melee attack (bite, claw …) from the source's melee ones when it has any (not Venom's web shoot)
                if (Melee(t) && pool.Where(a => !Ranged(a)).ToList() is { Count: > 0 } close) pool = close;
                if (pool.Count == 0) pool = attacks;
                pick = pool[turn++ % pool.Count];
            }
            map[t] = pick;
        }
        return map;
    }

    /// <summary>
    /// A summon class that extends the package a whole character was built into (Kurt, 2026-11-10: Ant-Man on the Big Fat Demon's
    /// package stayed small: Magik's summon, MarvelAgent_MagikLimboBigFatDemon, is a class of its own extending the enemy's, and
    /// its own component sets Scale 0.75, which wins over the build's). Its own package gets what the build gave the model's:
    /// the size (<paramref name="scale"/>: the character's own times Size in Game, written on its component when it has one),
    /// no material override (the blue demon's see-through material), no random materials, and without
    /// <paramref name="keepLook"/> none of its effect-only attachments. Null when nothing in it needed changing.
    /// </summary>
    public static byte[]? WholeChild(string childPath, string childFile, string childClass, float scale, bool keepLook, string? cooked, List<string> log)
    {
        string cls = childClass.ToLowerInvariant();
        byte[] start = File.ReadAllBytes(childPath), b = start;
        var p = Package.FromBytes(b);
        int c = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals($"marvelgamecontent.default__{cls}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
        if (c >= 0)
        {
            // its size (set, or added before None)
            byte[] d = p.ReadExportBytes(p.Exports[c]).ToArray();
            var tags = TagWalker.Walk(p, d, 16) ?? throw new InvalidDataException($"{childFile}: its model component doesn't read");
            var addNames = new List<string>();
            int NameIdx(string n)
            {
                int i = Array.FindIndex(p.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
                if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
                return p.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            }
            byte[] nd;
            if (tags.FirstOrDefault(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } st)
            {
                nd = d.ToArray();
                BinaryPrimitives.WriteSingleLittleEndian(nd.AsSpan(st.ValueAt), scale);
            }
            else
            {
                var tag = new byte[28];
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("Scale"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("FloatProperty"));
                BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
                BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(24), scale);
                nd = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
            }
            var replace = new Dictionary<int, Func<long, byte[]>> { [c] = _ => nd };
            b = PackageRebuilder.Rebuild(p, replace, [], out var written, addNames);
            var problems = PackageRebuilder.Verify(p, b, replace.Keys.ToList(), [], written, addNames);
            if (problems.Count > 0) throw new InvalidDataException($"{childFile}'s model component didn't verify: " + string.Join("; ", problems.Take(3)));
            log.Add($"{childFile}: size {scale:0.##}");
            b = ClearMaterials(b, cls, log);
        }
        string temp = Path.Combine(Path.GetTempPath(), $"summonchild_{Guid.NewGuid():N}_{childFile}");
        try
        {
            File.WriteAllBytes(temp, b);
            if (MaterialVariations.ReadClass(temp) is { } cv && cv.Materials is { Count: > 0 }) { b = MaterialVariations.SetList(temp, []); log.Add($"{childFile}: no random materials"); }
            if (!keepLook && cooked != null)
            {
                File.WriteAllBytes(temp, b);
                b = OwnFxBorrow.Rebuild(b, childFile, AttachmentHome(temp, cooked), ModMeshes.AttachmentClasses(childPath), null, null, true, log);
            }
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
        return b.AsSpan().SequenceEqual(start) ? null : b;
    }

    /// <summary>Whether a class's model component sets a Scale (a smaller summon: then a size is always written).</summary>
    static bool ComponentScaled(Package p, string cls)
    {
        int c = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals($"marvelgamecontent.default__{cls}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
        return c >= 0 && TagWalker.Walk(p, p.ReadExportBytes(p.Exports[c]).ToArray(), 16)?.Any(t => t.Name.Equals("Scale", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>The package whose class's attachment list a summon plays: its own when it has a list, else up its class chain
    /// (the spitter demons: the blue lesser demon's); null when none has one.</summary>
    static string? AttachmentHome(string packagePath, string cooked)
    {
        string at = packagePath;
        for (int guard = 0; guard < 5; guard++)
        {
            if (ModMeshes.AttachmentClasses(at) != null) return at;
            if (PowerColorBuild.ParentPackage(at, cooked) is not string parent || StockFiles.For(cooked, parent) is not { } pp || !File.Exists(pp)) return null;
            at = pp;
        }
        return null;
    }

    /// <summary>The class's model component with an empty material override of its own (its own or inherited Materials list
    /// replaced the model's materials: the blue demon's see-through material), so the model's own materials show.</summary>
    static byte[] ClearMaterials(byte[] package, string cls, List<string> log)
    {
        var pkg = Package.FromBytes(package);
        int c = Array.FindIndex(pkg.Exports, e => pkg.PathOf(e).Equals($"marvelgamecontent.default__{cls}.initialskeletalmesh", StringComparison.OrdinalIgnoreCase));
        if (c < 0) return package;
        byte[] d = pkg.ReadExportBytes(pkg.Exports[c]).ToArray();
        var tags = TagWalker.Walk(pkg, d, 16) ?? throw new InvalidDataException("the summon's model component doesn't read");
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(pkg.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return pkg.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        byte[] nd;
        if (tags.FirstOrDefault(t => t.Name.Equals("Materials", StringComparison.OrdinalIgnoreCase)) is { } mt)
        {
            nd = [.. d.AsSpan(0, mt.ValueAt), 0, 0, 0, 0, .. d.AsSpan(mt.End)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(mt.Start + 16), 4);
        }
        else
        {
            var tag = new byte[28];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), NameIdx("Materials"));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), NameIdx("ArrayProperty"));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 4);
            nd = [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        }
        var replace = new Dictionary<int, Func<long, byte[]>> { [c] = _ => nd };
        byte[] output = PackageRebuilder.Rebuild(pkg, replace, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(pkg, output, replace.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the summon's model component didn't verify: " + string.Join("; ", problems.Take(3)));
        log.Add("look: the summon's material override taken out (the character's own materials show)");
        return output;
    }

    /// <summary>The path of the mesh a class default's initialskeletalmesh component shows, else null.</summary>
    static string? ComponentMesh(Package p, string cls)
    {
        string cp = $"marvelgamecontent.default__{cls}.initialskeletalmesh";
        int ci = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(cp, StringComparison.OrdinalIgnoreCase));
        if (ci < 0) return null;
        byte[] d = p.ReadExportBytes(p.Exports[ci]).ToArray();
        if (TagWalker.Walk(p, d, 16)?.FirstOrDefault(t => t.Name.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is not { } st) return null;
        int r = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(st.ValueAt));
        return r > 0 ? p.PathOf(p.Exports[r - 1]) : null;
    }

    sealed record Sock(int Export, string Name, string Bone, Vector3 Loc, int[] Rot, TagWalker.Tag? LocTag, TagWalker.Tag BoneTag, byte[] Data);

    static List<Sock> Sockets(Package p, int mesh)
    {
        var list = new List<Sock>();
        byte[] md = p.ReadExportBytes(p.Exports[mesh]).ToArray();
        if (TagWalker.Walk(p, md, 4)?.FirstOrDefault(t => t.Name.Equals("Sockets", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase)) is not { } arr) return list;
        int n = BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(arr.ValueAt));
        for (int k = 0; k < n; k++)
        {
            int r = BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(arr.ValueAt + 4 + 4 * k));
            if (r <= 0) continue;
            byte[] sd = p.ReadExportBytes(p.Exports[r - 1]).ToArray();
            var props = TagWalker.Walk(p, sd, 4);
            if (props == null) continue;
            var sn = props.FirstOrDefault(t => t.Name.Equals("SocketName", StringComparison.OrdinalIgnoreCase));
            var bn = props.FirstOrDefault(t => t.Name.Equals("BoneName", StringComparison.OrdinalIgnoreCase));
            if (sn == null || bn == null) continue;
            var lt = props.FirstOrDefault(t => t.Name.Equals("RelativeLocation", StringComparison.OrdinalIgnoreCase) && t.Size == 12);
            var rt = props.FirstOrDefault(t => t.Name.Equals("RelativeRotation", StringComparison.OrdinalIgnoreCase) && t.Size == 12);
            Vector3 loc = lt == null ? Vector3.Zero : new(BitConverter.ToSingle(sd, lt.ValueAt), BitConverter.ToSingle(sd, lt.ValueAt + 4), BitConverter.ToSingle(sd, lt.ValueAt + 8));
            int[] rot = rt == null ? [0, 0, 0] : [BitConverter.ToInt32(sd, rt.ValueAt), BitConverter.ToInt32(sd, rt.ValueAt + 4), BitConverter.ToInt32(sd, rt.ValueAt + 8)];
            list.Add(new Sock(r - 1, TagWalker.NameAt(p, sd, sn.ValueAt), TagWalker.NameAt(p, sd, bn.ValueAt), loc, rot, lt, bn, sd));
        }
        return list;
    }

    static HashSet<string> MeshSocketNames(Package p, string meshName)
    {
        int m = Array.FindIndex(p.Exports, e => e.ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        return m < 0 ? [] : Sockets(p, m).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Each bone's rest matrix in model space.</summary>
    static Matrix4x4[] ModelMatrices(AnimExportCli.Meshes.SkeletalMesh m)
    {
        var mats = new Matrix4x4[m.Bones.Count];
        for (int i = 0; i < mats.Length; i++)
        {
            var b = m.Bones[i];
            var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(b.Orientation)) * Matrix4x4.CreateTranslation(b.Position);
            mats[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? local * mats[b.ParentIndex] : local;
        }
        return mats;
    }

    static float Height(AnimExportCli.Meshes.SkeletalMesh m) => m.HighestDetail is { Positions.Count: > 0 } l ? l.Positions.Max(p => p.Z) - l.Positions.Min(p => p.Z) : 0;

    /// <summary>
    /// The target's sockets on the donor's model, where its effects come from (the turret's arrows): each at the place it has
    /// on the target's model, scaled by the two models' heights. A socket the copy already took from the target (CrossMove:
    /// same bone) is moved to that place; one on a bone the donor's model lacks is added on its root bone there. The donor's
    /// own sockets are left as they are.
    /// </summary>
    static byte[] FitSockets(byte[] bytes, string targetClass, string stockMeshPath, IReadOnlySet<string> donorOwn, List<string> log)
    {
        var p = Package.FromBytes(bytes);
        int sm = Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(stockMeshPath, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        string? dmPath = ComponentMesh(p, targetClass);
        int dm = dmPath == null ? -1 : Array.FindIndex(p.Exports, e => p.PathOf(e).Equals(dmPath, StringComparison.OrdinalIgnoreCase) && p.ClassOf(e).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase));
        if (sm < 0 || dm < 0 || sm == dm) return bytes;
        var ap = AnimPackage.Read(p.RawFile);
        if (AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, sm) is not { } sMesh || AnimExportCli.Meshes.SkeletalMeshReader.TryRead(ap, dm) is not { } dMesh) return bytes;
        float sh = Height(sMesh), dh = Height(dMesh);
        float ratio = sh > 1 && dh > 1 ? Math.Clamp(dh / sh, 0.05f, 20f) : 1;
        var sMats = ModelMatrices(sMesh); var dMats = ModelMatrices(dMesh);
        int SBone(string n) => sMesh.Bones.ToList().FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        int DBone(string n) => dMesh.Bones.ToList().FindIndex(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        string dRoot = dMesh.Bones[0].Name;
        int rootName = Array.FindIndex(p.Names, x => x.Equals(dRoot, StringComparison.OrdinalIgnoreCase));
        int locName = Array.FindIndex(p.Names, x => x.Equals("RelativeLocation", StringComparison.OrdinalIgnoreCase));
        int structName = Array.FindIndex(p.Names, x => x.Equals("StructProperty", StringComparison.OrdinalIgnoreCase));
        int vectorName = Array.FindIndex(p.Names, x => x.Equals("Vector", StringComparison.OrdinalIgnoreCase));
        // a socket's data with its place (and bone) set: the RelativeLocation tag written, or added before None when it has none
        byte[]? Placed(Sock s, Vector3 loc, int? bone)
        {
            byte[] d = (byte[])s.Data.Clone();
            if (bone is int b) { BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(s.BoneTag.ValueAt), b); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(s.BoneTag.ValueAt + 4), 0); }
            if (s.LocTag != null)
            {
                BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(s.LocTag.ValueAt), loc.X); BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(s.LocTag.ValueAt + 4), loc.Y); BinaryPrimitives.WriteSingleLittleEndian(d.AsSpan(s.LocTag.ValueAt + 8), loc.Z);
                return d;
            }
            if (loc.LengthSquared() < 1e-6f) return d;
            if (locName < 0 || structName < 0 || vectorName < 0) return null;   // (no tag to add with: left where it is)
            if (TagWalker.Walk(p, d, 4) is not { } tags) return null;
            var tag = new byte[44];
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(0), locName);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(8), structName);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 12);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(24), vectorName);
            BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(32), loc.X); BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(36), loc.Y); BinaryPrimitives.WriteSingleLittleEndian(tag.AsSpan(40), loc.Z);
            return [.. d.AsSpan(0, tags.NoneAt), .. tag, .. d.AsSpan(tags.NoneAt)];
        }
        // where a target socket sits on the donor's model: its place on the target's model, scaled, in the donor bone's frame
        Vector3 Where(Sock s, int dBone)
        {
            int sb = SBone(s.Bone);
            var local = Fx.FxSockets.RotationMatrix(s.Rot[0], s.Rot[1], s.Rot[2]);
            local.M41 = s.Loc.X; local.M42 = s.Loc.Y; local.M43 = s.Loc.Z;
            Vector3 model = (sb >= 0 ? local * sMats[sb] : local).Translation * ratio;
            return Matrix4x4.Invert(dMats[dBone], out var inv) ? Vector3.Transform(model, inv) : model;
        }
        var stock = Sockets(p, sm).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var have = Sockets(p, dm);
        var rep = new Dictionary<int, Func<long, byte[]>>();
        int moved = 0;
        foreach (var h in have.Where(h => !donorOwn.Contains(h.Name) && stock.ContainsKey(h.Name)))
            if (DBone(h.Bone) is int db && db >= 0 && Placed(h, Where(stock[h.Name], db), null) is { } nd) { rep[h.Export] = _ => nd; moved++; }
        // the ones on bones the donor's model lacks: copies on its root bone
        var missing = stock.Values.Where(s => !have.Any(h => h.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var adds = new List<NewExport>();
        byte[]? meshData = null;
        if (missing.Count > 0 && rootName >= 0)
        {
            byte[] md = p.ReadExportBytes(p.Exports[dm]).ToArray();
            var arr = TagWalker.Walk(p, md, 4)?.FirstOrDefault(t => t.Name.Equals("Sockets", StringComparison.OrdinalIgnoreCase) && t.Type.Equals("ArrayProperty", StringComparison.OrdinalIgnoreCase));
            if (arr != null)
            {
                var refs = new List<byte>();
                foreach (var s in missing)
                {
                    if (Placed(s, Where(s, 0), rootName) is not { } nd) continue;
                    byte[] entry = p.Body.AsSpan(p.ExportEntryStart[s.Export], p.ExportEntryEnd[s.Export] - p.ExportEntryStart[s.Export]).ToArray();
                    BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(8), dm + 1);              // owner: the donor's model
                    BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(16), 21000 + adds.Count);  // a name number no socket of it uses
                    adds.Add(new NewExport(s.Export, 0, _ => nd) { Entry = entry });
                    refs.AddRange(BitConverter.GetBytes(p.Exports.Length + adds.Count));
                }
                if (adds.Count > 0)
                {
                    int count = BinaryPrimitives.ReadInt32LittleEndian(md.AsSpan(arr.ValueAt)), end = arr.ValueAt + 4 + 4 * count;
                    meshData = [.. md.AsSpan(0, end), .. refs, .. md.AsSpan(end)];
                    BinaryPrimitives.WriteInt32LittleEndian(meshData.AsSpan(arr.ValueAt), count + adds.Count);
                    BinaryPrimitives.WriteInt32LittleEndian(meshData.AsSpan(arr.Start + 16), arr.Size + 4 * adds.Count);
                    byte[] fm = meshData; rep[dm] = _ => fm;
                }
            }
        }
        if (rep.Count == 0 && adds.Count == 0) return bytes;
        byte[] output = PackageRebuilder.Rebuild(p, rep, adds, out var written);
        var problems = PackageRebuilder.Verify(p, output, rep.Keys.ToList(), adds, written);
        if (problems.Count > 0) throw new InvalidDataException("the summon's sockets: " + string.Join("; ", problems.Take(3)));
        log.Add($"sockets: the target's on the donor's model at its size ({ratio:0.##} of the target's height): {moved} moved, {adds.Count} added on its {dRoot}");
        return output;
    }
}
