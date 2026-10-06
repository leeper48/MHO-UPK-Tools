namespace MhoExtendedModManager.Fx;

// Ported from the MHO Hero Creator's PowerClosure.cs (2026-09-30; power effects in the 3D preview); kept in step with it.
/// <summary>
/// Everything a power sets off, with its art (Kurt, 2026-09-30: of Thor's powers on Worthy Captain America only Forked
/// Lightning showed all its effects; Hammer Strike's didn't). A power reaches other prototypes through P references: combo
/// and proc powers (Hammer Strike → BasicMeleeThunderclapCombo, BasicMeleeChainLightningCombo, LightningStrike …),
/// conditions (the Odinforce glow: OdinforceVisualAndDamageBuff, UnrealClass MarvelConditionEffect_ThorOdinforceVisualPartial),
/// summons, hotspots and missiles. Each has its own Unreal class (PowerUnrealClass for powers, UnrealClass for conditions and
/// entities), whose effects spawn at sockets and whose animations play on the caster. Build steps 2b (animations) and 2d
/// (sockets) look at all of them, not only the swapped-in power's own class. Checked with --power-closure.
/// </summary>
static class PowerClosure
{
    /// <param name="ViaEntity">Reached through an entity the power makes (a hotspot, a summon: Vision's Channeled Solar Beam
    /// summons VisionChanneledEnergyBeamArea, which applies SolarChanneledEnergyBeamEffect): what it applies lands on others,
    /// not the caster.</param>
    /// <param name="Hostile">Its prototype derives from a damage-over-time or debuff blueprint (Vision's Tri-Beam:
    /// DamageOverTimeRecurringCostPower): what it applies is put on the ones it hits. A plain ConditionPower (his Healing
    /// Nanites) is the caster's own buff.</param>
    /// <param name="Generic">Its nearest blueprint is the plain ConditionPower (buffs and hits alike: Healing Nanites, Green
    /// Goblin's laser impact): the effect's own name decides.</param>
    public sealed record Art(string Prototype, string Class, bool IsPower, bool ViaEntity = false, bool Hostile = false, bool Generic = false)
    {
        /// <summary>Steps from the power (0: itself, 1: named by it …).</summary>
        public int Depth { get; init; }
    }

    /// <summary>
    /// Who a condition lands on, from its blueprint lineage (the census of all 66 heroes, 2026-10-06): self-buff blueprints
    /// (damage shields, summon buffs, revives, heals over time, resistance changes, invulnerability, dashes) are the caster's;
    /// the plain ConditionPower (or none) is undecided; every other condition blueprint (stun, damage over time, slow, bleed,
    /// knockup, immobilize, freeze, weaken, vulnerability, chain / dual / summon / target-restricted conditions …) is put on
    /// the ones it hits.
    /// </summary>
    public static (bool Hostile, bool Generic) Target(GameData db, ulong id)
    {
        string lin = Lineage(db, id);
        string first = lin.Split(" < ")[0];
        if (first.Length == 0 || first.Equals("ConditionPower", StringComparison.OrdinalIgnoreCase)) return (false, true);
        foreach (var self in new[] { "Shield", "Buff", "Revive", "Restore", "HoT", "Heal", "Resistance", "Immune", "Invulnerab", "Dash", "Phasing", "Stealth", "Haste" })
            if (first.Contains(self, StringComparison.OrdinalIgnoreCase)) return (false, false);
        return (first.Contains("Condition", StringComparison.OrdinalIgnoreCase) || first.Contains("Power", StringComparison.OrdinalIgnoreCase) || IsHostile(db, id), false);
    }

