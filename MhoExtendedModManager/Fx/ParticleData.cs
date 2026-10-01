using System.Numerics;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// (Ported from the MHO Hero Creator's Fx/ParticleData.cs, 2026-09-30; kept in step with it.)
/// A value of a particle module (UE3 FRawDistributionFloat / Vector), as cooked in MHO's packages (checked 2026-09-29 on
/// Thor's Lightning Storm package; survey of 4,908 power packages: nearly everything is baked):
///   - a baked **lookup table**: [min, max] of every value, then LookupTableNumElements samples of LookupTableChunkSize
///     floats; Op 1 = the value (chunk = dimensions), 2 = random between the chunk's two halves (min…, max…), 3 = one of
///     the two (extreme); a sample is picked by (t − LookupTableStartTime) × LookupTableTimeScale and interpolated
///     (e.g. Lifetime [0.2, 0.3, 0.2, 0.3, 0.2, 0.3] = random 0.2–0.3; a constant rate 8 = [8, 8, 8, 8]);
///   - else the Distribution object's own properties (constant, uniform, uniform range, particle parameter's Constant,
///     constant curve points).
/// </summary>
sealed class Dist
{
    public readonly int Dim;
    float[] table = [];
    int op = 1, elements, chunk;
    float timeScale, startTime;
    Func<float, Random, float[]>? fallback;

    Dist(int dim) { Dim = dim; }

    public bool IsSet => table.Length > 0 || fallback != null;

    /// <summary>The value at time <paramref name="t"/> (0–1 of a particle's life, or seconds of the emitter for rates).</summary>
    public float[] Eval(float t, Random r)
    {
        if (table.Length >= 2 + Math.Max(1, chunk))
        {
            int n = Math.Max(1, elements), c = Math.Max(1, chunk);
            float idx = elements > 1 ? (t - startTime) * timeScale : 0;
            int i0 = Math.Clamp((int)MathF.Floor(idx), 0, n - 1), i1 = Math.Min(i0 + 1, n - 1);
            float a = Math.Clamp(idx - i0, 0, 1);
            var v = new float[Dim];
            for (int k = 0; k < Dim; k++)
            {
                float At(int i, int off) { int at = 2 + i * c + off; return at < table.Length ? table[at] : 0; }
                float lo = At(i0, k) + (At(i1, k) - At(i0, k)) * a;
                if (op >= 2 && c >= 2 * Dim)
                {
                    float hi = At(i0, Dim + k) + (At(i1, Dim + k) - At(i0, Dim + k)) * a;
                    v[k] = op == 3 ? (r.NextDouble() < 0.5 ? lo : hi) : lo + (hi - lo) * (float)r.NextDouble();
                }
                else v[k] = lo;
            }
            return v;
        }
        return fallback?.Invoke(t, r) ?? new float[Dim];
    }

    public float F(float t, Random r) => Eval(t, r)[0];
    public Vector3 V(float t, Random r) { var v = Eval(t, r); return new Vector3(v[0], Dim > 1 ? v[1] : v[0], Dim > 2 ? v[2] : v[0]); }

    /// <summary>A short description for --fx-dump (the table's range, or the object's kind).</summary>
    public string Describe() => table.Length >= 2 ? $"{(op == 2 ? "random " : op == 3 ? "extreme " : "")}{table[0]:0.###}…{table[1]:0.###} ({elements}×{chunk})"
        : fallback != null ? "object" : "unset";

