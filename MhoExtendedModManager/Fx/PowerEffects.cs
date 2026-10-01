using System.Numerics;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// A power's effects as the 3D preview plays them with its animation (read only; ported from the MHO Hero Creator's
/// Fx/PowerEffects.cs, 2026-09-30, the power's own class; no reference to the Hero Creator). A power's class lives in its
/// packages (AssetPackageCache.bin) and has PowerFxParticle components (Thor's Elemental Storm): ParticleSystemTemplate,
/// SpawnSockets (SocketName …), ActivationPoint (power_on_start …), ActivationOffset (seconds after it), SpawnOn
/// (loc_worldposition = at the target), StopEmittingOnEnd, AttachToSubject; also beams, decals, weapon slot changes and
/// mesh scaling; a projectile class in the power's packages gives the weapon a missile carries away.
/// Not yet (Notes say so): animated actors and summoned entities' models, and what a power sets off (combos, procs,
/// conditions: the Hero Creator's PowerClosure through the game data).
/// </summary>
sealed class PowerEffects
{
    public sealed record Effect(string Name, ParticleData System, List<string> Sockets, float Offset, string Point, bool AtTarget, bool StopOnEnd, bool Attached)
    {
        public string? TriggeredBy { get; init; }
        /// <summary>Whose component: "power", "condition", "entity" (plays at the target point) or "projectile".</summary>
        public string Kind { get; init; } = "power";
        /// <summary>A beam's far end (PowerFxBeam TargetSocket), else null.</summary>
        public string? BeamTarget { get; init; }
    }

    /// <summary>A decal (ground cracks, scorch marks): its material's texture, size and offset, laid flat at the target.</summary>
    public sealed record Decal(string Name, Gui.ModelView.Map? Tex, bool Additive, float W, float H, Vector3 Shift, bool RandomRotation, float Offset, string Point, string Kind)
    {
        public string? TriggeredBy { get; init; }
    }

    /// <summary>A power showing / hiding a weapon slot of the hero while it runs (PowerFxMeshAttachment).</summary>
    public sealed record SlotChange(string Name, string? Show, string? Hide, string Point, string? EndPoint, float Offset)
    {
        public string? TriggeredBy { get; init; }
    }

    /// <summary>The hero grown or shrunk while a power runs (ConditionFxMeshScale / PowerFxMeshScale).</summary>
    public sealed record MeshScale(string Name, float Scale, float Transition, float Offset, string Point, string Kind)
    {
        public string? TriggeredBy { get; init; }
    }

    public readonly List<MeshScale> Scales = new();
    public readonly List<Decal> Decals = new();
    public readonly List<SlotChange> Slots = new();
    /// <summary>The weapon slot a missile carries away (the projectile class's OwnerWeaponSlotToDetach), or null.</summary>
    public string? ThrownSlot;
    /// <summary>Mesh emitters' meshes, with each section's texture and blending.</summary>
    public readonly Dictionary<ParticleData.Emitter, (StaticMeshData Mesh, (Gui.ModelView.Map? Tex, bool Additive)[] Sections)> Meshes = new();
    /// <summary>When the power makes contact, as a fraction of its animation (the game data's AnimationContactTimePercent;
    /// not read yet: the Hero Creator's default).</summary>
    public float ContactPercent = 0.4f;
    public bool Returning;
    public readonly List<Effect> Effects = new();
    public readonly List<string> Notes = new();
    /// <summary>Each emitter's texture and blending (additive: glows).</summary>
    public readonly Dictionary<ParticleData.Emitter, (Gui.ModelView.Map? Tex, bool Additive)> Looks = new();

    static readonly string[] Kinds = ["PowerFxParticle", "ConditionFxParticle", "EntityFxParticle", "ProjectileFxParticle"];