    /// <summary>A plain condition's effect that looks like a hit or a debuff (by its own name).</summary>
    public static bool HitLike(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "(hit|impact|slow|debuff|stun|bleed|poison|burn|freez|knock|daze)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The prototype's parents, nearest first (blueprint defaults and prototypes), for the census.</summary>
    public static string Lineage(GameData db, ulong id)
    {
        var names = new List<string>();
        for (int k = 0; k < 10 && id != 0; k++)
        {
            Calligraphy.Data d;
            try { d = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { break; }
            if (!d.HasParent) break;
            names.Add(Path.GetFileNameWithoutExtension(db.Name(d.Parent)));
            id = d.Parent;
        }
        return string.Join(" < ", names);
    }

    /// <summary>The prototype derives (through its parents) from a damage-over-time or debuff blueprint.</summary>
    public static bool IsHostile(GameData db, ulong id)
    {
        for (int k = 0; k < 10 && id != 0; k++)
        {
            Calligraphy.Data d;
            try { d = db.Prototype(id).Data; } catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException) { return false; }
            if (!d.HasParent) return false;
            string n = db.Name(d.Parent);
            if (n.Contains("DamageOverTime", StringComparison.OrdinalIgnoreCase) || n.Contains("Debuff", StringComparison.OrdinalIgnoreCase)) return true;
            id = d.Parent;
        }
        return false;
    }

    static readonly Dictionary<string, List<Art>> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The power itself first, then what it reaches (breadth first, <paramref name="maxDepth"/> steps): each
    /// prototype with an Unreal class of its own. Other heroes' avatars, items, blueprints and defaults are not followed.</summary>
    public static List<Art> Of(GameData db, string powerPath, int maxDepth = 4)
    {
        lock (cache)
            if (cache.TryGetValue(powerPath, out var hit)) return hit;
        var list = new List<Art>();
        var start = db.Find(powerPath);
        if (start != null)
        {
            var seen = new HashSet<ulong> { start.Id };
            var queue = new Queue<(ulong Id, int Depth, bool Via)>();
            queue.Enqueue((start.Id, 0, false));
            while (queue.Count > 0)
            {
                var (id, depth, via) = queue.Dequeue();
                var e = db.Prototypes[id];
                var d = db.Prototype(id).Data;
                foreach (var g in d.Groups)
                    foreach (var f in g.Simple)
                        if (f.Type == 'A' && db.FieldName(g.Blueprint, f.Id) is "PowerUnrealClass" or "UnrealClass" && db.Assets.TryGetValue(f.Value.Raw, out var a)
                            && !list.Any(x => x.Class.Equals(a.Asset.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            bool isPower = db.FieldName(g.Blueprint, f.Id) == "PowerUnrealClass";
                            var (hostile, generic) = isPower ? (IsHostile(db, id), false) : Target(db, id);
                            list.Add(new Art(e.Path, a.Asset.Name, isPower, via, hostile, generic) { Depth = depth });
                        }
                if (depth >= maxDepth) continue;
                foreach (ulong r in Refs(db, d))
                    if (r != 0 && seen.Add(r) && db.Prototypes.TryGetValue(r, out var re) && Follow(re.Path))
                        queue.Enqueue((r, depth + 1, via || re.Path.StartsWith(@"Entity\", StringComparison.OrdinalIgnoreCase) || EntityLike(re.Path)));
            }
        }
        lock (cache) cache[powerPath] = list;
        return list;
    }

    /// <summary>A prototype that makes something out in the world, by its name (Hawkeye's HawkeyeShriekingArrowEntity lives under
    /// Powers\ and sets off the arrow's explosion): an entity, hotspot, missile, projectile or summon.</summary>
    static bool EntityLike(string path) => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path), "(Entity|Hotspot|Missile|Projectile|Summon)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static bool Follow(string path) =>
        (path.StartsWith(@"Powers\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"Entity\", StringComparison.OrdinalIgnoreCase))
        && !path.StartsWith(@"Entity\Characters\Avatars", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(@"Entity\Items", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\Blueprints\", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\Talents\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The prototypes a power's data names, left out: tooltip fields and eval expressions (Big Foot Sighting's
    /// TooltipPowerSynergyBonuses and EvalPowerSynergies name Antnado, which it doesn't set off; talent checks name talents).
    /// What sets things off is in the rest: ActionsTriggeredOnPowerEvent[].Power (Hammer Strike's combos), summon / missile
    /// contexts, conditions.</summary>
    internal static IEnumerable<ulong> Refs(GameData db, Calligraphy.Data d)
    {
        // Tooltips and synergy formulas only describe (Big Foot's name Antnado); other evals can start things (Thor's
        // OdinforceMechanics starts its glow manager in EvalOnCreate). Talents are left out in Follow (talent checks name them).
        static bool Skip(string field) => field.StartsWith("Tooltip", StringComparison.OrdinalIgnoreCase) || field.Equals("EvalPowerSynergies", StringComparison.OrdinalIgnoreCase);
        static bool EvalStruct(Calligraphy.Data x) => false;
        foreach (var g in d.Groups)
        {
            foreach (var f in g.Simple)
            {
                if (Skip(db.FieldName(g.Blueprint, f.Id))) continue;
                if (f.Type == 'P') yield return f.Value.Raw;
                else if (f.Type == 'R' && f.Value.Struct != null && !EvalStruct(f.Value.Struct)) foreach (var x in Refs(db, f.Value.Struct)) yield return x;
            }
            foreach (var l in g.Lists)
            {
                if (Skip(db.FieldName(g.Blueprint, l.Id))) continue;
                foreach (var v in l.Values)
                {
                    if (l.Type == 'P') yield return v.Raw;
                    else if (l.Type == 'R' && v.Struct != null && !EvalStruct(v.Struct)) foreach (var x in Refs(db, v.Struct)) yield return x;
                }
            }
        }
    }
}
