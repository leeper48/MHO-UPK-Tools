using System.Numerics;

namespace MhoExtendedModManager.Fx;

// Ported from the MHO Hero Creator's Fx/ParticleSim.cs (2026-09-30); kept in step with it.
/// <summary>A particle ready to draw: where, how big, turned how far, which colour, which image of the sub-image grid.</summary>
readonly record struct Sprite(Vector3 Position, Vector2 Size, float Rotation, Vector4 Color, int Image, Vector3 Velocity)
{
    /// <summary>The full 3D size (a mesh particle's scale) and a mesh particle's rotation in turns (roll, pitch, yaw).</summary>
    public Vector3 Size3 { get; init; }
    public Vector3 MeshRotation { get; init; }
}

/// <summary>
/// Plays one particle system (sprite emitters; beams / meshes / lights / ribbons are left for later phases) the way UE3's
/// modules describe it (Kurt, 2026-09-29: the power's lightning during its animation). Per emitter: a clock (delay,
/// duration, loops), spawning (Rate × RateScale per second, read at the emitter's time; BurstList at fractions of the
/// duration), and per particle the spawn-time values (read at the emitter's time: Lifetime, StartSize, StartColor /
/// StartAlpha, StartVelocity / StartVelocityRadial, StartLocation, sphere / cylinder spawn shapes, StartRotation /
/// StartRotationRate, a random image) and the over-life values (read at the particle's life 0–1: ColorOverLife /
/// AlphaOverLife and their scales, LifeMultiplier, SizeScale, VelOverLife, Acceleration, SubImageIndex). Rotations are in
/// turns (1 = 360°). Each particle has its own random seed, so a random over-life value doesn't flicker.
/// </summary>
sealed class ParticleSim
{
    sealed class P
    {
        public Vector3 Pos, Vel, Size, Color = Vector3.One;
        public float Age, Life, Rot, RotRate, Alpha = 1;
        public Vector3 MeshRot, MeshRotRate;
        public int Seed, Image;
    }

    sealed class Em(ParticleData.Emitter e)
    {
        public readonly ParticleData.Emitter E = e;
        public readonly List<P> Ps = new();
        public float Time, Accum;
        public int Loop;
        public readonly HashSet<int> Bursts = new();
        public bool Done;
    }

    readonly List<Em> emitters;
    readonly Random rng = new(1234);
    public const int MaxPerEmitter = 600;

    /// <summary>Where the system is (its socket's model matrix); particles in world space keep where they were spawned.</summary>
    public Matrix4x4 Origin = Matrix4x4.Identity;
    public float Age { get; private set; }
    /// <summary>False once every emitter has finished its loops and its last particle has died.</summary>
    public bool Alive => emitters.Any(m => !m.Done || m.Ps.Count > 0);
    public bool Stopped { get; private set; }

    public ParticleData Data { get; }

    public ParticleSim(ParticleData data)
    {
        Data = data;
        // Sprites, and beams and meshes (their particles give a beam's width and colour, a mesh's place, size and turn).
        emitters = data.Emitters.Where(e => e.Kind is "sprite" or "beam" or "mesh").Select(e => new Em(e)).ToList();
    }

    /// <summary>Stops spawning (the power's deactivation point); live particles finish their lives.</summary>
    public void Stop() { Stopped = true; foreach (var m in emitters) m.Done = true; }

    /// <summary>Advances every emitter by <paramref name="dt"/> seconds.</summary>
    public void Step(float dt)
    {
        if (dt <= 0) return;
        Age += dt;
        foreach (var m in emitters) StepEmitter(m, dt);
    }