    /// <summary>
    /// A power class's effects (e.g. powerthor_shockwave). <paramref name="hero"/>: the power's hero (Thor), whose main package
    /// is also read for what the effects import from there (as the game has it loaded when the hero plays).
    /// </summary>
    public static PowerEffects ForClass(FxGame game, string cls, string? hero)
    {
        var extra = new List<FxPkg>();
        if (hero != null)
            try { if (game.Open($"UC__MarvelPlayer_{hero}_SF") is { } h) extra.Add(h); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        return ForClass(game, cls, new HashSet<string>(StringComparer.OrdinalIgnoreCase), extra);
    }

    /// <summary>A power prototype's PowerUnrealClass (its Unreal class name), or null.</summary>
    public static string? UnrealClassOf(GameData db, string powerPath) =>
        db.Find(powerPath) is { } pe ? db.Prototype(pe.Id).Data.Groups.SelectMany(g => g.Simple.Select(f => (N: db.FieldName(g.Blueprint, f.Id), f)))
            .Where(x => x.N == "PowerUnrealClass" && x.f.Type == 'A').Select(x => db.Assets.TryGetValue(x.f.Value.Raw, out var a) ? a.Asset.Name : null).FirstOrDefault(x => x != null) : null;

    /// <summary>
    /// A power's effects (a prototype path, Powers\Player\Thor\Rework\GroundSmash.prototype): its own class's, and
    /// (<paramref name="triggered"/>) those of every class it sets off (combos, procs, conditions: PowerClosure; Lightning
    /// Strike's own class has none, everything shown is its triggered powers'), its contact time and returning missiles.
    /// </summary>
    public static PowerEffects For(FxGame g, GameData db, string powerPath, string? hero, bool triggered = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<FxPkg>();
        if (hero != null)
            try { if (g.Open($"UC__MarvelPlayer_{hero}_SF") is { } h) extra.Add(h); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { }
        string? ownClass = UnrealClassOf(db, powerPath);
        var fx = ownClass != null ? ForClass(g, ownClass, seen, extra) : new PowerEffects();
        if (ownClass == null) fx.Notes.Add(Path.GetFileNameWithoutExtension(powerPath) + ": no PowerUnrealClass of its own");
        if (db.Find(powerPath) is { } pe)
        {
            var d = db.Prototype(pe.Id).Data;
            foreach (var gr in d.Groups)
                foreach (var f in gr.Simple)
                    if (f.Type == 'D' && db.FieldName(gr.Blueprint, f.Id) == "AnimationContactTimePercent") { float v = (float)BitConverter.Int64BitsToDouble((long)f.Value.Raw); if (v > 0 && v <= 1) fx.ContactPercent = v; }
            fx.Returning = Contains(db, d, "IsReturningMissile");
        }
        else fx.Notes.Add("no power " + powerPath);
        if (!triggered) return fx;
        foreach (var art in PowerClosure.Of(db, powerPath).Where(x => !(x.Prototype.Equals(db.Find(powerPath)?.Path, StringComparison.OrdinalIgnoreCase) && x.Class.Equals(ownClass ?? "", StringComparison.OrdinalIgnoreCase))))
        {
            var more = ForClass(g, art.Class, seen, extra);
            string by = Path.GetFileNameWithoutExtension(art.Prototype);
            foreach (var e in more.Effects) fx.Effects.Add(e with { TriggeredBy = by });
            foreach (var kv in more.Looks) fx.Looks[kv.Key] = kv.Value;
            foreach (var kv in more.Meshes) fx.Meshes[kv.Key] = kv.Value;
            foreach (var dc in more.Decals) fx.Decals.Add(dc with { TriggeredBy = by });
            foreach (var sc in more.Slots) fx.Slots.Add(sc with { TriggeredBy = by });
            foreach (var sc in more.Scales) fx.Scales.Add(sc with { TriggeredBy = by });
            fx.ThrownSlot ??= more.ThrownSlot;
            fx.Notes.AddRange(more.Notes);
        }
        return fx;
    }

    /// <summary>A true B field of that name anywhere in the data (nested structs and lists too).</summary>
    static bool Contains(GameData db, Calligraphy.Data d, string field)
    {
        foreach (var gr in d.Groups)
        {
            foreach (var f in gr.Simple)
                if (f.Type == 'B' && f.Value.Raw != 0 && db.FieldName(gr.Blueprint, f.Id) == field || f.Type == 'R' && f.Value.Struct != null && Contains(db, f.Value.Struct, field)) return true;
            foreach (var l in gr.Lists)
                if (l.Type == 'R' && l.Values.Any(v => v.Struct != null && Contains(db, v.Struct, field))) return true;
        }
        return false;
    }

    static (FxPkg P, int Export)? Resolve(FxPkg p, int r, List<FxPkg> pkgs, FxGame g)
    {
        if (r > 0) return (p, r - 1);
        if (r == 0) return null;
        string path = p.T.PathOf(r);
        foreach (var q in pkgs.Concat(g.AlwaysLoaded()))
            if (q.Find(path) is int i && i >= 0) return (q, i);
        return null;
    }

    /// <summary>A material a mesh particle doesn't show as a surface: a DecalMaterial or one that uses distortion.</summary>
    static bool NotDrawn(FxPkg p, int r, List<FxPkg> pkgs, FxGame g)
    {
        if (r == 0 || Resolve(p, r, pkgs, g) is not { } at) return false;
        if (at.P.T.ClassOf(at.P.T.Exports[at.Export]).Equals("DecalMaterial", StringComparison.OrdinalIgnoreCase)) return true;
        var props = FxProps.Find(at.P.Bytes, at.P.T, at.P.T.Exports[at.Export])?.Props ?? [];
        return props.FirstOrDefault(x => x.Name.Equals("bUsesDistortion", StringComparison.OrdinalIgnoreCase)) is { } d && at.P.Bytes[d.ValueAt] != 0;
    }

    /// <summary>A mesh emitter's static mesh (its TypeDataMesh's Mesh).</summary>
    static (StaticMeshData Mesh, FxPkg P)? MeshOf(ParticleData.Emitter em, List<FxPkg> pkgs, FxGame g)
    {
        if (em.TypeData is not { } td) return null;
        int mr = td.Ref("Mesh");
        if (mr == 0) return null;
        if (Resolve(td.P, mr, pkgs, g) is not { } at || !at.P.T.ClassOf(at.P.T.Exports[at.Export]).Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)) return null;
        return StaticMeshData.Read(at.P, at.Export) is { } m ? (m, at.P) : null;
    }

    /// <summary>The components that aren't plain particle systems: beams, decals, weapon slots, mesh scaling, a projectile
    /// class's detached weapon slot (animated actors and summoned models: noted, not shown yet). True when handled.</summary>
    static bool Other(PowerEffects fx, FxTextures tex, FxPkg p, int i, string cname, string epath, string cls, HashSet<string> seen, List<FxPkg> pkgs, FxGame g)
    {
        bool mine = FxGame.OfChain(epath, g.ClassChain(cls));
        var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[i])?.Props ?? [];
        FxProps.Prop? P(string n) => props.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        string Pt(string n, string d) => P(n)?.Value is { } v ? v[(v.LastIndexOf('.') + 1)..] : d;
        float Fl(string n, float d) => P(n) is { Size: 4 } x && x.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) ? BitConverter.ToSingle(p.Bytes, x.ValueAt) : d;
        string name = p.T.Exports[i].ObjectName;
        if (epath.StartsWith("marvelgamecontent.default__marvelprojectile_", StringComparison.OrdinalIgnoreCase) && epath.Count(ch => ch == '.') == 1)
        {
            if (P("OwnerWeaponSlotToDetach")?.Value is { } slot && !slot.Equals("None", StringComparison.OrdinalIgnoreCase)) fx.ThrownSlot ??= slot;
            return true;
        }
        if (!mine) return false;
        if (cname.StartsWith("PowerFxBeam", StringComparison.OrdinalIgnoreCase) || cname.EndsWith("FxBeam", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(p.Name + "|" + epath)) return true;
            int sysRef = P("BeamParticleSystemTemplate") is { Size: 4 } sp ? BitConverter.ToInt32(p.Bytes, sp.ValueAt) : 0;
            if (Resolve(p, sysRef, pkgs, g) is not { } at || ParticleData.Read(at.P, at.Export) is not { } data) { fx.Notes.Add($"{name}: its beam isn't found"); return true; }
            var sockets = P("SpawnSocket")?.Value is { } ss && !ss.Equals("None", StringComparison.OrdinalIgnoreCase) ? new List<string> { ss } : [];
            fx.Effects.Add(new Effect(name, data, sockets, Fl("ActivationOffset", 0), Pt("ActivationPoint", "power_on_start"), false, P("DeactivatesOnEnd") is not { } de || p.Bytes[de.ValueAt] != 0, true)
                { BeamTarget = P("TargetSocket")?.Value ?? "target" });
            foreach (var em in data.Emitters)
                try { fx.Looks[em] = tex.ParticleMaterial(em.Required.P, em.Required.Ref("Material"), false); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException) { fx.Looks[em] = (null, true); }
            return true;
        }
        if (cname.EndsWith("FxDecal", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(p.Name + "|" + epath)) return true;
            int mat = P("DecalMat") is { Size: 4 } dm ? BitConverter.ToInt32(p.Bytes, dm.ValueAt) : 0;
            (Gui.ModelView.Map? Tex, bool Additive) look = (null, false);
            try { look = tex.ParticleMaterial(p, mat, false); } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException) { }
            var shift = P("OffsetVector") is { Size: 12 } ov ? new Vector3(BitConverter.ToSingle(p.Bytes, ov.ValueAt), BitConverter.ToSingle(p.Bytes, ov.ValueAt + 4), BitConverter.ToSingle(p.Bytes, ov.ValueAt + 8)) : Vector3.Zero;
            string kind = cname.StartsWith("Entity", StringComparison.OrdinalIgnoreCase) ? "entity" : cname.StartsWith("Projectile", StringComparison.OrdinalIgnoreCase) ? "projectile" : "power";
            fx.Decals.Add(new Decal(name, look.Tex, look.Additive, Fl("Width", 100), Fl("Height", 100), shift, P("RandomRotation") is { } rr && p.Bytes[rr.ValueAt] != 0,
                Fl("ActivationOffset", 0), Pt("ActivationPoint", "power_on_start"), kind));
            return true;
        }
        if ((cname.StartsWith("PowerFxAnimatedActor", StringComparison.OrdinalIgnoreCase) || cname.StartsWith("EntityFxAnimatedActor", StringComparison.OrdinalIgnoreCase))
            && !cname.EndsWith("MaterialParameter", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add(p.Name + "|" + epath) && P("EntityRequired") == null) fx.Notes.Add($"{name}: an animated actor (not shown yet)");
            return true;
        }
        if (cname.StartsWith("ConditionFxMeshScale", StringComparison.OrdinalIgnoreCase) || cname.StartsWith("PowerFxMeshScale", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(p.Name + "|" + epath)) return true;
            float scale = Fl("ActorScale", 1);
            if (Math.Abs(scale - 1) > 0.01f)
                fx.Scales.Add(new MeshScale(name, scale, Math.Max(0.01f, Fl("TransitionTimeSecs", 0.25f)), Fl("ActivationOffset", 0), Pt("ActivationPoint", "power_on_start"),
                    cname.StartsWith("Condition", StringComparison.OrdinalIgnoreCase) ? "condition" : "power"));
            return true;
        }
        if (cname.StartsWith("PowerFxMeshAttachment", StringComparison.OrdinalIgnoreCase) || cname.StartsWith("ConditionFxMeshAttachment", StringComparison.OrdinalIgnoreCase))
        {
            if (!seen.Add(p.Name + "|" + epath)) return true;
            string? N(string n) => P(n)?.Value is { } v && !v.Equals("None", StringComparison.OrdinalIgnoreCase) ? v : null;
            fx.Slots.Add(new SlotChange(name, N("WhileActivatedShowWeaponSlot"), N("WhileActivatedHideWeaponSlot"), Pt("ActivationPoint", "power_on_start"),
                P("DeactivationPoint") != null ? Pt("DeactivationPoint", "") : null, Fl("ActivationOffset", 0)));
            return true;
        }
        if (cname.Equals("SkeletalMeshComponent", StringComparison.OrdinalIgnoreCase) && epath.EndsWith(".initialskeletalmesh", StringComparison.OrdinalIgnoreCase)
            && cls.StartsWith("Marvel", StringComparison.OrdinalIgnoreCase) && !cls.StartsWith("MarvelPlayer", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add(p.Name + "|" + epath)) fx.Notes.Add($"{cls}: a summoned entity's model (not shown yet)");
            return true;
        }
        return false;
    }

    static PowerEffects ForClass(FxGame g, string cls, HashSet<string> seen, List<FxPkg> extra)
    {
        var fx = new PowerEffects();
        var pkgs = new List<FxPkg>();
        foreach (var pkgName in g.PackagesOf(cls))
            try { if (g.Open(pkgName) is { } q) pkgs.Add(q); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { fx.Notes.Add($"{pkgName}: {ex.Message}"); }
        if (pkgs.Count == 0) { fx.Notes.Add($"{cls}: no package found"); return fx; }
        var look = pkgs.Concat(extra).ToList();
        var chain = g.ClassChain(cls);
        var tex = new FxTextures([.. look, .. g.AlwaysLoaded()], g.Cooked);
        foreach (var p in pkgs)
            for (int i = 0; i < p.T.Exports.Count; i++)
            {
                string cname = p.T.ClassOf(p.T.Exports[i]);
                string epath = p.T.PathOf(i + 1);
                if (Other(fx, tex, p, i, cname, epath, cls, seen, look, g)) continue;
                string? kindOf = Kinds.FirstOrDefault(k => cname.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                if (kindOf == null) continue;
                bool mine = FxGame.OfChain(epath, chain);
                bool projectile = kindOf == "ProjectileFxParticle" && epath.Contains(".default__marvelprojectile_", StringComparison.OrdinalIgnoreCase);
                if (!mine && !projectile) continue;
                if (!seen.Add(p.Name + "|" + epath)) continue;
                string kindName = kindOf switch { "ConditionFxParticle" => "condition", "EntityFxParticle" => "entity", "ProjectileFxParticle" => "projectile", _ => "power" };
                var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[i])?.Props ?? [];
                FxProps.Prop? P(string n) => props.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                int sysRef = P("ParticleSystemTemplate") is { Size: 4 } sp ? BitConverter.ToInt32(p.Bytes, sp.ValueAt) : 0;
                if (Resolve(p, sysRef, look, g) is not { } sysAt) { if (sysRef != 0) fx.Notes.Add($"{p.T.Exports[i].ObjectName}: its particle system {p.T.PathOf(sysRef)} isn't in a package the view reads"); continue; }
                ParticleData? data;
                try { data = ParticleData.Read(sysAt.P, sysAt.Export); }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException) { fx.Notes.Add($"{p.T.Exports[i].ObjectName}: {ex.Message}"); continue; }
                if (data == null) continue;
                var sockets = P("SpawnSockets") is { } ss ? FxGame.StructArray(p.Bytes, p.T, ss.ValueAt, ss.Size)
                    .Select(d => d.TryGetValue("socketname", out var n) ? n : "None").Where(n => !n.Equals("None", StringComparison.OrdinalIgnoreCase)).ToList() : [];
                if (kindName == "projectile")
                {
                    int owner = p.T.Exports[i].Outer;
                    var op = owner > 0 ? FxProps.Find(p.Bytes, p.T, p.T.Exports[owner - 1])?.Props.FirstOrDefault(x => x.Name.Equals("SpawnFromSockets", StringComparison.OrdinalIgnoreCase)) : null;
                    if (op != null)
                        foreach (var el in FxGame.StructArray(p.Bytes, p.T, op.ValueAt, op.Size))
                            foreach (var v in el.Values) if (!v.Equals("None", StringComparison.OrdinalIgnoreCase) && !sockets.Contains(v, StringComparer.OrdinalIgnoreCase)) sockets.Add(v);
                }
                string point = P("ActivationPoint")?.Value is { } ap ? ap[(ap.LastIndexOf('.') + 1)..] : "power_on_start";
                float offset = P("ActivationOffset") is { Size: 4 } ao ? BitConverter.ToSingle(p.Bytes, ao.ValueAt) : 0;
                bool atTarget = P("SpawnOn")?.Value?.EndsWith("loc_worldposition", StringComparison.OrdinalIgnoreCase) == true || kindName == "entity";
                bool stop = P("StopEmittingOnEnd") is { } se && p.Bytes[se.ValueAt] != 0;
                bool attached = P("AttachToSubject") is not { } at || p.Bytes[at.ValueAt] != 0;
                fx.Effects.Add(new Effect(p.T.Exports[i].ObjectName, data, sockets, offset, point, atTarget, stop, attached) { Kind = kindName });
                foreach (var em in data.Emitters.Where(x => x.Kind == "mesh"))
                {
                    int emr = em.Required.Ref("Material");
                    bool overrides = em.TypeData?.Bool("bOverrideMaterial", false) == true;
                    string emName = emr == 0 ? "" : em.Required.P.T.PathOf(emr).ToLowerInvariant();
                    if (overrides && (emName.Contains("distort") || emName.Contains("warp") || emName.Contains("refract") || emName.Contains("radial") || NotDrawn(em.Required.P, emr, look, g))) continue;
                    if (MeshOf(em, look, g) is not { } mm) continue;
                    (Gui.ModelView.Map? Tex, bool Additive) own = (null, true);
                    try { if (overrides && emr != 0) own = tex.ParticleMaterial(em.Required.P, emr, false); } catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException) { }
                    if (overrides && own.Tex == null) continue;
                    fx.Meshes[em] = (mm.Mesh, mm.Mesh.Sections.Select(sec =>
                    {
                        if (overrides) return own;
                        if (NotDrawn(mm.P, sec.MaterialRef, look, g)) return ((Gui.ModelView.Map?)null, true);
                        try { return tex.ParticleMaterial(mm.P, sec.MaterialRef, false); }
                        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException) { return ((Gui.ModelView.Map?)null, true); }
                    }).ToArray());
                }
                foreach (var em in data.Emitters)
                    try { fx.Looks[em] = tex.ParticleMaterial(em.Required.P, em.Required.Ref("Material"), em.Required.Int("SubImages_Horizontal", 1) * em.Required.Int("SubImages_Vertical", 1) > 1); }
                    catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or IOException) { fx.Looks[em] = (null, true); fx.Notes.Add($"{em.Name}: {ex.Message}"); }
            }
        fx.Notes.AddRange(tex.Notes.Distinct().Take(4));
        return fx;
    }
}
