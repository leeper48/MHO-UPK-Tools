using MhoPackageModifier;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// Read only (Kurt, 2026-10-15: a user asked for dark power colors; most effects are additive, where dark adds nothing):
/// every base Material in the game's packages with its blend mode, lighting model, particle usage flags, texture / color /
/// scalar parameters, how many packages carry it and how many material instances use it as their parent. The point is to
/// find materials the game already has compiled shaders for that darken (translucent, modulate) and take a texture
/// parameter, so an additive effect could be switched to one of them. --material-census &lt;out.tsv&gt;.
/// </summary>
static class MaterialCensus
{
    sealed class Info
    {
        public string Blend = "", Lighting = "", Usage = "";
        public SortedSet<string> Tex = new(StringComparer.OrdinalIgnoreCase), Vec = new(StringComparer.OrdinalIgnoreCase), Scalar = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Children = new(StringComparer.OrdinalIgnoreCase);
    }

    static string Short(string v) { int d = v.LastIndexOf('.'); return (d >= 0 ? v[(d + 1)..] : v).Replace("BLEND_", "", StringComparison.OrdinalIgnoreCase).Replace("MLM_", "", StringComparison.OrdinalIgnoreCase); }

    /// <param name="listFor">A material path: its instances are listed (path and one package each).</param>
    public static int Run(string folder, string outTsv, string? listFor = null)
    {
        var files = new DirectoryInfo(folder).EnumerateFiles("UC__*_SF.upk")
            .Where(f => !f.Name.Contains("bak", StringComparison.OrdinalIgnoreCase) && !f.Name.Contains("copy", StringComparison.OrdinalIgnoreCase)).ToList();
        var mats = new System.Collections.Concurrent.ConcurrentDictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        var children = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, fi =>
        {
            Package p;
            try { p = Package.Open(fi.FullName); } catch (Exception ex) when (ex is IOException or InvalidDataException or PackageFormatException) { return; }
            var t = new FxTables(p);
            byte[] b = p.Body;
            for (int i = 0; i < t.Exports.Count; i++)
            {
                var e = t.Exports[i];
                string cls = t.ClassOf(e);
                if (cls.StartsWith("MaterialInstance", StringComparison.OrdinalIgnoreCase))
                {
                    var props = FxProps.Find(b, t, e)?.Props;
                    if (props?.FirstOrDefault(x => x.Name.Equals("Parent", StringComparison.OrdinalIgnoreCase)) is { } par && par.ValueAt + 4 <= b.Length)
                    {
                        int r = BitConverter.ToInt32(b, par.ValueAt);
                        if (r != 0) children.GetOrAdd(t.PathOf(r), _ => []).Add(t.PathOf(i + 1) + "  (" + fi.Name + ")");
                    }
                    continue;
                }
                if (!cls.Equals("Material", StringComparison.OrdinalIgnoreCase)) continue;
                string path = t.PathOf(i + 1);
                var info = mats.GetOrAdd(path, _ => new Info());
                lock (info)
                {
                    info.Packages.Add(fi.Name);
                    if (info.Packages.Count > 1) continue;   // (the first copy read describes it)
                    var props = FxProps.Find(b, t, e)?.Props ?? [];
                    info.Blend = props.FirstOrDefault(x => x.Name.Equals("BlendMode", StringComparison.OrdinalIgnoreCase)) is { } bm ? Short(bm.Value) : "Opaque";
                    info.Lighting = props.FirstOrDefault(x => x.Name.Equals("LightingModel", StringComparison.OrdinalIgnoreCase)) is { } lm ? Short(lm.Value) : "Phong";
                    info.Usage = string.Join(" ", props.Where(x => x.Name.StartsWith("bUsedWith", StringComparison.OrdinalIgnoreCase) && x.Value == "true").Select(x => x.Name[9..]));
                    for (int k = 0; k < t.Exports.Count; k++)
                    {
                        if (t.Exports[k].Outer != i + 1) continue;
                        string ec = t.ClassOf(t.Exports[k]);
                        var set = ec.StartsWith("MaterialExpressionTextureSampleParameter", StringComparison.OrdinalIgnoreCase) ? info.Tex
                            : ec.Equals("MaterialExpressionVectorParameter", StringComparison.OrdinalIgnoreCase) ? info.Vec
                            : ec.Equals("MaterialExpressionScalarParameter", StringComparison.OrdinalIgnoreCase) ? info.Scalar : null;
                        if (set == null) continue;
                        if (FxProps.Find(b, t, t.Exports[k])?.Props.FirstOrDefault(x => x.Name.Equals("ParameterName", StringComparison.OrdinalIgnoreCase)) is { } pn) set.Add(pn.Value);
                    }
                }
            }
        });
        foreach (var (path, info) in mats) if (children.TryGetValue(path, out var c)) foreach (var x in c) info.Children.Add(x[..x.IndexOf("  (")]);
        using (var w = new StreamWriter(outTsv))
        {
            w.WriteLine("path\tblend\tlighting\tusage\ttexture params\tvector params\tscalar params\tpackages\tinstances\texamples");
            foreach (var (path, i) in mats.OrderByDescending(kv => kv.Value.Packages.Count))
                w.WriteLine($"{path}\t{i.Blend}\t{i.Lighting}\t{i.Usage}\t{string.Join(",", i.Tex)}\t{string.Join(",", i.Vec)}\t{string.Join(",", i.Scalar)}\t{i.Packages.Count}\t{i.Children.Count}\t{string.Join(",", i.Packages.Order(StringComparer.OrdinalIgnoreCase).Take(3))}");
        }
        if (listFor != null && children.TryGetValue(listFor, out var kids))
        {
            Console.WriteLine($"Instances of {listFor}:");
            foreach (var g in kids.GroupBy(k => k[..k.IndexOf("  (")]).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key}  {g.Count()} package(s), e.g. {g.First()[(g.First().IndexOf("  (") + 2)..]}");
        }
        static bool Particle(Info i) => i.Usage.Contains("Particle", StringComparison.OrdinalIgnoreCase) || i.Usage.Contains("Beam", StringComparison.OrdinalIgnoreCase);
        var particle = mats.Where(kv => Particle(kv.Value)).ToList();
        Console.WriteLine($"{files.Count} packages, {mats.Count} base materials ({particle.Count} used with particles), {sw.Elapsed.TotalSeconds:0} s -> {outTsv}");
        Console.WriteLine("Particle materials by blend mode:");
        foreach (var g in particle.GroupBy(kv => kv.Value.Blend).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-22} {g.Count(),5} materials, {g.Sum(x => x.Value.Children.Count),6} instances, {g.Count(x => x.Value.Tex.Count > 0),5} with a texture parameter");
        Console.WriteLine("Darkening candidates (particle, not additive, with a texture parameter), most used first:");
        foreach (var (path, i) in particle.Where(kv => !kv.Value.Blend.Contains("Additive", StringComparison.OrdinalIgnoreCase) && kv.Value.Tex.Count > 0)
                     .OrderByDescending(kv => kv.Value.Packages.Count + kv.Value.Children.Count).Take(40))
            Console.WriteLine($"  {path}  [{i.Blend}, {i.Lighting}; {i.Usage}]  tex: {string.Join(",", i.Tex)}  vec: {string.Join(",", i.Vec)}  · {i.Packages.Count} pkgs, {i.Children.Count} instances");
        return 0;
    }
}
