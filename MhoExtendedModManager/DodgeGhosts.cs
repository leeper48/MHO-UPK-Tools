using System.Numerics;
using MhoPackageModifier;

namespace MhoExtendedModManager;

/// <summary>A dodge ghost of a hero's dodge effect rebuilt from the mod's model (Powers tab → Character → Parts → Dodge Ghosts),
/// kept in the manifest (DodgeGhosts) so the choice can be changed or taken back. The package itself carries the meshes.</summary>
sealed class DodgeGhostEntry
{
    /// <summary>The package holding the dodge effect (the hero's base package).</summary>
    public string Package { get; set; } = "";
    /// <summary>The stock ghost mesh's name (msh_taskmaster_dodge_01).</summary>
    public string Ghost { get; set; } = "";
    /// <summary>The package and mesh of the model the ghost is made from (the model the preview showed).</summary>
    public string ModelPackage { get; set; } = "";
    public string ModelMesh { get; set; } = "";
    /// <summary>The animation and frame of the pose; empty: picked automatically (the stock ghost's own pose).</summary>
    public string Anim { get; set; } = "";
    public float Frame { get; set; }
    /// <summary>Keep the effect's own size and drop (its emitters make the ghosts 1.1× and 5 units lower: Taskmaster's); off,
    /// the default, the meshes are made to undo them, so a ghost stands on the ground at the character's own size.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepEffectSize { get; set; }
    /// <summary>A lean of the whole pose about the feet, to look like it's moving: "forward", "back", "left", "right" or ""
    /// (Kurt, 2026-10-18: Idle Combat with a different lean on each ghost).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string Lean { get; set; } = "";
    /// <summary>How far the lean tips the pose, in degrees (the Lean slider; Kurt: 10 at first).</summary>
    public float LeanDegrees { get; set; } = DodgeGhosts.LeanDegrees;
}

/// <summary>
/// The dodge's ghosts made from the mod's own model (Kurt, 2026-10-17: Captain Carter on Taskmaster; 54 of 66 heroes have
/// ghosts: static meshes a mesh-particle emitter of the dodge effect spawns, mostly the upper body, in a glow-fade material).
/// The game keeps its ghosts in the character's own space, upright and at its size: a ghost is the character in a pose,
/// where the character stands. So a ghost here is the mod's model in the pose picked for it, else the hero's animation frame
/// that lies closest on the game's ghost (the stock model posed through every animation; GhostFit.Direct).
/// The meshes get new names (…_mod: other packages carry the stock ones at the same paths, the Taskmaster boss's does).
/// </summary>
static class DodgeGhosts
{
    /// <summary>A ghost of the stock dodge effect: its mesh's name, where it sits (the fit of the stock model), and the stock
    /// model's pose that fits it (the automatic pick).</summary>
    public sealed record Slot(string Name, GhostFit.Fit Place, string Anim, float Frame);