    /// <summary>A RawDistribution struct property of a module (null when it has none).</summary>
    public static Dist? Read(ParticleData.Obj module, string name, int dim)
    {
        var p = module.Prop(name);
        if (p == null || !p.Type.Equals("StructProperty", StringComparison.OrdinalIgnoreCase)) return null;
        var pk = module.P;
        var inner = FxProps.TryRead(pk.Bytes, pk.T, p.ValueAt, p.ValueAt + p.Size) ?? [];
        var d = new Dist(dim);
        foreach (var t in inner)
        {
            string n = t.Name.ToLowerInvariant();
            if (n == "op" && t.Size == 1) d.op = pk.Bytes[t.ValueAt];
            else if (n == "lookuptablenumelements" && t.Size == 1) d.elements = pk.Bytes[t.ValueAt];
            else if (n == "lookuptablechunksize" && t.Size == 1) d.chunk = pk.Bytes[t.ValueAt];
            else if (n == "lookuptabletimescale") d.timeScale = BitConverter.ToSingle(pk.Bytes, t.ValueAt);
            else if (n == "lookuptablestarttime") d.startTime = BitConverter.ToSingle(pk.Bytes, t.ValueAt);
            else if (n == "lookuptable")
            {
                int c = BitConverter.ToInt32(pk.Bytes, t.ValueAt);
                if (c > 0 && t.Size == 4 + 4 * c) d.table = Enumerable.Range(0, c).Select(k => BitConverter.ToSingle(pk.Bytes, t.ValueAt + 4 + 4 * k)).ToArray();
            }
            else if (n == "distribution" && t.Size == 4 && BitConverter.ToInt32(pk.Bytes, t.ValueAt) is int r && r > 0)
                d.fallback = FromObject(module.Data.Object(pk, r - 1), dim);
        }
        if (d.chunk == 0 && d.table.Length > 2) { d.chunk = dim; d.elements = (d.table.Length - 2) / dim; }   // defaults left out
        if (d.elements > 1 && d.timeScale == 0) d.timeScale = d.elements - 1;                              // samples over 0–1
        return d;
    }

    /// <summary>A distribution object's value (the kinds seen in power packages).</summary>
    static Func<float, Random, float[]>? FromObject(ParticleData.Obj o, int dim)
    {
        string c = o.Class.ToLowerInvariant();
        float Fl(string n, float d) => o.Prop(n) is { } p && p.Size == 4 ? BitConverter.ToSingle(o.P.Bytes, p.ValueAt) : d;
        Vector3 Ve(string n) => o.Prop(n) is { } p && p.Size == 12 ? new(BitConverter.ToSingle(o.P.Bytes, p.ValueAt), BitConverter.ToSingle(o.P.Bytes, p.ValueAt + 4), BitConverter.ToSingle(o.P.Bytes, p.ValueAt + 8)) : Vector3.Zero;
        float[] Arr(Vector3 v) => dim == 1 ? [v.X] : [v.X, v.Y, v.Z];
        switch (c)
        {
            case "distributionfloatconstant" or "distributionfloatparticleparameter": { float k = Fl("Constant", 0); return (_, _) => [k]; }
            case "distributionfloatuniform": { float lo = Fl("Min", 0), hi = Fl("Max", 0); return (_, r) => [lo + (hi - lo) * (float)r.NextDouble()]; }
            case "distributionvectorconstant" or "distributionvectorparticleparameter": { var k = Ve("Constant"); return (_, _) => Arr(k); }
            case "distributionvectoruniform": { var lo = Ve("Min"); var hi = Ve("Max"); return (_, r) => Arr(Vector3.Lerp(lo, hi, (float)r.NextDouble())); }
            case "distributionvectoruniformrange":
            {
                var lo = Ve("MinLow"); var hi = Ve("MaxHigh");
                return (_, r) => Arr(new Vector3(lo.X + (hi.X - lo.X) * (float)r.NextDouble(), lo.Y + (hi.Y - lo.Y) * (float)r.NextDouble(), lo.Z + (hi.Z - lo.Z) * (float)r.NextDouble()));
            }
            default: return null;
        }
    }
}