    void StepEmitter(Em m, float dt)
    {
        var rq = m.E.Required;
        float delay = rq.Float("EmitterDelay", 0), duration = Math.Max(0.01f, rq.Float("EmitterDuration", 1));
        int loops = rq.Int("EmitterLoops", 0);
        // Particles first (age, move, die).
        foreach (var p in m.Ps) Update(m.E, p, dt);
        m.Ps.RemoveAll(p => p.Age >= p.Life);
        if (m.Done) return;
        m.Time += dt;
        float t = m.Time - (m.Loop == 0 || !rq.Bool("bDelayFirstLoopOnly", false) ? delay : 0);
        if (t < 0) return;
        if (t >= duration)
        {
            m.Loop++;
            if (loops > 0 && m.Loop >= loops) { m.Done = true; return; }
            m.Time = t - duration; m.Bursts.Clear(); t = m.Time;
        }
        // Spawning: the rate (per second) and the bursts (at fractions of the duration).
        float rate = 0;
        if (m.E.Spawn is { } sp)
        {
            var r = sp.Dist("Rate", 1); var rs = sp.Dist("RateScale", 1);
            rate = (r?.IsSet == true ? r.F(t, rng) : 0) * (rs?.IsSet == true ? rs.F(t, rng) : 1);
        }
        var rr = rq.Dist("SpawnRate", 1);
        if (rr?.IsSet == true) rate += rr.F(t, rng);
        m.Accum += Math.Max(0, rate) * dt;
        int n = (int)m.Accum; m.Accum -= n;
        if (m.E.Spawn is { } sp2) n += Bursts(sp2, m, t / duration);
        for (int k = 0; k < n && m.Ps.Count < MaxPerEmitter; k++) Spawn(m, t);
    }

    /// <summary>BurstList entries due by the emitter's time fraction (struct array: Count, CountLow, Time).</summary>
    int Bursts(ParticleData.Obj spawn, Em m, float frac)
    {
        var p = spawn.Prop("BurstList");
        if (p == null) return 0;
        var b = spawn.P.Bytes; var t = spawn.P.T;
        int count = BitConverter.ToInt32(b, p.ValueAt), at = p.ValueAt + 4, end = p.ValueAt + p.Size, total = 0;
        for (int i = 0; i < count && at < end; i++)
        {
            var props = FxProps.TryRead(b, t, at, end) ?? [];
            int c = 0, low = -1; float time = 0;
            foreach (var x in props)
            {
                if (x.Name.Equals("Count", StringComparison.OrdinalIgnoreCase)) c = BitConverter.ToInt32(b, x.ValueAt);
                else if (x.Name.Equals("CountLow", StringComparison.OrdinalIgnoreCase)) low = BitConverter.ToInt32(b, x.ValueAt);
                else if (x.Name.Equals("Time", StringComparison.OrdinalIgnoreCase)) time = BitConverter.ToSingle(b, x.ValueAt);
            }
            at = props.Count > 0 ? props[^1].ValueAt + (props[^1].Type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? 1 : props[^1].Size) + 8 : end;
            if (frac >= time && m.Bursts.Add(i)) total += low >= 0 && low < c ? rng.Next(low, c + 1) : c;
        }
        return total;
    }

