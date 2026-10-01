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
    public sealed record Art(string Prototype, string Class, bool IsPower);

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
            var queue = new Queue<(ulong Id, int Depth)>();
            queue.Enqueue((start.Id, 0));
            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                var e = db.Prototypes[id];
                var d = db.Prototype(id).Data;
                foreach (var g in d.Groups)
                    foreach (var f in g.Simple)
                        if (f.Type == 'A' && db.FieldName(g.Blueprint, f.Id) is "PowerUnrealClass" or "UnrealClass" && db.Assets.TryGetValue(f.Value.Raw, out var a)
                            && !list.Any(x => x.Class.Equals(a.Asset.Name, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new Art(e.Path, a.Asset.Name, db.FieldName(g.Blueprint, f.Id) == "PowerUnrealClass"));
                if (depth >= maxDepth) continue;
                foreach (ulong r in Refs(db, d))
                    if (r != 0 && seen.Add(r) && db.Prototypes.TryGetValue(r, out var re) && Follow(re.Path)) queue.Enqueue((r, depth + 1));
            }
        }
        lock (cache) cache[powerPath] = list;
        return list;
    }

    static bool Follow(string path) =>
        (path.StartsWith(@"Powers\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"Entity\", StringComparison.OrdinalIgnoreCase))
        && !path.StartsWith(@"Entity\Characters\Avatars", StringComparison.OrdinalIgnoreCase) && !path.StartsWith(@"Entity\Items", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\Blueprints\", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".defaults", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(@"\Talents\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The prototypes a power's data names, left out: tooltip fields and eval expressions (Big Foot Sighting's
    /// TooltipPowerSynergyBonuses and EvalPowerSynergies name Antnado, which it doesn't set off; talent checks name talents).
    /// What sets things off is in the rest: ActionsTriggeredOnPowerEvent[].Power (Hammer Strike's combos), summon / missile
    /// contexts, conditions.</summary>
    static IEnumerable<ulong> Refs(GameData db, Calligraphy.Data d)
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