/// <summary>
/// A particle system as the 3D View plays it: its emitters at full detail (the first LOD level; one disabled there is left
/// out), each with its required module (material, screen alignment, sub-images, duration / loops / delay), spawn module
/// (rate, bursts), type module (sprite when none; beam, mesh, light, ribbon …) and its other modules by class.
/// Structure checked on Thor's Lightning Storm package: ParticleSystem.Emitters → ParticleSpriteEmitter.LODLevels →
/// ParticleLODLevel (RequiredModule, SpawnModule, TypeDataModule, Modules).
/// </summary>
sealed class ParticleData
{
    /// <summary>An object of a package with its properties.</summary>
    public sealed class Obj(ParticleData data, FxPkg p, int export, List<FxProps.Prop> props)
    {
        public ParticleData Data => data;
        public FxPkg P => p;
        public int Export => export;
        public string Class => p.T.ClassOf(p.T.Exports[export]);
        public string Name => p.T.Exports[export].ObjectName;
        public List<FxProps.Prop> Props => props;
        public FxProps.Prop? Prop(string name) => props.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        public int Ref(string name) => Prop(name) is { } x && x.Size == 4 ? BitConverter.ToInt32(p.Bytes, x.ValueAt) : 0;
        public float Float(string name, float d) => Prop(name) is { } x && x.Size == 4 && x.Type.Equals("FloatProperty", StringComparison.OrdinalIgnoreCase) ? BitConverter.ToSingle(p.Bytes, x.ValueAt) : d;
        public int Int(string name, int d) => Prop(name) is { } x && x.Size == 4 && x.Type.Equals("IntProperty", StringComparison.OrdinalIgnoreCase) ? BitConverter.ToInt32(p.Bytes, x.ValueAt) : d;
        public bool Bool(string name, bool d) => Prop(name) is { } x && x.Type.Equals("BoolProperty", StringComparison.OrdinalIgnoreCase) ? p.Bytes[x.ValueAt] != 0 : d;
        public string Enum(string name, string d) => Prop(name)?.Value is { } v ? v[(v.LastIndexOf('.') + 1)..] : d;
        public List<int> Refs(string name)
        {
            var x = Prop(name);
            if (x == null) return [];
            int c = BitConverter.ToInt32(p.Bytes, x.ValueAt);
            return x.Size == 4 + 4 * c ? Enumerable.Range(0, c).Select(k => BitConverter.ToInt32(p.Bytes, x.ValueAt + 4 + 4 * k)).ToList() : [];
        }
        public Dist? Dist(string name, int dim) => Fx.Dist.Read(this, name, dim);
    }

    public sealed class Emitter
    {
        public string Name = "", Kind = "sprite";
        public Obj Required = null!;
        public Obj? Spawn, TypeData;
        public readonly List<Obj> Modules = new();
        public Obj? Module(string cls) => Modules.FirstOrDefault(m => m.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
    }

    public string Name = "";
    public readonly List<Emitter> Emitters = new();
    readonly Dictionary<(string, int), Obj> objects = new();

    public Obj Object(FxPkg p, int export)
    {
        if (objects.TryGetValue((p.Name, export), out var o)) return o;
        var e = p.T.Exports[export];
        var props = FxProps.Find(p.Bytes, p.T, e)?.Props ?? [];
        return objects[(p.Name, export)] = new Obj(this, p, export, props);
    }

    /// <summary>A particle system export of a package, read (null if it isn't one).</summary>
    public static ParticleData? Read(FxPkg p, int export)
    {
        if (!p.T.ClassOf(p.T.Exports[export]).Equals("ParticleSystem", StringComparison.OrdinalIgnoreCase)) return null;
        var data = new ParticleData { Name = p.T.PathOf(export + 1) };
        var sys = data.Object(p, export);
        foreach (int er in sys.Refs("Emitters"))
        {
            if (er <= 0) continue;
            var em = data.Object(p, er - 1);
            // The first LOD level: the one without a Level property (0), else the lowest.
            var lods = em.Refs("LODLevels").Where(r => r > 0).Select(r => data.Object(p, r - 1)).ToList();
            var lod = lods.OrderBy(l => l.Int("Level", 0)).FirstOrDefault();
            if (lod == null || !lod.Bool("bEnabled", true)) continue;
            if (lod.Ref("RequiredModule") is int rq && rq > 0)
            {
                var e = new Emitter { Name = em.Name, Required = data.Object(p, rq - 1) };
                if (!e.Required.Bool("bEnabled", true) && false) continue;
                if (lod.Ref("SpawnModule") is int sp && sp > 0) e.Spawn = data.Object(p, sp - 1);
                if (lod.Ref("TypeDataModule") is int td && td > 0)
                {
                    e.TypeData = data.Object(p, td - 1);
                    string c = e.TypeData.Class.ToLowerInvariant();
                    e.Kind = c.Contains("beam") ? "beam" : c.Contains("meshphysx") || c.Contains("typedatamesh") ? "mesh" : c.Contains("light") ? "light" : c.Contains("ribbon") || c.Contains("trail") ? "ribbon" : c.Contains("physx") ? "physx" : c;
                }
                foreach (int mr in lod.Refs("Modules")) if (mr > 0) e.Modules.Add(data.Object(p, mr - 1));
                data.Emitters.Add(e);
            }
        }
        return data;
    }
}
