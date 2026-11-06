using System.Buffers.Binary;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>
/// Its Area's Look Too (Kurt, 2026-11-05): a power's main look can come from an area it puts down, not from the power itself.
/// Squirrel Girl's It's Raining Rodents draws its rain (vfx_squirrelg_ranged_aoe) on a hotspot (RangedSquirrelAoEArea,
/// reached through a moving entity, depth 3); a borrow copies the power's own components, so on Hawkeye's Claymore Turret
/// the animation, sounds and summon look came but no rain (telemetry ride65). Here the donor's area effects become effects of
/// the borrowed power itself: a PowerFXParticle at the target's spot (SpawnOn loc_worldposition), from the hit, lasting the
/// effect's own length (a looping one: <see cref="AreaLoopSeconds"/>). It is a look on the power, not a real area: it doesn't
/// follow a moving target.
/// </summary>
static partial class PowerBorrow
{
    /// <summary>How long a borrowed area effect that loops for good plays (the area's own lifetime is set by the summoning
    /// power's formulas, which aren't read).</summary>
    public const float AreaLoopSeconds = 4;

    /// <summary>An area effect of the donor: its particle system (file, export, path), its name and how long it plays.</summary>
    public sealed record AreaLook(string Name, string File, int Export, string SystemPath, float Seconds);