    void Spawn(Em m, float t)
    {
        var e = m.E;
        var p = new P { Seed = rng.Next() };
        var r = new Random(p.Seed);
        // A lifetime of 0 is UE3's "never ages" (its relative time stays 0, the particle lasts as long as its emitter): Iron
        // Man's Microlaser beams are such particles (Kurt 2026-10-02: with 0.02 s they never showed).
        float life = e.Module("ParticleModuleLifetime")?.Dist("Lifetime", 1)?.F(t, r) ?? 1;
        p.Life = life <= 0 ? 1e6f : Math.Max(0.02f, life);
        p.Size = e.Module("ParticleModuleSize")?.Dist("StartSize", 3)?.V(t, r) ?? new Vector3(1);
        foreach (var mod in e.Modules)
        {
            switch (mod.Class.ToLowerInvariant())
            {
                case "particlemodulecolor":
                    if (mod.Dist("StartColor", 3) is { IsSet: true } sc) p.Color = sc.V(t, r);
                    if (mod.Dist("StartAlpha", 1) is { IsSet: true } sa) p.Alpha = sa.F(t, r);
                    break;
                case "particlemodulevelocity":
                    if (mod.Dist("StartVelocity", 3) is { IsSet: true } sv) p.Vel += sv.V(t, r);
                    if (mod.Dist("StartVelocityRadial", 1) is { IsSet: true } radial) p.Pos = p.Pos;   // applied after the location (below)
                    break;
                case "particlemodulelocation":
                    if (mod.Dist("StartLocation", 3) is { IsSet: true } sl) p.Pos += sl.V(t, r);
                    break;
                case "particlemodulelocationprimitivesphere":
                {
                    float radius = mod.Dist("StartRadius", 1)?.F(t, r) ?? 0;
                    var dir = RandomDir(r, mod);
                    float d = mod.Bool("SurfaceOnly", false) ? radius : radius * MathF.Cbrt((float)r.NextDouble());
                    p.Pos += dir * d;
                    if (mod.Bool("Velocity", false)) p.Vel += dir * d * (mod.Dist("VelocityScale", 1)?.F(t, r) ?? 1);
                    break;
                }
                case "particlemodulelocationprimitivecylinder":
                {
                    float radius = mod.Dist("StartRadius", 1)?.F(t, r) ?? 0, height = mod.Dist("StartHeight", 1)?.F(t, r) ?? 0;
                    float ang = (float)(r.NextDouble() * Math.PI * 2), d = mod.Bool("SurfaceOnly", false) ? radius : radius * MathF.Sqrt((float)r.NextDouble());
                    var ring = new Vector3(MathF.Cos(ang) * d, MathF.Sin(ang) * d, 0);
                    p.Pos += ring + new Vector3(0, 0, ((float)r.NextDouble() - 0.5f) * height);
                    if (mod.Bool("Velocity", false)) p.Vel += ring * (mod.Dist("VelocityScale", 1)?.F(t, r) ?? 1);
                    break;
                }
                case "particlemodulerotation":
                    if (mod.Dist("StartRotation", 1) is { IsSet: true } rot) p.Rot = rot.F(t, r) * MathF.PI * 2;
                    break;
                case "particlemodulerotationrate":
                    if (mod.Dist("StartRotationRate", 1) is { IsSet: true } rr) p.RotRate = rr.F(t, r) * MathF.PI * 2;
                    break;
                case "particlemodulemeshrotation":
                    if (mod.Dist("StartRotation", 3) is { IsSet: true } mr) p.MeshRot = mr.V(t, r);
                    break;
                case "particlemodulemeshrotationrate":
                    if (mod.Dist("StartRotationRate", 3) is { IsSet: true } mrr) p.MeshRotRate = mrr.V(t, r);
                    break;
            }
        }
        if (e.Module("ParticleModuleVelocity") is { } vm && vm.Dist("StartVelocityRadial", 1) is { IsSet: true } rad && p.Pos.LengthSquared() > 1e-6f)
            p.Vel += Vector3.Normalize(p.Pos) * rad.F(t, r);
        int images = Math.Max(1, e.Required.Int("SubImages_Horizontal", 1) * e.Required.Int("SubImages_Vertical", 1));
        p.Image = images > 1 ? r.Next(images) : 0;
        // World-space particles leave the emitter where they were born: move them into model space now.
        if (!e.Required.Bool("bUseLocalSpace", false))
        {
            p.Pos = Vector3.Transform(p.Pos, Origin);
            if (!(e.Module("ParticleModuleVelocity")?.Bool("bInWorldSpace", false) ?? false)) p.Vel = Vector3.TransformNormal(p.Vel, Origin);
        }
        m.Ps.Add(p);
    }