    const string PoseNames = "idle|run|dodge|tumble|flip|kick|blink|dash";
    static readonly Dictionary<string, List<Slot>> slotsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ghost meshes (0-based exports) the package's dodge effect emits, and the effect's path.</summary>
    public static List<int> GhostExports(Package p, string file, string path)
    {
        var list = new List<int>();
        var sw = PowerParts.List(file, path).FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase));
        if (sw == null || sw.Original.Length == 0) return list;
        foreach (var e in p.Exports)
        {
            if (!p.ClassOf(e).Equals("ParticleModuleTypeDataMesh", StringComparison.OrdinalIgnoreCase) || !p.PathOf(e).StartsWith(sw.Original + ".", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] d = p.ReadExportBytes(e).ToArray();
            if (TagWalker.Walk(p, d, 4)?.FirstOrDefault(t => t.Name.Equals("mesh", StringComparison.OrdinalIgnoreCase)) is not { } mt) continue;
            int r = BitConverter.ToInt32(d, mt.ValueAt);
            if (r > 0 && !p.PathOf(p.Exports[r - 1]).Contains("vfx_shared", StringComparison.OrdinalIgnoreCase) && !list.Contains(r - 1)) list.Add(r - 1);
        }
        return list;
    }

    /// <summary>A ghost mesh's stock name (a rebuilt one is …_mod).</summary>
    public static string StockName(string name) => name.EndsWith("_mod", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>The hero package's character model (its mesh component's), else its first skeletal mesh.</summary>
    public static MeshRef? CharacterModel(string file, string path)
    {
        var mrs = ModMeshes.List([(file, path)]);
        string? body = Model.MhoSkeleton.ComponentMesh(path);
        return mrs.FirstOrDefault(m => m.Name.Equals(body, StringComparison.OrdinalIgnoreCase)) ?? mrs.FirstOrDefault();
    }

    /// <summary>
    /// The stock dodge ghosts of <paramref name="baseFile"/> and where each sits (read from the game's own copy; about 20 s
    /// the first time, then kept for the session). Throws InvalidDataException with the reason.
    /// </summary>
    public static List<Slot> Analyze(string baseFile, string cooked, Action<string>? log = null)
    {
        lock (slotsCache) if (slotsCache.TryGetValue(baseFile, out var hit)) return hit;
        string stock = StockFiles.For(cooked, baseFile);
        if (!File.Exists(stock)) throw new InvalidDataException($"{baseFile}: the game's copy wasn't found");
        if (LoadCache(baseFile, stock) is { } cached) { lock (slotsCache) slotsCache[baseFile] = cached; return cached; }
        var sp = Package.Open(stock);
        var ghosts = GhostExports(sp, baseFile, stock);
        if (ghosts.Count == 0) throw new InvalidDataException($"{baseFile}: its dodge has no ghosts of its own");
        var smr = CharacterModel(baseFile, stock) ?? throw new InvalidDataException($"{baseFile}: no character model in the game's copy");
        var sld = ModMeshes.Load(smr, null, out string why, componentMaterials: false) ?? throw new InvalidDataException($"{baseFile}: its model wasn't read ({why})");
        // the hero's dodge-like animations (idle, run, dodge, tumble …), at a few frames each: the pose that lies on each ghost as it stands (the game keeps its
        // ghosts in the character's own space, upright and at its size: no fitting; Kurt, 2026-10-17: fitted ones came out
        // bigger, turned, facing the wrong way)
        var animator = new MeshAnimator(sld.Bones, sld.Positions, sld.Normals, sld.Influences);
        var poses = new List<(string Anim, float Frame, Vector3[] P)> { ("", 0, sld.Positions) };
        var rx = new System.Text.RegularExpressions.Regex(PoseNames, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var a in ModAnimations.For(smr, sld.Bones, [], cooked).Where(a => rx.IsMatch(a.Name)))
        {
            var ba = ModAnimations.Load(a);
            if (ba == null) continue;
            var (frames, _) = MeshAnimator.Span(ba);
            for (float fr = 0, st = Math.Max(1f, frames / 8f); fr <= frames; fr += st)
            {
                animator.Pose(ba, fr);
                poses.Add((a.Name, fr, (Vector3[])animator.Positions.Clone()));
            }
        }
        var meshes = ghosts.Select(gi => StaticMesh.Read(sp, sp.Exports[gi]).Positions).ToList();
        // each pose's distance to each ghost (in parallel; a pose's grid is built once for all the ghosts)
        var scores = poses.AsParallel().Select(x => { var m = new GhostFit.Model(x.P); return meshes.Select(g => GhostFit.Direct(m, g)).ToArray(); }).ToArray();
        log?.Invoke($"{baseFile}: {ghosts.Count} ghost(s) matched against the game's {smr.Name} in {poses.Count} pose(s)");
        var slots = new List<Slot>();
        var used = new HashSet<int>();
        for (int k = 0; k < ghosts.Count; k++)
        {
            int best = Enumerable.Range(0, poses.Count).Where(i => !used.Contains(i)).OrderBy(i => scores[i][k].ErrorShare).First();
            used.Add(best);
            var fit = scores[best][k];
            slots.Add(new Slot(sp.Exports[ghosts[k]].ObjectName, fit, poses[best].Anim, poses[best].Frame));
            log?.Invoke($"  {sp.Exports[ghosts[k]].ObjectName}: {(poses[best].Anim.Length == 0 ? "rest pose" : $"{poses[best].Anim} frame {poses[best].Frame:0.#}")}, off by {fit.Error:0.0} units ({fit.ErrorShare * 100:0.0} %)");
        }
        lock (slotsCache) slotsCache[baseFile] = slots;
        SaveCache(baseFile, stock, slots);
        return slots;
    }

    // ---- the analysis kept on disk (data\cache\dodge_ghosts.json), by package and the game copy's size and date
    sealed record CachedSlot(string Name, float Error, float ErrorShare, float Scale, float[] Turn, float[] Offset, string Anim, float Frame);
    static string CacheFile => Path.Combine(Settings.Home, "cache", "dodge_ghosts.json");
    static string CacheKey(string baseFile, string stock) { var fi = new FileInfo(stock); return $"v6|{baseFile}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; }   // (v5: the character's own space, no fitting)

    static Dictionary<string, List<CachedSlot>> ReadCache()
    {
        try { return File.Exists(CacheFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<CachedSlot>>>(File.ReadAllText(CacheFile)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return []; }
    }

    static List<Slot>? LoadCache(string baseFile, string stock) =>
        ReadCache().TryGetValue(CacheKey(baseFile, stock), out var list) && list.Count > 0
            ? [.. list.Select(c => new Slot(c.Name, new GhostFit.Fit(c.Error, c.ErrorShare, c.Scale, new Quaternion(c.Turn[0], c.Turn[1], c.Turn[2], c.Turn[3]), new Vector3(c.Offset[0], c.Offset[1], c.Offset[2]), 0), c.Anim, c.Frame))]
            : null;

    static void SaveCache(string baseFile, string stock, List<Slot> slots)
    {
        try
        {
            var all = ReadCache();
            all[CacheKey(baseFile, stock)] = [.. slots.Select(s => new CachedSlot(s.Name, s.Place.Error, s.Place.ErrorShare, s.Place.Scale,
                [s.Place.Turn.X, s.Place.Turn.Y, s.Place.Turn.Z, s.Place.Turn.W], [s.Place.Offset.X, s.Place.Offset.Y, s.Place.Offset.Z], s.Anim, s.Frame))];
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The lean of a ghost (degrees of the whole body about the feet).</summary>
    public const float LeanDegrees = 10;
    public static readonly string[] Leans = ["forward", "back", "left", "right"];

    /// <summary>A lean's turn about the feet: forward tips the head towards +X (where the character faces), right towards +Y.</summary>
    public static Quaternion? LeanTurn(string lean, float degrees = LeanDegrees)
    {
        float a = degrees * MathF.PI / 180;
        return lean.ToLowerInvariant() switch
        {
            "forward" => Quaternion.CreateFromAxisAngle(Vector3.UnitY, a),
            "back" => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -a),
            "right" => Quaternion.CreateFromAxisAngle(Vector3.UnitX, -a),
            "left" => Quaternion.CreateFromAxisAngle(Vector3.UnitX, a),
            _ => null,
        };
    }

    /// <summary>The dodge effect's mesh emitters: per ghost mesh (stock name), the first emitter's start size and location.</summary>
    public static Dictionary<string, (float Size, Vector3 Loc)> Emitters(string path, string file)
    {
        var d = new Dictionary<string, (float, Vector3)>(StringComparer.OrdinalIgnoreCase);
        var sw = PowerParts.List(file, path).FirstOrDefault(s => s.PartName.Equals("dodgevfx", StringComparison.OrdinalIgnoreCase));
        if (sw == null || sw.Original.Length == 0) return d;
        var fx = Fx.FxPkg.Open(path);
        int sys = fx.Find(sw.Original);
        if (sys < 0 || Fx.ParticleData.Read(fx, sys) is not { } data) return d;
        var rnd = new Random(1);
        foreach (var em in data.Emitters.Where(e => e.Kind == "mesh"))
        {
            int r = em.TypeData?.Ref("Mesh") ?? 0;
            if (r <= 0) continue;
            string name = StockName(fx.T.Exports[r - 1].ObjectName);
            if (d.ContainsKey(name)) continue;
            float size = em.Module("ParticleModuleSize")?.Dist("StartSize", 3)?.V(0, rnd).X ?? 1;
            var loc = em.Module("ParticleModuleLocation")?.Dist("StartLocation", 3)?.V(0, rnd) ?? Vector3.Zero;
            d[name] = (size, loc);
        }
        return d;
    }

    /// <summary>
    /// <paramref name="source"/> (the package before any ghosts were rebuilt) with each ghost made from <paramref name="model"/>
    /// in its pose (<paramref name="poses"/>: ghost name → animation and frame; missing or empty: the slot's automatic pose),
    /// put where the stock ghost is. Read back and verified; throws InvalidDataException with the reason.
    /// </summary>
    public static byte[] Build(string source, string baseFile, MeshRef model, IReadOnlyList<(string File, string Path)> modPackages, string cooked,
        IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, (string Anim, float Frame)> poses, List<string> log, bool keepEffectSize = false,
        IReadOnlyDictionary<string, string>? leans = null, float leanDegrees = LeanDegrees)
    {
        // what the dodge's emitters do to each ghost mesh (its size, its drop), undone in the mesh unless kept
        var emit = keepEffectSize ? [] : Emitters(source, baseFile);
        var ld = ModMeshes.Load(model, cooked, out string why, componentMaterials: false) ?? throw new InvalidDataException($"{model.Name}: not read ({why})");
        if (ld.Positions.Length > 65535) throw new InvalidDataException($"{model.Name} has {ld.Positions.Length:N0} vertices; a ghost (a static mesh) can hold 65,535");
        var anims = ModAnimations.For(model, ld.Bones, modPackages, cooked).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var animator = new MeshAnimator(ld.Bones, ld.Positions, ld.Normals, ld.Influences);
        var p = Package.Open(source);
        var ghosts = GhostExports(p, baseFile, source);
        if (ghosts.Count == 0) throw new InvalidDataException($"{baseFile}: its dodge has no ghosts of its own");
        var replace = new Dictionary<int, Func<long, byte[]>>();
        var renames = new Dictionary<int, string>();
        foreach (int gi in ghosts)
        {
            string name = StockName(p.Exports[gi].ObjectName);
            var slot = slots.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (slot == null) { log.Add($"{name}: not a ghost of the game's dodge, left as it is"); continue; }
            var (anim, frame) = poses.TryGetValue(name, out var pk) && pk.Anim.Length > 0 ? pk : (slot.Anim, slot.Frame);
            (Vector3[] P, Vector3[] N) PoseOf(string a, float fr)
            {
                if (a.Length == 0) return (ld.Positions, ld.Normals);
                if (!anims.TryGetValue(a, out var ar) || ModAnimations.Load(ar) is not { } ba) throw new InvalidDataException($"{model.Name} has no animation {a}");
                animator.Pose(ba, fr);
                return ((Vector3[])animator.Positions.Clone(), (Vector3[])animator.Normals.Clone());
            }
            var (pos, nrm) = PoseOf(anim, frame);
            if (leans != null && leans.TryGetValue(name, out string? lean) && LeanTurn(lean, leanDegrees) is { } q)
            {
                // the whole pose leaned about the feet (the character faces +X, +Y is its right)
                pos = pos.Select(v => Vector3.Transform(v, q)).ToArray();
                nrm = nrm.Select(v => Vector3.Transform(v, q)).ToArray();
            }
            if (emit.TryGetValue(name, out var em) && em.Size > 0.01f)
            {
                // the emitter draws size × mesh + its location: the mesh made 1/size, lifted by its drop, comes out at the
                // character's size, on the ground (its trailing offset behind is kept)
                pos = pos.Select(v => new Vector3(v.X / em.Size, v.Y / em.Size, (v.Z - em.Loc.Z) / em.Size)).ToArray();
                log.Add($"{baseFile}: ghost {name}: the effect's size {em.Size:0.##} and drop {-em.Loc.Z:0.#} undone (it stands on the ground at the character's size)");
            }
            var f = slot.Place;
            var placedP = pos.Select(v => f.Scale * Vector3.Transform(v, f.Turn) + f.Offset).ToArray();
            var placedN = nrm.Select(n => Vector3.Transform(n, f.Turn)).ToArray();
            var stock = StaticMesh.Read(p, p.Exports[gi]);
            var built0 = StaticMeshBuilder.BuildGeometry(stock, placedP, placedN, ld.Indices, stock.Sections[0].MaterialRef, stock.Sections[0].MaterialName, ld.Uv);
            // vertex colours as the stock ghost's body has them (B, G, R, A = 0, 0, 0, 255; its cut edge fades with alpha 0)
            var cols = new byte[pos.Length * 4];
            for (int v = 0; v < pos.Length; v++) cols[v * 4 + 3] = 255;
            var built = stock.HasVertexColors ? new BuiltMesh { Positions = built0.Positions, Normals = built0.Normals, TexCoords = built0.TexCoords, TangentX = built0.TangentX, TangentZ = built0.TangentZ,
                Indices = built0.Indices, Adjacency = built0.Adjacency, Sections = built0.Sections, BoundsOrigin = built0.BoundsOrigin, BoundsExtent = built0.BoundsExtent, BoundsRadius = built0.BoundsRadius, ColorsBgra = cols } : built0;
            replace[gi] = off => StaticMeshBuilder.Serialize(stock, built, off, dropBodySetup: true);
            if (!p.Exports[gi].ObjectName.EndsWith("_mod", StringComparison.OrdinalIgnoreCase)) renames[gi] = p.Exports[gi].ObjectName + "_mod";
            log.Add($"{baseFile}: ghost {name} from {model.Name}, {(anim.Length == 0 ? "rest pose" : $"{anim} frame {frame:0.#}")}{(poses.ContainsKey(name) && poses[name].Anim.Length > 0 ? "" : " (automatic)")}");
        }
        if (replace.Count == 0) throw new InvalidDataException($"{baseFile}: none of its ghosts could be made");
        var addNames = renames.Values.Where(n => !p.Names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        byte[] output = PackageRebuilder.Rebuild(p, replace, [], out var written, addNames, null, renames, null);
        var problems = PackageRebuilder.Verify(p, output, replace.Keys.ToList(), [], written, addNames, null, renames, null);
        var back = Package.FromBytes(output);
        foreach (int gi in replace.Keys)
        {
            var rb = StaticMesh.Read(back, back.Exports[gi]);
            if (rb.Positions.Length != ld.Positions.Length) problems.Add($"{back.Exports[gi].ObjectName} reads back with {rb.Positions.Length} vertices");
        }
        if (problems.Count > 0) throw new InvalidDataException(string.Join("; ", problems.Take(6)));
        return output;
    }
}