    /// <summary>
    /// The effects the areas <paramref name="donorPower"/> puts down play at the target (hotspot entities it reaches, as the
    /// 3D preview finds them), each once. Left out: areas reached only through an ultimate, a talent, a signature or a
    /// specialization (Squirrel Girl's acorn meteor is her ultimate's), effects on summoned characters and effects of the
    /// things the area does to whoever is in it (conditions).
    /// </summary>
    public static List<AreaLook> AreaLooks(Fx.GameData db, Fx.FxGame game, string donorPower, List<string>? notes = null)
    {
        var res = new List<AreaLook>();
        Fx.PowerEffects fx;
        try { fx = Fx.PowerEffects.For(game, db, donorPower, HeroOf(donorPower)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException) { return res; }
        var extra = new System.Text.RegularExpressions.Regex(@"Ultimate|Talent|Signature|Specialization", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var areas = Fx.PowerClosure.Of(db, donorPower)
            .Where(a => !a.IsPower && a.Class.StartsWith("MarvelEntity_Hotspot", StringComparison.OrdinalIgnoreCase) && !extra.IsMatch(a.Prototype))
            .Select(a => Path.GetFileNameWithoutExtension(a.Prototype)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in fx.Effects.Where(e => e.Kind == "entity" && e.AtTarget && e.TriggeredBy != null && areas.Contains(e.TriggeredBy)))
        {
            if (e.System.Emitters.FirstOrDefault()?.Required.P is not { } sp) continue;
            string? file = game.FileFor(sp.Name) ?? (File.Exists(sp.Name) ? sp.Name : null);
            if (file == null) { notes?.Add($"{e.Name}: its package isn't found"); continue; }
            var pkg = Package.Open(file);
            int export = Array.FindIndex(pkg.Exports, x => pkg.PathOf(x).Equals(e.System.Name, StringComparison.OrdinalIgnoreCase) && pkg.ClassOf(x).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase));
            if (export < 0 || res.Any(r => r.SystemPath.Equals(e.System.Name, StringComparison.OrdinalIgnoreCase))) continue;
            // its length: the longest emitter (delay + duration × loops); one that loops for good plays AreaLoopSeconds
            bool forever = e.System.Emitters.Any(em => em.Required.Int("EmitterLoops", 0) == 0);
            float len = e.System.Emitters.Select(em => em.Required.Float("EmitterDelay", 0) + Math.Max(0.01f, em.Required.Float("EmitterDuration", 1)) * Math.Max(1, em.Required.Int("EmitterLoops", 1))).DefaultIfEmpty(1).Max();
            res.Add(new AreaLook(e.Name, file, export, e.System.Name, forever ? AreaLoopSeconds : Math.Clamp(len, 0.5f, 30)));
        }
        return res;
    }

    /// <summary>
    /// <paramref name="powerPackage"/> with each area look as a component of <paramref name="targetDefault"/>: a copy of a stock
    /// target-spot particle component (Doctor Doom's Unthinkable targetvfx: SpawnOn loc_worldposition, not ended with the
    /// power), its particle system the area's (copied in under its own path), on from the hit, off <c>Seconds</c> later.
    /// </summary>
    static byte[] AddAreaLooks(byte[] powerPackage, string targetDefault, IReadOnlyList<AreaLook> looks, string cooked, List<string> log)
    {
        if (looks.Count == 0) return powerPackage;
        string templateFile = StockFiles.For(cooked, "UC__PowerDrDoom_MagicOrb_SF.upk");
        if (!File.Exists(templateFile)) { log.Add("area look: the template (Doctor Doom's Unthinkable) isn't found; left out"); return powerPackage; }
        var tp = Package.Open(templateFile);
        const string templateDefault = "marvelgamecontent.default__powerdrdoom_magicorb";
        int tc = Find(tp, templateDefault + ".targetvfx");
        if (tc < 0) { log.Add("area look: the template component isn't found; left out"); return powerPackage; }
        var dst = Package.FromBytes(powerPackage);
        int td = Find(dst, targetDefault);
        if (td < 0) throw new InvalidDataException($"no {targetDefault} in the power package");
        var addedComps = new List<(int Comp, AreaLook Look)>();
        foreach (var look in looks)
        {
            var src = Package.Open(look.File);
            var sc = CrossMove.Quiet(() => ExportCopy.Copy(src, look.Export, dst, []), out string said1)
                ?? throw new InvalidDataException($"{look.SystemPath} couldn't be copied: " + string.Join(" ", said1.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(sc.Output);
            int sysRef = sc.RootRef;
            string name = "borrowed_area_" + look.Name;
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [templateDefault] = dst.PathOf(dst.Exports[td]) };
            var cc = CrossMove.Quiet(() => ExportCopy.Copy(tp, tc, dst, ["particlesystemtemplate"], name, replace), out string said2)
                ?? throw new InvalidDataException("the area look's component couldn't be copied: " + string.Join(" ", said2.Split(Environment.NewLine).Where(l => l.Contains("can't") || l.Contains("FAIL")).Select(l => l.Trim())));
            dst = Package.FromBytes(cc.Output);
            addedComps.Add((cc.RootRef, look with { Export = sysRef }));
        }
        // each component: its system, on at the hit, off its length later; then the default lists them
        var rep = new Dictionary<int, Func<long, byte[]>>();
        var addNames = new List<string>();
        int NameIdx(string n)
        {
            int i = Array.FindIndex(dst.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (!addNames.Contains(n, StringComparer.OrdinalIgnoreCase)) addNames.Add(n);
            return dst.Names.Length + addNames.FindIndex(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var (comp, look) in addedComps)
        {
            byte[] b = dst.ReadExportBytes(dst.Exports[comp - 1]).ToArray();
            var tags = TagWalker.Walk(dst, b, 16) ?? throw new InvalidDataException("the area look's component doesn't read");
            if (tags.FirstOrDefault(t => t.Name.Equals("ParticleSystemTemplate", StringComparison.OrdinalIgnoreCase) && t.Size == 4) is { } pst)
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(pst.ValueAt), look.Export);
            if (tags.FirstOrDefault(t => t.Name.Equals("ActivationPoint", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } ap)
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ap.ValueAt), NameIdx("power_at_contact_time"));
            if (tags.FirstOrDefault(t => t.Name.Equals("DeactivationPoint", StringComparison.OrdinalIgnoreCase) && t.Size == 8) is { } dp)
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(dp.ValueAt), NameIdx("power_at_contact_time"));
            // DeactivationOffset: the effect's length after the hit (a FloatProperty added before None)
            var off = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(off.AsSpan(0), NameIdx("DeactivationOffset"));
            BinaryPrimitives.WriteInt32LittleEndian(off.AsSpan(8), NameIdx("FloatProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(off.AsSpan(16), 4);
            BinaryPrimitives.WriteSingleLittleEndian(off.AsSpan(24), look.Seconds);
            byte[] nb = [.. b.AsSpan(0, tags.NoneAt), .. off, .. b.AsSpan(tags.NoneAt)];
            rep[comp - 1] = _ => nb;
            log.Add($"  area look: {look.Name} ({look.SystemPath}) at the target from the hit, {look.Seconds:0.#} s");
        }
        var list = Components(dst, td, out var ctag, out byte[] cdata);
        int count = list.Count, end = ctag.ValueAt + 4 + 4 * count;
        var refs = addedComps.SelectMany(x => BitConverter.GetBytes(x.Comp)).ToArray();
        byte[] nd = [.. cdata.AsSpan(0, end), .. refs, .. cdata.AsSpan(end)];
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(ctag.ValueAt), count + addedComps.Count);
        BinaryPrimitives.WriteInt32LittleEndian(nd.AsSpan(ctag.Start + 16), ctag.Size + refs.Length);
        rep[td] = _ => nd;
        byte[] output = PackageRebuilder.Rebuild(dst, rep, [], out var written, addNames);
        var problems = PackageRebuilder.Verify(dst, output, rep.Keys.ToList(), [], written, addNames);
        if (problems.Count > 0) throw new InvalidDataException("the area look: " + string.Join("; ", problems.Take(3)));
        return output;
    }
}