    static Vector3 RandomDir(Random r, ParticleData.Obj mod)
    {
        Vector3 v;
        do v = new Vector3((float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1, (float)r.NextDouble() * 2 - 1); while (v.LengthSquared() > 1 || v.LengthSquared() < 1e-4f);
        v = Vector3.Normalize(v);
        // Positive_X / Negative_X … (all on by default): a direction whose half is off is mirrored.
        if (!mod.Bool("Positive_X", true) && v.X > 0) v.X = -v.X; if (!mod.Bool("Negative_X", true) && v.X < 0) v.X = -v.X;
        if (!mod.Bool("Positive_Y", true) && v.Y > 0) v.Y = -v.Y; if (!mod.Bool("Negative_Y", true) && v.Y < 0) v.Y = -v.Y;
        if (!mod.Bool("Positive_Z", true) && v.Z > 0) v.Z = -v.Z; if (!mod.Bool("Negative_Z", true) && v.Z < 0) v.Z = -v.Z;
        return v;
    }

    void Update(ParticleData.Emitter e, P p, float dt)
    {
        p.Age += dt;
        float life = Math.Clamp(p.Age / p.Life, 0, 1);
        var r = new Random(p.Seed ^ 0x5bd1e995);
        if (e.Module("ParticleModuleAcceleration") is { } acc && acc.Dist("Acceleration", 3) is { IsSet: true } a) p.Vel += a.V(0, new Random(p.Seed)) * dt;
        var vel = p.Vel;
        if (e.Module("ParticleModuleVelocityOverLifetime") is { } vol && vol.Dist("VelOverLife", 3) is { IsSet: true } vl)
            vel = vol.Bool("Absolute", false) ? vl.V(life, r) : vel * vl.V(life, r);
        p.Pos += vel * dt;
        p.Rot += p.RotRate * dt;
        p.MeshRot += p.MeshRotRate * dt;
    }

    /// <summary>Every live particle as a sprite, with its emitter, in model space.</summary>
    public IEnumerable<(ParticleData.Emitter Emitter, Sprite Sprite)> Sprites()
    {
        foreach (var m in emitters)
        {
            var e = m.E;
            bool local = e.Required.Bool("bUseLocalSpace", false);
            var col = e.Module("ParticleModuleColorOverLife"); var cscale = e.Module("ParticleModuleColorScaleOverLife");
            var sml = e.Module("ParticleModuleSizeMultiplyLife"); var ss = e.Module("ParticleModuleSizeScale");
            var subuv = e.Module("ParticleModuleSubUV");
            int images = Math.Max(1, e.Required.Int("SubImages_Horizontal", 1) * e.Required.Int("SubImages_Vertical", 1));
            string interp = e.Required.Enum("InterpolationMethod", "psuvim_none").ToLowerInvariant();
            foreach (var p in m.Ps)
            {
                float life = Math.Clamp(p.Age / p.Life, 0, 1);
                var r = new Random(p.Seed ^ 0x2545f491);
                var color = p.Color; float alpha = p.Alpha;
                if (col != null)
                {
                    if (col.Dist("ColorOverLife", 3) is { IsSet: true } c) color = c.V(life, r);
                    if (col.Dist("AlphaOverLife", 1) is { IsSet: true } al) alpha = al.F(life, r);
                }
                if (cscale != null)
                {
                    if (cscale.Dist("ColorScaleOverLife", 3) is { IsSet: true } c) color *= c.V(life, r);
                    if (cscale.Dist("AlphaScaleOverLife", 1) is { IsSet: true } al) alpha *= al.F(life, r);
                }
                var size = p.Size;
                if (sml?.Dist("LifeMultiplier", 3) is { IsSet: true } lm)
                {
                    var k = lm.V(life, r);
                    if (sml.Bool("MultiplyX", true)) size.X *= k.X;
                    if (sml.Bool("MultiplyY", true)) size.Y *= k.Y;
                    if (sml.Bool("MultiplyZ", true)) size.Z *= k.Z;
                }
                if (ss?.Dist("SizeScale", 3) is { IsSet: true } sz) size *= sz.V(life, r);
                int image = p.Image;
                if (images > 1 && interp is "psuvim_linear" or "psuvim_linear_blend" && subuv?.Dist("SubImageIndex", 1) is { IsSet: true } si)
                    image = Math.Clamp((int)si.F(life, r), 0, images - 1);
                else if (images > 1 && interp is "psuvim_random" or "psuvim_random_blend")
                {
                    int changes = Math.Max(1, e.Required.Int("RandomImageChanges", 1));
                    image = new Random(p.Seed + (int)(life * changes)).Next(images);
                }
                var pos = local ? Vector3.Transform(p.Pos, Origin) : p.Pos;
                var vel = local ? Vector3.TransformNormal(p.Vel, Origin) : p.Vel;
                yield return (e, new Sprite(pos, new Vector2(MathF.Abs(size.X), MathF.Abs(size.Y)), p.Rot, new Vector4(color, alpha), image, vel) { Size3 = size, MeshRotation = p.MeshRot });
            }
        }
    }

    /// <summary>Live particles per emitter (for --fx-sim).</summary>
    public IEnumerable<(string Emitter, int Count)> Counts() => emitters.Select(m => (m.E.Name, m.Ps.Count));
}
