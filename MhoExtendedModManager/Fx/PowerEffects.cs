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
        /// <summary>The component's OffsetVector and OffsetRotation (pitch, yaw, roll in UE3 units: 65536 a turn), applied to the
        /// effect's place (Iron Man's Unibeam: its beam mesh points back along -X and the component turns it round).</summary>
        public Vector3 Shift { get; init; }
        public (int Pitch, int Yaw, int Roll) Turn { get; init; }
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
    /// <summary>A power's AnimationContactTimePercent (0.4 when the game data doesn't set it): for the props' timing.</summary>
    public static float ContactPercentOf(GameData db, string powerPath)
    {
        if (db.Find(powerPath) is not { } pe) return 0.4f;
        foreach (var gr in db.Prototype(pe.Id).Data.Groups)
            foreach (var f in gr.Simple)
                if (f.Type == 'D' && db.FieldName(gr.Blueprint, f.Id) == "AnimationContactTimePercent") { float v = (float)BitConverter.Int64BitsToDouble((long)f.Value.Raw); if (v >= 0 && v <= 1) return v; }   // (0 = at the start: Iron Man's Signature)
        return 0.4f;
    }

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
                    if (f.Type == 'D' && db.FieldName(gr.Blueprint, f.Id) == "AnimationContactTimePercent") { float v = (float)BitConverter.Int64BitsToDouble((long)f.Value.Raw); if (v >= 0 && v <= 1) fx.ContactPercent = v; }   // (0 = contact at the start: Iron Man's Signature lasers, Kurt 2026-10-02)
            fx.Returning = Contains(db, d, "IsReturningMissile");
        }
        else fx.Notes.Add("no power " + powerPath);
        if (!triggered) return fx;
        var arts = PowerClosure.Of(db, powerPath).Where(x => !(x.Prototype.Equals(db.Find(powerPath)?.Path, StringComparison.OrdinalIgnoreCase) && x.Class.Equals(ownClass ?? "", StringComparison.OrdinalIgnoreCase))).ToList();
        var names = arts.Select(x => Path.GetFileNameWithoutExtension(x.Prototype)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var art in arts)
        {
            // One variant (Kurt, 2026-09-30: Ground Smash fired both shockwaves): a resource version whose plain twin is also
            // set off (ShockwaveOFMissile beside ShockwaveNoOFMissile, HammerDashOdinforceCombo beside HammerDashNormalCombo)
            // is left out; in game only one plays, by the hero's resource.
            if (ResourceTwin(Path.GetFileNameWithoutExtension(art.Prototype), names) is { } plain) { fx.Notes.Add($"{Path.GetFileNameWithoutExtension(art.Prototype)}: left out (its plain twin {plain} plays)"); continue; }
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

    static readonly (string Plain, string Resource)[] Twins = [("NoOF", "OF"), ("Normal", "Odinforce"), ("NoOdin", "Odin"), ("NoOdinforce", "Odinforce")];

    /// <summary>The plain twin of a resource variant's name when that twin is also in <paramref name="names"/>, else null
    /// (ShockwaveOFMissile → ShockwaveNoOFMissile).</summary>
    static string? ResourceTwin(string name, HashSet<string> names)
    {
        foreach (var (plain, res) in Twins)
            for (int i = name.IndexOf(res, StringComparison.Ordinal); i >= 0; i = name.IndexOf(res, i + 1, StringComparison.Ordinal))
            {
                if (i >= plain.Length - res.Length && name.Substring(Math.Max(0, i - (plain.Length - res.Length)), plain.Length).Equals(plain, StringComparison.Ordinal)) continue;   // it's the plain one itself (NoOF contains OF)
                string twin = name[..i] + plain + name[(i + res.Length)..];
                if (names.Contains(twin)) return twin;
            }
        return null;
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
                // Only for some entities (EntityRequired: Unibeam's Hulkbuster variants): the preview can't tell, so left out
                // (as the props' rules are); the plain version plays.
                if (P("EntityRequired") is { Size: > 4 } er && BitConverter.ToInt32(p.Bytes, er.ValueAt) > 0) { fx.Notes.Add($"{p.T.Exports[i].ObjectName}: only for certain characters (left out)"); continue; }
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
                var shiftV = P("OffsetVector") is { Size: 12 } ovp ? new Vector3(BitConverter.ToSingle(p.Bytes, ovp.ValueAt), BitConverter.ToSingle(p.Bytes, ovp.ValueAt + 4), BitConverter.ToSingle(p.Bytes, ovp.ValueAt + 8)) : Vector3.Zero;
                var turnR = P("OffsetRotation") is { Size: 12 } orp ? (BitConverter.ToInt32(p.Bytes, orp.ValueAt), BitConverter.ToInt32(p.Bytes, orp.ValueAt + 4), BitConverter.ToInt32(p.Bytes, orp.ValueAt + 8)) : (0, 0, 0);
                if (Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1" && (shiftV != Vector3.Zero || turnR != (0, 0, 0)))
                    Console.WriteLine($"    {p.T.Exports[i].ObjectName}: offset {shiftV}, rotation pitch {turnR.Item1} yaw {turnR.Item2} roll {turnR.Item3}; local-space mesh {data.Emitters.Any(x => x.Kind == "mesh" && x.Required.Bool("bUseLocalSpace", false))}");
                fx.Effects.Add(new Effect(p.T.Exports[i].ObjectName, data, sockets, offset, point, atTarget, stop, attached) { Kind = kindName, Shift = shiftV, Turn = turnR });
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

    /// <summary>
    /// A power's effects playing: each starts at its offset after the animation starts, at its socket (posed each frame
    /// when attached; else where the socket was when it started) or at the target point.
    /// </summary>
    public sealed class Player(PowerEffects fx, Func<string, Matrix4x4?> socket, Vector3 target, Func<Effect, bool>? include = null)
    {
        readonly List<(Effect E, ParticleSim Sim, string? Socket)> running = new();

        /// <summary>
        /// Where an effect sits: the socket's position, facing the way the hero faces (+X, towards the target). A power's
        /// effects aim where the power goes, not where the bone happens to point: Forked Lightning's cone follows the
        /// socket on the hammer, but taking the hammer's turn sent the bolts off sideways (Kurt's game shot: straight ahead).
        /// </summary>
        static Matrix4x4 Facing(Matrix4x4 m) => Matrix4x4.CreateTranslation(m.Translation);

        /// <summary>An effect's place at its socket: facing the hero's way (Facing), except a mesh laid over what holds the socket
        /// (a local-space mesh emitter: the Odinforce lightning layer, the hammer's own shape), which takes the socket's turn too.</summary>
        static Matrix4x4 Place(Effect e, Matrix4x4 m)
        {
            var at = e.System.Emitters.Any(x => x.Kind == "mesh" && x.Required.Bool("bUseLocalSpace", false)) ? m : Facing(m);
            if (e.Turn == (0, 0, 0) && e.Shift == Vector3.Zero || !e.Attached) return at;
            // The component's offset and turn, in the frame it's placed in, for an effect that stays on its subject. (A
            // hypothesis from Iron Man's Unibeam: its unattached laser has offset 0,0,73 and its system starts 73 up already;
            // both together put the beam above the head, the system's alone at the chest, where the game fires it.)
            var turn = e.Turn == (0, 0, 0) ? Matrix4x4.Identity : FxSockets.RotationMatrix(e.Turn.Pitch, e.Turn.Yaw, e.Turn.Roll);
            return turn * Matrix4x4.CreateTranslation(e.Shift) * at;
        }
        readonly HashSet<Effect> started = new();
        float time;

        /// <summary>The animation's length in seconds: missiles leave and summons appear at the power's contact time in it.</summary>
        public float AnimSeconds { get; set; } = 1;
        float ContactTime => fx.ContactPercent * AnimSeconds;

        // Missiles in flight (by their particle system): where they left from, when, where they go (+X, the way the hero faces).
        readonly Dictionary<ParticleSim, (Vector3 From, float T0)> flights = new();
        const float MissileRange = 300, MissileSpeed = 900;

        /// <summary>A missile's place after <paramref name="t"/> seconds: out <see cref="MissileRange"/> units, back again when it
        /// returns; null when its flight is over.</summary>
        Vector3? Flight(Vector3 from, float t)
        {
            float d = t * MissileSpeed;
            if (d <= MissileRange) return from + new Vector3(d, 0, 0);
            if (fx.Returning && d <= 2 * MissileRange) return from + new Vector3(2 * MissileRange - d, 0, 0);
            return null;
        }

        public void Reset() { running.Clear(); started.Clear(); flights.Clear(); decals.Clear(); time = 0; endedAt = -1; }

        /// <summary>Also the parts that come from what the power sets off (decals, weapon slots, models) — the Triggered toggle.</summary>
        public bool ShowTriggered { get; set; } = true;
        float endedAt = -1;
        readonly List<(Decal D, float Rot)> decals = new();
        readonly HashSet<Decal> decalsStarted = new();

        /// <summary>When a component starts: its activation point (the contact time for "…contact…" points, and for summons' and
        /// missiles' components) plus its offset.</summary>
        float StartOf(string point, float offset, string kind) => (point.Contains("contact", StringComparison.OrdinalIgnoreCase) || kind is "entity" or "projectile" ? ContactTime : 0) + offset;
        bool Shown(string? by) => by == null || ShowTriggered;

        /// <summary>Whether a weapon slot is shown / hidden by the power now (PowerFxMeshAttachment: from its activation point to its
        /// deactivation point, else to the animation's end).</summary>
        public bool SlotShown(string? slot) => slot != null && fx.Slots.Any(sc => Shown(sc.TriggeredBy) && slot.Equals(sc.Show, StringComparison.OrdinalIgnoreCase) && SlotActive(sc));
        public bool SlotHidden(string? slot) => slot != null && fx.Slots.Any(sc => Shown(sc.TriggeredBy) && slot.Equals(sc.Hide, StringComparison.OrdinalIgnoreCase) && SlotActive(sc));
        bool SlotActive(SlotChange sc)
        {
            if (time < StartOf(sc.Point, sc.Offset, "power")) return false;
            if (sc.EndPoint is { Length: > 0 } ep && !ep.Contains("end", StringComparison.OrdinalIgnoreCase)) return time < StartOf(ep, 0, "power");
            return endedAt < 0 || time < endedAt;
        }

        /// <summary>
        /// How big the hero is now (1 = as built): each mesh scale grows over its transition from its start, and shrinks back
        /// over the same time once the animation has ended (a condition's end: Ant-Man's grow lasts the stomp).
        /// </summary>
        public float HeroScale
        {
            get
            {
                float k = 1;
                foreach (var sc in fx.Scales.Where(x => Shown(x.TriggeredBy)))
                {
                    float start = StartOf(sc.Point, sc.Offset, "power");
                    if (time < start) continue;
                    float grow = Math.Clamp((time - start) / sc.Transition, 0, 1);
                    if (endedAt >= 0) grow = Math.Min(grow, 1 - Math.Clamp((time - endedAt) / sc.Transition, 0, 1));
                    k *= 1 + (sc.Scale - 1) * grow;
                }
                return k;
            }
        }

        /// <summary>Where a missile carrying this weapon slot is now (the thrown hammer), else null.</summary>
        public Vector3? ThrownAt(string? slot)
        {
            if (slot == null || fx.ThrownSlot == null || !slot.Equals(fx.ThrownSlot, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var (_, fl) in flights) if (Flight(fl.From, time - fl.T0) is { } pos) return pos;
            return null;
        }

        /// <summary>The power customizer's colour change for this power (null: as the game has it): applied to the particles'
        /// colours and to the effect textures, as PowerRecolor writes them.</summary>
        public PowerColor? Color { get => color; set { color = value; recolored.Clear(); } }
        PowerColor? color;
        readonly Dictionary<Gui.ModelView.Map, Gui.ModelView.Map> recolored = new();

        Gui.ModelView.Map? Tex(Gui.ModelView.Map? t)
        {
            if (t == null || color is not { IsNone: false } c) return t;
            lock (recolored) { if (!recolored.TryGetValue(t, out var r)) recolored[t] = r = t.Recolored(c.Apply); return r; }
        }

        Vector4 Col(Vector4 v) => color is { IsNone: false } c ? new Vector4(c.Apply(new Vector3(v.X, v.Y, v.Z)), v.W) : v;

        /// <summary>Moves the effects on by <paramref name="dt"/> seconds; <paramref name="ended"/>: the animation is over.</summary>
        public void Step(float dt, bool ended)
        {
            time += dt;
            if (ended && endedAt < 0) endedAt = time;
            var rng = new Random(7);
            foreach (var d in fx.Decals)
                if (!decalsStarted.Contains(d) && Shown(d.TriggeredBy) && time >= StartOf(d.Point, d.Offset, d.Kind))
                { decalsStarted.Add(d); decals.Add((d, d.RandomRotation ? (float)(rng.NextDouble() * Math.PI * 2) : 0)); }
            foreach (var e in fx.Effects)
            {
                // Summons' and missiles' effects start at the power's contact time.
                float at = StartOf(e.Point, e.Offset, e.Kind);
                if (started.Contains(e) || time < at || include?.Invoke(e) == false) continue;
                started.Add(e);
                if (e.Kind == "projectile")
                {
                    // One missile from its (first) spawn socket, else chest height over the ground below the hero.
                    var from = e.Sockets.Select(n => socket(n)).FirstOrDefault(m => m != null)?.Translation ?? new Vector3(0, 0, target.Z + 60);
                    var sim = new ParticleSim(e.System) { Origin = Matrix4x4.CreateTranslation(from) };
                    flights[sim] = (from, time);
                    running.Add((e, sim, null));
                    continue;
                }
                var places = e.AtTarget ? [(Matrix4x4.CreateTranslation(target), (string?)null)]
                    : e.Sockets.Count == 0 ? [(Matrix4x4.Identity, null)] : e.Sockets.Select(n => (Place(e, socket(n) ?? Matrix4x4.Identity), (string?)n)).ToList();
                foreach (var (pl, sock) in places) running.Add((e, new ParticleSim(e.System) { Origin = pl }, sock));
            }
            for (int k = 0; k < running.Count; k++)
            {
                var (e, sim, sock) = running[k];
                if (e.Attached && sock != null && socket(sock) is { } m) sim.Origin = Place(e, m);
                if (flights.TryGetValue(sim, out var fl))
                {
                    if (Flight(fl.From, time - fl.T0) is { } pos) sim.Origin = Matrix4x4.CreateTranslation(pos);
                    else if (!sim.Stopped) sim.Stop();
                }
                if (ended && e.StopOnEnd && !sim.Stopped) sim.Stop();
                sim.Step(dt);
            }
            foreach (var r in running.Where(r => r.Sim.Stopped && !r.Sim.Alive || r.Sim.Age > 12)) flights.Remove(r.Sim);
            running.RemoveAll(r => r.Sim.Stopped && !r.Sim.Alive || r.Sim.Age > 12);
        }

        /// <summary>The live particles as the view's quads.</summary>
        public List<Gui.ModelView.FxQuad> Quads()
        {
            var list = new List<Gui.ModelView.FxQuad>();
            // Decals: flat on the ground at the target, until 1.5 s after the animation (fading the last 0.5 s).
            foreach (var (d, rot) in decals)
            {
                float left = endedAt < 0 ? 9 : endedAt + 1.5f - time;
                if (left <= 0 || d.Tex == null) continue;
                var pos = target + d.Shift + new Vector3(0, 0, 1);
                list.Add(new Gui.ModelView.FxQuad(pos, new Vector2(d.W, d.H), rot, new Vector4(1, 1, 1, Math.Min(1, left / 0.5f)), Tex(d.Tex), 1, 1, 0, 0, false, d.Additive, Vector3.Zero)
                    { PlaneRight = Vector3.UnitX, PlaneUp = Vector3.UnitY, KeepPlane = true });
            }
            foreach (var (re, sim, _) in running)
                foreach (var (em, sp) in sim.Sprites())
                {
                    if (em.Kind == "beam")
                    {
                        // A beam from its socket to the power's TargetSocket on the model, posed this frame (Iron Man's
                        // Microlaser Sweep: socket_l_laser_tgt, 450 units along the forearm, sweeping level; Kurt 2026-10-02:
                        // aimed at the point ahead, the beams ran at the camera and showed as tall vertical bars), else to
                        // the target point (chest high); in TextureTile pieces along its length.
                        var (btex, badd) = fx.Looks.TryGetValue(em, out var bl) ? bl : (null, true);
                        if (btex == null) continue;
                        var from = sim.Origin.Translation;
                        var to = re.BeamTarget is { Length: > 0 } bt && !bt.Equals("target", StringComparison.OrdinalIgnoreCase) && socket(bt) is { } tm
                            ? tm.Translation : target + new Vector3(0, 0, 60);
                        var dir = to - from; float len = dir.Length();
                        if (Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1")
                            Console.WriteLine($"    {Gui.ModelView.BeamStats()} (before); beam {re.Name}/{em.Name}: from {from} to {to} (target socket {re.BeamTarget}: {(re.BeamTarget != null && socket(re.BeamTarget) != null ? "found" : "not found")})");
                        if (len < 1) continue;
                        int tiles = Math.Clamp(em.TypeData?.Int("TextureTile", 1) ?? 1, 1, 12);
                        float width = sp.Size.X > 1 ? sp.Size.X : 10;
                        var bcol = Col(sp.Color);
                        // In short pieces (8 per texture repeat), each sized at its own depth: one long piece per repeat
                        // ballooned when the beam ran toward the camera (Microlaser Sweep) and missed the hand. A piece samples
                        // its slice of the texture (as a sub-image column).
                        const int per = 8;
                        int pieces = tiles * per;
                        for (int k = 0; k < pieces; k++)
                            list.Add(new Gui.ModelView.FxQuad(from + dir * ((k + 0.5f) / pieces), new Vector2(width, len / pieces), 0, bcol, Tex(btex), per, 1, k % per, 2, false, badd, dir) { SwapUV = true });
                        continue;
                    }
                    if (em.Kind is not ("sprite" or "physx")) continue;   // meshes: Tris()
                    if (Environment.GetEnvironmentVariable("MHO_FXONLY") is { Length: > 0 } only && !only.Split(',').Contains(em.Name)) continue;   // debug: these emitters only
                    var (tex, add) = fx.Looks.TryGetValue(em, out var l) ? l : (null, true);
                    if (tex == null) continue;                              // no texture found: not drawn (a white blob says nothing)
                    // A particle's Size is its full width (a ×2 reading was tried and was far too big once the bolts showed).
                    string align = em.Required.Enum("ScreenAlignment", "psa_square").ToLowerInvariant();
                    // Axis lock (UE3 EParticleAxisLock): EPAL_X / Y / Z (and negatives) = the sprite lies in the plane across
                    // that axis of the emitter (Z: flat, spanning X and Y); EPAL_ROTATE_* = faces the camera turning only
                    // around that axis (only Z is drawn so: upright).
                    string lk = em.Module("ParticleModuleOrientationAxisLock")?.Enum("LockAxisFlags", "epal_none").ToLowerInvariant() ?? "epal_none";
                    Vector3 pr = Vector3.Zero, pu = Vector3.Zero;
                    if (!lk.Contains("rotate") && lk != "epal_none")
                    {
                        (pr, pu) = lk.EndsWith("_x") ? (Vector3.UnitY, Vector3.UnitZ) : lk.EndsWith("_y") ? (Vector3.UnitX, Vector3.UnitZ) : (Vector3.UnitX, Vector3.UnitY);
                        if (lk.Contains("negative")) pr = -pr;
                        if (em.Required.Bool("bUseLocalSpace", false)) { pr = Vector3.TransformNormal(pr, sim.Origin); pu = Vector3.TransformNormal(pu, sim.Origin); }
                    }
                    var col = Col(sp.Color);
                    list.Add(new Gui.ModelView.FxQuad(sp.Position, sp.Size, sp.Rotation, col, Tex(tex),
                        em.Required.Int("SubImages_Horizontal", 1), em.Required.Int("SubImages_Vertical", 1), sp.Image,
                        align == "psa_velocity" ? 2 : align == "psa_rectangle" ? 1 : 0, lk.Contains("rotate_z"), add, sp.Velocity) { PlaneRight = pr, PlaneUp = pu });
                }
            if (Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1")
                foreach (var g in running.SelectMany(r => r.Sim.Sprites().Select(x => (Sys: r.Sim.Data.Name, x.Emitter, x.Sprite))).Where(x => x.Emitter.Kind == "sprite")
                    .GroupBy(x => x.Sys + " / " + x.Emitter.Name).OrderByDescending(g => g.Max(x => x.Sprite.Size.X)).Take(8))
                    Console.WriteLine($"    sprites {g.Key}: {g.Count()}, largest {g.Max(x => x.Sprite.Size.X):0}, colour {g.First().Sprite.Color}, texture {(fx.Looks.TryGetValue(g.First().Emitter, out var lk) && lk.Tex != null ? "yes" : "no")}");
            return list;
        }

        /// <summary>Mesh particles (shockwave rings, debris) and summoned entities' models as triangles.</summary>
        public List<Gui.ModelView.FxTri> Tris()
        {
            var tris = new List<Gui.ModelView.FxTri>();
            const int Max = 60000;
            foreach (var (_, sim, _) in running)
                foreach (var (em, sp) in sim.Sprites())
                {
                    if (em.Kind != "mesh" || !fx.Meshes.TryGetValue(em, out var mm) || tris.Count > Max) continue;
                    if (Environment.GetEnvironmentVariable("MHO_FXONLY") is { Length: > 0 } only && !only.Split(',').Contains(em.Name)) continue;   // debug: these emitters only
                    if (Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1")
                    {
                        var lo = mm.Mesh.Positions.Aggregate(Vector3.Min); var hi = mm.Mesh.Positions.Aggregate(Vector3.Max);
                        Console.WriteLine($"    mesh particle {sim.Data.Name} / {em.Name}: size {sp.Size3}, at {sp.Position}, mesh bounds {lo}…{hi}, colour {sp.Color}");
                    }
                    var rot = sp.MeshRotation * MathF.PI * 2;
                    // The type data's own turn of the mesh (Pitch / Yaw / Roll in degrees: Ant-Man's whirlwind cylinder -90), then the particle's.
                    var td = em.TypeData;
                    // UE3 is Z-up: yaw turns around Z, pitch around Y, roll around X (FRotator). .NET's CreateFromYawPitchRoll is
                    // Y-up (its yaw turns around Y): Iron Man's Microlaser Sweep ring, a flat disc spun around Z (rotation rate
                    // 0, 0, -10), tumbled on its edge and showed as two tall bars (Kurt, 2026-10-02).
                    var pre = td == null ? Matrix4x4.Identity : UeRotation(td.Float("Pitch", 0) * MathF.PI / 180, td.Float("Yaw", 0) * MathF.PI / 180, td.Float("Roll", 0) * MathF.PI / 180);
                    var m = pre * Matrix4x4.CreateScale(sp.Size3) * UeRotation(rot.Y, rot.Z, rot.X) * Matrix4x4.CreateTranslation(sp.Position);
                    var col = Col(sp.Color);
                    var mesh = mm.Mesh;
                    if (Environment.GetEnvironmentVariable("MHO_FXDEBUG") == "1")
                    {
                        var wp = mesh.Positions.Select(x => Vector3.Transform(x, m)).ToList();
                        Console.WriteLine($"      world bounds {wp.Aggregate(Vector3.Min)}…{wp.Aggregate(Vector3.Max)}; rot {sp.MeshRotation}; type data pitch {td?.Float("Pitch", 0)} yaw {td?.Float("Yaw", 0)} roll {td?.Float("Roll", 0)}; axis lock {td?.Enum("AxisLockOption", "-")}; alignment {td?.Enum("MeshAlignment", "-")}; camera facing {td?.Bool("bCameraFacing", false)}");
                    }
                    for (int si = 0; si < mesh.Sections.Count && si < mm.Sections.Length; si++)
                    {
                        var (stex, sadd) = mm.Sections[si];
                        if (stex == null) continue;
                        var sec = mesh.Sections[si]; var tx = Tex(stex);
                        for (int k = 0; k < sec.Triangles; k++)
                        {
                            int i0 = sec.First + k * 3;
                            if (i0 + 2 >= mesh.Indices.Length) break;
                            int a = mesh.Indices[i0], b = mesh.Indices[i0 + 1], c = mesh.Indices[i0 + 2];
                            tris.Add(new Gui.ModelView.FxTri(Vector3.Transform(mesh.Positions[a], m), Vector3.Transform(mesh.Positions[b], m), Vector3.Transform(mesh.Positions[c], m),
                                mesh.Uvs[a], mesh.Uvs[b], mesh.Uvs[c], col, tx, sadd, false));
                        }
                    }
                }
            // (Animated actors and summoned models: not shown yet.)
            return tris;
        }

        /// <summary>UE3's FRotationMatrix for pitch / yaw / roll in radians (rows = the turned X, Y, Z axes; Z up).</summary>
        static Matrix4x4 UeRotation(float pitch, float yaw, float roll)
        {
            float SP = MathF.Sin(pitch), CP = MathF.Cos(pitch), SY = MathF.Sin(yaw), CY = MathF.Cos(yaw), SR = MathF.Sin(roll), CR = MathF.Cos(roll);
            return new Matrix4x4(
                CP * CY, CP * SY, SP, 0,
                SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP, 0,
                -(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP, 0,
                0, 0, 0, 1);
        }

        public int Live => running.Sum(r => r.Sim.Sprites().Count());

        /// <summary>
        /// Which effects go with an animation of the power (a guess from the names, not the game's state machine): a
        /// "…_start" animation plays the effects of the power's start, "…_loop" those of its loop, "…_end" those of its
        /// end; any other animation the start's.
        /// </summary>
        /// <summary>
        /// The phase filter for <paramref name="animation"/> when the power plays it among <paramref name="powerAnimations"/>
        /// (every animation the power class uses): only a power with more than one of start / loop / end plays its effects by
        /// phase. Iron Man's Unibeam uses absattack_unibeam_end alone (the _start and _loop are the boss's): as an "end" it
        /// showed no beam (Kurt, 2026-10-02).
        /// </summary>
        public static Func<Effect, bool> PhaseFor(string animation, IEnumerable<string> powerAnimations)
        {
            static string Stem(string n) => n.EndsWith("_start") ? n[..^6] : n.EndsWith("_loop") ? n[..^5] : n.EndsWith("_end") ? n[..^4] : n;
            string a = animation.ToLowerInvariant(), stem = Stem(a);
            int phases = powerAnimations.Select(x => x.ToLowerInvariant()).Where(x => x != stem && Stem(x) == stem).Distinct().Count();
            return phases > 1 ? PhaseOf(animation) : _ => true;
        }

        public static Func<Effect, bool> PhaseOf(string animation)
        {
            string a = animation.ToLowerInvariant();
            var f = PhaseOnly(a);
            return e => e.Kind == "resource" || f(e);
        }

        static Func<Effect, bool> PhaseOnly(string a)
        {
            // Summons and missiles go at the contact: with any animation but a loop (Hammer of Storms throws in its _end).
            if (a.EndsWith("_loop")) return e => e.Kind is not ("entity" or "projectile") && e.Point.Contains("loop") && !e.Point.Contains("end");
            if (a.EndsWith("_end")) return e => e.Kind is "entity" or "projectile" || e.Point.Contains("end");
            return e => e.Kind is "entity" or "projectile" || !e.Point.Contains("loop") && !e.Point.Contains("end");
        }

    }
}
